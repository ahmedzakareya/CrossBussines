using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7PartialInvoiceModelTests
{
    [Fact]
    public void Partial_provenance_package_follows_its_authored_prerequisites_and_keeps_exact_bytes()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "CrossBuy/deploy/sql/manifest.json")));
        var rows = manifest.RootElement.GetProperty("scripts").EnumerateArray().ToArray();
        var partial = rows.Single(x => x.GetProperty("path").GetString() == "CrossBuy/deploy/sql/construction_r7_partial_invoice_provenance.sql");
        foreach (var name in new[] { "construction_r7_procurement_provenance.sql", "construction_r7_invoice_line_provenance.sql" })
        {
            var prerequisite = rows.Single(x => x.GetProperty("path").GetString() == "CrossBuy/deploy/sql/" + name);
            Assert.True(partial.GetProperty("rank").GetInt32() > prerequisite.GetProperty("rank").GetInt32());
        }
        var bytes = File.ReadAllBytes(Path.Combine(root.FullName, partial.GetProperty("path").GetString()!));
        Assert.Equal(partial.GetProperty("rawSha256").GetString(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), ignoreCase: true);
    }

    [Fact]
    public void Partial_lineage_does_not_make_optional_non_order_receipt_links_required()
    {
        using var fixture = new ConstructionTestFixture();
        Assert.True(fixture.Db.Model.FindEntityType(typeof(GoodsReceipt))!.FindProperty(nameof(GoodsReceipt.PurchaseOrderId))!.IsNullable);
        Assert.True(fixture.Db.Model.FindEntityType(typeof(GoodsReceiptLine))!.FindProperty(nameof(GoodsReceiptLine.PurchaseOrderLineId))!.IsNullable);
        var slice = fixture.Db.Model.FindEntityType(typeof(ConstructionPartialInvoiceSlice))!;
        Assert.Equal(28, slice.FindProperty(nameof(ConstructionPartialInvoiceSlice.Quantity))!.GetPrecision());
        Assert.Equal(8, slice.FindProperty(nameof(ConstructionPartialInvoiceSlice.Quantity))!.GetScale());
        Assert.All(slice.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
    }

    [Fact]
    public async Task Executing_company_scope_filters_both_headers_and_slices_in_a_fresh_context()
    {
        using var fixture = new ConstructionTestFixture();
        for (var company = 1; company <= 2; company++)
        {
            var holder = new CompanyScopeHolder(); holder.Set(company, null);
            var options = new DbContextOptionsBuilder<CrossDbContext>().UseSqlite(fixture.Db.Database.GetDbConnection()).Options;
            await using var db = new CrossDbContext(options, holder);
            var header = new ConstructionPartialInvoice { CompanyID = company, ProjectId = 10, PurchaseOrderId = 20,
                PurchaseInvoiceId = 30 + company, JournalEntryId = 40 + company, RequestId = Guid.NewGuid(),
                RequestHash = new string('A', 64), SourceVersion = new string('B', 64), CreatedBy = 7, CreatedAt = DateTime.UtcNow };
            db.Add(header); await db.SaveChangesAsync();
            db.Add(new ConstructionPartialInvoiceSlice { HeaderId = header.ID, CompanyID = company, ProjectId = header.ProjectId,
                PurchaseOrderId = header.PurchaseOrderId, PurchaseInvoiceId = header.PurchaseInvoiceId, JournalEntryId = header.JournalEntryId,
                PurchaseOrderLineId = 21, GoodsReceiptId = 50, GoodsReceiptLineId = 51, PurchaseInvoiceLineId = 60 + company,
                JournalEntryLineId = 70 + company, Quantity = .25m });
            await db.SaveChangesAsync();
        }
        // SQLite fixture deliberately disables unrelated FKs; these assertions prove scope filters only.
        await using var read = fixture.NewContext();
        Assert.Equal(1, (await read.Set<ConstructionPartialInvoice>().AsNoTracking().SingleAsync()).CompanyID);
        Assert.Equal(1, (await read.Set<ConstructionPartialInvoiceSlice>().AsNoTracking().SingleAsync()).CompanyID);
        var unresolved = new CompanyScopeHolder();
        await using var denied = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>()
            .UseSqlite(fixture.Db.Database.GetDbConnection()).Options, unresolved);
        Assert.Empty(await denied.Set<ConstructionPartialInvoice>().AsNoTracking().ToListAsync());
        Assert.Empty(await denied.Set<ConstructionPartialInvoiceSlice>().AsNoTracking().ToListAsync());
    }
}
