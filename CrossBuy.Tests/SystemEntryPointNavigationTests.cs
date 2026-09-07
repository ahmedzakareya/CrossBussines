using System.Text.RegularExpressions;
using CrossBuy.Models.Menu;
using Xunit;

namespace CrossBuy.Tests;

/// <summary>
/// THE WAY IN TO A MODULE — the two navigation surfaces that are hand-written Razor rather than
/// <see cref="MainMenu"/> entries, and which therefore no existing guard covers.
///
///   1. The Systems quick-switch, `sysItems` in Views/Shared/_MainMenu.cshtml. Rendered inside every
///      sidebar, so it is how you cross from one module to another.
///   2. The tiles on Views/Portal/Choose.cshtml — the page login redirects to, and the destination of
///      the quick-switch's own "All systems" row.
///
/// WHY THIS FILE EXISTS. NavigationGuardTests proves every MainMenu row points somewhere real, and it
/// proved the Tasks and Calendar SIDEBARS were complete and correct the whole time. None of that says
/// anything about how a user reaches those sidebars in the first place — both surfaces above are string
/// literals in .cshtml, invisible to a sweep over MainMenu.
///
/// The gap that motivated it: Calendar was listed in the quick-switch but NOT on Portal/Choose. A user
/// who landed on the portal after signing in — the default landing page — could not reach Calendar at
/// all without first entering some other system to borrow its sidebar. Timeline and Resource View were
/// built, routable, permission-correct, and undiscoverable from the front door.
/// </summary>
public sealed class SystemEntryPointNavigationTests
{
    /// A system the product expects a user to be able to ENTER from the front door. Each must appear in
    /// both surfaces. Adding a module here is the deliberate act of saying "this is a system, not a
    /// screen" — which is exactly the decision that was made for Calendar and then half-applied.
    public static IEnumerable<object[]> EnterableSystems() => new List<object[]>
    {
        new object[] { "Tasks",     "Index" },
        new object[] { "Calendar",  "Index" },
        new object[] { "Inventory", "Index" },
        new object[] { "Crm",       "Index" },
    };

    private static string RepoRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)
            && File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
        {
            return fromEnvironment;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string View(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Views", Path.Combine(parts)));

    private static string MainMenuPartial() => View("Shared", "_MainMenu.cshtml");
    private static string PortalChoose() => View("Portal", "Choose.cshtml");

    /// `Url.Action("Index","Calendar")` with any spacing/quoting the file happens to use.
    private static Regex UrlActionFor(string controller, string action) =>
        new(@"Url\.Action\(\s*""" + Regex.Escape(action) + @"""\s*,\s*""" + Regex.Escape(controller) + @"""\s*\)",
            RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------------------------------------
    // 1. The Systems quick-switch
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EnterableSystems))]
    public void Every_enterable_system_is_listed_in_the_systems_quick_switch(string controller, string action)
    {
        var partial = MainMenuPartial();
        var switcher = partial[partial.IndexOf("var sysItems", StringComparison.Ordinal)..];

        Assert.Matches(UrlActionFor(controller, action), switcher);
    }

    /// A row in the switcher that never lights up is a row that cannot tell you where you are. Each
    /// system needs a branch in SysActive, or it falls to the generic path — which is fine only if the
    /// URL segment matches. Tasks and Calendar both have explicit branches; this pins them.
    [Theory]
    [InlineData("tasks", "/tasks")]
    [InlineData("cal", "/calendar")]
    public void The_tasks_and_calendar_switcher_rows_have_an_explicit_active_rule(string key, string segment)
    {
        var partial = MainMenuPartial();

        Assert.Matches(
            new Regex(@"key\s*==\s*""" + Regex.Escape(key) + @"""\s*\)\s*return\s+path\.StartsWith\(\s*""" +
                      Regex.Escape(segment) + @"""\s*\)", RegexOptions.CultureInvariant),
            partial);
    }

    // ---------------------------------------------------------------------------------------------
    // 2. The portal — the page a user actually lands on
    // ---------------------------------------------------------------------------------------------

    /// THE REGRESSION. Before the fix this failed for Calendar and passed for Tasks, which is exactly
    /// the shape of the defect: one of a pair of sibling systems listed, the other silently omitted.
    [Theory]
    [MemberData(nameof(EnterableSystems))]
    public void Every_enterable_system_can_be_entered_from_the_portal(string controller, string action)
    {
        var choose = PortalChoose();
        var link = UrlActionFor(controller, action);

        Assert.True(link.IsMatch(choose),
            $"Views/Portal/Choose.cshtml offers no way into {controller}. That page is where sign-in lands " +
            "and where the 'All systems' row goes, so a system missing from it can only be reached by " +
            "entering a different system first and borrowing its sidebar.");
    }

    /// The portal must reuse the page's own tile component. A system added with bespoke markup would be
    /// a second navigation language on the one page the whole product funnels through.
    [Theory]
    [InlineData("Tasks")]
    [InlineData("Calendar")]
    public void A_system_tile_on_the_portal_uses_the_pages_existing_tile_component(string controller)
    {
        var choose = PortalChoose();

        var tile = Regex.Match(choose,
            @"<a class=""msys""[^>]*href=""@Url\.Action\(""Index"",""" + Regex.Escape(controller) + @"""\)""" +
            @"[^>]*>(?<body>.*?)</a>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(tile.Success, $"{controller} is not rendered as an .msys tile on the portal");
        Assert.Contains("msys-ic", tile.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("msys-t", tile.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("msys-d", tile.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// Two tiles pointing at one system is a menu telling the user two things.
    [Fact]
    public void No_portal_tile_destination_is_listed_twice()
    {
        var choose = PortalChoose();

        // The href is a Razor expression that CONTAINS quotes — href="@Url.Action("Index","Tasks")" —
        // so it cannot be captured as "everything up to the next quote": that yields `Url.Action(` for
        // every tile and reports six identical destinations. Capture the call itself.
        var destinations = Regex.Matches(choose,
                @"<a class=""msys""[^>]*href=""@(?<href>Url\.Action\([^)]*\))""",
                RegexOptions.CultureInvariant)
            .Select(m => m.Groups["href"].Value.Trim())
            .ToList();

        Assert.NotEmpty(destinations);

        var duplicates = destinations.GroupBy(d => d, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} appears {g.Count()} times")
            .ToList();

        Assert.True(duplicates.Count == 0,
            "The portal lists one destination more than once:\n  " + string.Join("\n  ", duplicates));
    }

    // ---------------------------------------------------------------------------------------------
    // 3. The two module sidebars, exactly as the owner specified them
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_tasks_sidebar_is_the_agreed_tree()
    {
        var actual = MainMenu.Tasks()
            .Select(c => c.LabelEn + ": " + string.Join(", ", c.Items.Select(i => i.LabelEn)))
            .ToArray();

        Assert.Equal(new[]
        {
            "Tasks: My tasks, All tasks, Board",
            "Reports: Reports dashboard, Hours report",
            "Setup: Auto-task rules, Match suggestions, Task templates",
        }, actual);
    }

    [Fact]
    public void The_calendar_sidebar_is_the_agreed_tree()
    {
        var actual = MainMenu.Calendar()
            .Select(c => c.LabelEn + ": " + string.Join(", ", c.Items.Select(i => i.LabelEn)))
            .ToArray();

        Assert.Equal(new[] { "Calendar: Calendar, Timeline, Resource view" }, actual);
    }

    /// Every row carries BOTH labels. An empty LabelAr renders as an empty menu row in the Arabic UI —
    /// which is the product's default culture.
    [Fact]
    public void Every_tasks_and_calendar_menu_row_is_labelled_in_both_languages()
    {
        var missing = MainMenu.Tasks().Concat(MainMenu.Calendar())
            .SelectMany(c => c.Items.Select(i => (Cat: c, Item: i)))
            .Where(x => string.IsNullOrWhiteSpace(x.Item.LabelAr) || string.IsNullOrWhiteSpace(x.Item.LabelEn))
            .Select(x => $"{x.Cat.LabelEn} > {x.Item.LabelEn}/{x.Item.LabelAr}")
            .Concat(MainMenu.Tasks().Concat(MainMenu.Calendar())
                .Where(c => string.IsNullOrWhiteSpace(c.LabelAr) || string.IsNullOrWhiteSpace(c.LabelEn))
                .Select(c => $"category {c.LabelEn}/{c.LabelAr}"))
            .ToList();

        Assert.True(missing.Count == 0, "Menu rows missing a label:\n  " + string.Join("\n  ", missing));
    }

    // ---------------------------------------------------------------------------------------------
    // 4. What must NOT appear
    // ---------------------------------------------------------------------------------------------

    /// Task Detail is contextual — it needs a task id, so a standalone menu row could only ever open it
    /// onto nothing. It is reached by clicking a task, and it is registered as an ALIAS of Board so the
    /// Board row stays lit while a task is open.
    [Fact]
    public void Task_detail_is_an_alias_and_never_a_menu_row_of_its_own()
    {
        var rows = MainMenu.Tasks().SelectMany(c => c.Items).ToList();

        Assert.DoesNotContain(rows, i => i.Controller == "Tasks" && i.Action == "Detail");
        Assert.Contains(rows, i => i.Controller == "Tasks"
                                   && i.Aliases.Contains("Detail", StringComparer.Ordinal));
    }

    /// Panels, tabs, dialogs and JSON endpoints are not screens. A row for one is a click that cannot
    /// keep its promise — it either 404s or silently opens the parent screen.
    [Theory]
    [InlineData("Dependencies")]
    [InlineData("DependencyCandidates")]
    [InlineData("Checklist")]
    [InlineData("ActivityHistory")]
    [InlineData("Availability")]
    [InlineData("FreeSlots")]
    [InlineData("Recurrence")]
    [InlineData("Schedule")]
    [InlineData("Conflicts")]
    [InlineData("TaskComments")]
    public void No_menu_row_is_invented_for_a_panel_or_a_json_endpoint(string action)
    {
        var rows = MainMenu.Tasks().Concat(MainMenu.Calendar()).SelectMany(c => c.Items);

        Assert.DoesNotContain(rows, i => string.Equals(i.Action, action, StringComparison.Ordinal));
    }

    /// Guards the guards: an empty parse would pass every assertion above.
    [Fact]
    public void Entry_point_discovery_is_not_vacuous()
    {
        Assert.Contains("var sysItems", MainMenuPartial(), StringComparison.Ordinal);
        Assert.True(Regex.Matches(PortalChoose(), @"<a class=""msys""").Count >= 5,
            "the portal tile parse found too few tiles to be real");
        Assert.Equal(8, MainMenu.Tasks().SelectMany(c => c.Items).Count());
        Assert.Equal(3, MainMenu.Calendar().SelectMany(c => c.Items).Count());
    }
}
