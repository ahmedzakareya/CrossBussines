using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7OrderAllocationTests
{
    internal static BusinessContext Actor(int company = 1) => new() { CompanyId = company, EmployeeId = 7, UserId = "r7-allocation", Source = BusinessContextSource.Test };
    internal sealed class Access(bool allow = true) : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext c, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default) => Task.FromResult(allow && action == ProjectsActions.BudgetManage);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
    internal static async Task<(int Project, int Order, int Line, int Node, int Code)> Seed(CrossDbContext db)
    {
        var currency = new Currency { Code = "TST", Name = "Test", NameEn = "Test", DecimalPlaces = 3 };
        var project = new Project { CompanyID = 1, Code = "ALLOC", Name = "Project", IsActive = true, Status = "Active" };
        db.AddRange(currency, project, new ConstructionBudgetPolicy { CompanyID = 1, Mode = "HardBlock", Reason = "Explicit" },
            new ConstructionStructureSettings { CompanyID = 1, MaxWbsDepth = 8, Reason = "Explicit", UpdatedBy = 7 });
        await db.SaveChangesAsync();
        var node = new WbsNode { CompanyID = 1, ProjectId = project.ID, Code = "A", Name = "Work", Status = "Active", OwnsBudget = true, OwnsCost = true, Depth = 1, Path = "/1/", ProgressMethod = "Quantity", CreatedBy = 7 };
        var code = new ConstructionCostCode { CompanyID = 1, Code = "MAT", Name = "Materials", Category = "Material", IsActive = true, AllowDirectPosting = true };
        var budget = new ConstructionBudgetVersion { CompanyID = 1, ProjectId = project.ID, CurrencyId = currency.ID, VersionNo = 1, Status = "Approved", IsBaseline = true, CreatedBy = 8, ApprovedBy = 9, Reason = "Approved fixture" };
        var order = new PurchaseOrder { CompanyID = 1, ProjectId = project.ID, VendorId = 5, CurrencyId = currency.ID, Status = "Approved", OrderDate = DateTime.Today, SubTotal = 100, TaxTotal = 15, GrandTotal = 115,
            Lines = new List<PurchaseOrderLine> { new() { LineNo = 1, Qty = 2, UnitPrice = 50, LineTotal = 100 } } };
        db.AddRange(node, code, budget, order); await db.SaveChangesAsync();
        db.Add(new ConstructionBudgetLine { CompanyID = 1, ProjectId = project.ID, BudgetVersionId = budget.ID, WbsNodeId = node.ID, CostCodeId = code.ID, CostCategory = "Material", Amount = 1000 });
        await db.SaveChangesAsync();
        return (project.ID, order.ID, order.Lines.Single().ID, node.ID, code.ID);
    }
    internal static ConstructionCommitmentSplit[] Splits((int Project, int Order, int Line, int Node, int Code) ids) => new[] { new ConstructionCommitmentSplit(ids.Line, ids.Node, ids.Code, 100) };
    private static ConstructionOrderAllocationService Service(ConstructionTestFixture f, bool allowed = true) => new(f.Db, new Access(allowed), f.Audit);

    [Fact]
    public async Task Retry_is_one_sealed_revision_even_after_source_closure_and_changed_payload_is_refused()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        var preview = await service.PreviewAsync(Actor(), ids.Project, ids.Order); Assert.True(preview.Ok, preview.Code);
        var request = Guid.NewGuid();
        var saved = await service.SaveAsync(Actor(), ids.Project, ids.Order, preview.Version!, request, Splits(ids), "Map source"); Assert.True(saved.Ok, saved.Code);
        await f.Db.PurchaseOrders.Where(x => x.ID == ids.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Closed"));
        var retry = await service.SaveAsync(Actor(), ids.Project, ids.Order, preview.Version!, request, Splits(ids), "Map source"); Assert.True(retry.Ok, retry.Code); Assert.Equal(saved.Id, retry.Id);
        var conflict = await service.SaveAsync(Actor(), ids.Project, ids.Order, preview.Version!, request, Splits(ids), "Different reason"); Assert.Equal("IdempotencyConflict", conflict.Code);
        await using var verify = f.NewContext(); Assert.True((await verify.Set<ConstructionOrderAllocation>().AsNoTracking().SingleAsync()).IsSealed);
        Assert.Equal(100m, (await verify.Set<ConstructionOrderAllocationLine>().AsNoTracking().SingleAsync()).NetDocumentAmount);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Source_edit_and_competing_revision_refuse_old_tokens_without_overwriting_history()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        var before = await service.PreviewAsync(Actor(), ids.Project, ids.Order);
        await f.Db.PurchaseOrders.Where(x => x.ID == ids.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.OrderNo, "Native edit"));
        Assert.Equal("ConcurrencyConflict", (await service.SaveAsync(Actor(), ids.Project, ids.Order, before.Version!, Guid.NewGuid(), Splits(ids), "Old source")).Code);
        var fresh = await service.PreviewAsync(Actor(), ids.Project, ids.Order);
        Assert.True((await service.SaveAsync(Actor(), ids.Project, ids.Order, fresh.Version!, Guid.NewGuid(), Splits(ids), "First mapping")).Ok);
        Assert.Equal("ConcurrencyConflict", (await service.SaveAsync(Actor(), ids.Project, ids.Order, fresh.Version!, Guid.NewGuid(), Splits(ids), "Stale mapping")).Code);
        var next = await service.PreviewAsync(Actor(), ids.Project, ids.Order);
        Assert.True((await service.SaveAsync(Actor(), ids.Project, ids.Order, next.Version!, Guid.NewGuid(), Splits(ids), "Revised mapping")).Ok);
        await using var verify = f.NewContext(); Assert.Equal(new[] { 1, 2 }, await verify.Set<ConstructionOrderAllocation>().AsNoTracking().OrderBy(x => x.RevisionNo).Select(x => x.RevisionNo).ToArrayAsync());
    }

    [Theory]
    [InlineData("policy", "PolicyNotConfigured")]
    [InlineData("baseline", "ApprovedBaselineRequired")]
    [InlineData("currency", "DocumentCurrencyNotProven")]
    public async Task Missing_prerequisites_never_become_defaults(string missing, string code)
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db);
        if (missing == "policy") await f.Db.Set<ConstructionBudgetPolicy>().ExecuteDeleteAsync();
        if (missing == "baseline") await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x => x.SetProperty(p => p.IsBaseline, false));
        if (missing == "currency") await f.Db.PurchaseOrders.ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrencyId, (int?)null));
        Assert.Equal(code, (await Service(f).PreviewAsync(Actor(), ids.Project, ids.Order)).Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionOrderAllocation>().ToListAsync());
    }

    [Fact]
    public async Task Permission_scope_invalid_dimensions_and_dirty_callers_are_refused_without_writes()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        Assert.Equal("AccessDenied", (await Service(f, false).PreviewAsync(Actor(), ids.Project, ids.Order)).Code);
        Assert.Equal("AccessDenied", (await service.PreviewAsync(Actor(2), ids.Project, ids.Order)).Code);
        Assert.Equal("SourceNotFound", (await service.PreviewAsync(Actor(), ids.Project, ids.Order + 1)).Code);
        var preview = await service.PreviewAsync(Actor(), ids.Project, ids.Order);
        var wrong = new[] { new ConstructionCommitmentSplit(ids.Line, ids.Node + 99, ids.Code, 100) };
        Assert.Equal("DimensionNotEligible", (await service.SaveAsync(Actor(), ids.Project, ids.Order, preview.Version!, Guid.NewGuid(), wrong, "Wrong dimension")).Code);
        var order = await f.Db.PurchaseOrders.SingleAsync(); order.Notes = "Caller pending";
        Assert.Equal("CleanUnitOfWorkRequired", (await service.SaveAsync(Actor(), ids.Project, ids.Order, preview.Version!, Guid.NewGuid(), Splits(ids), "Pending caller")).Code);
        Assert.True(f.Db.ChangeTracker.HasChanges());
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionOrderAllocation>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Company_filters_and_write_guards_cover_both_new_entities()
    {
        using var f = new ConstructionTestFixture();
        f.Db.Add(new ConstructionOrderAllocation { CompanyID = 2, RequestId = Guid.NewGuid() });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.SaveChangesAsync()); f.Db.ChangeTracker.Clear();
        f.Db.Add(new ConstructionOrderAllocationLine { CompanyID = 2 });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.SaveChangesAsync());
        Assert.NotNull(f.Db.Model.FindEntityType(typeof(ConstructionOrderAllocation))!.GetQueryFilter());
        Assert.NotNull(f.Db.Model.FindEntityType(typeof(ConstructionOrderAllocationLine))!.GetQueryFilter());
    }
}
