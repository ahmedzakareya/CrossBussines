using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Documents
{
    // =============================================================================================
    // DOCUMENT EXPIRY → ACTIONABLE FOLLOW-UP.
    //
    // WHAT THIS IS NOT, because the temptation in every direction was to build one:
    //
    //   NOT a reminder table. There is no DocumentReminder and no DocumentTask. This writes the
    //   platform's OWN TaskItem, linked by EntityType/EntityId the way every other linked task is.
    //
    //   NOT a second attention source. Workspace Attention is a COMPOSITION over My Work, approvals
    //   and mentions - its own code says a source of its own "would have to re-derive overdue, waiting
    //   and urgent from module data". So this adds nothing to Workspace at all. It sets the task's
    //   DUE DATE to the expiry date, and the attention taxonomy then does the entire job by itself:
    //
    //       expiring soon  -> an open task due in N days -> My Work
    //       expires today  -> due today                  -> Attention: DueToday
    //       expired        -> overdue                    -> Attention: OverdueTask, rank 0, AgeDays
    //
    //   That is the whole convergence. A document that expires without being renewed does not need
    //   anybody to escalate it; it becomes an overdue commitment on its own, in the place its owner
    //   already looks.
    //
    //   NOT a second notification engine. It emits Business Events and stops. Whatever the platform
    //   already does with events - notification projection, timeline - happens because they are events.
    //
    //   NOT a second date rule. Every state here comes from IPlatformDocumentService's canonical
    //   evaluator. There is no `< today` in this file.
    //
    // ONE TASK PER INSTRUMENT, TWO EVENTS PER INSTRUMENT, and the asymmetry is deliberate. A document
    // crossing into its warning window and later actually expiring are two distinct FACTS, so they are
    // two events. They are one PIECE OF WORK - "renew this passport" - so they are one task, which
    // ages into overdue by itself on the expiry date. Creating a second task on expiry would put two
    // open rows about one passport in front of the same person.
    // =============================================================================================

    public sealed record DocumentExpirySummary(
        int Examined, int TasksCreated, int EventsRaised, int Skipped, bool Truncated)
    {
        public static readonly DocumentExpirySummary Empty = new(0, 0, 0, 0, false);
    }

    public interface IDocumentExpiryProjection
    {
        /// Idempotent by construction. Running it twice creates nothing the second time; running it
        /// after a renewal correctly produces new work, because the key carries the EXPIRY DATE.
        Task<DocumentExpirySummary> RunAsync(int companyId, CancellationToken ct = default);
    }

    public sealed class DocumentExpiryProjection : IDocumentExpiryProjection
    {
        /// The rule name in the platform's own TaskAutoRule table. A deployment turns document expiry
        /// tasks off, or routes them to a default assignee, exactly the way it does for every other
        /// automatic rule - no new settings surface.
        public const string RuleType = "DocumentExpiry";

        /// BOUNDED, both directions.
        ///
        /// Forward is the largest lead time any type may configure, so the query cannot miss a document
        /// a type wants early notice of. Backward is a deliberate operational horizon: a document that
        /// expired eight months ago and was never actioned will not be fixed by a task appearing today,
        /// and creating tasks for the entire history on the first run after deployment would bury every
        /// real one. Inside the window nothing starves; outside it, silence is the honest answer.
        public const int LookbackDays = 90;

        /// Per company, per run. A cap rather than a full-table scan - see the worker.
        public const int MaxPerRun = 200;

        private readonly CrossDbContext _db;
        private readonly IBusinessEventService? _events;
        private readonly TimeProvider _clock;

        public DocumentExpiryProjection(CrossDbContext db,
            TimeProvider? clock = null, IBusinessEventService? events = null)
        { _db = db; _clock = clock ?? TimeProvider.System; _events = events; }

        private DateTime Today => _clock.GetUtcNow().UtcDateTime.Date;

        public async Task<DocumentExpirySummary> RunAsync(int companyId, CancellationToken ct = default)
        {
            // NO FALLBACK TO COMPANY 1, and no unscoped run. A projection that quietly processed
            // "everything" when it was told nothing is how a worker leaks one company's documents into
            // another company's task list.
            if (companyId <= 0) return DocumentExpirySummary.Empty;

            var rule = await _db.TaskAutoRules.AsNoTracking()
                .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.RuleType == RuleType, ct);

            // ABSENT MEANS ON; INACTIVE MEANS OFF. A deployment that has never heard of this rule still
            // gets its expiry warnings - a document platform that silently tracked nothing until
            // somebody found a settings page would be worse than useless. Turning it off is a
            // deliberate act, and it is respected.
            if (rule is { IsActive: false }) return DocumentExpirySummary.Empty;

            var today = Today;
            var horizon = today.AddDays(DocumentExpiryPolicy.MaxWarningDays);
            var floor = today.AddDays(-LookbackDays);

            // THE COMPANY PREDICATE IS IN THE QUERY, so a foreign document is never materialised and
            // cannot be reasoned about by mistake further down.
            var candidates = await _db.Set<PlatformDocument>().AsNoTracking()
                .Where(d => d.CompanyID == companyId
                            && d.Status == "Active"
                            && d.ExpiryDate != null
                            && d.ExpiryDate <= horizon
                            && d.ExpiryDate >= floor)
                .OrderBy(d => d.ExpiryDate)
                .Take(MaxPerRun + 1)
                .ToListAsync(ct);

            bool truncated = candidates.Count > MaxPerRun;
            if (truncated) candidates = candidates.Take(MaxPerRun).ToList();
            if (candidates.Count == 0) return DocumentExpirySummary.Empty;

            var typeIds = candidates.Where(d => d.DocumentTypeId != null)
                .Select(d => d.DocumentTypeId!.Value).Distinct().ToList();
            var types = await _db.Set<PlatformDocumentType>().AsNoTracking()
                .Where(t => typeIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, ct);

            // The keys that would be written, looked up in ONE query rather than one per document.
            var wanted = candidates.ToDictionary(d => d.Id, d => TaskKey(d.Id, d.ExpiryDate!.Value));
            var keys = wanted.Values.ToList();
            var already = new HashSet<string>(await _db.TaskAutoLogs.AsNoTracking()
                .Where(l => l.CompanyId == companyId && keys.Contains(l.RuleKey))
                .Select(l => l.RuleKey)
                .ToListAsync(ct));

            int created = 0, raised = 0, skipped = 0;

            foreach (var doc in candidates)
            {
                if (ct.IsCancellationRequested) break;

                var type = doc.DocumentTypeId != null && types.TryGetValue(doc.DocumentTypeId.Value, out var t) ? t : null;

                // THE CANONICAL EVALUATOR DECIDES, not this file. The query above is a coarse net cast
                // by date; whether a document is actually inside ITS type's warning window is the
                // platform's single date rule, and it is asked here rather than re-implemented.
                var state = PlatformDocumentService.EvaluateAt(doc, type, today);
                if (!state.NeedsAttention) { skipped++; continue; }

                var expiry = doc.ExpiryDate!.Value;
                var key = wanted[doc.Id];
                bool needTask = !already.Contains(key);

                var action = state.State == DocumentLifecycleState.Expired
                    ? DocumentEvents.Expired
                    : DocumentEvents.ExpiringSoon;

                // ONE TRANSACTION PER DOCUMENT. Per company would mean one unlucky row discarding two
                // hundred good ones; per document means the run makes progress and the failure is
                // isolated. The event is recorded INSIDE it (ADR-001), so a task and the fact that
                // announced it share one fate.
                Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
                try
                {
                    tx = await _db.Database.BeginTransactionAsync(ct);

                    if (needTask)
                    {
                        var task = new TaskItem
                        {
                            CompanyId = companyId,
                            Title = TitleAr(doc, type),
                            TitleEn = TitleEn(doc, type),
                            Description = null,
                            // 0 = unassigned, for a manager to route - the same convention the task
                            // platform's own generator uses for system-created work.
                            AssigneeEmployeeId = rule?.DefaultAssigneeEmployeeId ?? 0,
                            CreatedByEmployeeId = 0,
                            Priority = state.State == DocumentLifecycleState.Expired ? "High" : "Normal",
                            // THE LINE THAT MAKES ATTENTION WORK. Due on the expiry date, so the task
                            // becomes DueToday and then OverdueTask without anybody sweeping for it.
                            DueDate = expiry,
                            Status = "New",
                            ActualHours = 0m,
                            // ENTITY IDENTITY, never title matching. The task points at the employee,
                            // which is the subject the permission model and the link resolver both
                            // already understand.
                            EntityType = doc.EntityType,
                            EntityId = doc.EntityId,
                            CreatedAt = _clock.GetUtcNow().UtcDateTime,
                        };
                        _db.TaskItems.Add(task);
                        await _db.SaveChangesAsync(ct);

                        _db.TaskAutoLogs.Add(new TaskAutoLog
                        {
                            CompanyId = companyId,
                            RuleKey = key,
                            TaskId = task.ID,
                            CreatedAt = _clock.GetUtcNow().UtcDateTime,
                        });
                        await _db.SaveChangesAsync(ct);
                        created++;
                    }

                    // The event's key carries the expiry date AND the action, so "started warning" and
                    // "actually expired" are each announced once for this instrument, however many
                    // times the worker runs. The kernel deduplicates on it.
                    await DocumentEvents.RaiseAsync(_events, doc, type, action,
                        DocumentEvents.ExpiryKey(doc.Id, expiry, action), ct,
                        daysRemaining: state.DaysRemaining);
                    raised++;

                    await tx.CommitAsync(ct);
                    await tx.DisposeAsync();
                    already.Add(key);
                }
                catch (Exception)
                {
                    if (tx != null)
                    {
                        try { await tx.RollbackAsync(ct); } catch (Exception) { }
                        try { await tx.DisposeAsync(); } catch (Exception) { }
                    }
                    try { _db.ChangeTracker.Clear(); } catch (Exception) { }
                    skipped++;
                }
            }

            return new DocumentExpirySummary(candidates.Count, created, raised, skipped, truncated);
        }

        /// ONE TASK PER (document, expiry date). Not per run, and not per state.
        ///
        /// Per run would create a task every tick. Per document alone would be worse in a quieter way:
        /// a renewed passport has a NEW expiry date and genuinely deserves a new warning when that one
        /// approaches, and a key without the date would suppress it forever - the document would go
        /// silent exactly once it started mattering again.
        public static string TaskKey(long documentId, DateTime expiry)
            => $"DocExpiry:{documentId}:{expiry:yyyyMMdd}";

        // ---- titles ---------------------------------------------------------------------------
        //
        // A TASK TITLE IS PUBLISHED TEXT. Workspace Attention copies it verbatim into the dashboard,
        // the task list shows it, and a notification would repeat it - so whatever is written here is
        // readable by everyone the TASK reaches, which is not the same set as everyone the DOCUMENT
        // reaches. That mismatch is the leak, and it is closed by never writing anything that is not
        // already implied by the link.
        //
        // NEVER in a title: the document number (a passport number), the file name, a decision note,
        // any metadata value, or the employee's name. The task links to the employee; whoever may open
        // that link learns who it is through the authorized path, and whoever may not, does not.
        //
        // The TYPE NAME is the one thing that varies, and it varies on confidentiality. "Renew:
        // Passport" is unremarkable. "Renew: Disciplinary warning" on a shared dashboard is a
        // disclosure about a person, so a document above Internal gets a neutral title and the reader
        // opens the document to find out what it is - if they are allowed to.
        private static string TitleAr(PlatformDocument doc, PlatformDocumentType? type)
            => Nameable(doc, type)
                ? $"تجديد مستند: {type!.NameAr}"
                : "مستند يحتاج إلى تجديد";

        private static string TitleEn(PlatformDocument doc, PlatformDocumentType? type)
            => Nameable(doc, type)
                ? $"Renew document: {type!.NameEn}"
                : "A document needs renewal";

        private static bool Nameable(PlatformDocument doc, PlatformDocumentType? type)
            => type != null
               && string.Equals(doc.Confidentiality, DocumentConfidentiality.Internal, StringComparison.Ordinal);
    }
}
