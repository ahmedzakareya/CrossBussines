using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CrossBuy.Tests.UiConformance
{
    // =============================================================================================
    // UI CONFORMANCE — THE STATIC GATES.
    //
    // ONE visual language: the Inventory module is the authority. These tests mechanise the parts of
    // that rule which can be decided from source alone — no browser, no server, no screenshots — so
    // they run on every build in a couple of milliseconds.
    //
    // The parts that CANNOT be decided from source (layout / responsive / RTL / accessibility
    // regressions) are deliberately NOT faked here. They require rendering, and they live in
    // tools/ui-conformance (Playwright). A green run of THIS file is not a PASS verdict on its own,
    // and says so in the review protocol.
    //
    // WHY A SHRINK-ONLY EXCEPTION LIST, not a plain assertion:
    // the tree already contains known divergences owned by other tabs. A gate that simply failed
    // would be switched off within a day; a gate that silently passed would be worthless. So known
    // divergences are PINNED in baseline/known-exceptions.json with a file, a reason, a severity and
    // an owner. New divergences fail. A pinned divergence that has been FIXED also fails, forcing the
    // list to shrink instead of rotting. This mirrors authorization-baseline.json, which the platform
    // already governs the same way ("the baseline may only shrink").
    //
    // This file is VERIFICATION ONLY. It changes no view, no layout, no stylesheet.
    // =============================================================================================
    public class UiConformanceTests
    {
        // The authority. Everything else is measured against it.
        private const string AuthorityLayout = "_LayoutInventory.cshtml";

        // Manufacturing screens live under /Inventory but are their own system with their own shell,
        // so the authority module legitimately declares two layouts. Pinned so a THIRD cannot appear
        // without this test saying so.
        private static readonly string[] AuthorityLayouts = { "_LayoutInventory.cshtml", "_LayoutManufacturing.cshtml" };

        // Modules governed by the one-visual-language rule, as instructed.
        private static readonly string[] GovernedModules =
            { "Reports", "Workspace", "BusinessEventMonitor", "Tasks", "Calendar" };

        // Metronic's own table variants. A table outside this set is bespoke markup, which is how a
        // second visual language re-enters through a component rather than through a stylesheet.
        private static readonly string[] ApprovedTableVariants = { "table-row-dashed", "table-row-bordered" };

        // The only stylesheets a screen may pull in beyond the shell's own bundle.
        private static readonly string[] ApprovedStylesheets =
        {
            "crossbuy-brand.css",                    // the brand override — ledger green + gold
            "crossbusiness-platform-views.css",      // presentation-only adapters, no design tokens (asserted below)
            "fullcalendar.bundle.css",               // vendor plugin
            "datatables.bundle.css",                 // vendor plugin
            "jstree.bundle.css",                     // vendor plugin
        };

        // SCOPE. The one-visual-language rule governs the six modules above plus the authority. It does
        // NOT govern the legacy sign-in, portal and POS-terminal screens, which are their own products
        // with their own shells and were never claimed by it. Asserting over them would produce dozens
        // of findings nobody agreed to own — and a gate that cries wolf gets switched off.
        private static readonly string[] GovernedSurface =
            { "Inventory", "Reports", "Workspace", "BusinessEventMonitor", "Tasks", "Calendar" };

        // Vendor/theme bundles ARE the language; they are not competing with it.
        private static readonly string[] VendorStylesheets =
            { "style.bundle", "plugins.bundle", "fullcalendar.bundle", "datatables.bundle", "jstree.bundle", "dropzone" };

        // A "design token" is what makes a stylesheet a LANGUAGE rather than a layout adapter: a
        // HARDCODED colour, a typeface, or a custom property others inherit. Overflow, cursor,
        // max-width and reduced-motion rules are not a language — they constrain a box.
        //
        // Crucially, `var(--bs-primary, #13433a)` is CONSUMING the shared language, not defining a
        // rival one, so var() expressions are stripped before the scan. That single distinction is the
        // difference between flagging real divergence and flagging correct code.
        private static readonly System.Text.RegularExpressions.Regex HardcodedColour =
            new(@"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex CustomPropertyDefinition =
            new(@"^\s*--[\w-]+\s*:", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex VarExpression =
            new(@"var\([^)]*\)", System.Text.RegularExpressions.RegexOptions.Compiled);

        // -----------------------------------------------------------------------------------------
        // Gate 1 — every governed screen renders through the authority's shell.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void Every_governed_screen_renders_through_the_Inventory_shell()
        {
            var offenders = new List<string>();

            foreach (var module in GovernedModules)
            {
                foreach (var view in ScreenViews(module))
                {
                    var declared = DeclaredLayout(File.ReadAllText(view));
                    if (declared is null)
                    {
                        offenders.Add($"{Rel(view)} — declares no Layout (a screen must name its shell)");
                    }
                    else if (!declared.EndsWith(AuthorityLayout, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Rel(view)} — uses {declared}, not {AuthorityLayout}");
                    }
                }
            }

            AssertNone(offenders,
                "A governed screen is not rendering through the Inventory shell. One ERP, one visual language.");
        }

        // -----------------------------------------------------------------------------------------
        // Gate 2 — the authority itself declares only its two approved shells.
        // A drift here changes what every other module is measured AGAINST, so it must be loud.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void The_visual_authority_declares_only_its_approved_layouts()
        {
            var offenders = new List<string>();

            foreach (var view in ScreenViews("Inventory"))
            {
                var declared = DeclaredLayout(File.ReadAllText(view));
                if (declared is null) { continue; }   // a screen may inherit via _ViewStart

                if (!AuthorityLayouts.Any(a => declared.EndsWith(a, StringComparison.Ordinal)))
                {
                    offenders.Add($"{Rel(view)} — uses {declared}, outside the authority's approved shells");
                }
            }

            AssertNone(offenders, "The visual authority gained a shell it does not declare.");
        }

        // -----------------------------------------------------------------------------------------
        // Gate 3 — no view carries the retired design language as a LIVE class attribute.
        // Comment prose mentioning it is not a violation; a rendered class is.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void No_view_carries_the_retired_design_language()
        {
            var offenders = new List<string>();

            foreach (var view in AllViews())
            {
                var line = 0;
                foreach (var text in File.ReadLines(view))
                {
                    line++;
                    if (!CarriesLiveLegacyClass(text)) { continue; }
                    offenders.Add($"{Rel(view)}:{line} — live cbw-* class attribute");
                }
            }

            AssertAgainstExceptions(offenders, "legacy-design-language",
                "A view carries the retired CrossBusiness Blue class vocabulary.");
        }

        // -----------------------------------------------------------------------------------------
        // Gate 4 — the retired stylesheet has no ACTIVE referrer, and the retired layout is orphaned.
        // Together these are the deletion gate: when both lists are empty the file can go.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void The_retired_stylesheet_and_layout_have_no_active_referrer()
        {
            var offenders = new List<string>();

            foreach (var view in AllViews())
            {
                var text = File.ReadAllText(view);
                var name = Path.GetFileName(view);

                if (name != "_LayoutWorkspace.cshtml" && text.Contains("crossbusiness-workspace.css", StringComparison.Ordinal))
                {
                    offenders.Add($"{Rel(view)} — links the retired stylesheet");
                }

                // An orphan is only harmless while nothing renders it.
                if (name != "_LayoutWorkspace.cshtml" &&
                    (text.Contains("Layout = \"~/Views/Shared/_LayoutWorkspace.cshtml\"", StringComparison.Ordinal) ||
                     text.Contains("PartialAsync(\"_LayoutWorkspace\"", StringComparison.Ordinal)))
                {
                    offenders.Add($"{Rel(view)} — renders the retired layout");
                }
            }

            AssertNone(offenders,
                "The retired Workspace layout/stylesheet was re-adopted. It is a compatibility artifact awaiting deletion, not a resource.");
        }

        // -----------------------------------------------------------------------------------------
        // Gate 5 — a view may only pull in an approved stylesheet.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void Every_stylesheet_a_view_links_is_approved()
        {
            var offenders = new List<string>();

            foreach (var view in GovernedViews())
            {
                var line = 0;
                foreach (var text in File.ReadLines(view))
                {
                    line++;
                    if (!text.Contains("rel=\"stylesheet\"", StringComparison.Ordinal)) { continue; }
                    if (ApprovedStylesheets.Any(s => text.Contains(s, StringComparison.Ordinal))) { continue; }
                    if (VendorStylesheets.Any(s => text.Contains(s, StringComparison.Ordinal))) { continue; }

                    offenders.Add($"{Rel(view)}:{line} — links an unapproved stylesheet");
                }
            }

            AssertAgainstExceptions(offenders, "unapproved-stylesheet",
                "A screen pulled in a stylesheet outside the approved set.");
        }

        // -----------------------------------------------------------------------------------------
        // Gate 6 — no stylesheet outside the brand file may define DESIGN TOKENS.
        //
        // This is the check that distinguishes a second visual language from a layout adapter, and it
        // is the one that matters most: crossbusiness-platform-views.css is 648 bytes of cursor /
        // overflow / max-height / reduced-motion and defines NO colour, typeface or custom property,
        // so it is legitimately not a language. A file that did would fail here.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void No_stylesheet_outside_the_brand_defines_design_tokens()
        {
            var cssRoot = Path.Combine(RepoRoot(), "CrossBuy", "wwwroot", "Backend-assets", "css");
            var offenders = new List<string>();

            // Only the stylesheets the GOVERNED surface actually pulls in, plus the retired one, which
            // is pinned. Scanning the whole css folder would drag in the sign-in and POS themes, which
            // this rule does not govern.
            var inScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "crossbusiness-workspace.css" };
            foreach (var view in GovernedViews())
            {
                foreach (var text in File.ReadLines(view))
                {
                    if (!text.Contains("rel=\"stylesheet\"", StringComparison.Ordinal)) { continue; }
                    foreach (var candidate in Directory.GetFiles(cssRoot, "*.css").Select(Path.GetFileName))
                    {
                        if (candidate is not null && text.Contains(candidate, StringComparison.OrdinalIgnoreCase)) { inScope.Add(candidate); }
                    }
                }
            }

            foreach (var name in inScope.OrderBy(n => n, StringComparer.Ordinal))
            {
                var css = Path.Combine(cssRoot, name);
                if (!File.Exists(css)) { continue; }

                // The brand override and the vendor bundles ARE the language, by definition.
                if (name is "crossbuy-brand.css") { continue; }
                if (VendorStylesheets.Any(v => name.StartsWith(v, StringComparison.OrdinalIgnoreCase))) { continue; }

                var tokens = File.ReadAllLines(css).Select(StripComment).Count(DefinesDesignToken);
                if (tokens > 0)
                {
                    offenders.Add($"CrossBuy/wwwroot/Backend-assets/css/{name} — declares {tokens} hardcoded colour/typeface/custom-property rule(s)");
                }
            }

            AssertAgainstExceptions(offenders, "stylesheet-design-tokens",
                "A stylesheet outside the brand file defines colour/typeface/custom properties — that is a second visual language.");
        }

        // -----------------------------------------------------------------------------------------
        // Gate 7 — an inline <style> block may not define design tokens either.
        // Closing the stylesheet door while leaving this one open would achieve nothing.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void No_inline_style_block_defines_design_tokens()
        {
            var offenders = new List<string>();

            foreach (var view in GovernedViews())
            {
                var text = File.ReadAllText(view);
                if (!text.Contains("<style", StringComparison.OrdinalIgnoreCase)) { continue; }

                foreach (var block in StyleBlocks(text))
                {
                    var hits = block.Split('\n').Select(StripComment).Count(DefinesDesignToken);
                    if (hits > 0)
                    {
                        offenders.Add($"{Rel(view)} — inline <style> declares {hits} hardcoded colour/typeface rule(s)");
                    }
                }
            }

            AssertAgainstExceptions(offenders, "inline-design-tokens",
                "An inline <style> block defines colour or typeface instead of using Metronic tokens.");
        }

        // -----------------------------------------------------------------------------------------
        // Gate 8 — every table is a Metronic table.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void Every_governed_table_uses_an_approved_Metronic_variant()
        {
            var offenders = new List<string>();

            foreach (var module in GovernedModules)
            {
                foreach (var view in AllViewsIn(module))
                {
                    var line = 0;
                    foreach (var text in File.ReadLines(view))
                    {
                        line++;
                        if (!text.Contains("<table", StringComparison.OrdinalIgnoreCase)) { continue; }
                        if (ApprovedTableVariants.Any(v => text.Contains(v, StringComparison.Ordinal))) { continue; }

                        offenders.Add($"{Rel(view)}:{line} — table is not a Metronic table-row-* variant");
                    }
                }
            }

            AssertAgainstExceptions(offenders, "bespoke-table",
                "A governed screen renders a table outside Metronic's component set.");
        }

        // -----------------------------------------------------------------------------------------
        // Gate 9 — the authority matches its recorded baseline.
        //
        // Drift is NOT automatically wrong: the Inventory owner may change their own module. It must
        // however be SURFACED, because every other module is measured against it, and silently
        // re-baselining would make "matches Inventory" unfalsifiable.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void The_visual_authority_matches_its_recorded_baseline()
        {
            var baseline = ReadJson("authority-baseline.json");
            var offenders = new List<string>();

            foreach (var scope in baseline.EnumerateObject())
            {
                if (scope.Name.StartsWith("_", StringComparison.Ordinal)) { continue; }   // metadata keys

                var expected = scope.Value.GetProperty("aggregate").GetString();
                var actual = Aggregate(scope.Value.GetProperty("glob").GetString()!);

                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    offenders.Add(
                        $"{scope.Name} — recorded {Short(expected)}, now {Short(actual)}. " +
                        "If this change is intended, the owner re-blesses tools/ui-conformance/baseline/authority-baseline.json in a dedicated commit.");
                }
            }

            AssertNone(offenders, "The visual authority (or a module measured against it) drifted from its recorded baseline.");
        }

        // -----------------------------------------------------------------------------------------
        // Gate 10 — the exception list may only SHRINK.
        // A pinned divergence that has been fixed must be removed, or the list rots into a permanent
        // amnesty and the gates above quietly stop meaning anything.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void Pinned_exceptions_that_no_longer_occur_must_be_removed()
        {
            var stale = new List<string>();

            foreach (var category in ReadJson("known-exceptions.json").EnumerateObject())
            {
                if (category.Name.StartsWith("_", StringComparison.Ordinal)) { continue; }

                foreach (var entry in category.Value.EnumerateArray())
                {
                    var file = entry.GetProperty("file").GetString()!;
                    if (!File.Exists(Path.Combine(RepoRoot(), file.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        stale.Add($"{category.Name}: {file} — pinned as an exception, but the file no longer exists. Remove the entry.");
                    }
                }
            }

            AssertNone(stale, "The exception list has stale entries. It may only shrink.");
        }

        // ============================ helpers ============================

        // A "screen" is a routable view. Partials (leading underscore) inherit their parent's shell.
        private static IEnumerable<string> ScreenViews(string module) =>
            AllViewsIn(module).Where(v => !Path.GetFileName(v).StartsWith("_", StringComparison.Ordinal));

        private static IEnumerable<string> AllViewsIn(string module)
        {
            var dir = Path.Combine(RepoRoot(), "CrossBuy", "Views", module);
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cshtml").OrderBy(f => f, StringComparer.Ordinal)
                                         : Enumerable.Empty<string>();
        }

        private static IEnumerable<string> AllViews() =>
            Directory.GetFiles(Path.Combine(RepoRoot(), "CrossBuy", "Views"), "*.cshtml", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal);

        // Every view of every module the one-visual-language rule governs. Nothing else.
        private static IEnumerable<string> GovernedViews() =>
            GovernedSurface.SelectMany(AllViewsIn).OrderBy(f => f, StringComparer.Ordinal);

        // A hardcoded colour, a typeface, or a custom-property DEFINITION — after var() expressions are
        // removed, because `var(--bs-primary, #13433a)` consumes the shared language rather than
        // competing with it, and flagging it would train people to ignore this gate.
        private static bool DefinesDesignToken(string line)
        {
            if (CustomPropertyDefinition.IsMatch(line)) { return true; }
            if (line.Contains("font-family:", StringComparison.OrdinalIgnoreCase)) { return true; }

            return HardcodedColour.IsMatch(VarExpression.Replace(line, string.Empty));
        }

        private static string? DeclaredLayout(string text)
        {
            var at = text.IndexOf("Layout = \"", StringComparison.Ordinal);
            if (at < 0) { return null; }

            var start = at + "Layout = \"".Length;
            var end = text.IndexOf('"', start);
            return end < 0 ? null : text[start..end];
        }

        // A class attribute that actually renders — not the word "cbw-" inside a comment.
        private static bool CarriesLiveLegacyClass(string line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("@*", StringComparison.Ordinal) ||
                trimmed.StartsWith("*", StringComparison.Ordinal)) { return false; }

            var at = line.IndexOf("class=\"", StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = line.IndexOf('"', at + 7);
                if (end < 0) { break; }
                if (line[(at + 7)..end].Contains("cbw-", StringComparison.Ordinal)) { return true; }
                at = line.IndexOf("class=\"", end, StringComparison.Ordinal);
            }
            return false;
        }

        private static IEnumerable<string> StyleBlocks(string text)
        {
            var at = text.IndexOf("<style", StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                var open = text.IndexOf('>', at);
                var close = text.IndexOf("</style>", at, StringComparison.OrdinalIgnoreCase);
                if (open < 0 || close < 0) { yield break; }

                yield return text[(open + 1)..close];
                at = text.IndexOf("<style", close, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string StripComment(string line)
        {
            var at = line.IndexOf("/*", StringComparison.Ordinal);
            return at < 0 ? line : line[..at];
        }

        // Deterministic across machines: sort by ordinal relative path, hash "path:filehash\n" pairs.
        private static string Aggregate(string glob)
        {
            var separator = glob.LastIndexOf('/');
            var dir = Path.Combine(RepoRoot(), glob[..separator].Replace('/', Path.DirectorySeparatorChar));
            var pattern = glob[(separator + 1)..];

            var builder = new StringBuilder();
            foreach (var file in Directory.GetFiles(dir, pattern).OrderBy(Rel, StringComparer.Ordinal))
            {
                builder.Append(Rel(file)).Append(':').Append(Sha256(File.ReadAllBytes(file))).Append('\n');
            }

            return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
        }

        private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static string Short(string? hash) => hash is { Length: > 12 } ? hash[..12] : hash ?? "(none)";

        private static string Rel(string path) =>
            Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

        private static JsonElement ReadJson(string name)
        {
            var path = Path.Combine(RepoRoot(), "tools", "ui-conformance", "baseline", name);
            Assert.True(File.Exists(path), $"Missing conformance baseline: {path}");
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        }

        private static void AssertNone(List<string> offenders, string headline)
        {
            if (offenders.Count == 0) { return; }
            Assert.Fail(headline + "\n\n  " + string.Join("\n  ", offenders) + "\n");
        }

        // Fails on anything not pinned, and reports pinned-but-fixed so the list keeps shrinking.
        private static void AssertAgainstExceptions(List<string> offenders, string category, string headline)
        {
            var pinned = new List<string>();
            var root = ReadJson("known-exceptions.json");

            if (root.TryGetProperty(category, out var entries))
            {
                pinned.AddRange(entries.EnumerateArray().Select(e => e.GetProperty("file").GetString()!));
            }

            var unpinned = offenders
                .Where(o => !pinned.Any(p => o.StartsWith(p, StringComparison.Ordinal)))
                .ToList();

            if (unpinned.Count == 0) { return; }

            Assert.Fail(
                headline +
                $"\n\n  NEW divergence in category '{category}' — not present in known-exceptions.json:\n  " +
                string.Join("\n  ", unpinned) +
                "\n\n  Fix it, or (if the owner accepts it) pin it with file/reason/severity/owner. The list may only shrink.\n");
        }

        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment)) { return fromEnvironment; }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln"))) { return directory.FullName; }
                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the repository root above " + AppContext.BaseDirectory);
        }
    }
}
