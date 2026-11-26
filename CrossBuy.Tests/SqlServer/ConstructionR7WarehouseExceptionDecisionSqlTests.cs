using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7WarehouseExceptionDecisionSqlTests(UatSqlProbeFixture sql)
{
 private static Task Execute(string c,string text)=>ConstructionR7BudgetTransferDecisionSqlTests.Execute(c,text);
 private static string Script(string file)=>ConstructionR7BudgetTransferDecisionSqlTests.Script(file);
 private const string RequestFile="construction_r7_warehouse_exception_requests.sql",DecisionFile="construction_r7_warehouse_exception_decisions.sql";
 private async Task<(ConstructionR7WarehouseExceptionRequestTests.Setup Scope,int Request)> Prepare(UatSqlProbeFixture.ProbeDatabase probe)
 {
  ConstructionR7WarehouseExceptionRequestTests.Setup ids;
  using(var db=sql.ContextFor(probe)){ids=await ConstructionR7WarehouseExceptionRequestTests.Seed(db);}
  await Execute(probe.ConnectionString,"IF EXISTS(SELECT 1 FROM dbo.ConstructionWarehouseExceptionDecisions) OR EXISTS(SELECT 1 FROM dbo.ConstructionWarehouseExceptionRequests) THROW 52020,'Owned fixture must be empty',1; DROP TABLE dbo.ConstructionWarehouseExceptionDecisions; DROP TABLE dbo.ConstructionWarehouseExceptionRequests;");
  await Execute(probe.ConnectionString,Script(RequestFile));await Execute(probe.ConnectionString,Script(DecisionFile));await Execute(probe.ConnectionString,Script(DecisionFile));
  using var db2=sql.ContextFor(probe);var request=ConstructionR7WarehouseExceptionRequestTests.Service(db2,ids);var q=await request.PreviewAsync(ids.Order,ids.DestinationSite);Assert.True(q.Ok,q.Code);var saved=await request.RequestAsync(ids.Order,ids.DestinationSite,q.Version!,Guid.NewGuid(),"Native independent review");Assert.True(saved.Ok,saved.Code);return(ids,saved.Id!.Value);
 }
 [SkippableFact]
 public async Task Native_independent_parallel_consent_is_once_immutable_and_never_executes()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHDECONCE");
  try{
   var (s,id)=await Prepare(probe);var key=Guid.NewGuid();
   using(var db=sql.ContextFor(probe)){Assert.Equal("SeparateApproverRequired",(await ConstructionR7WarehouseExceptionDecisionTests.Service(db,s,actor:7).DecideAsync(s.Project,id,"Approved",Guid.NewGuid(),"Own request")).Code);}
   async Task<ConstructionWarehouseExceptionRequestResult> Send(){using var db=sql.ContextFor(probe);return await ConstructionR7WarehouseExceptionDecisionTests.Service(db,s).DecideAsync(s.Project,id,"Approved",key,"Independent native consent");}
   var results=await Task.WhenAll(Send(),Send());Assert.All(results,x=>{Assert.True(x.Ok,x.Code);Assert.False(x.OperationalGrant);});Assert.Equal(results[0].Id,results[1].Id);
   await Execute(probe.ConnectionString,Script(DecisionFile));
   foreach(var text in new[]{"UPDATE dbo.ConstructionWarehouseExceptionDecisions SET Outcome=N'Rejected';","DELETE dbo.ConstructionWarehouseExceptionDecisions;"})Assert.Equal(52021,(await Assert.ThrowsAsync<SqlException>(()=>Execute(probe.ConnectionString,text))).Number);
   using var verify=sql.ContextFor(probe);var row=await verify.Set<ConstructionWarehouseExceptionDecision>().AsNoTracking().SingleAsync();Assert.Equal(8,row.CreatedBy);Assert.Equal("Approved",row.Outcome);Assert.Single(await verify.ConstructionAuditEntries.Where(x=>x.EntityType==nameof(ConstructionWarehouseExceptionDecision)).ToArrayAsync());Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableTheory][InlineData("mapping")][InlineData("policy")][InlineData("source")]
 public async Task Native_fresh_approval_refuses_stale_facts_but_independent_rejection_can_close(string mode)
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHDECSTALE");
  try{
   var(s,id)=await Prepare(probe);
   using(var change=sql.ContextFor(probe)){
    if(mode=="mapping")await change.Set<ConstructionSite>().Where(x=>x.ID==s.OriginSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.IsActive,false));
    if(mode=="policy")await change.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"Restricted"));
    if(mode=="source")await change.PurchaseOrders.ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Notes,"Changed persisted source"));
   }
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseExceptionDecisionTests.Service(db,s).DecideAsync(s.Project,id,"Approved",Guid.NewGuid(),"Stale consent");Assert.False(result.Ok);}
   using(var verify=sql.ContextFor(probe)){Assert.Empty(await verify.Set<ConstructionWarehouseExceptionDecision>().ToArrayAsync());}
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseExceptionDecisionTests.Service(db,s).DecideAsync(s.Project,id,"Rejected",Guid.NewGuid(),"Close stale intent");Assert.True(result.Ok,result.Code);Assert.False(result.OperationalGrant);}
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_competing_outcomes_have_one_winner_and_audit_failure_rolls_back()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHDECAUDIT");
  try{
   var(s,id)=await Prepare(probe);
   await Execute(probe.ConnectionString,"CREATE TRIGGER dbo.R7RejectWarehouseDecisionAudit ON dbo.ConstructionAuditEntries AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE EntityType=N'ConstructionWarehouseExceptionDecision') THROW 52029,'Deliberate decision audit failure',1; END;");
   var key=Guid.NewGuid();using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseExceptionDecisionTests.Service(db,s).DecideAsync(s.Project,id,"Approved",key,"Atomic native decision");Assert.False(result.Ok);}
   using(var verify=sql.ContextFor(probe)){Assert.Empty(await verify.Set<ConstructionWarehouseExceptionDecision>().ToArrayAsync());Assert.Empty(await verify.ConstructionAuditEntries.Where(x=>x.EntityType==nameof(ConstructionWarehouseExceptionDecision)).ToArrayAsync());}
   await Execute(probe.ConnectionString,"DROP TRIGGER dbo.R7RejectWarehouseDecisionAudit;");
   async Task<ConstructionWarehouseExceptionRequestResult> Send(string outcome){using var db=sql.ContextFor(probe);return await ConstructionR7WarehouseExceptionDecisionTests.Service(db,s).DecideAsync(s.Project,id,outcome,outcome=="Approved"?key:Guid.NewGuid(),"Atomic native decision");}
   var outcomes=await Task.WhenAll(Send("Approved"),Send("Rejected"));Assert.Single(outcomes,x=>x.Ok);Assert.Single(outcomes,x=>!x.Ok);
   using var final=sql.ContextFor(probe);Assert.Single(await final.Set<ConstructionWarehouseExceptionDecision>().ToArrayAsync());Assert.Single(await final.ConstructionAuditEntries.Where(x=>x.EntityType==nameof(ConstructionWarehouseExceptionDecision)).ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
