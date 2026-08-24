using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // COMMUNICATION PLATFORM — ACTIVATION.
    //
    // The platform shipped complete and switched OFF: code, EF mapping and schema slice were all
    // committed, but nothing called AddCommunicationPlatform, so every consumer resolved null and
    // reported "not activated". Activating it is a DI change, and CLAUDE.md is blunt about what that
    // costs when it is only reasoned about rather than built:
    //
    //     "A DI graph is not verified by unit tests that construct services by hand. 112 green tests
    //      coexisted with an application that could not boot."
    //
    // So this file BUILDS the real container with ValidateOnBuild + ValidateScopes. Construction is
    // the assertion: an unsatisfiable constructor, or a singleton capturing a scoped service, throws
    // here rather than on the first real request.
    // ==========================================================================================
    public class CommunicationPlatformActivationTests
    {
        // The minimum the platform needs from outside itself: a DbContext (its entities are mapped by
        // CrossDbContext.OnModelCreating) and IEntityRegistry, which CommEntitySurface asks whether an
        // entity family may carry comments at all.
        private static ServiceCollection Baseline()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
            services.AddDbContext<CrossDbContext>(o => o.UseSqlite("DataSource=:memory:"));
            services.AddScoped<IEntityRegistry, EntityRegistry>();

            // The three platform services the Communication graph reaches into. They are NOT optional:
            // CommAccessPolicy needs IPlatformPermissionProvider, TeamCommPrincipalSource needs
            // IOrgHierarchy, and PlatformEventTimelineSource needs ITimelineProjectionService. All three
            // are already registered in Program.cs; omitting them here is what made the first version of
            // this test fail, and that failure was the TEST being under-specified, not the application
            // graph being broken. Listing them explicitly is the point: it records what activating
            // Communication actually depends on, so a future removal of any of them fails here.
            services.AddScoped<IOrgHierarchy, OrgHierarchy>();
            services.AddScoped<IPlatformPermissionProvider, PlatformPermissionProvider>();
            services.AddScoped<ITimelineProjectionService, TimelineProjectionService>();

            return services;
        }

        private static IConfiguration EmptyConfig() =>
            new ConfigurationBuilder().AddInMemoryCollection().Build();

        [Fact]
        public void The_activated_platform_graph_builds_with_ValidateOnBuild_and_ValidateScopes()
        {
            var services = Baseline();
            CommunicationPlatformRegistration.AddCommunicationPlatform(services, EmptyConfig());

            // Construction IS the assertion.
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            Assert.NotNull(provider);
        }

        [Fact]
        public void Every_public_platform_service_resolves_in_a_scope()
        {
            var services = Baseline();
            CommunicationPlatformRegistration.AddCommunicationPlatform(services, EmptyConfig());
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            Assert.NotNull(sp.GetRequiredService<ICommEntitySurface>());
            Assert.NotNull(sp.GetRequiredService<ICommThreadService>());
            Assert.NotNull(sp.GetRequiredService<ICommCommentService>());
            Assert.NotNull(sp.GetRequiredService<ICommMentionService>());
            Assert.NotNull(sp.GetRequiredService<ICommParticipationService>());
            Assert.NotNull(sp.GetRequiredService<ICommReactionService>());
            Assert.NotNull(sp.GetRequiredService<ICommNotificationService>());
            Assert.NotNull(sp.GetRequiredService<ICommTimelineAggregator>());
        }

        // THE ACTIVATION SAFETY PROPERTY. This increment was told not to start unrelated background
        // workers by accident, and a hosted service is exactly how that happens: the host starts every
        // IHostedService it can find, whether this feature needs it or not.
        [Fact]
        public void Activation_contributes_no_hosted_service_and_no_singleton()
        {
            var before = Baseline();
            int hostedBefore = before.Count(d => d.ServiceType == typeof(IHostedService));

            var after = Baseline();
            CommunicationPlatformRegistration.AddCommunicationPlatform(after, EmptyConfig());
            int hostedAfter = after.Count(d => d.ServiceType == typeof(IHostedService));

            Assert.Equal(hostedBefore, hostedAfter);

            // And nothing it adds is a singleton: a singleton here would capture the scoped DbContext.
            var added = after.Skip(before.Count).ToList();
            var singletons = added
                .Where(d => d.Lifetime == ServiceLifetime.Singleton
                            && d.ServiceType.Namespace?.StartsWith("CrossBuy", StringComparison.Ordinal) == true)
                .Select(d => d.ServiceType.Name)
                .ToList();

            Assert.True(singletons.Count == 0,
                "AddCommunicationPlatform introduced CrossBuy singleton(s): " + string.Join(", ", singletons));
        }

        // The business-event bridge stays OFF. PlatformBusinessEventBridge makes RecordAsync a hard
        // dependency of every comment, and CLAUDE.md records that coupling taking a whole screen down
        // when a kernel table is missing. Activating the platform must not silently opt into it.
        [Fact]
        public void Activation_leaves_the_business_event_bridge_disconnected()
        {
            var services = Baseline();
            CommunicationPlatformRegistration.AddCommunicationPlatform(services, EmptyConfig());
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var bridge = scope.ServiceProvider.GetRequiredService<ICommBusinessEventBridge>();

            Assert.Equal("NullCommBusinessEventBridge", bridge.GetType().Name);
        }

        // Program.cs must actually call it — the whole defect was that the code existed and nothing
        // registered it. Asserted on source with comments stripped, so a commented-out call cannot pass.
        [Fact]
        public void Program_activates_the_platform_and_not_the_bridge()
        {
            var program = StripLineComments(File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs")));

            Assert.Contains("AddCommunicationPlatform(", program, StringComparison.Ordinal);
            Assert.DoesNotContain("UseBusinessEventBridge", program, StringComparison.Ordinal);
        }

        private static string StripLineComments(string source) =>
            string.Join('\n', source.Split('\n').Select(line =>
            {
                var at = line.IndexOf("//", StringComparison.Ordinal);
                return at < 0 ? line : line[..at];
            }));

        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment)
                && File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
            {
                return fromEnvironment;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
