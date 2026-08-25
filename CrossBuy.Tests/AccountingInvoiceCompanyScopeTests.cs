using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // ACCOUNTING INVOICE READ PATHS — resolved company, and a view that actually renders.
    //
    // TWO DEFECTS BLOCKED THE INVOICE PRODUCT SURFACE, and they failed in opposite ways.
    //
    // 1. COMPANY. Six invoice read paths resolved company from DefaultCompanyId - the literal 1 -
    //    while the screens above them are company-scoped. A company-2 accountant saw company 1's
    //    invoices or an empty list, decided by which ids happened to exist rather than by policy.
    //    It failed SILENTLY: the page rendered, so nothing looked broken.
    //
    // 2. A MISSING PARTIAL. Three committed views include _DocEventTimeline, which was never
    //    committed. That one failed LOUDLY but only at runtime, and only for someone who could
    //    authenticate - PartialAsync resolves by name at render time, so the Razor compiler never
    //    sees it and a green build proves nothing about it.
    //
    // The second is why this file asserts a structural fact rather than only a behavioural one: a
    // test that needs a login to notice a missing partial is a test that does not run in CI.
    // ============================================================================================
    public class AccountingInvoiceCompanyScopeTests
    {
        // The six paths the invoice product surface reads through. Named, because this controller
        // still has ~191 other constant-company usages that are deliberately NOT in scope - a test
        // that swept the whole file would fail for reasons this fix never claimed to address.
        public static TheoryData<string> InvoiceReadPaths() => new()
        {
            "SalesInvoiceDetail",
            "PurchaseInvoiceDetail",
            "SalesInvoicesData",
            "PurchaseInvoicesData",
            "SalesInvoicesExport",
            "PurchaseInvoicesExport",
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Controller() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "AccountingController.cs"));

        // The action body, ending where the next action begins. Comments are stripped: the prose
        // around these methods legitimately discusses DefaultCompanyId, and an assertion that could
        // be broken by a comment is an assertion nobody trusts.
        private static string BodyOf(string action)
        {
            var source = Controller();
            int start = source.IndexOf("public async Task<IActionResult> " + action + "(", StringComparison.Ordinal);
            Assert.True(start >= 0, action + " not found");

            int next = source.IndexOf("public async Task<IActionResult> ", start + 10, StringComparison.Ordinal);
            var body = next > 0 ? source[start..next] : source[start..];

            return string.Join("\n", body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        [Theory]
        [MemberData(nameof(InvoiceReadPaths))]
        public void The_invoice_read_path_resolves_its_company(string action)
        {
            var body = BodyOf(action);

            Assert.Contains("_company.ResolveAsync()", body, StringComparison.Ordinal);
            Assert.Contains("scope.CompanyId", body, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(InvoiceReadPaths))]
        public void The_invoice_read_path_has_no_constant_company_fallback(string action)
        {
            // The whole defect in one assertion. A fallback is worse than the original constant,
            // because it looks like remediation while still answering for company 1.
            Assert.DoesNotContain("DefaultCompanyId", BodyOf(action), StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(InvoiceReadPaths))]
        public void An_unresolved_company_refuses_before_any_data_is_read(string action)
        {
            var body = BodyOf(action);

            int guard = body.IndexOf("if (!scope.Ok)", StringComparison.Ordinal);
            int read = body.IndexOf("_context.", StringComparison.Ordinal);

            Assert.True(guard >= 0, action + " does not refuse an unresolved scope");
            Assert.True(read < 0 || guard < read,
                action + " touches the database before checking whether the company resolved");
        }

        [Theory]
        [MemberData(nameof(InvoiceReadPaths))]
        public void The_company_is_never_taken_from_the_request(string action)
        {
            var body = BodyOf(action);

            // ResolveAsync is called with NO argument on these actions: there is no company parameter
            // to offer, so there is nothing a caller could supply for the resolver to validate.
            Assert.Contains("_company.ResolveAsync()", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ResolveAsync(companyId", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ResolveAsync(request", body, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("SalesInvoiceDetail", "SalesInvoices")]
        [InlineData("PurchaseInvoiceDetail", "PurchaseInvoices")]
        public void A_foreign_invoice_is_excluded_in_the_query_not_after_it(string action, string listAction)
        {
            var body = BodyOf(action);

            // The company predicate lives in the WHERE clause, so a foreign invoice is simply not
            // found. Missing and inaccessible then take the identical exit, which is what stops a
            // caller learning that an id exists in a company they cannot see.
            Assert.Matches(new Regex(@"i\.ID == id && i\.CompanyID == scope\.CompanyId"), body);
            Assert.Contains("if (inv == null) return RedirectToAction(nameof(" + listAction + "));", body, StringComparison.Ordinal);

            // And exactly one exit for both cases - no second branch that could describe them apart.
            Assert.Equal(1, Regex.Matches(body, @"if \(inv == null\)").Count);
        }

        [Fact]
        public void The_export_paths_emit_no_workbook_when_the_company_is_unresolved()
        {
            // A spreadsheet leaves the application and cannot be recalled, so an unresolved scope
            // must not reach the exporter at all.
            foreach (var action in new[] { "SalesInvoicesExport", "PurchaseInvoicesExport" })
            {
                var body = BodyOf(action);
                int guard = body.IndexOf("if (!scope.Ok)", StringComparison.Ordinal);
                int build = body.IndexOf("ExcelExporter.Build", StringComparison.Ordinal);

                Assert.True(guard >= 0 && build > guard, action + " could export before resolving a company");
            }
        }

        [Fact]
        public void The_scope_of_this_fix_was_held_to_the_six_read_paths()
        {
            // Stated as a test so the restraint is durable: this controller carries a large constant-
            // company debt that this change deliberately did NOT sweep. If someone later "finishes the
            // job" in one pass, that is a decision worth making on purpose, and this failing is the
            // prompt to make it rather than a rubber stamp.
            var remaining = Regex.Matches(Controller(), @"\bDefaultCompanyId\b").Count;

            Assert.True(remaining > 0,
                "the controller-wide constant-company debt appears closed; if that was intended, retire this test");
        }

        // ---- the missing-partial class of defect --------------------------------------------------

        // The same defect exists on three more screens, found by the sweep below while proving the
        // invoice ones were closed. They are OUT OF SCOPE for an invoice unblock and are pinned here
        // rather than fixed quietly or left invisible: each needs its own owner decision about whether
        // the partial should be landed or the include removed, exactly as _DocEventTimeline did.
        private static readonly string[] KnownMissingPartials =
        {
            "_DocTimeline  <- CrossBuy\\Views\\Inventory\\QuotationDetails.cshtml",
            "_HrDocGallery  <- CrossBuy\\Views\\Admin\\DocExpiryAlerts.cshtml",
            "_HrDocGallery  <- CrossBuy\\Views\\Admin\\HrDocuments.cshtml",
        };

        [Fact]
        public void No_view_gains_a_partial_that_does_not_exist()
        {
            var root = RepoRoot();
            var views = Path.Combine(root, "CrossBuy", "Views");

            var present = new HashSet<string>(
                Directory.EnumerateFiles(Path.Combine(views, "Shared"), "_*.cshtml")
                    .Select(f => Path.GetFileNameWithoutExtension(f)!),
                StringComparer.Ordinal);

            var missing = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var view in Directory.EnumerateFiles(views, "*.cshtml", SearchOption.AllDirectories))
                foreach (Match m in Regex.Matches(File.ReadAllText(view), @"PartialAsync\(""(_[A-Za-z0-9]+)""\)"))
                {
                    var name = m.Groups[1].Value;
                    if (!present.Contains(name))
                        missing.Add($"{name}  <- {Path.GetRelativePath(root, view)}");
                }

            // PartialAsync resolves BY NAME at render time, so the Razor compiler never validates it and
            // a green build proves nothing - the only other thing that notices is a user on the screen.
            // This asserts the EXACT known set, so a new one fails here immediately, and closing one of
            // the three also fails here, which is the prompt to delete its line rather than forget it.
            Assert.Equal(KnownMissingPartials.OrderBy(x => x, StringComparer.Ordinal).ToList(), missing.ToList());
        }

        [Theory]
        [InlineData("SalesInvoiceDetail")]
        [InlineData("PurchaseInvoiceDetail")]
        public void The_invoice_detail_view_can_resolve_every_partial_it_includes(string view)
        {
            var root = RepoRoot();
            var source = File.ReadAllText(Path.Combine(root, "CrossBuy", "Views", "Accounting", view + ".cshtml"));

            var includes = Regex.Matches(source, @"PartialAsync\(""(_[A-Za-z0-9]+)""\)")
                .Select(m => m.Groups[1].Value).Distinct().ToList();

            Assert.NotEmpty(includes);   // a view that includes nothing would pass vacuously
            foreach (var name in includes)
                Assert.True(File.Exists(Path.Combine(root, "CrossBuy", "Views", "Shared", name + ".cshtml")),
                    $"{view} includes {name}, which is not in Views/Shared");
        }

        [Fact]
        public void The_business_conversation_surface_is_still_the_one_the_invoice_views_use()
        {
            var root = Path.Combine(RepoRoot(), "CrossBuy", "Views");

            // Guards the thing this unblock exists to make visible: landing the event-timeline partial
            // must not have displaced the Communication product on these screens.
            foreach (var view in new[] { "SalesInvoiceDetail", "PurchaseInvoiceDetail" })
            {
                var source = File.ReadAllText(Path.Combine(root, "Accounting", view + ".cshtml"));
                Assert.Contains("_EntityConversation", source, StringComparison.Ordinal);
            }

            Assert.True(File.Exists(Path.Combine(root, "Shared", "_EntityConversation.cshtml")));
        }
    }
}
