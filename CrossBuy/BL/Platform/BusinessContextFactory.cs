using System.Security.Claims;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch A — the ONE pipeline that creates a BusinessContext.
    //
    // Before Stage 1 there was one resolver (BusinessContextAccessor) that did two jobs: read the HTTP
    // identity, and silently substitute company 1 when it could not. Both jobs move here, and the second one
    // is deleted:
    //
    //   * ForHttpAsync   — resolves and VALIDATES the interactive identity. Unresolvable ⇒ throws.
    //   * ForWorker      — an explicitly named company, no session, no HTTP.
    //   * ForSystem      — trusted platform code. Subject to SystemContextPolicy, not a blanket allow.
    //   * ForEmployeeAsync — a specific employee's rights, evaluated OUTSIDE a request. This is what makes a
    //                        background permission check per-recipient rather than per-process.
    //
    // Every method that resolves a company also publishes it to ICompanyScopeHolder, so Batch B's query
    // filters have a value to read without reaching back into this service.
    public interface IBusinessContextFactory
    {
        // Interactive request. Throws BusinessContextUnresolvedException when the company or the identity
        // cannot be established — it NEVER returns company 1 as a consolation.
        Task<BusinessContext> ForHttpAsync(CancellationToken cancellationToken = default);

        // Same as ForHttpAsync but returns null instead of throwing. For callers that legitimately run
        // before sign-in (the login page, an anonymous endpoint) and must not blow up.
        Task<BusinessContext?> TryForHttpAsync(CancellationToken cancellationToken = default);

        BusinessContext ForWorker(int companyId, Guid? correlationId = null);

        BusinessContext ForSystem(int companyId, Guid? correlationId = null);

        // The context ForWorker/ForSystem bound to THIS DI scope, or null when nothing has bound one (every
        // HTTP scope, and any scope a worker forgot to bind). IBusinessContextAccessor consults it before
        // attempting HTTP resolution, which is what lets a background worker resolve a context at all.
        // Null is the fail-closed answer: an unbound worker scope resolves nothing rather than a default.
        BusinessContext? ScopeBoundContext { get; }

        // A named employee's context, resolved from the database with no HTTP involvement. Returns null when
        // the employee does not exist or carries no company — a caller must then EXCLUDE that employee, not
        // fall back to anything.
        Task<BusinessContext?> ForEmployeeAsync(
            int employeeId, int? fallbackCompanyIdForOrphanRow = null, CancellationToken cancellationToken = default);
    }

    public class BusinessContextFactory : IBusinessContextFactory
    {
        private readonly IHttpContextAccessor _http;
        private readonly CrossDbContext _db;
        private readonly ICompanyScopeHolder _scope;
        private readonly ILogger<BusinessContextFactory> _log;

        // One correlation id per DI scope = per request (or per hosted-service scope), so every event
        // produced by one logical operation shares it.
        private readonly Guid _correlationId = Guid.NewGuid();

        // Deliberately does NOT take IEmployeeService. The old accessor used it purely to map a user id to an
        // employee, which is one indexed predicate — and IEmployeeService.GetEmployeeByUserIdAsync also
        // Include()s policy assignments and maps a full view model, none of which a context needs. Doing the
        // lookup directly keeps this class self-contained (no service graph to stand up in a test) and avoids
        // loading rows the caller will not read.
        public BusinessContextFactory(
            IHttpContextAccessor http, CrossDbContext db,
            ICompanyScopeHolder scope, ILogger<BusinessContextFactory> log)
        { _http = http; _db = db; _scope = scope; _log = log; }

        // -----------------------------------------------------------------------------------------------
        // HTTP
        // -----------------------------------------------------------------------------------------------
        public async Task<BusinessContext> ForHttpAsync(CancellationToken cancellationToken = default)
        {
            var context = await ResolveHttpAsync(cancellationToken);
            if (context == null)
                throw new BusinessContextUnresolvedException(
                    "No company could be resolved for this request. Either the user is not signed in, or the " +
                    "signed-in employee has no company assigned (Employee.EmpCompanyID). There is no default " +
                    "company: the previous silent fallback to company 1 was removed in Stage 1.");
            return context;
        }

        public Task<BusinessContext?> TryForHttpAsync(CancellationToken cancellationToken = default)
            => ResolveHttpAsync(cancellationToken);

        private async Task<BusinessContext?> ResolveHttpAsync(CancellationToken cancellationToken)
        {
            var httpContext = _http.HttpContext;
            int? employeeId = null, sessionCompanyId = null, sessionBranchId = null;
            string userId = "";

            // ---- source 1: the "Employee" session blob (the source the access services have always used) ----
            // Treated as a HINT, not as authority: the blob can be stale, and PosAppController writes one with
            // no ids at all for the KDS/delivery screens (Stage 1 fixed those two writers, but a session
            // created before that fix can still be in flight).
            // Stage 1 Hotfix A.1 — the session read is GUARDED, because `HttpContext.Session` is a property whose
            // GETTER throws `InvalidOperationException("Session has not been configured…")` when the session feature
            // is absent. `?.` cannot help with that: the null-conditional protects against a null HttpContext, not
            // against a throwing property.
            //
            // It matters on exactly the path this hotfix secures. A JWT request to /api has no session cookie, and
            // any context where UseSession has not run (a filter ordered before it, a hosted-service scope, a test
            // host) would make the factory THROW instead of falling through to source 2 — so an unauthenticated or
            // unresolvable request would surface as a 500 rather than a clean deny. Failing closed requires
            // returning null here, not raising.
            //
            // CompanyScopeMiddleware already guards the same call for the same reason; the factory did not.
            string? sessionJson = null;
            try { sessionJson = httpContext?.Session?.GetString("Employee"); }
            catch (InvalidOperationException) { /* no session feature on this path — fall through to claims */ }
            if (!string.IsNullOrEmpty(sessionJson))
            {
                EmployeeViewModel? vm = null;
                try { vm = JsonSerializer.Deserialize<EmployeeViewModel>(sessionJson); }
                catch { /* a malformed blob simply falls through to source 2 */ }
                if (vm != null)
                {
                    employeeId = vm.ID > 0 ? vm.ID : null;
                    sessionCompanyId = vm.EmpCompanyID is > 0 ? vm.EmpCompanyID : null;
                    sessionBranchId = vm.BranchID;
                    userId = vm.UserId ?? "";
                }
            }

            // ---- source 2: claims -> IEmployeeService -> Employee row ----
            if (string.IsNullOrEmpty(userId))
                userId = httpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

            if (employeeId == null && !string.IsNullOrEmpty(userId))
            {
                employeeId = await _db.Employee.AsNoTracking()
                    .Where(e => e.UserId == userId)
                    .Select(e => (int?)e.ID)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (employeeId == null) return null;   // no identity ⇒ no company-scoped context

            // ---- the Employee row is the AUTHORITY for company and branch ----
            // Stage 1 change: this read is no longer a "top-up when the session was incomplete". It always
            // runs, because validating the employee↔company relationship is the point. A session blob that
            // claims a company the employee does not belong to must not win.
            var row = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == employeeId.Value)
                .Select(e => new { e.EmpCompanyID, e.BranchID, e.UserId, e.IsActive })
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null)
            {
                _log.LogWarning("Session/claims resolved employee {EmployeeId}, but no such Employee row exists.", employeeId);
                return null;
            }
            if (!row.IsActive)
            {
                _log.LogWarning("Employee {EmployeeId} is inactive; no business context is produced.", employeeId);
                return null;
            }
            if (row.EmpCompanyID <= 0)
            {
                _log.LogWarning(
                    "Employee {EmployeeId} has no company (EmpCompanyID = {Company}); no business context is produced. " +
                    "This previously resolved to company 1.", employeeId, row.EmpCompanyID);
                return null;
            }

            // A session blob disagreeing with the Employee row is worth knowing about: it means the user's
            // company changed while they were signed in, or the blob was written by a path that guesses.
            if (sessionCompanyId.HasValue && sessionCompanyId.Value != row.EmpCompanyID)
                _log.LogWarning(
                    "Session company {SessionCompany} for employee {EmployeeId} disagrees with Employee.EmpCompanyID " +
                    "{RowCompany}; the Employee row wins.", sessionCompanyId, employeeId, row.EmpCompanyID);

            // Branch: prefer the row, but honour a session branch that is a REAL branch of this company.
            // The POS lanes legitimately operate on a branch other than the employee's default.
            int? branchId = row.BranchID;
            if (sessionBranchId.HasValue && sessionBranchId != row.BranchID)
            {
                bool belongs = await _db.Branches.AsNoTracking()
                    .AnyAsync(b => b.ID == sessionBranchId.Value && b.CompanyID == row.EmpCompanyID, cancellationToken);
                if (belongs) branchId = sessionBranchId;
                else _log.LogWarning(
                    "Session branch {SessionBranch} does not belong to company {Company}; using the employee's own branch.",
                    sessionBranchId, row.EmpCompanyID);
            }

            var roles = httpContext?.User?.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().ToArray()
                        ?? Array.Empty<string>();

            var context = new BusinessContext
            {
                CompanyId = row.EmpCompanyID,
                BranchId = branchId,
                EmployeeId = employeeId,
                UserId = string.IsNullOrEmpty(userId) ? (row.UserId ?? "") : userId,
                Roles = roles,
                CorrelationId = _correlationId,
                TenantId = null,
                Source = BusinessContextSource.Http,
            };
            Publish(context);
            return context;
        }

        // -----------------------------------------------------------------------------------------------
        // Worker / System
        // -----------------------------------------------------------------------------------------------
        public BusinessContext ForWorker(int companyId, Guid? correlationId = null)
        {
            var context = BusinessContext.ForWorker(companyId, correlationId ?? _correlationId);
            Publish(context);
            Bind(context);
            return context;
        }

        public BusinessContext ForSystem(int companyId, Guid? correlationId = null)
        {
            var context = BusinessContext.ForSystem(companyId, correlationId ?? _correlationId);
            Publish(context);
            Bind(context);
            return context;
        }

        // -----------------------------------------------------------------------------------------------
        // Scope binding — what makes a worker's context RESOLVABLE, not merely published.
        //
        // THE DEFECT THIS CLOSES. Publish() told ICompanyScopeHolder which company the scope operates as, so
        // the query filters worked and a worker read the right rows. But it then DISCARDED the BusinessContext
        // itself, and IBusinessContextAccessor — the seam IBusinessEventService.RecordAsync resolves through —
        // knew only how to rebuild a context from HTTP. A background sweep therefore had a company but no
        // resolvable context: TaskOverdueSweepService produced its notifications and recorded ZERO
        // Task.BecameOverdue events, reporting EventsSkippedNoContext instead.
        //
        // WHY A SCOPED FIELD AND NOT AN AMBIENT/ASYNCLOCAL SLOT. This type is registered AddScoped, and
        // WorkerScope.ForCompany creates a NEW DI scope per company — so "this scope's context" is already a
        // per-company, per-iteration cell that the container creates and destroys for us. An AsyncLocal would
        // add a second lifetime to reason about (push/restore, flow across Task.Run, leakage between parallel
        // iterations sharing an execution context) to model something the DI scope models exactly. It would
        // also be the process-global mutable company state this platform has twice been burned by. Nothing to
        // restore, nothing to clear: the field dies with the scope.
        //
        // FIRST BINDING WINS, mirroring ICompanyScopeHolder. Publish() already logs-and-keeps the first company
        // when a second, different one is set in one scope. If this field took the LAST binding instead, the
        // holder would filter for company A while the recorded event claimed company B — the divergence is
        // worse than either value alone. `??=` keeps the two in agreement by construction.
        //
        // NOT set on the HTTP path. TryForHttpAsync publishes too, but binding there would change the shape of
        // a code path this fix has no business touching: an HTTP scope leaves this null and the accessor takes
        // its original route, unchanged.
        private BusinessContext? _scopeBound;

        /// The context explicitly bound to THIS DI scope by a non-HTTP caller, or null in an HTTP/unbound scope.
        public BusinessContext? ScopeBoundContext => _scopeBound;

        private void Bind(BusinessContext context) => _scopeBound ??= context;

        // -----------------------------------------------------------------------------------------------
        // A specific employee, with no HTTP request
        // -----------------------------------------------------------------------------------------------
        public async Task<BusinessContext?> ForEmployeeAsync(
            int employeeId, int? fallbackCompanyIdForOrphanRow = null, CancellationToken cancellationToken = default)
        {
            if (employeeId <= 0) return null;

            var row = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == employeeId)
                .Select(e => new { e.UserId, e.BranchID, e.EmpCompanyID, e.IsActive })
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null) return null;

            // An employee row whose EmpCompanyID is 0 is a data defect, not a routine condition. The caller
            // may supply the company it is ALREADY processing (a worker knows it), and that is the only
            // permitted substitution — it is an explicit argument, never an ambient default, and it is
            // logged. Passing nothing means "exclude this employee", which is the safe answer.
            int companyId = row.EmpCompanyID;
            if (companyId <= 0)
            {
                if (fallbackCompanyIdForOrphanRow is not > 0)
                {
                    _log.LogWarning(
                        "Employee {EmployeeId} has no company and no explicit company was supplied; no context produced.",
                        employeeId);
                    return null;
                }
                companyId = fallbackCompanyIdForOrphanRow.Value;
                _log.LogWarning(
                    "Employee {EmployeeId} has no company (EmpCompanyID = 0); using the caller's explicit company {Company}.",
                    employeeId, companyId);
            }

            // Inactive employees are excluded rather than authorized. A notification to a leaver is both a
            // data leak and a dead end.
            if (!row.IsActive)
            {
                _log.LogDebug("Employee {EmployeeId} is inactive; excluded from permission evaluation.", employeeId);
                return null;
            }

            // Source = Integration, NOT System: this context represents a real employee's rights and must be
            // authorized like any other caller. Marking it System would hand it SystemContextPolicy's grants
            // and defeat the per-recipient check this method exists to enable.
            return new BusinessContext
            {
                CompanyId = companyId,
                BranchId = row.BranchID,
                EmployeeId = employeeId,
                UserId = row.UserId ?? "",
                Roles = Array.Empty<string>(),
                CorrelationId = _correlationId,
                Source = BusinessContextSource.Integration,
            };
            // Deliberately NOT published to ICompanyScopeHolder: this context belongs to a recipient being
            // evaluated, not to the scope doing the evaluating. Publishing it would repoint Batch B's query
            // filters at someone else's company midway through a dispatch pass.
        }

        private void Publish(BusinessContext context)
        {
            // Best-effort: a conflicting company in one scope is a genuine bug and Set() throws, but that must
            // not take down a request that was otherwise fine. It is logged at Error because it means two
            // tenants touched one DbContext.
            try { _scope.Set(context.CompanyId, context.BranchId); }
            catch (InvalidOperationException ex)
            {
                _log.LogError(ex, "Two different companies were resolved in one DI scope. This is a bug — " +
                                  "the scope keeps its first company.");
            }
        }
    }
}