using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Communication Platform — THE DI GRAPH.
    //
    // WHY THIS FILE EXISTS
    //
    // CLAUDE.md states it as a permanent engineering rule, learned from a real outage: "A DI graph is not
    // verified by unit tests that construct services by hand. 112 green tests coexisted with an application
    // that could not boot." Every other test in this folder builds its services through CommunicationTestHost,
    // by hand. This one asks the CONTAINER, exactly as Program.cs would.
    //
    // It also pins the two facts that make this phase "architecture only":
    //   * AddCommunicationPlatform registers NO hosted service.
    //   * The kernel event bridge is the NULL bridge until a deployment opts in twice.
    // =============================================================================================
    public class CommunicationDiWiringTests : IDisposable
    {
        private readonly SqliteConnection _connection;

        public CommunicationDiWiringTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
        }

        public void Dispose() => _connection.Dispose();

        // Mirrors the host's own registration: the scoped holder, the write-guard interceptor, AddDbContext with
        // the (IServiceProvider, builder) overload — plus the PLATFORM KERNEL services this platform CONSUMES.
        //
        // The kernel services are registered here for a reason worth stating: they are this platform's
        // dependencies, not its implementations. If IPlatformPermissionProvider, IEntityRegistry, IOrgHierarchy or
        // ITimelineProjectionService were ever un-registered by the host, this platform would fail to resolve —
        // and this test is where that would surface, rather than on somebody's first comment.
        private ServiceProvider BuildContainer(Action<IServiceCollection>? extra = null)
            => BuildContainer(configuration: null, extra);

        private ServiceProvider BuildContainer(
            IConfiguration? configuration,
            Action<IServiceCollection>? extra)
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

            // ---- platform kernel dependencies (registered by Program.cs in the real host) ----
            services.AddScoped<IEntityRegistry, EntityRegistry>();
            services.AddScoped<IModulePermissionAdapter, DefaultPermissionAdapter>();
            services.AddScoped<IPlatformPermissionProvider, PlatformPermissionProvider>();
            services.AddScoped<IOrgHierarchy, OrgHierarchy>();
            services.AddScoped<ITimelineProjectionService, TimelineProjectionService>();
            services.AddScoped<IPlatformRoleDirectory, PlatformRoleDirectory>();

            // ---- the platform under test: ONE call ----
            services.AddCommunicationPlatform(configuration!);

            extra?.Invoke(services);

            // ValidateOnBuild surfaces a constructor DI cannot satisfy at BUILD time rather than on first request.
            // ValidateScopes catches a singleton capturing a scoped service — the captive dependency that stopped
            // this application from starting once already.
            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }

        // ============================================================================================
        // The container must BUILD. That is a separate claim from any rule being right.
        // ============================================================================================
        [Fact]
        public void Container_builds_with_validate_on_build_and_validate_scopes()
        {
            // No assertion body needed: BuildServiceProvider THROWS on an unsatisfiable constructor or a captive
            // dependency, so reaching the end is the assertion.
            using var provider = BuildContainer();
            Assert.NotNull(provider);
        }

        // Every public entry point of the platform must actually RESOLVE. Building the container proves the
        // descriptors are satisfiable; resolving proves the concrete types' constructors are too, including the
        // IEnumerable<> injections that ValidateOnBuild does not fully exercise.
        [Theory]
        [InlineData(typeof(ICommEntitySurface))]
        [InlineData(typeof(ICommBodyPolicy))]
        [InlineData(typeof(ICommActorDirectory))]
        [InlineData(typeof(ICommAuditWriter))]
        [InlineData(typeof(ICommPrincipalResolver))]
        [InlineData(typeof(ICommAccessPolicy))]
        [InlineData(typeof(ICommEventPublisher))]
        [InlineData(typeof(ICommBusinessEventBridge))]
        [InlineData(typeof(ICommTemplateCatalog))]
        [InlineData(typeof(ICommTemplateRenderer))]
        [InlineData(typeof(ICommTemplateTextProvider))]
        [InlineData(typeof(ICommPreferenceResolver))]
        [InlineData(typeof(ICommNotificationService))]
        [InlineData(typeof(ICommNotificationDispatcher))]
        [InlineData(typeof(ICommDeliveryClaimStore))]
        [InlineData(typeof(ICommFilePreviewProvider))]
        [InlineData(typeof(ICommAttachmentService))]
        [InlineData(typeof(ICommThreadService))]
        [InlineData(typeof(ICommMentionService))]
        [InlineData(typeof(ICommParticipationService))]
        [InlineData(typeof(ICommCommentService))]
        [InlineData(typeof(ICommReactionService))]
        [InlineData(typeof(ICommReadStatusService))]
        [InlineData(typeof(ICommTimelineAggregator))]
        public void Every_public_service_resolves(Type serviceType)
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var resolved = scope.ServiceProvider.GetService(serviceType);
            Assert.NotNull(resolved);
        }

        // ============================================================================================
        // "Architecture only" is a claim this test makes checkable.
        // ============================================================================================

        // NO HOSTED SERVICE. A hosted service is a singleton that may never inject a scoped service, must take
        // IServiceScopeFactory, and must bind an explicit company scope — three rules that each cost a real
        // defect in this repository. This phase ships none, and delivery is a callable drain instead.
        [Fact]
        public void Registers_no_hosted_service()
        {
            var services = new ServiceCollection();
            services.AddCommunicationPlatform(configuration: null!);

            var hosted = services
                .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                .ToList();

            Assert.Empty(hosted);
        }

        // The kernel bridge is OFF by default, and "off" means the NULL implementation is what resolves — not
        // merely that a flag is false. Registering PlatformBusinessEventBridge by default would make
        // IBusinessEventService a hard dependency of this graph, and a deployment whose kernel SQL is not applied
        // would then be unable to comment at all.
        [Fact]
        public void Business_event_bridge_defaults_to_the_null_implementation()
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var bridge = scope.ServiceProvider.GetRequiredService<ICommBusinessEventBridge>();

            Assert.IsType<NullCommBusinessEventBridge>(bridge);
        }

        // PERSISTENT TASK LINKS ARE DECIDED BY THE DATABASE, AND DEFAULT CLOSED.
        //
        // The two failure modes are not symmetric. Off against a provisioned database is a Link button
        // that honestly reports "not provisioned" — a configuration gap somebody notices. On against an
        // UNPROVISIONED one is a SQL-208 from the driver in the middle of somebody's work, which is the
        // HM-D44 shape. This solution reaches BOTH a provisioned database (CrossBuyDB2, via the https
        // profile's connection override) and an unprovisioned one (CrossBuyDev, via appsettings), so the
        // decision cannot come from the build.
        //
        // It resolves the PROBING store, which asks the catalogues before delegating. This fixture is
        // SQLite, so the probe answers "I cannot tell" — and that answer must read as NOT provisioned.
        [Fact]
        public void Task_links_resolve_the_probing_store_and_default_closed()
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var store = scope.ServiceProvider.GetRequiredService<ICommTaskLinkStore>();

            Assert.IsType<ProbingCommTaskLinkStore>(store);
            Assert.False(store.IsProvisioned);
        }

        // THE FLAG CANNOT GRANT. This is the invariant that makes the flag safe to commit.
        //
        // Under the old semantics `true` registered EfCommTaskLinkStore outright, which meant a committed
        // `true` without the committed CrossBuyDB2 connection string handed every clean checkout the EF
        // store pointed at CrossBuyDev — a table that does not exist there, and a SQL-208 on first use.
        // The flag was therefore chained to a machine-local connection string and could never travel.
        //
        // Now `true` means no more than "ask the database", so this asserts a NEGATIVE: declaring the
        // schema provisioned against a database that is not does NOT produce persistence. If anybody
        // restores the old swap, this fails.
        [Fact]
        public void Declaring_the_schema_provisioned_does_not_by_itself_activate_persistence()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CommunicationPlatform:TaskLinksProvisioned"] = "true",
                })
                .Build();

            using var provider = BuildContainer(configuration, extra: null);
            using var scope = provider.CreateScope();

            var store = scope.ServiceProvider.GetRequiredService<ICommTaskLinkStore>();

            Assert.IsType<ProbingCommTaskLinkStore>(store);
            Assert.IsNotType<EfCommTaskLinkStore>(store);

            // The database under this container has no CommTaskLinks and is not even SQL Server, so the
            // claim in configuration is overruled by the catalogue read.
            Assert.False(store.IsProvisioned);
        }

        // THE FLAG CAN STILL RESTRICT. An operator must be able to switch a feature off without a schema
        // change, and explicit `false` does that without a probe at all — the kill switch is not merely
        // "the probe will probably say no".
        [Fact]
        public void An_explicit_false_forces_the_unprovisioned_store_without_probing()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CommunicationPlatform:TaskLinksProvisioned"] = "false",
                })
                .Build();

            using var provider = BuildContainer(configuration, extra: null);
            using var scope = provider.CreateScope();

            var store = scope.ServiceProvider.GetRequiredService<ICommTaskLinkStore>();

            Assert.IsType<UnprovisionedCommTaskLinkStore>(store);
            Assert.False(store.IsProvisioned);
        }

        // THE ACTIVATION MUST LIVE IN A FILE, NOT IN SOMEBODY'S SHELL.
        //
        // The two tests above prove the SWITCH works. They cannot prove anybody threw it, and on
        // 2026-10-27 nobody had: the flag existed only as a per-process environment variable passed at
        // launch, so a routine restart silently removed the task-link action from all nine Communication
        // Hub host screens at once — conversation-level and message-level together, with no hint
        // rendered, because canLink() gates both on LinkingAvailable. Nothing had regressed; the
        // activation had simply never been durable.
        //
        // So this pins the PAIRING the registration comment prescribes: the flag belongs beside the
        // connection that earns it.
        //
        // BOTH DIRECTIONS, because the failure modes are not symmetric and each has bitten:
        //   * https overrides the connection to the PROVISIONED database, so it must declare the flag —
        //     without it the feature is dark, which is the defect above.
        //   * http and IIS Express fall back to appsettings.json and CrossBuyDev, which has NO
        //     CommTaskLinks table, so they must NOT declare it — a blanket "true" there is a SQL-208
        //     from the driver in the middle of somebody's work, the HM-D44 shape.
        [Fact]
        public void The_https_launch_profile_declares_task_links_provisioned_and_the_others_do_not()
        {
            var root = RepoRoot();
            using var doc = System.Text.Json.JsonDocument.Parse(
                System.IO.File.ReadAllText(
                    System.IO.Path.Combine(root, "CrossBuy", "Properties", "launchSettings.json")));

            var profiles = doc.RootElement.GetProperty("profiles");

            static (string? Connection, string? Flag) Read(System.Text.Json.JsonElement profile)
            {
                if (!profile.TryGetProperty("environmentVariables", out var env))
                    return (null, null);
                string? Get(string key) => env.TryGetProperty(key, out var v) ? v.GetString() : null;
                return (Get("ConnectionStrings__DefaultConnection"),
                        Get("CommunicationPlatform__TaskLinksProvisioned"));
            }

            var https = Read(profiles.GetProperty("https"));

            // The profile that serves the Communication Hub dev target points at the provisioned
            // database AND says so. Asserting only the flag would pass against a profile that had
            // meanwhile been repointed at an unprovisioned database — which is the outage, not the fix.
            Assert.NotNull(https.Connection);
            Assert.Contains("CrossBuyDB2", https.Connection!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("true", https.Flag);

            // Every OTHER profile inherits appsettings.json, whose DefaultConnection is CrossBuyDev —
            // verified here rather than assumed, so this half cannot go vacuous if that default moves.
            var appsettings = System.IO.File.ReadAllText(
                System.IO.Path.Combine(root, "CrossBuy", "appsettings.json"));
            Assert.Contains("CrossBuyDev", appsettings, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TaskLinksProvisioned", appsettings, StringComparison.OrdinalIgnoreCase);

            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Name == "https") { continue; }

                var other = Read(profile.Value);
                if (other.Connection != null
                    && other.Connection.Contains("CrossBuyDB2", StringComparison.OrdinalIgnoreCase))
                {
                    // A second profile may legitimately be pointed at the provisioned database later;
                    // if it is, it owes the same declaration.
                    Assert.Equal("true", other.Flag);
                    continue;
                }

                Assert.True(other.Flag == null,
                    $"Profile '{profile.Name}' declares TaskLinksProvisioned but does not point at a " +
                    "database known to have CommTaskLinks. Activating the EF store there is a SQL-208 " +
                    "on first use.");
            }
        }

        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment)
                && System.IO.File.Exists(System.IO.Path.Combine(fromEnvironment, "CrossBuy.sln")))
            {
                return fromEnvironment;
            }

            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // Opting in is an EXPLICIT second call. This proves the override actually takes effect — a registration
        // that appended without winning would leave the null bridge in place and the opt-in would silently do
        // nothing.
        [Fact]
        public void UseBusinessEventBridge_swaps_in_the_platform_bridge()
        {
            using var provider = BuildContainer(services =>
            {
                services.AddScoped<IBusinessEventService, BusinessEventService>();
                services.AddScoped<IBusinessContextAccessor>(_ =>
                    new StubContextAccessor(CommunicationTestHost.Context()));
                services.UseBusinessEventBridge();
            });
            using var scope = provider.CreateScope();

            var bridge = scope.ServiceProvider.GetRequiredService<ICommBusinessEventBridge>();

            Assert.IsType<PlatformBusinessEventBridge>(bridge);
        }

        // Only the in-app channel has an adapter. Email, Push and WhatsApp are in the VOCABULARY but have no
        // implementation, which is why the dispatcher parks their rows as Skipped with a reason rather than
        // leaving them Pending forever.
        [Fact]
        public void Only_the_in_app_channel_is_registered()
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var channels = scope.ServiceProvider.GetServices<ICommNotificationChannel>().ToList();

            Assert.Single(channels);
            Assert.Equal(CommChannel.InApp, channels[0].Channel);
        }

        // The timeline is an aggregate of contributors, and the KERNEL is one of them. If the kernel source were
        // ever dropped from the registration, record timelines would silently lose every business event and look
        // like a comment stream — which is the failure ADR-036 is written to prevent.
        [Fact]
        public void Timeline_registers_the_kernel_source_alongside_the_platform_sources()
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var sources = scope.ServiceProvider.GetServices<ICommTimelineSource>().Select(s => s.Source).ToList();

            Assert.Contains(CommTimelineSourceKinds.BusinessEvent, sources);
            Assert.Contains(CommTimelineSourceKinds.Comment, sources);
            Assert.Contains(CommTimelineSourceKinds.Mention, sources);
            Assert.Contains(CommTimelineSourceKinds.CommAudit, sources);
        }

        // Every built-in principal kind, including @role, must be wired. Role expansion remains disabled by
        // default in configuration; registering the source makes the capability explicitly opt-in rather
        // than silently unsupported.
        [Fact]
        public void Principal_sources_cover_employee_team_department_and_role()
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var resolver = scope.ServiceProvider.GetRequiredService<ICommPrincipalResolver>();

            Assert.Contains(CommMentionTargetKind.Employee, resolver.SupportedKinds);
            Assert.Contains(CommMentionTargetKind.Team, resolver.SupportedKinds);
            Assert.Contains(CommMentionTargetKind.Department, resolver.SupportedKinds);
            Assert.Contains(CommMentionTargetKind.Role, resolver.SupportedKinds);
        }

        // A host with no configuration section must still resolve valid options. Without the explicit
        // Configure fallback in AddCommunicationPlatform, EnabledChannels would be empty and every notification
        // would report no_channel — a failure that reads as a platform bug rather than as missing config.
        [Fact]
        public void Options_resolve_with_sensible_defaults_when_no_configuration_is_supplied()
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var options = scope.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<CommunicationPlatformOptions>>().Value;

            Assert.Contains(CommChannel.InApp, options.EnabledChannels);
            Assert.False(options.BridgeToBusinessEvents);
            Assert.True(options.MaxBodyBytes > 0);
            Assert.True(options.MaxPageSize > 0);
        }
    }
}
