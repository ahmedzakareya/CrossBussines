using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // COPYING LINES OFF AN EARLIER INVOICE TO THE SAME CUSTOMER.
    //
    // The sales half of the same feature, and it gets its own tests rather than sharing the
    // purchase ones. The two endpoints LOOK alike and read different tables through different
    // predicates: CustomerId not VendorId, SalesInvoice not PurchaseInvoice, and `acc` means the
    // REVENUE account here where it means the expense account there. A test that covered one and
    // assumed the other would prove nothing about the half it skipped - which is exactly how a
    // copy-pasted endpoint ends up filtering on the wrong column.
    //
    // THE COUNTS ARE ASYMMETRIC, for the reason they are on the purchase side: a fixture that gave
    // both companies the same number of invoices would pass with the predicate inverted.
    // ============================================================================================
    public class SalesInvoiceCopyFromTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 77;
        private const int CustomerOne = 10;
        private const int CustomerTwo = 20;

        private readonly SqliteConnection _conn;
        private readonly CrossDbContext _db;
        private readonly CompanyScopeHolder _holder = new();

        public SalesInvoiceCopyFromTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(CompanyA, null);

            // SalesInvoice carries a company query filter, so the bypass is held for the same
            // reason as on the purchase side: without it, B's rows vanish whether or not the
            // action filters at all, and every isolation assertion would pass with the predicate
            // deleted.
            _holder.ApplyBypass(new CompanyBypassGrant
            {
                Kind = CompanyBypassKind.CrossCompanyAdministration,
                Reason = "test fixture: prove the action's own predicate, not the global filter",
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

        private sealed class FixedCompany : IRequestCompanyResolver
        {
            private readonly CompanyResolution _r;
            public FixedCompany(CompanyResolution r) => _r = r;
            public Task<CompanyResolution> ResolveAsync(int? requestSuppliedCompanyId = null,
                CancellationToken cancellationToken = default) => Task.FromResult(_r);
        }

        private AccountingController Controller(CompanyResolution scope) =>
            new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, _db, null!, null!, null!, null!, null!, null!,
                new FixedCompany(scope));

        private int Invoice(int companyId, int customerId, string no, string date,
                            params (string desc, decimal qty, decimal price)[] lines)
        {
            var inv = new SalesInvoice
            {
                CompanyID = companyId,
                CustomerId = customerId,
                InvoiceNo = no,
                InvoiceDate = DateTime.Parse(date),
                Status = "Posted",
                GrandTotal = lines.Sum(l => l.qty * l.price),
            };
            _db.SalesInvoices.Add(inv);
            _db.SaveChanges();

            int n = 1;
            foreach (var l in lines)
            {
                _db.SalesInvoiceLines.Add(new SalesInvoiceLine
                {
                    SalesInvoiceId = inv.ID,
                    LineNo = n++,
                    ItemDescription = l.desc,
                    Qty = l.qty,
                    UnitPrice = l.price,
                    LineTotal = l.qty * l.price,
                    RevenueAccountId = 400,
                });
            }
            _db.SaveChanges();
            return inv.ID;
        }

        private static JsonElement Payload(Microsoft.AspNetCore.Mvc.IActionResult result)
        {
            var json = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(result);
            return JsonSerializer.SerializeToElement(json.Value);
        }

        private static List<string> Numbers(JsonElement list) =>
            list.EnumerateArray().Select(e => e.GetProperty("no").GetString() ?? "").ToList();

        // ---- the list ----------------------------------------------------------------------

        [Fact]
        public async Task A_customers_own_invoices_come_back_newest_first()
        {
            Invoice(CompanyA, CustomerOne, "SI-OLD", "2026-01-05", ("sugar", 2, 10));
            Invoice(CompanyA, CustomerOne, "SI-NEW", "2026-03-20", ("flour", 1, 50));

            var r = await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .SalesInvoicesForCustomer(CustomerOne);

            Assert.Equal(new[] { "SI-NEW", "SI-OLD" }, Numbers(Payload(r)));
        }

        // ANOTHER CUSTOMER'S INVOICES ARE NOT THIS CUSTOMER'S - and on the sales side that is not
        // only untidy: it would show one buyer what another was charged.
        [Fact]
        public async Task Another_customers_invoices_are_not_returned()
        {
            Invoice(CompanyA, CustomerOne, "SI-ONE", "2026-02-01", ("sugar", 2, 10));
            Invoice(CompanyA, CustomerTwo, "SI-TWO-A", "2026-02-02", ("salt", 1, 5));
            Invoice(CompanyA, CustomerTwo, "SI-TWO-B", "2026-02-03", ("salt", 1, 5));

            var mine = Numbers(Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .SalesInvoicesForCustomer(CustomerOne)));

            Assert.Equal(new[] { "SI-ONE" }, mine);
        }

        [Fact]
        public async Task Another_companys_invoices_are_not_returned()
        {
            Invoice(CompanyA, CustomerOne, "A-1", "2026-02-01", ("sugar", 2, 10));
            Invoice(CompanyB, CustomerOne, "B-1", "2026-02-05", ("sugar", 2, 10));
            Invoice(CompanyB, CustomerOne, "B-2", "2026-02-06", ("sugar", 2, 10));

            var a = Numbers(Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .SalesInvoicesForCustomer(CustomerOne)));
            var b = Numbers(Payload(await Controller(CompanyResolution.Resolved(CompanyB, null, null))
                .SalesInvoicesForCustomer(CustomerOne)));

            Assert.Equal(new[] { "A-1" }, a);
            Assert.Equal(new[] { "B-2", "B-1" }, b);
        }

        [Fact]
        public async Task An_unresolved_caller_is_told_nothing()
        {
            Invoice(CompanyA, CustomerOne, "SI-1", "2026-02-01", ("sugar", 2, 10));

            Assert.Empty(Payload(await Controller(CompanyResolution.Unresolved("no session"))
                .SalesInvoicesForCustomer(CustomerOne)).EnumerateArray());
        }

        [Fact]
        public async Task The_search_matches_the_invoice_number()
        {
            Invoice(CompanyA, CustomerOne, "SI-2026-07", "2026-07-01", ("sugar", 2, 10));
            Invoice(CompanyA, CustomerOne, "SI-2026-08", "2026-08-01", ("flour", 1, 50));

            var hit = Numbers(Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .SalesInvoicesForCustomer(CustomerOne, "2026-08")));

            Assert.Equal(new[] { "SI-2026-08" }, hit);
        }

        // ---- the lines ---------------------------------------------------------------------

        // `acc` IS THE REVENUE ACCOUNT HERE. The two screens' save handlers read the same field
        // name and mean different columns, so this is the assertion that would catch an endpoint
        // copied from the purchase side and left pointing at the wrong one.
        [Fact]
        public async Task The_lines_carry_the_revenue_account_the_screen_reads()
        {
            int id = Invoice(CompanyA, CustomerOne, "SI-1", "2026-02-01", ("sugar", 2, 10));

            var p = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .SalesInvoiceLinesForCopy(id));
            var l0 = p.GetProperty("lines").EnumerateArray().Single();

            Assert.True(p.GetProperty("ok").GetBoolean());
            Assert.Equal(400, l0.GetProperty("acc").GetInt32());
            foreach (var key in new[] { "itemId", "name", "wh", "d", "q", "p", "disc", "tax", "acc", "uom", "barcode", "itemName", "label" })
                Assert.True(l0.TryGetProperty(key, out _), "the payload is missing " + key);
        }

        // THE UNIT TRAVELS. Losing it turns 5 boxes into 5 pieces at the box price - the figures
        // look copied and the quantity is wrong by the pack factor.
        [Fact]
        public async Task The_unit_of_measure_travels_with_the_line()
        {
            int id = Invoice(CompanyA, CustomerOne, "SI-1", "2026-02-01", ("sugar", 5, 90));
            var line = _db.SalesInvoiceLines.Single(l => l.SalesInvoiceId == id);
            line.UoMId = 12;
            _db.SaveChanges();

            var p = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .SalesInvoiceLinesForCopy(id));

            Assert.Equal(12, p.GetProperty("lines").EnumerateArray().Single()
                .GetProperty("uom").GetInt32());
        }

        [Fact]
        public async Task A_line_carries_its_items_name_and_barcode_separately_from_the_description()
        {
            _db.Items.Add(new Item
            {
                ID = 7, CompanyID = CompanyA,
                Name = "سكر ناعم", NameEn = "Fine sugar", Barcode = "6221034500119",
            });
            _db.SaveChanges();

            int id = Invoice(CompanyA, CustomerOne, "SI-1", "2026-02-01", ("0", 3, 40));
            var line = _db.SalesInvoiceLines.Single(l => l.SalesInvoiceId == id);
            line.ItemId = 7;
            _db.SaveChanges();

            var l0 = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .SalesInvoiceLinesForCopy(id)).GetProperty("lines").EnumerateArray().Single();

            Assert.False(string.IsNullOrWhiteSpace(l0.GetProperty("itemName").GetString()));
            Assert.Equal("6221034500119", l0.GetProperty("barcode").GetString());
            Assert.Equal("0", l0.GetProperty("label").GetString());
            Assert.Equal("0", l0.GetProperty("d").GetString());
        }

        [Fact]
        public async Task An_item_name_is_not_borrowed_from_another_company()
        {
            _db.Items.Add(new Item { ID = 7, CompanyID = CompanyB, Name = "سر تجاري", Barcode = "999" });
            _db.SaveChanges();

            int id = Invoice(CompanyA, CustomerOne, "SI-1", "2026-02-01", ("x", 1, 10));
            var line = _db.SalesInvoiceLines.Single(l => l.SalesInvoiceId == id);
            line.ItemId = 7;
            _db.SaveChanges();

            var l0 = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .SalesInvoiceLinesForCopy(id)).GetProperty("lines").EnumerateArray().Single();

            Assert.Equal("", l0.GetProperty("itemName").GetString());
            Assert.Equal("", l0.GetProperty("barcode").GetString());
        }

        // AN INVOICE ID FROM ANOTHER COMPANY RESOLVES TO NOTHING. The id arrives from the browser,
        // so guessing one must not hand back another tenant's prices.
        [Fact]
        public async Task Another_companys_invoice_yields_no_lines()
        {
            int b = Invoice(CompanyB, CustomerOne, "B-1", "2026-02-05", ("secret", 1, 999));

            var p = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .SalesInvoiceLinesForCopy(b));

            Assert.False(p.GetProperty("ok").GetBoolean());
            Assert.Empty(p.GetProperty("lines").EnumerateArray());
        }

        [Fact]
        public async Task An_unresolved_caller_gets_no_lines()
        {
            int id = Invoice(CompanyA, CustomerOne, "SI-1", "2026-02-01", ("sugar", 2, 10));

            Assert.False(Payload(await Controller(CompanyResolution.Unresolved("no session"))
                .SalesInvoiceLinesForCopy(id)).GetProperty("ok").GetBoolean());
        }
    }
}
