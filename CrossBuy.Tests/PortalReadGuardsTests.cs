using CrossBuy.BL;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests;

public class PortalReadGuardsTests
{
    [Theory]
    [InlineData("Employee", "Internal", 100)]
    [InlineData("SalesInvoice", "Internal", 100)]
    [InlineData("Customer", "Confidential", 100)]
    [InlineData("Customer", "Restricted", 100)]
    [InlineData("Customer", "System", 100)]
    [InlineData("Customer", "unknown", 100)]
    [InlineData("Customer", "Internal", 200)]
    public async Task A_forged_publication_cannot_bypass_external_read_guards(string family, string tier, int owner)
    {
        using var h = new DocumentPortalHost();
        var id = h.SeedDocument(1);
        var doc = h.Db.Set<PlatformDocument>().Single(x => x.Id == id);
        doc.EntityType = family; doc.Confidentiality = tier; doc.EntityId = owner;
        h.Db.SaveChanges();
        var pub = h.Publish(id, 100);
        h.ActAs();
        Assert.Empty(await h.Service().ListAsync());
        Assert.Null(await h.Service().OpenAsync(pub));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Current_version_must_belong_to_the_same_document_and_company(bool foreignCompany)
    {
        using var h = new DocumentPortalHost();
        var id = h.SeedDocument(1);
        var doc = h.Db.Set<PlatformDocument>().Single(x => x.Id == id);
        var v = h.Db.Set<PlatformDocumentVersion>().Single(x => x.Id == doc.CurrentVersionId);
        if (foreignCompany) v.CompanyID = 2; else v.DocumentId = id + 99;
        h.Db.SaveChanges();
        var pub = h.Publish(id, 100);
        h.ActAs();
        Assert.Empty(await h.Service().ListAsync());
        Assert.Null(await h.Service().OpenAsync(pub));
    }

}
