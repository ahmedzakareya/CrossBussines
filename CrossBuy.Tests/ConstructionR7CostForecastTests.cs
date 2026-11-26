using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7CostForecastTests
{
 internal static ConstructionCostForecastService Service(CrossDbContext db,int employee=7,bool allowed=true,int company=1)=>
  new(db,ConstructionR7BudgetTransferDecisionTests.Actor(employee,company),new ConstructionR7ActualCostTests.Access(allowed),new ConstructionAuditService(db));
 internal static async Task<(int Project,ConstructionForecastEstimate[] Estimates)> Seed(CrossDbContext db)
 {
  var ids=await ConstructionR7BudgetTransferPreviewTests.Seed(db);await ConstructionR7BudgetTransferApplicationTests.ConfigureCompany(db);
  db.Add(new ConstructionCompanyPolicy{CompanyID=1,PolicyKey="CostForecast",Mode="ManualAuthorOwned",Reason="Explicit test company decision",UpdatedBy=7,UpdatedAt=DateTime.UtcNow});await db.SaveChangesAsync();
  var lines=await db.Set<ConstructionBudgetLine>().AsNoTracking().OrderBy(x=>x.ID).ToArrayAsync();
  return(ids.Project,lines.Select(x=>new ConstructionForecastEstimate(x.WbsNodeId,x.CostCodeId,x.Amount/2)).ToArray());
 }
 [Fact]
 public async Task Snapshot_replay_and_append_preserve_author_history_and_financial_sources()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var service=Service(f.Db);
  var q=await service.PreviewAsync(s.Project,s.Estimates);Assert.True(q.Ok,q.Code);Assert.Equal(525m,q.Calculation!.EstimateAtCompletion);
  var key=Guid.NewGuid();var saved=await service.SaveAsync(s.Project,s.Estimates,q.Witness!,key,null,"Original forecast");Assert.True(saved.Ok,saved.Code);
  var replay=await service.SaveAsync(s.Project,s.Estimates.Reverse().ToArray(),q.Witness!,key,null,"Original forecast");Assert.Equal(saved.Id,replay.Id);Assert.True(replay.Ok,replay.Code);
  Assert.Equal("ForecastRequestIdConflict",(await service.SaveAsync(s.Project,s.Estimates,q.Witness!,key,null,"Different reason")).Code);
  Assert.Equal("ConcurrencyConflict",(await service.SaveAsync(s.Project,s.Estimates,q.Witness!,Guid.NewGuid(),null,"Stale history")).Code);
  var next=await Service(f.Db,employee:10).PreviewAsync(s.Project,s.Estimates);Assert.True(next.Ok,next.Code);
  var revision=await Service(f.Db,employee:10).SaveAsync(s.Project,s.Estimates,next.Witness!,Guid.NewGuid(),saved.Id,"Updated authored forecast");Assert.True(revision.Ok,revision.Code);
  await using var verify=f.NewContext();var rows=await verify.Set<ConstructionCostForecastSnapshot>().AsNoTracking().OrderBy(x=>x.ID).ToArrayAsync();Assert.Equal(2,rows.Length);
  Assert.Equal(7,rows[0].CreatedBy);Assert.Equal(10,rows[1].CreatedBy);Assert.Equal(rows[0].ID,rows[1].PreviousSnapshotId);Assert.Equal("Original forecast",rows[0].Reason);
  Assert.Equal(2,await verify.ConstructionAuditEntries.CountAsync());Assert.Equal(1050m,(await verify.Set<ConstructionBudgetLine>().ToArrayAsync()).Sum(x=>x.Amount));
  Assert.Single(await verify.Set<ConstructionBudgetVersion>().ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
 }
 [Theory][InlineData("disabled")][InlineData("missing")][InlineData("denied")][InlineData("foreign")][InlineData("inactive")][InlineData("currency")][InlineData("dimensions")][InlineData("precision")]
 public async Task Invalid_authority_or_coverage_never_persists_a_forecast(string mode)
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var service=Service(f.Db);
  var q=await service.PreviewAsync(s.Project,s.Estimates);Assert.True(q.Ok,q.Code);
  if(mode=="disabled")await f.Db.Set<ConstructionCompanyPolicy>().Where(x=>x.PolicyKey=="CostForecast").ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Disabled"));
  if(mode=="missing")await f.Db.Set<ConstructionCompanyPolicy>().Where(x=>x.PolicyKey=="CostForecast").ExecuteDeleteAsync();
  if(mode=="inactive")await f.Db.Set<WbsNode>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Status,"Archived"));
  if(mode=="currency")await f.Db.Companies.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.DefaultCurrencyId,(int?)null));
  var estimates=mode=="dimensions"?s.Estimates.Take(1).ToArray():mode=="precision"?s.Estimates.Select(x=>x with{RemainingCostIncludingCommitments=0.0001m}).ToArray():s.Estimates;
  await using var db=f.NewContext();var result=await Service(db,allowed:mode!="denied",company:mode=="foreign"?2:1).SaveAsync(s.Project,estimates,q.Witness!,Guid.NewGuid(),null,"Unaccepted forecast");Assert.False(result.Ok);
  await using var verify=f.NewContext();Assert.Empty(await verify.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());
 }
 [Fact]
 public async Task Policy_token_rotation_invalidates_preview_but_exact_saved_retry_stays_idempotent()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var service=Service(f.Db);var q=await service.PreviewAsync(s.Project,s.Estimates);Assert.True(q.Ok,q.Code);
  var rotatedToken=Guid.NewGuid().ToByteArray();
  await f.Db.Set<ConstructionCompanyPolicy>().Where(x=>x.PolicyKey=="CostForecast").ExecuteUpdateAsync(x=>x.SetProperty(y=>y.ConcurrencyToken,rotatedToken));
  Assert.Equal("ConcurrencyConflict",(await service.SaveAsync(s.Project,s.Estimates,q.Witness!,Guid.NewGuid(),null,"Stale quote")).Code);
  q=await service.PreviewAsync(s.Project,s.Estimates);var key=Guid.NewGuid();var first=await service.SaveAsync(s.Project,s.Estimates,q.Witness!,key,null,"Persisted snapshot");Assert.True(first.Ok,first.Code);
  await f.Db.Set<ConstructionCompanyPolicy>().Where(x=>x.PolicyKey=="CostForecast").ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Disabled"));
  var retry=await service.SaveAsync(s.Project,s.Estimates,q.Witness!,key,null,"Persisted snapshot");Assert.True(retry.Ok,retry.Code);Assert.Equal(first.Id,retry.Id);
  await using var verify=f.NewContext();Assert.Single(await verify.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());Assert.Single(await verify.ConstructionAuditEntries.ToArrayAsync());
 }
 private sealed class FailingAudit:IConstructionAuditService
 {
  public int Record(IEnumerable<ConstructionAuditDraft> drafts)=>throw new InvalidOperationException("Injected late audit failure");
  public Task<List<ConstructionAuditEntry>> ForEntityAsync(int companyId,string entityType,int entityId,CancellationToken ct=default)=>throw new NotSupportedException();
  public Task<List<ConstructionAuditEntry>> ForCompanyAsync(int companyId,string? entityType=null,CancellationToken ct=default)=>throw new NotSupportedException();
  public Task<List<ConstructionAuditEntry>> ForCorrelationAsync(int companyId,Guid correlationId,CancellationToken ct=default)=>throw new NotSupportedException();
 }
 [Fact]
 public async Task Late_audit_failure_rolls_back_snapshot_and_safe_retry_succeeds()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);var q=await Service(f.Db).PreviewAsync(s.Project,s.Estimates);Assert.True(q.Ok,q.Code);var key=Guid.NewGuid();
  var broken=new ConstructionCostForecastService(f.Db,ConstructionR7BudgetTransferDecisionTests.Actor(7),new ConstructionR7ActualCostTests.Access(),new FailingAudit());
  await Assert.ThrowsAsync<InvalidOperationException>(()=>broken.SaveAsync(s.Project,s.Estimates,q.Witness!,key,null,"Atomic forecast"));
  await using(var verify=f.NewContext()){Assert.Empty(await verify.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());}
  var saved=await Service(f.Db).SaveAsync(s.Project,s.Estimates,q.Witness!,key,null,"Atomic forecast");Assert.True(saved.Ok,saved.Code);
  await using var final=f.NewContext();Assert.Single(await final.Set<ConstructionCostForecastSnapshot>().ToArrayAsync());Assert.Single(await final.ConstructionAuditEntries.ToArrayAsync());
 }
 [Fact]
 public async Task Denied_history_and_dirty_unit_of_work_are_refused()
 {
  using var f=new ConstructionTestFixture();var s=await Seed(f.Db);
  Assert.Null(await Service(f.Db,allowed:false).HistoryAsync(s.Project));Assert.Null(await Service(f.Db,company:2).HistoryAsync(s.Project));
  var line=await f.Db.Set<ConstructionBudgetLine>().FirstAsync();line.ChangeReason="Pending unrelated edit";
  Assert.Equal("CleanUnitOfWorkRequired",(await Service(f.Db).PreviewAsync(s.Project,s.Estimates)).Code);
 }
}
