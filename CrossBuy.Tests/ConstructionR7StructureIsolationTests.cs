using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7StructureIsolationTests
{
    [Fact]
    public async Task All_four_structural_dimensions_are_protected_by_the_real_write_guard()
    {
        await Refused<ConstructionStructureSettings>();await Refused<WbsNode>();
        await Refused<ConstructionCostCode>();await Refused<ConstructionSite>();
    }

    private static async Task Refused<T>() where T:class,new()
    {
        using var host=new PlatformTestHost(1);
        var foreign=new T();typeof(T).GetProperty("CompanyID")!.SetValue(foreign,2);
        host.Db.Add(foreign);
        await Assert.ThrowsAsync<CompanyWriteDeniedException>(()=>host.Db.SaveChangesAsync());
        host.Db.ChangeTracker.Clear();
        using var verify=host.AllCompanies();
        Assert.Empty(await verify.Set<T>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Read_filters_follow_the_executing_context_for_all_four_dimensions()
    {
        await Scoped<ConstructionStructureSettings>();await Scoped<WbsNode>();
        await Scoped<ConstructionCostCode>();await Scoped<ConstructionSite>();
    }

    private static async Task Scoped<T>() where T:class,new()
    {
        using var host=new PlatformTestHost(1);
        var company=typeof(T).GetProperty("CompanyID")!;
        var first=new T();company.SetValue(first,1);var foreign=new T();company.SetValue(foreign,2);
        // A site includes ProjectId in its scoped alternate key. Supply real principals rather
        // than the unknown zero key; disabled SQLite FKs cannot make an unknown EF key valid.
        var firstProject=new CrossBuy.Models.Context.Accounting.Project{CompanyID=1,Code="R7-SCOPE-1",Name="Scope one"};
        var secondProject=new CrossBuy.Models.Context.Accounting.Project{CompanyID=2,Code="R7-SCOPE-2",Name="Scope two"};
        host.Seed.AddRange(firstProject,secondProject);await host.Seed.SaveChangesAsync();
        typeof(T).GetProperty("ProjectId")?.SetValue(first,firstProject.ID);
        typeof(T).GetProperty("ProjectId")?.SetValue(foreign,secondProject.ID);
        host.Seed.AddRange(first,foreign);await host.Seed.SaveChangesAsync();
        var visible=await host.Db.Set<T>().AsNoTracking().ToListAsync();
        Assert.Equal(1,(int)company.GetValue(Assert.Single(visible))!);
        // A fresh context reuses the cached model with a different unresolved scope.
        var options=new DbContextOptionsBuilder<CrossDbContext>().UseSqlite(host.Db.Database.GetDbConnection()).Options;
        await using var unresolved=new CrossDbContext(options,new CompanyScopeHolder());
        Assert.Empty(await unresolved.Set<T>().AsNoTracking().ToListAsync());
    }
}
