using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // THE ACTIVATION CHECK for CommPushTokens — does the database REALLY have it?
    //
    // Same question, same answer-shape and the same fail-closed rule as CommTaskLinkSchemaProbe; read
    // that file for why a .sql file existing in the repository is not an answer. The objects differ,
    // so the probe does, but nothing else about the reasoning is new here.
    //
    // IT IS A READ. Nothing creates, alters or repairs. Provisioning is a deployment act performed by
    // a human with authorization, never a side effect of a service starting up.
    // =============================================================================================
    public sealed class CommPushTokenSchemaStatus
    {
        public required bool Provisioned { get; init; }
        public required IReadOnlyList<string> Missing { get; init; }
        public string? Inconclusive { get; init; }

        public static CommPushTokenSchemaStatus NotProvisioned(string reason) =>
            new() { Provisioned = false, Missing = Array.Empty<string>(), Inconclusive = reason };
    }

    public interface ICommPushTokenSchemaProbe
    {
        Task<CommPushTokenSchemaStatus> CheckAsync(CancellationToken cancellationToken = default);
    }

    public sealed class CommPushTokenSchemaProbe : ICommPushTokenSchemaProbe
    {
        // The exact objects deploy/sql/comm_push_tokens.sql creates. Named here so the check and the
        // DDL can be compared by a person in one glance, and so a renamed index fails the check
        // instead of passing it silently.
        public const string TableName = "CommPushTokens";
        public const string LiveIndexName = "UX_CommPushTokens_Live";
        public const string RecipientIndexName = "IX_CommPushTokens_Recipient";
        public const string PlatformCheckName = "CK_CommPushTokens_Platform";

        private readonly CrossDbContext _db;
        private readonly ILogger<CommPushTokenSchemaProbe> _log;

        public CommPushTokenSchemaProbe(CrossDbContext db, ILogger<CommPushTokenSchemaProbe> log)
        { _db = db; _log = log; }

        public async Task<CommPushTokenSchemaStatus> CheckAsync(CancellationToken cancellationToken = default)
        {
            // System catalogues are SQL Server's. Against any other provider the honest answer is "I
            // cannot tell", which is NOT provisioned — never an optimistic pass.
            if (!_db.Database.IsSqlServer())
                return CommPushTokenSchemaStatus.NotProvisioned(
                    "the connected provider is not SQL Server, so the system catalogues cannot be read");

            try
            {
                var missing = new List<string>();

                if (!await ExistsAsync(
                        $"SELECT COUNT(*) AS Value FROM sys.tables WHERE name = '{TableName}'",
                        cancellationToken))
                {
                    // No table means every other object is missing too. Reporting the table alone
                    // keeps the message about the cause rather than burying it in three consequences.
                    return new CommPushTokenSchemaStatus
                    {
                        Provisioned = false,
                        Missing = new[] { "table " + TableName },
                    };
                }

                // THE UNIQUE INDEX IS THE ONE OBJECT THE APPLICATION CANNOT SUBSTITUTE FOR, and both
                // its uniqueness AND its filter are verified. Un-filtered, soft revocation becomes
                // impossible — the retired row would permanently own the token value, so a device that
                // re-registers after revocation could never enrol again. Non-unique, two live rows
                // could hold one token and the same device would be pushed to twice.
                if (!await ExistsAsync(
                        $@"SELECT COUNT(*) AS Value FROM sys.indexes
                           WHERE name = '{LiveIndexName}'
                             AND object_id = OBJECT_ID('dbo.{TableName}')
                             AND is_unique = 1
                             AND has_filter = 1", cancellationToken))
                    missing.Add($"unique filtered index {LiveIndexName}");

                if (!await ExistsAsync(
                        $@"SELECT COUNT(*) AS Value FROM sys.indexes
                           WHERE name = '{RecipientIndexName}' AND object_id = OBJECT_ID('dbo.{TableName}')",
                        cancellationToken))
                    missing.Add("index " + RecipientIndexName);

                // The platform vocabulary, in the schema as well as in CommPushPlatform. Without it a
                // token for a transport this product cannot address reaches the table and a
                // notification is addressed to a device nobody can reach.
                if (!await ExistsAsync(
                        $"SELECT COUNT(*) AS Value FROM sys.check_constraints WHERE name = '{PlatformCheckName}'",
                        cancellationToken))
                    missing.Add("check constraint " + PlatformCheckName);

                return new CommPushTokenSchemaStatus
                {
                    Provisioned = missing.Count == 0,
                    Missing = missing,
                };
            }
            catch (Exception ex)
            {
                // A probe that cannot run has not found a schema. Reported, never swallowed into a pass.
                _log.LogError(ex, "CommPushTokens schema probe failed; treating the schema as absent.");
                return CommPushTokenSchemaStatus.NotProvisioned(
                    ex.GetType().Name + ": the schema check could not complete");
            }
        }

        private async Task<bool> ExistsAsync(string sql, CancellationToken cancellationToken)
        {
            var rows = await _db.Database.SqlQueryRaw<int>(sql).ToListAsync(cancellationToken);
            return rows.Count > 0 && rows[0] > 0;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The cached answer, and the store that reads it. Both are thin by design: the caching rule lives
    // in CommSchemaProvisioning and is shared with task links, so there is one place where "when do
    // we re-probe" is decided for the whole platform.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommPushTokenProvisioning
    {
        private readonly CommSchemaProvisioning _inner;

        public CommPushTokenProvisioning() : this(() => DateTimeOffset.UtcNow) { }

        public CommPushTokenProvisioning(Func<DateTimeOffset> now)
            => _inner = new CommSchemaProvisioning(CommPushTokenSchemaProbe.TableName, now);

        public string? Reason => _inner.Reason;

        public bool IsProvisioned(ICommPushTokenSchemaProbe probe, ILogger? log = null)
            => _inner.IsProvisioned(ct => AnswerAsync(probe, ct), log);

        public Task<bool> ResolveAsync(
            ICommPushTokenSchemaProbe probe, ILogger? log = null, CancellationToken cancellationToken = default)
            => _inner.ResolveAsync(ct => AnswerAsync(probe, ct), log, cancellationToken);

        private static async Task<CommSchemaAnswer> AnswerAsync(
            ICommPushTokenSchemaProbe probe, CancellationToken cancellationToken)
        {
            var status = await probe.CheckAsync(cancellationToken);
            return new CommSchemaAnswer
            {
                Provisioned = status.Provisioned,
                Missing = status.Missing,
                Inconclusive = status.Inconclusive,
            };
        }
    }

    public sealed class ProbingCommPushTokenStore : ICommPushTokenStore
    {
        private static readonly UnprovisionedCommPushTokenStore Off = new();

        private readonly EfCommPushTokenStore _persistent;
        private readonly ICommPushTokenSchemaProbe _probe;
        private readonly CommPushTokenProvisioning _provisioning;
        private readonly ILogger<ProbingCommPushTokenStore> _log;

        public ProbingCommPushTokenStore(
            EfCommPushTokenStore persistent,
            ICommPushTokenSchemaProbe probe,
            CommPushTokenProvisioning provisioning,
            ILogger<ProbingCommPushTokenStore> log)
        {
            _persistent = persistent;
            _probe = probe;
            _provisioning = provisioning;
            _log = log;
        }

        public bool IsProvisioned => _provisioning.IsProvisioned(_probe, _log);

        // GATED ON EVERY MEMBER, not only on IsProvisioned — a caller that skips the gate must still
        // not reach the Ef store against a database without the table.
        private ICommPushTokenStore Inner => IsProvisioned ? _persistent : (ICommPushTokenStore)Off;

        public Task<IReadOnlyList<CrossBuy.Models.Communication.CommPushToken>> ForRecipientAsync(
            int companyId, int employeeId, CancellationToken cancellationToken = default)
            => Inner.ForRecipientAsync(companyId, employeeId, cancellationToken);

        public Task<CrossBuy.Models.Communication.CommPushToken?> RegisterAsync(
            int companyId, int employeeId, string platform, string token, int? registeredBy,
            CancellationToken cancellationToken = default)
            => Inner.RegisterAsync(companyId, employeeId, platform, token, registeredBy, cancellationToken);

        public Task<bool> RevokeAsync(
            int companyId, string token, string? reason, int? revokedBy,
            CancellationToken cancellationToken = default)
            => Inner.RevokeAsync(companyId, token, reason, revokedBy, cancellationToken);
    }
}
