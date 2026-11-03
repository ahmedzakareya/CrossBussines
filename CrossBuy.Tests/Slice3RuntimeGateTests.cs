using CrossBuy.BL.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 0 Batch B — runtime identity and the single-worker-process gate.
    //
    // The lease itself is a SQL Server session application lock, so the two-processes-contend case can only be
    // proven against a real engine — that lives in SqlServer/WorkerLeaseTests. These tests cover the parts that are
    // pure logic and are exactly where a "control" quietly becomes decoration:
    //   * the opt-out really opts out;
    //   * a failure to evaluate the lease is reported, not swallowed;
    //   * WorkersEnabled and WorkerSafetyMode never claim enforcement that is not happening.
    public class Slice3RuntimeGateTests
    {
        private static IConfiguration Config(string? connectionString) =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
            }).Build();

        private static (RuntimeInstanceInfo instance, WorkerGate gate) Build(
            RuntimeOptions options, string? connectionString)
        {
            var opts = Options.Create(options);
            var instance = new RuntimeInstanceInfo(opts, new StubHostEnvironment());
            var gate = new WorkerGate(opts, instance, Config(connectionString), NullLogger<WorkerGate>.Instance);
            return (instance, gate);
        }

        // ---- Identity is real, and specific enough to tell two processes apart ----
        [Fact]
        public void The_instance_identifies_this_process()
        {
            var (instance, _) = Build(new RuntimeOptions(), null);

            Assert.NotEqual(Guid.Empty, instance.InstanceId);
            Assert.Equal(Environment.MachineName, instance.MachineName);
            Assert.Equal(Environment.ProcessId, instance.ProcessId);
            // The PROCESS start time, not "now" — that is what distinguishes two runs on one machine when the OS
            // has recycled a PID.
            Assert.True(instance.StartedAtUtc <= DateTime.UtcNow);
            Assert.True(instance.StartedAtUtc > DateTime.UtcNow.AddDays(-7));
            Assert.False(string.IsNullOrWhiteSpace(instance.ApplicationVersion));
            Assert.Equal("Testing", instance.EnvironmentName);
            // Default name carries machine AND pid, so a log line is unambiguous with no configuration at all.
            Assert.Contains(Environment.MachineName, instance.InstanceName);
            Assert.Contains(Environment.ProcessId.ToString(), instance.InstanceName);
        }

        [Fact]
        public void A_configured_instance_name_replaces_the_default()
        {
            var (instance, _) = Build(new RuntimeOptions { InstanceName = "  web-01  " }, null);
            Assert.Equal("web-01", instance.InstanceName);
        }

        [Fact]
        public void A_blank_configured_instance_name_falls_back_to_machine_and_pid()
        {
            // appsettings ships InstanceName as "", which must not produce an empty identity in every log line.
            var (instance, _) = Build(new RuntimeOptions { InstanceName = "   " }, null);
            Assert.Contains(Environment.MachineName, instance.InstanceName);
        }

        // ---- Before anything is evaluated, the mode must not claim a state it does not know ----
        [Fact]
        public void Before_evaluation_the_role_is_unknown_and_workers_are_not_enabled()
        {
            var (instance, _) = Build(new RuntimeOptions(), null);

            Assert.Equal(WorkerRole.Unknown, instance.WorkerRole);
            Assert.False(instance.WorkersEnabled);
            Assert.Equal("evaluating", instance.WorkerSafetyMode);
            Assert.Null(instance.WorkerSafetyWarning);
        }

        // ---- Opting out really opts out, and says so honestly ----
        [Fact]
        public async Task With_the_requirement_disabled_the_gate_opens_immediately_and_does_not_claim_enforcement()
        {
            var (instance, gate) = Build(new RuntimeOptions { RequireSingleWorkerProcess = false }, null);
            await using var _ = gate;

            Assert.True(await gate.WaitUntilAllowedAsync("test", CancellationToken.None));

            Assert.Equal(WorkerRole.Unrestricted, instance.WorkerRole);
            Assert.True(instance.WorkersEnabled);
            // An explicit opt-out is NOT a warning condition — it is a decision. The message must reflect that
            // rather than crying wolf, which is why the two Unrestricted cases read differently.
            Assert.Null(instance.WorkerSafetyWarning);
            Assert.Contains("not enforced", instance.WorkerSafetyMode);
            Assert.DoesNotContain("UNENFORCED", instance.WorkerSafetyMode);
        }

        // ---- A missing connection string fails OPEN, loudly ----
        [Fact]
        public async Task With_no_connection_string_the_gate_opens_but_reports_that_enforcement_is_off()
        {
            var (instance, gate) = Build(new RuntimeOptions { RequireSingleWorkerProcess = true }, null);
            await using var _ = gate;

            // Fail-open is the deliberate choice: a configuration mistake must not silently stop the audit outbox
            // and every scheduled job. What it must never do is stay quiet about it.
            Assert.True(await gate.WaitUntilAllowedAsync("test", CancellationToken.None));

            Assert.Equal(WorkerRole.Unrestricted, instance.WorkerRole);
            Assert.True(instance.WorkersEnabled);
            Assert.NotNull(instance.WorkerSafetyWarning);
            Assert.Contains("connection string", instance.WorkerSafetyWarning!);
            Assert.Contains("UNENFORCED", instance.WorkerSafetyMode);
        }

        // =========================================================================================
        // D-16 - A FAILED CHECK IS NOT PERMISSION.
        //
        // The test that stood here asserted the OPPOSITE: that an unreachable database opened the gate.
        // It is deleted rather than adapted, because it pinned the defect. Its own comment called
        // fail-open "the deliberate choice", and for an ABSENT CONNECTION STRING it still is - that case
        // is kept green immediately above. What was never deliberate was failing open on a database that
        // was configured and did not answer, and then CACHING that answer for the life of the process.
        //
        // NO DATABASE IS OPENED BY ANY TEST BELOW. The gate takes its acquisition step as a function, so
        // the policy - fail closed, never latch, recover - is exercised with a stub. That seam exists
        // because the latch survived precisely by being untestable without an engine.
        // =========================================================================================

        private sealed class StubLease : IWorkerLease
        {
            public bool IsHeld { get; set; } = true;
            public int Disposals { get; private set; }
            public ValueTask DisposeAsync() { Disposals++; IsHeld = false; return ValueTask.CompletedTask; }
        }

        private static (RuntimeInstanceInfo instance, WorkerGate gate) BuildWith(
            Func<CancellationToken, Task<WorkerLeaseAttempt>> attempt, int retrySeconds = 60)
        {
            var opts = Options.Create(new RuntimeOptions
            { RequireSingleWorkerProcess = true, LeaseRetrySeconds = retrySeconds });
            var instance = new RuntimeInstanceInfo(opts, new StubHostEnvironment());
            var gate = new WorkerGate(opts, instance, Config("Server=unused"), NullLogger<WorkerGate>.Instance, attempt);
            return (instance, gate);
        }

        private static Task<WorkerLeaseAttempt> Failed(string detail = "SqlException: network") =>
            Task.FromResult(new WorkerLeaseAttempt(WorkerLeaseOutcome.Failed, detail, null));

        /// SQL UNAVAILABLE AT STARTUP. The old behaviour was Unrestricted - "run anyway". A worker must now
        /// stand down, because an unanswered question about who is primary is not a yes.
        [Fact]
        public async Task An_unavailable_database_at_startup_stands_the_worker_down_instead_of_running_it()
        {
            var (instance, gate) = BuildWith(_ => Failed());
            await using var _ = gate;

            Assert.False(await gate.IsStillAllowedAsync("test", CancellationToken.None));
            Assert.Equal(WorkerRole.Standby, instance.WorkerRole);
            Assert.False(instance.WorkersEnabled);
            // And it must NOT have claimed enforcement is off - that is the fail-open story, and it is gone.
            Assert.NotEqual(WorkerRole.Unrestricted, instance.WorkerRole);
        }

        /// THE LATCH, WHICH IS THE DEFECT THAT MADE THE OTHER ONE PERMANENT. One failure after a healthy
        /// acquisition must not pin the process. The third call proves the answer was never cached.
        [Fact]
        public async Task A_transient_failure_after_a_good_check_is_not_cached()
        {
            var lease = new StubLease();
            var calls = 0;
            var (instance, gate) = BuildWith(_ =>
            {
                calls++;
                if (calls == 1) return Task.FromResult(new WorkerLeaseAttempt(WorkerLeaseOutcome.Acquired, null, lease));
                if (calls == 2) return Failed();
                return Task.FromResult(new WorkerLeaseAttempt(WorkerLeaseOutcome.Acquired, null, new StubLease()));
            });
            await using var _ = gate;

            Assert.True(await gate.IsStillAllowedAsync("test", CancellationToken.None));   // 1: primary
            Assert.Equal(WorkerRole.Primary, instance.WorkerRole);

            lease.IsHeld = false;                                                         // the connection died
            Assert.False(await gate.IsStillAllowedAsync("test", CancellationToken.None));  // 2: failed -> stand down
            Assert.Equal(WorkerRole.Standby, instance.WorkerRole);

            Assert.True(await gate.IsStillAllowedAsync("test", CancellationToken.None));   // 3: RECOVERED
            Assert.Equal(WorkerRole.Primary, instance.WorkerRole);
            Assert.Equal(3, calls);
        }

        /// NO UNRESTRICTED CACHED STATE. Repeated failures must each be re-asked, never answered from a
        /// stored verdict - and must never resolve to Unrestricted.
        [Fact]
        public async Task Repeated_failures_are_re_asked_every_time_and_never_become_unrestricted()
        {
            var calls = 0;
            var (instance, gate) = BuildWith(_ => { calls++; return Failed(); });
            await using var _ = gate;

            for (var i = 0; i < 4; i++)
                Assert.False(await gate.IsStillAllowedAsync("test", CancellationToken.None));

            Assert.Equal(4, calls);                       // asked every time, not once
            Assert.Equal(WorkerRole.Standby, instance.WorkerRole);
            Assert.NotEqual(WorkerRole.Unrestricted, instance.WorkerRole);
        }

        /// LEASE REVOKED MID-RUN. The holder's connection drops and another process takes it. The old gate
        /// never looked again; this one stands down and releases the dead handle rather than leaking it.
        [Fact]
        public async Task Losing_the_lease_mid_run_stands_the_worker_down_and_drops_the_dead_handle()
        {
            var lease = new StubLease();
            var (instance, gate) = BuildWith(_ =>
                lease.Disposals == 0 && lease.IsHeld
                    ? Task.FromResult(new WorkerLeaseAttempt(WorkerLeaseOutcome.Acquired, null, lease))
                    : Task.FromResult(new WorkerLeaseAttempt(WorkerLeaseOutcome.HeldByAnother, null, null)));
            await using var _ = gate;

            Assert.True(await gate.IsStillAllowedAsync("test", CancellationToken.None));
            Assert.Equal(WorkerRole.Primary, instance.WorkerRole);

            lease.IsHeld = false;
            Assert.False(await gate.IsStillAllowedAsync("test", CancellationToken.None));
            Assert.Equal(WorkerRole.Standby, instance.WorkerRole);
            Assert.Equal(1, lease.Disposals);   // the dead lease was released, not left in place
        }

        /// A HEALTHY LEASE COSTS NOTHING TO RE-CHECK. The acquisition step runs once; every later tick is
        /// answered from the held lease. This is what makes a per-tick check affordable.
        [Fact]
        public async Task A_held_lease_is_confirmed_without_asking_again()
        {
            var calls = 0;
            var (instance, gate) = BuildWith(_ =>
            { calls++; return Task.FromResult(new WorkerLeaseAttempt(WorkerLeaseOutcome.Acquired, null, new StubLease())); });
            await using var _ = gate;

            for (var i = 0; i < 5; i++)
                Assert.True(await gate.IsStillAllowedAsync("test", CancellationToken.None));

            Assert.Equal(1, calls);
            Assert.Equal(WorkerRole.Primary, instance.WorkerRole);
        }

        /// CANCELLATION DURING A RE-CHECK returns false without throwing, so a shutdown looks like a clean
        /// stand-down to the caller rather than an exception escaping a worker loop.
        [Fact]
        public async Task A_cancelled_recheck_returns_false_rather_than_throwing()
        {
            var (_, gate) = BuildWith(_ => Task.FromResult(new WorkerLeaseAttempt(WorkerLeaseOutcome.Acquired, null, new StubLease())));
            await using var __ = gate;

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.False(await gate.IsStillAllowedAsync("test", cts.Token));
        }

        /// NO WORK WHILE AUTHORIZATION IS UNKNOWN. The blocking startup path must PARK on a failed check,
        /// not open. Bounded by cancellation so the test does not sit on the retry delay.
        [Fact]
        public async Task The_startup_wait_parks_on_a_failed_check_and_never_opens_the_gate()
        {
            var (instance, gate) = BuildWith(_ => Failed());
            await using var _ = gate;

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            Assert.False(await gate.WaitUntilAllowedAsync("test", cts.Token));
            Assert.NotEqual(WorkerRole.Unrestricted, instance.WorkerRole);
            Assert.False(instance.WorkersEnabled);
        }

        // ---- The lease is evaluated ONCE per process, not once per worker ----
        //
        // D-16 REWROTE THIS TEST, AND THE REASON MATTERS. It used to point six callers at an UNREACHABLE
        // database and assert they all came back true with the role Unrestricted. That passed only because
        // the gate failed open and then LATCHED: the first caller's failure was cached and the other five
        // read the cache. So the test was not measuring "one evaluation per process" at all - it was
        // measuring the defect, and it would now park six workers on the retry delay for ever and take the
        // test host down with it. That is the fix working, not a regression.
        //
        // The invariant it was WRITTEN for is real and still holds, so it is proven directly instead: six
        // concurrent callers, one acquisition, five served from the held lease. No database, by the seam.
        [Fact]
        public async Task Six_workers_asking_the_gate_do_not_each_evaluate_the_lease()
        {
            var acquisitions = 0;
            var (instance, gate) = BuildWith(_ =>
            {
                Interlocked.Increment(ref acquisitions);
                return Task.FromResult(new WorkerLeaseAttempt(WorkerLeaseOutcome.Acquired, null, new StubLease()));
            });
            await using var _ = gate;

            // Six concurrent callers, as at startup. A per-caller evaluation would take six connections and,
            // on a working database, six locks - of which five would fail and turn the process into a standby
            // of itself.
            var results = await Task.WhenAll(Enumerable.Range(0, 6)
                .Select(i => gate.WaitUntilAllowedAsync("worker" + i, CancellationToken.None)));

            Assert.All(results, Assert.True);
            Assert.Equal(WorkerRole.Primary, instance.WorkerRole);
            Assert.Equal(1, acquisitions);   // the point of the test, now asserted rather than implied
        }

        // ---- A cancelled gate returns false so the worker exits instead of looping ----
        [Fact]
        public async Task A_cancelled_wait_returns_false_rather_than_throwing()
        {
            var (_, gate) = Build(new RuntimeOptions { RequireSingleWorkerProcess = false }, null);
            await using var _ = gate;

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // With the requirement disabled the gate opens without touching the token at all, which is what lets a
            // dev machine start instantly.
            Assert.True(await gate.WaitUntilAllowedAsync("test", cts.Token));
        }

        // ---- The safety mode text never claims Primary while workers are idle, or vice versa ----
        [Theory]
        [InlineData(WorkerRole.Primary, true, "PRIMARY")]
        [InlineData(WorkerRole.Standby, false, "STANDBY")]
        public void The_reported_mode_matches_whether_workers_actually_run(WorkerRole role, bool enabled, string marker)
        {
            var (instance, _) = Build(new RuntimeOptions(), null);
            instance.SetWorkerRole(role);

            Assert.Equal(enabled, instance.WorkersEnabled);
            Assert.Contains(marker, instance.WorkerSafetyMode);
            Assert.Contains("single-process enforced", instance.WorkerSafetyMode);
        }

        // ---- Defaults are the SAFE posture, so an unconfigured deployment is protected ----
        [Fact]
        public void The_default_options_require_a_single_worker_process()
        {
            var options = new RuntimeOptions();

            Assert.True(options.RequireSingleWorkerProcess);
            Assert.Equal("CrossBuy.BackgroundWorkers", options.WorkerLeaseName);
            Assert.True(options.LeaseRetrySeconds >= 30);
            Assert.Null(options.InstanceName);
        }

        private sealed class StubHostEnvironment : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Testing";
            public string ApplicationName { get; set; } = "CrossBuy.Tests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
                new Microsoft.Extensions.FileProviders.NullFileProvider();
        }
    }
}
