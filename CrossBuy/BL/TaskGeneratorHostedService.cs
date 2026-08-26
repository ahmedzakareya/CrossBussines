using CrossBuy.BL.Platform;
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
		private readonly CrossBuy.BL.Platform.IWorkerGate _gate;
		private readonly CrossBuy.BL.Platform.CertificationRuntimeState _certification;
		private readonly ILogger<TaskGeneratorHostedService> _log;
		public TaskGeneratorHostedService(IServiceScopeFactory scopes, CrossBuy.BL.Platform.IWorkerGate gate,
			CrossBuy.BL.Platform.CertificationRuntimeState certification, ILogger<TaskGeneratorHostedService> log)
			{ _scopes = scopes; _gate = gate; _certification = certification; _log = log; }

		/// Exposed so the suppression is assertable without starting a host. Read-only, side-effect free.
		public bool Suppressed => _certification.BackgroundWritersSuppressed;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			// CERTIFICATION. This worker WRITES (it creates and links task rows), and a conformance capture
			// must observe stable data or two runs of the same page disagree. Suppression lives HERE rather
			// than as a second `if (!certificationRuntime)` around an AddHostedService, because Program.cs
			// deliberately carries exactly one of those (the event dispatcher) and
			// BusinessEventDispatchWorkerCompositionTests asserts it. Suppressing in the worker is also
			// stronger: it holds however the service is composed.
			if (_certification.BackgroundWritersSuppressed)
			{
				_log.LogInformation(
					"TaskGeneratorHostedService is suppressed: this host is a certification runtime, so no background " +
					"writes are performed.");
				return;
			}

			try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (TaskCanceledException) { return; }

			// Stage 0 Batch B: this worker is NOT safe to run in two processes at once — it would duplicate the
			// records it creates. Only the worker-primary instance proceeds; a standby waits here.
			if (!await _gate.WaitUntilAllowedAsync(nameof(TaskGeneratorHostedService), stoppingToken)) return;
			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
			do
			{
				try
				{
					using var scopeRoot = _scopes.CreateScope();
					var companyScope = scopeRoot.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();
					await WorkerCompanyRunner.ForEachCompanyAsync(companyScope, _log, nameof(TaskGeneratorHostedService),
						async companyId =>
						{
							// B2: the scope is BOUND to this company, so the query filters read a resolved company instead
								// of nothing. See WorkerScope.ForCompany.
								using var scope = WorkerScope.ForCompany(_scopes, companyId);
							var gen = scope.ServiceProvider.GetRequiredService<ITaskGeneratorService>();
							var summary = await gen.RunAsync(companyId);
							int total = summary.Values.Sum();
							if (total > 0)
								_log.LogInformation("Auto-tasks generated for company {CompanyId}: {Total} ({Detail})", companyId, total,
									string.Join(", ", summary.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")));

							// Tasks & Calendar integration (TAB 4) — OVERDUE DETECTION LIVES HERE, and only here.
							//
							// This worker is the single owner: it is already the per-company, gated, timer-driven
							// task-side worker, so overdue detection needs no third timer competing over TaskItems.
							// The sweep is idempotent (its dedup key comes from the task's DUE DATE, not from this
							// tick), so a 15-minute cadence notifies ONCE per missed date.
							//
							// It runs in its OWN try/catch on purpose: a notification failure must never stop TM-7
							// rule generation, which is this worker's primary job.
							try
							{
								var overdue = scope.ServiceProvider
									.GetRequiredService<CrossBuy.BL.TasksCalendar.ITaskOverdueSweepService>();
								var sweep = await overdue.SweepAsync(companyId, stoppingToken);
								if (sweep.Notified > 0)
									_log.LogInformation(
										"Overdue task notifications for company {CompanyId}: {Notified} sent, {Already} already notified, {Examined} examined, {Recorded} business events recorded",
										companyId, sweep.Notified, sweep.AlreadyNotified, sweep.Examined, sweep.EventsRecorded);

								// THE GAP, MADE VISIBLE IN OPERATION. A non-zero count means those tasks were
								// notified but produced NO Task.BecameOverdue event — the bell and the Business
								// Event Monitor still disagree for them. It happens because RecordAsync attributes
								// an event through IBusinessContextAccessor, which resolves only from an HTTP
								// request, and this worker has none (HM-D58). Logged as a WARNING rather than
								// counted silently: a reconciliation gap nobody can see is the defect that was
								// just closed, wearing different clothes.
								if (sweep.EventsSkippedNoContext > 0)
									_log.LogWarning(
										"Overdue sweep for company {CompanyId}: {Skipped} of {Examined} overdue tasks were notified WITHOUT a Task.BecameOverdue business event — no BusinessContext resolves in the worker scope (HM-D58).",
										companyId, sweep.EventsSkippedNoContext, sweep.Examined);
							}
							catch (Exception overdueEx)
							{
								_log.LogError(overdueEx, "Overdue task sweep failed for company {CompanyId}", companyId);
							}
						}, stoppingToken);
				}
				catch (Exception ex) { _log.LogError(ex, "Auto-task generation failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}
	}
}
