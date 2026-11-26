using Xunit;
using Xunit.Abstractions;

namespace CrossBuy.Tests.SqlServer
{
    // =================================================================================================
    // THE LIVE INVENTORY — the only place the detector meets a real server, and it only looks.
    //
    // NO [Collection(SqlServerCollection.Name)], DELIBERATELY. Joining the SqlServer collection would
    // construct SqlServerFixture, and SqlServerFixture CREATES A DATABASE in InitializeAsync. A detector
    // whose live test created a scratch database in order to count scratch databases would be absurd, and
    // on this machine CROSSBUY_TEST_SQL is a persisted USER variable, so that would happen by default
    // rather than by accident. This class reads the environment variable itself and opens one connection.
    //
    // IT ASSERTS NOTHING ABOUT ORPHAN COUNTS. Guard3 already fails a run that leaves scratch databases
    // behind; a second failing guard over the same facts would add noise, not information. This test
    // REPORTS - it fails only if the detector itself misbehaves, for instance by producing a report that
    // does not state it changed nothing.
    //
    // WHAT IT MAY DO: one SELECT against sys.databases, through ScratchCatalogSnapshot. What it may not
    // do, and cannot: CREATE, DROP, ALTER, RESTORE, deploy schema, or clean anything up.
    // =================================================================================================
    public class ScratchOrphanLiveInventoryTests
    {
        private readonly ITestOutputHelper _output;

        public ScratchOrphanLiveInventoryTests(ITestOutputHelper output) { _output = output; }

        [SkippableFact]
        public async Task Reports_the_scratch_database_inventory_without_modifying_anything()
        {
            var connectionString = Environment.GetEnvironmentVariable(SqlServerFixture.ConnectionStringVariable);

            Skip.If(string.IsNullOrWhiteSpace(connectionString),
                $"{SqlServerFixture.ConnectionStringVariable} is not set - the live scratch inventory needs a " +
                "SQL Server instance to read. It is skipped rather than reported as a clean instance.");

            var catalog = await ScratchCatalogSnapshot.ReadAsync(connectionString);

            Skip.If(!catalog.Available,
                "the SQL catalog could not be read, so no inventory can be taken: " + catalog.UnavailableReason);

            var manifests = ScratchManifestReader.ReadDirectory();
            var world = ScratchLifecycleBuilder.Build(manifests);

            var inspector = new SystemProcessInspector();
            var assessment = ScratchOrphanClassifier.Classify(
                world, catalog, run => ScratchRunLiveness.Assess(run, inspector, Environment.MachineName));

            // Output only. Nothing is written to the repository, and SQL-Evidence-Summary.md is untouched.
            _output.WriteLine(ScratchOrphanReport.Render(assessment));

            // The only assertions are about the DETECTOR, not about the instance.
            Assert.Contains(ScratchOrphanReport.NoCleanupStatement, ScratchOrphanReport.Render(assessment));
            Assert.True(assessment.CatalogAvailable, "the snapshot reported itself available and then was not");

            // Every finding must carry a recommended action a person can act on. A classification with no
            // stated next step is how an orphan report turns into an unexamined deletion list.
            Assert.All(assessment.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.RecommendedAction)));
        }
    }
}
