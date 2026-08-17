using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // =============================================================================================
    // Stage 2A Batch B — B4: the ONE read path for bootstrap policy.
    //
    // Same discipline as IPlatformRoleDirectory, for the same reason: three things must be true of every
    // bootstrap decision, and each is a separate mistake waiting to be made once per access service —
    //   1. the NEVER classification is evaluated BEFORE any policy row can produce an allow;
    //   2. POS is refused outright;
    //   3. expiry and IsActive are honoured against ONE clock.
    // Written here once, tested here once.
    //
    // THE ORDERING IS THE SECURITY PROPERTY. If a policy query ran first and the Never check second, a
    // hand-inserted row would produce an allow that a later check merely contradicts — and any refactor
    // that returned early on the policy hit would ship that allow. So Never is evaluated before the
    // database is touched at all, and a mandatory mutation proof moves the check to prove the test fails.
    //
    // NOTHING CONSUMES THIS YET. B6 is not in this increment: the three financial services still carry
    // Mechanism A. This reader changes no production authorization outcome.
    // =============================================================================================

    public interface IBootstrapAccessPolicyReader
    {
        /// <summary>The active policy row for one (company, scope, action), or null. No decision implied.</summary>
        Task<BootstrapAccessPolicy?> GetEffectivePolicyAsync(
            BusinessContext context, string scope, string actionCode, CancellationToken cancellationToken = default);

        /// <summary>Every policy for a company, optionally one scope. Administration and diagnostics.</summary>
        Task<IReadOnlyList<BootstrapAccessPolicy>> ListPoliciesAsync(
            BusinessContext context, string? scope = null, bool includeInactive = false,
            CancellationToken cancellationToken = default);

        /// <summary>THE decision. Never-first, POS-refused, expiry-aware, source-attributed.</summary>
        Task<AuthorizationDecision> ResolveDecisionAsync(
            BusinessContext context, string scope, string actionCode, CancellationToken cancellationToken = default);

        /// <summary>The decision plus the policy's review/expiry state, for future console plumbing.</summary>
        Task<BootstrapDecisionMetadata> GetDecisionMetadataAsync(
            BusinessContext context, string scope, string actionCode, CancellationToken cancellationToken = default);

        /// <summary>Pure classification lookup. No database, no context — the list is code.</summary>
        Task<bool> IsNeverBootstrapOpenAsync(
            string scope, string actionCode, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A decision plus the policy facts a console needs. Deliberately carries no confidential payload: it
    /// explains itself in terms of scope, action, state and dates, never in terms of the data protected.
    /// </summary>
    public sealed class BootstrapDecisionMetadata
    {
        public AuthorizationDecision Decision { get; init; } = null!;
        public int? PolicyId { get; init; }
        public string? State { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime? ReviewedAt { get; init; }
        public DateTime? AcknowledgedAt { get; init; }
        public bool RequiresReview { get; init; }
        public bool IsNeverBootstrapOpen { get; init; }
        public string? NeverReason { get; init; }
        public string? Reason { get; init; }
        public string? SourceSystem { get; init; }
    }

    public sealed class BootstrapAccessPolicyReader : IBootstrapAccessPolicyReader
    {
        private readonly CrossDbContext _db;
        private readonly ILogger<BootstrapAccessPolicyReader> _log;

        public BootstrapAccessPolicyReader(CrossDbContext db, ILogger<BootstrapAccessPolicyReader> log)
        { _db = db; _log = log; }

        /// <summary>
        /// ONE clock per call, matching PlatformRoleDirectory's policy: two expiry comparisons inside one
        /// decision must not straddle a tick, or a policy expiring "exactly now" becomes non-deterministic.
        /// </summary>
        private static DateTime UtcNow() => DateTime.UtcNow;

        public Task<bool> IsNeverBootstrapOpenAsync(
            string scope, string actionCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(NeverBootstrapOpen.Contains(scope, actionCode));

        // =========================================================================================
        // THE DECISION — the order below is mandatory and is what the mutation proof protects
        // =========================================================================================

        public async Task<AuthorizationDecision> ResolveDecisionAsync(
            BusinessContext context, string scope, string actionCode, CancellationToken cancellationToken = default)
        {
            var (decision, _) = await ResolveAsync(context, scope, actionCode, cancellationToken);
            return decision;
        }

        public async Task<BootstrapDecisionMetadata> GetDecisionMetadataAsync(
            BusinessContext context, string scope, string actionCode, CancellationToken cancellationToken = default)
        {
            var (decision, policy) = await ResolveAsync(context, scope, actionCode, cancellationToken);
            var never = NeverBootstrapOpen.Find(scope, actionCode);

            return new BootstrapDecisionMetadata
            {
                Decision = decision,
                PolicyId = policy?.ID,
                State = policy?.State,
                ExpiresAt = policy?.ExpiresAt,
                ReviewedAt = policy?.ReviewedAt,
                AcknowledgedAt = policy?.AcknowledgedAt,
                // A ReviewRequired policy, or a permitting policy nobody has reviewed, needs a human.
                RequiresReview = policy != null
                    && (string.Equals(policy.State, BootstrapPolicyStates.ReviewRequired, StringComparison.Ordinal)
                        || (BootstrapPolicyStates.Permits(policy.State) && policy.ReviewedAt == null)),
                IsNeverBootstrapOpen = never != null,
                NeverReason = never?.Reason,
                Reason = policy?.Reason,
                SourceSystem = policy?.SourceSystem,
            };
        }

        private async Task<(AuthorizationDecision decision, BootstrapAccessPolicy? policy)> ResolveAsync(
            BusinessContext? context, string? scope, string? actionCode, CancellationToken cancellationToken)
        {
            // ---- 1. a resolved context, or nothing. Fail closed; there is no default company. ----
            if (context == null || context.CompanyId <= 0)
                return (AuthorizationDecision.Deny(
                    context?.CompanyId ?? 0, scope ?? "", actionCode ?? "",
                    AuthorizationReasonCodes.CompanyUnresolved), null);

            int companyId = context.CompanyId;

            // ---- 2/3. a known scope ----
            if (string.IsNullOrWhiteSpace(scope) || !EntityRegistry.IsKnownScope(scope))
                return (AuthorizationDecision.Deny(
                    companyId, scope ?? "", actionCode ?? "", AuthorizationReasonCodes.UnknownScope), null);

            // ---- 4. a non-empty action ----
            if (string.IsNullOrWhiteSpace(actionCode))
                return (AuthorizationDecision.Deny(
                    companyId, scope, actionCode ?? "", AuthorizationReasonCodes.UnknownAction), null);

            // ---- 5. NEVER — BEFORE the database is touched at all. ----
            //
            // Deliberately first. A policy row can never produce an allow for one of these, and evaluating the
            // classification before the query means no refactor can accidentally return early on a policy hit.
            if (NeverBootstrapOpen.Contains(scope, actionCode))
            {
                var entry = NeverBootstrapOpen.Find(scope, actionCode);
                _log.LogInformation(
                    "Bootstrap DENIED for {Scope}.{Action} in company {Company}: Never-Bootstrap-Open. {Reason}",
                    scope, actionCode, companyId, entry?.Reason);

                return (AuthorizationDecision.Deny(
                    companyId, scope, actionCode, AuthorizationReasonCodes.NeverBootstrapOpen), null);
            }

            // ---- 6. POS has no bootstrap path, ever. ----
            if (string.Equals(scope, EntityRegistry.ScopePos, StringComparison.Ordinal))
                return (AuthorizationDecision.Deny(
                    companyId, scope, actionCode, AuthorizationReasonCodes.PosHasNoBootstrap), null);

            // ---- 7. the active policy for THIS company. The company predicate is the isolation. ----
            var policy = await _db.BootstrapAccessPolicies.AsNoTracking()
                .Where(p => p.CompanyID == companyId
                         && p.Scope == scope
                         && p.ActionCode == actionCode
                         && p.IsActive)
                .FirstOrDefaultAsync(cancellationToken);

            // No policy = no compatibility. DENY, not allow.
            //
            // This is the inversion Batch B exists to make: today the ABSENCE of configuration means open.
            // Once B6 consults this reader, absence means closed and compatibility must be written down. The
            // B3 seed is what keeps that safe — it records today's implicit openness explicitly before B6
            // ever runs.
            if (policy == null)
                return (AuthorizationDecision.Deny(
                    companyId, scope, actionCode, AuthorizationReasonCodes.NoPolicyConfigured), null);

            // ---- 8. the state must be one we can evaluate ----
            if (!BootstrapPolicyStates.IsKnown(policy.State))
            {
                _log.LogError(
                    "Bootstrap policy {Id} for company {Company} has unknown state '{State}'. Denying and " +
                    "reporting a configuration error — an unevaluable policy must not read as 'no policy'.",
                    policy.ID, companyId, policy.State);

                return (AuthorizationDecision.Misconfigured(
                    companyId, scope, actionCode, AuthorizationReasonCodes.UnknownPolicyState, policy.ID), policy);
            }

            // A Temporary policy with no expiry is a misconfiguration, not a permanent allowance. The CHECK
            // constraint prevents it being written; this catches a row that predates the constraint or arrived
            // by another route, and refuses to honour it.
            if (BootstrapPolicyStates.RequiresExpiry(policy.State) && policy.ExpiresAt == null)
                return (AuthorizationDecision.Misconfigured(
                    companyId, scope, actionCode, AuthorizationReasonCodes.TemporaryWithoutExpiry, policy.ID), policy);

            if (string.Equals(policy.State, BootstrapPolicyStates.Disabled, StringComparison.Ordinal))
                return (AuthorizationDecision.Deny(
                    companyId, scope, actionCode, AuthorizationReasonCodes.PolicyDisabled), policy);

            // ---- 9. expiry, against the single clock ----
            var now = UtcNow();
            if (policy.ExpiresAt.HasValue && policy.ExpiresAt.Value <= now)
            {
                _log.LogInformation(
                    "Bootstrap policy {Id} for {Scope}.{Action} in company {Company} EXPIRED at {Expiry}.",
                    policy.ID, scope, actionCode, companyId, policy.ExpiresAt);

                return (AuthorizationDecision.Deny(
                    companyId, scope, actionCode, AuthorizationReasonCodes.PolicyExpired), policy);
            }

            if (!BootstrapPolicyStates.Permits(policy.State))
                return (AuthorizationDecision.Deny(
                    companyId, scope, actionCode, AuthorizationReasonCodes.PolicyDisabled), policy);

            // ---- 10. an allow, with its source named ----
            var source = AuthorizationDecisionSources.ForState(policy.State);

            // Logged at Information every time, as Mechanism B already does, because a bootstrap allow is
            // TEMPORARY COMPATIBILITY and not the security destination. A quiet one is indistinguishable from
            // a working policy — that is RISK-041.
            _log.LogInformation(
                "Bootstrap ALLOW for {Scope}.{Action} in company {Company} via policy {Id} ({State}). " +
                "This is compatibility, not role authorization.",
                scope, actionCode, companyId, policy.ID, policy.State);

            return (AuthorizationDecision.Allow(
                source, companyId, scope, actionCode, AuthorizationReasonCodes.PolicyPermits,
                policyId: policy.ID), policy);
        }

        // =========================================================================================
        // plain reads
        // =========================================================================================

        public async Task<BootstrapAccessPolicy?> GetEffectivePolicyAsync(
            BusinessContext context, string scope, string actionCode, CancellationToken cancellationToken = default)
        {
            if (context == null || context.CompanyId <= 0) return null;
            if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(actionCode)) return null;

            return await _db.BootstrapAccessPolicies.AsNoTracking()
                .FirstOrDefaultAsync(
                    p => p.CompanyID == context.CompanyId && p.Scope == scope
                      && p.ActionCode == actionCode && p.IsActive,
                    cancellationToken);
        }

        public async Task<IReadOnlyList<BootstrapAccessPolicy>> ListPoliciesAsync(
            BusinessContext context, string? scope = null, bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            if (context == null || context.CompanyId <= 0) return Array.Empty<BootstrapAccessPolicy>();

            var query = _db.BootstrapAccessPolicies.AsNoTracking()
                .Where(p => p.CompanyID == context.CompanyId);

            if (!string.IsNullOrWhiteSpace(scope)) query = query.Where(p => p.Scope == scope);
            if (!includeInactive) query = query.Where(p => p.IsActive);

            return await query
                .OrderBy(p => p.Scope).ThenBy(p => p.ActionCode).ThenByDescending(p => p.ID)
                .ToListAsync(cancellationToken);
        }
    }
}
