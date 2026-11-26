using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7BudgetExceptionTests
{
    [Fact]
    public async Task Browser_request_binds_displayed_financial_witness_and_replay_never_changes_it()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var service = Service(f.Db);
        var actor = ConstructionR7OrderAllocationTests.Actor();
        var preview = await service.PreviewAsync(actor, s.Setup.Project, s.Setup.SecondAllocation, s.Setup.SecondVersion);
        Assert.True(preview.Ok, preview.Code); Assert.Equal(60m, preview.ProposedAmount); Assert.Equal(20m, preview.Overrun);
        Assert.Equal(64, preview.Version!.Length);
        var id = Guid.NewGuid();
        var first = await service.RequestCurrentAsync(actor, s.Setup.Project, s.Setup.SecondAllocation, s.Setup.SecondVersion, preview.Version, id, "Displayed amount");
        Assert.True(first.Ok, first.Code); Assert.Equal(preview.Version, first.Version);
        var changedPolicyToken = Guid.NewGuid().ToByteArray();
        await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ConcurrencyToken, changedPolicyToken));
        var retry = await service.RequestCurrentAsync(actor, s.Setup.Project, s.Setup.SecondAllocation, s.Setup.SecondVersion, preview.Version, id, "Displayed amount");
        Assert.True(retry.Ok); Assert.Equal("AlreadyRequested", retry.Code); Assert.Equal(first.Id, retry.Id);
        var current = await service.PreviewAsync(actor, s.Setup.Project, s.Setup.SecondAllocation, s.Setup.SecondVersion);
        Assert.False(current.Ok); Assert.Equal("AllocationSourceChanged", current.Code);
        var differentWitness = new string(preview.Version[0] == 'A' ? 'B' : 'A', 64);
        var conflict = await service.RequestCurrentAsync(actor, s.Setup.Project, s.Setup.SecondAllocation, s.Setup.SecondVersion, differentWitness, id, "Displayed amount");
        Assert.Equal("IdempotencyConflict", conflict.Code);
        await using var verify = f.NewContext();
        Assert.Equal(2, await verify.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().CountAsync());
        Assert.Empty(await verify.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Changed_exposure_refuses_browser_request_instead_of_silently_replacing_displayed_overrun()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db, "ApprovalRequired");
        var actor = ConstructionR7OrderAllocationTests.Actor(); var service = Service(f.Db);
        var noException = await service.PreviewAsync(actor, s.Project, s.SecondAllocation, s.SecondVersion);
        Assert.Equal("ExceptionNotRequired", noException.Code);
        Assert.True((await ConstructionR7ReservationTests.Service(f.Db).ReserveAsync(actor, s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "First commitment")).Ok);
        var preview = await service.PreviewAsync(actor, s.Project, s.SecondAllocation, s.SecondVersion);
        Assert.True(preview.Ok, preview.Code); Assert.Equal(20m, preview.Overrun);
        await f.Db.PurchaseOrders.Where(x => x.ID == s.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        var reservation = await f.Db.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
        Assert.True((await ConstructionR7ReservationTests.Service(f.Db).ReleaseCancelledAsync(actor, s.Project, reservation.ID, Guid.NewGuid(), "Verified cancellation")).Ok);
        var result = await service.RequestCurrentAsync(actor, s.Project, s.SecondAllocation, s.SecondVersion, preview.Version!, Guid.NewGuid(), "Old screen");
        Assert.False(result.Ok); Assert.Equal("ExceptionSourceChanged", result.Code);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("")] [InlineData("bad")] [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task Browser_request_cannot_fall_back_to_unversioned_request(string version)
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db);
        var result = await Service(f.Db).RequestCurrentAsync(ConstructionR7OrderAllocationTests.Actor(), s.Setup.Project,
            s.Setup.SecondAllocation, s.Setup.SecondVersion, version, Guid.NewGuid(), "Missing witness");
        Assert.False(result.Ok); Assert.Equal("InvalidExceptionRequest", result.Code);
        await using var verify = f.NewContext();
        Assert.Single(await verify.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("request")] [InlineData("decision")] [InlineData("consumption")]
    public async Task Each_new_workflow_fact_has_company_read_filter_and_rejects_foreign_company_writes(string kind)
    {
        using var f = new ConstructionTestFixture();
        object entity = kind switch
        {
            "request" => new ConstructionBudgetExceptionRequest { CompanyID = 2 },
            "decision" => new ConstructionBudgetExceptionDecision { CompanyID = 2 },
            _ => new ConstructionBudgetExceptionConsumption { CompanyID = 2 }
        };
        Assert.NotNull(f.Db.Model.FindEntityType(entity.GetType())!.GetQueryFilter());
        f.Db.Add(entity);
        await Assert.ThrowsAsync<CrossBuy.BL.Platform.CompanyWriteDeniedException>(() => f.Db.SaveChangesAsync());
    }

    internal static BusinessContext Approver => new() { CompanyId = 1, EmployeeId = 8, UserId = "independent-finance", Source = BusinessContextSource.Test };
    internal static ConstructionBudgetExceptionService Service(CrossDbContext db, bool allowed = true) => new(db,
        new ConstructionR7OrderAllocationTests.Access(allowed), new ConstructionAuditService(db),
        new ConstructionBudgetPolicyService(db, new ConstructionR7OrderAllocationTests.Access(allowed), new ConstructionAuditService(db)));
    internal static async Task<(ConstructionR7ReservationTests.Setup Setup, ConstructionBudgetExceptionResult Request)> Arrange(CrossDbContext db)
    {
        var setup = await ConstructionR7ReservationTests.Arrange(db, "ApprovalRequired");
        var reserved = await ConstructionR7ReservationTests.Service(db).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(),
            setup.Project, setup.FirstAllocation, setup.FirstVersion, Guid.NewGuid(), "First commitment");
        Assert.True(reserved.Ok, reserved.Code);
        var request = await Service(db).RequestAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project,
            setup.SecondAllocation, setup.SecondVersion, Guid.NewGuid(), "Exception for exact proposal");
        Assert.True(request.Ok, request.Code); Assert.Equal(60m, request.ProposedAmount); Assert.Equal(20m, request.Overrun);
        return (setup, request);
    }

    [Fact]
    public async Task Independent_approval_is_consumed_once_and_retry_never_renews_admission()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var service = Service(f.Db);
        Assert.Equal("IndependentApproverRequired", (await service.ApproveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Setup.Project,
            s.Request.Id!.Value, s.Request.Version!, Guid.NewGuid(), "Self approval")).Code);
        var approved = await service.ApproveAsync(Approver, s.Setup.Project, s.Request.Id!.Value, s.Request.Version!, Guid.NewGuid(), "Independent review");
        Assert.True(approved.Ok, approved.Code);
        var requestId = Guid.NewGuid();
        var result = await service.ConsumeAsync(ConstructionR7OrderAllocationTests.Actor(), s.Setup.Project, s.Request.Id.Value, requestId, "Use exact approval");
        Assert.True(result.GrantsCurrentAdmission, result.Code); Assert.Equal("ApprovedBudgetException", result.Code);
        var retry = await service.ConsumeAsync(Approver, s.Setup.Project, s.Request.Id.Value, requestId, "Use exact approval");
        Assert.Equal(result.Id, retry.Id); Assert.Equal("AlreadyReserved", retry.Code); Assert.False(retry.GrantsCurrentAdmission);
        Assert.Equal("ExceptionAlreadyConsumed", (await service.ConsumeAsync(Approver, s.Setup.Project, s.Request.Id.Value, Guid.NewGuid(), "Do not reuse")).Code);
        await using var verify = f.NewContext();
        Assert.Equal(2, await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().CountAsync());
        Assert.Single(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Source_policy_change_invalidates_approval_before_decision_or_consumption(bool afterApproval)
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var service = Service(f.Db);
        if (afterApproval) Assert.True((await service.ApproveAsync(Approver, s.Setup.Project, s.Request.Id!.Value, s.Request.Version!, Guid.NewGuid(), "Review")).Ok);
        var token = Guid.NewGuid().ToByteArray();
        await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ConcurrencyToken, token));
        if (afterApproval) Assert.Equal("ExceptionSourceChanged", (await service.ConsumeAsync(Approver, s.Setup.Project, s.Request.Id!.Value, Guid.NewGuid(), "Stale decision")).Code);
        else Assert.Equal("ExceptionSourceChanged", (await service.ApproveAsync(Approver, s.Setup.Project, s.Request.Id!.Value, s.Request.Version!, Guid.NewGuid(), "Stale proposal")).Code);
        await using var verify = f.NewContext();
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Rejected_or_pending_request_cannot_be_consumed_and_decision_cannot_be_replaced()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var service = Service(f.Db);
        Assert.Equal("ApprovedExceptionRequired", (await service.ConsumeAsync(Approver, s.Setup.Project, s.Request.Id!.Value, Guid.NewGuid(), "Pending")).Code);
        Assert.True((await service.RejectAsync(Approver, s.Setup.Project, s.Request.Id!.Value, s.Request.Version!, Guid.NewGuid(), "Reject")).Ok);
        Assert.Equal("AlreadyDecided", (await service.ApproveAsync(Approver, s.Setup.Project, s.Request.Id!.Value, s.Request.Version!, Guid.NewGuid(), "Cannot overwrite")).Code);
        Assert.Equal("ApprovedExceptionRequired", (await service.ConsumeAsync(Approver, s.Setup.Project, s.Request.Id!.Value, Guid.NewGuid(), "Rejected")).Code);
    }

    [Theory]
    [InlineData("HardBlock", "BudgetHardBlock")] [InlineData("Warning", "ExceptionNotRequired")]
    public async Task Workflow_does_not_override_other_company_modes(string mode, string expected)
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db, mode);
        Assert.True((await ConstructionR7ReservationTests.Service(f.Db).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "First")).Ok);
        var result = await Service(f.Db).RequestAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.SecondAllocation, s.SecondVersion, Guid.NewGuid(), "No implicit override");
        Assert.False(result.Ok); Assert.Equal(expected, result.Code);
        Assert.Empty(await f.Db.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Authorization_scope_clean_context_and_idempotency_remain_enforced()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db);
        Assert.Equal("AccessDenied", (await Service(f.Db, false).ApproveAsync(Approver, s.Setup.Project, s.Request.Id!.Value, s.Request.Version!, Guid.NewGuid(), "Denied")).Code);
        Assert.Equal("AccessDenied", (await Service(f.Db).ConsumeAsync(ConstructionR7OrderAllocationTests.Actor(2), s.Setup.Project, s.Request.Id!.Value, Guid.NewGuid(), "Other company")).Code);
        var external = new BusinessContext { CompanyId = 1, EmployeeId = 8, UserId = "external", Source = BusinessContextSource.ExternalPortal };
        Assert.Equal("AccessDenied", (await Service(f.Db).ApproveAsync(external, s.Setup.Project, s.Request.Id!.Value, s.Request.Version!, Guid.NewGuid(), "No external finance authority")).Code);
        var id = Guid.NewGuid(); var service = Service(f.Db);
        var first = await service.RequestAsync(ConstructionR7OrderAllocationTests.Actor(), s.Setup.Project, s.Setup.SecondAllocation, s.Setup.SecondVersion, id, "Retry reason");
        var retry = await service.RequestAsync(ConstructionR7OrderAllocationTests.Actor(), s.Setup.Project, s.Setup.SecondAllocation, s.Setup.SecondVersion, id, "Retry reason");
        Assert.Equal(first.Id, retry.Id); Assert.Equal("AlreadyRequested", retry.Code);
        Assert.Equal("IdempotencyConflict", (await service.RequestAsync(ConstructionR7OrderAllocationTests.Actor(), s.Setup.Project, s.Setup.SecondAllocation, s.Setup.SecondVersion, id, "Changed reason")).Code);
        f.Db.Add(new ConstructionBudgetExceptionDecision());
        Assert.Equal("CleanUnitOfWorkRequired", (await service.ConsumeAsync(Approver, s.Setup.Project, s.Request.Id!.Value, Guid.NewGuid(), "Dirty")).Code);
    }
}
