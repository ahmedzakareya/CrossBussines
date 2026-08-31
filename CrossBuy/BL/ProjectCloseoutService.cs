using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    // =============================================================================================
    // PROJECT CLOSEOUT — one authority for "may this project close", and one place that closes it.
    //
    // THE POINT OF A SINGLE READINESS SERVICE. The alternative is what usually happens: the screen
    // computes a list of blockers to display, and the close endpoint re-checks a slightly different
    // list before it acts. They agree until one of them is edited, and then the button is enabled for
    // a project that cannot close, or disabled for one that can. Both surfaces here call
    // ReadinessAsync, so there is exactly one answer and the screen is a rendering of it.
    //
    // BLOCKERS ARE ONLY WHAT THIS DEPLOYMENT CAN PROVE. Every blocker below is derived from a record
    // that exists on current master:
    //
    //   * an unresolved progress billing            -> ProgressBilling.Status
    //   * retention still withheld                  -> posted GL lines on 1104, tagged with the project
    //   * a customer advance not yet recovered      -> posted GL lines on 2104, same
    //   * an open task on this project              -> TaskItem.EntityType/EntityId, a real relation
    //   * the project is closed already             -> the current closeout row
    //
    // Nothing is inferred from a title, a name or a convention. Things this deployment CANNOT prove -
    // subcontractor retention that may belong to a later handover, a project with no billing at all -
    // are WARNINGS, because a blocker nobody can satisfy is just a project that never closes.
    //
    // WHAT IS DELIBERATELY NOT A BLOCKER: a posted billing that ought to be reversed. Reversal does not
    // exist yet - it is blocked on an AR receipt reversal foundation that is not landed - so a blocker
    // for "unresolved reversal state" would be unsatisfiable by construction. It is a warning that says
    // so plainly instead.
    // =============================================================================================

    public sealed record CloseoutItem(string Code, string TitleAr, string TitleEn, decimal? Amount = null, int? Count = null);

    public sealed record CloseoutFinancials(
        decimal ContractValue,
        decimal BilledGross,
        decimal PostedNetDue,
        decimal RetentionBalance,
        decimal AdvanceBalance,
        decimal SubcontractorRetentionBalance,
        int PostedBillingCount,
        int UnresolvedBillingCount);

    public sealed record CloseoutOperations(
        int OpenTaskCount,
        int TotalTaskCount,
        DateTime? LastBillingDate,
        string? ProjectStatus);

    public sealed record ProjectCloseoutReadiness(
        bool IsReady,
        IReadOnlyList<CloseoutItem> BlockingItems,
        IReadOnlyList<CloseoutItem> Warnings,
        CloseoutFinancials Financials,
        CloseoutOperations Operations)
    {
        /// A refusal that discloses nothing, for a caller who may not ask about this project at all.
        /// Deliberately shaped like an ordinary "not ready" so the screen has one code path.
        public static ProjectCloseoutReadiness Unavailable() => new(
            false,
            new[] { new CloseoutItem(CloseoutBlockers.NotAvailable, "غير متاح", "Not available") },
            Array.Empty<CloseoutItem>(),
            new CloseoutFinancials(0, 0, 0, 0, 0, 0, 0, 0),
            new CloseoutOperations(0, 0, null, null));
    }

    public static class CloseoutBlockers
    {
        public const string NotAvailable = "not_available";
        public const string AlreadyClosed = "already_closed";
        public const string UnresolvedBilling = "unresolved_billing";
        public const string RetentionOutstanding = "retention_outstanding";
        public const string AdvanceOutstanding = "advance_outstanding";
        public const string OpenTasks = "open_tasks";
    }

    public static class CloseoutWarnings
    {
        public const string NoBilling = "no_billing";
        public const string SubcontractorRetention = "subcontractor_retention_outstanding";
        public const string ReversalUnavailable = "reversal_unavailable";
    }

    public interface IProjectCloseoutService
    {
        Task<ProjectCloseoutReadiness> ReadinessAsync(int projectId, CancellationToken ct = default);
        Task<(bool ok, string? error)> CloseAsync(int projectId, string reason, CancellationToken ct = default);
        Task<(bool ok, string? error)> ReopenAsync(int projectId, string reason, CancellationToken ct = default);
        Task<IReadOnlyList<ProjectCloseout>> HistoryAsync(int projectId, CancellationToken ct = default);
    }

    public sealed class ProjectCloseoutService : IProjectCloseoutService
    {
        public const string ReasonRequired = "A reason is required.";
        public const string NotReady = "This project is not ready to close.";
        public const string NotClosed = "This project is not closed.";

        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _contexts;
        private readonly IProjectsAccessService _access;
        private readonly IContractService _contracts;

        public ProjectCloseoutService(CrossDbContext db, IBusinessContextAccessor contexts,
            IProjectsAccessService access, IContractService contracts)
        { _db = db; _contexts = contexts; _access = access; _contracts = contracts; }

        private DbSet<ProjectCloseout> Closeouts => _db.Set<ProjectCloseout>();

        /// One gate for every entry point, so reading readiness and acting on it cannot drift apart.
        /// The project's company comes from the PROJECT ROW and is compared with the caller's - a
        /// project in another company is refused exactly as a non-existent one is.
        private async Task<BusinessContext?> GateAsync(int projectId, string action, CancellationToken ct)
        {
            if (projectId <= 0) return null;

            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 } || ctx.EmployeeId is not > 0) return null;

            bool exists = await _db.Projects.AsNoTracking()
                .AnyAsync(p => p.ID == projectId && p.CompanyID == ctx.CompanyId, ct);
            if (!exists) return null;

            return await _access.CanAsync(ctx, action, PermissionTarget.ForProject(projectId, ctx.CompanyId), ct)
                ? ctx : null;
        }

        public async Task<ProjectCloseoutReadiness> ReadinessAsync(int projectId, CancellationToken ct = default)
        {
            // READ authority is enough to ASK. Closing needs more - see CloseAsync - but a project
            // manager should be able to see what stands between the project and its close.
            var ctx = await GateAsync(projectId, ProjectsActions.Read, ct);
            if (ctx == null) return ProjectCloseoutReadiness.Unavailable();

            var blockers = new List<CloseoutItem>();
            var warnings = new List<CloseoutItem>();

            var project = await _db.Projects.AsNoTracking()
                .FirstAsync(p => p.ID == projectId && p.CompanyID == ctx.CompanyId, ct);

            // ---- 1. already closed ----
            bool alreadyClosed = await Closeouts.AsNoTracking()
                .AnyAsync(c => c.CompanyID == ctx.CompanyId && c.ProjectId == projectId && c.IsCurrent, ct);
            if (alreadyClosed)
                blockers.Add(new CloseoutItem(CloseoutBlockers.AlreadyClosed,
                    "المشروع مقفل بالفعل", "The project is already closed"));

            // ---- 2. billings that are not Posted ----
            var billings = await _db.ProgressBillings.AsNoTracking()
                .Where(b => b.CompanyID == ctx.CompanyId && b.ProjectId == projectId)
                .Select(b => new { b.Status, b.GrossWork, b.NetDue, b.BillingDate })
                .ToListAsync(ct);

            var unresolved = billings.Where(b => b.Status != ProgressBillingStatuses.Posted).ToList();
            if (unresolved.Count > 0)
                blockers.Add(new CloseoutItem(CloseoutBlockers.UnresolvedBilling,
                    "مستخلصات غير مرحّلة", "Progress billings that are not posted", Count: unresolved.Count));

            if (billings.Count == 0)
                warnings.Add(new CloseoutItem(CloseoutWarnings.NoBilling,
                    "لا يوجد أي مستخلص لهذا المشروع", "This project has no progress billing at all"));

            // ---- 3/4. money the ledger says is still outstanding ----
            //
            // From POSTED GL lines tagged with this project, not from the billing rows: the ledger is
            // what an auditor reads, and a retention released outside progress billing still shows here.
            var summary = await _contracts.GetSummaryAsync(ctx.CompanyId, projectId);

            if (summary.RetentionBalance > 0)
                blockers.Add(new CloseoutItem(CloseoutBlockers.RetentionOutstanding,
                    "محتجزات لم يتم الإفراج عنها", "Retention is still withheld", Amount: summary.RetentionBalance));

            if (summary.AdvanceBalance > 0)
                blockers.Add(new CloseoutItem(CloseoutBlockers.AdvanceOutstanding,
                    "دفعة مقدمة لم تُستردّ بالكامل", "A customer advance has not been fully recovered",
                    Amount: summary.AdvanceBalance));

            var subRetention = await _contracts.SubRetentionBalanceAsync(ctx.CompanyId, projectId);
            if (subRetention > 0)
                warnings.Add(new CloseoutItem(CloseoutWarnings.SubcontractorRetention,
                    "محتجزات مقاولي الباطن قائمة", "Subcontractor retention is still payable", Amount: subRetention));

            // ---- 5. open work, through the real relation ----
            //
            // TaskItem.EntityType/EntityId is the polymorphic link the platform already uses. No title
            // matching: a task is on this project because it SAYS so, not because it mentions the name.
            var tasks = await _db.TaskItems.AsNoTracking()
                .Where(t => t.CompanyId == ctx.CompanyId
                            && t.EntityType == EntityRegistry.Project
                            && t.EntityId == projectId)
                .Select(t => t.Status)
                .ToListAsync(ct);

            int openTasks = tasks.Count(s => !string.Equals(s, "Done", StringComparison.Ordinal));
            if (openTasks > 0)
                blockers.Add(new CloseoutItem(CloseoutBlockers.OpenTasks,
                    "مهام مفتوحة على المشروع", "Open tasks on this project", Count: openTasks));

            // ---- the reversal note ----
            //
            // Stated as a warning rather than a blocker because there is no way to satisfy it: reversing
            // a posted billing needs an AR receipt reversal foundation that is not landed. A closeout
            // that demanded it would never complete.
            if (billings.Any(b => b.Status == ProgressBillingStatuses.Posted))
                warnings.Add(new CloseoutItem(CloseoutWarnings.ReversalUnavailable,
                    "لا يمكن عكس مستخلص مُرحَّل بعد — الإقفال لا ينتظر ذلك",
                    "A posted billing cannot be reversed yet; closeout does not wait for it"));

            var financials = new CloseoutFinancials(
                ContractValue: project.ContractValue ?? 0m,
                BilledGross: billings.Sum(b => b.GrossWork),
                PostedNetDue: billings.Where(b => b.Status == ProgressBillingStatuses.Posted).Sum(b => b.NetDue),
                RetentionBalance: summary.RetentionBalance,
                AdvanceBalance: summary.AdvanceBalance,
                SubcontractorRetentionBalance: subRetention,
                PostedBillingCount: billings.Count(b => b.Status == ProgressBillingStatuses.Posted),
                UnresolvedBillingCount: unresolved.Count);

            var operations = new CloseoutOperations(
                OpenTaskCount: openTasks,
                TotalTaskCount: tasks.Count,
                LastBillingDate: billings.Count == 0 ? null : billings.Max(b => b.BillingDate),
                ProjectStatus: project.Status);

            return new ProjectCloseoutReadiness(blockers.Count == 0, blockers, warnings, financials, operations);
        }

        public async Task<(bool ok, string? error)> CloseAsync(int projectId, string reason, CancellationToken ct = default)
        {
            // `close` and not `read`: ProjectsAccessService answers close only for the module role and
            // returns FALSE on the membership path, so a project's own manager cannot close it. Freezing
            // a project's financial history is administrative.
            var ctx = await GateAsync(projectId, ProjectsActions.Close, ct);
            if (ctx == null) return (false, NotReady);

            if (string.IsNullOrWhiteSpace(reason)) return (false, ReasonRequired);

            // THE SAME AUTHORITY THE SCREEN USED. Re-evaluated here rather than trusted from the caller:
            // readiness is a fact about the project at this instant, and the instant that matters is the
            // one in which it closes.
            var readiness = await ReadinessAsync(projectId, ct);
            if (!readiness.IsReady) return (false, NotReady);

            await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

            // Re-checked INSIDE the transaction. Two administrators closing the same project at the same
            // moment would otherwise both pass readiness and write two current closeouts.
            bool raced = await Closeouts
                .AnyAsync(c => c.CompanyID == ctx.CompanyId && c.ProjectId == projectId && c.IsCurrent, ct);
            if (raced) return (false, NotReady);

            Closeouts.Add(new ProjectCloseout
            {
                CompanyID = ctx.CompanyId,
                ProjectId = projectId,
                ClosedAt = DateTime.UtcNow,
                ClosedBy = ctx.EmployeeId!.Value,
                Reason = reason.Trim(),
                ReadinessNote = readiness.Warnings.Count == 0
                    ? null
                    : string.Join("; ", readiness.Warnings.Select(w => w.Code)),
                IsCurrent = true,
            });

            var project = await _db.Projects.FirstAsync(p => p.ID == projectId && p.CompanyID == ctx.CompanyId, ct);
            project.Status = "Completed";

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync();
            return (true, null);
        }

        public async Task<(bool ok, string? error)> ReopenAsync(int projectId, string reason, CancellationToken ct = default)
        {
            // The SAME authority as closing, and deliberately so. There is no stronger Projects action to
            // require - `close` is already module-role-only and refused on the membership path - so
            // inventing one purely to gate a reopen would add vocabulary without adding a decision.
            // What makes a reopen heavier is the evidence: a reason is mandatory and the reopen is
            // recorded against the closeout it undoes rather than erasing it.
            var ctx = await GateAsync(projectId, ProjectsActions.Close, ct);
            if (ctx == null) return (false, NotClosed);

            if (string.IsNullOrWhiteSpace(reason)) return (false, ReasonRequired);

            await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

            var current = await Closeouts
                .FirstOrDefaultAsync(c => c.CompanyID == ctx.CompanyId && c.ProjectId == projectId && c.IsCurrent, ct);
            if (current == null) return (false, NotClosed);

            current.ReopenedAt = DateTime.UtcNow;
            current.ReopenedBy = ctx.EmployeeId!.Value;
            current.ReopenReason = reason.Trim();
            current.IsCurrent = false;      // history, not deletion

            var project = await _db.Projects.FirstAsync(p => p.ID == projectId && p.CompanyID == ctx.CompanyId, ct);
            project.Status = "Active";

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync();
            return (true, null);
        }

        public async Task<IReadOnlyList<ProjectCloseout>> HistoryAsync(int projectId, CancellationToken ct = default)
        {
            var ctx = await GateAsync(projectId, ProjectsActions.Read, ct);
            if (ctx == null) return Array.Empty<ProjectCloseout>();

            return await Closeouts.AsNoTracking()
                .Where(c => c.CompanyID == ctx.CompanyId && c.ProjectId == projectId)
                .OrderByDescending(c => c.ClosedAt)
                .ToListAsync(ct);
        }
    }
}
