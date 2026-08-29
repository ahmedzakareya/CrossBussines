using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // WRITER WORKER GOVERNANCE — the two that were still pinned to company 1.
    //
    // IntegrityCheckHostedService and CrmReminderHostedService each carried
    //
    //     private const int CompanyId = 1;
    //
    // and passed it straight into their writes. Neither was global: IntegrityCheckRun has a CompanyID
    // column, and the CRM worker both reads activities and marks them reminded per company. So on any
    // install with more than one company, company 1 was processed and every other company was never
    // processed at all - silently, because the worker logged a successful run each time.
    //
    // The defect is OMISSION, not leakage, and the distinction matters for what a fix has to prove:
    // the constant was too NARROW. Neither worker could read another company's rows, because every
    // query was pinned. So the tests below prove enumeration and per-company scoping, not that a leak
    // was closed - claiming a leak was fixed would misdescribe what happened.
    //
    // Neither had a gate either, so a second instance duplicated a daily integrity row and re-sent a
    // real person the same reminder.
    // ============================================================================================
    public class WriterWorkerGovernanceTests
    {
        private static string SourceOf(string file) =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", file));

        private static string ProgramSource() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string CodeOf(string file) =>
            string.Join("\n", SourceOf(file).Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        public static TheoryData<string> Workers() => new()
        {
            "IntegrityCheckHostedService.cs",
            "CrmReminderHostedService.cs",
        };

        // ---- the defect itself ----------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(Workers))]
        public void No_worker_pins_itself_to_a_company(string file)
        {
            var code = CodeOf(file);

            // The exact shape both carried. This assertion fails on the pre-fix file.
            Assert.DoesNotContain("const int CompanyId = 1", code, StringComparison.Ordinal);
            Assert.DoesNotContain("DefaultCompanyId", code, StringComparison.Ordinal);

            // And nothing reintroduced it as a literal argument.
            Assert.DoesNotMatch(new Regex(@"Async\(\s*1\s*,"), code);
        }

        [Theory]
        [MemberData(nameof(Workers))]
        public void Every_worker_enumerates_companies_through_the_canonical_runner(string file)
        {
            var code = CodeOf(file);

            // The platform mechanism, not a private loop over Companies.
            Assert.Contains("WorkerCompanyRunner.ForEachCompanyAsync", code, StringComparison.Ordinal);
            Assert.Contains("IWorkerCompanyScope", code, StringComparison.Ordinal);

            // Each company gets its own scope, which is what carries the BusinessContext.
            Assert.Contains("WorkerScope.ForCompany", code, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(Workers))]
        public void Every_worker_is_behind_the_existing_gate_and_certification_switch(string file)
        {
            var code = CodeOf(file);

            // The canonical gate - not a second configuration system.
            Assert.Contains("IWorkerGate", code, StringComparison.Ordinal);
            Assert.Contains("WaitUntilAllowedAsync", code, StringComparison.Ordinal);

            // A conformance capture must observe stable data, so a writer suppresses itself there.
            Assert.Contains("BackgroundWritersSuppressed", code, StringComparison.Ordinal);
        }

        [Theory]
        [MemberData(nameof(Workers))]
        public void The_gate_is_awaited_before_any_work_and_suppression_short_circuits_first(string file)
        {
            var code = CodeOf(file);

            int suppress = code.IndexOf("BackgroundWritersSuppressed", StringComparison.Ordinal);
            int gate = code.IndexOf("WaitUntilAllowedAsync", StringComparison.Ordinal);
            int work = code.IndexOf("ForEachCompanyAsync", StringComparison.Ordinal);

            // Order is the assertion: suppression, then gate, then any company work. A gate awaited
            // after the first tick would let one cycle through on every instance.
            Assert.True(suppress >= 0 && gate > suppress && work > gate,
                $"{file}: expected suppression -> gate -> work, got {suppress}/{gate}/{work}");
        }

        // ---- the registration all of this depends on -------------------------------------------------

        [Fact]
        public void The_company_scope_the_workers_resolve_is_actually_registered()
        {
            // THE REGRESSION THIS FILE EXISTS FOR. Three committed workers already resolved
            // IWorkerCompanyScope with GetRequiredService - TaskGenerator, TaskScheduleMatch and
            // TaskEscalation - and nothing registered it. GetRequiredService throws when unregistered,
            // each worker catches inside its own tick loop, so all three failed every cycle forever
            // while appearing to run. Their tests inject a stub company scope, so nothing caught it.
            //
            // This assertion fails against the pre-fix Program.cs.
            Assert.Matches(
                new Regex(@"IWorkerCompanyScope\s*,\s*CrossBuy\.BL\.Platform\.WorkerCompanyScope>"),
                ProgramSource());
        }

        [Fact]
        public void Every_worker_that_resolves_the_company_scope_can_have_it_resolved()
        {
            var root = RepoRoot();
            var program = ProgramSource();

            // Whoever asks for it must be able to get it - including the three that already did.
            var askers = Directory
                .EnumerateFiles(Path.Combine(root, "CrossBuy"), "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("GetRequiredService<IWorkerCompanyScope>", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            Assert.NotEmpty(askers);   // a sweep that matched nothing would pass vacuously
            Assert.Contains("IWorkerCompanyScope", program, StringComparison.Ordinal);
        }

        // ---- enumeration and failure isolation, against the real runner --------------------------------

        [Fact]
        public async Task Every_eligible_company_is_processed_and_none_is_assumed()
        {
            var seen = new List<int>();
            var scope = new StubCompanyScope(new List<int> { 3, 7, 11 });

            var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
                scope, NullLogger.Instance, "test",
                id => { seen.Add(id); return Task.CompletedTask; },
                CancellationToken.None);

            Assert.Equal(new[] { 3, 7, 11 }, seen.ToArray());
            Assert.Equal(3, processed);
            Assert.Equal(0, failed);
        }

        [Fact]
        public async Task One_companys_failure_does_not_stop_or_contaminate_the_others()
        {
            var seen = new List<int>();
            var scope = new StubCompanyScope(new List<int> { 1, 2, 3 });

            var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
                scope, NullLogger.Instance, "test",
                id =>
                {
                    if (id == 2) throw new InvalidOperationException("company 2 is broken");
                    seen.Add(id);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            // 3 still runs after 2 throws, and 2 is counted rather than silently dropped.
            Assert.Equal(new[] { 1, 3 }, seen.ToArray());
            Assert.Equal(2, processed);
            Assert.Equal(1, failed);
        }

        [Fact]
        public async Task No_companies_means_no_work_rather_than_company_one()
        {
            var ran = false;
            var scope = new StubCompanyScope(new List<int>());

            var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
                scope, NullLogger.Instance, "test",
                _ => { ran = true; return Task.CompletedTask; },
                CancellationToken.None);

            // The failure mode being excluded: an empty list must not become "well, company 1 then".
            Assert.False(ran);
            Assert.Equal(0, processed);
            Assert.Equal(0, failed);
        }

        [Fact]
        public async Task Cancellation_stops_the_loop_and_is_not_recorded_as_a_company_failure()
        {
            using var cts = new CancellationTokenSource();
            var seen = new List<int>();
            var scope = new StubCompanyScope(new List<int> { 1, 2, 3 });

            var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
                scope, NullLogger.Instance, "test",
                id => { seen.Add(id); cts.Cancel(); return Task.CompletedTask; },
                cts.Token);

            Assert.Single(seen);
            Assert.Equal(1, processed);
            Assert.Equal(0, failed);   // shutdown is not a defect
        }

        [Fact]
        public void The_company_list_is_read_from_Companies_rather_than_assumed()
        {
            using var host = new PlatformTestHost();
            host.Db.Companies.Add(Company("Alpha"));
            host.Db.Companies.Add(Company("Beta"));
            host.Db.SaveChanges();

            var expected = host.Db.Companies.Select(c => c.CompanyID).OrderBy(id => id).ToList();
            Assert.Equal(2, expected.Count);

            var ids = new WorkerCompanyScope(host.Db).EligibleCompanyIdsAsync().GetAwaiter().GetResult();

            // Both rows come back, in id order - so a second company is visible to every worker that
            // uses this, which is the whole difference from the constant that was here before.
            Assert.Equal(expected, ids);
        }

        private static CrossBuy.Models.Context.Admin.Companies Company(string name) => new()
        {
            CompanyName = name,
            Address = "-",
            PhoneNumber = "-",
            Email = "-",
        };

        // ---- runtime behaviour, not source shape --------------------------------------------------------

        [Fact]
        public async Task A_certification_host_runs_neither_worker_and_touches_neither_gate_nor_scope()
        {
            // The strong form: both collaborators throw. If the suppression check were deleted, this fails
            // with the double's message rather than passing. This is the "gate off means zero business
            // writes" case proved at runtime - no scope is created, so no service that could write is ever
            // resolved.
            var cert = new CertificationRuntimeState(certificationMode: true);

            var integrity = new IntegrityCheckHostedService(new ThrowingScopeFactory(), new ThrowingGate(),
                cert, NullLogger<IntegrityCheckHostedService>.Instance);
            var crm = new CrmReminderHostedService(new ThrowingScopeFactory(), new ThrowingGate(),
                cert, NullLogger<CrmReminderHostedService>.Instance);

            await integrity.StartAsync(CancellationToken.None);
            await integrity.StopAsync(CancellationToken.None);
            await crm.StartAsync(CancellationToken.None);
            await crm.StopAsync(CancellationToken.None);

            Assert.True(integrity.Suppressed);
            Assert.True(crm.Suppressed);
        }

        [Fact]
        public void A_normal_host_does_not_report_itself_suppressed()
        {
            // Guards the other direction: a suppression flag stuck on would silently disable both writers in
            // production, which the test above cannot distinguish from working code.
            var normal = new CertificationRuntimeState(certificationMode: false);

            Assert.False(new IntegrityCheckHostedService(new ThrowingScopeFactory(), new ThrowingGate(),
                normal, NullLogger<IntegrityCheckHostedService>.Instance).Suppressed);
            Assert.False(new CrmReminderHostedService(new ThrowingScopeFactory(), new ThrowingGate(),
                normal, NullLogger<CrmReminderHostedService>.Instance).Suppressed);
        }

        [Theory]
        [InlineData(4)]
        [InlineData(9)]
        public void A_per_company_scope_is_bound_to_that_company_and_no_other(int companyId)
        {
            var recorder = new RecordingContextFactory();
            var provider = new ServiceCollection()
                .AddScoped<IBusinessContextFactory>(_ => recorder)
                .BuildServiceProvider();

            using (WorkerScope.ForCompany(provider.GetRequiredService<IServiceScopeFactory>(), companyId)) { }

            // The company reaches BusinessContext before anything in the scope can run a query - which is
            // what makes the query filters inside it see company `companyId` and nothing else.
            Assert.Equal(new[] { companyId }, recorder.Bound.ToArray());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void An_unresolved_company_fails_closed_instead_of_becoming_company_one(int companyId)
        {
            var provider = new ServiceCollection()
                .AddScoped<IBusinessContextFactory>(_ => new RecordingContextFactory(passThrough: true))
                .BuildServiceProvider();

            // The exact failure this batch exists to prevent: no company means STOP, never "company 1 then".
            Assert.Throws<BusinessContextUnresolvedException>(() =>
                WorkerScope.ForCompany(provider.GetRequiredService<IServiceScopeFactory>(), companyId));
        }

        private sealed class ThrowingScopeFactory : IServiceScopeFactory
        {
            public IServiceScope CreateScope() =>
                throw new InvalidOperationException("a suppressed worker must never create a scope");
        }

        private sealed class ThrowingGate : IWorkerGate
        {
            public Task<bool> WaitUntilAllowedAsync(string workerName, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("a suppressed worker must never reach the worker gate");
        }

        // Records what each scope was bound to. passThrough: use the real guard in BusinessContext.ForWorker
        // so the unresolved-company test proves the PRODUCTION rule, not this double's opinion of it.
        private sealed class RecordingContextFactory : IBusinessContextFactory
        {
            private readonly bool _passThrough;
            public RecordingContextFactory(bool passThrough = false) { _passThrough = passThrough; }
            public List<int> Bound { get; } = new();

            public BusinessContext ForWorker(int companyId, Guid? correlationId = null)
            {
                var context = BusinessContext.ForWorker(companyId, correlationId);   // the real guard
                Bound.Add(companyId);
                return context;
            }

            public BusinessContext ForSystem(int companyId, Guid? correlationId = null)
                => BusinessContext.ForSystem(companyId, correlationId);
            public BusinessContext? ScopeBoundContext => null;
            public Task<BusinessContext> ForHttpAsync(CancellationToken cancellationToken = default)
                => throw new NotSupportedException("a worker scope never resolves from HTTP");
            public Task<BusinessContext?> TryForHttpAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<BusinessContext?>(null);
            public Task<BusinessContext?> ForEmployeeAsync(int employeeId, int? fallbackCompanyIdForOrphanRow = null,
                CancellationToken cancellationToken = default)
                => Task.FromResult<BusinessContext?>(null);
        }

        private sealed class StubCompanyScope : IWorkerCompanyScope
        {
            private readonly List<int> _ids;
            public StubCompanyScope(List<int> ids) { _ids = ids; }
            public Task<List<int>> EligibleCompanyIdsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(_ids);
        }
    }
}
