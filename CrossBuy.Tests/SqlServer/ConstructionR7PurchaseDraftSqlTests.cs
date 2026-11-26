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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7PurchaseDraftSqlTests(UatSqlProbeFixture sql) : IAsyncLifetime
{
    private UatSqlProbeFixture.ProbeDatabase? probe;
    private ConstructionR7PurchaseDraftTests.SeedData seed = null!;
    private string script = "";
    public async Task InitializeAsync()
    {
        if (!sql.Available) return;
        probe = await sql.CreateProbeDatabaseAsync("R7DRAFT");
        try
        {
            using var db = sql.ContextFor(probe, 1); await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionPurchaseDrafts");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root); script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql/construction_r7_purchase_drafts.sql"));
            await Apply(db); await Apply(db);
            seed = await ConstructionR7PurchaseDraftTests.Seed(db);
        }
        catch (Exception original)
        {
            try { await DisposeAsync(); probe = null; } catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }
    private async Task Apply(CrossDbContext db)
    {
        foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
            if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
    }
    public async Task DisposeAsync() { if (probe != null) await sql.DropProbeDatabaseAsync(probe); }

    [SkippableFact]
    public async Task Native_retry_race_preserves_one_draft_with_fixed_line_ids_and_populated_schema_rerun()
    {
        Skip.If(!sql.Available, sql.SkipReason); var id = Guid.NewGuid();
        using var a = sql.ContextFor(probe!, 1); using var b = sql.ContextFor(probe!, 1);
        var results = await Task.WhenAll(ConstructionR7PurchaseDraftTests.Create(a, seed, id), ConstructionR7PurchaseDraftTests.Create(b, seed, id));
        Assert.All(results, x => Assert.True(x.Ok, x.Code)); Assert.Single(results, x => x.Code == "DraftCreated");
        Assert.Single(results, x => x.Code == "AlreadyCreated"); Assert.Equal(results[0].OrderId, results[1].OrderId); Assert.Equal(results[0].LineIds, results[1].LineIds);
        using var verify = sql.ContextFor(probe!, 1); await verify.Database.OpenConnectionAsync();
        await Apply(verify); // populated rerun: the source, lines, receipt and audit must survive unchanged
        var order = await verify.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleAsync();
        Assert.Equal("Draft", order.Status); Assert.Equal(1.235m, order.SubTotal); Assert.Equal(.247m, order.TaxTotal); Assert.Equal(1.482m, order.GrandTotal);
        Assert.Equal(results[0].LineIds, order.Lines.Select(x => x.ID));
        var receipt = await verify.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleAsync(); Assert.Equal(3, receipt.CurrencyDecimals); Assert.Equal(64, receipt.InitialSourceVersion.Length);
        Assert.Equal(4, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionPurchaseDrafts') AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
        Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
        var immutable = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlRawAsync("UPDATE dbo.ConstructionPurchaseDrafts SET Reason=N'changed'")); Assert.Equal(51902, immutable.Number);
        var bypass = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlRawAsync("UPDATE dbo.PurchaseOrders SET Status=N'Approved'")); Assert.Equal(51903, bypass.Number);
        var originalLine = order.Lines.Single();
        await verify.PurchaseOrderLines.Where(x => x.PurchaseOrderId == order.ID).ExecuteDeleteAsync();
        verify.Add(new PurchaseOrderLine { PurchaseOrderId = order.ID, LineNo = originalLine.LineNo, ItemId = originalLine.ItemId,
            ItemDescription = originalLine.ItemDescription, Qty = originalLine.Qty, UoMId = originalLine.UoMId,
            UnitPrice = originalLine.UnitPrice, DiscountAmount = originalLine.DiscountAmount, TaxRate = originalLine.TaxRate, LineTotal = originalLine.LineTotal });
        await verify.SaveChangesAsync();
        var changed = await ConstructionR7PurchaseDraftTests.Create(verify, seed, id);
        Assert.False(changed.Ok); Assert.Equal("DraftSourceChanged", changed.Code); Assert.Null(changed.LineIds);
    }

    [SkippableFact]
    public async Task Native_foreign_company_cannot_attach_its_creation_receipt_to_another_company_source()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        Assert.True((await ConstructionR7PurchaseDraftTests.Create(db, seed, Guid.NewGuid())).Ok);
        var id = Guid.NewGuid();
        var refusal = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.ConstructionPurchaseDrafts(CompanyID,ProjectId,PurchaseOrderId,BudgetVersionId,CurrencyId,CurrencyDecimals,RequestId,RequestHash,InitialSourceVersion,Reason,CreatedBy,CreatedAt) SELECT 2,ProjectId,PurchaseOrderId,BudgetVersionId,CurrencyId,CurrencyDecimals,{id},RequestHash,InitialSourceVersion,Reason,CreatedBy,CreatedAt FROM dbo.ConstructionPurchaseDrafts WHERE CompanyID=1"));
        Assert.Equal(547, refusal.Number);
    }

    [SkippableFact]
    public async Task Native_late_audit_failure_rolls_back_header_lines_and_identity_then_same_request_can_retry()
    {
        Skip.If(!sql.Available, sql.SkipReason); var scope = new CompanyScopeHolder(); scope.Set(1, null); var failure = new FailAudit();
        using var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString).AddInterceptors(failure).Options, scope);
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConstructionR7PurchaseDraftTests.Create(db, seed, id));
        using (var verify = sql.ContextFor(probe!, 1))
        {
            Assert.Empty(await verify.PurchaseOrders.AsNoTracking().ToListAsync()); Assert.Empty(await verify.PurchaseOrderLines.AsNoTracking().ToListAsync());
            Assert.Empty(await verify.Set<ConstructionPurchaseDraft>().AsNoTracking().ToListAsync());
        }
        failure.Enabled = false; Assert.True((await ConstructionR7PurchaseDraftTests.Create(db, seed, id)).Ok);
    }

    [SkippableTheory]
    [InlineData(false, "Draft")] [InlineData(true, "Draft")] [InlineData(false, "draft")]
    public async Task Native_draft_cannot_receive_or_invoice_and_borrowed_refusal_preserves_owners_work(bool borrowed, string status)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using (var create = sql.ContextFor(probe!, 1)) Assert.True((await ConstructionR7PurchaseDraftTests.Create(create, seed, Guid.NewGuid())).Ok);
        using var graph = Graph(); var db = graph.GetRequiredService<CrossDbContext>();
        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync();
        if (status != "Draft") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.PurchaseOrders SET Status={status} WHERE ID={order.ID}");
        await using var outer = borrowed ? await db.Database.BeginTransactionAsync() : null;
        var marker = "OWNER-" + Guid.NewGuid().ToString("N");
        if (borrowed) { db.Add(new Vendor { CompanyID = 1, Name = marker, NameEn = marker }); await db.SaveChangesAsync(); }
        var procurement = graph.GetRequiredService<IProcurementService>();
        var receipt = await procurement.CreateReceiptAsync(1, seed.Vendor, seed.Warehouse, order.ID, DateTime.Today, null,
            new() { new() { ItemId = seed.Item, Qty = 1, UnitCost = 1.2345m, UoMId = seed.Unit, PurchaseOrderLineId = (await db.PurchaseOrderLines.AsNoTracking().SingleAsync()).ID } }, "test", seed.Currency, 1);
        Assert.False(receipt.ok); Assert.Equal("The purchase order must be approved before receiving goods", receipt.error);
        var invoice = await procurement.ConvertToInvoiceAsync(1, order.ID, "test");
        Assert.False(invoice.ok); Assert.Equal("The purchase order must be approved before invoicing", invoice.error);
        if (borrowed) { Assert.Same(outer, db.Database.CurrentTransaction); await outer!.CommitAsync(); }
        using var verify = sql.ContextFor(probe!, 1);
        Assert.Equal(borrowed ? 1 : 0, await verify.Vendors.CountAsync(x => x.Name == marker));
        Assert.Equal(status, (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await verify.GoodsReceipts.AsNoTracking().ToListAsync()); Assert.Empty(await verify.PurchaseInvoices.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }
    private ServiceProvider Graph()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddLocalization();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()); services.AddScoped(_ => sql.ContextFor(probe!, 1));
        services.AddScoped<ICurrencyService, CurrencyService>(); services.AddScoped<ICurrencyRounding, CurrencyRounding>();
        services.AddScoped<IFiscalPeriodService, FiscalPeriodService>(); services.AddScoped<IJournalEntryService, JournalEntryService>();
        services.AddScoped<IStockService, StockService>(); services.AddScoped<IPayableService, PayableService>();
        services.AddScoped<IFixedAssetService, FixedAssetService>(); services.AddScoped<IThreeWayMatchService, ThreeWayMatchService>();
        services.AddScoped<IProcurementService, ProcurementService>(); services.AddScoped<IEntityRegistry, EntityRegistry>();
        services.AddScoped<IBusinessContextAccessor>(_ => new ContextAccessor()); services.AddScoped<IBusinessEventService, BusinessEventService>();
        services.AddScoped<INotificationService>(_ => DispatchProxy.Create<INotificationService, SilentNotifications>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = false });
    }
    private sealed class ContextAccessor : IBusinessContextAccessor
    {
        public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(ConstructionR7OrderAllocationTests.Actor());
        public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult<BusinessContext?>(ConstructionR7OrderAllocationTests.Actor());
    }
    public class SilentNotifications : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var type = method.ReturnType.GenericTypeArguments[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type).Invoke(null, new[] { type.IsValueType ? Activator.CreateInstance(type) : null });
            }
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
    private sealed class FailAudit : SaveChangesInterceptor
    {
        public bool Enabled = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<ConstructionAuditEntry>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Injected draft audit failure");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
}
