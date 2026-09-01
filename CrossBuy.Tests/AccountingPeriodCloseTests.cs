using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // ACCOUNTING PERIOD CLOSE — the control around a guard that already existed.
    //
    // THE GUARD WAS ALREADY THERE. JournalEntryService.PostAsync resolves the period for the entry's
    // company and date and refuses a closed one, and it is the ONLY writer of JournalEntries in the
    // repository. So posting was already period-controlled everywhere.
    //
    // WHAT WAS NOT THERE, and what these tests hold:
    //
    //   * FiscalPeriodService.SetStatusAsync took NO company. It loaded the period by id alone, so a
    //     chief accountant in company 1 could close company 2's period.
    //   * Nothing recorded who closed a period, when, or why it was reopened.
    //   * Closing was bound to AccPerm("manage") — the ROLE-MANAGEMENT right — so sealing a month
    //     required the power to grant accounting roles.
    //   * SoftClosed was accepted by the setter and honoured by nothing: the guard tested only for
    //     "Closed", so a soft-closed period still took postings.
    // ================================================================================================
    public class AccountingPeriodCloseTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int Chief = 10;
        private const int Clerk = 11;

        private static Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S", UserId = "u" + id,
        };

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static AccountingAccessService Accounting(PlatformTestHost host)
        {
            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));
            return new AccountingAccessService(
                host.Db, http, accessor,
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<AccountingAccessService>.Instance);
        }

        // The reconciliation engine, stubbed. Readiness CONSUMES it rather than re-deriving AR/AP
        // agreement, so the tests control what it reports instead of seeding thirty checks' worth of data.
        private sealed class Integrity : IIntegrityCheckService
        {
            private readonly List<IntegrityCheck> _checks;
            public Integrity(params IntegrityCheck[] checks) => _checks = checks.ToList();
            public Task<List<IntegrityCheck>> RunAsync(int companyId) => Task.FromResult(_checks);
            public Task<(IntegrityCheckRun run, List<IntegrityCheck> checks)> RunAndLogAsync(int a, string b)
                => throw new NotImplementedException("Period readiness only reads; it never logs a run.");
            public Task<List<IntegrityCheckRun>> RecentRunsAsync(int a, int b = 30)
                => throw new NotImplementedException("Period readiness does not read run history.");
        }

        private static AccountingPeriodControlService Control(PlatformTestHost host, params IntegrityCheck[] checks)
            => new(host.Db, Accounting(host), new Integrity(checks));

        private static IntegrityCheck Failing(string key) => new()
        { Key = key, NameAr = key, NameEn = key, Expected = 100m, Actual = 90m, Ok = false };

        // A company with one open period, and (by default) a chief who may control it.
        private static async Task<int> SeedAsync(PlatformTestHost host, int companyId = CompanyOne, bool withChief = true)
        {
            var db = host.Seed;
            if (!await db.Employee.AnyAsync(e => e.ID == Chief)) db.Employee.Add(Emp(Chief, CompanyOne));
            if (!await db.Employee.AnyAsync(e => e.ID == Clerk)) db.Employee.Add(Emp(Clerk, CompanyOne));
            var year = new FiscalYear { CompanyID = companyId, Name = "FY26", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31), Status = "Open" };
            db.FiscalYears.Add(year);
            await db.SaveChangesAsync();
            var period = new FiscalPeriod
            {
                FiscalYearId = year.ID, PeriodNo = 1,
                StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 1, 31),
                Status = AccountingPeriodStatuses.Open,
            };
            db.FiscalPeriods.Add(period);
            if (withChief && !await db.AccountingUserRoles.AnyAsync(r => r.EmployeeId == Chief))
                db.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = Chief, Role = "ChiefAccountant" });
            await db.SaveChangesAsync();
            return period.ID;
        }

        private static async Task<string> StatusAsync(PlatformTestHost host, int periodId) =>
            (await host.Db.FiscalPeriods.AsNoTracking().FirstAsync(p => p.ID == periodId)).Status;

        // ============================================================================================
        // COMPANY ISOLATION — the hole this batch closes
        // ============================================================================================

        [Fact]
        public async Task A_period_belonging_to_another_company_cannot_be_closed()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);                                   // company 1, gives Chief his role
            var theirs = await SeedAsync(host, CompanyTwo, withChief: false);

            // The chief is entitled IN HIS OWN COMPANY. He names their period by its exact id.
            var (ok, err) = await Control(host).CloseAsync(Ctx(Chief, CompanyOne), theirs);

            Assert.False(ok);
            Assert.Equal(AccountingPeriodControlService.Refused, err);
            Assert.Equal(AccountingPeriodStatuses.Open, await StatusAsync(host, theirs));
        }

        [Fact]
        public async Task A_period_in_another_company_answers_exactly_like_one_that_does_not_exist()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);
            var theirs = await SeedAsync(host, CompanyTwo, withChief: false);

            var foreign = await Control(host).CloseAsync(Ctx(Chief, CompanyOne), theirs);
            var missing = await Control(host).CloseAsync(Ctx(Chief, CompanyOne), 999999);

            Assert.Equal(foreign.error, missing.error);              // no probe for other companies' ids
        }

        [Fact]
        public async Task Another_companys_period_state_is_irrelevant_to_this_ones_posting()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var mine = await SeedAsync(host);
            var theirs = await SeedAsync(host, CompanyTwo, withChief: false);

            // Close THEIR period directly in the data.
            var p = await host.Db.FiscalPeriods.FirstAsync(x => x.ID == theirs);
            p.Status = AccountingPeriodStatuses.Closed;
            await host.Db.SaveChangesAsync();

            // Mine is untouched and still open for posting.
            Assert.Equal(AccountingPeriodStatuses.Open, await StatusAsync(host, mine));
            Assert.False(AccountingPeriodStatuses.BlocksPosting(await StatusAsync(host, mine)));
        }

        // ============================================================================================
        // AUTHORITY
        // ============================================================================================

        [Fact]
        public async Task A_clerk_without_the_accounting_role_cannot_close()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);

            var (ok, err) = await Control(host).CloseAsync(Ctx(Clerk, CompanyOne), id);

            Assert.False(ok);
            Assert.Equal(AccountingPeriodControlService.Refused, err);
            Assert.Equal(AccountingPeriodStatuses.Open, await StatusAsync(host, id));
        }

        [Fact]
        public async Task Reopen_needs_stronger_authority_than_close()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            // An Accountant may close; only a ChiefAccountant may reopen.
            host.Seed.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = Clerk, Role = "Accountant" });
            await host.Seed.SaveChangesAsync();

            var ctl = Control(host);
            Assert.True((await ctl.CloseAsync(Ctx(Clerk, CompanyOne), id)).ok);

            var (reopened, err) = await ctl.ReopenAsync(Ctx(Clerk, CompanyOne), id, "correction");
            Assert.False(reopened);
            Assert.Equal(AccountingPeriodControlService.Refused, err);
            Assert.Equal(AccountingPeriodStatuses.Closed, await StatusAsync(host, id));

            // The chief can.
            Assert.True((await ctl.ReopenAsync(Ctx(Chief, CompanyOne), id, "audit adjustment")).ok);
            Assert.Equal(AccountingPeriodStatuses.Open, await StatusAsync(host, id));
        }

        [Fact]
        public async Task An_unconfigured_company_cannot_close_or_reopen_by_bootstrap()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host, withChief: false);        // NO accounting role anywhere

            var ctl = Control(host);
            // Both actions are on the NeverBootstrapOpen list, so an unconfigured company gets nothing.
            Assert.False((await ctl.CloseAsync(Ctx(Chief, CompanyOne), id)).ok);
            Assert.False((await ctl.ReopenAsync(Ctx(Chief, CompanyOne), id, "reason")).ok);
            Assert.Equal(AccountingPeriodStatuses.Open, await StatusAsync(host, id));
        }

        [Fact]
        public void Period_control_is_not_bound_to_the_role_management_permission()
        {
            var raw = File.ReadAllText(RepoFile("CrossBuy", "BL", "AccountingPeriodControlService.cs"));
            // Comments stripped: the file EXPLAINS in prose that closing used to require AccPerm("manage"),
            // and the assertion is about the code, not about the explanation of what was wrong.
            var src = string.Join("\n", raw
                .Replace("\r\n", "\n")
                .Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//")));

            // Closing a month must not require the power to grant accounting roles.
            Assert.Contains("AccountingActions.PeriodClose", src);
            Assert.Contains("AccountingActions.PeriodReopen", src);
            Assert.DoesNotContain("\"manage\"", src);
        }

        // ============================================================================================
        // EVIDENCE AND HISTORY
        // ============================================================================================

        [Fact]
        public async Task Closing_records_who_and_when()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);

            Assert.True((await Control(host).CloseAsync(Ctx(Chief, CompanyOne), id)).ok);

            var p = await host.Db.FiscalPeriods.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal(Chief, p.ClosedBy);
            Assert.NotNull(p.ClosedAt);
        }

        [Fact]
        public async Task Reopening_without_a_reason_is_refused()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            var ctl = Control(host);
            Assert.True((await ctl.CloseAsync(Ctx(Chief, CompanyOne), id)).ok);

            foreach (var blank in new[] { "", "   " })
            {
                var (ok, err) = await ctl.ReopenAsync(Ctx(Chief, CompanyOne), id, blank);
                Assert.False(ok);
                Assert.Equal(AccountingPeriodControlService.ReasonRequired, err);
            }
            Assert.Equal(AccountingPeriodStatuses.Closed, await StatusAsync(host, id));
        }

        [Fact]
        public async Task Reopening_records_the_reason_and_the_whole_story_survives()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            var ctl = Control(host);
            var ctx = Ctx(Chief, CompanyOne);

            Assert.True((await ctl.CloseAsync(ctx, id)).ok);
            Assert.True((await ctl.ReopenAsync(ctx, id, "auditor found a missing accrual")).ok);
            Assert.True((await ctl.CloseAsync(ctx, id)).ok);

            var p = await host.Db.FiscalPeriods.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal("auditor found a missing accrual", p.ReopenReason);
            Assert.Equal(Chief, p.ReopenedBy);

            // Three transitions, in order, as ROWS — not just the latest values on the period.
            var history = await ctl.HistoryAsync(ctx, id);
            Assert.Equal(3, history.Count);
            Assert.Equal(
                new[] { "Open->Closed", "Closed->Open", "Open->Closed" },
                history.OrderBy(h => h.ID).Select(h => $"{h.FromStatus}->{h.ToStatus}").ToArray());
            Assert.All(history, h => Assert.Equal(Chief, h.ActorEmployeeId));
        }

        [Fact]
        public async Task History_does_not_leak_across_companies()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var mine = await SeedAsync(host);
            Assert.True((await Control(host).CloseAsync(Ctx(Chief, CompanyOne), mine)).ok);

            Assert.Empty(await Control(host).HistoryAsync(Ctx(Chief, CompanyTwo), mine));
        }

        // ============================================================================================
        // READINESS
        // ============================================================================================

        [Fact]
        public async Task An_unposted_journal_inside_the_period_blocks_the_close()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            host.Seed.JournalEntries.Add(new JournalEntry
            {
                CompanyID = CompanyOne, EntryDate = new DateTime(2026, 1, 15), Status = "Draft",
                JournalType = "Manual", Description = "d", DescriptionEn = "d",
            });
            await host.Seed.SaveChangesAsync();

            var readiness = await Control(host).ReadinessAsync(Ctx(Chief, CompanyOne), id);
            Assert.False(readiness.Ready);
            Assert.False(readiness.HasBlocking);                     // a draft is a judgement call...
            Assert.Contains(readiness.Warnings, i => i.Code == "unposted-journals" && i.Count == 1);

            // ...so the close is refused, and readiness is a gate rather than a report.
            var (ok, _) = await Control(host).CloseAsync(Ctx(Chief, CompanyOne), id);
            Assert.False(ok);
            Assert.Equal(AccountingPeriodStatuses.Open, await StatusAsync(host, id));

            // ...but it can be overridden EXPLICITLY, and the override is recorded.
            Assert.True((await Control(host).CloseAsync(Ctx(Chief, CompanyOne), id, overrideWarnings: true)).ok);
            var hist = await Control(host).HistoryAsync(Ctx(Chief, CompanyOne), id);
            Assert.Contains("unposted-journals", Assert.Single(hist).Reason);
        }

        [Fact]
        public async Task A_clean_period_reports_ready_and_closes()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);

            var readiness = await Control(host).ReadinessAsync(Ctx(Chief, CompanyOne), id);
            Assert.True(readiness.Ready);
            Assert.Empty(readiness.Issues);
            Assert.True((await Control(host).CloseAsync(Ctx(Chief, CompanyOne), id)).ok);
        }

        [Fact]
        public async Task A_draft_outside_the_period_does_not_block_it()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            host.Seed.JournalEntries.Add(new JournalEntry
            {
                CompanyID = CompanyOne, EntryDate = new DateTime(2026, 6, 15), Status = "Draft",   // June, not January
                JournalType = "Manual", Description = "d", DescriptionEn = "d",
            });
            await host.Seed.SaveChangesAsync();

            Assert.True((await Control(host).ReadinessAsync(Ctx(Chief, CompanyOne), id)).Ready);
        }

        [Fact]
        public async Task Reopening_is_not_gated_on_readiness()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            var ctl = Control(host);
            Assert.True((await ctl.CloseAsync(Ctx(Chief, CompanyOne), id)).ok);

            // A draft appears after the close — exactly the situation a reopen exists for. Blocking the
            // reopen on readiness would trap the very work it is meant to re-admit.
            host.Seed.JournalEntries.Add(new JournalEntry
            {
                CompanyID = CompanyOne, EntryDate = new DateTime(2026, 1, 20), Status = "Draft",
                JournalType = "Manual", Description = "d", DescriptionEn = "d",
            });
            await host.Seed.SaveChangesAsync();

            Assert.True((await ctl.ReopenAsync(Ctx(Chief, CompanyOne), id, "late accrual")).ok);
        }

        // ============================================================================================
        // THE GUARD — one predicate, both closed states
        // ============================================================================================

        [Theory]
        [InlineData("Open", false)]
        [InlineData("SoftClosed", true)]     // was accepted by the setter and honoured by NOTHING
        [InlineData("Closed", true)]
        public void The_posting_guard_answers_for_both_closed_states(string status, bool blocks)
        {
            Assert.Equal(blocks, AccountingPeriodStatuses.BlocksPosting(status));
        }

        [Fact]
        public void The_gl_writer_asks_the_shared_predicate_rather_than_a_literal()
        {
            var src = File.ReadAllText(RepoFile("CrossBuy", "BL", "JournalEntryService.cs"));

            // The guard lives in the platform's ONLY GL writer, so every posting path inherits it. If it
            // tested a literal again, SoftClosed would silently stop meaning anything.
            Assert.Contains("AccountingPeriodStatuses.BlocksPosting(period.Status)", src);
            Assert.DoesNotContain("period.Status == \"Closed\"", src);
        }

        [Fact]
        public void The_guard_is_not_scattered_through_controllers()
        {
            var root = new DirectoryInfo(RepoFile("CrossBuy", "Controllers"));
            var offenders = root.GetFiles("*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f.FullName).Contains("AccountingPeriodStatuses.BlocksPosting"))
                .Select(f => f.Name).ToList();

            // The control belongs in the accounting service layer. A controller repeating it is how the
            // rule drifts.
            Assert.Empty(offenders);
        }

        // ============================================================================================
        // SEVERITY — blocking is not merely a loud warning
        // ============================================================================================

        [Fact]
        public async Task An_out_of_balance_posted_journal_blocks_and_cannot_be_overridden()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            var je = new JournalEntry
            {
                CompanyID = CompanyOne, EntryDate = new DateTime(2026, 1, 10), Status = "Posted",
                JournalType = "Manual", Description = "x", DescriptionEn = "x", FiscalPeriodId = id,
            };
            je.Lines.Add(new JournalEntryLine { LineNo = 1, AccountId = 1, Debit = 100m, Credit = 0m });
            je.Lines.Add(new JournalEntryLine { LineNo = 2, AccountId = 2, Debit = 0m, Credit = 60m });
            host.Seed.JournalEntries.Add(je);
            await host.Seed.SaveChangesAsync();

            var readiness = await Control(host).ReadinessAsync(Ctx(Chief, CompanyOne), id);
            Assert.True(readiness.HasBlocking);
            Assert.Contains(readiness.Blocking, i => i.Code == "out-of-balance");

            // The override exists for WARNINGS. An imbalance frozen into a closed period is not a
            // judgement call, so it refuses even when the caller explicitly asks to override.
            var (ok, _) = await Control(host).CloseAsync(Ctx(Chief, CompanyOne), id, overrideWarnings: true);
            Assert.False(ok);
            Assert.Equal(AccountingPeriodStatuses.Open, await StatusAsync(host, id));
        }

        [Fact]
        public async Task A_reconciliation_failure_surfaces_as_a_warning_from_the_existing_engine()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);

            // IntegrityCheckService is COMPANY-wide, not period-scoped, so a failure cannot be attributed
            // to this period and must not block it - but a finance user closing a month should have to
            // see that the AR subledger disagrees with its control account.
            var ctl = Control(host, Failing("ar_sub"));
            var readiness = await ctl.ReadinessAsync(Ctx(Chief, CompanyOne), id);

            Assert.False(readiness.HasBlocking);
            Assert.Contains(readiness.Warnings, w => w.Code == "integrity:ar_sub");
            Assert.False((await ctl.CloseAsync(Ctx(Chief, CompanyOne), id)).ok);
            Assert.True((await ctl.CloseAsync(Ctx(Chief, CompanyOne), id, overrideWarnings: true)).ok);
        }

        [Fact]
        public async Task Every_exception_carries_a_localization_key_and_somewhere_to_go()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            host.Seed.JournalEntries.Add(new JournalEntry
            {
                CompanyID = CompanyOne, EntryDate = new DateTime(2026, 1, 15), Status = "Draft",
                JournalType = "Manual", Description = "d", DescriptionEn = "d",
            });
            await host.Seed.SaveChangesAsync();

            var readiness = await Control(host, Failing("tb_balanced")).ReadinessAsync(Ctx(Chief, CompanyOne), id);

            Assert.NotEmpty(readiness.Issues);
            Assert.All(readiness.Issues, i =>
            {
                // English prose must never be the only authority for what a finding says.
                Assert.False(string.IsNullOrWhiteSpace(i.MessageKey));
                Assert.False(string.IsNullOrWhiteSpace(i.MessageAr));
                Assert.Contains(i.Severity, PeriodIssueSeverity.All);
                Assert.False(string.IsNullOrWhiteSpace(i.NavigateController));
                Assert.False(string.IsNullOrWhiteSpace(i.NavigateAction));
            });
        }

        // ============================================================================================
        // CLOSED-PERIOD REVERSAL CONTRACT
        // ============================================================================================

        private static async Task AddTodayPeriodAsync(PlatformTestHost host, string status)
        {
            var year = await host.Db.FiscalYears.FirstAsync(y => y.CompanyID == CompanyOne);
            var today = DateTime.UtcNow.Date;
            host.Seed.FiscalPeriods.Add(new FiscalPeriod
            {
                FiscalYearId = year.ID, PeriodNo = 2,
                StartDate = today.AddDays(-5), EndDate = today.AddDays(5), Status = status,
            });
            await host.Seed.SaveChangesAsync();
        }

        [Fact]
        public async Task A_compensating_entry_is_dated_today_and_ignores_the_originals_closed_period()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var januaryId = await SeedAsync(host);
            await AddTodayPeriodAsync(host, AccountingPeriodStatuses.Open);
            Assert.True((await Control(host).CloseAsync(Ctx(Chief, CompanyOne), januaryId)).ok);

            // The original sits in a CLOSED period. Compensation is still allowed - it lands today.
            // Refusing would leave an error found after close permanently uncorrectable.
            var (ok, date, err) = await Control(host).ResolveCompensatingPostingDateAsync(
                Ctx(Chief, CompanyOne), new DateTime(2026, 1, 10));

            Assert.True(ok, err);
            Assert.Equal(DateTime.UtcNow.Date, date);
            Assert.Equal(AccountingPeriodStatuses.Closed, await StatusAsync(host, januaryId));
        }

        [Fact]
        public async Task Compensation_is_refused_when_todays_own_period_is_closed()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);
            await AddTodayPeriodAsync(host, AccountingPeriodStatuses.Closed);

            var (ok, _, err) = await Control(host).ResolveCompensatingPostingDateAsync(
                Ctx(Chief, CompanyOne), new DateTime(2026, 1, 10));

            // Told BEFORE any financial row is written, rather than discovered at the GL.
            Assert.False(ok);
            Assert.NotNull(err);
        }

        [Fact]
        public async Task Compensation_is_refused_when_no_period_covers_today()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await SeedAsync(host);   // only a January 2026 period exists

            var (ok, _, err) = await Control(host).ResolveCompensatingPostingDateAsync(
                Ctx(Chief, CompanyOne), new DateTime(2026, 1, 10));

            Assert.False(ok);
            Assert.NotNull(err);
        }

        // ============================================================================================
        // TRANSACTIONAL BEHAVIOUR
        // ============================================================================================

        [Fact]
        public async Task A_close_that_rolls_back_leaves_the_period_open_and_writes_no_history()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);

            await using (var outer = await ScopedTx.BeginOrJoinAsync(host.Db))
            {
                Assert.True(outer.Owns);
                Assert.True((await Control(host).CloseAsync(Ctx(Chief, CompanyOne), id)).ok);
                // disposed WITHOUT commit - the caller failed
            }

            using var fresh = host.NewContext();
            var p = await fresh.FiscalPeriods.AsNoTracking().FirstAsync(x => x.ID == id);
            Assert.Equal(AccountingPeriodStatuses.Open, p.Status);
            Assert.Null(p.ClosedBy);
            Assert.Empty(await fresh.AccountingPeriodAudits.AsNoTracking()
                .Where(a => a.FiscalPeriodId == id).ToListAsync());
        }

        [Fact]
        public async Task Once_closed_the_state_a_poster_reads_is_the_committed_one()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            Assert.True((await Control(host).CloseAsync(Ctx(Chief, CompanyOne), id)).ok);

            // A separate context - the GL writer's - sees Closed immediately. There is no cached copy
            // for a late post to slip past.
            using var fresh = host.NewContext();
            var status = (await fresh.FiscalPeriods.AsNoTracking().FirstAsync(p => p.ID == id)).Status;
            Assert.True(AccountingPeriodStatuses.BlocksPosting(status));
        }

        // ============================================================================================
        // THE LEGACY BYPASS — closed, and held closed
        //
        // FiscalPeriodService.SetStatusAsync took no company and no actor, loaded the period by id alone,
        // asked no readiness question and wrote no history. Every one of these tests fails if it, or
        // anything like it, comes back.
        // ============================================================================================

        [Fact]
        public void The_period_service_exposes_no_status_mutator_at_all()
        {
            // Reflection, not a source grep: this is a property of the TYPE, so a differently-named
            // resurrection of the same capability is caught too.
            var methods = typeof(IFiscalPeriodService).GetMethods().Select(m => m.Name).ToArray();

            Assert.DoesNotContain("SetStatusAsync", methods);
            Assert.All(methods, m => Assert.DoesNotContain("Status", m));

            // What remains is read-only resolution and listing.
            Assert.Contains("ResolveAsync", methods);
            Assert.Contains("ListAsync", methods);
        }

        [Fact]
        public void Only_the_control_service_moves_a_period_between_posting_states()
        {
            var bl = new DirectoryInfo(RepoFile("CrossBuy", "BL"));
            var controllers = new DirectoryInfo(RepoFile("CrossBuy", "Controllers"));

            var offenders = bl.GetFiles("*.cs", SearchOption.AllDirectories)
                .Concat(controllers.GetFiles("*.cs", SearchOption.AllDirectories))
                .Where(f => f.Name != "AccountingPeriodControlService.cs")
                // The [DevOnly] diagnostic fabricates a closed period to PROVE the guard blocks posting,
                // and is not a business API. It is the one deliberate exception, named here so it cannot
                // grow quietly into a second one.
                .Where(f => f.Name != "DevSeedController.cs")
                .Select(f => new { f.Name, Code = StripComments(File.ReadAllText(f.FullName)) })
                .Where(f => f.Code.Contains(".Status = AccountingPeriodStatuses.")
                         || f.Code.Contains("FiscalPeriods.FirstAsync") && f.Code.Contains(".Status ="))
                .Select(f => f.Name)
                .ToList();

            Assert.True(offenders.Count == 0,
                "Period status is written outside the control service by: " + string.Join(", ", offenders));
        }

        [Fact]
        public void The_live_screen_routes_through_the_governed_service_and_not_a_setter()
        {
            var src = StripComments(File.ReadAllText(RepoFile("CrossBuy", "Controllers", "AccountingController.cs")));

            // The classified live caller (A: legitimate close/reopen) now asks the control service.
            Assert.Contains("SoftCloseAsync(ctx, id)", src);
            Assert.Contains("CloseAsync(ctx, id, overrideWarnings)", src);
            Assert.Contains("ReopenAsync(ctx, id, reason", src);
            Assert.DoesNotContain("_periods.SetStatusAsync", src);

            // And SEALING A MONTH no longer demands the ROLE-MANAGEMENT right. Scoped to this action on
            // purpose: AccPerm("manage") is legitimate elsewhere in this controller - it is what gates
            // role assignment - so a whole-file assertion would be wrong rather than strict.
            var i = src.IndexOf("SetPeriodStatus", StringComparison.Ordinal);
            Assert.True(i > 0);
            var action = src[Math.Max(0, i - 400)..Math.Min(src.Length, i + 1600)];
            Assert.DoesNotContain("AccPerm", action);
            Assert.Contains("AccountingActions.PeriodReopen", action);
            Assert.Contains("AccountingActions.PeriodClose", action);
        }

        [Fact]
        public async Task A_period_cannot_be_closed_without_going_through_readiness_and_history()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            host.Seed.JournalEntries.Add(new JournalEntry
            {
                CompanyID = CompanyOne, EntryDate = new DateTime(2026, 1, 15), Status = "Draft",
                JournalType = "Manual", Description = "d", DescriptionEn = "d",
            });
            await host.Seed.SaveChangesAsync();

            // The ONLY way in refuses, because a warning stands. Before this batch the setter would have
            // sealed the period over it without a word.
            Assert.False((await Control(host).CloseAsync(Ctx(Chief, CompanyOne), id)).ok);
            Assert.Equal(AccountingPeriodStatuses.Open, await StatusAsync(host, id));
            Assert.Empty(await Control(host).HistoryAsync(Ctx(Chief, CompanyOne), id));
        }

        [Fact]
        public async Task A_period_cannot_be_reopened_without_authority_reason_and_history()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var id = await SeedAsync(host);
            var ctl = Control(host);
            Assert.True((await ctl.CloseAsync(Ctx(Chief, CompanyOne), id)).ok);

            // No authority.
            Assert.False((await ctl.ReopenAsync(Ctx(Clerk, CompanyOne), id, "because")).ok);
            // Authority, no reason.
            Assert.False((await ctl.ReopenAsync(Ctx(Chief, CompanyOne), id, "  ")).ok);
            Assert.Equal(AccountingPeriodStatuses.Closed, await StatusAsync(host, id));

            // Only the complete, governed call succeeds - and it leaves evidence.
            Assert.True((await ctl.ReopenAsync(Ctx(Chief, CompanyOne), id, "audit adjustment")).ok);
            Assert.Equal(2, (await ctl.HistoryAsync(Ctx(Chief, CompanyOne), id)).Count);
        }

        private static string StripComments(string source) =>
            string.Join("\n", source.Replace("\r\n", "\n").Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//")));

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }
    }
}
