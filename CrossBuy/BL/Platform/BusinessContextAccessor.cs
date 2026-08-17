using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (PKS-001) — the per-request BusinessContext, cached for the scope.
    //
    // STAGE 1 BATCH A: this class no longer resolves anything. All resolution — and all validation — moved to
    // IBusinessContextFactory. What remains here is the per-scope cache and the existing interface, so the
    // ~20 callers that already depend on IBusinessContextAccessor keep compiling unchanged.
    //
    // WHAT WAS REMOVED, and why it mattered:
    //
    //     public const int FallbackCompanyId = 1;
    //     CompanyId = companyId is > 0 ? companyId.Value : FallbackCompanyId,
    //
    // That fallback meant any request whose company could not be resolved silently became company 1. It was
    // not hypothetical: PosAppController's KDS and delivery screens wrote a session blob containing only
    // FullName/Email/ProfileImage — no employee id, no company, no branch — so a kitchen screen on a branch
    // belonging to company 71 resolved to company 1 and every downstream event, notification and timeline
    // read inherited that. Both writers are fixed in this batch, AND the fallback is gone, because fixing
    // only the writers would leave the next incomplete session blob doing the same thing quietly.
    //
    // GetCurrentAsync now returns an UNRESOLVED context's absence honestly. It keeps its non-throwing shape
    // (callers include the notification bell and the layout) by returning a context only when one exists;
    // callers that need a hard failure use IBusinessContextFactory.ForHttpAsync directly.
    public interface IBusinessContextAccessor
    {
        // The current request's context. Throws BusinessContextUnresolvedException when no company can be
        // resolved — the same contract the platform services have always assumed, now actually enforced.
        Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default);

        // Non-throwing variant for code that legitimately runs before sign-in.
        Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default);
    }

    public class BusinessContextAccessor : IBusinessContextAccessor
    {
        private readonly IBusinessContextFactory _factory;
        private BusinessContext? _cached;
        private bool _resolved;

        public BusinessContextAccessor(IBusinessContextFactory factory) { _factory = factory; }

        public async Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            var context = await TryGetCurrentAsync(cancellationToken);
            if (context == null)
                throw new BusinessContextUnresolvedException(
                    "No company could be resolved for this request. Either the user is not signed in, or the " +
                    "signed-in employee has no company assigned. There is no default company.");
            return context;
        }

        public async Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
        {
            // A worker/system scope binds its context EXPLICITLY (BusinessContextFactory.ForWorker/ForSystem),
            // and that binding is the answer — there is no request to rebuild one from. Without this, a
            // background scope fell straight through to HTTP resolution, found no request, and reported "no
            // company" while ICompanyScopeHolder was sitting on the right one. That gap is why
            // TaskOverdueSweepService recorded zero Task.BecameOverdue events.
            //
            // Checked BEFORE the cache, deliberately: something earlier in the scope may already have asked and
            // cached the ABSENCE of an HTTP context, and a later binding must still win rather than be masked
            // by that negative. Re-reading a scoped field costs nothing.
            //
            // HTTP is untouched. ForWorker/ForSystem are never called in a request scope, so ScopeBoundContext
            // is null there and the original path below runs exactly as before.
            var bound = _factory.ScopeBoundContext;
            if (bound != null) return bound;

            // Cached per DI scope = per request, including the NEGATIVE answer: an anonymous request must not
            // re-run the resolution on every call.
            if (_resolved) return _cached;
            _cached = await _factory.TryForHttpAsync(cancellationToken);
            _resolved = true;
            return _cached;
        }
    }
}