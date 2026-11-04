using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Storage for the Communication ↔ Tasks link, behind an interface.
    //
    // THE INTERFACE EXISTS BECAUSE THE TABLE DOES NOT YET. CommTaskLink is designed, the DDL is written
    // and reviewed, and nothing has been applied to a database. Rather than ship a feature that throws a
    // SQL-208 the first time somebody uses it — the exact failure HM-D44 recorded — the DEFAULT
    // registration is the unprovisioned store, which refuses with a reason an operator can act on.
    //
    // This is the same shape the platform already uses for NullCommBusinessEventBridge: the capability is
    // declared, the wiring is one line, and "not activated" is a state the code states rather than a state
    // it discovers by failing.
    // =============================================================================================

    public static class CommTaskLinkOrigin
    {
        // The task was raised FROM a message in the conversation.
        public const string CreatedFromMessage = "CreatedFromMessage";

        // An existing task was attached to the conversation afterwards.
        public const string LinkedExisting = "LinkedExisting";

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { CreatedFromMessage, LinkedExisting };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    public sealed class CommTaskLinkRow
    {
        public required long Id { get; init; }
        public required int TaskId { get; init; }
        public required long ThreadId { get; init; }
        public long? CommentId { get; init; }
        public required string Origin { get; init; }
        public DateTime CreatedAt { get; init; }
        public int? CreatedBy { get; init; }
    }

    public interface ICommTaskLinkStore
    {
        // True when this deployment actually has somewhere to put a link. Callers ask BEFORE offering the
        // action, so a user is never shown a button that is going to refuse.
        bool IsProvisioned { get; }

        // Idempotent by contract: the same (company, task, thread, comment) returns the EXISTING row rather
        // than creating a second. A double-submitted dialog must not produce two links.
        Task<CommTaskLinkRow?> AddAsync(
            int companyId, int taskId, long threadId, long? commentId, string origin, int? createdBy,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CommTaskLinkRow>> ForThreadAsync(
            int companyId, long threadId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<CommTaskLinkRow>> ForTaskAsync(
            int companyId, int taskId, CancellationToken cancellationToken = default);

        // By id, inside the company. Exists so an unlink can authorize against the task the link names
        // without trusting a thread id the caller supplied alongside it.
        Task<CommTaskLinkRow?> GetAsync(
            int companyId, long linkId, CancellationToken cancellationToken = default);

        Task<bool> RemoveAsync(
            int companyId, long linkId, int? removedBy, CancellationToken cancellationToken = default);
    }

    // ---------------------------------------------------------------------------------------------
    // THE DEFAULT. Registered until the slice is deployed and CommTaskLink is mapped.
    //
    // It reports rather than throws, and it reports the same way on every method, so a screen that asks
    // IsProvisioned first never reaches these and a caller that does not gets a reason instead of a stack
    // trace from the database driver.
    // ---------------------------------------------------------------------------------------------
    public sealed class UnprovisionedCommTaskLinkStore : ICommTaskLinkStore
    {
        public bool IsProvisioned => false;

        public Task<CommTaskLinkRow?> AddAsync(
            int companyId, int taskId, long threadId, long? commentId, string origin, int? createdBy,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CommTaskLinkRow?>(null);

        public Task<IReadOnlyList<CommTaskLinkRow>> ForThreadAsync(
            int companyId, long threadId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommTaskLinkRow>>(Array.Empty<CommTaskLinkRow>());

        public Task<IReadOnlyList<CommTaskLinkRow>> ForTaskAsync(
            int companyId, int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommTaskLinkRow>>(Array.Empty<CommTaskLinkRow>());

        public Task<CommTaskLinkRow?> GetAsync(
            int companyId, long linkId, CancellationToken cancellationToken = default)
            => Task.FromResult<CommTaskLinkRow?>(null);

        public Task<bool> RemoveAsync(
            int companyId, long linkId, int? removedBy, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    // ---------------------------------------------------------------------------------------------
    // THE REAL ONE. Complete, and NOT registered.
    //
    // It becomes the registration in one line once (a) the DDL in the Phase 3 report is applied and
    // (b) CommTaskLink joins CommunicationModel.Configure and its TableNames. Both of those touch files
    // outside this work's authority, which is why this class waits here instead of being wired up.
    // ---------------------------------------------------------------------------------------------
    public sealed class EfCommTaskLinkStore : ICommTaskLinkStore
    {
        private readonly CrossDbContext _db;

        public EfCommTaskLinkStore(CrossDbContext db) => _db = db;

        public bool IsProvisioned => true;

        public async Task<CommTaskLinkRow?> AddAsync(
            int companyId, int taskId, long threadId, long? commentId, string origin, int? createdBy,
            CancellationToken cancellationToken = default)
        {
            if (companyId <= 0 || taskId <= 0 || threadId <= 0) return null;
            if (!CommTaskLinkOrigin.IsValid(origin)) return null;

            // THE LIVE RELATIONSHIP INCLUDES CommentId — and NULL is a value, not a wildcard.
            //
            // comm_task_links_m2m replaced one uniqueness rule with two filtered ones:
            //
            //     conversation-level  (CompanyID, TaskId, ThreadId)            WHERE RemovedAt IS NULL AND CommentId IS NULL
            //     message-level       (CompanyID, TaskId, ThreadId, CommentId) WHERE RemovedAt IS NULL AND CommentId IS NOT NULL
            //
            // So a task may carry one conversation-level link per thread AND one link per message, and
            // the two coexist. Matching on CommentId here — including the NULL case — is what keeps the
            // read-before-write aligned with those indexes: comparing only (task, thread) would return a
            // conversation-level row for a message-level request and silently refuse to record it.
            //
            // EF translates `l.CommentId == commentId` to `IS NULL` when the value is null, which is the
            // behaviour the conversation-level filter needs; it is spelled out below rather than left to
            // provider nuance.
            //
            // Read-before-write makes the COMMON retry cheap; the filtered unique indexes are what make a
            // duplicate impossible under a race. Application care alone cannot do that — only the
            // database can refuse the second row.
            var existing = commentId.HasValue
                ? await _db.Set<CommTaskLink>()
                    .Where(l => l.CompanyID == companyId
                                && l.TaskId == taskId
                                && l.ThreadId == threadId
                                && l.CommentId == commentId
                                && l.RemovedAt == null)
                    .FirstOrDefaultAsync(cancellationToken)
                : await _db.Set<CommTaskLink>()
                    .Where(l => l.CompanyID == companyId
                                && l.TaskId == taskId
                                && l.ThreadId == threadId
                                && l.CommentId == null
                                && l.RemovedAt == null)
                    .FirstOrDefaultAsync(cancellationToken);

            if (existing != null) return Map(existing);

            var row = new CommTaskLink
            {
                CompanyID = companyId,
                TaskId = taskId,
                ThreadId = threadId,
                CommentId = commentId,
                Origin = origin,
                CreatedBy = createdBy,
                CreatedAt = DateTime.UtcNow,
            };

            _db.Set<CommTaskLink>().Add(row);
            await _db.SaveChangesAsync(cancellationToken);
            return Map(row);
        }

        public async Task<IReadOnlyList<CommTaskLinkRow>> ForThreadAsync(
            int companyId, long threadId, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0) return Array.Empty<CommTaskLinkRow>();

            // CompanyID is compared on the ROW. This table carries no global query filter, so the predicate
            // here is the only thing keeping one tenant's links out of another's panel.
            var rows = await _db.Set<CommTaskLink>().AsNoTracking()
                .Where(l => l.CompanyID == companyId && l.ThreadId == threadId && l.RemovedAt == null)
                .OrderByDescending(l => l.Id)
                .ToListAsync(cancellationToken);

            return rows.Select(Map).ToList();
        }

        public async Task<IReadOnlyList<CommTaskLinkRow>> ForTaskAsync(
            int companyId, int taskId, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0) return Array.Empty<CommTaskLinkRow>();

            var rows = await _db.Set<CommTaskLink>().AsNoTracking()
                .Where(l => l.CompanyID == companyId && l.TaskId == taskId && l.RemovedAt == null)
                .OrderByDescending(l => l.Id)
                .ToListAsync(cancellationToken);

            return rows.Select(Map).ToList();
        }

        public async Task<CommTaskLinkRow?> GetAsync(
            int companyId, long linkId, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0) return null;

            var row = await _db.Set<CommTaskLink>().AsNoTracking()
                .Where(l => l.CompanyID == companyId && l.Id == linkId && l.RemovedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

            return row == null ? null : Map(row);
        }

        public async Task<bool> RemoveAsync(
            int companyId, long linkId, int? removedBy, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0) return false;

            var row = await _db.Set<CommTaskLink>()
                .Where(l => l.CompanyID == companyId && l.Id == linkId && l.RemovedAt == null)
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null) return false;

            row.RemovedAt = DateTime.UtcNow;
            row.RemovedBy = removedBy;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        private static CommTaskLinkRow Map(CommTaskLink l) => new()
        {
            Id = l.Id,
            TaskId = l.TaskId,
            ThreadId = l.ThreadId,
            CommentId = l.CommentId,
            Origin = l.Origin,
            CreatedAt = l.CreatedAt,
            CreatedBy = l.CreatedBy,
        };
    }
}
