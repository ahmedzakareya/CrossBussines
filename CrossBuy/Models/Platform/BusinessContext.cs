namespace CrossBuy.Models.Platform
{
    // Stage 1 Batch A — where a BusinessContext came from.
    //
    // This is not decoration. Three rules depend on being able to tell these apart:
    //   * only a System context may name a company freely (an Http context's company is derived from the
    //     employee, never from a posted value);
    //   * only a System context is subject to SystemContextPolicy, which grants a NARROW action list rather
    //     than the blanket allow it replaced;
    //   * a Worker context must name the company it is processing, so a scheduled job cannot quietly run
    //     against "whatever company happened to be resolved".
    public enum BusinessContextSource
    {
        // An interactive HTTP request. Company and employee are resolved from the authenticated identity.
        Http = 0,

        // A background worker processing ONE explicitly named company, with no interactive user.
        Worker,

        // Trusted platform code with no company-scoped identity of its own (the outbox dispatcher, the
        // registry's own resolution). Subject to SystemContextPolicy.
        System,

        // An external caller (mobile/API integration) acting as a resolved employee.
        Integration,

        // An authenticated EXTERNAL identity - a Client Portal login acting for one customer of one
        // company. Deliberately NOT Integration: that one is an external CALLER acting as a resolved
        // EMPLOYEE, and this one has no employee at all. Deliberately not System either, because a
        // customer is not trusted platform code - IsSystem stays false and every system-only branch
        // (the CompanyIdOverride path in BusinessEventService, SystemContextPolicy) keeps refusing it.
        ExternalPortal,

        // Constructed by a test. Never produced by production code paths.
        Test,
    }

    // Platform Kernel (PKS-001) — the isolation + authorization context every platform service receives.
    //
    // Built by IBusinessContextFactory from the sources the project already uses. No platform service may
    // read HttpContext, Session or claims directly.
    //
    // STAGE 1 RULE: CompanyId is required and is NEVER silently defaulted. The company-1 fallback that used
    // to live in BusinessContextAccessor is gone; an unresolvable company now raises
    // BusinessContextUnresolvedException instead of quietly producing company 1.
    public sealed class BusinessContext
    {
        public required int CompanyId { get; init; }
        public int? BranchId { get; init; }
        public int? EmployeeId { get; init; }
        public string UserId { get; init; } = "";
        public IReadOnlyCollection<string> Roles { get; init; } = Array.Empty<string>();

        // Correlates every event produced by one logical operation (one request / one job run).
        public Guid? CorrelationId { get; init; }

        // SaaS-forward: reserved for the future Tenant above Company. There is no Tenant table and nothing
        // reads this yet — it exists so service CONTRACTS do not change when SaaS lands
        // (PKS-001 "Future TenantId strategy"). Always null today.
        public int? TenantId { get; init; }

        // Stage 1: where this context came from. Defaults to Http so an existing literal construction keeps
        // its previous meaning.
        public BusinessContextSource Source { get; init; } = BusinessContextSource.Http;

        // True only for BusinessContextSource.System. Kept as a property (rather than callers comparing
        // Source) because BusinessEventService and SqlEventDispatchStore already branch on it, and their
        // meaning of "trusted platform code" is exactly this.
        // The PortalUser behind an ExternalPortal context, for audit attribution. Null for every other
        // source. It is NOT an authority: nothing may grant on the strength of it.
        public int? PortalUserId { get; init; }

        // True only for an authenticated external customer identity. Separate from IsSystem on purpose:
        // a consumer asking "is this trusted platform code" and one asking "is this an outsider" are
        // asking opposite questions, and one flag answering both is how they get confused.
        public bool IsExternalPortal => Source == BusinessContextSource.ExternalPortal;

        public bool IsSystem => Source == BusinessContextSource.System;

        public bool IsAuthenticated => EmployeeId.HasValue || !string.IsNullOrEmpty(UserId);

        // ---------------------------------------------------------------------------------------------
        // Factories. Constructing a context by hand is still possible (tests do it), but every production
        // path goes through IBusinessContextFactory, which validates.
        // ---------------------------------------------------------------------------------------------

        // Trusted platform code acting on one company with no interactive identity.
        // NOTE: since Stage 1 this no longer means "may do anything" — see SystemContextPolicy.
        public static BusinessContext ForSystem(int companyId, Guid? correlationId = null)
        {
            if (companyId <= 0)
                throw new BusinessContextUnresolvedException(
                    "A system context requires an explicit company id. There is no default company.");
            return new BusinessContext
            {
                CompanyId = companyId,
                CorrelationId = correlationId,
                Source = BusinessContextSource.System,
            };
        }

        // AN AUTHENTICATED EXTERNAL PORTAL IDENTITY, acting for one company.
        //
        // WHY THIS EXISTS AT ALL: platform services that legitimately need a company - recording a
        // business event, for one - take it from BusinessContext, and the employee factory correctly
        // returns nothing when there is no employee. A portal customer is authenticated and belongs to
        // exactly one company, so the honest answer is a context that says so, not a pretended employee.
        //
        // WHAT IT REFUSES TO BE, and each of these was a tempting shortcut:
        //   * NOT an employee - EmployeeId stays null. Fabricating one would attribute a customer-s
        //     decision to a member of staff in every audit row that follows it;
        //   * NOT System - IsSystem is false by construction, so the override paths reserved for trusted
        //     platform code stay closed to it;
        //   * NOT company-1 - the company is required here exactly as it is everywhere else.
        //
        // THE COMPANY IS THE CALLER-S ONLY IF THE CALLER IS THE PORTAL RESOLVER. This factory takes a
        // company id because it cannot resolve one itself; its contract is that the id came from an
        // ACTIVE PortalUser row reached through the authenticated principal, never from a route, query,
        // form value, CustomerId or anything else the browser can choose.
        public static BusinessContext ForExternalPortal(
            int companyId, string userId, int? portalUserId = null, Guid? correlationId = null)
        {
            if (companyId <= 0)
                throw new BusinessContextUnresolvedException(
                    "An external portal context requires the company resolved from the portal user. " +
                    "There is no default company.");

            // An external context with no identity would be an anonymous caller wearing a company, which
            // is the one thing this must never produce.
            if (string.IsNullOrWhiteSpace(userId))
                throw new BusinessContextUnresolvedException(
                    "An external portal context requires the authenticated identity it was resolved from.");

            return new BusinessContext
            {
                CompanyId = companyId,
                UserId = userId,
                EmployeeId = null,               // stated rather than omitted: there is no employee here
                CorrelationId = correlationId,
                Source = BusinessContextSource.ExternalPortal,
                PortalUserId = portalUserId,
            };
        }

        // A background worker processing one company. Distinct from ForSystem: a worker context is NOT
        // granted SystemContextPolicy's actions, because a worker acts on business data and must be
        // authorized like any other caller (see ForEmployee when a specific employee's rights are needed).
        public static BusinessContext ForWorker(int companyId, Guid? correlationId = null)
        {
            if (companyId <= 0)
                throw new BusinessContextUnresolvedException(
                    "A worker context requires the explicit company id it is processing. There is no default company.");
            return new BusinessContext
            {
                CompanyId = companyId,
                CorrelationId = correlationId,
                Source = BusinessContextSource.Worker,
            };
        }
    }

    // Raised when a company or an identity cannot be resolved for a company-scoped operation.
    //
    // This type exists so the removal of the company-1 fallback is LOUD. Before Stage 1 an unresolved
    // company silently became 1, which meant a POS kitchen screen — whose session blob carries no ids at
    // all — read and wrote company 1's data regardless of the branch's real company.
    public sealed class BusinessContextUnresolvedException : Exception
    {
        public BusinessContextUnresolvedException(string message) : base(message) { }
    }
}