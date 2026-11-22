namespace CrossBuy.Models.Platform
{
    // Stage 1 Batch B / B3 — the ONLY sanctioned ways to read outside the current company scope.
    //
    // WHY FOUR KINDS AND NOT ONE FLAG
    //
    // B1 found four real flows that a company filter would break, and they are NOT the same kind of operation.
    // Collapsing them into one "ignore filters" switch would mean the anonymous public catalogue and a
    // consolidation report share a permission — so the weakest caller would define the strongest right.
    //
    // The decisive difference is AllowsCrossCompany:
    //
    //   * CrossCompanyAdministration / PlatformDispatch / PlatformMonitoring genuinely UNRESTRICT: they must see
    //     rows belonging to companies other than the scope's.
    //   * PublicCompanyRead does NOT unrestrict. It PINS the scope to one configured company and stays fully
    //     filtered. It exists because the storefront is anonymous — it has no BusinessContext to resolve a
    //     company from — not because it needs to cross companies. It never can.
    public enum CompanyBypassKind
    {
        // An authorized human performing cross-company administration or consolidation. Requires an admin role.
        CrossCompanyAdministration = 0,

        // Trusted platform code draining the outbox. The dispatch queue spans every company in one pass, and the
        // worker then loads each event by id (BusinessEventDispatchWorker), so it must read across companies.
        // Only a System or Worker context may hold it — never an interactive user.
        PlatformDispatch,

        // The Business Event Monitor's elevated cross-company view — a shipped feature for platform admins.
        // Separate from CrossCompanyAdministration because it is read-only observation of the event stream, not
        // business administration, and because it must be revocable on its own.
        PlatformMonitoring,

        // The anonymous public catalogue. NOT a bypass in the isolation sense: it pins the scope to the
        // configured public company and remains filtered. No identity, no elevation, no company from the request.
        PublicCompanyRead,

        // HR0-FOLLOW-UP — the org-tree walk's cross-company READ, so an approver climb is decided from the
        // REQUESTER'S OWN company rather than the ambient request's.
        //
        // WHY IT MUST CROSS COMPANIES EVEN THOUGH IT ENFORCES A BOUNDARY. `Hierarchical` has no CompanyID of
        // its own meaning (B1 classified it CrossCompanyOperational) and the org tree is ONE shared structure
        // that legitimately spans tenants. The approver climb (LeaveWorkflowService.ApproverChainAsync) reads
        // the WHOLE tree and then intersects every candidate against Employee.EmpCompanyID — the requester's
        // own company, read from their row — so a foreign node is SEEN and dropped as a defect rather than
        // auto-approved. HR0 (2026-11-11) added a company query filter to Hierarchical for the org-structure
        // SCREEN; under it the climb could no longer SEE a foreign node when the ambient company differed from
        // the requester's, turning a cross-company graft into a false "top of tree". This kind restores the
        // climb's intended whole-tree read through the audited mechanism instead of the (now absent) filter gap.
        //
        // It is READ-ONLY and crosses companies only to EXCLUDE: nothing cross-company escapes the walk, which
        // returns employee-id sets already intersected to the one requester company. Obtained ONLY through
        // BeginOrgTreeRead (never Begin), held for the smallest possible span around the tree read, and audited.
        OrgTreeRead,
    }

    public static class CompanyBypassKinds
    {
        // The kinds that genuinely see other companies' rows. PublicCompanyRead is deliberately absent.
        public static bool AllowsCrossCompany(this CompanyBypassKind kind) => kind switch
        {
            CompanyBypassKind.CrossCompanyAdministration => true,
            CompanyBypassKind.PlatformDispatch => true,
            CompanyBypassKind.PlatformMonitoring => true,
            CompanyBypassKind.OrgTreeRead => true,        // sees the whole org tree, to intersect foreign nodes OUT
            CompanyBypassKind.PublicCompanyRead => false,
            _ => false,
        };

        // Read-only kinds may not be used to write. PublicCompanyRead and PlatformMonitoring are observation
        // only; PlatformDispatch writes dispatch state, and administration may write by definition.
        public static bool IsReadOnly(this CompanyBypassKind kind) => kind switch
        {
            CompanyBypassKind.PublicCompanyRead => true,
            CompanyBypassKind.PlatformMonitoring => true,
            CompanyBypassKind.OrgTreeRead => true,        // reads the org tree to decide a chain; never writes
            _ => false,
        };
    }

    // The evidence record for one granted bypass. Every field is required by the B3 contract: an audit line that
    // cannot say WHO, WHY, over WHAT and WHEN is not an audit line.
    public sealed class CompanyBypassGrant
    {
        public required CompanyBypassKind Kind { get; init; }
        public required string Reason { get; init; }

        // The actor, or null for PublicCompanyRead (which is anonymous by design and says so).
        public int? ActorEmployeeId { get; init; }
        public string? ActorUserId { get; init; }

        // The company the scope was operating as when the bypass began; null when nothing was resolved.
        public int? ScopeCompanyId { get; init; }

        // For PublicCompanyRead: the single company the scope is pinned to.
        public int? PinnedCompanyId { get; init; }

        public Guid? CorrelationId { get; init; }
        public required DateTime GrantedAtUtc { get; init; }

        public bool AllowsCrossCompany => Kind.AllowsCrossCompany();

        public override string ToString()
            => $"{Kind} by employee={ActorEmployeeId?.ToString() ?? "(anonymous)"} " +
               $"scope={ScopeCompanyId?.ToString() ?? "(unresolved)"}" +
               (PinnedCompanyId.HasValue ? $" pinned={PinnedCompanyId}" : "") +
               $" correlation={CorrelationId} at={GrantedAtUtc:u} reason=\"{Reason}\"";
    }

    // Raised when a bypass is requested without the right to hold it. Deny-by-default: a caller that cannot be
    // authorized gets an exception rather than a silently ordinary (still-filtered) query, because a silent
    // downgrade would look like the bypass worked.
    public sealed class CompanyBypassDeniedException : Exception
    {
        public CompanyBypassDeniedException(CompanyBypassKind kind, string reason) : base(reason) { Kind = kind; }
        public CompanyBypassKind Kind { get; }
    }

    // Configuration for the public catalogue scope. Bound from the "Store" section.
    // THE COMPANY THE TILL SELLS FOR. Same shape and same reason as PublicCatalogOptions below:
    // PosAppController, HyperPosController, HyperController, PosSetupService and PosAccessService
    // each carried their own `private const int PosCompanyId = 1`, reaching 127 lines between them.
    //
    // THE PINNING ITSELF IS NOT THE BUG. A till catalogue and its cash accounts genuinely live under
    // one company while the branches sit under 65-79, so this company is a deployment fact rather
    // than something a request can be asked for. The bug is that the fact was COMPILED IN: it could
    // not be changed without a rebuild, it could not differ between installations, and five copies
    // of it could drift from each other silently.
    //
    // It comes from configuration and nowhere else - never from a route value, a query string, a
    // header or a cookie - exactly as the storefront company does.
    public sealed class PosCompanyOptions
    {
        public int CatalogCompanyId { get; set; } = 1;

        // HM-22 — WHAT ONE LOYALTY POINT IS WORTH AT THE TILL, in the document currency.
        //
        // ZERO MEANS REDEMPTION IS OFF, and that is the default on purpose. The earn rate cannot supply
        // this number: redeeming at 1/earn-rate would hand the customer back 100% of what they spent,
        // which is not a loyalty scheme, it is a refund. Deriving it would therefore be taking a pricing
        // decision on the owner's behalf and hiding it in a formula, so the number is stated or nothing
        // is redeemed.
        //
        // It lives in configuration rather than a column because it is one number for the deployment,
        // the same way CatalogCompanyId is. A per-branch rate is a schema change and is not pretended at.
        public decimal LoyaltyPointValue { get; set; } = 0m;
    }

    public sealed class PublicCatalogOptions
    {
        // The ONE company the anonymous storefront may read. It comes from configuration and nowhere else:
        // never from a route value, a query string, a header or a cookie. StoreController and
        // HomeController.Store each carried `private const int StoreCompanyId = 1`; this is that constant with a
        // name, a home and a test.
        public int StoreCompanyId { get; set; } = 1;

        // When false the storefront is closed rather than falling back to unfiltered reads.
        public bool Enabled { get; set; } = true;
    }
}
