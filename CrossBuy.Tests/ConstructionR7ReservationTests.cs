using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7ReservationTests
{
    internal sealed record Setup(int Project, int FirstOrder, int SecondOrder, int FirstAllocation, int SecondAllocation, string FirstVersion, string SecondVersion);
    internal static ConstructionCommitmentReservationService Service(CrossDbContext db, bool allow = true) => new(db,
        new ConstructionR7OrderAllocationTests.Access(allow), new ConstructionAuditService(db),
        new ConstructionBudgetPolicyService(db, new ConstructionR7OrderAllocationTests.Access(allow), new ConstructionAuditService(db)));
    internal static async Task<Setup> Arrange(CrossDbContext db, string mode = "HardBlock", bool approvedFirst = false, decimal? exchangeRate = null, string? orderNumber = null, bool withWarehouse = false)
    {
        var ids = await ConstructionR7OrderAllocationTests.Seed(db);
        await db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, mode));
        await db.Set<ConstructionBudgetLine>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Amount, 100m));
        await db.PurchaseOrders.ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, approvedFirst ? "Approved" : "Draft").SetProperty(p => p.SubTotal, 60m).SetProperty(p => p.TaxTotal, 9m).SetProperty(p => p.GrandTotal, 69m));
        await db.PurchaseOrderLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.Qty, 1m).SetProperty(p => p.UnitPrice, 60m).SetProperty(p => p.LineTotal, 60m));
        if (withWarehouse)
        {
            var warehouse = new CrossBuy.Models.Context.Inventory.Warehouse { CompanyID = 1, Code = "R7-REBASE-WH", Name = "Source warehouse", IsActive = true };
            db.Add(warehouse); await db.SaveChangesAsync();
            await db.PurchaseOrders.Where(x => x.ID == ids.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.WarehouseId, warehouse.ID));
        }
        var currency = (await db.PurchaseOrders.AsNoTracking().SingleAsync()).CurrencyId;
        var second = new PurchaseOrder { CompanyID = 1, ProjectId = ids.Project, VendorId = 5, CurrencyId = currency, Status = "Draft",
            OrderDate = DateTime.Today, SubTotal = 60, TaxTotal = 9, GrandTotal = 69,
            Lines = new List<PurchaseOrderLine> { new() { LineNo = 1, Qty = 1, UnitPrice = 60, LineTotal = 60 } } };
        db.Add(second); await db.SaveChangesAsync();
        // Optional explicit rate for source-provenance fixtures; preserve older tests' defaults.
        if (exchangeRate.HasValue)
            await db.PurchaseOrders.Where(x => x.CompanyID == 1 && x.ProjectId == ids.Project)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.ExchangeRate, exchangeRate));
        // Numbered source fixtures must establish identity BEFORE sealing any allocation/reservation.
        if (orderNumber != null)
        {
            await db.PurchaseOrders.Where(x => x.ID == ids.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.OrderNo, orderNumber));
            await db.PurchaseOrders.Where(x => x.ID == second.ID).ExecuteUpdateAsync(x => x.SetProperty(p => p.OrderNo, orderNumber + "-2"));
        }
        var allocations = new ConstructionOrderAllocationService(db, new ConstructionR7OrderAllocationTests.Access(), new ConstructionAuditService(db));
        async Task<(int Id, string Version)> Map(int order, int line)
        {
            var preview = await allocations.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, order); Assert.True(preview.Ok, preview.Code);
            var saved = await allocations.SaveAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, order, preview.Version!, Guid.NewGuid(),
                new[] { new ConstructionCommitmentSplit(line, ids.Node, ids.Code, 60) }, "Allocate proposal"); Assert.True(saved.Ok, saved.Code);
            return (saved.Id!.Value, preview.Version!);
        }
        var firstMap = await Map(ids.Order, ids.Line); var secondMap = await Map(second.ID, second.Lines.Single().ID);
        return new(ids.Project, ids.Order, second.ID, firstMap.Id, secondMap.Id, firstMap.Version, secondMap.Version);
    }

    [Theory]
    [InlineData("HardBlock", false, false, false)]
    [InlineData("ApprovalRequired", false, false, true)]
    [InlineData("Warning", true, true, false)]
    public async Task Explicit_company_policy_controls_second_proposal_from_persisted_dimensions(string mode, bool allowed, bool warning, bool approval)
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db, mode); var service = Service(f.Db);
        var first = await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "First proposal"); Assert.True(first.Ok, first.Code);
        var second = await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.SecondAllocation, s.SecondVersion, Guid.NewGuid(), "Second proposal");
        Assert.Equal(allowed, second.Ok); Assert.Equal(warning, second.Warning); Assert.Equal(approval, second.RequiresApproval);
        await using var verify = f.NewContext(); Assert.Equal(allowed ? 2 : 1, await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().CountAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Existing_approved_orders_consume_budget_even_without_a_new_reservation()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db, approvedFirst: true);
        var result = await Service(f.Db).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.SecondAllocation, s.SecondVersion, Guid.NewGuid(), "Respect legacy commitment");
        Assert.Equal("BudgetHardBlock", result.Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancelled_status_does_not_free_money_until_verified_release_and_retry_never_duplicates_history()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var service = Service(f.Db); var request = Guid.NewGuid();
        var first = await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, request, "Reserve"); Assert.True(first.Ok, first.Code);
        await f.Db.PurchaseOrders.Where(x => x.ID == s.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        Assert.Equal("BudgetHardBlock", (await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.SecondAllocation, s.SecondVersion, Guid.NewGuid(), "Before release")).Code);
        var releaseRequest = Guid.NewGuid(); var released = await service.ReleaseCancelledAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, first.Id!.Value, releaseRequest, "Verified cancellation"); Assert.True(released.Ok, released.Code);
        Assert.Equal(released.Id, (await service.ReleaseCancelledAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, first.Id.Value, releaseRequest, "Verified cancellation")).Id);
        var historical = await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, request, "Reserve");
        Assert.Equal(first.Id, historical.Id); Assert.False(historical.GrantsCurrentAdmission);
        Assert.True((await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.SecondAllocation, s.SecondVersion, Guid.NewGuid(), "After verified release")).Ok);
        await using var verify = f.NewContext(); Assert.Equal(2, await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().CountAsync()); Assert.Single(await verify.Set<ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("invoice", "RealizationCoverageRequired")]
    [InlineData("subcontract", "SubcontractCoverageRequired")]
    [InlineData("policy", "PolicyNotConfigured")]
    [InlineData("source", "AllocationSourceChanged")]
    public async Task Unproven_sources_and_missing_policy_are_not_coerced_to_zero(string change, string expected)
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db);
        if (change == "invoice") { f.Db.PurchaseInvoices.Add(new() { CompanyID = 1, ProjectId = s.Project, VendorId = 5, Status = "Posted" }); await f.Db.SaveChangesAsync(); }
        if (change == "subcontract") { f.Db.Subcontracts.Add(new() { CompanyID = 1, ProjectId = s.Project, VendorId = 5, Status = "Active", ContractValue = null }); await f.Db.SaveChangesAsync(); }
        if (change == "policy") await f.Db.Set<ConstructionBudgetPolicy>().ExecuteDeleteAsync();
        if (change == "source") await f.Db.PurchaseOrders.Where(x => x.ID == s.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.OrderNo, "Changed after allocation"));
        Assert.Equal(expected, (await Service(f.Db).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "No guessed coverage")).Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Approved_unallocated_source_is_unknown_exposure_not_zero()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db);
        var currency = (await f.Db.PurchaseOrders.AsNoTracking().FirstAsync()).CurrencyId;
        f.Db.Add(new PurchaseOrder { CompanyID = 1, ProjectId = s.Project, VendorId = 5, CurrencyId = currency, Status = "Approved", SubTotal = 200, GrandTotal = 200 });
        await f.Db.SaveChangesAsync();
        Assert.Equal("AllocationCoverageRequired", (await Service(f.Db).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "Uncovered old order")).Code);
    }

    [Fact]
    public async Task Receipt_history_prevents_cancellation_release()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var service = Service(f.Db);
        var saved = await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "Reserve"); Assert.True(saved.Ok, saved.Code);
        await f.Db.PurchaseOrders.Where(x => x.ID == s.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        await f.Db.PurchaseOrderLines.Where(x => x.PurchaseOrderId == s.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReceivedQty, 1m));
        Assert.Equal("CancellationCoverageRequired", (await service.ReleaseCancelledAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, saved.Id!.Value, Guid.NewGuid(), "Cannot erase receipt history")).Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task New_entities_refuse_foreign_company_writes_and_dirty_callers_keep_their_pending_changes()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db);
        f.Db.Add(new ConstructionCommitmentReservation { CompanyID = 2 });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.SaveChangesAsync()); f.Db.ChangeTracker.Clear();
        f.Db.Add(new ConstructionCommitmentRelease { CompanyID = 2 });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Db.SaveChangesAsync()); f.Db.ChangeTracker.Clear();
        var project = await f.Db.Projects.SingleAsync(); project.Name = "Pending caller edit";
        Assert.Equal("CleanUnitOfWorkRequired", (await Service(f.Db).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "Do not flush caller")).Code);
        Assert.True(f.Db.ChangeTracker.HasChanges());
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.NotEqual("Pending caller edit", (await verify.Projects.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task Foreign_company_permission_denial_and_changed_retry_payload_cannot_reserve()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var service = Service(f.Db); var request = Guid.NewGuid();
        Assert.Equal("AccessDenied", (await Service(f.Db, false).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, request, "Denied")).Code);
        Assert.Equal("AccessDenied", (await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(2), s.Project, s.FirstAllocation, s.FirstVersion, request, "Foreign")).Code);
        Assert.True((await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, request, "Original")).Ok);
        Assert.Equal("IdempotencyConflict", (await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, request, "Different")).Code);
        Assert.Equal("CancellationRequired", (await service.ReleaseCancelledAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, 1, Guid.NewGuid(), "Not cancelled")).Code);
    }
}
