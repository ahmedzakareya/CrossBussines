using CrossBuy.BL.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CrossBuy.Models.Communication;

namespace CrossBuy.BL.Communication;

public sealed class CommNotificationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IWorkerGate _gate;
    private readonly CommunicationPlatformOptions _options;
    private readonly ILogger<CommNotificationHostedService> _log;

    public CommNotificationHostedService(IServiceScopeFactory scopes, IWorkerGate gate,
        IOptions<CommunicationPlatformOptions> options, ILogger<CommNotificationHostedService> log)
    { _scopes = scopes; _gate = gate; _options = options.Value; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.NotificationWorkerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.NotificationPollSeconds, 1, 300)));
        do
        {
            try { await RunPassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogError(ex, "Communication notification pass failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        if (!_options.NotificationWorkerEnabled) return;
        if (_options.NotificationWorkerCompanyIds.Count == 0 || _options.NotificationWorkerCompanyIds.Any(id => id <= 0))
            throw new InvalidOperationException("An explicit positive company allow-list is required for notification activation.");
        if (!await _gate.WaitUntilAllowedAsync(nameof(CommNotificationHostedService), cancellationToken)) return;
        foreach (var company in _options.NotificationWorkerCompanyIds.Distinct().OrderBy(id => id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var scope = WorkerScope.ForCompany(_scopes, company);
                var dispatcher = scope.ServiceProvider.GetRequiredService<ICommNotificationDispatcher>();
                await dispatcher.DispatchPendingAsync(company, Math.Clamp(_options.DispatchBatchSize, 1, 1000), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogError(ex, "Communication notifications failed for company {CompanyId}", company); }
        }
    }
}
