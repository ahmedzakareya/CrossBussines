using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7PurchaseDraftTests
{
    internal sealed record SeedData(int Project, int Baseline, byte[] Version, int Vendor, int Warehouse, int Item, int Unit, int Currency, int Node, int Cost);
    internal sealed class Localizer : IStringLocalizer<CrossBuy.SharedResources>
    {
        public LocalizedString this[string key] => new(key, key);
        public LocalizedString this[string key, params object[] arguments] => new(key, string.Format(key, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
    internal static ConstructionPurchaseDraftService Service(CrossDbContext db, bool allow = true) => new(db,
        new ConstructionR7OrderAllocationTests.Access(allow), new ConstructionAuditService(db),
        new CurrencyRounding(db, new CurrencyService(db), new Localizer()));
    internal static async Task<SeedData> Seed(CrossDbContext db)
    {
        var currency = new Currency { Code = "TST", Name = "Test", NameEn = "Test", DecimalPlaces = 3 };
        db.Add(currency); await db.SaveChangesAsync();
        var company = new Companies { CompanyName = "Test", Address = "", PhoneNumber = "", Email = "", DefaultCurrencyId = currency.ID };
        db.Add(company); await db.SaveChangesAsync(); Assert.Equal(1, company.CompanyID);
        var project = new Project { CompanyID = 1, Code = "R7DRAFT", Name = "Project", Status = "Active", IsActive = true };
        var vendor = new Vendor { CompanyID = 1, Name = "Vendor", NameEn = "Vendor", IsActive = true };
        var warehouse = new Warehouse { CompanyID = 1, Code = "R7DRAFT", Name = "Warehouse", NameEn = "Warehouse", IsActive = true };
        var unit = new UnitOfMeasure { CompanyID = 1, Code = "R7DRAFT", Name = "Unit", NameEn = "Unit", IsActive = true };
        var category = new ItemCategory { CompanyID = 1, Code = "R7DRAFT", Name = "Services", NameEn = "Services" };
        db.AddRange(project, vendor, warehouse, unit, category, new ConstructionBudgetPolicy { CompanyID = 1, Mode = "HardBlock", Reason = "Explicit company choice" });
        await db.SaveChangesAsync();
        var item = new Item { CompanyID = 1, ItemCode = "R7DRAFT", Barcode = "R7DRAFT", Name = "Service", NameEn = "Service",
            ItemType = ItemTypes.Service, ItemCategoryId = category.ID, BaseUoMId = unit.ID, IsActive = true };
        var baseline = new ConstructionBudgetVersion { CompanyID = 1, ProjectId = project.ID, VersionNo = 1, Status = "Approved",
            IsBaseline = true, CurrencyId = currency.ID, CreatedBy = 7, ApprovedBy = 8, Reason = "Approved estimate" };
        var node = new WbsNode { CompanyID = 1, ProjectId = project.ID, Code = "A", Name = "Work", Path = "/1/", Depth = 1,
            Status = "Active", OwnsBudget = true, OwnsCost = true, CreatedBy = 7, ProgressMethod = "Quantity" };
        var cost = new ConstructionCostCode { CompanyID = 1, Code = "MAT", Name = "Materials", Category = "Material", IsActive = true, AllowDirectPosting = true };
        db.AddRange(item, baseline, node, cost); await db.SaveChangesAsync();
        db.Add(new ConstructionBudgetLine { CompanyID = 1, ProjectId = project.ID, BudgetVersionId = baseline.ID,
            WbsNodeId = node.ID, CostCodeId = cost.ID, CostCategory = cost.Category, Amount = 100 }); await db.SaveChangesAsync();
        var fresh = await db.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x => x.ID == baseline.ID);
        return new(project.ID, baseline.ID, fresh.RowVersion, vendor.ID, warehouse.ID, item.ID, unit.ID, currency.ID, node.ID, cost.ID);
    }
    internal static Task<ConstructionPurchaseDraftResult> Create(CrossDbContext db, SeedData s, Guid id,
        IReadOnlyList<ConstructionPurchaseDraftLine>? lines = null, bool allow = true) => Service(db, allow).CreateAsync(
            ConstructionR7OrderAllocationTests.Actor(), s.Project, s.Baseline, s.Version, s.Vendor, s.Warehouse,
            DateTime.Today, null, "Persisted proposal", lines ?? new[] { new ConstructionPurchaseDraftLine(s.Item, "Service", 1, s.Unit, 1.2345m, 0, 20) }, id, "Prepare exact source");

    [Fact]
    public async Task Draft_has_real_source_line_ids_currency_precision_and_historical_retry_without_admission()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f.Db); var id = Guid.NewGuid();
        var first = await Create(f.Db, s, id); Assert.True(first.Ok, first.Code); Assert.Equal("DraftCreated", first.Code);
        var retry = await Create(f.Db, s, id); Assert.Equal(first.OrderId, retry.OrderId); Assert.Equal(first.LineIds, retry.LineIds); Assert.Equal("AlreadyCreated", retry.Code);
        await using var verify = f.NewContext(); var order = await verify.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleAsync();
        Assert.Equal("Draft", order.Status); Assert.Equal(1.235m, order.SubTotal); Assert.Equal(.247m, order.TaxTotal); Assert.Equal(1.482m, order.GrandTotal);
        Assert.Equal(s.Currency, order.CurrencyId); Assert.Equal(1m, order.ExchangeRate); Assert.Equal(first.LineIds, order.Lines.OrderBy(x => x.LineNo).Select(x => x.ID));
        Assert.Equal(3, (await verify.Set<ConstructionPurchaseDraft>().AsNoTracking().SingleAsync()).CurrencyDecimals);
        Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
        var allocation = new ConstructionOrderAllocationService(verify, new ConstructionR7OrderAllocationTests.Access(), new ConstructionAuditService(verify));
        var preview = await allocation.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, order.ID);
        Assert.True(preview.Ok, preview.Code); Assert.Equal("DraftProposal", preview.Source!.Classification);
        var saved = await allocation.SaveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, order.ID, preview.Version!, Guid.NewGuid(),
            new[] { new ConstructionCommitmentSplit(order.Lines.Single().ID, s.Node, s.Cost, 1.235m) }, "Allocate persisted source");
        Assert.True(saved.Ok, saved.Code);
    }

    [Theory]
    [InlineData("policy", "PolicyNotConfigured")] [InlineData("currency", "FunctionalCurrencyNotProven")]
    [InlineData("baseline", "ApprovedBaselineRequired")] [InlineData("precision", "DocumentPrecisionNotSupported")]
    [InlineData("supplier", "SupplierNotFound")] [InlineData("unit", "UnitNotFound")]
    public async Task Missing_or_unproven_source_configuration_creates_no_document(string changed, string expected)
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f.Db);
        if (changed == "policy") await f.Db.Set<ConstructionBudgetPolicy>().ExecuteDeleteAsync();
        if (changed == "currency") await f.Db.Companies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DefaultCurrencyId, (int?)null));
        if (changed == "baseline") await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x => x.SetProperty(p => p.IsBaseline, false));
        if (changed == "precision") await f.Db.Currencies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DecimalPlaces, (byte)5));
        if (changed == "supplier") await f.Db.Vendors.ExecuteUpdateAsync(x => x.SetProperty(p => p.CompanyID, 2));
        if (changed == "unit") await f.Db.UnitsOfMeasure.ExecuteUpdateAsync(x => x.SetProperty(p => p.CompanyID, 2));
        var result = await Create(f.Db, s, Guid.NewGuid()); Assert.False(result.Ok); Assert.Equal(expected, result.Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.PurchaseOrders.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseDraft>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Stale_baseline_changed_retry_inputs_and_unrepresentable_lines_never_create_extra_orders()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f.Db); var id = Guid.NewGuid();
        Assert.True((await Create(f.Db, s, id)).Ok);
        Assert.Equal("IdempotencyConflict", (await Create(f.Db, s, id, new[] { new ConstructionPurchaseDraftLine(s.Item, "Changed", 1, s.Unit, 2, 0, 0) })).Code);
        Assert.Equal("InvalidDraftLines", (await Create(f.Db, s, Guid.NewGuid(), new[] { new ConstructionPurchaseDraftLine(s.Item, "Too precise quantity", 1.00001m, s.Unit, 2, 0, 0) })).Code);
        var stale = s with { Version = Guid.NewGuid().ToByteArray() };
        Assert.Equal("ConcurrencyConflict", (await Create(f.Db, stale, Guid.NewGuid())).Code);
        await using var verify = f.NewContext(); Assert.Single(await verify.PurchaseOrders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Equal_amount_replacement_lines_are_not_returned_as_the_original_creation_identity()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f.Db); var id = Guid.NewGuid();
        var first = await Create(f.Db, s, id); Assert.True(first.Ok, first.Code);
        await f.Db.PurchaseOrders.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        Assert.Equal("AlreadyCreated", (await Create(f.Db, s, id)).Code); // lifecycle acknowledgement, not permission to reopen
        var oldLine = await f.Db.PurchaseOrderLines.AsNoTracking().SingleAsync();
        await f.Db.PurchaseOrderLines.ExecuteDeleteAsync();
        f.Db.Add(new PurchaseOrderLine { PurchaseOrderId = oldLine.PurchaseOrderId, LineNo = oldLine.LineNo, ItemId = oldLine.ItemId,
            ItemDescription = oldLine.ItemDescription, Qty = oldLine.Qty, UoMId = oldLine.UoMId, UnitPrice = oldLine.UnitPrice,
            DiscountAmount = oldLine.DiscountAmount, TaxRate = oldLine.TaxRate, LineTotal = oldLine.LineTotal }); await f.Db.SaveChangesAsync();
        var retry = await Create(f.Db, s, id); Assert.False(retry.Ok); Assert.Equal("DraftSourceChanged", retry.Code); Assert.Null(retry.LineIds);
        await using var verify = f.NewContext(); Assert.Single(await verify.PurchaseOrders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Implicit_item_unit_is_persisted_and_a_foreign_base_unit_cannot_enter_the_proposal()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f.Db);
        var lines = new[] { new ConstructionPurchaseDraftLine(s.Item, "Use base unit", 1, null, 1, 0, 0) };
        Assert.True((await Create(f.Db, s, Guid.NewGuid(), lines)).Ok);
        await using (var verify = f.NewContext()) Assert.Equal(s.Unit, (await verify.PurchaseOrderLines.AsNoTracking().SingleAsync()).UoMId);
        await f.Db.UnitsOfMeasure.ExecuteUpdateAsync(x => x.SetProperty(p => p.CompanyID, 2));
        Assert.Equal("UnitNotFound", (await Create(f.Db, s, Guid.NewGuid(), lines)).Code);
    }

    [Fact]
    public async Task Permission_company_write_guard_and_clean_unit_of_work_are_required()
    {
        using var f = new ConstructionTestFixture(); var s = await Seed(f.Db);
        Assert.Equal("AccessDenied", (await Create(f.Db, s, Guid.NewGuid(), allow: false)).Code);
        f.Db.Add(new ConstructionPurchaseDraft { CompanyID = 2 });
        await Assert.ThrowsAsync<CrossBuy.BL.Platform.CompanyWriteDeniedException>(() => f.Db.SaveChangesAsync()); f.Db.ChangeTracker.Clear();
        f.Db.Add(new Vendor { CompanyID = 1, Name = "Unsaved" });
        Assert.Equal("CleanUnitOfWorkRequired", (await Create(f.Db, s, Guid.NewGuid())).Code);
    }
}
