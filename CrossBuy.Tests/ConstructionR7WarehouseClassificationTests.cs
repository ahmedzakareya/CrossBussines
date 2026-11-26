using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7WarehouseClassificationTests
{
 internal sealed class WarehouseAccess(params int[] allowed):IConstructionWarehouseAccess
 {
  public List<int> Checked {get;}=new();
  public Task<bool> CanBindAsync(BusinessContext c,int warehouseId,CancellationToken ct=default){Checked.Add(warehouseId);return Task.FromResult(allowed.Contains(warehouseId));}
 }
 internal static ConstructionWarehouseClassifier Service(CrossDbContext db,WarehouseAccess access,bool permitted=true,int company=1,int employee=7,BusinessContextSource source=BusinessContextSource.Test)=>
  new(db,new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=new BusinessContext{CompanyId=company,EmployeeId=employee,UserId="D10-"+employee,Source=source}},new ConstructionR7ActualCostTests.Access(permitted),access);
 internal sealed record Ids(int Project,int OtherProject,int OwnWarehouse,int OtherWarehouse,int OwnSite,int OtherSite);
 internal static async Task<Ids> Seed(CrossDbContext db)
 {
  var project=new Project{CompanyID=1,Code="D10-A",Name="Target project"};var other=new Project{CompanyID=1,Code="D10-B",Name="Private other project"};
  var own=new Warehouse{CompanyID=1,Code="D10-A",Name="Target warehouse"};var source=new Warehouse{CompanyID=1,Code="D10-B",Name="Private other warehouse"};
  db.AddRange(project,other,own,source,new ConstructionCompanyPolicy{CompanyID=1,PolicyKey="CrossSiteWarehouse",Mode="Restricted",Reason="Explicit isolated company choice",UpdatedBy=7,UpdatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
  var ownSite=new ConstructionSite{CompanyID=1,ProjectId=project.ID,WarehouseId=own.ID,Code="D10-A",Name="Target site"};
  var otherSite=new ConstructionSite{CompanyID=1,ProjectId=other.ID,WarehouseId=source.ID,Code="D10-B",Name="Private other site"};db.AddRange(ownSite,otherSite);await db.SaveChangesAsync();return new(project.ID,other.ID,own.ID,source.ID,ownSite.ID,otherSite.ID);
 }
 [Theory][InlineData("same-site",ConstructionWarehouseRelation.SameSite)][InlineData("same-project-other-site",ConstructionWarehouseRelation.SameProjectOtherSite)]
 [InlineData("project-unspecified",ConstructionWarehouseRelation.SameProjectDestinationUnspecified)][InlineData("other-project",ConstructionWarehouseRelation.OtherProject)]
 [InlineData("ambiguous",ConstructionWarehouseRelation.AmbiguousMapping)][InlineData("unmapped",ConstructionWarehouseRelation.Unmapped)]
 [InlineData("inactive",ConstructionWarehouseRelation.InactiveMapping)][InlineData("inactive-project",ConstructionWarehouseRelation.InactiveMapping)][InlineData("unproven",ConstructionWarehouseRelation.UnprovenMapping)]
 public async Task Canonical_relationships_are_read_only_facts_never_exception_grants(string mode,ConstructionWarehouseRelation expected)
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);int warehouse=mode is "same-site" or "project-unspecified"?s.OwnWarehouse:s.OtherWarehouse;
  if(mode=="same-project-other-site")await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.OtherSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.ProjectId,s.Project));
  if(mode=="ambiguous"){f.Db.Add(new ConstructionSite{CompanyID=1,ProjectId=s.Project,WarehouseId=s.OtherWarehouse,Code="D10-C",Name="Second mapping"});await f.Db.SaveChangesAsync();}
  if(mode=="unmapped")await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.OtherSite).ExecuteDeleteAsync();
  if(mode=="inactive")await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.OtherSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
  if(mode=="inactive-project")await f.Db.Projects.Where(x=>x.ID==s.OtherProject).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
  if(mode=="unproven")await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.OtherSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.ProjectId,999999));
  await using var db=f.NewContext();var grants=new WarehouseAccess(s.OwnWarehouse,s.OtherWarehouse);var service=Service(db,grants);var result=await service.InspectAsync(s.Project,warehouse,mode=="project-unspecified"?null:s.OwnSite);
  Assert.True(result.Ok,result.Code);Assert.Equal(expected,result.Relation);Assert.False(result.OperationalGrant);Assert.Equal("Restricted",result.CompanyMode);Assert.Equal(64,result.Version!.Length);
  Assert.Equal(result.Version,(await service.InspectAsync(s.Project,warehouse,mode=="project-unspecified"?null:s.OwnSite)).Version);
  if(warehouse!=s.OwnWarehouse)Assert.Contains(s.OwnWarehouse,grants.Checked);
  var json=System.Text.Json.JsonSerializer.Serialize(result);Assert.DoesNotContain("Private other",json);
  await using var verify=f.NewContext();Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
 }
 [Theory][InlineData("denied-project")][InlineData("denied-source")][InlineData("denied-destination")][InlineData("foreign-company")][InlineData("portal")][InlineData("system")][InlineData("worker")]
 [InlineData("missing-policy")][InlineData("unknown-policy")][InlineData("foreign-site")][InlineData("inactive-warehouse")][InlineData("unbound-destination")][InlineData("inactive-project")]
 public async Task Unproven_policy_scope_or_existing_inventory_grants_are_never_bypassed(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);
  if(mode=="missing-policy")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteDeleteAsync();
  if(mode=="unknown-policy")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Unknown"));
  if(mode=="inactive-warehouse")await f.Db.Warehouses.Where(x=>x.ID==s.OtherWarehouse).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
  if(mode=="unbound-destination")await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.OwnSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.WarehouseId,(int?)null));
  if(mode=="inactive-project")await f.Db.Projects.Where(x=>x.ID==s.Project).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
  await using var db=f.NewContext();var access=new WarehouseAccess(mode=="denied-destination"?new[]{s.OtherWarehouse}:mode=="denied-source"?new[]{s.OwnWarehouse}:new[]{s.OwnWarehouse,s.OtherWarehouse});
  var result=await Service(db,access,permitted:mode!="denied-project",company:mode=="foreign-company"?2:1,source:mode=="portal"?BusinessContextSource.ExternalPortal:mode=="system"?BusinessContextSource.System:mode=="worker"?BusinessContextSource.Worker:BusinessContextSource.Test).InspectAsync(s.Project,s.OtherWarehouse,mode=="foreign-site"?s.OtherSite:s.OwnSite);
  Assert.False(result.Ok);Assert.Null(result.Version);Assert.False(result.OperationalGrant);Assert.Null(result.Relation);
 }
 [Fact]
 public async Task Explicit_exception_mode_does_not_grant_and_mapping_policy_actor_changes_rotate_the_witness()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var service=Service(f.Db,new WarehouseAccess(s.OwnWarehouse,s.OtherWarehouse));var first=await service.InspectAsync(s.Project,s.OtherWarehouse,s.OwnSite);Assert.True(first.Ok,first.Code);
  await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"ApprovedException"));var policy=await service.InspectAsync(s.Project,s.OtherWarehouse,s.OwnSite);Assert.True(policy.Ok,policy.Code);Assert.False(policy.OperationalGrant);Assert.NotEqual(first.Version,policy.Version);
  await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.OtherSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.ProjectId,s.Project));var mapping=await service.InspectAsync(s.Project,s.OtherWarehouse,s.OwnSite);Assert.True(mapping.Ok,mapping.Code);Assert.NotEqual(policy.Version,mapping.Version);
  Assert.NotEqual(mapping.Version,(await Service(f.Db,new WarehouseAccess(s.OwnWarehouse,s.OtherWarehouse),employee:10).InspectAsync(s.Project,s.OtherWarehouse,s.OwnSite)).Version);
 }
 [Fact]
 public async Task Borrowed_transaction_and_dirty_context_refuse_without_rolling_back_caller_changes()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var service=Service(f.Db,new WarehouseAccess(s.OwnWarehouse,s.OtherWarehouse));
  await using(var tx=await f.Db.Database.BeginTransactionAsync()){Assert.Equal("CleanUnitOfWorkRequired",(await service.InspectAsync(s.Project,s.OtherWarehouse,s.OwnSite)).Code);Assert.NotNull(f.Db.Database.CurrentTransaction);await tx.RollbackAsync();}
  var project=await f.Db.Projects.SingleAsync(x=>x.ID==s.Project);project.Name="Caller pending edit";Assert.Equal("CleanUnitOfWorkRequired",(await service.InspectAsync(s.Project,s.OtherWarehouse,s.OwnSite)).Code);Assert.True(f.Db.ChangeTracker.HasChanges());
 }
}
