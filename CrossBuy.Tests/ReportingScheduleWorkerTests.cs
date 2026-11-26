using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // REP-06 — THE WORKER'S THREE REFUSALS, AND THE ONE PATH THAT PROCEEDS.
    //
    // ReportScheduleHostedService refuses to dispatch for three different reasons, and each is a
    // separate decision that can regress on its own:
    //
    //      1. the worker is disabled              (the operator has not asked for it)
    //      2. this host is a certification runtime (it must write nothing, ever)
    //      3. IWorkerGate says another process owns background work  <-- DUPLICATE-RUN PROTECTION
    //
    // THE THIRD IS THE ONE WITH TEETH. A scheduled report GENERATES a run row, an archive entry and
    // a delivery attempt, and the schedule's NextRunAt is advanced by the runner rather than claimed
    // under a lock. So if two processes both swept, the same document would be generated twice and
    // DELIVERED TWICE to the same recipient. The gate is the only thing preventing that, which makes
    // "the worker consults the gate and obeys it" a property worth pinning rather than assuming.
    //
    // DRIVEN THROUGH StartAsync, NOT BY CALLING ExecuteAsync. The refusals live inside the hosted
    // service's own lifecycle, so a test that reached past it would prove something about a method
    // instead of about the worker.
    //
    // EVERY WAIT IS BOUNDED. A worker test that hangs is indistinguishable from one that fails, and
    // it blocks the whole suite rather than reporting anything.
    // ============================================================================================
    public class ReportingScheduleWorkerTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

        /// Counts scope creation and signals the first one. Creating a scope is the worker's first
        /// act once every gate has let it through, so the counter IS "did it proceed to sweep".
        private sealed class CountingScopeFactory : IServiceScopeFactory
        {
            private readonly TaskCompletionSource _first =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Scopes;
            public Task FirstScope => _first.Task;

            public IServiceScope CreateScope()
            {
                Interlocked.Increment(ref Scopes);
                _first.TrySetResult();

                // An EMPTY provider on purpose. The worker asks it for IWorkerCompanyScope, which is
                // absent, so GetRequiredService throws and the worker's own catch logs and waits for
                // the next tick. That keeps the loop alive without this test having to stand up the
                // whole sweep, and it is the worker's documented "a sweep that throws must not end
                // the loop" behaviour being exercised rather than bypassed.
                return new Scope();
            }

            private sealed class Scope : IServiceScope
            {
                public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();
                public void Dispose() { }
            }
        }

        /// A gate that answers however the test needs, and records that it was asked.
        private sealed class StubGate : IWorkerGate
        {
            private readonly bool _allowed;
            public int Asked;
            public string? AskedFor;

            public StubGate(bool allowed) => _allowed = allowed;

            public Task<bool> WaitUntilAllowedAsync(string workerName, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Asked);
                AskedFor = workerName;
                return Task.FromResult(_allowed);
            }
        }

        private static ReportScheduleHostedService Worker(
            CountingScopeFactory scopes, StubGate gate, bool enabled,
            bool certification = false, TimeSpan? startupDelay = null) =>
            new(scopes, gate, new CertificationRuntimeState(certification),
                new ReportScheduleWorkerOptions
                {
                    Enabled = enabled,
                    StartupDelay = startupDelay ?? TimeSpan.Zero,
                    SweepInterval = TimeSpan.FromMilliseconds(50),
                },
                NullLogger<ReportScheduleHostedService>.Instance);

        /// Runs the worker briefly and stops it, whatever it did. Bounded so a regression that makes
        /// the worker block reports as a failure instead of hanging the suite.
        private static async Task RunBrieflyAsync(ReportScheduleHostedService worker, Task? until = null)
        {
            await worker.StartAsync(CancellationToken.None);
            try
            {
                if (until is not null) await until.WaitAsync(Bound);
                else await Task.Delay(200);
            }
            catch (TimeoutException) { /* the assertion below says what that means */ }
            finally
            {
                using var stop = new CancellationTokenSource(Bound);
                await worker.StopAsync(stop.Token);
            }
        }

        // ---- refusal 1: disabled ---------------------------------------------------------------

        // The shipped state of every deployment today. It must not merely avoid sweeping — it must
        // not even ASK the gate, because asking is what a process does when it intends to run.
        [Fact]
        public async Task A_disabled_worker_never_sweeps_and_never_claims_the_gate()
        {
            var scopes = new CountingScopeFactory();
            var gate = new StubGate(allowed: true);

            await RunBrieflyAsync(Worker(scopes, gate, enabled: false));

            Assert.Equal(0, scopes.Scopes);
            Assert.Equal(0, gate.Asked);
        }

        // ---- refusal 2: certification runtime ---------------------------------------------------

        // A certification host must write nothing. A scheduled report writes a run row, an archive
        // entry and a delivery attempt, so it is suppressed before the gate for the same reason every
        // other worker states.
        [Fact]
        public async Task A_certification_runtime_is_suppressed_even_when_enabled()
        {
            var scopes = new CountingScopeFactory();
            var gate = new StubGate(allowed: true);

            await RunBrieflyAsync(Worker(scopes, gate, enabled: true, certification: true));

            Assert.Equal(0, scopes.Scopes);
            Assert.Equal(0, gate.Asked);
        }

        // ---- refusal 3: DUPLICATE-RUN PROTECTION ------------------------------------------------

        // THE PROPERTY THAT MATTERS. Enabled, not a certification host, and the gate says another
        // process owns background work — so this one must ask, obey, and sweep nothing. If it swept
        // anyway, two processes would deliver the same report to the same recipient twice, and
        // nothing downstream would catch it: NextRunAt is advanced by the runner, not claimed under
        // a lock.
        [Fact]
        public async Task A_standby_process_asks_the_gate_and_does_not_sweep()
        {
            var scopes = new CountingScopeFactory();
            var gate = new StubGate(allowed: false);

            await RunBrieflyAsync(Worker(scopes, gate, enabled: true));

            Assert.Equal(1, gate.Asked);
            Assert.Equal(0, scopes.Scopes);
        }

        // The gate is claimed under the worker's own name, so an operator reading the runtime's
        // worker table sees which worker is holding and which is standing by.
        [Fact]
        public async Task The_gate_is_claimed_under_this_workers_name()
        {
            var gate = new StubGate(allowed: false);

            await RunBrieflyAsync(Worker(new CountingScopeFactory(), gate, enabled: true));

            Assert.Equal(nameof(ReportScheduleHostedService), gate.AskedFor);
        }

        // ---- THE POSITIVE CONTROL ---------------------------------------------------------------

        // Proves the three refusals above are refusals and not a worker that never runs at all.
        // Without this, every assertion in this file passes against `ExecuteAsync => return;`.
        [Fact]
        public async Task An_allowed_worker_does_proceed_to_sweep()
        {
            var scopes = new CountingScopeFactory();
            var gate = new StubGate(allowed: true);

            await RunBrieflyAsync(Worker(scopes, gate, enabled: true), until: scopes.FirstScope);

            Assert.Equal(1, gate.Asked);
            Assert.True(scopes.Scopes > 0,
                "the worker was enabled, not a certification host, and allowed by the gate, " +
                "so it should have created at least one scope to sweep in.");
        }

        // A sweep that throws must not end the loop — the next tick is the recovery. The empty
        // provider makes every sweep throw, so a worker that survives to a SECOND scope has proven
        // it recovers rather than dying on the first bad company.
        [Fact]
        public async Task A_failing_sweep_does_not_kill_the_loop()
        {
            var scopes = new CountingScopeFactory();
            var gate = new StubGate(allowed: true);
            var worker = Worker(scopes, gate, enabled: true);

            await worker.StartAsync(CancellationToken.None);
            try
            {
                await scopes.FirstScope.WaitAsync(Bound);
                await Task.Delay(300);          // several 50ms ticks, each throwing
            }
            finally
            {
                using var stop = new CancellationTokenSource(Bound);
                await worker.StopAsync(stop.Token);
            }

            Assert.True(scopes.Scopes > 1,
                $"the loop stopped after {scopes.Scopes} sweep(s); a throwing sweep must not end it.");
        }
    }
}
