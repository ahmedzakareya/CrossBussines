using System.Reflection;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests.Communication;

public sealed class CommNotificationHostedServiceTests
{
    private sealed class Gate(bool allowed) : IWorkerGate
    {
        public Task<bool> WaitUntilAllowedAsync(string workerName, CancellationToken token) => Task.FromResult(allowed);
    }
    private sealed class Recorder
    {
        public List<(int Company, ICompanyScopeHolder Holder)> Calls { get; } = new();
    }
    private sealed class Dispatcher(ICompanyScopeHolder holder, Recorder recorder) : ICommNotificationDispatcher
    {
        public Task<CommDispatchReport> DispatchPendingAsync(int? companyId = null, int? batchSize = null, CancellationToken cancellationToken = default)
        {
            Assert.True(holder.IsResolved);
            Assert.Equal(companyId, holder.CompanyId);
            Assert.NotNull(companyId);
            recorder.Calls.Add((companyId!.Value, holder));
            return Task.FromResult(CommDispatchReport.Nothing("test"));
        }
    }
    private static Task Pass(CommNotificationHostedService service) =>
        (Task)typeof(CommNotificationHostedService).GetMethod("RunPassAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, new object[] { CancellationToken.None })!;

    [Fact]
    public void Worker_resolves_as_one_singleton_without_capturing_scoped_services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<CommunicationPlatformOptions>();
        services.AddSingleton<IWorkerGate>(new Gate(true));
        services.AddHostedService<CommNotificationHostedService>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var first = Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
        var second = Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
        Assert.IsType<CommNotificationHostedService>(first);
        Assert.Same(first, second);
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 2)]
    public async Task Worker_honors_activation_and_gate_and_binds_distinct_company_scopes(bool enabled, bool allowed, int expected)
    {
        using var host = new CommunicationTestHost();
        var recorder = new Recorder();
        var services = new ServiceCollection();
        services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
        services.AddScoped<CrossDbContext>(sp => host.NewContext(sp.GetRequiredService<ICompanyScopeHolder>()));
        services.AddScoped<IBusinessContextFactory>(sp => new BusinessContextFactory(new HttpContextAccessor(),
            sp.GetRequiredService<CrossDbContext>(), sp.GetRequiredService<ICompanyScopeHolder>(), NullLogger<BusinessContextFactory>.Instance));
        services.AddSingleton(recorder);
        services.AddScoped<ICommNotificationDispatcher, Dispatcher>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var options = new CommunicationPlatformOptions { NotificationWorkerEnabled = enabled, NotificationWorkerCompanyIds = new() { 2, 1, 1 } };
        using var worker = new CommNotificationHostedService(provider.GetRequiredService<IServiceScopeFactory>(),
            new Gate(allowed), Options.Create(options), NullLogger<CommNotificationHostedService>.Instance);
        await Pass(worker);
        Assert.Equal(expected, recorder.Calls.Count);
        if (expected > 0)
        {
            Assert.Equal(new[] { 1, 2 }, recorder.Calls.Select(c => c.Company));
            Assert.NotSame(recorder.Calls[0].Holder, recorder.Calls[1].Holder);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_company_is_refused_before_claiming(int company)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var worker = new CommNotificationHostedService(provider.GetRequiredService<IServiceScopeFactory>(), new Gate(true),
            Options.Create(new CommunicationPlatformOptions { NotificationWorkerEnabled = true, NotificationWorkerCompanyIds = new() { company } }),
            NullLogger<CommNotificationHostedService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Pass(worker));
    }
}
