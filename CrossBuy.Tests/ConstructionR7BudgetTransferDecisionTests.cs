using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7BudgetTransferDecisionTests
{
 internal static ConstructionR7PurchaseApprovalTests.ActorAccessor Actor(int employee=9,int company=1)=>new(){Current=new BusinessContext{CompanyId=company,EmployeeId=employee,UserId="transfer-review-"+employee,Source=BusinessContextSource.Test}};
 internal static ConstructionBudgetTransferDecisionService Service(CrossDbContext db,int employee=9,bool allowed=true,int company=1)=>new(db,Actor(employee,company),new ConstructionR7ActualCostTests.Access(allowed),new ConstructionAuditService(db));
 internal static async Task<(int Project,int Request)> Seed(CrossDbContext db)
 {
  var ids=await ConstructionR7BudgetTransferPreviewTests.Seed(db);var quote=await ConstructionR7BudgetTransferPreviewTests.Service(db).PreviewAsync(ids.Project,ids.From,ids.To,1.23m);Assert.True(quote.Ok,quote.Code);
  var requested=await ConstructionR7BudgetTransferRequestTests.Service(db).RequestAsync(ids.Project,ids.From,ids.To,1.23m,quote.Witness!,Guid.NewGuid(),"Original request");Assert.True(requested.Ok,requested.Code);return(ids.Project,requested.Id!.Value);
 }
 [Fact]
 public async Task Independent_consent_replays_once_and_never_changes_budget_or_original_request()
 {
  using var f=new ConstructionTestFixture();var ids=await Seed(f.Db);var key=Guid.NewGuid();var service=Service(f.Db);
  var first=await service.DecideAsync(ids.Project,ids.Request,"Approved",key,"Independent review");Assert.True(first.Ok,first.Code);
  var repeat=await service.DecideAsync(ids.Project,ids.Request,"Approved",key,"Independent review");Assert.True(repeat.Ok,repeat.Code);Assert.Equal(first.Id,repeat.Id);
  Assert.Equal("DecisionIdConflict",(await service.DecideAsync(ids.Project,ids.Request,"Rejected",key,"Independent review")).Code);
  Assert.Equal("TransferAlreadyDecided",(await service.DecideAsync(ids.Project,ids.Request,"Rejected",Guid.NewGuid(),"Second decision")).Code);
  await using var verify=f.NewContext();var decision=await verify.Set<ConstructionBudgetTransferDecision>().AsNoTracking().SingleAsync();Assert.Equal(9,decision.CreatedBy);Assert.Equal("Approved",decision.Outcome);
  var request=await verify.Set<ConstructionBudgetTransferRequest>().AsNoTracking().SingleAsync();Assert.Equal(7,request.CreatedBy);Assert.Equal("Original request",request.Reason);Assert.Equal(request.SourceVersion,decision.SourceVersion);
  Assert.Single(await verify.ConstructionAuditEntries.Where(x=>x.EntityType==nameof(ConstructionBudgetTransferDecision)).ToArrayAsync());
  Assert.Single(await verify.Set<ConstructionBudgetVersion>().ToArrayAsync());Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
  var page=await new ConstructionBudgetTransferPreview(verify,Actor(),new ConstructionR7ActualCostTests.Access(),new ConstructionAuditService(verify)).ReadAsync(ids.Project);Assert.Equal(9,page!.ViewerEmployeeId);Assert.Single(page.Decisions);
 }
 [Theory][InlineData("Approved")][InlineData("Rejected")]
 public async Task Requester_cannot_decide_on_own_intent(string outcome)
 {
  using var f=new ConstructionTestFixture();var ids=await Seed(f.Db);var result=await Service(f.Db,employee:7).DecideAsync(ids.Project,ids.Request,outcome,Guid.NewGuid(),"Own decision");Assert.False(result.Ok);Assert.Equal("SeparateApproverRequired",result.Code);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionBudgetTransferDecision>().ToArrayAsync());Assert.Single(await verify.ConstructionAuditEntries.ToArrayAsync());
 }
 [Theory][InlineData("disabled")][InlineData("missing")][InlineData("policy-token")][InlineData("baseline-token")][InlineData("denied")][InlineData("foreign")]
 [InlineData("reason")][InlineData("long-reason")][InlineData("key")][InlineData("outcome")][InlineData("unknown-request")]
 public async Task Invalid_or_changed_basis_cannot_be_approved(string mode)
 {
  using var f=new ConstructionTestFixture();var ids=await Seed(f.Db);var token=Guid.NewGuid().ToByteArray();
  if(mode=="disabled")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Mode,"Disabled"));
  if(mode=="missing")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteDeleteAsync();
  if(mode=="policy-token")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ConcurrencyToken,token));
  if(mode=="baseline-token")await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.RowVersion,token));
  await using var db=f.NewContext();var result=await Service(db,allowed:mode!="denied",company:mode=="foreign"?2:1).DecideAsync(ids.Project,mode=="unknown-request"?ids.Request+9999:ids.Request,mode=="outcome"?"Execute":"Approved",mode=="key"?Guid.Empty:Guid.NewGuid(),mode=="reason"?" ":mode=="long-reason"?new string('x',1001):"Review");
  Assert.False(result.Ok);await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionBudgetTransferDecision>().ToArrayAsync());Assert.Single(await verify.ConstructionAuditEntries.ToArrayAsync());
 }
 [Fact]
 public async Task Independent_refusal_can_close_a_request_after_policy_disables_new_transfers()
 {
  using var f=new ConstructionTestFixture();var ids=await Seed(f.Db);await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Mode,"Disabled"));
  var result=await Service(f.Db).DecideAsync(ids.Project,ids.Request,"Rejected",Guid.NewGuid(),"Company disabled transfers");Assert.True(result.Ok,result.Code);
  await using var verify=f.NewContext();Assert.Equal("Rejected",(await verify.Set<ConstructionBudgetTransferDecision>().SingleAsync()).Outcome);Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));
 }
 [Fact]
 public async Task Audit_failure_rolls_back_decision_but_preserves_request_and_its_audit()
 {
  using var f=new ConstructionTestFixture();var ids=await Seed(f.Db);
  await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectTransferDecisionAudit BEFORE INSERT ON ConstructionAuditEntries BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
  var result=await Service(f.Db).DecideAsync(ids.Project,ids.Request,"Approved",Guid.NewGuid(),"Review");Assert.False(result.Ok);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionBudgetTransferDecision>().ToArrayAsync());Assert.Single(await verify.Set<ConstructionBudgetTransferRequest>().ToArrayAsync());Assert.Single(await verify.ConstructionAuditEntries.ToArrayAsync());
 }
 [Fact]
 public void Decision_endpoint_requires_antiforgery_project_permission_and_no_execution_command()
 {
  var method=typeof(CrossBuy.Controllers.ConstructionBudgetTransferController).GetMethod("Decide")!;
  Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute>(method));Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<CrossBuy.Models.ProjectPermAttribute>(method));
  Assert.Equal("DecideAsync",Assert.Single(typeof(IConstructionBudgetTransferDecisions).GetMethods()).Name);
 }
}
