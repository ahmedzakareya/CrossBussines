namespace CrossBuy.Analyzers.Tests;

/// <summary>
/// R1 — the Reporting authorization seam was added to the declared authority surface.
///
/// These tests exist to pin the BOUNDARY, not to make Reporting green. The surface must credit an
/// endpoint that reaches <c>IReportAuthorizationService</c> and must refuse everything that merely
/// looks like it does — a service with a similar name, a similar method, or no authority at all.
///
/// The failure mode being guarded against is the one CORRECTION-004 records: crediting an authority
/// that checks nothing. A surface that says "authorized" where no decision is taken is worse than no
/// surface, because it converts an unknown into a false assurance.
/// </summary>
public class ReportingAuthoritySurfaceTests
{
    private static EndpointFacts Single(string controllerSource, string action)
    {
        var compilation = AnalyzerHarness.CompileController(controllerSource);
        AnalyzerHarness.AssertCompiles(compilation);
        var inventory = AuthorizationInventory.Build(compilation, CancellationToken.None);
        var match = inventory.Endpoints.SingleOrDefault(e => e.Action == action);
        Assert.NotNull(match);
        return match!;
    }

    // -----------------------------------------------------------------------------------------------------
    // RECOGNISED — the seam is a real authority and reaching it is credited.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_reporting_write_that_calls_the_authorization_seam_is_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IReportAuthorizationService _authorization;
    public TestController(IReportAuthorizationService authorization) { _authorization = authorization; }

    [HttpPost]
    public async Task<IActionResult> AddFavorite(string code)
    {
        var decision = await _authorization.AuthorizeReportAsync(code, 1, new BusinessContext());
        if (!decision.Allowed) return Forbid();
        return Ok();
    }
}
""", "AddFavorite");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
        Assert.Contains("IReportAuthorizationService", endpoint.InBodyEvidence);
    }

    [Fact]
    public void A_reporting_write_that_reaches_the_seam_through_a_helper_is_credited()
    {
        // This is the real shape: ReportTemplateService.SaveAsync / ForkAsync / SetDefaultAsync /
        // DeleteAsync do not call the seam directly — they go through LoadForAccessAsync, which does.
        // If the resolver stopped at direct calls, all four would be wrongly reported as unauthorized.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IReportAuthorizationService _authorization;
    public TestController(IReportAuthorizationService authorization) { _authorization = authorization; }

    private async Task<bool> LoadForAccessAsync(int templateId) =>
        (await _authorization.AuthorizeTemplateAsync(templateId, new BusinessContext())).Allowed;

    [HttpPost]
    public async Task<IActionResult> SaveReport(int templateId)
    {
        if (!await LoadForAccessAsync(templateId)) return Forbid();
        return Ok();
    }
}
""", "SaveReport");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
        Assert.Contains("LoadForAccessAsync", endpoint.InBodyChain);
    }

    [Fact]
    public void The_concrete_reporting_authorization_service_is_also_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly ReportAuthorizationService _authorization;
    public TestController(ReportAuthorizationService authorization) { _authorization = authorization; }

    [HttpPost]
    public async Task<IActionResult> Export(string code)
    {
        var decision = await _authorization.AuthorizeReportAsync(code, 1, new BusinessContext());
        if (!decision.Allowed) return Forbid();
        return Ok();
    }
}
""", "Export");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    // -----------------------------------------------------------------------------------------------------
    // REFUSED — the boundary. Each of these would be a false credit.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_fake_reporting_service_with_an_authorization_shaped_name_is_NOT_credited()
    {
        // The whole point of a DECLARED surface: authority is granted by name, deliberately, one type at
        // a time. A type nobody declared is not an authority however convincing its method reads.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IFakeReportAuthorizationService _authorization;
    public TestController(IFakeReportAuthorizationService authorization) { _authorization = authorization; }

    [HttpPost]
    public async Task<IActionResult> SaveReport()
    {
        var decision = await _authorization.AuthorizeReportAsync("x", 1, new BusinessContext());
        if (!decision.Allowed) return Forbid();
        return Ok();
    }
}
""", "SaveReport");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void An_unrelated_reporting_service_is_NOT_credited()
    {
        // IReportTemplateService and IReportLibraryService are NOT authorities. They CONSUME the seam.
        // Declaring them would credit every endpoint that touches a template — including the two that
        // authorize only by row ownership — and that is precisely the widening the brief forbids.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IReportTemplateService _templates;
    public TestController(IReportTemplateService templates) { _templates = templates; }

    [HttpPost]
    public async Task<IActionResult> DeleteSavedReport(int templateId)
    {
        await _templates.DeleteAsync(templateId, new BusinessContext());
        return Ok();
    }
}
""", "DeleteSavedReport");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void A_reporting_write_with_no_authority_at_all_remains_rejected()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    [HttpPost]
    public async Task<IActionResult> ReorderFavorites(int[] ordered)
    {
        await Task.CompletedTask;
        return Ok();
    }
}
""", "ReorderFavorites");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void An_unrelated_controller_is_unaffected_by_the_reporting_addition()
    {
        // Adding an authority must not widen anything outside its own type. A regression here would mean
        // the surface had started crediting by shape rather than by declaration.
        var endpoint = Single("""
public class TestController : Controller
{
    [HttpPost]
    public async Task<IActionResult> DoSomething()
    {
        await Task.CompletedTask;
        return Ok();
    }
}
""", "DoSomething");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    // -----------------------------------------------------------------------------------------------------
    // The surface stays explicit and shrink-only.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_reporting_authority_is_declared_explicitly_by_both_names()
    {
        Assert.Contains("IReportAuthorizationService", AuthorizationSurface.AuthorityTypes);
        Assert.Contains("ReportAuthorizationService", AuthorizationSurface.AuthorityTypes);
    }

    [Fact]
    public void No_reporting_service_other_than_the_seam_is_declared_an_authority()
    {
        // Pins the narrowness of the addition. If someone later adds IReportService or
        // IReportLibraryService to the surface, this fails and they must justify it in the file header
        // the way every prior addition was justified.
        string[] mustNotBeAuthorities =
        {
            "IReportService", "ReportService",
            "IReportTemplateService", "ReportTemplateService",
            "IReportLibraryService", "ReportLibraryService",
            "IReportHistoryService", "IReportArchiveService",
            "IReportDatasetRegistry", "IReportPermissionEvaluator",
        };

        foreach (var name in mustNotBeAuthorities)
        {
            Assert.DoesNotContain(name, AuthorizationSurface.AuthorityTypes);
        }
    }
}
