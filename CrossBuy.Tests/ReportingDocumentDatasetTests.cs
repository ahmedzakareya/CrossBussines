using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // THE PRINTED DOCUMENTS — the part of the reporting surface that reaches a customer's or an
    // employee's hand, and the part that had NO test at all.
    //
    // Measured 2026-11-10: six dataset-code families were referenced by neither symbol nor literal
    // anywhere in CrossBuy.Tests — TradeDocumentDatasetCodes, StockDocumentDatasetCodes,
    // HrDocumentDatasetCodes, HrPayslipDatasetCodes, WorkingHoursDatasetCodes, RosterDatasetCodes.
    // Between them they are the sales and purchase invoices, the goods receipt and delivery note,
    // the stock count and write-off, the payslip, the HR letter and the working-hours schedule.
    //
    // That matters more than an untested analytic: the owner rule is that nothing prints except
    // through Report Studio, so these datasets are not an optional view of the data — they are the
    // only path by which the product issues an official document.
    //
    // NO DATABASE OF ANY KIND. Definitions are pure, and the authorisation rule under test is a pure
    // function of a BusinessContext. The row-building half stays unverified and is declared as such
    // rather than implied by a green count.
    // ============================================================================================
    public class ReportingDocumentDatasetTests
    {
        private sealed class AlwaysAllow : IReportPermissionEvaluator
        {
            public Task<bool> HasPermissionAsync(string permissionKey, BusinessContext context,
                CancellationToken cancellationToken = default) => Task.FromResult(true);
        }

        private static IEnumerable<ReportDatasetDefinition> AllDocuments() =>
            TradeDocumentDatasets.All()
                .Concat(StockDocumentDatasets.All())
                .Concat(new[] { HrDocumentDatasets.Letter(), HrDocumentDatasets.EmployeeDocuments() })
                .Concat(new[] { HrPayslipDataset.Definition() });

        // ---- they are declared, and declared legally ---------------------------------------------

        // ReportDatasetRegistry validates every definition IN ITS CONSTRUCTOR, so building one over the
        // document packs is the whole declaration check: a duplicate code, an empty field key, a
        // malformed format or a bad aggregate throws here rather than when somebody presses print.
        [Fact]
        public void Every_document_dataset_constructs_and_validates()
        {
            var documents = AllDocuments().ToList();
            Assert.NotEmpty(documents);   // positive control: the packs really were enumerated

            var registry = new ReportDatasetRegistry(
                documents.Cast<IReportDatasetDefinition>(), new AlwaysAllow());

            foreach (var d in documents)
                Assert.True(registry.TryResolve(d.DatasetCode, out _), $"{d.DatasetCode} did not resolve.");

            Assert.Equal(documents.Count, registry.RegisteredCodes.Count);
        }

        // Every document must name a permission key. A dataset with none is not "open", it is a
        // dataset nobody decided about - and these print invoices and payslips.
        [Fact]
        public void Every_document_names_a_permission_key()
        {
            Assert.All(AllDocuments(), d => Assert.False(
                string.IsNullOrWhiteSpace(d.RequiredPermissionKey),
                $"{d.DatasetCode} declares no permission key."));
        }

        // ---- the two that answer to "any authenticated caller" ------------------------------------

        // THE PAYSLIP AND THE HR LETTER DECLARE ReportPermissions.Public, WHICH IS CORRECT AND LOOKS
        // ALARMING. Public means "any authenticated caller in a resolved company" - the weakest key the
        // platform can express - because a dataset KEY cannot say "your own payslip". The real rule is
        // a row-level one: HrPayslipDataSource.MayRead requires the same company AND either that the
        // caller IS the subject or that they hold an HR/administration role.
        //
        // So the key being Public is not the defect it resembles; the defect would be the row gate
        // going missing while the key stayed Public, and nothing in this repository noticed that until
        // this test. The letter REUSES the payslip's rule rather than copying it, which is why one
        // guard covers both.
        [Theory]
        [InlineData("Hr.Payslip")]
        [InlineData("Hr.Letter")]
        public void A_document_answering_to_public_is_row_gated_instead(string code)
        {
            var document = Assert.Single(AllDocuments().Where(d => d.DatasetCode == code));
            Assert.Equal(ReportPermissions.Public, document.RequiredPermissionKey);

            var source = SourceFileFor(code);
            Assert.Contains("MayRead(", source, StringComparison.Ordinal);
        }

        // ...and the rule itself, exercised rather than grepped. A source scan proves the CALL exists;
        // this proves the call REFUSES. Without it, MayRead could be rewritten to return true and every
        // assertion above would still pass.
        [Fact]
        public void The_payslip_rule_refuses_another_employees_document()
        {
            var mine = new BusinessContext { CompanyId = 7, EmployeeId = 41, Source = BusinessContextSource.Test };

            Assert.True(HrPayslipDataSource.MayRead(mine, slipCompany: 7, slipEmployee: 41),
                "a caller must be able to read their OWN document, or the positive case is untested.");

            Assert.False(HrPayslipDataSource.MayRead(mine, slipCompany: 7, slipEmployee: 42),
                "a colleague's document in the same company must be refused.");

            Assert.False(HrPayslipDataSource.MayRead(mine, slipCompany: 9, slipEmployee: 41),
                "the SAME employee id in another company must be refused - ids are not unique across tenants.");
        }

        // An unresolved tenant refuses before anything is read. Company 0 is "nobody", not "everybody".
        [Fact]
        public void The_payslip_rule_refuses_an_unresolved_company()
        {
            var nobody = new BusinessContext { CompanyId = 0, EmployeeId = 41, Source = BusinessContextSource.Test };

            Assert.False(HrPayslipDataSource.MayRead(nobody, slipCompany: 7, slipEmployee: 41));
        }

        // ---- what a document may NOT do -----------------------------------------------------------

        // A PAYSLIP MUST NOT BE SHAREABLE OR SCHEDULABLE. Sharing hands a link to somebody the row gate
        // never saw, and a schedule mails it on a timer to whoever the schedule names - both of which
        // step straight around MayRead. This is the capability-level half of the same protection.
        [Theory]
        [InlineData("Hr.Payslip")]
        [InlineData("Hr.Letter")]
        [InlineData("Hr.EmployeeDocuments")]
        public void A_personal_document_is_neither_shareable_nor_schedulable(string code)
        {
            var definition = Assert.Single(
                new HrDocumentReportDefinitionProvider().GetDefinitions()
                    .Concat(new HrPayslipReportDefinitionProvider().GetDefinitions())
                    .Where(d => d.Code == code));

            Assert.False(definition.Capabilities.AllowShare, $"{code} must not be shareable.");
            Assert.False(definition.Capabilities.AllowSchedule, $"{code} must not be schedulable.");
        }

        // ---- the source file behind each code -----------------------------------------------------

        private static string SourceFileFor(string datasetCode)
        {
            var file = datasetCode switch
            {
                "Hr.Payslip" => "HrPayslipDataset.cs",
                "Hr.Letter" => "HrDocumentDatasets.cs",
                _ => throw new ArgumentOutOfRangeException(nameof(datasetCode), datasetCode, "no source mapped"),
            };

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);

            var path = Path.Combine(dir!.FullName, "CrossBuy", "BL", "Reporting", file);
            Assert.True(File.Exists(path), $"{file} is not where this test expects it.");
            return File.ReadAllText(path);
        }
    }
}
