using CrossBuy.BL.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	// ==============================================================================================
	// CRM ACTIVITY REMINDERS — every five minutes, per company.
	//
	// WHAT WAS WRONG. `private const int CompanyId = 1` was passed to both GetDueRemindersAsync and
	// MarkRemindedAsync, so only company 1's activities were ever scanned. Every other company's sales
	// team simply never received a reminder, and nothing reported it: the worker logged a cheerful
	// "CRM reminders dispatched: 0" forever.
	//
	// It also had no gate, so a second instance dispatched the SAME reminders again — and since the
	// notification carries a dedup key only within its own recipient, that is a duplicate ping to a
	// real person.
	//
	// NOT A LEAK, AND WORTH BEING PRECISE ABOUT IT: the hardcoded 1 was too NARROW, never too wide. It
	// could not read another company's records because every query was pinned to company 1. The defect
	// is omission, and the fix is to process each company explicitly rather than to widen anything.
	//
	// Notification company is unchanged and deliberately not touched here: NotificationService derives
	// CompanyID from the RECIPIENT's EmpCompanyID, not from ambient context, so a reminder is stamped
	// with the company of the employee it reaches.
	// ==============================================================================================
	public class CrmReminderHostedService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly IWorkerGate _gate;
		private readonly CertificationRuntimeState _certification;
		private readonly ILogger<CrmReminderHostedService> _log;

		public CrmReminderHostedService(
			IServiceScopeFactory scopes, IWorkerGate gate,
			CertificationRuntimeState certification, ILogger<CrmReminderHostedService> log)
		{ _scopes = scopes; _gate = gate; _certification = certification; _log = log; }

		public bool Suppressed => _certification.BackgroundWritersSuppressed;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			// Reminders mutate state twice — a notification row and the activity's reminded flag — so a
			// certification host must not run them.
			if (_certification.BackgroundWritersSuppressed)
			{
				_log.LogInformation(
					"CrmReminderHostedService is suppressed: this host is a certification runtime, so no " +
					"reminders are dispatched.");
				return;
			}

			try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (TaskCanceledException) { return; }

			// One dispatcher. A second instance would send the same person the same reminder twice.
			if (!await _gate.WaitUntilAllowedAsync(nameof(CrmReminderHostedService), stoppingToken)) return;

			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
			do
			{
				try
				{
					using var scopeRoot = _scopes.CreateScope();
					var companyScope = scopeRoot.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();

					await WorkerCompanyRunner.ForEachCompanyAsync(
						companyScope, _log, nameof(CrmReminderHostedService),
						async companyId => await DispatchForCompanyAsync(companyId, stoppingToken),
						stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex) { _log.LogError(ex, "CRM reminder dispatch failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}

		// One company, one scope, one BusinessContext. The scope is created per company rather than per
		// tick so that a company whose dispatch throws cannot leave a half-resolved context behind for
		// the next company in the loop — the runner catches it and the scope is disposed with it.
		private async Task DispatchForCompanyAsync(int companyId, CancellationToken cancellationToken)
		{
			using var scope = WorkerScope.ForCompany(_scopes, companyId);
			var crm = scope.ServiceProvider.GetRequiredService<ICrmService>();
			var notify = scope.ServiceProvider.GetRequiredService<INotificationService>();

			var due = await crm.GetDueRemindersAsync(companyId, DateTime.UtcNow);
			if (due.Count == 0) return;

			var fired = new List<int>();
			foreach (var a in due)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (a.OwnerEmployeeId is int emp && emp > 0)
				{
					await notify.NotifyAsync(emp,
						"تذكير نشاط: " + a.Subject, "Activity reminder: " + a.Subject,
						a.Notes, a.Notes, "crm-reminder", a.ID);
				}

				fired.Add(a.ID);   // mark fired even if unowned, so it doesn't re-scan forever
			}

			await crm.MarkRemindedAsync(companyId, fired);
			_log.LogInformation("CRM reminders dispatched for company {CompanyId}: {Count}", companyId, fired.Count);
		}
	}
}
