using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

            // ---- the platform under test: ONE call ----
            services.AddCommunicationPlatform(configuration: null!);

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

        // All three built-in principal kinds must be present. @role is deliberately absent — the vocabulary
        // declares it, and no source resolves it (ADR-032 §5). This test is what stops "we'll wire role mentions
        // later" from quietly becoming "role mentions resolve to nobody and nobody noticed".
        [Fact]
        public void Principal_sources_cover_employee_team_and_department_but_not_role()
        {
            using var provider = BuildContainer();
            using var scope = provider.CreateScope();

            var resolver = scope.ServiceProvider.GetRequiredService<ICommPrincipalResolver>();

            Assert.Contains(CommMentionTargetKind.Employee, resolver.SupportedKinds);
            Assert.Contains(CommMentionTargetKind.Team, resolver.SupportedKinds);
            Assert.Contains(CommMentionTargetKind.Department, resolver.SupportedKinds);
            Assert.DoesNotContain(CommMentionTargetKind.Role, resolver.SupportedKinds);
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
