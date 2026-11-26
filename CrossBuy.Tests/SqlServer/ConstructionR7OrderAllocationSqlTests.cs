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
public sealed class ConstructionR7OrderAllocationSqlTests(UatSqlProbeFixture sql) : IAsyncLifetime
{
    private UatSqlProbeFixture.ProbeDatabase? probe;
    private (int Project, int Order, int Line, int Node, int Code) ids;
    public async Task InitializeAsync()
    {
        if (!sql.Available) return;
        probe = await sql.CreateProbeDatabaseAsync("R7ALLOC");
        try
        {
            using var db = sql.ContextFor(probe, 1);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionOrderAllocationLines; DROP TABLE dbo.ConstructionOrderAllocations;");
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            var script = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "CrossBuy/deploy/sql/construction_r7_order_allocations.sql"));
            for (var run = 0; run < 2; run++)
                foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                    if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);
            ids = await ConstructionR7OrderAllocationTests.Seed(db);
        }
        catch (Exception original)
        {
            try { await DisposeAsync(); probe = null; }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            throw;
        }
    }
    public async Task DisposeAsync() { if (probe != null) await sql.DropProbeDatabaseAsync(probe); }

    [SkippableFact]
    public async Task Concurrent_same_snapshot_writers_save_one_revision_and_retry_returns_its_identity()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var first = sql.ContextFor(probe!, 1); using var second = sql.ContextFor(probe!, 1);
        var access = new ConstructionR7OrderAllocationTests.Access();
        var a = new ConstructionOrderAllocationService(first, access, new ConstructionAuditService(first));
        var b = new ConstructionOrderAllocationService(second, access, new ConstructionAuditService(second));
        var preview = await a.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order); Assert.True(preview.Ok, preview.Code);
        var requestA = Guid.NewGuid(); var requestB = Guid.NewGuid();
        var results = await Task.WhenAll(a.SaveAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order, preview.Version!, requestA, ConstructionR7OrderAllocationTests.Splits(ids), "Concurrent mapping"),
            b.SaveAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order, preview.Version!, requestB, ConstructionR7OrderAllocationTests.Splits(ids), "Concurrent mapping"));
        Assert.Single(results, x => x.Ok); Assert.Single(results, x => x.Code == "ConcurrencyConflict");
        var winner = results[0].Ok ? requestA : requestB;
        using var retryDb = sql.ContextFor(probe!, 1);
        var retry = await new ConstructionOrderAllocationService(retryDb, access, new ConstructionAuditService(retryDb))
            .SaveAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order, preview.Version!, winner, ConstructionR7OrderAllocationTests.Splits(ids), "Concurrent mapping");
        Assert.True(retry.Ok, retry.Code); Assert.Equal(results.Single(x => x.Ok).Id, retry.Id);
        using var verify = sql.ContextFor(probe!, 1);
        Assert.True((await verify.Set<ConstructionOrderAllocation>().AsNoTracking().SingleAsync()).IsSealed);
        Assert.Equal(100m, (await verify.Set<ConstructionOrderAllocationLine>().AsNoTracking().SingleAsync()).NetDocumentAmount);
        Assert.Empty(await verify.JournalEntries.AsNoTracking().ToListAsync()); Assert.Empty(await verify.StockMovements.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Authored_constraints_preserve_sealed_history_and_scoped_line_references()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var db = sql.ContextFor(probe!, 1);
        var service = new ConstructionOrderAllocationService(db, new ConstructionR7OrderAllocationTests.Access(), new ConstructionAuditService(db));
        var preview = await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order);
        var saved = await service.SaveAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order, preview.Version!, Guid.NewGuid(), ConstructionR7OrderAllocationTests.Splits(ids), "Immutable mapping");
        Assert.True(saved.Ok, saved.Code);
        var lineFailure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.ConstructionOrderAllocationLines SET NetDocumentAmount=99")); Assert.Equal(51402, lineFailure.Number);
        var headerFailure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.ConstructionOrderAllocations SET Reason='Rewrite history'")); Assert.Equal(51402, headerFailure.Number);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            // A distinct valid parent avoids the dimension uniqueness constraint masking the scope FK.
            await db.Database.ExecuteSqlRawAsync("INSERT dbo.ConstructionOrderAllocations(CompanyID,ProjectId,PurchaseOrderId,BudgetVersionId,RevisionNo,RequestId,RequestHash,SourceVersion,Reason,CreatedBy,CreatedAt,IsSealed) SELECT CompanyID,ProjectId,PurchaseOrderId,BudgetVersionId,RevisionNo+1,NEWID(),RequestHash,SourceVersion,Reason,CreatedBy,CreatedAt,0 FROM dbo.ConstructionOrderAllocations");
            var foreignScope = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("INSERT dbo.ConstructionOrderAllocationLines(CompanyID,ProjectId,AllocationId,PurchaseOrderId,SourceLineId,WbsNodeId,CostCodeId,CostCategory,NetDocumentAmount) SELECT a.CompanyID+1,a.ProjectId,(SELECT MAX(ID) FROM dbo.ConstructionOrderAllocations),a.PurchaseOrderId,a.SourceLineId,a.WbsNodeId,a.CostCodeId,a.CostCategory,1 FROM dbo.ConstructionOrderAllocationLines a"));
            Assert.Equal(547, foreignScope.Number); Assert.Contains("FK_ConstructionOrderAllocationLines_Header", foreignScope.Message);
            await tx.RollbackAsync();
        }
        using var verify = sql.ContextFor(probe!, 1);
        Assert.Equal(100m, (await verify.Set<ConstructionOrderAllocationLine>().AsNoTracking().SingleAsync()).NetDocumentAmount);
        Assert.Equal(6, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionOrderAllocations'),OBJECT_ID('dbo.ConstructionOrderAllocationLines')) AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
    }

    [SkippableFact]
    public async Task Authored_sealing_rejects_incomplete_line_coverage_without_persisting_a_revision()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var db = sql.ContextFor(probe!, 1);
        var service = new ConstructionOrderAllocationService(db, new ConstructionR7OrderAllocationTests.Access(), new ConstructionAuditService(db));
        var preview = await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var header = new ConstructionOrderAllocation { CompanyID = 1, ProjectId = ids.Project, PurchaseOrderId = ids.Order,
                BudgetVersionId = preview.BudgetVersionId!.Value, RevisionNo = 1, RequestId = Guid.NewGuid(),
                RequestHash = new string('A',64), SourceVersion = preview.Version!, Reason = "Incomplete raw revision", CreatedBy = 7 };
            db.Add(header); await db.SaveChangesAsync(); header.IsSealed = true;
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(51403, Assert.IsType<SqlException>(error.InnerException).Number);
            await tx.RollbackAsync();
        }
        using var verify = sql.ContextFor(probe!, 1);
        Assert.Empty(await verify.Set<ConstructionOrderAllocation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionOrderAllocationLine>().AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Late_audit_failure_rolls_back_header_lines_and_seal_then_clean_retry_succeeds()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var holder = new CompanyScopeHolder(); holder.Set(1, null);
        var failure = new FailAllocationAudit();
        using var db = new CrossDbContext(new DbContextOptionsBuilder<CrossDbContext>().UseSqlServer(probe!.ConnectionString).AddInterceptors(failure).Options, holder);
        // Inject only the late audit persistence boundary, after the real allocation lines were saved.
        var service = new ConstructionOrderAllocationService(db, new ConstructionR7OrderAllocationTests.Access(), new ConstructionAuditService(db));
        var preview = await service.PreviewAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order);
        var request = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order, preview.Version!, request, ConstructionR7OrderAllocationTests.Splits(ids), "Atomic mapping"));
        Assert.Equal("Injected allocation audit persistence failure", error.Message);
        using (var verify = sql.ContextFor(probe!, 1))
        { Assert.Empty(await verify.Set<ConstructionOrderAllocation>().AsNoTracking().ToListAsync()); Assert.Empty(await verify.Set<ConstructionOrderAllocationLine>().AsNoTracking().ToListAsync()); }
        failure.Enabled = false;
        Assert.True((await service.SaveAsync(ConstructionR7OrderAllocationTests.Actor(), ids.Project, ids.Order, preview.Version!, request, ConstructionR7OrderAllocationTests.Splits(ids), "Atomic mapping")).Ok);
    }

    private sealed class FailAllocationAudit : SaveChangesInterceptor
    {
        public bool Enabled = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<ConstructionAuditEntry>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("Injected allocation audit persistence failure");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
}
