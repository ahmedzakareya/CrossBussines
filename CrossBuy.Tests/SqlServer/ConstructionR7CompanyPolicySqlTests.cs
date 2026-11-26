using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7CompanyPolicySqlTests(UatSqlProbeFixture sql)
{
 [SkippableFact]
 public async Task Native_choices_have_no_seed_are_rerunnable_and_reject_stale_updates()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7COMPOL");
  try{
   using var db=sql.ContextFor(probe,1);await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionCompanyPolicies;");
   DirectoryInfo? root=new(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"CrossBuy.sln")))root=root.Parent;Assert.NotNull(root);
   var script=await File.ReadAllTextAsync(Path.Combine(root!.FullName,"CrossBuy/deploy/sql/construction_r7_company_policies.sql"));for(var i=0;i<2;i++)await db.Database.ExecuteSqlRawAsync(script);
   var actors=new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=ConstructionR7OrderAllocationTests.Actor()};var service=new ConstructionCompanyPolicyWorkspace(db,actors,new ConstructionR7CompanyPolicyTests.CompanyAdminAccess(),new ConstructionAuditService(db));Assert.Empty((await service.ReadAsync())!.Policies);
   foreach(var key in ConstructionCompanyPolicies.Choices.Keys)Assert.Null(await service.SaveAsync(key,ConstructionCompanyPolicies.Choices[key][0],null,"Native explicit choice"));
   var saved=(await service.ReadAsync())!.Policies;Assert.Equal(3,saved.Count);var row=saved.First();var token=Convert.ToBase64String(row.ConcurrencyToken);
   using var rivalDb=sql.ContextFor(probe,1);var rival=new ConstructionCompanyPolicyWorkspace(rivalDb,actors,new ConstructionR7CompanyPolicyTests.CompanyAdminAccess(),new ConstructionAuditService(rivalDb));
   Assert.Null(await rival.SaveAsync(row.PolicyKey,row.Mode,token,"Newer choice"));Assert.Equal("ConcurrencyConflict",await service.SaveAsync(row.PolicyKey,row.Mode,token,"Stale choice"));
   using var verify=sql.ContextFor(probe,1);Assert.Equal(3,await verify.Set<ConstructionCompanyPolicy>().CountAsync());Assert.Empty(await verify.JournalEntries.ToListAsync());Assert.Empty(await verify.StockMovements.ToListAsync());
   await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(()=>verify.Database.ExecuteSqlRawAsync("UPDATE dbo.ConstructionCompanyPolicies SET Mode=N'UnconfiguredDefault'"));
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
