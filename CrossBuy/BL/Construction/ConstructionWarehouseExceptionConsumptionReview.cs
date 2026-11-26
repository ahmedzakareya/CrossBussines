using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public sealed record ConstructionWarehouseConsumptionReview(bool Ok,string Code,string? Version=null,int? DecisionId=null,int? ApprovalRequestId=null)
{
 public bool OperationalGrant=>false;
}
public interface IConstructionWarehouseExceptionConsumptionReview
{
 Task<ConstructionWarehouseConsumptionReview> PreviewAsync(int projectId,int orderId,int decisionId,int approvalRequestId,CancellationToken ct=default);
}
// Read-only pre-consumption boundary. NEVER reserve, approve, persist an application or grant stock access here.
public sealed class ConstructionWarehouseExceptionConsumptionReview(CrossDbContext db,IBusinessContextAccessor contexts,IProjectsAccessService projects,
 IEnumerable<IModuleAccessService> modules,IConstructionWarehouseAccess warehouses,IConstructionAuditService audit):IConstructionWarehouseExceptionConsumptionReview
{
 private ConstructionWarehouseExceptionRequests Reader=>new(db,contexts,projects,modules,warehouses,audit);
 public async Task<ConstructionWarehouseConsumptionReview> PreviewAsync(int projectId,int orderId,int decisionId,int approvalRequestId,CancellationToken ct=default)
 {
  if(projectId<=0||orderId<=0||decisionId<=0||approvalRequestId<=0)return new(false,"InvalidWarehouseConsumptionReview");
  if(db.ChangeTracker.HasChanges()||db.Database.CurrentTransaction!=null)return new(false,"CleanUnitOfWorkRequired");
  var c=await Reader.Actor(ct);if(c==null)return new(false,"AccessDenied");
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,projectId,ct))return new(false,"ConcurrencyConflict");
   var result=await ReadLockedAsync(c,projectId,orderId,decisionId,approvalRequestId,ct);await tx.CommitAsync(ct);return result;
  }catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){return new(false,"ConcurrencyConflict");}
 }
 // Canonical operation caller must own the serializable project transaction and repeat this read at use.
 internal async Task<ConstructionWarehouseConsumptionReview> ReadLockedAsync(BusinessContext c,int projectId,int orderId,int decisionId,int approvalRequestId,CancellationToken ct)
 {
  if(db.Database.CurrentTransaction==null)return new(false,"CleanUnitOfWorkRequired");
  var current=await Reader.Actor(ct);var inventory=modules.FirstOrDefault(x=>x.Scope==EntityRegistry.ScopeInventory);
  if(current==null||current.CompanyId!=c.CompanyId||current.EmployeeId!=c.EmployeeId||current.UserId!=c.UserId||inventory==null||
   !await inventory.CanAsync(current,"manage",new PermissionTarget{CompanyId=c.CompanyId},ct)||
   !await projects.CanAsync(current,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=projectId},ct))return new(false,"AccessDenied");
  var decision=await db.Set<ConstructionWarehouseExceptionDecision>().AsNoTracking().SingleOrDefaultAsync(x=>x.ID==decisionId&&x.CompanyID==c.CompanyId&&x.ProjectId==projectId,ct);
  if(decision==null||decision.Outcome!="Approved")return new(false,"ApprovedWarehouseExceptionRequired");
  var request=await db.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.ID==decision.ExceptionRequestId&&x.CompanyID==c.CompanyId&&x.ProjectId==projectId&&x.PurchaseOrderId==orderId,ct);
  if(request==null||request.CreatedBy==decision.CreatedBy||request.SourceFactsVersion!=decision.SourceFactsVersion)return new(false,"WarehouseExceptionSourceMismatch");
  var approval=await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.ID==approvalRequestId&&x.CompanyID==c.CompanyId&&x.ProjectId==projectId&&x.PurchaseOrderId==orderId&&x.DraftId==request.DraftId,ct);
  var draft=await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x=>x.ID==request.DraftId&&x.CompanyID==c.CompanyId&&x.ProjectId==projectId&&x.PurchaseOrderId==orderId,ct);
  if(approval==null||draft==null)return new(false,"ExactPendingPurchaseApprovalRequired");
  if(approval.CreatedBy==c.EmployeeId||draft.CreatedBy==c.EmployeeId)return new(false,"IndependentApproverRequired");
  var q=await Reader.Quote(current,projectId,orderId,request.DestinationSiteId,ct,approvalRequestId);
  if(!q.Ok)return new(false,q.Code);
  if(q.FactsVersion!=request.SourceFactsVersion||q.CreationVersion!=request.CreationVersion||q.BudgetVersionId!=request.BudgetVersionId||q.SourceWarehouseId!=request.SourceWarehouseId||q.PolicyToken==null||!q.PolicyToken.SequenceEqual(request.PolicyToken))return new(false,"ConcurrencyConflict");
  var version=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Kind="WarehouseConsumptionReview/v1",Company=c.CompanyId,Project=projectId,Order=orderId,Actor=c.EmployeeId,c.UserId,Decision=decision.ID,Request=request.ID,Approval=approval.ID,approval.AllocationId,approval.SourceVersion,approval.ContentHash,Facts=q.FactsVersion})));
  return new(true,"WarehouseConsumptionReviewReady",version,decision.ID,approval.ID);
 }
}
