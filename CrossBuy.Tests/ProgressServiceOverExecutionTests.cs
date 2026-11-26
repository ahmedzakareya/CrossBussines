using CrossBuy.BL;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ProgressServiceOverExecutionTests
{
    [Fact]
    public async Task Confirmation_refuses_quantity_above_current_boq_and_keeps_draft_evidence()
    {
        using var fixture = new ConstructionTestFixture();
        var project = await fixture.SeedProjectAsync();
        var saved = await fixture.Boq.SaveLinesAsync(1, project.ID, new List<BoqLineInput>
        {
            new() { Code = "A-01", Description = "Concrete", Unit = "m3", Quantity = 10m, UnitPrice = 25m }
        }, actorEmployeeId: 7, reason: "test baseline");
        Assert.True(saved.ok, saved.error);
        var itemId = (await fixture.ActiveLineIdsByCodeAsync(project.ID))["A-01"];
        var measurement = new ProjectProgress
        {
            CompanyID = 1,
            ProjectId = project.ID,
            MeasurementNo = 1,
            MeasurementDate = DateTime.Today,
            Status = "Draft",
            Lines = { new ProjectProgressLine { BoqItemId = itemId, CumulativeQty = 11m } }
        };
        fixture.Db.ProjectProgresses.Add(measurement);
        await fixture.Db.SaveChangesAsync();

        var result = await new ProgressService(fixture.Db).ConfirmAsync(1, measurement.ID);

        Assert.False(result.ok);
        Assert.False(string.IsNullOrWhiteSpace(result.error));
        Assert.Equal("Draft", await fixture.Db.ProjectProgresses
            .Where(x => x.ID == measurement.ID).Select(x => x.Status).SingleAsync());
        Assert.Equal(11m, await fixture.Db.ProjectProgressLines
            .Where(x => x.ProgressId == measurement.ID).Select(x => x.CumulativeQty).SingleAsync());
    }
}
