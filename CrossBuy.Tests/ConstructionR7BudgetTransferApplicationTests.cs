using CrossBuy.BL;
using Microsoft.Extensions.Localization;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7BudgetTransferApplicationTests
{
 internal static ConstructionBudgetTransferApplicationService Service(CrossDbContext db,int employee=7,bool allowed=true,int company=1)
  =>new(db,ConstructionR7BudgetTransferDecisionTests.Actor(employee,company),new ConstructionR7ActualCostTests.Access(allowed),new ConstructionAuditService(db),new CurrencyRounding(db,new CurrencyService(db),new Localizer()));
 internal static async Task<(int Project,int Request,int Decision)> Seed(CrossDbContext db,string outcome="Approved")
 {
  var ids=await ConstructionR7BudgetTransferPreviewTests.Seed(db);await ConfigureCompany(db);
  var quote=await ConstructionR7BudgetTransferPreviewTests.Service(db).PreviewAsync(ids.Project,ids.From,ids.To,1.23m);Assert.True(quote.Ok,quote.Code);
  var request=await ConstructionR7BudgetTransferRequestTests.Service(db).RequestAsync(ids.Project,ids.From,ids.To,1.23m,quote.Witness!,Guid.NewGuid(),"Original request");Assert.True(request.Ok,request.Code);
  var consent=await ConstructionR7BudgetTransferDecisionTests.Service(db).DecideAsync(ids.Project,request.Id!.Value,outcome,Guid.NewGuid(),"Independent consent");
  Assert.True(consent.Ok,consent.Code);return(ids.Project,request.Id.Value,consent.Id!.Value);
 }
 internal static async Task ConfigureCompany(CrossDbContext db)
 {
  // Explicit test-company configuration, not a production default or bypass.
  var currency=await db.Set<ConstructionBudgetVersion>().AsNoTracking().Where(x=>x.CompanyID==1&&x.IsBaseline).Select(x=>x.CurrencyId).SingleAsync();
  var company=await db.Companies.IgnoreQueryFilters().SingleOrDefaultAsync(x=>x.CompanyID==1);
  if(company==null){company=new CrossBuy.Models.Context.Admin.Companies{CompanyName="Transfer test company",Address="",PhoneNumber="",Email="",DefaultCurrencyId=currency};db.Companies.Add(company);await db.SaveChangesAsync();Assert.Equal(1,company.CompanyID);}
  else{company.DefaultCurrencyId=currency;await db.SaveChangesAsync();}
 }
 [Theory][InlineData(7)][InlineData(10)]
 public async Task Approved_transfer_creates_one_linked_balanced_baseline_without_rewriting_history_or_financial_posting(int executor)
 {
  using var f=new ConstructionTestFixture();var ids=await Seed(f.Db);var key=Guid.NewGuid();var service=Service(f.Db,executor);
  var old=await f.Db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync();
  var first=await service.ApplyAsync(ids.Project,ids.Decision,key,"Execute approved intent");Assert.True(first.Ok,first.Code);
  var repeat=await service.ApplyAsync(ids.Project,ids.Decision,key,"Execute approved intent");Assert.True(repeat.Ok,repeat.Code);Assert.Equal(first.Id,repeat.Id);
  Assert.Equal("ApplicationIdConflict",(await service.ApplyAsync(ids.Project,ids.Decision,key,"Different command")).Code);
  Assert.Equal("TransferAlreadyApplied",(await service.ApplyAsync(ids.Project,ids.Decision,Guid.NewGuid(),"Another application")).Code);
  await using var verify=f.NewContext();var fact=await verify.Set<ConstructionBudgetTransferApplication>().AsNoTracking().SingleAsync();
  Assert.Equal(executor,fact.AppliedBy);Assert.Equal(ids.Decision,fact.TransferDecisionId);Assert.Equal(ids.Request,fact.TransferRequestId);Assert.Equal(old.ID,fact.OriginalBudgetVersionId);
  var baseline=await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x=>x.IsBaseline);
  Assert.Equal(fact.ResultBudgetVersionId,baseline.ID);Assert.Equal(old.ID,baseline.PreviousVersionId);Assert.Equal("Approved",baseline.Status);
  Assert.Equal(executor,baseline.CreatedBy);Assert.Equal(executor,baseline.SubmittedBy);Assert.Equal(9,baseline.ApprovedBy);Assert.Equal(old.CurrencyId,baseline.CurrencyId);
  var prior=await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x=>x.ID==old.ID);Assert.False(prior.IsBaseline);Assert.Equal("Superseded",prior.Status);
  var oldLines=await verify.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.BudgetVersionId==old.ID).ToArrayAsync();
  var result=await verify.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.BudgetVersionId==baseline.ID).ToArrayAsync();
  var request=await verify.Set<ConstructionBudgetTransferRequest>().AsNoTracking().SingleAsync();Assert.Equal("Original request",request.Reason);Assert.Equal(7,request.CreatedBy);
  Assert.Equal(1050m,oldLines.Sum(x=>x.Amount));Assert.Equal(1050m,result.Sum(x=>x.Amount));Assert.Equal(oldLines.Length,result.Length);
  foreach(var line in oldLines){var next=result.Single(x=>x.WbsNodeId==line.WbsNodeId&&x.CostCodeId==line.CostCodeId);
   Assert.Equal(line.Amount,next.PreviousAmount);Assert.Equal(line.ID==request.FromLineId?request.FromAfter:line.ID==request.ToLineId?request.ToAfter:line.Amount,next.Amount);
   Assert.Equal(line.Quantity,next.Quantity);Assert.Equal(line.Rate,next.Rate);Assert.Equal(line.IsContingency,next.IsContingency);}
  Assert.Single(await verify.ConstructionAuditEntries.Where(x=>x.EntityType==nameof(ConstructionBudgetTransferApplication)).ToArrayAsync());
  Assert.Single(await verify.Set<ConstructionBudgetTransferDecision>().AsNoTracking().ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
  var page=await new ConstructionBudgetTransferPreview(verify,ConstructionR7BudgetTransferDecisionTests.Actor(executor),new ConstructionR7ActualCostTests.Access(),new ConstructionAuditService(verify)).ReadAsync(ids.Project);
  Assert.Single(page!.Applications);Assert.Equal(baseline.ID,page.BudgetVersionId);
 }
 [Theory]
 [InlineData("rejected")][InlineData("unknown")][InlineData("approver")][InlineData("denied")][InlineData("foreign")]
 [InlineData("disabled")][InlineData("missing-policy")][InlineData("policy-token")][InlineData("baseline-token")][InlineData("line-basis")][InlineData("inactive")]
 [InlineData("reason")][InlineData("long-reason")][InlineData("key")][InlineData("blocked-code")][InlineData("currency-default")]
 public async Task Changed_or_invalid_authority_cannot_materialize_a_transfer(string mode)
 {
  using var f=new ConstructionTestFixture();var ids=await Seed(f.Db,mode=="rejected"?"Rejected":"Approved");
  var token=Guid.NewGuid().ToByteArray();
  if(mode=="disabled")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Mode,"Disabled"));
  if(mode=="missing-policy")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteDeleteAsync();
  if(mode=="policy-token")await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ConcurrencyToken,token));
  if(mode=="baseline-token")await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.RowVersion,token));
  if(mode=="line-basis")await f.Db.Set<ConstructionBudgetLine>().Where(x=>x.Amount==1000m).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Amount,999m));
  if(mode=="currency-default")await f.Db.Companies.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DefaultCurrencyId,(int?)null));
  if(mode=="blocked-code")await f.Db.Set<ConstructionCostCode>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.AllowDirectPosting,false));
  if(mode=="inactive")await f.Db.Set<WbsNode>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Archived"));
  var auditBefore=await f.Db.ConstructionAuditEntries.AsNoTracking().CountAsync();
  await using var db=f.NewContext();var service=Service(db,employee:mode=="approver"?9:7,allowed:mode!="denied",company:mode=="foreign"?2:1);
  var result=await service.ApplyAsync(ids.Project,mode=="unknown"?ids.Decision+99999:ids.Decision,mode=="key"?Guid.Empty:Guid.NewGuid(),mode=="reason"?" ":mode=="long-reason"?new string('x',1001):"Execution");
  Assert.False(result.Ok);if(mode=="approver")Assert.Equal("SeparateExecutorRequired",result.Code);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionBudgetTransferApplication>().ToArrayAsync());
  var baseline=Assert.Single(await verify.Set<ConstructionBudgetVersion>().AsNoTracking().ToArrayAsync());Assert.True(baseline.IsBaseline);Assert.Equal("Approved",baseline.Status);
  Assert.Equal(auditBefore,await verify.ConstructionAuditEntries.AsNoTracking().CountAsync());
 }
 [Fact]
 public async Task Late_audit_failure_rolls_back_all_versions_lines_and_application_evidence()
 {
  using var f=new ConstructionTestFixture();var ids=await Seed(f.Db);var old=await f.Db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync();var auditBefore=await f.Db.ConstructionAuditEntries.CountAsync();
  await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectTransferApplicationAudit BEFORE INSERT ON ConstructionAuditEntries BEGIN SELECT RAISE(ABORT, 'application audit failure'); END;");
  var result=await Service(f.Db).ApplyAsync(ids.Project,ids.Decision,Guid.NewGuid(),"Atomic execution");Assert.False(result.Ok);
  await using var verify=f.NewContext();var baseline=Assert.Single(await verify.Set<ConstructionBudgetVersion>().AsNoTracking().ToArrayAsync());Assert.Equal(old.ID,baseline.ID);Assert.True(baseline.IsBaseline);Assert.Equal("Approved",baseline.Status);Assert.Equal(old.RowVersion,baseline.RowVersion);
  Assert.Empty(await verify.Set<ConstructionBudgetTransferApplication>().ToArrayAsync());Assert.Equal(2,await verify.Set<ConstructionBudgetLine>().CountAsync());Assert.Equal(auditBefore,await verify.ConstructionAuditEntries.CountAsync());
 }
 [Fact]
 public async Task Successful_replay_after_policy_is_disabled_returns_existing_evidence_without_new_authority()
 {
  using var f=new ConstructionTestFixture();var ids=await Seed(f.Db);var key=Guid.NewGuid();var first=await Service(f.Db).ApplyAsync(ids.Project,ids.Decision,key,"Execute");Assert.True(first.Ok,first.Code);
  await f.Db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Mode,"Disabled"));
  await using var db=f.NewContext();var repeat=await Service(db).ApplyAsync(ids.Project,ids.Decision,key,"Execute");Assert.True(repeat.Ok,repeat.Code);Assert.Equal(first.Id,repeat.Id);
  await using var verify=f.NewContext();Assert.Single(await verify.Set<ConstructionBudgetTransferApplication>().ToArrayAsync());Assert.Equal(2,await verify.Set<ConstructionBudgetVersion>().CountAsync());
 }
 [Fact]
 public void Execution_endpoint_has_distinct_interface_antiforgery_and_project_authorization()
 {
  var method=typeof(CrossBuy.Controllers.ConstructionBudgetTransferController).GetMethod("Apply")!;
  Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute>(method));
  Assert.NotNull(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<CrossBuy.Models.ProjectPermAttribute>(method));
  Assert.Equal("ApplyAsync",Assert.Single(typeof(IConstructionBudgetTransferApplications).GetMethods()).Name);
  Assert.Equal(new[]{"PreviewAsync","ReadAsync"},typeof(IConstructionBudgetTransferPreview).GetMethods().Select(x=>x.Name).OrderBy(x=>x));
 }
 [Theory][InlineData((byte)0,"1")][InlineData((byte)3,"1.234")][InlineData((byte)8,"0.12345678")]
 public async Task Execution_preserves_the_approved_currency_precision_without_a_two_decimal_default(byte decimals,string value)
 {
  using var f=new ConstructionTestFixture();var ids=await ConstructionR7BudgetTransferPreviewTests.Seed(f.Db);await ConfigureCompany(f.Db);
  await f.Db.Currencies.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DecimalPlaces,decimals));
  await using var db=f.NewContext();var amount=decimal.Parse(value,System.Globalization.CultureInfo.InvariantCulture);
  var quote=await ConstructionR7BudgetTransferPreviewTests.Service(db).PreviewAsync(ids.Project,ids.From,ids.To,amount);Assert.True(quote.Ok,quote.Code);
  var request=await ConstructionR7BudgetTransferRequestTests.Service(db).RequestAsync(ids.Project,ids.From,ids.To,amount,quote.Witness!,Guid.NewGuid(),"Exact precision intent");Assert.True(request.Ok,request.Code);
  var decision=await ConstructionR7BudgetTransferDecisionTests.Service(db).DecideAsync(ids.Project,request.Id!.Value,"Approved",Guid.NewGuid(),"Exact precision consent");Assert.True(decision.Ok,decision.Code);
  var result=await Service(db).ApplyAsync(ids.Project,decision.Id!.Value,Guid.NewGuid(),"Exact precision application");Assert.True(result.Ok,result.Code);
  await using var verify=f.NewContext();var app=await verify.Set<ConstructionBudgetTransferApplication>().AsNoTracking().SingleAsync();
  var rows=await verify.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.BudgetVersionId==app.ResultBudgetVersionId).ToArrayAsync();
  Assert.Contains(rows,x=>x.Amount==1000m-amount);Assert.Contains(rows,x=>x.Amount==50m+amount);Assert.Equal(1050m,rows.Sum(x=>x.Amount));
 }

 [Fact]
 public async Task Unchanged_allocation_dimensions_must_still_pass_current_budget_approval_validation()
 {
  using var f=new ConstructionTestFixture();var ids=await ConstructionR7BudgetTransferPreviewTests.Seed(f.Db);await ConfigureCompany(f.Db);
  var baseline=await f.Db.Set<ConstructionBudgetVersion>().SingleAsync();var source=await f.Db.Set<ConstructionBudgetLine>().SingleAsync(x=>x.ID==ids.From);
  var extraNode=new WbsNode{CompanyID=1,ProjectId=ids.Project,Code="EXTRA",Name="Other work",Path="/extra/",Depth=1,Status="Active",ProgressMethod="Quantity",OwnsBudget=true,OwnsCost=true,CreatedBy=7};
  f.Db.Add(extraNode);await f.Db.SaveChangesAsync();
  f.Db.Add(new ConstructionBudgetLine{CompanyID=1,ProjectId=ids.Project,BudgetVersionId=baseline.ID,WbsNodeId=extraNode.ID,CostCodeId=source.CostCodeId,CostCategory=source.CostCategory,Amount=10m,ChangeReason="Other approved estimate"});await f.Db.SaveChangesAsync();
  var quote=await ConstructionR7BudgetTransferPreviewTests.Service(f.Db).PreviewAsync(ids.Project,ids.From,ids.To,1.23m);Assert.True(quote.Ok,quote.Code);
  var request=await ConstructionR7BudgetTransferRequestTests.Service(f.Db).RequestAsync(ids.Project,ids.From,ids.To,1.23m,quote.Witness!,Guid.NewGuid(),"Intent with complete baseline");Assert.True(request.Ok,request.Code);
  var decision=await ConstructionR7BudgetTransferDecisionTests.Service(f.Db).DecideAsync(ids.Project,request.Id!.Value,"Approved",Guid.NewGuid(),"Independent consent");Assert.True(decision.Ok,decision.Code);
  await f.Db.Set<WbsNode>().Where(x=>x.ID==extraNode.ID).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Status,"Archived"));
  await using var db=f.NewContext();var result=await Service(db).ApplyAsync(ids.Project,decision.Id!.Value,Guid.NewGuid(),"Execute after unrelated dimension retired");
  Assert.False(result.Ok);Assert.Equal("InvalidBudgetDimension",result.Code);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionBudgetTransferApplication>().ToArrayAsync());Assert.Single(await verify.Set<ConstructionBudgetVersion>().ToArrayAsync());Assert.Equal(1060m,(await verify.Set<ConstructionBudgetLine>().ToArrayAsync()).Sum(x=>x.Amount));
 }
 private sealed class Localizer:IStringLocalizer<CrossBuy.SharedResources>
 {
  public LocalizedString this[string name]=>new(name,name);
  public LocalizedString this[string name,params object[] args]=>new(name,string.Format(name,args));
  public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)=>Array.Empty<LocalizedString>();
 }

}
