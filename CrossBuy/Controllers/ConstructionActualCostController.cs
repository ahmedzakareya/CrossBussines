using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
namespace CrossBuy.Controllers;
[SessionValidation]
[Route("Project/ActualCostAllocation")]
public sealed class ConstructionActualCostController(IConstructionActualCostWorkspace workspace) : Controller
{
    [HttpGet, ProjectPerm(ProjectsActions.BudgetManage)]
    public async Task<IActionResult> Index(int projectId, int? lineId)
    {
        var page = await workspace.ReadAsync(projectId, lineId, HttpContext.RequestAborted);
        return page == null ? Forbid() : View("~/Views/Project/ActualCostAllocation.cshtml", page);
    }
    private async Task<IActionResult> Page(int project, int line, ConstructionActualInput input, string? error, ConstructionActualPreview? preview = null)
    {
        var page = await workspace.ReadAsync(project, line, HttpContext.RequestAborted); if (page == null) return Forbid();
        if (preview?.Ok == true && preview.Version != page.Source?.Version) { preview = null; error = "ConcurrencyConflict"; }
        if (error != null) Response.StatusCode = 409;
        return View("~/Views/Project/ActualCostAllocation.cshtml", page with { Input = input, Confirmed = preview?.Ok == true, Error = error });
    }
    [HttpPost("Preview"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.BudgetManage), RequestSizeLimit(131072)]
    public async Task<IActionResult> Preview(int projectId, int lineId, ConstructionActualInput input)
    {
        var result = ModelState.IsValid ? await workspace.PreviewAsync(projectId, lineId, input, HttpContext.RequestAborted) : new ConstructionActualPreview(false, "InvalidAllocationRequest");
        if (result.Code == "AccessDenied") return Forbid();
        input.Version = result.Ok ? result.Version : null;
        return await Page(projectId, lineId, input, result.Ok ? null : result.Code, result);
    }
    [HttpPost("Allocate"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.BudgetManage), RequestSizeLimit(131072)]
    public async Task<IActionResult> Allocate(int projectId, int lineId, ConstructionActualInput input)
    {
        var result = ModelState.IsValid ? await workspace.AllocateAsync(projectId, lineId, input, HttpContext.RequestAborted) : new ConstructionActualWrite(false, "InvalidAllocationRequest");
        if (result.Code == "AccessDenied") return Forbid();
        if (!result.Ok) return await Page(projectId, lineId, input, result.Code);
        TempData["ConstructionActualAllocated"] = true;
        return RedirectToAction(nameof(Index), new Microsoft.AspNetCore.Routing.RouteValueDictionary { ["projectId"] = projectId, ["lineId"] = lineId,
            ["culture"] = System.Globalization.CultureInfo.CurrentCulture.Name, ["ui-culture"] = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }
}
