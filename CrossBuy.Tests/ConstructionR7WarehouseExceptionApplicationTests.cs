using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7WarehouseExceptionApplicationTests
{
 [Theory][InlineData(false)][InlineData(true)]
 public async Task Proven_foreign_mapping_requires_binding_even_before_first_exception_intent(bool alreadyPending)
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7PurchaseApprovalTests.Arrange(f.Db);var actor=new ConstructionR7PurchaseApprovalTests.ActorAccessor();var service=ConstructionR7PurchaseApprovalTests.Service(f.Db,actor);int approval=0;
  if(alreadyPending){var pending=await ConstructionR7PurchaseApprovalTests.Request(service,s,Guid.NewGuid());Assert.True(pending.Ok,pending.Code);approval=pending.ApprovalId!.Value;}
  var other=new CrossBuy.Models.Context.Accounting.Project{CompanyID=1,Code="OTHER-GUARD",Name="Foreign source mapping"};f.Db.Add(other);await f.Db.SaveChangesAsync();f.Db.Add(new ConstructionSite{CompanyID=1,ProjectId=other.ID,WarehouseId=s.Seed.Warehouse,Code="OTHER-SITE",Name="Source site"});await f.Db.SaveChangesAsync();
  CrossBuy.BL.Construction.ConstructionPurchaseApprovalResult result;
  if(alreadyPending){actor.Current=ConstructionR7BudgetExceptionTests.Approver;result=await service.ApproveAsync(approval,Guid.NewGuid(),"Unbound approval");}else result=await ConstructionR7PurchaseApprovalTests.Request(service,s,Guid.NewGuid());
  Assert.False(result.Ok);Assert.Equal("WarehouseExceptionBindingRequired",result.Code);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Equal("Draft",(await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
 }
 internal static ConstructionPurchaseApprovalService Service(CrossDbContext db,ConstructionR7WarehouseExceptionConsumptionTests.Setup s,bool origin=true,bool destination=true,bool manage=true)
 {
  var actor=new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=ConstructionR7BudgetExceptionTests.Approver};
  return ConstructionR7PurchaseApprovalTests.Service(db,actor,new ConstructionR7PurchaseApprovalTests.InventoryAuthority{Manage=manage},warehouses:new ConstructionR7WarehouseClassificationTests.WarehouseAccess(new[]{origin?s.Scope.SourceWarehouse:0,destination?s.Scope.DestinationWarehouse:0}),projectAccess:new ConstructionR7ActualCostTests.Access());
 }
 [Fact]
 public async Task Canonical_gateway_consumes_once_and_preserves_stock_and_GL()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7WarehouseExceptionConsumptionTests.Seed(f.Db);var service=Service(f.Db,s);var key=Guid.NewGuid();
  var result=await service.ApproveAsync(s.Approval,key,"Atomic warehouse purchase");Assert.True(result.Ok,result.Code);Assert.True((await service.ApproveAsync(s.Approval,key,"Atomic warehouse purchase")).Ok);
  await using var verify=f.NewContext();var app=await verify.Set<ConstructionWarehouseExceptionApplication>().AsNoTracking().SingleAsync();var admission=await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().SingleAsync();Assert.Equal(admission.ID,app.AdmissionId);Assert.Equal(key,app.RequestId);Assert.Equal("Approved",(await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);Assert.Single(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Equal(2,await verify.BusinessEvents.CountAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());
 }
 [Theory][InlineData("source")][InlineData("policy")][InlineData("mapping")][InlineData("origin")][InlineData("destination")][InlineData("manage")][InlineData("binding")]
 public async Task Canonical_approval_refuses_stale_or_unbound_consent_without_partial_execution(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7WarehouseExceptionConsumptionTests.Seed(f.Db);
  if(mode=="source")await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Notes,"Changed"));
  if(mode=="policy")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Restricted"));
  if(mode=="mapping")await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.Scope.OriginSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
  if(mode=="binding")await f.Db.Set<ConstructionWarehouseExceptionBinding>().ExecuteDeleteAsync();
  var before=await f.Db.ConstructionAuditEntries.CountAsync();var events=await f.Db.BusinessEvents.CountAsync();
  var result=await Service(f.Db,s,origin:mode!="origin",destination:mode!="destination",manage:mode!="manage").ApproveAsync(s.Approval,Guid.NewGuid(),"Refused execution");Assert.False(result.Ok);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionWarehouseExceptionApplication>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Equal(before,await verify.ConstructionAuditEntries.CountAsync());Assert.Equal(events,await verify.BusinessEvents.CountAsync());Assert.Equal("Draft",(await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
 }
 [Fact]
 public async Task Direct_ordinary_request_cannot_bypass_existing_warehouse_intent()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7WarehouseExceptionConsumptionTests.Seed(f.Db);var actor=new ConstructionR7PurchaseApprovalTests.ActorAccessor();var request=await f.Db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().SingleAsync();
  var result=await ConstructionR7PurchaseApprovalTests.Service(f.Db,actor).RequestAsync(s.Scope.Project,s.Scope.Order,request.AllocationId,request.SourceVersion,null,Guid.NewGuid(),"Bypass attempt");Assert.False(result.Ok);Assert.Equal("WarehouseExceptionBindingRequired",result.Code);
  await using var verify=f.NewContext();Assert.Single(await verify.InventoryApprovals.ToArrayAsync());Assert.Single(await verify.Set<ConstructionWarehouseExceptionBinding>().ToArrayAsync());
 }
}
