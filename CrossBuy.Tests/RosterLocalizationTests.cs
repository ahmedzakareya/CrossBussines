using System.Xml.Linq;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // ROSTER — the screen's strings, and the resources that must answer them.
    //
    // WHY THIS TEST POINTS AT TWO PLACES. CrossBuy/Resources/** is TAB-1-owned, so this batch could
    // not author the three resx files the roster views need. It authored them into
    // docs/handoff/roster-resources/ instead, complete and ready to move — a handoff that is a set of
    // finished files rather than a request for somebody else to do the translating.
    //
    // The test resolves the CANONICAL location first and falls back to the handoff one. So it passes
    // today against the staged files, and the moment TAB-1 moves them it starts guarding the real
    // ones — with no edit here. If it ever fails after the move, the resources and the view have
    // drifted apart, which is exactly what it exists to catch.
    //
    // The failure mode being guarded is not "untranslated". It is worse: a key the view asks for that
    // no resx answers renders as the raw ENGLISH KEY under an Arabic UI, which looks like a
    // translation somebody merely has not got to yet rather than the wiring fault it actually is.
    // ============================================================================================
    public class RosterLocalizationTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "CrossBuy", "Views")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string[] ViewFiles() => new[]
        {
            Path.Combine(RepoRoot(), "CrossBuy", "Views", "Roster", "Index.cshtml"),
            Path.Combine(RepoRoot(), "CrossBuy", "Views", "Roster", "Mine.cshtml"),
        };

        // CANONICAL ONLY, now that the files have moved.
        //
        // This deliberately no longer falls back to docs/handoff/roster-resources. The fallback was
        // right while the resources were staged and the destination belonged to another owner - it let
        // the guard exist before the files it guards. Keeping it afterwards would be worse than useless:
        // deleting the real resources would then be caught by nothing, because the test would quietly
        // find the staging copies and pass while the product rendered English keys under an Arabic UI.
        private static string ResourcePath(string culture)
        {
            var canonical = Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "Roster", $"Index.{culture}.resx");
            Assert.True(File.Exists(canonical),
                $"The canonical {culture} resource is missing. Production localization lives at:\n  {canonical}");
            return canonical;
        }

        private static HashSet<string> KeysUsedByTheScreen()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in ViewFiles())
            {
                Assert.True(File.Exists(file), $"View not found: {file}");
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(
                             File.ReadAllText(file), "Localizer\\[\"([^\"]+)\"\\]"))
                {
                    used.Add(m.Groups[1].Value);
                }
            }
            return used;
        }

        private static Dictionary<string, string> Resource(string culture)
        {
            var doc = XDocument.Load(ResourcePath(culture));
            return doc.Root!.Elements("data")
                .Where(d => d.Attribute("name") != null)
                .ToDictionary(
                    d => d.Attribute("name")!.Value,
                    d => d.Element("value")?.Value ?? "",
                    StringComparer.Ordinal);
        }

        [Fact]
        public void Every_string_the_screen_asks_for_is_answered_in_all_three_languages()
        {
            var used = KeysUsedByTheScreen();

            // A sweep that found nothing would pass vacuously and prove the opposite of what it claims.
            Assert.NotEmpty(used);

            foreach (var culture in new[] { "ar", "en", "fr" })
            {
                var answered = Resource(culture);
                var unanswered = used.Where(k => !answered.ContainsKey(k)).OrderBy(k => k).ToList();
                Assert.True(unanswered.Count == 0,
                    $"{culture}: {unanswered.Count} key(s) the roster screen asks for are unanswered:\n  "
                    + string.Join("\n  ", unanswered));
            }
        }

        [Fact]
        public void The_three_resources_answer_exactly_the_same_keys()
        {
            var ar = Resource("ar").Keys.ToHashSet(StringComparer.Ordinal);
            var en = Resource("en").Keys.ToHashSet(StringComparer.Ordinal);
            var fr = Resource("fr").Keys.ToHashSet(StringComparer.Ordinal);

            // A key present in one language and absent in another is the failure that survives a
            // casual review: the screen looks translated right up until somebody switches culture.
            Assert.Equal(ar.OrderBy(k => k), en.OrderBy(k => k));
            Assert.Equal(ar.OrderBy(k => k), fr.OrderBy(k => k));
        }

        [Fact]
        public void No_resource_carries_an_empty_translation()
        {
            foreach (var culture in new[] { "ar", "en", "fr" })
            {
                var empty = Resource(culture).Where(kv => string.IsNullOrWhiteSpace(kv.Value))
                    .Select(kv => kv.Key).OrderBy(k => k).ToList();
                Assert.True(empty.Count == 0,
                    $"{culture}: empty value for {empty.Count} key(s):\n  " + string.Join("\n  ", empty));
            }
        }

        [Fact]
        public void Arabic_and_French_are_translated_rather_than_copied_from_English()
        {
            var en = Resource("en");
            var ar = Resource("ar");
            var fr = Resource("fr");

            // Parity alone is not enough: a file that echoes the English passes a key check while
            // localizing nothing. "Date" is legitimately identical in English and French, so this
            // asserts on the PROPORTION that differs rather than demanding every entry differ.
            int arSame = ar.Count(kv => en.TryGetValue(kv.Key, out var e) && e == kv.Value);
            int frSame = fr.Count(kv => en.TryGetValue(kv.Key, out var e) && e == kv.Value);

            Assert.True(arSame == 0, $"Arabic echoes the English for {arSame} key(s).");
            Assert.True(frSame <= fr.Count / 10,
                $"French echoes the English for {frSame} of {fr.Count} keys — that is not a translation.");
        }

        [Fact]
        public void The_roster_screen_uses_the_canonical_localizer_and_not_an_inline_bilingual_helper()
        {
            // The onboarding screen shipped a documented T(ar, en) deviation and the repository has
            // since retired it. This screen must not reintroduce the shape now that a resource path
            // exists — even a staged one.
            foreach (var file in ViewFiles())
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("Func<string, string, string> T", text);
                Assert.Contains("IViewLocalizer", text);
            }
        }
    }
}
