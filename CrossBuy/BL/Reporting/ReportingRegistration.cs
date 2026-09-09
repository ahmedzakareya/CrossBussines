using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — DI REGISTRATION.
    //
    // ONE extension method, so Program.cs gains ONE line. That is not cosmetic: Program.cs is a file the parallel
    // team is also editing, and a thirty-line block there would collide on every merge. It also means the whole
    // platform's lifetime story is reviewable in one place.
    //
    // LIFETIME RULES, each one earned by a defect this codebase already recorded:
    //
    //   SINGLETON is used ONLY for objects that inject nothing scoped: the clock, the options, the pure catalog and
    //   its pure providers, the stateless renderers/exporters and their registries. CLAUDE.md's rule is that a
    //   process-lifetime object capturing a scoped service serves every later request from the FIRST request's
    //   state — so the test for singleton here is "does it hold any per-request state at all", and the answer must
    //   be no.
    //
    //   SCOPED is used for everything that touches CrossDbContext or the BusinessContext: the engine, the façade,
    //   authorization, templates, library, history, archive, schedules, delivery, and every data source.
    //
    //   NO HOSTED SERVICE is registered. The schedule runner is scoped and invocable; the worker that would drive
    //   it is a separate, reviewed change — see the header of ReportScheduleService.cs for the three reasons.
    //   When that worker is added it must take IServiceScopeFactory (a hosted service is a singleton and may never
    //   inject a scoped service) and must be added to Stage1DiWiringTests, which builds the real graph with
    //   ValidateOnBuild + ValidateScopes.
    // ============================================================================================
    public static class ReportingServiceCollectionExtensions
    {
        // configure: lets a host set the archive root, the row ceilings and the permission role map without this
        // method growing parameters.
        public static IServiceCollection AddCrossBusinessReporting(this IServiceCollection services,
            Action<ReportingPlatformOptions>? configure = null)
        {
            var options = new ReportingPlatformOptions();
            configure?.Invoke(options);

            // ---- options + clock (singletons: immutable, no scoped dependency) ---------------------------
            services.AddSingleton(options.Engine);
            services.AddSingleton(options.Archive);
            services.AddSingleton(options.Permissions);
            services.AddSingleton<IReportClock, SystemReportClock>();

            // ---- catalog (singleton: definitions are code-first and frozen) ------------------------------
            //
            // Registered as a singleton on purpose, and safe because ReportCatalog + its providers inject nothing.
            // Validation runs in the catalog's constructor, so a malformed definition fails at graph construction
            // rather than on a user's first click.
            services.AddSingleton<IReportDefinitionProvider, PlatformReportDefinitionProvider>();
            services.AddSingleton<IReportCatalog, ReportCatalog>();

            // ---- value/shaping layer (singletons: pure functions over their arguments) --------------------
            services.AddSingleton<IReportDataShaper, ReportDataShaper>();
            services.AddSingleton<IReportScheduleCalculator, ReportScheduleCalculator>();
            services.AddSingleton<IReportParameterBinder, ReportParameterBinder>();

            // ---- renderers + exporters -------------------------------------------------------------------
            //
            // THE SUBSTITUTION POINT. Registering another IReportRenderer for Pdf AFTER this call replaces PDF
            // generation product-wide with no caller change (the registry is last-wins by design). A future
            // StimulsoftRenderer is exactly this one line.
            // Stateless, so a singleton. Injected into HtmlReportRenderer, which is where the visual branch lives.
            services.AddSingleton<IReportVisualRenderer, ReportVisualRenderer>();

            services.AddSingleton<HtmlReportRenderer>();
            services.AddSingleton<IReportRenderer>(sp => sp.GetRequiredService<HtmlReportRenderer>());

            // The PDF renderer is registered even though its converter is unbound: the registry reports it
            // unavailable, so a Pdf request fails with a clear operator-facing reason instead of "no renderer".
            // V2: the browser seam is BOUND. PlaywrightPdfReportRenderer was always registered and always
            // answered "engine unavailable" because its converter was the unconfigured stub. A singleton
            // because it holds one Chromium instance for the process — see the converter's header.
            services.AddSingleton<IHtmlToPdfConverter, PlaywrightHtmlToPdfConverter>();
            services.AddSingleton<IReportRenderer, PlaywrightPdfReportRenderer>();

            services.AddSingleton<IReportExporter, CsvReportExporter>();
            services.AddSingleton<IReportExporter, ExcelReportExporter>();

            services.AddSingleton<IReportRendererRegistry, ReportRendererRegistry>();
            services.AddSingleton<IReportExportEngine, ReportExportEngine>();
            services.AddSingleton<IReportOutputPipeline, ReportOutputPipeline>();

            // ---- archive store (singleton: stateless over the filesystem) --------------------------------
            services.AddSingleton<IReportArchiveStore, FileSystemReportArchiveStore>();

            // ---- print transports ------------------------------------------------------------------------
            services.AddSingleton<IReportPrintTransport, BrowserPrintTransport>();

            // ---- delivery --------------------------------------------------------------------------------
            //
            // NullReportMailSender records deliveries as Skipped rather than Sent. Replace it with an SMTP or
            // outbox-backed sender to switch delivery on — one line, no other change.
            services.AddSingleton<IReportMailSender, NullReportMailSender>();
            services.AddScoped<IReportDeliveryChannel, EmailReportDeliveryChannel>();
            services.AddScoped<IReportDeliveryService, ReportDeliveryService>();

            // ---- scoped services (DbContext / BusinessContext) -------------------------------------------
            services.AddScoped<IReportPermissionEvaluator, RoleMapReportPermissionEvaluator>();
            services.AddScoped<IReportTeamResolver, EmployeeDepartmentTeamResolver>();
            services.AddScoped<IReportAuthorizationService, ReportAuthorizationService>();
            services.AddScoped<IReportBrandingProvider, CompanyReportBrandingProvider>();
            services.AddScoped<IReportTemplateService, ReportTemplateService>();
            services.AddScoped<IReportLibraryService, ReportLibraryService>();
            services.AddScoped<IReportHistoryService, ReportHistoryService>();
            services.AddScoped<IReportArchiveService, ReportArchiveService>();
            services.AddScoped<IReportScheduleService, ReportScheduleService>();
            services.AddScoped<IReportSchedulePrincipalFactory, IdentityReportSchedulePrincipalFactory>();
            services.AddScoped<IReportScheduleRunner, ReportScheduleRunner>();

            // ---- the pipeline + the one public door ------------------------------------------------------
            services.AddScoped<IReportEngine, ReportEngine>();
            services.AddScoped<IReportService, ReportService>();
            services.AddScoped<IReportPrintService, ReportPrintService>();

            // ---- platform data sources -------------------------------------------------------------------
            //
            // Module data sources are registered by their own modules, later. Nothing about the platform changes
            // when they are: one AddScoped<IReportDataSource, XDataSource>() line each.
            services.AddScoped<IReportDataSource, ReportCatalogInventoryDataSource>();
            services.AddScoped<IReportDataSource, ReportRunHistoryDataSource>();

            // R1 ACTIVATION — the Business Event log.
            //
            // The platform's first dataset-backed report. It reads BusinessEvents, the kernel's own append-only
            // fact log — NOT a module table, so activation still couples reporting to no module's schema.
            //
            // Its three permission keys are UNMAPPED by default, and the evaluator is fail-closed, so registering
            // it grants nobody anything until a host maps them deliberately. See BusinessEventsDataset for why the
            // per-entity View check the kernel applies is replaced by a single stricter report permission.
            services.AddScoped<IReportDataSource, BusinessEventsReportDataSource>();
            services.AddSingleton<IReportDefinitionProvider, BusinessEventsReportDefinitionProvider>();
            services.AddScoped<IReportDatasetDefinition>(_ => BusinessEventsDataset.Definition());

            // ---- R2 ACTIVATION — the MODULE datasets: Accounting · Inventory · CRM ------------------------
            //
            // R1's single platform dataset proved the layer; it could not make Report Studio useful, because a
            // builder over one dataset is a builder over nothing. These are that prerequisite.
            //
            // THE SHAPE IS IDENTICAL FOR ALL THREE, deliberately, and it is the shape R1 predicted: a definition
            // provider (singleton — pure, injects nothing), one data source per dataset (scoped — they touch
            // CrossDbContext or a module service), and the dataset definitions themselves. No platform type
            // changed to accommodate them, which is the property the seam existed to have.
            //
            // TWO OF THE ACCOUNTING SOURCES INJECT IReceivableService RATHER THAN CrossDbContext, and that is the
            // architecture visible in the DI list: receivables aging and customer profitability are Accounting's
            // calculations, so Reporting calls the owner instead of reproducing the arithmetic. A source that
            // needs a module service is a source that is reading a module's answer.
            //
            // EVERY key below is UNMAPPED until a host maps it, and the evaluator is fail-closed — so registering
            // these datasets grants nobody anything by itself. See Program.cs for the deliberate mapping.
            services.AddSingleton<IReportDefinitionProvider, AccountingReportDefinitionProvider>();
            services.AddScoped<IReportDataSource, SalesRevenueDataSource>();
            services.AddScoped<IReportDataSource, PurchasesDataSource>();
            services.AddScoped<IReportDataSource, CustomerAgingDataSource>();
            services.AddScoped<IReportDataSource, CustomerProfitabilityDataSource>();
            services.AddScoped<IReportDataSource, JournalActivityDataSource>();

            // The voucher: one entry as a document, which is what the journal screen prints.
            services.AddScoped<IReportDataSource, JournalVoucherDataSource>();

            // The trade documents. One source per table, one shape out — the field keys are identical
            // across all three, which is what lets a single house layout print an invoice and a
            // quotation without being authored twice.
            services.AddScoped<IReportDataSource, SalesInvoiceDocumentSource>();
            services.AddScoped<IReportDataSource, PurchaseInvoiceDocumentSource>();
            services.AddScoped<IReportDataSource, QuotationDocumentSource>();

            foreach (var document in TradeDocumentDatasets.All())
            {
                var definition = document;
                services.AddScoped<IReportDatasetDefinition>(_ => definition);
            }

            services.AddSingleton<IReportDefinitionProvider, InventoryReportDefinitionProvider>();
            services.AddScoped<IReportDataSource, StockOnHandDataSource>();
            services.AddScoped<IReportDataSource, StockMovementsDataSource>();

            services.AddSingleton<IReportDefinitionProvider, CrmReportDefinitionProvider>();
            services.AddScoped<IReportDataSource, CrmLeadsDataSource>();
            services.AddScoped<IReportDataSource, CrmOpportunitiesDataSource>();

            // Administrative structure — dbo.Hierarchicals, the organisation tree behind
            // /Admin/AdministrativeStructure. One dataset, one source, registered exactly like the modules
            // above so it reaches Studio, widgets, exports and scheduling with no new execution path.
            //
            // Its tenancy is DERIVED rather than stored: that table has no CompanyID, so the source enters
            // the tree only at the type-1 node carrying the caller's own company and walks down from there.
            // See the file header for what that means and where it differs from the screen.
            services.AddSingleton<IReportDefinitionProvider, OrgStructureReportDefinitionProvider>();
            services.AddScoped<IReportDataSource, OrgStructureDataSource>();

            // HR Product Batch 2 — roster. Two data sources, no new engine and no second store.
            services.AddScoped<IReportDataSource, RosterScheduleDataSource>();
            services.AddScoped<IReportDataSource, RosterPlannedVsActualDataSource>();

            // HR-B3: the planned-vs-actual data source asks the canonical attendance authority what
            // an employee was expected to work, rather than carrying a second copy of the formula.
            // That makes the resolver a dependency of REPORTING, so reporting must be able to supply
            // it — Program.cs also registers it, and a module whose graph only stands up because the
            // host happened to register something first is a graph that breaks the moment anyone
            // builds it in isolation. TryAdd so the host's registration still wins if present.
            //
            // The DI validation test caught exactly this, which is why it exists.
            services.TryAddScoped<CrossBuy.BL.Hr.IAttendanceBaselineResolver,
                                  CrossBuy.BL.Hr.AttendanceBaselineResolver>();

            services.AddScoped<IReportDataSourceRegistry, ReportDataSourceRegistry>();

            // ---- dataset layer (ADR-037 §Dataset) --------------------------------------------------------
            //
            // SCOPED, not singleton, and the reason is ListForStudioAsync: it calls IReportPermissionEvaluator,
            // which is scoped because it reads the caller's BusinessContext. A singleton registry holding a
            // scoped evaluator is the captive dependency CLAUDE.md records as having once stopped this
            // application from booting — ValidateScopes would (correctly) refuse to build the graph.
            //
            // TEN datasets are registered as of R2: the platform's Business Event log (above) plus five
            // Accounting, two Inventory and two CRM. Each is a separate AddScoped<IReportDatasetDefinition>
            // registration — the registry validates every one in its constructor and refuses duplicate codes, so
            // a malformed or colliding dataset fails at graph construction rather than on a user's first click.
            foreach (var accounting in AccountingDatasets.All())
            {
                var definition = accounting;   // captured per iteration, NOT the loop variable's final value
                services.AddScoped<IReportDatasetDefinition>(_ => definition);
            }
            foreach (var inventory in InventoryDatasets.All())
            {
                var definition = inventory;
                services.AddScoped<IReportDatasetDefinition>(_ => definition);
            }
            foreach (var crm in CrmDatasets.All())
            {
                var definition = crm;
                services.AddScoped<IReportDatasetDefinition>(_ => definition);
            }

            // HR Product Batch 2 — the two roster datasets, registered exactly like the modules above
            // so they reach Studio, widgets and scheduling with no new execution path.
            foreach (var roster in HrRosterDatasets.All())
            {
                var definition = roster;
                services.AddScoped<IReportDatasetDefinition>(_ => definition);
            }

            // The administrative structure, registered the same way. Same per-iteration capture: the loop
            // variable's final value in a closure is the bug this pattern exists to avoid.
            foreach (var org in OrgStructureDatasets.All())
            {
                var definition = org;
                services.AddScoped<IReportDatasetDefinition>(_ => definition);
            }

            services.AddScoped<IReportDatasetRegistry, ReportDatasetRegistry>();

            // ---- WAVE 1: Report Studio -------------------------------------------------------------------
            //
            // SCOPED, because it resolves the caller's BusinessContext and their permissions on every call —
            // which is the whole point: a Studio draft is untrusted input and is re-validated per request,
            // never cached across them.
            //
            // It adds no engine, no second saved-report store and no execution path: it composes the dataset
            // registry, the permission evaluator, the template service and IReportService, all of which were
            // already here. See the file header for the one thing it does add — the draft gate.
            services.AddScoped<IReportStudioService, ReportStudioService>();

            // V2 — the visual designer's two supporting services.
            //
            // The validator is a SINGLETON: it is a pure function of (layout, dataset, permitted sets) and
            // holds nothing per-request. The permitted sets are resolved per request and handed IN, which is
            // what keeps a stateless validator from becoming a stale-permission cache.
            services.AddSingleton<IReportVisualLayoutValidator, ReportVisualLayoutValidator>();

            // THE HOST ANCHORS THE ASSET ROOT. A relative default is resolved against the content root, which
            // is the only place that knows where the application actually lives; an absolute one set by the
            // host is honoured untouched.
            services.AddSingleton(sp =>
            {
                var assets = options.Assets;
                if (!Path.IsPathRooted(assets.RootPath))
                {
                    var root = sp.GetService<IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;
                    assets.RootPath = Path.Combine(root, assets.RootPath);
                }
                return assets;
            });
            services.AddScoped<IReportAssetService, ReportAssetService>();

            // ---- R3: the user-facing surface --------------------------------------------------------------
            //
            // The presenter behind the Reports Center and the Report Viewer. SCOPED: it holds the report
            // services, which hold CrossDbContext.
            services.AddScoped<IReportsCenterPresenter, ReportsCenterPresenter>();

            // ---- R3 Phase 5: the Workspace extension point ------------------------------------------------
            //
            // Reporting CONTRIBUTES to the Workspace; the Workspace does not reach into Reporting. This is the
            // only place the two products meet, and it is one registration of one adapter against an interface
            // the Workspace publishes for exactly this purpose (IWorkspaceReportSource).
            //
            // Registered HERE rather than in the Workspace's own AddCrossBusinessWorkspace() so that a
            // deployment which has not activated Reporting simply has no contributor — the Workspace panel
            // reports "unavailable" honestly instead of failing to resolve a service.
            services.AddScoped<CrossBuy.BL.Workspace.IWorkspaceReportSource, ReportingWorkspaceSource>();

            return services;
        }
    }

    // Everything a host may configure, in one object so the extension method never grows parameters.
    public sealed class ReportingPlatformOptions
    {
        public ReportEngineOptions Engine { get; } = new();
        public ReportArchiveOptions Archive { get; } = new();
        public ReportPermissionOptions Permissions { get; } = new();
        public ReportAssetOptions Assets { get; } = new();

        // Convenience for the common case: point the archive at the web root's uploads folder, matching where the
        // product's other uploads live.
        public ReportingPlatformOptions UseArchiveRoot(string rootPath)
        {
            if (!string.IsNullOrWhiteSpace(rootPath)) Archive.RootPath = rootPath;
            return this;
        }

        // Where designer images live. Beside the archive by default; a host that separates uploads can point
        // it elsewhere without the platform learning about the path.
        public ReportingPlatformOptions UseAssetRoot(string rootPath)
        {
            if (!string.IsNullOrWhiteSpace(rootPath)) Assets.RootPath = rootPath;
            return this;
        }

        // Maps a report permission key to the roles that hold it. Until IReportPermissionEvaluator is bound to the
        // platform's real permission provider, this is how module reports become reachable — and an UNMAPPED key
        // stays denied, which is the intended fail-closed default.
        public ReportingPlatformOptions MapPermission(string permissionKey, params string[] roles)
        {
            Permissions.RoleMap[permissionKey] = roles;
            return this;
        }

        public ReportingPlatformOptions AdministratorRoles(params string[] roles)
        {
            Permissions.AdministratorRoles = roles;
            return this;
        }
    }
}