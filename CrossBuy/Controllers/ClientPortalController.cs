using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Portal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // CLIENT PORTAL — the external surface.
    //
    // WHY THIS IS NOT PortalController. That name is already taken by the post-login chooser between
    // the HR back office and People self-service — an INTERNAL screen. Reusing it would have put an
    // external product inside a controller whose whole purpose is routing staff, which is exactly
    // the confusion this batch exists to prevent.
    //
    // [Authorize] AND NOT [SessionValidation]. SessionValidation is the internal filter: it exists to
    // police the employee session blob, and a portal user has none. Requiring it here would have
    // pushed somebody toward giving customers an employee session — the single worst thing that could
    // happen to this boundary. Authentication is enough at the filter level, because authorization is
    // the portal context, and there is no portal context without a PortalUser row.
    //
    // EVERY ACTION IS SCOPE-ONLY. Not one of these takes a customerId or a companyId. There is
    // nothing for a caller to substitute: the identity decides the scope, and the URL carries no
    // authority at all. That is why there are no id parameters to validate here — the safest
    // parameter is the one that does not exist.
    //
    // A null summary means "no portal identity", and it renders as a refusal rather than an empty
    // dashboard: an external user must never be shown a working-looking portal with nothing in it,
    // because that is indistinguishable from a customer who has no invoices.
    // =============================================================================================
    [Authorize]
    [Route("ClientPortal")]
    public class ClientPortalController : Controller
    {
        private readonly IPortalDataService _portal;

        public ClientPortalController(IPortalDataService portal) { _portal = portal; }

        [HttpGet("")]
        [HttpGet("Index")]
        public async Task<IActionResult> Index(CancellationToken ct = default)
        {
            var home = await _portal.HomeAsync(ct);
            if (home == null) return View("NoAccess");
            return View(home);
        }

        [HttpGet("Quotations")]
        public async Task<IActionResult> Quotations(CancellationToken ct = default)
            => View(await _portal.QuotationsAsync(ct));

        [HttpGet("Projects")]
        public async Task<IActionResult> Projects(CancellationToken ct = default)
            => View(await _portal.ProjectsAsync(ct));

        [HttpGet("Invoices")]
        public async Task<IActionResult> Invoices(CancellationToken ct = default)
            => View(await _portal.InvoicesAsync(ct));

        // JSON twins, for the shell to refresh a panel without a full render. Same service, same
        // scope, same refusals — there is no second read path to keep in step.
        [HttpGet("api/summary")]
        public async Task<IActionResult> Summary(CancellationToken ct = default)
        {
            var home = await _portal.HomeAsync(ct);
            return home == null ? NotFound() : Json(home);
        }
    }
}
