using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	// CRM 3-4: dispatches activity reminders. Every few minutes it finds open activities whose ReminderAt has
	// passed and notifies the owner (in-app + SignalR via NotificationService), then marks them Reminded so they
	// fire once. Runs inside the same host — no separate process.
	public class CrmReminderHostedService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly ILogger<CrmReminderHostedService> _log;
		private const int CompanyId = 1;
		public CrmReminderHostedService(IServiceScopeFactory scopes, ILogger<CrmReminderHostedService> log) { _scopes = scopes; _log = log; }

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (TaskCanceledException) { return; }
			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
			do
			{
				try
				{
					using var scope = _scopes.CreateScope();
					var crm = scope.ServiceProvider.GetRequiredService<ICrmService>();
					var notify = scope.ServiceProvider.GetRequiredService<INotificationService>();
					var due = await crm.GetDueRemindersAsync(CompanyId, DateTime.UtcNow);
					if (due.Count == 0) continue;
					var fired = new List<int>();
					foreach (var a in due)
					{
						if (a.OwnerEmployeeId is int emp && emp > 0)
						{
							await notify.NotifyAsync(emp,
								"تذكير نشاط: " + a.Subject, "Activity reminder: " + a.Subject,
								a.Notes, a.Notes, "crm-reminder", a.ID);
						}
						fired.Add(a.ID);   // mark fired even if unowned, so it doesn't re-scan forever
					}
					await crm.MarkRemindedAsync(CompanyId, fired);
					_log.LogInformation("CRM reminders dispatched: {Count}", fired.Count);
				}
				catch (Exception ex) { _log.LogError(ex, "CRM reminder dispatch failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}
	}
}
