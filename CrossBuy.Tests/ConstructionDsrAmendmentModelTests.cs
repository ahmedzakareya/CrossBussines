using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionDsrAmendmentModelTests
{
    private static CrossDbContext NewContext() => new(new DbContextOptionsBuilder<CrossDbContext>()
        .UseSqlite("Data Source=:memory:").Options);

    [Fact]
    public void Amendment_identity_is_unique_per_company_project_site_date_and_revision()
    {
        using var db = NewContext();
        var report = db.Model.FindEntityType(typeof(ConstructionDailySiteReport));
        Assert.NotNull(report);

        var index = Assert.Single(report!.GetIndexes(), x => x.IsUnique &&
            x.Properties.Select(p => p.Name).SequenceEqual(new[]
                { "CompanyID", "ProjectId", "SiteId", "ReportDate", "RevisionNo" }));
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Amendment_parent_must_match_company_project_site_and_report_date()
    {
        using var db = NewContext();
        var report = db.Model.FindEntityType(typeof(ConstructionDailySiteReport));
        Assert.NotNull(report);

        var relationship = Assert.Single(report!.GetForeignKeys(), x =>
            x.Properties.Any(p => p.Name == nameof(ConstructionDailySiteReport.AmendsReportId)));
        Assert.Equal(new[] { "AmendsReportId", "SiteId", "ProjectId", "CompanyID", "ReportDate" },
            relationship.Properties.Select(x => x.Name));
        Assert.Equal(new[] { "ID", "SiteId", "ProjectId", "CompanyID", "ReportDate" },
            relationship.PrincipalKey.Properties.Select(x => x.Name));
        Assert.Equal(DeleteBehavior.Restrict, relationship.DeleteBehavior);
    }
}
