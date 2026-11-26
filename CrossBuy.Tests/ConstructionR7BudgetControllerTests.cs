using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Globalization;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7BudgetControllerTests
{
    [Fact]
    public async Task Exception_request_cannot_apply_another_orders_allocation_and_invalid_model_cannot_write()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7BudgetExceptionTests.Arrange(f.Db);
        var service = ConstructionR7BudgetExceptionTests.Service(f.Db); var controller = Controller(f, new Access(true));
        var preview = (await service.ReadPageAsync(Actor(), setup.Setup.Project, setup.Setup.SecondOrder))!;
        var input = new ConstructionExceptionForm { AllocationId = preview.AllocationId!.Value, SourceVersion = preview.SourceVersion,
            AdmissionVersion = preview.Preview!.Version, RequestId = Guid.NewGuid(), Reason = "Wrong order on screen" };
        Assert.IsType<ViewResult>(await controller.RequestBudgetException(setup.Setup.Project, setup.Setup.FirstOrder, input, service));
        Assert.Equal(400, controller.Response.StatusCode);
        controller.ModelState.AddModelError("Reason", "Invalid model");
        Assert.IsType<ViewResult>(await controller.RequestBudgetException(setup.Setup.Project, setup.Setup.SecondOrder, input, service));
        await using var verify = f.NewContext(); Assert.Single(await verify.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public void Exception_screen_has_complete_shared_translations_and_each_write_requires_antiforgery()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln"))) directory = directory.Parent;
        Assert.NotNull(directory); var root = directory!.FullName;
        Dictionary<string, string> Read(string suffix) => System.Xml.Linq.XDocument.Load(Path.Combine(root, "CrossBuy/Resources/SharedResources" + suffix + ".resx"))
            .Root!.Elements("data").Where(x => ((string?)x.Attribute("name"))?.StartsWith("Construction.Exception.", StringComparison.Ordinal) == true)
            .ToDictionary(x => (string)x.Attribute("name")!, x => (string?)x.Element("value") ?? "");
        var neutral = Read(""); Assert.NotEmpty(neutral);
        foreach (var suffix in new[] { ".en", ".ar", ".fr" })
        {
            var localized = Read(suffix); Assert.Equal(neutral.Keys.OrderBy(x => x), localized.Keys.OrderBy(x => x));
            Assert.All(localized, x => Assert.False(string.IsNullOrWhiteSpace(x.Value)));
        }
        foreach (var action in new[] { nameof(ProjectController.RequestBudgetException), nameof(ProjectController.ApproveBudgetException), nameof(ProjectController.RejectBudgetException) })
        {
            var method = typeof(ProjectController).GetMethod(action)!;
            Assert.Single(method.GetCustomAttributes(typeof(HttpPostAttribute), true));
            Assert.Single(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }
        var view = File.ReadAllText(Path.Combine(root, "CrossBuy/Views/Project/BudgetExceptions.cshtml"));
        Assert.DoesNotContain("Html.Raw", view); Assert.Contains("@Model.RejectedForm.Reason", view);
    }

    [Fact]
    public async Task Exception_screen_reads_exact_amounts_and_independent_decision_redirects_without_spending()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7BudgetExceptionTests.Arrange(f.Db);
        var service = ConstructionR7BudgetExceptionTests.Service(f.Db);
        var own = Assert.IsType<ConstructionBudgetExceptionPage>(Assert.IsType<ViewResult>(await Controller(f, new Access(true)).BudgetExceptions(setup.Setup.Project, setup.Setup.SecondOrder, service)).Model);
        Assert.Equal(60m, own.Preview!.ProposedAmount); Assert.Equal(20m, own.Preview.Overrun);
        Assert.False(Assert.Single(own.History).CanApprove); Assert.False(Assert.Single(own.History).CanReject);
        var independent = Controller(f, new Access(true), ConstructionR7BudgetExceptionTests.Approver);
        var page = Assert.IsType<ConstructionBudgetExceptionPage>(Assert.IsType<ViewResult>(await independent.BudgetExceptions(setup.Setup.Project, setup.Setup.SecondOrder, service)).Model);
        var row = Assert.Single(page.History); Assert.True(row.CanApprove); Assert.True(row.CanReject);
        var input = new ConstructionExceptionForm { ExceptionId = row.Id, AdmissionVersion = row.AdmissionVersion, RequestId = Guid.NewGuid(), Reason = "Reviewed exact overrun" };
        Assert.IsType<RedirectToActionResult>(await independent.ApproveBudgetException(setup.Setup.Project, setup.Setup.SecondOrder, input, service));
        Assert.IsType<RedirectToActionResult>(await independent.ApproveBudgetException(setup.Setup.Project, setup.Setup.SecondOrder, input, service));
        await using var verify = f.NewContext(); Assert.Single(await verify.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().ToListAsync());
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Exception_screen_self_approval_refusal_preserves_original_reason_and_version()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7BudgetExceptionTests.Arrange(f.Db);
        var service = ConstructionR7BudgetExceptionTests.Service(f.Db); var controller = Controller(f, new Access(true));
        var input = new ConstructionExceptionForm { ExceptionId = setup.Request.Id!.Value, AdmissionVersion = setup.Request.Version, RequestId = Guid.NewGuid(), Reason = "Do not lose this reason" };
        var result = Assert.IsType<ViewResult>(await controller.ApproveBudgetException(setup.Setup.Project, setup.Setup.SecondOrder, input, service));
        var page = Assert.IsType<ConstructionBudgetExceptionPage>(result.Model);
        Assert.Equal(409, controller.Response.StatusCode); Assert.Equal("IndependentApproverRequired", page.Refusal); Assert.Equal(input, page.RejectedForm);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Exception_screen_stale_request_cannot_be_approved_but_can_be_independently_rejected()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7BudgetExceptionTests.Arrange(f.Db);
        var token = Guid.NewGuid().ToByteArray();
        await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ConcurrencyToken, token));
        var service = ConstructionR7BudgetExceptionTests.Service(f.Db); var independent = Controller(f, new Access(true), ConstructionR7BudgetExceptionTests.Approver);
        var page = Assert.IsType<ConstructionBudgetExceptionPage>(Assert.IsType<ViewResult>(await independent.BudgetExceptions(setup.Setup.Project, setup.Setup.SecondOrder, service)).Model);
        var row = Assert.Single(page.History); Assert.Equal("Stale", row.State); Assert.False(row.CanApprove); Assert.True(row.CanReject);
        var input = new ConstructionExceptionForm { ExceptionId = row.Id, AdmissionVersion = row.AdmissionVersion, RequestId = Guid.NewGuid(), Reason = "Old financial basis rejected" };
        Assert.IsType<ViewResult>(await independent.ApproveBudgetException(setup.Setup.Project, setup.Setup.SecondOrder, input, service));
        Assert.IsType<RedirectToActionResult>(await independent.RejectBudgetException(setup.Setup.Project, setup.Setup.SecondOrder, input with { RequestId = Guid.NewGuid() }, service));
        await using var verify = f.NewContext(); Assert.Equal("Rejected", (await verify.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().SingleAsync()).Outcome);
    }

    [Fact]
    public async Task Exception_request_post_binds_preview_and_preserves_culture_and_rejected_intent()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7BudgetExceptionTests.Arrange(f.Db);
        var service = ConstructionR7BudgetExceptionTests.Service(f.Db); var controller = Controller(f, new Access(true));
        var page = (await service.ReadPageAsync(Actor(), setup.Setup.Project, setup.Setup.SecondOrder))!;
        var input = new ConstructionExceptionForm { AllocationId = page.AllocationId!.Value, SourceVersion = page.SourceVersion, AdmissionVersion = page.Preview!.Version, RequestId = Guid.NewGuid(), Reason = "Exact displayed request" };
        var old = CultureInfo.CurrentCulture; var oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new("ar-KW"); CultureInfo.CurrentUICulture = new("ar");
            var redirect = Assert.IsType<RedirectToActionResult>(await controller.RequestBudgetException(setup.Setup.Project, setup.Setup.SecondOrder, input, service));
            Assert.Equal("ar-KW", redirect.RouteValues!["culture"]); Assert.Equal("ar", redirect.RouteValues["ui-culture"]);
        }
        finally { CultureInfo.CurrentCulture = old; CultureInfo.CurrentUICulture = oldUi; }
        var token = Guid.NewGuid().ToByteArray(); await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ConcurrencyToken, token));
        var stale = input with { RequestId = Guid.NewGuid(), Reason = "Retain stale values" };
        var refused = Assert.IsType<ConstructionBudgetExceptionPage>(Assert.IsType<ViewResult>(await controller.RequestBudgetException(setup.Setup.Project, setup.Setup.SecondOrder, stale, service)).Model);
        Assert.Equal("ExceptionSourceChanged", refused.Refusal); Assert.Equal(stale, refused.RejectedForm); Assert.Equal(409, controller.Response.StatusCode);
        await using var verify = f.NewContext(); Assert.Equal(2, await verify.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().CountAsync());
    }

    [Theory]
    [InlineData("foreign")] [InlineData("external")] [InlineData("denied")] [InlineData("missing")]
    public async Task Exception_read_boundary_does_not_disclose_foreign_or_unauthorized_history(string kind)
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7BudgetExceptionTests.Arrange(f.Db);
        var actor = kind == "foreign" ? new BusinessContext { CompanyId = 2, EmployeeId = 8, UserId = "foreign", Source = BusinessContextSource.Test }
            : kind == "external" ? new BusinessContext { CompanyId = 1, EmployeeId = 8, UserId = "external", Source = BusinessContextSource.ExternalPortal } : Actor();
        Assert.Null(await ConstructionR7BudgetExceptionTests.Service(f.Db, kind != "denied").ReadPageAsync(actor, kind == "missing" ? int.MaxValue : setup.Setup.Project, null));
    }

    private static BusinessContext Actor() => new() { CompanyId = 1, EmployeeId = 7, UserId = "R7-budget-controller", Source = BusinessContextSource.Test };
    private static ConstructionBudgetVersionService Service(ConstructionTestFixture f, Access access)
        => new(f.Db, access, f.Audit, new CurrencyRounding(f.Db, new CurrencyService(f.Db), new Localizer()));
    private static ProjectController Controller(ConstructionTestFixture f, Access access, BusinessContext? actor = null)
    {
        var http = new DefaultHttpContext();
        return new ProjectController(new ProjectService(f.Db), f.Boq,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, f.Db,
            access, null!, null!, new Context(actor), new Company(), null!, new Localizer(),
            new ConstructionStructureService(f.Db, access, new Warehouses(), f.Audit), Service(f, access),
            new ConstructionBudgetPolicyService(f.Db, access, f.Audit), new ConstructionBudgetExposureService(f.Db, access, f.Audit))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new MemoryTempData())
        };
    }
    private static string Rows(int node, int code, string amount) => System.Text.Json.JsonSerializer.Serialize(new[] {
        new { wbsNodeId = node.ToString(CultureInfo.InvariantCulture), costCodeId = code.ToString(CultureInfo.InvariantCulture),
            amount, quantity = (string?)null, rate = (string?)null, isContingency = false, changeReason = "Estimate review" }
    });

    [Fact]
    public async Task Decimal_strings_save_exactly_under_Arabic_culture_and_redirect_retains_both_cultures()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f);
        var access = new Access(true); var created = await Service(f, access).CreateAsync(Actor(), ids.Project, "Original", null, null, "Initial estimate"); Assert.True(created.Ok, created.Code);
        var controller = Controller(f, access); var oldCulture = CultureInfo.CurrentCulture; var oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new("ar-KW"); CultureInfo.CurrentUICulture = new("ar");
            var redirect = Assert.IsType<RedirectToActionResult>(await controller.SaveConstructionBudgetDraft(ids.Project, created.Id!.Value,
                created.Version!, Rows(ids.Node, ids.Code, "1.234"), "Precise estimate"));
            Assert.Equal(nameof(ProjectController.BudgetVersions), redirect.ActionName);
            Assert.Equal("ar-KW", redirect.RouteValues!["culture"]); Assert.Equal("ar", redirect.RouteValues["ui-culture"]);
            Assert.Equal(created.Id.Value, redirect.RouteValues["v"]);
        }
        finally { CultureInfo.CurrentCulture = oldCulture; CultureInfo.CurrentUICulture = oldUi; }
        await using var verify = f.NewContext(); Assert.Equal(1.234m, (await verify.Set<ConstructionBudgetLine>().AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task A_stale_post_shows_rejected_values_and_current_values_without_overwriting_the_budget()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f); var access = new Access(true); var service = Service(f, access);
        var created = await service.CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, created.Id!.Value, created.Version!,
            new[] { new ConstructionBudgetDraftLine(ids.Node, ids.Code, 100, null, null, false, "Stored estimate") }, "Save estimate"); Assert.True(saved.Ok, saved.Code);
        var controller = Controller(f, access);
        var response = Assert.IsType<ViewResult>(await controller.SaveConstructionBudgetDraft(ids.Project, created.Id.Value, created.Version!, Rows(ids.Node, ids.Code, "900"), "Stale attempt"));
        Assert.Equal(StatusCodes.Status409Conflict, controller.Response.StatusCode);
        var page = Assert.IsType<ConstructionBudgetPage>(response.Model);
        Assert.Equal(900m, Assert.Single(page.RejectedLines!).Amount); Assert.Equal(100m, Assert.Single(page.Document!.Lines).Amount);
        Assert.Equal("ConcurrencyConflict", page.RefusalCode); Assert.NotEqual(created.Version, page.Document.Header.RowVersion);
        await using var verify = f.NewContext(); Assert.Equal(100m, (await verify.Set<ConstructionBudgetLine>().AsNoTracking().SingleAsync()).Amount);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("not-json")]
    public async Task Malformed_rows_are_not_treated_as_an_empty_replacement(string payload)
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f); var access = new Access(true);
        var created = await Service(f, access).CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        var controller = Controller(f, access);
        var response = Assert.IsType<ViewResult>(await controller.SaveConstructionBudgetDraft(ids.Project, created.Id!.Value, created.Version!, payload, "Invalid payload"));
        Assert.Equal(StatusCodes.Status400BadRequest, controller.Response.StatusCode);
        Assert.Equal("InvalidForm", Assert.IsType<ConstructionBudgetPage>(response.Model).RefusalCode);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionBudgetLine>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ConstructionAuditEntries.AsNoTracking().Where(x => x.FieldName == "BudgetLines").ToListAsync());
    }

    [Fact]
    public async Task A_refused_financial_gate_creates_no_budget_or_audit()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f);
        var controller = Controller(f, new Access(false));
        Assert.IsType<RedirectToActionResult>(await controller.CreateConstructionBudgetVersion(ids.Project, "Original", null, null, "Unauthorized"));
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionBudgetVersion>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ConstructionAuditEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_creator_cannot_use_the_approval_endpoint_to_approve_their_own_submission()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f); var access = new Access(true); var service = Service(f, access);
        var created = await service.CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        var saved = await service.SaveDraftAsync(Actor(), ids.Project, created.Id!.Value, created.Version!,
            new[] { new ConstructionBudgetDraftLine(ids.Node, ids.Code, 100, null, null, false, "Estimate") }, "Save"); Assert.True(saved.Ok, saved.Code);
        var submitted = await service.SubmitAsync(Actor(), ids.Project, created.Id.Value, saved.Version!, "Submit"); Assert.True(submitted.Ok, submitted.Code);
        var controller = Controller(f, access);
        Assert.IsType<RedirectToActionResult>(await controller.ApproveConstructionBudgetVersion(ids.Project, created.Id.Value, submitted.Version!, "Approve myself"));
        Assert.Contains("SeparateApproverRequired", controller.TempData["PrjErr"]!.ToString());
        await using var verify = f.NewContext(); Assert.Equal("Submitted", (await verify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await verify.ConstructionAuditEntries.AsNoTracking().Where(x => x.FieldName == "ApproveBudget").ToListAsync());
    }

    [Fact]
    public async Task A_budget_reader_sees_the_version_but_cannot_post_an_edit()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f);
        var created = await Service(f, new Access(true)).CreateAsync(Actor(), ids.Project, "Original", null, null, "Estimate"); Assert.True(created.Ok, created.Code);
        var controller = Controller(f, new Access(true, ProjectsActions.BudgetView));
        var response = Assert.IsType<ViewResult>(await controller.BudgetVersions(ids.Project, created.Id));
        var page = Assert.IsType<ConstructionBudgetPage>(response.Model); Assert.False(page.CanManage); Assert.Equal(created.Id, page.Document!.Header.ID);
        Assert.IsType<RedirectToActionResult>(await controller.SaveConstructionBudgetDraft(ids.Project, created.Id!.Value, created.Version!, Rows(ids.Node, ids.Code, "100"), "Reader attempt"));
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionBudgetLine>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ConstructionAuditEntries.AsNoTracking().Where(x => x.FieldName == "BudgetLines").ToListAsync());
    }

    [Fact]
    public async Task Exposure_panel_uses_the_approved_currency_even_when_the_selected_draft_has_another_currency()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7ReservationTests.Arrange(f.Db);
        var approved = await f.Db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync();
        var alternative = new CrossBuy.Models.Context.Accounting.Currency { Code = "ALT", Name = "Alternative", NameEn = "Alternative", DecimalPlaces = 2 };
        f.Db.Add(alternative); await f.Db.SaveChangesAsync();
        var draft = new ConstructionBudgetVersion { CompanyID = 1, ProjectId = setup.Project, VersionNo = 2,
            Kind = "Scenario", Status = "Draft", CurrencyId = alternative.ID, CreatedBy = 7, Reason = "Unapproved scenario" };
        f.Db.Add(draft); await f.Db.SaveChangesAsync();
        var controller = Controller(f, new Access(true, ProjectsActions.BudgetView));
        var response = Assert.IsType<ViewResult>(await controller.BudgetVersions(setup.Project, draft.ID));
        var page = Assert.IsType<ConstructionBudgetPage>(response.Model);
        Assert.False(page.CanManage); Assert.Equal("ALT", page.CurrencyCode);
        Assert.True(page.Exposure!.Ok, page.Exposure.Code); Assert.Equal("TST", page.Exposure.CurrencyCode);
        Assert.Equal(approved.ID, page.Exposure.BudgetVersionId); Assert.Equal(100, Assert.Single(page.Exposure.Rows!).Remaining);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Exposure_panel_get_returns_no_amounts_when_a_posted_expense_has_no_attribution()
    {
        using var f = new ConstructionTestFixture(); var setup = await ConstructionR7ReservationTests.Arrange(f.Db);
        await ConstructionR7BudgetExposureTests.Expense(f.Db, setup.Project, 50);
        var controller = Controller(f, new Access(true, ProjectsActions.BudgetView));
        var response = Assert.IsType<ViewResult>(await controller.BudgetVersions(setup.Project, null));
        var page = Assert.IsType<ConstructionBudgetPage>(response.Model);
        Assert.NotNull(page.Exposure); Assert.False(page.Exposure.Ok); Assert.Null(page.Exposure.Rows); Assert.Null(page.Exposure.CurrencyCode);
        Assert.Equal("RealizationCoverageRequired", page.Exposure.Code);
        await using var verify = f.NewContext(); Assert.Single(await verify.JournalEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionActualCostAllocation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
    }

    private sealed class Access(bool allow, string? onlyAction = null) : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default) => Task.FromResult(allow && (onlyAction == null || action == onlyAction));
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
    private sealed class Warehouses : IConstructionWarehouseAccess
    { public Task<bool> CanBindAsync(BusinessContext context, int warehouseId, CancellationToken ct = default) => Task.FromResult(false); }
    private sealed class Context(BusinessContext? actor = null) : IBusinessContextAccessor
    {
        public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(actor ?? Actor());
        public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<BusinessContext?>(actor ?? Actor());
    }
    private sealed class Company : IRequestCompanyResolver
    {
        public Task<CompanyResolution> ResolveAsync(int? requestSuppliedCompanyId = null, CancellationToken cancellationToken = default) => Task.FromResult(CompanyResolution.Resolved(1, 7, null));
    }
    private sealed class Localizer : IStringLocalizer<CrossBuy.SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
