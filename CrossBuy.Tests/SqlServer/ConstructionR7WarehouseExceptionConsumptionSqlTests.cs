using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7WarehouseExceptionConsumptionSqlTests(UatSqlProbeFixture sql)
{
 [SkippableFact]
 public async Task Native_exact_pending_review_is_repeatable_and_writes_no_admission_reservation_audit_or_events()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHUSEREAD");
  try{
   ConstructionR7WarehouseExceptionConsumptionTests.Setup s;int audits,events;
   using(var db=sql.ContextFor(probe)){s=await ConstructionR7WarehouseExceptionConsumptionTests.Seed(db);audits=await db.ConstructionAuditEntries.CountAsync();events=await db.BusinessEvents.CountAsync();}
   string? witness;
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseExceptionConsumptionTests.Service(db,s).PreviewAsync(s.Scope.Project,s.Scope.Order,s.Decision,s.ApprovalRequest);Assert.True(result.Ok,result.Code);Assert.False(result.OperationalGrant);witness=result.Version;}
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseExceptionConsumptionTests.Service(db,s).PreviewAsync(s.Scope.Project,s.Scope.Order,s.Decision,s.ApprovalRequest);Assert.True(result.Ok,result.Code);Assert.Equal(witness,result.Version);}
   using var verify=sql.ContextFor(probe);Assert.Equal(audits,await verify.ConstructionAuditEntries.CountAsync());Assert.Equal(events,await verify.BusinessEvents.CountAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());Assert.Equal("Pending",(await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableTheory][InlineData("mapping")][InlineData("policy")][InlineData("source")]
 public async Task Native_review_rechecks_live_facts_after_consent_and_purchase_submission(string mode)
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHUSESTALE");
  try{
   ConstructionR7WarehouseExceptionConsumptionTests.Setup s;int audits;
   using(var db=sql.ContextFor(probe)){s=await ConstructionR7WarehouseExceptionConsumptionTests.Seed(db);audits=await db.ConstructionAuditEntries.CountAsync();
    if(mode=="mapping")await db.Set<ConstructionSite>().Where(x=>x.ID==s.Scope.OriginSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
    if(mode=="policy")await db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Restricted"));
    if(mode=="source")await db.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Notes,"Changed persisted source"));
   }
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseExceptionConsumptionTests.Service(db,s).PreviewAsync(s.Scope.Project,s.Scope.Order,s.Decision,s.ApprovalRequest);Assert.False(result.Ok);Assert.False(result.OperationalGrant);}
   using var verify=sql.ContextFor(probe);Assert.Equal(audits,await verify.ConstructionAuditEntries.CountAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
