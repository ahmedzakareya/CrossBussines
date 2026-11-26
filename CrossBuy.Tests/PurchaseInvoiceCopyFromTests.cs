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
    // COPYING LINES OFF AN EARLIER INVOICE BY THE SAME VENDOR.
    //
    // WHY THESE ARE BEHAVIOURAL AND NOT A GREP. The sibling file AccountingInvoiceCompanyScopeTests
    // asserts the SHAPE of these actions - that the source contains a resolve and a predicate - and
    // that is the right tool for sweeping 109 call sites. It is the wrong tool for a new endpoint,
    // because a structural assertion fails OPEN: the text can be present and the query still wrong,
    // and nothing would notice. So these call the real action against a real database and read what
    // it actually returned.
    //
    // THE RISK THIS FEATURE CARRIES IS READING SOMEBODY ELSE'S PURCHASES. A picker that takes an id
    // from the browser and answers with invoice lines is a disclosure surface: the vendor id and the
    // invoice id both arrive from the client. Two separate questions have to hold - the right
    // company, and the right vendor - and a test that seeds only one company would prove neither.
    //
    // THE COUNTS ARE DELIBERATELY ASYMMETRIC. Company A gets two invoices and company B one, and the
    // vendors differ in how many they have, so an assertion cannot pass with the predicate inverted:
    // a symmetric fixture would return the same count either way and the test would prove nothing.
    // ============================================================================================
    public class PurchaseInvoiceCopyFromTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 77;
        private const int VendorOne = 10;
        private const int VendorTwo = 20;

        private readonly SqliteConnection _conn;
        private readonly CrossDbContext _db;
        private readonly CompanyScopeHolder _holder = new();

        public PurchaseInvoiceCopyFromTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();

            // THE GLOBAL FILTER IS DELIBERATELY TAKEN OUT OF THE WAY.
            //
            // PurchaseInvoice carries a company query filter, so a fixture that simply set the
            // holder to A would see B's rows disappear whether or not the action filters at all -
            // and every isolation assertion below would pass with the predicate deleted. Holding a
            // cross-company bypass makes the filter inert, which leaves the ACTION's own `CompanyID
            // == scope.CompanyId` as the only thing that can refuse. That is the predicate these
            // tests exist to pin, and it is the one that has to hold where the filter does not:
            // background and worker scopes resolve no holder at all.
            _holder.Set(CompanyA, null);
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

        // ---- fixture -----------------------------------------------------------------------

        private sealed class FixedCompany : IRequestCompanyResolver
        {
            private readonly CompanyResolution _r;
            public FixedCompany(CompanyResolution r) => _r = r;
            public Task<CompanyResolution> ResolveAsync(int? requestSuppliedCompanyId = null,
                CancellationToken cancellationToken = default) => Task.FromResult(_r);
        }

        /// Only the three dependencies these two actions touch are real. The rest are never
        /// reached, and passing null for them is the honest statement of that: a stub would imply
        /// the action consults something it does not.
        private AccountingController Controller(CompanyResolution scope) =>
            new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, _db, null!, null!, null!, null!, null!, null!,
                new FixedCompany(scope));

        private int Invoice(int companyId, int vendorId, string no, string date,
                            params (string desc, decimal qty, decimal price)[] lines)
        {
            var inv = new PurchaseInvoice
            {
                CompanyID = companyId,
                VendorId = vendorId,
                InvoiceNo = no,
                InvoiceDate = DateTime.Parse(date),
                Status = "Posted",
                GrandTotal = lines.Sum(l => l.qty * l.price),
            };
            _db.PurchaseInvoices.Add(inv);
            _db.SaveChanges();

            int n = 1;
            foreach (var l in lines)
            {
                _db.PurchaseInvoiceLines.Add(new PurchaseInvoiceLine
                {
                    PurchaseInvoiceId = inv.ID,
                    LineNo = n++,
                    ItemDescription = l.desc,
                    Qty = l.qty,
                    UnitPrice = l.price,
                    LineTotal = l.qty * l.price,
                    ExpenseAccountId = 500,
                });
            }
            _db.SaveChanges();
            return inv.ID;
        }

        /// The action's payload as JSON, which is what the browser actually receives - asserting on
        /// the anonymous object's properties would test the server's type, not its answer.
        private static JsonElement Payload(Microsoft.AspNetCore.Mvc.IActionResult result)
        {
            var json = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(result);
            return JsonSerializer.SerializeToElement(json.Value);
        }

        private static List<string> Numbers(JsonElement list) =>
            list.EnumerateArray().Select(e => e.GetProperty("no").GetString() ?? "").ToList();

        // ---- the list ----------------------------------------------------------------------

        [Fact]
        public async Task A_vendors_own_invoices_come_back_newest_first()
        {
            Invoice(CompanyA, VendorOne, "PI-OLD", "2026-01-05", ("sugar", 2, 10));
            Invoice(CompanyA, VendorOne, "PI-NEW", "2026-03-20", ("flour", 1, 50));

            var r = await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .PurchaseInvoicesForVendor(VendorOne);

            Assert.Equal(new[] { "PI-NEW", "PI-OLD" }, Numbers(Payload(r)));
        }

        // THE OTHER VENDOR'S INVOICES ARE NOT THIS VENDOR'S. The picker is opened from a chosen
        // vendor, so answering with the whole company's purchase history would be a different
        // feature - and one that puts another supplier's prices in front of the reader.
        [Fact]
        public async Task Another_vendors_invoices_are_not_returned()
        {
            Invoice(CompanyA, VendorOne, "PI-ONE", "2026-02-01", ("sugar", 2, 10));
            Invoice(CompanyA, VendorTwo, "PI-TWO-A", "2026-02-02", ("salt", 1, 5));
            Invoice(CompanyA, VendorTwo, "PI-TWO-B", "2026-02-03", ("salt", 1, 5));

            var mine = Numbers(Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .PurchaseInvoicesForVendor(VendorOne)));

            Assert.Equal(new[] { "PI-ONE" }, mine);
        }

        // ANOTHER COMPANY'S INVOICE IS NOT VISIBLE even for the same vendor id - vendor numbering is
        // per tenant, so the same id is a different supplier in another company's books.
        [Fact]
        public async Task Another_companys_invoices_are_not_returned()
        {
            Invoice(CompanyA, VendorOne, "A-1", "2026-02-01", ("sugar", 2, 10));
            Invoice(CompanyB, VendorOne, "B-1", "2026-02-05", ("sugar", 2, 10));
            Invoice(CompanyB, VendorOne, "B-2", "2026-02-06", ("sugar", 2, 10));

            var a = Numbers(Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .PurchaseInvoicesForVendor(VendorOne)));
            var b = Numbers(Payload(await Controller(CompanyResolution.Resolved(CompanyB, null, null))
                .PurchaseInvoicesForVendor(VendorOne)));

            // Asymmetric on purpose: A has one and B has two, so neither assertion could pass with
            // the company predicate inverted or dropped.
            Assert.Equal(new[] { "A-1" }, a);
            Assert.Equal(new[] { "B-2", "B-1" }, b);
        }

        [Fact]
        public async Task An_unresolved_caller_is_told_nothing()
        {
            Invoice(CompanyA, VendorOne, "PI-1", "2026-02-01", ("sugar", 2, 10));

            var r = await Controller(CompanyResolution.Unresolved("no session"))
                .PurchaseInvoicesForVendor(VendorOne);

            Assert.Empty(Payload(r).EnumerateArray());
        }

        [Fact]
        public async Task The_search_matches_the_invoice_number()
        {
            Invoice(CompanyA, VendorOne, "PI-2026-07", "2026-07-01", ("sugar", 2, 10));
            Invoice(CompanyA, VendorOne, "PI-2026-08", "2026-08-01", ("flour", 1, 50));

            var hit = Numbers(Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .PurchaseInvoicesForVendor(VendorOne, "2026-08")));

            Assert.Equal(new[] { "PI-2026-08" }, hit);
        }

        // ---- the lines ---------------------------------------------------------------------

        [Fact]
        public async Task The_lines_come_back_in_line_order_with_what_the_form_needs()
        {
            int id = Invoice(CompanyA, VendorOne, "PI-1", "2026-02-01",
                ("sugar", 2, 10), ("flour", 3, 20));

            var p = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .PurchaseInvoiceLinesForCopy(id));

            Assert.True(p.GetProperty("ok").GetBoolean());
            var lines = p.GetProperty("lines").EnumerateArray().ToList();
            Assert.Equal(2, lines.Count);
            Assert.Equal("sugar", lines[0].GetProperty("d").GetString());
            Assert.Equal(2m, lines[0].GetProperty("q").GetDecimal());
            Assert.Equal(10m, lines[0].GetProperty("p").GetDecimal());
            Assert.Equal("flour", lines[1].GetProperty("d").GetString());

            // Every field fillRow() reads must be present, or a copied line silently loses a column.
            foreach (var key in new[] { "itemId", "name", "wh", "d", "q", "p", "disc", "tax", "acc", "cc", "barcode" })
                Assert.True(lines[0].TryGetProperty(key, out _), "the payload is missing " + key);
        }

        // AN INVOICE ID FROM ANOTHER COMPANY RESOLVES TO NOTHING. This is the disclosure that
        // matters: the id travels on the query string, so guessing one must not return lines.
        [Fact]
        public async Task Another_companys_invoice_yields_no_lines()
        {
            int b = Invoice(CompanyB, VendorOne, "B-1", "2026-02-05", ("secret", 1, 999));

            var p = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .PurchaseInvoiceLinesForCopy(b));

            Assert.False(p.GetProperty("ok").GetBoolean());
            Assert.Empty(p.GetProperty("lines").EnumerateArray());
        }

        // THE ITEM IDENTIFIES THE LINE, AND THE DESCRIPTION IS SEPARATE FROM IT.
        //
        // The picker first showed only the description, which is free text: it is legitimately
        // blank, and on real data it was literally "0". Either way the reader was ticking a row
        // they could not identify. So the item's own name and barcode travel as their own fields,
        // and the description stays exactly as stored.
        //
        // NOT FOLDED TOGETHER. An earlier version substituted the item name when the description
        // was empty, which made a line with no description look as though it had one - and did
        // nothing at all for the "0" case, because "0" is not blank.
        [Fact]
        public async Task A_line_carries_its_items_name_and_barcode_separately_from_the_description()
        {
            _db.Items.Add(new Item
            {
                ID = 7, CompanyID = CompanyA,
                Name = "سكر ناعم", NameEn = "Fine sugar", Barcode = "6221034500119",
            });
            _db.SaveChanges();

            // the exact shape seen on the live invoice: a description that says nothing useful
            int id = Invoice(CompanyA, VendorOne, "PI-1", "2026-02-01", ("0", 3, 40));
            var line = _db.PurchaseInvoiceLines.Single(l => l.PurchaseInvoiceId == id);
            line.ItemId = 7;
            _db.SaveChanges();

            var p = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .PurchaseInvoiceLinesForCopy(id));
            var l0 = p.GetProperty("lines").EnumerateArray().Single();

            Assert.False(string.IsNullOrWhiteSpace(l0.GetProperty("itemName").GetString()));
            Assert.Equal("6221034500119", l0.GetProperty("barcode").GetString());
            Assert.Equal("0", l0.GetProperty("label").GetString());   // shown as stored, not dressed up
            Assert.Equal("0", l0.GetProperty("d").GetString());       // and what gets SAVED is faithful
        }

        // AN EXPENSE LINE HAS NO ITEM, and must say so rather than borrow one. Empty strings here
        // are what let the screen draw "no item" instead of a blank cell that reads as a failure.
        [Fact]
        public async Task A_line_with_no_item_carries_no_name_and_no_barcode()
        {
            int id = Invoice(CompanyA, VendorOne, "PI-1", "2026-02-01", ("Freight", 1, 250));

            var p = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .PurchaseInvoiceLinesForCopy(id));
            var l0 = p.GetProperty("lines").EnumerateArray().Single();

            Assert.Equal("", l0.GetProperty("itemName").GetString());
            Assert.Equal("", l0.GetProperty("barcode").GetString());
            Assert.Equal("Freight", l0.GetProperty("label").GetString());
        }

        // AN ITEM IN ANOTHER COMPANY IS NOT THIS LINE'S ITEM. The name lookup is a second query with
        // its own predicate, so it needs its own proof: borrowing a name across tenants would print
        // another company's product on this one's screen.
        [Fact]
        public async Task An_item_name_is_not_borrowed_from_another_company()
        {
            _db.Items.Add(new Item { ID = 7, CompanyID = CompanyB, Name = "سر تجاري", Barcode = "999" });
            _db.SaveChanges();

            int id = Invoice(CompanyA, VendorOne, "PI-1", "2026-02-01", ("x", 1, 10));
            var line = _db.PurchaseInvoiceLines.Single(l => l.PurchaseInvoiceId == id);
            line.ItemId = 7;
            _db.SaveChanges();

            var p = Payload(await Controller(CompanyResolution.Resolved(CompanyA, null, null))
                .PurchaseInvoiceLinesForCopy(id));
            var l0 = p.GetProperty("lines").EnumerateArray().Single();

            Assert.Equal("", l0.GetProperty("itemName").GetString());
            Assert.Equal("", l0.GetProperty("barcode").GetString());
        }

        [Fact]
        public async Task An_unresolved_caller_gets_no_lines()
        {
            int id = Invoice(CompanyA, VendorOne, "PI-1", "2026-02-01", ("sugar", 2, 10));

            var p = Payload(await Controller(CompanyResolution.Unresolved("no session"))
                .PurchaseInvoiceLinesForCopy(id));

            Assert.False(p.GetProperty("ok").GetBoolean());
        }
    }
}
