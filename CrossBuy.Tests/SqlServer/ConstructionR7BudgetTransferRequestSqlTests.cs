using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7BudgetTransferRequestSqlTests(UatSqlProbeFixture sql)
{
 private static string Script()
 {
  for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent){var p=Path.Combine(d.FullName,"CrossBuy/deploy/sql/construction_r7_budget_transfer_requests.sql");if(File.Exists(p))return File.ReadAllText(p);}
  throw new FileNotFoundException("Transfer request schema");
 }
 private static async Task Execute(string connectionString,string text)
 {
  await using var connection=new SqlConnection(connectionString);await connection.OpenAsync();
  foreach(var batch in Regex.Split(text,@"(?im)^\s*GO\s*(?:--[^\r\n]*)?\r?$")){
   if(string.IsNullOrWhiteSpace(batch))continue;await using var cmd=connection.CreateCommand();cmd.CommandText=batch;cmd.CommandTimeout=120;await cmd.ExecuteNonQueryAsync();
  }
 }
 [SkippableFact]
 public async Task Authored_request_schema_reruns_and_parallel_replay_records_one_immutable_intent_without_budget_mutation()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFREQ");
  try{
   (int Project,int From,int To) ids;using(var seed=sql.ContextFor(probe,1))ids=await ConstructionR7BudgetTransferPreviewTests.Seed(seed);
   await Execute(probe.ConnectionString,"IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferDecisions) THROW 51960,'Fixture decisions must be empty',1; IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferApplications) THROW 51970,'Owned application fixture must be empty',1; DROP TABLE dbo.ConstructionBudgetTransferApplications; DROP TABLE dbo.ConstructionBudgetTransferDecisions; IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferRequests) THROW 51950,'Fixture must be empty',1; DROP TABLE dbo.ConstructionBudgetTransferRequests;");
   await Execute(probe.ConnectionString,Script());await Execute(probe.ConnectionString,Script());
   string witness;using(var db=sql.ContextFor(probe,1)){var q=await ConstructionR7BudgetTransferPreviewTests.Service(db).PreviewAsync(ids.Project,ids.From,ids.To,1.2300m);Assert.True(q.Ok,q.Code);witness=q.Witness!;}
   var key=Guid.NewGuid();
   async Task<ConstructionTransferRequestResult> Send(){using var db=sql.ContextFor(probe,1);return await ConstructionR7BudgetTransferRequestTests.Service(db).RequestAsync(ids.Project,ids.From,ids.To,1.23m,witness,key,"Native request");}
   var results=await Task.WhenAll(Send(),Send());Assert.All(results,x=>Assert.True(x.Ok,x.Code));Assert.Equal(results[0].Id,results[1].Id);
   await Execute(probe.ConnectionString,Script());
   foreach(var text in new[]{"UPDATE dbo.ConstructionBudgetTransferRequests SET Amount=2;","DELETE dbo.ConstructionBudgetTransferRequests;"})
    Assert.Equal(51951,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,text))).Number);
   using var verify=sql.ContextFor(probe,1);var row=await verify.Set<ConstructionBudgetTransferRequest>().AsNoTracking().SingleAsync();
   Assert.Equal(1.23m,row.Amount);Assert.Equal(1050m,row.FromAfter+row.ToAfter);Assert.Single(await verify.ConstructionAuditEntries.AsNoTracking().Where(x=>x.EntityType==nameof(ConstructionBudgetTransferRequest)).ToArrayAsync());
   Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));Assert.Single(await verify.Set<ConstructionBudgetVersion>().ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
   Assert.Equal(3,await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionBudgetTransferRequests') AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_audit_failure_rolls_back_the_new_request_without_leaving_partial_evidence()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFROLL");
  try{
   (int Project,int From,int To) ids;using(var seed=sql.ContextFor(probe,1))ids=await ConstructionR7BudgetTransferPreviewTests.Seed(seed);
   await Execute(probe.ConnectionString,"IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferDecisions) THROW 51960,'Fixture decisions must be empty',1; IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferApplications) THROW 51970,'Owned application fixture must be empty',1; DROP TABLE dbo.ConstructionBudgetTransferApplications; DROP TABLE dbo.ConstructionBudgetTransferDecisions; DROP TABLE dbo.ConstructionBudgetTransferRequests;");await Execute(probe.ConnectionString,Script());
   await Execute(probe.ConnectionString,"CREATE TRIGGER dbo.R7RejectTransferAudit ON dbo.ConstructionAuditEntries AFTER INSERT AS THROW 51959,'Deliberate audit failure',1;");
   using(var db=sql.ContextFor(probe,1)){
    var q=await ConstructionR7BudgetTransferPreviewTests.Service(db).PreviewAsync(ids.Project,ids.From,ids.To,1m);Assert.True(q.Ok,q.Code);
    var result=await ConstructionR7BudgetTransferRequestTests.Service(db).RequestAsync(ids.Project,ids.From,ids.To,1m,q.Witness!,Guid.NewGuid(),"Rollback probe");Assert.False(result.Ok);
   }
   using var verify=sql.ContextFor(probe,1);Assert.Empty(await verify.Set<ConstructionBudgetTransferRequest>().ToArrayAsync());Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
