using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7RebasedWarehouseConsentTests
{
    internal sealed record Scope(int Project, int Order, int SourceWarehouse, int DestinationSite, int OriginSite, int DestinationWarehouse);

    private static ConstructionRebasedWarehouseConsentService Service(CrossDbContext db,
        ConstructionR7PurchaseApprovalTests.ActorAccessor actor, params int[] warehouses) => new(db, actor,
        new ConstructionR7ActualCostTests.Access(),
        new[] { new ConstructionR7PurchaseApprovalTests.InventoryAuthority() },
        new ConstructionR7WarehouseClassificationTests.WarehouseAccess(warehouses), new ConstructionAuditService(db));

    internal static async Task<Scope> Seed(CrossDbContext db)
    {
        var ids = await ConstructionR7RebasedPurchaseRequestTests.Prepare(db, withWarehouse: true);
        var sourceWarehouse = await db.PurchaseOrders.AsNoTracking().Where(x => x.ID == ids.Order).Select(x => x.WarehouseId).SingleAsync();
        var other = new Project { CompanyID = 1, Code = "R7-WH-REBASE-OTHER", Name = "Origin project", IsActive = true };
        var destinationWarehouse = new Warehouse { CompanyID = 1, Code = "R7-WH-REBASE-DEST", Name = "Destination warehouse", IsActive = true };
        db.AddRange(other, destinationWarehouse, new ConstructionCompanyPolicy { CompanyID = 1,
            PolicyKey = "CrossSiteWarehouse", Mode = "ApprovedException", Reason = "Explicit test policy",
            UpdatedBy = 7, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var origin = new ConstructionSite { CompanyID = 1, ProjectId = other.ID, WarehouseId = sourceWarehouse,
            Code = "R7-WH-REBASE-SOURCE", Name = "Source site", IsActive = true };
        var destination = new ConstructionSite { CompanyID = 1, ProjectId = ids.Project, WarehouseId = destinationWarehouse.ID,
            Code = "R7-WH-REBASE-TARGET", Name = "Target site", IsActive = true };
        db.AddRange(origin, destination);
        await db.SaveChangesAsync();
        return new(ids.Project, ids.Order, sourceWarehouse!.Value, destination.ID, origin.ID, destinationWarehouse.ID);
    }

    [Fact]
    public async Task Independent_consent_is_source_bound_and_never_admits_or_grants_warehouse_access()
    {
        using var f = new ConstructionTestFixture();
        var s = await Seed(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = Service(f.Db, actor, s.SourceWarehouse, s.DestinationWarehouse);
        var quote = await service.PreviewAsync(s.Project, s.Order, s.DestinationSite);
        Assert.True(quote.Ok, quote.Code);
        Assert.False(quote.OperationalGrant);
        var requestId = Guid.NewGuid();
        var request = await service.RequestAsync(s.Project, s.Order, s.DestinationSite, quote.Version!, requestId, "Explicit rebased destination");
        Assert.True(request.Ok, request.Code);
        Assert.False(request.OperationalGrant);
        Assert.Equal(request.Id, (await service.RequestAsync(s.Project, s.Order, s.DestinationSite, quote.Version!, requestId,
            "Explicit rebased destination")).Id);
        var requesterAfterRequest = await service.PreviewAsync(s.Project, s.Order, s.DestinationSite);
        Assert.True(requesterAfterRequest.Ok, requesterAfterRequest.Code);
        Assert.Equal(quote.SourceFactsVersion, requesterAfterRequest.SourceFactsVersion);
        Assert.Equal(quote.MappingVersion, requesterAfterRequest.MappingVersion);

        actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        var approverQuote = await service.PreviewAsync(s.Project, s.Order, s.DestinationSite);
        Assert.True(approverQuote.Ok, approverQuote.Code);
        Assert.Equal(quote.SourceFactsVersion, approverQuote.SourceFactsVersion);
        Assert.Equal(quote.MappingVersion, approverQuote.MappingVersion);
        var decisionId = Guid.NewGuid();
        var decision = await service.DecideAsync(s.Project, request.Id!.Value, "Approved", decisionId, "Independent consent");
        Assert.True(decision.Ok, decision.Code);
        Assert.False(decision.OperationalGrant);
        Assert.Equal(decision.Id, (await service.DecideAsync(s.Project, request.Id.Value, "Approved", decisionId, "Independent consent")).Id);

        await using var verify = f.NewContext();
        var savedRequest = await verify.Set<ConstructionRebasedWarehouseConsentRequest>().AsNoTracking().SingleAsync();
        var savedDecision = await verify.Set<ConstructionRebasedWarehouseConsentDecision>().AsNoTracking().SingleAsync();
        Assert.Equal(s.SourceWarehouse, savedRequest.SourceWarehouseId);
        Assert.Equal(s.DestinationSite, savedRequest.DestinationSiteId);
        Assert.Equal(savedRequest.SourceFactsVersion, savedDecision.SourceFactsVersion);
        Assert.Equal("Approved", savedDecision.Outcome);
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());
        Assert.Empty(await verify.Set<ConstructionWarehouseExceptionApplication>().ToArrayAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseApprovalRequest>().ToArrayAsync());
        Assert.Empty(await verify.StockMovements.ToArrayAsync());
        Assert.Empty(await verify.JournalEntries.ToArrayAsync());
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("policy")]
    public async Task Changed_mapping_or_company_policy_invalidates_consent_before_decision(string changed)
    {
        using var f = new ConstructionTestFixture();
        var s = await Seed(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = Service(f.Db, actor, s.SourceWarehouse, s.DestinationWarehouse);
        var quote = await service.PreviewAsync(s.Project, s.Order, s.DestinationSite);
        Assert.True(quote.Ok, quote.Code);
        var request = await service.RequestAsync(s.Project, s.Order, s.DestinationSite, quote.Version!, Guid.NewGuid(), "Review mapping");
        Assert.True(request.Ok, request.Code);
        if (changed == "mapping")
            await f.Db.Set<ConstructionSite>().Where(x => x.ID == s.OriginSite).ExecuteUpdateAsync(x => x.SetProperty(y => y.IsActive, false));
        else
            await f.Db.Set<ConstructionCompanyPolicy>().Where(x => x.CompanyID == 1 && x.PolicyKey == "CrossSiteWarehouse")
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Mode, "Restricted"));

        actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        var result = await Service(f.Db, actor, s.SourceWarehouse, s.DestinationWarehouse)
            .DecideAsync(s.Project, request.Id!.Value, "Approved", Guid.NewGuid(), "Must not approve stale facts");
        Assert.False(result.Ok);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<ConstructionRebasedWarehouseConsentDecision>().ToArrayAsync());
        Assert.Single(await verify.Set<ConstructionRebasedWarehouseConsentRequest>().ToArrayAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());
    }

    [Fact]
    public async Task Requester_cannot_decide_own_consent()
    {
        using var f = new ConstructionTestFixture();
        var s = await Seed(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = Service(f.Db, actor, s.SourceWarehouse, s.DestinationWarehouse);
        var quote = await service.PreviewAsync(s.Project, s.Order, s.DestinationSite);
        Assert.True(quote.Ok, quote.Code);
        var request = await service.RequestAsync(s.Project, s.Order, s.DestinationSite, quote.Version!, Guid.NewGuid(), "Independent decision required");
        Assert.True(request.Ok, request.Code);
        var result = await service.DecideAsync(s.Project, request.Id!.Value, "Approved", Guid.NewGuid(), "Self approval");
        Assert.False(result.Ok);
        Assert.Equal("SeparateApproverRequired", result.Code);
        await using var verify = f.NewContext();
        Assert.Empty(await verify.Set<ConstructionRebasedWarehouseConsentDecision>().ToArrayAsync());
    }

    [Fact]
    public async Task Approved_consent_is_bound_and_consumed_by_the_exact_rebased_purchase_admission()
    {
        using var f = new ConstructionTestFixture();
        var s = await Seed(f.Db);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var warehouses = new ConstructionR7WarehouseClassificationTests.WarehouseAccess(s.SourceWarehouse, s.DestinationWarehouse);
        var consentService = Service(f.Db, actor, s.SourceWarehouse, s.DestinationWarehouse);
        var consentQuote = await consentService.PreviewAsync(s.Project, s.Order, s.DestinationSite);
        Assert.True(consentQuote.Ok, consentQuote.Code);
        var consentRequest = await consentService.RequestAsync(s.Project, s.Order, s.DestinationSite,
            consentQuote.Version!, Guid.NewGuid(), "Approve the named rebased destination");
        Assert.True(consentRequest.Ok, consentRequest.Code);
        actor.Current = ConstructionR7BudgetExceptionTests.Approver;
        var approverQuote = await consentService.PreviewAsync(s.Project, s.Order, s.DestinationSite);
        Assert.True(approverQuote.Ok, approverQuote.Code);
        Assert.Equal(consentQuote.SourceFactsVersion, approverQuote.SourceFactsVersion);
        Assert.Equal(consentQuote.MappingVersion, approverQuote.MappingVersion);
        var consentDecision = await consentService.DecideAsync(s.Project, consentRequest.Id!.Value,
            "Approved", Guid.NewGuid(), "Independent destination approval");
        Assert.True(consentDecision.Ok, consentDecision.Code);

        actor.Current = ConstructionR7OrderAllocationTests.Actor();
        var purchases = ConstructionR7PurchaseApprovalTests.Service(f.Db, actor, warehouses: warehouses,
            projectAccess: new ConstructionR7ActualCostTests.Access());
        var unboundQuote = await purchases.PreviewRebasedRequestAsync(s.Project, s.Order);
        Assert.Equal("WarehouseExceptionContinuationUnavailable", unboundQuote.Code);
        var purchaseQuote = await purchases.PreviewRebasedRequestWithWarehouseConsentAsync(s.Project, s.Order, consentDecision.Id!.Value);
        Assert.True(purchaseQuote.Ok, purchaseQuote.Code);
        var pending = await purchases.RequestRebasedWithWarehouseConsentAsync(s.Project, s.Order, consentDecision.Id.Value,
            purchaseQuote.Version!, Guid.NewGuid(), "Submit rebased purchase for independent approval");
        Assert.True(pending.Ok, pending.Code);
        var rechecked = await purchases.PreviewRebasedRequestWithWarehouseConsentAsync(s.Project, s.Order, consentDecision.Id.Value);
        Assert.True(rechecked.Ok, $"post-request review: {rechecked.Code}");

        await using (var before = f.NewContext())
        {
            var binding = Assert.Single(await before.Set<ConstructionRebasedWarehouseConsentBinding>().AsNoTracking().ToListAsync());
            Assert.Empty(await before.Set<ConstructionRebasedWarehouseConsentApplication>().AsNoTracking().ToListAsync());
            Assert.Equal(consentDecision.Id, binding.ConsentDecisionRowId);
            Assert.Equal(pending.OrderId, binding.PurchaseOrderId);
        }

        actor.Current = new BusinessContext { CompanyId = 1, EmployeeId = 9, UserId = "independent-purchase-approver", Source = BusinessContextSource.Test };
        var approved = await purchases.ApproveAsync(pending.ApprovalId!.Value, Guid.NewGuid(), "Approve exact rebased purchase");
        Assert.True(approved.Ok, approved.Code);
        await using var verify = f.NewContext();
        var admission = Assert.Single(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
        var application = Assert.Single(await verify.Set<ConstructionRebasedWarehouseConsentApplication>().AsNoTracking().ToListAsync());
        Assert.Equal(admission.ID, application.AdmissionId);
        Assert.Equal(admission.RequestId, application.RequestId);
        Assert.Equal("Approved", (await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await verify.StockMovements.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToArrayAsync());
    }
}
