using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7WarehouseExceptionRequestTests
{
 internal sealed record Setup(int Order,int Project,int OtherProject,int SourceWarehouse,int DestinationWarehouse,int DestinationSite,int OriginSite);
 internal static ConstructionWarehouseExceptionRequests Service(CrossDbContext db,Setup s,bool inventory=true,bool project=true,bool origin=true,bool destination=true,int company=1,IConstructionAuditService? audit=null)=>new(db,
  ConstructionR7BudgetTransferDecisionTests.Actor(7,company),new ConstructionR7ActualCostTests.Access(project),new[]{new ConstructionR7PurchaseApprovalTests.InventoryAuthority{Purchase=inventory}},
  new ConstructionR7WarehouseClassificationTests.WarehouseAccess(new[]{origin?s.SourceWarehouse:0,destination?s.DestinationWarehouse:0}),audit??new ConstructionAuditService(db));
 internal static async Task<Setup> Seed(CrossDbContext db)
 {
  var seed=await ConstructionR7PurchaseDraftTests.Seed(db);var other=new Project{CompanyID=1,Code="WHREQ-OTHER",Name="Other project"};var target=new Warehouse{CompanyID=1,Code="WHREQ-TARGET",Name="Destination warehouse"};
  db.AddRange(other,target,new ConstructionCompanyPolicy{CompanyID=1,PolicyKey="CrossSiteWarehouse",Mode="ApprovedException",Reason="Explicit fixture choice",UpdatedBy=7,UpdatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
  var destination=new ConstructionSite{CompanyID=1,ProjectId=seed.Project,WarehouseId=target.ID,Code="WHREQ-DEST",Name="Requested destination"};var origin=new ConstructionSite{CompanyID=1,ProjectId=other.ID,WarehouseId=seed.Warehouse,Code="WHREQ-SRC",Name="Origin"};db.AddRange(destination,origin);await db.SaveChangesAsync();
  var created=await ConstructionR7PurchaseDraftTests.Create(db,seed,Guid.NewGuid());Assert.True(created.Ok,created.Code);return new(created.OrderId!.Value,seed.Project,other.ID,seed.Warehouse,target.ID,destination.ID,origin.ID);
 }
 [Fact]
 public async Task Immutable_intent_binds_actual_order_and_requested_site_replays_once_and_never_grants_or_rewrites_source()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var service=Service(f.Db,s);var before=await f.Db.ConstructionAuditEntries.CountAsync();var q=await service.PreviewAsync(s.Order,s.DestinationSite);Assert.True(q.Ok,q.Code);Assert.False(q.OperationalGrant);Assert.Equal(s.SourceWarehouse,q.SourceWarehouseId);
  var key=Guid.NewGuid();var first=await service.RequestAsync(s.Order,s.DestinationSite,q.Version!,key,"Explicit destination proposal");Assert.True(first.Ok,first.Code);Assert.False(first.OperationalGrant);
  var repeat=await service.RequestAsync(s.Order,s.DestinationSite,q.Version!,key,"Explicit destination proposal");Assert.True(repeat.Ok,repeat.Code);Assert.Equal(first.Id,repeat.Id);
  Assert.Equal("WarehouseExceptionRequestIdConflict",(await service.RequestAsync(s.Order,s.DestinationSite,q.Version!,key,"Changed reason")).Code);
  Assert.Equal("WarehouseExceptionAlreadyRequested",(await service.RequestAsync(s.Order,s.DestinationSite,q.Version!,Guid.NewGuid(),"Duplicate intent")).Code);
  await using var verify=f.NewContext();var row=await verify.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().SingleAsync();Assert.Equal(s.Project,row.ProjectId);Assert.Equal(s.Order,row.PurchaseOrderId);Assert.Equal(s.SourceWarehouse,row.SourceWarehouseId);Assert.Equal(s.DestinationSite,row.DestinationSiteId);Assert.Equal(7,row.CreatedBy);Assert.Equal(q.Version,row.SourceVersion);Assert.Equal(q.MappingVersion,row.MappingVersion);
  var order=await verify.PurchaseOrders.AsNoTracking().Include(x=>x.Lines).SingleAsync();Assert.Equal("Draft",order.Status);Assert.Equal(s.SourceWarehouse,order.WarehouseId);Assert.Equal(row.CreationVersion,(await verify.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleAsync()).InitialSourceVersion);Assert.Equal(before+1,await verify.ConstructionAuditEntries.CountAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
 }
 [Theory][InlineData("restricted")][InlineData("missing-policy")][InlineData("inactive-mapping")][InlineData("changed-source")][InlineData("foreign-site")][InlineData("denied-origin")][InlineData("denied-destination")][InlineData("denied-inventory")][InlineData("denied-project")][InlineData("foreign-company")][InlineData("baseline")][InlineData("currency")][InlineData("approved")][InlineData("received")][InlineData("legacy")][InlineData("stale-witness")]
 public async Task Changed_source_policy_scope_or_inventory_grants_never_persist_an_intent(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var q=await Service(f.Db,s).PreviewAsync(s.Order,s.DestinationSite);Assert.True(q.Ok,q.Code);var before=await f.Db.ConstructionAuditEntries.CountAsync();
  if(mode=="restricted")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Restricted"));
  if(mode=="missing-policy")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteDeleteAsync();
  if(mode=="inactive-mapping")await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.OriginSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
  if(mode=="changed-source")await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Notes,"Mutated source"));
  if(mode=="baseline")await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsBaseline,false));
  if(mode=="currency")await f.Db.Companies.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.DefaultCurrencyId,(int?)null));
  if(mode=="approved")await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Status,"Approved"));
  if(mode=="received")await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.ReceivedQty,1m));
  if(mode=="legacy")await f.Db.Set<ConstructionPurchaseDraft>().ExecuteDeleteAsync();
  await using var db=f.NewContext();var result=await Service(db,s,inventory:mode!="denied-inventory",project:mode!="denied-project",origin:mode!="denied-origin",destination:mode!="denied-destination",company:mode=="foreign-company"?2:1).RequestAsync(s.Order,mode=="foreign-site"?s.OriginSite:s.DestinationSite,mode=="stale-witness"?new string('A',64):q.Version!,Guid.NewGuid(),"Unaccepted intent");Assert.False(result.Ok);Assert.False(result.OperationalGrant);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());Assert.Equal(before,await verify.ConstructionAuditEntries.CountAsync());
 }
 [Theory][InlineData("key")][InlineData("reason")][InlineData("long-reason")][InlineData("witness")]
 public async Task Malformed_command_cannot_create_request_evidence(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var service=Service(f.Db,s);var q=await service.PreviewAsync(s.Order,s.DestinationSite);Assert.True(q.Ok,q.Code);
  var result=await service.RequestAsync(s.Order,s.DestinationSite,mode=="witness"?"":q.Version!,mode=="key"?Guid.Empty:Guid.NewGuid(),mode=="reason"?" ":mode=="long-reason"?new string('x',1001):"Reason");Assert.False(result.Ok);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());
 }
 [Fact]
 public async Task Saved_exact_retry_after_policy_restriction_remains_intent_only_and_writes_no_second_audit()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var service=Service(f.Db,s);var q=await service.PreviewAsync(s.Order,s.DestinationSite);var key=Guid.NewGuid();var saved=await service.RequestAsync(s.Order,s.DestinationSite,q.Version!,key,"Saved intent");Assert.True(saved.Ok,saved.Code);var before=await f.Db.ConstructionAuditEntries.CountAsync();
  await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Restricted"));var result=await service.RequestAsync(s.Order,s.DestinationSite,q.Version!,key,"Saved intent");Assert.True(result.Ok,result.Code);Assert.Equal(saved.Id,result.Id);Assert.False(result.OperationalGrant);
  await using var verify=f.NewContext();Assert.Single(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());Assert.Equal(before,await verify.ConstructionAuditEntries.CountAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());
 }
 private sealed class FailingAudit:IConstructionAuditService
 {
  public int Record(IEnumerable<ConstructionAuditDraft> drafts)=>throw new InvalidOperationException("Deliberate request audit failure");
  public Task<List<ConstructionAuditEntry>> ForEntityAsync(int companyId,string entityType,int entityId,CancellationToken ct=default)=>throw new NotSupportedException();
  public Task<List<ConstructionAuditEntry>> ForCompanyAsync(int companyId,string? entityType=null,CancellationToken ct=default)=>throw new NotSupportedException();
  public Task<List<ConstructionAuditEntry>> ForCorrelationAsync(int companyId,Guid correlationId,CancellationToken ct=default)=>throw new NotSupportedException();
 }
 [Fact]
 public async Task Late_audit_failure_rolls_back_intent_and_same_key_can_retry_safely()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var q=await Service(f.Db,s).PreviewAsync(s.Order,s.DestinationSite);Assert.True(q.Ok,q.Code);var key=Guid.NewGuid();var before=await f.Db.ConstructionAuditEntries.CountAsync();
  await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(f.Db,s,audit:new FailingAudit()).RequestAsync(s.Order,s.DestinationSite,q.Version!,key,"Atomic intent"));
  await using(var verify=f.NewContext()){Assert.Empty(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());Assert.Equal(before,await verify.ConstructionAuditEntries.CountAsync());}
  var result=await Service(f.Db,s).RequestAsync(s.Order,s.DestinationSite,q.Version!,key,"Atomic intent");Assert.True(result.Ok,result.Code);
 }
}
