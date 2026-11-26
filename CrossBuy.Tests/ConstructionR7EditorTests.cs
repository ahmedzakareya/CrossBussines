using CrossBuy.BL;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7EditorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stale_form_cannot_overwrite_or_retire_a_line_changed_since_it_was_displayed(bool remove)
    {
        using var f = new ConstructionTestFixture(); var project = await f.SeedProjectAsync();
        Assert.True((await f.Boq.SaveLinesAsync(1, project.ID,
            new() { new() { Code="A", Description="Work", Quantity=10, UnitPrice=2, MaterialCost=1 } },7,"Initial")).ok);
        var opened = await f.Boq.GetEditorAsync(1,project.ID); var line=Assert.Single(opened.Lines);
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,
            new(){new(){Id=line.ID,Code="A",Description="Work",Quantity=10,UnitPrice=2,MaterialCost=8}},8,"New estimate")).ok);
        var posted = remove ? new List<BoqRowInput>() : new(){new(){Id=line.ID,Code="A",Description="Work",Quantity=10,UnitPrice=2,MaterialCost=3}};
        Assert.False((await f.Boq.SaveEditorAsync(1,project.ID,posted,opened.Snapshot,7)).ok);
        await using var verify=f.NewContext();
        var stored=await verify.BoqItems.AsNoTracking().SingleAsync(x=>x.ID==line.ID);
        Assert.Equal(8m,stored.MaterialCost); Assert.Equal("Active",stored.Status);
    }

    [Fact]
    public async Task An_unseen_new_line_is_not_silently_retired_by_an_older_empty_editor()
    {
        using var f=new ConstructionTestFixture();var project=await f.SeedProjectAsync();
        var opened=await f.Boq.GetEditorAsync(1,project.ID);
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,
            new(){new(){Code="A",Description="Concurrent new line",Quantity=1,UnitPrice=2}},8,"New scope")).ok);
        Assert.False((await f.Boq.SaveEditorAsync(1,project.ID,new(),opened.Snapshot,7)).ok);
        await using var verify=f.NewContext();
        Assert.Equal("Active",(await verify.BoqItems.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_fresh_editor_renames_the_same_identity_and_records_the_actual_actor()
    {
        using var f=new ConstructionTestFixture();var project=await f.SeedProjectAsync();
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,
            new(){new(){Code="A",Description="Original",Quantity=1,UnitPrice=2}},7,"Initial")).ok);
        var opened=await f.Boq.GetEditorAsync(1,project.ID);var id=Assert.Single(opened.Lines).ID;
        var saved=await f.Boq.SaveEditorAsync(1,project.ID,
            new(){new(){Id=id,Code="B",Description="Renamed",Quantity=1,UnitPrice=2}},opened.Snapshot,42);
        Assert.True(saved.ok,saved.error);
        await using var verify=f.NewContext();
        var row=await verify.BoqItems.AsNoTracking().SingleAsync();
        Assert.Equal(id,row.ID);Assert.Equal("B",row.Code);Assert.Equal("Renamed",row.Description);
        Assert.Contains(await verify.ConstructionAuditEntries.AsNoTracking().ToListAsync(),x=>x.ActorEmployeeId==42);
    }

    [Fact]
    public async Task Missing_or_cross_project_snapshots_and_duplicate_identities_have_no_effect()
    {
        using var f=new ConstructionTestFixture();var project=await f.SeedProjectAsync();
        var opened=await f.Boq.GetEditorAsync(1,project.ID);
        Assert.False((await f.Boq.SaveEditorAsync(1,project.ID,new(),null,7)).ok);
        Assert.False((await f.Boq.SaveEditorAsync(1,project.ID+1,new(),opened.Snapshot,7)).ok);
        var rows=new List<BoqRowInput>{new(){Id=99,Description="First"},new(){Id=99,Description="Second"}};
        Assert.False((await f.Boq.SaveEditorAsync(1,project.ID,rows,opened.Snapshot,7)).ok);
        await using var verify=f.NewContext();
        Assert.Empty(await verify.BoqItems.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ConstructionAuditEntries.AsNoTracking().ToListAsync());
    }
}
