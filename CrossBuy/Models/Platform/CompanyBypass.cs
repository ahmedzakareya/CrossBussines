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
    }

    public static class CompanyBypassKinds
    {
        // The kinds that genuinely see other companies' rows. PublicCompanyRead is deliberately absent.
        public static bool AllowsCrossCompany(this CompanyBypassKind kind) => kind switch
        {
            CompanyBypassKind.CrossCompanyAdministration => true,
            CompanyBypassKind.PlatformDispatch => true,
            CompanyBypassKind.PlatformMonitoring => true,
            CompanyBypassKind.PublicCompanyRead => false,
            _ => false,
        };

        // Read-only kinds may not be used to write. PublicCompanyRead and PlatformMonitoring are observation
        // only; PlatformDispatch writes dispatch state, and administration may write by definition.
        public static bool IsReadOnly(this CompanyBypassKind kind) => kind switch
        {
            CompanyBypassKind.PublicCompanyRead => true,
            CompanyBypassKind.PlatformMonitoring => true,
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
