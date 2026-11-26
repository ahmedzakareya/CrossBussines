using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CrossBuy.BL.Construction;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7PurchaseRequestUiTests
{
    internal static InventoryController Controller(CrossDbContext db)
    {
        var http = new DefaultHttpContext();
        return new InventoryController(null!, null!, null!, db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, new Localizer())
        { ControllerContext = new ControllerContext { HttpContext = http }, TempData = new TempDataDictionary(http, new TempProvider()) };
    }
    [Fact]
    public async Task Saved_proposal_preview_roundtrips_through_real_controller_and_preserves_one_pending_request()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7PurchaseApprovalTests.Arrange(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor(); var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor);
        var controller = Controller(f.Db);
        var page = Assert.IsType<ConstructionPurchaseRequestPage>(Assert.IsType<ViewResult>(await controller.ConstructionPurchaseApproval(setup.Seed.Project, setup.Order, service)).Model);
        Assert.True(page.CanSubmit, page.ReadinessCode); Assert.Equal(3, page.CurrencyDecimals); Assert.Equal(1.482m, page.TotalAmount);
        Assert.Equal(1.235m, Assert.Single(page.Splits).Amount); Assert.Equal(setup.Version, page.SourceVersion); Assert.Empty(page.History);
        await AssertSubmission(controller, service, page);
        await AssertSubmission(controller, service, page);
        await using var verify = f.NewContext(); Assert.Single(await verify.InventoryApprovals.ToListAsync()); Assert.Single(await verify.BusinessEvents.ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToListAsync()); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().ToListAsync());
        var pending = await service.ReadRequestAsync(setup.Seed.Project, setup.Order); Assert.NotNull(pending);
        Assert.False(pending.CanSubmit); Assert.Equal("ApprovalAlreadyPending", pending.ReadinessCode); Assert.Equal("Pending", Assert.Single(pending.History).Status);
    }
    internal static async Task AssertSubmission(InventoryController controller, IConstructionPurchaseApprovalService service, ConstructionPurchaseRequestPage page, int? exceptionId = null)
    {
        var result = Assert.IsType<RedirectToActionResult>(await controller.RequestConstructionPurchaseApproval(page.ProjectId, page.OrderId, page.AllocationId!.Value,
            page.SourceVersion!, exceptionId, page.RequestId, "Independent review requested", service));
        Assert.Equal(nameof(InventoryController.ConstructionPurchaseApproval), result.ActionName);
        Assert.Equal(page.OrderId, result.RouteValues!["orderId"]); Assert.Contains("culture", result.RouteValues.Keys); Assert.Contains("ui-culture", result.RouteValues.Keys);
    }

    [Theory][InlineData("source", "DraftSourceChanged")][InlineData("currency", "DraftCurrencyChanged")]
    [InlineData("baseline", "ApprovedBaselineRequired")][InlineData("policy", "AllocationSourceChanged")]
    public async Task Preview_refuses_stale_source_currency_baseline_or_policy(string changed, string expected)
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7PurchaseApprovalTests.Arrange(f.Db);
        if (changed == "source") await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.ItemDescription, "Changed saved identity"));
        if (changed == "currency") await f.Db.Currencies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DecimalPlaces, (byte)2));
        if (changed == "baseline") await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x => x.SetProperty(p => p.IsBaseline, false));
        if (changed == "policy") { var token = Guid.NewGuid().ToByteArray(); await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ConcurrencyToken, token)); }
        var page = await ConstructionR7PurchaseApprovalTests.Service(f.Db, new()).ReadRequestAsync(setup.Seed.Project, setup.Order);
        Assert.NotNull(page); Assert.False(page.CanSubmit); Assert.Equal(expected, page.ReadinessCode); Assert.Empty(page.Splits);
        await using var verify = f.NewContext(); Assert.Empty(await verify.InventoryApprovals.ToListAsync());
    }

    [Fact]
    public async Task Stale_post_preserves_reason_request_id_and_old_witness_with_conflict_and_no_partial_submission()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7PurchaseApprovalTests.Arrange(f.Db);
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, new()); var page = (await service.ReadRequestAsync(setup.Seed.Project, setup.Order))!;
        await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.ItemDescription, "Concurrent source change"));
        await AssertConflict(Controller(f.Db), service, page);
        await using var verify = f.NewContext(); Assert.Empty(await verify.InventoryApprovals.ToListAsync()); Assert.Empty(await verify.BusinessEvents.ToListAsync());
    }
    internal static async Task AssertConflict(InventoryController controller, IConstructionPurchaseApprovalService service, ConstructionPurchaseRequestPage page)
    {
        const string reason = "Keep this explanation <unchanged>";
        var result = Assert.IsType<ViewResult>(await controller.RequestConstructionPurchaseApproval(page.ProjectId, page.OrderId, page.AllocationId!.Value,
            page.SourceVersion!, null, page.RequestId, reason, service));
        var rejected = Assert.IsType<ConstructionPurchaseRequestPage>(result.Model);
        Assert.Equal(409, controller.Response.StatusCode); Assert.False(rejected.CanSubmit); Assert.Equal(reason, rejected.Reason);
        Assert.Equal(page.RequestId, rejected.RequestId); Assert.Equal(page.SourceVersion, rejected.SourceVersion); Assert.Equal(page.AllocationId, rejected.AllocationId);
        Assert.NotNull(rejected.RefusalCode);
    }

    [Fact]
    public async Task Preview_and_navigation_require_actual_inventory_project_company_and_internal_actor()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7PurchaseApprovalTests.Arrange(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var allowed = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor);
        Assert.Equal(setup.Seed.Project, Assert.Single(await allowed.RequestLinksAsync(new[] { setup.Order, int.MaxValue })).ProjectId);
        var denied = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor, projectAllowed: false);
        Assert.Null(await denied.ReadRequestAsync(setup.Seed.Project, setup.Order)); Assert.Empty(await denied.RequestLinksAsync(new[] { setup.Order }));
        denied = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor, new ConstructionR7PurchaseApprovalTests.InventoryAuthority { Purchase = false });
        Assert.Null(await denied.ReadRequestAsync(setup.Seed.Project, setup.Order)); Assert.Empty(await denied.RequestLinksAsync(new[] { setup.Order }));
        actor.Current = ConstructionR7OrderAllocationTests.Actor(2); Assert.Null(await allowed.ReadRequestAsync(setup.Seed.Project, setup.Order)); Assert.Empty(await allowed.RequestLinksAsync(new[] { setup.Order }));
        actor.Current = new BusinessContext { CompanyId = 1, EmployeeId = 7, Source = BusinessContextSource.ExternalPortal };
        Assert.Null(await allowed.ReadRequestAsync(setup.Seed.Project, setup.Order)); Assert.Empty(await allowed.RequestLinksAsync(new[] { setup.Order }));
        Assert.IsType<ForbidResult>(await Controller(f.Db).RequestConstructionPurchaseApproval(setup.Seed.Project, setup.Order, setup.Allocation, setup.Version, null, Guid.NewGuid(), "Denied submission", allowed));
    }

    [Fact]
    public async Task Only_current_independently_approved_unconsumed_exception_is_offered()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7PurchaseApprovalTests.Arrange(f.Db, 200m, "ApprovalRequired");
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor(); var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor);
        var finance = ConstructionR7BudgetExceptionTests.Service(f.Db);
        var raised = await finance.RequestAsync(actor.Current, setup.Seed.Project, setup.Allocation, setup.Version, Guid.NewGuid(), "Review exact overrun"); Assert.True(raised.Ok, raised.Code);
        var page = (await service.ReadRequestAsync(setup.Seed.Project, setup.Order))!; Assert.False(page.CanSubmit); Assert.Empty(page.Exceptions);
        Assert.Equal("MatchingBudgetExceptionRequired", page.ReadinessCode);
        Assert.True((await finance.ApproveAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Seed.Project, raised.Id!.Value, raised.Version!, Guid.NewGuid(), "Independent budget decision")).Ok);
        page = (await service.ReadRequestAsync(setup.Seed.Project, setup.Order))!; Assert.True(page.CanSubmit, page.ReadinessCode); Assert.Equal(raised.Id, Assert.Single(page.Exceptions).Id);
        var posted = Assert.IsType<RedirectToActionResult>(await Controller(f.Db).RequestConstructionPurchaseApproval(page.ProjectId, page.OrderId, page.AllocationId!.Value,
            page.SourceVersion!, raised.Id, page.RequestId, "Purchase with approved exception", service));
        Assert.NotNull(posted.RouteValues);
    }

    [Fact]
    public async Task Invalid_model_binding_refuses_before_submission_and_preserves_input()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7PurchaseApprovalTests.Arrange(f.Db);
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, new()); var controller = Controller(f.Db); controller.ModelState.AddModelError("requestId", "Malformed identifier");
        var response = Assert.IsType<ViewResult>(await controller.RequestConstructionPurchaseApproval(setup.Seed.Project, setup.Order, setup.Allocation, setup.Version, null, Guid.Empty, "Preserve reason", service));
        var page = Assert.IsType<ConstructionPurchaseRequestPage>(response.Model); Assert.Equal(400, controller.Response.StatusCode); Assert.Equal("Preserve reason", page.Reason); Assert.False(page.CanSubmit);
        await using var verify = f.NewContext(); Assert.Empty(await verify.InventoryApprovals.ToListAsync());
    }

    [Fact]
    public void Submission_has_antiforgery_inventory_permission_and_no_posted_actor_company_or_amount()
    {
        var action = typeof(InventoryController).GetMethod(nameof(InventoryController.RequestConstructionPurchaseApproval))!;
        Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>()); Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        var permission = action.GetCustomAttribute<InvPermAttribute>(); Assert.NotNull(permission);
        Assert.Equal("purchase", typeof(InvPermAttribute).GetField("_action", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(permission));
        Assert.DoesNotContain(action.GetParameters(), p => new[] { "actor", "employeeId", "companyId", "amount", "approved" }.Contains(p.Name));
    }

    [Fact]
    public void View_uses_paired_resources_existing_layout_and_encoded_text_without_new_design_assets()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent; Assert.NotNull(root);
        var view = File.ReadAllText(Path.Combine(root!.FullName, "CrossBuy/Views/Inventory/ConstructionPurchaseApproval.cshtml"));
        Assert.Contains("_LayoutInventory.cshtml", view); Assert.Contains("@Html.AntiForgeryToken()", view); Assert.DoesNotContain("Html.Raw", view);
        Assert.DoesNotContain("<style", view); Assert.DoesNotContain("<script", view); Assert.Contains("@Model.Reason", view); Assert.Contains("DisplayName.Of", view);
        var keys = Regex.Matches(view, "SR\\[\"([^\"]+)\"\\]").Select(x => x.Groups[1].Value).Distinct().ToArray();
        foreach (var culture in new[] { "", ".en", ".ar", ".fr" })
        {
            var resources = XDocument.Load(Path.Combine(root.FullName, "CrossBuy/Resources/SharedResources" + culture + ".resx"));
            foreach (var key in keys) Assert.Single(resources.Root!.Elements("data"), x => (string?)x.Attribute("name") == key && !string.IsNullOrWhiteSpace(x.Element("value")?.Value));
        }
    }
    private sealed class Localizer : IStringLocalizer<CrossBuy.SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
    private sealed class TempProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
