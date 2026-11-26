using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7RebasedPurchaseRequestTests
{
    internal static async Task<(int Project, int Order)> Prepare(CrossDbContext db, bool withWarehouse = false)
    {
        var ids = await ConstructionR7ExposureRebaseTests.Prepare(db, withWarehouse: withWarehouse);
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        db.Add(new CrossBuy.Models.Context.Admin.Companies { CompanyName = "Test",
            Address = "", PhoneNumber = "", Email = "", DefaultCurrencyId = reservation.CurrencyId });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var activate = await new ConstructionReservationRebaseRequestService(db,
            new ConstructionR7ExposureRebaseTests.Access(), new ConstructionAuditService(db)).ActivateExposureAsync(
                ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Proposal, ids.Version, Guid.NewGuid(), "Activate request fixture");
        Assert.True(activate.Ok, activate.Code);
        return (ids.Project, reservation.PurchaseOrderId);
    }

    [Fact]
    public async Task Request_is_canonical_atomic_idempotent_and_never_an_admission()
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor);
        var quote = await service.PreviewRebasedRequestAsync(ids.Project, ids.Order); Assert.True(quote.Ok, quote.Code);
        Assert.False(quote.GrantsCurrentAdmission);
        var requestId = Guid.NewGuid();
        var result = await service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, requestId, "Rebase purchase review");
        Assert.True(result.Ok, result.Code);
        Assert.Equal(result.ApprovalId, (await service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, requestId, "Rebase purchase review")).ApprovalId);
        Assert.Equal("IdempotencyConflict", (await service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, requestId, "Changed reason")).Code);
        Assert.Equal("ApprovalAlreadyPending", (await service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, Guid.NewGuid(), "Another request")).Code);
        actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        await using (var verify = f.NewContext())
        {
            var bridge = Assert.Single(await verify.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking().ToListAsync());
            var canonical = Assert.Single(await verify.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().ToListAsync());
            Assert.Equal(canonical.ID, bridge.ApprovalRequestId); Assert.Equal(quote.Version, bridge.ReviewVersion);
            Assert.Equal("Pending", (await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
            Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
            Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
            Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
            Assert.Equal("Draft", (await verify.PurchaseOrders.AsNoTracking().SingleAsync(x => x.ID == ids.Order)).Status);
        }
        Assert.True((await service.RejectAsync(result.ApprovalId!.Value, "Reject pending rebased request")).Ok);
        await using var rejected = f.NewContext();
        Assert.Equal("Rejected", (await rejected.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await rejected.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("actor")]
    [InlineData("source")]
    public async Task Stale_or_other_actor_quote_creates_no_canonical_request(string change)
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor);
        var quote = await service.PreviewRebasedRequestAsync(ids.Project, ids.Order); Assert.True(quote.Ok, quote.Code);
        if (change == "policy") await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, "Warning"));
        if (change == "actor") actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        if (change == "source") await f.Db.PurchaseOrders.Where(x => x.ID == ids.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.VendorId, 99));
        var result = await service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, Guid.NewGuid(), "Stale request");
        Assert.False(result.Ok);
        Assert.Equal(change == "source" ? "DraftSourceChanged" : "ConcurrencyConflict", result.Code);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.InventoryApprovals.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking().ToListAsync());
    }
    [Fact]
    public async Task Late_bridge_write_failure_rolls_back_the_canonical_pending_request()
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor);
        var quote = await service.PreviewRebasedRequestAsync(ids.Project, ids.Order); Assert.True(quote.Ok, quote.Code);
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectRebaseBridge BEFORE INSERT ON ConstructionPurchaseRebaseRequests BEGIN SELECT RAISE(ABORT, 'deliberate late bridge failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => service.RequestRebasedAsync(ids.Project, ids.Order,
            quote.Version!, Guid.NewGuid(), "Must roll back"));
        await using var verify = f.NewContext();
        Assert.Empty(await verify.InventoryApprovals.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("purchase")]
    [InlineData("project")]
    [InlineData("company")]
    public async Task Request_requires_purchasing_authority_and_exact_project_company(string denied)
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        if (denied == "company") actor.Current = ConstructionR7OrderAllocationTests.Actor(2);
        var inventory = new ConstructionR7PurchaseApprovalTests.InventoryAuthority { Purchase = denied != "purchase" };
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor, inventory, denied != "project");
        var result = await service.RequestRebasedAsync(ids.Project, ids.Order, new string('A', 64), Guid.NewGuid(), "Denied request");
        Assert.False(result.Ok); Assert.Equal("AccessDenied", result.Code);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.InventoryApprovals.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking().ToListAsync());
    }

}
