using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7CostForecastSqlTests(UatSqlProbeFixture sql)
{
 private const string File="construction_r7_cost_forecast_snapshots.sql";
 private static Task Execute(string connection,string text)=>ConstructionR7BudgetTransferDecisionSqlTests.Execute(connection,text);
 private static string Script()=>ConstructionR7BudgetTransferDecisionSqlTests.Script(File);
 private async Task<(int Project,ConstructionForecastEstimate[] Estimates)> Prepare(UatSqlProbeFixture.ProbeDatabase probe,byte? decimals=null)
 {
  (int Project,ConstructionForecastEstimate[] Estimates) ids;
  using(var db=sql.ContextFor(probe)){ids=await ConstructionR7CostForecastTests.Seed(db);if(decimals.HasValue){var precision=decimals.Value;await db.Currencies.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.DecimalPlaces,precision));}}
  await Execute(probe.ConnectionString,"IF EXISTS(SELECT 1 FROM dbo.ConstructionCostForecastSnapshots) THROW 51980,'Owned forecast fixture must be empty',1; DROP TABLE dbo.ConstructionCostForecastSnapshots;");
  await Execute(probe.ConnectionString,Script());await Execute(probe.ConnectionString,Script());return ids;
 }
 private static string Clone()=>"INSERT dbo.ConstructionCostForecastSnapshots(CompanyID,ProjectId,BudgetVersionId,CurrencyId,CurrencyDecimals,PreviousSnapshotId,RequestId,RequestHash,SourceVersion,PolicyToken,Method,SnapshotJson,EstimateAtCompletion,BudgetVariance,HasCommitmentWarning,Reason,CreatedBy,CreatedAt) SELECT CompanyID,ProjectId,BudgetVersionId,CurrencyId,CurrencyDecimals,ID,NEWID(),RequestHash,SourceVersion,PolicyToken,Method,SnapshotJson,EstimateAtCompletion,BudgetVariance,HasCommitmentWarning,Reason,CreatedBy,CreatedAt FROM dbo.ConstructionCostForecastSnapshots WHERE PreviousSnapshotId IS NULL;";
 [SkippableFact]
 public async Task Native_parallel_retry_is_once_and_history_is_immutable_after_idempotent_schema_rerun()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7FCRETRY");
  try{
   var ids=await Prepare(probe);ConstructionForecastPreview quote;using(var db=sql.ContextFor(probe)){quote=await ConstructionR7CostForecastTests.Service(db).PreviewAsync(ids.Project,ids.Estimates);Assert.True(quote.Ok,quote.Code);}
   var key=Guid.NewGuid();async Task<ConstructionForecastSave> Send(){using var db=sql.ContextFor(probe);return await ConstructionR7CostForecastTests.Service(db).SaveAsync(ids.Project,ids.Estimates,quote.Witness!,key,null,"Native authored forecast");}
   var result=await Task.WhenAll(Send(),Send());Assert.All(result,x=>Assert.True(x.Ok,x.Code));Assert.Equal(result[0].Id,result[1].Id);
   await Execute(probe.ConnectionString,Script());
   foreach(var change in new[]{"UPDATE dbo.ConstructionCostForecastSnapshots SET Reason=N'Overwrite';","DELETE dbo.ConstructionCostForecastSnapshots;"})Assert.Equal(51981,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,change))).Number);
   using var verify=sql.ContextFor(probe);var row=await verify.Set<ConstructionCostForecastSnapshot>().AsNoTracking().SingleAsync();Assert.Equal(7,row.CreatedBy);Assert.Equal(525m,row.EstimateAtCompletion);Assert.Single(await verify.ConstructionAuditEntries.ToArrayAsync());
   Assert.Single(await verify.Set<ConstructionBudgetVersion>().ToArrayAsync());Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_competing_new_keys_cannot_fork_project_history()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7FCRACE");
  try{
   var ids=await Prepare(probe);ConstructionForecastPreview quote;using(var db=sql.ContextFor(probe)){quote=await ConstructionR7CostForecastTests.Service(db).PreviewAsync(ids.Project,ids.Estimates);Assert.True(quote.Ok,quote.Code);}
   async Task<ConstructionForecastSave> Send(){using var db=sql.ContextFor(probe);return await ConstructionR7CostForecastTests.Service(db).SaveAsync(ids.Project,ids.Estimates,quote.Witness!,Guid.NewGuid(),null,"Concurrent new forecast");}
   var results=await Task.WhenAll(Send(),Send());Assert.Single(results.Where(x=>x.Ok));Assert.Equal("ConcurrencyConflict",Assert.Single(results.Where(x=>!x.Ok)).Code);
   using var verify=sql.ContextFor(probe);Assert.Single(await verify.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());Assert.Single(await verify.ConstructionAuditEntries.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_late_audit_failure_leaves_no_snapshot_or_audit_then_safe_retry_works()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7FCAUDIT");
  try{
   var ids=await Prepare(probe);ConstructionForecastPreview quote;using(var db=sql.ContextFor(probe)){quote=await ConstructionR7CostForecastTests.Service(db).PreviewAsync(ids.Project,ids.Estimates);Assert.True(quote.Ok,quote.Code);}
   await Execute(probe.ConnectionString,"CREATE TRIGGER dbo.R7RejectForecastAudit ON dbo.ConstructionAuditEntries AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE EntityType=N'ConstructionCostForecastSnapshot') THROW 51989,'Deliberate forecast audit failure',1; END;");
   var key=Guid.NewGuid();using(var db=sql.ContextFor(probe)){var result=await ConstructionR7CostForecastTests.Service(db).SaveAsync(ids.Project,ids.Estimates,quote.Witness!,key,null,"Atomic native forecast");Assert.False(result.Ok);}
   using(var verify=sql.ContextFor(probe)){Assert.Empty(await verify.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());}
   await Execute(probe.ConnectionString,"DROP TRIGGER dbo.R7RejectForecastAudit;");
   using(var retry=sql.ContextFor(probe)){var result=await ConstructionR7CostForecastTests.Service(retry).SaveAsync(ids.Project,ids.Estimates,quote.Witness!,key,null,"Atomic native forecast");Assert.True(result.Ok,result.Code);}
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableTheory][InlineData((byte)0,"1")][InlineData((byte)8,"0.12345678")]
 public async Task Native_currency_precision_is_retained_without_silent_rounding(byte decimals,string amount)
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7FCCUR");
  try{
   var ids=await Prepare(probe,decimals);var value=decimal.Parse(amount,System.Globalization.CultureInfo.InvariantCulture);var estimates=ids.Estimates.Select(x=>x with{RemainingCostIncludingCommitments=value}).ToArray();
   using(var db=sql.ContextFor(probe)){var service=ConstructionR7CostForecastTests.Service(db);var quote=await service.PreviewAsync(ids.Project,estimates);Assert.True(quote.Ok,quote.Code);var result=await service.SaveAsync(ids.Project,estimates,quote.Witness!,Guid.NewGuid(),null,"Exact currency forecast");Assert.True(result.Ok,result.Code);}
   using var verify=sql.ContextFor(probe);var fact=await verify.Set<ConstructionCostForecastSnapshot>().AsNoTracking().SingleAsync();Assert.Equal(decimals,fact.CurrencyDecimals);Assert.Equal(2*value,fact.EstimateAtCompletion);
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_direct_tampering_fails_schema_policy_scope_precision_and_math_guards()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7FCTAMPER");
  try{
   var ids=await Prepare(probe);using(var db=sql.ContextFor(probe)){var service=ConstructionR7CostForecastTests.Service(db);var quote=await service.PreviewAsync(ids.Project,ids.Estimates);Assert.True(quote.Ok,quote.Code);var saved=await service.SaveAsync(ids.Project,ids.Estimates,quote.Witness!,Guid.NewGuid(),null,"Original native forecast");Assert.True(saved.Ok,saved.Code);}
   var clone=Clone();
   async Task Reject(int code,string text){var error=await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,text));Assert.Equal(code,error.Number);}
   await Reject(51983,clone.Replace("SnapshotJson,EstimateAtCompletion,BudgetVariance,HasCommitmentWarning,Reason,CreatedBy,CreatedAt FROM","SnapshotJson,EstimateAtCompletion+1,BudgetVariance,HasCommitmentWarning,Reason,CreatedBy,CreatedAt FROM"));
   await Reject(51983,clone.Replace("Method,SnapshotJson,EstimateAtCompletion,BudgetVariance,HasCommitmentWarning,Reason,CreatedBy,CreatedAt FROM","Method,N'[]',EstimateAtCompletion,BudgetVariance,HasCommitmentWarning,Reason,CreatedBy,CreatedAt FROM"));
   await Reject(547,clone.Replace("SELECT CompanyID,ProjectId,","SELECT CompanyID+99999,ProjectId,"));
   await Reject(51984,clone.Replace("Method,SnapshotJson,EstimateAtCompletion,BudgetVariance,HasCommitmentWarning,Reason,CreatedBy,CreatedAt FROM","Method,JSON_MODIFY(SnapshotJson,'$[0].RemainingCostIncludingCommitments',500.000000001),EstimateAtCompletion,BudgetVariance,HasCommitmentWarning,Reason,CreatedBy,CreatedAt FROM"));
   await Execute(probe.ConnectionString,"UPDATE dbo.ConstructionCompanyPolicies SET Mode=N'Disabled' WHERE PolicyKey=N'CostForecast';");
   await Reject(51982,clone);
   using(var verify=sql.ContextFor(probe)){Assert.Single(await verify.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());Assert.Single(await verify.ConstructionAuditEntries.ToArrayAsync());}
   await Execute(probe.ConnectionString,"UPDATE dbo.ConstructionCompanyPolicies SET Mode=N'ManualAuthorOwned' WHERE PolicyKey=N'CostForecast';");
   await Reject(2601,clone.Replace("CurrencyDecimals,ID,NEWID()","CurrencyDecimals,NULL,NEWID()"));
   await Execute(probe.ConnectionString,clone);
   await Reject(2601,clone);
   await Execute(probe.ConnectionString,Script());
   await Execute(probe.ConnectionString,"ALTER TABLE dbo.ConstructionCostForecastSnapshots NOCHECK CONSTRAINT CK_ConstructionCostForecastSnapshots_Values;");
   await Reject(51980,Script());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
