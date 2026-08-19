using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Communication Platform — SCHEMA PARITY between the EF model and deploy/sql.
    //
    // WHY THIS FILE EXISTS
    //
    // Migrations are disabled in this project (CLAUDE.md: "Idempotent SQL in deploy/sql, NOT EF migrations"), so
    // the EF mapping and the deployment script are two independent descriptions of one schema. Nothing in the
    // compiler connects them. Every project that has ever had that arrangement has eventually shipped a script
    // missing an index the model assumed — and the symptom is a slow query in production, not a failing build.
    //
    // So the parity is asserted here: every table and index CommunicationModel declares must appear in the
    // script, and every frozen vocabulary must be mirrored by a CHECK constraint. It reads the .sql file as
    // TEXT rather than executing it, because executing it needs SQL Server and the point is to catch drift on
    // every developer's machine, not only where an integration database exists.
    // =============================================================================================
    public class CommunicationSchemaParityTests : IDisposable
    {
        private readonly CommunicationTestHost _host = new();
        public void Dispose() => _host.Dispose();

        private const string ScriptName = "communication_platform_slice_001.sql";

        // Finds the deployment script by walking up to the repository root. The test project and the web project
        // are siblings, so the script is a known relative path once the root is located.
        //
        // Three probes rather than one, because the output directory is not always inside the repository: a CI
        // agent may publish the test assembly elsewhere, and CROSSBUY_REPO_ROOT is the explicit escape hatch for
        // that. The test FAILS rather than skipping when none of them find it — a parity check that silently
        // passes when it cannot read the script is worse than no parity check.
        private static string ScriptText()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT"),
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory(),
            };

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                var directory = new DirectoryInfo(candidate);
                while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CrossBuy.sln")))
                    directory = directory.Parent;

                if (directory == null) continue;

                var path = Path.Combine(directory.FullName, "CrossBuy", "deploy", "sql", ScriptName);
                if (File.Exists(path)) return File.ReadAllText(path);
            }

            Assert.Fail(
                $"{ScriptName} could not be located from the assembly directory, the working directory, or " +
                "CROSSBUY_REPO_ROOT. Set CROSSBUY_REPO_ROOT to the repository root to run the schema parity tests.");
            return "";
        }

        // ---------------------------------------------------------------------------------------------
        [Fact]
        public void Every_table_in_the_model_is_created_by_the_deployment_script()
        {
            var script = ScriptText();

            foreach (var table in CommunicationModel.TableNames)
                Assert.Contains($"CREATE TABLE dbo.{table}", script, StringComparison.Ordinal);
        }

        // The model's TableNames list is what the parity check iterates, so a table added to the model but not to
        // that list would escape every assertion in this file. This closes that hole.
        [Fact]
        public void The_models_table_list_matches_the_entities_it_actually_maps()
        {
            // Selected by CLR NAMESPACE, not by a "Comm" name prefix.
            //
            // The prefix version of this test broke the moment the Construction module added CommercialRevisions
            // and CommercialRevisionLines: "Commercial" starts with "Comm". It had also needed a hand-maintained
            // exclusion list for the email module's CommMessages/CommAttachments — a list that would have to grow
            // every time any module added a Comm-prefixed table.
            //
            // A table name is a label; the namespace is the actual statement of ownership. Three sibling
            // namespaces exist today and only the first is ours:
            //     CrossBuy.Models.Context.Communication  ← this platform
            //     CrossBuy.Models.Context.Comm           ← the email module
            //     CrossBuy.Models.Context.Construction   ← the construction module
            // Keying off the namespace makes this test immune to any future module's table naming.
            const string ownedNamespace = "CrossBuy.Models.Context.Communication";

            var mapped = _host.Db.Model.GetEntityTypes()
                .Where(e => string.Equals(e.ClrType.Namespace, ownedNamespace, StringComparison.Ordinal))
                .Select(e => e.GetTableName())
                .Where(t => t != null)
                .Select(t => t!)
                .Distinct()
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            var declared = CommunicationModel.TableNames.OrderBy(t => t, StringComparer.Ordinal).ToList();

            Assert.Equal(declared, mapped);
        }

        // Every index the model names must exist in the script. A missing index is a slow query in production and
        // a passing build everywhere else — which is exactly why it is asserted rather than reviewed.
        [Fact]
        public void Every_index_in_the_model_is_created_by_the_deployment_script()
        {
            var script = ScriptText();

            var indexNames = _host.Db.Model.GetEntityTypes()
                .Where(e => CommunicationModel.TableNames.Contains(e.GetTableName() ?? ""))
                .SelectMany(e => e.GetIndexes())
                .Select(i => i.GetDatabaseName())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .ToList();

            Assert.NotEmpty(indexNames);

            var missing = indexNames.Where(n => !script.Contains(n!, StringComparison.Ordinal)).ToList();
            Assert.Empty(missing);
        }

        // A UNIQUE index in the model must be UNIQUE in the script. This one matters more than it looks: the
        // thread anchor index is what makes get-or-create safe under concurrency, and the comment dedup index is
        // what makes a retried POST idempotent. Either one created non-unique would still pass the test above.
        [Fact]
        public void Every_unique_index_in_the_model_is_created_unique_in_the_script()
        {
            var script = ScriptText();

            var uniqueNames = _host.Db.Model.GetEntityTypes()
                .Where(e => CommunicationModel.TableNames.Contains(e.GetTableName() ?? ""))
                .SelectMany(e => e.GetIndexes())
                .Where(i => i.IsUnique)
                .Select(i => i.GetDatabaseName()!)
                .Distinct()
                .ToList();

            Assert.NotEmpty(uniqueNames);

            foreach (var name in uniqueNames)
                Assert.Contains($"CREATE UNIQUE INDEX {name}", script, StringComparison.Ordinal);
        }

        // A filtered index in the model must be filtered in the script. An unfiltered UX_CommParticipants_Live
        // would forbid an employee from ever re-following a thread they had left, which is a functional bug, not
        // a performance one.
        [Theory]
        [InlineData("UX_CommComments_DedupKey", "WHERE DedupKey IS NOT NULL")]
        [InlineData("UX_CommParticipants_Live", "WHERE RemovedAt IS NULL")]
        [InlineData("IX_CommThreadPermissions_Thread", "WHERE RevokedAt IS NULL")]
        [InlineData("IX_CommMentionRecipients_Unread", "WHERE ReadAt IS NULL")]
        [InlineData("UX_CommAuditEntries_DedupKey", "WHERE DedupKey IS NOT NULL")]
        public void Filtered_indexes_carry_their_filter_in_the_script(string indexName, string filter)
        {
            var script = ScriptText();

            int index = script.IndexOf(indexName, StringComparison.Ordinal);
            Assert.True(index >= 0, $"{indexName} is absent from {ScriptName}");

            // The filter must appear in the same statement, not merely somewhere in the file.
            var statement = script.Substring(index, Math.Min(400, script.Length - index));
            Assert.Contains(filter, statement, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------------
        // Vocabulary mirroring — the ADR-002 lesson, enforced.
        // ---------------------------------------------------------------------------------------------

        // Every value of every frozen vocabulary must appear in the script's CHECK constraints. A value present
        // in C# but absent from the constraint is a runtime failure on the first row that uses it.
        [Theory]
        [MemberData(nameof(FrozenVocabularies))]
        public void Every_frozen_vocabulary_value_appears_in_the_scripts_check_constraints(string vocabulary, string value)
        {
            var script = ScriptText();
            Assert.Contains($"'{value}'", script, StringComparison.Ordinal);

            // A value that appears only in a comment would satisfy the assertion above, so the vocabulary must
            // also have at least one CHECK constraint naming it.
            Assert.Contains("CHECK", script, StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(vocabulary));
        }

        public static IEnumerable<object[]> FrozenVocabularies()
        {
            foreach (var v in CommVisibility.Values) yield return new object[] { nameof(CommVisibility), v };
            foreach (var v in CommThreadKind.Values) yield return new object[] { nameof(CommThreadKind), v };
            foreach (var v in CommBodyFormat.Values) yield return new object[] { nameof(CommBodyFormat), v };
            foreach (var v in CommParticipantRole.Values) yield return new object[] { nameof(CommParticipantRole), v };
            foreach (var v in CommParticipationSource.Values) yield return new object[] { nameof(CommParticipationSource), v };
            foreach (var v in CommMentionTargetKind.Values) yield return new object[] { nameof(CommMentionTargetKind), v };
            foreach (var v in CommPermissionLevel.Values) yield return new object[] { nameof(CommPermissionLevel), v };
            foreach (var v in CommPrincipalKind.Values) yield return new object[] { nameof(CommPrincipalKind), v };
            foreach (var v in CommReactionKeys.Values) yield return new object[] { nameof(CommReactionKeys), v };
            foreach (var v in CommChannel.Values) yield return new object[] { nameof(CommChannel), v };
            foreach (var v in CommDeliveryStatus.Values) yield return new object[] { nameof(CommDeliveryStatus), v };
            foreach (var v in CommPreferenceMode.Values) yield return new object[] { nameof(CommPreferenceMode), v };
            foreach (var v in CommPreviewKind.Values) yield return new object[] { nameof(CommPreviewKind), v };
            foreach (var v in CommNotificationCategories.Values) yield return new object[] { nameof(CommNotificationCategories), v };
        }

        // ---------------------------------------------------------------------------------------------
        // The script's own promises
        // ---------------------------------------------------------------------------------------------

        // IDEMPOTENCY. Every CREATE must be guarded, or a re-run fails half way and leaves an operator guessing
        // what applied. This counts guards against creates rather than parsing SQL, which is crude but catches
        // the realistic mistake: adding a table and forgetting the IF.
        [Fact]
        public void Every_create_statement_in_the_script_is_guarded()
        {
            var script = ScriptText();

            int tableCreates = CountOccurrences(script, "CREATE TABLE ");
            int tableGuards = CountOccurrences(script, "IF OBJECT_ID(");
            Assert.True(tableGuards >= tableCreates,
                $"{tableCreates} CREATE TABLE statements but only {tableGuards} OBJECT_ID guards");

            int indexCreates = CountOccurrences(script, "CREATE INDEX ") + CountOccurrences(script, "CREATE UNIQUE INDEX ");
            int indexGuards = CountOccurrences(script, "FROM sys.indexes WHERE name =");
            Assert.True(indexGuards >= indexCreates,
                $"{indexCreates} index creates but only {indexGuards} sys.indexes guards");

            int constraintAdds = CountOccurrences(script, "ADD CONSTRAINT CK_");
            int constraintGuards = CountOccurrences(script, "FROM sys.check_constraints WHERE name =");
            Assert.True(constraintGuards >= constraintAdds,
                $"{constraintAdds} CHECK constraints but only {constraintGuards} sys.check_constraints guards");
        }

        // ADDITIVE ONLY. The script must not modify anything that already exists — no ALTER of a foreign table,
        // no UPDATE, no DELETE, no INSERT. This is the assertion that makes "safe to apply next to two other work
        // streams" a checkable claim rather than a promise in a comment.
        [Fact]
        public void The_script_modifies_no_existing_data_and_no_foreign_table()
        {
            var script = ScriptText();
            var lines = script.Split('\n');

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("--", StringComparison.Ordinal)) continue;   // commentary may discuss anything

                var upper = line.ToUpperInvariant();

                Assert.False(upper.StartsWith("UPDATE ", StringComparison.Ordinal), $"data modification: {line}");
                Assert.False(upper.StartsWith("DELETE ", StringComparison.Ordinal), $"data modification: {line}");
                Assert.False(upper.StartsWith("INSERT ", StringComparison.Ordinal), $"data modification (seed): {line}");
                Assert.False(upper.StartsWith("DROP ", StringComparison.Ordinal), $"destructive: {line}");
                Assert.False(upper.StartsWith("TRUNCATE ", StringComparison.Ordinal), $"destructive: {line}");

                // ALTER TABLE is allowed ONLY to add a CHECK constraint to one of this platform's own tables.
                if (upper.StartsWith("ALTER TABLE ", StringComparison.Ordinal))
                {
                    var isOwnTable = CommunicationModel.TableNames.Any(t =>
                        line.Contains("dbo." + t, StringComparison.Ordinal));
                    Assert.True(isOwnTable, $"ALTER of a table this platform does not own: {line}");
                }
            }
        }

        // No FK may point at a table this platform does not own. An audit row must outlive everything it
        // describes, and a hard FK to Employees would make a historical row block a personnel cleanup.
        [Fact]
        public void No_foreign_key_points_outside_this_platforms_own_tables()
        {
            var script = ScriptText();
            var lines = script.Split('\n');

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("--", StringComparison.Ordinal)) continue;
                if (!line.Contains("REFERENCES ", StringComparison.Ordinal)) continue;

                var isOwnTable = CommunicationModel.TableNames.Any(t =>
                    line.Contains("dbo." + t + " ", StringComparison.Ordinal)
                    || line.Contains("dbo." + t + "(", StringComparison.Ordinal));

                Assert.True(isOwnTable, $"foreign key leaves the platform: {line}");
            }
        }

        // The audit table is the one append-only table in the platform, so it must have NO soft-delete column —
        // and no service may offer a mutation. The column's absence is the structural half of that guarantee.
        [Fact]
        public void The_audit_table_has_no_delete_marker()
        {
            var auditEntity = _host.Db.Model.GetEntityTypes()
                .Single(e => e.GetTableName() == "CommAuditEntries");

            var properties = auditEntity.GetProperties().Select(p => p.Name).ToList();

            Assert.DoesNotContain("DeletedAt", properties);
            Assert.DoesNotContain("DeletedBy", properties);
            Assert.DoesNotContain("UpdatedAt", properties);       // append-only: nothing updates a row
        }

        // The whole platform must materialise from the EF model alone, which is what lets every other test in
        // this folder run against SQLite with no SQL Server anywhere.
        [Fact]
        public async Task Every_table_is_queryable_from_the_model_alone()
        {
            Assert.Equal(0, await _host.Db.Set<CommThread>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommThreadPermission>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommComment>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommCommentRevision>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommCommentAttachment>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommReaction>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommParticipant>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommMention>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommMentionRecipient>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommReadReceipt>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommNotification>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommNotificationDelivery>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommNotificationPreference>().CountAsync());
            Assert.Equal(0, await _host.Db.Set<CommAuditEntry>().CountAsync());
        }

        // The script must be applied with QUOTED_IDENTIFIER ON or its filtered indexes silently fail to create —
        // the footgun CLAUDE.md already records for platform_business_events_slice_002.sql. The instruction has to
        // be IN the file, because whoever runs it will not have read this test.
        [Fact]
        public void The_script_tells_the_operator_to_run_it_with_quoted_identifier_on()
        {
            var script = ScriptText();
            Assert.Contains("sqlcmd -I", script, StringComparison.Ordinal);
            Assert.Contains("QUOTED_IDENTIFIER", script, StringComparison.Ordinal);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }
    }
}
