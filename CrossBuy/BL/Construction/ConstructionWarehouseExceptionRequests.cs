using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public sealed record ConstructionWarehouseExceptionQuote(bool Ok,string Code,string? Version=null,int? ProjectId=null,int? OrderId=null,int? DraftId=null,
 int? BudgetVersionId=null,int? SourceWarehouseId=null,int? DestinationSiteId=null,string? CreationVersion=null,string? MappingVersion=null,byte[]? PolicyToken=null,string? FactsVersion=null)
{
 public bool OperationalGrant=>false;
}
public sealed record ConstructionWarehouseExceptionRequestResult(bool Ok,string Code,int? Id=null){public bool OperationalGrant=>false;}
public interface IConstructionWarehouseExceptionRequests
{
 Task<ConstructionWarehouseExceptionQuote> PreviewAsync(int orderId,int requestedDestinationSiteId,CancellationToken ct=default);
 Task<ConstructionWarehouseExceptionRequestResult> RequestAsync(int orderId,int requestedDestinationSiteId,string witness,Guid requestId,string reason,CancellationToken ct=default);
}
// Intent only for a fresh, unsubmitted canonical construction draft. No approval/admission,
// source-site backfill, order rewrite, reservation, warehouse transfer, stock or GL posting.
public sealed class ConstructionWarehouseExceptionRequests(CrossDbContext db,IBusinessContextAccessor contexts,
 IProjectsAccessService projects,IEnumerable<IModuleAccessService> modules,IConstructionWarehouseAccess warehouses,IConstructionAuditService audit):IConstructionWarehouseExceptionRequests
{
 private static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
 internal async Task<BusinessContext?> Actor(CancellationToken ct)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  if(c is not {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}||c.Source is BusinessContextSource.System or BusinessContextSource.Worker||db.CompanyScope.FilterCompanyId!=c.CompanyId||db.CompanyScope.AllowsCrossCompany)return null;
  var inventory=modules.FirstOrDefault(x=>x.Scope==EntityRegistry.ScopeInventory);
  return inventory!=null&&await inventory.CanAsync(c,"purchase",new PermissionTarget{CompanyId=c.CompanyId},ct)?c:null;
 }
 private async Task<int?> Scope(BusinessContext c,int order,CancellationToken ct)
 {
  var project=await db.PurchaseOrders.AsNoTracking().Where(x=>x.ID==order&&x.CompanyID==c.CompanyId).Select(x=>x.ProjectId).SingleOrDefaultAsync(ct);
  return project is >0&&await projects.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=project.Value},ct)?project:null;
 }
 public async Task<ConstructionWarehouseExceptionQuote> PreviewAsync(int orderId,int requestedDestinationSiteId,CancellationToken ct=default)
 {
  var c=await Actor(ct);if(c==null)return new(false,"AccessDenied");
  if(orderId<=0||requestedDestinationSiteId<=0)return new(false,"InvalidWarehouseExceptionRequest");
  if(db.ChangeTracker.HasChanges()||db.Database.CurrentTransaction!=null)return new(false,"CleanUnitOfWorkRequired");
  var project=await Scope(c,orderId,ct);if(project==null)return new(false,"AccessDenied");
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,project.Value,ct))return new(false,"ConcurrencyConflict");
   var q=await Quote(c,project.Value,orderId,requestedDestinationSiteId,ct);await tx.CommitAsync(ct);return q;
  }catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){return new(false,"ConcurrencyConflict");}
 }
 internal async Task<ConstructionWarehouseExceptionQuote> Quote(BusinessContext c,int project,int orderId,int site,CancellationToken ct,int? allowedApprovalRequestId=null)
 {
  if(db.Database.CurrentTransaction==null)return new(false,"CleanUnitOfWorkRequired");
  var current=await Actor(ct);if(current==null||current.CompanyId!=c.CompanyId||current.EmployeeId!=c.EmployeeId||current.UserId!=c.UserId||!await projects.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=project},ct))return new(false,"AccessDenied");
  var order=await db.PurchaseOrders.AsNoTracking().Include(x=>x.Lines).SingleOrDefaultAsync(x=>x.ID==orderId&&x.CompanyID==c.CompanyId&&x.ProjectId==project,ct);
  if(order==null)return new(false,"SourceChanged");
  var draft=await db.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.PurchaseOrderId==orderId,ct);
  if(draft==null||order.WarehouseId is not >0||order.Lines.Count==0)return new(false,"ConstructionDraftSourceRequired");
  if(order.Status!="Draft"||order.Lines.Any(x=>x.ReceivedQty!=0)||
   await db.GoodsReceipts.AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.PurchaseOrderId==orderId,ct)||
   await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.PurchaseOrderId==orderId,ct)||
   await db.Set<ConstructionPurchaseAdmission>().AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.PurchaseOrderId==orderId,ct))return new(false,"FreshUnsubmittedDraftRequired");
  var approvals=await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.PurchaseOrderId==orderId).ToArrayAsync(ct);
  if(allowedApprovalRequestId==null&&approvals.Length!=0)return new(false,"FreshUnsubmittedDraftRequired");
  if(allowedApprovalRequestId.HasValue){
   if(approvals.Length!=1)return new(false,"ExactPendingPurchaseApprovalRequired");
   var a=approvals[0];
   if(a.ID!=allowedApprovalRequestId||a.ProjectId!=project||a.DraftId!=draft.ID||a.ContentHash!=draft.InitialSourceVersion||
    !await db.InventoryApprovals.AsNoTracking().AnyAsync(x=>x.ID==a.ApprovalId&&x.CompanyID==c.CompanyId&&x.Status=="Pending"&&x.DocType==ConstructionPurchaseApprovalService.DocumentType&&x.PayloadJson==a.PayloadJson&&x.Amount==order.GrandTotal&&x.RequestedByEmployeeId==a.CreatedBy,ct))return new(false,"ExactPendingPurchaseApprovalRequired");
  }
  var creation=ConstructionPurchaseDraftService.CreationHash(order);
  if(creation!=draft.InitialSourceVersion)return new(false,"SourceChanged");
  var baseline=await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.ID==draft.BudgetVersionId&&x.Status=="Approved"&&x.IsBaseline,ct);
  if(baseline==null||baseline.CurrencyId!=draft.CurrencyId||order.CurrencyId!=draft.CurrencyId)return new(false,"ApprovedSourceBaselineRequired");
  var companyCurrency=await db.Companies.AsNoTracking().Where(x=>x.CompanyID==c.CompanyId).Select(x=>x.DefaultCurrencyId).SingleOrDefaultAsync(ct);
  var decimals=await db.Currencies.AsNoTracking().Where(x=>x.ID==draft.CurrencyId).Select(x=>(int?)x.DecimalPlaces).SingleOrDefaultAsync(ct);
  if(companyCurrency!=draft.CurrencyId||decimals!=draft.CurrencyDecimals||draft.CurrencyDecimals is <0 or >8||order.ExchangeRate!=1m)return new(false,"SourceCurrencyChanged");
  var classification=await new ConstructionWarehouseClassifier(db,contexts,projects,warehouses).InspectLockedAsync(c,project,order.WarehouseId.Value,site,ct);
  if(!classification.Ok)return new(false,classification.Code);
  if(classification.CompanyMode!="ApprovedException")return new(false,"WarehouseExceptionPolicyRestricted");
  if(classification.Relation is not (ConstructionWarehouseRelation.OtherProject or ConstructionWarehouseRelation.SameProjectOtherSite))return new(false,"CrossSiteSourceNotProven");
  var policy=await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleAsync(x=>x.CompanyID==c.CompanyId&&x.PolicyKey=="CrossSiteWarehouse",ct);
  var facts=Hash(new{Company=c.CompanyId,Project=project,Order=orderId,Draft=draft.ID,RequestedDestinationSite=site,SourceWarehouse=order.WarehouseId,
   Creation=creation,Mapping=classification.FactsVersion,Policy=policy.ConcurrencyToken,Baseline=baseline.ID,baseline.RowVersion,Currency=draft.CurrencyId,Precision=draft.CurrencyDecimals});
  var version=Hash(new{Facts=facts,Actor=c.EmployeeId,c.UserId});
  return new(true,"Ready",version,project,orderId,draft.ID,baseline.ID,order.WarehouseId.Value,site,creation,classification.Version,policy.ConcurrencyToken.ToArray(),facts);
 }
 public async Task<ConstructionWarehouseExceptionRequestResult> RequestAsync(int orderId,int requestedDestinationSiteId,string witness,Guid requestId,string reason,CancellationToken ct=default)
 {
  var c=await Actor(ct);if(c==null)return new(false,"AccessDenied");
  if(orderId<=0||requestedDestinationSiteId<=0||requestId==Guid.Empty||string.IsNullOrWhiteSpace(witness)||witness.Length!=64||string.IsNullOrWhiteSpace(reason)||reason.Trim().Length>1000)return new(false,"InvalidWarehouseExceptionRequest");
  if(db.ChangeTracker.HasChanges()||db.Database.CurrentTransaction!=null)return new(false,"CleanUnitOfWorkRequired");
  var project=await Scope(c,orderId,ct);if(project==null)return new(false,"AccessDenied");reason=reason.Trim();
  var hash=Hash(new{Company=c.CompanyId,Project=project.Value,Order=orderId,Destination=requestedDestinationSiteId,Actor=c.EmployeeId,c.UserId,Witness=witness,Reason=reason});
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,project.Value,ct))return new(false,"ConcurrencyConflict");
   var prior=await db.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.RequestId==requestId,ct);
   if(prior!=null)return prior.RequestHash==hash?new(true,"WarehouseExceptionRequested",prior.ID):new(false,"WarehouseExceptionRequestIdConflict");
   var q=await Quote(c,project.Value,orderId,requestedDestinationSiteId,ct);if(!q.Ok)return new(false,q.Code);if(q.Version!=witness)return new(false,"ConcurrencyConflict");
   if(await db.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.PurchaseOrderId==orderId&&x.DestinationSiteId==requestedDestinationSiteId&&x.SourceVersion==witness&&x.CreatedBy==c.EmployeeId,ct))return new(false,"WarehouseExceptionAlreadyRequested");
   var row=new ConstructionWarehouseExceptionRequest{CompanyID=c.CompanyId,ProjectId=project.Value,PurchaseOrderId=orderId,DraftId=q.DraftId!.Value,BudgetVersionId=q.BudgetVersionId!.Value,SourceWarehouseId=q.SourceWarehouseId!.Value,
    DestinationSiteId=requestedDestinationSiteId,RequestId=requestId,RequestHash=hash,SourceVersion=witness,SourceFactsVersion=q.FactsVersion!,CreationVersion=q.CreationVersion!,MappingVersion=q.MappingVersion!,PolicyToken=q.PolicyToken!.ToArray(),Reason=reason,CreatedBy=c.EmployeeId!.Value,CreatedAt=DateTime.UtcNow};
   db.Add(row);await db.SaveChangesAsync(ct);
   audit.Record(new[]{ConstructionAuditDraft.Text(c.CompanyId,project.Value,nameof(ConstructionWarehouseExceptionRequest),row.ID,null,"RequestWarehouseException",null,
    JsonSerializer.Serialize(new{row.PurchaseOrderId,row.DraftId,RequestedDestinationSiteId=row.DestinationSiteId,row.SourceWarehouseId,row.SourceVersion,OperationalGrant=false}),ConstructionChangeKind.Created,reason,c.EmployeeId,nameof(ConstructionWarehouseExceptionRequests),requestId)});
   await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(true,"WarehouseExceptionRequested",row.ID);
  }catch(DbUpdateException){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){db.ChangeTracker.Clear();return new(false,"ConcurrencyConflict");}
  catch{db.ChangeTracker.Clear();throw;}
 }
}
