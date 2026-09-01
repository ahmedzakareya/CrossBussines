using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 0 (Slice-003) — multi-company background processing.
    //
    // Four hosted services each carried `private const int CompanyId = 1`, so on a multi-company install the
    // integrity reconciliation, CRM reminders, auto-task generation and scheduled-task matching all silently
    // processed company 1 and nothing else. The fix is one shared resolver + one shared per-company driver, so
    // these tests target that shared code rather than re-hosting four BackgroundServices.
    public class Slice3WorkerCompanyTests
    {
        // Companies has three non-nullable string columns (Address / PhoneNumber / Email); all are filled so the
        // inserted row is a valid company rather than a minimal stub the schema would reject.
        private static Companies Company(int id, string name) => new()
        {
            CompanyID = id, CompanyName = name, ComoanyNameAr = name, CountryID = 1, CompanyTypeId = 1,
            Address = "-", PhoneNumber = "-", Email = $"company{id}@example.com",
        };

        private static async Task<PlatformTestHost> WithCompaniesAsync(params int[] ids)
        {
            var host = new PlatformTestHost();
            foreach (var id in ids) host.Db.Companies.Add(Company(id, "Company " + id));
            await host.Db.SaveChangesAsync();
            return host;
        }

        // ---- Tests 30 + 31: every eligible company is processed ----
        [Fact]
        public async Task All_companies_are_returned_by_the_shared_scope()
        {
            using var host = await WithCompaniesAsync(1, 2, 7);
            var ids = await new WorkerCompanyScope(host.Db).EligibleCompanyIdsAsync();

            Assert.Equal(new[] { 1, 2, 7 }, ids);   // ordered, distinct, and NOT just company 1
        }

        [Fact]
        public async Task Every_company_is_processed_exactly_once_per_pass()
        {
            using var host = await WithCompaniesAsync(1, 2, 3);
            var seen = new List<int>();

            var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
                new WorkerCompanyScope(host.Db), NullLogger.Instance, "TestWorker",
                companyId => { seen.Add(companyId); return Task.CompletedTask; }, CancellationToken.None);

            Assert.Equal(new[] { 1, 2, 3 }, seen);
            Assert.Equal(3, processed);
            Assert.Equal(0, failed);
        }

        // ---- Test 32: company A failure does not block company B ----
        [Fact]
        public async Task A_failure_in_one_company_does_not_stop_the_others()
        {
            using var host = await WithCompaniesAsync(1, 2, 3, 4);
            var completed = new List<int>();

            var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
                new WorkerCompanyScope(host.Db), NullLogger.Instance, "TestWorker",
                companyId =>
                {
                    // Company 2 throws; 1, 3 and 4 must still run.
                    if (companyId == 2) throw new InvalidOperationException("company 2 is broken");
                    completed.Add(companyId);
                    return Task.CompletedTask;
                }, CancellationToken.None);

            Assert.Equal(new[] { 1, 3, 4 }, completed);
            Assert.Equal(3, processed);
            Assert.Equal(1, failed);
        }

        [Fact]
        public async Task Every_company_can_fail_without_the_pass_throwing()
        {
            using var host = await WithCompaniesAsync(1, 2);
            var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
                new WorkerCompanyScope(host.Db), NullLogger.Instance, "TestWorker",
                _ => throw new InvalidOperationException("all broken"), CancellationToken.None);

            Assert.Equal(0, processed);
            Assert.Equal(2, failed);   // a worker must survive a total failure and try again next tick
        }

        // ---- Test 33: no silent default to company 1 ----
        [Fact]
        public async Task With_no_companies_nothing_is_processed_and_company_1_is_not_assumed()
        {
            using var host = new PlatformTestHost();   // deliberately no Companies rows
            var seen = new List<int>();

            var (processed, failed) = await WorkerCompanyRunner.ForEachCompanyAsync(
                new WorkerCompanyScope(host.Db), NullLogger.Instance, "TestWorker",
                companyId => { seen.Add(companyId); return Task.CompletedTask; }, CancellationToken.None);

            Assert.Empty(seen);          // the old behaviour would have run company 1 regardless
            Assert.Equal(0, processed);
            Assert.Equal(0, failed);
        }

        [Fact]
        public async Task A_company_that_is_not_company_1_is_processed_when_it_is_the_only_one()
        {
            // Directly contradicts the old `const int CompanyId = 1`: an install whose only company is 7 must
            // process 7, not 1.
            using var host = await WithCompaniesAsync(7);
            var seen = new List<int>();
            await WorkerCompanyRunner.ForEachCompanyAsync(
                new WorkerCompanyScope(host.Db), NullLogger.Instance, "TestWorker",
                companyId => { seen.Add(companyId); return Task.CompletedTask; }, CancellationToken.None);

            Assert.Equal(new[] { 7 }, seen);
        }

        [Fact]
        public async Task Cancellation_stops_the_pass_between_companies()
        {
            using var host = await WithCompaniesAsync(1, 2, 3);
            using var cts = new CancellationTokenSource();
            var seen = new List<int>();

            await WorkerCompanyRunner.ForEachCompanyAsync(
                new WorkerCompanyScope(host.Db), NullLogger.Instance, "TestWorker",
                companyId =>
                {
                    seen.Add(companyId);
                    cts.Cancel();   // shutdown requested while working the first company
                    return Task.CompletedTask;
                }, cts.Token);

            Assert.Single(seen);   // the remaining companies are left for the next run, not half-processed
        }

        [Fact]
        public async Task Shutdown_during_a_company_propagates_rather_than_being_logged_as_a_company_failure()
        {
            using var host = await WithCompaniesAsync(1, 2);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                WorkerCompanyRunner.ForEachCompanyAsync(
                    new WorkerCompanyScope(host.Db), NullLogger.Instance, "TestWorker",
                    _ => throw new OperationCanceledException(cts.Token), cts.Token));
        }

        [Fact]
        public void No_hosted_service_still_hard_codes_a_company_id()
        {
            // Regression guard on the actual source: the four workers must not reintroduce the constant.
            var root = FindRepoRoot();
            foreach (var worker in new[]
            {
                "IntegrityCheckHostedService", "CrmReminderHostedService",
                "TaskGeneratorHostedService", "TaskScheduleMatchHostedService",
            })
            {
                var path = Path.Combine(root, "CrossBuy", "BL", worker + ".cs");
                Assert.True(File.Exists(path), path);
                // COMMENTS STRIPPED FIRST. Two of these workers carry a header explaining the
                // constant that USED to be here, and a raw scan matched that explanation - so the
                // guard failed on the documentation of the fix rather than on a regression. A
                // guardrail that punishes the comment teaches people to delete the comment.
                var text = StripComments(File.ReadAllText(path));
                Assert.DoesNotContain("const int CompanyId = 1", text);
                Assert.Contains("WorkerCompanyRunner.ForEachCompanyAsync", text);
            }
        }

        /// Source with its prose removed, so the scan judges code.
        private static string StripComments(string src)
        {
            src = System.Text.RegularExpressions.Regex.Replace(src, @"/\*.*?\*/", " ",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(src, @"//.*?$", " ",
                System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        internal static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("CrossBuy.sln not found above " + AppContext.BaseDirectory);
        }
    }
}
