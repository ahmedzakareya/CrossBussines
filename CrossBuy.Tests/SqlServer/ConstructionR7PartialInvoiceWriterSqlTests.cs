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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

// Real receipt, payable, journal, stock and business-event writers; explicit permission policy doubles.
// Probe strips unrelated model FKs. Partial provenance uses the real authored FKs/triggers.
[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7PartialInvoiceWriterSqlTests(UatSqlProbeFixture sql) : IAsyncLifetime
{
    private UatSqlProbeFixture.ProbeDatabase? probe;
    private ConstructionR7PurchaseApprovalTests.Setup setup = null!;
    public async Task InitializeAsync()
    {
        if (!sql.Available) return;
        probe = await sql.CreateProbeDatabaseAsync("R7PARTWRITE");
        try
        {
            using var db = sql.ContextFor(probe, 1); await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionPartialInvoiceSlices; DROP TABLE dbo.ConstructionPartialInvoices;");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql/construction_r7_partial_invoice_provenance.sql"));
            foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
            setup = await ConstructionR7ReceiptWitnessTests.Admitted(db);
            await ConstructionR7StockInvoiceTests.Accounts(db, setup);
            var year = new FiscalYear { CompanyID = 1, Name = "Partial invoice test year", StartDate = DateTime.Today.AddDays(-10),
                EndDate = DateTime.Today.AddDays(10), Status = "Open" };
            db.Add(year); await db.SaveChangesAsync();
            db.FiscalPeriods.Add(new() { FiscalYearId = year.ID, PeriodNo = 1, StartDate = year.StartDate, EndDate = year.EndDate, Status = "Open" });
            await db.SaveChangesAsync();
        }
        catch (Exception original)
        {
            try { await DisposeAsync(); probe = null; } catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }
    public async Task DisposeAsync() { if (probe != null) await sql.DropProbeDatabaseAsync(probe); }
    private ServiceProvider Graph()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddLocalization();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped(_ => sql.ContextFor(probe!, 1));
        services.AddScoped<ICurrencyService, CurrencyService>(); services.AddScoped<ICurrencyRounding, CurrencyRounding>();
        services.AddScoped<IFiscalPeriodService, FiscalPeriodService>(); services.AddScoped<IJournalEntryService, JournalEntryService>();
        services.AddScoped<IStockService, StockService>(); services.AddScoped<IPayableService, PayableService>();
        services.AddScoped<IFixedAssetService, FixedAssetService>(); services.AddScoped<IThreeWayMatchService, ThreeWayMatchService>();
        services.AddScoped<IProcurementService, ProcurementService>(); services.AddScoped<IEntityRegistry, EntityRegistry>();
        services.AddScoped<IBusinessContextAccessor>(_ => new ConstructionR7PurchaseApprovalTests.ActorAccessor());
        services.AddScoped<IBusinessEventService, BusinessEventService>();
        services.AddScoped<INotificationService>(_ => DispatchProxy.Create<INotificationService, ConstructionR7ProcurementWriterSqlTests.SilentNotifications>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
    private ConstructionPartialInvoiceService Service(IServiceProvider services, bool purchase = true, bool project = true, bool failAudit = false)
    {
        var db = services.GetRequiredService<CrossDbContext>();
        IConstructionAuditService audit = new ConstructionAuditService(db);
        if (failAudit)
        {
            audit = DispatchProxy.Create<IConstructionAuditService, FailedAudit>();
            ((FailedAudit)audit).Context = db;
        }
        return new(db, new ConstructionR7OrderAllocationTests.Access(project),
            new ConstructionR7PurchaseApprovalTests.InventoryAuthority { Purchase = purchase },
            audit,
            services.GetRequiredService<ICurrencyRounding>(), services.GetRequiredService<IPayableService>());
    }
    public class FailedAudit : DispatchProxy
    {
        public CrossDbContext Context = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(Context.Database.CurrentTransaction);
            Assert.Single(Context.PurchaseInvoices.AsNoTracking().ToArray());
            Assert.Single(Context.Set<ConstructionPartialInvoice>().AsNoTracking().Where(x => x.IsSealed).ToArray());
            Assert.Single(Context.JournalEntries.AsNoTracking().Where(x => x.SourceType == "PurchaseInvoice").ToArray());
            Assert.Single(Context.BusinessEvents.AsNoTracking().Where(x => x.EventType == PurchaseInvoiceEvents.Created).ToArray());
            throw new InvalidOperationException("Injected late partial invoice audit failure");
        }
    }
    private async Task<int> Receive(decimal quantity)
    {
        using var graph = Graph(); using var scope = graph.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IProcurementService>().CreateReceiptAsync(1, setup.Seed.Vendor,
            setup.Seed.Warehouse, setup.Order, DateTime.Today, "Real partial invoice receiving",
            new() { new() { ItemId = setup.Seed.Item, Qty = quantity, UoMId = setup.Seed.Unit, UnitCost = 10,
                PurchaseOrderLineId = setup.Lines.Single() } }, "r7-partial-writer", setup.Seed.Currency, 1m);
        Assert.True(result.ok, result.error);
        using var read = sql.ContextFor(probe!, 1);
        return (await read.GoodsReceiptLines.AsNoTracking().OrderByDescending(x => x.ID).FirstAsync()).ID;
    }
    private async Task AssertPosted(int invoices, decimal received, int receipts = 1)
    {
        using var read = sql.ContextFor(probe!, 1);
        Assert.Equal(invoices, await read.PurchaseInvoices.CountAsync());
        Assert.Equal(invoices, await read.JournalEntries.CountAsync(x => x.SourceType == "PurchaseInvoice"));
        Assert.Equal(invoices, await read.BusinessEvents.CountAsync(x => x.EventType == PurchaseInvoiceEvents.Created));
        Assert.Equal(invoices, await read.Set<ConstructionPartialInvoice>().CountAsync(x => x.IsSealed));
        Assert.Equal(receipts, await read.StockMovements.CountAsync());
        Assert.Equal(received, (await read.StockBalances.AsNoTracking().SingleAsync()).QtyOnHand);
        Assert.All(await read.GoodsReceipts.AsNoTracking().ToArrayAsync(), x => Assert.Null(x.InvoiceId));
        Assert.Empty(await read.Set<PurchaseOrderInvoiceLink>().ToArrayAsync());
        Assert.Empty(await read.Set<PurchaseOrderInvoiceLineLink>().ToArrayAsync());
        Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToArrayAsync());
    }
    private async Task<ConstructionBudgetExposure> Budget()
    {
        using var read = sql.ContextFor(probe!, 1);
        return await ConstructionR7BudgetExposureTests.Reader(read).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Seed.Project);
    }

    [SkippableFact]
    public async Task Real_split_invoices_replay_once_and_clear_GRNI_without_duplicate_stock()
    {
        Skip.If(!sql.Available, sql.SkipReason); var receipt = await Receive(1);
        using var graph = Graph(); using var scope = graph.CreateScope(); var writer = Service(scope.ServiceProvider);
        var actor = ConstructionR7OrderAllocationTests.Actor(); var firstId = Guid.NewGuid();
        var page = await writer.ReadAsync(actor, setup.Seed.Project, setup.Order);
        Assert.True(page.Ok, page.Code);
        Assert.Equal(1m, Assert.Single(page.Receipts!).Available);
        Assert.Equal(0m, Assert.Single(page.Receipts!).PreviouslyBilled);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<CrossDbContext>().ChangeTracker.Entries());
        ConstructionInvoiceQuantityRequest[] first = [new(receipt, .4m)];
        var preview = await writer.PreviewAsync(actor, setup.Seed.Project, setup.Order, firstId, first); Assert.True(preview.Ok, preview.Code);
        Assert.Equal(4m, preview.Net); Assert.Equal(.8m, preview.Tax); await AssertPosted(0, 1);
        var before = await Budget(); Assert.True(before.Ok, before.Code);
        var issued = await writer.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, firstId, preview.Version!, first, "First partial");
        Assert.True(issued.Ok, issued.Code);
        var whole = await scope.ServiceProvider.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(1, setup.Order, "r7-no-mix");
        Assert.False(whole.ok); Assert.Equal(ProcurementService.PartialInvoiceConversionRefusal, whole.error); Assert.Null(whole.invoiceId);
        await AssertPosted(1, 1);
        var partialPage = await writer.ReadAsync(actor, setup.Seed.Project, setup.Order);
        Assert.True(partialPage.Ok, partialPage.Code);
        Assert.Equal(.4m, Assert.Single(partialPage.Receipts!).PreviouslyBilled);
        Assert.Equal(.6m, Assert.Single(partialPage.Receipts!).Available);
        Assert.NotEqual(page.Version, partialPage.Version);
        var intermediate = await Budget(); Assert.True(intermediate.Ok, intermediate.Code);
        Assert.Equal(10m, Assert.Single(intermediate.Rows!).Commitment); Assert.Equal(0m, Assert.Single(intermediate.Rows!).Actual);
        Assert.NotEqual(before.Version, intermediate.Version);
        var retry = await writer.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, firstId, preview.Version!, first, "First partial");
        Assert.True(retry.Ok, retry.Code); Assert.Equal("AlreadyIssued", retry.Code); Assert.Equal(issued.InvoiceId, retry.InvoiceId);
        var nextId = Guid.NewGuid(); ConstructionInvoiceQuantityRequest[] next = [new(receipt, .6m)];
        var remaining = await writer.PreviewAsync(actor, setup.Seed.Project, setup.Order, nextId, next); Assert.True(remaining.Ok, remaining.Code);
        Assert.True((await writer.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, nextId, remaining.Version!, next, "Final partial")).Ok);
        await AssertPosted(2, 1);
        var closedWhole = await scope.ServiceProvider.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(1, setup.Order, "r7-no-mix-final");
        Assert.False(closedWhole.ok); Assert.Equal(ProcurementService.PartialInvoiceConversionRefusal, closedWhole.error); Assert.Null(closedWhole.invoiceId);
        await AssertPosted(2, 1);
        using var read = sql.ContextFor(probe!, 1);
        Assert.Equal(1m, await read.Set<ConstructionPartialInvoiceSlice>().SumAsync(x => x.Quantity));
        Assert.Equal(10m, await read.PurchaseInvoices.SumAsync(x => x.SubTotal)); Assert.Equal(2m, await read.PurchaseInvoices.SumAsync(x => x.TaxTotal));
        var final = await Budget(); Assert.True(final.Ok, final.Code);
        var budget = Assert.Single(final.Rows!); Assert.Equal(10m, budget.Commitment); Assert.Equal(0m, budget.Actual); Assert.Equal(90m, budget.Remaining);
        Assert.NotEqual(intermediate.Version, final.Version);
        await read.PurchaseInvoiceLines.Where(x => x.PurchaseInvoiceId == issued.InvoiceId).ExecuteUpdateAsync(x => x.SetProperty(p => p.UnitPrice, 11m));
        var corruptPage = await writer.ReadAsync(actor, setup.Seed.Project, setup.Order);
        Assert.False(corruptPage.Ok); Assert.Null(corruptPage.Receipts); Assert.Null(corruptPage.Version);
        var refused = await Budget(); Assert.False(refused.Ok); Assert.Null(refused.Rows);
    }

    [SkippableTheory]
    [InlineData("quantity")][InlineData("stale")][InlineData("permission")][InlineData("project-permission")]
    [InlineData("company")][InlineData("request-change")][InlineData("borrowed")]
    [InlineData("portal")][InlineData("missing-currency")][InlineData("dirty")][InlineData("closed-period")][InlineData("changed-precision")]
    [InlineData("legacy-no-mix-trigger")]
    public async Task Invalid_partial_requests_leave_no_financial_or_stock_side_effect(string kind)
    {
        Skip.If(!sql.Available, sql.SkipReason); var receipt = await Receive(1);
        using var graph = Graph(); using var scope = graph.CreateScope(); var services = scope.ServiceProvider;
        var actor = ConstructionR7OrderAllocationTests.Actor(); var writer = Service(services); var id = Guid.NewGuid();
        ConstructionInvoiceQuantityRequest[] input = [new(receipt, .4m)];
        var preview = await writer.PreviewAsync(actor, setup.Seed.Project, setup.Order, id, input); Assert.True(preview.Ok, preview.Code);
        if (kind == "legacy-no-mix-trigger")
        {
            // Force the authored no-mix error at the real converter's late link insert.
            // This proves rollback/translation, not the scheduling of a concurrent request.
            await services.GetRequiredService<CrossDbContext>().Database.ExecuteSqlRawAsync(
                "CREATE OR ALTER TRIGGER dbo.TR_PurchaseOrderInvoiceLinks_NoPartialMix ON dbo.PurchaseOrderInvoiceLinks AFTER INSERT AS BEGIN SET NOCOUNT ON; THROW 51911,'Injected late no-mix guard',1; END;");
            var full = await services.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(1, setup.Order, "r7-late-no-mix");
            Assert.False(full.ok); Assert.Equal(ProcurementService.PartialInvoiceConversionRefusal, full.error); Assert.Null(full.invoiceId);
            await AssertPosted(0, 1); return;
        }
        if (kind == "request-change") Assert.True((await writer.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, id, preview.Version!, input, "Original")).Ok);
        if (kind == "quantity") input = [new(receipt, 1.1m)];
        if (kind == "stale") await services.GetRequiredService<CrossDbContext>().GoodsReceipts.ExecuteUpdateAsync(x => x.SetProperty(p => p.ReceiptDate, DateTime.Today.AddDays(-1)));
        if (kind == "permission") writer = Service(services, purchase: false);
        if (kind == "project-permission") writer = Service(services, project: false);
        if (kind == "company") actor = ConstructionR7OrderAllocationTests.Actor(2);
        if (kind == "portal") actor = new BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "external-test", Source = BusinessContextSource.ExternalPortal };
        if (kind == "missing-currency") await services.GetRequiredService<CrossDbContext>().Currencies.ExecuteDeleteAsync();
        if (kind == "changed-precision") await services.GetRequiredService<CrossDbContext>().Currencies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DecimalPlaces, (byte)4));
        if (kind == "closed-period") await services.GetRequiredService<CrossDbContext>().FiscalPeriods.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Closed"));
        if (kind == "dirty") services.GetRequiredService<CrossDbContext>().Add(new AccountType { Code = "UNSAVED", Name = "Caller owns this pending change" });
        await using var borrowed = kind == "borrowed" ? await services.GetRequiredService<CrossDbContext>().Database.BeginTransactionAsync() : null;
        if (kind is "permission" or "project-permission" or "company" or "portal" or "dirty" or "borrowed" or "missing-currency" or "changed-precision")
        {
            var deniedPage = await writer.ReadAsync(actor, setup.Seed.Project, setup.Order);
            Assert.False(deniedPage.Ok); Assert.Null(deniedPage.Receipts); Assert.Null(deniedPage.Version);
        }
        var result = await writer.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, id, preview.Version!, input, "Changed request");
        Assert.False(result.Ok); Assert.Null(result.InvoiceId);
        if (kind == "dirty")
        {
            Assert.Equal("CleanUnitOfWorkRequired", result.Code);
            Assert.True(services.GetRequiredService<CrossDbContext>().ChangeTracker.HasChanges());
        }
        if (borrowed != null) await borrowed.RollbackAsync();
        await AssertPosted(kind == "request-change" ? 1 : 0, 1);
    }

    [SkippableFact]
    public async Task Later_receiving_remains_available_after_the_first_partial_invoice()
    {
        Skip.If(!sql.Available, sql.SkipReason); var firstReceipt = await Receive(.4m);
        using var graph = Graph(); using var scope = graph.CreateScope(); var writer = Service(scope.ServiceProvider);
        var actor = ConstructionR7OrderAllocationTests.Actor(); var id = Guid.NewGuid(); ConstructionInvoiceQuantityRequest[] first = [new(firstReceipt, .4m)];
        var preview = await writer.PreviewAsync(actor, setup.Seed.Project, setup.Order, id, first); Assert.True(preview.Ok, preview.Code);
        var issued = await writer.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, id, preview.Version!, first, "Received first part"); Assert.True(issued.Ok, issued.Code);
        var secondReceipt = await Receive(.6m); var nextId = Guid.NewGuid(); ConstructionInvoiceQuantityRequest[] next = [new(secondReceipt, .6m)];
        var remaining = await writer.PreviewAsync(actor, setup.Seed.Project, setup.Order, nextId, next); Assert.True(remaining.Ok, remaining.Code);
        var second = await writer.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, nextId, remaining.Version!, next, "Received remainder"); Assert.True(second.Ok, second.Code);
        await AssertPosted(2, 1, 2);
    }

    [SkippableFact]
    public async Task Late_audit_failure_rolls_back_real_invoice_journal_event_and_provenance_then_retries()
    {
        Skip.If(!sql.Available, sql.SkipReason); var receipt = await Receive(1);
        using var graph = Graph(); using var scope = graph.CreateScope(); var services = scope.ServiceProvider;
        var actor = ConstructionR7OrderAllocationTests.Actor(); var writer = Service(services); var id = Guid.NewGuid();
        ConstructionInvoiceQuantityRequest[] input = [new(receipt, .4m)];
        var preview = await writer.PreviewAsync(actor, setup.Seed.Project, setup.Order, id, input); Assert.True(preview.Ok, preview.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(services, failAudit: true).IssueAsync(actor,
            setup.Seed.Project, setup.Order, DateTime.Today, id, preview.Version!, input, "Retry after audit failure"));
        await AssertPosted(0, 1);
        using (var read = sql.ContextFor(probe!, 1)) Assert.Empty(await read.Set<ConstructionPartialInvoiceSlice>().ToArrayAsync());
        var retry = await writer.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, id, preview.Version!, input, "Retry after audit failure");
        Assert.True(retry.Ok, retry.Code); await AssertPosted(1, 1);
    }

    [SkippableTheory][InlineData(false)][InlineData(true)]
    public async Task Concurrent_requests_or_identical_replays_cannot_duplicate_an_invoice(bool sameRequest)
    {
        Skip.If(!sql.Available, sql.SkipReason); var receipt = await Receive(1);
        using var graph = Graph(); var firstId = Guid.NewGuid(); var nextId = sameRequest ? firstId : Guid.NewGuid();
        ConstructionInvoiceQuantityRequest[] input = [new(receipt, sameRequest ? .4m : .6m)];
        string version;
        using (var previewScope = graph.CreateScope())
        {
            var preview = await Service(previewScope.ServiceProvider).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Seed.Project, setup.Order, firstId, input);
            Assert.True(preview.Ok, preview.Code); version = preview.Version!;
        }
        async Task<ConstructionPartialInvoiceResult> Issue(Guid id)
        {
            using var work = graph.CreateScope();
            return await Service(work.ServiceProvider).IssueAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Seed.Project,
                setup.Order, DateTime.Today, id, version, input, "Concurrent partial");
        }
        var outcomes = await Task.WhenAll(Task.Run(() => Issue(firstId)), Task.Run(() => Issue(nextId)));
        Assert.Single(outcomes, x => x.Code == "Issued");
        Assert.Contains(outcomes, x => sameRequest ? x.Code == "AlreadyIssued" : x.Code == "ConcurrencyConflict");
        await AssertPosted(1, 1);
    }

    private async Task<int> IssueMaterial(ServiceProvider graph)
    {
        using var read = sql.ContextFor(probe!, 1);
        var expenseType = new AccountType { Code = "EXP", Name = "Material expense", StatementType = "IncomeStatement" };
        read.Add(expenseType); await read.SaveChangesAsync();
        var expense = new Account { CompanyID = 1, Code = "510199", Name = "Partial invoice consumption", AccountTypeId = expenseType.ID, IsActive = true, IsPostable = true };
        read.Add(expense); await read.SaveChangesAsync();
        using var work = graph.CreateScope();
        var issued = await work.ServiceProvider.GetRequiredService<IStockService>().PostMovementAsync(1, new MovementRequest {
            ItemId = setup.Seed.Item, WarehouseId = setup.Seed.Warehouse, UoMId = setup.Seed.Unit, Direction = -1, Qty = .4m,
            SourceType = "Issue", ProjectId = setup.Seed.Project, CounterAccountOverride = expense.ID, PostToGl = true }, "r7-partial-consumption");
        Assert.True(issued.ok, issued.error);
        var movement = await read.StockMovements.AsNoTracking().SingleAsync(x => x.ID == issued.movement!.ID);
        var debit = await read.JournalEntryLines.AsNoTracking().SingleAsync(x => x.JournalEntryId == movement.JournalEntryId && x.Debit > 0);
        await ConstructionR7BudgetExposureTests.Attribute(read, setup.Seed.Project, debit.ID, movement.TotalCost);
        return movement.ID;
    }
    private async Task ApplyConsumptionDdl()
    {
        using var db = sql.ContextFor(probe!, 1); await db.Database.OpenConnectionAsync();
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var name in new[] { "construction_r7_actual_cost_allocations.sql", "construction_r7_material_consumptions.sql" })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql", name));
            foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
        }
    }

    [SkippableTheory][InlineData("before")][InlineData("between")][InlineData("after")]
    public async Task Real_consumption_before_between_or_after_partial_invoices_transfers_commitment_once(string stage)
    {
        Skip.If(!sql.Available, sql.SkipReason); var receipt = await Receive(1); await ApplyConsumptionDdl();
        using var graph = Graph(); var actor = ConstructionR7OrderAllocationTests.Actor(); string? factHash = null; string? financialVersion = null;
        async Task Bill(decimal quantity)
        {
            using var work = graph.CreateScope(); var service = Service(work.ServiceProvider); var id = Guid.NewGuid();
            ConstructionInvoiceQuantityRequest[] input = [new(receipt, quantity)];
            var preview = await service.PreviewAsync(actor, setup.Seed.Project, setup.Order, id, input); Assert.True(preview.Ok, preview.Code);
            var result = await service.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, id, preview.Version!, input, "Partial invoice with consumption"); Assert.True(result.Ok, result.Code);
        }
        async Task Consume()
        {
            var issue = await IssueMaterial(graph);
            using var write = sql.ContextFor(probe!, 1); var service = ConstructionR7MaterialConsumptionTests.Service(write);
            var preview = await service.PreviewAsync(actor, setup.Seed.Project, receipt, issue); Assert.True(preview.Ok, preview.Code);
            var result = await service.RegisterAsync(actor, setup.Seed.Project, receipt, issue, preview.Version!, Guid.NewGuid(), "Real partial invoice material use"); Assert.True(result.Ok, result.Code);
            using var fresh = sql.ContextFor(probe!, 1); factHash = (await fresh.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync()).SourceVersion;
            var exposure = await Budget(); Assert.True(exposure.Ok, exposure.Code); financialVersion = exposure.Version;
        }
        if (stage == "before") await Consume();
        await Bill(.4m);
        if (stage == "between") await Consume();
        await Bill(.6m);
        if (stage == "after") await Consume();
        var final = await Budget(); Assert.True(final.Ok, final.Code);
        var row = Assert.Single(final.Rows!); Assert.Equal(4m, row.Actual); Assert.Equal(6m, row.Commitment); Assert.Equal(90m, row.Remaining);
        if (stage != "after") Assert.NotEqual(financialVersion, final.Version);
        using var read = sql.ContextFor(probe!, 1);
        Assert.Equal(factHash, (await read.Set<ConstructionMaterialConsumption>().AsNoTracking().SingleAsync()).SourceVersion);
        Assert.Equal(.6m, (await read.StockBalances.AsNoTracking().SingleAsync()).QtyOnHand);
        Assert.Equal(2, await read.StockMovements.CountAsync()); Assert.Equal(2, await read.PurchaseInvoices.CountAsync());
        Assert.Empty(await read.Set<ConstructionCommitmentRelease>().ToArrayAsync());
    }

    [SkippableFact]
    public async Task Partial_invoice_invalidates_a_stale_consumption_intent_without_rewriting_issue_facts()
    {
        Skip.If(!sql.Available, sql.SkipReason); var receipt = await Receive(1); await ApplyConsumptionDdl();
        using var graph = Graph(); var issue = await IssueMaterial(graph); var actor = ConstructionR7OrderAllocationTests.Actor();
        using var write = sql.ContextFor(probe!, 1); var consumption = ConstructionR7MaterialConsumptionTests.Service(write);
        var stale = await consumption.PreviewAsync(actor, setup.Seed.Project, receipt, issue); Assert.True(stale.Ok, stale.Code);
        using (var work = graph.CreateScope())
        {
            var service = Service(work.ServiceProvider); var id = Guid.NewGuid(); ConstructionInvoiceQuantityRequest[] input = [new(receipt, .4m)];
            var preview = await service.PreviewAsync(actor, setup.Seed.Project, setup.Order, id, input); Assert.True(preview.Ok, preview.Code);
            var invoice = await service.IssueAsync(actor, setup.Seed.Project, setup.Order, DateTime.Today, id, preview.Version!, input, "Financial transition"); Assert.True(invoice.Ok, invoice.Code);
        }
        var rejected = await consumption.RegisterAsync(actor, setup.Seed.Project, receipt, issue, stale.Version!, Guid.NewGuid(), "Stale financial stage");
        Assert.False(rejected.Ok); Assert.Equal("ConcurrencyConflict", rejected.Code);
        var fresh = await consumption.PreviewAsync(actor, setup.Seed.Project, receipt, issue); Assert.True(fresh.Ok, fresh.Code); Assert.NotEqual(stale.Version, fresh.Version);
        Assert.True((await consumption.RegisterAsync(actor, setup.Seed.Project, receipt, issue, fresh.Version!, Guid.NewGuid(), "Current financial stage")).Ok);
        var exposure = await Budget(); Assert.True(exposure.Ok, exposure.Code); Assert.Equal(6m, Assert.Single(exposure.Rows!).Commitment);
    }

    [SkippableFact]
    public async Task Consumption_workspace_links_real_receipt_and_issue_without_new_stock_or_gl()
    {
        Skip.If(!sql.Available,sql.SkipReason);var receipt=await Receive(1);await ApplyConsumptionDdl();
        using var graph=Graph();var issue=await IssueMaterial(graph);
        using var db=sql.ContextFor(probe!,1);
        var stock=await db.StockMovements.CountAsync();var gl=await db.JournalEntries.CountAsync();
        var actors=new ConstructionR7PurchaseApprovalTests.ActorAccessor{Current=ConstructionR7OrderAllocationTests.Actor()};
        var access=new ConstructionR7ActualCostTests.Access(true);
        var workspace=new ConstructionConsumptionWorkspace(db,actors,access,ConstructionR7MaterialConsumptionTests.Service(db));
        var input=new CrossBuy.ViewModel.ConstructionConsumptionInput{ReceiptLineId=receipt,IssueMovementId=issue,RequestId=Guid.NewGuid(),Reason="Explicit native source linkage"};
        var choices=await workspace.ReadAsync(setup.Seed.Project);Assert.Contains(choices!.Sources!.Receipts,x=>x.Id==receipt);Assert.Contains(choices.Sources.Issues,x=>x.Id==issue);
        var preview=await workspace.PreviewAsync(setup.Seed.Project,input);Assert.True(preview.Ok,preview.Code);Assert.Equal(4m,preview.NetBaseCost);input.Version=preview.Version;
        var result=await workspace.RegisterAsync(setup.Seed.Project,input);Assert.True(result.Ok,result.Code);
        Assert.Equal("AlreadyRegistered",(await workspace.RegisterAsync(setup.Seed.Project,input)).Code);
        var history=await workspace.ReadAsync(setup.Seed.Project);Assert.Single(history!.History);Assert.DoesNotContain(history.Sources!.Issues,x=>x.Id==issue);
        using var verify=sql.ContextFor(probe!,1);Assert.Single(await verify.Set<ConstructionMaterialConsumption>().AsNoTracking().ToListAsync());
        Assert.Equal(stock,await verify.StockMovements.CountAsync());Assert.Equal(gl,await verify.JournalEntries.CountAsync());
        Assert.Empty(await verify.Set<ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
    }
}
