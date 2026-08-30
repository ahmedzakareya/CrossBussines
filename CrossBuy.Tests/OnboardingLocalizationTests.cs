using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // EMPLOYEE ONBOARDING — the screen's strings live in Resources, and stay there.
    //
    // The onboarding screen shipped with a deliberate, documented deviation: a `T(ar, en)` helper
    // holding both languages inline, because CrossBuy/Resources/** is TAB-1-owned and the three resx
    // files did not exist yet. That was the right call at the time — with no resx, IViewLocalizer
    // returns the KEY, so a localized build would have rendered this screen in English under an
    // Arabic UI. A visible Arabic string beats an invisible English one.
    //
    // The resx files exist now, so the deviation is over. These tests are what stop it coming back
    // and, more usefully, stop the resources drifting out from under the view: a key the view asks
    // for that no resx answers renders as the raw English key, which looks like a translation that
    // merely has not been done rather than the wiring fault it actually is.
    // ============================================================================================
    public class OnboardingLocalizationTests
    {
        private const string ViewPath = "CrossBuy/Views/EmployeeOnboarding/Index.cshtml";
        private const string ControllerPath = "CrossBuy/Controllers/EmployeeOnboardingController.cs";
        private const string ResourceDir = "CrossBuy/Resources/Views/EmployeeOnboarding";

        private static readonly string[] Languages = { "ar", "en", "fr" };

        [Fact]
        public void The_screen_no_longer_carries_both_languages_inline()
        {
            var view = Read(ViewPath);
            var controller = Read(ControllerPath);

            // The exact shape of the retired deviation. Either file reintroducing it is the regression.
            var bilingual = new Regex(@"T\(\s*""[^""]*""\s*,\s*""[^""]*""\s*\)");
            Assert.DoesNotMatch(bilingual, view);
            Assert.DoesNotMatch(bilingual, controller);

            Assert.DoesNotContain("Func<string, string, string> T", view, StringComparison.Ordinal);
            Assert.DoesNotContain("string T(string ar, string en)", controller, StringComparison.Ordinal);

            // ...and the canonical mechanism is actually in use, so "no T()" cannot be satisfied by a
            // screen that simply hardcoded English.
            Assert.Contains("IViewLocalizer Localizer", view, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_string_the_screen_asks_for_is_answered_in_all_three_languages()
        {
            var used = KeysUsedByTheView();
            Assert.NotEmpty(used);          // a sweep that found nothing would pass vacuously

            foreach (var language in Languages)
            {
                var provided = KeysIn(language);
                var missing = used.Except(provided, StringComparer.Ordinal).OrderBy(k => k).ToList();

                Assert.True(missing.Count == 0,
                    $"{language}: the view asks for {missing.Count} key(s) the resource does not answer — "
                    + string.Join(" | ", missing.Take(5)));
            }
        }

        [Fact]
        public void The_three_resources_answer_exactly_the_same_keys()
        {
            var arabic = KeysIn("ar");

            // A key present in one language and absent in another is the failure mode that survives a
            // casual review: the screen looks translated until someone switches culture.
            foreach (var language in Languages.Where(l => l != "ar"))
                Assert.Equal(arabic.OrderBy(k => k, StringComparer.Ordinal),
                             KeysIn(language).OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void French_is_translated_rather_than_copied_from_English()
        {
            var english = ValuesIn("en");
            var french = ValuesIn("fr");

            // The repository's French resources carry real French. A fr file that echoes the English
            // passes a key-parity check while localizing nothing, so parity alone is not enough.
            var untranslated = french
                .Where(kv => english.TryGetValue(kv.Key, out var en)
                             && string.Equals(en, kv.Value, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .ToList();

            // "Action", "Document" and "Responsible" are genuinely identical in both languages; a
            // handful of true cognates is expected, a wholesale copy is not.
            Assert.True(untranslated.Count <= 5,
                $"{untranslated.Count} French values are byte-identical to the English — "
                + string.Join(" | ", untranslated.Take(8)));
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static ISet<string> KeysUsedByTheView()
        {
            var view = Read(ViewPath);
            var controller = Read(ControllerPath);

            // Literal keys the view resolves...
            var keys = new HashSet<string>(
                Regex.Matches(view, @"Localizer\[""([^""]+)""\]").Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);

            // ...plus the flash messages, which the CONTROLLER stores as keys and the view resolves.
            // They live in this screen's resource rather than SharedResources deliberately: they are
            // this screen's sentences, and routing them here keeps the shared file out of the change.
            foreach (Match m in Regex.Matches(controller, @"TempData\[""Onb(?:Msg|Err)""\]\s*=\s*""([^""]+)"""))
                keys.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(controller, @"^\s*[?:]\s*""([^""]+)""\s*$", RegexOptions.Multiline))
                keys.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(controller, @"=>\s*""([^""]+)"",?\s*$", RegexOptions.Multiline))
                keys.Add(m.Groups[1].Value);

            return keys;
        }

        private static IReadOnlyDictionary<string, string> ValuesIn(string language)
        {
            var path = Path.Combine(RepoRoot(), ResourceDir.Replace('/', Path.DirectorySeparatorChar),
                $"Index.{language}.resx");
            Assert.True(File.Exists(path), $"missing resource: {path}");

            return XDocument.Load(path).Root!.Elements("data")
                .ToDictionary(d => d.Attribute("name")!.Value,
                              d => d.Element("value")?.Value ?? "",
                              StringComparer.Ordinal);
        }

        private static ISet<string> KeysIn(string language) =>
            new HashSet<string>(ValuesIn(language).Keys, StringComparer.Ordinal);

        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
