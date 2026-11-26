using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7RebasedAdmissionReviewTests
{
    private static BusinessContext Reviewer(int employee = 11) => new()
    { CompanyId = 1, EmployeeId = employee, UserId = "reviewer-" + employee, Source = BusinessContextSource.Test };

    private static async Task<(int Project, int Order)> Arrange(ConstructionTestFixture f, bool activate = true)
    {
        var ids = await ConstructionR7ExposureRebaseTests.Prepare(f.Db);
        var reservation = await f.Db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        // The existing rebase fixture intentionally has no company master; supply the
        // explicit functional currency required by the real purchase approval reader.
        f.Db.Add(new CrossBuy.Models.Context.Admin.Companies { CompanyName = "Test",
            Address = "", PhoneNumber = "", Email = "", DefaultCurrencyId = reservation.CurrencyId });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        if (activate)
        {
            var service = new ConstructionReservationRebaseRequestService(f.Db,
                new ConstructionR7ExposureRebaseTests.Access(), f.Audit);
            var result = await service.ActivateExposureAsync(ConstructionR7OrderAllocationTests.Actor(),
                ids.Project, ids.Proposal, ids.Version, Guid.NewGuid(), "Activate for purchase review");
            Assert.True(result.Ok, result.Code);
        }
        return (ids.Project, reservation.PurchaseOrderId);
    }

    [Fact]
    public async Task Review_quotes_existing_exposure_once_and_never_grants_or_writes_admission()
    {
        using var f = new ConstructionTestFixture(); var ids = await Arrange(f);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = Reviewer() };
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor);
        var result = await service.PreviewRebasedAdmissionAsync(ids.Project, ids.Order);
        Assert.True(result.Ok, result.Code); Assert.False(result.GrantsCurrentAdmission);
        Assert.Equal("RebasedAdmissionReviewReady", result.Code); Assert.Equal(64, result.Version!.Length);
        Assert.Equal(60m, result.Commitment);
        Assert.Equal(result.Version, (await service.PreviewRebasedAdmissionAsync(ids.Project, ids.Order)).Version);
        actor.Current = Reviewer(12);
        var other = await service.PreviewRebasedAdmissionAsync(ids.Project, ids.Order);
        Assert.True(other.Ok, other.Code); Assert.NotEqual(result.Version, other.Version);
        await using var verify = f.NewContext();
        var exposure = await new ConstructionBudgetExposureService(verify,
            new ConstructionR7ExposureRebaseTests.Access(), new ConstructionAuditService(verify))
            .PreviewAsync(actor.Current, ids.Project);
        Assert.True(exposure.Ok, exposure.Code); Assert.Equal(60m, Assert.Single(exposure.Rows!).Commitment);
        Assert.Equal(40m, Assert.Single(exposure.Rows!).Remaining);
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.InventoryApprovals.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
        Assert.Equal("Draft", (await verify.PurchaseOrders.AsNoTracking().SingleAsync(x => x.ID == ids.Order)).Status);
    }

    [Theory]
    [InlineData("inventory", "AccessDenied")]
    [InlineData("project", "AccessDenied")]
    [InlineData("company", "AccessDenied")]
    [InlineData("creator", "IndependentApproverRequired")]
    [InlineData("policy", "ConcurrencyConflict")]
    [InlineData("currency", "DraftCurrencyChanged")]
    [InlineData("source", "DraftSourceChanged")]
    public async Task Review_refuses_missing_authority_or_changed_witnesses(string change, string code)
    {
        using var f = new ConstructionTestFixture(); var ids = await Arrange(f);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = Reviewer() };
        if (change == "creator") actor.Current = ConstructionR7OrderAllocationTests.Actor();
        if (change == "company") actor.Current = ConstructionR7OrderAllocationTests.Actor(2);
        if (change == "policy") await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, "Warning"));
        if (change == "currency") await f.Db.Companies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DefaultCurrencyId, (int?)null));
        if (change == "source") await f.Db.PurchaseOrders.Where(x => x.ID == ids.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.VendorId, 99));
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor,
            new ConstructionR7PurchaseApprovalTests.InventoryAuthority { Manage = change != "inventory" }, change != "project");
        var result = await service.PreviewRebasedAdmissionAsync(ids.Project, ids.Order);
        Assert.False(result.Ok); Assert.Equal(code, result.Code); Assert.Null(result.Version); Assert.False(result.GrantsCurrentAdmission);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.InventoryApprovals.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Independent_rebase_decision_without_activated_map_is_insufficient()
    {
        using var f = new ConstructionTestFixture(); var ids = await Arrange(f, false);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = Reviewer() };
        var result = await ConstructionR7PurchaseApprovalTests.Service(f.Db, actor)
            .PreviewRebasedAdmissionAsync(ids.Project, ids.Order);
        Assert.False(result.Ok); Assert.Equal("ActivatedExposureRebaseRequired", result.Code);
        Assert.False(result.GrantsCurrentAdmission); Assert.Null(result.Version);
    }
}
