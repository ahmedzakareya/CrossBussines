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
            services.AddSingleton<HtmlReportRenderer>();
            services.AddSingleton<IReportRenderer>(sp => sp.GetRequiredService<HtmlReportRenderer>());

            // The PDF renderer is registered even though its converter is unbound: the registry reports it
            // unavailable, so a Pdf request fails with a clear operator-facing reason instead of "no renderer".
            services.AddSingleton<IHtmlToPdfConverter, UnconfiguredHtmlToPdfConverter>();
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

            services.AddScoped<IReportDataSourceRegistry, ReportDataSourceRegistry>();

            // ---- dataset layer (ADR-037 §Dataset) --------------------------------------------------------
            //
            // SCOPED, not singleton, and the reason is ListForStudioAsync: it calls IReportPermissionEvaluator,
            // which is scoped because it reads the caller's BusinessContext. A singleton registry holding a
            // scoped evaluator is the captive dependency CLAUDE.md records as having once stopped this
            // application from booting — ValidateScopes would (correctly) refuse to build the graph.
            //
            // ONE dataset is registered as of R1: Platform.BusinessEvents.Log (above). Accounting, Inventory and
            // CRM datasets remain out of scope — a module adds one AddScoped<IReportDatasetDefinition, X>() line
            // when its own increment arrives, and nothing in the platform changes.
            services.AddScoped<IReportDatasetRegistry, ReportDatasetRegistry>();

            // ---- R3: the user-facing surface --------------------------------------------------------------
            //
            // The presenter behind the Reports Center and the Report Viewer. SCOPED: it holds the report
            // services, which hold CrossDbContext.
            services.AddScoped<IReportsCenterPresenter, ReportsCenterPresenter>();

            // ---- R3 Phase 5: the Workspace extension point --------------------------------------------
            //
            // DEFERRED, NOT DROPPED. Reporting CONTRIBUTES to the Workspace through
            // IWorkspaceReportSource, implemented by ReportingWorkspaceSource. That adapter and the
            // contract it implements (CrossBuy.BL.Workspace) are NOT committed yet, and an adapter
            // cannot land before the interface it implements. Both the adapter and this one
            // registration belong to the Workspace ownership phase:
            //
            //     services.AddScoped<CrossBuy.BL.Workspace.IWorkspaceReportSource, ReportingWorkspaceSource>();
            //
            // Until then the Workspace simply has no Reporting contributor, which is the exact
            // degradation the original comment describes: the panel reports "unavailable" honestly
            // rather than failing to resolve a service.

            return services;
        }
    }

    // Everything a host may configure, in one object so the extension method never grows parameters.
    public sealed class ReportingPlatformOptions
    {
        public ReportEngineOptions Engine { get; } = new();
        public ReportArchiveOptions Archive { get; } = new();
        public ReportPermissionOptions Permissions { get; } = new();

        // Convenience for the common case: point the archive at the web root's uploads folder, matching where the
        // product's other uploads live.
        public ReportingPlatformOptions UseArchiveRoot(string rootPath)
        {
            if (!string.IsNullOrWhiteSpace(rootPath)) Archive.RootPath = rootPath;
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