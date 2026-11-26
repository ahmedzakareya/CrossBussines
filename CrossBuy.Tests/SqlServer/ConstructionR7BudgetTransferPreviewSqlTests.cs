using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7BudgetTransferPreviewSqlTests(UatSqlProbeFixture sql)
{
 [SkippableFact]
 public async Task Native_preview_reads_exact_scoped_budget_and_changed_policy_without_writes()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7TRFPREV");
  try{
   (int Project,int From,int To) ids;using(var seed=sql.ContextFor(probe,1))ids=await ConstructionR7BudgetTransferPreviewTests.Seed(seed);
   using(var db=sql.ContextFor(probe,1)){var service=ConstructionR7BudgetTransferPreviewTests.Service(db);var result=await service.PreviewAsync(ids.Project,ids.From,ids.To,1.234m);Assert.True(result.Ok,result.Code);Assert.Equal(998.766m,result.FromAfter);Assert.Equal(51.234m,result.ToAfter);Assert.Equal(result.Witness,(await service.PreviewAsync(ids.Project,ids.From,ids.To,1.234m)).Witness);}
   using(var alter=sql.ContextFor(probe,1))await alter.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Mode,"Disabled"));
   using(var db=sql.ContextFor(probe,1)){var result=await ConstructionR7BudgetTransferPreviewTests.Service(db).PreviewAsync(ids.Project,ids.From,ids.To,1.234m);Assert.False(result.Ok);Assert.Equal("TransferDisabled",result.Code);Assert.Null(result.FromAfter);}
   using var verify=sql.ContextFor(probe,1);Assert.Single(await verify.Set<ConstructionBudgetVersion>().ToArrayAsync());Assert.Equal(1050m,await verify.Set<ConstructionBudgetLine>().SumAsync(x=>x.Amount));Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
