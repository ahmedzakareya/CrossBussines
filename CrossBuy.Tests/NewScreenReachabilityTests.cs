using System.Reflection;
using System.Xml.Linq;
using CrossBuy.Models.Menu;
using Xunit;

namespace CrossBuy.Tests;

/// <summary>
/// Reachability invariants for the platform surfaces delivered in the Workspace/Reporting increments.
///
/// These exist because the screens were built, compiled, and were still unreachable: a bare "/Workspace"
/// returned an empty 404 because the default route defaults the ACTION to "Login", and no menu anywhere
/// linked to the new screens. Both defects passed every build and every existing test.
///
/// What is asserted here is what a build CAN prove: the navigation entries exist exactly once, they point
/// at actions that really exist, they are permission-aware, and their labels are localized. Live HTTP
/// status codes are verified separately and recorded in the reachability evidence document - a unit test
/// cannot prove a route resolves in a running pipeline.
/// </summary>
public class NewScreenReachabilityTests
{
    private static readonly Assembly App = typeof(CrossBuy.Models.Menu.MainMenu).Assembly;

    private static List<MenuItem> AllMenuItems()
    {
        var items = new List<MenuItem>();
        foreach (var m in typeof(MainMenu).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.ReturnType != typeof(List<MenuCategory>) || m.GetParameters().Length != 0) continue;
            var cats = (List<MenuCategory>)m.Invoke(null, null)!;
            items.AddRange(cats.SelectMany(c => c.Items));
        }
        return items;
    }

    private static bool ActionExists(string controller, string action)
    {
        var type = App.GetTypes().FirstOrDefault(t =>
            t.Name == controller + "Controller" &&
            typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t));
        if (type == null) return false;
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                   .Any(m => m.Name == action);
    }

    // ------------------------------------------------------------------ navigation exists

    [Theory]
    [InlineData("Workspace", "Index")]
    [InlineData("Reports", "Index")]
    [InlineData("BusinessEventMonitor", "Index")]
    public void The_platform_menu_links_to_the_new_screens(string controller, string action)
    {
        // The BARE entry - the one with no arguments. An action can now carry several entries that differ
        // only by route value (Reports/Index and Reports/Index?favorites=true are two screens to the owner),
        // so "the entry for this screen" means the unfiltered one.
        var hit = MainMenu.Platform().SelectMany(c => c.Items)
            .SingleOrDefault(i => i.Controller == controller && i.Action == action
                                  && (i.RouteValues is null || i.RouteValues.Count == 0));

        Assert.NotNull(hit);
        Assert.False(hit!.Soon, $"{controller} is delivered, not a coming-soon placeholder.");
    }

    [Theory]
    [InlineData("Workspace")]
    [InlineData("Reports")]
    [InlineData("BusinessEventMonitor")]
    public void A_navigation_entry_appears_exactly_once_across_every_menu(string controller)
    {
        // The Business Event Monitor was previously defined in Admin(). It was MOVED to Platform(), not
        // copied - if anyone re-adds it, this fails rather than producing two identical sidebar links.
        //
        // Counted WITHOUT arguments: what must not be duplicated is the destination, and a filtered view of
        // the same action (Reports?favorites=true) is a different destination, not a second copy of this one.
        // Counting bare Action alone would call the favourites row a duplicate and force it to be deleted -
        // the guard would be removing working navigation to protect a rule it had misread.
        var count = AllMenuItems().Count(i => i.Controller == controller && i.Action == "Index"
                                              && (i.RouteValues is null || i.RouteValues.Count == 0));
        Assert.Equal(1, count);
    }

    [Fact]
    public void Every_platform_menu_item_points_at_an_action_that_exists()
    {
        // Guards the failure this whole increment exists to prevent: a menu entry that 404s.
        foreach (var item in MainMenu.Platform().SelectMany(c => c.Items))
        {
            Assert.True(ActionExists(item.Controller, item.Action),
                $"menu links to {item.Controller}Controller.{item.Action}, which does not exist");

            foreach (var alias in item.Aliases)
            {
                Assert.True(ActionExists(item.Controller, alias),
                    $"alias {item.Controller}Controller.{alias} does not exist");
            }
        }
    }

    // ------------------------------------------------------------------ permission awareness

    [Fact]
    public void The_business_event_monitor_link_is_permission_aware()
    {
        // [PlatformOps] guards the screen. Without a Perm hook the link renders for everyone and then
        // redirects - which is how an owner concludes a screen is broken when it is merely forbidden.
        var item = MainMenu.Platform().SelectMany(c => c.Items)
            .Single(i => i.Controller == "BusinessEventMonitor");

        Assert.Equal("platform-ops", item.Perm);
    }

    [Fact]
    public void Workspace_and_reports_center_are_open_to_any_signed_in_employee()
    {
        // Both controllers carry only [SessionValidation]. A Perm hook here would hide a screen the user
        // is entitled to open, which is the mirror-image defect of showing one they cannot.
        // Single() was correct when Workspace had one entry. The Platform section now lists its four
        // surfaces (Agenda, Notifications, Mentions, My reports) as entries of their own, so the
        // assertion moves from "the one item" to "every item" - the intent is unchanged and now covers
        // more ground: not one of these screens may acquire a Perm hook, because their controllers
        // carry only [SessionValidation].
        foreach (var c in new[] { "Workspace", "Reports" })
        {
            var items = MainMenu.Platform().SelectMany(x => x.Items)
                .Where(i => i.Controller == c).ToList();

            Assert.NotEmpty(items);
            foreach (var item in items)
            {
                // THE RULE, stated precisely: a row may not be hidden more tightly than the screen it opens.
                //
                // A row that names ONE report is the exception, and not a loophole. The Viewer is open to any
                // signed-in employee, but a SPECIFIC report is a separate, fail-closed decision - an unmapped
                // or unheld permission key makes the Viewer answer 404 rather than reveal that the report
                // exists. So "Perm = report" does not hide something the user could open; it hides the one
                // row whose only possible outcome for this user is a 404. Any OTHER hook here would.
                if (item.RouteValues is not null && item.RouteValues.ContainsKey("id"))
                {
                    Assert.Equal("report", item.Perm);
                    continue;
                }

                Assert.Null(item.Perm);
            }
        }
    }

    [Fact]
    public void The_platform_ops_predicate_is_reachable_from_a_view()
    {
        // _MainMenu.cshtml calls PlatformOpsAttribute.IsAdmin so the nav predicate is the SAME one the
        // filter applies. If this stops being public, navigation silently falls back to "always visible".
        var m = typeof(CrossBuy.Models.PlatformOpsAttribute)
            .GetMethod("IsAdmin", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(m);
        Assert.Equal(typeof(bool), m!.ReturnType);
    }

    // ------------------------------------------------------------------ localization

    [Theory]
    [InlineData("Platform")]
    [InlineData("Workspace")]
    [InlineData("Reports center")]
    [InlineData("Business event monitor")]
    public void Menu_labels_have_an_arabic_resource(string key)
    {
        // The partial renders @SR[LabelEn]; a missing key silently renders the English text inside an
        // Arabic-first RTL layout, which reads as a bug to the owner rather than as a missing translation.
        var path = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "SharedResources.ar.resx");
        Assert.True(File.Exists(path), $"resx not found: {path}");

        var names = XDocument.Load(path).Root!.Elements("data")
            .Select(e => (string?)e.Attribute("name")).ToHashSet();

        Assert.Contains(key, names!);
    }

    [Fact]
    public void Every_platform_menu_item_carries_both_labels()
    {
        foreach (var item in MainMenu.Platform().SelectMany(c => c.Items))
        {
            Assert.False(string.IsNullOrWhiteSpace(item.LabelAr), $"{item.Controller} has no Arabic label");
            Assert.False(string.IsNullOrWhiteSpace(item.LabelEn), $"{item.Controller} has no English label");
        }
    }

    // ------------------------------------------------------------------ the report code the owner needs

    [Fact]
    public void The_documented_business_events_report_code_is_the_one_in_source()
    {
        // The owner-review guide hands out this exact string. If the constant changes, the documented URL
        // becomes a 404 and the owner is left guessing again - which is the complaint that started this.
        Assert.Equal("Platform.BusinessEventLog",
            CrossBuy.BL.Reporting.BusinessEventsReportCodes.ReportCode);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
