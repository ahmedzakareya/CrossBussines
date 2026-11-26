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
public sealed class ConstructionR7ReservationSqlTests(UatSqlProbeFixture sql) : IAsyncLifetime
{
    private UatSqlProbeFixture.ProbeDatabase? probe;
    private ConstructionR7ReservationTests.Setup setup = null!;
    public async Task InitializeAsync()
    {
        if (!sql.Available) return;
        probe = await sql.CreateProbeDatabaseAsync("R7RESERVE");
        try
        {
            using var db = sql.ContextFor(probe, 1); await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionReservationExposureRebases; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionMaterialConsumptions; DROP TABLE dbo.ConstructionCommitmentReleases; DROP TABLE dbo.ConstructionCommitmentReservations; DROP TABLE dbo.ConstructionOrderAllocationLines; DROP TABLE dbo.ConstructionOrderAllocations;");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            foreach (var filename in new[] { "construction_r7_order_allocations.sql", "construction_r7_commitment_reservations.sql", "construction_r7_material_consumptions.sql" })
            {
                var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql", filename));
                for (var run = 0; run < 2; run++)
                    foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                        if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
            }
            setup = await ConstructionR7ReservationTests.Arrange(db);
        }
        catch (Exception original)
        {
            try { await DisposeAsync(); probe = null; } catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }
    public async Task DisposeAsync() { if (probe != null) await sql.DropProbeDatabaseAsync(probe); }

    [SkippableFact]
    public async Task Native_borrowed_reservation_and_refusal_stay_inside_the_owners_transaction()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        var service = ConstructionR7ReservationTests.Service(db);
        await using (var owner = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
        {
            Assert.True((await ConstructionR7AdmissionTransactionTests.Reserve(service, setup, true, Guid.NewGuid())).GrantsCurrentAdmission);
            Assert.Equal("BudgetHardBlock", (await ConstructionR7AdmissionTransactionTests.Reserve(service, setup, false, Guid.NewGuid())).Code);
            Assert.Same(owner, db.Database.CurrentTransaction);
            Assert.Single(await db.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
            await owner.RollbackAsync();
        }
        using var verify = sql.ContextFor(probe!, 1); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Competing_proposals_cannot_both_consume_the_same_dimension_budget()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var first = sql.ContextFor(probe!, 1); using var second = sql.ContextFor(probe!, 1);
        var requestA = Guid.NewGuid(); var requestB = Guid.NewGuid();
        var results = await Task.WhenAll(ConstructionR7ReservationTests.Service(first).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.FirstAllocation, setup.FirstVersion, requestA, "Concurrent proposal"),
            ConstructionR7ReservationTests.Service(second).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.SecondAllocation, setup.SecondVersion, requestB, "Concurrent proposal"));
        Assert.Single(results, x => x.Ok); Assert.Single(results, x => x.Code == "BudgetHardBlock");
        using var verify = sql.ContextFor(probe!, 1);
        var row = await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().SingleAsync(); Assert.Equal(60m, row.NetDocumentAmount);
        var retry = await ConstructionR7ReservationTests.Service(verify).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project,
            results[0].Ok ? setup.FirstAllocation : setup.SecondAllocation, results[0].Ok ? setup.FirstVersion : setup.SecondVersion,
            results[0].Ok ? requestA : requestB, "Concurrent proposal");
        Assert.True(retry.Ok, retry.Code); Assert.Equal(row.ID, retry.Id);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Cancellation_release_is_explicit_immutable_and_frees_only_its_own_reservation()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1); var service = ConstructionR7ReservationTests.Service(db);
        var reserved = await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.FirstAllocation, setup.FirstVersion, Guid.NewGuid(), "First reservation"); Assert.True(reserved.Ok, reserved.Code);
        await db.PurchaseOrders.Where(x => x.ID == setup.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        Assert.Equal("BudgetHardBlock", (await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.SecondAllocation, setup.SecondVersion, Guid.NewGuid(), "Not yet released")).Code);
        var request = Guid.NewGuid(); var release = await service.ReleaseCancelledAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, reserved.Id!.Value, request, "Verified cancellation"); Assert.True(release.Ok, release.Code);
        Assert.Equal(release.Id, (await service.ReleaseCancelledAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, reserved.Id.Value, request, "Verified cancellation")).Id);
        Assert.True((await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.SecondAllocation, setup.SecondVersion, Guid.NewGuid(), "Freed by explicit release")).Ok);
        var edit = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.ConstructionCommitmentReservations SET NetDocumentAmount=0")); Assert.Equal(51502, edit.Number);
        var erase = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("DELETE dbo.ConstructionCommitmentReleases")); Assert.Equal(51502, erase.Number);
        using var verify = sql.ContextFor(probe!, 1); Assert.Equal(2, await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().CountAsync()); Assert.Single(await verify.Set<ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
        Assert.Equal(4, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionCommitmentReservations'),OBJECT_ID('dbo.ConstructionCommitmentReleases')) AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
    }

    [SkippableFact]
    public async Task Late_audit_failure_leaves_no_reservation_and_same_request_can_retry()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var holder = new CompanyScopeHolder(); holder.Set(1, null); var failure = new FailAudit();
        using var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString).AddInterceptors(failure).Options, holder);
        var service = ConstructionR7ReservationTests.Service(db); var request = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.FirstAllocation, setup.FirstVersion, request, "Atomic reservation"));
        Assert.Equal("Injected reservation audit failure", error.Message);
        using (var verify = sql.ContextFor(probe!, 1)) Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        failure.Enabled = false;
        Assert.True((await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.FirstAllocation, setup.FirstVersion, request, "Atomic reservation")).Ok);
    }

    [SkippableFact]
    public async Task Failed_release_audit_keeps_the_reservation_consuming_budget_until_successful_retry()
    {
        Skip.If(!sql.Available, sql.SkipReason); int reservation;
        using (var arrange = sql.ContextFor(probe!, 1))
        {
            var saved = await ConstructionR7ReservationTests.Service(arrange).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.FirstAllocation, setup.FirstVersion, Guid.NewGuid(), "Reserve before cancellation");
            Assert.True(saved.Ok, saved.Code); reservation = saved.Id!.Value;
            await arrange.PurchaseOrders.Where(x => x.ID == setup.FirstOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Cancelled"));
        }
        var holder = new CompanyScopeHolder(); holder.Set(1, null); var failure = new FailAudit();
        using var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString).AddInterceptors(failure).Options, holder);
        var request = Guid.NewGuid(); var service = ConstructionR7ReservationTests.Service(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReleaseCancelledAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, reservation, request, "Atomic cancellation release"));
        using (var verify = sql.ContextFor(probe!, 1))
        {
            Assert.Empty(await verify.Set<ConstructionCommitmentRelease>().AsNoTracking().ToListAsync());
            Assert.Equal("BudgetHardBlock", (await ConstructionR7ReservationTests.Service(verify).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.SecondAllocation, setup.SecondVersion, Guid.NewGuid(), "Still held")).Code);
        }
        failure.Enabled = false; Assert.True((await service.ReleaseCancelledAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, reservation, request, "Atomic cancellation release")).Ok);
    }

    [SkippableFact]
    public async Task Native_source_change_and_uncovered_approved_orders_cannot_disappear_from_budget_checks()
    {
        Skip.If(!sql.Available, sql.SkipReason); using var db = sql.ContextFor(probe!, 1);
        await db.PurchaseOrders.Where(x => x.ID == setup.SecondOrder).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Approved"));
        var refusal = await ConstructionR7ReservationTests.Service(db).ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), setup.Project, setup.FirstAllocation, setup.FirstVersion, Guid.NewGuid(), "Reject stale other source");
        Assert.Equal("AllocationSourceChanged", refusal.Code);
        using var verify = sql.ContextFor(probe!, 1); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
    }

    private sealed class FailAudit : SaveChangesInterceptor
    {
        public bool Enabled = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<ConstructionAuditEntry>().Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("Injected reservation audit failure");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
}



