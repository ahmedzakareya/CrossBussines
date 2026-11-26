using System.Data;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7RebasedPurchaseDecisionTests
{
    internal static BusinessContext Actor(int employee) => new()
    { CompanyId = 1, EmployeeId = employee, UserId = "rebased-review-" + employee, Source = BusinessContextSource.Test };

    internal static async Task<(int Project, int Order, int Approval)> Prepare(CrossDbContext db)
    {
        var ids = await ConstructionR7RebasedPurchaseRequestTests.Prepare(db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = Actor(9) };
        var service = ConstructionR7PurchaseApprovalTests.Service(db, actor);
        var quote = await service.PreviewRebasedRequestAsync(ids.Project, ids.Order); Assert.True(quote.Ok, quote.Code);
        var request = await service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, Guid.NewGuid(), "Exact rebased request");
        Assert.True(request.Ok, request.Code);
        return (ids.Project, ids.Order, request.ApprovalId!.Value);
    }

    [Fact]
    public async Task Independent_decision_preserves_original_reservation_and_counts_approved_exposure_once()
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var original = await f.Db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = Actor(8) };
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor); var id = Guid.NewGuid();
        var result = await service.ApproveAsync(ids.Approval, id, "Independent rebased admission"); Assert.True(result.Ok, result.Code);
        Assert.Equal("AlreadyApproved", (await service.ApproveAsync(ids.Approval, id, "Independent rebased admission")).Code);
        Assert.Equal("IdempotencyConflict", (await service.ApproveAsync(ids.Approval, id, "Changed reason")).Code);
        Assert.Equal("ApprovalAlreadyDecided", (await service.RejectAsync(ids.Approval, "Too late")).Code);
        await using var verify = f.NewContext();
        var decision = Assert.Single(await verify.Set<ConstructionPurchaseRebaseDecision>().AsNoTracking().ToListAsync());
        var admission = Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        var reservation = Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Equal(original.BudgetVersionId, reservation.BudgetVersionId); Assert.Equal(original.SourceVersion, reservation.SourceVersion);
        Assert.Equal(reservation.ID, decision.ReservationId); Assert.Equal(reservation.ID, admission.ReservationId);
        Assert.Equal(id, decision.RequestId); Assert.Equal(id, admission.RequestId); Assert.Equal(8, decision.CreatedBy);
        Assert.Equal("Approved", (await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("Approved", (await verify.PurchaseOrders.AsNoTracking().SingleAsync(x => x.ID == ids.Order)).Status);
        var exposure = await new ConstructionBudgetExposureService(verify, new ConstructionR7ExposureRebaseTests.Access(),
            new ConstructionAuditService(verify)).PreviewAsync(actor.Current, ids.Project);
        Assert.True(exposure.Ok, exposure.Code); var row = Assert.Single(exposure.Rows!);
        Assert.Equal(60, row.Commitment); Assert.Equal(0, row.Actual); Assert.Equal(40, row.Remaining);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("draft-creator", "IndependentApproverRequired")]
    [InlineData("requester", "IndependentApproverRequired")]
    [InlineData("policy", "ConcurrencyConflict")]
    [InlineData("source", "DraftSourceChanged")]
    [InlineData("currency", "DraftCurrencyChanged")]
    [InlineData("budget", "ConcurrencyConflict")]
    [InlineData("inventory", "AccessDenied")]
    [InlineData("project", "AccessDenied")]
    public async Task Decision_revalidates_independence_authority_and_pending_financial_witness(string change, string code)
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = Actor(change == "draft-creator" ? 7 : change == "requester" ? 9 : 8) };
        if (change == "policy") await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, "Warning"));
        if (change == "source") await f.Db.PurchaseOrders.Where(x => x.ID == ids.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.VendorId, 99));
        if (change == "currency") await f.Db.Companies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DefaultCurrencyId, (int?)null));
        if (change == "budget")
        {
            var target = await f.Db.Set<ConstructionReservationExposureRebase>().Select(m => m.TargetBudgetVersionId).SingleAsync();
            await f.Db.Set<ConstructionBudgetLine>().Where(x => x.BudgetVersionId == target)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Amount, p => p.Amount + 1));
        }
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor,
            new ConstructionR7PurchaseApprovalTests.InventoryAuthority { Manage = change != "inventory" }, change != "project");
        var result = await service.ApproveAsync(ids.Approval, Guid.NewGuid(), "Must revalidate");
        Assert.False(result.Ok); Assert.Equal(code, result.Code);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<ConstructionPurchaseRebaseDecision>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        Assert.Equal("Pending", (await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_source_transition_failure_rolls_back_decision_admission_and_canonical_state(bool borrowed)
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectRebasedTransition BEFORE UPDATE OF Status ON PurchaseOrders WHEN NEW.Status='Approved' BEGIN SELECT RAISE(ABORT, 'deliberate rebased transition failure'); END;");
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = Actor(8) };
        var service = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor);
        await using var owner = borrowed ? await f.Db.Database.BeginTransactionAsync(IsolationLevel.Serializable) : null;
        if (borrowed) await f.Db.Projects.Where(x => x.ID == ids.Project).ExecuteUpdateAsync(x => x.SetProperty(p => p.Name, "Owner change"));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => service.ApproveAsync(ids.Approval, Guid.NewGuid(), "Atomic approval"));
        Assert.Contains("deliberate rebased transition failure", failure.ToString());
        if (borrowed) { Assert.NotNull(f.Db.Database.CurrentTransaction); await owner!.CommitAsync(); }
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<ConstructionPurchaseRebaseDecision>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        Assert.Equal("Draft", (await verify.PurchaseOrders.AsNoTracking().SingleAsync(x => x.ID == ids.Order)).Status);
        Assert.Equal("Pending", (await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
        if (borrowed) Assert.Equal("Owner change", (await verify.Projects.AsNoTracking().SingleAsync()).Name);
    }

    [Theory]
    [InlineData("decision", "RebasedAdmissionSourceNotProven")]
    [InlineData("canonical", "RebasedAdmissionSourceNotProven")]
    [InlineData("received", "ReceiptCoverageRequired")]
    [InlineData("source", "DraftSourceChanged")]
    public async Task Approved_exposure_refuses_unproven_or_realized_continuation(string change, string code)
    {
        using var f = new ConstructionTestFixture(); var ids = await Prepare(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = Actor(8) };
        var result = await ConstructionR7PurchaseApprovalTests.Service(f.Db, actor).ApproveAsync(ids.Approval, Guid.NewGuid(), "Approved");
        Assert.True(result.Ok, result.Code);
        if (change == "decision") await f.Db.Set<ConstructionPurchaseRebaseDecision>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ReviewVersion, new string('F', 64)));
        if (change == "canonical") await f.Db.InventoryApprovals.ExecuteUpdateAsync(x => x.SetProperty(p => p.DecisionNote, "Changed"));
        if (change == "received") await f.Db.PurchaseOrderLines.Where(x => x.PurchaseOrderId == ids.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReceivedQty, .1m));
        if (change == "source") await f.Db.PurchaseOrders.Where(x => x.ID == ids.Order).ExecuteUpdateAsync(x => x.SetProperty(p => p.VendorId, 99));
        await using var verify = f.NewContext();
        var exposure = await new ConstructionBudgetExposureService(verify, new ConstructionR7ExposureRebaseTests.Access(),
            new ConstructionAuditService(verify)).PreviewAsync(actor.Current, ids.Project);
        Assert.False(exposure.Ok); Assert.Equal(code, exposure.Code); Assert.Null(exposure.Rows);
    }
}
