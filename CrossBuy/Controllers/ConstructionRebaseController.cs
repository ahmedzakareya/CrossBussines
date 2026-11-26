using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace CrossBuy.Controllers;

[SessionValidation]
[Route("Project/ReservationRebases")]
public sealed class ConstructionRebaseController(IConstructionRebaseWorkspaceService workspace) : Controller
{
    [HttpGet, ProjectPerm(ProjectsActions.BudgetManage)]
    public async Task<IActionResult> Index(int projectId, int? orderId, int? proposalId)
    {
        var page = await workspace.ReadAsync(projectId, orderId, proposalId, HttpContext.RequestAborted);
        return page == null ? Forbid() : View("~/Views/Project/ReservationRebases.cshtml", page);
    }
    private async Task<IActionResult> Submit(int projectId, int orderId, ConstructionRebaseInput input, ConstructionRebaseOperation operation)
    {
        var result = ModelState.IsValid
            ? await workspace.SubmitAsync(projectId, orderId, operation, input, HttpContext.RequestAborted)
            : new ConstructionRebaseRequestResult(false, "InvalidRebaseRequest");
        if (result.Code == "AccessDenied") return Forbid();
        if (result.Ok)
        {
            TempData["ConstructionRebaseSaved"] = true;
            return RedirectToAction(nameof(Index), new RouteValueDictionary { ["projectId"] = projectId, ["orderId"] = orderId,
                ["culture"] = System.Globalization.CultureInfo.CurrentCulture.Name,
                ["ui-culture"] = System.Globalization.CultureInfo.CurrentUICulture.Name });
        }
        var page = await workspace.ReadAsync(projectId, orderId, input.ProposalId > 0 ? input.ProposalId : null, HttpContext.RequestAborted);
        if (page == null) return Forbid();
        Response.StatusCode = StatusCodes.Status409Conflict;
        return View("~/Views/Project/ReservationRebases.cshtml", page with { Error = result.Code, FailedOperation = operation, RejectedInput = input });
    }
    [HttpPost("Request"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.BudgetManage), RequestSizeLimit(65536)]
    public Task<IActionResult> Request(int projectId, int orderId, ConstructionRebaseInput input) => Submit(projectId, orderId, input, ConstructionRebaseOperation.Request);
    [HttpPost("Approve"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.BudgetManage), RequestSizeLimit(65536)]
    public Task<IActionResult> Approve(int projectId, int orderId, ConstructionRebaseInput input) => Submit(projectId, orderId, input, ConstructionRebaseOperation.Approve);
    [HttpPost("Reject"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.BudgetManage), RequestSizeLimit(65536)]
    public Task<IActionResult> Reject(int projectId, int orderId, ConstructionRebaseInput input) => Submit(projectId, orderId, input, ConstructionRebaseOperation.Reject);
    [HttpPost("Activate"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.BudgetManage), RequestSizeLimit(65536)]
    public Task<IActionResult> Activate(int projectId, int orderId, ConstructionRebaseInput input) => Submit(projectId, orderId, input, ConstructionRebaseOperation.Activate);
    [HttpPost("PurchaseRequest"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.BudgetManage), InvPerm("purchase"), RequestSizeLimit(65536)]
    public Task<IActionResult> PurchaseRequest(int projectId, int orderId, ConstructionRebaseInput input) => Submit(projectId, orderId, input, ConstructionRebaseOperation.PurchaseRequest);
}
