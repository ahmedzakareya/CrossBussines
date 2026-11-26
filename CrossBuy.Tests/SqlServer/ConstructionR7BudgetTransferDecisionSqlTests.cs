using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7BudgetTransferDecisionSqlTests(UatSqlProbeFixture sql)
{
 internal static string Script(string file)
 {
  for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent){var p=Path.Combine(d.FullName,"CrossBuy/deploy/sql",file);if(File.Exists(p))return File.ReadAllText(p);}
  throw new FileNotFoundException(file);
 }
 internal static async Task Execute(string connectionString,string text)
 {
  await using var c=new SqlConnection(connectionString);await c.OpenAsync();
  foreach(var batch in Regex.Split(text,@"(?im)^\s*GO\s*(?:--[^\r\n]*)?\r?$")){
   if(string.IsNullOrWhiteSpace(batch))continue;await using var cmd=c.CreateCommand();cmd.CommandText=batch;cmd.CommandTimeout=120;await cmd.ExecuteNonQueryAsync();
  }
 }
 internal async Task<(int Project,int Request)> Prepare(UatSqlProbeFixture.ProbeDatabase probe,decimal amount=1.23m,byte? decimals=null,bool configureCompany=false)
 {
  (int Project,int From,int To) ids;using(var seed=sql.ContextFor(probe)){ids=await ConstructionR7BudgetTransferPreviewTests.Seed(seed);if(configureCompany)await ConstructionR7BudgetTransferApplicationTests.ConfigureCompany(seed);}
  await Execute(probe.ConnectionString,"IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferDecisions) OR EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferRequests) THROW 51960,'Owned fixture must be empty',1; IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferApplications) THROW 51970,'Owned application fixture must be empty',1; DROP TABLE dbo.ConstructionBudgetTransferApplications; DROP TABLE dbo.ConstructionBudgetTransferDecisions; DROP TABLE dbo.ConstructionBudgetTransferRequests;");
  await Execute(probe.ConnectionString,Script("construction_r7_budget_transfer_requests.sql"));
  await Execute(probe.ConnectionString,Script("construction_r7_budget_transfer_decisions.sql"));await Execute(probe.ConnectionString,Script("construction_r7_budget_transfer_decisions.sql"));
  if(decimals.HasValue){var precision=decimals.Value;using var change=sql.ContextFor(probe);await change.Currencies.ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DecimalPlaces,precision));}
  using var db=sql.ContextFor(probe);var quote=await ConstructionR7BudgetTransferPreviewTests.Service(db).PreviewAsync(ids.Project,ids.From,ids.To,amount);Assert.True(quote.Ok,quote.Code);
  var saved=await ConstructionR7BudgetTransferRequestTests.Service(db).RequestAsync(ids.Project,ids.From,ids.To,amount,quote.Witness!,Guid.NewGuid(),"Native intent");Assert.True(saved.Ok,saved.Code);return(ids.Project,saved.Id!.Value);
 }
 [SkippableFact]
 public async Task Native_rerunnable_schema_parallel_consent_is_once_independent_and_immutable_without_budget_execution()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFDEC");
  try{
   var ids=await Prepare(probe);using(var own=sql.ContextFor(probe)){var result=await ConstructionR7BudgetTransferDecisionTests.Service(own,employee:7).DecideAsync(ids.Project,ids.Request,"Approved",Guid.NewGuid(),"Self");Assert.Equal("SeparateApproverRequired",result.Code);}
   // SQL defense independently refuses a fabricated self-decision, even outside the service.
   Assert.Equal(51962,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,"INSERT dbo.ConstructionBudgetTransferDecisions(CompanyID,ProjectId,TransferRequestId,Outcome,DecisionId,RequestHash,SourceVersion,Reason,CreatedBy,CreatedAt) SELECT CompanyID,ProjectId,ID,N'Approved',NEWID(),RequestHash,SourceVersion,N'Self',CreatedBy,SYSUTCDATETIME() FROM dbo.ConstructionBudgetTransferRequests;"))).Number);
   var key=Guid.NewGuid();async Task<ConstructionTransferRequestResult> Send(){using var db=sql.ContextFor(probe);return await ConstructionR7BudgetTransferDecisionTests.Service(db).DecideAsync(ids.Project,ids.Request,"Approved",key,"Independent native review");}
   var results=await Task.WhenAll(Send(),Send());Assert.All(results,x=>Assert.True(x.Ok,x.Code));Assert.Equal(results[0].Id,results[1].Id);
   await Execute(probe.ConnectionString,Script("construction_r7_budget_transfer_decisions.sql"));
   foreach(var change in new[]{"UPDATE dbo.ConstructionBudgetTransferDecisions SET Outcome=N'Rejected';","DELETE dbo.ConstructionBudgetTransferDecisions;"})Assert.Equal(51961,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,change))).Number);
   using var verify=sql.ContextFor(probe);var decision=await verify.Set<ConstructionBudgetTransferDecision>().AsNoTracking().SingleAsync();Assert.Equal(9,decision.CreatedBy);Assert.Equal("Approved",decision.Outcome);
   Assert.Single(await verify.Set<ConstructionBudgetTransferRequest>().ToArrayAsync());Assert.Single(await verify.ConstructionAuditEntries.Where(x=>x.EntityType==nameof(ConstructionBudgetTransferDecision)).ToArrayAsync());Assert.Single(await verify.Set<ConstructionBudgetVersion>().ToArrayAsync());Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_decision_audit_failure_rolls_back_and_disabled_policy_can_only_be_refused()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFDECRB");
  try{
   var ids=await Prepare(probe);await Execute(probe.ConnectionString,"CREATE TRIGGER dbo.R7RejectDecisionAudit ON dbo.ConstructionAuditEntries AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE EntityType=N'ConstructionBudgetTransferDecision') THROW 51969,'Deliberate audit failure',1; END;");
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7BudgetTransferDecisionTests.Service(db).DecideAsync(ids.Project,ids.Request,"Approved",Guid.NewGuid(),"Review");Assert.False(result.Ok);}
   using(var verify=sql.ContextFor(probe)){Assert.Empty(await verify.Set<ConstructionBudgetTransferDecision>().ToArrayAsync());Assert.Single(await verify.ConstructionAuditEntries.ToArrayAsync());}
   await Execute(probe.ConnectionString,"DROP TRIGGER dbo.R7RejectDecisionAudit; UPDATE dbo.ConstructionCompanyPolicies SET Mode=N'Disabled' WHERE PolicyKey=N'BudgetTransfer';");
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7BudgetTransferDecisionTests.Service(db).DecideAsync(ids.Project,ids.Request,"Approved",Guid.NewGuid(),"Disabled consent");Assert.False(result.Ok);Assert.Equal("TransferDisabled",result.Code);}
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7BudgetTransferDecisionTests.Service(db).DecideAsync(ids.Project,ids.Request,"Rejected",Guid.NewGuid(),"Disabled refusal");Assert.True(result.Ok,result.Code);}
   using var final=sql.ContextFor(probe);Assert.Equal("Rejected",(await final.Set<ConstructionBudgetTransferDecision>().SingleAsync()).Outcome);Assert.Equal(1050m,await final.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
