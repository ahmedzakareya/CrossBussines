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
///   2. The opening screen, /Portal/Choose — the page login redirects to, and the destination of
///      the quick-switch's own "All systems" row.
///
/// SINCE 2026-11-02 BOTH READ ONE LIST, <see cref="SystemCatalog"/>: the switcher renders
/// SystemCatalog.Sidebar and the opening screen renders SystemCatalog.All (less what the user may not
/// open). The gap below can no longer open by forgetting one surface; these tests pin that both surfaces
/// still read the catalogue, and what the catalogue must contain.
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
        new object[] { "Chat",      "Index" },   // on the opening screen since it was rebuilt on SystemCatalog
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
    private static string PortalControllerSource() => File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "PortalController.cs"));

    private static bool InCatalog(IEnumerable<SystemEntry> list, string controller, string action) =>
        list.Any(e => e.Controller == controller && e.Action == action);

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

        // The switcher renders the catalogue's sidebar list, and that list holds the system.
        Assert.Contains("SystemCatalog.Sidebar", switcher, StringComparison.Ordinal);
        Assert.True(InCatalog(SystemCatalog.Sidebar, controller, action), $"{controller}/{action} is not in SystemCatalog.Sidebar");
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
    // 1b. The Communication Hub row - the one gated entry in the switcher
    // ---------------------------------------------------------------------------------------------

    /// THE GAP THIS CLOSES. /Chat was built, routed and reachable, and listed on no navigation
    /// surface at all: the only way in was to already know the URL. That is the same defect this file
    /// was written for, one module further on.
    ///
    /// The stated gap — the hub in the switcher but not on the portal — closed when the opening screen
    /// was rebuilt on SystemCatalog: Chat is now in <see cref="EnterableSystems"/>.
    [Fact]
    public void The_communication_hub_is_listed_in_the_systems_quick_switch()
    {
        Assert.True(InCatalog(SystemCatalog.Sidebar, "Chat", "Index"), "Chat/Index is not in SystemCatalog.Sidebar");
    }

    /// Same rule the Tasks and Calendar rows are held to: a row that never lights up cannot tell you
    /// where you are, and /chat does not fall out of the generic URL-segment branch.
    [Fact]
    public void The_communication_hub_switcher_row_has_an_explicit_active_rule()
    {
        var partial = MainMenuPartial();

        Assert.Matches(
            new Regex(@"key\s*==\s*""chat""\s*\)\s*return\s+path\.StartsWith\(\s*""/chat""\s*\)",
                RegexOptions.CultureInvariant),
            partial);
    }

    /// THE PERMISSION QUESTION, PINNED TO THE ONE THE SCREEN ASKS.
    ///
    /// ChatController carries no module permission. Its gate is [SessionValidation] plus MeAsync(),
    /// which refuses unless the resolved business context names both a company and an employee. A menu
    /// predicate invented for the row - some "chat-view" key nothing enforces - would hide the hub from
    /// people who can open it and show it to people who cannot, which is the drift this file already
    /// records for platform-ops.
    ///
    /// So this pins three things: the row IS gated, the gate resolves the business context, and it tests
    /// EmployeeId - the field that distinguishes an employee from an external portal visitor.
    [Fact]
    public void The_communication_hub_row_is_gated_on_the_same_context_the_controller_resolves()
    {
        var partial = MainMenuPartial();

        Assert.Matches(
            new Regex(@"s\.key\s*==\s*""chat""\s*&&\s*!canChat\s*\)\s*\{\s*continue;",
                RegexOptions.CultureInvariant),
            partial);

        var predicate = Regex.Match(partial,
            @"bool canChat = false;(?<body>.*?)catch \{ canChat = false; \}",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(predicate.Success, "canChat is not computed in _MainMenu.cshtml");
        Assert.Contains("IBusinessContextAccessor", predicate.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("EmployeeId", predicate.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// The hub must reach the screen that exists. A row pointing at a controller or action the product
    /// does not have is the failure mode UrlActionFor cannot see, because Url.Action returns "#".
    [Fact]
    public void The_communication_hub_row_points_at_a_controller_action_that_exists()
    {
        var controller = typeof(CrossBuy.Controllers.ChatController);
        var index = controller.GetMethod("Index");

        Assert.NotNull(index);
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "CrossBuy", "Views", "Chat", "Index.cshtml")),
            "Views/Chat/Index.cshtml is missing, so the switcher row would resolve to a view that does not exist");
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
        Assert.True(InCatalog(SystemCatalog.All, controller, action),
            $"SystemCatalog.All offers no way into {controller}. The opening screen is where sign-in lands " +
            "and where the 'All systems' row goes, so a system missing from it can only be reached by " +
            "entering a different system first and borrowing its sidebar.");
        // ...and the opening screen really renders the catalogue, not a list of its own.
        Assert.Contains("SystemCatalog.All", PortalControllerSource(), StringComparison.Ordinal);
        Assert.Contains("Model.Systems", PortalChoose(), StringComparison.Ordinal);
    }

    /// Every system the catalogue offers must open a screen that exists: a Url.Action to a missing action
    /// renders "#" and the tile goes nowhere.
    [Fact]
    public void Every_catalogue_system_points_at_a_controller_action_that_exists()
    {
        var asm = typeof(CrossBuy.Controllers.PortalController).Assembly;
        var missing = SystemCatalog.All.Where(e =>
        {
            var type = asm.GetTypes().FirstOrDefault(t => t.Name == e.Controller + "Controller");
            return type == null || type.GetMethods().All(m => !string.Equals(m.Name, e.Action, StringComparison.Ordinal));
        }).Select(e => $"{e.Key}: {e.Controller}/{e.Action}").ToList();
        Assert.True(missing.Count == 0, "Catalogue systems pointing nowhere:\n  " + string.Join("\n  ", missing));
    }

    /// The portal draws every system with its ONE tile component. A system added with bespoke markup would
    /// be a second navigation language on the one page the whole product funnels through.
    [Fact]
    public void Every_system_tile_on_the_portal_is_the_pages_one_tile_component()
    {
        var choose = PortalChoose();

        Assert.Single(Regex.Matches(choose, @"class=""lx-app""", RegexOptions.CultureInvariant));
        Assert.Single(Regex.Matches(choose, @"function app\(s\)", RegexOptions.CultureInvariant));
        Assert.DoesNotContain("msys", choose, StringComparison.Ordinal);   // the old hand-written tiles are gone
    }

    /// Two tiles pointing at one system is a menu telling the user two things.
    [Fact]
    public void No_portal_tile_destination_is_listed_twice()
    {
        var destinations = SystemCatalog.All.Select(e => e.Controller + "/" + e.Action).ToList();

        Assert.NotEmpty(destinations);
        Assert.Equal(SystemCatalog.All.Count, SystemCatalog.All.Select(e => e.Key).Distinct().Count());

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
        Assert.True(SystemCatalog.All.Count >= 13, "the catalogue holds too few systems to be real");
        Assert.Equal(13, SystemCatalog.Sidebar.Count());
        Assert.Equal(8, MainMenu.Tasks().SelectMany(c => c.Items).Count());
        Assert.Equal(3, MainMenu.Calendar().SelectMany(c => c.Items).Count());
    }
}
