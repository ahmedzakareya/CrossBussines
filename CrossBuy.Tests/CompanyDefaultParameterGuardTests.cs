using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests
{
    // A COMPLEMENTARY detector for dangerous company defaults that CORRECTION-005's ratchet cannot see.
    //
    // WHY IT EXISTS. The existing ratchet matches DECLARATIONS — `const int DefaultCompanyId = 1` and its
    // siblings. It found twelve controllers and pins them. It never looked at PARAMETER DEFAULTS, and
    // that blind spot hid a worse defect than any it caught: Api/AiController exposed six JWT-authenticated
    // endpoints taking `int companyId = 1` straight from the query string. Four of the tables they read
    // carry no global company filter, so `?companyId=7` returned another tenant's chart of accounts,
    // cost centres, bank accounts and cash boxes — and three of the endpoints forwarded that data to an
    // external processor.
    //
    // A declaration-only ratchet reports "12 controllers, no growth" while that endpoint sits open. This
    // closes the blind spot rather than widening the original regex, because the two patterns need
    // different remedies: a constant is refactored, a parameter default is REMOVED from the signature.
    //
    // Scope note: this increment is NOT the CORRECTION-005 remediation wave. Anything found beyond the
    // AI path is REPORTED, not repaired.
    public class CompanyDefaultParameterGuardTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // `int companyId = 1` / `int CompanyID = 65` as a PARAMETER DEFAULT.
        //
        // TWO EXCLUSIONS, both learned by getting this wrong first — the initial version of this guard
        // reported three controllers and ALL THREE were false positives:
        //
        //   * `= 0` is EXCLUDED. Zero is the "not supplied" sentinel, not a tenant. AccountingApiController
        //     uses it correctly: the company is resolved from the authenticated BusinessContext, and a
        //     supplied non-zero id that disagrees is REFUSED rather than coerced
        //     (AccountingApiAuthorization.AuthorizeAsync). Flagging that pattern would train people to
        //     ignore this guard, which is worse than not having it.
        //
        //   * `const` declarations are EXCLUDED. `const int CompanyId = 1` is a DECLARATION, already
        //     counted by Correction005StructuralTests. Matching it here would double-report the same debt
        //     under a guard whose whole purpose is the pattern that ratchet CANNOT see.
        //
        // `[1-9]\d*` therefore matches a real company literal and nothing else.
        private static readonly Regex CompanyDefault =
            new(@"(?<!const\s)\bint\s+(companyId|CompanyId|companyID|CompanyID)\s*=\s*[1-9]\d*",
                RegexOptions.Compiled);

        // Comments are stripped before matching — the same lesson the CORRECTION-005 ratchet had to learn
        // when a remediation left its old declaration behind as an explanatory note and the guard went on
        // counting it.
        private static string StripComments(string source)
        {
            var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var sb = new System.Text.StringBuilder(withoutBlocks.Length);
            foreach (var line in withoutBlocks.Split('\n'))
            {
                int slash = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(slash >= 0 ? line[..slash] : line).Append('\n');
            }
            return sb.ToString();
        }

        private static List<string> OffendingFiles(string relativeDir, string pattern)
        {
            var root = Path.Combine(RepoRoot(), relativeDir);
            if (!Directory.Exists(root)) return new List<string>();

            return Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
                .Where(f => CompanyDefault.IsMatch(StripComments(File.ReadAllText(f))))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        // ---- the AI path specifically: this is what the increment repaired and must stay repaired ----
        [Fact]
        public void The_ai_controller_takes_no_company_from_the_caller()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "Api", "AiController.cs"));
            var code = StripComments(src);

            Assert.False(CompanyDefault.IsMatch(code),
                "Api/AiController has a company parameter default again. Company must come from " +
                "IRequestCompanyResolver; a caller-supplied company on this controller is a cross-company " +
                "read AND an external egress.");

            // ...and the trusted resolver is actually used.
            Assert.Contains("_company.ResolveAsync()", code, StringComparison.Ordinal);
        }

        // Every endpoint must resolve before it queries. Checked per-action, so an endpoint added later
        // without the resolver is caught rather than being covered by its neighbours.
        [Fact]
        public void Every_ai_controller_endpoint_resolves_the_company_before_using_it()
        {
            var code = StripComments(File.ReadAllText(
                Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "Api", "AiController.cs")));

            var actions = Regex.Matches(code, @"\[Http(Get|Post)\(""([^""]*)""\)\]");
            Assert.True(actions.Count >= 6, $"expected at least 6 AI endpoints, found {actions.Count}");

            foreach (Match action in actions)
            {
                // The body of this action, up to the next attribute or the end of the file.
                int start = action.Index;
                int next = code.IndexOf("[Http", start + 5, StringComparison.Ordinal);
                var body = next > 0 ? code[start..next] : code[start..];

                Assert.True(body.Contains("ResolveCompanyAsync()", StringComparison.Ordinal),
                    $"AI endpoint '{action.Groups[2].Value}' does not resolve its company from the trusted context.");
            }
        }

        // ---- the wider surface: MEASURED and pinned, not repaired here ----
        //
        // A ratchet rather than a hard ban: the pattern exists elsewhere in the tree and remediating it is
        // the CORRECTION-005 wave's work, not this increment's. What must not happen is the count GROWING
        // while nobody is looking — which is exactly how the AI controller stayed invisible.
        // MEASURED, not estimated — and every entry is a FINDING this guard surfaced the moment it
        // existed, which is the argument for the guard. Api/AiController is deliberately ABSENT: it was
        // repaired, and if the pattern returns there the test above fails independently of this list.
        //
        // TRIAGED IN INCREMENT 4, and the list SHRANK because two of the three earlier entries were false
        // positives of my own detector, not defects:
        //
        //   Api/AccountingApiController.cs  — CLEARED. Its 13 parameters default to `= 0` ("not
        //                                     supplied"), and AccountingApiAuthorization resolves the
        //                                     company from the authenticated BusinessContext, REFUSING a
        //                                     supplied id that disagrees. That is the correct pattern.
        //   Api/InventoryApiController.cs   — CLEARED here. Its `const int CompanyId = 1` is a
        //                                     DECLARATION already counted by Correction005StructuralTests;
        //                                     double-reporting it under a parameter-default guard would
        //                                     confuse the two remedies (refactor vs remove-the-parameter).
        //   Api/DevSeedController.cs        — CLEARED IN INCREMENT 4.1. Its 69 `int companyId = 1`
        //                                     parameter defaults now read
        //                                     `= DevSeedFixture.DefaultCompanyId`. Behaviour is
        //                                     unchanged — company 1 is still the dev seed tenant — but
        //                                     the intent is now NAMED instead of being a bare literal
        //                                     indistinguishable from hidden tenant authority.
        //
        // THE LIST IS THEREFORE EMPTY, and that is the honest current state rather than a suppression:
        // no controller carries a company parameter default any more.
        private static readonly string[] KnownControllerParameterDefaults =
            System.Array.Empty<string>();

        [Fact]
        public void The_company_parameter_default_surface_in_controllers_has_not_grown()
        {
            var found = OffendingFiles(Path.Combine("CrossBuy", "Controllers"), "*.cs");

            var unexpected = found.Except(KnownControllerParameterDefaults, StringComparer.Ordinal).ToList();

            Assert.True(unexpected.Count == 0,
                "A controller gained a company PARAMETER DEFAULT (e.g. `int companyId = 1`). This pattern is " +
                "invisible to Correction005StructuralTests, which matches declarations only. Resolve the " +
                "company from IRequestCompanyResolver and remove the parameter: " + string.Join(", ", unexpected));
        }

        // The other direction, mirroring Correction005StructuralTests: when an entry is legitimately
        // repaired, this fails so the baseline and the report are updated together instead of the list
        // quietly describing a defect that no longer exists.
        [Fact]
        public void A_repaired_controller_must_be_removed_from_the_baseline_rather_than_left_listed()
        {
            var found = OffendingFiles(Path.Combine("CrossBuy", "Controllers"), "*.cs");
            var goneButStillListed = KnownControllerParameterDefaults.Except(found, StringComparer.Ordinal).ToList();

            Assert.True(goneButStillListed.Count == 0,
                "A controller's company parameter default was REMOVED — good. Update " +
                "KnownControllerParameterDefaults and the report in the same commit: " +
                string.Join(", ", goneButStillListed));
        }

        // The named fixture must remain exactly that — a DEV fixture. If it leaked into a production code
        // path the increment would have replaced a VISIBLE literal with an INVISIBLE one, which is worse
        // than leaving it alone.
        [Fact]
        public void The_dev_seed_fixture_constant_is_referenced_only_by_the_dev_only_surface()
        {
            var root = Path.Combine(RepoRoot(), "CrossBuy");
            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                var name = Path.GetFileName(file);
                if (name is "DevSeedFixture.cs" or "DevSeedController.cs") continue;   // its definition and its one user

                Assert.DoesNotContain("DevSeedFixture", StripComments(File.ReadAllText(file)), StringComparison.Ordinal);
            }
        }

        // The guard must actually be capable of detecting the pattern — a regex that matches nothing would
        // pass the ratchet above forever and prove nothing.
        [Fact]
        public void The_detector_recognises_the_pattern_it_exists_to_find()
        {
            foreach (var sample in new[]
            {
                "public async Task<IActionResult> X(int companyId = 1)",
                "public IActionResult Y(string? q, int companyId = 65)",
                "void Z(int CompanyID = 1)",
            })
                Assert.Matches(CompanyDefault, sample);

            // ...and must NOT flag ordinary parameters, or it would be noise nobody acts on.
            foreach (var safe in new[]
            {
                "public IActionResult A(int companyId)",
                "public IActionResult B(int horizonDays = 90)",
                "public IActionResult C(int slowDays = 90, int take = 10)",
                "var companyId = scope.CompanyId;",
                // `= 0` is the "not supplied" sentinel — the CORRECT pattern, where the company is
                // resolved from context and a mismatched supplied value is refused.
                "public IActionResult D(int companyId = 0, CancellationToken ct = default)",
                // a const DECLARATION belongs to the other ratchet, not this one
                "private const int CompanyId = 1;",
            })
                Assert.DoesNotMatch(CompanyDefault, safe);
        }

        // A commented-out example must not count — the same false positive that once made the
        // CORRECTION-005 baseline over-report by one.
        [Fact]
        public void A_commented_out_default_is_not_counted()
            => Assert.False(CompanyDefault.IsMatch(StripComments("// public IActionResult X(int companyId = 1)\n")));
    }
}
