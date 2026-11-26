using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — DI WIRING.
    //
    // This file exists because of a defect this codebase already paid for, recorded in CLAUDE.md:
    //
    //      "A DI graph is not verified by unit tests that construct services by hand. 112 green tests coexisted
    //       with an application that could not boot."
    //
    // ReportingTestHost wires the platform by hand, which is right for testing behaviour and useless for testing
    // WIRING. So this file builds the REAL container from the REAL registration extension with ValidateOnBuild and
    // ValidateScopes, and resolves the façade.
    // ============================================================================================
    public class ReportingDiWiringTests
    {
        // The minimum the reporting registration depends on from outside itself: a DbContext, the company-scope
        // holder it needs, and the BusinessContext accessor the façade resolves. Deliberately NOT the whole
        // application graph — that is Stage1DiWiringTests's job, and duplicating it here would make this file fail
        // for unrelated reasons.
        private static ServiceProvider Build(Action<ReportingPlatformOptions>? configure = null, bool includeKpis = false)
        {
            var services = new ServiceCollection();

            services.AddLogging();

            services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
            services.AddDbContext<CrossDbContext>(options =>
                options.UseSqlite("DataSource=:memory:"));

            // The façade's only external dependency. A real one needs HttpContext; the reporting graph only needs
            // it to be RESOLVABLE, which is exactly what ValidateOnBuild checks.
            services.AddScoped<IBusinessContextAccessor, StubBusinessContextAccessor>();

            // THE FOURTH. ReportOrgImageProvider needs IWebHostEnvironment to locate the company
            // and branch marks; the graph stopped building when nobody registered one. See
            // ReportingTestHostEnvironment for why it is a stub rather than a real web root.
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(
                new ReportingTestHostEnvironment());

            services.AddCrossBusinessReporting(configure);
            if (includeKpis)
            {
                services.AddScoped<CrossBuy.BL.IProjectsAccessService, KpiProjectsAccess>();
                services.AddProjectsReporting();
                services.AddKpiReporting();
            }

            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                // Both, always. ValidateOnBuild catches a missing registration at build time; ValidateScopes
                // catches a SINGLETON capturing a SCOPED service — the specific defect that makes a process serve
                // every later request from the first request's state.
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }

        [Fact]
        public void The_real_graph_builds_with_ValidateOnBuild_and_ValidateScopes()
        {
            // Construction IS the assertion: BuildServiceProvider throws here if any reporting service has an
            // unresolvable dependency or a lifetime violation.
            using var provider = Build();
            Assert.NotNull(provider);
        }

        [Fact]
        public void Kpi_registry_and_evaluator_resolve_through_the_production_registration()
        {
            using var provider = Build(includeKpis: true);
            using var scope = provider.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IKpiRegistry>();
            // NOT a literal count. This assertion was Assert.Equal(10, ...) and went red the moment the
            // Hospital pack shipped its KPIs (10 -> 22) - a false failure in a test that is not about
            // counting anything. The property worth guarding here is the one whose violation stops the
            // application BOOTING: KpiRegistry validates in its CONSTRUCTOR, so a KPI naming a dataset
            // nobody registered throws while the container is still being built. Assert that instead,
            // against the dataset registry resolved from this same container, and it stays true however
            // many KPIs any module adds later.
            var datasets = scope.ServiceProvider.GetRequiredService<IReportDatasetRegistry>();
            Assert.NotEmpty(registry.Definitions);
            foreach (var kpi in registry.Definitions)
                Assert.True(datasets.TryResolve(kpi.DatasetCode, out _),
                    $"KPI '{kpi.Key}' names dataset '{kpi.DatasetCode}', which is not registered.");
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IKpiEvaluator>());
        }

        private sealed class KpiProjectsAccess : CrossBuy.BL.IProjectsAccessService
        {
            public Task<bool> CanAsync(CrossBuy.Models.Platform.BusinessContext c,string a,CrossBuy.Models.Platform.PermissionTarget? t=null,CancellationToken ct=default) => Task.FromResult(false);
            public Task<CrossBuy.Models.Platform.AccessScope> ResolveProjectScopeAsync(CrossBuy.Models.Platform.BusinessContext c,string a,CancellationToken ct=default)
                => Task.FromResult(CrossBuy.Models.Platform.AccessScope.None());
            public Task<IReadOnlySet<int>> MemberProjectIdsAsync(CrossBuy.Models.Platform.BusinessContext c,CancellationToken ct=default)
                => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
        }

        [Fact]
        public void The_public_facade_and_every_service_behind_it_resolve_in_a_scope()
        {
            using var provider = Build();
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            // The one public door.
            Assert.NotNull(sp.GetRequiredService<IReportService>());

            // ...and everything the pipeline needs behind it.
            Assert.NotNull(sp.GetRequiredService<IReportEngine>());
            Assert.NotNull(sp.GetRequiredService<IReportCatalog>());
            Assert.NotNull(sp.GetRequiredService<IReportAuthorizationService>());
            Assert.NotNull(sp.GetRequiredService<IReportTemplateService>());
            Assert.NotNull(sp.GetRequiredService<IReportParameterBinder>());
            Assert.NotNull(sp.GetRequiredService<IReportDataShaper>());
            Assert.NotNull(sp.GetRequiredService<IReportDataSourceRegistry>());
            Assert.NotNull(sp.GetRequiredService<IReportOutputPipeline>());
            Assert.NotNull(sp.GetRequiredService<IReportRendererRegistry>());
            Assert.NotNull(sp.GetRequiredService<IReportExportEngine>());
            Assert.NotNull(sp.GetRequiredService<IReportArchiveService>());
            Assert.NotNull(sp.GetRequiredService<IReportHistoryService>());
            Assert.NotNull(sp.GetRequiredService<IReportLibraryService>());
            Assert.NotNull(sp.GetRequiredService<IReportScheduleService>());
            Assert.NotNull(sp.GetRequiredService<IReportScheduleRunner>());
            Assert.NotNull(sp.GetRequiredService<IReportDeliveryService>());
            Assert.NotNull(sp.GetRequiredService<IReportPrintService>());
        }

        [Fact]
        public void The_catalog_validates_at_container_build_time_not_on_first_use()
        {
            using var provider = Build();

            // Resolved from the ROOT, because it is a singleton — and a singleton that injected anything scoped
            // would have failed ValidateScopes above.
            var catalog = provider.GetRequiredService<IReportCatalog>();

            Assert.Contains(catalog.GetDefinitions(), d => d.Code == PlatformReportCodes.ReportCatalog);
            Assert.Contains(catalog.GetDefinitions(), d => d.Code == PlatformReportCodes.ReportRunHistory);
        }

        [Fact]
        public void The_singletons_really_are_singletons_and_the_scoped_services_really_are_scoped()
        {
            using var provider = Build();

            // A singleton holding per-request state is the captured-scoped-service defect. These hold none, which
            // is why they may be singletons at all.
            Assert.Same(provider.GetRequiredService<IReportCatalog>(),
                provider.GetRequiredService<IReportCatalog>());
            Assert.Same(provider.GetRequiredService<IReportClock>(),
                provider.GetRequiredService<IReportClock>());
            Assert.Same(provider.GetRequiredService<IReportOutputPipeline>(),
                provider.GetRequiredService<IReportOutputPipeline>());

            using var first = provider.CreateScope();
            using var second = provider.CreateScope();

            Assert.NotSame(first.ServiceProvider.GetRequiredService<IReportService>(),
                second.ServiceProvider.GetRequiredService<IReportService>());
            Assert.NotSame(first.ServiceProvider.GetRequiredService<IReportAuthorizationService>(),
                second.ServiceProvider.GetRequiredService<IReportAuthorizationService>());
        }

        [Fact]
        public void Renderers_and_exporters_are_registered_and_pdf_availability_follows_its_converter()
        {
            using var provider = Build();

            var renderers = provider.GetRequiredService<IReportRendererRegistry>();
            var exporters = provider.GetRequiredService<IReportExportEngine>();

            Assert.NotNull(renderers.Resolve(ReportOutputFormat.Html));
            Assert.NotNull(renderers.Resolve(ReportOutputFormat.PrintHtml));
            Assert.NotNull(exporters.Resolve(ReportOutputFormat.Csv));
            Assert.NotNull(exporters.Resolve(ReportOutputFormat.Xlsx));

            // PDF IS REGISTERED EITHER WAY, so a failure is a clear operator-facing reason rather than
            // "no renderer". Whether it is AVAILABLE is a fact about the DEPLOYMENT: the Playwright browser
            // is a separate download, present on some machines and not on others.
            //
            // This test used to assert unavailability outright, which was true of the increment that wrote
            // it and stopped being true when Microsoft.Playwright was referenced and the converter
            // registered. It then failed on any machine with the browser installed - a test enshrining a
            // stale belief about its own product.
            //
            // So the invariant asserted is the one that holds in both deployments: the registry's claim and
            // its behaviour AGREE. Advertised means resolvable; not advertised means a named refusal.
            if (renderers.AvailableFormats.Contains(ReportOutputFormat.Pdf))
                Assert.NotNull(renderers.Resolve(ReportOutputFormat.Pdf));
            else
                Assert.Throws<ReportRendererUnavailableException>(() => renderers.Resolve(ReportOutputFormat.Pdf));
        }

        [Fact]
        public void The_shipped_permission_evaluator_fails_closed_on_an_unmapped_key()
        {
            using var provider = Build();
            using var scope = provider.CreateScope();

            var evaluator = scope.ServiceProvider.GetRequiredService<IReportPermissionEvaluator>();

            var context = new CrossBuy.Models.Platform.BusinessContext
            {
                CompanyId = 1,
                EmployeeId = 7,
                Roles = new[] { "Reports" },
                Source = CrossBuy.Models.Platform.BusinessContextSource.Test,
            };

            // "Forgot to configure it" must never mean "everyone can see it".
            Assert.False(evaluator.HasPermissionAsync("some.module.report", context).GetAwaiter().GetResult());

            // Administer IS mapped by the registration in Program.cs; here it is mapped by the test's configure.
            Assert.True(evaluator.HasPermissionAsync(ReportPermissions.Public, context).GetAwaiter().GetResult());
        }

        [Fact]
        public void Mapping_a_permission_through_the_options_makes_a_report_reachable()
        {
            using var provider = Build(reporting => reporting
                .MapPermission("some.module.report", "Reports"));

            using var scope = provider.CreateScope();
            var evaluator = scope.ServiceProvider.GetRequiredService<IReportPermissionEvaluator>();

            var context = new CrossBuy.Models.Platform.BusinessContext
            {
                CompanyId = 1, EmployeeId = 7, Roles = new[] { "Reports" },
                Source = CrossBuy.Models.Platform.BusinessContextSource.Test,
            };

            Assert.True(evaluator.HasPermissionAsync("some.module.report", context).GetAwaiter().GetResult());
        }

        [Fact]
        public void Registering_a_replacement_renderer_after_the_platform_substitutes_it()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
            services.AddDbContext<CrossDbContext>(o => o.UseSqlite("DataSource=:memory:"));
            services.AddScoped<IBusinessContextAccessor, StubBusinessContextAccessor>();

            // THE FOURTH. ReportOrgImageProvider needs IWebHostEnvironment to locate the company
            // and branch marks; the graph stopped building when nobody registered one. See
            // ReportingTestHostEnvironment for why it is a stub rather than a real web root.
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(
                new ReportingTestHostEnvironment());

            services.AddCrossBusinessReporting();

            // THE substitution point, exercised exactly as a future StimulsoftRenderer would be added: one line,
            // after the platform, and no caller changes.
            services.AddSingleton<IReportRenderer, SubstituteePdfRenderer>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true, ValidateScopes = true,
            });

            var renderers = provider.GetRequiredService<IReportRendererRegistry>();

            Assert.Equal("Substitute.Pdf", renderers.Resolve(ReportOutputFormat.Pdf).EngineName);
            Assert.Contains(ReportOutputFormat.Pdf, renderers.AvailableFormats);
        }

        [Fact]
        public void A_module_adds_a_report_by_registering_a_provider_and_a_data_source_and_nothing_else()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
            services.AddDbContext<CrossDbContext>(o => o.UseSqlite("DataSource=:memory:"));
            services.AddScoped<IBusinessContextAccessor, StubBusinessContextAccessor>();

            // THE FOURTH. ReportOrgImageProvider needs IWebHostEnvironment to locate the company
            // and branch marks; the graph stopped building when nobody registered one. See
            // ReportingTestHostEnvironment for why it is a stub rather than a real web root.
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(
                new ReportingTestHostEnvironment());

            services.AddCrossBusinessReporting();

            // The ENTIRE integration surface for a module: a definition provider and a data source.
            services.AddSingleton<IReportDefinitionProvider, TestReportDefinitionProvider>();
            services.AddScoped<IReportDataSource>(_ => TestReportDefinitions.CreateDataSource());

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true, ValidateScopes = true,
            });

            Assert.True(provider.GetRequiredService<IReportCatalog>()
                .IsRegistered(TestReportDefinitions.SalesCode));

            using var scope = provider.CreateScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportDataSourceRegistry>()
                .Resolve(TestReportDefinitions.SalesDataSourceKey));
        }

        // ------------------------------------------------------------------------------------------------
        private sealed class StubBusinessContextAccessor : IBusinessContextAccessor
        {
            public Task<CrossBuy.Models.Platform.BusinessContext> GetCurrentAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(CrossBuy.Models.Platform.BusinessContext.ForSystem(1));

            public Task<CrossBuy.Models.Platform.BusinessContext?> TryGetCurrentAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<CrossBuy.Models.Platform.BusinessContext?>(
                    CrossBuy.Models.Platform.BusinessContext.ForSystem(1));
        }

        private sealed class SubstituteePdfRenderer : IReportRenderer
        {
            public string EngineName => "Substitute.Pdf";
            public IReadOnlyList<ReportOutputFormat> Formats { get; } = new[] { ReportOutputFormat.Pdf };
            public bool IsAvailable => true;
            public string? UnavailableReason => null;

            public Task<ReportArtifact> RenderAsync(ReportRenderContext context,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(ReportArtifact.FromText("x.pdf", ReportOutputFormat.Pdf, "x"));
        }
    
        // Walks up from the test assembly to the solution, the same way the other source-scanning
        // guards in this suite do.
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
        // ==========================================================================================
        // A ROLE-MAP ENTRY INSIDE A CLAIMED NAMESPACE IS DEAD POLICY
        // ==========================================================================================

        // ReportModulePermissionAdapter asks the module that claims the key's prefix and returns
        // `decided ?? false` - it NEVER falls back to the role map inside a claimed namespace, and
        // says so: "an unrecognised key inside a claimed namespace is a REFUSAL, not a fall-through".
        //
        // So a MapPermission line for a key whose prefix an authority claims can never be consulted.
        // Found 2026-11-10: accounting.reports.view and accounting.reports.profitability are BOTH
        // mapped in Program.cs and claimed by AccountingReportModuleAuthority. The roles listed there
        // have no effect whatever, while the comment directly above them reads "Narrow these further,
        // or widen them, by editing THESE lines" - so an operator tightening accounting report access
        // by removing a role from that list would believe they had done it.
        //
        // Dead policy that LOOKS live is worse than no policy: nobody re-checks a line they have
        // already edited. This fails until each such key is either unmapped or unclaimed.
        [Fact]
        public void No_mapped_permission_sits_inside_a_namespace_an_authority_already_claims()
        {
            var program = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

            // The prefixes really claimed, taken from the authorities themselves rather than a list.
            var prefixes = typeof(IReportModuleAuthority).Assembly.GetTypes()
                .Where(x => !x.IsAbstract && !x.IsInterface && typeof(IReportModuleAuthority).IsAssignableFrom(x))
                .Select(x => x.GetProperty(nameof(IReportModuleAuthority.KeyPrefix)))
                .Where(pi => pi != null)
                .Select(pi => pi!.GetValue(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(pi.DeclaringType!)) as string)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .ToList();

            Assert.NotEmpty(prefixes);   // positive control: the discovery itself works

            // Every reporting permission constant in the platform, by value.
            var keys = typeof(IReportModuleAuthority).Assembly.GetTypes()
                .Where(x => x.IsAbstract && x.IsSealed && x.Name.Contains("ReportPermissions", StringComparison.Ordinal))
                .SelectMany(x => x.GetFields(BindingFlags.Public | BindingFlags.Static))
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => new { Owner = f.DeclaringType!.Name, f.Name, Value = (string)f.GetRawConstantValue()! })
                .Where(k => k.Value.Contains(".reports.", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(keys);       // positive control

            var dead = keys
                .Where(k => prefixes.Any(pre => k.Value.StartsWith(pre!, StringComparison.Ordinal)))
                .Where(k => program.Contains($"{k.Owner}.{k.Name}", StringComparison.Ordinal))
                .Select(k => $"{k.Value} (mapped as {k.Owner}.{k.Name}, but its namespace is claimed)")
                .ToList();

            Assert.True(dead.Count == 0,
                "These MapPermission entries can never be consulted - the module that claims the " +
                "namespace decides first and the adapter never falls back. Either remove the mapping " +
                "or stop claiming the namespace:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", dead));
        }
}
}
