using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // Reporting R2 — THE MODULE DATASET GATES.
    //
    // R1's single platform dataset could be trusted on its own reasoning. Nine module datasets cannot: they are
    // the first Reporting surfaces that read ACCOUNTING, INVENTORY and CRM tables, so the thing that has to be
    // proved is not "does it return rows" but "can it ever return somebody else's rows".
    //
    // So the shape of this file is one gate per property, run for EVERY dataset family rather than for one
    // representative — a cross-company leak in the CRM source is not caught by proving the Accounting source is
    // clean, and that is exactly the kind of gap a "representative" test leaves behind.
    //
    // FIXTURES ARE SEEDED FOR TWO COMPANIES ON PURPOSE. A single-company fixture cannot fail the isolation
    // test: every row it can see is legitimately its own, so the assertion passes whether the filter exists or
    // not. Company 1 is the caller; company 99 is the neighbour whose rows must never appear.
    // =============================================================================================
    public class ReportingModuleDatasetTests
    {
        private const int Mine = 1;
        private const int Theirs = 99;

        // ---- permission plumbing ---------------------------------------------------------------------
        //
        // The host's RoleMap deliberately does NOT carry the module keys, so the default context is denied and
        // a test that wants access has to say so. Granting is therefore an explicit act in every test below,
        // which is what makes the fail-closed gate meaningful rather than incidental.
        private static void Grant(ReportingTestHost host, params string[] keys)
        {
            foreach (var key in keys) host.PermissionOptions.RoleMap[key] = new[] { "Reports" };
        }

        // =========================================================================================
        // SEEDING
        //
        // Written straight through the DbContext rather than through the module writers, and that is a
        // deliberate choice for a READ test: routing through ReceivableService/StockService would drag their
        // posting rules, their event wiring and their transactions into a test about whether a SELECT is
        // company-filtered. These rows are fixtures, not documents.
        //
        // THE NEIGHBOUR'S ROWS NEED THEIR OWN SCOPE, and finding that out was useful: seeding company 99
        // through the host's company-1 context is REFUSED by CompanyWriteGuardInterceptor —
        //
        //     "This scope operates as company 1 but the new Customer names company 99. A company id from a
        //      request, a view model or a route may not redirect a write."
        //
        // which is the platform guard working exactly as designed. So the fixture opens a second context bound
        // to company 99 and writes the neighbour there. The test is stronger for it: the two companies' rows
        // arrive by the only route the platform permits, so the isolation being proved downstream is isolation
        // between two legitimately-written tenants rather than between two rows one scope forced in.
        // =========================================================================================

        // A context bound to the NEIGHBOUR's scope. Only ever used to write the rows that must not be visible.
        private static CrossBuy.Models.Context.CrossDbContext Neighbour(ReportingTestHost host)
        {
            var scope = new CrossBuy.BL.Platform.CompanyScopeHolder();
            scope.Set(Theirs, null);
            return host.NewContext(scope);
        }

        private static async Task SeedAccountingAsync(ReportingTestHost host)
        {
            var db = host.Db;

            db.Customers.Add(
                new Customer { ID = 10, CompanyID = Mine, Name = "عميل واحد", NameEn = "Customer One", ControlAccountId = 1 });

            db.Vendors.Add(
                new Vendor { ID = 20, CompanyID = Mine, Name = "مورّد", NameEn = "Vendor One", ControlAccountId = 1 });

            db.SalesInvoices.AddRange(
                new SalesInvoice
                {
                    ID = 100, CompanyID = Mine, InvoiceNo = "SI-1", InvoiceDate = new DateTime(2026, 3, 10),
                    CustomerId = 10, SubTotal = 100m, TaxTotal = 14m, GrandTotal = 114m, Status = "Posted",
                },
                new SalesInvoice
                {
                    ID = 101, CompanyID = Mine, InvoiceNo = "SI-2", InvoiceDate = new DateTime(2026, 3, 20),
                    CustomerId = 10, SubTotal = 200m, TaxTotal = 28m, GrandTotal = 228m, Status = "Draft",
                });

            db.PurchaseInvoices.AddRange(
                new PurchaseInvoice
                {
                    ID = 200, CompanyID = Mine, InvoiceNo = "PI-1", InvoiceDate = new DateTime(2026, 3, 12),
                    VendorId = 20, SubTotal = 50m, TaxTotal = 7m, GrandTotal = 57m, Status = "Posted",
                });

            db.JournalEntries.AddRange(
                new JournalEntry
                {
                    ID = 300, CompanyID = Mine, EntryNo = "JV-1", EntryDate = new DateTime(2026, 3, 5),
                    FiscalPeriodId = 1, JournalType = "Manual", SourceType = "Manual", CurrencyId = 1,
                    Status = "Posted", Description = "قيد", DescriptionEn = "Entry",
                });

            await db.SaveChangesAsync();

            // ---- the neighbour, under its OWN scope ----
            await using var theirs = Neighbour(host);
            theirs.Customers.Add(
                new Customer { ID = 11, CompanyID = Theirs, Name = "جار", NameEn = "Neighbour", ControlAccountId = 1 });
            theirs.Vendors.Add(
                new Vendor { ID = 21, CompanyID = Theirs, Name = "مورّد جار", NameEn = "Neighbour Vendor", ControlAccountId = 1 });
            theirs.SalesInvoices.Add(new SalesInvoice
            {
                ID = 102, CompanyID = Theirs, InvoiceNo = "SI-X", InvoiceDate = new DateTime(2026, 3, 15),
                CustomerId = 11, SubTotal = 9_999m, TaxTotal = 0m, GrandTotal = 9_999m, Status = "Posted",
            });
            theirs.PurchaseInvoices.Add(new PurchaseInvoice
            {
                ID = 201, CompanyID = Theirs, InvoiceNo = "PI-X", InvoiceDate = new DateTime(2026, 3, 12),
                VendorId = 21, SubTotal = 8_888m, TaxTotal = 0m, GrandTotal = 8_888m, Status = "Posted",
            });
            theirs.JournalEntries.Add(new JournalEntry
            {
                ID = 301, CompanyID = Theirs, EntryNo = "JV-X", EntryDate = new DateTime(2026, 3, 6),
                FiscalPeriodId = 1, JournalType = "Auto", SourceType = "SalesInvoice", CurrencyId = 1,
                Status = "Posted", Description = "جار", DescriptionEn = "Neighbour entry",
            });
            await theirs.SaveChangesAsync();
        }

        private static async Task SeedInventoryAsync(ReportingTestHost host)
        {
            var db = host.Db;

            db.Items.Add(
                new Item
                {
                    ID = 40, CompanyID = Mine, ItemCode = "IT-1", Barcode = "B1", Name = "صنف", NameEn = "Item One",
                    ItemCategoryId = 1, BaseUoMId = 1, ItemType = "Stockable",
                });

            db.Warehouses.Add(
                new Warehouse { ID = 50, CompanyID = Mine, Code = "WH1", Name = "مخزن", NameEn = "Main" });

            db.StockBalances.AddRange(
                // A positive, a NEGATIVE (the state the low/negative filter exists for) and the neighbour's row.
                new StockBalance { ID = 60, CompanyID = Mine, ItemId = 40, WarehouseId = 50, QtyOnHand = 12m, AvgCost = 5m, TotalValue = 60m },
                new StockBalance { ID = 61, CompanyID = Mine, ItemId = 40, WarehouseId = 50, QtyOnHand = -3m, AvgCost = 5m, TotalValue = -15m });

            db.StockMovements.AddRange(
                new StockMovement
                {
                    ID = 70, CompanyID = Mine, MovementNo = "MV-1", MovementDate = new DateTime(2026, 3, 8),
                    ItemId = 40, WarehouseId = 50, Direction = 1, QtyBase = 10m, UnitCost = 5m, TotalCost = 50m,
                    SourceType = "PurchaseInvoice",
                },
                new StockMovement
                {
                    ID = 71, CompanyID = Mine, MovementNo = "MV-2", MovementDate = new DateTime(2026, 3, 9),
                    ItemId = 40, WarehouseId = 50, Direction = -1, QtyBase = 2m, UnitCost = 5m, TotalCost = 10m,
                    SourceType = "SalesInvoice",
                });

            await db.SaveChangesAsync();

            // ---- the neighbour, under its OWN scope ----
            await using var theirs = Neighbour(host);
            theirs.Items.Add(new Item
            {
                ID = 41, CompanyID = Theirs, ItemCode = "IT-X", Barcode = "BX", Name = "جار", NameEn = "Neighbour Item",
                ItemCategoryId = 1, BaseUoMId = 1, ItemType = "Stockable",
            });
            theirs.Warehouses.Add(
                new Warehouse { ID = 51, CompanyID = Theirs, Code = "WHX", Name = "جار", NameEn = "Neighbour WH" });
            theirs.StockBalances.Add(new StockBalance
            {
                ID = 62, CompanyID = Theirs, ItemId = 41, WarehouseId = 51, QtyOnHand = 7_777m, AvgCost = 1m, TotalValue = 7_777m,
            });
            theirs.StockMovements.Add(new StockMovement
            {
                ID = 72, CompanyID = Theirs, MovementNo = "MV-X", MovementDate = new DateTime(2026, 3, 9),
                ItemId = 41, WarehouseId = 51, Direction = 1, QtyBase = 6_666m, UnitCost = 1m, TotalCost = 6_666m,
                SourceType = "PurchaseInvoice",
            });
            await theirs.SaveChangesAsync();
        }

        private static async Task SeedCrmAsync(ReportingTestHost host)
        {
            var db = host.Db;

            db.Leads.AddRange(
                new Lead
                {
                    ID = 80, CompanyID = Mine, Name = "محتمل", NameEn = "Lead One", Company = "Acme",
                    Source = "Website", Status = "New", EstimatedValue = 500m, Score = 10,
                    CreatedAt = new DateTime(2026, 3, 3),
                },
                new Lead
                {
                    ID = 81, CompanyID = Mine, Name = "محوَّل", NameEn = "Lead Two", Company = "Beta",
                    Source = "Referral", Status = "Converted", EstimatedValue = 900m, Score = 40, AccountId = 5,
                    CreatedAt = new DateTime(2026, 3, 4),
                });

            db.CrmPipelineStages.Add(new CrmPipelineStage
            {
                ID = 90, CompanyID = Mine, PipelineId = 1, Name = "تفاوض", NameEn = "Negotiation", Sort = 3,
            });

            db.Opportunities.AddRange(
                new Opportunity
                {
                    ID = 95, CompanyID = Mine, Title = "فرصة", TitleEn = "Opp One", Stage = "Proposal",
                    Amount = 1_000m, Probability = 40, PipelineId = 1, StageId = 90,
                    CreatedAt = new DateTime(2026, 3, 6),
                },
                new Opportunity
                {
                    ID = 96, CompanyID = Mine, Title = "فرصة ٢", TitleEn = "Opp Two", Stage = "Won",
                    Amount = 300m, Probability = 100, CreatedAt = new DateTime(2026, 3, 7),
                });

            await db.SaveChangesAsync();

            // ---- the neighbour, under its OWN scope ----
            await using var theirs = Neighbour(host);
            theirs.Leads.Add(new Lead
            {
                ID = 82, CompanyID = Theirs, Name = "جار", NameEn = "Neighbour Lead", Source = "Website",
                Status = "New", EstimatedValue = 5_555m, CreatedAt = new DateTime(2026, 3, 4),
            });
            theirs.Opportunities.Add(new Opportunity
            {
                ID = 97, CompanyID = Theirs, Title = "جار", TitleEn = "Neighbour Opp", Stage = "Proposal",
                Amount = 4_444m, Probability = 50, CreatedAt = new DateTime(2026, 3, 7),
            });
            await theirs.SaveChangesAsync();
        }

        // The full period, so a date-bounded dataset returns its fixtures without each test restating it.
        private static Dictionary<string, string?> Period() => new()
        {
            ["From"] = "2026-01-01",
            ["To"] = "2026-12-31",
        };

        private static async Task<ReportResult> RunAsync(ReportingTestHost host, IReportDataSource source,
            string code, Dictionary<string, string?>? parameters = null,
            ReportOutputFormat format = ReportOutputFormat.Html)
        {
            var engine = host.EngineWith(source);
            return await host.Reports(engine).GenerateAsync(new ReportRequest
            {
                ReportCode = code,
                Format = format,
                Parameters = parameters ?? Period(),
            });
        }

        // =========================================================================================
        // GATE 1 — REGISTRATION. Every dataset the increment claims is actually in the registry, resolvable by
        // its code, and its definition passes the validator. A dataset that exists only in a delivery note is
        // the failure this catches.
        // =========================================================================================
        [Fact]
        public void Every_module_dataset_is_registered_once_and_valid()
        {
            var datasets = AccountingDatasets.All()
                .Concat(InventoryDatasets.All())
                .Concat(CrmDatasets.All())
                .ToList();

            // NO FROZEN COUNT. This read `Assert.Equal(9, ...)` and broke the day a module added its
            // tenth dataset - a failure that says nothing about whether anything is wrong, and trains a
            // reader to update the number rather than look. The gate's own comment says what it is really
            // for: every dataset the increment claims is present, resolvable and valid. That is asserted
            // below, plus the one property a count was standing in for - no two datasets claim one code.
            Assert.NotEmpty(datasets);
            Assert.Equal(datasets.Count,
                datasets.Select(d => d.DatasetCode).Distinct(StringComparer.Ordinal).Count());

            // The validator is the same one the registry runs at container-build time.
            foreach (var dataset in datasets)
            {
                var problems = ReportDatasetValidator.Validate(dataset);
                Assert.True(problems.Count == 0,
                    $"{dataset.DatasetCode} is invalid: {string.Join(" · ", problems)}");
            }

            // Codes are unique — a duplicate would make the registry throw, and it is worth failing here with a
            // readable message instead.
            var duplicates = datasets.GroupBy(d => d.DatasetCode).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicates.Count == 0, "duplicate dataset codes: " + string.Join(", ", duplicates));
        }

        [Fact]
        public void Every_module_dataset_has_a_matching_report_definition_and_data_source_key()
        {
            using var host = new ReportingTestHost();

            foreach (var dataset in AccountingDatasets.All().Concat(InventoryDatasets.All()).Concat(CrmDatasets.All()))
            {
                Assert.True(host.Catalog.TryGetDefinition(dataset.DatasetCode, out var definition),
                    $"{dataset.DatasetCode} has no report definition in the catalogue");

                // The definition must point at the dataset's own source key and carry its permission — the two
                // drifting apart is how a report ends up reading one thing and authorizing another.
                Assert.Equal(dataset.DataSourceKey, definition!.DataSourceKey);
                Assert.Equal(dataset.RequiredPermissionKey, definition.PermissionKey);

                // Columns are projected FROM the fields, so the counts must match exactly.
                Assert.Equal(dataset.Fields.Count, definition.Columns.Count);
            }
        }

        // =========================================================================================
        // GATE 2 — AUTHORIZATION. Granted callers query; ungranted callers are refused, and refused BEFORE the
        // source is reached.
        // =========================================================================================
        [Theory]
        [InlineData(AccountingDatasetCodes.SalesRevenue, AccountingReportPermissions.View)]
        [InlineData(AccountingDatasetCodes.Purchases, AccountingReportPermissions.View)]
        [InlineData(AccountingDatasetCodes.JournalActivity, AccountingReportPermissions.View)]
        [InlineData(InventoryDatasetCodes.StockOnHand, InventoryReportPermissions.View)]
        [InlineData(InventoryDatasetCodes.StockMovements, InventoryReportPermissions.View)]
        [InlineData(CrmDatasetCodes.Leads, CrmReportPermissions.View)]
        [InlineData(CrmDatasetCodes.Opportunities, CrmReportPermissions.View)]
        public async Task An_unmapped_module_key_denies_the_report_for_everyone(string code, string key)
        {
            using var host = new ReportingTestHost();

            // No Grant call. The key is absent from the RoleMap and the evaluator is fail-closed.
            Assert.False(host.PermissionOptions.RoleMap.ContainsKey(key));

            // DescribeAsync is what the Viewer and the write endpoints both gate on: null means "no such report,
            // as far as you are concerned".
            Assert.Null(await host.Reports().DescribeAsync(code));

            var visible = await host.Reports().BrowseAsync();
            Assert.DoesNotContain(visible, d => d.Code == code);
        }

        [Fact]
        public async Task A_granted_caller_can_query_every_family()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View, InventoryReportPermissions.View, CrmReportPermissions.View);

            await SeedAccountingAsync(host);
            await SeedInventoryAsync(host);
            await SeedCrmAsync(host);

            var cases = new (string Code, IReportDataSource Source)[]
            {
                (AccountingDatasetCodes.SalesRevenue, new SalesRevenueDataSource(host.Db)),
                (AccountingDatasetCodes.Purchases, new PurchasesDataSource(host.Db)),
                (AccountingDatasetCodes.JournalActivity, new JournalActivityDataSource(host.Db)),
                (InventoryDatasetCodes.StockOnHand, new StockOnHandDataSource(host.Db)),
                (InventoryDatasetCodes.StockMovements, new StockMovementsDataSource(host.Db)),
                (CrmDatasetCodes.Leads, new CrmLeadsDataSource(host.Db)),
                (CrmDatasetCodes.Opportunities, new CrmOpportunitiesDataSource(host.Db)),
            };

            foreach (var (code, source) in cases)
            {
                var result = await RunAsync(host, source, code);
                Assert.True(result.IsSuccess, $"{code} failed: {string.Join(" · ", result.Errors.Select(e => e.Message))}");
                Assert.False(result.IsDenied);
                Assert.True(result.Run!.RowCount > 0, $"{code} returned no rows against its own fixtures");
            }
        }

        // The gate that matters most: a denial must not merely hide the output, it must never reach the data.
        [Fact]
        public async Task A_denied_module_report_never_reaches_its_data_source()
        {
            using var host = new ReportingTestHost();
            await SeedAccountingAsync(host);

            var spy = new EntryRecordingSource(AccountingDatasetCodes.SalesRevenue);
            var result = await RunAsync(host, spy, AccountingDatasetCodes.SalesRevenue);

            Assert.True(result.IsDenied || !result.IsSuccess);
            Assert.False(spy.WasEntered, "a denied run reached the data source");
        }

        // =========================================================================================
        // GATE 3 — COMPANY ISOLATION, one test per family. The neighbour's rows carry distinctive values, so a
        // leak fails on an exact number rather than on a count.
        // =========================================================================================
        [Fact]
        public async Task Accounting_datasets_exclude_another_companys_rows()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedAccountingAsync(host);

            var sales = await RunAsync(host, new SalesRevenueDataSource(host.Db), AccountingDatasetCodes.SalesRevenue);
            Assert.Equal(2, sales.Run!.RowCount);                       // 2 mine, the neighbour's excluded
            Assert.DoesNotContain("9999", Text(sales));
            Assert.DoesNotContain("SI-X", Text(sales));

            var purchases = await RunAsync(host, new PurchasesDataSource(host.Db), AccountingDatasetCodes.Purchases);
            Assert.Equal(1, purchases.Run!.RowCount);
            Assert.DoesNotContain("8888", Text(purchases));

            var journals = await RunAsync(host, new JournalActivityDataSource(host.Db), AccountingDatasetCodes.JournalActivity);
            Assert.Equal(1, journals.Run!.RowCount);
            Assert.DoesNotContain("JV-X", Text(journals));
        }

        [Fact]
        public async Task Inventory_datasets_exclude_another_companys_rows()
        {
            using var host = new ReportingTestHost();
            Grant(host, InventoryReportPermissions.View, InventoryReportPermissions.Cost);
            await SeedInventoryAsync(host);

            var onHand = await RunAsync(host, new StockOnHandDataSource(host.Db), InventoryDatasetCodes.StockOnHand,
                new Dictionary<string, string?>());
            Assert.Equal(2, onHand.Run!.RowCount);
            Assert.DoesNotContain("7777", Text(onHand));
            Assert.DoesNotContain("Neighbour", Text(onHand));

            var movements = await RunAsync(host, new StockMovementsDataSource(host.Db), InventoryDatasetCodes.StockMovements);
            Assert.Equal(2, movements.Run!.RowCount);
            Assert.DoesNotContain("6666", Text(movements));
            Assert.DoesNotContain("MV-X", Text(movements));
        }

        [Fact]
        public async Task Crm_datasets_exclude_another_companys_rows()
        {
            using var host = new ReportingTestHost();
            Grant(host, CrmReportPermissions.View);
            await SeedCrmAsync(host);

            var leads = await RunAsync(host, new CrmLeadsDataSource(host.Db), CrmDatasetCodes.Leads);
            Assert.Equal(2, leads.Run!.RowCount);
            Assert.DoesNotContain("5555", Text(leads));
            Assert.DoesNotContain("Neighbour", Text(leads));

            var opportunities = await RunAsync(host, new CrmOpportunitiesDataSource(host.Db), CrmDatasetCodes.Opportunities);
            Assert.Equal(2, opportunities.Run!.RowCount);
            Assert.DoesNotContain("4444", Text(opportunities));
        }

        // An UNRESOLVED company is not "everything" and not company 1 — it is nothing. Driven through a host
        // built with no company at all, which is the state a background caller would arrive in.
        [Fact]
        public async Task An_unresolved_company_reads_no_module_data()
        {
            using var withCompany = new ReportingTestHost();
            await SeedAccountingAsync(withCompany);

            var noCompany = new BusinessContext
            {
                CompanyId = 0, UserId = "nobody", Roles = new[] { "Reports" },
                Source = BusinessContextSource.Test,
            };

            // The source is called DIRECTLY here, past the engine's own gate, so the assertion is about the
            // source's own guard rather than about authorization happening to refuse first.
            var source = new SalesRevenueDataSource(withCompany.Db);
            var set = await source.FetchAsync(new ReportDataQuery
            {
                Definition = withCompany.Catalog.GetDefinition(AccountingDatasetCodes.SalesRevenue),
                Context = noCompany,
                Parameters = EmptyParameters(),
            });

            Assert.Empty(set.Rows);
        }

        // =========================================================================================
        // GATE 4 — FILTERS, SORTING, PAGING, EMPTY, EXPORT.
        // =========================================================================================
        [Fact]
        public async Task Parameters_filter_the_result()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedAccountingAsync(host);

            // Status: 2 invoices exist, 1 posted.
            var posted = await RunAsync(host, new SalesRevenueDataSource(host.Db), AccountingDatasetCodes.SalesRevenue,
                new Dictionary<string, string?> { ["From"] = "2026-01-01", ["To"] = "2026-12-31", ["Status"] = "Posted" });
            Assert.Equal(1, posted.Run!.RowCount);

            // A date window that excludes both invoices returns nothing rather than everything — the failure
            // mode of an ignored filter.
            var outside = await RunAsync(host, new SalesRevenueDataSource(host.Db), AccountingDatasetCodes.SalesRevenue,
                new Dictionary<string, string?> { ["From"] = "2025-01-01", ["To"] = "2025-12-31" });
            Assert.Equal(0, outside.Run!.RowCount);

            // INCLUSIVE upper bound: asking "to 2026-03-10" must include the invoice dated 2026-03-10.
            var inclusive = await RunAsync(host, new SalesRevenueDataSource(host.Db), AccountingDatasetCodes.SalesRevenue,
                new Dictionary<string, string?> { ["From"] = "2026-03-10", ["To"] = "2026-03-10" });
            Assert.Equal(1, inclusive.Run!.RowCount);
        }

        [Fact]
        public async Task The_inventory_stock_state_filter_finds_negative_balances()
        {
            using var host = new ReportingTestHost();
            Grant(host, InventoryReportPermissions.View);
            await SeedInventoryAsync(host);

            var negative = await RunAsync(host, new StockOnHandDataSource(host.Db), InventoryDatasetCodes.StockOnHand,
                new Dictionary<string, string?> { ["StockState"] = "Negative" });
            Assert.Equal(1, negative.Run!.RowCount);

            var positive = await RunAsync(host, new StockOnHandDataSource(host.Db), InventoryDatasetCodes.StockOnHand,
                new Dictionary<string, string?> { ["StockState"] = "Positive" });
            Assert.Equal(1, positive.Run!.RowCount);

            // The user's own threshold, not a reorder rule Reporting invented.
            var below = await RunAsync(host, new StockOnHandDataSource(host.Db), InventoryDatasetCodes.StockOnHand,
                new Dictionary<string, string?> { ["BelowQty"] = "0" });
            Assert.Equal(1, below.Run!.RowCount);
        }

        // Deterministic ordering matters more than any particular order: a report whose rows shuffle between
        // identical runs cannot be reconciled, and paging over it silently repeats and drops rows.
        [Fact]
        public async Task Sorting_is_deterministic_across_identical_runs()
        {
            using var host = new ReportingTestHost();
            Grant(host, CrmReportPermissions.View);
            await SeedCrmAsync(host);

            var first = await RunAsync(host, new CrmOpportunitiesDataSource(host.Db), CrmDatasetCodes.Opportunities);
            var second = await RunAsync(host, new CrmOpportunitiesDataSource(host.Db), CrmDatasetCodes.Opportunities);

            Assert.Equal(Text(first), Text(second));

            // And the declared default sort is honoured: Amount descending puts 1000 before 300.
            var body = Text(first);
            Assert.True(body.IndexOf("Opp One", StringComparison.Ordinal)
                        < body.IndexOf("Opp Two", StringComparison.Ordinal),
                "opportunities were not ordered by amount descending");
        }

        // Paging here means the ROW CAP, which is the platform's paging primitive: a capped run truncates and
        // SAYS SO. A cap that silently dropped rows would make every total wrong without a symptom.
        [Fact]
        public async Task A_row_cap_truncates_and_declares_it()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedAccountingAsync(host);

            var engine = host.EngineWith(new SalesRevenueDataSource(host.Db));
            var capped = await host.Reports(engine).GenerateAsync(new ReportRequest
            {
                ReportCode = AccountingDatasetCodes.SalesRevenue,
                Format = ReportOutputFormat.Html,
                Parameters = Period(),
                MaxRows = 1,
            });

            Assert.True(capped.IsSuccess);
            Assert.Equal(1, capped.Run!.RowCount);
            Assert.True(capped.Run.Truncated, "a capped run did not declare truncation");
        }

        // An empty dataset is a legitimate answer, not an error. Every family is exercised because "no rows"
        // is the state a lookup-resolution bug (an empty id list) most easily turns into an exception.
        [Fact]
        public async Task An_empty_result_succeeds_for_every_family()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View, InventoryReportPermissions.View, CrmReportPermissions.View);

            // Nothing seeded at all.
            var cases = new (string Code, IReportDataSource Source)[]
            {
                (AccountingDatasetCodes.SalesRevenue, new SalesRevenueDataSource(host.Db)),
                (AccountingDatasetCodes.Purchases, new PurchasesDataSource(host.Db)),
                (AccountingDatasetCodes.JournalActivity, new JournalActivityDataSource(host.Db)),
                (InventoryDatasetCodes.StockOnHand, new StockOnHandDataSource(host.Db)),
                (InventoryDatasetCodes.StockMovements, new StockMovementsDataSource(host.Db)),
                (CrmDatasetCodes.Leads, new CrmLeadsDataSource(host.Db)),
                (CrmDatasetCodes.Opportunities, new CrmOpportunitiesDataSource(host.Db)),
            };

            foreach (var (code, source) in cases)
            {
                var result = await RunAsync(host, source, code);
                Assert.True(result.IsSuccess, $"{code} failed on an empty dataset");
                Assert.Equal(0, result.Run!.RowCount);
            }
        }

        // The exporters are the platform's, not the datasets' — but a dataset whose columns the exporter cannot
        // consume is still broken, and CSV/XLSX are the two formats every module report advertises.
        [Fact]
        public async Task The_csv_and_xlsx_exporters_consume_every_family()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View, InventoryReportPermissions.View, CrmReportPermissions.View,
                  InventoryReportPermissions.Cost, ReportPermissions.Administer);

            await SeedAccountingAsync(host);
            await SeedInventoryAsync(host);
            await SeedCrmAsync(host);

            var cases = new (string Code, Func<IReportDataSource> Source)[]
            {
                (AccountingDatasetCodes.SalesRevenue, () => new SalesRevenueDataSource(host.Db)),
                (InventoryDatasetCodes.StockOnHand, () => new StockOnHandDataSource(host.Db)),
                (CrmDatasetCodes.Leads, () => new CrmLeadsDataSource(host.Db)),
            };

            foreach (var (code, source) in cases)
            foreach (var format in new[] { ReportOutputFormat.Csv, ReportOutputFormat.Xlsx })
            {
                var result = await RunAsync(host, source(), code, Period(), format);
                Assert.True(result.IsSuccess, $"{code} could not be exported as {format}");
                Assert.NotNull(result.Artifact);
                Assert.True(result.Artifact!.Content.Length > 0, $"{code} produced an empty {format} artifact");
            }
        }

        // =========================================================================================
        // GATE 5 — NO WRITE. Reporting reads. A dataset that wrote would be inserting business facts from a
        // report screen, and the row counts are the only proof that carries.
        // =========================================================================================
        [Fact]
        public async Task Running_every_module_dataset_writes_no_business_row()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View, InventoryReportPermissions.View, CrmReportPermissions.View);

            await SeedAccountingAsync(host);
            await SeedInventoryAsync(host);
            await SeedCrmAsync(host);

            async Task<int[]> CountsAsync()
            {
                var db = host.NewContext();
                return new[]
                {
                    await db.SalesInvoices.CountAsync(), await db.PurchaseInvoices.CountAsync(),
                    await db.JournalEntries.CountAsync(), await db.StockBalances.CountAsync(),
                    await db.StockMovements.CountAsync(), await db.Leads.CountAsync(),
                    await db.Opportunities.CountAsync(), await db.Customers.CountAsync(),
                    await db.Items.CountAsync(),
                };
            }

            var before = await CountsAsync();

            foreach (var (code, source) in new (string, IReportDataSource)[]
            {
                (AccountingDatasetCodes.SalesRevenue, new SalesRevenueDataSource(host.Db)),
                (AccountingDatasetCodes.Purchases, new PurchasesDataSource(host.Db)),
                (AccountingDatasetCodes.JournalActivity, new JournalActivityDataSource(host.Db)),
                (InventoryDatasetCodes.StockOnHand, new StockOnHandDataSource(host.Db)),
                (InventoryDatasetCodes.StockMovements, new StockMovementsDataSource(host.Db)),
                (CrmDatasetCodes.Leads, new CrmLeadsDataSource(host.Db)),
                (CrmDatasetCodes.Opportunities, new CrmOpportunitiesDataSource(host.Db)),
            })
            {
                var result = await RunAsync(host, source, code);
                Assert.True(result.IsSuccess);
            }

            // Re-read from a NEW context, per CLAUDE.md: proving from the context that ran the query would be
            // asserting against the same tracked graph.
            Assert.Equal(before, await CountsAsync());
        }

        // =========================================================================================
        // GATE 6 — FIELD SENSITIVITY. The cost and margin tiers are the reason these datasets are safe to give
        // to an operational role at all, so the gate is proved rather than assumed.
        // =========================================================================================
        [Fact]
        public void Cost_and_margin_fields_are_hidden_without_their_own_key()
        {
            var onHand = InventoryDatasets.StockOnHand();
            var withView = new HashSet<string>(StringComparer.Ordinal) { InventoryReportPermissions.View };
            var withCost = new HashSet<string>(StringComparer.Ordinal)
                { InventoryReportPermissions.View, InventoryReportPermissions.Cost };

            var viewOnly = onHand.VisibleFields(withView).Select(f => f.Key).ToList();
            Assert.DoesNotContain("AvgCost", viewOnly);
            Assert.DoesNotContain("TotalValue", viewOnly);
            Assert.Contains("QtyOnHand", viewOnly);          // quantity is NOT gated

            var costed = onHand.VisibleFields(withCost).Select(f => f.Key).ToList();
            Assert.Contains("AvgCost", costed);
            Assert.Contains("TotalValue", costed);

            var profitability = AccountingDatasets.CustomerProfitability();
            var billingOnly = profitability.VisibleFields(
                new HashSet<string>(StringComparer.Ordinal) { AccountingReportPermissions.View })
                .Select(f => f.Key).ToList();

            Assert.DoesNotContain("Margin", billingOnly);
            Assert.DoesNotContain("Cogs", billingOnly);
            Assert.DoesNotContain("Revenue", billingOnly);
            Assert.Contains("Invoiced", billingOnly);        // what they were billed is not gated
        }

        // =========================================================================================
        // GATE 7 — THE DELEGATING SOURCES. They must degrade to empty when the Accounting module is not
        // registered, because AddCrossBusinessReporting is self-sufficient and nine DI tests depend on it.
        // =========================================================================================
        [Fact]
        public async Task A_delegating_source_returns_empty_when_its_module_is_absent()
        {
            using var host = new ReportingTestHost();

            // A provider that resolves NOTHING — the shape of a host that never registered Accounting.
            var source = new CustomerAgingDataSource(new EmptyProvider(), host.Clock);

            var set = await source.FetchAsync(new ReportDataQuery
            {
                Definition = host.Catalog.GetDefinition(AccountingDatasetCodes.CustomerAging),
                Context = host.Ctx,
                Parameters = EmptyParameters(),
            });

            Assert.Empty(set.Rows);
        }

        // =========================================================================================
        // GATE 8 — ONE END-TO-END QUERY PER MODULE, through the real façade, asserting on the rendered output
        // rather than on an internal count. This is the "can a person actually get this report" test.
        // =========================================================================================
        [Fact]
        public async Task End_to_end_accounting_query_renders_the_expected_invoice()
        {
            using var host = new ReportingTestHost();
            Grant(host, AccountingReportPermissions.View);
            await SeedAccountingAsync(host);

            var result = await RunAsync(host, new SalesRevenueDataSource(host.Db), AccountingDatasetCodes.SalesRevenue);

            Assert.True(result.IsSuccess);
            var body = Text(result);
            Assert.Contains("SI-1", body);
            Assert.Contains("Customer One", body);   // the lookup resolved, culture-aware
            Assert.Contains("114", body);            // the stored GrandTotal, unrounded by Reporting
        }

        [Fact]
        public async Task End_to_end_inventory_query_renders_the_expected_stock_row()
        {
            using var host = new ReportingTestHost();
            Grant(host, InventoryReportPermissions.View, InventoryReportPermissions.Cost);
            await SeedInventoryAsync(host);

            var result = await RunAsync(host, new StockOnHandDataSource(host.Db), InventoryDatasetCodes.StockOnHand,
                new Dictionary<string, string?>());

            Assert.True(result.IsSuccess);
            var body = Text(result);
            Assert.Contains("IT-1", body);
            Assert.Contains("Item One", body);
            Assert.Contains("Main", body);           // warehouse lookup resolved
        }

        [Fact]
        public async Task End_to_end_crm_query_renders_the_configured_stage_name()
        {
            using var host = new ReportingTestHost();
            Grant(host, CrmReportPermissions.View);
            await SeedCrmAsync(host);

            var result = await RunAsync(host, new CrmOpportunitiesDataSource(host.Db), CrmDatasetCodes.Opportunities);

            Assert.True(result.IsSuccess);
            var body = Text(result);
            Assert.Contains("Opp One", body);

            // The CONFIGURED pipeline stage wins over the stored free-text Stage for an opportunity on a
            // pipeline, and the free-text one is the fallback for the opportunity that is not.
            Assert.Contains("Negotiation", body);
            Assert.Contains("Won", body);
        }

        // ---- helpers ---------------------------------------------------------------------------------
        private static string Text(ReportResult result) =>
            result.Artifact is null ? "" : System.Text.Encoding.UTF8.GetString(result.Artifact.Content);

        // A parameter set with nothing in it. Used only by the two tests that call a data source DIRECTLY,
        // past the engine — which is the point of those tests: they assert the SOURCE's own guard rather than
        // relying on the binder or on authorization having refused first.
        private static ReportParameterSet EmptyParameters() =>
            new(Array.Empty<ReportParameterValue>(), new Dictionary<string, string?>());

        // Records whether it was entered. The only way to prove a denial stopped BEFORE the data.
        private sealed class EntryRecordingSource : IReportDataSource
        {
            public EntryRecordingSource(string key) { Key = key; }
            public string Key { get; }
            public bool WasEntered { get; private set; }

            public Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
            {
                WasEntered = true;
                return Task.FromResult(new ReportDataSetBuilder(query.Definition.Columns).Build());
            }
        }

        // Resolves nothing at all.
        private sealed class EmptyProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
