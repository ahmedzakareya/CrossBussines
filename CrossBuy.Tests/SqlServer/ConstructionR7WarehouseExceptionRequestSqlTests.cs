using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7WarehouseExceptionRequestSqlTests(UatSqlProbeFixture sql)
{
 private const string File="construction_r7_warehouse_exception_requests.sql";
 private static string Script()=>ConstructionR7BudgetTransferDecisionSqlTests.Script(File);
 private static Task Execute(string c,string text)=>ConstructionR7BudgetTransferDecisionSqlTests.Execute(c,text);
 private async Task<ConstructionR7WarehouseExceptionRequestTests.Setup> Prepare(UatSqlProbeFixture.ProbeDatabase probe)
 {
  ConstructionR7WarehouseExceptionRequestTests.Setup ids;
  using(var db=sql.ContextFor(probe)){ids=await ConstructionR7WarehouseExceptionRequestTests.Seed(db);}
  await Execute(probe.ConnectionString,"IF EXISTS(SELECT 1 FROM dbo.ConstructionWarehouseExceptionRequests) THROW 52010,'Owned fixture must be empty',1; DROP TABLE dbo.ConstructionWarehouseExceptionRequests;");
  await Execute(probe.ConnectionString,Script());await Execute(probe.ConnectionString,Script());return ids;
 }
 private async Task<ConstructionWarehouseExceptionQuote> Quote(UatSqlProbeFixture.ProbeDatabase probe,ConstructionR7WarehouseExceptionRequestTests.Setup ids)
 {
  using var db=sql.ContextFor(probe);var q=await ConstructionR7WarehouseExceptionRequestTests.Service(db,ids).PreviewAsync(ids.Order,ids.DestinationSite);Assert.True(q.Ok,q.Code);return q;
 }
 [SkippableFact]
 public async Task Native_parallel_replay_is_once_immutable_and_rerunnable_without_operational_grant()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHREQONCE");
  try{
   var ids=await Prepare(probe);var q=await Quote(probe,ids);var key=Guid.NewGuid();
   async Task<ConstructionWarehouseExceptionRequestResult> Send(){using var db=sql.ContextFor(probe);return await ConstructionR7WarehouseExceptionRequestTests.Service(db,ids).RequestAsync(ids.Order,ids.DestinationSite,q.Version!,key,"Native request");}
   var results=await Task.WhenAll(Send(),Send());Assert.All(results,x=>{Assert.True(x.Ok,x.Code);Assert.False(x.OperationalGrant);});Assert.Equal(results[0].Id,results[1].Id);
   await Execute(probe.ConnectionString,Script());
   foreach(var text in new[]{"UPDATE dbo.ConstructionWarehouseExceptionRequests SET Reason=N'Overwrite';","DELETE dbo.ConstructionWarehouseExceptionRequests;"})Assert.Equal(52011,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,text))).Number);
   using var verify=sql.ContextFor(probe);Assert.Single(await verify.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().ToArrayAsync());Assert.Equal("Draft",(await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());
   Assert.Single(await verify.ConstructionAuditEntries.Where(x=>x.EntityType==nameof(ConstructionWarehouseExceptionRequest)).ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_audit_failure_rolls_back_receipt_and_permits_safe_retry()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHREQAUDIT");
  try{
   var ids=await Prepare(probe);var q=await Quote(probe,ids);var key=Guid.NewGuid();
   await Execute(probe.ConnectionString,"CREATE TRIGGER dbo.R7RejectWarehouseRequestAudit ON dbo.ConstructionAuditEntries AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE EntityType=N'ConstructionWarehouseExceptionRequest') THROW 52019,'Deliberate audit failure',1; END;");
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseExceptionRequestTests.Service(db,ids).RequestAsync(ids.Order,ids.DestinationSite,q.Version!,key,"Atomic request");Assert.False(result.Ok);}
   using(var verify=sql.ContextFor(probe)){Assert.Empty(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());Assert.Empty(await verify.ConstructionAuditEntries.Where(x=>x.EntityType==nameof(ConstructionWarehouseExceptionRequest)).ToArrayAsync());}
   await Execute(probe.ConnectionString,"DROP TRIGGER dbo.R7RejectWarehouseRequestAudit;");
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseExceptionRequestTests.Service(db,ids).RequestAsync(ids.Order,ids.DestinationSite,q.Version!,key,"Atomic request");Assert.True(result.Ok,result.Code);}
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_direct_insert_rejects_foreign_scope_receipt_mismatch_and_disabled_policy()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHREQGUARD");
  try{
   var ids=await Prepare(probe);var q=await Quote(probe,ids);
   using(var db=sql.ContextFor(probe)){var saved=await ConstructionR7WarehouseExceptionRequestTests.Service(db,ids).RequestAsync(ids.Order,ids.DestinationSite,q.Version!,Guid.NewGuid(),"Guarded original");Assert.True(saved.Ok,saved.Code);}
   var clone="INSERT dbo.ConstructionWarehouseExceptionRequests(CompanyID,ProjectId,PurchaseOrderId,DraftId,BudgetVersionId,SourceWarehouseId,DestinationSiteId,RequestId,RequestHash,SourceVersion,SourceFactsVersion,CreationVersion,MappingVersion,PolicyToken,Reason,CreatedBy,CreatedAt) SELECT CompanyID,ProjectId,PurchaseOrderId,DraftId,BudgetVersionId,SourceWarehouseId,DestinationSiteId,NEWID(),RequestHash,REPLICATE(N'F',64),SourceFactsVersion,CreationVersion,MappingVersion,PolicyToken,Reason,CreatedBy,CreatedAt FROM dbo.ConstructionWarehouseExceptionRequests;";
   async Task Reject(int code,string text)=>Assert.Equal(code,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,text))).Number);
   await Reject(547,clone.Replace("SELECT CompanyID,ProjectId,","SELECT CompanyID+9999,ProjectId,"));
   await Reject(52012,clone.Replace("64),SourceFactsVersion,CreationVersion,MappingVersion","64),SourceFactsVersion,REPLICATE(N'0',64),MappingVersion"));
   await Execute(probe.ConnectionString,"UPDATE dbo.ConstructionCompanyPolicies SET Mode=N'Restricted' WHERE PolicyKey=N'CrossSiteWarehouse';");await Reject(52012,clone);
   await Execute(probe.ConnectionString,"ALTER TABLE dbo.ConstructionWarehouseExceptionRequests NOCHECK CONSTRAINT CK_ConstructionWarehouseExceptionRequests_Values;");await Reject(52010,Script());
   using var verify=sql.ContextFor(probe);Assert.Single(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
