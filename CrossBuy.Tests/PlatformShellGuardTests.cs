using Xunit;

namespace CrossBuy.Tests;

/// <summary>
/// GLOBAL UI RULE — the Inventory module is the single visual authority, and _LayoutInventory is the
/// single platform shell.
///
/// This guard exists because the rule is not self-enforcing. Twice already a screen arrived on its own
/// improvised shell: _LayoutWorkspace (212 lines) and _LayoutReporting (141 lines), against Inventory's
/// 4771. Both compiled, both passed every test, and both were invisible until someone opened the page.
/// A rule nothing checks is a preference.
///
/// What this asserts is deliberately narrow: WHICH SHELL a screen uses, and that no second one appears.
/// It does not assert markup, spacing or colour - those belong to the views and to the brand stylesheet.
///
/// OWNERSHIP: failures name the owning tab. A platform screen may be mid-edit by the tab that owns it,
/// and a red result here should send the reader to that tab, not to this file.
/// </summary>
public sealed class PlatformShellGuardTests
{
    private const string Shell = "_LayoutInventory.cshtml";

    /// <summary>
    /// Every user-facing platform screen and the tab accountable for it.
    /// Reporting is listed because the rule is global - but a failure here is TAB-2's to resolve.
    /// </summary>
    private static readonly (string Folder, string Tab)[] PlatformSurfaces =
    {
        ("Workspace",            "TAB-1"),
        ("BusinessEventMonitor", "TAB-1"),
        ("Reports",              "TAB-2"),
    };

    /// <summary>
    /// Every screen inside a platform surface, discovered from disk.
    ///
    /// A hardcoded list was the first version and it was wrong in the one way that matters: a screen
    /// added tomorrow would not be covered, and the guard would keep reporting green while the very
    /// thing it exists to prevent walked in beside it. Enumerating the folder means a NEW screen is
    /// protected the moment it is created, with no one having to remember this file.
    ///
    /// Adding a whole new platform SURFACE still takes one line above - deliberately. That is a real
    /// architectural act and should be visible in a diff, unlike adding a page to a surface that exists.
    ///
    /// Partials (_Name.cshtml) are excluded: they inherit the shell of whatever renders them and
    /// declare no Layout of their own.
    /// </summary>
    private static IEnumerable<(string View, string Tab)> PlatformScreens()
    {
        foreach (var (folder, tab) in PlatformSurfaces)
        {
            var dir = Path.Combine(ViewRoot(), folder);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cshtml").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                yield return (Path.Combine(folder, name), tab);
            }
        }
    }

    /// <summary>
    /// Layouts that legitimately exist. Every entry is a pre-existing module or special-purpose shell.
    /// A NEW name appearing here is exactly the "second design language" the rule forbids, so it must be
    /// added deliberately and justified - which is what makes this list worth maintaining by hand.
    /// </summary>
    private static readonly string[] ApprovedLayouts =
    {
        "_Layout.cshtml", "_LayoutAccounting.cshtml", "_LayoutBackend.cshtml", "_LayoutEmbed.cshtml",
        "_LayoutHyperPos.cshtml", "_LayoutInventory.cshtml", "_LayoutManufacturing.cshtml",
        "_LayoutPeople.cshtml", "_LayoutPos.cshtml", "_LayoutPosApp.cshtml",
        // Orphaned: referenced by no view. Kept in the list so its DELETION is a deliberate act rather
        // than something this guard forces, but Retired_shells_are_referenced_by_nothing keeps it dead.
        "_LayoutWorkspace.cshtml",
    };

    /// <summary>Shells that were replaced. Nothing may reference them again.</summary>
    private static readonly string[] RetiredShells = { "_LayoutWorkspace", "_LayoutReporting" };

    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_platform_screen_uses_the_inventory_shell()
    {
        var failures = new List<string>();

        foreach (var (view, tab) in PlatformScreens())
        {
            var path = ViewFile(view);
            if (!File.Exists(path))
            {
                // A deleted screen is not this guard's business - reachability tests cover that.
                continue;
            }

            var text = File.ReadAllText(path);
            if (!text.Contains(Shell, StringComparison.Ordinal))
            {
                var actual = ExtractLayout(text) ?? "<none>";
                failures.Add($"{view} uses {actual}, expected {Shell}  [owner: {tab}]");
            }
        }

        Assert.True(failures.Count == 0,
            "Platform screens must reuse the Inventory shell:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void Screen_discovery_actually_finds_the_known_platform_screens()
    {
        // A folder sweep that returns nothing passes every other test in this file vacuously. This is
        // the guard on the guard: if discovery silently stops matching, this fails rather than turning
        // the whole file into a no-op.
        var found = PlatformScreens().Select(s => s.View.Replace(Path.DirectorySeparatorChar, '/')).ToList();

        Assert.Contains("Workspace/Index.cshtml", found);
        Assert.Contains("Workspace/Agenda.cshtml", found);
        Assert.Contains("BusinessEventMonitor/Index.cshtml", found);
        Assert.Contains("Reports/Index.cshtml", found);
        Assert.True(found.Count >= 8, $"discovery found only {found.Count} platform screens: {string.Join(", ", found)}");
        Assert.DoesNotContain(found, f => Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal));
    }

    [Fact]
    public void Retired_shells_are_referenced_by_nothing()
    {
        var offenders = new List<string>();

        foreach (var view in Directory.EnumerateFiles(ViewRoot(), "*.cshtml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(view);
            if (RetiredShells.Contains(name, StringComparer.Ordinal))
            {
                continue;   // the retired file itself, if it still exists on disk
            }

            var text = File.ReadAllText(view);
            foreach (var retired in RetiredShells)
            {
                if (text.Contains(retired, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(ViewRoot(), view)} -> {retired}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A retired shell is referenced again:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_new_layout_is_introduced()
    {
        var onDisk = Directory
            .EnumerateFiles(Path.Combine(ViewRoot(), "Shared"), "_Layout*.cshtml")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var unexpected = onDisk.Where(n => !ApprovedLayouts.Contains(n, StringComparer.Ordinal)).ToList();

        Assert.True(unexpected.Count == 0,
            "A new layout appeared. One ERP, one visual language - reuse _LayoutInventory instead:\n  "
            + string.Join("\n  ", unexpected));
    }

    [Fact]
    public void The_inventory_shell_keeps_the_two_approved_reuse_hooks()
    {
        // Approved by the owner precisely so a platform screen can reuse this shell rather than clone it.
        // Both are non-visual: Inventory's own views pass neither and render exactly as before. Remove
        // either one and the next screen that needs a stylesheet, or a sidebar other than Inventory's, is
        // pushed onto a second shell - which is how the last two improvised layouts came to exist.
        var shell = File.ReadAllText(ViewFile(Path.Combine("Shared", Shell)));

        Assert.Contains("RenderSectionAsync(\"Styles\", required: false)", shell, StringComparison.Ordinal);
        Assert.Contains("ViewBag.SidebarMenu", shell, StringComparison.Ordinal);
        Assert.Contains("MainMenu.Inventory()", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_own_screens_still_resolve_their_own_menu()
    {
        // The hook must not have changed what Inventory itself renders. Its views pass no SidebarMenu, so
        // the ?? fallback has to remain MainMenu.Inventory() - otherwise the authority's own navigation
        // would have been altered by a change made for somebody else's benefit.
        var shell = File.ReadAllText(ViewFile(Path.Combine("Shared", Shell)));

        Assert.Contains("?? CrossBuy.Models.Menu.MainMenu.Inventory()", shell, StringComparison.Ordinal);

        var inventoryIndex = File.ReadAllText(ViewFile(Path.Combine("Inventory", "Index.cshtml")));
        Assert.Contains(Shell, inventoryIndex, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewBag.SidebarMenu", inventoryIndex, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------

    private static string? ExtractLayout(string viewText)
    {
        var i = viewText.IndexOf("_Layout", StringComparison.Ordinal);
        if (i < 0) return null;
        var end = viewText.IndexOf(".cshtml", i, StringComparison.Ordinal);
        return end < 0 ? null : viewText[i..(end + 7)];
    }

    private static string ViewRoot() => Path.Combine(RepoRoot(), "CrossBuy", "Views");

    private static string ViewFile(string relative) => Path.Combine(ViewRoot(), relative);

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
}
