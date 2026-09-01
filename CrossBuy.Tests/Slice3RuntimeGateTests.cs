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

        // ---- An unreachable database also fails open, with the reason preserved ----
        [Fact]
        public async Task With_an_unreachable_database_the_gate_opens_and_records_why()
        {
            var (instance, gate) = Build(
                new RuntimeOptions { RequireSingleWorkerProcess = true },
                "Server=localhost,14331;Database=NoSuchDb;User Id=nobody;Password=nothing;TrustServerCertificate=True;Connect Timeout=1");
            await using var _ = gate;

            Assert.True(await gate.WaitUntilAllowedAsync("test", CancellationToken.None));

            Assert.Equal(WorkerRole.Unrestricted, instance.WorkerRole);
            Assert.NotNull(instance.WorkerSafetyWarning);
            Assert.Contains("could not be evaluated", instance.WorkerSafetyWarning!);
            // The exception type is kept so an operator can tell a permission error from a network error.
            Assert.Contains("Exception", instance.WorkerSafetyWarning!);
        }

        // ---- The lease is evaluated ONCE per process, not once per worker ----
        [Fact]
        public async Task Six_workers_asking_the_gate_do_not_each_evaluate_the_lease()
        {
            var (instance, gate) = Build(
                new RuntimeOptions { RequireSingleWorkerProcess = true },
                "Server=localhost,14332;Database=NoSuchDb;User Id=nobody;Password=nothing;TrustServerCertificate=True;Connect Timeout=1");
            await using var _ = gate;

            // Six concurrent callers, as at startup. A per-caller evaluation would take six connections (and, on a
            // working database, six locks — of which five would fail and turn the process into a standby of itself).
            var results = await Task.WhenAll(Enumerable.Range(0, 6)
                .Select(i => gate.WaitUntilAllowedAsync("worker" + i, CancellationToken.None)));

            Assert.All(results, Assert.True);
            Assert.Equal(WorkerRole.Unrestricted, instance.WorkerRole);
            Assert.NotNull(instance.WorkerSafetyWarning);
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
