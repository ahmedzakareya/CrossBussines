using CrossBuy.BL.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE SCHEDULE WORKER — the one piece the scheduling slice deliberately did not ship.
    //
    // ReportScheduleService.cs records why it was deferred, in three reasons. Two are satisfied by how
    // this class is written; the third is satisfied by it being OFF unless someone turns it on.
    //
    //   1. "ADR-013 constrains this deployment to a single worker process. Adding a second background
    //      loop is a platform decision with an owner."
    //      -> It takes IWorkerGate, the same gate every other worker takes, so this is not a second
    //         independent loop competing for the role - it is one more participant in the arrangement
    //         that already decides which process runs background work.
    //
    //   2. "A hosted service is a SINGLETON and may never inject a scoped service. The eventual worker
    //      must take IServiceScopeFactory and create a scope per due schedule."
    //      -> It takes IServiceScopeFactory and nothing scoped. Every resolve below happens inside a
    //         scope this class created, which is also what lets the DI graph validate with
    //         ValidateScopes.
    //
    //   3. "Turning on an unattended process that generates and emails documents before the permission
    //      and delivery layers have been reviewed would be exactly the wrong order."
    //      -> THIS IS WHY IT IS DISABLED BY DEFAULT. ReportScheduleWorkerOptions.Enabled is false, and
    //         nothing in the platform sets it. The code is complete, reviewable and testable; firing it
    //         stays one explicit decision by whoever owns that review, taken in configuration rather
    //         than inherited from a merge.
    //
    // A DISABLED WORKER SAYS SO, ONCE, AT STARTUP. Silence would be indistinguishable from a worker
    // that is running and finding nothing due - and "the schedules are not firing" is the single
    // question this feature will generate.
    // ============================================================================================
    public sealed class ReportScheduleWorkerOptions
    {
        /// OFF by default, and deliberately. See reason 3 above.
        public bool Enabled { get; set; }

        /// How often the worker looks for due schedules. A schedule's own next-run time decides when it
        /// fires; this only bounds how late that can be.
        public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// Quiet period after startup, so a restart does not race the application's own warm-up.
        public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(1);
    }

    public sealed class ReportScheduleHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IWorkerGate _gate;
        private readonly CertificationRuntimeState _certification;
        private readonly ReportScheduleWorkerOptions _options;
        private readonly ILogger<ReportScheduleHostedService> _log;

        public ReportScheduleHostedService(
            IServiceScopeFactory scopes, IWorkerGate gate, CertificationRuntimeState certification,
            ReportScheduleWorkerOptions options, ILogger<ReportScheduleHostedService> log)
        {
            _scopes = scopes;
            _gate = gate;
            _certification = certification;
            _options = options;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _log.LogInformation(
                    "Report schedules are NOT being dispatched: the schedule worker is disabled " +
                    "(Reporting:ScheduleWorker:Enabled is false). Saved schedules will not fire.");
                return;
            }

            // A scheduled report GENERATES a run row, an archive entry and a delivery attempt. A
            // certification host must not write any of them, for the reason every other worker states.
            if (_certification.BackgroundWritersSuppressed)
            {
                _log.LogInformation(
                    "ReportScheduleHostedService is suppressed: this host is a certification runtime.");
                return;
            }

            try { await Task.Delay(_options.StartupDelay, stoppingToken); }
            catch (TaskCanceledException) { return; }

            // ONE DISPATCHER. Two processes sweeping the same due schedule would deliver the same
            // document to the same recipient twice, and the schedule's NextRunAt is advanced by the
            // runner rather than claimed under a lock - so the gate is what prevents the duplicate.
            if (!await _gate.WaitUntilAllowedAsync(nameof(ReportScheduleHostedService), stoppingToken)) return;

            _log.LogInformation("Report schedule worker started; sweeping every {Interval}.",
                _options.SweepInterval);

            using var timer = new PeriodicTimer(_options.SweepInterval);
            do
            {
                try
                {
                    using var scopeRoot = _scopes.CreateScope();
                    var companyScope = scopeRoot.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();

                    await WorkerCompanyRunner.ForEachCompanyAsync(
                        companyScope, _log, nameof(ReportScheduleHostedService),
                        async companyId => await SweepCompanyAsync(companyId, stoppingToken),
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A sweep that throws must not end the loop: the next tick is the recovery, and a
                    // worker that dies on one bad company stops every other company's reports too.
                    _log.LogError(ex, "Report schedule sweep failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task SweepCompanyAsync(int companyId, CancellationToken stoppingToken)
        {
            // A SCOPE PER COMPANY, not per process. The runner is scoped and reaches the DbContext and
            // the company scope holder through it, so sharing one scope across companies would carry
            // one tenant's resolved context into the next tenant's run.
            using var scope = _scopes.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IReportScheduleRunner>();

            var outcomes = await runner.RunDueAsync(companyId, stoppingToken);
            if (outcomes.Count == 0) return;

            // Counted, not enumerated, at information level: a schedule's own failure is already recorded
            // in its outcome and in report history, and re-logging each one here would duplicate the
            // record that a reader is supposed to trust.
            _log.LogInformation("Company {CompanyId}: {Count} scheduled report(s) dispatched.",
                companyId, outcomes.Count);
        }
    }
}
