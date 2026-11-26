using System;
using System.Linq;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // EDITING A PURCHASE INVOICE MUST NOT MOVE STOCK THAT IS NOT THE INVOICE'S.
    //
    // THE DEFECT, IN ONE LINE: the edit reversed every movement whose SourceType was
    // "PurchaseInvoice", and the re-receipt it then posted carried that same SourceType - so the
    // next edit reversed it again, along with the one before it, and so on.
    //
    // Measured on a real invoice before the fix, 5 receipts and 10 reversals for a single line:
    //
    //     post +5 | edit −5 +5 | edit −5 −5 +9 | edit −5 −5 −9 +9 | edit −5 −5 −9 −9 +5
    //     received 33, reversed 62, NET −29 where the invoice says +5.
    //
    // It is silent, it compounds with every edit, and it moves inventory rather than a report. The
    // GL side was never affected: journal reversal follows JournalEntryId, which names exactly one
    // entry, so only the stock path swept by type.
    //
    // THE FIXTURE IS THAT RECORDED HISTORY, not an invented one. These rows are the movements the
    // live database actually held, so the regression test fails on the shape the defect really
    // produced rather than on a tidier one it might have.
    // ============================================================================================
    public class PurchaseInvoiceEditStockTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 77;
        private const int Invoice = 5071;
        private const int OtherInvoice = 6000;
        private const int Item = 3119;
        private const int Warehouse = 2;

        private readonly SqliteConnection _conn;
        private readonly CrossDbContext _db;
        private readonly CompanyScopeHolder _holder = new();

        public PurchaseInvoiceEditStockTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(CompanyA, null);

            // StockMovement is not one of the company-filtered entities, but the bypass is held
            // anyway so that nothing in the fixture depends on a filter doing the service's job -
            // the same reason the copy-from tests hold one.
            _holder.ApplyBypass(new CompanyBypassGrant
            {
                Kind = CompanyBypassKind.CrossCompanyAdministration,
                Reason = "test fixture: prove the service's own predicates",
                GrantedAtUtc = DateTime.UtcNow,
            });

            var options = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(
                    NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            _db = new CrossDbContext(options, _holder);
            _db.Database.EnsureCreated();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose() { _db.Dispose(); _conn.Dispose(); }

        /// Only the context is reached by the method under test; the other six collaborators are
        /// never touched, and null says so more honestly than a stub would.
        private PayableService Service() =>
            new(_db, null!, null!, null!, null!, null!, null!);

        private void Move(int invoiceId, string sourceType, short direction, decimal qty,
                          decimal unitCost = 3m, int companyId = CompanyA,
                          int item = Item, int warehouse = Warehouse)
        {
            _db.StockMovements.Add(new StockMovement
            {
                CompanyID = companyId,
                ItemId = item,
                WarehouseId = warehouse,
                Direction = direction,
                QtyBase = qty,
                UnitCost = unitCost,
                SourceType = sourceType,
                SourceId = invoiceId,
                MovementDate = new DateTime(2026, 8, 3),
                TotalCost = qty * unitCost,
            });
            _db.SaveChanges();
        }

        /// The exact sequence the live invoice carried, in order.
        private void TheRecordedRunaway()
        {
            Move(Invoice, "PurchaseInvoice", 1, 5);                      // original posting
            Move(Invoice, "PurchaseInvoiceEdit", -1, 5);                 // edit 1
            Move(Invoice, "PurchaseInvoice", 1, 5);
            Move(Invoice, "PurchaseInvoiceEdit", -1, 5);                 // edit 2 reversed TWO
            Move(Invoice, "PurchaseInvoiceEdit", -1, 5);
            Move(Invoice, "PurchaseInvoice", 1, 9);
            Move(Invoice, "PurchaseInvoiceEdit", -1, 5);                 // edit 3 reversed THREE
            Move(Invoice, "PurchaseInvoiceEdit", -1, 5);
            Move(Invoice, "PurchaseInvoiceEdit", -1, 9);
            Move(Invoice, "PurchaseInvoice", 1, 9);
            Move(Invoice, "PurchaseInvoiceEdit", -1, 5);                 // edit 4 reversed FOUR
            Move(Invoice, "PurchaseInvoiceEdit", -1, 5);
            Move(Invoice, "PurchaseInvoiceEdit", -1, 9);
            Move(Invoice, "PurchaseInvoiceEdit", -1, 9);
            Move(Invoice, "PurchaseInvoice", 1, 5);
        }

        // ---- the ordinary case -------------------------------------------------------------

        // A FIRST EDIT MUST STILL TAKE OUT EXACTLY WHAT WENT IN. The fix cannot be "reverse less":
        // on a clean invoice the answer is unchanged, and this is what says so.
        [Fact]
        public async Task A_freshly_posted_invoice_owes_exactly_its_receipt()
        {
            Move(Invoice, "PurchaseInvoice", 1, 5);

            var outstanding = await Service().OutstandingStockAsync(CompanyA, Invoice);

            var row = Assert.Single(outstanding);
            Assert.Equal(Item, row.ItemId);
            Assert.Equal(Warehouse, row.WarehouseId);
            Assert.Equal(5m, row.Qty);
        }

        // AFTER ONE PROPER EDIT THE INVOICE OWES THE NEW QUANTITY, NOT THE OLD ONE PLUS THE NEW.
        [Fact]
        public async Task An_invoice_already_reversed_once_owes_only_what_it_now_holds()
        {
            Move(Invoice, "PurchaseInvoice", 1, 5);
            Move(Invoice, "PurchaseInvoiceEdit", -1, 5);
            Move(Invoice, "PurchaseInvoice", 1, 9);

            var row = Assert.Single(await Service().OutstandingStockAsync(CompanyA, Invoice));
            Assert.Equal(9m, row.Qty);
        }

        // THE REGRESSION. Reading receipts alone gives 33; the truth is −29, and the correction the
        // service posts from it is what brings the invoice back to zero before it re-receives.
        [Fact]
        public async Task The_recorded_runaway_nets_to_what_the_movements_actually_say()
        {
            TheRecordedRunaway();

            var row = Assert.Single(await Service().OutstandingStockAsync(CompanyA, Invoice));

            Assert.Equal(-29m, row.Qty);
            // and the gross figure the old code would have used, stated so the two cannot be confused
            Assert.Equal(33m, _db.StockMovements.AsNoTracking()
                .Where(m => m.SourceId == Invoice && m.SourceType == "PurchaseInvoice")
                .Sum(m => m.QtyBase));
        }

        // A BALANCED INVOICE NEEDS NO CORRECTION, and must not be handed a zero-quantity movement.
        [Fact]
        public async Task An_invoice_that_already_balances_owes_nothing()
        {
            Move(Invoice, "PurchaseInvoice", 1, 5);
            Move(Invoice, "PurchaseInvoiceEdit", -1, 5);

            Assert.Empty(await Service().OutstandingStockAsync(CompanyA, Invoice));
        }

        // ---- what must NOT be swept in ------------------------------------------------------

        // ANOTHER INVOICE'S MOVEMENTS ARE NOT THIS INVOICE'S. Asymmetric on purpose: this invoice
        // holds 5 and the other holds 40, so the assertion cannot pass with the id predicate gone.
        [Fact]
        public async Task Another_invoices_movements_are_not_counted()
        {
            Move(Invoice, "PurchaseInvoice", 1, 5);
            Move(OtherInvoice, "PurchaseInvoice", 1, 40);

            var row = Assert.Single(await Service().OutstandingStockAsync(CompanyA, Invoice));
            Assert.Equal(5m, row.Qty);
        }

        [Fact]
        public async Task Another_companys_movements_are_not_counted()
        {
            Move(Invoice, "PurchaseInvoice", 1, 5);
            Move(Invoice, "PurchaseInvoice", 1, 40, companyId: CompanyB);

            var row = Assert.Single(await Service().OutstandingStockAsync(CompanyA, Invoice));
            Assert.Equal(5m, row.Qty);
        }

        // A SALE OR AN ADJUSTMENT AGAINST THE SAME NUMBER IS NOT THIS INVOICE'S DOING. SourceId is
        // only unique within a SourceType, so without the type filter an unrelated document with
        // the same id would be netted in and the correction would move stock nobody asked it to.
        [Fact]
        public async Task Movements_from_another_document_type_are_not_counted()
        {
            Move(Invoice, "PurchaseInvoice", 1, 5);
            Move(Invoice, "Issue", -1, 40);
            Move(Invoice, "Adjustment", 1, 100);

            var row = Assert.Single(await Service().OutstandingStockAsync(CompanyA, Invoice));
            Assert.Equal(5m, row.Qty);
        }

        // ---- per position, not per invoice ---------------------------------------------------

        // TWO ITEMS ARE TWO CORRECTIONS. Summing across positions would post one movement for a
        // quantity that belongs to neither of them.
        [Fact]
        public async Task Each_item_and_warehouse_is_answered_separately()
        {
            Move(Invoice, "PurchaseInvoice", 1, 5, item: 100, warehouse: 1);
            Move(Invoice, "PurchaseInvoice", 1, 7, item: 200, warehouse: 1);
            Move(Invoice, "PurchaseInvoice", 1, 3, item: 100, warehouse: 2);

            var rows = await Service().OutstandingStockAsync(CompanyA, Invoice);

            Assert.Equal(3, rows.Count);
            Assert.Equal(5m, rows.Single(r => r.ItemId == 100 && r.WarehouseId == 1).Qty);
            Assert.Equal(7m, rows.Single(r => r.ItemId == 200 && r.WarehouseId == 1).Qty);
            Assert.Equal(3m, rows.Single(r => r.ItemId == 100 && r.WarehouseId == 2).Qty);
        }

        // THE VALUE BACKED OUT IS THE VALUE THIS INVOICE PUT IN. Two receipts at different costs
        // weight by quantity; the reversals carry no cost of their own into the answer.
        [Fact]
        public async Task The_cost_is_the_quantity_weighted_average_of_the_receipts()
        {
            Move(Invoice, "PurchaseInvoice", 1, 10, unitCost: 2m);
            Move(Invoice, "PurchaseInvoice", 1, 30, unitCost: 6m);
            Move(Invoice, "PurchaseInvoiceEdit", -1, 10, unitCost: 999m);   // must not skew it

            var row = Assert.Single(await Service().OutstandingStockAsync(CompanyA, Invoice));

            Assert.Equal(30m, row.Qty);
            Assert.Equal(5m, row.Cost);        // (10*2 + 30*6) / 40
        }
    }
}
