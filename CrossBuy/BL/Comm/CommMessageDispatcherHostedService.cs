using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Comm
{
    // Stage 0 (Slice-003) — the email outbox dispatcher that the Communication Hub design specified and that was
    // never built.
    //
    // WHAT IT FIXES: CommService.SendAsync queued a CommMessage and then transmitted it SYNCHRONOUSLY inside the
    // request. If SMTP was down the row became Failed and stayed Failed forever; if SMTP was unconfigured the row
    // stayed Queued forever. Either way nobody was told, and a customer's invoice email was silently lost. The
    // columns for an outbox already existed — only the drain did not.
    //
    // WHAT IT DOES NOT CHANGE: the interactive send path. SendAsync still attempts an immediate send so a user
    // gets instant feedback; this worker only picks up what that attempt left behind. A message that sends first
    // time is never touched here.
    //
    // Delivery-state ownership is strict: the store is the ONLY writer of Status/Attempts/ClaimedAt/SentAt/Error.
    // The dispatcher calls ICommService.TransmitAsync, which performs the wire send and nothing else, so Attempts
    // is incremented exactly once — by the claim.
    public class CommMessageDispatcherHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly CommMessageDispatchOptions _options;
        private readonly CrossBuy.BL.Platform.IWorkerGate _gate;
        private readonly ILogger<CommMessageDispatcherHostedService> _log;

        public CommMessageDispatcherHostedService(
            IServiceScopeFactory scopes,
            IOptions<CommMessageDispatchOptions> options,
            CrossBuy.BL.Platform.IWorkerGate gate,
            ILogger<CommMessageDispatcherHostedService> log)
        { _scopes = scopes; _options = options.Value; _gate = gate; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Startup must never depend on SMTP being reachable.
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds)), stoppingToken); }
            catch (TaskCanceledException) { return; }

            // Stage 0 Batch B: only the worker-primary process sends. Atomic claiming already prevents two
            // processes sending the same message, but a standby that keeps polling adds connection churn for
            // nothing — and a duplicate email is the one failure here that cannot be taken back.
            if (!await _gate.WaitUntilAllowedAsync(nameof(CommMessageDispatcherHostedService), stoppingToken)) return;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)));
            do
            {
                try
                {
                    await RunPassAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A pass-level failure (database unreachable) must not kill the worker: claimed rows age out
                    // via StaleClaimMinutes and the next tick retries them.
                    _log.LogError(ex, "Email outbox dispatch pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        // Drains the queue while full batches keep coming, so a backlog clears in one pass.
        // Internal so a test can drive a single deterministic pass instead of waiting on the timer.
        internal async Task RunPassAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int handled = await RunBatchAsync(cancellationToken);
                if (handled < _options.BatchSize) break;
            }
        }

        private async Task<int> RunBatchAsync(CancellationToken cancellationToken)
        {
            // One DI scope per batch — CrossDbContext is Scoped.
            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ICommMessageDispatchStore>();
            var mail = scope.ServiceProvider.GetRequiredService<ICommService>();

            // Do not claim anything we cannot possibly send. Without this, an unconfigured SMTP would burn every
            // message's Attempts budget until all of them hit MaxAttempts and stopped retrying for good.
            if (!mail.SmtpConfigured)
            {
                _log.LogDebug("Email outbox: SMTP not configured — skipping this pass, nothing claimed");
                return 0;
            }

            var batch = await store.ClaimPendingAsync(_options.BatchSize, cancellationToken);
            if (batch.Count == 0) return 0;

            foreach (var work in batch)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    var (ok, error) = await mail.TransmitAsync(work.MessageId, cancellationToken);
                    if (ok)
                    {
                        await store.MarkSentAsync(work.MessageId, cancellationToken);
                        _log.LogInformation("Email outbox: message {MessageId} sent for company {CompanyId} on attempt {Attempt}",
                            work.MessageId, work.CompanyId, work.Attempts);
                    }
                    else
                    {
                        await store.MarkFailedAsync(work.MessageId, error ?? "Unknown SMTP failure", cancellationToken);
                        LogFailure(work, error);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Shutdown: leave the row Claimed. It is reclaimed after StaleClaimMinutes.
                    throw;
                }
                catch (Exception ex)
                {
                    await store.MarkFailedAsync(work.MessageId, ex.Message, cancellationToken);
                    LogFailure(work, ex.Message);
                }
            }
            return batch.Count;
        }

        // Final failures are never swallowed: an exhausted message is logged at Error so it surfaces in
        // operations, and its Failed row keeps the reason for an operator.
        private void LogFailure(CommMessageWorkItem work, string? error)
        {
            bool exhausted = work.Attempts >= _options.MaxAttempts;
            if (exhausted)
                _log.LogError("Email outbox: message {MessageId} (company {CompanyId}) FAILED PERMANENTLY after {Attempts}/{Max} attempts — {Error}",
                    work.MessageId, work.CompanyId, work.Attempts, _options.MaxAttempts, error);
            else
                _log.LogWarning("Email outbox: message {MessageId} (company {CompanyId}) failed on attempt {Attempts}/{Max}, will retry — {Error}",
                    work.MessageId, work.CompanyId, work.Attempts, _options.MaxAttempts, error);
        }
    }
}
