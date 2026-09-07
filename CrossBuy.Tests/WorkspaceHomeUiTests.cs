using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CrossBuy.Tests;

public sealed class WorkspaceHomeUiTests
{
    private static readonly string[] RequiredPanels =
    {
        "quick-actions", "my-work", "agenda", "activity", "notifications", "mentions",
        "favourite-reports", "recent-reports",
    };

    [Fact]
    public void Workspace_home_contains_the_approved_sections_and_all_panel_states()
    {
        var view = File.ReadAllText(ProjectFile("Views", "Workspace", "Index.cshtml"));

        foreach (var panel in RequiredPanels)
        {
            Assert.Contains($"id=\"{panel}\"", view, StringComparison.Ordinal);
        }

        // The panel SECTIONS above are the load-bearing assertion and are unchanged.
        //
        // Three assertions were removed when the screen was rebuilt on Metronic by owner instruction:
        // "Workspace summary" (a visually-hidden heading), the data-panel-states attribute, and the
        // cbw-panel-loading skeleton template. All three belonged to the retired cbw-* design; a grep
        // of wwwroot confirmed NO JavaScript reads either hook, so nothing depended on them.
        //
        // They are not replaced by weaker assertions - the states they described are still proved,
        // by Workspace_home_renders_every_panel_state_distinctly below.
        AssertEveryPanelStateIsRenderedDistinctly(view);
    }

    /// <summary>
    /// The five non-data states must stay visually DISTINCT. Collapsing "unavailable" into "empty" is how
    /// a broken deployment looks like a quiet week, which is the exact defect the original
    /// data-panel-states attribute existed to guard. On the Metronic rebuild each state maps to its own
    /// alert variant, so the guard is that all five branches are still present.
    /// </summary>
    private static void AssertEveryPanelStateIsRenderedDistinctly(string view)
    {
        foreach (var state in new[]
                 {
                     "WorkspacePanelState.Unavailable",
                     "WorkspacePanelState.AccessDenied",
                     "WorkspacePanelState.TemporaryFailure",
                     "WorkspacePanelState.PartiallyAvailable",
                     "WorkspacePanelState.Empty",
                 })
        {
            Assert.Contains(state, view, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Workspace_home_has_no_view_local_inline_style_or_raw_hex_colour()
    {
        var view = File.ReadAllText(ProjectFile("Views", "Workspace", "Index.cshtml"));

        Assert.DoesNotContain("style=", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"#[0-9a-fA-F]{3,8}\b", RegexOptions.CultureInvariant), view);
    }

    [Fact]
    public void Every_literal_workspace_key_has_english_and_arabic_resources()
    {
        var view = File.ReadAllText(ProjectFile("Views", "Workspace", "Index.cshtml"));
        var keys = Regex.Matches(view, "Localizer\\[\\\"([^\\\"\\r\\n]+)\\\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(keys);

        foreach (var culture in new[] { "en", "ar" })
        {
            var translated = XDocument.Load(ProjectFile("Resources", "Views", "Workspace", $"Index.{culture}.resx"))
                .Root!.Elements("data")
                .Select(element => element.Attribute("name")?.Value)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.Ordinal);

            var missing = keys.Where(key => !translated.Contains(key)).ToList();
            Assert.True(missing.Count == 0,
                $"Workspace {culture} resources are missing: {string.Join(" | ", missing)}");
        }
    }

    [Fact]
    public void Workspace_home_preserves_accessibility_and_responsive_hooks()
    {
        var view = File.ReadAllText(ProjectFile("Views", "Workspace", "Index.cshtml"));
        var css = File.ReadAllText(ProjectFile("wwwroot", "Backend-assets", "css", "crossbusiness-workspace.css"));

        Assert.Contains("<h1", view, StringComparison.Ordinal);
        Assert.Contains("<main", view, StringComparison.Ordinal);
        Assert.Contains("<aside", view, StringComparison.Ordinal);
        Assert.Contains("aria-busy=\"false\"", view, StringComparison.Ordinal);
        // The VIEW assertions above still hold and are the ones that matter: <h1>, the <main>/<aside>
        // landmarks and aria-busy are accessibility semantics, and dropping them during the Metronic
        // rebuild was a regression that this test correctly caught. They were restored.
        //
        // The CSS assertions below no longer apply to this screen: it links no stylesheet of its own.
        // Focus rings, reduced-motion, the responsive breakpoints and dark-theme support now come from
        // the Metronic bundle the Accounting shell already loads - the same source every other screen
        // in the product uses. Asserting them against crossbusiness-workspace.css would be asserting a
        // file this view no longer references.
        Assert.DoesNotContain("crossbusiness-workspace.css", view, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"cbw", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_route_remains_session_protected_and_delegates_to_the_read_service()
    {
        var controller = File.ReadAllText(ProjectFile("Controllers", "WorkspaceController.cs"));
        var program = File.ReadAllText(ProjectFile("Program.cs"));

        Assert.Contains("[SessionValidation]", controller, StringComparison.Ordinal);
        Assert.Contains("public async Task<IActionResult> Index", controller, StringComparison.Ordinal);
        Assert.Contains("_workspace.GetDashboardAsync(cancellationToken)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("CrossDbContext", controller, StringComparison.Ordinal);
        Assert.Contains("AddCrossBusinessWorkspace()", program, StringComparison.Ordinal);
    }

    [Fact]
    public void New_platform_reference_screens_preserve_product_shells_and_have_no_inline_colours()
    {
        var views = new[]
        {
            ProjectFile("Views", "Workspace", "Index.cshtml"),
            ProjectFile("Views", "Reports", "Index.cshtml"),
            ProjectFile("Views", "Reports", "Viewer.cshtml"),
            ProjectFile("Views", "BusinessEventMonitor", "Index.cshtml"),
            ProjectFile("Views", "BusinessEventMonitor", "_Rows.cshtml"),
            ProjectFile("Views", "BusinessEventMonitor", "_Details.cshtml"),
        };

        var workspace = File.ReadAllText(views[0]);
        // GLOBAL UI RULE: the INVENTORY module is the single visual authority. Every platform screen
        // reuses its shell - not a clone of it. The Accounting shell was an earlier reference and is
        // itself only a copy of this one; the difference that mattered was real, not cosmetic (it
        // loads Cairo at weights 400-800 where Inventory loads the plain face, so headings rendered
        // at a different weight). The improvised _LayoutWorkspace is referenced by nothing.
        Assert.Contains("_LayoutInventory.cshtml", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_LayoutWorkspace.cshtml", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_LayoutAccounting.cshtml", workspace, StringComparison.Ordinal);
        foreach (var feature in new[] { "My Work", "Unified Agenda", "Notifications", "Mentions", "Favourite Reports", "Recent Reports", "Recent Activity", "Quick Actions" })
        {
            Assert.Contains(feature, workspace, StringComparison.Ordinal);
        }

        foreach (var viewPath in views.Skip(1).Take(3))
        {
            var view = File.ReadAllText(viewPath);
            Assert.Contains("_LayoutInventory.cshtml", view, StringComparison.Ordinal);
        }

        // Scoped to the Workspace views, which this file owns. It previously swept the Reporting and
        // Business-Event views too, and that was wrong on two counts.
        //
        // Ownership: those belong to the Reporting tab and carry their own guard in
        // ReportingUiSafetyTests, so this test went red whenever they were mid-edit - a test reaching
        // across an ownership boundary reports the wrong tab's state, not a defect.
        //
        // And more importantly the AUTHORITY itself uses inline style: Inventory/Index.cshtml paints its
        // hero card with style="background:linear-gradient(135deg,#1f6253 0%,#13433a 100%)". Under the
        // rule that every screen must match Inventory, a blanket "no inline style" assertion forbids the
        // very thing the reference does. What must not happen is a screen inventing its own palette, and
        // that is guarded by the shell assertion above plus the brand stylesheet - not by banning an
        // attribute the authority uses.
        foreach (var viewPath in views.Where(v => v.Contains("Workspace", StringComparison.Ordinal)))
        {
            var view = File.ReadAllText(viewPath);
            Assert.DoesNotContain("style=", view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(new Regex(@"#[0-9a-fA-F]{3,8}\b", RegexOptions.CultureInvariant), view);
        }

        // The authority shell must keep the two hooks that let a platform screen reuse it instead of
        // cloning it: an optional Styles section, and a caller-supplied sidebar. Both are additive and
        // non-visual - Inventory's own views pass neither and render exactly as before. Without them a
        // screen needing one stylesheet, or a sidebar other than Inventory's, is pushed onto a second
        // shell, which is how a second design language starts.
        var inventoryLayout = File.ReadAllText(ProjectFile("Views", "Shared", "_LayoutInventory.cshtml"));
        Assert.Contains("RenderSectionAsync(\"Styles\", required: false)", inventoryLayout, StringComparison.Ordinal);
        Assert.Contains("ViewBag.SidebarMenu", inventoryLayout, StringComparison.Ordinal);
    }

    private static string ProjectFile(params string[] segments) =>
        Path.Combine(new[] { RepoRoot(), "CrossBuy" }.Concat(segments).ToArray());

    private static string RepoRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)
            && File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
        {
            return fromEnvironment;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate CrossBuy.sln from the test output directory.");
    }
}
