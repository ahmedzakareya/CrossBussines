using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7BudgetExposureTests
{
    [Fact]
    public async Task Exposure_witness_is_stable_for_reads_but_changes_when_equal_totals_have_different_sources()
    {
        using var f = new ConstructionTestFixture();
        var setup = await ConstructionR7ReservationTests.Arrange(f.Db);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var first = await Reader(f.Db).PreviewAsync(actor, setup.Project);
        var repeated = await Reader(f.Db).PreviewAsync(actor, setup.Project);
        Assert.True(first.Ok, first.Code); Assert.True(repeated.Ok, repeated.Code);
        Assert.NotNull(first.Version); Assert.Equal(64, first.Version.Length);
        Assert.Equal(first.Version, repeated.Version);

        async Task Remap(int orderId)
        {
            var allocation = new ConstructionOrderAllocationService(f.Db,
                new ConstructionR7OrderAllocationTests.Access(), new ConstructionAuditService(f.Db));
            var preview = await allocation.PreviewAsync(actor, setup.Project, orderId);
            Assert.True(preview.Ok, preview.Code);
            var line = await f.Db.PurchaseOrderLines.AsNoTracking().SingleAsync(x => x.PurchaseOrderId == orderId);
            var node = await f.Db.Set<WbsNode>().AsNoTracking().FirstAsync();
            var code = await f.Db.Set<ConstructionCostCode>().AsNoTracking().FirstAsync();
            var saved = await allocation.SaveAsync(actor, setup.Project, orderId, preview.Version!, Guid.NewGuid(),
                new[] { new ConstructionCommitmentSplit(line.ID, node.ID, code.ID, 60m) }, "Map current source status");
            Assert.True(saved.Ok, saved.Code);
        }

        // Equal approved commitment totals do not make distinct source documents interchangeable.
        await f.Db.PurchaseOrders.Where(x => x.ID == setup.FirstOrder)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Approved"));
        await Remap(setup.FirstOrder);
        var before = await Reader(f.Db).PreviewAsync(actor, setup.Project);
        Assert.True(before.Ok, before.Code);
        await f.Db.PurchaseOrders.Where(x => x.ID == setup.FirstOrder)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Draft"));
        await f.Db.PurchaseOrders.Where(x => x.ID == setup.SecondOrder)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Approved"));
        await Remap(setup.SecondOrder);
        var after = await Reader(f.Db).PreviewAsync(actor, setup.Project);
        Assert.True(after.Ok, after.Code);
        Assert.Equal(Assert.Single(before.Rows!), Assert.Single(after.Rows!));
        Assert.NotEqual(before.Version, after.Version);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync());
    }

    internal static ConstructionBudgetExposureService Reader(CrossDbContext db, bool allow = true) =>
        new(db, new ConstructionR7ActualCostTests.Access(allow), new ConstructionAuditService(db));

    // Arrangement only. Native writer qualification separately creates the financial source through JES.
    internal static async Task<(JournalEntry Entry, JournalEntryLine Expense)> Expense(CrossDbContext db, int project, decimal amount)
    {
        var expType = new AccountType { Code = "EXP", Name = "Expense", StatementType = "IncomeStatement" };
        var liabType = new AccountType { Code = "LIAB", Name = "Liability", StatementType = "BalanceSheet" };
        db.AddRange(expType, liabType); await db.SaveChangesAsync();
        var exp = new Account { CompanyID = 1, Code = "EXP-" + Guid.NewGuid().ToString("N"), Name = "Cost", AccountTypeId = expType.ID, IsActive = true, IsPostable = true };
        var ap = new Account { CompanyID = 1, Code = "AP-" + Guid.NewGuid().ToString("N"), Name = "Payable", AccountTypeId = liabType.ID, IsActive = true, IsPostable = true };
        db.AddRange(exp, ap); await db.SaveChangesAsync();
        var currency = (await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync()).CurrencyId;
        var line = new JournalEntryLine { LineNo = 1, AccountId = exp.ID, ProjectId = project, Debit = amount };
        var entry = new JournalEntry { CompanyID = 1, EntryDate = DateTime.Today, FiscalPeriodId = 1, Status = "Posted", CurrencyId = currency,
            FunctionalCurrencyId = currency, FunctionalCurrencyDecimals = 3,
            Lines = new List<JournalEntryLine> { line, new() { LineNo = 2, AccountId = ap.ID, ProjectId = project, Credit = amount } } };
        db.Add(entry); await db.SaveChangesAsync(); return (entry, line);
    }
    internal static async Task Attribute(CrossDbContext db, int project, int line, decimal amount)
    {
        var node = (await db.Set<WbsNode>().AsNoTracking().FirstAsync()).ID;
        var code = (await db.Set<ConstructionCostCode>().AsNoTracking().FirstAsync()).ID;
        var service = ConstructionR7ActualCostTests.Service(db); var actor = ConstructionR7OrderAllocationTests.Actor();
        var preview = await service.PreviewAsync(actor, project, line); Assert.True(preview.Ok, preview.Code);
        var saved = await service.AllocateAsync(actor, project, line, preview.Version!, Guid.NewGuid(),
            new[] { new ConstructionActualSplit(node, code, amount) }, "Authoritative expense attribution"); Assert.True(saved.Ok, saved.Code);
    }

    [Theory]
    [InlineData(40, true)] [InlineData(41, false)]
    public async Task Proven_actuals_are_included_in_new_admission_instead_of_a_blanket_history_refusal(int amount, bool allowed)
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db);
        var source = await Expense(f.Db, s.Project, amount); await Attribute(f.Db, s.Project, source.Expense.ID, amount);
        var actor = ConstructionR7OrderAllocationTests.Actor(); var before = await Reader(f.Db).PreviewAsync(actor, s.Project);
        Assert.True(before.Ok, before.Code); var row = Assert.Single(before.Rows!); Assert.Equal(amount, row.Actual); Assert.Equal(0, row.Commitment);
        var result = await ConstructionR7ReservationTests.Service(f.Db).ReserveAsync(actor, s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "Include actual cost");
        Assert.Equal(allowed, result.Ok); Assert.Equal(allowed ? "WithinBudget" : "BudgetHardBlock", result.Code);
        if (allowed)
        {
            var after = await Reader(f.Db).PreviewAsync(actor, s.Project); var current = Assert.Single(after.Rows!);
            Assert.Equal(60, current.Commitment); Assert.Equal(0, current.Remaining);
        }
    }

    internal static async Task<int> ConvertArrangedInvoice(CrossDbContext db, ConstructionR7ReservationTests.Setup s)
    {
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var reserved = await ConstructionR7ReservationTests.Service(db).ReserveAsync(actor, s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "Original commitment");
        Assert.True(reserved.Ok, reserved.Code);
        var source = await Expense(db, s.Project, 60);
        var order = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.ID == s.FirstOrder);
        var poLine = order.Lines.Single();
        var invoice = new PurchaseInvoice { CompanyID = 1, VendorId = order.VendorId, ProjectId = s.Project, CurrencyId = order.CurrencyId,
            ExchangeRate = 1, Status = "Posted", JournalEntryId = source.Entry.ID, SubTotal = 60, TaxTotal = 9, GrandTotal = 69,
            SubTotalBase = 60, TaxTotalBase = 9, GrandTotalBase = 69,
            Lines = new List<PurchaseInvoiceLine> { new() { LineNo = 1, Qty = poLine.Qty, UnitPrice = poLine.UnitPrice,
                DiscountAmount = poLine.DiscountAmount, TaxRate = poLine.TaxRate, LineTotal = 60, ExpenseAccountId = source.Expense.AccountId } } };
        db.Add(invoice); await db.SaveChangesAsync();
        // Arrange VAT separately so gross/AP are reconciled without becoming expense.
        var asset = new AccountType { Code = "ASSET", Name = "Asset", StatementType = "BalanceSheet" };
        db.Add(asset); await db.SaveChangesAsync(); var tax = new Account { CompanyID = 1, Code = "VAT", Name = "Input tax", AccountTypeId = asset.ID };
        db.Add(tax); await db.SaveChangesAsync();
        source.Entry.Lines.Single(x => x.Credit > 0).Credit = 69;
        source.Entry.Lines.Single(x => x.Credit > 0).LineNo = 3;
        source.Entry.Lines.Add(new JournalEntryLine { LineNo = 2, ProjectId = s.Project, AccountId = tax.ID, Debit = 9 });
        source.Entry.SourceType = "PurchaseInvoice"; source.Entry.SourceId = invoice.ID;
        await db.PurchaseOrders.Where(x => x.ID == order.ID).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Closed"));
        db.Add(new PurchaseOrderInvoiceLink { CompanyID = 1, PurchaseOrderId = order.ID, PurchaseInvoiceId = invoice.ID, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.Add(new PurchaseOrderInvoiceLineLink { CompanyID = 1, PurchaseOrderId = order.ID, PurchaseOrderLineId = poLine.ID,
            PurchaseInvoiceId = invoice.ID, PurchaseInvoiceLineId = invoice.Lines.Single().ID,
            JournalEntryId = source.Entry.ID, JournalEntryLineId = source.Expense.ID, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(); await Attribute(db, s.Project, source.Expense.ID, 60);
        var ready = await Reader(db).PreviewAsync(actor, s.Project);
        Assert.True(ready.Ok, ready.Code + ": " + string.Join(",", ready.Issues?.Select(x => x.Code) ?? Array.Empty<string>()));
        return invoice.ID;
    }

    [Fact]
    public async Task Complete_expense_conversion_replaces_a_reservation_once_and_never_counts_gross_as_cost()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db);
        await ConvertArrangedInvoice(f.Db, s); var actor = ConstructionR7OrderAllocationTests.Actor();
        var snapshot = await Reader(f.Db).PreviewAsync(actor, s.Project); Assert.True(snapshot.Ok, snapshot.Code + ": " + string.Join(",", snapshot.Issues?.Select(x => x.Code) ?? Array.Empty<string>()));
        var row = Assert.Single(snapshot.Rows!); Assert.Equal(60, row.Actual); Assert.Equal(0, row.Commitment); Assert.Equal(40, row.Remaining);
        Assert.Equal(new[] { s.FirstOrder }, snapshot.ReplacedOrderIds);
        Assert.Equal("BudgetHardBlock", (await ConstructionR7ReservationTests.Service(f.Db).ReserveAsync(actor, s.Project, s.SecondAllocation, s.SecondVersion, Guid.NewGuid(), "No double counting")).Code);
        await using var verify = f.NewContext(); Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("lineage")][InlineData("invoice-reversed")][InlineData("source-price")][InlineData("inventory")][InlineData("currency")]
    public async Task Incomplete_or_changed_conversion_never_manufactures_available_budget(string changed)
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db); var invoice = await ConvertArrangedInvoice(f.Db, s);
        if (changed == "lineage") await f.Db.Set<PurchaseOrderInvoiceLineLink>().ExecuteDeleteAsync();
        if (changed == "invoice-reversed") await f.Db.PurchaseInvoices.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        if (changed == "source-price") await f.Db.PurchaseOrderLines.Where(x => x.PurchaseOrderId == s.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.UnitPrice, 61m));
        if (changed == "inventory") await f.Db.PurchaseOrderLines.Where(x => x.PurchaseOrderId == s.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReceivedQty, 1m));
        if (changed == "currency") await f.Db.PurchaseInvoices.ExecuteUpdateAsync(x => x.SetProperty(p => p.ExchangeRate, 2m));
        var result = await Reader(f.Db).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project);
        Assert.False(result.Ok); Assert.Null(result.Rows); Assert.Null(result.CurrencyId);
        if (changed == "currency") Assert.Equal("BudgetCurrencyMismatch", result.Code);
        else Assert.Contains(result.Issues!, x => x.Code == (changed switch
        {
            "lineage" => "InvoiceLineageCoverageRequired", "invoice-reversed" => "InvoiceSourceNotProven",
            "source-price" => "InvoiceNetExpenseNotProven", _ => "InventoryConsumptionCoverageRequired"
        }));
    }

    [Fact]
    public async Task Missing_actual_attribution_and_historical_currency_are_unknown_not_zero()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db);
        var cost = await Expense(f.Db, s.Project, 40);
        Assert.Null((await Reader(f.Db).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project)).Rows);
        await f.Db.JournalEntries.ExecuteUpdateAsync(x => x.SetProperty(p => p.FunctionalCurrencyId, (int?)null).SetProperty(p => p.FunctionalCurrencyDecimals, (int?)null));
        var result = await Reader(f.Db).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project);
        Assert.Contains(result.Issues!, x => x.Code == "HistoricalBaseCurrencyIdentityNotProven"); Assert.Null(result.Rows);
    }

    [Fact]
    public async Task Reader_requires_the_executing_company_permission_and_a_clean_context()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db);
        Assert.Equal("AccessDenied", (await Reader(f.Db, false).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project)).Code);
        Assert.Equal("AccessDenied", (await Reader(f.Db).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(2), s.Project)).Code);
        var source = await f.Db.PurchaseOrders.SingleAsync(x => x.ID == s.FirstOrder); source.Notes = "Unsaved caller change";
        Assert.Equal("CleanUnitOfWorkRequired", (await Reader(f.Db).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project)).Code);
        await using var verify = f.NewContext(); Assert.Null((await verify.PurchaseOrders.AsNoTracking().SingleAsync(x => x.ID == s.FirstOrder)).Notes);
    }

    [Fact]
    public async Task Cancelling_an_unspent_source_releases_only_its_hold_and_does_not_authorize_spending_against_unknown_costs()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db);
        var actor = ConstructionR7OrderAllocationTests.Actor(); var reservations = ConstructionR7ReservationTests.Service(f.Db);
        var saved = await reservations.ReserveAsync(actor, s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "Unspent source"); Assert.True(saved.Ok, saved.Code);
        await Expense(f.Db, s.Project, 40); // Deliberately unclassified, so availability remains unknown.
        await f.Db.PurchaseOrders.Where(x => x.ID == s.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        Assert.True((await reservations.ReleaseCancelledAsync(actor, s.Project, saved.Id!.Value, Guid.NewGuid(), "Release only this clean cancellation")).Ok);
        var proposal = await reservations.ReserveAsync(actor, s.Project, s.SecondAllocation, s.SecondVersion, Guid.NewGuid(), "Unknown actual cost is still refused");
        Assert.False(proposal.Ok); Assert.Equal("RealizationCoverageRequired", proposal.Code);
        await using var verify = f.NewContext(); Assert.Single(await verify.Set<ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_net_credit_balance_is_not_advertised_as_extra_spending_authority_when_policy_refuses_negative_consumption()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db);
        var source = await Expense(f.Db, s.Project, 40);
        foreach (var line in source.Entry.Lines) (line.Debit, line.Credit) = (line.Credit, line.Debit);
        await f.Db.SaveChangesAsync(); await Attribute(f.Db, s.Project, source.Expense.ID, -40);
        var result = await Reader(f.Db).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project);
        Assert.False(result.Ok); Assert.Null(result.Rows); Assert.Contains(result.Issues!, x => x.Code == "NegativeConsumedAmountRequiresReview");
    }
}
