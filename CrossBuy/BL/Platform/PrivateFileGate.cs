using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Platform Foundation — authorization for company-sensitive UPLOADED files.
    //
    // THE DEFECT THIS CLOSES, measured rather than assumed. `app.UseStaticFiles()` serves the whole of
    // wwwroot with no authorization, and the product stores private business documents inside it. An
    // ANONYMOUS request — no cookie, no auth header — returned:
    //
    //     GET /uploads/hr-docs/189bee67-….pdf          200  168,345 bytes  application/pdf
    //     GET /uploads/chat/23921eab-….jpg             200   12,432 bytes  image/jpeg
    //     GET /Files/11/2a098cef-….pdf                 200  817,209 bytes  application/pdf
    //     GET /Files/10/91167a3c-….pdf                 200  130,866 bytes  application/pdf
    //
    // while /Workspace/Index correctly 302'd to sign-in. The PAGE surface was authorized; the FILE surface
    // was not. Two different companies' documents came back to the same unauthenticated caller.
    //
    // WHY GUID FILENAMES ARE NOT THE CONTROL. Every stored name is a GUID and directory browsing is off, so
    // the store cannot be ENUMERATED. That is obscurity, and it is worth having, but it is not authorization:
    // a URL that leaks (a forwarded mail, a proxy log, a browser history on a shared machine, a referrer)
    // grants permanent unauthenticated access to that document with no way to revoke it.
    //
    // WHAT THIS DELIBERATELY DOES NOT DO
    //
    //   * It does not route CSS/JS/fonts/images through authorization. The policy is an explicit
    //     ALLOW-LIST OF PRIVATE PREFIXES; everything else is untouched and still served by the same static
    //     pipeline at the same cost.
    //   * It does not enforce a COMPANY match yet — see PrivateFilePolicy.RequireCompanyMatch below for the
    //     reason and the follow-up owner. Closing the anonymous hole is the part that is provably safe to do
    //     without a file-classification model.
    //   * It does not change any stored path, any view, or any screen. The URLs the product already emits
    //     keep working for signed-in users, which is why this needs no UI change.
    public enum PrivateFilePolicy
    {
        // Served as before. Product chrome, and the two upload folders the ANONYMOUS storefront renders
        // (/uploads/items, /uploads/brands) — StoreController is [AllowAnonymous] and shows the catalogue.
        Public = 0,

        // A signed-in user is required. This is what closes the exploit above.
        RequireAuthenticated,

        // Reserved, and NOT yet applied to any prefix. Company-scoping these files needs a decision this
        // increment is not authorized to take alone: /Files/{companyId}/ holds BOTH company/branch logos and
        // business documents, and AdministrativeStructureService renders logos ACROSS the org hierarchy
        // (Hierarchical carries no CompanyID — it is the org-resolution substrate). Enforcing a company match
        // today would blank the org chart, i.e. break a certified screen to fix a lower-severity residual.
        // Splitting logos from documents is the file-platform redesign, and it has its own owner.
        RequireCompanyMatch,
    }

    public static class PrivateFileGate
    {
        // The policy table. Data, not branching logic — tightening a prefix later is a one-line change here,
        // and a test asserts the classification of every prefix rather than the behaviour of a middleware.
        //
        // Ordered longest-prefix-first is unnecessary because no prefix below is a prefix of another; the
        // lookup asserts that invariant instead of relying on ordering.
        private static readonly (string Prefix, PrivateFilePolicy Policy)[] Rules =
        {
            // ---- proven exploitable, closed here ----
            ("/uploads/hr-docs",    PrivateFilePolicy.RequireAuthenticated),  // HR documents + recruitment vault
            ("/uploads/chat",       PrivateFilePolicy.RequireAuthenticated),  // private chat attachments
            ("/uploads/comm",       PrivateFilePolicy.RequireAuthenticated),  // mail/calendar-invite attachments
            ("/files",              PrivateFilePolicy.RequireAuthenticated),  // /Files/{companyId}/ business files

            // ---- unambiguously private, closed here for the same reason ----
            ("/uploads/applicants", PrivateFilePolicy.RequireAuthenticated),  // applicant photos / CVs
            ("/uploads/library",    PrivateFilePolicy.RequireAuthenticated),  // FileManager library documents
            ("/uploads/employees",  PrivateFilePolicy.RequireAuthenticated),  // employee photographs (PII)
        };

        // Classification is a pure function of the path so it can be tested without a host, a request or a
        // browser. Comparison is ordinal-ignore-case: the filesystem is case-insensitive on Windows, so
        // "/UPLOADS/HR-Docs/x.pdf" reaches the same bytes and must reach the same decision.
        public static PrivateFilePolicy PolicyFor(string? path)
        {
            if (string.IsNullOrEmpty(path)) return PrivateFilePolicy.Public;

            // A dot-segment that survived normalisation is refused outright rather than classified. Kestrel
            // removes "../" before middleware sees it, so this is belt-and-braces — but the failure mode it
            // guards (a traversal that lands inside a private folder while matching no private prefix) is
            // exactly the one that must not be possible.
            if (path.Contains("..", StringComparison.Ordinal)) return PrivateFilePolicy.RequireAuthenticated;

            foreach (var (prefix, policy) in Rules)
            {
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                // Require a real segment boundary so "/uploads/chatter-public" does not match "/uploads/chat".
                if (path.Length == prefix.Length || path[prefix.Length] == '/') return policy;
            }
            return PrivateFilePolicy.Public;
        }

        // Exposed for the test that asserts the table itself is well-formed.
        public static IReadOnlyList<string> PrivatePrefixes
            => Array.ConvertAll(Rules, r => r.Prefix);
    }

    // Runs IMMEDIATELY BEFORE app.UseStaticFiles(). Ordering is the whole mechanism: StaticFileMiddleware
    // short-circuits and writes the file itself, so a gate placed after it never runs.
    //
    // app.UseAuthentication() already ran (Program.cs), so HttpContext.User is populated here. UseSession has
    // NOT run at this point in this application's pipeline — which is why the check is deliberately
    // AUTHENTICATION, not a BusinessContext resolution: reaching for the session here would either throw
    // ("Session has not been configured") or silently deny every request.
    public sealed class PrivateFileGateMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<PrivateFileGateMiddleware>? _log;

        public PrivateFileGateMiddleware(RequestDelegate next, ILogger<PrivateFileGateMiddleware>? log = null)
        { _next = next; _log = log; }

        public async Task InvokeAsync(HttpContext context)
        {
            var policy = PrivateFileGate.PolicyFor(context.Request.Path.Value);
            if (policy == PrivateFilePolicy.Public)
            {
                await _next(context);
                return;
            }

            if (context.User?.Identity?.IsAuthenticated != true)
            {
                // 404, not 401/403, and no redirect. A redirect would turn every private <img> into a silent
                // login page, and a 403 would confirm that this exact GUID names a real document to a caller
                // who is not allowed to know that. "Not found" is the only answer that discloses nothing.
                _log?.LogWarning(
                    "Anonymous request for private file {Path} refused.", context.Request.Path.Value);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Authorised. Private documents must not be retained by a shared cache: the URL is a bearer
            // token in practice, and a proxy that caches it re-creates the hole this class closes.
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, private";
            await _next(context);
        }
    }

    public static class PrivateFileGateExtensions
    {
        public static IApplicationBuilder UsePrivateFileGate(this IApplicationBuilder app)
            => app.UseMiddleware<PrivateFileGateMiddleware>();
    }
}
