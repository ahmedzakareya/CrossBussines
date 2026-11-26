using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
namespace CrossBuy.Controllers;
[SessionValidation,Route("Project/BudgetTransferPreview")]
public sealed class ConstructionBudgetTransferController(IConstructionBudgetTransferPreview workspace,IConstructionBudgetTransferRequests requests,IConstructionBudgetTransferDecisions decisions,IConstructionBudgetTransferApplications applications):Controller
{
 [HttpGet,ProjectPerm(ProjectsActions.BudgetManage)]
 public async Task<IActionResult> Index(int projectId){var page=await workspace.ReadAsync(projectId,HttpContext.RequestAborted);return page==null?Forbid():View("~/Views/Project/BudgetTransferPreview.cshtml",page);}
 [HttpPost("Preview"),ValidateAntiForgeryToken,ProjectPerm(ProjectsActions.BudgetManage),RequestSizeLimit(16384)]
 public async Task<IActionResult> Preview(int projectId,int fromLineId,int toLineId,decimal amount)
 {
  var quote=ModelState.IsValid?await workspace.PreviewAsync(projectId,fromLineId,toLineId,amount,HttpContext.RequestAborted):new ConstructionTransferQuote(false,"InvalidTransfer");
  if(quote.Code=="AccessDenied")return Forbid();var page=await workspace.ReadAsync(projectId,HttpContext.RequestAborted);if(page==null)return Forbid();
  if(quote.Ok&&(quote.BudgetVersionId!=page.BudgetVersionId||quote.PolicyToken!=page.PolicyToken))quote=new(false,"ConcurrencyConflict");
  if(!quote.Ok)Response.StatusCode=409;
  return View("~/Views/Project/BudgetTransferPreview.cshtml",page with{FromLineId=fromLineId,ToLineId=toLineId,Amount=amount,Quote=quote.Ok?quote:null,Error=quote.Ok?null:quote.Code});
 }
 [HttpPost("Requests"),ValidateAntiForgeryToken,ProjectPerm(ProjectsActions.BudgetManage),RequestSizeLimit(16384)]
 public async Task<IActionResult> Request(int projectId,int fromLineId,int toLineId,decimal amount,string witness,Guid requestId,string reason)
 {
  var result=ModelState.IsValid?await requests.RequestAsync(projectId,fromLineId,toLineId,amount,witness,requestId,reason,HttpContext.RequestAborted):new ConstructionTransferRequestResult(false,"InvalidTransferRequest");
  if(result.Code=="AccessDenied")return Forbid();
  var page=await workspace.ReadAsync(projectId,HttpContext.RequestAborted);if(page==null)return Forbid();
  if(!result.Ok)Response.StatusCode=409;
  return View("~/Views/Project/BudgetTransferPreview.cshtml",page with{RequestSaved=result.Ok,Error=result.Ok?null:result.Code});
 }

 [HttpPost("Decisions"),ValidateAntiForgeryToken,ProjectPerm(ProjectsActions.BudgetManage),RequestSizeLimit(16384)]
 public async Task<IActionResult> Decide(int projectId,int transferRequestId,string outcome,Guid decisionId,string reason)
 {
  var result=ModelState.IsValid?await decisions.DecideAsync(projectId,transferRequestId,outcome,decisionId,reason,HttpContext.RequestAborted):new ConstructionTransferRequestResult(false,"InvalidTransferDecision");
  if(result.Code=="AccessDenied")return Forbid();
  var page=await workspace.ReadAsync(projectId,HttpContext.RequestAborted);if(page==null)return Forbid();
  if(!result.Ok)Response.StatusCode=409;
  return View("~/Views/Project/BudgetTransferPreview.cshtml",page with{DecisionSaved=result.Ok,Error=result.Ok?null:result.Code});
 }

 [HttpPost("Applications"),ValidateAntiForgeryToken,ProjectPerm(ProjectsActions.BudgetManage),RequestSizeLimit(16384)]
 public async Task<IActionResult> Apply(int projectId,int transferDecisionId,Guid applicationId,string reason)
 {
  var result=ModelState.IsValid?await applications.ApplyAsync(projectId,transferDecisionId,applicationId,reason,HttpContext.RequestAborted):new ConstructionTransferRequestResult(false,"InvalidTransferApplication");
  if(result.Code=="AccessDenied")return Forbid();
  var page=await workspace.ReadAsync(projectId,HttpContext.RequestAborted);if(page==null)return Forbid();
  if(!result.Ok)Response.StatusCode=409;
  return View("~/Views/Project/BudgetTransferPreview.cshtml",page with{ApplicationSaved=result.Ok,Error=result.Ok?null:result.Code});
 }

}
