using Microsoft.Data.SqlClient;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // =============================================================================================
    // PROBE LEASE — the runtime proof.
    //
    // Everything else about ProbeLease is established by construction and compilation. This is the
    // only place it is exercised against a real SQL Server: a lease is taken, `await using` releases
    // it, and the server is asked whether the database actually went away.
    //
    // WHY THAT NEEDED SAYING OUT LOUD. The defect this whole lifecycle exists to fix was 300 scratch
    // databases that every green test run reported as absent. A mechanism that claims to drop a
    // database is worth exactly as much as the query that confirms it did.
    //
    // THE BLAST RADIUS IS TWO DATABASES, both owned-prefix and both released by the end:
    //   CrossBuyPlatformTest_<guid>        the collection fixture's own, dropped at teardown
    //   CrossBuyProbe_LeaseProof_<guid>    this lease's, dropped by DisposeAsync
    //
    // AND THE 300 ARE WITNESSES, NOT BYSTANDERS. Every pre-existing scratch database is listed
    // before the lease, while it is held, and after it is released. A lifecycle that reclaims its own
    // database is only half the claim; the other half is that it reclaimed NOTHING ELSE, and the one
    // way to show that is to count the neighbours and find them all still there.
    // =============================================================================================
    [Collection(SqlServerCollection.Name)]
    public class ProbeLeaseRuntimeProofTests
    {
        private const string Family = "LeaseProof";

        private readonly SqlServerFixture _sql;
        public ProbeLeaseRuntimeProofTests(SqlServerFixture sql) { _sql = sql; }

        private string Master =>
            new SqlConnectionStringBuilder(_sql.TestConnectionString) { InitialCatalog = "master" }.ConnectionString;

        /// Every owned-prefix database on the instance. Escaped underscores: '_' is a single-character
        /// wildcard in T-SQL, so the unescaped form also matches names this suite never created.
        private async Task<List<string>> ScratchNamesAsync()
        {
            var names = new List<string>();
            await using var c = new SqlConnection(Master);
            await c.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT name FROM sys.databases WHERE name LIKE 'CrossBuyPlatformTest[_]%' " +
                "OR name LIKE 'CrossBuyProbe[_]%' OR name LIKE 'CrossBuyImp003[_]%' ORDER BY name;", c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) names.Add(r.GetString(0));
            return names;
        }

        private async Task<bool> ExistsAsync(string database)
        {
            await using var c = new SqlConnection(Master);
            await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @n;", c);
            cmd.Parameters.AddWithValue("@n", database);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
        }

        [SkippableFact]
        public async Task A_lease_creates_its_database_releases_it_and_touches_nothing_else()
        {
            Skip.If(!_sql.Available, _sql.SkipReason);

            // The witnesses. Taken AFTER fixture initialisation, so this already includes the fixture's
            // own CrossBuyPlatformTest_ database.
            var before = await ScratchNamesAsync();
            Assert.NotEmpty(before);

            string name;
            await using (var lease = await _sql.LeaseProbeDatabaseAsync(Family, applyModelSchema: false))
            {
                name = lease.Database.Name;

                // Shape: the lease names what it owns, and that name is one this suite may drop.
                Assert.StartsWith("CrossBuyProbe_" + Family + "_", name, StringComparison.Ordinal);
                Assert.True(SqlEvidenceGuards.IsOwnedScratchName(name));

                // It really exists on the server, not merely in a return value.
                Assert.True(await ExistsAsync(name), $"the lease reported '{name}' but the server has no such database");

                // While held: Leased, in history, and outstanding.
                Assert.Equal(SqlServerFixture.ProbeCleanupStatus.Leased, lease.Status);
                Assert.Contains(_sql.LeaseHistory, r => r.DatabaseName == name);
                Assert.Contains(_sql.ActiveLeases, r => r.DatabaseName == name);

                // The neighbours are untouched while the lease is held.
                var during = await ScratchNamesAsync();
                Assert.Equal(before.OrderBy(x => x, StringComparer.Ordinal),
                             during.Where(n => n != name).OrderBy(x => x, StringComparer.Ordinal));
            }

            // ---- after `await using` released it -------------------------------------------------

            // THE CLAIM THAT MATTERS: the database is gone from the server.
            Assert.False(await ExistsAsync(name), $"'{name}' survived disposal — the lease did not reclaim it");

            // Recorded as Released, with no cleanup error, and kept in history rather than pruned.
            var record = Assert.Single(_sql.LeaseHistory.Where(r => r.DatabaseName == name));
            Assert.Equal(SqlServerFixture.ProbeCleanupStatus.Released, record.Status);
            Assert.Null(record.CleanupError);

            // Nothing is outstanding, so the fixture's teardown will not fail this run.
            Assert.Empty(_sql.OutstandingLeases);
            Assert.DoesNotContain(_sql.ActiveLeases, r => r.DatabaseName == name);

            // And the instance is exactly as it was: same names, same count, nothing else reclaimed.
            var after = await ScratchNamesAsync();
            Assert.Equal(before.OrderBy(x => x, StringComparer.Ordinal),
                         after.OrderBy(x => x, StringComparer.Ordinal));
        }

    }
}
