using System.Text.RegularExpressions;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // PROJECT BILLING — SEPARATION OF DUTIES: STRUCTURAL REGRESSIONS.
    //
    // This file began life as a CHARACTERISATION of the old insecure behaviour, written to give the
    // batch a factual starting point. Batch 1 closed those defects, so every assertion here is now
    // inverted: each one holds a specific defect shut rather than recording that it exists.
    //
    // These are the STRUCTURAL proofs - the shape of the code that made the defect possible. The
    // behavioural proofs (rollback, retry, preparer-cannot-approve, the state machine) live in
    // ProjectsBillingWorkflowTests, which exercises the service against a real database.
    //
    // Two assertions deliberately still describe an ABSENCE: reversal and business events are carried
    // to Product Batch 2, and are marked so that batch breaks them on purpose.
    // ================================================================================================
    public class ProjectsBillingSodTests
    {
        private const int CompanyOne = 1;

        private static Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static ProjectsAccessService Projects(PlatformTestHost host)
        {
            var http = new Microsoft.AspNetCore.Http.HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));
            var accounting = new AccountingAccessService(
                host.Db, http, accessor,
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<AccountingAccessService>.Instance);
            return new ProjectsAccessService(
                host.Db, new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                accounting, NullLogger<ProjectsAccessService>.Instance);
        }

        private static async Task GrantAsync(PlatformTestHost host, string scope, int employeeId, string role)
        {
            host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment
            {
                CompanyID = CompanyOne, Scope = scope, PrincipalType = PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
            });
            await host.Seed.SaveChangesAsync();
        }

        // ============================================================================================
        // THE ACTION VOCABULARY
        // ============================================================================================

        [Fact]
        public async Task The_three_lifecycle_steps_now_ask_for_three_distinct_rights()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            host.Seed.Projects.Add(new Project { ID = 5, CompanyID = CompanyOne, Code = "P5", Name = "p5", NameEn = "p5" });
            host.Seed.AccountingUserRoles.Add(new AccountingUserRole
            { CompanyID = CompanyOne, EmployeeId = 10, Role = "Accountant" });
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsFinance);

            var projects = Projects(host);
            var target = PermissionTarget.ForProject(5);

            // A billing user still holds all three at MODULE level, and that is intended: the separation
            // is not expressible as a role rule. It is the RECORD-level preparer check in
            // ProgressBillingService that stops this same actor approving what they prepared - proved in
            // ProjectsBillingWorkflowTests.The_preparer_cannot_approve_their_own_billing.
            Assert.True(await projects.CanAsync(Ctx(10, CompanyOne), ProjectsActions.BillingPrepare, target));
            Assert.True(await projects.CanAsync(Ctx(10, CompanyOne), ProjectsActions.BillingApprove, target));
            Assert.True(await projects.CanAsync(Ctx(10, CompanyOne), ProjectsActions.BillingPost, target));
        }

        [Fact]
        public async Task Posting_is_refused_without_the_accounting_right_even_with_every_projects_role()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            host.Seed.Projects.Add(new Project { ID = 5, CompanyID = CompanyOne, Code = "P5", Name = "p5", NameEn = "p5" });
            // An accounting role exists for SOMEBODY ELSE, which closes accounting's bootstrap for this
            // company; employee 10 holds none, so accounting refuses "post" for them.
            host.Seed.AccountingUserRoles.Add(new AccountingUserRole
            { CompanyID = CompanyOne, EmployeeId = 999, Role = "ChiefAccountant" });
            await host.Seed.SaveChangesAsync();
            await GrantAsync(host, EntityRegistry.ScopeProjects, 10, ProjectsRoles.ProjectsAdministrator);

            var projects = Projects(host);
            var target = PermissionTarget.ForProject(5);

            // Prepare and approve are still available - they create no ledger effect...
            Assert.True(await projects.CanAsync(Ctx(10, CompanyOne), ProjectsActions.BillingApprove, target));
            // ...and posting is refused, because that boundary was preserved by the split, not relaxed.
            Assert.False(await projects.CanAsync(Ctx(10, CompanyOne), ProjectsActions.BillingPost, target));
        }

        [Fact]
        public void The_three_billing_actions_each_name_their_own_capability()
        {
            var src = ControllerSource();

            // When these shared one right, holding it once was holding it for the whole lifecycle.
            Assert.Contains("ProjectsActions.BillingPrepare", GateFor(src, "SaveBilling"));
            Assert.Contains("ProjectsActions.BillingApprove", GateFor(src, "ApproveBilling"));
            Assert.Contains("ProjectsActions.BillingPost", GateFor(src, "PostBilling"));
            Assert.Contains("ProjectsActions.BillingPrepare", GateFor(src, "SubmitBilling"));
            Assert.Contains("ProjectsActions.BillingApprove", GateFor(src, "ReturnBilling"));
        }

        [Fact]
        public void Posting_still_requires_the_accounting_right_and_approving_does_not()
        {
            var access = File.ReadAllText(RepoFile("CrossBuy", "BL", "ProjectsAccessService.cs"));

            // Posting keeps the accounting requirement, which is NeverBootstrapOpen - so no projects-side
            // bootstrap can imply a path to the ledger.
            var postBranch = Slice(access, "if (action == ProjectsActions.BillingPost)", "// ---- preparing");
            Assert.Contains("_accounting.CanAsync(context, \"post\"", postBranch);

            // Preparing and approving must NOT demand it, or approval would require ledger rights and the
            // split would have bought nothing.
            var prepBranch = Slice(access, "if (action is ProjectsActions.BillingPrepare", "if (action == ProjectsActions.Billing)");
            Assert.DoesNotContain("_accounting.CanAsync", prepBranch);
        }

        // ============================================================================================
        // ACTOR EVIDENCE
        // ============================================================================================

        [Fact]
        public void The_billing_header_records_every_lifecycle_actor()
        {
            var entity = File.ReadAllText(RepoFile("CrossBuy", "Models", "Context", "Accounting", "ProgressBilling.cs"));

            foreach (var col in new[] { "CreatedBy", "UpdatedBy", "SubmittedBy", "ApprovedBy", "PostedBy" })
                Assert.Contains($"public int? {col}", entity);
            foreach (var col in new[] { "CreatedAt", "UpdatedAt", "SubmittedAt", "ApprovedAt", "PostedAt" })
                Assert.Contains($"public DateTime? {col}", entity);
        }

        [Fact]
        public void Every_mutating_billing_method_requires_an_actor()
        {
            var service = File.ReadAllText(RepoFile("CrossBuy", "BL", "ProgressBillingService.cs"));

            // The signatures are the fix: an actor must be supplied to move a billing at all, and it is
            // an int rather than an int? so "no actor" is not expressible.
            Assert.Contains("SubmitAsync(int companyId, int id, int actorEmployeeId);", service);
            Assert.Contains("ApproveAsync(int companyId, int id, int actorEmployeeId);", service);
            Assert.Contains("ReturnAsync(int companyId, int id, int actorEmployeeId);", service);
            Assert.Contains("PostAsync(int companyId, int id, int actorEmployeeId);", service);
            Assert.Contains("string? note, int actorEmployeeId);", service);
        }

        [Fact]
        public void No_billing_call_site_passes_a_null_actor()
        {
            var src = ControllerSource();

            // The actor comes from the gate, which resolved it from the authenticated BusinessContext -
            // never from the request, so there is no posted field it could ride in on.
            Assert.DoesNotMatch(@"_billing\.\w+Async\([^)]*,\s*null\s*\)", src);
            Assert.Contains("gate.ActorEmployeeId", src);
        }

        [Fact]
        public void The_gate_refuses_when_no_employee_can_be_resolved()
        {
            var src = ControllerSource();

            // Rather than recording a null or zero actor on a financial record.
            Assert.Contains("if (ctx.EmployeeId is not > 0) return new ProjectGate { Denied = Deny() };", src);
        }

        // ============================================================================================
        // ATOMIC POSTING
        // ============================================================================================

        [Fact]
        public void Posting_is_one_transaction_and_takes_a_row_lock()
        {
            var service = File.ReadAllText(RepoFile("CrossBuy", "BL", "ProgressBillingService.cs"));

            // One transaction: the nested AR calls JOIN it rather than owning their own, so the invoice,
            // the settlement receipts, the back-references and the Posted state share one fate.
            Assert.Contains("ScopedTx.BeginOrJoinAsync(_db)", service);
            Assert.Contains("await tx.CommitAsync();", service);

            // And concurrent posts are serialized by a real row lock on SQL Server.
            Assert.Contains("UPDLOCK", service);
        }

        [Fact]
        public void The_state_machine_is_enforced_in_the_service_not_only_the_view()
        {
            var service = File.ReadAllText(RepoFile("CrossBuy", "BL", "ProgressBillingService.cs"));

            // A hidden button is not a control: every transition asks the transition table.
            Assert.Contains("ProgressBillingStatuses.CanMove(hdr.Status", service);
            Assert.Contains("ProgressBillingStatuses.IsEditable(hdr.Status)", service);
        }

        // ============================================================================================
        // CARRIED TO PRODUCT BATCH 2 — these two still describe an ABSENCE, on purpose.
        // ============================================================================================

        [Fact]
        public void Reversal_is_still_absent_and_is_carried_to_batch_2()
        {
            var service = File.ReadAllText(RepoFile("CrossBuy", "BL", "ProgressBillingService.cs"));

            // Batch 2 is expected to BREAK this deliberately, using JournalEntryService.ReverseAsync plus
            // the matching AR reversal - never a bare JE reversal that leaves the business state behind.
            Assert.DoesNotContain("ReverseAsync", service);
            Assert.Empty(ProgressBillingStatuses.LegalNext[ProgressBillingStatuses.Posted]);
        }

        [Fact]
        public void The_general_ledger_can_reverse_even_though_billing_does_not_yet_ask_it_to()
        {
            var je = File.ReadAllText(RepoFile("CrossBuy", "BL", "JournalEntryService.cs"));
            Assert.Contains("ReverseAsync", je);
        }

        [Fact]
        public void Every_lifecycle_transition_publishes_through_the_canonical_event_backbone()
        {
            var service = File.ReadAllText(RepoFile("CrossBuy", "BL", "ProgressBillingService.cs"));

            // Batch 2 broke the Batch-1 marker deliberately: the four transitions now emit through the
            // platform's own IBusinessEventService, and no billing-specific event engine was added.
            Assert.Contains("IBusinessEventService", service);
            Assert.Contains("EntityRegistry.ProjectBilling", service);
            foreach (var action in new[] { "Submitted", "Returned", "Approved", "Posted" })
                Assert.Contains($"ProgressBillingEvents.{action}", service);

            // The dedup key is the transition itself, which is what makes a retry idempotent in the
            // permanent event log rather than merely harmless in the database.
            Assert.Contains("DedupKey", service);
        }

        // ---- helpers -------------------------------------------------------------------------------

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var path = Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), $"not found: {path}");
            return path;
        }

        private static string ControllerSource()
            => File.ReadAllText(RepoFile("CrossBuy", "Controllers", "ProjectController.cs"));

        private static string GateFor(string source, string action)
        {
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var i = Array.FindIndex(lines, l =>
                Regex.IsMatch(l, $@"public\s+async\s+Task<IActionResult>\s+{action}\s*\("));
            Assert.True(i >= 0, $"{action} not found");
            return string.Join("\n", lines.Skip(i).Take(10));
        }

        private static string Slice(string source, string from, string to)
        {
            var a = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(a >= 0, $"anchor not found: {from}");
            var b = source.IndexOf(to, a, StringComparison.Ordinal);
            return b > a ? source[a..b] : source[a..];
        }
    }
}
