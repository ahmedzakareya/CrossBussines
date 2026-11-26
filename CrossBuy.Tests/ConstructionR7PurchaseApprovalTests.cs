using System.Reflection;
using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7PurchaseApprovalTests
{
    internal sealed record Setup(ConstructionR7PurchaseDraftTests.SeedData Seed, int Order, int Allocation, string Version, int[] Lines);
    internal sealed class ActorAccessor : IBusinessContextAccessor
    {
        public BusinessContext Current = ConstructionR7OrderAllocationTests.Actor();
        public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult<BusinessContext?>(Current);
    }
    internal sealed class InventoryAuthority : IModuleAccessService
    {
        public bool Purchase = true, Manage = true;
        public string Scope => EntityRegistry.ScopeInventory;
        public IReadOnlyCollection<string> Actions => new[] { "purchase", "manage" };
        public Task<bool> CanAsync(BusinessContext c, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(c.IsAuthenticated && c.CompanyId == 1 && target?.CompanyId == 1 && (action == "purchase" ? Purchase : action == "manage" && Manage));
        public Task<IReadOnlyList<string>> RolesAsync(BusinessContext c, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(new[] { "InventoryManager" });
    }
    internal static BusinessEventService Events(CrossDbContext db, ActorAccessor actor) => new(db, new EntityRegistry(db), actor, NullLogger<BusinessEventService>.Instance);
    internal static ConstructionPurchaseApprovalService Service(CrossDbContext db, ActorAccessor actor, InventoryAuthority? inventory = null, bool projectAllowed = true, IConstructionWarehouseAccess? warehouses = null, IProjectsAccessService? projectAccess = null) => new(db,
        actor, new[] { inventory ?? new InventoryAuthority() }, projectAccess ?? new ConstructionR7OrderAllocationTests.Access(projectAllowed), new ConstructionAuditService(db),
        new ConstructionBudgetPolicyService(db, new ConstructionR7OrderAllocationTests.Access(projectAllowed), new ConstructionAuditService(db)), Events(db, actor), warehouses);
    internal static async Task<Setup> Arrange(CrossDbContext db, decimal? price = null, string policy = "HardBlock")
    {
        var seed = await ConstructionR7PurchaseDraftTests.Seed(db);
        await db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, policy));
        var created = await ConstructionR7PurchaseDraftTests.Create(db, seed, Guid.NewGuid(), price.HasValue ? new[] { new ConstructionPurchaseDraftLine(seed.Item, "Service", 1, seed.Unit, price.Value, 0, 20) } : null);
        Assert.True(created.Ok, created.Code);
        var allocation = new ConstructionOrderAllocationService(db, new ConstructionR7OrderAllocationTests.Access(), new ConstructionAuditService(db));
        var preview = await allocation.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), seed.Project, created.OrderId!.Value); Assert.True(preview.Ok, preview.Code);
        var amount = await db.PurchaseOrders.AsNoTracking().Where(x => x.ID == created.OrderId).Select(x => x.SubTotal).SingleAsync();
        var saved = await allocation.SaveAsync(ConstructionR7OrderAllocationTests.Actor(), seed.Project, created.OrderId.Value, preview.Version!, Guid.NewGuid(),
            new[] { new ConstructionCommitmentSplit(created.LineIds!.Single(), seed.Node, seed.Cost, amount) }, "Allocate persisted proposal");
        Assert.True(saved.Ok, saved.Code); return new(seed, created.OrderId.Value, saved.Id!.Value, preview.Version!, created.LineIds.ToArray());
    }
    internal static Task<ConstructionPurchaseApprovalResult> Request(ConstructionPurchaseApprovalService service, Setup s, Guid id, int? exception = null) =>
        service.RequestAsync(s.Seed.Project, s.Order, s.Allocation, s.Version, exception, id, "Request exact construction purchase");
    internal static InventoryApprovalService Gateway(CrossDbContext db, ActorAccessor actor, ConstructionPurchaseApprovalService service, InventoryAuthority? inventory = null) =>
        new(db, DispatchProxy.Create<IStockService, UnexpectedLegacyCall>(), DispatchProxy.Create<IProcurementService, UnexpectedLegacyCall>(),
            DispatchProxy.Create<INotificationService, UnexpectedLegacyCall>(), actor, new[] { inventory ?? new InventoryAuthority() }, Events(db, actor), service);
    public class UnexpectedLegacyCall : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Construction approval must not recreate the legacy document or send a pre-commit notification.");
    }

    [Fact]
    public async Task Inbox_review_uses_candidate_project_authority_and_excludes_both_source_actors()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var actor = new ActorAccessor();
        actor.Current = new BusinessContext { CompanyId = 1, EmployeeId = 9, UserId = "requester-nine", Source = BusinessContextSource.Test };
        var service = Service(f.Db, actor); var request = await Request(service, s, Guid.NewGuid()); Assert.True(request.Ok, request.Code);
        var id = request.ApprovalId!.Value;
        Assert.False(await service.CanReviewAsync(actor.Current, id));
        Assert.False(await service.CanReviewAsync(ConstructionR7OrderAllocationTests.Actor(), id));
        var candidate = ConstructionR7BudgetExceptionTests.Approver;
        Assert.True(await service.CanReviewAsync(candidate, id));
        Assert.False(await Service(f.Db, actor, projectAllowed: false).CanReviewAsync(candidate, id));
        Assert.False(await Service(f.Db, actor, new InventoryAuthority { Manage = false }).CanReviewAsync(candidate, id));
        Assert.False(await service.CanReviewAsync(ConstructionR7OrderAllocationTests.Actor(2), id));
        Assert.Empty(await f.Db.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Existing_recent_list_and_task_inbox_share_construction_project_visibility_without_hiding_legacy_rows()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var actor = new ActorAccessor();
        var service = Service(f.Db, actor); var request = await Request(service, s, Guid.NewGuid()); Assert.True(request.Ok, request.Code);
        f.Db.Add(new InventoryUserRole { CompanyID = 1, EmployeeId = 8, Role = "InventoryManager", CreatedAt = DateTime.UtcNow });
        f.Db.Add(new InventoryApproval { CompanyID = 1, DocType = "PurchaseOrder", Status = "Pending", Amount = 10, RequestedByEmployeeId = 7, RequestedAt = DateTime.UtcNow, PayloadJson = "{}" });
        await f.Db.SaveChangesAsync(); actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        var gateway = Gateway(f.Db, actor, service);
        Assert.Equal(2, (await gateway.RecentAsync()).Count);
        Assert.Equal(2, (await gateway.ApprovalInboxAsync(actor.Current, 8)).pending.Count);
        var denied = Gateway(f.Db, actor, Service(f.Db, actor, projectAllowed: false));
        Assert.Equal("PurchaseOrder", Assert.Single(await denied.RecentAsync()).DocType);
        Assert.Equal("PurchaseOrder", Assert.Single((await denied.ApprovalInboxAsync(actor.Current, 8)).pending).DocType);
        actor.Current = ConstructionR7OrderAllocationTests.Actor();
        Assert.Equal("PurchaseOrder", Assert.Single(await gateway.RecentAsync()).DocType);
    }

    [Fact]
    public async Task Existing_gateway_approves_original_source_with_exact_currency_and_current_budget_witness()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var actor = new ActorAccessor(); var service = Service(f.Db, actor);
        var id = Guid.NewGuid(); var request = await Request(service, s, id); Assert.True(request.Ok, request.Code);
        Assert.Equal("AlreadyRequested", (await Request(service, s, id)).Code);
        actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        var gateway = Gateway(f.Db, actor, service);
        Assert.True((await gateway.ApproveAsync(request.ApprovalId!.Value, 8, "Independent purchase decision")).ok);
        Assert.True((await gateway.ApproveAsync(request.ApprovalId.Value, 8, "Independent purchase decision")).ok);
        await using var verify = f.NewContext(); var source = await verify.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleAsync();
        Assert.Equal(s.Order, source.ID); Assert.Equal("Approved", source.Status); Assert.Equal(s.Lines, source.Lines.Select(x => x.ID).OrderBy(x => x).ToArray());
        var stored = await verify.InventoryApprovals.AsNoTracking().SingleAsync(); Assert.Equal(1.482m, stored.Amount); Assert.Equal("Approved", stored.Status);
        Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Equal(2, await verify.BusinessEvents.AsNoTracking().CountAsync()); Assert.NotEmpty(await verify.BusinessEventDispatches.AsNoTracking().ToListAsync());
        var exposure = await ConstructionR7BudgetExposureTests.Reader(verify).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), s.Seed.Project);
        Assert.True(exposure.Ok, exposure.Code); Assert.Equal(1.235m, Assert.Single(exposure.Rows!).Commitment);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Inventory_project_company_actor_and_both_independence_boundaries_are_enforced()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var actor = new ActorAccessor(); var inventory = new InventoryAuthority(); var service = Service(f.Db, actor, inventory);
        actor.Current = new BusinessContext { CompanyId = 1, EmployeeId = 9, UserId = "requester", Source = BusinessContextSource.Test };
        var request = await Request(service, s, Guid.NewGuid()); Assert.True(request.Ok, request.Code);
        Assert.Equal("IndependentApproverRequired", (await service.ApproveAsync(request.ApprovalId!.Value, Guid.NewGuid(), "Cannot decide own request")).Code);
        actor.Current = ConstructionR7OrderAllocationTests.Actor();
        Assert.Equal("IndependentApproverRequired", (await service.ApproveAsync(request.ApprovalId.Value, Guid.NewGuid(), "Creator cannot use a proxy requester")).Code);
        actor.Current = ConstructionR7BudgetExceptionTests.Approver; inventory.Manage = false;
        Assert.Equal("AccessDenied", (await service.ApproveAsync(request.ApprovalId.Value, Guid.NewGuid(), "Denied inventory role")).Code); inventory.Manage = true;
        Assert.Equal("AccessDenied", (await Service(f.Db, actor, inventory, false).ApproveAsync(request.ApprovalId.Value, Guid.NewGuid(), "Denied project")).Code);
        Assert.False((await Gateway(f.Db, actor, service, inventory).ApproveAsync(request.ApprovalId.Value, 99, "Spoofed employee")).ok);
        actor.Current = ConstructionR7OrderAllocationTests.Actor(2);
        Assert.Equal("AccessDenied", (await service.ApproveAsync(request.ApprovalId.Value, Guid.NewGuid(), "Foreign company")).Code);
        await using var verify = f.NewContext(); Assert.Equal("Draft", (await verify.PurchaseOrders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync()); Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("source", "DraftSourceChanged")][InlineData("payload", "ApprovalSnapshotChanged")]
    [InlineData("currency", "DraftCurrencyChanged")][InlineData("baseline", "ApprovedBaselineRequired")][InlineData("policy", "AllocationSourceChanged")]
    public async Task Changed_source_request_currency_baseline_or_policy_cannot_be_approved(string changed, string expected)
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var actor = new ActorAccessor(); var service = Service(f.Db, actor);
        var request = await Request(service, s, Guid.NewGuid()); Assert.True(request.Ok, request.Code); actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        if (changed == "source") await f.Db.PurchaseOrderLines.ExecuteUpdateAsync(x => x.SetProperty(p => p.ItemDescription, "Changed identity"));
        if (changed == "payload") await f.Db.InventoryApprovals.ExecuteUpdateAsync(x => x.SetProperty(p => p.PayloadJson, "{}"));
        if (changed == "currency") await f.Db.Currencies.ExecuteUpdateAsync(x => x.SetProperty(p => p.DecimalPlaces, (byte)2));
        if (changed == "baseline") await f.Db.Set<ConstructionBudgetVersion>().ExecuteUpdateAsync(x => x.SetProperty(p => p.IsBaseline, false));
        if (changed == "policy")
        {
            var token = Guid.NewGuid().ToByteArray();
            await f.Db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ConcurrencyToken, token));
        }
        Assert.Equal(expected, (await service.ApproveAsync(request.ApprovalId!.Value, Guid.NewGuid(), "Do not approve changed facts")).Code);
        await using var verify = f.NewContext(); Assert.Equal("Pending", (await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync()); Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        Assert.Single(await verify.BusinessEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Rejection_preserves_request_history_and_allows_a_new_independent_request()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db); var actor = new ActorAccessor(); var service = Service(f.Db, actor);
        var first = await Request(service, s, Guid.NewGuid()); Assert.True(first.Ok, first.Code); actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        Assert.True((await Gateway(f.Db, actor, service).RejectAsync(first.ApprovalId!.Value, 8, "Revise the request")).ok);
        Assert.Equal("AlreadyRejected", (await service.RejectAsync(first.ApprovalId.Value, "Revise the request")).Code);
        actor.Current = ConstructionR7OrderAllocationTests.Actor(); var next = await Request(service, s, Guid.NewGuid()); Assert.True(next.Ok, next.Code);
        actor.Current = ConstructionR7BudgetExceptionTests.Approver; Assert.True((await service.ApproveAsync(next.ApprovalId!.Value, Guid.NewGuid(), "Revised review")).Ok);
        await using var verify = f.NewContext(); Assert.Equal(2, await verify.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().CountAsync());
        Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync()); Assert.Equal(4, await verify.BusinessEvents.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Budget_exception_is_consumed_in_the_same_purchase_decision_and_cannot_be_reused()
    {
        using var f = new ConstructionTestFixture(); var s = await Arrange(f.Db, 200m, "ApprovalRequired"); var actor = new ActorAccessor(); var service = Service(f.Db, actor);
        var exceptions = ConstructionR7BudgetExceptionTests.Service(f.Db);
        var exception = await exceptions.RequestAsync(actor.Current, s.Seed.Project, s.Allocation, s.Version, Guid.NewGuid(), "Review budget overrun"); Assert.True(exception.Ok, exception.Code);
        var request = await Request(service, s, Guid.NewGuid(), exception.Id); Assert.True(request.Ok, request.Code); actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        Assert.Equal("ApprovedExceptionRequired", (await service.ApproveAsync(request.ApprovalId!.Value, Guid.NewGuid(), "Not approved by finance yet")).Code);
        Assert.True((await exceptions.ApproveAsync(actor.Current, s.Seed.Project, exception.Id!.Value, exception.Version!, Guid.NewGuid(), "Independent finance review")).Ok);
        var id = Guid.NewGuid(); Assert.True((await service.ApproveAsync(request.ApprovalId.Value, id, "Approve exact exception")).Ok);
        Assert.Equal("AlreadyApproved", (await service.ApproveAsync(request.ApprovalId.Value, id, "Approve exact exception")).Code);
        await using var verify = f.NewContext(); Assert.Single(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
        Assert.Equal("ApprovedBudgetException", (await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync()).DecisionCode);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task New_history_entities_are_company_filtered_and_write_guarded(bool admission)
    {
        using var f = new ConstructionTestFixture(); object row = admission ? new ConstructionPurchaseAdmission { CompanyID = 2 } : new ConstructionPurchaseApprovalRequest { CompanyID = 2 };
        Assert.NotNull(f.Db.Model.FindEntityType(row.GetType())!.GetQueryFilter()); f.Db.Add(row);
        await Assert.ThrowsAsync<CompanyWriteDeniedException>(() => f.Db.SaveChangesAsync());
    }
}
