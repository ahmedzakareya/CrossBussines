using System.Xml.Linq;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // ROSTER SCREENS — every string resolves, in every language, FROM ITS OWN VIEW'S RESOURCES.
    //
    // WHAT THIS TEST GOT WRONG BEFORE, AND WHY IT MATTERED.
    //
    // The previous version swept the keys of BOTH roster views and checked them against
    // Index.*.resx. It passed, and it was wrong: ASP.NET Core's view localizer resolves
    // Views/Roster/Mine.cshtml against Resources/Views/Roster/Mine.*.resx and nowhere else. So
    // Mine.cshtml shipped with NO resources at all, rendered raw English keys under an Arabic UI,
    // and a green test said otherwise. A test that pools resources across views cannot detect the
    // one fault it exists to detect.
    //
    // It is now per-view, and there is no staging or handoff fallback: each view is checked against
    // its own canonical resource file, and a missing file is a failure rather than a redirect to
    // somewhere more convenient.
    //
    // The failure mode being guarded is not "untranslated". It is worse: a key nothing answers
    // renders as the raw English key, which looks like a translation somebody has not got to yet
    // rather than the wiring fault it actually is.
    // ============================================================================================
    public class RosterLocalizationTests
    {
        private static readonly string[] Cultures = { "ar", "en", "fr" };

        /// The two roster views, each paired with the resource BASE NAME the framework will actually
        /// look for. The pairing is the point of this file: Index may only be answered by Index.*,
        /// and Mine may only be answered by Mine.*.
        public static IEnumerable<object[]> Views => new[]
        {
            new object[] { "Index" },
            new object[] { "Mine" },
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "CrossBuy", "Views")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string ViewPath(string view) =>
            Path.Combine(RepoRoot(), "CrossBuy", "Views", "Roster", view + ".cshtml");

        // CANONICAL ONLY. No docs/handoff fallback: a resource that lives anywhere else is a resource
        // the running application will never load, so accepting one would make this test agree with a
        // screen that is still broken.
        private static string ResourcePath(string view, string culture) =>
            Path.Combine(RepoRoot(), "CrossBuy", "Resources", "Views", "Roster", $"{view}.{culture}.resx");

        private static HashSet<string> KeysUsedBy(string view)
        {
            var file = ViewPath(view);
            Assert.True(File.Exists(file), $"View not found: {file}");

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(file), "Localizer\\[\"([^\"]+)\"\\]"))
            {
                used.Add(m.Groups[1].Value);
            }
            return used;
        }

        private static Dictionary<string, string> Resource(string view, string culture)
        {
            var path = ResourcePath(view, culture);

            // THE ASSERTION THAT MAKES DELETION FAIL. Without it a missing file would surface as an
            // exception from XDocument.Load with no explanation of what was actually wrong.
            Assert.True(File.Exists(path),
                $"Missing canonical resource for Views/Roster/{view}.cshtml: {path}. "
                + "The view localizer resolves per view — Index resources cannot answer Mine's keys.");

            return XDocument.Load(path).Root!.Elements("data")
                .Where(d => d.Attribute("name") != null)
                .ToDictionary(d => d.Attribute("name")!.Value,
                              d => d.Element("value")?.Value ?? "",
                              StringComparer.Ordinal);
        }

        [Theory]
        [MemberData(nameof(Views))]
        public void Every_string_a_view_asks_for_is_answered_by_that_views_own_resources(string view)
        {
            var used = KeysUsedBy(view);

            // A sweep that found nothing would pass vacuously and prove the opposite of its name.
            Assert.NotEmpty(used);

            foreach (var culture in Cultures)
            {
                var answered = Resource(view, culture);
                var unanswered = used.Where(k => !answered.ContainsKey(k)).OrderBy(k => k).ToList();
                Assert.True(unanswered.Count == 0,
                    $"{view}.{culture}.resx does not answer {unanswered.Count} key(s) the view asks for:\n  "
                    + string.Join("\n  ", unanswered));
            }
        }

        [Theory]
        [MemberData(nameof(Views))]
        public void A_views_resources_carry_no_keys_the_view_does_not_ask_for(string view)
        {
            // The other half of per-view discipline. Mine's keys sat in Index.*.resx for a whole
            // release precisely because nothing objected to a resource answering somebody else's
            // screen — so unused keys are now a failure, not clutter.
            var used = KeysUsedBy(view);

            foreach (var culture in Cultures)
            {
                var stray = Resource(view, culture).Keys.Where(k => !used.Contains(k))
                    .OrderBy(k => k).ToList();
                Assert.True(stray.Count == 0,
                    $"{view}.{culture}.resx carries {stray.Count} key(s) {view}.cshtml never asks for "
                    + $"(they likely belong to another view):\n  " + string.Join("\n  ", stray));
            }
        }

        [Theory]
        [MemberData(nameof(Views))]
        public void The_three_languages_answer_exactly_the_same_keys(string view)
        {
            var ar = Resource(view, "ar").Keys.ToHashSet(StringComparer.Ordinal);
            var en = Resource(view, "en").Keys.ToHashSet(StringComparer.Ordinal);
            var fr = Resource(view, "fr").Keys.ToHashSet(StringComparer.Ordinal);

            // A key present in one language and absent in another is the failure that survives a
            // casual review: the screen looks translated right up until somebody switches culture.
            Assert.Equal(ar.OrderBy(k => k), en.OrderBy(k => k));
            Assert.Equal(ar.OrderBy(k => k), fr.OrderBy(k => k));
        }

        [Theory]
        [MemberData(nameof(Views))]
        public void No_resource_carries_an_empty_translation(string view)
        {
            foreach (var culture in Cultures)
            {
                var empty = Resource(view, culture)
                    .Where(kv => string.IsNullOrWhiteSpace(kv.Value))
                    .Select(kv => kv.Key).OrderBy(k => k).ToList();
                Assert.True(empty.Count == 0,
                    $"{view}.{culture}.resx has an empty value for {empty.Count} key(s):\n  "
                    + string.Join("\n  ", empty));
            }
        }

        [Theory]
        [MemberData(nameof(Views))]
        public void Arabic_never_falls_back_to_the_english_key(string view)
        {
            // Arabic and English share no script, so ANY Arabic value equal to its English key is an
            // untranslated entry — there is no legitimate coincidence to allow for, which is why this
            // demands zero rather than a proportion.
            var en = Resource(view, "en");
            var echoes = Resource(view, "ar")
                .Where(kv => en.TryGetValue(kv.Key, out var e) && e == kv.Value)
                .Select(kv => kv.Key).OrderBy(k => k).ToList();

            Assert.True(echoes.Count == 0,
                $"{view}.ar.resx echoes the English for {echoes.Count} key(s) — Arabic would render "
                + $"English text:\n  " + string.Join("\n  ", echoes));
        }

        [Theory]
        [MemberData(nameof(Views))]
        public void French_never_falls_back_to_the_english_key(string view)
        {
            // French legitimately shares a few words with English ("Date"), so this asserts on the
            // PROPORTION. A file that simply copied the English would blow past a tenth immediately.
            var en = Resource(view, "en");
            var fr = Resource(view, "fr");
            var echoes = fr.Where(kv => en.TryGetValue(kv.Key, out var e) && e == kv.Value)
                .Select(kv => kv.Key).OrderBy(k => k).ToList();

            Assert.True(echoes.Count <= fr.Count / 10,
                $"{view}.fr.resx echoes the English for {echoes.Count} of {fr.Count} keys — that is a "
                + $"copy, not a translation:\n  " + string.Join("\n  ", echoes));
        }

        [Fact]
        public void The_english_resource_answers_each_key_with_the_key_itself()
        {
            // The English file is the identity mapping by convention. Stated as a test because a
            // drifted English value is invisible on screen (it still reads as English) while quietly
            // breaking the echo checks above, which compare the other languages AGAINST it.
            foreach (var view in new[] { "Index", "Mine" })
            {
                foreach (var kv in Resource(view, "en"))
                    Assert.Equal(kv.Key, kv.Value);
            }
        }

        // =========================================================================================
        // THE PLANNED-VS-ACTUAL PANEL specifically (§E/§G).
        // =========================================================================================

        [Fact]
        public void The_planned_versus_actual_panel_uses_localized_labels_for_every_figure_it_shows()
        {
            var used = KeysUsedBy("Mine");

            // Each label the panel puts beside a number. Hardcoding any of these would leave an
            // English word next to an Arabic figure, which is the exact defect this batch is closing.
            foreach (var required in new[]
                     {
                         "Actual", "Late", "Early", "m",
                         "Approved leave", "No attendance recorded",
                         "Overtime candidate — not a payroll decision",
                         "Measured against your published roster",
                         "Measured against your standard working hours",
                     })
            {
                Assert.True(used.Contains(required),
                    $"The planned-vs-actual panel no longer localizes \"{required}\" — a hardcoded "
                    + "label would render English beside an Arabic number.");
            }
        }

        [Fact]
        public void Overtime_is_labelled_a_candidate_rather_than_a_payroll_decision()
        {
            // Wording, asserted. Overtime here is a workforce fact; whether those minutes are payable
            // is payroll's judgement, and a screen that said "Overtime pay" would be making it.
            var ar = Resource("Mine", "ar");
            var fr = Resource("Mine", "fr");
            const string key = "Overtime candidate — not a payroll decision";

            Assert.Contains("مرشحة", ar[key]);          // "candidate"
            Assert.Contains("potentielles", fr[key]);   // "potential"
        }
    }
}
