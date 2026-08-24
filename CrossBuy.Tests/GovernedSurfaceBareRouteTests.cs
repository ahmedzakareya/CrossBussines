using System.Text.Json;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // EVERY GOVERNED SURFACE MUST ANSWER ON ITS BARE URL.
    //
    // THE DEFECT, FOUND SIX TIMES. The default route is
    //
    //     pattern: "{controller=Account}/{action=Login}/{id?}"
    //
    // — the ACTION defaults to "Login", not "Index", so the application lands on the sign-in screen.
    // The unintended consequence: a bare "/Xxx" resolves to XxxController.Login. For every controller
    // that has no Login action that is an EMPTY 404, while "/Xxx/Index" returns the real screen.
    //
    // It has now been found on Workspace, Reports, BusinessEventMonitor, Tasks, Calendar and Comm — one
    // at a time, each by a different owner, each time reported as a module defect before being traced
    // back to this same routing default. Nothing in the suite caught any of them, because every menu
    // link is built by Url.Action and therefore emits "/Xxx/Index": only a hand-typed, bookmarked or
    // shared URL fails, and only for a SIGNED-IN request. Unauthenticated it 302s to sign-in, which is
    // why five of the six survived until a UI conformance matrix asked for them while authenticated.
    //
    // WHY THIS TEST READS routes.json. The governed set is not a list this file should keep its own copy
    // of — routes.json IS the certification contract, and a surface added there is a surface the matrix
    // will request by its bare path. Deriving the assertion from that file means a NEW governed surface
    // inherits this guard automatically instead of waiting to be found a seventh time.
    //
    // The assertion is on Program.cs source rather than on a live pipeline for the same reason
    // TasksWithoutCommunicationTests gives: resolving a route for real needs a running host, and what
    // actually goes missing is the registration line.
    // ==========================================================================================
    public class GovernedSurfaceBareRouteTests
    {
        [Fact]
        public void Every_governed_single_segment_route_has_a_bare_controller_route_registered()
        {
            var program = ProgramCode();
            var surfaces = GovernedBareSurfaces();

            // Guards the guard: if routes.json moves or its shape changes, an empty set would make this
            // test vacuously green — the exact way a governance-derived assertion rots.
            Assert.True(surfaces.Count >= 6,
                $"routes.json yielded only {surfaces.Count} single-segment governed surfaces; the contract " +
                "lists at least six (Workspace, Reports, BusinessEventMonitor, Tasks, Calendar, Comm). " +
                "Has the file moved or changed shape?");

            var missing = surfaces
                .Where(name => !program.Contains("pattern: \"" + name + "\"", StringComparison.Ordinal))
                .ToList();

            Assert.True(missing.Count == 0,
                "No route maps the bare \"/" + string.Join("\", \"/", missing) + "\" to its Index action. " +
                "Because the default route defaults the ACTION to \"Login\", a signed-in request for the " +
                "bare URL resolves to a Login action that does not exist and returns an empty 404, while " +
                "\"/<name>/Index\" works. Add: app.MapControllerRoute(name: \"<name>-home\", pattern: " +
                "\"<name>\", defaults: new { controller = \"<name>\", action = \"Index\" });");
        }

        // Comm specifically, named so a regression reads as itself rather than as a list membership.
        [Fact]
        public void The_bare_Comm_route_is_registered()
        {
            var program = ProgramCode();

            Assert.Contains("pattern: \"Comm\"", program, StringComparison.Ordinal);
            Assert.Contains("controller = \"Comm\", action = \"Index\"", program, StringComparison.Ordinal);
        }

        // A COMMENTED-OUT registration must not satisfy any assertion above. The first version of this
        // file searched the raw file text, so commenting the route out left `pattern: "Comm"` sitting in
        // the comment and every guard here still passed — a test that cannot fail is worse than none.
        [Fact]
        public void A_commented_out_route_does_not_count_as_registered()
        {
            const string commented = """
                // app.MapControllerRoute(name: "comm-home", pattern: "Comm",
                //     defaults: new { controller = "Comm", action = "Index" });
                """;

            Assert.DoesNotContain("pattern: \"Comm\"", StripComments(commented), StringComparison.Ordinal);
        }

        // Communication is an ACTIVE surface, which is WHY the 404 mattered. If the Email entry ever
        // disappears from navigation the route above becomes dead weight and this pairing should be
        // revisited deliberately rather than drifting.
        [Fact]
        public void Communication_is_reachable_from_active_navigation()
        {
            var menu = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Models", "Menu", "MainMenu.cs"));

            Assert.Contains("Controller = \"Comm\"", menu, StringComparison.Ordinal);
        }

        // ----------------------------------------------------------------------------------------

        /// Program.cs with comments removed, so a registration that has been commented OUT cannot satisfy
        /// a Contains() check. Program.cs documents each of these routes in prose directly above it, and
        /// that prose quotes the very strings being searched for.
        private static string ProgramCode() =>
            StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs")));

        /// Line comments only — that is what a disabled registration looks like, and a full C# comment
        /// parser here would be more machinery than the claim needs.
        private static string StripComments(string source) =>
            string.Join('\n', source
                .Split('\n')
                .Select(line =>
                {
                    var at = line.IndexOf("//", StringComparison.Ordinal);
                    return at < 0 ? line : line[..at];
                }));

        /// The single-segment paths in routes.json — "/Comm" yields "Comm", "/Workspace/Agenda" yields
        /// nothing, because a two-segment path already names its action and never hits the default.
        private static List<string> GovernedBareSurfaces()
        {
            var path = Path.Combine(RepoRoot(), "tools", "ui-conformance", "routes.json");
            Assert.True(File.Exists(path), "routes.json not found at " + path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var paths = new List<string>();

            if (root.TryGetProperty("control", out var control)
                && control.TryGetProperty("path", out var controlPath))
            {
                paths.Add(controlPath.GetString() ?? "");
            }

            if (root.TryGetProperty("routes", out var routes))
            {
                foreach (var route in routes.EnumerateArray())
                {
                    if (route.TryGetProperty("path", out var p)) { paths.Add(p.GetString() ?? ""); }
                }
            }

            return paths
                .Select(p => p.Trim('/'))
                .Where(p => p.Length > 0 && !p.Contains('/'))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

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
}
