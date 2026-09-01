using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch D1 / CORRECTION-005 — the structural guard.
    //
    // The measurement defect this pins: the authorization-coverage metric asks *"is a role checked?"* and never
    // *"where did the company come from?"*. So 150 attribute-protected mutating actions were counted as protected
    // while resolving their company from a compile-time constant. A role check above a literal company is a
    // control over WHO, with no control over WHICH TENANT.
    //
    // Wave 1 does NOT sweep all 931 references — that is a separate batch with its own regression surface, and
    // the brief explicitly forbids a blind sweep here. What this file does instead is make the number VISIBLE and
    // MONOTONIC: it cannot grow without a test failing, and it cannot be forgotten, because the count is asserted
    // rather than described in a document nobody re-reads.
    public class Correction005StructuralTests
    {
        // Every controller holding a hardcoded company, with its constant name. TWELVE files, measured — not
        // estimated. A NEW entry here means someone added a thirteenth hardcoded company, which is the
        // regression this test exists to stop.
        //
        // WAS THIRTEEN. TasksController's `private const int DefaultCompanyId = 1` was genuinely removed (the
        // controller now resolves its company through IRequestCompanyResolver), but the removal left the old
        // declaration behind as an EXPLANATORY COMMENT — and DeclaringFiles() matched the comment, so the
        // ratchet went on counting a constant that no longer exists. The baseline over-reported the debt by
        // one and, worse, silently absorbed a real remediation that should have forced this list to be
        // updated. Stripping comments before matching (see DeclaringFiles) is what surfaced it.
        private static readonly string[] KnownHardcodedCompanyControllers =
        {
            "AccountingController.cs",          // DefaultCompanyId
            "AdminController.cs",               // HrCompanyId
            "Api/InventoryApiController.cs",    // CompanyId
            "BrandController.cs",               // DefaultCompanyId
            "CurrencyController.cs",            // DefaultCompanyId
            "HyperController.cs",               // PosCompanyId
            "HyperPosController.cs",            // PosCompanyId
            "PosAppController.cs",              // PosCompanyId
        };

        // WAS TWELVE, NOW EIGHT. Four more were genuinely removed between the baseline and platform
        // stabilization - CrmController, InventoryController, PosController and ProjectController all
        // resolve through IRequestCompanyResolver now - and this test is the reason the removals are
        // RECORDED rather than absorbed: its second assertion refuses to let the ratchet quietly
        // loosen. Recording a removal is as much its job as catching an addition.
        //
        // The four that remain are POS and currency surfaces where company 1 is the documented
        // catalogue tenant; they are classified in docs/stabilization/COMPANY-ONE-INVENTORY.md rather
        // than left as an unexplained residue.
        private const int KnownCount = 8;

        private static string ControllersRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "CrossBuy", "Controllers");
        }

        private static readonly System.Text.RegularExpressions.Regex Declaration =
            new(@"const\s+int\s+(DefaultCompanyId|HrCompanyId|PosCompanyId|CompanyId|CompanyID)\s*=",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Comments are stripped BEFORE matching, so the ratchet counts code rather than prose.
        //
        // This is a tightening, not a relaxation: 12 is a stricter ceiling than the 13 that counted
        // TasksController's commented-out declaration. Without it the guard cannot tell a real constant from a
        // note explaining that the constant was deleted — and the note is exactly what a careful remediation
        // leaves behind, so the guard was blindest at the moment it most needed to notice.
        private static string StripComments(string source)
        {
            // Block comments first: a `/* … */` may span lines and contain `//`.
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);

            var sb = new System.Text.StringBuilder(withoutBlocks.Length);
            foreach (var line in withoutBlocks.Split('\n'))
            {
                int slash = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(slash >= 0 ? line[..slash] : line).Append('\n');
            }
            return sb.ToString();
            // A `//` inside a string literal would be trimmed too. That can only ever REMOVE a match, and a
            // company constant is never declared inside a string, so the guard stays conservative.
        }

        private static List<string> DeclaringFiles()
        {
            var root = ControllersRoot();
            return Directory.GetFiles(root, "*Controller.cs", SearchOption.AllDirectories)
                .Where(f => Declaration.IsMatch(StripComments(File.ReadAllText(f))))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        // ============================================================================================

        [Fact]
        public void The_hardcoded_company_constant_count_has_not_grown()
        {
            var found = DeclaringFiles();

            Assert.Equal(KnownCount, found.Count);

            // Named individually, because "thirteen controllers" is not a finding anyone can act on.
            var unexpected = found.Except(KnownHardcodedCompanyControllers, StringComparer.Ordinal).ToList();
            Assert.True(unexpected.Count == 0,
                "A NEW controller hardcodes its company. Resolve it from BusinessContext via " +
                "IRequestCompanyResolver instead (CORRECTION-005): " + string.Join(", ", unexpected));
        }

        // The other direction: when a wave removes one, this fails so the count and the report are updated
        // together rather than the documentation drifting behind the code.
        [Fact]
        public void A_removed_constant_must_be_recorded_rather_than_silently_reducing_the_baseline()
        {
            var found = DeclaringFiles();
            var goneButStillListed = KnownHardcodedCompanyControllers
                .Except(found, StringComparer.Ordinal).ToList();

            Assert.True(goneButStillListed.Count == 0,
                "A hardcoded company constant was REMOVED — good. Update KnownHardcodedCompanyControllers, " +
                "KnownCount and the Wave report's remaining count in the same commit: " +
                string.Join(", ", goneButStillListed));
        }

        // The endpoint Wave 1 actually remediated must no longer read the constant. This is the narrow,
        // per-endpoint claim Wave 1 is allowed to make — as opposed to "the controller is company-safe", which
        // would be false: AccountingController still holds the constant for its other 198 references.
        [Fact]
        public void The_remediated_stamp_action_resolves_its_company_and_no_longer_reads_the_constant()
        {
            var path = Path.Combine(ControllersRoot(), "AccountingController.cs");
            var body = MethodBody(File.ReadAllText(path), "StampInvoiceCustomer");

            Assert.DoesNotContain("DefaultCompanyId", body);
            Assert.Contains("_company.ResolveAsync", body);
            Assert.Contains("scope.CompanyId", body);
        }

        [Fact]
        public void The_remediated_stamp_action_requires_the_accounting_post_right()
        {
            var text = File.ReadAllText(Path.Combine(ControllersRoot(), "AccountingController.cs"));
            var idx = text.IndexOf("StampInvoiceCustomer", StringComparison.Ordinal);
            Assert.True(idx > 0);

            // the attribute line sits immediately above the signature
            var window = text[Math.Max(0, idx - 400)..idx];
            Assert.Contains("AccPerm(\"post\")", window);
        }

        // The POS twin keeps its lane guard AND gains a role check. Both must be present: the lane guard is not
        // authorization, and removing it would lose the branch binding.
        [Fact]
        public void The_pos_stamp_action_keeps_its_branch_checks_and_gains_a_role_check()
        {
            var text = File.ReadAllText(Path.Combine(ControllersRoot(), "HyperPosController.cs"));
            var body = MethodBody(text, "StampInvoiceCustomer");

            Assert.Contains("_access.CanSell", body);                 // the new role check
            Assert.Contains("BranchOwnsInvoiceAsync", body);          // preserved branch ownership
            Assert.Contains("OfficialInvoice", body);                 // preserved capability gate
            Assert.Contains("PosLaneActivityGuard", text);            // preserved lane guard (controller level)
        }

        // Brace-matched extraction from the signature, so an assertion cannot accidentally read the NEXT method
        // and report a false pass (or a false failure — which is how this helper earned its existence).
        private static string MethodBody(string text, string methodName)
        {
            var sig = text.IndexOf(" " + methodName + "(", StringComparison.Ordinal);
            Assert.True(sig > 0, $"{methodName} not found");

            var open = text.IndexOf('{', sig);
            Assert.True(open > 0);

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text[open..(i + 1)];
                }
            }
            Assert.Fail($"unbalanced braces after {methodName}");
            return "";
        }
    }
}
