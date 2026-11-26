using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7BudgetTransferApplicationSqlTests(UatSqlProbeFixture sql)
{
 private static Task Execute(string c,string text)=>ConstructionR7BudgetTransferDecisionSqlTests.Execute(c,text);
 private static string Script(string name)=>ConstructionR7BudgetTransferDecisionSqlTests.Script(name);
 private async Task<(int Project,int Decision)> Prepare(UatSqlProbeFixture.ProbeDatabase probe,decimal amount=1.23m,byte? decimals=null)
 {
  var ids=await new ConstructionR7BudgetTransferDecisionSqlTests(sql).Prepare(probe,amount,decimals,true);
  // Exercise the actual native history and immutable-line triggers for every
  // intermediate state of the new version, not just EF-generated persistence.
  await Execute(probe.ConnectionString,Script("construction_r7_budget_versions.sql"));
  await Execute(probe.ConnectionString,Script("construction_r7_budget_transfer_applications.sql"));
  await Execute(probe.ConnectionString,Script("construction_r7_budget_transfer_applications.sql"));
  using var db=sql.ContextFor(probe);var decision=await ConstructionR7BudgetTransferDecisionTests.Service(db).DecideAsync(ids.Project,ids.Request,"Approved",Guid.NewGuid(),"Native independent consent");
  Assert.True(decision.Ok,decision.Code);return(ids.Project,decision.Id!.Value);
 }
 [SkippableFact]
 public async Task Native_application_is_once_parallel_balanced_linked_and_immutable_with_real_history_triggers()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFAPP");
  try{
   var ids=await Prepare(probe);var key=Guid.NewGuid();
   async Task<ConstructionTransferRequestResult> Send(){using var db=sql.ContextFor(probe);return await ConstructionR7BudgetTransferApplicationTests.Service(db).ApplyAsync(ids.Project,ids.Decision,key,"Native execution");}
   var results=await Task.WhenAll(Send(),Send());Assert.All(results,x=>Assert.True(x.Ok,x.Code));Assert.Equal(results[0].Id,results[1].Id);
   await Execute(probe.ConnectionString,Script("construction_r7_budget_transfer_applications.sql"));
   foreach(var change in new[]{"UPDATE dbo.ConstructionBudgetTransferApplications SET Reason=N'Overwrite';","DELETE dbo.ConstructionBudgetTransferApplications;"})
    Assert.Equal(51971,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,change))).Number);
   using var verify=sql.ContextFor(probe);var fact=await verify.Set<ConstructionBudgetTransferApplication>().AsNoTracking().SingleAsync();
   Assert.Equal(7,fact.AppliedBy);var baseline=await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x=>x.IsBaseline);
   Assert.Equal(fact.ResultBudgetVersionId,baseline.ID);Assert.Equal(9,baseline.ApprovedBy);Assert.Equal(7,baseline.CreatedBy);Assert.Equal("Approved",baseline.Status);
   var old=await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x=>x.ID==fact.OriginalBudgetVersionId);Assert.Equal("Superseded",old.Status);Assert.False(old.IsBaseline);
   var oldLines=await verify.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.BudgetVersionId==old.ID).ToArrayAsync();
   var newLines=await verify.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.BudgetVersionId==baseline.ID).ToArrayAsync();
   Assert.Equal(1050m,oldLines.Sum(x=>x.Amount));Assert.Equal(1050m,newLines.Sum(x=>x.Amount));Assert.Equal(oldLines.Length,newLines.Length);
   var request=await verify.Set<ConstructionBudgetTransferRequest>().AsNoTracking().SingleAsync();
   foreach(var line in oldLines){var next=newLines.Single(x=>x.WbsNodeId==line.WbsNodeId&&x.CostCodeId==line.CostCodeId);Assert.Equal(line.Amount,next.PreviousAmount);Assert.Equal(line.ID==request.FromLineId?request.FromAfter:line.ID==request.ToLineId?request.ToAfter:line.Amount,next.Amount);}
   Assert.Single(await verify.ConstructionAuditEntries.Where(x=>x.EntityType==nameof(ConstructionBudgetTransferApplication)).ToArrayAsync());
   Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_late_audit_failure_restores_original_baseline_rowversion_lines_and_allows_safe_retry()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFAPPRB");
  try{
   var ids=await Prepare(probe);byte[] token;int baselineId;int auditCount;
   using(var before=sql.ContextFor(probe)){var baseline=await before.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync();baselineId=baseline.ID;token=baseline.RowVersion; auditCount=await before.ConstructionAuditEntries.CountAsync();}
   await Execute(probe.ConnectionString,"CREATE TRIGGER dbo.R7RejectApplicationAudit ON dbo.ConstructionAuditEntries AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE EntityType=N'ConstructionBudgetTransferApplication') THROW 51979,'Deliberate application audit failure',1; END;");
   var key=Guid.NewGuid();using(var db=sql.ContextFor(probe)){var result=await ConstructionR7BudgetTransferApplicationTests.Service(db).ApplyAsync(ids.Project,ids.Decision,key,"Atomic native execution");Assert.False(result.Ok);}
   using(var verify=sql.ContextFor(probe)){var old=await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync();Assert.Equal(baselineId,old.ID);Assert.True(old.IsBaseline);Assert.Equal("Approved",old.Status);Assert.Equal(token,old.RowVersion);Assert.Empty(await verify.Set<ConstructionBudgetTransferApplication>().ToArrayAsync());Assert.Equal(2,await verify.Set<ConstructionBudgetLine>().CountAsync());Assert.Equal(auditCount,await verify.ConstructionAuditEntries.CountAsync());}
   await Execute(probe.ConnectionString,"DROP TRIGGER dbo.R7RejectApplicationAudit;");
   using(var retry=sql.ContextFor(probe)){var result=await ConstructionR7BudgetTransferApplicationTests.Service(retry).ApplyAsync(ids.Project,ids.Decision,key,"Atomic native execution");Assert.True(result.Ok,result.Code);}
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableTheory][InlineData("disabled")][InlineData("currency-default")]
 public async Task Native_policy_or_company_currency_change_after_consent_never_materializes_a_new_budget(string mode)
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFAPPDIS");
  try{
   var ids=await Prepare(probe);await Execute(probe.ConnectionString,mode=="disabled"?"UPDATE dbo.ConstructionCompanyPolicies SET Mode=N'Disabled' WHERE PolicyKey=N'BudgetTransfer';":"UPDATE dbo.Companies SET DefaultCurrencyId=NULL WHERE CompanyID=1;");
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7BudgetTransferApplicationTests.Service(db).ApplyAsync(ids.Project,ids.Decision,Guid.NewGuid(),"Stale consent");Assert.False(result.Ok);if(mode=="disabled")Assert.Equal("TransferDisabled",result.Code);}
   using var verify=sql.ContextFor(probe);Assert.Empty(await verify.Set<ConstructionBudgetTransferApplication>().ToArrayAsync());Assert.Single(await verify.Set<ConstructionBudgetVersion>().Where(x=>x.IsBaseline).ToArrayAsync());Assert.Equal(2,await verify.Set<ConstructionBudgetLine>().CountAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_competing_application_keys_produce_only_one_effective_budget()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFAPPRACE");
  try{
   var ids=await Prepare(probe);async Task<ConstructionTransferRequestResult> Send(){using var db=sql.ContextFor(probe);return await ConstructionR7BudgetTransferApplicationTests.Service(db).ApplyAsync(ids.Project,ids.Decision,Guid.NewGuid(),"Distinct concurrent keys");}
   var results=await Task.WhenAll(Send(),Send());Assert.Single(results.Where(x=>x.Ok));Assert.Equal("TransferAlreadyApplied",Assert.Single(results.Where(x=>!x.Ok)).Code);
   using var verify=sql.ContextFor(probe);Assert.Single(await verify.Set<ConstructionBudgetTransferApplication>().ToArrayAsync());Assert.Equal(2,await verify.Set<ConstructionBudgetVersion>().CountAsync());Assert.Single(await verify.Set<ConstructionBudgetVersion>().Where(x=>x.IsBaseline).ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableTheory][InlineData((byte)0,"1")][InlineData((byte)8,"0.12345678")]
 public async Task Native_execution_retains_zero_and_eight_decimal_currency_amounts(byte decimals,string value)
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFAPPCUR");
  try{
   var amount=decimal.Parse(value,System.Globalization.CultureInfo.InvariantCulture);var ids=await Prepare(probe,amount,decimals);
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7BudgetTransferApplicationTests.Service(db).ApplyAsync(ids.Project,ids.Decision,Guid.NewGuid(),"Native exact precision");Assert.True(result.Ok,result.Code);}
   using var verify=sql.ContextFor(probe);var app=await verify.Set<ConstructionBudgetTransferApplication>().AsNoTracking().SingleAsync();
   var rows=await verify.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.BudgetVersionId==app.ResultBudgetVersionId).ToArrayAsync();
   Assert.Contains(rows,x=>x.Amount==1000m-amount);Assert.Contains(rows,x=>x.Amount==50m+amount);Assert.Equal(1050m,rows.Sum(x=>x.Amount));
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }

 [SkippableFact]
 public async Task Losing_racer_returns_the_winning_application_when_the_project_lock_times_out()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFAPPLOSE");
  try{
   var ids=await Prepare(probe);var key=Guid.NewGuid();int winner;
   // The winner runs and COMMITS first. Everything after this point is the losing racer.
   using(var first=sql.ContextFor(probe)){var ok=await ConstructionR7BudgetTransferApplicationTests.Service(first).ApplyAsync(ids.Project,ids.Decision,key,"Native execution");
    Assert.True(ok.Ok,ok.Code);winner=ok.Id!.Value;}
   // Force the lock to fail exactly as a 10-second sp_getapplock timeout does. Before the fix the
   // loser returned ConcurrencyConflict here, never reaching its own idempotency re-read.
   ConstructionCommitmentReservationService.LockOutcomeForTests=(_,_)=>false;
   try{
    using var second=sql.ContextFor(probe);
    var loser=await ConstructionR7BudgetTransferApplicationTests.Service(second).ApplyAsync(ids.Project,ids.Decision,key,"Native execution");
    Assert.True(loser.Ok,loser.Code);Assert.Equal(winner,loser.Id);
   }finally{ConstructionCommitmentReservationService.LockOutcomeForTests=null;}
   // Exactly once: one application, one baseline, no orphaned version, totals still balanced.
   using var verify=sql.ContextFor(probe);
   Assert.Single(await verify.Set<ConstructionBudgetTransferApplication>().AsNoTracking().ToArrayAsync());
   Assert.Single(await verify.Set<ConstructionBudgetVersion>().AsNoTracking().Where(x=>x.IsBaseline).ToArrayAsync());
   var baseline=await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x=>x.IsBaseline);
   var superseded=await verify.Set<ConstructionBudgetVersion>().AsNoTracking().Where(x=>!x.IsBaseline).ToArrayAsync();
   Assert.Single(superseded);Assert.Equal("Superseded",superseded[0].Status);
   var newLines=await verify.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.BudgetVersionId==baseline.ID).ToArrayAsync();
   var oldLines=await verify.Set<ConstructionBudgetLine>().AsNoTracking().Where(x=>x.BudgetVersionId==superseded[0].ID).ToArrayAsync();
   Assert.Equal(oldLines.Sum(x=>x.Amount),newLines.Sum(x=>x.Amount));
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Losing_racer_still_refuses_when_no_prior_application_exists()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFAPPNONE");
  try{
   var ids=await Prepare(probe);
   // No winner has run. A lock failure with nothing committed must stay a refusal - the recovery
   // must not turn every concurrency failure into a success.
   ConstructionCommitmentReservationService.LockOutcomeForTests=(_,_)=>false;
   try{
    using var db=sql.ContextFor(probe);
    var result=await ConstructionR7BudgetTransferApplicationTests.Service(db).ApplyAsync(ids.Project,ids.Decision,Guid.NewGuid(),"Native execution");
    Assert.False(result.Ok);Assert.Equal("ConcurrencyConflict",result.Code);
   }finally{ConstructionCommitmentReservationService.LockOutcomeForTests=null;}
   using var verify=sql.ContextFor(probe);
   Assert.Empty(await verify.Set<ConstructionBudgetTransferApplication>().AsNoTracking().ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Losing_racer_refuses_when_the_prior_application_hash_differs()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFAPPHASH");
  try{
   var ids=await Prepare(probe);var key=Guid.NewGuid();
   using(var first=sql.ContextFor(probe)){var ok=await ConstructionR7BudgetTransferApplicationTests.Service(first).ApplyAsync(ids.Project,ids.Decision,key,"Native execution");
    Assert.True(ok.Ok,ok.Code);}
   // Same idempotency key, DIFFERENT payload. A reused key must never adopt a new payload.
   ConstructionCommitmentReservationService.LockOutcomeForTests=(_,_)=>false;
   try{
    using var second=sql.ContextFor(probe);
    var reused=await ConstructionR7BudgetTransferApplicationTests.Service(second).ApplyAsync(ids.Project,ids.Decision,key,"A conflicting payload");
    Assert.False(reused.Ok);Assert.Equal("ApplicationIdConflict",reused.Code);
   }finally{ConstructionCommitmentReservationService.LockOutcomeForTests=null;}
   using var verify=sql.ContextFor(probe);
   Assert.Single(await verify.Set<ConstructionBudgetTransferApplication>().AsNoTracking().ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [Fact]
 public void No_production_path_assigns_the_lock_test_seam()
 {
  // LockOutcomeForTests must be public because this repository declares no InternalsVisibleTo, so a
  // public mutable static now exists that could silently disable a production lock. This is the same
  // structural guard InventoryApprovalService's hook carries: assert nothing under CrossBuy/ assigns it.
  var root=new DirectoryInfo(AppContext.BaseDirectory);
  while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"CrossBuy","BL")))root=root.Parent;
  Assert.NotNull(root);
  var offenders=Directory.EnumerateFiles(Path.Combine(root!.FullName,"CrossBuy"),"*.cs",SearchOption.AllDirectories)
   .Where(f=>!f.Contains(Path.DirectorySeparatorChar+"obj"+Path.DirectorySeparatorChar)
          &&!f.Contains(Path.DirectorySeparatorChar+"bin"+Path.DirectorySeparatorChar))
   .Where(f=>File.ReadAllLines(f).Where(l=>!l.TrimStart().StartsWith("//")&&!l.TrimStart().StartsWith("///"))
    .Any(l=>l.Contains("LockOutcomeForTests")&&l.Contains("=")&&!l.Contains("public static Func")))
   .ToArray();
  Assert.Empty(offenders);
 }
}
