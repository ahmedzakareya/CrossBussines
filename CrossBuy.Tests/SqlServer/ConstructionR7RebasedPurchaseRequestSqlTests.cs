using System.Text.RegularExpressions;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7RebasedPurchaseRequestSqlTests(UatSqlProbeFixture sql)
{
    [SkippableFact]
    public async Task Native_request_bridge_is_rerunnable_immutable_and_serializes_duplicate_pending_requests()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7REBPOREQ");
        try
        {
            using var db = sql.ContextFor(probe, 1);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionPurchaseRebaseDecisions; DROP TABLE dbo.ConstructionPurchaseRebaseRequests;");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            foreach (var name in new[] { "construction_r7_purchase_approvals.sql", "construction_r7_rebase_requests.sql",
                "construction_r7_rebase_decisions.sql", "construction_r7_exposure_rebases.sql", "construction_r7_purchase_rebase_requests.sql" })
            {
                var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql", name));
                for (var run = 0; run < 2; run++)
                    foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                        if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
            }
            var ids = await ConstructionR7RebasedPurchaseRequestTests.Prepare(db);
            var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor();
            var service = ConstructionR7PurchaseApprovalTests.Service(db, actor);
            var quote = await service.PreviewRebasedRequestAsync(ids.Project, ids.Order); Assert.True(quote.Ok, quote.Code);
            using var rivalDb = sql.ContextFor(probe, 1);
            var rival = ConstructionR7PurchaseApprovalTests.Service(rivalDb, new ConstructionR7PurchaseApprovalTests.ActorAccessor());
            var requestId = Guid.NewGuid();
            var results = await Task.WhenAll(
                service.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, requestId, "Native request"),
                rival.RequestRebasedAsync(ids.Project, ids.Order, quote.Version!, Guid.NewGuid(), "Native rival"));
            var winner = Assert.Single(results.Where(x => x.Ok));
            Assert.Equal("ApprovalAlreadyPending", Assert.Single(results.Where(x => !x.Ok)).Code);
            using var verify = sql.ContextFor(probe, 1);
            var bridge = await verify.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking().SingleAsync();
            Assert.Equal("Pending", (await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
            Assert.Single(await verify.Set<ConstructionPurchaseApprovalRequest>().AsNoTracking().ToListAsync());
            Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
            Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
            Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
            var update = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.ConstructionPurchaseRebaseRequests SET CreatedBy=99 WHERE ID={bridge.ID}"));
            Assert.Equal(51930, update.Number);
            var delete = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"DELETE dbo.ConstructionPurchaseRebaseRequests WHERE ID={bridge.ID}"));
            Assert.Equal(51930, delete.Number);
            var admissionId = Guid.NewGuid(); var admissionHash = new string('A', 64);
            var gated = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.ConstructionPurchaseAdmissions(CompanyID,ProjectId,PurchaseOrderId,ApprovalRequestId,ReservationId,RequestId,RequestHash,BeforeVersion,ApprovedVersion,ContentHash,Reason,CreatedBy,CreatedAt) SELECT q.CompanyID,q.ProjectId,q.PurchaseOrderId,q.ID,e.ReservationId,{admissionId},{admissionHash},q.SourceVersion,{admissionHash},q.ContentHash,N'Premature admission',11,SYSUTCDATETIME() FROM dbo.ConstructionPurchaseApprovalRequests q JOIN dbo.ConstructionPurchaseRebaseRequests b ON b.ApprovalRequestId=q.ID JOIN dbo.ConstructionReservationExposureRebases e ON e.ID=b.ExposureRebaseId WHERE b.ID={bridge.ID}"));
            Assert.Equal(51932, gated.Number);
            Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
            Assert.Equal("Draft", (await verify.PurchaseOrders.AsNoTracking().SingleAsync(x => x.ID == ids.Order)).Status);
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }
}
