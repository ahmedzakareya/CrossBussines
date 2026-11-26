using CrossBuy.BL.Construction;
using CrossBuy.BL;
using CrossBuy.Models.Platform;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7RebaseRequestTests
{
    private sealed class Access : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext c, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
            => Task.FromResult(action is ProjectsActions.BudgetManage or ProjectsActions.BudgetView);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext c, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext c, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
    private static ConstructionReservationRebaseRequestService Service(ConstructionTestFixture f) =>
        new(f.Db, new ConstructionR7OrderAllocationTests.Access(), f.Audit);

    internal static async Task<(int Project, int Reservation, int Target)> Seed(CrossBuy.Models.Context.CrossDbContext db, decimal? targetBudgetAmount = null, bool withWarehouse = false)
    {
        var setup = await ConstructionR7ReservationTests.Arrange(db, exchangeRate: 1m, orderNumber: "ZZ-R7-RB-" + Guid.NewGuid().ToString("N")[..12], withWarehouse: withWarehouse);
        var admitted = await ConstructionR7ReservationTests.Service(db).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(),
            setup.Project, setup.FirstAllocation, setup.FirstVersion, Guid.NewGuid(), "Original admission");
        Assert.True(admitted.Ok, admitted.Code);
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        var old = await db.Set<ConstructionBudgetVersion>().SingleAsync(x => x.ID == reservation.BudgetVersionId);
        old.Status = "Superseded"; old.IsBaseline = false;
        var target = new ConstructionBudgetVersion { CompanyID = 1, ProjectId = setup.Project, CurrencyId = old.CurrencyId,
            PreviousVersionId = old.ID, VersionNo = 2, Kind = "Revised", Status = "Approved", IsBaseline = true,
            CreatedBy = 7, ApprovedBy = 9, ApprovedAt = DateTime.UtcNow, Reason = "Approved revision" };
        db.Add(target); await db.SaveChangesAsync();
        var oldLines = await db.Set<ConstructionBudgetLine>().AsNoTracking().Where(x => x.BudgetVersionId == old.ID).ToListAsync();
        db.AddRange(oldLines.Select(x => new ConstructionBudgetLine { CompanyID = 1, ProjectId = setup.Project,
            BudgetVersionId = target.ID, WbsNodeId = x.WbsNodeId, CostCodeId = x.CostCodeId, CostCategory = x.CostCategory,
            Amount = targetBudgetAmount ?? x.Amount, ChangeReason = "Preserve dimensions" }));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        return (setup.Project, reservation.ID, target.ID);
    }

    [Fact]
    public async Task Request_is_durable_idempotent_and_does_not_replace_original_admission_or_enable_exposure()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var before = await f.Db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        var preview = await service.PreviewAsync(actor, ids.Project, ids.Reservation, ids.Target); Assert.True(preview.Ok, preview.Code);
        var request = Guid.NewGuid();
        var saved = await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, request, "Review rebase");
        Assert.True(saved.Ok, saved.Code); Assert.Equal("RebaseRequested", saved.Code);
        Assert.Equal(saved.Id, (await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, request, "Review rebase")).Id);
        Assert.Equal("IdempotencyConflict", (await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, request, "Different reason")).Code);
        await using var verify = f.NewContext();
        var stored = await verify.Set<ConstructionReservationRebaseRequest>().AsNoTracking().SingleAsync();
        Assert.Equal(7, stored.CreatedBy); Assert.Equal(preview.Version, stored.SourceVersion);
        var original = await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        Assert.Equal(before.BudgetVersionId, original.BudgetVersionId); Assert.Equal(before.SourceVersion, original.SourceVersion);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
        var exposure = await new ConstructionBudgetExposureService(verify, new Access(), new ConstructionAuditService(verify)).PreviewAsync(actor, ids.Project);
        Assert.False(exposure.Ok); // A proposal must never cure an incompatible admitted source.
        Assert.NotEqual("AccessDenied", exposure.Code);
    }

    [Fact]
    public async Task Changed_target_lines_invalidate_the_proposal_before_any_history_is_written()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var preview = await service.PreviewAsync(actor, ids.Project, ids.Reservation, ids.Target); Assert.True(preview.Ok, preview.Code);
        await f.Db.Set<ConstructionBudgetLine>().Where(x => x.BudgetVersionId == ids.Target).ExecuteUpdateAsync(x => x.SetProperty(p => p.Amount, p => p.Amount + 1));
        var result = await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, Guid.NewGuid(), "Stale review");
        Assert.Equal("ConcurrencyConflict", result.Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionReservationRebaseRequest>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Independent_review_is_once_only_and_never_changes_the_original_baseline_reference()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        var requester = ConstructionR7OrderAllocationTests.Actor();
        var reviewer = new BusinessContext { CompanyId = 1, EmployeeId = 9, UserId = "reviewer", Source = BusinessContextSource.Test };
        var preview = await service.PreviewAsync(requester, ids.Project, ids.Reservation, ids.Target); Assert.True(preview.Ok, preview.Code);
        var proposal = await service.RequestAsync(requester, ids.Project, ids.Reservation, ids.Target, preview.Version!, Guid.NewGuid(), "Request"); Assert.True(proposal.Ok, proposal.Code);
        Assert.Equal("SeparateApproverRequired", (await service.DecideAsync(requester, ids.Project, proposal.Id!.Value, preview.Version!, true, Guid.NewGuid(), "Self review")).Code);
        var request = Guid.NewGuid();
        var decision = await service.DecideAsync(reviewer, ids.Project, proposal.Id.Value, preview.Version!, true, request, "Independent review"); Assert.True(decision.Ok, decision.Code);
        Assert.Equal(decision.Id, (await service.DecideAsync(reviewer, ids.Project, proposal.Id.Value, preview.Version!, true, request, "Independent review")).Id);
        Assert.Equal("IdempotencyConflict", (await service.DecideAsync(reviewer, ids.Project, proposal.Id.Value, preview.Version!, false, request, "Independent review")).Code);
        Assert.Equal("RebaseAlreadyDecided", (await service.DecideAsync(reviewer, ids.Project, proposal.Id.Value, preview.Version!, false, Guid.NewGuid(), "Late rejection")).Code);
        await using var verify = f.NewContext();
        Assert.Equal("Approved", (await verify.Set<ConstructionReservationRebaseDecision>().AsNoTracking().SingleAsync()).Outcome);
        Assert.NotEqual(ids.Target, (await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync()).BudgetVersionId);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Stale_proposal_cannot_be_approved_but_can_be_explicitly_rejected()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var reviewer = new BusinessContext { CompanyId = 1, EmployeeId = 9, UserId = "reviewer", Source = BusinessContextSource.Test };
        var preview = await service.PreviewAsync(actor, ids.Project, ids.Reservation, ids.Target); Assert.True(preview.Ok, preview.Code);
        var proposal = await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, Guid.NewGuid(), "Request"); Assert.True(proposal.Ok, proposal.Code);
        await f.Db.Set<ConstructionBudgetLine>().Where(x => x.BudgetVersionId == ids.Target).ExecuteUpdateAsync(x => x.SetProperty(p => p.Amount, p => p.Amount + 1));
        Assert.Equal("ConcurrencyConflict", (await service.DecideAsync(reviewer, ids.Project, proposal.Id!.Value, preview.Version!, true, Guid.NewGuid(), "Stale approval")).Code);
        await using (var verify = f.NewContext()) Assert.Empty(await verify.Set<ConstructionReservationRebaseDecision>().AsNoTracking().ToListAsync());
        Assert.True((await service.DecideAsync(reviewer, ids.Project, proposal.Id.Value, preview.Version!, false, Guid.NewGuid(), "Reject stale proposal")).Ok);
        await using var final = f.NewContext(); Assert.Equal("Rejected", (await final.Set<ConstructionReservationRebaseDecision>().AsNoTracking().SingleAsync()).Outcome);
    }

    [Fact]
    public async Task Source_readiness_requires_independent_approval_and_preserved_creation_evidence()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var preview = await service.PreviewAsync(actor, ids.Project, ids.Reservation, ids.Target); Assert.True(preview.Ok, preview.Code);
        var proposal = await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, Guid.NewGuid(), "Request"); Assert.True(proposal.Ok, proposal.Code);
        Assert.Equal("ApprovedRebaseProposalRequired", (await service.ReadSourceReadinessAsync(actor, ids.Project, proposal.Id!.Value)).Code);
        var reviewer = new BusinessContext { CompanyId = 1, EmployeeId = 9, UserId = "reviewer", Source = BusinessContextSource.Test };
        Assert.True((await service.DecideAsync(reviewer, ids.Project, proposal.Id.Value, preview.Version!, true, Guid.NewGuid(), "Review")).Ok);
        Assert.Equal("DraftSourceUnproven", (await service.ReadSourceReadinessAsync(actor, ids.Project, proposal.Id.Value)).Code);
        await AttachCreationWitness(f.Db, ids.Reservation);
        var ready = await service.ReadSourceReadinessAsync(actor, ids.Project, proposal.Id.Value);
        Assert.True(ready.Ok, ready.Code); Assert.Equal("RebaseSourceReady", ready.Code); Assert.Equal(64, ready.Version!.Length);
        await using var verify = f.NewContext();
        Assert.NotEqual(ids.Target, (await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync()).BudgetVersionId);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    internal static async Task AttachCreationWitness(CrossBuy.Models.Context.CrossDbContext db, int reservationId)
    {
        var reservation = await db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync(x => x.ID == reservationId);
        var order = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.ID == reservation.PurchaseOrderId);
        // Deliberate fixture evidence; this does not claim execution of the purchase-creation writer.
        db.Add(new ConstructionPurchaseDraft { CompanyID = 1, ProjectId = reservation.ProjectId, PurchaseOrderId = order.ID,
            BudgetVersionId = reservation.BudgetVersionId, CurrencyId = reservation.CurrencyId, CurrencyDecimals = 3,
            RequestId = Guid.NewGuid(), RequestHash = new string('A', 64), InitialSourceVersion = (string)typeof(ConstructionPurchaseDraftService).GetMethod("CreationHash", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object?[] { order, null })!,
            Reason = "Fixture creation witness", CreatedBy = 7, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    }

    [Theory]
    [InlineData("header", "DraftSourceChanged")]
    [InlineData("quantity", "DraftSourceChanged")]
    [InlineData("received", "RebaseRealizedSourceUnsupported")]
    [InlineData("approved", "RebaseRealizedSourceUnsupported")]
    [InlineData("dimension", "InvalidBudgetDimension")]
    [InlineData("precision", "BudgetCurrencyMismatch")]
    public async Task Source_readiness_refuses_changed_or_unsupported_financial_sources(string change, string expected)
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        await AttachCreationWitness(f.Db, ids.Reservation);
        var preview = await service.PreviewAsync(actor, ids.Project, ids.Reservation, ids.Target); Assert.True(preview.Ok, preview.Code);
        var proposal = await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, Guid.NewGuid(), "Request"); Assert.True(proposal.Ok, proposal.Code);
        var reviewer = new BusinessContext { CompanyId = 1, EmployeeId = 9, UserId = "reviewer", Source = BusinessContextSource.Test };
        Assert.True((await service.DecideAsync(reviewer, ids.Project, proposal.Id!.Value, preview.Version!, true, Guid.NewGuid(), "Review")).Ok);
        var reservation = await f.Db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        var orderId = reservation.PurchaseOrderId;
        if (change == "header") await f.Db.PurchaseOrders.Where(x => x.ID == orderId).ExecuteUpdateAsync(x => x.SetProperty(p => p.VendorId, 99));
        if (change == "quantity") await f.Db.PurchaseOrderLines.Where(x => x.PurchaseOrderId == orderId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Qty, 2));
        if (change == "received") await f.Db.PurchaseOrderLines.Where(x => x.PurchaseOrderId == orderId).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReceivedQty, 1));
        if (change == "approved") await f.Db.PurchaseOrders.Where(x => x.ID == orderId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Approved"));
        if (change == "dimension") await f.Db.Set<WbsNode>().ExecuteUpdateAsync(x => x.SetProperty(p => p.OwnsCost, false));
        if (change == "precision") await f.Db.Set<ConstructionPurchaseDraft>().ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrencyDecimals, 2));
        Assert.Equal(expected, (await service.ReadSourceReadinessAsync(actor, ids.Project, proposal.Id.Value)).Code);
        await using var verify = f.NewContext();
        Assert.NotEqual(ids.Target, (await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync()).BudgetVersionId);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Foreign_scope_missing_policy_and_released_reservation_cannot_create_requests()
    {
        using var f = new ConstructionTestFixture(); var ids = await Seed(f.Db); var service = Service(f);
        Assert.False((await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(2), ids.Project, ids.Reservation, ids.Target)).Ok);
        await f.Db.Set<ConstructionBudgetPolicy>().ExecuteDeleteAsync();
        Assert.Equal("PolicyNotConfigured", (await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Reservation, ids.Target)).Code);
        f.Db.Add(new ConstructionCommitmentRelease { CompanyID = 1, ProjectId = ids.Project, ReservationId = ids.Reservation,
            RequestId = Guid.NewGuid(), RequestHash = new string('A', 64), Reason = "Cancelled", CreatedBy = 7 });
        await f.Db.SaveChangesAsync();
        Assert.Equal("ReservationReleased", (await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Reservation, ids.Target)).Code);
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionReservationRebaseRequest>().AsNoTracking().ToListAsync());
    }
}



