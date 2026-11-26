using System.Text.RegularExpressions;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7RebasedWarehouseConsentSqlTests(UatSqlProbeFixture sql)
{
    [SkippableFact]
    public async Task Native_consent_schema_is_rerunnable_and_keeps_append_only_guards()
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7REBASEWHCONSENT");
        try
        {
            using var db = sql.ContextFor(probe, 1);
            await db.Database.OpenConnectionAsync();
            // The probe creates the complete EF model first, with foreign keys stripped for
            // SQL Server's unrelated cascade-path limitation. Drop just these empty owned tables
            // so this test qualifies the deployment script as the schema owner.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.ConstructionRebasedWarehouseConsentApplications; DROP TABLE dbo.ConstructionRebasedWarehouseConsentBindings; DROP TABLE dbo.ConstructionRebasedWarehouseConsentDecisions; DROP TABLE dbo.ConstructionRebasedWarehouseConsentRequests;");
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "CrossBuy.sln"))) root = root.Parent;
            Assert.NotNull(root);
            var path = Path.Combine(root!.FullName, "CrossBuy", "deploy", "sql", "construction_r7_rebased_warehouse_consent.sql");
            var script = await File.ReadAllTextAsync(path);
            for (var run = 0; run < 2; run++)
                foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*\r?$"))
                    if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch);

            Assert.Equal(16, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentRequests')").SingleAsync());
            Assert.Equal(11, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentDecisions')").SingleAsync());
            Assert.Equal(17, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentBindings')").SingleAsync());
            Assert.Equal(10, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentApplications')").SingleAsync());
            Assert.Equal(5, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentRequests'),OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentDecisions')) AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
            Assert.Equal(7, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentBindings') AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
            Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentApplications') AND is_disabled=0 AND is_not_trusted=0").SingleAsync());
            Assert.Equal(8, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.triggers WHERE parent_id IN(OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentRequests'),OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentDecisions'),OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentBindings'),OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentApplications')) AND is_disabled=0").SingleAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.triggers WHERE object_id=OBJECT_ID('dbo.TR_ConstructionPurchaseRebaseRequests_AdmissionGate') AND is_disabled=0").SingleAsync());
            var proof = await db.Database.SqlQueryRaw<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ConstructionRebasedWarehouseConsentApplications_Proof')) AS Value").SingleAsync();
            Assert.Contains("LEFT JOIN dbo.ConstructionPurchaseRebaseDecisions d", proof, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("d.ID IS NULL", proof, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("a.RequestHash<>d.RequestHash", proof, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("a.RequestId<>d.RequestId", proof, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("d.RequestVersion<>br.ReviewVersion", proof, StringComparison.OrdinalIgnoreCase);
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }
}
