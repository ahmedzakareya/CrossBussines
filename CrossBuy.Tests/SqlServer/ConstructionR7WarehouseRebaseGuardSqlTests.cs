using CrossBuy.Tests.TestSupport;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(UatSqlProbeCollection.Name)]
public sealed class ConstructionR7WarehouseRebaseGuardSqlTests(UatSqlProbeFixture sql)
{
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Native_mapping_change_cannot_create_or_admit_an_unbound_rebased_request(bool pending)
    {
        Skip.If(!sql.Available, sql.SkipReason);
        var probe = await sql.CreateProbeDatabaseAsync("R7WHREBASEGUARD");
        try
        {
            using var db = sql.ContextFor(probe, 1);
            await ConstructionR7WarehouseRebaseGuardTests.Exercise(db, () => sql.ContextFor(probe, 1), pending, false);
        }
        finally { await sql.DropProbeDatabaseAsync(probe); }
    }
}
