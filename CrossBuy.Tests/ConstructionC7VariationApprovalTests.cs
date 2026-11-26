using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionC7VariationApprovalTests
{
    [Fact]
    public async Task Approving_variation_routes_adjustments_through_immutable_revision_and_tracks_new_lines()
    {
        using var f = new ConstructionTestFixture();
        var project = await f.SeedProjectAsync();
        await f.Boq.SaveLinesAsync(1, project.ID, new List<BoqLineInput>
        {
            new() { Code = "A", Description = "Existing", Quantity = 10m, UnitPrice = 5m }
        }, 7, "baseline");
        var existingId = (await f.ActiveLineIdsByCodeAsync(project.ID))["A"];
        var vo = await f.SeedDraftVariationAsync(project.ID, voNo: 2);
        var service = new VariationOrderService(f.Db, f.Revisions);

        var saved = await service.SaveDraftAsync(1, project.ID, vo.ID, "Change order", null,
            "approved instruction", new List<VoLineInput>
            {
                new() { Kind = "Adjust", BoqItemId = existingId, Quantity = 12m, UnitPrice = 6m },
                new() { Kind = "New", Code = "B", Description = "Added", Quantity = 3m, UnitPrice = 4m }
            }, 7);
        Assert.True(saved.ok, saved.error);

        var result = await service.ApproveAsync(1, vo.ID, 9);
        Assert.True(result.ok, result.error);

        await using var verify = f.NewContext();
        var adjusted = await verify.BoqItems.AsNoTracking().SingleAsync(x => x.ID == existingId);
        Assert.Equal(12m, adjusted.Quantity);
        Assert.Equal(6m, adjusted.UnitPrice);
        var added = await verify.BoqItems.AsNoTracking().SingleAsync(x => x.ProjectId == project.ID && x.Code == "B");
        Assert.Equal(vo.ID, added.VariationOrderId);

        var revision = await verify.CommercialRevisions.AsNoTracking()
            .SingleAsync(x => x.SourceVariationOrderId == vo.ID);
        Assert.Equal(CommercialRevisionSource.Variation, revision.Source);
        Assert.Equal(CommercialRevisionStatus.Approved, revision.Status);
        Assert.Equal("approved instruction", revision.Reason);
        Assert.Equal(9, revision.ApprovedBy);
        Assert.Equal(revision.ID, (await verify.BoqItems.AsNoTracking().SingleAsync(x => x.ID == added.ID)).CurrentRevisionId);
        Assert.Equal(34m, (await verify.VariationOrders.AsNoTracking().SingleAsync(x => x.ID == vo.ID)).Value);
    }

    [Fact]
    public async Task Variation_without_reason_is_not_approved_or_applied()
    {
        using var f = new ConstructionTestFixture();
        var project = await f.SeedProjectAsync();
        await f.Boq.SaveLinesAsync(1, project.ID, new List<BoqLineInput>
        {
            new() { Code = "A", Description = "Existing", Quantity = 10m, UnitPrice = 5m }
        }, 7, "baseline");
        var existingId = (await f.ActiveLineIdsByCodeAsync(project.ID))["A"];
        var vo = await f.SeedDraftVariationAsync(project.ID);
        var service = new VariationOrderService(f.Db, f.Revisions);
        var saved = await service.SaveDraftAsync(1, project.ID, vo.ID, "Change", null, null,
            new List<VoLineInput> { new() { Kind = "Adjust", BoqItemId = existingId, Quantity = 20m, UnitPrice = 8m } }, 7);
        Assert.True(saved.ok, saved.error);

        var result = await service.ApproveAsync(1, vo.ID, 9);

        Assert.False(result.ok);
        Assert.Contains("reason", result.error!, StringComparison.OrdinalIgnoreCase);
        await using var verify = f.NewContext();
        Assert.Equal("Draft", (await verify.VariationOrders.AsNoTracking().SingleAsync(x => x.ID == vo.ID)).Status);
        var unchanged = await verify.BoqItems.AsNoTracking().SingleAsync(x => x.ID == existingId);
        Assert.Equal(10m, unchanged.Quantity);
        Assert.Equal(5m, unchanged.UnitPrice);
        Assert.Empty(await verify.CommercialRevisions.AsNoTracking().Where(x => x.SourceVariationOrderId == vo.ID).ToListAsync());
    }
}
