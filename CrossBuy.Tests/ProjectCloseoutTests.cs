using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // PROJECT CLOSEOUT — closing a project is a decision with evidence, not a status edit.
    //
    // The property every test below defends is that ONE authority answers "may this close". The
    // failure mode being designed out is the ordinary one: a screen computes a list of blockers to
    // display, the close endpoint re-checks a slightly different list, and they agree only until
    // somebody edits one of them. CloseAsync re-runs ReadinessAsync rather than trusting a caller
    // who says the project was ready — the instant that matters is the one in which it closes.
    //
    // Every blocker asserted here is derived from a record that exists on current master. Things this
    // deployment cannot prove are warnings, because a blocker nobody can satisfy is a project that
    // never closes.
    // ============================================================================================
    public class ProjectCloseoutTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;
        private const int Admin = 5;        // holds the Projects module role
        private const int Member = 6;       // on the project, but no module role
        private const int Outsider = 900;   // another company

        // ---- readiness ---------------------------------------------------------------------------

        [Fact]
        public async Task A_clean_project_is_ready_to_close()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Admin));

            var readiness = await svc.ReadinessAsync(h.ProjectA);

            // Without this the suite would be satisfied by a service that blocks everything.
            Assert.True(readiness.IsReady, string.Join(" | ", readiness.BlockingItems.Select(b => b.Code)));
            Assert.Empty(readiness.BlockingItems);
        }

        [Fact]
        public async Task An_unposted_billing_blocks_the_close()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            AddBilling(h, CompanyA, h.ProjectA, ProgressBillingStatuses.Approved);
            var svc = Build(h, Ctx(CompanyA, Admin));

            var readiness = await svc.ReadinessAsync(h.ProjectA);

            // Approved is authorized to post and has NOT posted. Closing over it would strand a billing
            // that still has a financial effect waiting to happen.
            Assert.False(readiness.IsReady);
            Assert.Contains(readiness.BlockingItems, b => b.Code == CloseoutBlockers.UnresolvedBilling);
            Assert.Equal(1, readiness.Financials.UnresolvedBillingCount);
        }

        [Theory]
        [InlineData(ProgressBillingStatuses.Draft)]
        [InlineData(ProgressBillingStatuses.Submitted)]
        [InlineData(ProgressBillingStatuses.Returned)]
        [InlineData(ProgressBillingStatuses.Approved)]
        public async Task Every_pre_posted_status_blocks(string status)
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            AddBilling(h, CompanyA, h.ProjectA, status);
            var svc = Build(h, Ctx(CompanyA, Admin));

            Assert.False((await svc.ReadinessAsync(h.ProjectA)).IsReady);
        }

        [Fact]
        public async Task A_posted_billing_does_not_block_but_says_reversal_is_unavailable()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            AddBilling(h, CompanyA, h.ProjectA, ProgressBillingStatuses.Posted);
            var svc = Build(h, Ctx(CompanyA, Admin));

            var readiness = await svc.ReadinessAsync(h.ProjectA);

            // Posted is the resolved state, so it cannot block. The warning is honest about the gap
            // rather than inventing a blocker nobody could satisfy: reversing a posted billing needs an
            // AR receipt reversal foundation that is not landed.
            Assert.True(readiness.IsReady);
            Assert.Contains(readiness.Warnings, w => w.Code == CloseoutWarnings.ReversalUnavailable);
        }

        [Fact]
        public async Task Retention_the_ledger_still_shows_blocks_the_close()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);

            // From POSTED GL lines tagged with the project, which is what an auditor reads — not from
            // the billing row, so retention released outside progress billing is seen too.
            PostRetention(h, CompanyA, h.ProjectA, 5_000m);
            var svc = Build(h, Ctx(CompanyA, Admin));

            var readiness = await svc.ReadinessAsync(h.ProjectA);

            Assert.False(readiness.IsReady);
            var blocker = Assert.Single(readiness.BlockingItems, b => b.Code == CloseoutBlockers.RetentionOutstanding);
            Assert.Equal(5_000m, blocker.Amount);
        }

        [Fact]
        public async Task An_unrecovered_customer_advance_blocks_the_close()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            PostAdvance(h, CompanyA, h.ProjectA, 2_500m);
            var svc = Build(h, Ctx(CompanyA, Admin));

            var readiness = await svc.ReadinessAsync(h.ProjectA);

            Assert.False(readiness.IsReady);
            Assert.Contains(readiness.BlockingItems, b => b.Code == CloseoutBlockers.AdvanceOutstanding);
        }

        [Fact]
        public async Task An_open_task_on_the_project_blocks_and_a_done_one_does_not()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            AddTask(h, CompanyA, h.ProjectA, "InProgress");
            var svc = Build(h, Ctx(CompanyA, Admin));

            Assert.False((await svc.ReadinessAsync(h.ProjectA)).IsReady);

            // The relation is EntityType/EntityId, the platform's own polymorphic link. Nothing here
            // matches on a title, so a task merely mentioning the project name is not a blocker...
            using var clean = Seed();
            GiveRole(clean, CompanyA, Admin);
            AddTask(clean, CompanyA, clean.ProjectA, "Done");
            AddTask(clean, CompanyA, entityId: clean.ProjectB, status: "InProgress");   // another project
            var svc2 = Build(clean, Ctx(CompanyA, Admin));

            Assert.True((await svc2.ReadinessAsync(clean.ProjectA)).IsReady);
        }

        // ---- authority and isolation --------------------------------------------------------------

        [Fact]
        public async Task Another_companys_project_cannot_be_read_or_closed_with_its_exact_id()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Admin));

            // The exact identifier of company B's project. Not a guess.
            var readiness = await svc.ReadinessAsync(h.ProjectOther);
            Assert.False(readiness.IsReady);
            Assert.Contains(readiness.BlockingItems, b => b.Code == CloseoutBlockers.NotAvailable);

            var (ok, _) = await svc.CloseAsync(h.ProjectOther, "trying anyway");
            Assert.False(ok);
            Assert.Empty(h.Host.Db.Set<ProjectCloseout>().ToList());
        }

        [Fact]
        public async Task A_project_member_without_the_module_role_cannot_close_it()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Member));

            // ProjectsAccessService answers `close` only for the module role and returns FALSE on the
            // membership path. Freezing a project's financial history is administrative, so its own
            // manager cannot do it.
            var (ok, _) = await svc.CloseAsync(h.ProjectA, "I manage this project");
            Assert.False(ok);
            Assert.Empty(h.Host.Db.Set<ProjectCloseout>().ToList());
        }

        [Fact]
        public async Task An_unresolved_context_closes_nothing()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, ctx: null);

            Assert.False((await svc.CloseAsync(h.ProjectA, "no context")).ok);
            Assert.False((await svc.ReadinessAsync(h.ProjectA)).IsReady);
        }

        // ---- closing ------------------------------------------------------------------------------

        [Fact]
        public async Task A_ready_project_closes_and_records_who_why_and_when()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Admin));

            var (ok, err) = await svc.CloseAsync(h.ProjectA, "  handover accepted, final account agreed  ");
            Assert.True(ok, err);

            var row = Assert.Single(h.Host.Db.Set<ProjectCloseout>().ToList());
            Assert.Equal(CompanyA, row.CompanyID);
            Assert.Equal(h.ProjectA, row.ProjectId);
            Assert.Equal(Admin, row.ClosedBy);                      // server identity, never a posted value
            Assert.Equal("handover accepted, final account agreed", row.Reason);   // trimmed
            Assert.True(row.IsCurrent);
            Assert.Null(row.ReopenedAt);

            // The existing status column moves too, so screens that read it see the closed project.
            Assert.Equal("Completed", h.Host.Db.Projects.Single(p => p.ID == h.ProjectA).Status);
        }

        [Fact]
        public async Task A_reason_is_mandatory()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Admin));

            foreach (var empty in new[] { "", "   ", "\t" })
            {
                var (ok, err) = await svc.CloseAsync(h.ProjectA, empty);
                Assert.False(ok);
                Assert.Equal(ProjectCloseoutService.ReasonRequired, err);
            }
            Assert.Empty(h.Host.Db.Set<ProjectCloseout>().ToList());
        }

        [Fact]
        public async Task Closing_twice_is_refused_and_writes_no_second_row()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Admin));

            Assert.True((await svc.CloseAsync(h.ProjectA, "first")).ok);
            Assert.False((await svc.CloseAsync(h.ProjectA, "second")).ok);

            Assert.Single(h.Host.Db.Set<ProjectCloseout>().ToList());
        }

        [Fact]
        public async Task Close_re_evaluates_readiness_rather_than_trusting_the_caller()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Admin));

            // The screen asks, and the answer is yes...
            Assert.True((await svc.ReadinessAsync(h.ProjectA)).IsReady);

            // ...and then something changes before the button is pressed. The close must see that.
            AddBilling(h, CompanyA, h.ProjectA, ProgressBillingStatuses.Draft);

            Assert.False((await svc.CloseAsync(h.ProjectA, "the screen said it was ready")).ok);
            Assert.Empty(h.Host.Db.Set<ProjectCloseout>().ToList());
        }

        // ---- reopening ----------------------------------------------------------------------------

        [Fact]
        public async Task A_reopen_preserves_the_closeout_rather_than_erasing_it()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Admin));

            Assert.True((await svc.CloseAsync(h.ProjectA, "handover accepted")).ok);
            Assert.True((await svc.ReopenAsync(h.ProjectA, "a subcontractor invoice arrived late")).ok);

            // The row survives, carrying BOTH decisions. Nothing was deleted, and the original reason
            // is still readable - which is the whole reason this is a table and not four columns.
            var row = Assert.Single(h.Host.Db.Set<ProjectCloseout>().ToList());
            Assert.Equal("handover accepted", row.Reason);
            Assert.Equal("a subcontractor invoice arrived late", row.ReopenReason);
            Assert.Equal(Admin, row.ReopenedBy);
            Assert.NotNull(row.ReopenedAt);
            Assert.False(row.IsCurrent);

            Assert.Equal("Active", h.Host.Db.Projects.Single(p => p.ID == h.ProjectA).Status);
        }

        [Fact]
        public async Task A_reopen_needs_a_reason_and_a_closed_project()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Admin));

            // Not closed yet.
            Assert.False((await svc.ReopenAsync(h.ProjectA, "why not")).ok);

            Assert.True((await svc.CloseAsync(h.ProjectA, "handover")).ok);
            var (ok, err) = await svc.ReopenAsync(h.ProjectA, "   ");
            Assert.False(ok);
            Assert.Equal(ProjectCloseoutService.ReasonRequired, err);

            // ...and the closeout is untouched by the refused reopen.
            Assert.True(h.Host.Db.Set<ProjectCloseout>().Single().IsCurrent);
        }

        [Fact]
        public async Task Closing_again_after_a_reopen_writes_a_second_row_so_the_history_is_a_sequence()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            var svc = Build(h, Ctx(CompanyA, Admin));

            Assert.True((await svc.CloseAsync(h.ProjectA, "first close")).ok);
            Assert.True((await svc.ReopenAsync(h.ProjectA, "late invoice")).ok);
            Assert.True((await svc.CloseAsync(h.ProjectA, "second close")).ok);

            var history = await svc.HistoryAsync(h.ProjectA);
            Assert.Equal(2, history.Count);
            Assert.Single(history.Where(c => c.IsCurrent));
            Assert.Contains(history, c => c.Reason == "first close");
            Assert.Contains(history, c => c.Reason == "second close");
        }

        [Fact]
        public async Task Accepted_warnings_are_recorded_on_the_closeout()
        {
            using var h = Seed();
            GiveRole(h, CompanyA, Admin);
            AddBilling(h, CompanyA, h.ProjectA, ProgressBillingStatuses.Posted);
            var svc = Build(h, Ctx(CompanyA, Admin));

            Assert.True((await svc.CloseAsync(h.ProjectA, "handover")).ok);

            // A later reader can tell a clean close from one that closed over known warnings.
            var row = Assert.Single(h.Host.Db.Set<ProjectCloseout>().ToList());
            Assert.NotNull(row.ReadinessNote);
            Assert.Contains(CloseoutWarnings.ReversalUnavailable, row.ReadinessNote!, StringComparison.Ordinal);
        }

        // ---- fixture ------------------------------------------------------------------------------

        private sealed class Fixture : IDisposable
        {
            public PlatformTestHost Host { get; init; } = null!;
            public int ProjectA { get; init; }
            public int ProjectB { get; init; }
            public int ProjectOther { get; init; }
            public void Dispose() => Host.Dispose();
        }

        private static IProjectCloseoutService Build(Fixture f, BusinessContext? ctx)
        {
            var db = f.Host.Db;
            var accessor = ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx);
            var access = new ProjectsAccessService(
                db,
                new PlatformRoleDirectory(db, NullLogger<PlatformRoleDirectory>.Instance),
                new AccountingAccessService(
                    db,
                    new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                    accessor,
                    new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                    NullLogger<AccountingAccessService>.Instance),
                NullLogger<ProjectsAccessService>.Instance);

            return new ProjectCloseoutService(db, accessor, access,
                new ContractService(db, new StubJournal()));
        }

        private static BusinessContext Ctx(int companyId, int employeeId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = "u" + employeeId,
            Source = BusinessContextSource.Http,
        };

        private static Fixture Seed()
        {
            // AS COMPANY A. CompanyWriteGuardInterceptor refuses an insert whose CompanyID disagrees
            // with the scope, so a host operating as company 1 cannot seed company A's ledger - and
            // working around that would be working around the guard these tests rely on.
            var host = new PlatformTestHost(CompanyA);

            foreach (var (id, company) in new[] { (Admin, CompanyA), (Member, CompanyA) })
                host.Db.Employee.Add(new Employee
                {
                    ID = id, EmpCompanyID = company, IsActive = true,
                    FirstName = "E" + id, LastName = "T", FullName = "E" + id,
                    Address = "-", PhoneNumber = "-", Email = "-", ProfileImage = "-",
                    Gender = "-", MaritalStatus = "-", UserId = "u" + id,
                });

            var a = new Project { CompanyID = CompanyA, Code = "P-A", Name = "A", NameEn = "A", Status = "Active" };
            var b = new Project { CompanyID = CompanyA, Code = "P-B", Name = "B", NameEn = "B", Status = "Active" };
            host.Db.Projects.AddRange(a, b);
            host.Db.SaveChanges();

            // The foreign project, written through a SECOND context bound to company B.
            //
            // Not by re-pointing this host's holder: CompanyScopeHolder refuses to change company
            // within one scope, on purpose - "two companies must never share one DI scope or DbContext
            // instance". A second context is what the production code would use, so it is what the
            // fixture uses.
            var foreignScope = new CompanyScopeHolder();
            foreignScope.Set(CompanyB, null);
            using var foreignDb = host.NewContext(foreignScope);
            var other = new Project { CompanyID = CompanyB, Code = "P-X", Name = "X", NameEn = "X", Status = "Active" };
            foreignDb.Projects.Add(other);
            foreignDb.SaveChanges();

            return new Fixture { Host = host, ProjectA = a.ID, ProjectB = b.ID, ProjectOther = other.ID };
        }

        private static void GiveRole(Fixture f, int companyId, int employeeId)
        {
            f.Host.Db.Set<CrossBuy.Models.Context.Platform.PlatformRoleAssignment>().Add(new()
            {
                CompanyID = companyId,
                Scope = EntityRegistry.ScopeProjects,
                PrincipalType = CrossBuy.Models.Context.Platform.PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId,
                Role = ProjectsRoles.ProjectsAdministrator,
                IsActive = true,
            });
            f.Host.Db.SaveChanges();
        }

        private static void AddBilling(Fixture f, int companyId, int projectId, string status)
        {
            f.Host.Db.ProgressBillings.Add(new ProgressBilling
            {
                CompanyID = companyId, ProjectId = projectId, Status = status,
                BillingDate = DateTime.UtcNow.Date, GrossWork = 1_000m, NetDue = 900m,
            });
            f.Host.Db.SaveChanges();
        }

        private static void AddTask(Fixture f, int companyId, int entityId, string status)
        {
            f.Host.Db.TaskItems.Add(new TaskItem
            {
                CompanyId = companyId, Title = "work", Status = status,
                EntityType = EntityRegistry.Project, EntityId = entityId,
                AssigneeEmployeeId = Admin, CreatedByEmployeeId = Admin,
            });
            f.Host.Db.SaveChanges();
        }

        private static void PostRetention(Fixture f, int companyId, int projectId, decimal amount)
            => PostToAccount(f, companyId, projectId, ContractService.RetentionAccountCode, debit: amount, credit: 0m);

        private static void PostAdvance(Fixture f, int companyId, int projectId, decimal amount)
            => PostToAccount(f, companyId, projectId, ContractService.AdvanceAccountCode, debit: 0m, credit: amount);

        /// Writes a POSTED journal line tagged with the project, which is exactly what
        /// ContractService.GetSummaryAsync reads. The balance is therefore derived, never asserted.
        private static void PostToAccount(Fixture f, int companyId, int projectId, string code,
            decimal debit, decimal credit)
        {
            var account = f.Host.Db.Accounts.FirstOrDefault(a => a.CompanyID == companyId && a.Code == code);
            if (account == null)
            {
                account = new CrossBuy.Models.Context.Accounting.Account
                { CompanyID = companyId, Code = code, Name = code, NameEn = code };
                f.Host.Db.Accounts.Add(account);
                f.Host.Db.SaveChanges();
            }

            var entry = new CrossBuy.Models.Context.Accounting.JournalEntry
            { CompanyID = companyId, EntryDate = DateTime.UtcNow.Date, Status = "Posted" };
            f.Host.Db.JournalEntries.Add(entry);
            f.Host.Db.SaveChanges();

            f.Host.Db.JournalEntryLines.Add(new CrossBuy.Models.Context.Accounting.JournalEntryLine
            {
                JournalEntryId = entry.ID, AccountId = account.ID, ProjectId = projectId,
                Debit = debit, Credit = credit,
            });
            f.Host.Db.SaveChanges();
        }

        /// Closeout never posts. ContractService needs a journal service to construct; anything that
        /// reached it here would be a defect, so every member throws rather than silently succeeding.
        private sealed class StubJournal : IJournalEntryService
        {
            private static Exception No([System.Runtime.CompilerServices.CallerMemberName] string m = "")
                => new NotSupportedException($"Closeout must not post. IJournalEntryService.{m} was called.");

            public Task<(bool ok, string? error, CrossBuy.Models.Context.Accounting.JournalEntry? entry)>
                CreateDraftAsync(JournalEntryInput input, int? userId) => throw No();

            public Task<(bool ok, string? error)> PostAsync(int entryId, int? userId) => throw No();

            public Task<(bool ok, string? error, CrossBuy.Models.Context.Accounting.JournalEntry? entry)>
                CreateAndPostAsync(JournalEntryInput input, int? userId) => throw No();

            public Task<(bool ok, string? error, CrossBuy.Models.Context.Accounting.JournalEntry? entry)>
                CreateAndPostNoTxAsync(JournalEntryInput input, int? userId) => throw No();

            public Task<(bool ok, string? error, int? reversalId)>
                ReverseAsync(int entryId, int? userId, string? reason) => throw No();

            // These two carry a companyId where PostAsync and ReverseAsync do not - the draft edit and
            // delete paths resolve the tenant themselves rather than trusting a caller that already
            // checked. Closeout reaches neither, so they refuse like the rest.
            public Task<(bool ok, string? error)>
                UpdateDraftAsync(int entryId, int companyId, JournalEntryInput input, int? userId) => throw No();

            public Task<(bool ok, string? error)>
                DeleteDraftAsync(int entryId, int companyId, int? userId) => throw No();
        }
    }
}
