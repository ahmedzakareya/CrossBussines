using System.Text.RegularExpressions;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

// Storage qualification only. Invoice/GL records below are explicit arrangements, not payable-writer proof.
// The probe strips unrelated model FKs; this test recreates BOTH new tables from authored DDL with real FKs/triggers.
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7PartialInvoiceProvenanceSqlTests(UatSqlProbeFixture sql) : IAsyncLifetime
{
    private UatSqlProbeFixture.ProbeDatabase? probe;
    private ConstructionR7PurchaseApprovalTests.Setup setup = null!;
    private string script = "";
    private int invoice;
    public async Task InitializeAsync()
    {
        if (!sql.Available) return;
        probe = await sql.CreateProbeDatabaseAsync("R7PARTIAL");
        try
        {
            using var db = sql.ContextFor(probe, 1);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionPartialInvoiceSlices; DROP TABLE dbo.ConstructionPartialInvoices;");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql/construction_r7_partial_invoice_provenance.sql"));
            await Apply(db); await Apply(db);
            setup = await ConstructionR7StockInvoiceTests.Arranged(db);
            invoice = await PrepareInvoice(db, .4m, create: false);
        }
        catch (Exception original)
        {
            try { await DisposeAsync(); probe = null; } catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }
    public async Task DisposeAsync() { if (probe != null) await sql.DropProbeDatabaseAsync(probe); }
    private async Task Apply(CrossDbContext db)
    {
        foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
            if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
    }
    private async Task<int> PrepareInvoice(CrossDbContext db, decimal quantity, bool create = true)
    {
        if (create) await ConstructionR7StockInvoiceTests.Invoice(db, setup, includeLegacyProvenance: false);
        var inv = await db.PurchaseInvoices.AsNoTracking().OrderByDescending(x => x.ID).FirstAsync();
        await db.Set<PurchaseOrderInvoiceLineLink>().ExecuteDeleteAsync();
        await db.Set<PurchaseOrderInvoiceLink>().ExecuteDeleteAsync();
        await db.GoodsReceipts.ExecuteUpdateAsync(x => x.SetProperty(p => p.InvoiceId, (int?)null));
        await db.PurchaseOrders.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Received"));
        await db.PurchaseInvoiceLines.Where(x => x.PurchaseInvoiceId == inv.ID).ExecuteUpdateAsync(x => x
            .SetProperty(p => p.Qty, quantity).SetProperty(p => p.LineTotal, quantity * 10));
        await db.PurchaseInvoices.Where(x => x.ID == inv.ID).ExecuteUpdateAsync(x => x
            .SetProperty(p => p.SubTotal, quantity * 10).SetProperty(p => p.TaxTotal, quantity * 2).SetProperty(p => p.GrandTotal, quantity * 12)
            .SetProperty(p => p.SubTotalBase, quantity * 10).SetProperty(p => p.TaxTotalBase, quantity * 2).SetProperty(p => p.GrandTotalBase, quantity * 12));
        await db.JournalEntryLines.Where(x => x.JournalEntryId == inv.JournalEntryId && x.LineNo == 1).ExecuteUpdateAsync(x => x.SetProperty(p => p.Debit, quantity * 10));
        await db.JournalEntryLines.Where(x => x.JournalEntryId == inv.JournalEntryId && x.LineNo == 2).ExecuteUpdateAsync(x => x.SetProperty(p => p.Debit, quantity * 2));
        await db.JournalEntryLines.Where(x => x.JournalEntryId == inv.JournalEntryId && x.LineNo == 3).ExecuteUpdateAsync(x => x.SetProperty(p => p.Credit, quantity * 12));
        db.ChangeTracker.Clear();
        return inv.ID;
    }
    private async Task<ConstructionPartialInvoice> Header(CrossDbContext db, int inv, Guid? request = null)
    {
        var source = await db.PurchaseInvoices.AsNoTracking().SingleAsync(x => x.ID == inv);
        var header = new ConstructionPartialInvoice { CompanyID = 1, ProjectId = setup.Seed.Project, PurchaseOrderId = setup.Order,
            PurchaseInvoiceId = inv, JournalEntryId = source.JournalEntryId!.Value, RequestId = request ?? Guid.NewGuid(),
            RequestHash = new string('A', 64), SourceVersion = new string('B', 64), CreatedBy = 7, CreatedAt = DateTime.UtcNow };
        db.Add(header); await db.SaveChangesAsync(); return header;
    }
    private async Task<ConstructionPartialInvoiceSlice> Slice(CrossDbContext db, ConstructionPartialInvoice header, decimal quantity)
    {
        var receipt = await db.GoodsReceiptLines.AsNoTracking().SingleAsync();
        var line = await db.PurchaseInvoiceLines.AsNoTracking().SingleAsync(x => x.PurchaseInvoiceId == header.PurchaseInvoiceId);
        var journal = await db.JournalEntryLines.AsNoTracking().SingleAsync(x => x.JournalEntryId == header.JournalEntryId && x.LineNo == 1);
        return new() { HeaderId = header.ID, CompanyID = header.CompanyID, ProjectId = header.ProjectId, PurchaseOrderId = header.PurchaseOrderId,
            PurchaseInvoiceId = header.PurchaseInvoiceId, JournalEntryId = header.JournalEntryId, PurchaseOrderLineId = setup.Lines.Single(),
            GoodsReceiptId = receipt.GoodsReceiptId, GoodsReceiptLineId = receipt.ID, PurchaseInvoiceLineId = line.ID,
            JournalEntryLineId = journal.ID, Quantity = quantity };
    }
    private async Task<ConstructionPartialInvoice> Record(CrossDbContext db, int inv, decimal quantity)
    {
        var header = await Header(db, inv); db.Add(await Slice(db, header, quantity)); await db.SaveChangesAsync();
        header.IsSealed = true; await db.SaveChangesAsync(); return header;
    }
    private static void Error(Exception exception, params int[] numbers)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is SqlException sqlError) { Assert.Contains(sqlError.Number, numbers); return; }
        Assert.Fail("Expected a native SQL constraint/trigger error");
    }

    [SkippableFact]
    public async Task Multiple_partial_invoices_keep_separate_immutable_receipt_slices_and_ddl_is_rerunnable()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using (var db = sql.ContextFor(probe!, 1))
        {
            await Record(db, invoice, .4m);
            var second = await PrepareInvoice(db, .6m);
            await Record(db, second, .6m);
            await db.Database.OpenConnectionAsync(); await Apply(db); await Apply(db);
        }
        using var read = sql.ContextFor(probe!, 1);
        Assert.Equal(2, await read.Set<ConstructionPartialInvoice>().CountAsync(x => x.IsSealed));
        Assert.Equal(1m, await read.Set<ConstructionPartialInvoiceSlice>().SumAsync(x => x.Quantity));
        Assert.Empty(await read.Set<PurchaseOrderInvoiceLink>().ToListAsync());
        Assert.Empty(await read.Set<PurchaseOrderInvoiceLineLink>().ToListAsync());
        Assert.Single(await read.StockMovements.ToListAsync()); // arrangement receipt, no extra stock created by lineage
    }

    [SkippableTheory][InlineData(false)][InlineData(true)]
    public async Task Empty_or_incomplete_invoice_cannot_be_sealed(bool incomplete)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var db = sql.ContextFor(probe!, 1); await using var tx = await db.Database.BeginTransactionAsync();
        var header = await Header(db, invoice);
        if (incomplete) { db.Add(await Slice(db, header, .2m)); await db.SaveChangesAsync(); }
        header.IsSealed = true;
        Error(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), 51904);
        await tx.RollbackAsync();
        using var read = sql.ContextFor(probe!, 1); Assert.Empty(await read.Set<ConstructionPartialInvoice>().ToListAsync());
    }

    [SkippableTheory]
    [InlineData("valid")][InlineData("invoice-price")][InlineData("invoice-reversed")][InlineData("receipt-cancelled")]
    public async Task Fresh_reader_revalidates_sealed_split_history_on_native_SQL(string change)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using (var db = sql.ContextFor(probe!, 1))
        {
            await Record(db, invoice, .4m);
            var second = await PrepareInvoice(db, .6m);
            await Record(db, second, .6m);
            if (change == "invoice-price")
                await db.PurchaseInvoiceLines.Where(x => x.PurchaseInvoiceId == second)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.UnitPrice, 11m));
            if (change == "invoice-reversed")
                await db.JournalEntries.Where(x => x.SourceType == "PurchaseInvoice" && x.SourceId == second)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Reversed"));
            if (change == "receipt-cancelled")
                await db.GoodsReceipts.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        }
        using var read = sql.ContextFor(probe!, 1);
        await using var tx = await read.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var result = await ConstructionPartialInvoiceHistoryReader.ReadAsync(read,
            new(1, setup.Seed.Project, setup.Order), 3);
        if (change == "valid")
        {
            Assert.True(result.Ok, result.Code);
            Assert.Equal(2, result.Quantities!.Count); Assert.Equal(1m, result.Quantities.Sum(x => x.Billed));
            var history = Assert.Single(result.Values!).Value;
            Assert.Equal(2, history.Count); Assert.Equal(10m, history.Sum(x => x.Net)); Assert.Equal(2m, history.Sum(x => x.Tax));
        }
        else { Assert.False(result.Ok); Assert.Null(result.Quantities); Assert.Null(result.Values); }
        Assert.Empty(read.ChangeTracker.Entries());
    }

    [SkippableTheory][InlineData("header-delete")][InlineData("header-reopen")][InlineData("header-hash")]
    [InlineData("slice-update")][InlineData("slice-delete")]
    public async Task Sealed_history_cannot_be_rewritten_or_deleted(string kind)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var db = sql.ContextFor(probe!, 1); var header = await Record(db, invoice, .4m);
        var command = kind switch
        {
            "header-delete" => "DELETE dbo.ConstructionPartialInvoices",
            "header-reopen" => "UPDATE dbo.ConstructionPartialInvoices SET IsSealed=0",
            "header-hash" => "UPDATE dbo.ConstructionPartialInvoices SET RequestHash=LOWER(RequestHash)",
            "slice-update" => "UPDATE dbo.ConstructionPartialInvoiceSlices SET Quantity=0.3",
            _ => "DELETE dbo.ConstructionPartialInvoiceSlices"
        };
        Error(await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(command)), 51901, 51905, 547);
        using var read = sql.ContextFor(probe!, 1);
        Assert.True((await read.Set<ConstructionPartialInvoice>().AsNoTracking().SingleAsync()).IsSealed);
        Assert.Equal(new string('A', 64), (await read.Set<ConstructionPartialInvoice>().AsNoTracking().SingleAsync()).RequestHash);
        Assert.Equal(.4m, (await read.Set<ConstructionPartialInvoiceSlice>().AsNoTracking().SingleAsync()).Quantity);
    }

    [SkippableTheory][InlineData("receipt-status")][InlineData("receipt-quantity")][InlineData("net-line")][InlineData("receipt-currency")]
    public async Task Seal_revalidates_parent_evidence_changed_after_slice_insertion(string kind)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var db = sql.ContextFor(probe!, 1); await using var tx = await db.Database.BeginTransactionAsync();
        var header = await Header(db, invoice); var slice = await Slice(db, header, .4m); db.Add(slice); await db.SaveChangesAsync();
        if (kind == "receipt-status") await db.GoodsReceipts.ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, "Cancelled"));
        if (kind == "receipt-quantity") await db.GoodsReceiptLines.ExecuteUpdateAsync(x => x.SetProperty(r => r.Qty, .3m));
        if (kind == "net-line") await db.JournalEntryLines.Where(x => x.ID == slice.JournalEntryLineId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Debit, 3m));
        if (kind == "receipt-currency") await db.GoodsReceipts.ExecuteUpdateAsync(x => x.SetProperty(r => r.CurrencyId, (int?)999));
        header.IsSealed = true;
        Error(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), 51910);
        await tx.RollbackAsync();
    }

    [SkippableFact]
    public async Task Posted_invoice_line_and_receipt_caps_are_both_enforced()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using (var db = sql.ContextFor(probe!, 1))
        {
            await using var tx = await db.Database.BeginTransactionAsync(); var header = await Header(db, invoice);
            db.Add(await Slice(db, header, .5m));
            Error(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), 51909); await tx.RollbackAsync();
        }
        using (var db = sql.ContextFor(probe!, 1))
        {
            await Record(db, invoice, .4m); var second = await PrepareInvoice(db, .7m);
            await using var tx = await db.Database.BeginTransactionAsync(); var header = await Header(db, second);
            db.Add(await Slice(db, header, .7m));
            Error(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), 51907); await tx.RollbackAsync();
        }
        using var read = sql.ContextFor(probe!, 1);
        Assert.Single(await read.Set<ConstructionPartialInvoice>().ToListAsync());
        Assert.Equal(.4m, await read.Set<ConstructionPartialInvoiceSlice>().SumAsync(x => x.Quantity));
    }

    [SkippableFact]
    public async Task Concurrent_receipt_claims_cannot_both_commit_over_the_same_quantity()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        int first, second;
        using (var arrange = sql.ContextFor(probe!, 1)) { first = await PrepareInvoice(arrange, .6m, false); second = await PrepareInvoice(arrange, .6m); }
        async Task<bool> Attempt(int inv)
        {
            using var db = sql.ContextFor(probe!, 1); await using var tx = await db.Database.BeginTransactionAsync();
            try { await Record(db, inv, .6m); await tx.CommitAsync(); return true; }
            catch (DbUpdateException ex) { Error(ex, 51907, 1205); return false; }
        }
        var results = await Task.WhenAll(Attempt(first), Attempt(second)); Assert.Single(results, x => x);
        using var read = sql.ContextFor(probe!, 1);
        Assert.Single(await read.Set<ConstructionPartialInvoice>().ToListAsync());
        Assert.True((await read.Set<ConstructionPartialInvoice>().SingleAsync()).IsSealed);
        Assert.Equal(.6m, await read.Set<ConstructionPartialInvoiceSlice>().SumAsync(x => x.Quantity));
    }

    [SkippableTheory][InlineData("header-scope")][InlineData("receipt-ownership")][InlineData("order-line")]
    [InlineData("invoice-line")][InlineData("zero-quantity")]
    public async Task Authored_scoped_relations_refuse_foreign_or_inconsistent_slices(string kind)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var db = sql.ContextFor(probe!, 1); await using var tx = await db.Database.BeginTransactionAsync();
        var header = await Header(db, invoice);
        if (kind == "header-scope")
        {
            Error(await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE dbo.ConstructionPartialInvoices SET ProjectId=999,IsSealed=1")), 547, 51901);
        }
        else
        {
            var slice = await Slice(db, header, .4m);
            if (kind == "receipt-ownership")
            {
                var other = new GoodsReceipt { CompanyID = 1, VendorId = setup.Seed.Vendor, PurchaseOrderId = setup.Order,
                    WarehouseId = setup.Seed.Warehouse, CurrencyId = setup.Seed.Currency, ExchangeRate = 1, Status = "Posted", ReceiptDate = DateTime.Today };
                db.Add(other); await db.SaveChangesAsync(); slice.GoodsReceiptId = other.ID;
            }
            if (kind == "order-line")
            {
                var other = new PurchaseOrderLine { PurchaseOrderId = setup.Order, LineNo = 2, ItemId = setup.Seed.Item, UoMId = setup.Seed.Unit, Qty = 1 };
                db.Add(other); await db.SaveChangesAsync(); slice.PurchaseOrderLineId = other.ID;
            }
            if (kind == "invoice-line") slice.PurchaseInvoiceLineId = 999;
            if (kind == "zero-quantity") slice.Quantity = 0;
            db.Add(slice);
            Error(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), 547, 51906);
        }
        await tx.RollbackAsync();
    }

    [SkippableFact]
    public async Task Request_identity_cannot_be_reused_for_another_invoice()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var db = sql.ContextFor(probe!, 1); var first = await Record(db, invoice, .4m);
        var second = await PrepareInvoice(db, .4m);
        Error(await Assert.ThrowsAsync<DbUpdateException>(() => Header(db, second, first.RequestId)), 2601, 2627);
        using var read = sql.ContextFor(probe!, 1); Assert.Single(await read.Set<ConstructionPartialInvoice>().ToListAsync());
    }

    [SkippableTheory][InlineData(false)][InlineData(true)]
    public async Task Legacy_and_partial_source_relations_cannot_be_mixed_in_either_order(bool partialFirst)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var db = sql.ContextFor(probe!, 1);
        if (partialFirst) await Record(db, invoice, .4m);
        db.Add(new PurchaseOrderInvoiceLink { CompanyID = 1, PurchaseOrderId = setup.Order, PurchaseInvoiceId = invoice, CreatedAt = DateTime.UtcNow });
        if (partialFirst) Error(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), 51911);
        else
        {
            await db.SaveChangesAsync();
            Error(await Assert.ThrowsAsync<DbUpdateException>(() => Header(db, invoice)), 51903);
        }
        using var read = sql.ContextFor(probe!, 1);
        Assert.Equal(partialFirst ? 1 : 0, await read.Set<ConstructionPartialInvoice>().CountAsync());
        Assert.Equal(partialFirst ? 0 : 1, await read.Set<PurchaseOrderInvoiceLink>().CountAsync());
    }
}
