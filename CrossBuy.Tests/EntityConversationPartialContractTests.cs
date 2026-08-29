using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // _EntityConversation — the shared conversation panel's CALLER CONTRACT.
    //
    // The partial used to resolve its own endpoints:
    //
    //     var convListUrl = Url.Action("InvoiceConversation", "Accounting");
    //     var convAddUrl  = Url.Action("InvoiceConversationAdd", "Accounting");
    //
    // while its own header claimed it "knows nothing about invoices". Any second module that
    // included it would have posted its messages at the Accounting controller, to be judged by a
    // permission gate written for invoices - a silent wrong-module write, not a visible error.
    //
    // The endpoints are now the including view's to name. These tests hold that line: the partial
    // must stay module-neutral, and every caller must name its own two endpoints. Nothing here
    // asserts that a string exists somewhere in the file - the checks read the EXECUTABLE Razor with
    // comments stripped, because the header legitimately shows Accounting's URLs as the worked
    // example and a naive text search would be satisfied by that comment forever.
    // ============================================================================================
    public class EntityConversationPartialContractTests
    {
        private const string Partial = "CrossBuy/Views/Shared/_EntityConversation.cshtml";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        // Razor carries two comment forms and the partial uses both: @* *@ around markup, and // inside
        // the @{ } block. Strip both, so what remains is only what actually runs.
        private static string Executable(string source)
        {
            var withoutRazor = Regex.Replace(source, @"@\*.*?\*@", "", RegexOptions.Singleline);
            return string.Join("\n", withoutRazor.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        // 1. The partial carries no Accounting dependency.
        [Fact]
        public void The_shared_partial_names_no_module_and_no_controller()
        {
            var code = Executable(Read(Partial));

            // The exact pair that was there. Both fail on the pre-change file.
            Assert.DoesNotContain("InvoiceConversation", code, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Accounting\"", code, StringComparison.Ordinal);

            // And no route resolution of ANY kind, so the next module cannot be hardcoded either -
            // including a default that would quietly restore the old behaviour.
            Assert.DoesNotMatch(new Regex(@"Url\.Action\s*\("), code);
            Assert.DoesNotContain("Inventory", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Quotation", code, StringComparison.Ordinal);

            // It takes them from the caller instead.
            Assert.Contains("ViewBag.ConversationListUrl", code, StringComparison.Ordinal);
            Assert.Contains("ViewBag.ConversationAddUrl", code, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unwired_caller_gets_a_visible_failure_rather_than_an_empty_conversation()
        {
            var code = Executable(Read(Partial));

            // Server side: the not-configured branch exists and is distinct from loading/empty/503.
            Assert.Contains("convWired", code, StringComparison.Ordinal);
            Assert.Contains("Conversations are not configured for this screen", code, StringComparison.Ordinal);

            // Client side: with no endpoints the script must return before fetching. Without this guard a
            // null list url resolves against the CURRENT page, and the panel reports a load failure -
            // which reads as "Communication is broken" rather than "this view forgot two lines".
            var script = code[code.IndexOf("<script>", StringComparison.Ordinal)..];
            int guard = script.IndexOf("data-list') || !box.getAttribute('data-add')", StringComparison.Ordinal);
            int fetch = script.IndexOf("fetch(", StringComparison.Ordinal);
            Assert.True(guard >= 0 && guard < fetch,
                "the unwired guard must come before the first fetch");
        }

        // 2. Every caller names its own endpoints - checked by sweep, so a new caller cannot be added
        //    without them and quietly render the disabled panel.
        public static TheoryData<string> Callers()
        {
            var root = RepoRoot();
            var data = new TheoryData<string>();
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "CrossBuy", "Views"), "*.cshtml",
                         SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "_EntityConversation.cshtml") continue;
                if (File.ReadAllText(file).Contains("PartialAsync(\"_EntityConversation\")", StringComparison.Ordinal))
                    data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
            return data;
        }

        [Theory]
        [MemberData(nameof(Callers))]
        public void Every_including_view_supplies_all_four_values(string caller)
        {
            var code = Executable(Read(caller));

            foreach (var required in new[]
                     {
                         "ViewBag.ConversationEntityCode",
                         "ViewBag.ConversationEntityId",
                         "ViewBag.ConversationListUrl",
                         "ViewBag.ConversationAddUrl",
                     })
                Assert.Contains(required, code, StringComparison.Ordinal);
        }

        [Fact]
        public void The_sweep_actually_found_the_accounting_callers()
        {
            // Without this the theory above passes vacuously the day someone renames the partial.
            var found = Callers().Cast<object[]>().Select(row => (string)row[0]).ToList();

            Assert.Contains("CrossBuy/Views/Accounting/SalesInvoiceDetail.cshtml", found);
            Assert.Contains("CrossBuy/Views/Accounting/PurchaseInvoiceDetail.cshtml", found);
        }

        // 3. Accounting's behaviour is preserved: the SAME two actions it used before, still reachable.
        [Theory]
        [InlineData("CrossBuy/Views/Accounting/SalesInvoiceDetail.cshtml", "SalesInvoice")]
        [InlineData("CrossBuy/Views/Accounting/PurchaseInvoiceDetail.cshtml", "PurchaseInvoice")]
        public void Accounting_still_points_at_the_endpoints_it_always_used(string view, string entityCode)
        {
            var code = Executable(Read(view));

            Assert.Contains($"EntityRegistry.{entityCode}", code, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"ConversationListUrl\s*=\s*Url\.Action\(""InvoiceConversation"",\s*""Accounting""\)"), code);
            Assert.Matches(new Regex(@"ConversationAddUrl\s*=\s*Url\.Action\(""InvoiceConversationAdd"",\s*""Accounting""\)"), code);
        }

        [Theory]
        [InlineData("InvoiceConversation")]
        [InlineData("InvoiceConversationAdd")]
        public void The_accounting_actions_those_urls_name_exist(string action)
        {
            // Url.Action of a missing action renders an empty string, and the panel would then show the
            // not-configured notice on a screen that used to work. The names must resolve to real methods.
            var controller = Read("CrossBuy/Controllers/AccountingController.cs");
            Assert.Matches(new Regex($@"IActionResult>\s+{action}\s*\("), controller);
        }

        [Fact]
        public void The_accounting_write_endpoint_keeps_its_protection()
        {
            // Parameterizing a URL must not become a way to reach an unguarded write. This asserts the
            // guard on the POST is still there, on the committed controller, after the change.
            var controller = Read("CrossBuy/Controllers/AccountingController.cs");
            var idx = controller.IndexOf("InvoiceConversationAdd(", StringComparison.Ordinal);
            Assert.True(idx > 0);

            var preamble = controller[Math.Max(0, idx - 600)..idx];
            Assert.Contains("[HttpPost]", preamble, StringComparison.Ordinal);
            Assert.Contains("[ValidateAntiForgeryToken]", preamble, StringComparison.Ordinal);
            Assert.Contains("[SessionValidation]", preamble, StringComparison.Ordinal);
        }
    }
}
