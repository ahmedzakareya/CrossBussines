using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // INVENTORY APPROVAL — THE SERVICE AS ITS OWN SECURITY BOUNDARY.
    //
    // ApproveAsync and RejectAsync enforced company, Pending state and separation of duties, and asked
    // NOBODY whether the caller may approve anything at all. The only permission check was the
    // [InvPerm("manage")] attribute on the controller — which means the authoritative service was not a
    // boundary but an assumption. Any other caller reaching the same method reached the same
    // consequence: a purchase order created, stock transferred, an adjustment posted.
    //
    // THE TEST THAT MATTERS IS THE PAIR. A service that refuses everybody would pass every negative
    // below, so each denial is written beside the identical call that succeeds once the caller holds
    // the right. The only difference between the two is one InventoryUserRoles row.
    //
    // THE AUTHORITY IS THE REAL ONE. These tests build InventoryAccessService, not a permissive double,
    // and it answers "manage" from the same InventoryManager role the controller attribute consults —
    // so this proves the chain, not a mock's opinion of it.
    //
    // COMPANIES ARE 41 AND 77, NEVER 1.
    // ================================================================================================
    public class InventoryApprovalServiceAuthorizationTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;
        private const int Requester = 5;
        private const int Manager = 6;
        private const int Nobody = 7;

        // ------------------------------------------------------------------------------------------
        private sealed class Ctx : IBusinessContextAccessor
        {
            private readonly BusinessContext? _c;
            public Ctx(int? companyId, int? employeeId)
                => _c = companyId is int co
                    ? new BusinessContext
                    {
                        CompanyId = co, EmployeeId = employeeId, UserId = "u",
                        Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
                        Source = BusinessContextSource.Http,
                    }
                    : null;
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default)
                => _c != null ? Task.FromResult(_c) : throw new BusinessContextUnresolvedException("no company");
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
        }

        // The stubs below are copied verbatim from InventoryApprovalReplayCompanyTests, which already
        // implements these three interfaces correctly. Re-deriving them by hand produced a dozen
        // signature mismatches and taught nothing; RecordingStock throws on every method the approval
        // path has no business calling, which is the assertion that matters here.
        private sealed class RecordingStock : IStockService
        {
            public readonly List<(string Op, int CompanyId)> Executed = new();

            public Task<(bool ok, string? error, StockTransfer? transfer)> TransferAsync(int companyId, int fromWarehouseId, int toWarehouseId, DateTime date, string? notes, List<TransferLineInput> lines, string? userId)
            { Executed.Add(("StockTransfer", companyId)); return Task.FromResult<(bool, string?, StockTransfer?)>((true, null, new StockTransfer { TransferNo = "TR-1" })); }

            public Task<(bool ok, string? error, StockCount? count)> PostCountAsync(int companyId, int warehouseId, DateTime date, string? notes, List<CountLineInput> lines, string? userId)
            { Executed.Add(("StockCount", companyId)); return Task.FromResult<(bool, string?, StockCount?)>((true, null, new StockCount { CountNo = "CN-1" })); }

            public Task<(bool ok, string? error, string? docNo, int? docId, string mode)> WriteOffAsync(int companyId, int warehouseId, DateTime date, string? reason, string? notes, List<WriteOffLineInput> lines, string? userId)
            { Executed.Add(("WriteOff", companyId)); return Task.FromResult<(bool, string?, string?, int?, string)>((true, null, "WO-1", 1, "posted")); }

            private static Task<T> No<T>() => throw new InvalidOperationException(
                "the approval replay path reached a stock method it has no business calling");

            public Task<(bool ok, string? error, LandedCost? landed)> PostLandedCostAsync(int companyId, int goodsReceiptId, DateTime date, string allocationMethod, List<LandedChargeInput> charges, string? notes, string? userId) => No<(bool, string?, LandedCost?)>();
            public Task<(bool ok, string? error, int? jeId, decimal total)> PostOpeningStockAsync(int companyId, DateTime cutoff, List<OpeningStockLineInput> lines, string? userId) => No<(bool, string?, int?, decimal)>();
            public Task<(bool ok, string? error, int? assetId, decimal cost)> CapitalizeFromStockAsync(int companyId, int itemId, int warehouseId, decimal qty, DateTime date, int? costCenterId, string? userId) => No<(bool, string?, int?, decimal)>();
            public Task<(bool ok, string? error, StockMovement? movement)> PostMovementAsync(int companyId, MovementRequest req, string? userId) => No<(bool, string?, StockMovement?)>();
            public Task<(decimal qty, decimal value, decimal avg)> GetBalanceAsync(int companyId, int itemId, int warehouseId) => No<(decimal, decimal, decimal)>();
            public Task<List<StockBalance>> GetBalancesAsync(int companyId, int? warehouseId = null) => No<List<StockBalance>>();
            public Task<(List<StockBalanceRow> rows, int total, decimal grandValue)> SearchBalancesAsync(int companyId, string? q, int? warehouseId, bool onlyInStock, int page, int pageSize) => No<(List<StockBalanceRow>, int, decimal)>();
            public Task<List<StockMovement>> GetMovementsAsync(int companyId, int? itemId = null, int? warehouseId = null, int take = 300) => No<List<StockMovement>>();
            public Task<(bool ok, string? error, StockMovement? produced)> AssembleAsync(int companyId, int assemblyItemId, int warehouseId, decimal qty, DateTime date, bool disassemble, string? userId) => No<(bool, string?, StockMovement?)>();
            public Task<(bool ok, string? error)> ReleaseWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId) => No<(bool, string?)>();
            public Task<(bool ok, string? error, decimal unitCost)> CompleteWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId) => No<(bool, string?, decimal)>();
            public Task<(bool ok, string? error, decimal produced)> ProducePartialAsync(int companyId, int workOrderId, decimal qty, decimal stdUnitCost, bool finalize, DateTime date, string? userId) => No<(bool, string?, decimal)>();
            public Task<(bool ok, string? error)> CancelWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId) => No<(bool, string?)>();
            public Task<(bool ok, string? error, int laborId)> AddWorkOrderLaborAsync(int companyId, int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal ratePerHour, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, DateTime date, string? userId, string? workerNameEn = null) => No<(bool, string?, int)>();
            public Task<(bool ok, string? error)> RemoveWorkOrderLaborAsync(int companyId, int laborId, DateTime date, string? userId) => No<(bool, string?)>();
            public Task<List<BinStock>> GetBinStocksAsync(int companyId, int warehouseId) => No<List<BinStock>>();
            public Task<(bool ok, string? error)> RelocateBinAsync(int companyId, int warehouseId, int itemId, int fromBinId, int toBinId, decimal qty, string? userId) => No<(bool, string?)>();
            public Task<(bool ok, string? error)> SetBinCountAsync(int companyId, int warehouseId, int binLocationId, int itemId, decimal countedQty, string? userId) => No<(bool, string?)>();
            public Task<(bool ok, string? error, int rows)> InitializeBinStockFromDefaultsAsync(int companyId, int warehouseId, string? userId) => No<(bool, string?, int)>();
        }

        private sealed class RecordingProcurement : IProcurementService
        {
            public readonly List<(string Op, int CompanyId)> Executed = new();

            public Task<(bool ok, string? error, PurchaseOrder? po)> CreatePurchaseOrderAsync(int companyId, int vendorId, int? warehouseId, DateTime date, DateTime? expected, string? notes, List<PoLineInput> lines, string? userId, int? projectId = null)
            { Executed.Add(("PurchaseOrder", companyId)); return Task.FromResult<(bool, string?, PurchaseOrder?)>((true, null, new PurchaseOrder { OrderNo = "PO-1" })); }

            private static Task<T> No<T>() => throw new InvalidOperationException(
                "the approval replay path reached a procurement method it has no business calling");

            public Task<List<PurchaseOrder>> GetPurchaseOrdersAsync(int companyId) => No<List<PurchaseOrder>>();
            public Task<PurchaseOrder?> GetPurchaseOrderAsync(int companyId, int id) => No<PurchaseOrder?>();
            public Task<List<GoodsReceipt>> GetReceiptsAsync(int companyId) => No<List<GoodsReceipt>>();
            public Task<GoodsReceipt?> GetReceiptAsync(int companyId, int id) => No<GoodsReceipt?>();
            public Task<(bool ok, string? error, GoodsReceipt? gr)> CreateReceiptAsync(int companyId, int? vendorId, int warehouseId, int? poId, DateTime date, string? notes, List<ReceiptLineInput> lines, string? userId, int? currencyId = null, decimal? exchangeRate = null) => No<(bool, string?, GoodsReceipt?)>();
            public Task<(bool ok, string? error, int? invoiceId)> ConvertToInvoiceAsync(int companyId, int poId, string? userId) => No<(bool, string?, int?)>();
        }

        private sealed class NoopNotifications : INotificationService
        {
            public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn, string? bodyAr,
                string? bodyEn, string type, int? refId = null, string? url = null, int? companyId = null,
                int? actorEmployeeId = null, string? priority = null, string? category = null,
                string? dedupKey = null, DateTime? expiresAt = null, string? icon = null,
                string? entityType = null, int? entityId = null) => Task.CompletedTask;

            public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles, string? titleAr,
                string? titleEn, string? bodyAr, string? bodyEn, string type, int? refId = null,
                int? exceptEmployeeId = null) => Task.FromResult(0);
        }

        /// The approver's request context. Deliberately settable per test, because the point is what
        /// happens when it disagrees with the row.

        // ------------------------------------------------------------------------------------------

        private static IInventoryApprovalService Service(
            PlatformTestHost host, int? companyId, int? employeeId,
            IProcurementService? proc = null, IStockService? stock = null, bool withAuthority = true)
        {
            var ctx = new Ctx(companyId, employeeId);
            var access = new InventoryAccessService(
                host.Db, new Microsoft.AspNetCore.Http.HttpContextAccessor(), ctx,
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<InventoryAccessService>.Instance);

            return withAuthority
                ? new InventoryApprovalService(host.Db, stock ?? new RecordingStock(), proc ?? new RecordingProcurement(),
                    new NoopNotifications(), ctx, new IModuleAccessService[] { access })
                : new InventoryApprovalService(host.Db, stock ?? new RecordingStock(), proc ?? new RecordingProcurement(),
                    new NoopNotifications(), ctx);
        }

        private static async Task<PlatformTestHost> SeedAsync()
        {
            var host = new PlatformTestHost(companyId: CompanyA);
            foreach (var (id, co) in new[] { (Requester, CompanyA), (Manager, CompanyA), (Nobody, CompanyA) })
                host.Seed.Employee.Add(new Employee
                {
                    ID = id, FirstName = "T", LastName = "T", FullName = "e" + id, FullNameEn = "e" + id,
                    EmpCompanyID = co, IsActive = true, Address = "-", PhoneNumber = "-",
                    Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
                    UserId = "u" + id,
                });

            // A CONFIGURED company: at least one inventory role exists, so the module's role branch decides
            // and bootstrap does not. Without this the whole suite would be measuring the bootstrap path.
            host.Seed.InventoryUserRoles.Add(new InventoryUserRole
            { CompanyID = CompanyA, EmployeeId = Manager, Role = "InventoryManager" });
            host.Seed.InventoryUserRoles.Add(new InventoryUserRole
            { CompanyID = CompanyB, EmployeeId = Manager, Role = "InventoryManager" });
            await host.Seed.SaveChangesAsync();
            return host;
        }

        private static async Task<int> PendingAsync(PlatformTestHost host, int companyId = CompanyA)
        {
            var ap = new InventoryApproval
            {
                CompanyID = companyId, DocType = "PurchaseOrder", Amount = 500m,
                PayloadJson = "{\"VendorId\":1,\"WarehouseId\":1,\"OrderDate\":\"2026-01-01\",\"Lines\":[]}",
                Status = "Pending", RequestedByEmployeeId = Requester, RequestedAt = DateTime.UtcNow,
            };
            host.Seed.InventoryApprovals.Add(ap);
            await host.Seed.SaveChangesAsync();
            return ap.ID;
        }

        private static async Task AssertUntouchedAsync(PlatformTestHost host, int id)
        {
            var ap = await host.Db.InventoryApprovals.AsNoTracking().FirstAsync(a => a.ID == id);
            Assert.Equal("Pending", ap.Status);
            Assert.Null(ap.DecidedByEmployeeId);
            Assert.Null(ap.DecidedAt);
            Assert.Null(ap.ResultDocNo);
        }

        // ============================================================================================
        // A. THE DIRECT-SERVICE BYPASS — the defect this increment closes.
        // ============================================================================================

        [Fact]
        public async Task A1_an_actor_without_inventory_manage_cannot_approve_directly()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host);
            var proc = new RecordingProcurement();

            var (ok, err) = await Service(host, CompanyA, Nobody, proc).ApproveAsync(id, Nobody, "n");

            Assert.False(ok);
            Assert.NotNull(err);
            Assert.Empty(proc.Executed);          // NOTHING happened - not merely "false was returned"
            await AssertUntouchedAsync(host, id);
        }

        [Fact]
        public async Task A2_the_same_call_succeeds_for_an_inventory_manager()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host);
            var proc = new RecordingProcurement();

            // Identical call. The ONLY difference from A1 is one InventoryUserRoles row, which is what
            // separates "the service authorizes" from "the service refuses everybody".
            var (ok, err) = await Service(host, CompanyA, Manager, proc).ApproveAsync(id, Manager, "ok");

            Assert.True(ok, err);
            Assert.Single(proc.Executed);
            var ap = await host.Db.InventoryApprovals.AsNoTracking().FirstAsync(a => a.ID == id);
            Assert.Equal("Approved", ap.Status);
            Assert.Equal(Manager, ap.DecidedByEmployeeId);
        }

        [Fact]
        public async Task A3_an_actor_without_inventory_manage_cannot_reject_directly()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host);

            var (ok, err) = await Service(host, CompanyA, Nobody).RejectAsync(id, Nobody, "n");

            Assert.False(ok);
            Assert.NotNull(err);
            await AssertUntouchedAsync(host, id);
        }

        [Fact]
        public async Task A4_the_same_rejection_succeeds_for_an_inventory_manager()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host);

            var (ok, err) = await Service(host, CompanyA, Manager).RejectAsync(id, Manager, "no thanks");

            Assert.True(ok, err);
            var ap = await host.Db.InventoryApprovals.AsNoTracking().FirstAsync(a => a.ID == id);
            Assert.Equal("Rejected", ap.Status);
            Assert.Equal(Manager, ap.DecidedByEmployeeId);
            Assert.Equal("no thanks", ap.DecisionNote);
            Assert.NotNull(ap.DecidedAt);
        }

        [Fact]
        public async Task A5_an_approver_who_may_not_approve_may_not_reject_either()
        {
            using var host = await SeedAsync();
            int approve = await PendingAsync(host);
            int reject = await PendingAsync(host);
            var svc = Service(host, CompanyA, Nobody);

            // Rejecting destroys a pending purchase order. Letting the weaker verb through would hand an
            // unauthorized actor the power to cancel work instead of the power to create it.
            Assert.False((await svc.ApproveAsync(approve, Nobody, null)).ok);
            Assert.False((await svc.RejectAsync(reject, Nobody, null)).ok);
            await AssertUntouchedAsync(host, approve);
            await AssertUntouchedAsync(host, reject);
        }

        // ============================================================================================
        // B. NO AUTHORITY WIRED MEANS REFUSED — the opposite of the usual optional default.
        // ============================================================================================

        [Fact]
        public async Task B1_a_service_built_without_an_authority_refuses_both_decisions()
        {
            using var host = await SeedAsync();
            int a = await PendingAsync(host);
            int r = await PendingAsync(host);
            var proc = new RecordingProcurement();
            var svc = Service(host, CompanyA, Manager, proc, withAuthority: false);

            // Even for a genuine manager. The thing behind this gate creates purchase orders and moves
            // stock, so an unanswerable question is a refusal rather than a default-allow.
            Assert.False((await svc.ApproveAsync(a, Manager, null)).ok);
            Assert.False((await svc.RejectAsync(r, Manager, null)).ok);
            Assert.Empty(proc.Executed);
            await AssertUntouchedAsync(host, a);
        }

        [Fact]
        public async Task B1b_an_authority_list_without_the_inventory_module_refuses()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host);
            var ctx = new Ctx(CompanyA, Manager);
            var proc = new RecordingProcurement();

            // Modules ARE wired — just not the one that answers for Inventory. A missing module is exactly
            // as unanswerable as a missing list, and must fail the same way rather than falling through to
            // the first module that happens to be registered.
            var svc = new InventoryApprovalService(host.Db, new RecordingStock(), proc,
                new NoopNotifications(), ctx, Array.Empty<IModuleAccessService>());

            Assert.False((await svc.ApproveAsync(id, Manager, null)).ok);
            Assert.False((await svc.RejectAsync(id, Manager, null)).ok);
            Assert.Empty(proc.Executed);
            await AssertUntouchedAsync(host, id);
        }

        [Fact]
        public async Task B2_an_unresolved_company_refuses_both_decisions()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host);

            // No company on the context at all. CurrentCompanyIdAsync throws for reads by design, so this
            // asserts the shape rather than the message.
            await Assert.ThrowsAnyAsync<Exception>(
                () => Service(host, null, Manager).ApproveAsync(id, Manager, null));
            await AssertUntouchedAsync(host, id);
        }

        [Fact]
        public async Task B3_a_context_with_no_employee_is_refused()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host);

            Assert.False((await Service(host, CompanyA, null).ApproveAsync(id, Manager, null)).ok);
            await AssertUntouchedAsync(host, id);
        }

        // ============================================================================================
        // C. COMPANY ISOLATION.
        // ============================================================================================

        [Fact]
        public async Task C1_a_manager_in_another_company_cannot_approve_this_one()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host, CompanyA);
            var proc = new RecordingProcurement();

            // Manager holds InventoryManager in company B too, so the ROLE is not what refuses this - the
            // company-scoped lookup is, and the approval reads as "not found" rather than "forbidden".
            var (ok, _) = await Service(host, CompanyB, Manager, proc).ApproveAsync(id, Manager, null);

            Assert.False(ok);
            Assert.Empty(proc.Executed);
            await AssertUntouchedAsync(host, id);
        }

        [Fact]
        public async Task C2_a_manager_in_another_company_cannot_reject_this_one()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host, CompanyA);

            Assert.False((await Service(host, CompanyB, Manager).RejectAsync(id, Manager, null)).ok);
            await AssertUntouchedAsync(host, id);
        }

        // ============================================================================================
        // D. SEGREGATION OF DUTIES AND STATE — unchanged, and re-proven behind the new gate.
        // ============================================================================================

        [Fact]
        public async Task D1_the_requester_cannot_approve_their_own_request_even_holding_manage()
        {
            using var host = await SeedAsync();
            host.Seed.InventoryUserRoles.Add(new InventoryUserRole
            { CompanyID = CompanyA, EmployeeId = Requester, Role = "InventoryManager" });
            await host.Seed.SaveChangesAsync();
            int id = await PendingAsync(host);
            var proc = new RecordingProcurement();

            var (ok, _) = await Service(host, CompanyA, Requester, proc).ApproveAsync(id, Requester, null);

            Assert.False(ok);
            Assert.Empty(proc.Executed);
            await AssertUntouchedAsync(host, id);
        }

        [Fact]
        public async Task D2_the_requester_cannot_reject_their_own_request_even_holding_manage()
        {
            using var host = await SeedAsync();
            host.Seed.InventoryUserRoles.Add(new InventoryUserRole
            { CompanyID = CompanyA, EmployeeId = Requester, Role = "InventoryManager" });
            await host.Seed.SaveChangesAsync();
            int id = await PendingAsync(host);

            Assert.False((await Service(host, CompanyA, Requester).RejectAsync(id, Requester, null)).ok);
            await AssertUntouchedAsync(host, id);
        }

        [Fact]
        public async Task D3_an_already_decided_approval_cannot_be_decided_again()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host);
            Assert.True((await Service(host, CompanyA, Manager).RejectAsync(id, Manager, "first")).ok);

            // One approval, one terminal decision. No return, no resubmission, no second round.
            Assert.False((await Service(host, CompanyA, Manager).ApproveAsync(id, Manager, "second")).ok);
            Assert.False((await Service(host, CompanyA, Manager).RejectAsync(id, Manager, "third")).ok);

            var ap = await host.Db.InventoryApprovals.AsNoTracking().FirstAsync(a => a.ID == id);
            Assert.Equal("Rejected", ap.Status);
            Assert.Equal("first", ap.DecisionNote);
        }

        [Fact]
        public async Task D4_authorization_is_asked_before_the_row_is_read()
        {
            using var host = await SeedAsync();
            int real = await PendingAsync(host);

            // An unauthorized caller gets the SAME answer for a real approval and a missing one. Asking
            // after the lookup would let the error text distinguish them, turning a refusal into a probe.
            var svc = Service(host, CompanyA, Nobody);
            var onReal = await svc.ApproveAsync(real, Nobody, null);
            var onGhost = await svc.ApproveAsync(999_999, Nobody, null);

            Assert.False(onReal.ok);
            Assert.False(onGhost.ok);
            Assert.Equal(onGhost.error, onReal.error);
        }

        // ============================================================================================
        // E. REJECT IS TRANSACTIONAL — the prerequisite for a canonical Rejected event.
        // ============================================================================================

        [Fact]
        public async Task E1_a_failure_during_rejection_leaves_the_approval_pending()
        {
            using var host = await SeedAsync();
            int id = await PendingAsync(host);
            var svc = (InventoryApprovalService)Service(host, CompanyA, Manager);

            // The seam the approve path already carries. A bare SaveChanges would have written the decision
            // with nowhere to put the event that must accompany it.
            svc.BeforeDecisionSaveHookForTests = () => throw new InvalidOperationException("boom");

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RejectAsync(id, Manager, "x"));
            await AssertUntouchedAsync(host, id);
        }

        [Fact]
        public void E2_reject_opens_a_transaction_and_commits_it()
        {
            // Structural, because the in-memory provider cannot demonstrate a real rollback boundary and a
            // behavioural test alone would pass against a bare SaveChanges wrapped in nothing.
            string code = string.Join("\n", File.ReadAllLines(
                    Path.Combine(RepoRoot(), "CrossBuy", "BL", "InventoryApprovalService.cs"))
                .Where(l => !l.TrimStart().StartsWith("//")));

            int i = code.IndexOf("public async Task<(bool ok, string? error)> RejectAsync(", StringComparison.Ordinal);
            Assert.True(i > 0);
            string body = code[i..];
            int end = body.IndexOf("private static string DocTypeName", StringComparison.Ordinal);
            if (end > 0) body = body[..end];

            Assert.Contains("ScopedTx.BeginOrJoinAsync", body);
            Assert.Contains("tx.CommitAsync", body);
            // The notification must follow the commit: a message about a rejection that rolled back
            // cannot be unsent.
            Assert.True(body.IndexOf("tx.CommitAsync", StringComparison.Ordinal)
                      < body.IndexOf("_notify.NotifyAsync", StringComparison.Ordinal));
        }

        // ============================================================================================
        // F. THE FOUR DOC TYPES SHARE ONE ENGINE — regression across all of them.
        // ============================================================================================

        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("StockTransfer")]
        [InlineData("StockCount")]
        [InlineData("WriteOff")]
        public async Task F1_every_doc_type_refuses_an_unauthorized_approver(string docType)
        {
            using var host = await SeedAsync();
            var ap = new InventoryApproval
            {
                CompanyID = CompanyA, DocType = docType, Amount = 500m, PayloadJson = "{}",
                Status = "Pending", RequestedByEmployeeId = Requester, RequestedAt = DateTime.UtcNow,
            };
            host.Seed.InventoryApprovals.Add(ap);
            await host.Seed.SaveChangesAsync();

            // ThrowingStock is the assertion: if authorization let this through, the replay would reach a
            // stock method and blow up rather than quietly returning false.
            var (ok, _) = await Service(host, CompanyA, Nobody).ApproveAsync(ap.ID, Nobody, null);

            Assert.False(ok);
            await AssertUntouchedAsync(host, ap.ID);
        }

        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("StockTransfer")]
        [InlineData("StockCount")]
        [InlineData("WriteOff")]
        public async Task F2_every_doc_type_refuses_an_unauthorized_rejecter(string docType)
        {
            using var host = await SeedAsync();
            var ap = new InventoryApproval
            {
                CompanyID = CompanyA, DocType = docType, Amount = 500m, PayloadJson = "{}",
                Status = "Pending", RequestedByEmployeeId = Requester, RequestedAt = DateTime.UtcNow,
            };
            host.Seed.InventoryApprovals.Add(ap);
            await host.Seed.SaveChangesAsync();

            Assert.False((await Service(host, CompanyA, Nobody).RejectAsync(ap.ID, Nobody, null)).ok);
            await AssertUntouchedAsync(host, ap.ID);
        }

        // ============================================================================================
        // G. STRUCTURE — the authority is asked, never restated.
        // ============================================================================================

        [Fact]
        public void G1_the_approval_service_names_no_role_of_its_own()
        {
            string code = string.Join("\n", File.ReadAllLines(
                    Path.Combine(RepoRoot(), "CrossBuy", "BL", "InventoryApprovalService.cs"))
                .Where(l => !l.TrimStart().StartsWith("//")));

            Assert.Contains("IModuleAccessService", code);           // it asks
            Assert.Contains("class InventoryApprovalService", code);  // the guard read real code

            // SCOPED TO THE DECISION PATH, not the whole file. Two OTHER methods legitimately read
            // InventoryUserRoles: SubmitAsync finds the managers to notify, and ApprovalInboxAsync answers
            // "is this caller a manager" for a READ. Forbidding the string outright would have banned both,
            // and the first version of this guard did exactly that. What must not appear is a role name
            // inside the two methods that DECIDE — that would be a second authorization source.
            int gate = code.IndexOf("private async Task<bool> CanDecideAsync(", StringComparison.Ordinal);
            Assert.True(gate > 0);
            string gateBody = code[gate..Math.Min(gate + 1400, code.Length)];
            Assert.DoesNotContain("InventoryManager", gateBody);
            Assert.DoesNotContain("InventoryUserRoles", gateBody);
            Assert.DoesNotContain("WarehouseKeeper", gateBody);

            foreach (var sig in new[]
            {
                "public async Task<(bool ok, string? error)> ApproveAsync(",
                "public async Task<(bool ok, string? error)> RejectAsync(",
            })
            {
                int i = code.IndexOf(sig, StringComparison.Ordinal);
                Assert.True(i > 0, sig);
                string body = code[i..Math.Min(i + 1400, code.Length)];
                Assert.DoesNotContain("InventoryManager", body);
                Assert.DoesNotContain("InventoryUserRoles", body);
            }
        }

        [Fact]
        public void G2_both_decisions_ask_before_they_act()
        {
            string code = string.Join("\n", File.ReadAllLines(
                    Path.Combine(RepoRoot(), "CrossBuy", "BL", "InventoryApprovalService.cs"))
                .Where(l => !l.TrimStart().StartsWith("//")));

            foreach (var sig in new[]
            {
                "public async Task<(bool ok, string? error)> ApproveAsync(",
                "public async Task<(bool ok, string? error)> RejectAsync(",
            })
            {
                int i = code.IndexOf(sig, StringComparison.Ordinal);
                Assert.True(i > 0, sig);
                string body = code[i..Math.Min(i + 1400, code.Length)];
                int gate = body.IndexOf("CanDecideAsync", StringComparison.Ordinal);
                int read = body.IndexOf("InventoryApprovals.FirstOrDefaultAsync", StringComparison.Ordinal);
                Assert.True(gate > 0, sig + " has no authorization gate");
                Assert.True(read > 0 && gate < read, sig + " reads the row before authorizing");
            }
        }

        [Fact]
        public void G3_no_approver_table_or_cycle_was_invented()
        {
            string code = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "BL", "InventoryApprovalService.cs"));
            foreach (var forbidden in new[]
            {
                "InventoryApprover", "ProjectBillingApprover", "ApprovalCycle", "ApprovalLevel",
                "ApprovalStep", "Resubmit", "Returned",
            })
                Assert.DoesNotContain(forbidden, code);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
