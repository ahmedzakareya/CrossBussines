using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public interface IConstructionBudgetTransferDecisions
{
 Task<ConstructionTransferRequestResult> DecideAsync(int project,int transferRequestId,string outcome,Guid decisionId,string reason,CancellationToken ct=default);
}
public sealed class ConstructionBudgetTransferDecisionService(CrossDbContext db,IBusinessContextAccessor contexts,IProjectsAccessService access,IConstructionAuditService audit):IConstructionBudgetTransferDecisions
{
 public async Task<ConstructionTransferRequestResult> DecideAsync(int project,int transferRequestId,string outcome,Guid decisionId,string reason,CancellationToken ct=default)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  if(c is not {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}||db.CompanyScope.FilterCompanyId!=c.CompanyId||db.CompanyScope.AllowsCrossCompany||
   !await access.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=project},ct)||!await db.Projects.AsNoTracking().AnyAsync(x=>x.ID==project&&x.CompanyID==c.CompanyId,ct))return new(false,"AccessDenied");
  if(transferRequestId<=0||outcome is not ("Approved" or "Rejected")||decisionId==Guid.Empty||string.IsNullOrWhiteSpace(reason)||reason.Trim().Length>1000)return new(false,"InvalidTransferDecision");
  if(db.Database.CurrentTransaction!=null||db.ChangeTracker.HasChanges())return new(false,"CleanUnitOfWorkRequired");
  reason=reason.Trim();
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,project,ct))return new(false,"ConcurrencyConflict");
   var request=await db.Set<ConstructionBudgetTransferRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.ID==transferRequestId,ct);
   if(request==null)return new(false,"TransferRequestNotFound");
   if(request.CreatedBy==c.EmployeeId)return new(false,"SeparateApproverRequired");
   var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Company=c.CompanyId,Project=project,Request=request.ID,request.SourceVersion,Outcome=outcome,Reason=reason,Actor=c.EmployeeId})));
   var prior=await db.Set<ConstructionBudgetTransferDecision>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.DecisionId==decisionId,ct);
   if(prior!=null)return prior.RequestHash==hash?new(true,"DecisionSaved",prior.ID):new(false,"DecisionIdConflict");
   if(await db.Set<ConstructionBudgetTransferDecision>().AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.TransferRequestId==request.ID,ct))return new(false,"TransferAlreadyDecided");
   if(outcome=="Approved"){
    var quote=await new ConstructionBudgetTransferPreview(db,contexts,access,audit).QuoteLockedAsync(c,project,request.FromLineId,request.ToLineId,request.Amount,ct);
    if(!quote.Ok)return new(false,quote.Code);
    if(quote.Witness!=request.SourceVersion||quote.BudgetVersionId!=request.BudgetVersionId||quote.PolicyToken!=Convert.ToBase64String(request.PolicyToken)||
     quote.FromBefore!=request.FromBefore||quote.ToBefore!=request.ToBefore||quote.FromAfter!=request.FromAfter||quote.ToAfter!=request.ToAfter)return new(false,"ConcurrencyConflict");
   }
   // Rejection may close a stale/disabled request; it grants no authority and still requires
   // a different authorized employee. Approval always rechecks the complete live quote.
   var decision=new ConstructionBudgetTransferDecision{CompanyID=c.CompanyId,ProjectId=project,TransferRequestId=request.ID,Outcome=outcome,
    DecisionId=decisionId,RequestHash=hash,SourceVersion=request.SourceVersion,Reason=reason,CreatedBy=c.EmployeeId!.Value,CreatedAt=DateTime.UtcNow};
   db.Add(decision);await db.SaveChangesAsync(ct);
   audit.Record(new[]{ConstructionAuditDraft.Text(c.CompanyId,project,nameof(ConstructionBudgetTransferDecision),decision.ID,null,"DecideBudgetTransfer",null,
    JsonSerializer.Serialize(new{decision.TransferRequestId,decision.Outcome,decision.SourceVersion,decision.DecisionId}),ConstructionChangeKind.Created,reason,c.EmployeeId,nameof(ConstructionBudgetTransferDecisionService),decisionId)});
   await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(true,"DecisionSaved",decision.ID);
  }catch(DbUpdateException){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch(OverflowException){db.ChangeTracker.Clear();return new(false,"AmountOverflow");}
 }
}
