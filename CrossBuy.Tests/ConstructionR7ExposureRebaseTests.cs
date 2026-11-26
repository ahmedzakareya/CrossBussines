using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7ExposureRebaseTests
{
    internal sealed class Access : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext c, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
            => Task.FromResult(action is ProjectsActions.BudgetView or ProjectsActions.BudgetManage);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }

    internal static async Task<(int Project, int Reservation, int Target, int Proposal, string Version)> Prepare(CrossDbContext db,
        decimal? targetBudgetAmount = null, string mode = "HardBlock", bool withWarehouse = false)
    {
        var ids = await ConstructionR7RebaseRequestTests.Seed(db, targetBudgetAmount, withWarehouse);
        await ConstructionR7RebaseRequestTests.AttachCreationWitness(db, ids.Reservation);
        if (mode != "HardBlock") await db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, mode));
        var service = new ConstructionReservationRebaseRequestService(db, new Access(), new ConstructionAuditService(db));
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var preview = await service.PreviewAsync(actor, ids.Project, ids.Reservation, ids.Target); Assert.True(preview.Ok, preview.Code);
        var proposal = await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, Guid.NewGuid(), "Request exposure rebase"); Assert.True(proposal.Ok, proposal.Code);
        var reviewer = new BusinessContext { CompanyId = 1, EmployeeId = 9, UserId = "reviewer", Source = BusinessContextSource.Test };
        Assert.True((await service.DecideAsync(reviewer, ids.Project, proposal.Id!.Value, preview.Version!, true, Guid.NewGuid(), "Review exposure rebase")).Ok);
        var ready = await service.ReadSourceReadinessAsync(actor, ids.Project, proposal.Id.Value); Assert.True(ready.Ok, ready.Code);
        return (ids.Project, ids.Reservation, ids.Target, proposal.Id.Value, ready.Version!);
    }

    [Fact]
    public async Task Effective_exposure_counts_original_commitment_once_without_rewriting_admission()
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var before = await f.Db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        var service = new ConstructionReservationRebaseRequestService(f.Db, new Access(), f.Audit);
        var actor = ConstructionR7OrderAllocationTests.Actor(); var request = Guid.NewGuid();
        var result = await service.ActivateExposureAsync(actor, ids.Project, ids.Proposal, ids.Version, request, "Activate exposure map"); Assert.True(result.Ok, result.Code);
        Assert.Equal(result.Id, (await service.ActivateExposureAsync(actor, ids.Project, ids.Proposal, ids.Version, request, "Activate exposure map")).Id);
        Assert.Equal("IdempotencyConflict", (await service.ActivateExposureAsync(actor, ids.Project, ids.Proposal, ids.Version, request, "Changed reason")).Code);
        Assert.Equal("RebaseAlreadyActivated", (await service.ActivateExposureAsync(actor, ids.Project, ids.Proposal, ids.Version, Guid.NewGuid(), "Second activation")).Code);
        await using var verify = f.NewContext();
        var exposure = await new ConstructionBudgetExposureService(verify, new Access(), new ConstructionAuditService(verify)).PreviewAsync(actor, ids.Project);
        Assert.True(exposure.Ok, exposure.Code); Assert.Equal(ids.Target, exposure.BudgetVersionId);
        var row = Assert.Single(exposure.Rows!); Assert.Equal(100, row.Budget); Assert.Equal(60, row.Commitment); Assert.Equal(0, row.Actual); Assert.Equal(40, row.Remaining);
        var original = await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        Assert.Equal(before.BudgetVersionId, original.BudgetVersionId); Assert.Equal(before.SourceVersion, original.SourceVersion);
        Assert.Single(await verify.Set<ConstructionReservationExposureRebase>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
        var replay = await ConstructionR7ReservationTests.Service(verify).ReserveAsync(actor, ids.Project, original.AllocationId,
            original.SourceVersion, original.RequestId, "Original admission");
        Assert.Equal("AlreadyReserved", replay.Code); Assert.False(replay.GrantsCurrentAdmission);
    }

    [Theory]
    [InlineData("HardBlock")]
    [InlineData("Warning")]
    [InlineData("ApprovalRequired")]
    public async Task Proposal_approval_never_substitutes_for_an_overrun_exception(string mode)
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db, 25, mode);
        var service = new ConstructionReservationRebaseRequestService(f.Db, new Access(), f.Audit);
        var result = await service.ActivateExposureAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Proposal, ids.Version, Guid.NewGuid(), "Must not bypass overrun");
        Assert.Equal("RebaseOverrunRequiresReview", result.Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionReservationExposureRebase>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Changed_source_after_activation_refuses_amounts_instead_of_renewing_admission()
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var service = new ConstructionReservationRebaseRequestService(f.Db, new Access(), f.Audit);
        Assert.True((await service.ActivateExposureAsync(actor, ids.Project, ids.Proposal, ids.Version, Guid.NewGuid(), "Activate")).Ok);
        var order = (await f.Db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync()).PurchaseOrderId;
        await f.Db.PurchaseOrders.Where(x => x.ID == order).ExecuteUpdateAsync(x => x.SetProperty(p => p.VendorId, 99));
        await using var verify = f.NewContext();
        var exposure = await new ConstructionBudgetExposureService(verify, new Access(), new ConstructionAuditService(verify)).PreviewAsync(actor, ids.Project);
        Assert.False(exposure.Ok); Assert.Equal("DraftSourceChanged", exposure.Code); Assert.Null(exposure.Rows);
        Assert.Single(await verify.Set<ConstructionReservationExposureRebase>().AsNoTracking().ToListAsync());
    }
    [Theory]
    [InlineData("policy", "ConcurrencyConflict")]
    [InlineData("baseline", "ApprovedBaselineRequired")]
    [InlineData("approved-source", "RebasedAdmissionSourceNotProven")]
    public async Task Activated_map_never_survives_changed_admission_preconditions(string change, string expectedCode)
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var service = new ConstructionReservationRebaseRequestService(f.Db, new Access(), f.Audit);
        Assert.True((await service.ActivateExposureAsync(actor, ids.Project, ids.Proposal, ids.Version,
            Guid.NewGuid(), "Activate before admission precondition changes")).Ok);
        if (change == "policy")
            await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, "Warning"));
        else if (change == "baseline")
            await f.Db.Set<ConstructionBudgetVersion>().Where(x => x.ID == ids.Target)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Superseded").SetProperty(p => p.IsBaseline, false));
        else
        {
            var reservation = await f.Db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
            await f.Db.PurchaseOrders.Where(x => x.ID == reservation.PurchaseOrderId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Approved"));
        }
        await using var verify = f.NewContext();
        var exposure = await new ConstructionBudgetExposureService(verify, new Access(), new ConstructionAuditService(verify))
            .PreviewAsync(actor, ids.Project);
        Assert.False(exposure.Ok); Assert.Equal(expectedCode, exposure.Code); Assert.Null(exposure.Rows);
        Assert.Single(await verify.Set<ConstructionReservationExposureRebase>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

}
