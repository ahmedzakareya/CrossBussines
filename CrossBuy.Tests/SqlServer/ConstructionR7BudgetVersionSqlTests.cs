using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Text.RegularExpressions;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class ConstructionR7BudgetVersionSqlTests(SqlServerFixture sql)
{
    private static BusinessContext Actor(int employee = 7) => new()
        { CompanyId = 1, EmployeeId = employee, UserId = "R7-native-budget", Source = BusinessContextSource.Test };
    private static ConstructionBudgetVersionService Service(CrossDbContext db) => new(db, new Access(), new ConstructionAuditService(db),
        new CurrencyRounding(db, new CurrencyService(db), new Localizer()));

    private static string RepositoryFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }
    private static string Script(string name) => RepositoryFile(Path.Combine("CrossBuy", "deploy", "sql", name));
    private static async Task Execute(SqlServerFixture.ProbeDatabase probe, string text)
    {
        await using var connection = new SqlConnection(probe.ConnectionString); await connection.OpenAsync();
        foreach (var batch in Regex.Split(text, @"(?im)^\s*GO\s*(?:--[^\r\n]*)?\r?$"))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = connection.CreateCommand(); command.CommandText = batch; command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Authored_schema_is_rerunnable_native_line_changes_invalidate_forms_and_approved_history_is_immutable()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        using var manifest = System.Text.Json.JsonDocument.Parse(RepositoryFile(Path.Combine("CrossBuy", "deploy", "sql", "manifest.json")));
        var ranks = manifest.RootElement.GetProperty("scripts").EnumerateArray()
            .ToDictionary(x => x.GetProperty("path").GetString()!, x => x.GetProperty("rank").GetInt32());
        var ordered = new[] { "construction_c1_commercial_foundation.sql", "construction_c1_contract_mapping_measurement.sql",
            "construction_r7_native_rowversion.sql", "construction_r7_consolidate_commercial_state.sql", "construction_r7_budget_policy.sql",
            "construction_r7_structure.sql", "construction_r7_budget_versions.sql" };
        for (var index = 1; index < ordered.Length; index++)
            Assert.True(ranks["CrossBuy/deploy/sql/" + ordered[index - 1]] < ranks["CrossBuy/deploy/sql/" + ordered[index]], "Construction schema dependencies must not share an unordered rank.");
        var probe = await sql.CreateProbeDatabaseAsync("R7Budget");
        try
        {
            // Remove only empty model-created copies in this owned probe. Authored SQL is never stripped.
            await Execute(probe, """
                IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetLines) OR EXISTS(SELECT 1 FROM dbo.ConstructionBudgetVersions)
                 OR EXISTS(SELECT 1 FROM dbo.ConstructionSites) OR EXISTS(SELECT 1 FROM dbo.ConstructionWbsNodes)
                 OR EXISTS(SELECT 1 FROM dbo.ConstructionCostCodes) OR EXISTS(SELECT 1 FROM dbo.ConstructionStructureSettings)
                 THROW 51209,'Only empty fixture tables may be replaced by authored DDL.',1;
                IF EXISTS(SELECT 1 FROM dbo.ConstructionReservationRebaseRequests) THROW 51209,'Owned fixture rebase requests must be empty.',1;
                DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseDecisions; DROP TABLE IF EXISTS dbo.ConstructionPurchaseRebaseRequests; DROP TABLE IF EXISTS dbo.ConstructionReservationExposureRebases; DROP TABLE IF EXISTS dbo.ConstructionReservationRebaseDecisions; DROP TABLE dbo.ConstructionReservationRebaseRequests;
                IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferDecisions) THROW 51209,'Owned fixture transfer decisions must be empty.',1;
                IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferApplications) THROW 51970,'Owned application fixture must be empty',1; DROP TABLE dbo.ConstructionBudgetTransferApplications; DROP TABLE dbo.ConstructionBudgetTransferDecisions;
                IF EXISTS(SELECT 1 FROM dbo.ConstructionBudgetTransferRequests) THROW 51209,'Owned fixture transfer requests must be empty.',1;
                DROP TABLE dbo.ConstructionBudgetTransferRequests;
                DROP TABLE dbo.ConstructionBudgetLines;
                DROP TABLE dbo.ConstructionBudgetVersions;
                DROP TABLE dbo.ConstructionSites;
                DROP TABLE dbo.ConstructionWbsNodes;
                DROP TABLE dbo.ConstructionCostCodes;
                DROP TABLE dbo.ConstructionStructureSettings;
                IF EXISTS(SELECT 1 FROM dbo.ConstructionAuditEntries)
                 THROW 51209,'Only empty model audit fixtures may be replaced.',1;
                DROP TABLE dbo.ConstructionAuditEntries;
                """);
            // Use the real C1 audit batch, including its original 1000-character payload limit.
            // Otherwise an EF-generated nvarchar(max) table would hide a real deployment failure.
            var c1Audit = Regex.Split(Script("construction_c1_commercial_foundation.sql"), @"(?im)^\s*GO\s*(?:--[^\r\n]*)?\r?$")
                .Single(x => x.Contains("CREATE TABLE dbo.ConstructionAuditEntries (", StringComparison.Ordinal));
            await Execute(probe, c1Audit);
            await Execute(probe, "INSERT dbo.ConstructionAuditEntries(CompanyID,EntityType,EntityId,OldValue,NewValue,OldNumeric,CorrelationId) VALUES(1,N'R7HistoricalProbe',0,N'قبل التوسعة',N'تاريخ محفوظ',123.4567,NEWID());");
            var structure = Script("construction_r7_structure.sql"); var budgets = Script("construction_r7_budget_versions.sql");
            Assert.Equal(51200, (await Assert.ThrowsAsync<SqlException>(() => Execute(probe, budgets))).Number);
            await Execute(probe, structure); await Execute(probe, structure);
            await Execute(probe, budgets); await Execute(probe, budgets);
            await using var db = sql.ContextFor(probe);
            Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionAuditEntries') AND name IN('OldValue','NewValue') AND max_length=-1 AND system_type_id=231").SingleAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM dbo.ConstructionAuditEntries WHERE EntityType=N'R7HistoricalProbe' AND OldValue=N'قبل التوسعة' AND NewValue=N'تاريخ محفوظ' AND OldNumeric=123.4567").SingleAsync());
            Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id IN(OBJECT_ID('dbo.ConstructionBudgetVersions'),OBJECT_ID('dbo.ConstructionBudgetLines')) AND name='RowVersion' AND system_type_id=189").SingleAsync());
            Assert.Equal(7, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionBudgetVersions'),OBJECT_ID('dbo.ConstructionBudgetLines')) AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
            var project = new Project { CompanyID = 1, Code = "R7-BUDGET", Name = "Budget test" };
            var currency = new Currency { Code = "TST", Name = "Test", NameEn = "Test", DecimalPlaces = 3 };
            db.AddRange(project, currency); await db.SaveChangesAsync();
            // This native test uses explicit company currency; no fallback currency is involved.
            var company = new CrossBuy.Models.Context.Admin.Companies { Address = "", PhoneNumber = "", Email = "", DefaultCurrencyId = currency.ID };
            db.Companies.Add(company); await db.SaveChangesAsync();
            Assert.Equal(1, company.CompanyID); // A fresh owned probe; never reuse a normal company database.
            db.Add(new ConstructionBudgetPolicy { CompanyID = 1, Mode = "HardBlock", Reason = "Owner choice" });
            db.Add(new ConstructionStructureSettings { CompanyID = 1, MaxWbsDepth = 8, Reason = "Owner hierarchy", UpdatedBy = 7, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var node = new WbsNode { CompanyID = 1, ProjectId = project.ID, Code = "A", Name = "Work", Path = "/pending/", Depth = 1,
                Status = "Active", ProgressMethod = "Quantity", OwnsBudget = true, OwnsCost = true, CreatedBy = 7, CreatedAt = DateTime.UtcNow };
            var code = new ConstructionCostCode { CompanyID = 1, Code = "MAT", Name = "Material", Category = "Material", AllowDirectPosting = true, IsActive = true };
            db.AddRange(node, code); await db.SaveChangesAsync();
            var service = Service(db);
            // Hold the exact project admission lock on a separate SQL session. Observe a real
            // APPLICATION lock wait, rather than inferring exclusion from a delayed task.
            Task<StructureResult>? pending = null;
            await using (var blocker = new SqlConnection(probe.ConnectionString))
            {
                await blocker.OpenAsync();
                await using var hold = blocker.CreateCommand();
                hold.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@scope,@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0; SELECT @r;";
                hold.Parameters.AddWithValue("@scope", $"R7:Commitments:1:{project.ID}");
                Assert.True(Convert.ToInt32(await hold.ExecuteScalarAsync()) >= 0);
                var observedWait = false;
                try
                {
                    pending = service.CreateAsync(Actor(), project.ID, "Original", null, null, "Estimate");
                    for (var attempt = 0; attempt < 60 && !pending.IsCompleted; attempt++)
                    {
                        await using var inspect = blocker.CreateCommand();
                        inspect.CommandText = """
                            SELECT COUNT(*) FROM sys.dm_tran_locks w
                            JOIN sys.dm_tran_locks h ON h.resource_database_id=w.resource_database_id
                              AND h.resource_description=w.resource_description
                            WHERE w.resource_type=N'APPLICATION' AND h.resource_type=N'APPLICATION'
                              AND w.request_status=N'WAIT' AND h.request_status=N'GRANT'
                              AND h.request_session_id=@@SPID AND w.request_session_id<>@@SPID;
                            """;
                        if (Convert.ToInt32(await inspect.ExecuteScalarAsync()) > 0)
                        { observedWait = true; break; }
                        await Task.Delay(50);
                    }
                }
                finally
                {
                    await using var release = blocker.CreateCommand();
                    release.CommandText = "EXEC sys.sp_releaseapplock @Resource=@scope,@LockOwner=N'Session';";
                    release.Parameters.AddWithValue("@scope", $"R7:Commitments:1:{project.ID}");
                    await release.ExecuteNonQueryAsync();
                }
                var unblocked = await pending!;
                Assert.True(observedWait, "Budget write must wait on the shared project admission lock.");
                Assert.True(unblocked.Ok, unblocked.Code);
            }
            var created = await pending!; Assert.True(created.Ok, created.Code);
            var longReason = new string('م', 600);
            List<ConstructionBudgetDraftLine> Rows(decimal amount) => new() { new(node.ID, code.ID, amount, null, null, false, longReason) };
            var saved = await service.SaveDraftAsync(Actor(), project.ID, created.Id!.Value, created.Version!, Rows(100), "Save draft"); Assert.True(saved.Ok, saved.Code);
            await Execute(probe, "UPDATE dbo.ConstructionBudgetLines SET Amount=101;");
            db.ChangeTracker.Clear();
            var stale = await service.SaveDraftAsync(Actor(), project.ID, created.Id.Value, saved.Version!, Rows(900), "Stale form");
            Assert.False(stale.Ok); Assert.Equal("ConcurrencyConflict", stale.Code);
            var fresh = await service.GetAsync(Actor(), project.ID, created.Id.Value); Assert.NotNull(fresh); Assert.Equal(101m, Assert.Single(fresh.Lines).Amount);
            var submitted = await service.SubmitAsync(Actor(), project.ID, created.Id.Value, fresh.Header.RowVersion, "Submit"); Assert.True(submitted.Ok, submitted.Code);
            var frozen = await Assert.ThrowsAsync<SqlException>(() => Execute(probe, "UPDATE dbo.ConstructionBudgetLines SET Amount=999;")); Assert.Equal(51201, frozen.Number);
            Assert.Equal("SeparateApproverRequired", (await service.ApproveAsync(Actor(), project.ID, created.Id.Value, submitted.Version!, "Self approval")).Code);
            var approved = await service.ApproveAsync(Actor(9), project.ID, created.Id.Value, submitted.Version!, "Independent approval"); Assert.True(approved.Ok, approved.Code);
            foreach (var illegal in new[] { "UPDATE dbo.ConstructionBudgetLines SET Amount=999;", "DELETE dbo.ConstructionBudgetLines;", "UPDATE dbo.ConstructionBudgetVersions SET Reason=N'Rewrite approved history';" })
                Assert.Equal(51201, (await Assert.ThrowsAsync<SqlException>(() => Execute(probe, illegal))).Number);
            // The restricted line FK refuses parent deletion before the AFTER history trigger executes.
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => Execute(probe, "DELETE dbo.ConstructionBudgetVersions;"))).Number);
            await Execute(probe, budgets); // Re-run with approved history present, not just on empty tables.
            var revised = await service.CreateAsync(Actor(), project.ID, "Revised", created.Id, approved.Version, "New estimate"); Assert.True(revised.Ok, revised.Code);
            var changed = await service.SaveDraftAsync(Actor(), project.ID, revised.Id!.Value, revised.Version!, Rows(125), "Updated estimate"); Assert.True(changed.Ok, changed.Code);
            var sent = await service.SubmitAsync(Actor(), project.ID, revised.Id.Value, changed.Version!, "Submit revision"); Assert.True(sent.Ok, sent.Code);
            var replacement = await service.ApproveAsync(Actor(9), project.ID, revised.Id.Value, sent.Version!, "Approve revision"); Assert.True(replacement.Ok, replacement.Code);
            await using var verify = sql.ContextFor(probe);
            var persistedLines = await verify.Set<ConstructionBudgetLine>().AsNoTracking().ToListAsync();
            Assert.Equal(101m, persistedLines.Single(x => x.BudgetVersionId == created.Id).Amount);
            var revisedLine = persistedLines.Single(x => x.BudgetVersionId == revised.Id);
            Assert.Equal(125m, revisedLine.Amount); Assert.Equal(101m, revisedLine.PreviousAmount);
            var persistedHeaders = await verify.Set<ConstructionBudgetVersion>().AsNoTracking().ToListAsync();
            Assert.Equal(revised.Id, Assert.Single(persistedHeaders.Where(x => x.IsBaseline)).ID);
            Assert.Equal("Superseded", persistedHeaders.Single(x => x.ID == created.Id).Status);
            var recorded = await verify.ConstructionAuditEntries.AsNoTracking().Where(x => x.EntityId == revised.Id && x.FieldName == "BudgetLines").SingleAsync();
            Assert.True(recorded.NewValue!.Length > 1000);
            var recordedLines = System.Text.Json.JsonSerializer.Deserialize<List<ConstructionBudgetDraftLine>>(recorded.NewValue);
            Assert.Equal(longReason, Assert.Single(recordedLines!).ChangeReason);

            // Ordinary estimate approval must not become a transfer authority, even
            // with an independently authorized approver and a valid native rowversion.
            var otherCode = new ConstructionCostCode { CompanyID = 1, Code = "GUARD", Name = "Other material",
                Category = "Material", IsActive = true, AllowDirectPosting = true };
            db.Add(otherCode); await db.SaveChangesAsync();
            foreach (var targetAmount in new[] { 25m, 50m, 10m })
            {
                var move = await service.CreateAsync(Actor(), project.ID, "Revised", revised.Id,
                    replacement.Version, "Allocation revision"); Assert.True(move.Ok, move.Code);
                var moveLines = new List<ConstructionBudgetDraftLine> {
                    new(node.ID, code.ID, 100m, null, null, false, "Source decrease"),
                    new(node.ID, otherCode.ID, targetAmount, null, null, false, "Target increase") };
                var moveSaved = await service.SaveDraftAsync(Actor(), project.ID, move.Id!.Value,
                    move.Version!, moveLines, "Allocation review"); Assert.True(moveSaved.Ok, moveSaved.Code);
                var moveSubmitted = await service.SubmitAsync(Actor(8), project.ID, move.Id.Value,
                    moveSaved.Version!, "Submit allocation"); Assert.True(moveSubmitted.Ok, moveSubmitted.Code);
                var auditBefore = await db.ConstructionAuditEntries.AsNoTracking().CountAsync();
                var blocked = await service.ApproveAsync(Actor(9), project.ID, move.Id.Value,
                    moveSubmitted.Version!, "Ordinary revision approval");
                Assert.False(blocked.Ok); Assert.Equal("BudgetTransferWorkflowRequired", blocked.Code);
                await using var guardVerify = sql.ContextFor(probe);
                var stillBaseline = await guardVerify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x => x.IsBaseline);
                Assert.Equal(revised.Id, stillBaseline.ID); Assert.Equal("Approved", stillBaseline.Status);
                Assert.Equal(replacement.Version, stillBaseline.RowVersion);
                var refused = await guardVerify.Set<ConstructionBudgetVersion>().AsNoTracking().SingleAsync(x => x.ID == move.Id);
                Assert.Equal("Submitted", refused.Status); Assert.Null(refused.ApprovedBy); Assert.Null(refused.ApprovedAt);
                Assert.Equal(moveSubmitted.Version, refused.RowVersion);
                Assert.Equal(auditBefore, await guardVerify.ConstructionAuditEntries.AsNoTracking().CountAsync());
                Assert.Equal(125m, (await guardVerify.Set<ConstructionBudgetLine>().AsNoTracking()
                    .SingleAsync(x => x.BudgetVersionId == revised.Id)).Amount);
            }

        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }
    private sealed class Access : IProjectsAccessService
    {
        public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context, string action, CancellationToken cancellationToken = default) => Task.FromResult(AccessScope.None());
        public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
    }
    private sealed class Localizer : IStringLocalizer<CrossBuy.SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
    }
}



