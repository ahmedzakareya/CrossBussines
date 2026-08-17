using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Platform
{
    // Platform Kernel (ADR-001 / ADR-003) — the transactional-outbox dispatcher.
    //
    // Runs in-process as a BackgroundService, the same hosting pattern as IntegrityCheckHostedService /
    // TaskGeneratorHostedService, and creates a DI scope per pass because CrossDbContext is Scoped.
    //
    // Per pass, per registered consumer: claim a batch atomically -> load each event -> hand the envelope
    // to the consumer -> Done, or Failed with the reason. It NEVER tracks a cursor: what to do next is
    // always "whatever rows are claimable now".
    public class BusinessEventDispatchWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly BusinessEventDispatchOptions _options;
        private readonly IWorkerGate _gate;
        private readonly ILogger<BusinessEventDispatchWorker> _log;

        public BusinessEventDispatchWorker(
            IServiceScopeFactory scopes,
            IOptions<BusinessEventDispatchOptions> options,
            IWorkerGate gate,
            ILogger<BusinessEventDispatchWorker> log)
        { _scopes = scopes; _options = options.Value; _gate = gate; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Small startup delay so the app is fully up before the first pass (same as the other workers).
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (TaskCanceledException) { return; }

            // Stage 0 Batch B: only the worker-primary process dispatches. This dispatcher is the one worker that
            // would SURVIVE running twice — atomic claiming partitions the work — but it still waits, so that
            // "which process runs the jobs?" has one answer for every worker rather than one per worker.
            if (!await _gate.WaitUntilAllowedAsync(nameof(BusinessEventDispatchWorker), stoppingToken)) return;

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
                    // A pass-level failure (connection down) must not kill the worker: the rows stay
                    // claimable and the next tick retries them.
                    _log.LogError(ex, "Business event dispatch pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        // Drains every consumer's queue for one pass. Internal so a test can drive a single pass
        // deterministically instead of waiting on the timer.
        internal async Task RunPassAsync(CancellationToken cancellationToken)
        {
            foreach (var consumerName in BusinessEventConsumers.Registered)
            {
                // Sweep abandoned claims BEFORE claiming. A worker that died on its last attempt leaves a row
                // Claimed with Attempts = MaxAttempts, which ClaimPendingAsync can never match again because
                // every branch of its WHERE sits under `Attempts < MaxAttempts`. Releasing first means such a
                // row reaches its terminal state in the same pass that would otherwise have walked past it,
                // instead of reading as in-flight forever. It touches nothing a live worker holds — the lease
                // is part of the predicate.
                await ReleaseAbandonedAsync(consumerName, cancellationToken);

                // Keep draining while the consumer returns full batches, so a backlog clears in one pass.
                while (!cancellationToken.IsCancellationRequested)
                {
                    int handled = await RunBatchAsync(consumerName, cancellationToken);
                    if (handled < _options.BatchSize) break;
                }
            }
        }

        private async Task ReleaseAbandonedAsync(string consumerName, CancellationToken cancellationToken)
        {
            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IEventDispatchStore>();

            int released = await store.ReleaseAbandonedClaimsAsync(consumerName, cancellationToken);
            if (released > 0)
            {
                // Warning, not information: rows only land here because a worker stopped mid-batch, and an
                // operator should see how many and for which consumer rather than find them by querying.
                _log.LogWarning(
                    "Dispatch: released {Released} abandoned claim(s) for consumer {Consumer} whose lease expired " +
                    "with no attempts remaining. They are now Failed and awaiting an operator retry.",
                    released, consumerName);
            }
        }

        private async Task<int> RunBatchAsync(string consumerName, CancellationToken cancellationToken)
        {
            using var scope = _scopes.CreateScope();
            var provider = scope.ServiceProvider;

            var store = provider.GetRequiredService<IEventDispatchStore>();
            var db = provider.GetRequiredService<CrossDbContext>();
            var events = provider.GetRequiredService<IBusinessEventService>();

            // Stage 1 Batch B / B3 — the dispatcher reads ACROSS companies, by design.
            //
            // BusinessEventDispatch is one queue for every company (ClaimPendingAsync claims in a single atomic
            // statement), and this pass then loads each claimed event BY ID. Without a bypass, Batch B's filter on
            // BusinessEvent would return null for any event outside this scope's company, and the code below would
            // mark a perfectly healthy row Failed with "Event N no longer exists" while burning an attempt.
            //
            // The bypass is held for exactly this batch and disposed with it. Only a System or Worker context may
            // hold PlatformDispatch — an interactive user never can.
            var bypass = provider.GetRequiredService<ICompanyIsolationBypass>();
            using var dispatchScope = bypass.BeginPlatformDispatch(
                $"Outbox dispatch pass for consumer '{consumerName}' — the queue spans every company.");

            var consumer = provider.GetServices<IBusinessEventConsumer>()
                .FirstOrDefault(c => string.Equals(c.Consumer, consumerName, StringComparison.Ordinal));
            if (consumer == null)
            {
                // A registered consumer with no implementation would accumulate rows nothing drains.
                _log.LogError("Consumer '{Consumer}' is in BusinessEventConsumers.Registered but no IBusinessEventConsumer " +
                              "implementation is registered in DI — its dispatch rows will never be processed", consumerName);
                return 0;
            }

            var batch = await store.ClaimPendingAsync(consumerName, _options.BatchSize, cancellationToken);
            if (batch.Count == 0) return 0;

            foreach (var work in batch)
            {
                var stored = await db.BusinessEvents.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.EventId == work.EventId, cancellationToken);
                if (stored == null)
                {
                    // The FK makes this practically impossible; if it ever happens the row must not spin.
                    await store.MarkFailedAsync(work.DispatchId, $"Event {work.EventId} no longer exists.", cancellationToken);
                    continue;
                }

                try
                {
                    var envelope = events.BuildEnvelope(stored);
                    await consumer.HandleAsync(envelope, cancellationToken);
                    await store.MarkDoneAsync(work.DispatchId, cancellationToken);
                    await store.TryCompleteEventAsync(work.EventId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Shutdown: leave the row Claimed. It is reclaimed after StaleClaimMinutes.
                    throw;
                }
                catch (Exception ex)
                {
                    // Attempts was already incremented by the claim, so a permanently failing row stops at
                    // MaxAttempts and stays Failed with its reason for an operator to inspect.
                    await store.MarkFailedAsync(work.DispatchId, ex.Message, cancellationToken);
                    _log.LogWarning(ex, "Consumer {Consumer} failed on event {EventId} (attempt {Attempt}/{Max})",
                        consumerName, work.EventId, work.Attempts, _options.MaxAttempts);
                }
            }
            return batch.Count;
        }
    }
}