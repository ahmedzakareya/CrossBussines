using CrossBuy.BL.Platform;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch B — the DI wiring itself.
    //
    // WHY THIS FILE EXISTS, AND WHY IT IS NOT COVERED BY THE OTHER TESTS
    //
    // Every other test constructs CrossDbContext explicitly, passing the holder. Production does not: it relies on
    // dependency injection choosing the right CONSTRUCTOR. CrossDbContext now has two public constructors:
    //
    //     CrossDbContext(DbContextOptions<CrossDbContext> options, ICompanyScopeHolder companyScope)   // intended
    //     CrossDbContext(DbContextOptions<CrossDbContext> options)                                    // fail-closed
    //
    // If DI ever selected the second — a different DI container, a package upgrade changing the activator's
    // preference, someone registering the context by hand — every request would silently read NOTHING from the
    // twelve pilot entities, and every write would be refused. The application would look broken rather than
    // insecure, but nothing in the suite would notice, because the suite never asks DI to build one.
    //
    // So this asks DI to build one, exactly as Program.cs does.
    public class Stage1DiWiringTests : IDisposable
    {
        private readonly SqliteConnection _connection;

        public Stage1DiWiringTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
        }

        public void Dispose() => _connection.Dispose();

        // Mirrors the Program.cs registration: the scoped holder, the singleton write guard, and AddDbContext with
        // the (IServiceProvider, DbContextOptionsBuilder) overload that attaches the interceptor.
        private ServiceProvider BuildContainer()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
            services.AddSingleton<CompanyWriteGuardInterceptor>();
            services.AddDbContext<CrossDbContext>((sp, options) =>
            {
                options.UseSqlite(_connection);
                options.AddInterceptors(sp.GetRequiredService<CompanyWriteGuardInterceptor>());
            }, ServiceLifetime.Scoped);

            // ValidateOnBuild surfaces a constructor DI cannot satisfy at build time rather than on first request.
            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }

        // ============================================================================================
        // Stage 1 Batch C — the container must BUILD, which is a separate claim from any rule being right
        // ============================================================================================

        // THE bug this test exists for, found at runtime and not by any unit test:
        //
        //   AggregateException: Some services are not able to be constructed
        //   Error while validating the service descriptor 'ServiceType: IHostedService … ImplementationType:
        //   PermissionScopeStartupValidator': Cannot consume scoped service
        //   'IEnumerable<IModuleAccessService>' from singleton 'IHostedService'.
        //
        // `AddHostedService` registers a SINGLETON. The four module access services are SCOPED (they depend on
        // CrossDbContext). Injecting them into the validator made the whole application fail to start — the
        // exact rule this project already wrote down as a permanent engineering rule ("never capture a scoped
        // service instance in a singleton"), broken by the validator meant to enforce discipline.
        //
        // 111 access-service tests all passed while the app could not boot, because every one of them
        // constructs its service by hand. Only asking the CONTAINER to build catches it.
        [Fact]
        public void The_batch_c_container_builds_with_scope_validation()
        {
            using var provider = BuildBatchCContainer();

            // ...and the validator is genuinely resolvable as the singleton it is registered as.
            var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
            Assert.Contains(hosted, h => h is PermissionScopeStartupValidator);
        }

        // The full Batch C graph, mirroring Program.cs.
        private ServiceProvider BuildBatchCContainer()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
            services.AddSingleton<CompanyWriteGuardInterceptor>();
            services.AddDbContext<CrossDbContext>((sp, options) =>
            {
                options.UseSqlite(_connection);
                options.AddInterceptors(sp.GetRequiredService<CompanyWriteGuardInterceptor>());
            }, ServiceLifetime.Scoped);

            // The Batch C graph, mirroring Program.cs.
            services.AddScoped<IEntityRegistry, EntityRegistry>();
            services.AddScoped<IPlatformRoleDirectory, PlatformRoleDirectory>();

            // Stage 2A B6: the converted access services take the bootstrap policy reader. Registered with the
            // PRODUCTION implementation, not a permissive mock — the whole point of this suite is that the REAL
            // graph builds with ValidateOnBuild + ValidateScopes, and a mock that resolves anything would hide a
            // lifetime defect rather than catch one. Scoped, matching Program.cs.
            services.AddScoped<IBootstrapAccessPolicyReader, BootstrapAccessPolicyReader>();
            services.AddScoped<IOrgHierarchy, OrgHierarchy>();
            services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor, Microsoft.AspNetCore.Http.HttpContextAccessor>();
            services.AddScoped<IBusinessContextFactory, BusinessContextFactory>();
            services.AddScoped<IBusinessContextAccessor, BusinessContextAccessor>();

            // Accounting, because ProjectsAccessService takes the CONCRETE type — asking for
            // IEnumerable<IModuleAccessService> would be a circular dependency, which is what this test catches.
            services.AddScoped<CrossBuy.BL.AccountingAccessService>();
            services.AddScoped<CrossBuy.BL.IAccountingAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.AccountingAccessService>());
            services.AddScoped<IModuleAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.AccountingAccessService>());

            services.AddScoped<CrossBuy.BL.ProjectsAccessService>();
            services.AddScoped<CrossBuy.BL.IProjectsAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.ProjectsAccessService>());
            services.AddScoped<IModuleAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.ProjectsAccessService>());

            services.AddScoped<CrossBuy.BL.HrAccessService>();
            services.AddScoped<CrossBuy.BL.IHrAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.HrAccessService>());
            services.AddScoped<IModuleAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.HrAccessService>());

            // The Func<> that breaks the provider↔module cycle. Without it, RESOLVING the module collection
            // recurses forever and the application hangs before Kestrel starts.
            services.AddScoped<Func<IPlatformPermissionProvider>>(sp => () => sp.GetRequiredService<IPlatformPermissionProvider>());
            services.AddScoped<CrossBuy.BL.TasksAccessService>();
            services.AddScoped<CrossBuy.BL.ITasksAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.TasksAccessService>());
            services.AddScoped<IModuleAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.TasksAccessService>());

            services.AddScoped<CrossBuy.BL.CommunicationAccessService>();
            services.AddScoped<CrossBuy.BL.ICommunicationAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.CommunicationAccessService>());
            services.AddScoped<IModuleAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.CommunicationAccessService>());

            // Calendar. Added the moment CalendarAccessService existed: a new IModuleAccessService that
            // is not in this graph is a service nobody has proved can be constructed.
            services.AddScoped<CrossBuy.BL.CalendarAccessService>();
            services.AddScoped<CrossBuy.BL.ICalendarAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.CalendarAccessService>());
            services.AddScoped<IModuleAccessService>(sp => sp.GetRequiredService<CrossBuy.BL.CalendarAccessService>());

            services.AddScoped<IPlatformPermissionProvider, PlatformPermissionProvider>();
            services.AddScoped<IModulePermissionAdapter, HrPermissionAdapter>();
            services.AddScoped<IModulePermissionAdapter, ProjectsPermissionAdapter>();
            services.AddScoped<IModulePermissionAdapter, TasksPermissionAdapter>();
            services.AddScoped<IModulePermissionAdapter, CommunicationPermissionAdapter>();
            services.AddScoped<IModulePermissionAdapter, CalendarPermissionAdapter>();
            services.AddScoped<IModulePermissionAdapter, DefaultPermissionAdapter>();

            // The singleton hosted service that broke the container. It must take IServiceScopeFactory.
            services.AddHostedService<PermissionScopeStartupValidator>();

            // ValidateOnBuild + ValidateScopes catches a singleton consuming a scoped service. It does NOT catch
            // a cycle through IEnumerable<T> — see Resolving_every_module_access_service_terminates.
            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }

        // ValidateOnBuild is NOT enough, and this test is why.
        //
        // A second defect survived it: TasksAccessService took IPlatformPermissionProvider, whose adapters take
        // IEnumerable<IModuleAccessService> — which contains TasksAccessService. ValidateOnBuild validates call
        // sites without recursing through IEnumerable<T>, so it passed while the real graph recursed forever.
        // The application then hung BEFORE Kestrel started: no exception, no crash, an endless page load and a
        // blank screen.
        //
        // So this test RESOLVES the collection and RUNS the validator, which is what boot actually does.
        [Fact]
        public async Task Resolving_every_module_access_service_terminates_and_the_validator_runs()
        {
            using var provider = BuildBatchCContainer();
            using var scope = provider.CreateScope();

            // If the provider↔module cycle came back, this line never returns.
            var modules = scope.ServiceProvider.GetServices<IModuleAccessService>().ToList();

            // The six this container registers: Accounting (needed by Projects) + the four Batch C modules
            // + Calendar. Inventory/CRM/POS are deliberately absent — they are unchanged by Batch C and pulling
            // them in would make this test about their graphs too.
            //
            // The count is still EXACT, and is still followed by a Contains per module: a new access service
            // that is silently dropped from the container fails here, which is the property this line exists
            // for. Calendar was added when CalendarAccessService was written, not afterwards.
            Assert.Equal(6, modules.Count);
            Assert.Contains(modules, m => m.Scope == EntityRegistry.ScopeAccounting);
            Assert.Contains(modules, m => m.Scope == EntityRegistry.ScopeHr);
            Assert.Contains(modules, m => m.Scope == EntityRegistry.ScopeProjects);
            Assert.Contains(modules, m => m.Scope == EntityRegistry.ScopeTasks);
            Assert.Contains(modules, m => m.Scope == EntityRegistry.ScopeCommunication);
            Assert.Contains(modules, m => m.Scope == EntityRegistry.ScopeCalendar);

            // …and the lazily-resolved provider still works once construction is done.
            var tasks = scope.ServiceProvider.GetRequiredService<CrossBuy.BL.TasksAccessService>();
            Assert.NotNull(tasks);

            // The startup gate itself — exactly what the host awaits before Kestrel binds.
            var validator = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<PermissionScopeStartupValidator>().Single();
            await validator.StartAsync(default);   // must complete, not hang or throw
        }

        // The validator must create its own scope to read the scoped module services — asserted directly, so a
        // future change back to constructor injection fails here rather than at startup.
        [Fact]
        public void The_scope_validator_takes_a_scope_factory_not_the_scoped_services()
        {
            var parameters = typeof(PermissionScopeStartupValidator)
                .GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();

            Assert.Contains(typeof(IServiceScopeFactory), parameters);
            Assert.DoesNotContain(typeof(IEnumerable<IModuleAccessService>), parameters);
        }

        // THE test in this file: the context DI builds must share the scope's holder instance. If it used the
        // fail-closed constructor it would hold a private, permanently unresolved holder and this fails.
        [Fact]
        public void The_injected_context_shares_the_scopes_company_holder()
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var holder = scope.ServiceProvider.GetRequiredService<ICompanyScopeHolder>();
            var db = scope.ServiceProvider.GetRequiredService<CrossDbContext>();

            Assert.Same(holder, db.CompanyScope);
        }

        // ...and the filters therefore see a company resolved AFTER the context was created, which is the real
        // request sequence: the DbContext is built when first injected, the middleware resolves the company next.
        [Fact]
        public async Task A_company_resolved_after_injection_filters_the_injected_context()
        {
            using var provider = BuildContainer();

            // Arrange two companies' rows through a context that may see both.
            using (var setup = provider.CreateScope())
            {
                var holder = setup.ServiceProvider.GetRequiredService<ICompanyScopeHolder>();
                var db = setup.ServiceProvider.GetRequiredService<CrossDbContext>();
                await db.Database.EnsureCreatedAsync();

                var bypass = new CompanyIsolationBypass(
                    holder, new CompanyBypassPolicy(),
                    new LoggingCompanyBypassAudit(NullLogger<LoggingCompanyBypassAudit>.Instance),
                    Microsoft.Extensions.Options.Options.Create(new PublicCatalogOptions()));

                using var _ = bypass.Begin(CompanyBypassKind.CrossCompanyAdministration, new BusinessContext
                {
                    CompanyId = 1, EmployeeId = 7, UserId = "user-7", Roles = new[] { "PlatformOps" },
                }, "test arrangement");

                db.Customers.AddRange(
                    new CrossBuy.Models.Context.Accounting.Customer { CompanyID = 1, Name = "mine" },
                    new CrossBuy.Models.Context.Accounting.Customer { CompanyID = 2, Name = "theirs" });
                await db.SaveChangesAsync();
            }

            // A fresh "request": resolve the context FIRST, then the company — the production order.
            using var request = provider.CreateScope();
            var requestDb = request.ServiceProvider.GetRequiredService<CrossDbContext>();
            Assert.Empty(await requestDb.Customers.AsNoTracking().ToListAsync());   // nothing resolved yet

            request.ServiceProvider.GetRequiredService<ICompanyScopeHolder>().Set(2, null);

            var visible = await requestDb.Customers.AsNoTracking().Select(c => c.Name).ToListAsync();
            Assert.Equal(new[] { "theirs" }, visible);
        }

        // The write guard must be attached by the registration, not only by the tests that construct it by hand.
        [Fact]
        public async Task The_write_guard_is_attached_by_the_registration()
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var db = scope.ServiceProvider.GetRequiredService<CrossDbContext>();
            await db.Database.EnsureCreatedAsync();
            scope.ServiceProvider.GetRequiredService<ICompanyScopeHolder>().Set(1, null);

            db.Customers.Add(new CrossBuy.Models.Context.Accounting.Customer { CompanyID = 2, Name = "elsewhere" });

            await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => db.SaveChangesAsync());
        }

        // Two concurrent "requests" get two holders and two contexts. Asserted through DI rather than by hand,
        // because the Scoped lifetimes are what make the bypass state safe (ADR-023 §2.3) — a mistaken Singleton
        // registration would share one company across every request in the process.
        [Fact]
        public void Two_scopes_get_their_own_holder_and_their_own_context()
        {
            using var provider = BuildContainer();
            using var a = provider.CreateScope();
            using var b = provider.CreateScope();

            var holderA = a.ServiceProvider.GetRequiredService<ICompanyScopeHolder>();
            var holderB = b.ServiceProvider.GetRequiredService<ICompanyScopeHolder>();
            Assert.NotSame(holderA, holderB);

            var dbA = a.ServiceProvider.GetRequiredService<CrossDbContext>();
            var dbB = b.ServiceProvider.GetRequiredService<CrossDbContext>();
            Assert.NotSame(dbA, dbB);
            Assert.Same(holderA, dbA.CompanyScope);
            Assert.Same(holderB, dbB.CompanyScope);

            holderA.Set(1, null);
            Assert.False(holderB.IsResolved);      // no leak between requests
        }
    }
}
