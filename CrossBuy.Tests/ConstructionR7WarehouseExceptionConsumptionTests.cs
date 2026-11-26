using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7WarehouseExceptionConsumptionTests
{
 internal sealed record Setup(ConstructionR7WarehouseExceptionRequestTests.Setup Scope,int Decision,int ApprovalRequest,int Approval);
 internal static ConstructionWarehouseExceptionConsumptionReview Service(CrossDbContext db,Setup s,int actor=8,bool inventory=true,bool manage=true,bool project=true,bool origin=true,bool destination=true,int company=1)=>new(db,
  ConstructionR7BudgetTransferDecisionTests.Actor(actor,company),new ConstructionR7ActualCostTests.Access(project),new[]{new ConstructionR7PurchaseApprovalTests.InventoryAuthority{Purchase=inventory,Manage=manage}},
  new ConstructionR7WarehouseClassificationTests.WarehouseAccess(new[]{origin?s.Scope.SourceWarehouse:0,destination?s.Scope.DestinationWarehouse:0}),new ConstructionAuditService(db));
 internal static async Task<Setup> Seed(CrossDbContext db,bool submit=true)
 {
  var purchase=await ConstructionR7PurchaseApprovalTests.Arrange(db);
  var other=new Project{CompanyID=1,Code="WHUSE-OTHER",Name="Other project"};var destinationWarehouse=new Warehouse{CompanyID=1,Code="WHUSE-DEST",Name="Destination warehouse"};
  db.AddRange(other,destinationWarehouse,new ConstructionCompanyPolicy{CompanyID=1,PolicyKey="CrossSiteWarehouse",Mode="ApprovedException",Reason="Explicit fixture choice",UpdatedBy=7,UpdatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
  var origin=new ConstructionSite{CompanyID=1,ProjectId=other.ID,WarehouseId=purchase.Seed.Warehouse,Code="WHUSE-SRC",Name="Origin"};var destination=new ConstructionSite{CompanyID=1,ProjectId=purchase.Seed.Project,WarehouseId=destinationWarehouse.ID,Code="WHUSE-DEST",Name="Destination"};db.AddRange(origin,destination);await db.SaveChangesAsync();
  var scope=new ConstructionR7WarehouseExceptionRequestTests.Setup(purchase.Order,purchase.Seed.Project,other.ID,purchase.Seed.Warehouse,destinationWarehouse.ID,destination.ID,origin.ID);
  var requests=ConstructionR7WarehouseExceptionRequestTests.Service(db,scope);var quote=await requests.PreviewAsync(scope.Order,scope.DestinationSite);Assert.True(quote.Ok,quote.Code);var request=await requests.RequestAsync(scope.Order,scope.DestinationSite,quote.Version!,Guid.NewGuid(),"Warehouse consent before submission");Assert.True(request.Ok,request.Code);
  var decision=await ConstructionR7WarehouseExceptionDecisionTests.Service(db,scope).DecideAsync(scope.Project,request.Id!.Value,"Approved",Guid.NewGuid(),"Independent warehouse consent");Assert.True(decision.Ok,decision.Code);
  if(!submit)return new(scope,decision.Id!.Value,0,0);
  var actor=new ConstructionR7PurchaseApprovalTests.ActorAccessor();var submitted=await ConstructionR7PurchaseApprovalTests.Service(db,actor,warehouses:new ConstructionR7WarehouseClassificationTests.WarehouseAccess(new[]{scope.SourceWarehouse,scope.DestinationWarehouse}),projectAccess:new ConstructionR7ActualCostTests.Access()).RequestWithWarehouseExceptionAsync(scope.Project,scope.Order,purchase.Allocation,purchase.Version,null,decision.Id!.Value,Guid.NewGuid(),"Bind exact warehouse consent");Assert.True(submitted.Ok,submitted.Code);
  var approvalRequest=await db.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().SingleAsync();return new(scope,decision.Id!.Value,approvalRequest.ID,submitted.ApprovalId!.Value);
 }
 [Fact]
 public async Task Ready_review_binds_exact_pending_approval_and_never_consumes_or_grants()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var before=await f.Db.ConstructionAuditEntries.CountAsync();var events=await f.Db.BusinessEvents.CountAsync();
  var service=Service(f.Db,s);var first=await service.PreviewAsync(s.Scope.Project,s.Scope.Order,s.Decision,s.ApprovalRequest);Assert.True(first.Ok,first.Code);Assert.False(first.OperationalGrant);Assert.Equal(s.Decision,first.DecisionId);
  var repeat=await service.PreviewAsync(s.Scope.Project,s.Scope.Order,s.Decision,s.ApprovalRequest);Assert.Equal(first.Version,repeat.Version);
  var other=await Service(f.Db,s,actor:9).PreviewAsync(s.Scope.Project,s.Scope.Order,s.Decision,s.ApprovalRequest);Assert.True(other.Ok,other.Code);Assert.NotEqual(first.Version,other.Version);
  await using var verify=f.NewContext();Assert.Equal(before,await verify.ConstructionAuditEntries.CountAsync());Assert.Equal(events,await verify.BusinessEvents.CountAsync());Assert.Equal("Draft",(await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);Assert.Equal("Pending",(await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
 }
 [Theory][InlineData("actor")][InlineData("inventory")][InlineData("manage")][InlineData("project")][InlineData("origin")][InlineData("destination")][InlineData("company")][InlineData("source")][InlineData("mapping")][InlineData("policy")][InlineData("approval")][InlineData("wrong-approval")][InlineData("wrong-decision")][InlineData("wrong-order")]
 public async Task Stale_or_unauthorized_review_refuses_without_business_writes(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var before=await f.Db.ConstructionAuditEntries.CountAsync();
  if(mode=="source")await f.Db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Notes,"Changed after consent"));
  if(mode=="mapping")await f.Db.Set<ConstructionSite>().Where(x=>x.ID==s.Scope.OriginSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
  if(mode=="policy")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Restricted"));
  if(mode=="approval")await f.Db.InventoryApprovals.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Status,"Rejected"));
  await using var db=f.NewContext();var result=await Service(db,s,actor:mode=="actor"?7:8,inventory:mode!="inventory",manage:mode!="manage",project:mode!="project",origin:mode!="origin",destination:mode!="destination",company:mode=="company"?2:1).PreviewAsync(s.Scope.Project,mode=="wrong-order"?int.MaxValue:s.Scope.Order,mode=="wrong-decision"?int.MaxValue:s.Decision,mode=="wrong-approval"?int.MaxValue:s.ApprovalRequest);Assert.False(result.Ok);Assert.False(result.OperationalGrant);
  await using var verify=f.NewContext();Assert.Equal(before,await verify.ConstructionAuditEntries.CountAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());
 }
 [Fact]
 public async Task Borrowed_transaction_is_not_accepted_as_a_public_review_bypass()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);await using var tx=await f.Db.Database.BeginTransactionAsync();var result=await Service(f.Db,s).PreviewAsync(s.Scope.Project,s.Scope.Order,s.Decision,s.ApprovalRequest);Assert.False(result.Ok);Assert.Equal("CleanUnitOfWorkRequired",result.Code);
 }
}
