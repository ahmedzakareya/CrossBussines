using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch D1 / CORRECTION-005 — THE VALIDATED COMPANY SOURCE.
    //
    // WHAT WENT WRONG, MEASURED. Twelve controllers hold the company as a compile-time constant
    // (`private const int DefaultCompanyId = 1;` and its siblings `HrCompanyId`, `PosCompanyId`, `CompanyId`),
    // and 242 of the 388 mutating actions live in them — INCLUDING ALL 150 of the platform's
    // attribute-protected mutating actions. So the coverage metric could report an endpoint as protected while
    // its company came from a literal rather than from the caller.
    //
    // That is not a smaller version of the request-supplied-company problem; it is the same class of defect one
    // step earlier. A request-supplied company can at least be validated. A compile-time company cannot be
    // wrong-for-this-caller, because it was never about the caller at all: on a multi-company install an
    // employee of company 2 either operates on company 1's data or is denied everything, and the role check
    // above it passes either way.
    //
    // WHY A SERVICE AND NOT A HELPER METHOD. The rule has four parts that must not drift apart — resolve from
    // BusinessContext, fail closed when unresolved, validate any request-supplied value, never coerce — and
    // each remediated endpoint needs all four. A static helper copied into controllers would be the
    // "authorization predicate copied into a controller" the brief forbids. This resolves the company; it does
    // NOT decide permissions. Permission decisions stay in the access services.
    public interface IRequestCompanyResolver
    {
        // The company for THIS request, from the resolved BusinessContext.
        //
        // `requestSuppliedCompanyId` is COMPATIBILITY ONLY — a value that arrived on the query string or form
        // because some existing view posts it. It is validated against the resolved context and a mismatch is
        // REFUSED. It is never used as the answer, and never coerced into one.
        Task<CompanyResolution> ResolveAsync(
            int? requestSuppliedCompanyId = null, CancellationToken cancellationToken = default);
    }

    // Deliberately not a bare `int`. A method returning `int` invites `?? 1`, and the failure modes here are
    // distinct outcomes a caller must handle differently — not shades of "no company".
    public sealed record CompanyResolution
    {
        public bool Ok { get; init; }
        public int CompanyId { get; init; }
        public int? EmployeeId { get; init; }
        public int? BranchId { get; init; }

        // Why it failed. Safe to log; NOT safe to render to the caller, because "your company is 2, the record
        // is company 1" tells an attacker the record exists in another company.
        public string? Reason { get; init; }

        public CompanyResolutionFailure Failure { get; init; }

        public static CompanyResolution Resolved(int companyId, int? employeeId, int? branchId) =>
            new() { Ok = true, CompanyId = companyId, EmployeeId = employeeId, BranchId = branchId };

        public static CompanyResolution Unresolved(string reason) =>
            new() { Ok = false, Reason = reason, Failure = CompanyResolutionFailure.Unresolved };

        public static CompanyResolution Mismatch(string reason) =>
            new() { Ok = false, Reason = reason, Failure = CompanyResolutionFailure.CompanyMismatch };
    }

    public enum CompanyResolutionFailure
    {
        None = 0,
        // No signed-in employee/company could be established. Fail closed — read nothing, write nothing.
        Unresolved = 1,
        // A request carried a company that is not the caller's. Refuse; never coerce to the caller's own, which
        // would silently execute a different operation than the one requested.
        CompanyMismatch = 2,
    }

    public sealed class RequestCompanyResolver : IRequestCompanyResolver
    {
        private readonly IBusinessContextAccessor _context;
        private readonly ILogger<RequestCompanyResolver> _log;

        public RequestCompanyResolver(IBusinessContextAccessor context, ILogger<RequestCompanyResolver> log)
        { _context = context; _log = log; }

        public async Task<CompanyResolution> ResolveAsync(
            int? requestSuppliedCompanyId = null, CancellationToken cancellationToken = default)
        {
            // TryGet, not Get: an unresolved identity is a REFUSAL to answer, not an exception to surface to a
            // user. The caller turns it into the denial its own pipeline needs (redirect for MVC, 401 for API).
            var ctx = await _context.TryGetCurrentAsync(cancellationToken);

            if (ctx == null)
            {
                _log.LogWarning("CORRECTION-005: no BusinessContext resolved; the operation is refused with no company assumed.");
                return CompanyResolution.Unresolved("no BusinessContext");
            }

            if (ctx.CompanyId <= 0)
            {
                _log.LogWarning(
                    "CORRECTION-005: BusinessContext resolved for employee {Employee} but carries no company; refused.",
                    ctx.EmployeeId);
                return CompanyResolution.Unresolved("BusinessContext carries no company");
            }

            // The compatibility check. A mismatch is refused rather than corrected: coercing it to the caller's
            // company would carry out a DIFFERENT operation than the one requested, silently.
            if (requestSuppliedCompanyId is > 0 && requestSuppliedCompanyId.Value != ctx.CompanyId)
            {
                _log.LogWarning(
                    "CORRECTION-005: request supplied company {Supplied} but the resolved company is {Resolved} " +
                    "(employee {Employee}); refused, not coerced.",
                    requestSuppliedCompanyId.Value, ctx.CompanyId, ctx.EmployeeId);
                return CompanyResolution.Mismatch("request company does not match the resolved company");
            }

            return CompanyResolution.Resolved(ctx.CompanyId, ctx.EmployeeId, ctx.BranchId);
        }
    }
}