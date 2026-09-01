using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // GLOBAL UI RULE — TASKS AND CALENDAR MUST BE INDISTINGUISHABLE FROM INVENTORY
    //
    // "If any new screen can be visually distinguished from the Inventory module, the implementation
    // is considered FAILED."
    //
    // A rule that lives only in a document gets broken by the next person in a hurry. These tests make
    // it mechanical: they read the Inventory authority and the Tasks/Calendar views from disk and
    // compare the structures that decide what a screen looks like.
    //
    // The authority is Views/Inventory/Items.cshtml (the list-page reference behind
    // https://localhost:44368/Inventory/Index) — not a copy of it, and not a description of it.
    // ==========================================================================================
    public class TasksCalendarVisualConformanceTests
    {
        private const string Authority = "Items.cshtml";

        private static string ProjectRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "CrossBuy", "Views", "Inventory")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "CrossBuy");
        }

        private static string View(params string[] parts) =>
            File.ReadAllText(Path.Combine(ProjectRoot(), "Views", Path.Combine(parts)));

        /// Every Tasks and Calendar screen. Adding a screen here is deliberate: a new screen that is not
        /// listed is a screen nobody is checking.
        public static IEnumerable<object[]> AllScreens() => new List<object[]>
        {
            new object[] { Path.Combine("Tasks", "Index.cshtml") },
            new object[] { Path.Combine("Tasks", "AutoRules.cshtml") },
            new object[] { Path.Combine("Tasks", "HoursReport.cshtml") },
            new object[] { Path.Combine("Tasks", "MatchSuggestions.cshtml") },
            new object[] { Path.Combine("Tasks", "Reports.cshtml") },
            // Task ecosystem screens. Registered the moment they were created: a screen that is not
            // registered is a screen that is not protected.
            new object[] { Path.Combine("Tasks", "Board.cshtml") },
            new object[] { Path.Combine("Tasks", "Templates.cshtml") },
            new object[] { Path.Combine("Tasks", "Detail.cshtml") },
            new object[] { Path.Combine("Calendar", "Index.cshtml") },
            new object[] { Path.Combine("Calendar", "Timeline.cshtml") },
            new object[] { Path.Combine("Calendar", "ResourceView.cshtml") },
        };

        // ------------------------------------------------------------------------------------------
        // 1. THE SHELL. Calendar keeps FullCalendar, but it lives inside the Inventory shell.
        // ------------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(AllScreens))]
        public void Every_tasks_and_calendar_screen_uses_the_inventory_shell(string relative)
        {
            var view = View(relative.Split(Path.DirectorySeparatorChar));

            Assert.Contains("_LayoutInventory.cshtml", view, StringComparison.Ordinal);
            // The shells these screens used to sit in. A second shell is a second visual language.
            Assert.DoesNotContain("_LayoutBackend.cshtml", view, StringComparison.Ordinal);
            Assert.DoesNotContain("_LayoutAccounting.cshtml", view, StringComparison.Ordinal);
            Assert.DoesNotContain("_LayoutWorkspace.cshtml", view, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------------------------------
        // 2. THE TOOLBAR AND CONTENT CONTAINERS, taken from the authority rather than described.
        // ------------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(AllScreens))]
        public void Every_screen_reuses_the_inventory_toolbar_and_content_structure(string relative)
        {
            var authority = View("Inventory", Authority);
            var view = View(relative.Split(Path.DirectorySeparatorChar));

            foreach (var marker in new[]
            {
                "kt_app_toolbar",           // the Metronic toolbar id Inventory uses
                "app-toolbar",
                "page-heading",             // the page title treatment
                "breadcrumb-separatorless", // the breadcrumb treatment
                "kt_app_content",
                "app-content",
                "app-container container-fluid",
            })
            {
                Assert.Contains(marker, authority, StringComparison.Ordinal);   // still true of the authority
                Assert.Contains(marker, view, StringComparison.Ordinal);        // and true of this screen
            }
        }

        // ------------------------------------------------------------------------------------------
        // 3. THE CARD. Inventory's card is `card card-flush`. A bespoke card is a bespoke language.
        // ------------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(AllScreens))]
        public void Every_screen_uses_the_inventory_card(string relative)
        {
            var view = View(relative.Split(Path.DirectorySeparatorChar));
            Assert.Contains("card card-flush", view, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------------------------------
        // 4. NO BESPOKE CLASSES. These are the exact class names the Tasks board invented, and the
        //    comments in that stylesheet said what they were for: "matched to the approved mockup" and
        //    "active is blue like the mockup (not the brand green)". That is a second design language,
        //    named by its own author.
        // ------------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(AllScreens))]
        public void No_screen_reintroduces_a_bespoke_component_class(string relative)
        {
            var view = View(relative.Split(Path.DirectorySeparatorChar));

            foreach (var bespoke in new[]
            {
                "kpi-card", "kpi-icon", "kpi-num", "kpi-lbl",
                "kpi-total", "kpi-urgent", "kpi-overdue", "kpi-prog", "kpi-done",
                "task-tabs", "tasks-card", "sort-ico",
            })
            {
                // class="…" / class='…' only — the word may legitimately appear in a comment explaining
                // why it was removed, and forbidding that would forbid the code from explaining itself.
                Assert.DoesNotMatch(
                    new Regex($@"class\s*=\s*[""'][^""']*\b{Regex.Escape(bespoke)}\b", RegexOptions.CultureInvariant),
                    view);
            }
        }

        // ------------------------------------------------------------------------------------------
        // 5. NO VIEW-LOCAL STYLESHEET on the Tasks screens.
        //
        //    Calendar is EXEMPT and the exemption is narrow and stated: the rule says "Calendar keeps
        //    FullCalendar functionality", and FullCalendar needs view-local CSS for its event colours
        //    (which already use the brand #13433a) and its responsive toolbar. Tasks needs none.
        // ------------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(AllScreens))]
        public void No_tasks_screen_carries_a_view_local_stylesheet(string relative)
        {
            if (relative.Contains("Calendar", StringComparison.Ordinal)) return;   // stated exemption

            var view = View(relative.Split(Path.DirectorySeparatorChar));
            Assert.DoesNotContain("<style", view, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------------------------------
        // 6. NO INVENTED PALETTE. A screen may not introduce a hex colour the authority does not use.
        //    Inventory itself paints inline with #13433a / #1f6253, so the test bans *unknown* hexes
        //    rather than the attribute — banning the attribute would forbid what the reference does.
        // ------------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(AllScreens))]
        public void No_screen_introduces_a_colour_the_authority_does_not_use(string relative)
        {
            var authority = View("Inventory", Authority) + View("Inventory", "Index.cshtml");
            var view = View(relative.Split(Path.DirectorySeparatorChar));

            var hex = new Regex(@"#[0-9a-fA-F]{6}\b", RegexOptions.CultureInvariant);
            var allowed = hex.Matches(authority).Select(m => m.Value.ToLowerInvariant()).ToHashSet();

            // Calendar's FullCalendar palette is part of its stated exemption.
            var calendarAllowed = new[] { "#13433a", "#1b84ff", "#e1e3ea", "#f4f6fa", "#181c32", "#7e8299", "#e9f1ee" };
            if (relative.Contains("Calendar", StringComparison.Ordinal))
                foreach (var c in calendarAllowed) allowed.Add(c);

            var used = hex.Matches(view).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
            var invented = used.Where(c => !allowed.Contains(c)).ToList();

            Assert.True(invented.Count == 0,
                $"{relative} introduces colours the Inventory authority does not use: {string.Join(", ", invented)}");
        }
    }
}
