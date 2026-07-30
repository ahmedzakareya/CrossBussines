using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	// Daily integrity reconciliation — runs inside the same host (no separate process).
	// Logs each run and notifies inventory managers when a deviation is detected.
	public class IntegrityCheckHostedService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly ILogger<IntegrityCheckHostedService> _log;
		private const int CompanyId = 1;
		public IntegrityCheckHostedService(IServiceScopeFactory scopes, ILogger<IntegrityCheckHostedService> log) { _scopes = scopes; _log = log; }

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			// small startup delay so the app is fully up before the first run
			try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (TaskCanceledException) { return; }
			using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
			do
			{
				try
				{
					using var scope = _scopes.CreateScope();
					var svc = scope.ServiceProvider.GetRequiredService<IIntegrityCheckService>();
					var (run, _) = await svc.RunAndLogAsync(CompanyId, "Scheduled");
					_log.LogInformation("Integrity check run #{Id}: {Status} ({Failed} failed)", run.ID, run.AllOk ? "OK" : "DEVIATION", run.FailedCount);
				}
				catch (Exception ex) { _log.LogError(ex, "Scheduled integrity check failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}
	}
}
