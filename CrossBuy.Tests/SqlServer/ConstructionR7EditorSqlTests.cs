using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

// Native caller/transaction proof, not deployment proof. Authored migration evidence is separate.
// Every test owns a fresh probe; cleanup confirms removal. No application database is used.
[Collection(SqlServerCollection.Name)]
public sealed class ConstructionR7EditorSqlTests(SqlServerFixture sql)
{
    private static BoqService Service(CrossDbContext db) => new(db,new ConstructionAuditService(db));
    private static List<BoqRowInput> Row(int id,decimal cost) => new()
    { new() { Id=id, Code="A", Description="Work "+cost, Quantity=10, UnitPrice=2, MaterialCost=cost } };

    private async Task<(int project,int line)> SeedAsync(SqlServerFixture.ProbeDatabase probe)
    {
        await using var db=sql.ContextFor(probe);
        var project=new Project{CompanyID=1,Code="R7-"+Guid.NewGuid().ToString("N"),Name="Native editor test",Status="Active",IsActive=true,CreatedAt=DateTime.UtcNow};
        db.Projects.Add(project);await db.SaveChangesAsync();
        var result=await Service(db).SaveLinesAsync(1,project.ID,
            new(){new(){Code="A",Description="Work",Quantity=10,UnitPrice=2,MaterialCost=1}},7,"Native fixture");
        Assert.True(result.ok,result.error);
        return(project.ID,await db.BoqItems.AsNoTracking().Where(x=>x.ProjectId==project.ID).Select(x=>x.ID).SingleAsync());
    }

    [SkippableFact]
    public async Task A_native_only_update_refuses_the_stale_browser_deletion_and_preserves_the_new_fact()
    {
        Skip.If(!sql.Available,sql.SkipReason);
        var probe=await sql.CreateProbeDatabaseAsync("R7Editor");
        try
        {
            var ids=await SeedAsync(probe);
            BoqEditorState opened;
            await using(var reader=sql.ContextFor(probe))opened=await Service(reader).GetEditorAsync(1,ids.project);
            await using(var writer=sql.ContextFor(probe))
                await writer.BoqItems.Where(x=>x.ID==ids.line).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DescriptionEn,"Later native-only change"));
            await using(var request=sql.ContextFor(probe))
                Assert.False((await Service(request).SaveEditorAsync(1,ids.project,new(),opened.Snapshot,7)).ok);
            await using var verify=sql.ContextFor(probe);
            var stored=await verify.BoqItems.AsNoTracking().SingleAsync(x=>x.ID==ids.line);
            Assert.Equal("Later native-only change",stored.DescriptionEn);Assert.Equal("Active",stored.Status);
            Assert.Equal(Assert.Single(opened.Lines).ConcurrencyToken,stored.ConcurrencyToken);
            Assert.NotEqual(opened.Snapshot,(await Service(verify).GetEditorAsync(1,ids.project)).Snapshot);
        }
        finally{await sql.DropProbeDatabaseAsync(probe);}
    }

    [SkippableFact]
    public async Task Two_real_browser_writers_from_one_snapshot_commit_exactly_one_edit()
    {
        Skip.If(!sql.Available,sql.SkipReason);
        var probe=await sql.CreateProbeDatabaseAsync("R7EditorRace");
        try
        {
            var ids=await SeedAsync(probe);
            BoqEditorState opened;
            await using(var reader=sql.ContextFor(probe))opened=await Service(reader).GetEditorAsync(1,ids.project);
            await using var first=sql.ContextFor(probe);
            await using var second=sql.ContextFor(probe);
            var results=await Task.WhenAll(
                Service(first).SaveEditorAsync(1,ids.project,Row(ids.line,2),opened.Snapshot,42),
                Service(second).SaveEditorAsync(1,ids.project,Row(ids.line,3),opened.Snapshot,43));
            Assert.Single(results,x=>x.ok);Assert.Single(results,x=>!x.ok);
            Assert.Contains("another user",results.Single(x=>!x.ok).error!);
            await using var verify=sql.ContextFor(probe);
            var stored=await verify.BoqItems.AsNoTracking().SingleAsync(x=>x.ID==ids.line);
            Assert.Equal(results[0].ok ? 2m : 3m,stored.MaterialCost);
            Assert.Single(await verify.ConstructionAuditEntries.AsNoTracking()
                .Where(x=>x.ProjectId==ids.project && x.FieldName=="Description" && (x.ActorEmployeeId==42||x.ActorEmployeeId==43)).ToListAsync());
        }
        finally{await sql.DropProbeDatabaseAsync(probe);}
    }
}
