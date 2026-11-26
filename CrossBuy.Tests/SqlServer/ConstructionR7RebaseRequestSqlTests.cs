using System.Text.RegularExpressions;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7RebaseRequestSqlTests(UatSqlProbeFixture sql)
{
    [SkippableFact]
    public async Task Native_request_schema_reruns_and_protects_immutable_proposal_without_changing_admission()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7REBASE");
        try
        {
            using var db = sql.ContextFor(probe, 1);
            Assert.False(await db.Set<ConstructionReservationRebaseRequest>().AnyAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionReservationExposureRebases; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseDecisions; DROP TABLE dbo.ConstructionReservationRebaseRequests;");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql/construction_r7_rebase_requests.sql"));
            for (var run = 0; run < 2; run++)
                foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                    if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
            var decisionScript = await File.ReadAllTextAsync(Path.Combine(root.FullName, "CrossBuy/deploy/sql/construction_r7_rebase_decisions.sql"));
            for (var run = 0; run < 2; run++)
                foreach (var batch in Regex.Split(decisionScript, @"(?im)^\s*GO\s*\r?$"))
                    if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
            var exposureScript = await File.ReadAllTextAsync(Path.Combine(root.FullName, "CrossBuy/deploy/sql/construction_r7_exposure_rebases.sql"));
            for (var run = 0; run < 2; run++)
                foreach (var batch in Regex.Split(exposureScript, @"(?im)^\s*GO\s*\r?$"))
                    if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
            var ids = await ConstructionR7RebaseRequestTests.Seed(db);
            var service = new ConstructionReservationRebaseRequestService(db, new ConstructionR7OrderAllocationTests.Access(), new ConstructionAuditService(db));
            var actor = ConstructionR7OrderAllocationTests.Actor();
            var preview = await service.PreviewAsync(actor, ids.Project, ids.Reservation, ids.Target); Assert.True(preview.Ok, preview.Code);
            var request = Guid.NewGuid();
            var saved = await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, request, "Native rebase proposal");
            Assert.True(saved.Ok, saved.Code);
            Assert.Equal(saved.Id, (await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, request, "Native rebase proposal")).Id);
            var update = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.ConstructionReservationRebaseRequests SET Reason=N'changed' WHERE ID={saved.Id!.Value}"));
            Assert.Equal(51921, update.Number);
            var delete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE dbo.ConstructionReservationRebaseRequests WHERE ID={saved.Id!.Value}"));
            Assert.Equal(51921, delete.Number);
            var hash = new string('A', 64); var selfId = Guid.NewGuid(); var now = DateTime.UtcNow;
            var self = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.ConstructionReservationRebaseDecisions(CompanyID,ProjectId,RebaseRequestId,Outcome,RequestId,RequestHash,SourceVersion,Reason,CreatedBy,CreatedAt) VALUES(1,{ids.Project},{saved.Id.Value},N'Approved',{selfId},{hash},{preview.Version},N'self',7,{now})"));
            Assert.Equal(51925, self.Number);
            var reviewer = new BusinessContext { CompanyId = 1, EmployeeId = 9, UserId = "native-reviewer", Source = BusinessContextSource.Test };
            var reviewed = await service.DecideAsync(reviewer, ids.Project, saved.Id.Value, preview.Version!, true, Guid.NewGuid(), "Independent native review");
            Assert.True(reviewed.Ok, reviewed.Code);
            Assert.Equal("DraftSourceUnproven", (await service.ReadSourceReadinessAsync(actor, ids.Project, saved.Id.Value)).Code);
            await ConstructionR7RebaseRequestTests.AttachCreationWitness(db, ids.Reservation);
            var ready = await service.ReadSourceReadinessAsync(actor, ids.Project, saved.Id.Value);
            Assert.True(ready.Ok, ready.Code); Assert.Equal("RebaseSourceReady", ready.Code);
            using (var activationDb = sql.ContextFor(probe, 1))
            {
                var rivalActivation = new ConstructionReservationRebaseRequestService(activationDb, new ConstructionR7ExposureRebaseTests.Access(), new ConstructionAuditService(activationDb));
                var activations = await Task.WhenAll(
                    service.ActivateExposureAsync(actor, ids.Project, saved.Id.Value, ready.Version!, Guid.NewGuid(), "Native exposure activation"),
                    rivalActivation.ActivateExposureAsync(actor, ids.Project, saved.Id.Value, ready.Version!, Guid.NewGuid(), "Native exposure activation"));
                Assert.Single(activations.Where(x => x.Ok));
                Assert.Equal("RebaseAlreadyActivated", Assert.Single(activations.Where(x => !x.Ok)).Code);
            }
            using (var exposureDb = sql.ContextFor(probe, 1))
            {
                var exposure = await new ConstructionBudgetExposureService(exposureDb, new ConstructionR7ExposureRebaseTests.Access(), new ConstructionAuditService(exposureDb)).PreviewAsync(actor, ids.Project);
                Assert.True(exposure.Ok, exposure.Code);
                var row = Assert.Single(exposure.Rows!); Assert.Equal(60, row.Commitment); Assert.Equal(40, row.Remaining);
                Assert.Single(await exposureDb.Set<ConstructionReservationExposureRebase>().AsNoTracking().ToListAsync());
            }
            var alterExposure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.ConstructionReservationExposureRebases SET Reason=N'changed' WHERE ReservationId={ids.Reservation}"));
            Assert.Equal(51927, alterExposure.Number);
            var deleteExposure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE dbo.ConstructionReservationExposureRebases WHERE ReservationId={ids.Reservation}"));
            Assert.Equal(51927, deleteExposure.Number);
            var changeDecision = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.ConstructionReservationRebaseDecisions SET Outcome=N'Rejected' WHERE ID={reviewed.Id!.Value}"));
            Assert.Equal(51924, changeDecision.Number);
            var deleteDecision = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE dbo.ConstructionReservationRebaseDecisions WHERE ID={reviewed.Id!.Value}"));
            Assert.Equal(51924, deleteDecision.Number);
            var secondProposal = await service.RequestAsync(actor, ids.Project, ids.Reservation, ids.Target, preview.Version!, Guid.NewGuid(), "Concurrent review proposal"); Assert.True(secondProposal.Ok, secondProposal.Code);
            using var rivalDb = sql.ContextFor(probe, 1);
            var rival = new ConstructionReservationRebaseRequestService(rivalDb, new ConstructionR7OrderAllocationTests.Access(), new ConstructionAuditService(rivalDb));
            var rivalActor = new BusinessContext { CompanyId = 1, EmployeeId = 10, UserId = "native-rival", Source = BusinessContextSource.Test };
            var results = await Task.WhenAll(
                service.DecideAsync(reviewer, ids.Project, secondProposal.Id!.Value, preview.Version!, true, Guid.NewGuid(), "Concurrent approval"),
                rival.DecideAsync(rivalActor, ids.Project, secondProposal.Id.Value, preview.Version!, false, Guid.NewGuid(), "Concurrent rejection"));
            Assert.Single(results.Where(x => x.Ok)); Assert.Equal("RebaseAlreadyDecided", Assert.Single(results.Where(x => !x.Ok)).Code);
            using var verify = sql.ContextFor(probe, 1);
            Assert.Equal("Native rebase proposal", (await verify.Set<ConstructionReservationRebaseRequest>().AsNoTracking().SingleAsync(x => x.ID == saved.Id)).Reason);
            var original = await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync();
            Assert.NotEqual(ids.Target, original.BudgetVersionId);
            Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync());
            Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
            Assert.Equal(2, await verify.Set<ConstructionReservationRebaseDecision>().AsNoTracking().CountAsync());
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }
}



