using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public interface IConstructionWarehouseExceptionDecisions
{
 Task<ConstructionWarehouseExceptionRequestResult> DecideAsync(int projectId,int exceptionRequestId,string outcome,Guid decisionId,string reason,CancellationToken ct=default);
}
public sealed class ConstructionWarehouseExceptionDecisions(CrossDbContext db,IBusinessContextAccessor contexts,IProjectsAccessService projects,
 IEnumerable<IModuleAccessService> modules,IConstructionWarehouseAccess warehouses,IConstructionAuditService audit):IConstructionWarehouseExceptionDecisions
{
 public async Task<ConstructionWarehouseExceptionRequestResult> DecideAsync(int projectId,int exceptionRequestId,string outcome,Guid decisionId,string reason,CancellationToken ct=default)
 {
  var reader=new ConstructionWarehouseExceptionRequests(db,contexts,projects,modules,warehouses,audit);
  var c=await reader.Actor(ct);
  if(c==null||!await projects.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=projectId},ct))return new(false,"AccessDenied");
  if(projectId<=0||exceptionRequestId<=0||outcome is not ("Approved" or "Rejected")||decisionId==Guid.Empty||string.IsNullOrWhiteSpace(reason)||reason.Trim().Length>1000)return new(false,"InvalidWarehouseExceptionDecision");
  if(db.Database.CurrentTransaction!=null||db.ChangeTracker.HasChanges())return new(false,"CleanUnitOfWorkRequired");
  reason=reason.Trim();
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,projectId,ct))return new(false,"ConcurrencyConflict");
   var current=await reader.Actor(ct);
   if(current==null||current.CompanyId!=c.CompanyId||current.EmployeeId!=c.EmployeeId||current.UserId!=c.UserId||!await projects.CanAsync(current,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=projectId},ct))return new(false,"AccessDenied");
   var request=await db.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.ID==exceptionRequestId&&x.ProjectId==projectId&&x.CompanyID==c.CompanyId,ct);
   if(request==null)return new(false,"WarehouseExceptionRequestNotFound");
   if(request.CreatedBy==c.EmployeeId)return new(false,"SeparateApproverRequired");
   if(!await db.Projects.AsNoTracking().AnyAsync(x=>x.ID==projectId&&x.CompanyID==c.CompanyId&&x.IsActive,ct))return new(false,"AccessDenied");
   var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Company=c.CompanyId,Project=projectId,Request=request.ID,request.SourceFactsVersion,Outcome=outcome,Reason=reason,Actor=c.EmployeeId,c.UserId})));
   var prior=await db.Set<ConstructionWarehouseExceptionDecision>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.DecisionId==decisionId,ct);
   if(prior!=null)return prior.RequestHash==hash?new(true,"WarehouseExceptionDecisionSaved",prior.ID):new(false,"DecisionIdConflict");
   if(await db.Set<ConstructionWarehouseExceptionDecision>().AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.ExceptionRequestId==request.ID,ct))return new(false,"WarehouseExceptionAlreadyDecided");
   if(outcome=="Approved"){
    // Quote authorizes the real approver. Facts omit actor identity; the command witness does not.
    var q=await reader.Quote(c,projectId,request.PurchaseOrderId,request.DestinationSiteId,ct);
    if(!q.Ok)return new(false,q.Code);
    if(q.FactsVersion!=request.SourceFactsVersion||q.DraftId!=request.DraftId||q.BudgetVersionId!=request.BudgetVersionId||q.SourceWarehouseId!=request.SourceWarehouseId||q.CreationVersion!=request.CreationVersion||q.PolicyToken==null||!q.PolicyToken.SequenceEqual(request.PolicyToken))return new(false,"ConcurrencyConflict");
   }
   // Rejection can close stale intent; it never grants authority or impersonates its creator.
   var row=new ConstructionWarehouseExceptionDecision{CompanyID=c.CompanyId,ProjectId=projectId,ExceptionRequestId=request.ID,Outcome=outcome,DecisionId=decisionId,RequestHash=hash,SourceFactsVersion=request.SourceFactsVersion,Reason=reason,CreatedBy=c.EmployeeId!.Value,CreatedAt=DateTime.UtcNow};
   db.Add(row);await db.SaveChangesAsync(ct);
   audit.Record(new[]{ConstructionAuditDraft.Text(c.CompanyId,projectId,nameof(ConstructionWarehouseExceptionDecision),row.ID,null,"DecideWarehouseException",null,JsonSerializer.Serialize(new{row.ExceptionRequestId,row.Outcome,row.SourceFactsVersion,OperationalGrant=false}),ConstructionChangeKind.Created,reason,c.EmployeeId,nameof(ConstructionWarehouseExceptionDecisions),decisionId)});
   await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(true,"WarehouseExceptionDecisionSaved",row.ID);
  }catch(DbUpdateException){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch{db.ChangeTracker.Clear();throw;}
 }
}
