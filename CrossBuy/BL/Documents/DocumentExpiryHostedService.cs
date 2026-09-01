using System;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Documents
{
    // =============================================================================================
    // The periodic half of document expiry. It owns NO rule and NO date arithmetic: it decides WHEN to
    // ask and WHICH companies to ask about, and DocumentExpiryProjection answers.
    //
    // Every isolation decision here is the platform's, not this worker's - IWorkerCompanyScope for the
    // company list, WorkerCompanyRunner for the per-company loop and failure containment,
    // WorkerScope.ForCompany to bind the DI scope so the query filters read a resolved company. There
    // is no `const int CompanyId = 1` in this file, and the shared runner is what guarantees there
    // cannot be: it has no fallback, so a host with no companies processes nothing, loudly.
    //
    // ONCE A DAY, not every fifteen minutes. Expiry moves at the speed of the calendar: the state of a
    // document changes when the DATE changes, so a shorter period would re-ask a question whose answer
    // cannot have moved, and the deduplication would silently absorb every extra run. The first pass is
    // delayed so startup is not competing with request traffic.
    // =============================================================================================
    public sealed class DocumentExpiryHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IWorkerGate _gate;
        private readonly CertificationRuntimeState _certification;
        private readonly ILogger<DocumentExpiryHostedService> _log;

        public DocumentExpiryHostedService(IServiceScopeFactory scopes, IWorkerGate gate,
            CertificationRuntimeState certification, ILogger<DocumentExpiryHostedService> log)
        { _scopes = scopes; _gate = gate; _certification = certification; _log = log; }

        /// Exposed so the suppression is assertable without starting a host. Read-only, side-effect free.
        public bool Suppressed => _certification.BackgroundWritersSuppressed;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // CERTIFICATION. This worker WRITES - it creates task rows and records events - and a
            // conformance capture must observe stable data or two runs of the same page disagree.
            // Suppression lives here rather than around the registration, which is both stronger (it
            // holds however the service is composed) and consistent with the task generator.
            if (_certification.BackgroundWritersSuppressed)
            {
                _log.LogInformation(
                    "DocumentExpiryHostedService is suppressed: this host is a certification runtime, so no " +
                    "background writes are performed.");
                return;
            }

            try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); }
            catch (TaskCanceledException) { return; }

            // NOT SAFE IN TWO PROCESSES AT ONCE. The projection is idempotent per (document, expiry
            // date), but two instances racing the same key would both see "not yet logged" before
            // either wrote, and create the task twice. The gate is the platform's answer to that and
            // costs nothing to use.
            if (!await _gate.WaitUntilAllowedAsync(nameof(DocumentExpiryHostedService), stoppingToken)) return;

            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            do
            {
                try
                {
                    using var scopeRoot = _scopes.CreateScope();
                    var companyScope = scopeRoot.ServiceProvider.GetRequiredService<IWorkerCompanyScope>();

                    await WorkerCompanyRunner.ForEachCompanyAsync(companyScope, _log,
                        nameof(DocumentExpiryHostedService),
                        async companyId =>
                        {
                            // Bound to THIS company before anything in the scope runs a query.
                            using var scope = WorkerScope.ForCompany(_scopes, companyId);
                            var projection = scope.ServiceProvider.GetRequiredService<IDocumentExpiryProjection>();
                            var summary = await projection.RunAsync(companyId, stoppingToken);

                            if (summary.TasksCreated > 0 || summary.Truncated)
                                _log.LogInformation(
                                    "Document expiry, company {CompanyId}: {Examined} examined, {Created} task(s) " +
                                    "created, {Raised} event(s), {Skipped} skipped{Truncated}",
                                    companyId, summary.Examined, summary.TasksCreated, summary.EventsRaised,
                                    summary.Skipped,
                                    summary.Truncated
                                        ? $" — CAPPED at {DocumentExpiryProjection.MaxPerRun}; the remainder is picked up next run"
                                        : string.Empty);
                        },
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;   // shutdown
                }
                catch (Exception ex)
                {
                    // One bad tick must not end the worker for the life of the process.
                    _log.LogError(ex, "DocumentExpiryHostedService tick failed; the next tick will retry.");
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
