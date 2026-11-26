using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public sealed record WarehouseSiteOption(int Id,string Code,string Name,string? NameEn);
public sealed record WarehouseExceptionHistory(int Id,string SiteName,string? SiteNameEn,string Reason,DateTime CreatedAt,string Status,bool CanApprove,bool CanReject);
public sealed record WarehouseDecisionOption(int Id,string Reason);
public sealed record ConstructionWarehouseExceptionPage(int ProjectId,int OrderId,string? CompanyMode,bool RequiresBinding,
 IReadOnlyList<WarehouseSiteOption> Sites,int? SelectedSiteId,string? Witness,Guid RequestId,bool CanCreate,
 IReadOnlyList<WarehouseExceptionHistory> History,IReadOnlyList<WarehouseDecisionOption> ReadyDecisions,
 string Reason="",bool Refusal=false,int? FailedRequestId=null,Guid? FailedDecisionId=null,string? FailedOutcome=null);
public interface IConstructionWarehouseExceptionWorkspace
{
 Task<ConstructionWarehouseExceptionPage?> ReadAsync(int projectId,int orderId,int? siteId=null,CancellationToken ct=default);
}
// Scoped UI reader only. Quotes and visible controls never grant execution.
public sealed class ConstructionWarehouseExceptionWorkspace(CrossDbContext db,IBusinessContextAccessor contexts,IProjectsAccessService projects,
 IEnumerable<IModuleAccessService> modules,IConstructionWarehouseAccess warehouses,IConstructionAuditService audit):IConstructionWarehouseExceptionWorkspace
{
 public async Task<ConstructionWarehouseExceptionPage?> ReadAsync(int projectId,int orderId,int? siteId=null,CancellationToken ct=default)
 {
  if(projectId<=0||orderId<=0||db.Database.CurrentTransaction!=null||db.ChangeTracker.HasChanges())return null;
  var reader=new ConstructionWarehouseExceptionRequests(db,contexts,projects,modules,warehouses,audit);var c=await reader.Actor(ct);
  if(c==null||!await projects.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=projectId},ct))return null;
  await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
  if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,projectId,ct))return null;
  var current=await reader.Actor(ct);
  if(current==null||current.CompanyId!=c.CompanyId||current.EmployeeId!=c.EmployeeId||current.UserId!=c.UserId||!await projects.CanAsync(current,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=projectId},ct))return null;
  var order=await db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(x=>x.ID==orderId&&x.ProjectId==projectId&&x.CompanyID==c.CompanyId,ct);
  if(order==null||!await db.Projects.AsNoTracking().AnyAsync(x=>x.ID==projectId&&x.CompanyID==c.CompanyId&&x.IsActive,ct))return null;
  var requires=await ConstructionWarehouseExceptionRequirement.RequiredAsync(db,c.CompanyId,projectId,orderId,ct);
  var mode=await db.Set<ConstructionCompanyPolicy>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.PolicyKey=="CrossSiteWarehouse").Select(x=>x.Mode).SingleOrDefaultAsync(ct);
  var siteRows=await db.Set<ConstructionSite>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==projectId).OrderBy(x=>x.Code).ToArrayAsync(ct);
  var sites=new List<WarehouseSiteOption>();var history=new List<WarehouseExceptionHistory>();var ready=new List<WarehouseDecisionOption>();
  var sourceAllowed=order.WarehouseId is >0&&await warehouses.CanBindAsync(c,order.WarehouseId.Value,ct);
  if(!sourceAllowed){await tx.CommitAsync(ct);return new(projectId,orderId,mode,requires,sites,null,null,Guid.NewGuid(),false,history,ready);}
  foreach(var s in siteRows.Where(x=>x.IsActive&&x.WarehouseId is >0)){
   if(await db.Warehouses.AsNoTracking().AnyAsync(x=>x.ID==s.WarehouseId&&x.CompanyID==c.CompanyId&&x.IsActive,ct)&&await warehouses.CanBindAsync(c,s.WarehouseId!.Value,ct))sites.Add(new(s.ID,s.Code,s.Name,s.NameEn));
  }
  var requests=await db.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==projectId&&x.PurchaseOrderId==orderId).OrderByDescending(x=>x.ID).Take(50).ToArrayAsync(ct);
  var ids=requests.Select(x=>x.ID).ToArray();
  var decisions=await db.Set<ConstructionWarehouseExceptionDecision>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==projectId&&ids.Contains(x.ExceptionRequestId)).ToArrayAsync(ct);
  var decisionIds=decisions.Select(x=>x.ID).ToArray();var bound=await db.Set<ConstructionWarehouseExceptionBinding>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&decisionIds.Contains(x.DecisionId)).Select(x=>x.DecisionId).ToArrayAsync(ct);
  var quotes=new Dictionary<int,ConstructionWarehouseExceptionQuote>();
  async Task<ConstructionWarehouseExceptionQuote> Quote(int id){if(!quotes.TryGetValue(id,out var q)){q=await reader.Quote(c,projectId,orderId,id,ct);quotes[id]=q;}return q;}
  ConstructionWarehouseExceptionQuote? selected=null;
  if(siteId.HasValue&&sites.Any(x=>x.Id==siteId.Value))selected=await Quote(siteId.Value);
  foreach(var request in requests){
   var site=siteRows.SingleOrDefault(x=>x.ID==request.DestinationSiteId);if(site==null)continue;
   var decision=decisions.SingleOrDefault(x=>x.ExceptionRequestId==request.ID);var live=sites.Any(x=>x.Id==site.ID)?await Quote(site.ID):null;
   var fresh=live is {Ok:true}&&live.FactsVersion==request.SourceFactsVersion;
   history.Add(new(request.ID,site.Name,site.NameEn,request.Reason,request.CreatedAt,decision?.Outcome??"Pending",decision==null&&request.CreatedBy!=c.EmployeeId&&fresh,decision==null&&request.CreatedBy!=c.EmployeeId));
   if(decision is {Outcome:"Approved"}&&decision.CreatedBy!=request.CreatedBy&&decision.SourceFactsVersion==request.SourceFactsVersion&&fresh&&!bound.Contains(decision.ID))ready.Add(new(decision.ID,request.Reason));
  }
  var duplicate=selected is {Ok:true}&&await db.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.PurchaseOrderId==orderId&&x.DestinationSiteId==siteId&&x.CreatedBy==c.EmployeeId&&x.SourceVersion==selected.Version,ct);
  await tx.CommitAsync(ct);
  return new(projectId,orderId,mode,requires,sites,siteId,selected?.Version,Guid.NewGuid(),selected is {Ok:true}&&!duplicate,history,ready);
 }
}
