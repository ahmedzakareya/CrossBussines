using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7WarehouseRebaseGuardTests
{
    [Theory]
    [InlineData("ar")] [InlineData("en")] [InlineData("fr")] [InlineData("")]
    public void Refusal_message_is_present_once_in_every_supported_resource(string culture)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var name = culture.Length == 0 ? "SharedResources.resx" : $"SharedResources.{culture}.resx";
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName, "CrossBuy", "Resources", name));
        var entry = Assert.Single(xml.Root!.Elements("data").Where(x =>
            (string?)x.Attribute("name") == "Construction.Rebase.Error.WarehouseExceptionContinuationUnavailable"));
        Assert.False(string.IsNullOrWhiteSpace(entry.Element("value")?.Value));
    }

    internal static async Task Exercise(CrossDbContext db, Func<CrossDbContext> fresh, bool pending, bool inactive)
    {
        var ids = await ConstructionR7RebasedPurchaseRequestTests.Prepare(db, withWarehouse: true);
        var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
        var service = ConstructionR7PurchaseApprovalTests.Service(db, actor);
        var quote = await service.PreviewRebasedRequestAsync(ids.Project, ids.Order);
        Assert.True(quote.Ok, quote.Code);
        var key = Guid.NewGuid();
        ConstructionPurchaseApprovalResult? prior = null;
        if (pending)
        {
            prior = await service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, key, "Existing request");
            Assert.True(prior.Ok, prior.Code);
        }
        var warehouse = await db.PurchaseOrders.AsNoTracking().Where(x => x.ID == ids.Order).Select(x => x.WarehouseId).SingleAsync();
        Assert.NotNull(warehouse);
        var other = new Project { CompanyID = 1, Code = "WH-REBASE-OTHER", Name = "Other project", IsActive = true };
        db.Add(other); await db.SaveChangesAsync();
        db.Add(new ConstructionSite { CompanyID = 1, ProjectId = other.ID, WarehouseId = warehouse,
            Code = "WH-REBASE-SITE", Name = "Foreign source mapping", IsActive = !inactive });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var audits = await db.ConstructionAuditEntries.CountAsync();
        var events = await db.BusinessEvents.CountAsync();
        var blockedPreview = await service.PreviewRebasedRequestAsync(ids.Project, ids.Order);
        Assert.False(blockedPreview.Ok);
        Assert.Equal("WarehouseExceptionContinuationUnavailable", blockedPreview.Code);
        Assert.Null(blockedPreview.Version); Assert.False(blockedPreview.GrantsCurrentAdmission);
        var blockedRequest = await service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, Guid.NewGuid(), "Stale ready quote");
        Assert.False(blockedRequest.Ok);
        Assert.Equal("WarehouseExceptionContinuationUnavailable", blockedRequest.Code);
        if (pending)
        {
            // A replay is the old receipt, never authority to approve the new mapping.
            var replay = await service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, key, "Existing request");
            Assert.Equal("AlreadyRequested", replay.Code); Assert.Equal(prior!.ApprovalId, replay.ApprovalId);
            actor.Current = ConstructionR7BudgetExceptionTests.Approver;
            var decision = await service.ApproveAsync(prior.ApprovalId!.Value, Guid.NewGuid(), "Must not admit");
            Assert.False(decision.Ok); Assert.Equal("WarehouseExceptionBindingRequired", decision.Code);
        }
        using var verify = fresh();
        Assert.Equal(pending ? 1 : 0, await verify.InventoryApprovals.CountAsync());
        Assert.Equal(pending ? 1 : 0, await verify.Set<ConstructionPurchaseApprovalRequest>().CountAsync());
        Assert.Equal(pending ? 1 : 0, await verify.Set<ConstructionPurchaseRebaseRequest>().CountAsync());
        Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().ToArrayAsync());
        Assert.Empty(await verify.Set<ConstructionWarehouseExceptionApplication>().ToArrayAsync());
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().ToArrayAsync());
        Assert.Equal(audits, await verify.ConstructionAuditEntries.CountAsync());
        Assert.Equal(events, await verify.BusinessEvents.CountAsync());
        Assert.Equal("Draft", await verify.PurchaseOrders.Where(x => x.ID == ids.Order).Select(x => x.Status).SingleAsync());
        Assert.Empty(await verify.StockMovements.ToArrayAsync()); Assert.Empty(await verify.JournalEntries.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Mapping_added_after_quote_refuses_new_pending_request_without_writes(bool pending, bool inactive)
    {
        using var fixture = new ConstructionTestFixture();
        await Exercise(fixture.Db, () => fixture.NewContext(), pending, inactive);
    }
}
