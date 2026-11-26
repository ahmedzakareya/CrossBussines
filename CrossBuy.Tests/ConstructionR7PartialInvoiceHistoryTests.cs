using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

// SQLite arrangements prove fresh-reader refusal behavior, not SQL triggers or the real payable writer.
public sealed class ConstructionR7PartialInvoiceHistoryTests
{
    private static async Task<ConstructionInvoiceScope> Arrange(CrossDbContext db)
    {
        var setup = await ConstructionR7StockInvoiceTests.Arranged(db);
        await db.Set<PurchaseOrderInvoiceLineLink>().ExecuteDeleteAsync();
        await db.Set<PurchaseOrderInvoiceLink>().ExecuteDeleteAsync();
        await db.PurchaseOrders.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Received"));
        await db.GoodsReceipts.ExecuteUpdateAsync(x => x.SetProperty(p => p.InvoiceId, (int?)null));
        var invoice = await db.PurchaseInvoices.AsNoTracking().SingleAsync();
        var line = await db.PurchaseInvoiceLines.AsNoTracking().SingleAsync();
        var receipt = await db.GoodsReceiptLines.AsNoTracking().SingleAsync();
        var gl = await db.JournalEntryLines.AsNoTracking().SingleAsync(x => x.JournalEntryId == invoice.JournalEntryId && x.LineNo == 1);
        var header = new ConstructionPartialInvoice { CompanyID = 1, ProjectId = setup.Seed.Project, PurchaseOrderId = setup.Order,
            PurchaseInvoiceId = invoice.ID, JournalEntryId = invoice.JournalEntryId!.Value, RequestId = Guid.NewGuid(),
            RequestHash = new string('A', 64), SourceVersion = new string('B', 64), CreatedBy = 7, CreatedAt = DateTime.UtcNow, IsSealed = true };
        db.Add(header); await db.SaveChangesAsync();
        db.Add(new ConstructionPartialInvoiceSlice { CompanyID = 1, ProjectId = header.ProjectId, HeaderId = header.ID,
            PurchaseOrderId = setup.Order, PurchaseOrderLineId = setup.Lines.Single(), PurchaseInvoiceId = invoice.ID,
            PurchaseInvoiceLineId = line.ID, JournalEntryId = gl.JournalEntryId, JournalEntryLineId = gl.ID,
            GoodsReceiptId = receipt.GoodsReceiptId, GoodsReceiptLineId = receipt.ID, Quantity = 1 });
        await db.SaveChangesAsync();
        return new(1, setup.Seed.Project, setup.Order);
    }

    [Fact]
    public async Task Complete_sealed_history_returns_original_receipt_quantities_and_commercial_values()
    {
        using var fixture = new ConstructionTestFixture(); var scope = await Arrange(fixture.Db);
        await using var read = fixture.NewContext(); await using var tx = await read.Database.BeginTransactionAsync();
        var result = await ConstructionPartialInvoiceHistoryReader.ReadAsync(read, scope, 3);
        Assert.True(result.Ok, result.Code);
        Assert.Equal(1m, Assert.Single(result.Quantities!).Billed);
        var value = Assert.Single(Assert.Single(result.Values!).Value);
        Assert.Equal(new ConstructionInvoiceCommercialHistory(1, 0, 10, 2), value);
        Assert.False(read.ChangeTracker.HasChanges()); Assert.Empty(read.ChangeTracker.Entries());
    }

    [Fact]
    public async Task A_transaction_and_resolved_owned_scope_are_required()
    {
        using var fixture = new ConstructionTestFixture(); var scope = await Arrange(fixture.Db);
        await using var read = fixture.NewContext();
        Assert.False((await ConstructionPartialInvoiceHistoryReader.ReadAsync(read, scope, 3)).Ok);
        await using var tx = await read.Database.BeginTransactionAsync();
        Assert.False((await ConstructionPartialInvoiceHistoryReader.ReadAsync(read, scope with { CompanyId = 2 }, 3)).Ok);
        Assert.False((await ConstructionPartialInvoiceHistoryReader.ReadAsync(read, scope, 9)).Ok);
    }

    [Theory]
    [InlineData("unsealed")][InlineData("hash")][InlineData("orphan")][InlineData("receipt-status")]
    [InlineData("receipt-quantity")][InlineData("receipt-link")][InlineData("receipt-unit")]
    [InlineData("price")][InlineData("tax")][InlineData("invoice-total")][InlineData("invoice-scope")]
    [InlineData("journal-reversal")][InlineData("journal-source")][InlineData("net-gl")]
    [InlineData("balance")][InlineData("vat-account")][InlineData("slice-quantity")]
    public async Task Changed_parent_or_incomplete_history_returns_no_usable_balances(string kind)
    {
        using var fixture = new ConstructionTestFixture(); var scope = await Arrange(fixture.Db); var db = fixture.Db;
        var invoice = await db.PurchaseInvoices.AsNoTracking().SingleAsync();
        if (kind == "unsealed") await db.Set<ConstructionPartialInvoice>().ExecuteUpdateAsync(x => x.SetProperty(p => p.IsSealed, false));
        if (kind == "hash") await db.Set<ConstructionPartialInvoice>().ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceVersion, "invalid"));
        if (kind == "orphan") await db.Set<ConstructionPartialInvoiceSlice>().ExecuteUpdateAsync(x => x.SetProperty(p => p.HeaderId, 999));
        if (kind == "receipt-status") await db.GoodsReceipts.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        if (kind == "receipt-quantity") await db.GoodsReceiptLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.Qty, .5m));
        if (kind == "receipt-link") await db.GoodsReceipts.ExecuteUpdateAsync(x => x.SetProperty(p => p.InvoiceId, (int?)invoice.ID));
        if (kind == "receipt-unit") await db.GoodsReceiptLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.UoMId, (int?)999));
        if (kind == "price") await db.PurchaseInvoiceLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.UnitPrice, 11m));
        if (kind == "tax") await db.PurchaseInvoiceLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.TaxRate, 0m));
        if (kind == "invoice-total") await db.PurchaseInvoices.ExecuteUpdateAsync(x => x.SetProperty(p => p.SubTotal, 9m));
        if (kind == "invoice-scope") await db.PurchaseInvoices.ExecuteUpdateAsync(x => x.SetProperty(p => p.ProjectId, (int?)999));
        if (kind == "journal-reversal") await db.JournalEntries.Where(x => x.ID == invoice.JournalEntryId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Reversed"));
        if (kind == "journal-source") await db.JournalEntries.Where(x => x.ID == invoice.JournalEntryId).ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceId, (int?)999));
        if (kind == "net-gl") await db.JournalEntryLines.Where(x => x.JournalEntryId == invoice.JournalEntryId && x.LineNo == 1).ExecuteUpdateAsync(x => x.SetProperty(p => p.ProjectId, (int?)999));
        if (kind == "balance") await db.JournalEntryLines.Where(x => x.JournalEntryId == invoice.JournalEntryId && x.Credit > 0).ExecuteUpdateAsync(x => x.SetProperty(p => p.Credit, 11m));
        if (kind == "vat-account") await db.Accounts.Where(x => x.Code == "110401").ExecuteUpdateAsync(x => x.SetProperty(p => p.Code, "OTHER"));
        if (kind == "slice-quantity") await db.Set<ConstructionPartialInvoiceSlice>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Quantity, 2m));
        await using var read = fixture.NewContext(); await using var tx = await read.Database.BeginTransactionAsync();
        var result = await ConstructionPartialInvoiceHistoryReader.ReadAsync(read, scope, 3);
        Assert.False(result.Ok); Assert.Null(result.Quantities); Assert.Null(result.Values);
    }
}
