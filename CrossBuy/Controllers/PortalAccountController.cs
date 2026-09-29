using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // THE EXTERNAL DOOR — Wave 3A-0B.
    //
    // WHY A SECOND LOGIN CONTROLLER RATHER THAN A BRANCH IN THE FIRST ONE. AccountController signs
    // in with ASP.NET Identity's APPLICATION scheme; that cookie is what every internal module
    // authorizes against. A customer authenticated through it would be holding the staff audience's
    // credential, and the only thing standing between them and an internal screen would be a session
    // blob. This controller issues a DIFFERENT scheme, so the separation is carried by the cookie
    // itself rather than by a check somebody has to remember.
    //
    // WHAT THE COOKIE CARRIES: one claim, the Identity user id. Nothing else. Company, customer,
    // capabilities and active state are read per request by PortalContextAccessor FROM THE
    // PortalUsers ROW. A cookie that carried the company would be a cookie worth forging; this one
    // is worth nothing but a database lookup, and a link revoked a second ago is refused on the very
    // next request rather than when the cookie eventually expires.
    //
    // NO PasswordSignInAsync ANYWHERE IN THIS FILE. That method signs into the internal scheme as a
    // side effect of checking the password - it is the one call that would silently undo this whole
    // wave. The password is verified with CheckPasswordAsync, and the sign-in is explicit and
    // scheme-named.
    // =============================================================================================
    // THERE IS NO CONTROLLER-LEVEL [AllowAnonymous], and its absence is load-bearing. ASP.NET Core
    // puts controller attributes into the ENDPOINT METADATA, and AuthorizationMiddleware skips
    // authorization entirely when it finds an IAllowAnonymousFilter anywhere in that metadata. A
    // class-level [AllowAnonymous] would therefore have silently disabled [PortalAuthorize] on
    // Logout - the endpoint would have read as protected and refused nobody. Each action declares
    // its own stance instead, so the two anonymous ones are anonymous by statement rather than by
    // inheritance.
    [Route("PortalAccount")]
    public class PortalAccountController : Controller
    {
        private readonly UserManager<Users> _users;
        private readonly CrossDbContext _db;

        public PortalAccountController(UserManager<Users> users, CrossDbContext db)
        { _users = users; _db = db; }

        // ---- GET /PortalAccount/Login -----------------------------------------------------------

        [HttpGet("Login")]
        [AllowAnonymous]
        public IActionResult Login(string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = SafeReturnUrl(returnUrl);
            return View();
        }

        // ---- POST /PortalAccount/Login ----------------------------------------------------------
        //
        // ONE REFUSAL FOR EVERY FAILURE. Unknown user, wrong password, no portal link, a deactivated
        // link and a disabled account all answer with the same message. Telling them apart would let
        // an outsider enumerate which e-mail addresses are customers of this business - and "your
        // password is wrong" versus "you are not a portal user" is exactly that oracle.
        [HttpPost("Login")]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [AnonymousByDesign(
            "This endpoint ESTABLISHES the portal credential, so requiring one would make it " +
            "unreachable by the only people who need it: a customer arrives holding nothing, and " +
            "the CrossBuy.Portal cookie does not exist until this action issues it. It is not an " +
            "unguarded mutator - what authorization does for a protected endpoint, the credential " +
            "check does here, and it is stricter than one check: UserManager.CheckPasswordAsync " +
            "must pass AND an active PortalUsers row must exist, so correct credentials alone do " +
            "not admit an internal user to the portal. It is also anti-forgery protected, it takes " +
            "no id from the request that could name a company, a customer or a portal user, and " +
            "every refusal is identical so it cannot be used to enumerate accounts.")]
        public async Task<IActionResult> Login(string userName, string password, bool rememberMe = false,
                                               string? returnUrl = null, CancellationToken ct = default)
        {
            var safeReturn = SafeReturnUrl(returnUrl);

            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
                return Refused(safeReturn);

            var user = await _users.FindByNameAsync(userName);
            if (user == null) return Refused(safeReturn);

            // The account must be usable at all. IsEndUser is the product's existing "may sign in"
            // flag and IsActive its disable switch; both are honoured here exactly as internal login
            // honours them, because a suspended account must not have a second door.
            if (!user.IsActive || !user.IsEndUser) return Refused(safeReturn);

            // CheckPasswordAsync VERIFIES AND NOTHING ELSE. It issues no cookie and touches no
            // scheme, which is the property that lets the sign-in below be the only one that happens.
            if (!await _users.CheckPasswordAsync(user, password)) return Refused(safeReturn);

            // ELIGIBILITY IS A SECOND, INDEPENDENT REQUIREMENT. Correct credentials prove who the
            // person is; the PortalUsers row is what says this product has an external identity for
            // them. Both, or nothing.
            var link = await ActivePortalLinkAsync(user.Id, ct);
            if (link == null) return Refused(safeReturn);

            // THE PRINCIPAL. One claim, deliberately - see the header. The authentication type must
            // be the portal scheme so the resulting identity reports IsAuthenticated and belongs to
            // the scheme the portal policy names.
            var identity = new ClaimsIdentity(
                new[] { new Claim(PortalAuthenticationDefaults.RequiredClaim, user.Id) },
                PortalAuthenticationDefaults.AuthenticationScheme);

            await HttpContext.SignInAsync(
                PortalAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = rememberMe,
                    IssuedUtc = DateTimeOffset.UtcNow,
                });

            // NO Session["Employee"] IS WRITTEN, and none is cleared either: a member of staff signed
            // in on this browser keeps their own session, because the two surfaces are separate
            // credentials rather than two states of one login.
            return Json(new { success = true, redirectUrl = safeReturn ?? Url.Action("Index", "ClientPortal") });
        }

        // ---- POST /PortalAccount/Logout ---------------------------------------------------------

        // [PortalAuthorize] AND NOT [AllowAnonymous]. Signing out is a mutation, so it needs an
        // identity to act on: an anonymous POST here would be a free endpoint whose only effect is
        // to delete somebody else's cookie if one were ever addressable. It also means the analyzer
        // sees a real authority rather than a declaration - this action is PROTECTED, not excused.
        [HttpPost("Logout")]
        [PortalAuthorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            // THE SCHEME IS NAMED, and that is the whole point of this action. A bare SignOutAsync()
            // signs out the DEFAULT scheme, and HttpContext.Session.Clear() would take the employee
            // session with it - so a customer logging out of the portal would log a colleague out of
            // the ERP on a shared machine. Only the portal cookie is removed.
            await HttpContext.SignOutAsync(PortalAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        // ---- GET /PortalAccount/Denied ----------------------------------------------------------

        // ANONYMOUS OF NECESSITY: this is the cookie handler's AccessDeniedPath. If it required the
        // portal identity, a refused request would be redirected here, refused again, and redirected
        // here again - the denial page would be the one page nobody denied can read.
        [HttpGet("Denied")]
        [AllowAnonymous]
        public IActionResult Denied() => View();

        // ---- helpers -----------------------------------------------------------------------------

        /// The ACTIVE portal link for an identity, or null. The predicate mirrors
        /// PortalContextAccessor exactly: if login accepted an identity the accessor would later
        /// refuse, the customer would sign in successfully and then be told they have no access.
        private Task<PortalUser?> ActivePortalLinkAsync(string userId, CancellationToken ct)
            => _db.Set<PortalUser>().AsNoTracking()
                .Where(p => p.UserId == userId && p.IsActive
                            && p.CompanyID > 0 && p.CustomerId > 0)
                .FirstOrDefaultAsync(ct);

        /// A returnUrl is honoured ONLY when it is local AND inside the portal surface.
        ///
        /// Url.IsLocalUrl alone is not enough here. It would happily accept "/Tasks" - so the portal
        /// login would become a way to aim an external customer at an internal screen, and the
        /// redirect itself would read as though the portal had granted it. Anything else is dropped
        /// silently and the customer lands on the portal home.
        private string? SafeReturnUrl(string? returnUrl)
        {
            if (string.IsNullOrWhiteSpace(returnUrl)) return null;
            if (!Url.IsLocalUrl(returnUrl)) return null;

            var path = returnUrl.Split('?', '#')[0];
            var inSurface = PortalAuthenticationDefaults.SurfacePathPrefixes
                .Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            return inSurface ? returnUrl : null;
        }

        /// The one refusal. Shaped like AccountController's so the page script handles both the same
        /// way, and carrying no hint about which of the checks above failed.
        private IActionResult Refused(string? returnUrl)
        {
            ViewData["ReturnUrl"] = returnUrl;
            return Json(new { success = false, message = CrossBuy.Resources.SharedResources.InvalidLogin });
        }
    }
}
