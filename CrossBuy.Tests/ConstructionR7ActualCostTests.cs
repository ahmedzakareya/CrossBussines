using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7ActualCostTests
{
    internal sealed class Access(bool allow = true) : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext c, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default) => Task.FromResult(allow && action is ProjectsActions.BudgetManage or ProjectsActions.BudgetView);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
    internal static ConstructionActualCostService Service(CrossDbContext db, bool allow = true) => new(db, new Access(allow), new ConstructionAuditService(db));
    internal static async Task<(int Project, int Node, int Code, int Entry, int ExpenseLine)> Seed(ConstructionTestFixture f, bool proven = true)
    {
        var ids = await ConstructionR7BudgetVersionTests.Seed(f);
        var expenseType = new AccountType { Code = "EXP", Name = "Expense", StatementType = "IncomeStatement" };
        var liabilityType = new AccountType { Code = "LIAB", Name = "Liability", StatementType = "BalanceSheet" };
        f.Db.AddRange(expenseType, liabilityType); await f.Db.SaveChangesAsync();
        var expense = new Account { CompanyID = 1, Code = "510104", Name = "Cost", AccountTypeId = expenseType.ID, IsPostable = true, IsActive = true };
        var payable = new Account { CompanyID = 1, Code = "2101", Name = "Payable", AccountTypeId = liabilityType.ID, IsPostable = true, IsActive = true };
        f.Db.AddRange(expense, payable); await f.Db.SaveChangesAsync();
        var currency = await f.Db.Currencies.AsNoTracking().SingleAsync();
        var entry = new JournalEntry { CompanyID = 1, EntryDate = DateTime.Today.AddDays(-1), FiscalPeriodId = 1, Status = "Posted", CurrencyId = currency.ID,
            FunctionalCurrencyId = proven ? currency.ID : null, FunctionalCurrencyDecimals = proven ? 3 : null,
            Lines = new List<JournalEntryLine> { new() { LineNo = 1, AccountId = expense.ID, ProjectId = ids.Project, Debit = 12.345m }, new() { LineNo = 2, AccountId = payable.ID, ProjectId = ids.Project, Credit = 12.345m } } };
        f.Db.Add(entry); await f.Db.SaveChangesAsync();
        return (ids.Project, ids.Node, ids.Code, entry.ID, entry.Lines.Single(x => x.Debit > 0).ID);
    }

    [Fact]
    public async Task Missing_historical_snapshot_is_unknown_even_when_header_and_current_company_currency_agree()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f, false); var service = Service(f.Db);
        Assert.Equal("HistoricalBaseCurrencyIdentityNotProven", (await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.ExpenseLine)).Code);
        var coverage = await service.CoverageAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project); Assert.Null(coverage.NetActualsByCurrency);
        Assert.Contains(coverage.Issues, x => x.Code == "HistoricalBaseCurrencyIdentityNotProven");
    }

    [Fact]
    public async Task Exact_allocation_retry_is_one_history_and_current_defaults_do_not_revalue_it()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f); var service = Service(f.Db); var actor = ConstructionR7OrderAllocationTests.Actor();
        Assert.Null((await service.CoverageAsync(actor, s.Project)).NetActualsByCurrency);
        var preview = await service.PreviewAsync(actor, s.Project, s.ExpenseLine); Assert.True(preview.Ok, preview.Code); var request = Guid.NewGuid();
        var splits = new[] { new ConstructionActualSplit(s.Node, s.Code, 12.345m) };
        var saved = await service.AllocateAsync(actor, s.Project, s.ExpenseLine, preview.Version!, request, splits, "Attribute actual expense"); Assert.True(saved.Ok, saved.Code);
        Assert.Equal(saved.Id, (await service.AllocateAsync(actor, s.Project, s.ExpenseLine, preview.Version!, request, splits, "Attribute actual expense")).Id);
        Assert.Equal("IdempotencyConflict", (await service.AllocateAsync(actor, s.Project, s.ExpenseLine, preview.Version!, request, splits, "Changed payload")).Code);
        await f.Db.Companies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DefaultCurrencyId, (int?)null));
        await f.Db.Currencies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DecimalPlaces, (byte)0));
        var coverage = await service.CoverageAsync(actor, s.Project); Assert.Empty(coverage.Issues); Assert.Equal(12.345m, coverage.NetActualsByCurrency![preview.CurrencyId!.Value]);
        await using var verify = f.NewContext(); Assert.True((await verify.Set<ConstructionActualCostAllocation>().AsNoTracking().SingleAsync()).IsSealed);
        Assert.Equal(12.345m, (await verify.JournalEntryLines.AsNoTracking().SingleAsync(x => x.ID == s.ExpenseLine)).Debit);
    }

    [Fact]
    public async Task Reversed_original_and_actual_mirror_remain_in_their_own_periods_and_reconcile_to_zero()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f); var service = Service(f.Db); var actor = ConstructionR7OrderAllocationTests.Actor();
        var original = await f.Db.JournalEntries.AsNoTracking().Include(x => x.Lines).SingleAsync();
        var mirror = new JournalEntry { CompanyID = 1, EntryDate = DateTime.Today, FiscalPeriodId = 1, Status = "Posted", SourceType = "Reversal", SourceId = original.ID,
            CurrencyId = original.CurrencyId, FunctionalCurrencyId = original.FunctionalCurrencyId, FunctionalCurrencyDecimals = original.FunctionalCurrencyDecimals,
            Lines = original.Lines.Select(x => new JournalEntryLine { LineNo = x.LineNo, AccountId = x.AccountId, ProjectId = x.ProjectId, Debit = x.Credit, Credit = x.Debit }).ToList() };
        f.Db.Add(mirror); await f.Db.SaveChangesAsync();
        await f.Db.JournalEntries.Where(x => x.ID == original.ID).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Reversed"));
        var reversalLine = mirror.Lines.Single(x => x.AccountId == original.Lines.Single(l => l.ID == s.ExpenseLine).AccountId);
        foreach (var source in new[] { (s.ExpenseLine, 12.345m), (reversalLine.ID, -12.345m) })
        {
            var preview = await service.PreviewAsync(actor, s.Project, source.Item1); Assert.True(preview.Ok, preview.Code);
            Assert.True((await service.AllocateAsync(actor, s.Project, source.Item1, preview.Version!, Guid.NewGuid(), new[] { new ConstructionActualSplit(s.Node, s.Code, source.Item2) }, "Allocate source and mirror")).Ok);
        }
        Assert.Equal(0m, Assert.Single((await service.CoverageAsync(actor, s.Project)).NetActualsByCurrency!).Value);
        Assert.Equal(12.345m, Assert.Single((await service.CoverageAsync(actor, s.Project, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-1))).NetActualsByCurrency!).Value);
        Assert.Equal(-12.345m, Assert.Single((await service.CoverageAsync(actor, s.Project, DateTime.Today, DateTime.Today)).NetActualsByCurrency!).Value);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("-12.345")]
    [InlineData("12.345000001")]
    public async Task Wrong_sign_precision_or_sum_never_changes_financial_history(string amount)
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f); var service = Service(f.Db); var actor = ConstructionR7OrderAllocationTests.Actor();
        var preview = await service.PreviewAsync(actor, s.Project, s.ExpenseLine);
        var result = await service.AllocateAsync(actor, s.Project, s.ExpenseLine, preview.Version!, Guid.NewGuid(),
            new[] { new ConstructionActualSplit(s.Node, s.Code, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)) }, "Refuse invalid distribution");
        Assert.Equal("ActualAllocationDoesNotReconcile", result.Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionActualCostAllocation>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task An_allocated_source_hidden_from_the_ledger_is_a_gap_not_a_zero_actual_total()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f); var service = Service(f.Db); var actor = ConstructionR7OrderAllocationTests.Actor();
        var preview = await service.PreviewAsync(actor, s.Project, s.ExpenseLine);
        Assert.True((await service.AllocateAsync(actor, s.Project, s.ExpenseLine, preview.Version!, Guid.NewGuid(), new[] { new ConstructionActualSplit(s.Node, s.Code, 12.345m) }, "Allocate expense")).Ok);
        await f.Db.JournalEntries.Where(x => x.ID == s.Entry).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        var coverage = await service.CoverageAsync(actor, s.Project); Assert.Null(coverage.NetActualsByCurrency);
        Assert.Contains(coverage.Issues, x => x.Code == "ActualAllocationSourceMissing");
    }

    [Fact]
    public async Task Nonexpense_foreign_scope_and_dirty_callers_are_refused()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f); var service = Service(f.Db); var actor = ConstructionR7OrderAllocationTests.Actor();
        var balance = await f.Db.JournalEntryLines.AsNoTracking().SingleAsync(x => x.Credit > 0);
        Assert.Equal("SourceNotExpense", (await service.PreviewAsync(actor, s.Project, balance.ID)).Code);
        Assert.Equal("AccessDenied", (await Service(f.Db, false).PreviewAsync(actor, s.Project, s.ExpenseLine)).Code);
        Assert.Equal("AccessDenied", (await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(2), s.Project, s.ExpenseLine)).Code);
        var project = await f.Db.Projects.SingleAsync(); project.Name = "Caller pending";
        Assert.Equal("CleanUnitOfWorkRequired", (await service.CoverageAsync(actor, s.Project)).Code); Assert.True(f.Db.ChangeTracker.HasChanges());
    }
}
