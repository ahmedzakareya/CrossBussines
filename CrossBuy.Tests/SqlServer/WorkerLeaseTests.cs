using CrossBuy.BL.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // Stage 0 Batch B — the single-worker-process lease, against a real SQL Server.
    //
    // The claim being tested is the one that matters operationally: with two processes running, EXACTLY ONE runs
    // the background workers. That cannot be shown in-process against SQLite — sp_getapplock is a SQL Server
    // feature, and the whole point is that the arbiter is the shared database rather than anything in the app.
    //
    // Skipped — never silently passed — without CROSSBUY_TEST_SQL, against a scratch database the fixture creates
    // and drops. The fixture REFUSES a connection string naming a real CrossBuy database.
    [Collection(SqlServerCollection.Name)]
    public class WorkerLeaseTests
    {
        private readonly SqlServerFixture _sql;
        public WorkerLeaseTests(SqlServerFixture sql) { _sql = sql; }

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        // A distinct lease name per test, so tests in this class cannot contend with each other (or with a
        // developer's app running against the same instance — the lock is database-scoped, but the fixture's
        // database is unique per run anyway).
        private static string LeaseName(string test) => "CrossBuyTest.Lease." + test;

        private (RuntimeInstanceInfo instance, WorkerGate gate) NewProcess(string leaseName, string? name = null)
        {
            var options = Options.Create(new RuntimeOptions
            {
                RequireSingleWorkerProcess = true,
                WorkerLeaseName = leaseName,
                LeaseRetrySeconds = 5,
                InstanceName = name,
            });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _sql.TestConnectionString,
            }).Build();
            var instance = new RuntimeInstanceInfo(options, new StubEnvironment());
            return (instance, new WorkerGate(options, instance, configuration, NullLogger<WorkerGate>.Instance));
        }

        // ---- Batch B item 48: two processes, one primary ----
        [SkippableFact]
        public async Task Only_one_of_two_processes_becomes_the_worker_primary()
        {
            Ready();
            var lease = LeaseName(nameof(Only_one_of_two_processes_becomes_the_worker_primary));

            var (firstInfo, firstGate) = NewProcess(lease, "proc-A");
            await using var _a = firstGate;
            var (secondInfo, secondGate) = NewProcess(lease, "proc-B");
            await using var _b = secondGate;

            Assert.True(await firstGate.WaitUntilAllowedAsync("worker", CancellationToken.None));
            Assert.Equal(WorkerRole.Primary, firstInfo.WorkerRole);
            Assert.True(firstInfo.WorkersEnabled);
            Assert.Null(firstInfo.WorkerSafetyWarning);

            // The second "process" must NOT be allowed through. It waits — so the call is given a token that
            // cancels, and the gate is expected to return false rather than to open.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Assert.False(await secondGate.WaitUntilAllowedAsync("worker", cts.Token));
            Assert.Equal(WorkerRole.Standby, secondInfo.WorkerRole);
            Assert.False(secondInfo.WorkersEnabled);
            // A standby is a normal state, not a fault: no warning, and the mode says enforcement is WORKING.
            Assert.Null(secondInfo.WorkerSafetyWarning);
            Assert.Contains("STANDBY", secondInfo.WorkerSafetyMode);
            Assert.Contains("single-process enforced", secondInfo.WorkerSafetyMode);
        }

        // ---- The lease survives the acquiring "process" doing other database work ----
        [SkippableFact]
        public async Task The_lease_is_held_for_the_process_lifetime_not_for_one_transaction()
        {
            Ready();
            var lease = LeaseName(nameof(The_lease_is_held_for_the_process_lifetime_not_for_one_transaction));

            var (info, gate) = NewProcess(lease, "proc-A");
            await using var _ = gate;
            Assert.True(await gate.WaitUntilAllowedAsync("worker", CancellationToken.None));

            // LockOwner='Transaction' would have released here. Do a full unrelated round trip to make sure
            // nothing about ordinary work drops the lease.
            await _sql.ResetAsync();
            await using (var connection = new SqlConnection(_sql.TestConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("SELECT COUNT(*) FROM BusinessEvents;", connection);
                await command.ExecuteScalarAsync();
            }

            // Still held: a fresh contender is still refused.
            var (_, contender) = NewProcess(lease, "proc-B");
            await using var _c = contender;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Assert.False(await contender.WaitUntilAllowedAsync("worker", cts.Token));

            Assert.Equal(WorkerRole.Primary, info.WorkerRole);
        }

        // ---- Handover: when the primary shuts down, a standby can take over ----
        [SkippableFact]
        public async Task A_standby_can_take_over_after_the_primary_releases()
        {
            Ready();
            var lease = LeaseName(nameof(A_standby_can_take_over_after_the_primary_releases));

            var (firstInfo, firstGate) = NewProcess(lease, "proc-A");
            Assert.True(await firstGate.WaitUntilAllowedAsync("worker", CancellationToken.None));
            Assert.Equal(WorkerRole.Primary, firstInfo.WorkerRole);

            var (secondInfo, secondGate) = NewProcess(lease, "proc-B");
            await using var _b = secondGate;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                Assert.False(await secondGate.WaitUntilAllowedAsync("worker", cts.Token));

            // Graceful shutdown of the primary. DisposeAsync calls sp_releaseapplock explicitly rather than relying
            // on the connection close, so a rolling restart hands over promptly instead of waiting for a timeout.
            await firstGate.DisposeAsync();

            Assert.True(await secondGate.WaitUntilAllowedAsync("worker", CancellationToken.None));
            Assert.Equal(WorkerRole.Primary, secondInfo.WorkerRole);
            Assert.True(secondInfo.WorkersEnabled);
        }

        // ---- Six workers in ONE process share the single lease ----
        [SkippableFact]
        public async Task All_workers_in_one_process_share_a_single_lease()
        {
            Ready();
            var lease = LeaseName(nameof(All_workers_in_one_process_share_a_single_lease));

            var (info, gate) = NewProcess(lease, "proc-A");
            await using var _ = gate;

            // If each caller took its own lock, the first would win and the other five would become standbys —
            // one process would end up refusing to run five of its own six workers.
            var results = await Task.WhenAll(new[]
            {
                nameof(BusinessEventDispatchWorker), "CommMessageDispatcherHostedService",
                "IntegrityCheckHostedService", "CrmReminderHostedService",
                "TaskGeneratorHostedService", "TaskScheduleMatchHostedService",
            }.Select(w => gate.WaitUntilAllowedAsync(w, CancellationToken.None)));

            Assert.All(results, Assert.True);
            Assert.Equal(WorkerRole.Primary, info.WorkerRole);

            // And the database agrees that the lease IS held, asked from an outside session.
            //
            // APPLOCK_TEST rather than sys.dm_tran_locks: for application locks the DMV exposes a HASHED
            // resource_description (0:[name]:(hash)), so a LIKE on the lease name finds nothing and the assertion
            // would pass vacuously as "0 holders". APPLOCK_TEST answers the real question — could another session
            // take this lock exclusively? — and returns 0 when it could not.
            Assert.Equal(0, await LeaseGrantableAsync(lease));
        }

        // ---- Two DIFFERENT lease names do not contend: separate deployments stay independent ----
        [SkippableFact]
        public async Task Different_lease_names_do_not_contend()
        {
            Ready();

            var (oneInfo, oneGate) = NewProcess(LeaseName("independent-A"), "deploy-A");
            await using var _1 = oneGate;
            var (twoInfo, twoGate) = NewProcess(LeaseName("independent-B"), "deploy-B");
            await using var _2 = twoGate;

            Assert.True(await oneGate.WaitUntilAllowedAsync("worker", CancellationToken.None));
            Assert.True(await twoGate.WaitUntilAllowedAsync("worker", CancellationToken.None));

            Assert.Equal(WorkerRole.Primary, oneInfo.WorkerRole);
            Assert.Equal(WorkerRole.Primary, twoInfo.WorkerRole);
        }

        // ---- The opt-out bypasses the lease entirely, even with a working database ----
        [SkippableFact]
        public async Task With_the_requirement_disabled_both_processes_run_workers()
        {
            Ready();
            var lease = LeaseName(nameof(With_the_requirement_disabled_both_processes_run_workers));

            (RuntimeInstanceInfo, WorkerGate) Unenforced(string name)
            {
                var options = Options.Create(new RuntimeOptions
                {
                    RequireSingleWorkerProcess = false, WorkerLeaseName = lease, InstanceName = name,
                });
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _sql.TestConnectionString,
                }).Build();
                var instance = new RuntimeInstanceInfo(options, new StubEnvironment());
                return (instance, new WorkerGate(options, instance, configuration, NullLogger<WorkerGate>.Instance));
            }

            var (aInfo, aGate) = Unenforced("proc-A");
            await using var _a = aGate;
            var (bInfo, bGate) = Unenforced("proc-B");
            await using var _b = bGate;

            Assert.True(await aGate.WaitUntilAllowedAsync("worker", CancellationToken.None));
            Assert.True(await bGate.WaitUntilAllowedAsync("worker", CancellationToken.None));
            Assert.True(aInfo.WorkersEnabled);
            Assert.True(bInfo.WorkersEnabled);

            // And no lock was taken at all — the opt-out must not leave a stray lease behind that a later
            // enforced process would then be refused by. 1 = an outside session could still take it.
            Assert.Equal(1, await LeaseGrantableAsync(lease));
        }

        // Asked from a session that holds nothing: 1 = the exclusive lock is available (nobody holds it),
        // 0 = it is not (somebody does). Negative values mean the test's own call is malformed.
        private async Task<int> LeaseGrantableAsync(string lease)
        {
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT CAST(APPLOCK_TEST('public', @resource, 'Exclusive', 'Session') AS INT);", connection);
            command.Parameters.AddWithValue("@resource", lease);
            var value = await command.ExecuteScalarAsync();
            return Convert.ToInt32(value);
        }

        private sealed class StubEnvironment : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Testing";
            public string ApplicationName { get; set; } = "CrossBuy.Tests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
                new Microsoft.Extensions.FileProviders.NullFileProvider();
        }
    }
}
