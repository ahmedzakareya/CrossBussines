using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	// TM-7: periodically runs the auto-task rules (same pattern as CrmReminderHostedService / IntegrityCheckHostedService).
	// Operational only — creates tasks via TaskGeneratorService; touches NO GL/stock. Dedupe is inside the service.
	public class TaskGeneratorHostedService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly ILogger<TaskGeneratorHostedService> _log;
		private const int CompanyId = 1;
		public TaskGeneratorHostedService(IServiceScopeFactory scopes, ILogger<TaskGeneratorHostedService> log) { _scopes = scopes; _log = log; }

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (TaskCanceledException) { return; }
			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
			do
			{
				try
				{
					using var scope = _scopes.CreateScope();
					var gen = scope.ServiceProvider.GetRequiredService<ITaskGeneratorService>();
					var summary = await gen.RunAsync(CompanyId);
					int total = summary.Values.Sum();
					if (total > 0) _log.LogInformation("Auto-tasks generated: {Total} ({Detail})", total, string.Join(", ", summary.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")));
				}
				catch (Exception ex) { _log.LogError(ex, "Auto-task generation failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}
	}
}
