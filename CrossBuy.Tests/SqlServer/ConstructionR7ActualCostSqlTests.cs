using System.Text.RegularExpressions;
using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Construction;
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
public sealed class ConstructionR7ActualCostSqlTests(UatSqlProbeFixture sql) : IAsyncLifetime
{
    private UatSqlProbeFixture.ProbeDatabase? probe;
    private int project, node, code, expense, payable, functional, document, changedCurrency;
    private string valuationScript = "";
    public async Task InitializeAsync()
    {
        if (!sql.Available) return;
        probe = await sql.CreateProbeDatabaseAsync("R7ACTUAL");
        try
        {
            using var db = sql.ContextFor(probe, 1); await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.ConstructionMaterialConsumptions; DROP TABLE dbo.ConstructionActualCostAllocationLines; DROP TABLE dbo.ConstructionActualCostAllocations; DROP INDEX IF EXISTS IX_JournalEntries_FunctionalCurrencyId ON dbo.JournalEntries; ALTER TABLE dbo.JournalEntries DROP COLUMN FunctionalCurrencyId,FunctionalCurrencyDecimals;");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            foreach (var filename in new[] { "journal_functional_currency_snapshot.sql", "construction_r7_actual_cost_allocations.sql", "construction_r7_material_consumptions.sql" })
            {
                var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql", filename));
                if (filename == "journal_functional_currency_snapshot.sql") valuationScript = script;
                for (var run = 0; run < 2; run++) await Apply(db, script);
            }
            var ids = await ConstructionR7OrderAllocationTests.Seed(db); project = ids.Project; node = ids.Node; code = ids.Code;
            var kwd = new Currency { Code = "KWD", Name = "Dinar", NameEn = "Dinar", DecimalPlaces = 3 };
            var egp = new Currency { Code = "EGP", Name = "Pound", NameEn = "Pound", DecimalPlaces = 2 };
            var jpy = new Currency { Code = "JPY", Name = "Yen", NameEn = "Yen", DecimalPlaces = 0 };
            var expType = new AccountType { Code = "EXP", Name = "Expense", StatementType = "IncomeStatement" };
            var liability = new AccountType { Code = "LIAB", Name = "Liability", StatementType = "BalanceSheet" };
            db.AddRange(kwd, egp, jpy, expType, liability); await db.SaveChangesAsync(); functional = kwd.ID; document = egp.ID; changedCurrency = jpy.ID;
            var company = new Companies { CompanyName = "Native test", Address = "", PhoneNumber = "", Email = "", DefaultCurrencyId = functional };
            db.Companies.Add(company);
            var cost = new Account { CompanyID = 1, Code = "510104", Name = "Project expense", NameEn = "Project expense", AccountTypeId = expType.ID, IsActive = true, IsPostable = true };
            var ap = new Account { CompanyID = 1, Code = "2101", Name = "Payable", NameEn = "Payable", AccountTypeId = liability.ID, IsActive = true, IsPostable = true };
            var year = new FiscalYear { CompanyID = 1, Name = "Native year", StartDate = DateTime.Today.AddDays(-10), EndDate = DateTime.Today.AddDays(10), Status = "Open" };
            db.AddRange(cost, ap, year); await db.SaveChangesAsync(); Assert.Equal(1, company.CompanyID); expense = cost.ID; payable = ap.ID;
            db.FiscalPeriods.Add(new FiscalPeriod { FiscalYearId = year.ID, PeriodNo = 1, StartDate = year.StartDate, EndDate = year.EndDate, Status = "Open" }); await db.SaveChangesAsync();
        }
        catch (Exception original)
        { try { await DisposeAsync(); probe = null; } catch (Exception cleanup) { throw new AggregateException(original, cleanup); } throw; }
    }
    private static async Task Apply(CrossDbContext db, string script)
    { foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$")) if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch); }
    public async Task DisposeAsync() { if (probe != null) await sql.DropProbeDatabaseAsync(probe); }
    private ServiceProvider Graph()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddLocalization();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped(_ => sql.ContextFor(probe!, 1));
        services.AddScoped<ICurrencyService, CurrencyService>(); services.AddScoped<ICurrencyRounding, CurrencyRounding>();
        services.AddScoped<IFiscalPeriodService, FiscalPeriodService>(); services.AddScoped<IJournalEntryService, JournalEntryService>();
        services.AddScoped<IEntityRegistry, EntityRegistry>(); services.AddScoped<IBusinessEventService, BusinessEventService>();
        services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(ConstructionR7OrderAllocationTests.Actor()));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = false });
    }
    private JournalEntryInput Input() => new() { CompanyID = 1, EntryDate = DateTime.Today.AddDays(-1), CurrencyId = document, Description = "Native actual source",
        Lines = new() { new() { AccountId = expense, ProjectId = project, Debit = 12.3456m }, new() { AccountId = payable, ProjectId = project, Credit = 12.3456m } } };
    private async Task<int> Posted()
    {
        using var graph = Graph(); var result = await graph.GetRequiredService<IJournalEntryService>().CreateAndPostAsync(Input(), null); Assert.True(result.ok, result.error);
        return result.entry!.Lines.Single(x => x.AccountId == expense).ID;
    }
    private async Task<ConstructionActualWrite> Allocate(CrossDbContext db, int line, Guid request, decimal amount)
    {
        var service = ConstructionR7ActualCostTests.Service(db); var preview = await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), project, line); Assert.True(preview.Ok, preview.Code);
        return await service.AllocateAsync(ConstructionR7OrderAllocationTests.Actor(), project, line, preview.Version!, request,
            new[] { new ConstructionActualSplit(node, code, amount) }, "Native expense attribution");
    }

    [SkippableFact]
    public async Task Workspace_uses_real_posted_source_and_registers_only_analytical_allocation()
    {
        Skip.If(!sql.Available, sql.SkipReason); var line = await Posted();
        using var db = sql.ContextFor(probe!, 1);
        var accessor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = ConstructionR7OrderAllocationTests.Actor() };
        var workspace = new ConstructionActualCostWorkspace(db, accessor, new ConstructionR7ActualCostTests.Access(), ConstructionR7ActualCostTests.Service(db));
        var page = await workspace.ReadAsync(project, line); Assert.NotNull(page); Assert.True(page.Source!.Ok, page.Source.Code);
        Assert.Equal(12.346m, page.Source.NetBaseAmount); Assert.Equal("KWD", page.SourceCurrency);
        var input = new CrossBuy.ViewModel.ConstructionActualInput { RequestId = Guid.NewGuid(), Reason = "Native UI attribution",
            Rows = [new() { WbsNodeId = node, CostCodeId = code, Amount = 12.346m }, new()] };
        var preview = await workspace.PreviewAsync(project, line, input); Assert.True(preview.Ok, preview.Code); input.Version = preview.Version;
        var saved = await workspace.AllocateAsync(project, line, input); Assert.True(saved.Ok, saved.Code);
        Assert.Equal("AlreadyAllocated", (await workspace.AllocateAsync(project, line, input)).Code);
        page = await workspace.ReadAsync(project, line); Assert.True(page!.AlreadyAllocated);
        using var verify = sql.ContextFor(probe!, 1);
        Assert.Single(await verify.JournalEntries.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.StockMovements.AsNoTracking().ToArrayAsync());
        Assert.Equal(12.346m, (await verify.Set<ConstructionActualCostAllocation>().AsNoTracking().SingleAsync()).NetBaseAmount);
    }

    [SkippableFact]
    public async Task Real_writer_snapshot_and_reversal_keep_original_basis_after_company_currency_changes()
    {
        Skip.If(!sql.Available, sql.SkipReason); var line = await Posted();
        using var graph = Graph(); var db = graph.GetRequiredService<CrossDbContext>();
        var original = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).SingleAsync();
        Assert.Equal(document, original.CurrencyId); Assert.Equal(functional, original.FunctionalCurrencyId); Assert.Equal(3, original.FunctionalCurrencyDecimals);
        Assert.Equal(12.346m, original.Lines.Single(x => x.ID == line).Debit);
        await db.Companies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DefaultCurrencyId, (int?)changedCurrency));
        await db.Currencies.Where(x => x.ID == functional).ExecuteUpdateAsync(x => x.SetProperty(p => p.DecimalPlaces, (byte)0));
        var reversal = await graph.GetRequiredService<IJournalEntryService>().ReverseAsync(original.ID, null, "Keep original valuation"); Assert.True(reversal.ok, reversal.error);
        var mirror = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.ID == reversal.reversalId);
        Assert.Equal(functional, mirror.FunctionalCurrencyId); Assert.Equal(3, mirror.FunctionalCurrencyDecimals);
        var mirrorLine = mirror.Lines.Single(x => x.AccountId == expense); Assert.Equal(12.346m, mirrorLine.Credit);
        Assert.True((await Allocate(db, line, Guid.NewGuid(), 12.346m)).Ok); Assert.True((await Allocate(db, mirrorLine.ID, Guid.NewGuid(), -12.346m)).Ok);
        var service = ConstructionR7ActualCostTests.Service(db); var coverage = await service.CoverageAsync(ConstructionR7OrderAllocationTests.Actor(), project);
        Assert.Empty(coverage.Issues); Assert.Equal(0m, coverage.NetActualsByCurrency![functional]);
        Assert.Equal(12.346m, (await service.CoverageAsync(ConstructionR7OrderAllocationTests.Actor(), project, original.EntryDate, original.EntryDate)).NetActualsByCurrency![functional]);
        await db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrencyId, functional));
        await db.PurchaseOrders.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Draft"));
        var exposure = await ConstructionR7BudgetExposureTests.Reader(db).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), project);
        Assert.True(exposure.Ok, exposure.Code); Assert.Equal(0m, Assert.Single(exposure.Rows!).Actual);
        var mutation = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.JournalEntries SET FunctionalCurrencyId={changedCurrency},FunctionalCurrencyDecimals=0 WHERE ID={original.ID}")); Assert.Equal(51601, mutation.Number);
    }

    [SkippableFact]
    public async Task Real_posted_expense_is_counted_in_serialized_admission_and_not_replaced_by_document_currency()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var line = await Posted(); using var db = sql.ContextFor(probe!, 1);
        Assert.True((await Allocate(db, line, Guid.NewGuid(), 12.346m)).Ok);
        await db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrencyId, functional));
        await db.Set<ConstructionBudgetLine>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Amount, 100m));
        await db.PurchaseOrders.ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrencyId, (int?)functional).SetProperty(p => p.Status, "Draft")
            .SetProperty(p => p.SubTotal, 90m).SetProperty(p => p.TaxTotal, 0m).SetProperty(p => p.GrandTotal, 90m));
        await db.PurchaseOrderLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.Qty, 1m).SetProperty(p => p.UnitPrice, 90m).SetProperty(p => p.LineTotal, 90m));
        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(); var sourceLine = await db.PurchaseOrderLines.AsNoTracking().SingleAsync();
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var allocations = new ConstructionOrderAllocationService(db, new ConstructionR7ActualCostTests.Access(), new ConstructionAuditService(db));
        async Task<(int Id, string Version)> Map(decimal amount)
        {
            var preview = await allocations.PreviewAsync(actor, project, order.ID); Assert.True(preview.Ok, preview.Code);
            var saved = await allocations.SaveAsync(actor, project, order.ID, preview.Version!, Guid.NewGuid(),
                new[] { new ConstructionCommitmentSplit(sourceLine.ID, node, code, amount) }, "Native expense-aware admission");
            Assert.True(saved.Ok, saved.Code); return (saved.Id!.Value, preview.Version!);
        }
        var first = await Map(90);
        var denied = await ConstructionR7ReservationTests.Service(db).ReserveAsync(actor, project, first.Id, first.Version, Guid.NewGuid(), "Actual cost makes this exceed budget");
        Assert.Equal("BudgetHardBlock", denied.Code);
        await db.PurchaseOrders.ExecuteUpdateAsync(x => x.SetProperty(p => p.SubTotal, 80m).SetProperty(p => p.GrandTotal, 80m));
        await db.PurchaseOrderLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.UnitPrice, 80m).SetProperty(p => p.LineTotal, 80m));
        var second = await Map(80);
        var accepted = await ConstructionR7ReservationTests.Service(db).ReserveAsync(actor, project, second.Id, second.Version, Guid.NewGuid(), "Exact remaining budget");
        Assert.True(accepted.GrantsCurrentAdmission, accepted.Code);
        using var verify = sql.ContextFor(probe!, 1);
        var exposure = await ConstructionR7BudgetExposureTests.Reader(verify).PreviewAsync(actor, project); Assert.True(exposure.Ok, exposure.Code);
        var row = Assert.Single(exposure.Rows!); Assert.Equal(12.346m, row.Actual); Assert.Equal(80m, row.Commitment); Assert.Equal(7.654m, row.Remaining);
        Assert.Equal(functional, exposure.CurrencyId); Assert.NotEqual(document, exposure.CurrencyId);
        Assert.Single(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Legacy_unknown_identity_is_never_backfilled_and_its_real_reversal_stays_unknown()
    {
        Skip.If(!sql.Available, sql.SkipReason); int legacy;
        using (var arrange = sql.ContextFor(probe!, 1))
        {
            var entry = new JournalEntry { CompanyID = 1, EntryDate = DateTime.Today.AddDays(-1), FiscalPeriodId = 1, CurrencyId = functional, Status = "Posted",
                Lines = new List<JournalEntryLine> { new() { LineNo = 1, AccountId = expense, ProjectId = project, Debit = 12.346m }, new() { LineNo = 2, AccountId = payable, ProjectId = project, Credit = 12.346m } } };
            arrange.Add(entry); await arrange.SaveChangesAsync(); legacy = entry.ID;
            await arrange.Database.OpenConnectionAsync(); await Apply(arrange, valuationScript); await Apply(arrange, valuationScript);
        }
        using var graph = Graph(); var db = graph.GetRequiredService<CrossDbContext>();
        var mirror = await graph.GetRequiredService<IJournalEntryService>().ReverseAsync(legacy, null, "Legacy reversal without guessing"); Assert.True(mirror.ok, mirror.error);
        using var verify = sql.ContextFor(probe!, 1); Assert.All(await verify.JournalEntries.AsNoTracking().ToListAsync(), x => { Assert.Null(x.FunctionalCurrencyId); Assert.Null(x.FunctionalCurrencyDecimals); });
        var coverage = await ConstructionR7ActualCostTests.Service(verify).CoverageAsync(ConstructionR7OrderAllocationTests.Actor(), project); Assert.Null(coverage.NetActualsByCurrency);
        Assert.Equal(2, coverage.Issues.Count(x => x.Code == "HistoricalBaseCurrencyIdentityNotProven"));
    }

    [SkippableFact]
    public async Task Concurrent_allocators_cannot_classify_one_gl_expense_twice_and_history_is_immutable()
    {
        Skip.If(!sql.Available, sql.SkipReason); var line = await Posted(); using var first = sql.ContextFor(probe!, 1); using var second = sql.ContextFor(probe!, 1);
        var results = await Task.WhenAll(Allocate(first, line, Guid.NewGuid(), 12.346m), Allocate(second, line, Guid.NewGuid(), 12.346m));
        Assert.Single(results, x => x.Ok); Assert.Single(results, x => x.Code is "ConcurrencyConflict" or "SourceAlreadyAllocated");
        using var verify = sql.ContextFor(probe!, 1); Assert.Single(await verify.Set<ConstructionActualCostAllocation>().AsNoTracking().ToListAsync());
        var edit = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlRawAsync("UPDATE dbo.ConstructionActualCostAllocationLines SET NetBaseAmount=1")); Assert.Equal(51702, edit.Number);
        Assert.Equal(7, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionActualCostAllocations'),OBJECT_ID('dbo.ConstructionActualCostAllocationLines')) AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
    }

    [SkippableFact]
    public async Task Late_attribution_audit_failure_preserves_gl_and_rolls_back_all_analytical_rows()
    {
        Skip.If(!sql.Available, sql.SkipReason); var line = await Posted(); var holder = new CompanyScopeHolder(); holder.Set(1, null); var failure = new FailAudit();
        using var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString).AddInterceptors(failure).Options, holder);
        var request = Guid.NewGuid(); await Assert.ThrowsAsync<InvalidOperationException>(() => Allocate(db, line, request, 12.346m));
        using (var verify = sql.ContextFor(probe!, 1))
        { Assert.Empty(await verify.Set<ConstructionActualCostAllocation>().AsNoTracking().ToListAsync()); Assert.Empty(await verify.Set<ConstructionActualCostAllocationLine>().AsNoTracking().ToListAsync()); Assert.Equal(12.346m, (await verify.JournalEntryLines.AsNoTracking().SingleAsync(x => x.ID == line)).Debit); }
        failure.Enabled = false; Assert.True((await Allocate(db, line, request, 12.346m)).Ok);
    }

    [SkippableFact]
    public async Task Manual_draft_rebuild_captures_the_new_rounding_basis_without_revaluing_posted_history()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var graph = Graph(); var writer = graph.GetRequiredService<IJournalEntryService>();
        var created = await writer.CreateDraftAsync(Input(), null); Assert.True(created.ok, created.error);
        var db = graph.GetRequiredService<CrossDbContext>(); await db.Companies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DefaultCurrencyId, (int?)changedCurrency));
        var edited = await writer.UpdateDraftAsync(created.entry!.ID, 1, Input(), null); Assert.True(edited.ok, edited.error);
        using var verify = sql.ContextFor(probe!, 1); var draft = await verify.JournalEntries.AsNoTracking().Include(x => x.Lines).SingleAsync();
        Assert.Equal(changedCurrency, draft.FunctionalCurrencyId); Assert.Equal(0, draft.FunctionalCurrencyDecimals); Assert.Equal(12m, draft.Lines.Single(x => x.AccountId == expense).Debit);
    }
    private sealed class FailAudit : SaveChangesInterceptor
    {
        public bool Enabled = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Enabled && data.Context!.ChangeTracker.Entries<ConstructionAuditEntry>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Injected actual attribution audit failure"); return base.SavingChangesAsync(data, result, cancellationToken); }
    }
}
