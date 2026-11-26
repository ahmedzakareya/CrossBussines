using System.Reflection;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests;

// The payable boundary deliberately persists arranged invoice/journal identities. These tests prove conversion
// transaction/tracking behavior, not GL/stock arithmetic; real-writer SQL qualification is separate.
public sealed class ConstructionR7ProcurementProvenanceTests
{
    [Theory]
    [InlineData("0.95", false)]
    [InlineData("0.99", false)]
    [InlineData("0.99", true)]
    public async Task Matching_tolerance_cannot_invoice_unreceived_project_stock(string received, bool inflatedCounter)
    {
        using var f = new ConstructionTestFixture();
        var setup = await ConstructionR7ReceiptWitnessTests.Admitted(f.Db);
        await ConstructionR7StockInvoiceTests.Accounts(f.Db, setup);
        var quantity = decimal.Parse(received, System.Globalization.CultureInfo.InvariantCulture);
        await ConstructionR7ReceiptWitnessTests.ReceiptFixture(f.Db, setup, quantity);
        if (inflatedCounter)
            await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x => x.SetProperty(l => l.ReceivedQty, 1m));
        await using (var read = f.NewContext())
            Assert.True((await new ThreeWayMatchService(read).CheckPoAsync(1, setup.Order)).Ok);
        var reservationBefore = System.Text.Json.JsonSerializer.Serialize(
            await f.Db.Set<CrossBuy.Models.Context.Construction.ConstructionCommitmentReservation>().AsNoTracking().SingleAsync());
        var admissionBefore = System.Text.Json.JsonSerializer.Serialize(
            await f.Db.Set<CrossBuy.Models.Context.Construction.ConstructionPurchaseAdmission>().AsNoTracking().SingleAsync());
        await using (var write = f.NewContext())
        {
            var (service, probe) = Service(write);
            var result = await service.ConvertToInvoiceAsync(1, setup.Order, "r7-exact-receiving");
            Assert.False(result.ok); Assert.Null(result.invoiceId);
            Assert.Contains("exact receipt coverage", result.error); Assert.Equal(0, probe.Calls);
        }
        await using var verify = f.NewContext();
        Assert.Empty(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<PurchaseOrderInvoiceLink>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<PurchaseOrderInvoiceLineLink>().AsNoTracking().ToListAsync());
        Assert.Equal("Approved", (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(reservationBefore, System.Text.Json.JsonSerializer.Serialize(
            await verify.Set<CrossBuy.Models.Context.Construction.ConstructionCommitmentReservation>().AsNoTracking().SingleAsync()));
        Assert.Equal(admissionBefore, System.Text.Json.JsonSerializer.Serialize(
            await verify.Set<CrossBuy.Models.Context.Construction.ConstructionPurchaseAdmission>().AsNoTracking().SingleAsync()));
        Assert.Empty(await verify.Set<CrossBuy.Models.Context.Construction.ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
        // Native writer tests separately prove calculated exposure on SQL Server's fixed decimal scale.
    }

    [Fact]
    public async Task Header_success_without_a_writer_line_receipt_cannot_close_an_order()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f);
        var (service, probe) = Service(f.Db); var po = await Order(service, ids); probe.OmitLineReceipt = true;
        var result = await service.ConvertToInvoiceAsync(1, po.ID, "r7-source");
        Assert.False(result.ok); Assert.Contains("writer line provenance", result.error);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<PurchaseOrderInvoiceLineLink>().AsNoTracking().ToListAsync());
        Assert.Equal("Approved", (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
    }
    private static async Task<(int Project, int Vendor, int Warehouse)> Seed(ConstructionTestFixture f)
    {
        var ids = await ConstructionR7BudgetVersionTests.Seed(f);
        var vendor = new Vendor { CompanyID = 1, Name = "Supplier", NameEn = "Supplier", IsActive = true };
        var warehouse = new Warehouse { CompanyID = 1, Code = "WH", Name = "Warehouse", IsActive = true };
        f.Db.AddRange(vendor, warehouse); await f.Db.SaveChangesAsync(); return (ids.Project, vendor.ID, warehouse.ID);
    }
    private static (ProcurementService Service, PayableProbe Probe) Service(CrossDbContext db)
    {
        var payable = DispatchProxy.Create<IPayableService, PayableProbe>(); var probe = (PayableProbe)(object)payable; probe.Db = db;
        var currency = new CurrencyService(db);
        return (new ProcurementService(db, null!, payable, null!, new ThreeWayMatchService(db), null!, currency, new CurrencyRounding(db, currency, new Localizer()), null!), probe);
    }
    private static async Task<PurchaseOrder> Order(ProcurementService service, (int Project, int Vendor, int Warehouse) ids)
    {
        var result = await service.CreatePurchaseOrderAsync(1, ids.Vendor, ids.Warehouse, DateTime.Today, null, "Source test",
            new() { new() { ItemDescription = "Service", Qty = 1, UnitPrice = 12.345m } }, "r7-source", ids.Project);
        Assert.True(result.ok, result.error); return result.po!;
    }

    [Fact]
    public async Task New_orders_stamp_the_currency_used_for_pricing_and_a_conversion_retry_returns_the_same_invoice()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var (service, probe) = Service(f.Db); var po = await Order(service, ids);
        Assert.Equal((await f.Db.Companies.AsNoTracking().SingleAsync()).DefaultCurrencyId, po.CurrencyId);
        Assert.Null(po.ExchangeRate); // Never freeze 1 against a future, different functional currency.
        var first = await service.ConvertToInvoiceAsync(1, po.ID, "r7-source"); Assert.True(first.ok, first.error);
        var retry = await service.ConvertToInvoiceAsync(1, po.ID, "r7-source"); Assert.True(retry.ok, retry.error);
        Assert.Equal(first.invoiceId, retry.invoiceId); Assert.Equal(1, probe.Calls);
        await using var verify = f.NewContext(); var link = await verify.Set<PurchaseOrderInvoiceLink>().AsNoTracking().SingleAsync();
        Assert.Equal(po.ID, link.PurchaseOrderId); Assert.Equal(first.invoiceId, link.PurchaseInvoiceId);
        Assert.Single(await verify.PurchaseInvoices.AsNoTracking().ToListAsync()); Assert.Equal("Closed", (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_link_save_rolls_back_the_invoice_and_preserves_the_callers_tracking(bool borrowed)
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var po = await Order(Service(f.Db).Service, ids);
        var holder = new CompanyScopeHolder(); holder.Set(1, null);
        await using var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlite(f.Db.Database.GetDbConnection())
            .AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance), new FailLink()).Options, holder);
        var tracked = await db.Projects.SingleAsync(x => x.ID == ids.Project); var before = tracked.Name;
        var (service, probe) = Service(db);
        await using var outer = borrowed ? await db.Database.BeginTransactionAsync() : null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConvertToInvoiceAsync(1, po.ID, "r7-source"));
        Assert.Equal("Injected conversion link failure", error.Message); Assert.Equal(1, probe.Calls);
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State); Assert.Equal(before, tracked.Name);
        tracked.Name = "Caller continues safely"; await db.SaveChangesAsync();
        if (outer != null) await outer.CommitAsync();
        await using var verify = f.NewContext(); Assert.Empty(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<PurchaseOrderInvoiceLink>().AsNoTracking().ToListAsync());
        Assert.Equal("Approved", (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("Caller continues safely", (await verify.Projects.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task Pending_caller_changes_are_neither_saved_nor_discarded_by_a_refused_conversion()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var (service, probe) = Service(f.Db); var po = await Order(service, ids);
        var project = await f.Db.Projects.SingleAsync(); var original = project.Name; project.Name = "Pending caller edit";
        var result = await service.ConvertToInvoiceAsync(1, po.ID, "r7-source"); Assert.False(result.ok); Assert.Equal(0, probe.Calls);
        Assert.Equal(EntityState.Modified, f.Db.Entry(project).State);
        await using var verify = f.NewContext(); Assert.Equal(original, (await verify.Projects.AsNoTracking().SingleAsync()).Name);
        Assert.Empty(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_foreign_invoice_write_is_denied_and_the_conversion_is_rolled_back()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var (service, probe) = Service(f.Db); var po = await Order(service, ids);
        probe.InvoiceCompany = 2;
        await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => service.ConvertToInvoiceAsync(1, po.ID, "r7-source"));
        await using var verify = f.NewContext(); Assert.Empty(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<PurchaseOrderInvoiceLink>().AsNoTracking().ToListAsync());
        Assert.Equal("Approved", (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Closed_legacy_orders_without_a_proven_link_are_not_guessed_from_notes()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var (service, probe) = Service(f.Db); var po = await Order(service, ids);
        po.Status = "Closed"; await f.Db.SaveChangesAsync();
        Assert.False((await service.ConvertToInvoiceAsync(1, po.ID, "r7-source")).ok); Assert.Equal(0, probe.Calls);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<PurchaseOrderInvoiceLink>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Conversion_uses_database_line_values_even_when_an_older_line_is_tracked()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var (service, probe) = Service(f.Db); var po = await Order(service, ids);
        var oldLine = Assert.Single(po.Lines); Assert.Equal(12.345m, oldLine.UnitPrice);
        await using (var other = f.NewContext())
        {
            await other.PurchaseOrderLines.Where(x => x.ID == oldLine.ID).ExecuteUpdateAsync(x => x.SetProperty(y => y.UnitPrice, 15.678m).SetProperty(y => y.LineTotal, 15.678m));
            await other.PurchaseOrders.Where(x => x.ID == po.ID).ExecuteUpdateAsync(x => x.SetProperty(y => y.SubTotal, 15.678m).SetProperty(y => y.GrandTotal, 15.678m));
        }
        Assert.True((await service.ConvertToInvoiceAsync(1, po.ID, "r7-source")).ok);
        Assert.Equal(15.678m, Assert.Single(probe.LastLines!).UnitPrice);
        await using var verify = f.NewContext(); Assert.Equal(15.678m, (await verify.PurchaseInvoices.AsNoTracking().SingleAsync()).GrandTotal);
    }

    [Fact]
    public async Task An_unresolved_or_foreign_company_is_refused_before_any_invoice_work()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var (service, probe) = Service(f.Db); var po = await Order(service, ids);
        foreach (var company in new[] { 0, 2 }) Assert.False((await service.ConvertToInvoiceAsync(company, po.ID, "r7-source")).ok);
        Assert.Equal(0, probe.Calls);
        await using var verify = f.NewContext(); Assert.Empty(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_real_write_guard_refuses_a_foreign_conversion_link()
    {
        using var f = new ConstructionTestFixture();
        f.Db.Add(new PurchaseOrderInvoiceLink { CompanyID = 2, PurchaseOrderId = 100, PurchaseInvoiceId = 200, CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => f.Db.SaveChangesAsync()); f.Db.ChangeTracker.Clear();
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<PurchaseOrderInvoiceLink>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Receipt_refuses_invalid_scope_or_source_relationships_before_any_stock_work()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var (service, _) = Service(f.Db); var po = await Order(service, ids);
        var requests = new[]
        {
            (Company: 0, Vendor: ids.Vendor, Warehouse: ids.Warehouse, Po: (int?)null, Item: 1, Line: (int?)null),
            (Company: 2, Vendor: ids.Vendor, Warehouse: ids.Warehouse, Po: (int?)null, Item: 1, Line: (int?)null),
            (Company: 1, Vendor: ids.Vendor, Warehouse: int.MaxValue, Po: (int?)null, Item: 1, Line: (int?)null),
            (Company: 1, Vendor: ids.Vendor, Warehouse: ids.Warehouse, Po: (int?)null, Item: 1, Line: (int?)1),
            (Company: 1, Vendor: int.MaxValue, Warehouse: ids.Warehouse, Po: (int?)po.ID, Item: 1, Line: (int?)1),
            (Company: 1, Vendor: ids.Vendor, Warehouse: ids.Warehouse, Po: (int?)po.ID, Item: 1, Line: (int?)int.MaxValue),
        };
        foreach (var request in requests)
        {
            var result = await service.CreateReceiptAsync(request.Company, request.Vendor, request.Warehouse, request.Po, DateTime.Today, "Refuse invalid source",
                new() { new() { ItemId = request.Item, Qty = 1, UnitCost = 10, PurchaseOrderLineId = request.Line } }, "r7-test");
            Assert.False(result.ok);
        }
        await using var verify = f.NewContext(); Assert.Empty(await verify.GoodsReceipts.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_refused_receipt_does_not_save_or_discard_pending_caller_changes()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f); var warehouse = await f.Db.Warehouses.SingleAsync(); var original = warehouse.Name;
        warehouse.Name = "Unsaved caller edit";
        var result = await Service(f.Db).Service.CreateReceiptAsync(1, ids.Vendor, ids.Warehouse, null, DateTime.Today, null,
            new() { new() { ItemId = 1, Qty = 1, UnitCost = 10 } }, "r7-test");
        Assert.False(result.ok); Assert.Equal(EntityState.Modified, f.Db.Entry(warehouse).State);
        await using var verify = f.NewContext(); Assert.Equal(original, (await verify.Warehouses.AsNoTracking().SingleAsync()).Name); Assert.Empty(await verify.GoodsReceipts.AsNoTracking().ToListAsync());
    }

    public class PayableProbe : DispatchProxy
    {
        public CrossDbContext Db = null!;
        public int Calls;
        public int InvoiceCompany = 1;
        public List<PurchaseLineInput>? LastLines;
        public bool OmitLineReceipt;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IPayableService.CreatePurchaseInvoiceAsync)) throw new InvalidOperationException("Unexpected payable dependency call");
            return Create(args!);
        }
        private async Task<(bool ok, string? error, PurchaseInvoice? inv)> Create(object?[] args)
        {
            Calls++; LastLines = (List<PurchaseLineInput>)args[3]!;
            var total = LastLines.Sum(x => x.Qty * x.UnitPrice - x.DiscountAmount);
            var invoice = new PurchaseInvoice { CompanyID = InvoiceCompany, VendorId = (int)args[1]!, InvoiceDate = (DateTime)args[2]!,
                CurrencyId = (int?)args[6], ProjectId = (int?)args[8], Status = "Posted", SubTotal = total, GrandTotal = total };
            for (var i = 0; i < LastLines.Count; i++)
                invoice.Lines.Add(new PurchaseInvoiceLine { LineNo = i + 1, Qty = LastLines[i].Qty, UnitPrice = LastLines[i].UnitPrice,
                    LineTotal = LastLines[i].Qty * LastLines[i].UnitPrice - LastLines[i].DiscountAmount });
            Db.Add(invoice); await Db.SaveChangesAsync();
            // Arrangement only: this unit double does not claim to post or qualify financial accounting.
            var entry = new JournalEntry { CompanyID = InvoiceCompany, SourceType = "PurchaseInvoice", SourceId = invoice.ID, Status = "Posted" };
            foreach (var line in invoice.Lines) entry.Lines.Add(new JournalEntryLine { LineNo = line.LineNo, Debit = line.LineTotal });
            Db.Add(entry); await Db.SaveChangesAsync(); invoice.JournalEntryId = entry.ID; await Db.SaveChangesAsync();
            if (!OmitLineReceipt)
            {
                // The receipt is deliberately internal, outside HTTP/model binding and public invoice serialization.
                var property = typeof(PurchaseInvoice).GetProperty("CreatedNetLineage", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var receipt = (System.Collections.IList)property.GetValue(invoice)!;
                var receiptType = property.PropertyType.GetGenericArguments()[0];
                foreach (var line in invoice.Lines)
                    receipt.Add(Activator.CreateInstance(receiptType, line.LineNo - 1, line.ID, entry.Lines.Single(x => x.LineNo == line.LineNo).ID));
            }
            return (true, null, invoice);
        }
    }
    private sealed class FailLink : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PurchaseOrderInvoiceLink>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("Injected conversion link failure");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Localizer : IStringLocalizer<CrossBuy.SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
}
