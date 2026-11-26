using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7WarehouseExceptionApplicationSqlTests(UatSqlProbeFixture sql)
{
 [SkippableFact]
 public async Task Native_mapping_added_after_plain_submission_cannot_bypass_exception_path()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHKNOWNGUARD");
  try{
   using var db=sql.ContextFor(probe);var s=await ConstructionR7PurchaseApprovalTests.Arrange(db);var actor=new ConstructionR7PurchaseApprovalTests.ActorAccessor();var service=ConstructionR7PurchaseApprovalTests.Service(db,actor);var pending=await ConstructionR7PurchaseApprovalTests.Request(service,s,Guid.NewGuid());Assert.True(pending.Ok,pending.Code);
   var other=new CrossBuy.Models.Context.Accounting.Project{CompanyID=1,Code="KNOWN-GUARD",Name="Mapped elsewhere"};db.Add(other);await db.SaveChangesAsync();db.Add(new ConstructionSite{CompanyID=1,ProjectId=other.ID,WarehouseId=s.Seed.Warehouse,Code="KNOWN-SITE",Name="Other source site"});await db.SaveChangesAsync();actor.Current=ConstructionR7BudgetExceptionTests.Approver;
   var result=await service.ApproveAsync(pending.ApprovalId!.Value,Guid.NewGuid(),"Refuse unbound mapped source");Assert.False(result.Ok);Assert.Equal("WarehouseExceptionBindingRequired",result.Code);
   using var verify=sql.ContextFor(probe);Assert.Empty(await verify.Set<ConstructionWarehouseExceptionRequest>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Equal("Pending",(await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 private static string Script()=>ConstructionR7BudgetTransferDecisionSqlTests.Script("construction_r7_warehouse_exception_application.sql");
 private static Task Execute(string c,string text)=>ConstructionR7BudgetTransferDecisionSqlTests.Execute(c,text);
 private async Task<ConstructionR7WarehouseExceptionConsumptionTests.Setup> Prepare(UatSqlProbeFixture.ProbeDatabase probe,bool submit=true)
 {
  await Execute(probe.ConnectionString,"IF EXISTS(SELECT 1 FROM dbo.ConstructionWarehouseExceptionApplications) OR EXISTS(SELECT 1 FROM dbo.ConstructionWarehouseExceptionBindings) THROW 52030,'Owned fixture must be empty',1; DROP TABLE dbo.ConstructionWarehouseExceptionApplications; DROP TABLE dbo.ConstructionWarehouseExceptionBindings;");
  await Execute(probe.ConnectionString,"DROP TABLE dbo.ConstructionPurchaseAdmissions; DROP TABLE dbo.ConstructionPurchaseApprovalRequests;");
  await Execute(probe.ConnectionString,ConstructionR7BudgetTransferDecisionSqlTests.Script("construction_r7_purchase_approvals.sql"));
  await Execute(probe.ConnectionString,Script());await Execute(probe.ConnectionString,Script());using var db=sql.ContextFor(probe);return await ConstructionR7WarehouseExceptionConsumptionTests.Seed(db,submit);
 }
 private async Task<CrossBuy.BL.Construction.ConstructionPurchaseApprovalResult> Submit(UatSqlProbeFixture.ProbeDatabase probe,ConstructionR7WarehouseExceptionConsumptionTests.Setup s,Guid key,string reason="Native binding")
 {
  using var db=sql.ContextFor(probe);var allocation=await db.Set<ConstructionOrderAllocation>().AsNoTracking().SingleAsync();
  var actor=new ConstructionR7PurchaseApprovalTests.ActorAccessor();
  var service=ConstructionR7PurchaseApprovalTests.Service(db,actor,warehouses:new ConstructionR7WarehouseClassificationTests.WarehouseAccess(new[]{s.Scope.SourceWarehouse,s.Scope.DestinationWarehouse}),projectAccess:new ConstructionR7ActualCostTests.Access());
  return await service.RequestWithWarehouseExceptionAsync(s.Scope.Project,s.Scope.Order,allocation.ID,allocation.SourceVersion,null,s.Decision,key,reason);
 }
 [SkippableFact]
 public async Task Native_parallel_approval_is_one_immutable_application_and_rerunnable_schema()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHAPPLY");
  try{
   var s=await Prepare(probe);var key=Guid.NewGuid();async Task<CrossBuy.BL.Construction.ConstructionPurchaseApprovalResult> Send(){using var db=sql.ContextFor(probe);return await ConstructionR7WarehouseExceptionApplicationTests.Service(db,s).ApproveAsync(s.Approval,key,"Native atomic purchase");}
   var results=await Task.WhenAll(Send(),Send());Assert.All(results,x=>Assert.True(x.Ok,x.Code));await Execute(probe.ConnectionString,Script());
   foreach(var table in new[]{"ConstructionWarehouseExceptionBindings","ConstructionWarehouseExceptionApplications"})foreach(var change in new[]{$"UPDATE dbo.{table} SET CreatedBy=99;",$"DELETE dbo.{table};"})Assert.Equal(52031,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,change))).Number);
   using var verify=sql.ContextFor(probe);Assert.Single(await verify.Set<ConstructionWarehouseExceptionApplication>().ToArrayAsync());Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Single(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Equal("Approved",(await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);Assert.Empty(await verify.StockMovements.ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_binding_failure_rolls_back_purchase_request_audit_and_outbox_then_same_key_retries()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHBINDROLL");
  try{
   var s=await Prepare(probe,false);int audits,events;
   using(var db=sql.ContextFor(probe)){audits=await db.ConstructionAuditEntries.CountAsync();events=await db.BusinessEvents.CountAsync();}
   await Execute(probe.ConnectionString,"CREATE TRIGGER dbo.R7RejectWarehouseBindingAudit ON dbo.ConstructionAuditEntries AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE FieldName=N'BindWarehouseException') THROW 52039,'Deliberate binding audit failure',1; END;");
   var key=Guid.NewGuid();Assert.NotNull(await Record.ExceptionAsync(()=>Submit(probe,s,key)));
   using(var verify=sql.ContextFor(probe)){Assert.Empty(await verify.InventoryApprovals.ToArrayAsync());Assert.Empty(await verify.Set<ConstructionPurchaseApprovalRequest>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionWarehouseExceptionBinding>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionWarehouseExceptionApplication>().ToArrayAsync());Assert.Equal(audits,await verify.ConstructionAuditEntries.CountAsync());Assert.Equal(events,await verify.BusinessEvents.CountAsync());Assert.Equal("Draft",(await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);}
   await Execute(probe.ConnectionString,"DROP TRIGGER dbo.R7RejectWarehouseBindingAudit;");var retry=await Submit(probe,s,key);Assert.True(retry.Ok,retry.Code);
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_competing_binding_keys_create_one_request_and_exact_key_replay_is_not_a_new_grant()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHBINDRACE");
  try{
   var s=await Prepare(probe,false);var keys=new[]{Guid.NewGuid(),Guid.NewGuid()};var results=await Task.WhenAll(Submit(probe,s,keys[0]),Submit(probe,s,keys[1]));Assert.Single(results,x=>x.Ok);Assert.Single(results,x=>!x.Ok);
   var winner=results[0].Ok?0:1;var replay=await Submit(probe,s,keys[winner]);Assert.True(replay.Ok,replay.Code);Assert.Equal(results[winner].ApprovalId,replay.ApprovalId);Assert.Equal("IdempotencyConflict",(await Submit(probe,s,keys[winner],"Different command")).Code);
   using var verify=sql.ContextFor(probe);Assert.Single(await verify.InventoryApprovals.ToArrayAsync());Assert.Single(await verify.Set<ConstructionPurchaseApprovalRequest>().ToArrayAsync());Assert.Single(await verify.Set<ConstructionWarehouseExceptionBinding>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionWarehouseExceptionApplication>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_different_approval_keys_cannot_consume_one_binding_twice()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHAPPLYRACE");
  try{
   var s=await Prepare(probe);async Task<CrossBuy.BL.Construction.ConstructionPurchaseApprovalResult> Send(){using var db=sql.ContextFor(probe);return await ConstructionR7WarehouseExceptionApplicationTests.Service(db,s).ApproveAsync(s.Approval,Guid.NewGuid(),"Competing admission");}
   var results=await Task.WhenAll(Send(),Send());Assert.Single(results,x=>x.Ok);Assert.Single(results,x=>!x.Ok);
   using var verify=sql.ContextFor(probe);Assert.Single(await verify.Set<ConstructionWarehouseExceptionApplication>().ToArrayAsync());Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Single(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Equal(2,await verify.BusinessEvents.CountAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_direct_tampering_and_disabled_constraint_refuse_without_repair()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHAPPLYGUARD");
  try{
   var s=await Prepare(probe);
   var clone="INSERT dbo.ConstructionWarehouseExceptionBindings(CompanyID,ProjectId,PurchaseOrderId,ApprovalRequestId,DecisionId,SourceFactsVersion,RequestId,RequestHash,CreatedBy,CreatedAt) SELECT CompanyID+9999,ProjectId,PurchaseOrderId,ApprovalRequestId,DecisionId,SourceFactsVersion,NEWID(),RequestHash,CreatedBy,CreatedAt FROM dbo.ConstructionWarehouseExceptionBindings;";
   // Change the unique approval key as well so the scoped FK is the rejecting guard.
   clone=clone.Replace("SELECT CompanyID+9999,ProjectId,PurchaseOrderId,ApprovalRequestId,","SELECT CompanyID+9999,ProjectId,PurchaseOrderId,ApprovalRequestId+9999,");
   Assert.Equal(547,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,clone))).Number);
   var forged="INSERT dbo.ConstructionWarehouseExceptionApplications(CompanyID,ProjectId,PurchaseOrderId,BindingId,AdmissionId,RequestId,ReviewVersion,CreatedBy,CreatedAt) SELECT CompanyID,ProjectId,PurchaseOrderId,ID,2147483647,NEWID(),SourceFactsVersion,CreatedBy,CreatedAt FROM dbo.ConstructionWarehouseExceptionBindings;";
   Assert.Equal(547,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,forged))).Number);
   await Execute(probe.ConnectionString,"ALTER TABLE dbo.ConstructionWarehouseExceptionApplications NOCHECK CONSTRAINT CK_ConstructionWarehouseExceptionApplications_Values;");Assert.Equal(52030,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,Script()))).Number);
   using var verify=sql.ContextFor(probe);Assert.Single(await verify.Set<ConstructionWarehouseExceptionBinding>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionWarehouseExceptionApplication>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableTheory][InlineData("audit")][InlineData("event")]
 public async Task Native_late_failure_rolls_back_application_admission_budget_and_source(string mode)
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHAPPLYROLL");
  try{
   var s=await Prepare(probe);int audits,events;using(var db=sql.ContextFor(probe)){audits=await db.ConstructionAuditEntries.CountAsync();events=await db.BusinessEvents.CountAsync();}
   var table=mode=="audit"?"ConstructionAuditEntries":"BusinessEvents";
   var predicate=mode=="audit"?"IF EXISTS(SELECT 1 FROM inserted WHERE FieldName=N'ConsumeWarehouseException')":"";
   await Execute(probe.ConnectionString,$"CREATE TRIGGER dbo.R7RejectWarehouseApplication ON dbo.{table} AFTER INSERT AS BEGIN {predicate} THROW 52039,'Deliberate late application failure',1; END;");
   var key=Guid.NewGuid();using(var db=sql.ContextFor(probe)){var error=await Record.ExceptionAsync(()=>ConstructionR7WarehouseExceptionApplicationTests.Service(db,s).ApproveAsync(s.Approval,key,"Atomic retry"));Assert.NotNull(error);}
   using(var verify=sql.ContextFor(probe)){Assert.Empty(await verify.Set<ConstructionWarehouseExceptionApplication>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());Assert.Equal("Draft",(await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);Assert.Equal("Pending",(await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);Assert.Equal(audits,await verify.ConstructionAuditEntries.CountAsync());Assert.Equal(events,await verify.BusinessEvents.CountAsync());}
   await Execute(probe.ConnectionString,"DROP TRIGGER dbo.R7RejectWarehouseApplication;");using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseExceptionApplicationTests.Service(db,s).ApproveAsync(s.Approval,key,"Atomic retry");Assert.True(result.Ok,result.Code);}
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
