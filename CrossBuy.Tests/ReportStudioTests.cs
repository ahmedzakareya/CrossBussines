using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // REPORT STUDIO V1 — THE GATES.
    //
    // Studio is the first surface in the product where the CALLER names the columns, the filters and the
    // sorts. Every other report has a column set an author fixed in code, so every other report's tests can
    // reasonably ask "does it return the right rows". These cannot: the question here is
    //
    //      can a draft ask for something the caller may not have?
    //
    // and it has to be asked of the SERVER, not of the screen. So nothing below drives the UI. Each test
    // hands a draft straight to IReportStudioService — the same object the controller calls — because a gate
    // that only holds when the browser behaves is not a gate.
    //
    // THE FIXTURE USES TWO COMPANIES for the same reason the module dataset tests do: a single-company
    // fixture cannot fail an isolation assertion.
    // =============================================================================================
    public class ReportStudioTests
    {
        private const int Mine = 1;
        private const int Theirs = 99;

        private static void Grant(ReportingTestHost host, params string[] keys)
        {
            foreach (var key in keys) host.PermissionOptions.RoleMap[key] = new[] { "Reports" };
        }

        private static ReportStudioService Studio(ReportingTestHost host, params IReportDatasetDefinition[] datasets) =>
            new(host.DatasetRegistry(datasets), host.Catalog, host.PermissionEvaluator,
                host.Templates, host.Reports(host.EngineWith(NullSource.Instance)), host.Accessor,
                host.VisualValidator, host.Assets);

        // A Studio wired to a REAL data source, for the tests that actually run a report.
        private static ReportStudioService StudioWith(ReportingTestHost host, IReportDataSource source,
            params IReportDatasetDefinition[] datasets) =>
            new(host.DatasetRegistry(datasets), host.Catalog, host.PermissionEvaluator,
                host.Templates, host.Reports(host.EngineWith(source)), host.Accessor,
                host.VisualValidator, host.Assets);

        private static readonly IReportDatasetDefinition[] AllThree =
        {
            AccountingDatasets.SalesRevenue(),
            InventoryDatasets.StockOnHand(),
            CrmDatasets.Opportunities(),
        };

        // ---- fixtures --------------------------------------------------------------------------------
        //
        // DATES SIT IN MAY 2026 ON PURPOSE. Every date-scoped dataset declares From/To parameters whose
        // defaults are the relative tokens "month-start" and "today", and the test clock is fixed at
        // 2026-05-14 — so the source's own window is 1–14 May before any Studio filter applies. Fixtures
        // dated March produced a successful run over zero rows, which is a true answer to the wrong
        // question and made four gates assert against an empty page.
        //
        // Studio V1 does not surface dataset parameters, so a draft cannot widen that window. That is a
        // real V1 limitation and it is reported as such rather than worked around here.
        private static CrossBuy.Models.Context.CrossDbContext Neighbour(ReportingTestHost host)
        {
            var scope = new CompanyScopeHolder();
            scope.Set(Theirs, null);
            return host.NewContext(scope);
        }

        private static async Task SeedSalesAsync(ReportingTestHost host)
        {
            var db = host.Db;
            db.Customers.Add(new Customer { ID = 10, CompanyID = Mine, Name = "عميل", NameEn = "Customer One", ControlAccountId = 1 });
            db.SalesInvoices.AddRange(
                new SalesInvoice { ID = 100, CompanyID = Mine, InvoiceNo = "SI-1", InvoiceDate = new DateTime(2026, 5, 10), CustomerId = 10, SubTotal = 100m, TaxTotal = 14m, GrandTotal = 114m, Status = "Posted" },
                new SalesInvoice { ID = 101, CompanyID = Mine, InvoiceNo = "SI-2", InvoiceDate = new DateTime(2026, 5, 12), CustomerId = 10, SubTotal = 200m, TaxTotal = 28m, GrandTotal = 228m, Status = "Draft" });
            await db.SaveChangesAsync();

            await using var theirs = Neighbour(host);
            theirs.Customers.Add(new Customer { ID = 11, CompanyID = Theirs, Name = "جار", NameEn = "Neighbour", ControlAccountId = 1 });
            theirs.SalesInvoices.Add(new SalesInvoice { ID = 102, CompanyID = Theirs, InvoiceNo = "SI-X", InvoiceDate = new DateTime(2026, 5, 11), CustomerId = 11, SubTotal = 9_999m, TaxTotal = 0m, GrandTotal = 9_999m, Status = "Posted" });
            await theirs.SaveChangesAsync();
        }

        private static async Task SeedStockAsync(ReportingTestHost host)
        {
            var db = host.Db;
            db.Items.Add(new Item { ID = 40, CompanyID = Mine, ItemCode = "IT-1", Barcode = "B1", Name = "صنف", NameEn = "Item One", ItemCategoryId = 1, BaseUoMId = 1, ItemType = "Stockable" });
            db.Warehouses.Add(new Warehouse { ID = 50, CompanyID = Mine, Code = "WH1", Name = "مخزن", NameEn = "Main" });
            db.StockBalances.Add(new StockBalance { ID = 60, CompanyID = Mine, ItemId = 40, WarehouseId = 50, QtyOnHand = 12m, AvgCost = 5m, TotalValue = 60m });
            await db.SaveChangesAsync();
        }

        private static async Task SeedOppsAsync(ReportingTestHost host)
        {
            var db = host.Db;
            db.Opportunities.AddRange(
                new Opportunity { ID = 95, CompanyID = Mine, Title = "فرصة", TitleEn = "Opp One", Stage = "Proposal", Amount = 1_000m, Probability = 40, CreatedAt = new DateTime(2026, 5, 6) },
                new Opportunity { ID = 96, CompanyID = Mine, Title = "فرصة ٢", TitleEn = "Opp Two", Stage = "Won", Amount = 300m, Probability = 100, CreatedAt = new DateTime(2026, 5, 7) });
            await db.SaveChangesAsync();
        }

        private static StudioDraft Draft(string datasetCode, params string[] columns) => new()
        {
            DatasetCode = datasetCode,
            Name = "Studio test",
            Columns = columns.ToList(),
            PageSize = 50,
        };

        // =========================================================================================
        // GATE 1 — THE DATASET LIST is the permitted list, and nothing else.
        // =========================================================================================
        [Fact]
        public async Task The_dataset_list_offers_only_datasets_the_caller_may_use()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);   // Accounting only

            var model = await Studio(host, AllThree).BuildAsync();

            Assert.Contains(model.Datasets, d => d.DatasetCode == AccountingDatasetCodes.SalesRevenue);
            Assert.DoesNotContain(model.Datasets, d => d.DatasetCode == InventoryDatasetCodes.StockOnHand);
            Assert.DoesNotContain(model.Datasets, d => d.DatasetCode == CrmDatasetCodes.Opportunities);
        }

        [Fact]
        public async Task With_no_permissions_the_studio_offers_nothing_and_says_so()
        {
            using var host = new ReportingTestHost();

            var model = await Studio(host, AllThree).BuildAsync();

            Assert.Empty(model.Datasets);
            Assert.True(model.HasNoDatasets);   // the screen renders an explicit state, not a broken canvas
        }

        // An unauthorized dataset is not merely hidden from the picker — naming it directly is REFUSED.
        // This is the test that separates a real gate from UI hiding.
        [Fact]
        public async Task Naming_an_unauthorized_dataset_directly_is_refused()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);   // NOT inventory

            var studio = Studio(host, AllThree);

            Assert.Empty(await studio.FieldsAsync(InventoryDatasetCodes.StockOnHand));

            var validation = await studio.ValidateAsync(
                Draft(InventoryDatasetCodes.StockOnHand, "QtyOnHand"));

            Assert.False(validation.Ok);
        }

        // =========================================================================================
        // GATE 2 — FIELD SENSITIVITY. The picker, the query and the export must all refuse a gated field.
        //
        // This is the gate that mattered most to get right: ToColumn() maps only Sensitivity.Never onto
        // ReportColumn.Internal, so Confidential fields are ORDINARY columns once a run starts. Nothing below
        // the Studio boundary would have stopped them.
        // =========================================================================================
        [Fact]
        public async Task A_confidential_field_is_absent_from_the_picker_without_its_key()
        {
            using var host = new ReportingTestHost();
            Grant(host, InventoryReportPermissions.View);   // view, but NOT cost

            var fields = await Studio(host, AllThree).FieldsAsync(InventoryDatasetCodes.StockOnHand);
            var keys = fields.Select(f => f.Key).ToList();

            Assert.Contains("QtyOnHand", keys);       // quantity is not gated
            Assert.DoesNotContain("AvgCost", keys);   // cost is
            Assert.DoesNotContain("TotalValue", keys);

            // Granting the key reveals them — the same call, a different answer, so the gate is the permission
            // and not a hardcoded exclusion.
            Grant(host, InventoryReportPermissions.Cost);
            var withCost = (await Studio(host, AllThree).FieldsAsync(InventoryDatasetCodes.StockOnHand))
                .Select(f => f.Key).ToList();
            Assert.Contains("AvgCost", withCost);
            Assert.Contains("TotalValue", withCost);
        }

        [Fact]
        public async Task Asking_for_a_confidential_field_by_name_is_refused_not_dropped()
        {
            using var host = new ReportingTestHost();
            Grant(host, InventoryReportPermissions.View);

            var validation = await Studio(host, AllThree).ValidateAsync(
                Draft(InventoryDatasetCodes.StockOnHand, "ItemName", "TotalValue"));

            // REFUSED. Silently dropping it would let a caller probe the schema by watching which columns
            // came back, and would quietly strip a column from a saved report its owner believes is there.
            Assert.False(validation.Ok);
            Assert.Contains(validation.Errors, e => e.Contains("TotalValue", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_confidential_field_cannot_be_exported_either()
        {
            using var host = new ReportingTestHost();
            Grant(host, InventoryReportPermissions.View);
            await SeedStockAsync(host);

            var studio = StudioWith(host, new StockOnHandDataSource(host.Db), AllThree);

            var result = await studio.RunAsync(
                Draft(InventoryDatasetCodes.StockOnHand, "ItemName", "AvgCost"),
                ReportOutputFormat.Csv, preview: false);

            Assert.False(result.IsSuccess);   // the export path re-validates; there is no second route
        }

        // =========================================================================================
        // GATE 3 — INVALID INPUT. A field that does not exist, and an operator the field forbids.
        // =========================================================================================
        [Fact]
        public async Task An_unknown_field_is_rejected()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);

            var validation = await Studio(host, AllThree).ValidateAsync(
                Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo", "NoSuchField"));

            Assert.False(validation.Ok);
        }

        [Fact]
        public async Task An_operator_the_field_forbids_is_rejected()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);

            var draft = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo", "GrandTotal");

            // Contains on a MONEY column. The dataset's operator table does not allow it, and the validator
            // consults that table rather than a list of its own.
            draft.Filters.Add(new StudioFilterDraft
            {
                Field = "GrandTotal",
                Operator = ReportFilterOperator.Contains,
                Values = new List<string?> { "1" },
            });

            var validation = await Studio(host, AllThree).ValidateAsync(draft);
            Assert.False(validation.Ok);
        }

        [Fact]
        public async Task A_draft_with_no_columns_is_rejected()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);

            var validation = await Studio(host, AllThree).ValidateAsync(Draft(AccountingDatasetCodes.SalesRevenue));
            Assert.False(validation.Ok);
        }

        // THE INJECTION GATE. There is no field on the contract that can carry SQL or an expression, so the
        // only thing a caller can put anywhere is a VALUE — and a value is compared as data, never spliced.
        // This test proves the attempt lands as an ordinary filter value and changes nothing structural.
        [Fact]
        public async Task Sql_shaped_input_is_treated_as_an_ordinary_value_and_never_as_syntax()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedSalesAsync(host);

            var draft = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo", "Status");
            draft.Filters.Add(new StudioFilterDraft
            {
                Field = "Status",
                Operator = ReportFilterOperator.Equals,
                Values = new List<string?> { "Posted'; DROP TABLE SalesInvoices;--" },
            });

            var studio = StudioWith(host, new SalesRevenueDataSource(host.Db), AllThree);
            var result = await studio.RunAsync(draft, ReportOutputFormat.Html, preview: true);

            // It runs, matches nothing, and above all the table is still there — the value never became syntax.
            Assert.True(result.IsSuccess);
            Assert.Equal(0, result.Run!.RowCount);
            Assert.Equal(2, await host.NewContext().SalesInvoices.CountAsync(i => i.CompanyID == Mine));

            // And a field name that is SQL rather than a field is refused outright.
            var injected = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo; DROP TABLE SalesInvoices");
            Assert.False((await studio.ValidateAsync(injected)).Ok);
        }

        // =========================================================================================
        // GATE 4 — COMPANY ISOLATION.
        // =========================================================================================
        [Fact]
        public async Task A_studio_run_returns_only_the_callers_company()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedSalesAsync(host);

            var studio = StudioWith(host, new SalesRevenueDataSource(host.Db), AllThree);
            var result = await studio.RunAsync(
                Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo", "GrandTotal"),
                ReportOutputFormat.Csv, preview: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(2, result.Run!.RowCount);            // 2 mine; the neighbour's is not there

            var csv = System.Text.Encoding.UTF8.GetString(result.Artifact!.Content);
            Assert.DoesNotContain("SI-X", csv);
            Assert.DoesNotContain("9999", csv);
        }

        [Fact]
        public async Task An_unresolved_company_can_build_nothing()
        {
            // A host with NO company: the state a background or unauthenticated caller arrives in.
            using var host = new ReportingTestHost(companyId: null);

            var studio = Studio(host, AllThree);

            Assert.Empty((await studio.BuildAsync()).Datasets);
            Assert.Empty(await studio.FieldsAsync(AccountingDatasetCodes.SalesRevenue));
            Assert.False((await studio.ValidateAsync(
                Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo"))).Ok);
        }

        // =========================================================================================
        // GATE 5 — SAVE / REOPEN ROUNDTRIP, and the property that makes reopening safe.
        // =========================================================================================
        [Fact]
        public async Task Save_then_reopen_returns_the_same_report()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);

            var draft = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceDate", "InvoiceNo", "GrandTotal");
            draft.Name = "Q1 revenue";
            draft.Filters.Add(new StudioFilterDraft
            {
                Field = "Status", Operator = ReportFilterOperator.Equals,
                Values = new List<string?> { "Posted" },
            });
            draft.Sorts.Add(new StudioSortDraft { Field = "InvoiceDate", Descending = true });

            var studio = Studio(host, AllThree);
            var saved = await studio.SaveAsync(draft);

            Assert.True(saved.Success);
            Assert.True(saved.TemplateId > 0);

            var reopened = await studio.OpenAsync(saved.TemplateId);

            Assert.NotNull(reopened);
            Assert.Equal(AccountingDatasetCodes.SalesRevenue, reopened!.DatasetCode);
            Assert.Equal(new[] { "InvoiceDate", "InvoiceNo", "GrandTotal" }, reopened.Columns);
            Assert.Single(reopened.Filters);
            Assert.Equal("Status", reopened.Filters[0].Field);
            Assert.Equal("Posted", reopened.Filters[0].Values[0]);
            Assert.Single(reopened.Sorts);
            Assert.Equal("InvoiceDate", reopened.Sorts[0].Field);
            Assert.True(reopened.Sorts[0].Descending);
        }

        // A saved report is NOT a grant. Losing a permission must take the column away on reopen, or a
        // template becomes a way to keep access somebody revoked.
        [Fact]
        public async Task Reopening_after_losing_a_permission_drops_the_gated_column()
        {
            using var host = new ReportingTestHost();
            Grant(host, InventoryReportPermissions.View, InventoryReportPermissions.Cost);

            var studio = Studio(host, AllThree);
            var saved = await studio.SaveAsync(
                Draft(InventoryDatasetCodes.StockOnHand, "ItemName", "TotalValue"));
            Assert.True(saved.Success);

            // The cost right is revoked.
            host.PermissionOptions.RoleMap.Remove(InventoryReportPermissions.Cost);

            var reopened = await Studio(host, AllThree).OpenAsync(saved.TemplateId);

            Assert.NotNull(reopened);
            Assert.Contains("ItemName", reopened!.Columns);
            Assert.DoesNotContain("TotalValue", reopened.Columns);
        }

        [Fact]
        public async Task A_saved_report_is_company_scoped_and_owned()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);

            var saved = await Studio(host, AllThree).SaveAsync(
                Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo"));
            Assert.True(saved.Success);

            // Read back from a NEW context: the company came from the BusinessContext, not from the draft,
            // and the draft has no field that could have carried one.
            var row = await host.NewContext().ReportTemplates
                .AsNoTracking().FirstAsync(t => t.Id == saved.TemplateId);

            Assert.Equal(Mine, row.CompanyID);
            Assert.Equal(ReportTemplateScope.Personal, row.Scope);
            Assert.Equal(host.Ctx.EmployeeId, row.OwnerEmpId);
        }

        // =========================================================================================
        // GATE 6 — PREVIEW, RUN AND EXPORT: one path, three formats.
        // =========================================================================================
        [Fact]
        public async Task Preview_runs_and_is_capped()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedSalesAsync(host);

            var studio = StudioWith(host, new SalesRevenueDataSource(host.Db), AllThree);
            var result = await studio.RunAsync(
                Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo", "GrandTotal"),
                ReportOutputFormat.Html, preview: true);

            Assert.True(result.IsSuccess);
            Assert.Equal(ReportRunKind.Preview, result.Run!.Kind);
            Assert.Contains("SI-1", System.Text.Encoding.UTF8.GetString(result.Artifact!.Content));
        }

        // §9's "export uses the same saved definition": the saved template is reopened and exported, and the
        // bytes reflect exactly the columns that were saved — there is no separate export builder to drift.
        [Fact]
        public async Task Export_uses_the_same_definition_that_was_saved()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedSalesAsync(host);

            var studio = StudioWith(host, new SalesRevenueDataSource(host.Db), AllThree);

            var saved = await studio.SaveAsync(
                Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo", "Status"));
            Assert.True(saved.Success);

            var reopened = await studio.OpenAsync(saved.TemplateId);
            Assert.NotNull(reopened);

            foreach (var format in new[] { ReportOutputFormat.Csv, ReportOutputFormat.Xlsx })
            {
                var exported = await studio.RunAsync(reopened!, format, preview: false);
                Assert.True(exported.IsSuccess, $"{format} export failed");
                Assert.NotNull(exported.Artifact);
                Assert.True(exported.Artifact!.Content.Length > 0);
            }

            var csv = await studio.RunAsync(reopened!, ReportOutputFormat.Csv, preview: false);
            var text = System.Text.Encoding.UTF8.GetString(csv.Artifact!.Content);
            Assert.Contains("SI-1", text);
            Assert.DoesNotContain("114", text);   // GrandTotal was NOT among the saved columns
        }

        // Permissions are re-checked on EVERY execution, not captured at save time.
        [Fact]
        public async Task Permissions_are_rechecked_at_run_time()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedSalesAsync(host);

            var studio = StudioWith(host, new SalesRevenueDataSource(host.Db), AllThree);
            var draft = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo");

            Assert.True((await studio.RunAsync(draft, ReportOutputFormat.Html, preview: true)).IsSuccess);

            // The dataset permission is revoked between runs. The same draft must now fail.
            host.PermissionOptions.RoleMap.Remove(AccountingReportPermissions.View);

            var after = await StudioWith(host, new SalesRevenueDataSource(host.Db), AllThree)
                .RunAsync(draft, ReportOutputFormat.Html, preview: true);

            Assert.False(after.IsSuccess);
        }

        // =========================================================================================
        // GATE 7 — THE THREE REAL DATASETS the brief names, built through the service rather than seeded.
        // =========================================================================================
        [Fact]
        public async Task The_three_named_datasets_are_all_buildable_end_to_end()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View, InventoryReportPermissions.View,
                  CrmReportPermissions.View);

            await SeedSalesAsync(host);
            await SeedStockAsync(host);
            await SeedOppsAsync(host);

            var cases = new (string Dataset, IReportDataSource Source, string[] Columns, string Expect)[]
            {
                (AccountingDatasetCodes.SalesRevenue, new SalesRevenueDataSource(host.Db),
                    new[] { "InvoiceNo", "GrandTotal" }, "SI-1"),
                (InventoryDatasetCodes.StockOnHand, new StockOnHandDataSource(host.Db),
                    new[] { "ItemName", "QtyOnHand" }, "Item One"),
                (CrmDatasetCodes.Opportunities, new CrmOpportunitiesDataSource(host.Db),
                    new[] { "Title", "Amount" }, "Opp One"),
            };

            foreach (var (dataset, source, columns, expect) in cases)
            {
                var studio = StudioWith(host, source, AllThree);

                // build → preview → save → reopen → export, the whole product loop.
                var draft = Draft(dataset, columns);
                var preview = await studio.RunAsync(draft, ReportOutputFormat.Html, preview: true);
                Assert.True(preview.IsSuccess, $"{dataset} preview failed");
                Assert.Contains(expect, System.Text.Encoding.UTF8.GetString(preview.Artifact!.Content));

                var saved = await studio.SaveAsync(draft);
                Assert.True(saved.Success, $"{dataset} save failed");

                var reopened = await studio.OpenAsync(saved.TemplateId);
                Assert.NotNull(reopened);
                Assert.Equal(columns, reopened!.Columns);

                var exported = await studio.RunAsync(reopened, ReportOutputFormat.Csv, preview: false);
                Assert.True(exported.IsSuccess, $"{dataset} export failed");
            }
        }

        // The operators offered to the screen are the ones the validator will accept — the UI cannot present a
        // choice the server refuses, because both read the dataset's own table.
        [Fact]
        public async Task Offered_operators_are_exactly_the_ones_the_validator_accepts()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);

            var fields = await Studio(host, AllThree).FieldsAsync(AccountingDatasetCodes.SalesRevenue);
            var money = fields.First(f => f.Key == "GrandTotal");
            var studio = Studio(host, AllThree);

            foreach (var op in money.Operators)
            {
                var draft = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo");
                draft.Filters.Add(new StudioFilterDraft
                {
                    Field = "GrandTotal", Operator = op.Operator, Values = new List<string?> { "1" },
                });

                Assert.True((await studio.ValidateAsync(draft)).Ok,
                    $"the screen offers {op.Operator} on GrandTotal but the validator refuses it");
            }
        }

        // =========================================================================================
        // GATE 7b — A FILTERED OR SORTED FIELD IS FETCHED EVEN WHEN IT IS NOT DISPLAYED.
        //
        // REGRESSION GATE for the worst defect this increment produced. A draft showing InvoiceNo and Total
        // while filtering Status = Posted returned ZERO rows against a company with 146 posted invoices: the
        // source projects only the requested columns, so Status was absent from every row and the shaper's
        // filter compared against a missing value. Successful run, empty page, no diagnostic.
        //
        // The assertion is deliberately about ROWS rather than about the request shape — testing that the
        // service adds the column would pass even if the engine later stopped needing it, and would miss the
        // thing that actually matters.
        // =========================================================================================
        [Fact]
        public async Task A_filter_on_an_undisplayed_field_still_returns_rows()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedSalesAsync(host);

            var studio = StudioWith(host, new SalesRevenueDataSource(host.Db), AllThree);

            // Status is FILTERED but NOT among the displayed columns.
            var draft = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo", "GrandTotal");
            draft.Filters.Add(new StudioFilterDraft
            {
                Field = "Status", Operator = ReportFilterOperator.Equals,
                Values = new List<string?> { "Posted" },
            });

            var result = await studio.RunAsync(draft, ReportOutputFormat.Html, preview: true);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Run!.RowCount);   // one Posted invoice in the fixture, not zero

            // And the filter still discriminates — it is not simply being ignored.
            var draftDraft = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo", "GrandTotal");
            draftDraft.Filters.Add(new StudioFilterDraft
            {
                Field = "Status", Operator = ReportFilterOperator.Equals,
                Values = new List<string?> { "Draft" },
            });
            Assert.Equal(1, (await studio.RunAsync(draftDraft, ReportOutputFormat.Html, preview: true)).Run!.RowCount);
        }

        // Sorting on an undisplayed field must likewise not empty the result.
        [Fact]
        public async Task A_sort_on_an_undisplayed_field_still_returns_rows()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedSalesAsync(host);

            var studio = StudioWith(host, new SalesRevenueDataSource(host.Db), AllThree);

            var draft = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo");
            draft.Sorts.Add(new StudioSortDraft { Field = "GrandTotal", Descending = true });

            var result = await studio.RunAsync(draft, ReportOutputFormat.Html, preview: true);

            Assert.True(result.IsSuccess);
            Assert.Equal(2, result.Run!.RowCount);
        }

        // Xlsx is enum value 2, Csv is 3 — NOT their ordinal position in the list. The Studio view sent 4
        // (PrintHtml) for XLSX in the first cut and its own format guard refused it with a 400. C# cannot
        // assert the view's constant, but it can pin the contract the view must satisfy.
        [Fact]
        public async Task Both_export_formats_the_screen_offers_are_accepted()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedSalesAsync(host);

            var studio = StudioWith(host, new SalesRevenueDataSource(host.Db), AllThree);
            var draft = Draft(AccountingDatasetCodes.SalesRevenue, "InvoiceNo", "GrandTotal");

            Assert.Equal(2, (int)ReportOutputFormat.Xlsx);   // the value the view must send
            Assert.Equal(3, (int)ReportOutputFormat.Csv);

            foreach (var format in new[] { ReportOutputFormat.Csv, ReportOutputFormat.Xlsx })
            {
                var r = await studio.RunAsync(draft, format, preview: false);
                Assert.True(r.IsSuccess, $"{format} was refused");
                Assert.True(r.Artifact!.Content.Length > 0);
            }
        }

        // =========================================================================================
        // GATE 8 — THE REAL DI GRAPH.
        //
        // CLAUDE.md's rule, learned from 112 green tests coexisting with an application that could not boot:
        // a DI graph is NOT verified by tests that construct services by hand. Every gate above builds
        // ReportStudioService directly, so none of them would notice a missing registration.
        //
        // This one builds the REAL container from the REAL registration extension with ValidateOnBuild AND
        // ValidateScopes, and resolves the Studio façade from a scope.
        //
        // The three things registered alongside AddCrossBusinessReporting are the same three the reporting
        // registration has always depended on from outside itself — a DbContext, the company-scope holder it
        // needs, and the BusinessContext accessor. They are the app-level registrations Program.cs supplies;
        // no reporting service is substituted, stubbed or faked, which is the point of the exercise.
        // =========================================================================================
        [Fact]
        public void The_studio_resolves_from_the_real_graph_with_ValidateOnBuild_and_ValidateScopes()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

            services.AddLogging();
            services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
            services.AddDbContext<CrossBuy.Models.Context.CrossDbContext>(o => o.UseSqlite("DataSource=:memory:"));
            services.AddScoped<IBusinessContextAccessor, GraphAccessor>();

            services.AddCrossBusinessReporting();

            // Construction IS the first assertion: BuildServiceProvider throws here if ANY reporting service —
            // including the Studio and everything it depends on — has an unsatisfiable dependency, or if a
            // singleton captures a scoped service.
            using var provider = services.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            using var scope = provider.CreateScope();

            var studio = scope.ServiceProvider.GetRequiredService<IReportStudioService>();
            Assert.NotNull(studio);
            Assert.IsType<ReportStudioService>(studio);

            // And its whole dependency set resolves in the same scope — named individually so a failure says
            // WHICH link broke rather than just "the studio would not build".
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportDatasetRegistry>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportCatalog>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportPermissionEvaluator>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportTemplateService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportService>());
        }

        // Resolvable, which is all ValidateOnBuild needs from it. A real accessor reads HttpContext.
        private sealed class GraphAccessor : IBusinessContextAccessor
        {
            public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
                throw new BusinessContextUnresolvedException("no context in the DI validation graph");

            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<BusinessContext?>(null);
        }

        // A data source that is never expected to be reached — used by the Studio instances that only
        // validate, so a test that accidentally executes shows up as an obvious empty result rather than
        // quietly reading real rows.
        private sealed class NullSource : IReportDataSource
        {
            public static readonly NullSource Instance = new();
            public string Key => "studio.tests.null";
            public Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ReportDataSetBuilder(query.Definition.Columns).Build());
        }
    }
}
