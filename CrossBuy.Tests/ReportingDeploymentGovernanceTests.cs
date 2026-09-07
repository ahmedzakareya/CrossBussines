using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // A0 — REPORTING DEPLOYMENT GOVERNANCE.
    //
    // DELIBERATELY NOT GATED ON CROSSBUY_TEST_SQL. That gate is the reason A0 existed: the SQL proofs
    // reported SKIPPED, nobody noticed, and a real database failed with `Invalid object name 'ReportShares'`
    // while 304 Reporting tests were green. Every assertion here runs on any machine, with no database,
    // because their whole job is to fail when the deployment story rots — and a proof that can skip cannot
    // do that job.
    //
    // These tests answer, without a database:
    //   * does the authored slice exist, in the canonical root, exactly once?
    //   * is it registered in the manifest with a CURRENT hash?
    //   * does it create a table for every Reporting entity the EF model maps?
    //   * is EnsureCreated() being mistaken for deployment anywhere?
    // =============================================================================================
    public class ReportingDeploymentGovernanceTests
    {
        private const string SliceName = "reporting_platform.sql";
        private const string CanonicalRoot = "CrossBuy/deploy/sql";
        private static string CanonicalSlicePath => Path.Combine(RepoRoot(), "CrossBuy", "deploy", "sql", SliceName);

        // ============================================================================================
        // 1. ONE SLICE, IN THE CANONICAL ROOT
        // ============================================================================================

        // The repository has TWO SQL trees — the legacy repo-root `deploy/sql` and the canonical
        // `CrossBuy/deploy/sql`. Searching the wrong one is not hypothetical: it produced a written report
        // claiming the Reporting platform had no deployment script at all, when the slice had existed in the
        // canonical root the whole time. This test makes the canonical location a fact a test knows.
        [Fact]
        public void The_reporting_slice_exists_in_the_canonical_sql_root()
        {
            Assert.True(File.Exists(CanonicalSlicePath),
                $"{CanonicalRoot}/{SliceName} is missing. The Reporting platform cannot be deployed without it, " +
                "and EnsureCreated() in the test host does not substitute for it.");
        }

        // Exactly one, so a stale copy in the legacy tree cannot be applied by mistake and cannot drift.
        [Fact]
        public void Exactly_one_reporting_slice_exists_anywhere_in_the_repository()
        {
            var root = RepoRoot();

            var found = Directory
                .EnumerateFiles(root, "*.sql", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Where(p => File.ReadAllText(p).Contains("dbo.ReportShares", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            Assert.True(found.Count == 1,
                "Expected exactly one Reporting DDL file. Found " + found.Count + ": " + string.Join(", ", found) +
                ". Two copies drift, and the wrong one gets applied.");

            Assert.Equal($"{CanonicalRoot}/{SliceName}", found[0]);
        }

        // ============================================================================================
        // 2. THE MANIFEST KNOWS IT, AND KNOWS IT CURRENTLY
        // ============================================================================================

        // A manifest whose hash is stale is worse than no manifest: it reports a file as reviewed and
        // deployable while describing different bytes.
        [Fact]
        public void The_manifest_registers_the_slice_with_its_current_hash()
        {
            var manifestPath = Path.Combine(RepoRoot(), "CrossBuy", "deploy", "sql", "manifest.json");
            Assert.True(File.Exists(manifestPath), manifestPath);

            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));

            var entries = document.RootElement.GetProperty("scripts").EnumerateArray()
                .Where(s => s.GetProperty("name").GetString() == SliceName)
                .ToList();

            Assert.True(entries.Count == 1,
                $"Expected exactly one manifest entry for {SliceName}, found {entries.Count}.");

            var entry = entries[0];
            var bytes = File.ReadAllBytes(CanonicalSlicePath);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            Assert.Equal($"{CanonicalRoot}/{SliceName}", entry.GetProperty("path").GetString());
            Assert.Equal(bytes.Length, entry.GetProperty("bytes").GetInt64());
            Assert.Equal(hash, entry.GetProperty("sha256").GetString());

            // The generator classifies guarding by inspection. "guarded" is what makes re-application safe,
            // and it is the property the runbook relies on when it says the slice may be re-run.
            Assert.Equal("guarded", entry.GetProperty("idempotency").GetString());
            Assert.False(entry.GetProperty("excludedFromDeploy").GetBoolean());
        }

        [Fact]
        public void The_slice_registry_owns_the_reporting_slice()
        {
            var registryPath = Path.Combine(RepoRoot(), "governance", "registry", "sql-slices.json");
            Assert.True(File.Exists(registryPath), registryPath);

            using var document = JsonDocument.Parse(File.ReadAllBytes(registryPath));

            Assert.Equal(CanonicalRoot, document.RootElement.GetProperty("canonicalAuthoredRoot").GetString());

            var slices = document.RootElement.GetProperty("slices").EnumerateArray()
                .Where(s => s.GetProperty("slice").GetString() == SliceName)
                .ToList();

            var slice = Assert.Single(slices);
            Assert.False(string.IsNullOrWhiteSpace(slice.GetProperty("sliceId").GetString()),
                "the Reporting slice must carry a unique SliceId");
            Assert.Contains("CANONICAL", slice.GetProperty("tree").GetString() ?? "");
        }

        // ============================================================================================
        // 3. THE SLICE COVERS THE MODEL — provable with no database at all
        // ============================================================================================
        //
        // The SQL-Server suite proves this against a real applied schema. This proves the same coverage from
        // the file text, so it still fails on a machine with no SQL Server — which is where A0's defect was
        // able to hide.
        [Fact]
        public void The_slice_creates_a_table_for_every_reporting_entity_in_the_ef_model()
        {
            var sql = File.ReadAllText(CanonicalSlicePath);

            using var host = new ReportingTestHost();

            var modelTables = host.Db.Model.GetEntityTypes()
                .Where(e => e.ClrType.Namespace == "CrossBuy.Models.Context.Reporting")
                .Select(e => e.GetTableName())
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => t!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(modelTables.Count > 0, "the EF model declares no Reporting entities — that cannot be right");

            var missing = modelTables
                .Where(t => !sql.Contains($"CREATE TABLE dbo.{t}", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(missing.Count == 0,
                "These Reporting entities are mapped by EF but the authored slice creates no table for them. " +
                "Deploying this build would fail at run time with 'Invalid object name' — the exact failure A0 " +
                "closed: " + string.Join(", ", missing));
        }

        // ============================================================================================
        // 4. ENSURECREATED IS NOT DEPLOYMENT
        // ============================================================================================

        // EnsureCreated() is legitimate INSIDE a test host — it derives the schema from the model, which is
        // what a unit test wants. It is not legitimate as evidence that a database can be deployed, and it is
        // not legitimate in production code at all.
        //
        // This test pins both halves: no Reporting production file may call it, and the one test host that
        // does must carry the warning explaining why that is not deployment evidence.
        [Fact]
        public void No_reporting_production_code_creates_its_own_schema()
        {
            var reporting = Path.Combine(RepoRoot(), "CrossBuy", "BL", "Reporting");
            Assert.True(Directory.Exists(reporting), reporting);

            var offenders = Directory.EnumerateFiles(reporting, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var code = StripComments(File.ReadAllText(f));
                    return code.Contains("EnsureCreated", StringComparison.Ordinal)
                           || code.Contains("Migrate()", StringComparison.Ordinal);
                })
                .Select(Path.GetFileName)
                .ToList();

            Assert.True(offenders.Count == 0,
                "Reporting production code must never create its own schema — deployment is the authored SQL " +
                "slice, applied before the code ships: " + string.Join(", ", offenders));
        }

        [Fact]
        public void The_reporting_test_host_declares_that_its_schema_is_not_deployment_evidence()
        {
            var host = Path.Combine(RepoRoot(), "CrossBuy.Tests", "ReportingTestHost.cs");
            Assert.True(File.Exists(host), host);

            var text = File.ReadAllText(host);

            // It does use EnsureCreated — that is correct for a test host.
            Assert.Contains("EnsureCreated", text, StringComparison.Ordinal);

            // And it must say, in the file, that this is not deployment evidence. A comment is weak
            // enforcement, but the alternative is that the next person reads a green suite as proof the
            // database is deployable, which is precisely what happened.
            Assert.Contains("not deployment evidence", text, StringComparison.OrdinalIgnoreCase);
        }

        // Comments are removed before scanning for code. This is the THIRD time in this workstream that a
        // text-matching guard has read prose as code — the first flagged a header saying "this file does not
        // reference the Workspace", the second an escaped `@@section Styles` inside the explanation of why
        // there is no section, and this one the comment in ReportsCenterPresenter explaining that
        // EnsureCreated() is why the SQLite tests could not see A0. A guard whose message is "you did the
        // forbidden thing" must not fire on the sentence describing why it is forbidden.
        private static string StripComments(string source)
        {
            var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
                source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);

            return System.Text.RegularExpressions.Regex.Replace(
                withoutBlocks, @"//.*?$", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        private static string RepoRoot()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
                return fromEnvironment;

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate the repository root above " + AppContext.BaseDirectory + ". Set CROSSBUY_REPO_ROOT.");
        }
    }
}
