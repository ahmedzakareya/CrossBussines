using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7BudgetTransferRequestTests
{
 internal static ConstructionBudgetTransferRequestService Service(CrossDbContext db,bool allowed=true,int company=1)=>new(db,
  new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=ConstructionR7OrderAllocationTests.Actor(company)},
  new ConstructionR7ActualCostTests.Access(allowed),new ConstructionAuditService(db));
 [Fact]
 public async Task Recorded_intent_replays_once_without_moving_budget_and_preserves_reason()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7BudgetTransferPreviewTests.Seed(f.Db);
  var quote=await ConstructionR7BudgetTransferPreviewTests.Service(f.Db).PreviewAsync(s.Project,s.From,s.To,1.2300m);Assert.True(quote.Ok,quote.Code);
  var key=Guid.NewGuid();var service=Service(f.Db);
  var first=await service.RequestAsync(s.Project,s.From,s.To,1.23m,quote.Witness!,key,"  Transfer reason  ");Assert.True(first.Ok,first.Code);
  var repeat=await service.RequestAsync(s.Project,s.From,s.To,1.230m,quote.Witness!,key,"Transfer reason");Assert.True(repeat.Ok,repeat.Code);Assert.Equal(first.Id,repeat.Id);
  var reused=await service.RequestAsync(s.Project,s.From,s.To,2m,quote.Witness!,key,"Transfer reason");Assert.False(reused.Ok);Assert.Equal("RequestIdConflict",reused.Code);
  await using var verify=f.NewContext();var row=await verify.Set<ConstructionBudgetTransferRequest>().AsNoTracking().SingleAsync();
  Assert.Equal("Transfer reason",row.Reason);Assert.Equal(1.23m,row.Amount);Assert.Equal(1050m,row.FromAfter+row.ToAfter);Assert.Equal(16,row.PolicyToken.Length);
  Assert.Single(await verify.ConstructionAuditEntries.AsNoTracking().Where(x=>x.EntityType==nameof(ConstructionBudgetTransferRequest)).ToListAsync());
  Assert.Single(await verify.Set<ConstructionBudgetVersion>().AsNoTracking().ToListAsync());Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));
  Assert.Empty(await verify.JournalEntries.ToListAsync());Assert.Empty(await verify.StockMovements.ToListAsync());
  var page=await ConstructionR7BudgetTransferPreviewTests.Service(verify).ReadAsync(s.Project);Assert.Single(page!.Requests);
 }
 [Theory]
 [InlineData("disabled")][InlineData("missing")][InlineData("stale-policy")][InlineData("stale-budget")][InlineData("denied")][InlineData("foreign")]
 [InlineData("wrong-witness")][InlineData("no-reason")][InlineData("long-reason")][InlineData("empty-id")][InlineData("bad-source")][InlineData("overdraw")]
 public async Task Invalid_or_changed_authority_never_records_a_request(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7BudgetTransferPreviewTests.Seed(f.Db);
  var quote=await ConstructionR7BudgetTransferPreviewTests.Service(f.Db).PreviewAsync(s.Project,s.From,s.To,1m);Assert.True(quote.Ok,quote.Code);
  if(mode=="disabled")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Mode,"Disabled"));
  if(mode=="missing")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteDeleteAsync();
  var changedToken=Guid.NewGuid().ToByteArray();
  if(mode=="stale-policy")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ConcurrencyToken,changedToken));
  if(mode=="stale-budget")await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.RowVersion,changedToken));
  await using var db=f.NewContext();var result=await Service(db,mode!="denied",mode=="foreign"?2:1).RequestAsync(s.Project,mode=="bad-source"?s.To:s.From,s.To,mode=="overdraw"?1001m:1m,
   mode=="wrong-witness"?new string('A',64):quote.Witness!,mode=="empty-id"?Guid.Empty:Guid.NewGuid(),mode=="no-reason"?" ":mode=="long-reason"?new string('x',1001):"Reason");
  Assert.False(result.Ok);await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionBudgetTransferRequest>().ToListAsync());Assert.Empty(await verify.ConstructionAuditEntries.ToListAsync());
 }
 [Fact]
 public async Task Audit_failure_rolls_back_request_identity_insert_and_budget_stays_unchanged()
 {
  using var f=new ConstructionTestFixture();var s=await ConstructionR7BudgetTransferPreviewTests.Seed(f.Db);
  var q=await ConstructionR7BudgetTransferPreviewTests.Service(f.Db).PreviewAsync(s.Project,s.From,s.To,1m);Assert.True(q.Ok,q.Code);
  await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectTransferAudit BEFORE INSERT ON ConstructionAuditEntries BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
  var result=await Service(f.Db).RequestAsync(s.Project,s.From,s.To,1m,q.Witness!,Guid.NewGuid(),"Reason");Assert.False(result.Ok);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionBudgetTransferRequest>().ToListAsync());Assert.Empty(await verify.ConstructionAuditEntries.ToListAsync());Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));
 }
 [Theory][InlineData("0.00000001")][InlineData("0.0001")]
 public async Task Eight_decimal_currency_accepts_small_exact_requests_without_scientific_notation_failure(string text)
 {
  using var f=new ConstructionTestFixture();var ids=await ConstructionR7BudgetTransferPreviewTests.Seed(f.Db);
  var currency=await f.Db.Currencies.SingleAsync();currency.DecimalPlaces=8;await f.Db.SaveChangesAsync();
  var amount=decimal.Parse(text,System.Globalization.CultureInfo.InvariantCulture);
  var quote=await ConstructionR7BudgetTransferPreviewTests.Service(f.Db).PreviewAsync(ids.Project,ids.From,ids.To,amount);Assert.True(quote.Ok,quote.Code);
  var result=await Service(f.Db).RequestAsync(ids.Project,ids.From,ids.To,amount,quote.Witness!,Guid.NewGuid(),"Exact small amount");Assert.True(result.Ok,result.Code);
  await using var verify=f.NewContext();Assert.Equal(amount,(await verify.Set<ConstructionBudgetTransferRequest>().AsNoTracking().SingleAsync()).Amount);
  Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));
 }
 [Fact]
 public void Mutation_endpoint_requires_antiforgery_project_permission_and_one_command_only()
 {
  var method=typeof(CrossBuy.Controllers.ConstructionBudgetTransferController).GetMethod("Request")!;
  Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute>(method));
  Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<CrossBuy.Models.ProjectPermAttribute>(method));
  Assert.Equal("RequestAsync",Assert.Single(typeof(IConstructionBudgetTransferRequests).GetMethods()).Name);
 }
}
