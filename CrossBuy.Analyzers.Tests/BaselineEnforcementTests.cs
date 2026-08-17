using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CrossBuy.Analyzers.Tests;

/// <summary>
/// Baseline enforcement: CBA001 through CBA006 as the analyzer actually reports them.
///
/// These assert on real <see cref="Diagnostic"/> objects from the Roslyn analyzer driver, not on the inventory, so
/// they cover the reporting layer too — including the compilation-end path, which is where a missing
/// <c>CompilationEnd</c> tag would have silently dropped four of the six diagnostics.
/// </summary>
public class BaselineEnforcementTests
{
    private static ImmutableArray<Diagnostic> Run(
        string controllerSource,
        string? baseline,
        ImmutableDictionary<string, ReportDiagnostic>? overrides = null)
    {
        var compilation = AnalyzerHarness.CompileController(controllerSource);
        AnalyzerHarness.AssertCompiles(compilation);
        return AnalyzerHarness.Run(compilation, baseline, overrides);
    }

    private const string UnprotectedController = """
public class TestController : Controller
{
    [HttpPost] public IActionResult Save() => Ok();
}
""";

    // -----------------------------------------------------------------------------------------------------
    // CBA001 — new debt
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void CBA001_fires_for_an_unprotected_endpoint_absent_from_the_baseline()
    {
        var diagnostics = Run(UnprotectedController, AnalyzerHarness.Baseline("OtherController.Something"));

        var reported = Assert.Single(diagnostics.OfId("CBA001"));
        Assert.Contains("TestController.Save", reported.GetMessage());
    }

    [Fact]
    public void CBA001_does_not_fire_for_an_endpoint_the_baseline_already_accepts()
    {
        var diagnostics = Run(UnprotectedController, AnalyzerHarness.Baseline("TestController.Save"));

        Assert.Empty(diagnostics.OfId("CBA001"));
        Assert.Empty(diagnostics.OfId("CBA004"));
    }

    [Fact]
    public void CBA001_does_not_fire_for_a_protected_endpoint()
    {
        var diagnostics = Run("""
public class TestController : Controller
{
    [HttpPost][AccPerm("post")] public IActionResult Save() => Ok();
}
""", AnalyzerHarness.Baseline());

        Assert.Empty(diagnostics.OfId("CBA001"));
    }

    [Fact]
    public void New_debt_can_be_escalated_to_a_build_ERROR_by_configuration()
    {
        // The dogfood rule is "start as Warning". The enforcement rule is "new debt breaks the build". Both are
        // satisfied by shipping Warning and escalating in .editorconfig — and this proves the escalation works
        // rather than asserting it in a document.
        var overrides = ImmutableDictionary<string, ReportDiagnostic>.Empty
            .Add("CBA001", ReportDiagnostic.Error);

        var diagnostics = Run(UnprotectedController, AnalyzerHarness.Baseline(), overrides);

        var reported = Assert.Single(diagnostics.OfId("CBA001"));
        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        Assert.Equal(DiagnosticSeverity.Warning, reported.DefaultSeverity);
    }

    [Fact]
    public void Without_a_baseline_CBA001_and_CBA004_stay_silent_and_that_is_declared()
    {
        // A misconfiguration must not report 143 accepted entries as new debt. The degradation is deliberate and
        // documented; this test exists so it cannot change without a failing test.
        var diagnostics = Run(UnprotectedController, baseline: null);

        Assert.Empty(diagnostics.OfId("CBA001"));
        Assert.Empty(diagnostics.OfId("CBA004"));
    }

    [Fact]
    public void A_malformed_baseline_is_treated_as_absent_rather_than_crashing_the_build()
    {
        var diagnostics = Run(UnprotectedController, "{ \"entries\": [ { \"id\": ");

        Assert.Empty(diagnostics.OfId("CBA001"));
    }

    // -----------------------------------------------------------------------------------------------------
    // CBA002 — authentication without authorization
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void CBA002_names_the_control_that_is_present_and_not_a_permission()
    {
        var diagnostics = Run("""
[SessionValidation]
public class TestController : Controller
{
    [HttpPost][ValidateAntiForgeryToken] public IActionResult Save() => Ok();
}
""", AnalyzerHarness.Baseline("TestController.Save"));

        var reported = Assert.Single(diagnostics.OfId("CBA002"));
        Assert.Contains("authentication", reported.GetMessage());
        Assert.Contains("anti-forgery", reported.GetMessage());
    }

    [Fact]
    public void CBA002_names_the_lane_guard_as_checking_no_role()
    {
        var diagnostics = Run("""
public class TestController : Controller
{
    [HttpPost][PosLaneActivityGuard("hyper")] public IActionResult Save() => Ok();
}
""", AnalyzerHarness.Baseline("TestController.Save"));

        Assert.Contains("checks no role", Assert.Single(diagnostics.OfId("CBA002")).GetMessage());
    }

    [Fact]
    public void CBA002_stays_silent_when_the_endpoint_is_authorized()
    {
        var diagnostics = Run("""
[SessionValidation]
public class TestController : Controller
{
    [HttpPost][AccPerm("post")] public IActionResult Save() => Ok();
}
""", AnalyzerHarness.Baseline());

        Assert.Empty(diagnostics.OfId("CBA002"));
    }

    // -----------------------------------------------------------------------------------------------------
    // CBA003 — unsupported helper
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void CBA003_fires_for_an_authorization_shaped_call_that_is_not_a_declared_authority()
    {
        var diagnostics = Run("""
public class TestController : Controller
{
    private readonly IThingService _things;
    public TestController(IThingService things) { _things = things; }

    [HttpPost]
    public async Task<IActionResult> Save(int id)
    {
        if (!await _things.AuthorizeThingAsync(id)) return Forbid();
        return Ok();
    }
}
""", AnalyzerHarness.Baseline("TestController.Save"));

        Assert.Contains("AuthorizeThingAsync", Assert.Single(diagnostics.OfId("CBA003")).GetMessage());
    }

    // -----------------------------------------------------------------------------------------------------
    // CBA004 — stale baseline, in all three shapes
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void CBA004_fires_when_a_baselined_endpoint_becomes_protected()
    {
        var diagnostics = Run("""
public class TestController : Controller
{
    [HttpPost][AccPerm("post")] public IActionResult Save() => Ok();
}
""", AnalyzerHarness.Baseline("TestController.Save"));

        var reported = Assert.Single(diagnostics.OfId("CBA004"));
        Assert.Contains("TestController.Save", reported.GetMessage());
        Assert.Contains("now protected by AccPerm", reported.GetMessage());
    }

    [Fact]
    public void CBA004_fires_when_a_baselined_endpoint_is_deleted()
    {
        var diagnostics = Run("""
public class TestController : Controller
{
    [HttpPost] public IActionResult Other() => Ok();
}
""", AnalyzerHarness.Baseline("TestController.Save", "TestController.Other"));

        Assert.Contains("no longer exists on TestController",
            Assert.Single(diagnostics.OfId("CBA004")).GetMessage());
    }

    [Fact]
    public void CBA004_fires_when_the_whole_controller_is_gone()
    {
        var diagnostics = Run(UnprotectedController,
            AnalyzerHarness.Baseline("TestController.Save", "GoneController.Save"));

        Assert.Contains("the controller no longer exists",
            Assert.Single(diagnostics.OfId("CBA004")).GetMessage());
    }

    [Fact]
    public void A_RENAME_costs_both_a_stale_entry_and_new_debt()
    {
        // The rule the baseline states: "Renaming a controller or action does NOT create a new allowance."
        var diagnostics = Run("""
public class TestController : Controller
{
    [HttpPost] public IActionResult SaveRenamed() => Ok();
}
""", AnalyzerHarness.Baseline("TestController.Save"));

        Assert.Contains("TestController.Save", Assert.Single(diagnostics.OfId("CBA004")).GetMessage());
        Assert.Contains("TestController.SaveRenamed", Assert.Single(diagnostics.OfId("CBA001")).GetMessage());
    }

    // -----------------------------------------------------------------------------------------------------
    // CBA005 — undeclared anonymous
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void CBA005_fires_for_an_anonymous_mutating_endpoint_that_is_not_declared_anonymous()
    {
        var diagnostics = Run("""
using Microsoft.AspNetCore.Authorization;
public class TestController : Controller
{
    [HttpPost][AllowAnonymous] public IActionResult Save() => Ok();
}
""", AnalyzerHarness.Baseline("TestController.Save"));

        Assert.Contains("TestController.Save", Assert.Single(diagnostics.OfId("CBA005")).GetMessage());
    }

    [Fact]
    public void CBA005_stays_silent_when_the_baseline_declares_it_AnonymousByDesign()
    {
        var baseline = AnalyzerHarness.BaselineWith(new[] { ("TestController.Login", "AnonymousByDesign") });

        var diagnostics = Run("""
using Microsoft.AspNetCore.Authorization;
public class TestController : Controller
{
    [HttpPost][AllowAnonymous] public IActionResult Login() => Ok();
}
""", baseline);

        Assert.Empty(diagnostics.OfId("CBA005"));
        Assert.Empty(diagnostics.OfId("CBA001"));
    }

    // -----------------------------------------------------------------------------------------------------
    // CBA006 — suppression
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void CBA006_fires_on_a_pragma_that_disables_an_authorization_diagnostic()
    {
        var diagnostics = Run("""
#pragma warning disable CBA001
public class TestController : Controller
{
    [HttpPost] public IActionResult Save() => Ok();
}
""", AnalyzerHarness.Baseline());

        Assert.Contains("CBA001", Assert.Single(diagnostics.OfId("CBA006")).GetMessage());
    }

    [Fact]
    public void CBA006_fires_on_a_SuppressMessage_attribute_naming_an_id_or_the_category()
    {
        var diagnostics = Run("""
public class TestController : Controller
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CrossBuy.Authorization", "CBA001:x")]
    [HttpPost] public IActionResult Save() => Ok();
}
""", AnalyzerHarness.Baseline("TestController.Save"));

        Assert.Single(diagnostics.OfId("CBA006"));
    }

    [Fact]
    public void CBA006_does_not_fire_on_an_unrelated_pragma()
    {
        var diagnostics = Run("""
#pragma warning disable CS1591
public class TestController : Controller
{
    [HttpPost][AccPerm("post")] public IActionResult Save() => Ok();
}
""", AnalyzerHarness.Baseline());

        Assert.Empty(diagnostics.OfId("CBA006"));
    }
}
