using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7WarehouseExceptionDecisionTests
{
 internal static ConstructionWarehouseExceptionDecisions Service(CrossDbContext db,ConstructionR7WarehouseExceptionRequestTests.Setup s,int actor=8,bool inventory=true,bool project=true,bool origin=true,bool destination=true,int company=1,IConstructionAuditService? audit=null)=>new(db,
  ConstructionR7BudgetTransferDecisionTests.Actor(actor,company),new ConstructionR7ActualCostTests.Access(project),new[]{new ConstructionR7PurchaseApprovalTests.InventoryAuthority{Purchase=inventory}},
  new ConstructionR7WarehouseClassificationTests.WarehouseAccess(new[]{origin?s.SourceWarehouse:0,destination?s.DestinationWarehouse:0}),audit??new ConstructionAuditService(db));
 internal static async Task<(ConstructionR7WarehouseExceptionRequestTests.Setup Scope,int Request)> Seed(CrossDbContext db)
 {
  var s=await ConstructionR7WarehouseExceptionRequestTests.Seed(db);var service=ConstructionR7WarehouseExceptionRequestTests.Service(db,s);var q=await service.PreviewAsync(s.Order,s.DestinationSite);Assert.True(q.Ok,q.Code);
  var result=await service.RequestAsync(s.Order,s.DestinationSite,q.Version!,Guid.NewGuid(),"Independent review requested");Assert.True(result.Ok,result.Code);return(s,result.Id!.Value);
 }
 [Fact]
 public async Task Different_actor_can_approve_live_facts_once_without_grant_or_business_execution()
 {
  using var f=new ConstructionTestFixture();var (s,id)=await Seed(f.Db);var key=Guid.NewGuid();var before=await f.Db.ConstructionAuditEntries.CountAsync();
  var service=Service(f.Db,s);var saved=await service.DecideAsync(s.Project,id,"Approved",key,"Independent consent");Assert.True(saved.Ok,saved.Code);Assert.False(saved.OperationalGrant);
  var replay=await service.DecideAsync(s.Project,id,"Approved",key,"Independent consent");Assert.True(replay.Ok,replay.Code);Assert.Equal(saved.Id,replay.Id);
  Assert.Equal("DecisionIdConflict",(await service.DecideAsync(s.Project,id,"Rejected",key,"Independent consent")).Code);
  Assert.Equal("WarehouseExceptionAlreadyDecided",(await service.DecideAsync(s.Project,id,"Rejected",Guid.NewGuid(),"Second outcome")).Code);
  await using var verify=f.NewContext();var row=await verify.Set<ConstructionWarehouseExceptionDecision>().AsNoTracking().SingleAsync();Assert.Equal(8,row.CreatedBy);Assert.Equal("Approved",row.Outcome);Assert.Equal(before+1,await verify.ConstructionAuditEntries.CountAsync());Assert.Equal("Draft",(await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
 }
 [Theory][InlineData("same-actor")][InlineData("inventory")][InlineData("project")][InlineData("origin")][InlineData("destination")][InlineData("company")][InlineData("source")][InlineData("policy")][InlineData("site")][InlineData("baseline")][InlineData("currency")][InlineData("missing")]
 public async Task Unauthorized_or_stale_approval_writes_nothing(string mode)
 {
  using var f=new ConstructionTestFixture();var (s,id)=await Seed(f.Db);var before=await f.Db.ConstructionAuditEntries.CountAsync();
  if(mode=="source")await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Notes,"Changed after request"));
  if(mode=="policy")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Restricted"));
  if(mode=="site")await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.OriginSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
  if(mode=="baseline")await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsBaseline,false));
  if(mode=="currency")await f.Db.Companies.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.DefaultCurrencyId,(int?)null));
  await using var db=f.NewContext();var result=await Service(db,s,actor:mode=="same-actor"?7:8,inventory:mode!="inventory",project:mode!="project",origin:mode!="origin",destination:mode!="destination",company:mode=="company"?2:1).DecideAsync(s.Project,mode=="missing"?int.MaxValue:id,"Approved",Guid.NewGuid(),"Unsafe consent");Assert.False(result.Ok);Assert.False(result.OperationalGrant);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionWarehouseExceptionDecision>().ToArrayAsync());Assert.Equal(before,await verify.ConstructionAuditEntries.CountAsync());
 }
 [Fact]
 public async Task Independent_authorized_rejection_can_close_stale_policy_without_operational_grant()
 {
  using var f=new ConstructionTestFixture();var (s,id)=await Seed(f.Db);await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Restricted"));
  var result=await Service(f.Db,s).DecideAsync(s.Project,id,"Rejected",Guid.NewGuid(),"Policy changed");Assert.True(result.Ok,result.Code);Assert.False(result.OperationalGrant);
  await using var verify=f.NewContext();Assert.Equal("Rejected",(await verify.Set<ConstructionWarehouseExceptionDecision>().AsNoTracking().SingleAsync()).Outcome);
 }
 [Theory][InlineData("outcome")][InlineData("key")][InlineData("reason")][InlineData("long-reason")]
 public async Task Invalid_decision_has_no_persistence(string mode)
 {
  using var f=new ConstructionTestFixture();var (s,id)=await Seed(f.Db);var result=await Service(f.Db,s).DecideAsync(s.Project,id,mode=="outcome"?"Maybe":"Approved",mode=="key"?Guid.Empty:Guid.NewGuid(),mode=="reason"?" ":mode=="long-reason"?new string('x',1001):"Consent");Assert.False(result.Ok);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionWarehouseExceptionDecision>().ToArrayAsync());
 }
 private sealed class FailingAudit:IConstructionAuditService
 {
  public int Record(IEnumerable<ConstructionAuditDraft> drafts)=>throw new InvalidOperationException("Deliberate decision audit failure");
  public Task<List<ConstructionAuditEntry>> ForEntityAsync(int companyId,string entityType,int entityId,CancellationToken ct=default)=>throw new NotSupportedException();
  public Task<List<ConstructionAuditEntry>> ForCompanyAsync(int companyId,string? entityType=null,CancellationToken ct=default)=>throw new NotSupportedException();
  public Task<List<ConstructionAuditEntry>> ForCorrelationAsync(int companyId,Guid correlationId,CancellationToken ct=default)=>throw new NotSupportedException();
 }
 [Fact]
 public async Task Late_audit_failure_rolls_back_consent_then_same_key_retries()
 {
  using var f=new ConstructionTestFixture();var (s,id)=await Seed(f.Db);var before=await f.Db.ConstructionAuditEntries.CountAsync();var key=Guid.NewGuid();
  await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(f.Db,s,audit:new FailingAudit()).DecideAsync(s.Project,id,"Approved",key,"Atomic consent"));
  await using(var verify=f.NewContext()){Assert.Empty(await verify.Set<ConstructionWarehouseExceptionDecision>().ToArrayAsync());Assert.Equal(before,await verify.ConstructionAuditEntries.CountAsync());}
  var result=await Service(f.Db,s).DecideAsync(s.Project,id,"Approved",key,"Atomic consent");Assert.True(result.Ok,result.Code);
 }
}
