using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch B / B2 — the pilot EF global query filters.
    //
    // Twelve entities, named one at a time. Deliberately NOT a loop over "every entity with a CompanyID
    // property": the instruction is not to apply filters blindly, and B1 established that 128 further entities
    // carry a direct CompanyID while being unsafe or unclassified. A reflection sweep would silently enrol every
    // one of them the moment someone adds a property — including the ones B1 excluded for cause.
    //
    // WHAT A FILTER HERE DOES AND DOES NOT DO
    //
    //   * It rewrites every LINQ read of that entity, including reads inside Include() and inside code that has
    //     never heard of BusinessContext. That is the point: the ~1,100 hand-written `.Where(x => x.CompanyID ==
    //     companyId)` predicates become a floor rather than the only defence.
    //   * It does NOT filter writes. An Update/Delete of a tracked entity is a write-side concern — B4.
    //   * It does NOT apply to raw SQL. `SqlQueryRaw<T>` scalars are never filtered, and a `FromSql` composes the
    //     filter as an OUTER predicate. B1 verified that ZERO raw SQL touches any of these twelve; B5 re-verifies.
    //   * It reads ICompanyScopeHolder ONLY — never IBusinessContextAccessor, because that accessor queries
    //     Employee to resolve the company, so reading it from a filter would be circular. The holder is reached
    //     THROUGH the executing CrossDbContext (`db.CompanyScope`), which is what makes the value per-request:
    //     see the CompanyScope property comment, and the model-cache test that proves it.
    //
    // THE THREE OUTCOMES, in the order the predicate reads them:
    //
    //   1. a cross-company bypass is in force  -> unfiltered. The four flows B1 found (dispatch worker,
    //      notification dedup, monitor elevated view, and — pinned, not unrestricted — the storefront) hold an
    //      authorized, audited grant. See ADR-023.
    //   2. the scope has a company             -> that company's rows only.
    //   3. the scope has NO company            -> NOTHING. FilterCompanyId is 0, which is not a company id any
    //      row can hold, so `WHERE CompanyID = 0` matches nothing. An unresolved scope reads nothing rather than
    //      everything. Fail closed — see ICompanyScopeHolder.FilterCompanyId for why it is a 0 and not a null
    //      check (EF evaluated `.Value` eagerly and threw).
    //
    // WHY OUTCOME 3 IS `false` AND NOT "unfiltered"
    //
    // An unresolved scope means the request could not establish who it is. The two available answers are "show
    // nothing" and "show everything"; only one of them is a security control. It is the same decision as Batch A's
    // removal of the company-1 fallback: absence must not resolve to a convenient value.
    //
    // The cost is real and is stated rather than hidden: a request that fails to resolve a company now shows an
    // EMPTY grid where it used to show company 1's data. CompanyScopeMiddleware exists to make that case rare
    // (it resolves the scope for every authenticated request before any controller runs), and it logs a Warning
    // naming the request when an authenticated request cannot resolve one — so an empty grid has a log line
    // beside it rather than being a silent mystery.
    public static class CompanyQueryFilters
    {
        // The twelve, as the report must list them. Referenced by the B2 verification test so the code and the
        // report cannot drift.
        public static readonly string[] PilotEntities =
        {
            nameof(JournalEntry), nameof(SalesInvoice), nameof(PurchaseInvoice), nameof(Customer),
            nameof(Item), nameof(Warehouse), nameof(Quotation), nameof(Lead), nameof(Opportunity),
            nameof(CrmAccount), nameof(BusinessEvent), nameof(Notification),
        };

        // The pilot membership test, used by B4's write guard so the read filter and the write guard cover exactly
        // the same twelve entities. Two lists would drift, and a write guard on an entity with no read filter (or
        // the reverse) would be a half-isolated entity — the most confusing possible state.
        public static bool IsPilotEntity(string clrTypeName)
            => Array.IndexOf(PilotEntities, clrTypeName) >= 0;

        // Entities that carry a CompanyID but are deliberately NOT filtered, with the reason in one word. The
        // full reasoning is in Stage-001-B1-Entity-Isolation-Classification.md; this list exists so a B2 test can
        // assert that none of them acquired a filter by accident.
        public static readonly (string Entity, string Reason)[] DeliberatelyUnfiltered =
        {
            ("StockBalance",           "FromSqlInterpolated UPDLOCK/HOLDLOCK — the sole-writer overselling guard"),
            ("Employee",               "the company is resolved FROM it — filtering is circular"),
            ("AccountingUserRole",     "read to decide permission, and for another company's audience"),
            ("InventoryUserRole",      "read to decide permission, and for another company's audience"),
            ("CrmUserRole",            "read to decide permission"),
            ("BranchUserRole",         "no CompanyID at all; read before any context exists"),
            ("Companies",              "cross-company administration and the worker company scope"),
            ("Branch",                 "validated before a context exists"),
            ("BusinessEventDispatch",  "a QUEUE claimed across every company in one atomic statement"),
            ("FiscalPeriod",           "no CompanyID column — periods may be global (risk R12)"),
        };

        // Applies the pilot filters.
        //
        // `db` is the context whose OnModelCreating is running, and every predicate below reads
        // `db.CompanyScope` rather than a holder passed in as a value. EF Core builds the model ONCE per context
        // type and caches it, but it re-evaluates filter subexpressions rooted at the executing DbContext, so
        // this indirection is what makes each request see its own company. Passing the holder directly compiled
        // the FIRST request's holder into the cached model and filtered the whole process to that one company —
        // a silent cross-tenant leak, caught by
        // Stage1QueryFilterTests.Two_contexts_sharing_the_cached_model_are_filtered_by_their_own_scope.
        public static void Apply(ModelBuilder builder, CrossBuy.Models.Context.CrossDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);

            // ---- Accounting (4) -------------------------------------------------------------------------
            // JournalEntry is the GL header written by JournalEntryService, our sole GL writer. The filter is a
            // READ control; it does not touch posting. AccountingApiController's `int companyId = 1` tampering
            // vector (Hotfix A.1) reads Journals/TrialBalance/Ledger through these entities, so the filter
            // incidentally narrows those reads to the caller's own company — reported in B2, NOT claimed as the
            // hotfix, which is about authorization.
            builder.Entity<JournalEntry>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            builder.Entity<SalesInvoice>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            builder.Entity<PurchaseInvoice>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            builder.Entity<Customer>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            // ---- Inventory (3) --------------------------------------------------------------------------
            // Item is read by the ANONYMOUS storefront, which has no BusinessContext. It is filtered anyway:
            // the storefront takes PublicCompanyRead, which PINS the scope to Store:StoreCompanyId and leaves
            // the filter fully in force (AllowsCrossCompany is false for that kind). So the catalogue reads
            // exactly one configured company through the same predicate as everyone else — it is not an
            // exception to the filter, it is a caller of it.
            builder.Entity<Item>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            builder.Entity<Warehouse>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            // Quotation holds ZERO rows live, so it is the one pilot entity where the filter cannot change any
            // existing read. It is also already onboarded to the event registry by the parallel team.
            builder.Entity<Quotation>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            // ---- CRM (3) --------------------------------------------------------------------------------
            // CrmAccessService.TeamOwnerIdsAsync walks Hierarchical (NOT filtered — no CompanyID, and it is the
            // org-resolution substrate) and then reads these three. The team resolution is unaffected; the
            // records it resolves to are now company-scoped as well.
            builder.Entity<Lead>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            builder.Entity<Opportunity>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            builder.Entity<CrmAccount>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            // ---- Platform (2) ---------------------------------------------------------------------------
            // BusinessEvent is read by the dispatch worker BY ID across every company. That flow holds
            // PlatformDispatch (ADR-023); without it this filter would make the worker mark healthy rows Failed.
            // BusinessEventDispatch — the queue itself — is NOT filtered, deliberately.
            builder.Entity<BusinessEvent>().HasQueryFilter(e => db.CompanyScope.AllowsCrossCompany
                || e.CompanyID == db.CompanyScope.FilterCompanyId);

            // Notification is the only pilot entity with a NULLABLE CompanyID, and the only one whose predicate
            // needed a documented decision first. NULL = owned by no company = invisible to a company-scoped
            // request; never inferred to belong to the reader. See NotificationCompanyPolicy and
            // docs/platform/Stage-001-B3-Notification-Company-Policy.md.
            builder.Entity<Notification>().HasQueryFilter(NotificationCompanyPolicy.QueryFilter(db));
        }
    }
}