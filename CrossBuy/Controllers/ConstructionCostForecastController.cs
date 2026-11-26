using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
namespace CrossBuy.Controllers;
[SessionValidation,Route("Project/CostForecast")]
public sealed class ConstructionCostForecastController(IConstructionCostForecastWorkspace workspace,IConstructionCostForecastService forecasts):Controller
{
 private const string Surface="~/Views/Project/CostForecast.cshtml";
 [HttpGet,ProjectPerm(ProjectsActions.BudgetView)]
 public async Task<IActionResult> Index(int projectId)
 {
  var page=await workspace.ReadAsync(projectId,HttpContext.RequestAborted);return page==null?Forbid():View(Surface,page with{Saved=TempData["ConstructionForecastSaved"] is true});
 }
 [HttpPost("Preview"),ValidateAntiForgeryToken,ProjectPerm(ProjectsActions.BudgetManage),RequestSizeLimit(524288),RequestFormLimits(ValueCountLimit=10000)]
 public async Task<IActionResult> Preview(int projectId,List<ConstructionForecastEstimate> estimates,Guid requestId)
 {
  estimates ??=new();
  if(estimates.Any(x=>x==null))ModelState.AddModelError("estimates",string.Empty);
  var page=await workspace.ReadAsync(projectId,HttpContext.RequestAborted);if(page==null)return Forbid();
  var quote=ModelState.IsValid&&estimates.Count<=1000?await forecasts.PreviewAsync(projectId,estimates,HttpContext.RequestAborted):new ConstructionForecastPreview(false,"InvalidForecastRequest");
  if(quote.Code=="AccessDenied")return Forbid();
  if(quote.Ok&&(quote.BudgetVersionId!=page.BaselineId||page.PolicyMode!="ManualAuthorOwned"))quote=new(false,"ConcurrencyConflict");
  if(!quote.Ok)Response.StatusCode=409;
  return View(Surface,page with{Estimates=estimates.Where(x=>x!=null).ToArray(),Preview=quote.Ok?quote:null,Error=quote.Ok?null:quote.Code,RequestId=requestId==Guid.Empty?Guid.NewGuid():requestId});
 }
 [HttpPost("Save"),ValidateAntiForgeryToken,ProjectPerm(ProjectsActions.BudgetManage),RequestSizeLimit(524288),RequestFormLimits(ValueCountLimit=10000)]
 public async Task<IActionResult> Save(int projectId,List<ConstructionForecastEstimate> estimates,string witness,Guid requestId,int? previousSnapshotId,string reason)
 {
  estimates ??=new();
  if(estimates.Any(x=>x==null))ModelState.AddModelError("estimates",string.Empty);
  var result=ModelState.IsValid&&estimates.Count<=1000?await forecasts.SaveAsync(projectId,estimates,witness,requestId,previousSnapshotId,reason,HttpContext.RequestAborted):new ConstructionForecastSave(false,"InvalidForecastRequest");
  if(result.Code=="AccessDenied")return Forbid();
  if(result.Ok){TempData["ConstructionForecastSaved"]=true;return RedirectToAction(nameof(Index),new Microsoft.AspNetCore.Routing.RouteValueDictionary{["projectId"]=projectId,["culture"]=System.Globalization.CultureInfo.CurrentCulture.Name,["ui-culture"]=System.Globalization.CultureInfo.CurrentUICulture.Name});}
  var page=await workspace.ReadAsync(projectId,HttpContext.RequestAborted);if(page==null)return Forbid();Response.StatusCode=409;
  return View(Surface,page with{Estimates=estimates.Where(x=>x!=null).ToArray(),Error=result.Code,RequestId=requestId});
 }
}
