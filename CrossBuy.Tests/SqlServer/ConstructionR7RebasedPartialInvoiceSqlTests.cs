using System.Reflection;
using System.Text.RegularExpressions;
using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7RebasedPartialInvoiceSqlTests(UatSqlProbeFixture sql)
{
    private static async Task PartialSchema(CrossDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionPartialInvoiceSlices; DROP TABLE dbo.ConstructionPartialInvoices;");
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql/construction_r7_partial_invoice_provenance.sql"));
        foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
            if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
    }

    private static ConstructionPartialInvoiceService Writer(IServiceProvider services, bool purchase = true, bool project = true, bool failAudit = false)
    {
        var db = services.GetRequiredService<CrossDbContext>();
        IConstructionAuditService audit = new ConstructionAuditService(db);
        if (failAudit)
        {
            audit = DispatchProxy.Create<IConstructionAuditService, ConstructionR7PartialInvoiceWriterSqlTests.FailedAudit>();
            ((ConstructionR7PartialInvoiceWriterSqlTests.FailedAudit)audit).Context = db;
        }
        return new(db, new ConstructionR7OrderAllocationTests.Access(project),
            new ConstructionR7PurchaseApprovalTests.InventoryAuthority { Purchase = purchase }, audit,
            services.GetRequiredService<ICurrencyRounding>(), services.GetRequiredService<IPayableService>());
    }

    [SkippableTheory]
    [InlineData("split")]
    [InlineData("interleaved")]
    [InlineData("replay")]
    [InlineData("overbill")]
    [InlineData("stale")]
    [InlineData("purchase-denied")]
    [InlineData("project-denied")]
    [InlineData("policy")]
    [InlineData("late-audit")]
    [InlineData("concurrent")]
    public async Task Real_rebased_partial_invoice_reconciles_and_keeps_one_commitment(string mode)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7REBPI");
        try
        {
            ConstructionR7PurchaseApprovalTests.Setup s;
            using (var db = sql.ContextFor(probe, 1))
            {
                await PartialSchema(db);
                await ConstructionR7RebasedReceiptSqlTests.Apply(db);
                s = await ConstructionR7RebasedReceiptSqlTests.Prepare(db);
            }
            var receiving = new ConstructionR7RebasedReceiptSqlTests(sql);
            async Task<int> Receive(decimal qty)
            {
                using var graph = receiving.Graph(probe, new());
                var result = await graph.GetRequiredService<IProcurementService>().CreateReceiptAsync(1, s.Seed.Vendor,
                    s.Seed.Warehouse, s.Order, DateTime.Today, "Rebased partial invoice receiving",
                    new() { new() { ItemId = s.Seed.Item, Qty = qty, UoMId = s.Seed.Unit, UnitCost = 10m,
                        PurchaseOrderLineId = s.Lines.Single() } }, "rebased-invoice-test", s.Seed.Currency, 1m);
                Assert.True(result.ok, result.error);
                using var read = sql.ContextFor(probe, 1);
                return await read.GoodsReceiptLines.AsNoTracking().OrderByDescending(x => x.ID).Select(x => x.ID).FirstAsync();
            }
            var receipt = await Receive(mode == "interleaved" ? .4m : 1m);
            var actor = ConstructionR7RebasedPurchaseDecisionTests.Actor(8);
            using var graph = receiving.Graph(probe, new());
            var writer = Writer(graph, mode != "purchase-denied", mode != "project-denied", mode == "late-audit");
            var request = Guid.NewGuid();
            var firstQty = mode is "split" or "interleaved" or "stale" ? .4m : mode == "overbill" ? 1.1m : 1m;
            ConstructionInvoiceQuantityRequest[] input = [new(receipt, firstQty)];
            var preview = await writer.PreviewAsync(actor, s.Seed.Project, s.Order, request, input);
            var invoices = 0;
            if (mode is "purchase-denied" or "project-denied" or "overbill")
            {
                Assert.False(preview.Ok);
                var page = await Writer(graph).ReadAsync(actor, s.Seed.Project, s.Order); Assert.True(page.Ok, page.Code);
                var refused = await writer.IssueAsync(actor, s.Seed.Project, s.Order, DateTime.Today, request, page.Version!, input, "Unauthorized or excessive invoice");
                Assert.False(refused.Ok);
                if (mode != "overbill") Assert.Equal("AccessDenied", refused.Code);
            }
            else
            {
                Assert.True(preview.Ok, preview.Code);
                if (mode == "policy")
                {
                    using var changed = sql.ContextFor(probe, 1);
                    await changed.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.Mode, "Warning"));
                    var refused = await writer.IssueAsync(actor, s.Seed.Project, s.Order, DateTime.Today, request, preview.Version!, input, "First rebased invoice");
                    Assert.False(refused.Ok);
                }
                else if (mode == "late-audit")
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => writer.IssueAsync(actor, s.Seed.Project, s.Order,
                        DateTime.Today, request, preview.Version!, input, "First rebased invoice"));
                }
                else
                {
                    Task<ConstructionPartialInvoiceResult> Issue(ConstructionPartialInvoiceService w) => w.IssueAsync(actor,
                        s.Seed.Project, s.Order, DateTime.Today, request, preview.Version!, input, "First rebased invoice");
                    if (mode == "concurrent")
                    {
                        using var other = receiving.Graph(probe, new());
                        var results = await Task.WhenAll(Issue(writer), Issue(Writer(other)));
                        Assert.All(results, x => Assert.True(x.Ok, x.Code));
                        Assert.Single(results.Where(x => x.Code == "Issued"));
                        Assert.Single(results.Where(x => x.Code == "AlreadyIssued"));
                        Assert.Equal(results[0].InvoiceId, results[1].InvoiceId);
                    }
                    else { var issued = await Issue(writer); Assert.True(issued.Ok, issued.Code); }
                    invoices = 1;
                    var retry = await Issue(writer); Assert.True(retry.Ok, retry.Code); Assert.Equal("AlreadyIssued", retry.Code);
                    var conflict = await writer.IssueAsync(actor, s.Seed.Project, s.Order, DateTime.Today, request, preview.Version!, input, "Changed reason");
                    Assert.False(conflict.Ok); Assert.Equal("IdempotencyConflict", conflict.Code);
                    var page = await writer.ReadAsync(actor, s.Seed.Project, s.Order); Assert.True(page.Ok, page.Code);
                    Assert.Equal(firstQty, page.Receipts!.Sum(x => x.PreviouslyBilled));
                    if (mode is "split" or "interleaved" or "stale")
                    {
                        var nextReceipt = mode == "interleaved" ? await Receive(.6m) : receipt;
                        ConstructionInvoiceQuantityRequest[] rest = [new(nextReceipt, .6m)];
                        var nextId = Guid.NewGuid();
                        if (mode == "stale")
                        {
                            var stale = await writer.IssueAsync(actor, s.Seed.Project, s.Order, DateTime.Today, nextId, preview.Version!, rest, "Remainder");
                            Assert.False(stale.Ok); Assert.Equal("ConcurrencyConflict", stale.Code);
                        }
                        var next = await writer.PreviewAsync(actor, s.Seed.Project, s.Order, nextId, rest); Assert.True(next.Ok, next.Code);
                        var issued = await writer.IssueAsync(actor, s.Seed.Project, s.Order, DateTime.Today, nextId, next.Version!, rest, "Remainder");
                        Assert.True(issued.Ok, issued.Code); invoices = 2;
                    }
                }
            }
            using var verify = sql.ContextFor(probe, 1);
            Assert.Equal(invoices, await verify.PurchaseInvoices.AsNoTracking().CountAsync());
            Assert.Equal(invoices, await verify.Set<ConstructionPartialInvoice>().AsNoTracking().CountAsync(x => x.IsSealed));
            Assert.Equal(invoices, await verify.JournalEntries.AsNoTracking().CountAsync(x => x.SourceType == "PurchaseInvoice"));
            Assert.Equal(invoices, await verify.BusinessEvents.AsNoTracking().CountAsync(x => x.EventType == PurchaseInvoiceEvents.Created));
            Assert.Equal(mode == "interleaved" ? 2 : 1, await verify.StockMovements.AsNoTracking().CountAsync());
            Assert.Equal(invoices > 0 ? 10m : 0m, await verify.PurchaseInvoices.AsNoTracking().SumAsync(x => x.SubTotal));
            Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.Set<ConstructionCommitmentRelease>().AsNoTracking().ToArrayAsync());
            Assert.Empty(await verify.Set<CrossBuy.Models.Context.Inventory.PurchaseOrderInvoiceLink>().AsNoTracking().ToArrayAsync());
            Assert.All(await verify.GoodsReceipts.AsNoTracking().ToArrayAsync(), x => Assert.Null(x.InvoiceId));
            var whole = await graph.GetRequiredService<IProcurementService>().ConvertToInvoiceAsync(1, s.Order, "No legacy conversion");
            Assert.False(whole.ok);
            if (mode != "policy")
            {
                var exposure = await new ConstructionBudgetExposureService(verify, new ConstructionR7ExposureRebaseTests.Access(), new ConstructionAuditService(verify))
                    .PreviewAsync(actor, s.Seed.Project);
                Assert.True(exposure.Ok, exposure.Code); var row = Assert.Single(exposure.Rows!);
                Assert.Equal(10m, row.Commitment); Assert.Equal(0m, row.Actual); Assert.Equal(row.Budget - 10m, row.Remaining);
            }
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }
}
