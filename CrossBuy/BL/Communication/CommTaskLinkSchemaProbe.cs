using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // THE ACTIVATION CHECK — does the database REALLY have CommTaskLinks?
    //
    // WHY A FILE EXISTING IS NOT AN ANSWER. deploy/sql/comm_task_links.sql being present in the repo,
    // registered in the manifest and hash-matched says the script is ready to run. It says nothing
    // whatever about whether anyone ran it against the database this process is connected to. Those are
    // different facts, and HM-D44 is what conflating them costs: a missing table surfaced as a SQL-208
    // in the middle of a sale rather than as a red check beforehand.
    //
    // IT FAILS CLOSED, ALWAYS. Absent table, missing index, missing constraint, wrong filter, a provider
    // that cannot answer, a thrown exception — every one of them reports NOT provisioned. The only path
    // to "provisioned" is every expected object being found and verified. A probe that guessed "probably
    // fine" when it could not tell would be worse than no probe, because it would be believed.
    //
    // IT IS A READ. Nothing here creates, alters or repairs anything: it asks the system catalogues what
    // exists. Provisioning is a deployment act performed by a human with authorization, never a side
    // effect of a service starting up.
    // =============================================================================================
    public sealed class CommTaskLinkSchemaStatus
    {
        public required bool Provisioned { get; init; }

        // Every expected object that was NOT found, named. An operator should learn what to fix from the
        // check itself rather than by reading the DDL and comparing by hand.
        public required IReadOnlyList<string> Missing { get; init; }

        // Set when the probe could not determine the answer at all — a provider without system
        // catalogues, or a failed query. Distinct from "checked and absent", because the remedies differ.
        public string? Inconclusive { get; init; }

        public static CommTaskLinkSchemaStatus NotProvisioned(string reason) =>
            new() { Provisioned = false, Missing = Array.Empty<string>(), Inconclusive = reason };
    }

    public interface ICommTaskLinkSchemaProbe
    {
        Task<CommTaskLinkSchemaStatus> CheckAsync(CancellationToken cancellationToken = default);
    }

    public sealed class CommTaskLinkSchemaProbe : ICommTaskLinkSchemaProbe
    {
        // The exact objects deploy/sql/comm_task_links.sql creates. Named here so the check and the DDL
        // can be compared by a person in one glance, and so a renamed index fails the check instead of
        // passing it silently.
        public const string TableName = "CommTaskLinks";
        // TWO filtered unique indexes since comm_task_links_m2m. They partition the table on
        // CommentId IS NULL / IS NOT NULL: one conversation-level link per (company, task, thread),
        // one message-level link per (company, task, thread, comment), coexisting. A database still
        // carrying only the superseded UX_CommTaskLinks_Live reports NOT provisioned — correctly,
        // because the application's read-before-write now assumes the message-level rule and the old
        // single index would refuse the second link under a race.
        public const string ConversationIndexName = "UX_CommTaskLinks_LiveConversation";
        public const string MessageIndexName = "UX_CommTaskLinks_LiveMessage";
        public const string ThreadIndexName = "IX_CommTaskLinks_Thread";
        public const string TaskIndexName = "IX_CommTaskLinks_Task";
        public const string OriginCheckName = "CK_CommTaskLinks_Origin";
        public const string ThreadForeignKeyName = "FK_CommTaskLinks_Thread";

        private readonly CrossDbContext _db;
        private readonly ILogger<CommTaskLinkSchemaProbe> _log;

        public CommTaskLinkSchemaProbe(CrossDbContext db, ILogger<CommTaskLinkSchemaProbe> log)
        { _db = db; _log = log; }

        public async Task<CommTaskLinkSchemaStatus> CheckAsync(CancellationToken cancellationToken = default)
        {
            // System catalogues are SQL Server's. Against any other provider the honest answer is "I
            // cannot tell", which is NOT provisioned — never an optimistic pass.
            if (!_db.Database.IsSqlServer())
                return CommTaskLinkSchemaStatus.NotProvisioned(
                    "the connected provider is not SQL Server, so the system catalogues cannot be read");

            try
            {
                var missing = new List<string>();

                if (!await ExistsAsync(
                        "SELECT COUNT(*) AS Value FROM sys.tables WHERE name = 'CommTaskLinks'",
                        cancellationToken))
                {
                    // No table means every other object is missing too. Reporting the table alone keeps
                    // the message about the cause rather than burying it in five consequences.
                    return new CommTaskLinkSchemaStatus
                    {
                        Provisioned = false,
                        Missing = new[] { "table " + TableName },
                    };
                }

                // The UNIQUE index is the one object the application cannot substitute for. Read-before-
                // write makes the common retry cheap; only this index refuses the second row under a
                // race. Its uniqueness AND its filter are both verified: a non-filtered unique index
                // would block relinking a pair that was once unlinked, and a non-unique one would permit
                // the duplicate relationship the key exists to prevent.
                foreach (var uniqueIndex in new[] { ConversationIndexName, MessageIndexName })
                    if (!await ExistsAsync(
                            $@"SELECT COUNT(*) AS Value FROM sys.indexes
                               WHERE name = '{uniqueIndex}'
                                 AND object_id = OBJECT_ID('dbo.{TableName}')
                                 AND is_unique = 1
                                 AND has_filter = 1", cancellationToken))
                        missing.Add($"unique filtered index {uniqueIndex}");

                foreach (var index in new[] { ThreadIndexName, TaskIndexName })
                    if (!await ExistsAsync(
                            $@"SELECT COUNT(*) AS Value FROM sys.indexes
                               WHERE name = '{index}' AND object_id = OBJECT_ID('dbo.{TableName}')",
                            cancellationToken))
                        missing.Add("index " + index);

                if (!await ExistsAsync(
                        $"SELECT COUNT(*) AS Value FROM sys.check_constraints WHERE name = '{OriginCheckName}'",
                        cancellationToken))
                    missing.Add("check constraint " + OriginCheckName);

                if (!await ExistsAsync(
                        $"SELECT COUNT(*) AS Value FROM sys.foreign_keys WHERE name = '{ThreadForeignKeyName}'",
                        cancellationToken))
                    missing.Add("foreign key " + ThreadForeignKeyName);

                return new CommTaskLinkSchemaStatus
                {
                    Provisioned = missing.Count == 0,
                    Missing = missing,
                };
            }
            catch (Exception ex)
            {
                // A probe that cannot run has not found a schema. Reported, never swallowed into a pass.
                _log.LogError(ex, "CommTaskLinks schema probe failed; treating the schema as absent.");
                return CommTaskLinkSchemaStatus.NotProvisioned(
                    ex.GetType().Name + ": the schema check could not complete");
            }
        }

        private async Task<bool> ExistsAsync(string sql, CancellationToken cancellationToken)
        {
            var rows = await _db.Database.SqlQueryRaw<int>(sql).ToListAsync(cancellationToken);
            return rows.Count > 0 && rows[0] > 0;
        }
    }
}
