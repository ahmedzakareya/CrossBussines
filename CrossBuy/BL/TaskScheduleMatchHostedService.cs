using CrossBuy.BL.Platform;
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
		private readonly CrossBuy.BL.Platform.IWorkerGate _gate;
		private readonly CrossBuy.BL.Platform.CertificationRuntimeState _certification;
		private readonly ILogger<TaskScheduleMatchHostedService> _log;
		public TaskScheduleMatchHostedService(IServiceScopeFactory scopes, CrossBuy.BL.Platform.IWorkerGate gate,
			CrossBuy.BL.Platform.CertificationRuntimeState certification, ILogger<TaskScheduleMatchHostedService> log)
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
					"TaskScheduleMatchHostedService is suppressed: this host is a certification runtime, so no background " +
					"writes are performed.");
				return;
			}

			try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (TaskCanceledException) { return; }

			// Stage 0 Batch B: this worker is NOT safe to run in two processes at once — it would duplicate the
			// records it creates. Only the worker-primary instance proceeds; a standby waits here.
			if (!await _gate.WaitUntilAllowedAsync(nameof(TaskScheduleMatchHostedService), stoppingToken)) return;
			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
			do
			{
				// D-16. THE LEASE CAN BE LOST MID-TICK — a dropped connection, a failover, another
				// process taking it. The gate above is asked once at startup, so without this a worker
				// that lost primacy would keep running beside the new primary and duplicate its output.
				//
				// PARK, NEVER RETURN. False means "not right now" and is recoverable; returning would end
				// this BackgroundService for good, so if the process holding the lease later died nothing
				// would reacquire it. Only cancellation ends the loop, which is the false below.
				if (!await _gate.IsStillAllowedAsync(nameof(TaskScheduleMatchHostedService), stoppingToken))
				{
					if (!await _gate.WaitUntilAllowedAsync(nameof(TaskScheduleMatchHostedService), stoppingToken)) return;
				}

				try
				{
					using var scopeRoot = _scopes.CreateScope();
					var companyScope = scopeRoot.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();
					await WorkerCompanyRunner.ForEachCompanyAsync(companyScope, _log, nameof(TaskScheduleMatchHostedService),
						async companyId =>
						{
							// B2: the scope is BOUND to this company, so the query filters read a resolved company instead
								// of nothing. See WorkerScope.ForCompany.
								using var scope = WorkerScope.ForCompany(_scopes, companyId);
							var matcher = scope.ServiceProvider.GetRequiredService<ITaskScheduleMatcher>();
							var s = await matcher.RunAsync(companyId);
							if (s.AutoLinked > 0 || s.Suggested > 0)
								_log.LogInformation("Scheduled-task matcher company {CompanyId}: scanned={Scanned} autoLinked={AutoLinked} suggested={Suggested}",
									companyId, s.Scanned, s.AutoLinked, s.Suggested);
						}, stoppingToken);
				}
				catch (Exception ex) { _log.LogError(ex, "Scheduled-task matcher failed"); }
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}
	}
}
