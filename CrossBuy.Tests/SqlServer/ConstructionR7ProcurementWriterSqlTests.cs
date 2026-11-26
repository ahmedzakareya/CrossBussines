using System.Reflection;
using System.Text.RegularExpressions;
using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7ProcurementWriterSqlTests(UatSqlProbeFixture sql) : IAsyncLifetime
{
    private UatSqlProbeFixture.ProbeDatabase? probe;
    private int vendor, warehouse, item, order;
    public async Task InitializeAsync()
    {
        if (!sql.Available) return;
        probe = await sql.CreateProbeDatabaseAsync("R7POWRITER");
        try
        {
        using var db = sql.ContextFor(probe, 41);
        // The authored script deliberately spans GO batches within one SQL transaction.
        // Keep the same physical connection open until its COMMIT; EF otherwise closes it per call.
        await db.Database.OpenConnectionAsync();
        // Replace only the empty generated relation with the unmodified authored deployment script.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionPurchaseDrafts; DROP TABLE dbo.PurchaseOrderInvoiceLineLinks; DROP TABLE dbo.PurchaseOrderInvoiceLinks");
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(directory!.FullName, "CrossBuy/deploy/sql/construction_r7_procurement_provenance.sql"));
        for (var run = 0; run < 2; run++)
            foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
        var lineScript = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "CrossBuy/deploy/sql/construction_r7_invoice_line_provenance.sql"));
        for (var run = 0; run < 2; run++)
            foreach (var batch in Regex.Split(lineScript, @"(?im)^\s*GO\s*\r?$"))
                if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
        var draftScript = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "CrossBuy/deploy/sql/construction_r7_purchase_drafts.sql"));
        for (var run = 0; run < 2; run++)
            foreach (var batch in Regex.Split(draftScript, @"(?im)^\s*GO\s*\r?$"))
                if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
        db.Currencies.Add(new Currency { Code = "EGP", Name = "جنيه", NameEn = "Pound", DecimalPlaces = 2 });
        var year = new FiscalYear { CompanyID = 41, Name = "Test year", StartDate = DateTime.Today.AddDays(-10), EndDate = DateTime.Today.AddDays(10), Status = "Open" };
        db.FiscalYears.Add(year); await db.SaveChangesAsync();
        db.FiscalPeriods.Add(new FiscalPeriod { FiscalYearId = year.ID, PeriodNo = 1, StartDate = year.StartDate, EndDate = year.EndDate, Status = "Open" });
        Account Account(string code) => new() { CompanyID = 41, Code = code, Name = code, NameEn = code, AccountTypeId = 1, IsPostable = true, IsActive = true };
        var inventory = Account("110301"); var ap = Account("2101"); var grni = Account("210203");
        db.Accounts.AddRange(inventory, ap, grni); await db.SaveChangesAsync();
        var category = new ItemCategory { CompanyID = 41, Code = "R7", Name = "مواد", NameEn = "Materials", InventoryAccountId = inventory.ID, GrniAccountId = grni.ID };
        db.ItemCategories.Add(category); await db.SaveChangesAsync();
        var material = new Item { CompanyID = 41, ItemCode = "R7-1", Barcode = "R7-1", Name = "مادة", NameEn = "Material", ItemCategoryId = category.ID, ItemType = "Stockable", BaseUoMId = 1, IsActive = true };
        var supplier = new Vendor { CompanyID = 41, Name = "مورد", NameEn = "Supplier", ControlAccountId = ap.ID, IsActive = true };
        var store = new Warehouse { CompanyID = 41, Code = "R7", Name = "مخزن", NameEn = "Store", IsActive = true };
        db.AddRange(material, supplier, store); await db.SaveChangesAsync();
        item = material.ID; vendor = supplier.ID; warehouse = store.ID;
        using var graph = Graph();
        var created = await graph.GetRequiredService<IProcurementService>().CreatePurchaseOrderAsync(41, vendor, warehouse, DateTime.Today, null, "Native R7 proof",
            new() { new() { ItemId = item, ItemDescription = "Material", Qty = 5m, UnitPrice = 10m } }, "r7-test");
        Assert.True(created.ok, created.error); order = created.po!.ID;
        }
        catch (Exception original)
        {
            try { await DisposeAsync(); probe = null; }
            catch (Exception cleanup) { throw new AggregateException("Native probe initialization and cleanup both failed", original, cleanup); }
            throw;
        }
    }
    public async Task DisposeAsync() { if (probe != null) await sql.DropProbeDatabaseAsync(probe); }

    private ServiceProvider Graph(bool failLink = false, string? receiptFailure = null, bool failLine = false, string? tamperLine = null)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddLocalization();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped(_ =>
        {
            if (!failLink && receiptFailure == null && !failLine && tamperLine == null) return sql.ContextFor(probe!, 41);
            var holder = new CompanyScopeHolder(); holder.Set(41, null);
            var options = new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString);
            if (failLink) options.AddInterceptors(new FailLink());
            if (failLine) options.AddInterceptors(new FailLineLink());
            if (tamperLine != null) options.AddInterceptors(new TamperLineLink(tamperLine));
            if (receiptFailure != null) options.AddInterceptors(new FailReceipt(receiptFailure));
            return new CrossDbContext(options.Options, holder);
        });
        services.AddScoped<ICurrencyService, CurrencyService>(); services.AddScoped<ICurrencyRounding, CurrencyRounding>();
        services.AddScoped<IFiscalPeriodService, FiscalPeriodService>(); services.AddScoped<IJournalEntryService, JournalEntryService>();
        services.AddScoped<IStockService, StockService>(); services.AddScoped<IPayableService, PayableService>();
        services.AddScoped<IFixedAssetService, FixedAssetService>(); services.AddScoped<IThreeWayMatchService, ThreeWayMatchService>();
        services.AddScoped<IProcurementService, ProcurementService>(); services.AddScoped<IEntityRegistry, EntityRegistry>();
        services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(new BusinessContext { CompanyId = 41, EmployeeId = 7, UserId = "r7-native", Source = BusinessContextSource.Test, CorrelationId = Guid.NewGuid() }));
        services.AddScoped<IBusinessEventService, BusinessEventService>();
        // Only the external notification boundary is inert. AP, GL, stock and durable facts are real.
        services.AddScoped<INotificationService>(_ => DispatchProxy.Create<INotificationService, SilentNotifications>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = false });
    }
    private async Task AssertEffects(int expected)
    {
        using var db = sql.ContextFor(probe!, 41);
        Assert.Equal(expected, await db.PurchaseInvoices.AsNoTracking().CountAsync());
        Assert.Equal(expected, await db.Set<PurchaseOrderInvoiceLink>().AsNoTracking().CountAsync());
        Assert.Equal(expected, await db.Set<PurchaseOrderInvoiceLineLink>().AsNoTracking().CountAsync());
        Assert.Equal(expected, await db.JournalEntries.AsNoTracking().CountAsync(x => x.SourceType == "PurchaseInvoice" && x.Status == "Posted"));
        Assert.Equal(expected, await db.StockMovements.AsNoTracking().CountAsync(x => x.SourceType == "PurchaseInvoice"));
        Assert.Equal(expected, await db.BusinessEvents.AsNoTracking().CountAsync(x => x.EventType == PurchaseInvoiceEvents.Created));
        if (expected == 0) Assert.Empty(await db.BusinessEventDispatches.AsNoTracking().ToListAsync());
        if (expected == 1)
        {
            var invoice = await db.PurchaseInvoices.AsNoTracking().SingleAsync(); Assert.Equal(50m, invoice.GrandTotalBase);
            var fact = await db.BusinessEvents.AsNoTracking().SingleAsync(x => x.EventType == PurchaseInvoiceEvents.Created);
            Assert.Equal(invoice.ID, fact.EntityId);
            var consumers = await db.BusinessEventDispatches.AsNoTracking().Where(x => x.EventId == fact.EventId).Select(x => x.Consumer).ToListAsync();
            Assert.Equal(BusinessEventConsumers.Registered.OrderBy(x => x), consumers.OrderBy(x => x));
            var entry = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.ID == invoice.JournalEntryId);
            Assert.Equal(50m, entry.Lines.Sum(x => x.Debit)); Assert.Equal(50m, entry.Lines.Sum(x => x.Credit));
            var balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ItemId == item && x.WarehouseId == warehouse);
            Assert.Equal(5m, balance.QtyOnHand); Assert.Equal(50m, balance.TotalValue);
        }
        else Assert.Empty(await db.StockBalances.AsNoTracking().ToListAsync());
    }
    [SkippableFact]
    public async Task Concurrent_conversions_and_retry_produce_one_invoice_one_journal_one_stock_receipt_and_one_fact()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var first = Graph(); using var second = Graph();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(bool ok, string? error, int? invoiceId)> Convert(ServiceProvider graph) { await gate.Task; return await graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native"); }
        var a = Convert(first); var b = Convert(second); gate.SetResult(); var results = await Task.WhenAll(a, b);
        Assert.All(results, result => Assert.True(result.ok, result.error)); Assert.Equal(results[0].invoiceId, results[1].invoiceId);
        using var retry = Graph(); var again = await retry.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native");
        Assert.True(again.ok, again.error); Assert.Equal(results[0].invoiceId, again.invoiceId); await AssertEffects(1);
    }
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Late_link_failure_rolls_back_real_invoice_journal_stock_and_business_fact(bool borrowed)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var graph = Graph(failLink: true); var db = graph.GetRequiredService<CrossDbContext>();
        await using var outer = borrowed ? await db.Database.BeginTransactionAsync() : null;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native"));
        Assert.Equal("Injected late source-link failure", failure.Message);
        if (outer != null) await outer.CommitAsync();
        await AssertEffects(0);
        using (var verify = sql.ContextFor(probe!, 41)) Assert.Equal("Approved", (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
        using var retry = Graph(); var result = await retry.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native");
        Assert.True(result.ok, result.error); await AssertEffects(1);
    }
    [SkippableFact]
    public async Task A_received_order_clears_grni_without_receiving_stock_a_second_time()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        int lineId;
        using (var read = sql.ContextFor(probe!, 41)) lineId = (await read.PurchaseOrderLines.AsNoTracking().SingleAsync()).ID;
        using (var receiving = Graph())
        {
            var receipt = await receiving.GetRequiredService<IProcurementService>().CreateReceiptAsync(41, vendor, warehouse, order, DateTime.Today,
                "Real receipt before billing", new() { new() { ItemId = item, Qty = 5m, UnitCost = 10m, PurchaseOrderLineId = lineId } }, "r7-native");
            Assert.True(receipt.ok, receipt.error);
        }
        using var conversion = Graph();
        var result = await conversion.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native");
        Assert.True(result.ok, result.error);
        using var verify = sql.ContextFor(probe!, 41);
        var invoice = await verify.PurchaseInvoices.AsNoTracking().SingleAsync();
        Assert.Equal(invoice.ID, (await verify.GoodsReceipts.AsNoTracking().SingleAsync()).InvoiceId);
        Assert.Single(await verify.Set<PurchaseOrderInvoiceLink>().AsNoTracking().ToListAsync());
        Assert.Equal(1, await verify.StockMovements.AsNoTracking().CountAsync(x => x.SourceType == "Receipt"));
        Assert.Equal(0, await verify.StockMovements.AsNoTracking().CountAsync(x => x.SourceType == "PurchaseInvoice"));
        var balance = await verify.StockBalances.AsNoTracking().SingleAsync(); Assert.Equal(5m, balance.QtyOnHand); Assert.Equal(50m, balance.TotalValue);
        var grni = await verify.Accounts.AsNoTracking().SingleAsync(x => x.Code == "210203");
        var invoiceEntry = await verify.JournalEntries.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.ID == invoice.JournalEntryId);
        Assert.Equal(50m, invoiceEntry.Lines.Where(x => x.AccountId == grni.ID).Sum(x => x.Debit));
        var postedIds = await verify.JournalEntries.AsNoTracking().Where(x => x.Status == "Posted").Select(x => x.ID).ToListAsync();
        Assert.Equal(0m, await verify.JournalEntryLines.AsNoTracking().Where(x => postedIds.Contains(x.JournalEntryId) && x.AccountId == grni.ID).SumAsync(x => x.Debit - x.Credit));
    }
    private sealed class FailLink : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<PurchaseOrderInvoiceLink>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Injected late source-link failure");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
    private sealed class FailLineLink : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<PurchaseOrderInvoiceLineLink>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("Injected late line-provenance failure");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
    private sealed class TamperLineLink(string mode) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = (CrossDbContext)data.Context!;
            foreach (var pending in db.ChangeTracker.Entries<PurchaseOrderInvoiceLineLink>().Where(x => x.State == EntityState.Added))
            {
                if (mode == "company") pending.Entity.CompanyID = 42;
                else pending.Entity.JournalEntryLineId = db.JournalEntryLines.Local.Single(x => x.JournalEntryId == pending.Entity.JournalEntryId && x.Credit > 0).ID;
            }
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }

    [SkippableTheory]
    [InlineData("company", 547)]
    [InlineData("payable", 51702)]
    public async Task Authored_line_provenance_refuses_foreign_company_and_payable_substitution_atomically(string mode, int expectedSqlNumber)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var graph = Graph(tamperLine: mode);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native"));
        var sqlException = Assert.IsType<Microsoft.Data.SqlClient.SqlException>(exception.InnerException);
        Assert.Equal(expectedSqlNumber, sqlException.Number);
        await AssertEffects(0);
        using var read = sql.ContextFor(probe!, 41);
        Assert.Equal("Approved", (await read.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Late_line_provenance_failure_rolls_back_header_invoice_gl_stock_and_fact(bool borrowed)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var graph = Graph(failLine: true);
        var db = graph.GetRequiredService<CrossDbContext>();
        await using var outer = borrowed ? await db.Database.BeginTransactionAsync() : null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native"));
        Assert.Equal("Injected late line-provenance failure", error.Message);
        if (outer != null) await outer.CommitAsync();
        await AssertEffects(0);
        using var retry = Graph();
        Assert.True((await retry.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native")).ok);
        await AssertEffects(1);
    }

    [SkippableFact]
    public async Task Equal_value_invoice_lines_keep_distinct_writer_identities_and_immutable_history()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using (var setup = sql.ContextFor(probe!, 41))
        {
            var first = await setup.PurchaseOrderLines.SingleAsync();
            setup.PurchaseOrderLines.Add(new PurchaseOrderLine { PurchaseOrderId = order, LineNo = 2, ItemId = item,
                ItemDescription = first.ItemDescription, Qty = first.Qty, UnitPrice = first.UnitPrice, LineTotal = first.LineTotal });
            var source = await setup.PurchaseOrders.SingleAsync(); source.SubTotal *= 2; source.GrandTotal *= 2;
            await setup.SaveChangesAsync();
        }
        using var graph = Graph();
        var result = await graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native");
        Assert.True(result.ok, result.error);
        using var verify = sql.ContextFor(probe!, 41);
        var links = await verify.Set<PurchaseOrderInvoiceLineLink>().AsNoTracking().ToListAsync();
        Assert.Equal(2, links.Count);
        foreach (var link in links)
        {
            var source = await verify.PurchaseOrderLines.AsNoTracking().SingleAsync(x => x.ID == link.PurchaseOrderLineId);
            var invoice = await verify.PurchaseInvoiceLines.AsNoTracking().SingleAsync(x => x.ID == link.PurchaseInvoiceLineId);
            var journal = await verify.JournalEntryLines.AsNoTracking().SingleAsync(x => x.ID == link.JournalEntryLineId);
            Assert.Equal(source.LineNo, invoice.LineNo); Assert.Equal(invoice.LineNo, journal.LineNo);
            Assert.Equal(invoice.ExpenseAccountId, journal.AccountId);
        }
        Assert.Equal(5, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.PurchaseOrderInvoiceLineLinks') AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
        var mutation = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => verify.Database.ExecuteSqlRawAsync("UPDATE dbo.PurchaseOrderInvoiceLineLinks SET CreatedAt=SYSUTCDATETIME()"));
        Assert.Equal(51701, mutation.Number);
        var deletion = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => verify.Database.ExecuteSqlRawAsync("DELETE FROM dbo.PurchaseOrderInvoiceLineLinks"));
        Assert.Equal(51701, deletion.Number);
        var edit = await graph.GetRequiredService<IPayableService>().EditPurchaseInvoiceAsync(41, result.invoiceId!.Value,
            vendor, DateTime.Today, new() { new() { ItemId = item, Qty = 1, UnitPrice = 1 } }, "Must refuse", null);
        Assert.False(edit.ok); Assert.Contains("amendment workflow", edit.error);
        Assert.Equal(2, await verify.Set<PurchaseOrderInvoiceLineLink>().AsNoTracking().CountAsync());
        Assert.Equal(1, await verify.JournalEntries.AsNoTracking().CountAsync(x => x.SourceType == "PurchaseInvoice"));
    }
    private async Task<List<ReceiptLineInput>> ReceiptLines(int count = 1)
    {
        using var read = sql.ContextFor(probe!, 41);
        var source = await read.PurchaseOrderLines.AsNoTracking().SingleAsync();
        return Enumerable.Range(0, count).Select(_ => new ReceiptLineInput { ItemId = item, Qty = 5m / count, UnitCost = 10m, PurchaseOrderLineId = source.ID }).ToList();
    }

    [SkippableTheory]
    [InlineData(false, "second-stock")] [InlineData(true, "second-stock")]
    [InlineData(false, "event")] [InlineData(true, "event")]
    public async Task Receipt_failure_rolls_back_all_real_effects_and_preserves_the_callers_transaction(bool borrowed, string failureMode)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var lines = await ReceiptLines(2);
        using var graph = Graph(receiptFailure: failureMode); var db = graph.GetRequiredService<CrossDbContext>();
        var caller = await db.Warehouses.SingleAsync(x => x.ID == warehouse);
        await using var outer = borrowed ? await db.Database.BeginTransactionAsync() : null;
        if (failureMode == "second-stock")
        {
            var refused = await graph.GetRequiredService<IProcurementService>().CreateReceiptAsync(41, vendor, warehouse, order, DateTime.Today, "Late receipt failure", lines, "r7-native");
            Assert.False(refused.ok); Assert.Contains("Injected receipt second-stock failure", refused.error);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => graph.GetRequiredService<IProcurementService>()
                .CreateReceiptAsync(41, vendor, warehouse, order, DateTime.Today, "Late receipt failure", lines, "r7-native"));
            Assert.Equal("Injected receipt event failure", failure.Message);
        }
        Assert.Equal(EntityState.Unchanged, db.Entry(caller).State);
        caller.NameEn = "Caller can continue"; await db.SaveChangesAsync();
        if (outer != null) await outer.CommitAsync();
        Assert.Equal(0, ((SilentNotifications)(object)graph.GetRequiredService<INotificationService>()).Calls);
        using var verify = sql.ContextFor(probe!, 41);
        Assert.Empty(await verify.GoodsReceipts.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.StockBalances.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntryLines.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.BusinessEvents.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.BusinessEventDispatches.AsNoTracking().ToListAsync());
        Assert.Equal("Approved", (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0m, (await verify.PurchaseOrderLines.AsNoTracking().SingleAsync()).ReceivedQty);
        Assert.Equal("Caller can continue", (await verify.Warehouses.AsNoTracking().SingleAsync()).NameEn);
    }

    [SkippableTheory]
    [InlineData(true)] [InlineData(false)]
    public async Task Receipt_and_conversion_share_the_order_lock_in_both_commit_orders(bool receiptFirst)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var lines = await ReceiptLines();
        using var first = Graph(); using var second = Graph();
        var db = first.GetRequiredService<CrossDbContext>(); await using var outer = await db.Database.BeginTransactionAsync();
        var firstService = first.GetRequiredService<IProcurementService>(); var secondService = second.GetRequiredService<IProcurementService>();
        if (receiptFirst)
        {
            var receipt = await firstService.CreateReceiptAsync(41, vendor, warehouse, order, DateTime.Today, "Receipt owns lock", lines, "r7-native");
            Assert.True(receipt.ok, receipt.error);
            Assert.Equal(0, ((SilentNotifications)(object)first.GetRequiredService<INotificationService>()).Calls);
            var conversion = secondService.ConvertToInvoiceAsync(41, order, "r7-native");
            await outer.CommitAsync(); var result = await conversion; Assert.True(result.ok, result.error);
        }
        else
        {
            var conversion = await firstService.ConvertToInvoiceAsync(41, order, "r7-native"); Assert.True(conversion.ok, conversion.error);
            var receipt = secondService.CreateReceiptAsync(41, vendor, warehouse, order, DateTime.Today, "Conversion owns lock", lines, "r7-native");
            await outer.CommitAsync(); var result = await receipt; Assert.False(result.ok); Assert.Contains("already invoiced", result.error);
        }
        await AssertReceiptConversionEffects(receiptFirst);
    }

    [SkippableFact]
    public async Task Simultaneous_receipt_and_conversion_never_receive_stock_twice()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var lines = await ReceiptLines(); using var receiving = Graph(); using var converting = Graph();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(bool ok, string? error, GoodsReceipt? gr)> Receive() { await gate.Task; return await receiving.GetRequiredService<IProcurementService>().CreateReceiptAsync(41, vendor, warehouse, order, DateTime.Today, "Race", lines, "r7-native"); }
        async Task<(bool ok, string? error, int? invoiceId)> Convert() { await gate.Task; return await converting.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native"); }
        var receipt = Receive(); var conversion = Convert(); gate.SetResult(); await Task.WhenAll(receipt, conversion);
        Assert.True(conversion.Result.ok, conversion.Result.error); await AssertReceiptConversionEffects(receipt.Result.ok);
    }

    private async Task AssertReceiptConversionEffects(bool received)
    {
        using var verify = sql.ContextFor(probe!, 41);
        Assert.Single(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
        Assert.Single(await verify.Set<PurchaseOrderInvoiceLink>().AsNoTracking().ToListAsync());
        Assert.Single(await verify.StockMovements.AsNoTracking().ToListAsync());
        var balance = await verify.StockBalances.AsNoTracking().SingleAsync(); Assert.Equal(5m, balance.QtyOnHand); Assert.Equal(50m, balance.TotalValue);
        Assert.Equal(received ? 1 : 0, await verify.GoodsReceipts.AsNoTracking().CountAsync());
        var facts = await verify.BusinessEvents.AsNoTracking().Where(x => x.EventType == GoodsReceiptEvents.Posted).ToListAsync();
        Assert.Equal(received ? 1 : 0, facts.Count);
        if (received)
        {
            var fact = Assert.Single(facts);
            var consumers = await verify.BusinessEventDispatches.AsNoTracking().Where(x => x.EventId == fact.EventId).Select(x => x.Consumer).ToListAsync();
            Assert.Equal(BusinessEventConsumers.Registered.OrderBy(x => x), consumers.OrderBy(x => x));
            Assert.True(TimelineEventPresenter.TryPresent(fact.EventType, fact.PayloadVersion, fact.Payload, out var presentation, out var error), error);
            Assert.False(string.IsNullOrWhiteSpace(presentation!.TitleAr)); Assert.False(string.IsNullOrWhiteSpace(presentation.TitleEn));
            using var graph = Graph(); var envelope = graph.GetRequiredService<IBusinessEventService>().BuildEnvelope(fact);
            var command = Assert.Single(new BusinessEventNotificationMapper().Map(envelope));
            Assert.Equal(EntityRegistry.GoodsReceipt, command.EntityType); Assert.Equal(41, command.CompanyId);
            Assert.Equal(new[] { "Accountant", "ChiefAccountant" }, command.RecipientRoles);
        }
        Assert.Equal("Closed", (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
    }

    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Asset_receipts_share_the_same_atomic_boundary_without_creating_stock(bool failEvent)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using (var arrange = sql.ContextFor(probe!, 41))
        {
            foreach (var code in new[] { "1201", "1202", "520103" }) arrange.Accounts.Add(new Account { CompanyID = 41, Code = code, Name = code, NameEn = code, AccountTypeId = 1, IsPostable = true, IsActive = true });
            (await arrange.Items.SingleAsync(x => x.ID == item)).ItemType = "Asset"; await arrange.SaveChangesAsync();
        }
        var lines = await ReceiptLines(); using var graph = Graph(receiptFailure: failEvent ? "event" : null);
        if (failEvent)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => graph.GetRequiredService<IProcurementService>().CreateReceiptAsync(41, vendor, warehouse, order, DateTime.Today, "Asset failure", lines, "r7-native"));
            Assert.Equal("Injected receipt event failure", error.Message);
        }
        else
        {
            var result = await graph.GetRequiredService<IProcurementService>().CreateReceiptAsync(41, vendor, warehouse, order, DateTime.Today, "Asset success", lines, "r7-native");
            Assert.True(result.ok, result.error);
            using var conversion = Graph(); var invoice = await conversion.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(41, order, "r7-native"); Assert.True(invoice.ok, invoice.error);
        }
        using var verify = sql.ContextFor(probe!, 41);
        Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockBalances.AsNoTracking().ToListAsync());
        Assert.Equal(failEvent ? 0 : 1, await verify.FixedAssets.AsNoTracking().CountAsync());
        Assert.Equal(failEvent ? 0 : 1, await verify.GoodsReceipts.AsNoTracking().CountAsync());
        Assert.Equal(failEvent ? 0 : 2, await verify.JournalEntries.AsNoTracking().CountAsync());
        if (failEvent)
        {
            Assert.Empty(await verify.BusinessEvents.AsNoTracking().ToListAsync()); Assert.Empty(await verify.BusinessEventDispatches.AsNoTracking().ToListAsync());
            Assert.Equal(0m, (await verify.PurchaseOrderLines.AsNoTracking().SingleAsync()).ReceivedQty);
        }
        else
        {
            Assert.Equal(50m, (await verify.FixedAssets.AsNoTracking().SingleAsync()).Cost);
            var grni = await verify.Accounts.AsNoTracking().SingleAsync(x => x.Code == "210203");
            Assert.Equal(0m, await verify.JournalEntryLines.AsNoTracking().Where(x => x.AccountId == grni.ID).SumAsync(x => x.Debit - x.Credit));
        }
    }

    [SkippableFact]
    public async Task Project_sources_follow_real_invoice_links_and_real_reversal_history_without_inventing_expense_or_currency()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        int projectId, expenseId, payableId;
        using (var arrange = sql.ContextFor(probe!, 41))
        {
            var project = new Project { CompanyID = 41, Code = "R7-SOURCE", Name = "مشروع", NameEn = "Project", Status = "Active", IsActive = true };
            var expenseType = new AccountType { Code = "EXP", Name = "مصروف", NameEn = "Expense", StatementType = "IncomeStatement" };
            var assetType = new AccountType { Code = "ASSET", Name = "أصل", NameEn = "Asset", StatementType = "BalanceSheet" };
            var liabilityType = new AccountType { Code = "LIAB", Name = "التزام", NameEn = "Liability", StatementType = "BalanceSheet", NormalBalance = "C" };
            arrange.AddRange(project, expenseType, assetType, liabilityType); await arrange.SaveChangesAsync(); projectId = project.ID;
            foreach (var account in await arrange.Accounts.ToListAsync()) account.AccountTypeId = account.Code == "110301" ? assetType.ID : liabilityType.ID;
            var expense = new Account { CompanyID = 41, Code = "510104", Name = "تكلفة المشروع", NameEn = "Project cost", AccountTypeId = expenseType.ID, IsActive = true, IsPostable = true };
            arrange.Add(expense); await arrange.SaveChangesAsync(); expenseId = expense.ID; payableId = (await arrange.Accounts.SingleAsync(x => x.Code == "2101")).ID;
        }
        using var graph = Graph(); var procurement = graph.GetRequiredService<IProcurementService>();
        var po = await procurement.CreatePurchaseOrderAsync(41, vendor, warehouse, DateTime.Today, null, "Source proof",
            new() { new() { ItemId = item, Qty = 5m, UnitPrice = 10m } }, "r7-native", projectId);
        Assert.True(po.ok, po.error); var invoice = await procurement.ConvertToInvoiceAsync(41, po.po!.ID, "r7-native"); Assert.True(invoice.ok, invoice.error);
        var db = graph.GetRequiredService<CrossDbContext>(); var reader = new ConstructionFinancialSourceService(db, new FinancialReadAccess());
        var actor = new BusinessContext { CompanyId = 41, EmployeeId = 7, UserId = "r7-native", Source = BusinessContextSource.Test };
        var purchased = await reader.GetAsync(actor, projectId); Assert.True(purchased.Ok, purchased.Code);
        var source = Assert.Single(purchased.Snapshot!.Orders); Assert.Equal(invoice.invoiceId, source.ProvenInvoiceId);
        Assert.Equal("TransferredToInvoice", source.Classification); Assert.Equal(0m, source.OpenCommitmentNetDocumentAmount);
        Assert.DoesNotContain(purchased.Snapshot.Ledger, x => x.Classification == "ExpensePosting"); Assert.Null(purchased.Snapshot.ProjectActualCost);
        var posted = await graph.GetRequiredService<IJournalEntryService>().CreateAndPostAsync(new JournalEntryInput
        {
            CompanyID = 41, EntryDate = DateTime.Today.AddDays(-1), JournalType = "Manual", Description = "Actual project cost source",
            Lines = new() { new() { AccountId = expenseId, ProjectId = projectId, Debit = 12.34m }, new() { AccountId = payableId, ProjectId = projectId, Credit = 12.34m } }
        }, null);
        Assert.True(posted.ok, posted.error);
        var reversed = await graph.GetRequiredService<IJournalEntryService>().ReverseAsync(posted.entry!.ID, null, "Reverse native project cost proof"); Assert.True(reversed.ok, reversed.error);
        var final = await reader.GetAsync(actor, projectId); Assert.True(final.Ok, final.Code);
        var expenseRows = final.Snapshot!.Ledger.Where(x => x.Classification == "ExpensePosting").ToList(); Assert.Equal(2, expenseRows.Count);
        Assert.Contains(expenseRows, x => x.EntryStatus == "Reversed" && x.ExpenseNetStoredBase == 12.34m);
        Assert.Contains(expenseRows, x => x.EntryStatus == "Posted" && x.SourceType == "Reversal" && x.ExpenseNetStoredBase == -12.34m);
        Assert.Equal(0m, expenseRows.Sum(x => x.ExpenseNetStoredBase));
        var priorDay = await reader.GetAsync(actor, projectId, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-1)); Assert.True(priorDay.Ok, priorDay.Code);
        Assert.Equal(12.34m, Assert.Single(priorDay.Snapshot!.Ledger.Where(x => x.Classification == "ExpensePosting")).ExpenseNetStoredBase);
        Assert.DoesNotContain(final.Snapshot.Issues, x => x.Code == "HistoricalBaseCurrencyIdentityNotProven");
        Assert.All(expenseRows, x => { Assert.NotNull(x.ProvenFunctionalCurrencyId); Assert.Equal(2, x.ProvenFunctionalCurrencyDecimals); });
        Assert.False(final.Snapshot.CanEvaluateBudget); Assert.Null(final.Snapshot.RemainingBudget);
    }

    [SkippableTheory]
    [InlineData(40, true)] [InlineData(41, false)]
    public async Task Real_expense_invoice_replaces_its_reserved_order_and_preserves_remaining_budget(int proposalAmount, bool admitted)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var graph = Graph(); var db = graph.GetRequiredService<CrossDbContext>();
        await db.Database.OpenConnectionAsync();
        // Compose the actual authored analytical schemas with the already-authored procurement provenance.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionReservationExposureRebases; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionMaterialConsumptions; DROP TABLE dbo.ConstructionCommitmentReleases; DROP TABLE dbo.ConstructionCommitmentReservations; DROP TABLE dbo.ConstructionOrderAllocationLines; DROP TABLE dbo.ConstructionOrderAllocations; DROP TABLE dbo.ConstructionActualCostAllocationLines; DROP TABLE dbo.ConstructionActualCostAllocations;");
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var filename in new[] { "construction_r7_order_allocations.sql", "construction_r7_commitment_reservations.sql", "journal_functional_currency_snapshot.sql", "construction_r7_actual_cost_allocations.sql", "construction_r7_material_consumptions.sql" })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql", filename));
            for (var run = 0; run < 2; run++)
                foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                    if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
        }
        var project = new Project { CompanyID = 41, Code = "R7-COST", Name = "تكلفة خدمة", NameEn = "Service cost", Status = "Active", IsActive = true };
        var expenseType = new AccountType { Code = "EXP", Name = "Expense", StatementType = "IncomeStatement" };
        var liabilityType = new AccountType { Code = "LIAB", Name = "Liability", StatementType = "BalanceSheet" };
        db.AddRange(project, expenseType, liabilityType); await db.SaveChangesAsync();
        var expenseAccount = new Account { CompanyID = 41, Code = "510104", Name = "Service cost", AccountTypeId = expenseType.ID, IsActive = true, IsPostable = true };
        db.Add(expenseAccount); (await db.Accounts.SingleAsync(x => x.Code == "2101")).AccountTypeId = liabilityType.ID; await db.SaveChangesAsync();
        var material = await db.Items.SingleAsync(x => x.ID == item); material.ItemType = ItemTypes.Service;
        (await db.ItemCategories.SingleAsync(x => x.ID == material.ItemCategoryId)).InventoryAccountId = expenseAccount.ID;
        var currency = (await db.Currencies.AsNoTracking().SingleAsync(x => x.Code == "EGP")).ID;
        var node = new WbsNode { CompanyID = 41, ProjectId = project.ID, Code = "WORK", Name = "العمل", NameEn = "Work", Depth = 1,
            Path = "/1/", Status = "Active", OwnsBudget = true, OwnsCost = true, ProgressMethod = "Quantity", CreatedBy = 7 };
        var costCode = new ConstructionCostCode { CompanyID = 41, Code = "SRV", Name = "خدمة", NameEn = "Service", Category = "Material", IsActive = true, AllowDirectPosting = true, DefaultAccountId = expenseAccount.ID };
        var baseline = new ConstructionBudgetVersion { CompanyID = 41, ProjectId = project.ID, CurrencyId = currency, VersionNo = 1,
            Status = "Approved", IsBaseline = true, CreatedBy = 8, ApprovedBy = 9, Reason = "Native approved budget" };
        db.AddRange(node, costCode, baseline, new ConstructionBudgetPolicy { CompanyID = 41, Mode = "HardBlock", Reason = "Explicit test policy" });
        await db.SaveChangesAsync();
        db.Add(new ConstructionBudgetLine { CompanyID = 41, ProjectId = project.ID, BudgetVersionId = baseline.ID,
            WbsNodeId = node.ID, CostCodeId = costCode.ID, CostCategory = "Material", Amount = 100 }); await db.SaveChangesAsync();
        var actor = ConstructionR7OrderAllocationTests.Actor(41); var access = new ConstructionR7ActualCostTests.Access(); var audit = new ConstructionAuditService(db);
        var procurement = graph.GetRequiredService<IProcurementService>();
        var allocations = new ConstructionOrderAllocationService(db, access, audit);
        var reservations = new ConstructionCommitmentReservationService(db, access, audit, new ConstructionBudgetPolicyService(db, access, audit));
        async Task<(int Order, int Allocation, string Version)> Proposal(decimal amount, bool draft)
        {
            var created = await procurement.CreatePurchaseOrderAsync(41, vendor, warehouse, DateTime.Today, null, "Native source",
                new() { new() { ItemId = item, ItemDescription = "Service", Qty = 1, UnitPrice = amount } }, "r7-native", project.ID);
            Assert.True(created.ok, created.error);
            if (draft) await db.PurchaseOrders.Where(x => x.ID == created.po!.ID).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Draft"));
            var sourceLine = await db.PurchaseOrderLines.AsNoTracking().SingleAsync(x => x.PurchaseOrderId == created.po!.ID);
            var preview = await allocations.PreviewAsync(actor, project.ID, created.po!.ID); Assert.True(preview.Ok, preview.Code);
            var mapped = await allocations.SaveAsync(actor, project.ID, created.po.ID, preview.Version!, Guid.NewGuid(),
                new[] { new ConstructionCommitmentSplit(sourceLine.ID, node.ID, costCode.ID, amount) }, "Map native service cost");
            Assert.True(mapped.Ok, mapped.Code); return (created.po.ID, mapped.Id!.Value, preview.Version!);
        }
        var first = await Proposal(60, false); var request = Guid.NewGuid();
        Assert.True((await reservations.ReserveAsync(actor, project.ID, first.Allocation, first.Version, request, "Reserve original service")).Ok);
        var converted = await procurement.ConvertToInvoiceAsync(41, first.Order, "r7-native"); Assert.True(converted.ok, converted.error);
        var provenance = await db.Set<PurchaseOrderInvoiceLineLink>().AsNoTracking().SingleAsync(x => x.PurchaseOrderId == first.Order);
        var actual = new ConstructionActualCostService(db, access, audit);
        var actualPreview = await actual.PreviewAsync(actor, project.ID, provenance.JournalEntryLineId); Assert.True(actualPreview.Ok, actualPreview.Code);
        Assert.True((await actual.AllocateAsync(actor, project.ID, provenance.JournalEntryLineId, actualPreview.Version!, Guid.NewGuid(),
            new[] { new ConstructionActualSplit(node.ID, costCode.ID, 60) }, "Attribute real posted expense")).Ok);
        var reader = new ConstructionBudgetExposureService(db, access, audit);
        var current = await reader.PreviewAsync(actor, project.ID); Assert.True(current.Ok, current.Code);
        var row = Assert.Single(current.Rows!); Assert.Equal(60, row.Actual); Assert.Equal(0, row.Commitment); Assert.Equal(40, row.Remaining);
        var retry = await reservations.ReserveAsync(actor, project.ID, first.Allocation, first.Version, request, "Reserve original service");
        Assert.Equal("AlreadyReserved", retry.Code); Assert.False(retry.GrantsCurrentAdmission);
        var second = await Proposal(proposalAmount, true);
        var decision = await reservations.ReserveAsync(actor, project.ID, second.Allocation, second.Version, Guid.NewGuid(), "Fresh expense-aware admission");
        Assert.Equal(admitted, decision.GrantsCurrentAdmission); Assert.Equal(admitted ? "WithinBudget" : "BudgetHardBlock", decision.Code);
        using var verify = sql.ContextFor(probe!, 41);
        Assert.Equal(admitted ? 2 : 1, await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().CountAsync());
        Assert.Empty(await verify.Set<ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
        Assert.Single(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
        Assert.Single(await verify.JournalEntries.AsNoTracking().Where(x => x.SourceType == "PurchaseInvoice").ToListAsync());
        Assert.Empty(await verify.StockMovements.AsNoTracking().Where(x => x.SourceType == "PurchaseInvoice").ToListAsync());
        var reversed = await graph.GetRequiredService<IJournalEntryService>().ReverseAsync(provenance.JournalEntryId, null, "Do not reopen the purchase commitment");
        Assert.True(reversed.ok, reversed.error);
        var mirrorLine = await db.JournalEntryLines.AsNoTracking().SingleAsync(x => x.JournalEntryId == reversed.reversalId && x.AccountId == expenseAccount.ID);
        var mirrorPreview = await actual.PreviewAsync(actor, project.ID, mirrorLine.ID); Assert.True(mirrorPreview.Ok, mirrorPreview.Code);
        Assert.True((await actual.AllocateAsync(actor, project.ID, mirrorLine.ID, mirrorPreview.Version!, Guid.NewGuid(),
            new[] { new ConstructionActualSplit(node.ID, costCode.ID, -60) }, "Attribute real reversal")).Ok);
        var afterReversal = await reader.PreviewAsync(actor, project.ID);
        Assert.False(afterReversal.Ok); Assert.Null(afterReversal.Rows); Assert.Equal("RealizationCoverageRequired", afterReversal.Code);
    }

    private sealed class FinancialReadAccess : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext c, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default) => Task.FromResult(action == ProjectsActions.BudgetView);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }

    private sealed class FailReceipt(string mode) : SaveChangesInterceptor
    {
        private int movements;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = data.Context!;
            if (mode == "second-stock" && db.ChangeTracker.Entries<StockMovement>().Any(x => x.State == EntityState.Added && x.Entity.SourceType == "Receipt") && ++movements == 2)
                throw new InvalidOperationException("Injected receipt second-stock failure");
            if (mode == "event" && db.ChangeTracker.Entries<CrossBuy.Models.Context.Platform.BusinessEvent>().Any(x => x.State == EntityState.Added && x.Entity.EventType == GoodsReceiptEvents.Posted))
                throw new InvalidOperationException("Injected receipt event failure");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
    public class SilentNotifications : DispatchProxy
    {
        public int Calls;
        protected override object? Invoke(MethodInfo? method, object?[]? args) { Calls++; return method!.ReturnType == typeof(Task<int>) ? Task.FromResult(0) : Task.CompletedTask; }
    }
}



