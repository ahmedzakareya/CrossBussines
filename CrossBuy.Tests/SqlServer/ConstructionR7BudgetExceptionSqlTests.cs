using System.Text.RegularExpressions;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7BudgetExceptionSqlTests(UatSqlProbeFixture sql) : IAsyncLifetime
{
    private UatSqlProbeFixture.ProbeDatabase? probe;
    private ConstructionR7ReservationTests.Setup setup = null!;
    private ConstructionBudgetExceptionResult request = null!;
    public async Task InitializeAsync()
    {
        if (!sql.Available) return;
        probe = await sql.CreateProbeDatabaseAsync("R7EXCEPTION");
        try
        {
            using var db = sql.ContextFor(probe, 1); await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionReservationExposureRebases; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionMaterialConsumptions; DROP TABLE dbo.ConstructionBudgetExceptionConsumptions; DROP TABLE dbo.ConstructionBudgetExceptionDecisions; DROP TABLE dbo.ConstructionBudgetExceptionRequests; DROP TABLE dbo.ConstructionCommitmentReleases; DROP TABLE dbo.ConstructionCommitmentReservations; DROP TABLE dbo.ConstructionOrderAllocationLines; DROP TABLE dbo.ConstructionOrderAllocations;");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            foreach (var name in new[] { "construction_r7_order_allocations.sql", "construction_r7_commitment_reservations.sql", "construction_r7_budget_exceptions.sql", "construction_r7_material_consumptions.sql" })
            {
                var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql", name));
                for (var run = 0; run < 2; run++)
                    foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                        if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
            }
            var arranged = await ConstructionR7BudgetExceptionTests.Arrange(db); setup = arranged.Setup; request = arranged.Request;
        }
        catch (Exception original)
        {
            try { await DisposeAsync(); probe = null; } catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }
    public async Task DisposeAsync() { if (probe != null) await sql.DropProbeDatabaseAsync(probe); }
    private async Task Approve()
    {
        using var db = sql.ContextFor(probe!, 1);
        var result = await ConstructionR7BudgetExceptionTests.Service(db).ApproveAsync(ConstructionR7BudgetExceptionTests.Approver,
            setup.Project, request.Id!.Value, request.Version!, Guid.NewGuid(), "Independent native review");
        Assert.True(result.Ok, result.Code);
    }

    [SkippableFact]
    public async Task Native_finance_screen_exposes_exact_preview_and_competing_independent_decisions_commit_once()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var first = sql.ContextFor(probe!, 1); using var second = sql.ContextFor(probe!, 1);
        var service = ConstructionR7BudgetExceptionTests.Service(first);
        var page = await service.ReadPageAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, setup.SecondOrder);
        Assert.NotNull(page); Assert.Equal(60m, page!.Preview!.ProposedAmount); Assert.Equal(20m, page.Preview.Overrun);
        Assert.True(Assert.Single(page.History).CanApprove);
        var a = service.ApproveAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, request.Id!.Value, request.Version!, Guid.NewGuid(), "Native finance approval");
        var b = ConstructionR7BudgetExceptionTests.Service(second).RejectAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, request.Id.Value, request.Version!, Guid.NewGuid(), "Native finance refusal");
        var results = await Task.WhenAll(a, b);
        Assert.Single(results, x => x.Ok); Assert.Single(results, x => !x.Ok);
        Assert.Contains(results.Single(x => !x.Ok).Code, new[] { "AlreadyDecided", "ConcurrencyConflict" });
        using var verify = sql.ContextFor(probe!, 1);
        Assert.Single(await verify.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().ToListAsync());
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Native_finance_screen_submission_refuses_changed_policy_and_retains_historical_request()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        var service = ConstructionR7BudgetExceptionTests.Service(db);
        var page = (await service.ReadPageAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.SecondOrder))!;
        Assert.True(page.Preview!.Ok, page.Preview.Code);
        var token = Guid.NewGuid().ToByteArray();
        await db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ConcurrencyToken, token));
        var result = await service.RequestCurrentAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, page.AllocationId!.Value,
            page.SourceVersion!, page.Preview.Version!, Guid.NewGuid(), "Stale native browser witness");
        Assert.Equal("ExceptionSourceChanged", result.Code);
        var refreshed = (await service.ReadPageAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, setup.SecondOrder))!;
        Assert.False(refreshed.Preview!.Ok); Assert.Equal("Stale", Assert.Single(refreshed.History).State);
        Assert.False(Assert.Single(refreshed.History).CanApprove); Assert.True(Assert.Single(refreshed.History).CanReject);
        using var verify = sql.ContextFor(probe!, 1);
        Assert.Single(await verify.Set<ConstructionBudgetExceptionRequest>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Native_competing_consumers_commit_one_reservation_and_immutable_exact_approval_history()
    {
        Skip.If(!sql.Available, sql.SkipReason); await Approve();
        using var first = sql.ContextFor(probe!, 1); using var second = sql.ContextFor(probe!, 1);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var results = await Task.WhenAll(
            ConstructionR7BudgetExceptionTests.Service(first).ConsumeAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, request.Id!.Value, a, "Concurrent approved reservation"),
            ConstructionR7BudgetExceptionTests.Service(second).ConsumeAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, request.Id!.Value, b, "Concurrent approved reservation"));
        Assert.Single(results, x => x.GrantsCurrentAdmission); Assert.Single(results, x => x.Code == "ExceptionAlreadyConsumed");
        using var verify = sql.ContextFor(probe!, 1);
        Assert.Equal(2, await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().CountAsync());
        var consumption = await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().SingleAsync();
        var reservation = await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync(x => x.ID == consumption.ReservationId);
        Assert.Equal(setup.SecondOrder, reservation.PurchaseOrderId); Assert.Equal(60m, reservation.NetDocumentAmount);
        var retry = await ConstructionR7BudgetExceptionTests.Service(verify).ConsumeAsync(ConstructionR7BudgetExceptionTests.Approver,
            setup.Project, request.Id!.Value, results[0].GrantsCurrentAdmission ? a : b, "Concurrent approved reservation");
        Assert.Equal("AlreadyReserved", retry.Code); Assert.False(retry.GrantsCurrentAdmission);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
        var exposure = await ConstructionR7BudgetExposureTests.Reader(verify).PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project);
        Assert.True(exposure.Ok, exposure.Code); Assert.Equal(-20m, Assert.Single(exposure.Rows!).Remaining);
        var editRequest = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlRawAsync("UPDATE dbo.ConstructionBudgetExceptionRequests SET Reason=N'changed'")); Assert.Equal(51804, editRequest.Number);
        var editDecision = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlRawAsync("UPDATE dbo.ConstructionBudgetExceptionDecisions SET Reason=N'changed'")); Assert.Equal(51804, editDecision.Number);
        var eraseConsumption = await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlRawAsync("DELETE dbo.ConstructionBudgetExceptionConsumptions")); Assert.Equal(51804, eraseConsumption.Number);
        Assert.Equal(6, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionBudgetExceptionRequests'),OBJECT_ID('dbo.ConstructionBudgetExceptionDecisions'),OBJECT_ID('dbo.ConstructionBudgetExceptionConsumptions')) AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
    }

    [SkippableFact]
    public async Task Native_constraints_refuse_self_approval_foreign_company_and_unapproved_exception_reservation()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        var id = Guid.NewGuid(); var now = DateTime.UtcNow; var hash = new string('A', 64);
        var self = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.ConstructionBudgetExceptionDecisions(CompanyID,ProjectId,ExceptionRequestId,Outcome,RequestId,RequestHash,Reason,CreatedBy,CreatedAt) VALUES(1,{setup.Project},{request.Id!.Value},N'Approved',{id},{hash},N'self',7,{now})")); Assert.Equal(51802, self.Number);
        var foreign = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.ConstructionBudgetExceptionDecisions(CompanyID,ProjectId,ExceptionRequestId,Outcome,RequestId,RequestHash,Reason,CreatedBy,CreatedAt) VALUES(2,{setup.Project},{request.Id!.Value},N'Approved',{id},{hash},N'foreign',8,{now})")); Assert.Equal(547, foreign.Number);
        var noApproval = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.ConstructionCommitmentReservations(CompanyID,ProjectId,PurchaseOrderId,AllocationId,BudgetVersionId,CurrencyId,NetDocumentAmount,RequestId,RequestHash,SourceVersion,PolicyMode,DecisionCode,Reason,CreatedBy,CreatedAt) SELECT CompanyID,ProjectId,PurchaseOrderId,AllocationId,BudgetVersionId,CurrencyId,ProposedAmount,{id},{hash},SourceVersion,N'ApprovalRequired',N'ApprovedBudgetException',N'unapproved',8,{now} FROM dbo.ConstructionBudgetExceptionRequests WHERE ID={request.Id!.Value}")); Assert.Equal(51803, noApproval.Number);
        using var verify = sql.ContextFor(probe!, 1); Assert.Empty(await verify.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().ToListAsync()); Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Native_policy_revision_after_approval_refuses_stale_consumption_without_new_business_effects()
    {
        Skip.If(!sql.Available, sql.SkipReason); await Approve();
        using var db = sql.ContextFor(probe!, 1); var token = Guid.NewGuid().ToByteArray();
        await db.Set<ConstructionBudgetPolicy>().ExecuteUpdateAsync(x => x.SetProperty(p => p.ConcurrencyToken, token));
        var result = await ConstructionR7BudgetExceptionTests.Service(db).ConsumeAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, request.Id!.Value, Guid.NewGuid(), "Stale approved source");
        Assert.Equal("ExceptionSourceChanged", result.Code);
        using var verify = sql.ContextFor(probe!, 1); Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Native_late_audit_failure_rolls_back_reservation_and_consumption_and_same_request_can_retry()
    {
        Skip.If(!sql.Available, sql.SkipReason); await Approve();
        var scope = new CompanyScopeHolder(); scope.Set(1, null); var failure = new FailAudit();
        using var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString).AddInterceptors(failure).Options, scope);
        var id = Guid.NewGuid(); var service = ConstructionR7BudgetExceptionTests.Service(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConsumeAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, request.Id!.Value, id, "Atomic exception use"));
        Assert.Equal("Injected exception audit failure", error.Message);
        using (var verify = sql.ContextFor(probe!, 1))
        {
            Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
            Assert.Empty(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
            Assert.Single(await verify.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().ToListAsync());
        }
        failure.Enabled = false;
        var retry = await service.ConsumeAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, request.Id!.Value, id, "Atomic exception use");
        Assert.True(retry.GrantsCurrentAdmission, retry.Code);
    }
    [SkippableFact]
    public async Task Native_borrowed_consumption_late_failure_preserves_saved_owner_work_and_retries()
    {
        Skip.If(!sql.Available, sql.SkipReason); await Approve();
        var scope = new CompanyScopeHolder(); scope.Set(1, null); var failure = new FailAudit { Enabled = false };
        using var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString).AddInterceptors(failure).Options, scope);
        var id = Guid.NewGuid(); var service = ConstructionR7BudgetExceptionTests.Service(db);
        await using (var owner = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            var project = await db.Projects.SingleAsync(); project.Name = "Native owner work survives"; await db.SaveChangesAsync(); failure.Enabled = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConstructionR7AdmissionTransactionTests.Consume(service, setup, request.Id!.Value, id));
            Assert.Equal("Injected exception audit failure", error.Message); Assert.Same(owner, db.Database.CurrentTransaction);
            Assert.Equal(EntityState.Unchanged, db.Entry(project).State); Assert.Equal("Native owner work survives", project.Name);
            Assert.False(db.ChangeTracker.HasChanges());
            Assert.Single(await db.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
            Assert.Empty(await db.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
            await owner.CommitAsync();
        }
        using (var verify = sql.ContextFor(probe!, 1))
        {
            Assert.Equal("Native owner work survives", (await verify.Projects.AsNoTracking().SingleAsync()).Name);
            Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
            Assert.Empty(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
        }
        failure.Enabled = false;
        var retry = await service.ConsumeAsync(ConstructionR7BudgetExceptionTests.Approver, setup.Project, request.Id!.Value, id, "Composed exception");
        Assert.True(retry.GrantsCurrentAdmission, retry.Code);
        using var final = sql.ContextFor(probe!, 1); Assert.Equal(2, await final.Set<ConstructionCommitmentReservation>().AsNoTracking().CountAsync());
        Assert.Single(await final.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Native_borrowed_consumption_requires_serializable_owner_and_cannot_commit_it()
    {
        Skip.If(!sql.Available, sql.SkipReason); await Approve(); using var db = sql.ContextFor(probe!, 1);
        var service = ConstructionR7BudgetExceptionTests.Service(db); var id = Guid.NewGuid();
        await using (var weak = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted))
        {
            Assert.Equal("SerializableAdmissionTransactionRequired", (await ConstructionR7AdmissionTransactionTests.Consume(service, setup, request.Id!.Value, id)).Code);
            Assert.Same(weak, db.Database.CurrentTransaction); await weak.CommitAsync();
        }
        await using (var owner = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            Assert.True((await ConstructionR7AdmissionTransactionTests.Consume(service, setup, request.Id!.Value, id)).GrantsCurrentAdmission);
            Assert.Same(owner, db.Database.CurrentTransaction); await owner.RollbackAsync();
        }
        using var verify = sql.ContextFor(probe!, 1); Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
    }

    private sealed class FailAudit : SaveChangesInterceptor
    {
        public bool Enabled = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<ConstructionAuditEntry>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Injected exception audit failure");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
}



