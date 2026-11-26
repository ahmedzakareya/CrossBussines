using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7FinancialSourceTests
{
    private static BusinessContext Actor(int company = 1) => new() { CompanyId = company, EmployeeId = 7, UserId = "r7-source", Source = BusinessContextSource.Test };
    private static ConstructionFinancialSourceService Service(ConstructionTestFixture f, bool allow = true) => new(f.Db, new Access(allow));
    private static async Task<PurchaseOrder> Order(ConstructionTestFixture f, int project, string status = "Approved", int? currency = 1)
    {
        var order = new PurchaseOrder { CompanyID = 1, ProjectId = project, VendorId = 5, CurrencyId = currency, Status = status,
            OrderDate = DateTime.Today, SubTotal = 100m, TaxTotal = 15m, GrandTotal = 115m,
            Lines = new List<PurchaseOrderLine> { new() { LineNo = 1, Qty = 2m, UnitPrice = 50m, LineTotal = 100m } } };
        f.Db.Add(order); await f.Db.SaveChangesAsync(); return order;
    }
    private static async Task<ConstructionFinancialSourceSnapshot> Read(ConstructionTestFixture f, int project)
    {
        var result = await Service(f).GetAsync(Actor(), project); Assert.True(result.Ok, result.Code); return result.Snapshot!;
    }

    [Fact]
    public async Task Open_commitments_use_stored_net_document_amounts_without_tax_or_currency_defaults()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f);
        await Order(f, ids.Project); await Order(f, ids.Project, currency: null);
        var snapshot = await Read(f, ids.Project);
        Assert.Equal(100m, snapshot.Orders[0].OpenCommitmentNetDocumentAmount); Assert.Equal(115m, snapshot.Orders[0].StoredGrandTotal);
        Assert.Null(snapshot.Orders[1].OpenCommitmentNetDocumentAmount); Assert.Null(snapshot.Orders[1].CurrencyId);
        Assert.Contains(snapshot.Issues, x => x.Code == "DocumentCurrencyNotProven");
        Assert.False(snapshot.CanEvaluateBudget); Assert.Null(snapshot.RemainingBudget); Assert.Null(snapshot.ProjectActualCost);
    }

    [Theory]
    [InlineData("Cancelled", "Released", true)]
    [InlineData("Draft", "DraftProposal", false)]
    [InlineData("Closed", "UnprovenClosed", false)]
    [InlineData("Unexpected", "UnknownStatus", false)]
    public async Task Unknown_closed_and_draft_sources_are_distinguished_from_released_commitments(string status, string classification, bool released)
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f); await Order(f, ids.Project, status);
        var row = Assert.Single((await Read(f, ids.Project)).Orders); Assert.Equal(classification, row.Classification);
        if (released) Assert.Equal(0m, row.OpenCommitmentNetDocumentAmount); else Assert.Null(row.OpenCommitmentNetDocumentAmount);
    }

    [Fact]
    public async Task Invoice_notes_or_same_vendor_and_project_never_prove_conversion_or_realized_expense()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f); var po = await Order(f, ids.Project, "Closed");
        f.Db.PurchaseInvoices.Add(new PurchaseInvoice { CompanyID = 1, ProjectId = ids.Project, VendorId = po.VendorId, Status = "Posted", Notes = "From purchase order " + po.OrderNo, GrandTotal = 115m });
        await f.Db.SaveChangesAsync(); var snapshot = await Read(f, ids.Project);
        Assert.Null(Assert.Single(snapshot.Orders).ProvenInvoiceId); Assert.Null(Assert.Single(snapshot.Invoices).ProvenPurchaseOrderId);
        Assert.Contains(snapshot.Issues, x => x.Code == "InvoiceSourceNotProven"); Assert.Empty(snapshot.Ledger); Assert.Null(snapshot.ProjectActualCost);
    }

    [Fact]
    public async Task A_conversion_relation_requires_the_matching_posted_financial_source_before_releasing_commercial_exposure()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f); var po = await Order(f, ids.Project, "Closed");
        var invoice = new PurchaseInvoice { CompanyID = 1, ProjectId = ids.Project, VendorId = po.VendorId, Status = "Posted", CurrencyId = 1, GrandTotal = 115m };
        f.Db.Add(invoice); await f.Db.SaveChangesAsync(); f.Db.Add(new PurchaseOrderInvoiceLink { CompanyID = 1, PurchaseOrderId = po.ID, PurchaseInvoiceId = invoice.ID, CreatedAt = DateTime.UtcNow }); await f.Db.SaveChangesAsync();
        var first = await Read(f, ids.Project); Assert.Equal("InvoicePostingNotProven", Assert.Single(first.Orders).Classification); Assert.Null(first.Orders[0].OpenCommitmentNetDocumentAmount);
        var journal = new JournalEntry { CompanyID = 1, Status = "Posted", CurrencyId = 1, SourceType = "PurchaseInvoice", SourceId = invoice.ID }; f.Db.Add(journal); await f.Db.SaveChangesAsync();
        invoice.JournalEntryId = journal.ID; await f.Db.SaveChangesAsync(); var second = await Read(f, ids.Project);
        Assert.Equal("TransferredToInvoice", Assert.Single(second.Orders).Classification); Assert.Equal(0m, second.Orders[0].OpenCommitmentNetDocumentAmount);
        Assert.Null(second.ProjectActualCost); Assert.Empty(second.Ledger);
        journal.Status = "Reversed"; await f.Db.SaveChangesAsync(); var reversed = await Read(f, ids.Project);
        Assert.Null(reversed.Orders[0].OpenCommitmentNetDocumentAmount); Assert.Equal("InvoicePostingNotProven", reversed.Orders[0].Classification);
    }

    [Fact]
    public async Task Reversed_originals_and_their_mirrors_remain_in_their_own_periods_and_balance_sheet_lines_are_not_expenses()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f);
        var expenseType = new AccountType { Code = "EXP", Name = "Expense", NameEn = "Expense", StatementType = "IncomeStatement" };
        var assetType = new AccountType { Code = "ASSET", Name = "Asset", NameEn = "Asset", StatementType = "BalanceSheet" }; f.Db.AddRange(expenseType, assetType); await f.Db.SaveChangesAsync();
        var expense = new Account { CompanyID = 1, Code = "Cost", Name = "Cost", NameEn = "Cost", AccountTypeId = expenseType.ID, IsActive = false };
        var asset = new Account { CompanyID = 1, Code = "Inventory", Name = "Inventory", NameEn = "Inventory", AccountTypeId = assetType.ID }; f.Db.AddRange(expense, asset); await f.Db.SaveChangesAsync();
        var earlier = new DateTime(2026, 9, 30); var later = earlier.AddDays(1);
        JournalEntry Entry(string status, DateTime date, int account, decimal debit, decimal credit) => new() { CompanyID = 1, Status = status, EntryDate = date, CurrencyId = 1,
            Lines = new List<JournalEntryLine> { new() { AccountId = account, ProjectId = ids.Project, Debit = debit, Credit = credit } } };
        f.Db.AddRange(Entry("Reversed", earlier, expense.ID, 30m, 0m), Entry("Posted", later, expense.ID, 0m, 30m), Entry("Posted", earlier, asset.ID, 100m, 0m), Entry("Draft", earlier, expense.ID, 999m, 0m)); await f.Db.SaveChangesAsync();
        var all = await Read(f, ids.Project); Assert.Equal(3, all.Ledger.Count); Assert.Equal(0m, all.Ledger.Where(x => x.Classification == "ExpensePosting").Sum(x => x.ExpenseNetStoredBase));
        Assert.Null(Assert.Single(all.Ledger.Where(x => x.Classification == "BalanceSheetPosting")).ExpenseNetStoredBase);
        var period = await Service(f).GetAsync(Actor(), ids.Project, earlier, earlier); Assert.True(period.Ok); Assert.Equal(30m, period.Snapshot!.Ledger.Where(x => x.Classification == "ExpensePosting").Sum(x => x.ExpenseNetStoredBase));
        Assert.Contains(all.Issues, x => x.Code == "HistoricalBaseCurrencyIdentityNotProven"); Assert.Null(all.ProjectActualCost);
    }

    [Fact]
    public async Task Source_versions_are_repeatable_but_change_for_database_truth_even_with_stale_tracking()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f); var po = await Order(f, ids.Project);
        var first = await Read(f, ids.Project); Assert.Equal(first.SourceVersion, (await Read(f, ids.Project)).SourceVersion);
        await using (var other = f.NewContext()) { var stored = await other.PurchaseOrders.SingleAsync(); stored.Status = "Cancelled"; await other.SaveChangesAsync(); }
        Assert.Equal("Approved", po.Status); var changed = await Read(f, ids.Project); Assert.Equal("Released", changed.Orders[0].Classification); Assert.NotEqual(first.SourceVersion, changed.SourceVersion);
    }

    [Fact]
    public async Task Unknown_caps_missing_policy_and_missing_classification_remain_explicit_without_invented_defaults()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f, policy: false);
        f.Db.Subcontracts.Add(new Subcontract { CompanyID = 1, ProjectId = ids.Project, VendorId = 5, ContractValue = null });
        f.Db.Add(new JournalEntry { CompanyID = 1, CurrencyId = 1, Status = "Posted", Lines = new List<JournalEntryLine> { new() { ProjectId = ids.Project, AccountId = int.MaxValue, Debit = 9m } } });
        await f.Db.SaveChangesAsync(); var report = await Read(f, ids.Project);
        foreach (var code in new[] { "ContractCapUnknown", "SubcontractCurrencyNotProven", "PolicyNotConfigured", "AccountClassificationNotProven", "ApprovedBaselineMissing", "WbsCostCodeAllocationsIncomplete" }) Assert.Contains(report.Issues, x => x.Code == code);
        Assert.Null(report.Subcontracts[0].ContractValue); Assert.Null(report.PolicyMode); Assert.Null(report.Ledger[0].ExpenseNetStoredBase);
    }

    [Fact]
    public async Task Denied_company_project_or_ambient_transaction_never_returns_a_false_empty_financial_report()
    {
        using var f = new ConstructionTestFixture(); var ids = await ConstructionR7BudgetVersionTests.Seed(f);
        foreach (var company in new[] { 0, 2 }) Assert.False((await Service(f).GetAsync(Actor(company), ids.Project)).Ok);
        Assert.False((await Service(f, false).GetAsync(Actor(), ids.Project)).Ok);
        Assert.Equal("ProjectNotFound", (await Service(f).GetAsync(Actor(), int.MaxValue)).Code);
        Assert.Equal("InvalidPeriod", (await Service(f).GetAsync(Actor(), ids.Project, DateTime.Today, DateTime.Today.AddDays(-1))).Code);
        await using var tx = await f.Db.Database.BeginTransactionAsync(); var project = await f.Db.Projects.SingleAsync(); project.Name = "Caller unsaved";
        var result = await Service(f).GetAsync(Actor(), ids.Project); Assert.False(result.Ok); Assert.Null(result.Snapshot); Assert.Equal("CommittedSourceReadRequired", result.Code);
        Assert.Equal(EntityState.Modified, f.Db.Entry(project).State); Assert.Same(tx, f.Db.Database.CurrentTransaction);
    }

    private sealed class Access(bool allow) : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext c, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default) => Task.FromResult(allow && action == ProjectsActions.BudgetView);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
}
