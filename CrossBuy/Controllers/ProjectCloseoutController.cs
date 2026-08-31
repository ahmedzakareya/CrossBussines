using CrossBuy.BL;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
    // =============================================================================================
    // PROJECT CLOSEOUT WORKSPACE — the screen is a rendering of the readiness answer, nothing more.
    //
    // It holds no close conditions of its own. Every blocker it displays comes from
    // IProjectCloseoutService.ReadinessAsync, and the Close action calls the same service, which
    // re-evaluates readiness before it writes. So "the button was enabled" is never the reason a
    // project closed - the service decides, twice, and the screen only reports.
    //
    // A SEPARATE CONTROLLER rather than four more actions on ProjectController. Closeout is its own
    // concern with its own authority - `close` is module-role-only and refused on the membership path -
    // and the project screens are owned and edited by a different work stream. This is not a duplicate
    // project screen; it answers one question the project screens do not ask.
    // =============================================================================================
    [SessionValidation]
    [Route("Project/Closeout")]
    public class ProjectCloseoutController : Controller
    {
        private readonly IProjectCloseoutService _closeout;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
        private readonly IProjectsAccessService _access;
        private readonly IStringLocalizer<CrossBuy.SharedResources> L;

        public ProjectCloseoutController(IProjectCloseoutService closeout,
            CrossBuy.BL.Platform.IBusinessContextAccessor contexts, IProjectsAccessService access,
            IStringLocalizer<CrossBuy.SharedResources> localizer)
        { _closeout = closeout; _contexts = contexts; _access = access; L = localizer; }

        // The COARSE pre-check, stated in the endpoint where a reader - and the authorization
        // analyzer - can see it. The service re-asks the precise per-project question straight after,
        // so this is a second gate rather than the only one. Both are wanted: an endpoint whose guard
        // is invisible at the endpoint is one nobody can audit by reading it.
        private async Task<bool> MayCloseAsync(int projectId, CancellationToken ct)
        {
            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 } || ctx.EmployeeId is not > 0) return false;

            return await _access.CanAsync(ctx, ProjectsActions.Close,
                CrossBuy.Models.Platform.PermissionTarget.ForProject(projectId, ctx.CompanyId), ct);
        }

        // GET /Project/Closeout/{id}
        [HttpGet("{id:int}")]
        public async Task<IActionResult> Index(int id, CancellationToken ct)
        {
            // No gate here beyond the service's own. ReadinessAsync refuses a project this caller may
            // not read and answers with the same shape it uses for "not ready", so a caller cannot tell
            // a foreign project from an unready one by watching this screen.
            var readiness = await _closeout.ReadinessAsync(id, ct);

            ViewBag.ProjectId = id;
            ViewBag.History = await _closeout.HistoryAsync(id, ct);
            return View(readiness);
        }

        [HttpPost("{id:int}/Close")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Close(int id, string reason, CancellationToken ct)
        {
            if (!await MayCloseAsync(id, ct))
            {
                TempData["PrjErr"] = L["You do not have permission to perform this action"].Value;
                return RedirectToAction(nameof(Index), new { id });
            }
            var (ok, error) = await _closeout.CloseAsync(id, reason, ct);
            TempData[ok ? "PrjMsg" : "PrjErr"] = ok
                ? L["The project has been closed"].Value
                : Describe(error);
            return RedirectToAction(nameof(Index), new { id });
        }

        [HttpPost("{id:int}/Reopen")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reopen(int id, string reason, CancellationToken ct)
        {
            if (!await MayCloseAsync(id, ct))
            {
                TempData["PrjErr"] = L["You do not have permission to perform this action"].Value;
                return RedirectToAction(nameof(Index), new { id });
            }
            var (ok, error) = await _closeout.ReopenAsync(id, reason, ct);
            TempData[ok ? "PrjMsg" : "PrjErr"] = ok
                ? L["The project has been reopened"].Value
                : Describe(error);
            return RedirectToAction(nameof(Index), new { id });
        }

        /// The service returns a small set of stable reasons; anything else becomes the generic refusal
        /// so an unexpected message cannot leak internals into a flash banner.
        private string Describe(string? error) => error switch
        {
            ProjectCloseoutService.ReasonRequired => L["A reason is required"].Value,
            ProjectCloseoutService.NotClosed => L["This project is not closed"].Value,
            _ => L["This project is not ready to close"].Value,
        };
    }
}
