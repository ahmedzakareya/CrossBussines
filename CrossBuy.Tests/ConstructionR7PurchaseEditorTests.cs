using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7PurchaseEditorTests
{
    internal sealed class Warehouses(bool allow = true) : IConstructionWarehouseAccess
    {
        public Task<bool> CanBindAsync(BusinessContext c, int id, CancellationToken ct = default) => Task.FromResult(allow && c.CompanyId == 1);
    }
    internal static ConstructionPurchaseEditorService Service(CrossDbContext db, ConstructionR7PurchaseApprovalTests.ActorAccessor actor,
        bool inventory = true, bool project = true, bool warehouse = true) => new(db, actor,
        new[] { new ConstructionR7PurchaseApprovalTests.InventoryAuthority { Purchase = inventory } }, new ConstructionR7OrderAllocationTests.Access(project),
        new Warehouses(warehouse), ConstructionR7PurchaseDraftTests.Service(db, project),
        new ConstructionOrderAllocationService(db, new ConstructionR7OrderAllocationTests.Access(project), new ConstructionAuditService(db)), new ConstructionAuditService(db));
    internal static ConstructionPurchaseEditorInput Draft(ConstructionR7PurchaseDraftTests.SeedData s) => new()
    {
        BaselineId = s.Baseline, BaselineVersion = Convert.ToBase64String(s.Version), VendorId = s.Vendor, WarehouseId = s.Warehouse,
        OrderDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Reason = "Prepare browser proposal", RequestId = Guid.NewGuid(),
        RowsJson = JsonSerializer.Serialize(new[] { new { ItemId = (int?)s.Item, Description = "Service", Quantity = "1", UnitId = (int?)s.Unit, UnitPrice = "1.2345", Discount = "0", TaxRate = "20" } })
    };
    internal static ConstructionPurchaseEditorInput Allocate(ConstructionPurchaseEditorPage page, ConstructionR7PurchaseDraftTests.SeedData s) => page.Input with
    {
        Reason = "Allocate saved browser lines", RowsJson = JsonSerializer.Serialize(page.Lines.Select(x => new { SourceLineId = x.Id, WbsNodeId = s.Node, CostCodeId = s.Cost, NetDocumentAmount = x.NetAmount.ToString(CultureInfo.InvariantCulture) }))
    };

    [Fact]
    public async Task Actual_editor_controller_creates_one_draft_allocates_exact_lines_then_reaches_approval_request()
    {
        using var f = new ConstructionTestFixture(); var seed = await ConstructionR7PurchaseDraftTests.Seed(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor(); var editor = Service(f.Db, actor); var controller = ConstructionR7PurchaseRequestUiTests.Controller(f.Db);
        var page = Assert.IsType<ConstructionPurchaseEditorPage>(Assert.IsType<ViewResult>(await controller.NewConstructionPurchase(seed.Project, editor)).Model);
        Assert.True(page.CanSave, page.Code); Assert.Single(page.Items); Assert.Single(page.Vendors); Assert.Single(page.Warehouses);
        var input = Draft(seed);
        var first = Assert.IsType<RedirectToActionResult>(await controller.CreateConstructionPurchaseDraft(seed.Project, input, editor));
        var repeated = Assert.IsType<RedirectToActionResult>(await controller.CreateConstructionPurchaseDraft(seed.Project, input, editor));
        var order = Assert.IsType<int>(first.RouteValues!["orderId"]); Assert.Equal(order, repeated.RouteValues!["orderId"]);
        page = Assert.IsType<ConstructionPurchaseEditorPage>(Assert.IsType<ViewResult>(await controller.ConstructionPurchaseAllocation(seed.Project, order, editor)).Model);
        Assert.True(page.CanSave, page.Code); Assert.Single(page.Dimensions); Assert.Equal(1.235m, Assert.Single(page.Lines).NetAmount);
        var allocation = Allocate(page, seed);
        Assert.Equal(nameof(InventoryController.ConstructionPurchaseApproval), Assert.IsType<RedirectToActionResult>(await controller.SaveConstructionPurchaseAllocation(seed.Project, order, allocation, editor)).ActionName);
        Assert.IsType<RedirectToActionResult>(await controller.SaveConstructionPurchaseAllocation(seed.Project, order, allocation, editor));
        var request = await ConstructionR7PurchaseApprovalTests.Service(f.Db, actor).ReadRequestAsync(seed.Project, order); Assert.NotNull(request); Assert.True(request.CanSubmit, request.ReadinessCode);
        await using var verify = f.NewContext(); Assert.Single(await verify.PurchaseOrders.ToListAsync()); Assert.Single(await verify.Set<ConstructionOrderAllocation>().ToListAsync());
        Assert.Empty(await verify.InventoryApprovals.ToListAsync()); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToListAsync());
        Assert.Empty(await verify.StockMovements.ToListAsync()); Assert.Empty(await verify.JournalEntries.ToListAsync());
    }

    [Theory][InlineData("malformed")][InlineData("comma")][InlineData("exponent")][InlineData("number")][InlineData("missing")][InlineData("extra")][InlineData("duplicate")][InlineData("null-version")]
    public async Task Invalid_or_ambiguous_browser_input_cannot_silently_create_financial_values(string defect)
    {
        using var f = new ConstructionTestFixture(); var seed = await ConstructionR7PurchaseDraftTests.Seed(f.Db); var input = Draft(seed);
        var json = JsonNode.Parse(input.RowsJson)!.AsArray(); var row = json[0]!.AsObject();
        if (defect == "comma") row["UnitPrice"] = "1,2345";
        if (defect == "exponent") row["UnitPrice"] = "1e2";
        if (defect == "number") row["UnitPrice"] = 1.2345m;
        if (defect == "missing") row.Remove("TaxRate");
        if (defect == "extra") row["Approved"] = true;
        input = input with { RowsJson = defect == "malformed" ? "not-json" : json.ToJsonString() };
        if (defect == "duplicate") input = input with { RowsJson = input.RowsJson.Replace("\"UnitPrice\":", "\"UnitPrice\":\"9\",\"UnitPrice\":", StringComparison.Ordinal) };
        if (defect == "null-version") input = input with { BaselineVersion = null };
        var result = await Service(f.Db, new()).CreateAsync(seed.Project, input); Assert.False(result.Ok); Assert.Equal("InvalidDraftRequest", result.Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.PurchaseOrders.ToListAsync()); Assert.Empty(await verify.Set<ConstructionPurchaseDraft>().ToListAsync());
    }

    [Fact]
    public async Task Actor_inventory_project_and_warehouse_boundaries_are_rechecked_on_direct_submission()
    {
        using var f = new ConstructionTestFixture(); var seed = await ConstructionR7PurchaseDraftTests.Seed(f.Db); var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor(); var input = Draft(seed);
        foreach(var denied in new[] { Service(f.Db, actor, inventory: false), Service(f.Db, actor, project: false), Service(f.Db, actor, warehouse: false) })
            Assert.Equal("AccessDenied", (await denied.CreateAsync(seed.Project, input)).Code);
        var read = await Service(f.Db, actor, warehouse: false).ReadAsync(seed.Project); Assert.NotNull(read); Assert.Empty(read.Warehouses);
        actor.Current = ConstructionR7OrderAllocationTests.Actor(2); Assert.Null(await Service(f.Db, actor).ReadAsync(seed.Project)); Assert.Equal("AccessDenied", (await Service(f.Db, actor).CreateAsync(seed.Project, input)).Code);
        actor.Current = new BusinessContext { CompanyId = 1, EmployeeId = 7, Source = BusinessContextSource.ExternalPortal };
        Assert.Equal("AccessDenied", (await Service(f.Db, actor).CreateAsync(seed.Project, input)).Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.PurchaseOrders.ToListAsync());
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Missing_explicit_policy_or_approved_baseline_never_invents_a_default(bool policy)
    {
        using var f = new ConstructionTestFixture(); var seed = await ConstructionR7PurchaseDraftTests.Seed(f.Db);
        if (policy) await f.Db.Set<ConstructionBudgetPolicy>().ExecuteDeleteAsync(); else await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x => x.SetProperty(p => p.IsBaseline, false));
        var page = await Service(f.Db, new()).ReadAsync(seed.Project); Assert.NotNull(page); Assert.False(page.CanSave); Assert.Equal(policy ? "PolicyNotConfigured" : "ApprovedBaselineRequired", page.Code);
        Assert.False((await Service(f.Db, new()).CreateAsync(seed.Project, Draft(seed))).Ok);
        await using var verify = f.NewContext(); Assert.Empty(await verify.PurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task Incomplete_allocation_is_refused_and_original_rows_reason_and_source_are_preserved()
    {
        using var f = new ConstructionTestFixture(); var seed = await ConstructionR7PurchaseDraftTests.Seed(f.Db); var editor = Service(f.Db, new());
        var created = await editor.CreateAsync(seed.Project, Draft(seed)); Assert.True(created.Ok, created.Code);
        var page = (await editor.ReadAsync(seed.Project, created.OrderId))!; var input = Allocate(page, seed);
        input = input with { RowsJson = input.RowsJson.Replace("1.235", "1.234", StringComparison.Ordinal), Reason = "Keep exact split <intent>" };
        var controller = ConstructionR7PurchaseRequestUiTests.Controller(f.Db);
        var rejected = Assert.IsType<ConstructionPurchaseEditorPage>(Assert.IsType<ViewResult>(await controller.SaveConstructionPurchaseAllocation(seed.Project, created.OrderId!.Value, input, editor)).Model);
        Assert.Equal(400, controller.Response.StatusCode); Assert.Equal("LineAllocationIncomplete", rejected.RefusalCode); Assert.Equal(input, rejected.Input); Assert.True(rejected.CanSave);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionOrderAllocation>().ToListAsync());
    }

    [Fact]
    public async Task Source_changes_and_pending_approval_block_new_allocations_but_do_not_revoke_historical_acknowledgements()
    {
        using var f = new ConstructionTestFixture(); var seed = await ConstructionR7PurchaseDraftTests.Seed(f.Db); var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor(); var editor = Service(f.Db, actor);
        var created = await editor.CreateAsync(seed.Project, Draft(seed)); var page = (await editor.ReadAsync(seed.Project, created.OrderId))!; var input = Allocate(page, seed);
        var saved = await editor.AllocateAsync(seed.Project, created.OrderId!.Value, input); Assert.True(saved.Ok, saved.Code);
        var request = await ConstructionR7PurchaseApprovalTests.Service(f.Db, actor).RequestAsync(seed.Project, created.OrderId.Value, saved.Id!.Value, input.SourceVersion!, null, Guid.NewGuid(), "Review frozen allocation"); Assert.True(request.Ok, request.Code);
        Assert.Equal("AlreadySaved", (await editor.AllocateAsync(seed.Project, created.OrderId.Value, input)).Code);
        Assert.Equal("ApprovalAlreadyPending", (await editor.AllocateAsync(seed.Project, created.OrderId.Value, input with { RequestId = Guid.NewGuid() })).Code);
        Assert.False((await editor.ReadAsync(seed.Project, created.OrderId))!.CanSave);
        await using var verify = f.NewContext(); Assert.Single(await verify.Set<ConstructionOrderAllocation>().ToListAsync());
    }

    [Fact]
    public async Task Stale_baseline_returns_conflict_without_replacing_posted_witness_or_reason()
    {
        using var f = new ConstructionTestFixture(); var seed = await ConstructionR7PurchaseDraftTests.Seed(f.Db); var editor = Service(f.Db, new()); var input = Draft(seed);
        var changed = Guid.NewGuid().ToByteArray(); await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x => x.SetProperty(p => p.RowVersion, changed));
        var controller = ConstructionR7PurchaseRequestUiTests.Controller(f.Db);
        var page = Assert.IsType<ConstructionPurchaseEditorPage>(Assert.IsType<ViewResult>(await controller.CreateConstructionPurchaseDraft(seed.Project, input, editor)).Model);
        Assert.Equal(409, controller.Response.StatusCode); Assert.False(page.CanSave); Assert.Equal(input, page.Input); Assert.Equal("ConcurrencyConflict", page.RefusalCode);
        await using var verify = f.NewContext(); Assert.Empty(await verify.PurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task Split_amounts_may_retain_more_precision_than_currency_without_changing_the_saved_line_total()
    {
        using var f = new ConstructionTestFixture(); var seed = await ConstructionR7PurchaseDraftTests.Seed(f.Db);
        var extra = new ConstructionCostCode { CompanyID = 1, Code = "SECOND", Name = "Other", Category = "Material", IsActive = true, AllowDirectPosting = true };
        f.Db.Add(extra); await f.Db.SaveChangesAsync();
        f.Db.Add(new ConstructionBudgetLine { CompanyID = 1, ProjectId = seed.Project, BudgetVersionId = seed.Baseline, WbsNodeId = seed.Node, CostCodeId = extra.ID, CostCategory = "Material", Amount = 100 }); await f.Db.SaveChangesAsync();
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor(); var editor = Service(f.Db, actor);
        var created = await editor.CreateAsync(seed.Project, Draft(seed)); Assert.True(created.Ok, created.Code);
        var page = (await editor.ReadAsync(seed.Project, created.OrderId))!; var line = Assert.Single(page.Lines);
        var input = page.Input with { Reason = "Explicit exact half splits", RowsJson = JsonSerializer.Serialize(new[] {
            new { SourceLineId = line.Id, WbsNodeId = seed.Node, CostCodeId = seed.Cost, NetDocumentAmount = "0.6175" },
            new { SourceLineId = line.Id, WbsNodeId = seed.Node, CostCodeId = extra.ID, NetDocumentAmount = "0.6175" } }) };
        var result = await editor.AllocateAsync(seed.Project, created.OrderId!.Value, input); Assert.True(result.Ok, result.Code);
        var request = (await ConstructionR7PurchaseApprovalTests.Service(f.Db, actor).ReadRequestAsync(seed.Project, created.OrderId.Value))!;
        Assert.True(request.CanSubmit, request.ReadinessCode); Assert.Equal(3, request.CurrencyDecimals); Assert.Equal(1.235m, request.Splits.Sum(x => x.Amount));
        Assert.All(request.Splits, x => Assert.Equal(0.6175m, x.Amount));
    }

    [Fact]
    public void Actual_MVC_metadata_does_not_require_the_other_steps_conditional_fields()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddControllers(); using var provider = services.BuildServiceProvider();
        var metadata = provider.GetRequiredService<IModelMetadataProvider>();
        foreach(var field in new[] { "BaselineVersion", "SourceVersion", "OrderDate" }) Assert.False(metadata.GetMetadataForProperty(typeof(ConstructionPurchaseEditorInput), field).IsRequired);
        Assert.True(metadata.GetMetadataForProperty(typeof(ConstructionPurchaseEditorInput), "Reason").IsRequired);
    }

    [Fact]
    public void Both_writes_require_purchase_and_antiforgery_and_shared_editor_uses_resources_and_decimal_strings()
    {
        foreach(var name in new[] { nameof(InventoryController.CreateConstructionPurchaseDraft), nameof(InventoryController.SaveConstructionPurchaseAllocation) })
        {
            var method = typeof(InventoryController).GetMethod(name)!; Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>()); Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.NotNull(method.GetCustomAttribute<InvPermAttribute>()); Assert.NotNull(method.GetCustomAttribute<RequestSizeLimitAttribute>());
        }
        DirectoryInfo? root = new(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent; Assert.NotNull(root);
        var view = File.ReadAllText(Path.Combine(root!.FullName, "CrossBuy/Views/Inventory/_ConstructionPurchaseEditor.cshtml")); Assert.Contains("@Html.AntiForgeryToken()", view); Assert.DoesNotContain("Html.Raw", view);
        var script = File.ReadAllText(Path.Combine(root.FullName, "CrossBuy/wwwroot/js/construction-purchase-editor.js")); Assert.DoesNotContain("parseFloat", script); Assert.DoesNotContain("innerHTML", script); Assert.Contains("JSON.stringify(data)", script);
        foreach(var language in new[] { "", ".en", ".ar", ".fr" })
        {
            var resource = XDocument.Load(Path.Combine(root.FullName, "CrossBuy/Resources/SharedResources" + language + ".resx"));
            foreach(var key in new[] { "Construction.Editor.DraftTitle", "Construction.Editor.AllocationTitle", "Construction.Editor.DecimalHint", "Construction.Editor.DraftSaved", "Construction.Editor.AllocationSaved" })
                Assert.Single(resource.Root!.Elements("data"), x => (string?)x.Attribute("name") == key && !string.IsNullOrWhiteSpace(x.Element("value")?.Value));
        }
    }
}
