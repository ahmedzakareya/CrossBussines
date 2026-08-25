using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // Fixture for the Reporting Platform tests (ADR-037).
    //
    // Mirrors PlatformTestHost's choices for the same reasons: the REAL CrossDbContext model (so the reporting
    // entity mapping under test is the production mapping) over a shared in-memory SQLite connection, with
    // referential integrity off because the reporting tables sit at the end of long FK chains
    // (Employee -> AspNetUsers -> …) that have nothing to do with what these tests assert.
    //
    // The clock is FIXED. Relative date tokens ("month-start"), schedule next-run arithmetic and run durations all
    // read IReportClock, and a test that asserted "month-end is the 28th" against the wall clock would pass only
    // in February.
    // ============================================================================================
    public sealed class ReportingTestHost : IDisposable
    {
        // A Thursday, mid-month, mid-quarter — chosen so week/month/quarter boundary arithmetic is all non-trivial
        // (a Sunday or the 1st would let an off-by-one bug pass).
        public static readonly DateTime FixedNow = new(2026, 5, 14, 10, 30, 0);

        private readonly SqliteConnection _connection;
        private readonly string _archiveRoot;

        public ReportingTestHost(int? companyId = 1, int? employeeId = 7, params string[] roles)
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            if (companyId is > 0) Holder.Set(companyId.Value, null);

            Db = NewContext();

            // ────────────────────────────────────────────────────────────────────────────────────────
            // EnsureCreated() IS NOT DEPLOYMENT EVIDENCE. Read this before trusting a green Reporting run.
            //
            // This builds the SQLite schema from the EF MODEL. It proves the model is coherent and it makes
            // these tests fast and hermetic — both legitimate. What it CANNOT prove is that the authored
            // deployment file, CrossBuy/deploy/sql/reporting_platform.sql, creates the same schema.
            //
            // The two disagreed silently once already: 304 Reporting tests were green while a real database
            // returned `Invalid object name 'ReportShares'`, because the slice had never been applied and
            // nothing in this suite could tell. That is what A0 closed.
            //
            // Deployment evidence lives in CrossBuy.Tests/SqlServer/ReportingSchemaDeploymentTests.cs, which
            // applies the real .sql file to an empty SQL Server database. If you change a Reporting entity,
            // that file — not this one — is what proves the change is deployable.
            // ────────────────────────────────────────────────────────────────────────────────────────
            Db.Database.EnsureCreated();

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = OFF;";
                command.ExecuteNonQuery();
            }

            _archiveRoot = Path.Combine(Path.GetTempPath(), "cb-report-tests", Guid.NewGuid().ToString("N")[..12]);

            Clock = new FixedReportClock(FixedNow);

            Context = companyId is > 0
                ? new BusinessContext
                {
                    CompanyId = companyId.Value,
                    EmployeeId = employeeId,
                    UserId = "test-user",
                    Roles = roles.Length > 0 ? roles : new[] { "Reports" },
                    Source = BusinessContextSource.Test,
                }
                : null;

            // ---- the platform, wired by hand ---------------------------------------------------------------
            //
            // Wired by hand rather than through AddCrossBusinessReporting so a test can substitute one piece
            // (a data source, a converter) without rebuilding a container. ReportingDiWiringTests covers the
            // real container separately — CLAUDE.md's rule is that a DI graph is NOT verified by tests that
            // construct services by hand, so both kinds exist.
            PermissionOptions = new ReportPermissionOptions
            {
                AdministratorRoles = new[] { "Admin" },
                RoleMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    [TestReportDefinitions.SalesPermission] = new[] { "Reports", "Sales" },
                    [ReportPermissions.Administer] = new[] { "Admin" },
                },
            };

            Catalog = new ReportCatalog(new IReportDefinitionProvider[]
            {
                new TestReportDefinitionProvider(),
                new PlatformReportDefinitionProvider(),

                // R1 activation. Included here for the same reason the platform provider is: the host builds the
                // catalog by hand, so a report registered only in DI would be invisible to every test — and the
                // Business Event log is the one report whose visibility tiering most needs proving.
                new BusinessEventsReportDefinitionProvider(),

                // R2 activation — the module datasets. Same reason again.
                //
                // Adding them here is SAFE FOR EVERY EXISTING TEST because their permission keys are absent from
                // PermissionOptions.RoleMap below, and the evaluator is fail-closed: the default "Reports" context
                // cannot see any of them, so no existing visibility or presenter assertion changes. A test that
                // wants one maps its key explicitly — which is itself the fail-closed proof.
                new AccountingReportDefinitionProvider(),
                new InventoryReportDefinitionProvider(),
                new CrmReportDefinitionProvider(),
            });

            PermissionEvaluator = new RoleMapReportPermissionEvaluator(PermissionOptions);
            Teams = new EmployeeDepartmentTeamResolver(Db);
            Authorization = new ReportAuthorizationService(Db, PermissionEvaluator, Teams, Clock);
            Templates = new ReportTemplateService(Db, Catalog, Authorization, Teams, Clock);
            Binder = new ReportParameterBinder(Clock);
            Shaper = new ReportDataShaper();

            Html = new HtmlReportRenderer(VisualRenderer);
            PdfConverter = new UnconfiguredHtmlToPdfConverter();
            Renderers = new ReportRendererRegistry(new IReportRenderer[]
            {
                Html,
                new PlaywrightPdfReportRenderer(Html, PdfConverter),
            });
            Exporters = new ReportExportEngine(new IReportExporter[]
            {
                new CsvReportExporter(),
                new ExcelReportExporter(),
            });
            Output = new ReportOutputPipeline(Renderers, Exporters);

            ArchiveOptions = new ReportArchiveOptions { RootPath = _archiveRoot };
            ArchiveStore = new FileSystemReportArchiveStore(ArchiveOptions,
                NullLogger<FileSystemReportArchiveStore>.Instance);
            Archive = new ReportArchiveService(Db, ArchiveStore, Catalog, Authorization, ArchiveOptions, Clock,
                NullLogger<ReportArchiveService>.Instance);
            History = new ReportHistoryService(Db, Catalog, Authorization, Clock,
                NullLogger<ReportHistoryService>.Instance);
            Library = new ReportLibraryService(Db, Catalog, Authorization, Clock);

            DataSource = TestReportDefinitions.CreateDataSource();
            DataSources = new ReportDataSourceRegistry(new IReportDataSource[]
            {
                DataSource,
                new ReportCatalogInventoryDataSource(Catalog, Authorization, Output),
                new ReportRunHistoryDataSource(Db),
            });

            EngineOptions = new ReportEngineOptions();
            Branding = new CompanyReportBrandingProvider(Db);

            Engine = new ReportEngine(Catalog, Authorization, Templates, Binder, DataSources, Shaper, Output,
                Archive, History, Branding, EngineOptions, Clock, NullLogger<ReportEngine>.Instance, Assets);

            ScheduleCalculator = new ReportScheduleCalculator();
            Schedules = new ReportScheduleService(Db, Catalog, Authorization, ScheduleCalculator, Clock);
            MailSender = new NullReportMailSender();
            Delivery = new ReportDeliveryService(Db, Catalog,
                new IReportDeliveryChannel[]
                {
                    new EmailReportDeliveryChannel(MailSender, NullLogger<EmailReportDeliveryChannel>.Instance),
                }, Clock, NullLogger<ReportDeliveryService>.Instance);
            Principals = new IdentityReportSchedulePrincipalFactory(Db);
            ScheduleRunner = new ReportScheduleRunner(Db, Engine, Schedules, ScheduleCalculator, Principals,
                Delivery, Clock, NullLogger<ReportScheduleRunner>.Instance);
        }

        // An engine wired to a DIFFERENT data source, everything else identical.
        //
        // Added for the security-invariant re-proof: proving that a denied run never REACHES the data source
        // needs a source that records whether it was entered. Substituting the registry rather than reaching
        // into the engine keeps this construction identical to the one above, so the test exercises the real
        // pipeline and not a second, simpler one.
        public ReportEngine EngineWith(IReportDataSource dataSource) =>
            new(Catalog, Authorization, Templates, Binder,
                new ReportDataSourceRegistry(new[] { dataSource }),
                Shaper, Output, Archive, History, Branding, EngineOptions, Clock,
                NullLogger<ReportEngine>.Instance, Assets);

        // ---- R3: the user-facing surface, wired on demand ------------------------------------------------
        //
        // Built lazily rather than in the constructor so a test can register a dataset (or swap the data
        // source) BEFORE the presenter closes over it. Every existing test's construction cost is unchanged.

        // The stub resolves the host's own BusinessContext, which is what ReportService and the presenter both
        // read. A host built with companyId: null yields an accessor that resolves nothing — which is exactly
        // the "no company" path the fail-closed tests drive.
        public IBusinessContextAccessor Accessor => Context is null
            ? StubContextAccessor.Unresolved()
            : new StubContextAccessor(Context);

        public ReportService Reports(IReportEngine? engine = null) =>
            new(engine ?? Engine, Catalog, Authorization, Output, Accessor);

        public ReportDatasetRegistry DatasetRegistry(params IReportDatasetDefinition[] datasets) =>
            new(datasets, PermissionEvaluator);

        // The Reports Center / Report Viewer presenter, over the SAME services every other test uses — so a
        // UI-safety assertion is made against the real authorization pipeline rather than a mock of it.
        public ReportsCenterPresenter Presenter(
            IReportEngine? engine = null, params IReportDatasetDefinition[] datasets) =>
            new(Reports(engine), Library, History, Archive, Templates,
                DatasetRegistry(datasets), Catalog, Accessor);

        // ---- REPORT STUDIO V2 --------------------------------------------------------------------
        //
        // Built lazily for the same reason the presenter is: every existing test's construction cost stays
        // exactly what it was, and a V2 test asks for what it needs.
        //
        // The asset ROOT is a per-host temp directory. Assets are files, and a suite that wrote them into a
        // shared folder would make one test's logo visible to the next — which is the one thing an isolation
        // test must not be able to get for free.
        public ReportAssetOptions AssetOptions => _assetOptions ??= new ReportAssetOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "cb-report-assets", Guid.NewGuid().ToString("N")[..12]),
        };
        private ReportAssetOptions? _assetOptions;

        public ReportAssetService Assets => _assets ??= new ReportAssetService(Db, AssetOptions, Clock);
        private ReportAssetService? _assets;

        public ReportVisualLayoutValidator VisualValidator { get; } = new();
        public ReportVisualRenderer VisualRenderer { get; } = new();

        public CompanyScopeHolder Holder { get; } = new();
        public CrossDbContext Db { get; }
        public BusinessContext? Context { get; }
        public FixedReportClock Clock { get; }

        public ReportPermissionOptions PermissionOptions { get; }
        public ReportCatalog Catalog { get; }
        public IReportPermissionEvaluator PermissionEvaluator { get; }
        public IReportTeamResolver Teams { get; }
        public ReportAuthorizationService Authorization { get; }
        public ReportTemplateService Templates { get; }
        public ReportParameterBinder Binder { get; }
        public ReportDataShaper Shaper { get; }
        public HtmlReportRenderer Html { get; }
        public IHtmlToPdfConverter PdfConverter { get; }
        public ReportRendererRegistry Renderers { get; }
        public ReportExportEngine Exporters { get; }
        public ReportOutputPipeline Output { get; }
        public ReportArchiveOptions ArchiveOptions { get; }
        public IReportArchiveStore ArchiveStore { get; }
        public ReportArchiveService Archive { get; }
        public ReportHistoryService History { get; }
        public ReportLibraryService Library { get; }
        public StaticReportDataSource DataSource { get; }
        public ReportDataSourceRegistry DataSources { get; }
        public ReportEngineOptions EngineOptions { get; }
        public IReportBrandingProvider Branding { get; }
        public ReportEngine Engine { get; }
        public ReportScheduleCalculator ScheduleCalculator { get; }
        public ReportScheduleService Schedules { get; }
        public IReportMailSender MailSender { get; }
        public ReportDeliveryService Delivery { get; }
        public IReportSchedulePrincipalFactory Principals { get; }
        public ReportScheduleRunner ScheduleRunner { get; }

        // The BusinessContext, or a failure if the host was built with no company. Tests that need a context say
        // so by calling this, rather than dereferencing a nullable and getting a confusing NRE.
        public BusinessContext Ctx => Context
            ?? throw new InvalidOperationException("This host was built with no resolved company.");

        public CrossDbContext NewContext(ICompanyScopeHolder? scope = null)
        {
            var options = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_connection)
                .EnableSensitiveDataLogging()
                .AddInterceptors(new CompanyWriteGuardInterceptor(
                    NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            return new CrossDbContext(options, scope ?? Holder);
        }

        // A minimal active employee, so the team resolver and the schedule principal factory have something real
        // to read. Only the columns those two touch are set — inventing a full HR record would obscure which
        // columns the code under test actually depends on.
        //
        // `roles` seeds the ASP.NET Identity rows too (a user + its role assignments), because
        // IdentityReportSchedulePrincipalFactory reads roles from there. Without them a SCHEDULED run resolves an
        // employee with no roles and is correctly DENIED — which is a real behaviour, tested separately, but not
        // what most tests are arranging.
        public void SeedEmployee(int id, int companyId = 1, int? departmentId = null, bool active = true,
            string? userId = null, params string[] roles)
        {
            // UserId is UNIQUE on Employee (a one-to-one with AspNetUsers), so it cannot default to "" for more
            // than one row — two employees sharing "" is a unique-constraint violation, not a null.
            var user = userId ?? $"user-{id}";

            Db.Employee.Add(new CrossBuy.Models.Context.Admin.Employee
            {
                ID = id,
                EmpCompanyID = companyId,
                DepartmentID = departmentId,
                IsActive = active,
                FullName = $"Employee {id}",
                FirstName = "E", LastName = id.ToString(),
                Address = "", PhoneNumber = "", Email = $"e{id}@test.local",
                Gender = "", MaritalStatus = "", ProfileImage = "",
                UserId = user,
                JobTitleID = 1,
            });
            Db.SaveChanges();

            foreach (var role in roles) SeedRole(user, role);
        }

        // One Identity role + its assignment. Read-only from the platform's point of view: the principal factory
        // only READS these, and the evaluator — not the factory — makes the authorization decision.
        public void SeedRole(string userId, string role)
        {
            var roleId = $"role-{role}";

            if (!Db.Roles.Any(r => r.Id == roleId))
                Db.Roles.Add(new Microsoft.AspNetCore.Identity.IdentityRole
                {
                    Id = roleId, Name = role, NormalizedName = role.ToUpperInvariant(),
                });

            Db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string>
            {
                UserId = userId, RoleId = roleId,
            });
            Db.SaveChanges();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();

            // The archive store writes real files; a test run must not leave them behind.
            try
            {
                if (Directory.Exists(_archiveRoot)) Directory.Delete(_archiveRoot, recursive: true);
            }
            catch (IOException)
            {
                // A locked temp file is not a test failure.
            }
        }
    }

    // ============================================================================================
    // The definition + data source the tests drive the pipeline with.
    //
    // Deliberately NOT a production report: this slice adds no data source over a production module table, and a
    // test that read Accounting would couple the reporting tests to Accounting's schema.
    // ============================================================================================
    public static class TestReportDefinitions
    {
        public const string SalesCode = "Test.Sales";
        public const string SalesPermission = "test.sales.view";
        public const string SalesDataSourceKey = "Test.Sales";

        public static ReportDefinition Sales() => new()
        {
            Code = SalesCode,
            Module = "Test",
            TitleAr = "تقرير المبيعات",
            TitleEn = "Sales report",
            DataSourceKey = SalesDataSourceKey,
            PermissionKey = SalesPermission,
            CategoryKey = "test.sales",
            Tags = new[] { "test" },
            Columns = new[]
            {
                new ReportColumn { Key = "Branch", TitleAr = "الفرع", TitleEn = "Branch", Groupable = true },
                new ReportColumn { Key = "Category", TitleAr = "التصنيف", TitleEn = "Category", Groupable = true },
                new ReportColumn { Key = "Item", TitleAr = "الصنف", TitleEn = "Item" },
                new ReportColumn
                {
                    Key = "SaleDate", TitleAr = "التاريخ", TitleEn = "Date", Type = ReportFieldType.Date,
                },
                new ReportColumn
                {
                    Key = "Qty", TitleAr = "الكمية", TitleEn = "Qty",
                    Type = ReportFieldType.Integer, Aggregate = ReportAggregate.Sum,
                },
                new ReportColumn
                {
                    Key = "Amount", TitleAr = "المبلغ", TitleEn = "Amount",
                    Type = ReportFieldType.Money, Aggregate = ReportAggregate.Sum,
                },
                new ReportColumn
                {
                    // Internal: must never reach a rendered page or an export, even if a layout names it.
                    Key = "Cost", TitleAr = "التكلفة", TitleEn = "Cost",
                    Type = ReportFieldType.Money, Internal = true,
                },
            },
            Parameters = new[]
            {
                new ReportParameterDescriptor
                {
                    Key = "From", TitleAr = "من", TitleEn = "From",
                    Type = ReportFieldType.Date, Required = true, DefaultValue = "month-start",
                },
                new ReportParameterDescriptor
                {
                    Key = "To", TitleAr = "إلى", TitleEn = "To",
                    Type = ReportFieldType.Date, Required = true, DefaultValue = "today",
                },
                new ReportParameterDescriptor
                {
                    Key = "Branch", TitleAr = "الفرع", TitleEn = "Branch",
                    Options = new[]
                    {
                        new ReportParameterOption { Value = "North", LabelAr = "الشمال", LabelEn = "North" },
                        new ReportParameterOption { Value = "South", LabelAr = "الجنوب", LabelEn = "South" },
                    },
                },
                new ReportParameterDescriptor
                {
                    Key = "MinAmount", TitleAr = "أقل مبلغ", TitleEn = "Minimum amount",
                    Type = ReportFieldType.Decimal, MinValue = "0", MaxValue = "1000000",
                },
                new ReportParameterDescriptor
                {
                    Key = ReportSystemParameters.CompanyId, TitleAr = "الشركة", TitleEn = "Company",
                    Type = ReportFieldType.Integer, SystemSupplied = true,
                },
            },
            DefaultSorts = new[] { ReportSort.By("Branch"), ReportSort.By("Item") },
            Capabilities = new ReportCapabilities { PreviewRows = 2 },
        };

        // Six rows across two branches and two categories — enough for two-level grouping with non-equal
        // subtotals, and small enough that an assertion can name the expected number.
        public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows() => new[]
        {
            Row("North", "Dairy", "Milk", new DateTime(2026, 5, 2), 10, 25.500m, 18m),
            Row("North", "Dairy", "Cheese", new DateTime(2026, 5, 3), 4, 40.000m, 30m),
            Row("North", "Bakery", "Bread", new DateTime(2026, 5, 4), 20, 15.750m, 9m),
            Row("South", "Dairy", "Milk", new DateTime(2026, 5, 5), 7, 17.850m, 12m),
            Row("South", "Bakery", "Bread", new DateTime(2026, 5, 6), 12, 9.450m, 5m),
            Row("South", "Bakery", "Cake", new DateTime(2026, 5, 7), 2, 22.000m, 14m),
        };

        public static StaticReportDataSource CreateDataSource() =>
            new(SalesDataSourceKey, _ => Rows());

        private static IReadOnlyDictionary<string, object?> Row(string branch, string category, string item,
            DateTime date, int qty, decimal amount, decimal cost) => new Dictionary<string, object?>
            {
                ["Branch"] = branch,
                ["Category"] = category,
                ["Item"] = item,
                ["SaleDate"] = date,
                ["Qty"] = qty,
                ["Amount"] = amount,
                ["Cost"] = cost,
            };
    }

    public sealed class TestReportDefinitionProvider : IReportDefinitionProvider
    {
        public string ProviderName => "Test";
        public IEnumerable<ReportDefinition> GetDefinitions() { yield return TestReportDefinitions.Sales(); }
    }
}
