using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // INVENTORY APPROVAL REPLAY — the company a held command executes under.
    //
    // ApprovalCompanyIsolationTests proves the READ half: an inventory manager sees their own
    // company's queue and nothing else. Its own comment says the write paths were left out.
    //
    // This file is the write half, and it is the more interesting one, because the fix does NOT use
    // the same company for both directions and that asymmetry is the whole design:
    //
    //   * a NEW submission belongs to the company the request resolved to;
    //   * an APPROVAL replays under the APPROVAL ROW'S OWN CompanyID.
    //
    // WHY THE SECOND HALF CANNOT BE "the current request's company". The payload was captured
    // against a specific company's warehouses, vendors, items and financial context. Replaying it
    // under whatever company the approver happens to resolve to today would execute a command with
    // company A's warehouse ids inside company B — a cross-company command execution, and the
    // failure would not look like a permission error. It would look like stock moving.
    //
    // Every test below observes the COMPANY ARGUMENT the replay actually passes to StockService and
    // ProcurementService. Asserting on the approval row's status afterwards would pass equally well
    // against the wrong company.
    //
    // WHERE THE ENFORCEMENT ACTUALLY LIVES, measured rather than assumed: the scoped lookup, not the
    // row-company argument. Loosening `a.ID == id && a.CompanyID == companyId` to `a.ID == id` fails
    // four tests here; swapping ap.CompanyID for the resolved company fails none, because the lookup
    // has already made them equal. Both rules are kept - one is the barrier, the other makes the
    // replay correct by construction if the barrier is ever weakened - and this file says which is
    // which so nobody mistakes the second for the first.
    // =============================================================================================
    public class InventoryApprovalReplayCompanyTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;
        private const int Requester = 5;
        private const int Approver = 6;

        // -----------------------------------------------------------------------------------------
        // THE OBSERVATION POINT
        // -----------------------------------------------------------------------------------------

        /// Records the company every replayed command was executed under. Every OTHER member throws:
        /// this path must reach exactly four methods, and a stub that quietly absorbed a fifth call
        /// would hide a replay nobody meant to happen.
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
            public Task<(bool ok, string? error, int laborId)> AddWorkOrderLaborAsync(int companyId, int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal ratePerHour, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, DateTime date, string? userId) => No<(bool, string?, int)>();
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
        private sealed class FixedContext : IBusinessContextAccessor
        {
            private readonly int? _companyId;
            public FixedContext(int? companyId) { _companyId = companyId; }

            public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
                => _companyId is int c
                    ? Task.FromResult(new BusinessContext { CompanyId = c, EmployeeId = Approver, UserId = "u", Source = BusinessContextSource.Http })
                    : throw new BusinessContextUnresolvedException("no company");

            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(_companyId is int c
                    ? new BusinessContext { CompanyId = c, EmployeeId = Approver, UserId = "u", Source = BusinessContextSource.Http }
                    : null);
        }

        // -----------------------------------------------------------------------------------------
        // §4.7–§4.11 — every replayed command executes under the APPROVAL ROW'S company
        // -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("StockTransfer")]
        [InlineData("StockCount")]
        [InlineData("WriteOff")]
        public async Task A_replayed_command_executes_under_the_approval_rows_company(string docType)
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var id = Seed(host, CompanyA, docType);

            var stock = new RecordingStock();
            var proc = new RecordingProcurement();
            var (ok, error) = await Service(host, stock, proc, approverCompany: CompanyA)
                .ApproveAsync(id, Approver, note: null);

            Assert.True(ok, error);
            var executed = docType == "PurchaseOrder" ? proc.Executed : stock.Executed;
            var single = Assert.Single(executed);
            Assert.Equal(docType, single.Op);
            Assert.Equal(CompanyA, single.CompanyId);
        }

        // -----------------------------------------------------------------------------------------
        // §4.12 — THE INVARIANT THAT MATTERS MOST
        // -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("PurchaseOrder")]
        [InlineData("StockTransfer")]
        [InlineData("StockCount")]
        [InlineData("WriteOff")]
        public async Task An_approver_in_another_company_cannot_replay_company_As_payload_at_all(string docType)
        {
            // The row belongs to company A. The approver's request resolves to company B.
            //
            // The correct outcome is NOT "replayed under A" — it is "not found". ApproveAsync scopes
            // the lookup to the approver's own company first, so a company-B approver never reaches
            // company A's row, and the payload therefore cannot execute anywhere. The row-company rule
            // governs WHICH company a legitimately reachable approval replays under; the lookup scope
            // is what stops the cross-company attempt in the first place. Both are asserted, here and
            // above, because either alone would leave a hole.
            using var host = new PlatformTestHost(companyId: CompanyA);
            var id = Seed(host, CompanyA, docType);

            var stock = new RecordingStock();
            var proc = new RecordingProcurement();
            var (ok, _) = await Service(host, stock, proc, approverCompany: CompanyB)
                .ApproveAsync(id, Approver, note: null);

            Assert.False(ok);
            Assert.Empty(stock.Executed);
            Assert.Empty(proc.Executed);

            // ...and nothing about the row moved.
            Assert.Equal("Pending", host.Db.InventoryApprovals.Find(id)!.Status);
        }

        [Fact]
        public async Task The_replay_company_comes_from_the_row_and_not_from_the_approvers_context()
        {
            // MEASURED, AND THE RESULT IS WORTH STATING PRECISELY. Swapping ap.CompanyID for the
            // resolved company in the four replay calls is BEHAVIOURALLY EQUIVALENT — every test here
            // still passed, and only the source assertion below caught it. That is not a weakness in
            // the tests; it is a fact about the code: ApproveAsync loads the row scoped to the
            // approver's own company, so by the time the replay runs the two values are provably equal.
            //
            // So the ACTIVE barrier is the scoped lookup, and that is what the id-substitution theory
            // above kills (loosening it to `a.ID == id` fails all four cases). Reading the company from
            // the ROW is defence in depth: it keeps the replay correct by construction even if the
            // lookup scope were ever loosened, which is exactly the change this suite would catch.
            using var host = new PlatformTestHost(companyId: CompanyB);
            var id = Seed(host, CompanyB, "WriteOff");

            var stock = new RecordingStock();
            var (ok, error) = await Service(host, stock, new RecordingProcurement(), approverCompany: CompanyB)
                .ApproveAsync(id, Approver, note: null);

            Assert.True(ok, error);
            Assert.Equal(CompanyB, Assert.Single(stock.Executed).CompanyId);

            // Not company 1, which is the value the constant used to supply.
            Assert.NotEqual(1, stock.Executed[0].CompanyId);
        }

        // -----------------------------------------------------------------------------------------
        // §4.13 — an unresolved company fails closed on every write path
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task An_unresolved_company_cannot_approve_and_replays_nothing()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var id = Seed(host, CompanyA, "WriteOff");

            var stock = new RecordingStock();
            var service = Service(host, stock, new RecordingProcurement(), approverCompany: null);

            await Assert.ThrowsAsync<BusinessContextUnresolvedException>(
                () => service.ApproveAsync(id, Approver, note: null));

            Assert.Empty(stock.Executed);
            Assert.Equal("Pending", host.Db.InventoryApprovals.Find(id)!.Status);
        }

        [Fact]
        public async Task An_unresolved_company_cannot_submit_and_writes_no_row()
        {
            using var host = new PlatformTestHost(companyId: CompanyA);
            var service = Service(host, new RecordingStock(), new RecordingProcurement(), approverCompany: null);

            await Assert.ThrowsAsync<BusinessContextUnresolvedException>(
                () => service.SubmitAsync("WriteOff", 5000m, new WriteOffApprovalPayload(), Requester));

            Assert.Empty(host.Db.InventoryApprovals);
        }

        // -----------------------------------------------------------------------------------------
        // §4.1 / §4.2 — a new submission belongs to the RESOLVED company
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_new_submission_belongs_to_the_resolved_company_and_never_to_company_one()
        {
            using var host = new PlatformTestHost(companyId: CompanyB);

            var id = await Service(host, new RecordingStock(), new RecordingProcurement(), approverCompany: CompanyB)
                .SubmitAsync("WriteOff", 5000m, new WriteOffApprovalPayload(), Requester);

            Assert.True(id > 0);
            var row = host.Db.InventoryApprovals.Find(id)!;
            Assert.Equal(CompanyB, row.CompanyID);
            Assert.NotEqual(1, row.CompanyID);
        }

        // -----------------------------------------------------------------------------------------
        // §4.14 — no implicit company-1 anywhere in the file
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_service_holds_no_company_constant_and_no_company_one_fallback()
        {
            var path = System.IO.Path.Combine(RepoRoot(), "CrossBuy", "BL", "InventoryApprovalService.cs");
            var code = System.Text.RegularExpressions.Regex.Replace(
                System.IO.File.ReadAllText(path), @"//.*?$", " ",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            Assert.DoesNotMatch(@"const\s+int\s+\w*[Cc]ompany\w*\s*=\s*1", code);
            Assert.DoesNotMatch(@"\?\?\s*1\b", code);

            // The two seams the design depends on, named so a refactor cannot quietly collapse them
            // into one.
            Assert.Contains("CurrentCompanyIdAsync()", code, StringComparison.Ordinal);
            Assert.Contains("ap.CompanyID", code, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------------------------

        private static IInventoryApprovalService Service(PlatformTestHost host, IStockService stock,
            IProcurementService proc, int? approverCompany)
            => new InventoryApprovalService(host.Db, stock, proc, new NoopNotifications(),
                new FixedContext(approverCompany));

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static int Seed(PlatformTestHost host, int companyId, string docType)
        {
            object payload = docType switch
            {
                "PurchaseOrder" => new PoApprovalPayload { VendorId = 3, WarehouseId = 9, OrderDate = DateTime.UtcNow.Date },
                "StockTransfer" => new TransferApprovalPayload { FromWarehouseId = 9, ToWarehouseId = 10, Date = DateTime.UtcNow.Date },
                "StockCount" => new CountApprovalPayload { WarehouseId = 9, CountDate = DateTime.UtcNow.Date },
                _ => new WriteOffApprovalPayload { WarehouseId = 9, WriteOffDate = DateTime.UtcNow.Date, Reason = "damage" },
            };

            var ap = new InventoryApproval
            {
                CompanyID = companyId, DocType = docType, Amount = 5000m,
                PayloadJson = JsonSerializer.Serialize(payload), Status = "Pending",
                RequestedByEmployeeId = Requester, RequestedAt = DateTime.UtcNow,
            };
            host.Seed.InventoryApprovals.Add(ap);
            host.Seed.SaveChanges();
            return ap.ID;
        }
    }
}
