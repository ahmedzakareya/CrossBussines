using CrossBuy.BL.Platform;
using Microsoft.Extensions.Hosting;

namespace CrossBuy.BL.Communication
{
	// ==========================================================================================
	// COMMUNICATION v2 — the background work of the chat, every minute, per company:
	//   * scheduled messages whose time has come are sent (as their author, through ChatPlusService.PostAsync);
	//   * reminders that are due become a notice to the person who set them;
	//   * once an hour, the company's message retention removes the content of messages older than its days
	//     (legal holds excepted; 0 days = kept).
	// The same shape as CrmReminderHostedService: a singleton that makes a scope per company (WorkerScope.ForCompany),
	// one primary through IWorkerGate (re-checked every pass, D-16), and silent on a certification host.
	// ==========================================================================================
	public sealed class CommWorkerHostedService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly IWorkerGate _gate;
		private readonly CertificationRuntimeState _certification;
		private readonly ILogger<CommWorkerHostedService> _log;
		private DateTime _lastRetention = DateTime.MinValue;

		public CommWorkerHostedService(IServiceScopeFactory scopes, IWorkerGate gate, CertificationRuntimeState certification, ILogger<CommWorkerHostedService> log)
		{ _scopes = scopes; _gate = gate; _certification = certification; _log = log; }

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (_certification.BackgroundWritersSuppressed) { _log.LogInformation("CommWorkerHostedService is suppressed on a certification runtime."); return; }
			try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); } catch (TaskCanceledException) { return; }
			if (!await _gate.WaitUntilAllowedAsync(nameof(CommWorkerHostedService), stoppingToken)) return;

			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
			do
			{
				// PARK, NEVER RETURN (D-16): a lost lease waits to be the primary again; only cancellation ends the worker.
				if (!await _gate.IsStillAllowedAsync(nameof(CommWorkerHostedService), stoppingToken))
				{
					if (!await _gate.WaitUntilAllowedAsync(nameof(CommWorkerHostedService), stoppingToken)) return;
				}
				bool retention = DateTime.UtcNow - _lastRetention > TimeSpan.FromHours(1);
				try
				{
					using var scopeRoot = _scopes.CreateScope();
					var companyScope = scopeRoot.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();
					await WorkerCompanyRunner.ForEachCompanyAsync(companyScope, _log, nameof(CommWorkerHostedService),
						async companyId => await RunForCompanyAsync(companyId, retention, stoppingToken), stoppingToken);
					if (retention) _lastRetention = DateTime.UtcNow;
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
				catch (Exception ex) { _log.LogError(ex, "CommWorkerHostedService pass failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}

		private async Task RunForCompanyAsync(int companyId, bool retention, CancellationToken ct)
		{
			using var scope = WorkerScope.ForCompany(_scopes, companyId);
			var plus = ActivatorUtilities.CreateInstance<ChatPlusService>(scope.ServiceProvider);
			var now = DateTime.UtcNow;
			try { int n = await plus.SendDueAsync(companyId, now); if (n > 0) _log.LogInformation("Chat: {Count} scheduled messages sent for company {CompanyId}", n, companyId); }
			catch (Microsoft.Data.SqlClient.SqlException) { return; }   // comm_messaging_v2.sql not applied here
			catch (Exception ex) { _log.LogError(ex, "Chat scheduled messages failed for company {CompanyId}", companyId); }
			try { await plus.NotifyRemindersAsync(companyId, now); }
			catch (Exception ex) { _log.LogError(ex, "Chat reminders failed for company {CompanyId}", companyId); }
			if (retention)
			{
				try { int n = await plus.ApplyRetentionAsync(companyId, now); if (n > 0) _log.LogInformation("Chat retention removed the content of {Count} messages for company {CompanyId}", n, companyId); }
				catch (Exception ex) { _log.LogError(ex, "Chat retention failed for company {CompanyId}", companyId); }
			}
		}
	}
}
