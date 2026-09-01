using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // INVENTORY APPROVALS — the company is resolved, and an unresolved one is an ERROR.
    //
    // InventoryApprovalService carried a hardcoded company across twelve call sites. Five of them
    // WRITE: the approval row, and then, on approval, a purchase order, a stock transfer, a stock
    // count and a write-off. The service is registered in Program.cs and injected into
    // InventoryController, so a manager in company 41 who approved a write-off created it in
    // company 1's stock and ledger. Not a fixture — a production HTTP path.
    //
    // Half the class had already been migrated: ApprovalInboxAsync takes a BusinessContext. That is
    // what a half-finished migration looks like, and it is why the remaining half was invisible —
    // the file *looked* company-aware.
    //
    // THE RULE THESE TESTS HOLD: an unknown company is an error, never company 1. Reads answer
    // empty; writes refuse. Neither may fall through to somebody else's tenant.
    // =============================================================================================
    public class InventoryApprovalCompanyResolutionTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        /// Answers whatever the test says the request resolved to — including "nothing", which is the
        /// case the old constant hid.
        private sealed class ScriptedCompany : IRequestCompanyResolver
        {
            private readonly int _companyId;
            private readonly bool _ok;
            public ScriptedCompany(int companyId, bool ok) { _companyId = companyId; _ok = ok; }

            public Task<CompanyResolution> ResolveAsync(
                int? requestSuppliedCompanyId = null, CancellationToken cancellationToken = default)
                => Task.FromResult(_ok
                    ? CompanyResolution.Resolved(_companyId, employeeId: 9, branchId: null)
                    : CompanyResolution.Unresolved("no signed-in employee"));
        }

        /// Stock and procurement are never reached on a refused path, so they stay null: a stub would
        /// imply the path may call them, and a NullReferenceException is a louder failure than a stub
        /// quietly absorbing a cross-tenant write.
        private static InventoryApprovalService Service(CrossDbContext db, int companyId, bool resolves = true)
            => new(db, null!, null!, new NoopNotifications(), new ScriptedCompany(companyId, resolves));

        /// The same shape ApprovalInboxAggregatorTests already uses. Notifications are irrelevant to
        /// company resolution and a real one would only add noise.
        private sealed class NoopNotifications : INotificationService
        {
            public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
                string? bodyAr, string? bodyEn, string type, int? refId = null,
                string? url = null, int? companyId = null, int? actorEmployeeId = null,
                string? priority = null, string? category = null, string? dedupKey = null,
                DateTime? expiresAt = null, string? icon = null,
                string? entityType = null, int? entityId = null) => Task.CompletedTask;

            public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
                string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
                string type, int? refId = null, int? exceptEmployeeId = null) => Task.FromResult(0);
        }

        // -----------------------------------------------------------------------------------------
        // WRITES REFUSE
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task An_unresolved_company_cannot_file_an_approval()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);

            var id = await Service(host.Db, CompanyA, resolves: false)
                .SubmitAsync("WriteOff", 5000m, new WriteOffApprovalPayload(), requestedBy: 9);

            Assert.Equal(0, id);
            Assert.Empty(host.Db.InventoryApprovals);
        }

        [Fact]
        public async Task An_unresolved_company_refuses_to_approve_rather_than_acting_as_company_one()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var seeded = Seed(host, CompanyA);

            var (ok, error) = await Service(host.Db, CompanyA, resolves: false)
                .ApproveAsync(seeded, approverEmp: 9, note: null);

            Assert.False(ok);
            Assert.NotNull(error);

            // and the row is untouched
            Assert.Equal("Pending", host.Db.InventoryApprovals.Find(seeded)!.Status);
        }

        [Fact]
        public async Task An_unresolved_company_is_told_approval_IS_required_rather_than_waved_through()
        {
            // The order of the two answers matters. If RequiresApprovalAsync said "no approval needed"
            // for an unresolved caller, the document would post straight past the control instead of
            // stopping at it.
            using var host = new PlatformTestHost(companyId: CompanyA);

            Assert.True(await Service(host.Db, CompanyA, resolves: false).RequiresApprovalAsync(999_999m));
        }

        // -----------------------------------------------------------------------------------------
        // READS ARE SCOPED, AND EMPTY IS NOT COMPANY 1
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task An_unresolved_company_reads_nothing_instead_of_company_ones_queue()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            Seed(host, 1);   // the tenant the constant used to name

            var service = Service(host.Db, CompanyA, resolves: false);

            Assert.Empty(await service.PendingAsync());
            Assert.Empty(await service.RecentAsync());
        }

        [Fact]
        public async Task A_resolved_company_sees_its_OWN_queue_and_not_another_tenants()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var mine = Seed(host, CompanyA);
            Seed(host, CompanyB);
            Seed(host, 1);

            var pending = await Service(host.Db, CompanyA).PendingAsync();

            Assert.Equal(mine, Assert.Single(pending).ID);
        }

        [Fact]
        public async Task Another_companys_approval_id_is_not_found_rather_than_refused_differently()
        {
            // A different refusal for "exists but not yours" would turn the endpoint into a probe for
            // what other tenants hold.
            using var host = new PlatformTestHost(companyId: CompanyA);
            var theirs = Seed(host, CompanyB);

            var (ok, _) = await Service(host.Db, CompanyA).ApproveAsync(theirs, approverEmp: 9, note: null);

            Assert.False(ok);
            Assert.Equal("Pending", host.Db.InventoryApprovals.Find(theirs)!.Status);
        }

        [Fact]
        public async Task A_filed_approval_carries_the_RESOLVED_company_and_not_company_one()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);

            var id = await Service(host.Db, CompanyA)
                .SubmitAsync("WriteOff", 5000m, new WriteOffApprovalPayload(), requestedBy: 9);

            Assert.True(id > 0);
            Assert.Equal(CompanyA, host.Db.InventoryApprovals.Find(id)!.CompanyID);
        }

        // -----------------------------------------------------------------------------------------
        // THE MUTATION GUARD
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_service_declares_no_company_constant_of_its_own()
        {
            // Reading the source, comments stripped, because the point is that no future edit may put
            // the constant back — and because scanning raw text would fail on the header that explains
            // why it was removed.
            var path = System.IO.Path.Combine(RepoRoot(), "CrossBuy", "BL", "InventoryApprovalService.cs");
            var code = System.Text.RegularExpressions.Regex.Replace(
                System.IO.File.ReadAllText(path), @"//.*?$", " ",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            Assert.DoesNotMatch(@"const\s+int\s+\w*CompanyI[dD]\s*=\s*\d", code);
            Assert.Contains("_company.ResolveAsync()", code, StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static int Seed(PlatformTestHost host, int companyId)
        {
            var ap = new InventoryApproval
            {
                CompanyID = companyId, DocType = "WriteOff", Amount = 5000m,
                PayloadJson = "{}", Status = "Pending", RequestedByEmployeeId = 9,
                RequestedAt = DateTime.UtcNow,
            };
            host.Seed.InventoryApprovals.Add(ap);
            host.Seed.SaveChanges();
            return ap.ID;
        }
    }
}
