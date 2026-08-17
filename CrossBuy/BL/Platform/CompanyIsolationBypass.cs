using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch B / B3 — the controlled bypass. The ONLY way to obtain cross-company visibility, and the only
    // way the anonymous storefront obtains a company scope.
    //
    // It exists because B1 found four flows a company filter would break, two of them Critical:
    //   * BusinessEventDispatchWorker:103 loads events by id with no company predicate, over a queue that spans
    //     every company — a filter would mark healthy rows Failed and burn Attempts;
    //   * NotificationProjectionConsumer:71 is the real idempotency guard and reads (recipient, dedupKey) with no
    //     company predicate — a filter would duplicate a notification on every redelivery;
    //   * the Business Event Monitor's elevated cross-company view is a shipped feature;
    //   * the anonymous storefront reads Items with no BusinessContext at all.
    //
    // WHAT MAKES IT "CONTROLLED" rather than a switch:
    //   1. it is requested explicitly, per kind, and cannot be acquired by accident;
    //   2. it requires an authorized BusinessContext — except PublicCompanyRead, which has no identity BY DESIGN
    //      and cannot cross companies in exchange;
    //   3. it requires a reason, and an empty reason is refused;
    //   4. it is scoped and disposable — it ends deterministically, and it cannot nest;
    //   5. it lives on the SCOPED holder, so two concurrent requests have no shared cell to leak through;
    //   6. every grant is audited with actor, kind, scope, reason, correlation id and timestamp;
    //   7. a missing context DENIES; it never becomes unrestricted access.
    public interface ICompanyIsolationBypass
    {
        // The bypass in force for this scope, or null.
        CompanyBypassGrant? Current { get; }

        // Requests a cross-company bypass. Throws CompanyBypassDeniedException when the policy refuses, when the
        // reason is missing, or when no context is supplied. The returned lease MUST be disposed — use `using`.
        IDisposable Begin(CompanyBypassKind kind, BusinessContext context, string reason);

        // The outbox dispatcher's bypass.
        //
        // DELIBERATE DEVIATION FROM "requires an explicit BusinessContext", with the reason recorded:
        //
        // The dispatcher has no identity and — crucially — NO SINGLE COMPANY. BusinessEventDispatch is one queue
        // for every company, claimed in a single atomic statement, so the pass legitimately spans companies. To
        // hand this method a BusinessContext we would have to invent a CompanyId for it, and the only value
        // available would be a constant — which is precisely the silent company-1 fallback Stage 1 Batch A
        // deleted. Faking a context to satisfy a signature would be worse than not having one.
        //
        // The authorization is therefore STRUCTURAL rather than identity-based, and deliberately narrow:
        //   * it is its own method, so it cannot be reached through Begin();
        //   * CompanyBypassPolicy refuses PlatformDispatch through Begin() for any interactive context;
        //   * the only production call site is BusinessEventDispatchWorker, inside the dispatch batch;
        //   * every grant and release is audited, with the consumer named in the reason.
        IDisposable BeginPlatformDispatch(string reason);

        // Pins the scope to the configured public catalogue company. Deliberately a DIFFERENT method with a
        // DIFFERENT signature: it takes no BusinessContext because the caller is anonymous, and it takes no
        // company because the company comes from configuration and nowhere else.
        //
        // It is not an administrative right and cannot become one — PublicCompanyRead.AllowsCrossCompany is false.
        IDisposable BeginPublicCatalogRead(string reason);

        // The company the public catalogue is pinned to, for callers that need to pass it to an existing
        // company-taking service signature (StoreCatalogService takes an int).
        int PublicCatalogCompanyId { get; }
    }

    // Who may hold which kind. Separated from the mechanism so the rule is readable and testable on its own, and
    // so Batch C/D can tighten it without touching the bypass plumbing.
    public interface ICompanyBypassPolicy
    {
        // null = allowed. A non-null string is the refusal reason, which is logged and thrown.
        string? Refuse(CompanyBypassKind kind, BusinessContext context);
    }

    public sealed class CompanyBypassPolicy : ICompanyBypassPolicy
    {
        // The same roles PlatformOpsAttribute treats as platform operators. Referenced rather than re-listed so
        // the two cannot drift.
        private static readonly string[] AdminRoles = CrossBuy.Models.PlatformOpsAttribute.AdminRoles;

        public string? Refuse(CompanyBypassKind kind, BusinessContext context)
        {
            if (context == null)
                return "A cross-company bypass requires an explicit BusinessContext. A missing context is denied, " +
                       "never treated as unrestricted.";

            switch (kind)
            {
                case CompanyBypassKind.PlatformDispatch:
                    // Trusted platform code only. An interactive user must never hold the dispatcher's right,
                    // because it is the broadest read in the system.
                    //
                    // NOTE: BusinessContextSource.Test is deliberately NOT allowed here, and there is no
                    // test-only allowance anywhere in this policy. A security rule that exempts a context
                    // source tests can construct is a rule the tests cannot prove. Tests reach the dispatch
                    // right the same way production does — through BeginPlatformDispatch, which takes no
                    // context at all.
                    return context.Source is BusinessContextSource.System or BusinessContextSource.Worker
                        ? null
                        : $"PlatformDispatch is reserved for System and Worker contexts; this context is {context.Source}.";

                case CompanyBypassKind.CrossCompanyAdministration:
                case CompanyBypassKind.PlatformMonitoring:
                    // A real, identified admin. Deliberately NOT satisfied by accounting "manage": reading every
                    // company's data is broader than any single module's highest right, and PlatformOpsAttribute
                    // already draws that line for the monitor screen (IsElevated = admin role only).
                    if (!context.IsAuthenticated)
                        return $"{kind} requires an authenticated identity.";
                    if (context.EmployeeId is not > 0)
                        return $"{kind} requires a resolved employee.";
                    return context.Roles.Any(r => AdminRoles.Contains(r, StringComparer.OrdinalIgnoreCase))
                        ? null
                        : $"{kind} requires one of the platform-admin roles [{string.Join(", ", AdminRoles)}].";

                case CompanyBypassKind.PublicCompanyRead:
                    // Never reachable through Begin(); BeginPublicCatalogRead has its own path. Refusing here
                    // stops anyone acquiring the public pin through the authenticated entry point and vice versa.
                    return "PublicCompanyRead is not obtainable through Begin(); use BeginPublicCatalogRead().";

                default:
                    return $"Unknown bypass kind '{kind}'.";
            }
        }
    }

    // The evidence record sink. Separated so a test can assert what was audited without parsing log output, and
    // so a future stage can persist grants without touching the bypass itself.
    public interface ICompanyBypassAudit
    {
        void Granted(CompanyBypassGrant grant);
        void Refused(CompanyBypassKind kind, BusinessContext? context, string reason);
        void Released(CompanyBypassGrant grant, TimeSpan held);
    }

    public sealed class LoggingCompanyBypassAudit : ICompanyBypassAudit
    {
        private readonly ILogger<LoggingCompanyBypassAudit> _log;
        public LoggingCompanyBypassAudit(ILogger<LoggingCompanyBypassAudit> log) { _log = log; }

        public void Granted(CompanyBypassGrant grant)
            // Information, not Debug: a cross-company read is an event an auditor must be able to find.
            => _log.LogInformation(
                "Company-isolation bypass GRANTED: kind={Kind} crossCompany={Cross} actor={Actor} user={User} " +
                "scopeCompany={ScopeCompany} pinnedCompany={PinnedCompany} correlation={Correlation} at={At:u} reason={Reason}",
                grant.Kind, grant.AllowsCrossCompany, grant.ActorEmployeeId, grant.ActorUserId,
                grant.ScopeCompanyId, grant.PinnedCompanyId, grant.CorrelationId, grant.GrantedAtUtc, grant.Reason);

        public void Refused(CompanyBypassKind kind, BusinessContext? context, string reason)
            // Warning: a refused bypass is exactly what an auditor wants to see, as with the monitor's retry.
            => _log.LogWarning(
                "Company-isolation bypass REFUSED: kind={Kind} actor={Actor} source={Source} scopeCompany={Company} reason={Reason}",
                kind, context?.EmployeeId, context?.Source, context?.CompanyId, reason);

        public void Released(CompanyBypassGrant grant, TimeSpan held)
            => _log.LogInformation(
                "Company-isolation bypass RELEASED: kind={Kind} actor={Actor} heldMs={HeldMs} correlation={Correlation}",
                grant.Kind, grant.ActorEmployeeId, (long)held.TotalMilliseconds, grant.CorrelationId);
    }

    public sealed class CompanyIsolationBypass : ICompanyIsolationBypass
    {
        private readonly ICompanyScopeHolder _scope;
        private readonly ICompanyBypassPolicy _policy;
        private readonly ICompanyBypassAudit _audit;
        private readonly PublicCatalogOptions _catalog;

        public CompanyIsolationBypass(
            ICompanyScopeHolder scope, ICompanyBypassPolicy policy, ICompanyBypassAudit audit,
            IOptions<PublicCatalogOptions> catalog)
        { _scope = scope; _policy = policy; _audit = audit; _catalog = catalog.Value; }

        public CompanyBypassGrant? Current => _scope.ActiveBypass;

        public int PublicCatalogCompanyId => _catalog.StoreCompanyId;

        public IDisposable Begin(CompanyBypassKind kind, BusinessContext context, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                const string missing = "A bypass requires a reason so the grant can be audited.";
                _audit.Refused(kind, context, missing);
                throw new CompanyBypassDeniedException(kind, missing);
            }

            var refusal = _policy.Refuse(kind, context);
            if (refusal != null)
            {
                _audit.Refused(kind, context, refusal);
                throw new CompanyBypassDeniedException(kind, refusal);
            }

            var grant = new CompanyBypassGrant
            {
                Kind = kind,
                Reason = reason.Trim(),
                ActorEmployeeId = context.EmployeeId,
                ActorUserId = string.IsNullOrEmpty(context.UserId) ? null : context.UserId,
                ScopeCompanyId = _scope.IsResolved ? _scope.CompanyId : context.CompanyId,
                CorrelationId = context.CorrelationId,
                GrantedAtUtc = DateTime.UtcNow,
            };
            return Grant(grant);
        }

        public IDisposable BeginPlatformDispatch(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                const string missing = "The dispatch bypass requires a reason so the grant can be audited.";
                _audit.Refused(CompanyBypassKind.PlatformDispatch, null, missing);
                throw new CompanyBypassDeniedException(CompanyBypassKind.PlatformDispatch, missing);
            }

            // No actor and no single company — and the audit line states both rather than implying otherwise.
            var grant = new CompanyBypassGrant
            {
                Kind = CompanyBypassKind.PlatformDispatch,
                Reason = reason.Trim(),
                ActorEmployeeId = null,
                ActorUserId = null,
                ScopeCompanyId = _scope.IsResolved ? _scope.CompanyId : null,
                CorrelationId = null,
                GrantedAtUtc = DateTime.UtcNow,
            };
            return Grant(grant);
        }

        public IDisposable BeginPublicCatalogRead(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                const string missing = "The public catalogue scope requires a reason so the grant can be audited.";
                _audit.Refused(CompanyBypassKind.PublicCompanyRead, null, missing);
                throw new CompanyBypassDeniedException(CompanyBypassKind.PublicCompanyRead, missing);
            }
            if (!_catalog.Enabled)
            {
                const string closed = "The public catalogue is disabled (Store:Enabled = false). It is closed rather " +
                                      "than falling back to an unfiltered read.";
                _audit.Refused(CompanyBypassKind.PublicCompanyRead, null, closed);
                throw new CompanyBypassDeniedException(CompanyBypassKind.PublicCompanyRead, closed);
            }
            if (_catalog.StoreCompanyId <= 0)
            {
                var bad = $"Store:StoreCompanyId is {_catalog.StoreCompanyId}, which is not a company. There is no " +
                          "default company — configure it explicitly.";
                _audit.Refused(CompanyBypassKind.PublicCompanyRead, null, bad);
                throw new CompanyBypassDeniedException(CompanyBypassKind.PublicCompanyRead, bad);
            }

            // PIN, do not unrestrict. The scope becomes the configured company and stays fully filtered, so the
            // storefront reads exactly one company's catalogue and cannot be steered to another.
            //
            // Set() throws if the scope is already operating as a DIFFERENT company, which is the behaviour we
            // want: an authenticated staff request must never be silently repointed at the public company.
            if (!_scope.IsResolved) _scope.Set(_catalog.StoreCompanyId, null);
            else if (_scope.CompanyId != _catalog.StoreCompanyId)
                throw new CompanyBypassDeniedException(CompanyBypassKind.PublicCompanyRead,
                    $"This scope already operates as company {_scope.CompanyId}; the public catalogue scope " +
                    $"({_catalog.StoreCompanyId}) cannot be applied over it.");

            var grant = new CompanyBypassGrant
            {
                Kind = CompanyBypassKind.PublicCompanyRead,
                Reason = reason.Trim(),
                ActorEmployeeId = null,          // anonymous BY DESIGN, and the audit line says so
                ActorUserId = null,
                ScopeCompanyId = _catalog.StoreCompanyId,
                PinnedCompanyId = _catalog.StoreCompanyId,
                CorrelationId = null,
                GrantedAtUtc = DateTime.UtcNow,
            };
            return Grant(grant);
        }

        private IDisposable Grant(CompanyBypassGrant grant)
        {
            var lease = _scope.ApplyBypass(grant);
            _audit.Granted(grant);
            return new AuditedLease(lease, grant, _audit);
        }

        // Wraps the holder's lease so RELEASE is audited too — an audit that records grants but not releases
        // cannot answer "was it still open when X happened?".
        private sealed class AuditedLease : IDisposable
        {
            private readonly IDisposable _inner;
            private readonly CompanyBypassGrant _grant;
            private readonly ICompanyBypassAudit _audit;
            private readonly DateTime _startedUtc = DateTime.UtcNow;
            private bool _disposed;

            public AuditedLease(IDisposable inner, CompanyBypassGrant grant, ICompanyBypassAudit audit)
            { _inner = inner; _grant = grant; _audit = audit; }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _inner.Dispose();
                _audit.Released(_grant, DateTime.UtcNow - _startedUtc);
            }
        }
    }
}
