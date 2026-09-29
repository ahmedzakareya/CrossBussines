using System.Security.Claims;

namespace CrossBuy.Models.Platform
{
    // =============================================================================================
    // CLIENT PORTAL AUTHENTICATION — the platform contract for the EXTERNAL audience.
    //
    // WHY A SECOND SCHEME AT ALL. Until this file, an employee and a customer were told apart by a
    // database lookup AFTER a shared .AspNetCore.Identity.Application cookie had already
    // authenticated them both. That is a check, not a boundary: one cookie meant two audiences, and
    // every internal [Authorize] was one missing lookup away from accepting an outsider. A separate
    // scheme with a separate cookie makes the separation structural — the internal scheme cannot
    // read the portal cookie because it is not looking at that cookie name, and the portal cookie
    // cannot populate the principal internal authorization reads because the portal scheme is not
    // the default one.
    //
    // WHAT IS DELIBERATELY *NOT* HERE. No EmployeeId, no company, no customer and no capability.
    // Those are facts about a row in PortalUsers, and a cookie is a value the holder carries: if
    // the company could be read from the claim, the tenant would be something the client asserts.
    // Every one of them is resolved server-side, per request, by PortalContextAccessor.
    // =============================================================================================
    public static class PortalAuthenticationDefaults
    {
        /// The scheme TAB-3 signs into, and the ONLY scheme the portal policy accepts.
        public const string AuthenticationScheme = "CrossBuy.Portal";

        /// DISTINCT FROM THE INTERNAL COOKIE ON PURPOSE, and a test asserts the difference rather
        /// than trusting it. Sharing ".AspNetCore.Identity.Application" would hand the portal the
        /// internal audience's cookie and undo the whole separation in one line.
        public const string CookieName = "CrossBuy.Portal";

        /// The authorization policy ClientPortalController binds to in 3A-0B. Named the same as the
        /// scheme because there is exactly one of each and a second name would only be a second
        /// thing to keep in step.
        public const string PolicyName = "CrossBuy.Portal";

        // ---- routes, owned by TAB-3 in 3A-0B but NAMED here ----------------------------------
        //
        // The cookie handler needs them at registration time, which is before the controller that
        // serves them exists. Declaring them as constants is what lets both halves agree without
        // either one guessing at the other's spelling.
        public const string LoginPath = "/PortalAccount/Login";
        public const string LogoutPath = "/PortalAccount/Logout";
        public const string AccessDeniedPath = "/PortalAccount/Denied";

        /// The path prefixes that make up the external surface: the portal itself and its door.
        /// SessionValidationMiddleware reads this to know which lane is gated by the portal scheme
        /// rather than by the employee session blob — so the bypass and the scheme cannot drift
        /// apart by one being edited without the other.
        public static readonly IReadOnlyList<string> SurfacePathPrefixes =
            new[] { "/clientportal", "/portalaccount" };

        // ---- the claim contract ----------------------------------------------------------------

        /// THE ONE REQUIRED CLAIM. PortalContextAccessor resolves the whole portal identity from
        /// this and nothing else: it reads ClaimTypes.NameIdentifier off the current principal,
        /// finds the active PortalUser row for that UserId, and takes CompanyID, CustomerId and the
        /// capabilities FROM THE ROW. So the cookie carries a reference, and the database carries
        /// the authority — which is why a stolen or tampered claim buys nothing but a lookup.
        public const string RequiredClaim = ClaimTypes.NameIdentifier;

        /// Claim types a portal principal may NEVER carry, asserted rather than merely intended.
        /// An EmployeeId on an external principal is the exact shape of the escalation this
        /// boundary exists to prevent: it would make a customer indistinguishable from staff to any
        /// code that reads claims instead of asking the platform.
        public static readonly IReadOnlyList<string> ForbiddenClaimTypes =
            new[] { "EmployeeId", "EmployeeID", "employee_id", ClaimTypes.Role };

        /// How long an external session lasts, SLIDING. Matched to the application's own idle
        /// window (AddSession uses eight hours) rather than invented, so the portal does not quietly
        /// keep a different clock from the rest of the product. Tightening it for the external
        /// audience is a policy decision for the owner, not a default to change silently.
        public static readonly TimeSpan ExpireTimeSpan = TimeSpan.FromHours(8);
    }
}
