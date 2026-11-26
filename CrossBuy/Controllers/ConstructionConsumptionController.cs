using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
namespace CrossBuy.Controllers;
[SessionValidation,Route("Project/MaterialConsumption")]
public sealed class ConstructionConsumptionController(IConstructionConsumptionWorkspace workspace):Controller
{
 [HttpGet,ProjectPerm(ProjectsActions.BudgetManage)]
 public async Task<IActionResult> Index(int projectId)
 {
  var page=await workspace.ReadAsync(projectId,HttpContext.RequestAborted);
  return page==null?Forbid():View("~/Views/Project/MaterialConsumption.cshtml",page);
 }
 private async Task<IActionResult> Page(int project,ConstructionConsumptionInput input,string? error,ConstructionConsumptionPreview? preview=null)
 {
  var page=await workspace.ReadAsync(project,HttpContext.RequestAborted);if(page==null)return Forbid();
  if(error!=null)Response.StatusCode=409;
  return View("~/Views/Project/MaterialConsumption.cshtml",page with {Input=input,Error=error,Preview=preview});
 }
 [HttpPost("Preview"),ValidateAntiForgeryToken,ProjectPerm(ProjectsActions.BudgetManage),RequestSizeLimit(16384)]
 public async Task<IActionResult> Preview(int projectId,ConstructionConsumptionInput input)
 {
  var result=ModelState.IsValid?await workspace.PreviewAsync(projectId,input,HttpContext.RequestAborted):new ConstructionConsumptionPreview(false,"InvalidConsumptionRequest");
  if(result.Code=="AccessDenied")return Forbid();
  input.Version=result.Ok?result.Version:null;
  return await Page(projectId,input,result.Ok?null:result.Code,result.Ok?result:null);
 }
 [HttpPost("Register"),ValidateAntiForgeryToken,ProjectPerm(ProjectsActions.BudgetManage),RequestSizeLimit(16384)]
 public async Task<IActionResult> Register(int projectId,ConstructionConsumptionInput input)
 {
  var result=ModelState.IsValid?await workspace.RegisterAsync(projectId,input,HttpContext.RequestAborted):new ConstructionConsumptionWrite(false,"InvalidConsumptionRequest");
  if(result.Code=="AccessDenied")return Forbid();
  if(!result.Ok){input.Version=null;return await Page(projectId,input,result.Code);}
  TempData["ConstructionConsumptionSaved"]=true;
  return RedirectToAction(nameof(Index),new Microsoft.AspNetCore.Routing.RouteValueDictionary{["projectId"]=projectId,
   ["culture"]=System.Globalization.CultureInfo.CurrentCulture.Name,["ui-culture"]=System.Globalization.CultureInfo.CurrentUICulture.Name});
 }
}
