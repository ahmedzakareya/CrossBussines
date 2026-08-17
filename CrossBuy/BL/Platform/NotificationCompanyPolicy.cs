using System.Linq.Expressions;
using CrossBuy.Models.Context.Admin;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch B / B3 — the explicit policy for Notifications.CompanyID = NULL, decided BEFORE B2 turns the
    // filter on, because a filter written without deciding this decides it by accident.
    //
    // WHY THERE IS A DECISION TO MAKE
    //
    // Notification.CompanyID is `int?`. Every other pilot entity's CompanyID is NOT NULL, so for them "the row's
    // company" is always a fact. Here it can be absent, and a nullable column under a company filter has three
    // possible readings — only one of which is safe:
    //
    //   (a) INFER: treat a NULL row as belonging to whoever is reading it. Rejected. It manufactures ownership
    //       from an absence, and it is the same class of mistake as the company-1 fallback Batch A deleted: a
    //       missing fact silently becoming a convenient one. Under (a) one NULL row is visible to EVERY company
    //       simultaneously.
    //   (b) EXPOSE: leave NULL rows outside the filter so everyone sees them. Rejected — that is a cross-company
    //       read with no bypass, no authorization and no audit line.
    //   (c) EXCLUDE (ADOPTED): a NULL-company row is not owned by any company, therefore it is not visible to a
    //       company-scoped request. It becomes visible only under an authorized cross-company bypass, where the
    //       operator can see it for what it is — an unattributed legacy row — and it is audited.
    //
    // The cost of (c) is stated plainly: if such rows existed, their recipients would stop seeing them. That is
    // why the backlog item below exists and why it is a resolution task, not a "monitor it" task.
    //
    // MEASURED, NOT ASSUMED (read-only count against CrossBuyDB2, 2026-08-03: 1271 rows, 0 with CompanyID IS
    // NULL). Nothing was written to reach that number. Adopting (c) therefore changes nothing for any existing
    // row today — the cost above is a future risk, not a present regression. The tests exist anyway, because "there is
    // no such data right now" is not a rule — NotifyAsync still accepts `companyId: null`, so the case is
    // reachable by any caller that omits it, and the behaviour must be pinned rather than left to whichever
    // filter expression happened to get written.
    //
    // BACKLOG (recorded here so it cannot be lost, and carried in docs/platform/Stage-001-B3-Notification-Company-Policy.md):
    //   B3-BACKLOG-1  Resolve legacy NULL-company notifications. Two parts, in order:
    //                 1. close the source — make CompanyID mandatory on the write path (NotifyAsync's `companyId`
    //                    is optional today, and NotificationProjectionConsumer always supplies it, but direct
    //                    callers may not), then
    //                 2. resolve existing rows from the recipient's Employee.EmpCompanyID — a RESOLUTION from a
    //                    real fact, never a default — and only then consider NOT NULL on the column.
    //                 Not done in B3: it is a data-migration with its own verification, and B3's remit is the
    //                 bypass. Doing half of it (a backfill without closing the source) would recreate the rows.
    public static class NotificationCompanyPolicy
    {
        // The predicate B2 installs as the global query filter for Notification. It lives here rather than inline
        // in OnModelCreating so the rule is testable on its own and so the reasoning above sits beside it.
        //
        // Three behaviours:
        //   * a cross-company bypass is in force  -> unfiltered (the dispatcher's dedup read, the monitor);
        //   * the scope has a company             -> rows of THAT company only. The comparison is against a
        //     NON-NULLABLE int, so it becomes `CompanyID = @p` in SQL, and SQL NULL never equals a value —
        //     exclusion (c) falls out of three-valued logic rather than needing a special case;
        //   * the scope has NO company            -> FilterCompanyId is 0, which no row can hold, so nothing is
        //     returned. Denied, never widened.
        //
        // Two formulations were rejected, both for reasons that were only visible once tested:
        //   * `n.CompanyID == scope.CompanyId` (nullable on both sides) — EF emits a NULL-SAFE comparison, so an
        //     unresolved scope would match `CompanyID IS NULL`: precisely the unattributed rows, handed to
        //     precisely the request that could not prove who it was.
        //   * `scope.CompanyId != null && n.CompanyID == scope.CompanyId.Value` — EF does not short-circuit this;
        //     it evaluates `.Value` while building parameters and throws "Nullable object must have a value"
        //     before any SQL runs. See ICompanyScopeHolder.FilterCompanyId.
        //
        // It reads the holder THROUGH the executing context, because EF caches the model and a filter closing over
        // a holder value would serve every request from the first request's company — see
        // CrossDbContext.CompanyScope and CompanyQueryFilters.Apply.
        public static Expression<Func<Notification, bool>> QueryFilter(CrossBuy.Models.Context.CrossDbContext db)
            => n => db.CompanyScope.AllowsCrossCompany
                 || n.CompanyID == db.CompanyScope.FilterCompanyId;

        // The same predicate over a holder directly. NOT used by EF — it is for assertions and for callers that
        // already hold a scope. Kept beside the installed form so the two cannot drift.
        public static Expression<Func<Notification, bool>> QueryFilter(ICompanyScopeHolder scope)
            => n => scope.AllowsCrossCompany
                 || n.CompanyID == scope.FilterCompanyId;

        // The same rule as a plain function, for assertions and for callers that already hold a row. Kept beside
        // the expression so the two cannot drift: if one changes, this file changes.
        public static bool IsVisible(int? rowCompanyId, ICompanyScopeHolder scope)
        {
            ArgumentNullException.ThrowIfNull(scope);
            if (scope.AllowsCrossCompany) return true;
            if (scope.CompanyId is not > 0) return false;
            return rowCompanyId == scope.CompanyId;
        }
    }
}
