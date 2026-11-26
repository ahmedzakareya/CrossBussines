using CrossBuy.BL.Communication;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CrossBuy.Tests.Communication;

// =================================================================================================
// THE ACTIVATION CHECK, RUN AGAINST A REAL DATABASE.
//
// WHY THE SQLITE SUITE IS NOT THIS. EfCommTaskLinkStoreTests proves the probe FAILS CLOSED: on a
// provider whose catalogues it cannot read, and on a database where the table exists but the indexes
// and constraints do not. Both of those are negative proofs. Neither of them can ever return
// Provisioned == true, by construction — so nothing in that suite establishes that the probe says yes
// when the schema really is there. Only a SQL Server database that has had comm_task_links.sql applied
// can do that, and that is the one fact a deployment needs before TAB-3 switches the store.
//
// IT NAMES ITS OWN VARIABLE, DELIBERATELY. CROSSBUY_TEST_SQL is SqlServerFixture's, it is persisted at
// USER scope on at least one machine here, and it points at an instance where scratch databases are
// created and dropped. Reading it would make this test run by default against a throwaway database and
// report "activated" about a catalogue nobody deployed to. A separate variable means this check runs
// only when someone deliberately aims it at the database they actually care about.
//
// IT ONLY READS. The probe issues five COUNT(*) queries against sys.tables, sys.indexes,
// sys.check_constraints and sys.foreign_keys. Nothing here creates, drops, migrates or seeds - and the
// context is built through the options-only constructor, whose unresolved company scope filters the
// pilot entities to nothing, so no business data is reachable through it even by accident.
// =================================================================================================
public sealed class CommTaskLinkActivationLiveTests
{
    // Not SqlServerFixture.ConnectionStringVariable. See the note above - that one is the scratch
    // instance, and pointing an activation check at a disposable database is the exact confusion
    // HM-D44 is about.
    public const string ConnectionStringVariable = "CROSSBUY_COMMTASKLINK_ACTIVATION_SQL";

    // A scratch database can be made to satisfy the probe by running the slice against it, and then
    // this test would pass while saying nothing about the deployment. The names SqlServerFixture mints
    // are refused outright rather than skipped, because a run aimed at one is a mistake, not an
    // absence.
    private static readonly string[] ScratchPrefixes = { "CrossBuyProbe_", "CrossBuyPlatformTest_" };

    private readonly ITestOutputHelper _output;

    public CommTaskLinkActivationLiveTests(ITestOutputHelper output) { _output = output; }

    [SkippableFact]
    public async Task The_probe_reports_provisioned_against_a_database_the_slice_has_been_applied_to()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            $"{ConnectionStringVariable} is not set. The activation check needs a SQL Server database " +
            "that comm_task_links.sql has been applied to; it is skipped rather than reported as " +
            "provisioned.");

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        foreach (var prefix in ScratchPrefixes)
            Assert.False(builder.InitialCatalog.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                $"'{builder.InitialCatalog}' is a scratch database. Applying the slice to a throwaway " +
                "catalogue and then checking it proves nothing about the deployed one.");

        var options = new DbContextOptionsBuilder<CrossDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        using var db = new CrossDbContext(options);

        var status = await new CommTaskLinkSchemaProbe(db, NullLogger<CommTaskLinkSchemaProbe>.Instance)
            .CheckAsync();

        _output.WriteLine($"database     : {builder.InitialCatalog}");
        _output.WriteLine($"provisioned  : {status.Provisioned}");
        _output.WriteLine($"inconclusive : {status.Inconclusive ?? "(none)"}");
        _output.WriteLine($"missing      : {(status.Missing.Count == 0 ? "(none)" : string.Join(", ", status.Missing))}");

        // Inconclusive first: it is the one outcome that would otherwise be read as a plain "no". A
        // provider that cannot answer and a catalogue that genuinely lacks the table need different
        // remedies, and the message is what tells them apart.
        Assert.True(status.Inconclusive is null,
            $"the probe could not complete the check: {status.Inconclusive}");

        Assert.Empty(status.Missing);
        Assert.True(status.Provisioned,
            "the probe read the catalogues and did not find the schema the slice creates");
    }
}
