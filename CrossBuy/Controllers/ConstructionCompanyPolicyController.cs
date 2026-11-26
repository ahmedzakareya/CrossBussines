using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
namespace CrossBuy.Controllers;
[SessionValidation,Route("Project/CompanyPolicies")]
public sealed class ConstructionCompanyPolicyController(IConstructionCompanyPolicyWorkspace workspace):Controller
{
 [HttpGet,ProjectPerm(ProjectsActions.Manage)]
 public async Task<IActionResult> Index(){var page=await workspace.ReadAsync(HttpContext.RequestAborted);return page==null?Forbid():View("~/Views/Project/CompanyPolicies.cshtml",page);}
 [HttpPost("Save"),ValidateAntiForgeryToken,ProjectPerm(ProjectsActions.Manage),RequestSizeLimit(16384)]
 public async Task<IActionResult> Save(string key,string mode,string? token,string reason)
 {
  var error=ModelState.IsValid?await workspace.SaveAsync(key??"",mode??"",token,reason??"",HttpContext.RequestAborted):"InvalidPolicy";
  if(error=="AccessDenied")return Forbid();
  if(error!=null){var page=await workspace.ReadAsync(HttpContext.RequestAborted);if(page==null)return Forbid();Response.StatusCode=409;return View("~/Views/Project/CompanyPolicies.cshtml",page with{Error=error});}
  TempData["ConstructionPolicySaved"]=true;
  return RedirectToAction(nameof(Index),new Microsoft.AspNetCore.Routing.RouteValueDictionary{["culture"]=System.Globalization.CultureInfo.CurrentCulture.Name,["ui-culture"]=System.Globalization.CultureInfo.CurrentUICulture.Name});
 }
}
