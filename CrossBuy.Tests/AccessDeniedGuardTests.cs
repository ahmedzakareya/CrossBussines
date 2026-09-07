using Xunit;

namespace CrossBuy.Tests;

/// <summary>
/// SHARED ACCESS-DENIED DESTINATION — regression guard.
///
/// THE DEFECT THIS PINS: ASP.NET Identity's cookie handler redirects an authenticated-but-unauthorised
/// request to AccessDeniedPath, which defaults to "/Account/AccessDenied". Nothing in this repository
/// declared that path and no action answered it, so every Forbid() on a page request ended as
/// 302 -> 404. The authorization system decided "forbidden" and the user was shown "missing" — the two
/// outcomes a security surface must never confuse, because a 404 tells an administrator to go looking
/// for a broken route instead of a missing grant.
///
/// These are SOURCE-level assertions, like PlatformShellGuardTests. They cannot prove what the running
/// server returns — that is what the runtime probe against CrossBuyDev does — but they fail the build
/// the moment the destination, its status code, or its non-leakage properties are removed, which is the
/// regression that actually happened and would otherwise be invisible until someone hit a Forbid().
///
/// OWNERSHIP: TAB-1 (shared authentication/platform level). This deliberately lives in no module: the
/// refusal is identical for every module, so a per-module page would be one screen copied N times.
/// </summary>
public sealed class AccessDeniedGuardTests
{
    private static string RepoRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)
            && File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
        {
            return fromEnvironment;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ControllerSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "AccountController.cs"));

    private static string ViewPath()
        => Path.Combine(RepoRoot(), "CrossBuy", "Views", "Account", "AccessDenied.cshtml");

    private static string ProgramSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

    // ---------------------------------------------------------------------------------------------
    // The destination exists at all. This is the assertion whose absence produced the 404.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void The_AccessDenied_action_exists_on_the_shared_account_controller()
    {
        Assert.Contains("IActionResult AccessDenied()", ControllerSource());
    }

    [Fact]
    public void The_AccessDenied_view_exists_so_the_action_can_render()
    {
        Assert.True(File.Exists(ViewPath()),
            "Views/Account/AccessDenied.cshtml is missing — the action would throw instead of rendering, " +
            "which is the 404 in a different costume.");
    }

    // ---------------------------------------------------------------------------------------------
    // The path is DECLARED, not inherited. A default nobody wrote down is a default nobody maintains —
    // that is precisely how the action came to be missing without anything failing.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void The_access_denied_path_is_configured_explicitly_rather_than_left_to_the_framework_default()
    {
        var program = ProgramSource();
        Assert.Contains("ConfigureApplicationCookie", program);
        Assert.Contains("AccessDeniedPath = \"/Account/AccessDenied\"", program);
    }

    // ---------------------------------------------------------------------------------------------
    // Semantics. A redirect target that answers 200 erases the refusal: to anything reading status
    // codes, a forbidden page becomes indistinguishable from a page the user was allowed to open.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void The_response_reports_403_so_the_redirect_does_not_erase_the_refusal()
    {
        var source = ControllerSource();
        Assert.Contains("Response.StatusCode = StatusCodes.Status403Forbidden", source);
        Assert.Contains("StatusCode(StatusCodes.Status403Forbidden", source);   // the AJAX/JSON branch
    }

    [Fact]
    public void A_script_caller_gets_json_rather_than_an_html_page()
    {
        var source = ControllerSource();
        Assert.Contains("XMLHttpRequest", source);
        Assert.Contains("access_denied", source);
    }

    // ---------------------------------------------------------------------------------------------
    // It must be reachable by the very user it refuses. Without [AllowAnonymous] a denied caller is
    // redirected to a page that denies them, which redirects again — an infinite loop.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void The_destination_is_anonymous_so_a_denied_user_does_not_loop()
    {
        var source = ControllerSource();
        var index = source.IndexOf("IActionResult AccessDenied()", StringComparison.Ordinal);
        Assert.True(index > 0, "AccessDenied action not found.");

        // Look only at the attributes immediately preceding the action, so an AllowAnonymous somewhere
        // else in this large controller cannot satisfy the assertion by accident.
        var window = source.Substring(Math.Max(0, index - 400), Math.Min(400, index));
        Assert.Contains("AllowAnonymous", window);
    }

    // ---------------------------------------------------------------------------------------------
    // Non-leakage. Naming the missing permission maps the authorization surface for whoever is probing
    // it; rendering the cookie handler's ReturnUrl puts attacker-controlled text on the page.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void The_page_names_no_permission_role_or_module_and_never_renders_the_return_url()
    {
        // Razor comments are stripped first, because the claim being tested is about what the page RENDERS,
        // not about what it documents. The first version of this test scanned the raw file and failed on the
        // view's own comment explaining that it deliberately does not render the ReturnUrl - the assertion was
        // reading the explanation as the offence.
        var view = RenderedMarkup(File.ReadAllText(ViewPath()));

        Assert.DoesNotContain("ReturnUrl", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Request.Query", view, StringComparison.Ordinal);

        // Role names that would identify the grant the caller is missing. Sourced from
        // PlatformOpsAttribute.AdminRoles and the Reporting permission map in Program.cs.
        foreach (var role in new[] { "SuperAdmin", "PlatformOps", "Auditor", "Administrator" })
        {
            Assert.DoesNotContain(role, view, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Everything a browser could receive: the view with its Razor comment blocks (@* ... *@) removed.
    /// A comment cannot leak anything because it is never emitted.
    /// </summary>
    private static string RenderedMarkup(string razor)
        => System.Text.RegularExpressions.Regex.Replace(
            razor, @"@\*.*?\*@", " ", System.Text.RegularExpressions.RegexOptions.Singleline);

    // ---------------------------------------------------------------------------------------------
    // Visual authority. This is the screen a user meets when something has gone wrong — the worst
    // possible moment to show them a shell the rest of the product does not use.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void The_page_renders_on_the_inventory_visual_authority_shell()
    {
        var view = File.ReadAllText(ViewPath());
        Assert.Contains("_LayoutInventory.cshtml", view);
    }

    [Fact]
    public void The_page_is_bilingual_arabic_and_english()
    {
        var view = File.ReadAllText(ViewPath());

        Assert.Contains("TwoLetterISOLanguageName == \"ar\"", view);
        Assert.Contains("Access denied", view);
        Assert.Contains("لا تملك صلاحية الوصول", view);
    }
}
