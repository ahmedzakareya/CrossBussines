using CrossBuy.BL.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	// ==============================================================================================
	// SCHEDULED INTEGRITY CHECK — one run per company, per day.
	//
	// WHAT WAS WRONG. This worker carried `private const int CompanyId = 1` and passed it straight to
	// RunAndLogAsync. IntegrityCheckRun has a CompanyID column and every check inside the service
	// filters on the company it is given, so the worker was not "global": it was a company-scoped
	// WRITER pinned to one company. On any install with more than one company, company 1 was checked
	// daily and every other company was never checked at all — silently, because the run it produced
	// looked perfectly healthy.
	//
	// It also had no gate. A second application instance ran the same daily check concurrently and
	// wrote a second IntegrityCheckRun row for the same company and day.
	//
	// This is now the same shape TaskGeneratorHostedService uses. No new configuration system was
	// introduced: certification suppression, IWorkerGate, IWorkerCompanyScope, WorkerCompanyRunner and
	// WorkerScope all already existed.
	// ==============================================================================================
	public class IntegrityCheckHostedService : BackgroundService
	{
		private readonly IServiceScopeFactory _scopes;
		private readonly IWorkerGate _gate;
		private readonly CertificationRuntimeState _certification;
		private readonly ILogger<IntegrityCheckHostedService> _log;

		public IntegrityCheckHostedService(
			IServiceScopeFactory scopes, IWorkerGate gate,
			CertificationRuntimeState certification, ILogger<IntegrityCheckHostedService> log)
		{ _scopes = scopes; _gate = gate; _certification = certification; _log = log; }

		public bool Suppressed => _certification.BackgroundWritersSuppressed;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			// A conformance capture must OBSERVE stable data rather than write to it: an integrity run
			// is a row, and two captures of the same screen would otherwise disagree.
			if (_certification.BackgroundWritersSuppressed)
			{
				_log.LogInformation(
					"IntegrityCheckHostedService is suppressed: this host is a certification runtime, so no " +
					"integrity runs are recorded.");
				return;
			}

			try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (TaskCanceledException) { return; }

			// One process performs the daily check. A second instance waits rather than duplicating the run.
			if (!await _gate.WaitUntilAllowedAsync(nameof(IntegrityCheckHostedService), stoppingToken)) return;

			using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
			do
			{
				try
				{
					using var scopeRoot = _scopes.CreateScope();
					var companyScope = scopeRoot.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();

					// EVERY company, each in its own scope. The runner logs and continues when one company
					// throws, so a broken chart of accounts in one tenant cannot stop the others being
					// checked — and it never falls back to company 1 when the list is empty.
					await WorkerCompanyRunner.ForEachCompanyAsync(
						companyScope, _log, nameof(IntegrityCheckHostedService),
						async companyId =>
						{
							using var scope = WorkerScope.ForCompany(_scopes, companyId);
							var svc = scope.ServiceProvider.GetRequiredService<IIntegrityCheckService>();

							var (run, _) = await svc.RunAndLogAsync(companyId, "Scheduled");
							_log.LogInformation(
								"Integrity check run #{Id} for company {CompanyId}: {Status} ({Failed} failed)",
								run.ID, companyId, run.AllOk ? "OK" : "DEVIATION", run.FailedCount);
						},
						stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					return;   // shutdown, not a failure
				}
				catch (Exception ex) { _log.LogError(ex, "Scheduled integrity check failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}
	}
}
