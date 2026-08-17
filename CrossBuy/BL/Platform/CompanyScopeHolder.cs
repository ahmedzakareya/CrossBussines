using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch A — a scoped holder for "which company is this scope operating as".
    //
    // WHY THIS EXISTS AND WHY IT IS SO SMALL
    //
    // Stage 1 Batch B adds EF global query filters. A filter expression has to read the current company from
    // somewhere the DbContext can reach — but it CANNOT read IBusinessContextAccessor, because that accessor
    // queries CrossDbContext.Employee to resolve the company. Wiring it into the DbContext would mean:
    //
    //     resolve the company  ->  query Employee  ->  apply the company filter  ->  resolve the company …
    //
    // a circular dependency, and a filtered read of the very table the resolution depends on.
    //
    // So the filter reads THIS instead: an int? and nothing else. No DbContext, no HTTP, no services, no
    // async. IBusinessContextFactory sets it the moment a context is resolved, and the filter reads it.
    //
    // It is registered SCOPED, and CrossDbContext is registered Scoped with no pooling
    // (Program.cs: AddDbContext(..., ServiceLifetime.Scoped)), so one holder belongs to exactly one
    // DbContext instance and cannot leak between requests or worker iterations.
    //
    // Batch A only POPULATES it. Nothing reads it until Batch B, which is deliberate: the holder ships and
    // is proven to be set correctly before anything depends on it for isolation.
    public interface ICompanyScopeHolder
    {
        // The company this scope operates as, or null when no context has been resolved yet.
        int? CompanyId { get; }

        // The branch, when the scope is branch-specific. Batch B may filter on it for the few entities
        // whose branch semantics are unambiguous.
        int? BranchId { get; }

        // True once a context has been resolved for this scope. Distinguishes "no company" from
        // "company not yet resolved", which Batch B's filters must treat differently: an unresolved scope
        // is a programming error, an unresolvable one is a denied request.
        bool IsResolved { get; }

        // Stage 1 Batch B / B2 — the value the query filters compare against: the resolved company, or 0 when
        // nothing is resolved.
        //
        // It exists because of a concrete EF behaviour, not for tidiness. The obvious predicate —
        //
        //     e => scope.CompanyId != null && e.CompanyID == scope.CompanyId.Value
        //
        // does NOT short-circuit inside EF. Parameter extraction evaluates `CompanyId.Value` while building the
        // query's parameters, before any SQL runs, so an unresolved scope threw
        // `InvalidOperationException: Nullable object must have a value` — a 500 with a message naming nothing,
        // on any request that could not resolve a company.
        //
        // A non-nullable 0 removes the null from the expression entirely: `Set` refuses companyId <= 0, so 0 is a
        // company id that cannot exist, and `WHERE CompanyID = 0` matches no row. Failing closed then costs one
        // parameter and no exception. Also true for Notification, whose CompanyID is nullable: SQL NULL never
        // equals 0, so an unattributed row stays invisible.
        int FilterCompanyId { get; }

        // Called by IBusinessContextFactory. Idempotent for the same company; a CONFLICTING company in one
        // scope is a bug and throws, because it would mean two different tenants shared a DbContext.
        void Set(int companyId, int? branchId);

        // ---- Stage 1 Batch B / B3: the bypass state Batch B's query filters will read ----

        // The bypass currently in force for this scope, or null. Set ONLY by ICompanyIsolationBypass, which
        // authorizes and audits first. Nothing else may write it, which is why there is no setter here.
        CompanyBypassGrant? ActiveBypass { get; }

        // True when the active bypass genuinely crosses companies. This — not "is a bypass active" — is what a
        // query filter must test, because PublicCompanyRead is a bypass that PINS a company rather than
        // unrestricting it.
        bool AllowsCrossCompany { get; }

        // Applied by ICompanyIsolationBypass after its policy check. Internal to the isolation mechanism.
        IDisposable ApplyBypass(CompanyBypassGrant grant);
    }

    public sealed class CompanyScopeHolder : ICompanyScopeHolder
    {
        public int? CompanyId { get; private set; }
        public int? BranchId { get; private set; }
        public bool IsResolved { get; private set; }

        public CompanyBypassGrant? ActiveBypass { get; private set; }
        public bool AllowsCrossCompany => ActiveBypass?.AllowsCrossCompany == true;

        // 0 = "no company resolved", which matches no row. See the interface comment for why this is not simply
        // `CompanyId.Value` guarded by a null check.
        public int FilterCompanyId => CompanyId ?? 0;

        public void Set(int companyId, int? branchId)
        {
            if (companyId <= 0)
                throw new ArgumentOutOfRangeException(nameof(companyId),
                    "A company scope must be a real company id. There is no default company.");

            if (IsResolved && CompanyId != companyId)
                throw new InvalidOperationException(
                    $"This scope is already operating as company {CompanyId} and cannot be changed to {companyId}. " +
                    "Two companies must never share one DI scope or DbContext instance — start a new scope instead.");

            CompanyId = companyId;
            BranchId = branchId;
            IsResolved = true;
        }

        // The bypass lives on the SCOPED holder, not in a static or an AsyncLocal.
        //
        // That choice is the isolation guarantee: one holder belongs to exactly one DI scope, and CrossDbContext is
        // registered Scoped with no pooling, so one holder belongs to exactly one DbContext instance. Two
        // concurrent requests therefore cannot see each other's bypass — there is no shared cell to leak through.
        //
        // A static AsyncLocal would also survive into places nobody intended (a fire-and-forget continuation, a
        // pooled thread) and would be a genuine global. Nesting is refused rather than stacked: two overlapping
        // bypasses in one scope would make "which right is in force" ambiguous at the exact moment it matters.
        public IDisposable ApplyBypass(CompanyBypassGrant grant)
        {
            ArgumentNullException.ThrowIfNull(grant);
            if (ActiveBypass != null)
                throw new InvalidOperationException(
                    $"A {ActiveBypass.Kind} bypass is already active in this scope; {grant.Kind} cannot be nested. " +
                    "End the current bypass first, or use a separate DI scope.");

            ActiveBypass = grant;
            return new BypassLease(this);
        }

        private sealed class BypassLease : IDisposable
        {
            private CompanyScopeHolder? _holder;
            public BypassLease(CompanyScopeHolder holder) { _holder = holder; }

            // Ends with its scope, deterministically. Double-dispose is a no-op so a `using` inside a `finally`
            // cannot throw over the top of a real exception.
            public void Dispose()
            {
                if (_holder == null) return;
                _holder.ActiveBypass = null;
                _holder = null;
            }
        }
    }
}