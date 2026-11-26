using System.Text.RegularExpressions;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7RebasedPurchaseDecisionSqlTests(UatSqlProbeFixture sql)
{
    private static async Task Apply(CrossDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionPurchaseRebaseDecisions;");
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var name in new[] { "construction_r7_purchase_approvals.sql", "construction_r7_rebase_requests.sql",
            "construction_r7_rebase_decisions.sql", "construction_r7_exposure_rebases.sql", "construction_r7_purchase_rebase_requests.sql",
            "construction_r7_purchase_rebase_decisions.sql" })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql", name));
            for (var run = 0; run < 2; run++)
                foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                    if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_independent_decisions_serialize_and_preserve_once_only_exposure(bool rejectRival)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7REBPOD");
        try
        {
            using var db = sql.ContextFor(probe, 1); await Apply(db);
            var ids = await ConstructionR7RebasedPurchaseDecisionTests.Prepare(db);
            var bridge = await db.Set<ConstructionPurchaseRebaseRequest>().AsNoTracking().SingleAsync();
            var fakeId = Guid.NewGuid(); var fakeHash = new string('A', 64);
            var self = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.ConstructionPurchaseRebaseDecisions(CompanyID,ProjectId,PurchaseOrderId,RebaseRequestId,ReservationId,TargetBudgetVersionId,RequestVersion,SourceVersion,ReviewVersion,RequestId,RequestHash,Reason,CreatedBy,CreatedAt) SELECT br.CompanyID,br.ProjectId,br.PurchaseOrderId,br.ID,e.ReservationId,e.TargetBudgetVersionId,br.ReviewVersion,e.SourceVersion,{fakeHash},{fakeId},{fakeHash},N'self',q.CreatedBy,SYSUTCDATETIME() FROM dbo.ConstructionPurchaseRebaseRequests br JOIN dbo.ConstructionPurchaseApprovalRequests q ON q.ID=br.ApprovalRequestId JOIN dbo.ConstructionReservationExposureRebases e ON e.ID=br.ExposureRebaseId WHERE br.ID={bridge.ID}"));
            Assert.Equal(51935, self.Number);
            var premature = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.ConstructionPurchaseAdmissions(CompanyID,ProjectId,PurchaseOrderId,ApprovalRequestId,ReservationId,RequestId,RequestHash,BeforeVersion,ApprovedVersion,ContentHash,Reason,CreatedBy,CreatedAt) SELECT q.CompanyID,q.ProjectId,q.PurchaseOrderId,q.ID,e.ReservationId,{fakeId},{fakeHash},q.SourceVersion,{fakeHash},q.ContentHash,N'premature',8,SYSUTCDATETIME() FROM dbo.ConstructionPurchaseApprovalRequests q JOIN dbo.ConstructionPurchaseRebaseRequests br ON br.ApprovalRequestId=q.ID JOIN dbo.ConstructionReservationExposureRebases e ON e.ID=br.ExposureRebaseId WHERE br.ID={bridge.ID}"));
            Assert.Equal(51932, premature.Number);
            var actor = new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = ConstructionR7RebasedPurchaseDecisionTests.Actor(8) };
            var service = ConstructionR7PurchaseApprovalTests.Service(db, actor);
            using var rivalDb = sql.ContextFor(probe, 1);
            var rival = ConstructionR7PurchaseApprovalTests.Service(rivalDb,
                new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = ConstructionR7RebasedPurchaseDecisionTests.Actor(10) });
            var results = await Task.WhenAll(service.ApproveAsync(ids.Approval, Guid.NewGuid(), "Native independent approval"),
                rejectRival ? rival.RejectAsync(ids.Approval, "Native independent rejection") : rival.ApproveAsync(ids.Approval, Guid.NewGuid(), "Native rival approval"));
            Assert.Single(results, x => x.Ok); Assert.Equal("ApprovalAlreadyDecided", Assert.Single(results, x => !x.Ok).Code);
            using var verify = sql.ContextFor(probe, 1);
            var approved = (await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status == "Approved";
            Assert.Equal(approved ? 1 : 0, await verify.Set<ConstructionPurchaseRebaseDecision>().AsNoTracking().CountAsync());
            Assert.Equal(approved ? 1 : 0, await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().CountAsync());
            Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
            if (approved)
            {
                var decision = await verify.Set<ConstructionPurchaseRebaseDecision>().AsNoTracking().SingleAsync();
                var update = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.ConstructionPurchaseRebaseDecisions SET Reason=N'changed' WHERE ID={decision.ID}"));
                Assert.Equal(51934, update.Number);
                var delete = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"DELETE dbo.ConstructionPurchaseRebaseDecisions WHERE ID={decision.ID}"));
                Assert.Equal(51934, delete.Number);
            }
            var exposure = await new ConstructionBudgetExposureService(verify, new ConstructionR7ExposureRebaseTests.Access(),
                new ConstructionAuditService(verify)).PreviewAsync(actor.Current, ids.Project);
            Assert.True(exposure.Ok, exposure.Code); var row = Assert.Single(exposure.Rows!);
            Assert.Equal(60, row.Commitment); Assert.Equal(0, row.Actual); Assert.Equal(40, row.Remaining);
            Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
            Assert.Equal(3, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionPurchaseRebaseDecisions') AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }

    [SkippableFact]
    public async Task Native_late_source_failure_rolls_back_decision_and_admission()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7REBPOROLLBACK");
        try
        {
            using var db = sql.ContextFor(probe, 1); await Apply(db);
            var ids = await ConstructionR7RebasedPurchaseDecisionTests.Prepare(db);
            await db.Database.ExecuteSqlRawAsync("CREATE OR ALTER TRIGGER dbo.TR_RebasedDecisionTestFailure ON dbo.PurchaseOrders AFTER UPDATE AS BEGIN IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON d.ID=i.ID WHERE i.Status=N'Approved' AND d.Status=N'Draft') THROW 51936,'deliberate native rebased transition failure',1; END;");
            var service = ConstructionR7PurchaseApprovalTests.Service(db,
                new ConstructionR7PurchaseApprovalTests.ActorAccessor { Current = ConstructionR7RebasedPurchaseDecisionTests.Actor(8) });
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => service.ApproveAsync(ids.Approval, Guid.NewGuid(), "Must roll back"));
            Assert.Contains("deliberate native rebased transition failure", failure.ToString());
            using var verify = sql.ContextFor(probe, 1);
            Assert.Empty(await verify.Set<ConstructionPurchaseRebaseDecision>().AsNoTracking().ToListAsync());
            Assert.Empty(await verify.Set<ConstructionPurchaseAdmission>().AsNoTracking().ToListAsync());
            Assert.Equal("Pending", (await verify.InventoryApprovals.AsNoTracking().SingleAsync()).Status);
            Assert.Equal("Draft", (await verify.PurchaseOrders.AsNoTracking().SingleAsync(x => x.ID == ids.Order)).Status);
            Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }
}
