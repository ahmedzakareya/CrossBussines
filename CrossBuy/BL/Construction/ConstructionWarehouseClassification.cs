using System.Security.Cryptography;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;

public enum ConstructionWarehouseRelation
{
 Unmapped, InactiveMapping, UnprovenMapping, AmbiguousMapping,
 SameSite, SameProjectOtherSite, SameProjectDestinationUnspecified, OtherProject
}
// This is a read-only classification, NEVER a warehouse-use grant or exception approval.
// No other-project names/site IDs are returned to the target-project viewer.
public sealed record ConstructionWarehouseClassification(bool Ok,string Code,
 ConstructionWarehouseRelation? Relation=null,string? CompanyMode=null,int? DestinationSiteId=null,string? Version=null,string? FactsVersion=null)
{
 public bool OperationalGrant=>false;
}
public interface IConstructionWarehouseClassifier
{
 Task<ConstructionWarehouseClassification> InspectAsync(int projectId,int warehouseId,int? destinationSiteId=null,CancellationToken ct=default);
}
public sealed class ConstructionWarehouseClassifier(CrossDbContext db,IBusinessContextAccessor contexts,
 IProjectsAccessService access,IConstructionWarehouseAccess warehouses):IConstructionWarehouseClassifier
{
 private static ConstructionWarehouseClassification Refuse(string code)=>new(false,code);
 public async Task<ConstructionWarehouseClassification> InspectAsync(int projectId,int warehouseId,int? destinationSiteId=null,CancellationToken ct=default)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  if(c is not {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}||c.Source is BusinessContextSource.System or BusinessContextSource.Worker||db.CompanyScope.FilterCompanyId!=c.CompanyId||db.CompanyScope.AllowsCrossCompany||
   !await access.CanAsync(c,ProjectsActions.BudgetView,new PermissionTarget{ProjectId=projectId},ct))return Refuse("AccessDenied");
  if(projectId<=0||warehouseId<=0||destinationSiteId is <=0)return Refuse("InvalidWarehouseScope");
  if(db.Database.CurrentTransaction!=null||db.ChangeTracker.HasChanges())return Refuse("CleanUnitOfWorkRequired");
  try{
   await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
   if(!await ConstructionCommitmentReservationService.AcquireProjectLock(db,c,projectId,ct))return Refuse("ConcurrencyConflict");
   var result=await InspectLockedAsync(c,projectId,warehouseId,destinationSiteId,ct);
   await tx.CommitAsync(ct);return result;
  }catch(Microsoft.Data.SqlClient.SqlException ex)when(ex.Number is 1205 or 1222){return Refuse("ConcurrencyConflict");}
 }
 // Internal composition boundary: caller must own the serializable transaction and project lock.
 // No public borrowed-transaction flag and no permission/identity checks are skipped.
 internal async Task<ConstructionWarehouseClassification> InspectLockedAsync(BusinessContext c,int projectId,int warehouseId,int? destinationSiteId,CancellationToken ct)
 {
  if(db.Database.CurrentTransaction==null)return Refuse("CleanUnitOfWorkRequired");
  var current=await contexts.TryGetCurrentAsync(ct);
  if(current==null||current.CompanyId!=c.CompanyId||current.EmployeeId!=c.EmployeeId||current.UserId!=c.UserId||current.Source!=c.Source||
   c is not {IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0}||c.Source is BusinessContextSource.System or BusinessContextSource.Worker||
   db.CompanyScope.FilterCompanyId!=c.CompanyId||db.CompanyScope.AllowsCrossCompany||
   !await access.CanAsync(c,ProjectsActions.BudgetView,new PermissionTarget{ProjectId=projectId},ct))return Refuse("AccessDenied");
  if(projectId<=0||warehouseId<=0||destinationSiteId is <=0)return Refuse("InvalidWarehouseScope");
   var project=await db.Projects.AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ID==projectId&&x.IsActive,ct);
   if(project==null)return Refuse("AccessDenied");
   var warehouse=await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.ID==warehouseId&&x.IsActive,ct);
   if(warehouse==null)return Refuse("WarehouseNotFound");
   if(!await warehouses.CanBindAsync(c,warehouseId,ct))return Refuse("WarehouseAccessDenied");
   var policy=await db.Set<ConstructionCompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId&&x.PolicyKey=="CrossSiteWarehouse",ct);
   if(policy?.Mode is not ("Restricted" or "ApprovedException"))return Refuse("CrossSitePolicyNotConfigured");
   var sites=await db.Set<ConstructionSite>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&(x.ProjectId==projectId||x.WarehouseId==warehouseId||x.ID==destinationSiteId)).OrderBy(x=>x.ID).ToListAsync(ct);
   ConstructionSite? destination=null;object? targetWarehouseState=null;
   if(destinationSiteId.HasValue){
    destination=sites.SingleOrDefault(x=>x.ID==destinationSiteId&&x.ProjectId==projectId&&x.IsActive);
    if(destination==null)return Refuse("DestinationSiteNotFound");
    if(destination.WarehouseId is not >0)return Refuse("DestinationWarehouseNotProven");
    var target=await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x=>x.ID==destination.WarehouseId&&x.CompanyID==c.CompanyId&&x.IsActive,ct);
    if(target==null)return Refuse("DestinationWarehouseNotProven");
    targetWarehouseState=new{target.ID,target.CompanyID,target.IsActive,target.BranchHierarchicalId,target.WarehouseType};
    if(target.ID!=warehouseId&&!await warehouses.CanBindAsync(c,target.ID,ct))return Refuse("WarehouseAccessDenied");
   }
   if(sites.Any(x=>x.RowVersion is not {Length:>0}))return Refuse("SiteVersionNotProven");
   var mapped=sites.Where(x=>x.WarehouseId==warehouseId).ToArray();
   var active=mapped.Where(x=>x.IsActive).ToArray();
   var projectIds=active.Select(x=>x.ProjectId).Distinct().ToArray();
   var sourceProjects=await db.Projects.AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&projectIds.Contains(x.ID)).OrderBy(x=>x.ID).Select(x=>new{x.ID,x.IsActive}).ToListAsync(ct);
   ConstructionWarehouseRelation relation;
   if(active.Length==0)relation=mapped.Length==0?ConstructionWarehouseRelation.Unmapped:ConstructionWarehouseRelation.InactiveMapping;
   else if(sourceProjects.Count!=projectIds.Length)relation=ConstructionWarehouseRelation.UnprovenMapping;
   else if(sourceProjects.Any(x=>!x.IsActive))relation=ConstructionWarehouseRelation.InactiveMapping;
   else if(active.Length>1)relation=ConstructionWarehouseRelation.AmbiguousMapping;
   else if(active[0].ProjectId!=projectId)relation=ConstructionWarehouseRelation.OtherProject;
   else if(destination==null)relation=ConstructionWarehouseRelation.SameProjectDestinationUnspecified;
   else relation=active[0].ID==destination.ID?ConstructionWarehouseRelation.SameSite:ConstructionWarehouseRelation.SameProjectOtherSite;
   // Witness describes this locked read only. A later writer MUST re-check inventory grants,
   // mappings, project/site activity and policy under its own canonical operation transaction.
   var facts=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{
    Company=c.CompanyId,Project=projectId,Destination=destinationSiteId,
    Warehouse=new{warehouse.ID,warehouse.CompanyID,warehouse.IsActive,warehouse.BranchHierarchicalId,warehouse.WarehouseType},
    DestinationWarehouse=targetWarehouseState,Policy=new{policy.Mode,policy.ConcurrencyToken},Mappings=sites.Select(x=>new{x.ID,x.CompanyID,x.ProjectId,x.WarehouseId,x.IsActive,x.RowVersion}),
    SourceProjects=sourceProjects,Relation=relation.ToString()})));
   var version=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Facts=facts,Actor=c.EmployeeId,c.UserId})));
   return new(true,"Classified",relation,policy.Mode,destinationSiteId,version,facts);
 }

}
