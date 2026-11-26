using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests.SqlServer;
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7WarehouseClassificationSqlTests(UatSqlProbeFixture sql)
{
 [SkippableFact]
 public async Task Native_read_is_repeatable_company_scoped_and_leaves_tokens_and_business_rows_unchanged()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHREAD");
  try{
   ConstructionR7WarehouseClassificationTests.Ids ids;byte[][] before;
   using(var db=sql.ContextFor(probe)){ids=await ConstructionR7WarehouseClassificationTests.Seed(db);before=await db.Set<ConstructionSite>().AsNoTracking().OrderBy(x=>x.ID).Select(x=>x.RowVersion).ToArrayAsync();}
   ConstructionWarehouseClassification first;
   using(var db=sql.ContextFor(probe)){var service=ConstructionR7WarehouseClassificationTests.Service(db,new(ids.OwnWarehouse,ids.OtherWarehouse));first=await service.InspectAsync(ids.Project,ids.OtherWarehouse,ids.OwnSite);Assert.True(first.Ok,first.Code);Assert.Equal(ConstructionWarehouseRelation.OtherProject,first.Relation);Assert.False(first.OperationalGrant);}
   using(var db=sql.ContextFor(probe)){var repeat=await ConstructionR7WarehouseClassificationTests.Service(db,new(ids.OwnWarehouse,ids.OtherWarehouse)).InspectAsync(ids.Project,ids.OtherWarehouse,ids.OwnSite);Assert.Equal(first.Version,repeat.Version);}
   using var verify=sql.ContextFor(probe);var sites=await verify.Set<ConstructionSite>().AsNoTracking().OrderBy(x=>x.ID).ToArrayAsync();Assert.Equal(2,sites.Length);for(int i=0;i<sites.Length;i++)Assert.Equal(before[i],sites[i].RowVersion);
   Assert.Equal(2,await verify.Projects.CountAsync());Assert.Equal(2,await verify.Warehouses.CountAsync());Assert.Single(await verify.Set<ConstructionCompanyPolicy>().ToArrayAsync());Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());Assert.Empty(await verify.JournalEntries.ToArrayAsync());Assert.Empty(await verify.StockMovements.ToArrayAsync());Assert.Empty(await verify.PurchaseOrders.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_mapping_rowversion_and_explicit_policy_changes_are_part_of_fresh_witnesses()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHVER");
  try{
   ConstructionR7WarehouseClassificationTests.Ids ids;ConstructionWarehouseClassification first;byte[] token;
   using(var db=sql.ContextFor(probe)){ids=await ConstructionR7WarehouseClassificationTests.Seed(db);token=await db.Set<ConstructionSite>().AsNoTracking().Where(x=>x.ID==ids.OtherSite).Select(x=>x.RowVersion).SingleAsync();first=await ConstructionR7WarehouseClassificationTests.Service(db,new(ids.OwnWarehouse,ids.OtherWarehouse)).InspectAsync(ids.Project,ids.OtherWarehouse,ids.OwnSite);Assert.True(first.Ok,first.Code);}
   using(var change=sql.ContextFor(probe)){await change.Set<ConstructionSite>().Where(x=>x.ID==ids.OtherSite).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.ProjectId,ids.Project));}
   ConstructionWarehouseClassification revised;
   using(var verify=sql.ContextFor(probe)){Assert.NotEqual(token,await verify.Set<ConstructionSite>().AsNoTracking().Where(x=>x.ID==ids.OtherSite).Select(x=>x.RowVersion).SingleAsync());revised=await ConstructionR7WarehouseClassificationTests.Service(verify,new(ids.OwnWarehouse,ids.OtherWarehouse)).InspectAsync(ids.Project,ids.OtherWarehouse,ids.OwnSite);Assert.True(revised.Ok,revised.Code);Assert.Equal(ConstructionWarehouseRelation.SameProjectOtherSite,revised.Relation);Assert.NotEqual(first.Version,revised.Version);}
   using(var change=sql.ContextFor(probe)){await change.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"ApprovedException"));}
   using(var verify=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseClassificationTests.Service(verify,new(ids.OwnWarehouse,ids.OtherWarehouse)).InspectAsync(ids.Project,ids.OtherWarehouse,ids.OwnSite);Assert.True(result.Ok,result.Code);Assert.NotEqual(revised.Version,result.Version);Assert.False(result.OperationalGrant);}
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_exception_configuration_never_bypasses_existing_destination_inventory_grants()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHACCESS");
  try{
   ConstructionR7WarehouseClassificationTests.Ids ids;using(var db=sql.ContextFor(probe)){ids=await ConstructionR7WarehouseClassificationTests.Seed(db);await db.Set<ConstructionCompanyPolicy>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Mode,"ApprovedException"));}
   using var verify=sql.ContextFor(probe);var grants=new ConstructionR7WarehouseClassificationTests.WarehouseAccess(ids.OtherWarehouse);var result=await ConstructionR7WarehouseClassificationTests.Service(verify,grants).InspectAsync(ids.Project,ids.OtherWarehouse,ids.OwnSite);
   Assert.False(result.Ok);Assert.Equal("WarehouseAccessDenied",result.Code);Assert.False(result.OperationalGrant);Assert.Null(result.Version);Assert.Contains(ids.OtherWarehouse,grants.Checked);Assert.Contains(ids.OwnWarehouse,grants.Checked);Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
 [SkippableFact]
 public async Task Native_project_lock_timeout_refuses_a_snapshot_without_mutating_caller_history()
 {
  Skip.If(!sql.Available,sql.SkipReason);var probe=await sql.CreateProbeDatabaseAsync("R7WHLOCK");
  try{
   ConstructionR7WarehouseClassificationTests.Ids ids;using(var db=sql.ContextFor(probe)){ids=await ConstructionR7WarehouseClassificationTests.Seed(db);}
   await using var hold=new SqlConnection(probe.ConnectionString);await hold.OpenAsync();await using var tx=await hold.BeginTransactionAsync();await using var command=hold.CreateCommand();command.Transaction=(SqlTransaction)tx;
   command.CommandText="DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@scope,@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=1000; SELECT @r;";command.Parameters.AddWithValue("@scope",$"R7:Commitments:1:{ids.Project}");Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync())>=0);
   using(var db=sql.ContextFor(probe)){var result=await ConstructionR7WarehouseClassificationTests.Service(db,new(ids.OwnWarehouse,ids.OtherWarehouse)).InspectAsync(ids.Project,ids.OtherWarehouse,ids.OwnSite);Assert.False(result.Ok);Assert.Equal("ConcurrencyConflict",result.Code);Assert.Null(result.Version);Assert.False(result.OperationalGrant);}
   await tx.RollbackAsync();using var verify=sql.ContextFor(probe);Assert.Equal(2,await verify.Set<ConstructionSite>().CountAsync());Assert.Empty(await verify.ConstructionAuditEntries.ToArrayAsync());
  }finally{await sql.DropProbeDatabaseAsync(probe);}
 }
}
