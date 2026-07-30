using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	// TM-9-ب: periodically runs the scheduled-task matcher (same pattern as TaskGeneratorHostedService). Operational only —
	// links scheduled tasks to EXISTING movements or records suggestions; touches NO GL/stock and creates no movement.
	public class TaskScheduleMatchHostedService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly ILogger<TaskScheduleMatchHostedService> _log;
		private const int CompanyId = 1;
		public TaskScheduleMatchHostedService(IServiceScopeFactory scopes, ILogger<TaskScheduleMatchHostedService> log) { _scopes = scopes; _log = log; }

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (TaskCanceledException) { return; }
			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
			do
			{
				try
				{
					using var scope = _scopes.CreateScope();
					var matcher = scope.ServiceProvider.GetRequiredService<ITaskScheduleMatcher>();
					var s = await matcher.RunAsync(CompanyId);
					if (s.AutoLinked > 0 || s.Suggested > 0)
						_log.LogInformation("Scheduled-task matcher: scanned={Scanned} autoLinked={AutoLinked} suggested={Suggested}", s.Scanned, s.AutoLinked, s.Suggested);
				}
				catch (Exception ex) { _log.LogError(ex, "Scheduled-task matcher failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}
	}
}
