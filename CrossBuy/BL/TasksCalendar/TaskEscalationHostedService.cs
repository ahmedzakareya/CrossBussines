using CrossBuy.BL.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.TasksCalendar
{
	// The driver for overdue manager escalation.
	//
	// WHY A DEDICATED WORKER RATHER THAN A LINE IN TaskGeneratorHostedService. That service is the natural
	// neighbour, but it is a legacy-tree file with uncommitted changes owned elsewhere, and editing it would
	// mean carrying somebody else's work-in-progress into this increment. A separate BackgroundService in the
	// Tasks tree follows the identical established pattern and touches nothing that is mid-flight.
	//
	// EVERY SAFETY PROPERTY IS BORROWED, NOT REINVENTED:
	//   * IWorkerGate — escalation must not run in two processes at once, or a manager is told twice about the
	//     same missed date before either notification has committed. A standby waits here and does not sweep.
	//   * WorkerCompanyRunner — one company's failure is logged with its id and does not stop the others, and
	//     an empty company list is a loud warning rather than a silent no-op. There is NO fallback to company 1.
	//   * WorkerScope.ForCompany — binds the scope's company so the query filters read a resolved company, and
	//     binds the BusinessContext so the escalation event can actually be attributed.
	//
	// NOT AGGRESSIVE. Escalation is a once-per-missed-due-date fact with a grace period measured in hours, so
	// polling faster than hourly cannot discover anything new; it would only re-run the same idempotent
	// queries. The first pass is deliberately late (5 minutes) so a restarting host finishes its own startup
	// before a sweep competes for the database.
	//
	// CERTIFICATION. The worker suppresses ITSELF via CertificationRuntimeState.BackgroundWritersSuppressed —
	// the flag that exists for exactly this question — rather than being left out of the DI graph. Two reasons,
	// and the second is the load-bearing one:
	//   * a conformance capture must OBSERVE stable data, and a worker writing notifications and business
	//     events underneath a screenshot makes two runs of the same page disagree;
	//   * gating the REGISTRATION would have added a second `if (!certificationRuntime)` around an
	//     AddHostedService, and Program.cs deliberately carries exactly one. That invariant is asserted by
	//     BusinessEventDispatchWorkerCompositionTests.Only_the_dispatcher_is_newly_gated, so re-gating in
	//     Program.cs would have silently broken a platform test to buy nothing: suppression here is stronger,
	//     because it holds however the service is composed.
	public sealed class TaskEscalationHostedService : BackgroundService
	{
		private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
		private static readonly TimeSpan Period = TimeSpan.FromHours(1);

		private readonly IServiceScopeFactory _scopes;
		private readonly IWorkerGate _gate;
		private readonly CertificationRuntimeState _certification;
		private readonly ILogger<TaskEscalationHostedService> _log;

		public TaskEscalationHostedService(IServiceScopeFactory scopes, IWorkerGate gate,
			CertificationRuntimeState certification, ILogger<TaskEscalationHostedService> log)
		{
			_scopes = scopes; _gate = gate; _certification = certification; _log = log;
		}

		/// Exposed so the suppression is assertable without starting a host. Read-only and side-effect free —
		/// there is no InternalsVisibleTo in this repository, so a test cannot see an internal member.
		public bool Suppressed => _certification.BackgroundWritersSuppressed;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (_certification.BackgroundWritersSuppressed)
			{
				_log.LogInformation(
					"Task escalation is suppressed: this host is a certification runtime, and a conformance " +
					"capture must observe stable data. No escalation is swept, notified or recorded.");
				return;
			}

			try { await Task.Delay(StartupDelay, stoppingToken); } catch (TaskCanceledException) { return; }

			// Only the worker-primary instance escalates; a standby blocks here rather than duplicating.
			if (!await _gate.WaitUntilAllowedAsync(nameof(TaskEscalationHostedService), stoppingToken)) return;

			using var timer = new PeriodicTimer(Period);
			do
			{
				try
				{
					using var root = _scopes.CreateScope();
					var companyScope = root.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();

					await WorkerCompanyRunner.ForEachCompanyAsync(companyScope, _log, nameof(TaskEscalationHostedService),
						async companyId =>
						{
							using var scope = WorkerScope.ForCompany(_scopes, companyId);
							var escalation = scope.ServiceProvider.GetRequiredService<ITaskEscalationService>();
							var result = await escalation.SweepAsync(companyId, stoppingToken);

							if (result.Escalated > 0 || result.NoManager > 0)
								_log.LogInformation(
									"Task escalation for company {CompanyId}: {Escalated} escalated to a direct manager, " +
									"{Already} already escalated, {NoManager} overdue with NO resolvable manager, " +
									"{Examined} examined, {Recorded} business events recorded",
									companyId, result.Escalated, result.AlreadyEscalated, result.NoManager,
									result.Examined, result.EventsRecorded);

							// The manager was told but the business fact was not durably recorded. Surfaced at
							// Warning because the bell and the Business Event Monitor now disagree about the same
							// escalation, which is precisely the class of defect the overdue sweep was fixed for.
							if (result.EventsSkippedNoContext > 0)
								_log.LogWarning(
									"Task escalation for company {CompanyId}: {Skipped} of {Examined} escalations were " +
									"notified WITHOUT a Task.Escalated business event — no BusinessContext resolved in " +
									"the worker scope.",
									companyId, result.EventsSkippedNoContext, result.Examined);
						},
						stoppingToken);
				}
				catch (OperationCanceledException) { return; }
				catch (Exception ex)
				{
					// One bad tick must not end the worker for the lifetime of the process.
					_log.LogError(ex, "Task escalation sweep tick failed.");
				}
			}
			while (await SafeWaitAsync(timer, stoppingToken));
		}

		private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
		{
			try { return await timer.WaitForNextTickAsync(ct); }
			catch (OperationCanceledException) { return false; }
		}
	}
}
