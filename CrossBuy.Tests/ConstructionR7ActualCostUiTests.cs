using System.Reflection;
using System.Xml.Linq;
using CrossBuy.BL.Construction;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Context.Construction;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace CrossBuy.Tests;
public sealed class ConstructionR7ActualCostUiTests
{
    [Theory]
    [InlineData("ready")][InlineData("missing-currency")][InlineData("denied")][InlineData("wrong-company")]
    [InlineData("wrong-total")][InlineData("duplicate")][InlineData("stale")][InlineData("foreign-line")]
    public async Task Workspace_checks_source_scope_and_preserves_financial_history(string mode)
    {
        using var f = new ConstructionTestFixture(); var seed = await ConstructionR7ActualCostTests.Seed(f, mode != "missing-currency");
        var actors = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = mode == "wrong-company"
            ? new() { CompanyId = 2, EmployeeId = 7, UserId = "other", Source = CrossBuy.Models.Platform.BusinessContextSource.Test }
            : ConstructionR7OrderAllocationTests.Actor() };
        var access = new ConstructionR7ActualCostTests.Access(mode != "denied");
        var workspace = new ConstructionActualCostWorkspace(f.Db, actors, access, new ConstructionActualCostService(f.Db, access, f.Audit));
        var line = mode == "foreign-line" ? seed.ExpenseLine + 9999 : seed.ExpenseLine;
        var page = await workspace.ReadAsync(seed.Project, line);
        var input = new ConstructionActualInput { RequestId = Guid.NewGuid(), Reason = "Cost attribution", Rows = [new() { WbsNodeId = seed.Node, CostCodeId = seed.Code, Amount = mode == "wrong-total" ? 1m : 12.345m }, new()] };
        if (mode == "duplicate") input.Rows.Add(new() { WbsNodeId = seed.Node, CostCodeId = seed.Code, Amount = 1m });
        var preview = await workspace.PreviewAsync(seed.Project, line, input);
        if (mode is "denied" or "wrong-company" or "foreign-line") Assert.Null(page);
        if (mode is "ready" or "stale") Assert.True(preview.Ok, preview.Code); else Assert.False(preview.Ok);
        input.Version = preview.Version ?? new string('A', 64);
        if (mode == "stale") await f.Db.JournalEntries.Where(x => x.ID == seed.Entry).ExecuteUpdateAsync(x => x.SetProperty(p => p.EntryDate, DateTime.Today.AddDays(-3)));
        var result = await workspace.AllocateAsync(seed.Project, line, input);
        Assert.Equal(mode == "ready", result.Ok);
        if (result.Ok)
        {
            var replay = await workspace.AllocateAsync(seed.Project, line, input); Assert.True(replay.Ok); Assert.Equal("AlreadyAllocated", replay.Code);
            page = await workspace.ReadAsync(seed.Project, line); Assert.True(page!.AlreadyAllocated);
        }
        await using var verify = f.NewContext();
        Assert.Equal(mode == "ready" ? 1 : 0, await verify.Set<ConstructionActualCostAllocation>().AsNoTracking().CountAsync());
        Assert.Single(await verify.JournalEntries.AsNoTracking().ToArrayAsync());
        Assert.Equal(12.345m, (await verify.JournalEntryLines.AsNoTracking().SingleAsync(x => x.ID == seed.ExpenseLine)).Debit);
        Assert.Empty(await verify.StockMovements.AsNoTracking().ToArrayAsync());
    }
    private sealed class Gateway : IConstructionActualCostWorkspace
    {
        public string Current = new('A', 64); public int Writes;
        public Task<ConstructionActualCostPage?> ReadAsync(int p, int? l, CancellationToken ct = default) => Task.FromResult<ConstructionActualCostPage?>(new(p,"P","Project",null,[],false,l,false,new(true,"Ready",Current,1,10m),[],[],new()));
        public Task<ConstructionActualPreview> PreviewAsync(int p, int l, ConstructionActualInput i, CancellationToken ct = default) => Task.FromResult(new ConstructionActualPreview(true,"Ready",new('B',64),1,10m));
        public Task<ConstructionActualWrite> AllocateAsync(int p, int l, ConstructionActualInput i, CancellationToken ct = default) { Writes++; return Task.FromResult(new ConstructionActualWrite(false,"ConcurrencyConflict")); }
    }
    [Fact]
    public async Task A_preview_is_not_confirmed_against_a_changed_source_and_refusal_preserves_request()
    {
        var gateway = new Gateway();var controller = new ConstructionActualCostController(gateway) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var input = new ConstructionActualInput { RequestId = Guid.NewGuid(), Reason = "keep", Rows = [new() { WbsNodeId=1,CostCodeId=2,Amount=10m }] };
        var result = Assert.IsType<ViewResult>(await controller.Preview(1,2,input)); var page = Assert.IsType<ConstructionActualCostPage>(result.Model);
        Assert.False(page.Confirmed);Assert.Equal("ConcurrencyConflict",page.Error);Assert.Equal(0,gateway.Writes);Assert.Same(input,page.Input);
        var refused=Assert.IsType<ViewResult>(await controller.Allocate(1,2,input));Assert.Same(input,Assert.IsType<ConstructionActualCostPage>(refused.Model).Input);
    }
    [Fact]
    public void Browser_writes_are_authorized_and_localization_keys_match()
    {
        Assert.NotNull(typeof(ConstructionActualCostController).GetCustomAttribute<SessionValidationAttribute>());
        foreach(var name in new[]{"Preview","Allocate"})
        {var m=typeof(ConstructionActualCostController).GetMethod(name)!;Assert.NotNull(m.GetCustomAttribute<ProjectPermAttribute>());Assert.NotNull(m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());Assert.NotNull(m.GetCustomAttribute<HttpPostAttribute>());}
        DirectoryInfo? root=new(AppContext.BaseDirectory);while(root!=null&&!File.Exists(Path.Combine(root.FullName,"CrossBuy.sln")))root=root.Parent;Assert.NotNull(root);string[]? keys=null;
        var menu=File.ReadAllText(Path.Combine(root!.FullName,"CrossBuy/Models/Menu/MainMenu.cs"));
        Assert.Contains("ConstructionRoute(\"actual-cost\")",menu);Assert.Contains("ConstructionRoute(\"rebase\")",menu);
        var projects=File.ReadAllText(Path.Combine(root.FullName,"CrossBuy/Controllers/ProjectController.cs"));Assert.Contains("or \"actual-cost\" or \"rebase\"",projects);

        foreach(var suffix in new[]{"",".en",".ar",".fr"})
        {var rows=XDocument.Load(Path.Combine(root!.FullName,"CrossBuy/Resources/SharedResources"+suffix+".resx")).Root!.Elements("data").Where(x=>((string)x.Attribute("name")!).StartsWith("Construction.ActualUi.")).ToArray();var actual=rows.Select(x=>(string)x.Attribute("name")!).OrderBy(x=>x).ToArray();Assert.True(actual.Length>25);Assert.Equal(actual.Length,actual.Distinct().Count());if(keys!=null)Assert.Equal(keys,actual);keys=actual;Assert.All(rows,x=>Assert.False(string.IsNullOrWhiteSpace(x.Element("value")?.Value)));}
    }
}
