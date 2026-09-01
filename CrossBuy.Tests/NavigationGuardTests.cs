using System.Reflection;
using CrossBuy.Models.Menu;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CrossBuy.Tests;

/// <summary>
/// Navigation invariants for the platform surfaces.
///
/// Two failures motivated this file, and neither was caught by anything that existed:
///
///   1. Screens were built, compiled and tested, and NOTHING anywhere linked to them. A grep across
///      327 views found no link to /Workspace or /Reports outside the Reporting views themselves.
///   2. A bare /Workspace returned an empty 404, because the default route defaults the ACTION to
///      "Login". The menu would have pointed at a dead URL.
///
/// So the two halves are asserted separately: a screen must be REACHABLE from navigation, and every
/// navigation entry must point at something that exists. Either one alone passes while the product is
/// unusable.
///
/// What a unit test cannot do is prove a route resolves in a running pipeline - only a real request
/// does that, and it is recorded in the integration report. What it CAN prove is that the controller
/// and action named by a menu entry are real, which is where dead links actually come from.
/// </summary>
public sealed class NavigationGuardTests
{
    private static readonly Assembly App = typeof(MainMenu).Assembly;

    /// <summary>
    /// Platform surfaces and the tab accountable for each. Mirrors PlatformShellGuardTests: a screen
    /// added to one of these folders is picked up automatically and must be navigable.
    /// </summary>
    private static readonly (string Folder, string Controller, string Tab)[] Surfaces =
    {
        ("Workspace",            "Workspace",            "TAB-1"),
        ("BusinessEventMonitor", "BusinessEventMonitor", "TAB-1"),
        ("Reports",              "Reports",              "TAB-2"),
    };

    // ---------------------------------------------------------------------------------------------
    // Every registered platform screen must appear in navigation.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_platform_screen_is_reachable_from_navigation()
    {
        var platformItems = MainMenu.Platform().SelectMany(c => c.Items).ToList();
        var unreachable = new List<string>();

        foreach (var (folder, controller, tab) in Surfaces)
        {
            var dir = Path.Combine(ViewRoot(), folder);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cshtml"))
            {
                var view = Path.GetFileNameWithoutExtension(file);
                if (view.StartsWith("_", StringComparison.Ordinal)) continue;   // partial

                // Reachable means EITHER a menu entry of its own, OR an alias of one. An alias is the
                // established pattern for a drill-down (Reports/Viewer is opened from a card, not from
                // the sidebar) - Inventory uses exactly this for its own report drill-downs. Demanding
                // a menu entry per screen would force sidebar clutter that Inventory itself avoids.
                var isEntry = platformItems.Any(i =>
                    i.Controller == controller && i.Action == view);

                var isAlias = platformItems.Any(i =>
                    i.Controller == controller && i.Aliases.Contains(view, StringComparer.Ordinal));

                if (!isEntry && !isAlias)
                {
                    unreachable.Add($"{folder}/{view}.cshtml has no menu entry and is no menu item's alias  [owner: {tab}]");
                }
            }
        }

        Assert.True(unreachable.Count == 0,
            "A delivered platform screen cannot be reached from navigation:\n  "
            + string.Join("\n  ", unreachable));
    }

    // ---------------------------------------------------------------------------------------------
    // Every navigation item must resolve. No dead routes.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_platform_menu_entry_points_at_a_real_action()
    {
        var dead = new List<string>();

        foreach (var item in MainMenu.Platform().SelectMany(c => c.Items))
        {
            if (!ActionExists(item.Controller, item.Action))
            {
                dead.Add($"{item.LabelEn}: {item.Controller}Controller.{item.Action} does not exist");
            }

            foreach (var alias in item.Aliases)
            {
                if (!ActionExists(item.Controller, alias))
                {
                    dead.Add($"{item.LabelEn}: alias {item.Controller}Controller.{alias} does not exist");
                }
            }
        }

        Assert.True(dead.Count == 0, "Dead navigation route:\n  " + string.Join("\n  ", dead));
    }

    [Fact]
    public void Every_menu_entry_in_the_whole_product_points_at_a_real_action()
    {
        // Widened past the platform on purpose. A dead link in any module is the same defect, and the
        // sweep costs nothing. Placeholders marked Soon are excluded - they navigate to a fallback by
        // design and the model says so.
        var dead = new List<string>();

        foreach (var (menu, item) in AllMenuItems())
        {
            if (item.Soon) continue;
            if (!ActionExists(item.Controller, item.Action))
            {
                dead.Add($"{menu}: {item.LabelEn} -> {item.Controller}Controller.{item.Action}");
            }
        }

        Assert.True(dead.Count == 0, "Dead navigation route:\n  " + string.Join("\n  ", dead));
    }

    [Fact]
    public void A_menu_entry_never_points_at_a_non_GET_action()
    {
        // A sidebar link issues a GET. Pointing one at a [HttpPost] action produces a 404 or a 405 that
        // reads to the user exactly like a broken screen.
        var wrong = new List<string>();

        foreach (var (menu, item) in AllMenuItems())
        {
            var method = FindAction(item.Controller, item.Action);
            if (method is null) continue;   // covered by the dead-route test

            var postOnly = method.GetCustomAttributes(inherit: true)
                .Any(a => a is HttpPostAttribute or HttpDeleteAttribute or HttpPutAttribute);

            if (postOnly)
            {
                wrong.Add($"{menu}: {item.LabelEn} -> {item.Controller}.{item.Action} is not GET-reachable");
            }
        }

        Assert.True(wrong.Count == 0, "Menu entry points at a non-GET action:\n  " + string.Join("\n  ", wrong));
    }

    // ---------------------------------------------------------------------------------------------
    // Structure
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void No_menu_lists_the_same_destination_twice()
    {
        // WITHIN one menu, not across menus.
        //
        // The first version grouped every destination across all menus and reported five "duplicates" -
        // Inventory.Items, Inventory.Units, Inventory.NewAssembly, Accounting.PurchaseInvoices,
        // Accounting.SalesInvoices. Every one turned out to be a shared screen listed in two DIFFERENT
        // module menus (Items appears under Inventory and under Manufacturing), which is deliberate:
        // Manufacturing needs the item list too, and only one menu is ever rendered at a time.
        //
        // The defect that actually harms a user is the same destination twice in the SAME sidebar -
        // two identical links that highlight together. That is what this asserts, and it holds across
        // the whole product because it is a real invariant rather than an assumption about sharing.
        var duplicates = new List<string>();

        foreach (var group in AllMenuItems().GroupBy(x => x.Menu, StringComparer.Ordinal))
        {
            duplicates.AddRange(group
                .Select(x => Destination(x.Item))
                .GroupBy(k => k, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{group.Key}: {g.Key} listed {g.Count()} times in one menu"));
        }

        Assert.True(duplicates.Count == 0,
            "One sidebar would show the same destination twice:\n  " + string.Join("\n  ", duplicates));
    }

    [Fact]
    public void A_screen_is_never_both_an_entry_and_another_entrys_alias()
    {
        // This is a real defect that was introduced and caught: when the four Workspace surfaces were
        // promoted to menu entries they were still listed as aliases of the dashboard, so BOTH lit up
        // on the same URL. An alias exists for a screen with NO entry of its own.
        var items = MainMenu.Platform().SelectMany(c => c.Items).ToList();
        var clashes = new List<string>();

        foreach (var item in items)
        {
            foreach (var alias in item.Aliases)
            {
                // An entry carrying ROUTE VALUES is a different destination from the bare action, so it does
                // not clash with an alias of that action: /Reports/Viewer/Platform.BusinessEventLog is one
                // screen, and "any other report opened from a card" is what the alias still covers. The
                // partial view resolves this at render time - the exact-argument row claims the highlight and
                // the hub stands down - so only an entry with NO arguments is a genuine clash.
                if (items.Any(other => other.Controller == item.Controller
                                       && other.Action == alias
                                       && (other.RouteValues is null || other.RouteValues.Count == 0)))
                {
                    clashes.Add($"{item.Controller}.{alias} is both a menu entry and an alias of {item.LabelEn}");
                }
            }
        }

        Assert.True(clashes.Count == 0,
            "Two menu items would highlight on one URL:\n  " + string.Join("\n  ", clashes));
    }

    // ---------------------------------------------------------------------------------------------
    // Route values - the arguments that make one action serve two menu entries
    // ---------------------------------------------------------------------------------------------

    /// A route value that names no parameter is a link that quietly drops its argument: the row would open
    /// the unfiltered screen and look like it worked. Each declared key must be a real parameter of the
    /// action, or the route's own {id}.
    [Fact]
    public void Every_route_value_names_a_real_parameter_of_its_action()
    {
        var broken = new List<string>();

        foreach (var (menu, item) in AllMenuItems())
        {
            if (item.RouteValues is null || item.RouteValues.Count == 0) continue;

            var method = FindAction(item.Controller, item.Action);
            if (method is null) continue;   // covered by the dead-route test

            var parameters = method.GetParameters().Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var key in item.RouteValues.Keys)
            {
                if (!parameters.Contains(key))
                {
                    broken.Add($"{menu}: {item.LabelEn} passes '{key}' to {item.Controller}.{item.Action}, " +
                               $"which takes ({string.Join(", ", parameters)})");
                }
            }
        }

        Assert.True(broken.Count == 0,
            "A menu entry passes an argument the action does not accept:\n  " + string.Join("\n  ", broken));
    }

    /// The Business event report entry names a report by code. A code nothing registers is a fake menu entry -
    /// it opens the viewer onto a report that does not exist.
    [Fact]
    public void A_report_named_by_a_menu_entry_is_a_registered_report_code()
    {
        var reportEntries = AllMenuItems()
            .Where(x => x.Item.Controller == "Reports"
                        && x.Item.RouteValues is not null
                        && x.Item.RouteValues.ContainsKey("id"))
            .ToList();

        Assert.NotEmpty(reportEntries);   // the sweep must not pass by finding nothing

        var declaredCodes = App.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.Contains("ReportCode", StringComparison.Ordinal))
            .Select(f => (string?)f.GetRawConstantValue())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        foreach (var (menu, item) in reportEntries)
        {
            var code = item.RouteValues!["id"];
            Assert.True(declaredCodes.Contains(code),
                $"{menu}: {item.LabelEn} opens report '{code}', which no ReportCode constant declares. " +
                "The entry would open the viewer onto nothing.");
        }
    }

    /// The Business event report row is gated by Perm "report", which asks the Reporting module the same
    /// question the Viewer will ask. A headless session carries no role claims, so the row is correctly
    /// hidden there and the live run cannot show it opening - this asserts the decision the row depends on:
    /// an administrator is granted, an unroled caller is refused. Without it, "the row is hidden" would be
    /// indistinguishable from "the row is broken".
    [Fact]
    public void The_business_event_report_is_granted_to_an_administrator_and_refused_to_everyone_else()
    {
        var options = new CrossBuy.BL.Reporting.ReportPermissionOptions();
        options.RoleMap[CrossBuy.BL.Reporting.BusinessEventsReportPermissions.View] =
            new[] { "Admin", "SuperAdmin", "Auditor" };   // the mapping Program.cs declares

        var evaluator = new CrossBuy.BL.Reporting.RoleMapReportPermissionEvaluator(options);

        var admin = new CrossBuy.Models.Platform.BusinessContext
        {
            CompanyId = 1, EmployeeId = 1, UserId = "u", Roles = new[] { "Admin" },
        };
        var nobody = new CrossBuy.Models.Platform.BusinessContext
        {
            CompanyId = 1, EmployeeId = 1, UserId = "u", Roles = Array.Empty<string>(),
        };

        Assert.True(evaluator.HasPermissionAsync(
            CrossBuy.BL.Reporting.BusinessEventsReportPermissions.View, admin).GetAwaiter().GetResult(),
            "an administrator cannot open the Business event report, so its menu row would never appear");

        Assert.False(evaluator.HasPermissionAsync(
            CrossBuy.BL.Reporting.BusinessEventsReportPermissions.View, nobody).GetAwaiter().GetResult(),
            "an unroled caller is granted an audit report - the row would appear for everyone");
    }

    // ---------------------------------------------------------------------------------------------
    // Orphan screens - built, routable, and impossible to find
    // ---------------------------------------------------------------------------------------------

    /// Every screen in the modules this integration covers must be reachable by clicking: a menu entry, or
    /// an alias of one (a drill-down opened from a row or a card). "Type the URL" is not reachable, and a
    /// screen nobody can find is indistinguishable from a screen that was never built.
    [Fact]
    public void No_screen_in_the_navigated_modules_is_an_orphan()
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, item) in AllMenuItems())
        {
            reachable.Add($"{item.Controller}/{item.Action}");
            foreach (var alias in item.Aliases) { reachable.Add($"{item.Controller}/{alias}"); }
        }

        var orphans = new List<string>();

        foreach (var controller in NavigatedModules)
        {
            var dir = Path.Combine(ViewRoot(), controller);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cshtml"))
            {
                var view = Path.GetFileNameWithoutExtension(file);
                if (view.StartsWith("_", StringComparison.Ordinal)) continue;   // partial, never navigated to
                if (!ActionExists(controller, view)) continue;                  // covered by the view/action sweep

                if (!reachable.Contains($"{controller}/{view}"))
                {
                    orphans.Add($"{controller}/{view} - a real screen with no menu entry and no alias");
                }
            }
        }

        Assert.True(orphans.Count == 0,
            "Orphan screens (the owner would have to type the URL):\n  " + string.Join("\n  ", orphans));
    }

    /// The mirror defect: a view file no action renders. It is dead weight that reads as a delivered screen.
    [Fact]
    public void No_navigated_module_carries_a_view_no_action_renders()
    {
        var stray = new List<string>();

        foreach (var controller in NavigatedModules)
        {
            var dir = Path.Combine(ViewRoot(), controller);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cshtml"))
            {
                var view = Path.GetFileNameWithoutExtension(file);
                if (view.StartsWith("_", StringComparison.Ordinal)) continue;

                // A view may be rendered by an action of another name (return View("Board")), so the source
                // is searched for the name before it is called stray.
                if (ActionExists(controller, view)) continue;

                var source = Path.Combine(RepoRoot(), "CrossBuy", "Controllers", controller + "Controller.cs");
                if (File.Exists(source) && File.ReadAllText(source).Contains("\"" + view + "\"", StringComparison.Ordinal)) continue;

                stray.Add($"{controller}/{view}.cshtml - no action of that name and no View(\"{view}\") call");
            }
        }

        Assert.True(stray.Count == 0,
            "View files nothing renders:\n  " + string.Join("\n  ", stray));
    }

    [Fact]
    public void Navigation_discovery_is_not_vacuous()
    {
        // Guards the guard: an empty sweep would pass everything above.
        Assert.True(AllMenuItems().Count >= 50, "menu sweep found too few items to be real");
        Assert.True(MainMenu.Platform().SelectMany(c => c.Items).Count() >= 7,
            "the Platform section lost entries");
    }

    // ---------------------------------------------------------------------------------------------

    /// The modules this navigation integration is accountable for. Deliberately a list, not a sweep of every
    /// controller: most of the product's screens are drill-downs opened from a row or a button, and demanding
    /// a menu entry for each would force exactly the sidebar clutter the visual authority avoids.
    private static readonly string[] NavigatedModules =
    {
        "Workspace", "BusinessEventMonitor", "Reports", "Tasks", "Calendar",
    };

    /// A destination is the action AND the arguments that reach it. Reports/Index and
    /// Reports/Index?favorites=true are two rows the user experiences as two screens, and keying on the
    /// action alone would call them a duplicate.
    private static string Destination(MenuItem item)
    {
        var key = $"{item.Controller}.{item.Action}";
        if (item.RouteValues is null || item.RouteValues.Count == 0) return key;

        var args = item.RouteValues.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                   .Select(kv => $"{kv.Key}={kv.Value}");
        return key + "?" + string.Join("&", args);
    }

    private static List<(string Menu, MenuItem Item)> AllMenuItems()
    {
        var all = new List<(string, MenuItem)>();

        foreach (var m in typeof(MainMenu).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.ReturnType != typeof(List<MenuCategory>) || m.GetParameters().Length != 0) continue;
            var cats = (List<MenuCategory>)m.Invoke(null, null)!;
            all.AddRange(cats.SelectMany(c => c.Items).Select(i => (m.Name, i)));
        }

        return all;
    }

    private static MethodInfo? FindAction(string controller, string action)
    {
        var type = App.GetTypes().FirstOrDefault(t =>
            t.Name == controller + "Controller" && typeof(ControllerBase).IsAssignableFrom(t));

        return type?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == action && !m.IsSpecialName);
    }

    private static bool ActionExists(string controller, string action) => FindAction(controller, action) is not null;

    private static string ViewRoot() => Path.Combine(RepoRoot(), "CrossBuy", "Views");

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
