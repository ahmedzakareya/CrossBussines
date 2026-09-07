using Microsoft.Data.SqlClient;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // Stage 0 Batch B — the deployment scripts themselves, verified against a real SQL Server.
    //
    // Everything else in this project tests CODE against a schema. These tests test the SCHEMA: they run the exact
    // files that ship in deploy/sql, more than once, and then interrogate sys.* to prove each object the code depends
    // on actually exists with the properties it depends on. That matters because the deployment discipline is
    // "idempotent SQL applied before the code" — if a script is not genuinely re-runnable, a partial deploy or a
    // repeated deploy breaks production, and no amount of C# testing would show it.
    //
    // These run on a DISPOSABLE database the fixture creates and drops. Skipped — never silently passed — without
    // CROSSBUY_TEST_SQL, and the fixture REFUSES a connection string naming a real CrossBuy database.
    [Collection(SqlServerCollection.Name)]
    public class PlatformSchemaDeploymentTests
    {
        private readonly SqlServerFixture _sql;
        public PlatformSchemaDeploymentTests(SqlServerFixture sql) { _sql = sql; }

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        private Task<int> CountAsync(string sql) => _sql.ScalarAsync<int>(sql);

        private async Task AssertObjectExistsAsync(string what, string sql)
            => Assert.True(await CountAsync(sql) == 1, $"{what} is missing after deployment");

        // =======================================================================================
        // Slice 1 — BusinessEvents / BusinessEventDispatch
        // =======================================================================================

        // ---- Batch B item 45: slice 1 applied twice leaves exactly one of everything ----
        [SkippableFact]
        public async Task Slice1_is_idempotent_and_creates_every_object_the_kernel_depends_on()
        {
            Ready();
            // The fixture already applied it once at start-up; run it twice more. Three passes total.
            await _sql.ReapplySlice1Async(2);

            // ---- tables + primary keys ----
            await AssertObjectExistsAsync("BusinessEvents",
                "SELECT COUNT(*) FROM sys.tables WHERE name = 'BusinessEvents';");
            await AssertObjectExistsAsync("BusinessEventDispatch",
                "SELECT COUNT(*) FROM sys.tables WHERE name = 'BusinessEventDispatch';");
            await AssertObjectExistsAsync("BusinessEvents primary key",
                "SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'PK' AND parent_object_id = OBJECT_ID('BusinessEvents');");
            await AssertObjectExistsAsync("BusinessEventDispatch primary key",
                "SELECT COUNT(*) FROM sys.key_constraints WHERE type = 'PK' AND parent_object_id = OBJECT_ID('BusinessEventDispatch');");

            // EventId must be an identity column — the whole "no cursor" argument rests on identity being assigned
            // at INSERT and made visible at COMMIT.
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID('BusinessEvents') AND name = 'EventId';"));

            // ---- the foreign key that stops orphan queue rows ----
            await AssertObjectExistsAsync("FK_BusinessEventDispatch_Event",
                "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_BusinessEventDispatch_Event';");

            // ---- check constraints (the frozen vocabularies) ----
            foreach (var name in new[]
            {
                "CK_BusinessEvents_Visibility", "CK_BusinessEvents_PayloadVersion",
                "CK_BusinessEventDispatch_Status", "CK_BusinessEventDispatch_Attempts",
            })
            {
                Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM sys.check_constraints WHERE name = '{name}';"));
            }

            // ---- indexes, each with the property the code relies on ----
            var indexes = await _sql.StringsAsync(@"
SELECT i.name + '|' + CAST(i.is_unique AS VARCHAR(1)) + '|' + CAST(i.has_filter AS VARCHAR(1))
  FROM sys.indexes i
 WHERE i.object_id IN (OBJECT_ID('BusinessEvents'), OBJECT_ID('BusinessEventDispatch'))
   AND i.name IS NOT NULL
 ORDER BY i.name;");

            // name -> (unique, filtered). Uniqueness IS the idempotency guarantee for DedupKey and for
            // (EventId, Consumer); the filters are what keep the indexes small forever.
            Assert.Contains("IX_BusinessEvents_Entity|0|0", indexes);
            Assert.Contains("IX_BusinessEvents_CreatedAt|0|0", indexes);
            Assert.Contains("IX_BusinessEvents_Correlation|0|1", indexes);
            Assert.Contains("UX_BusinessEvents_DedupKey|1|1", indexes);
            Assert.Contains("UX_BusinessEvents_EventUid|1|0", indexes);
            Assert.Contains("UX_BusinessEventDispatch_Event_Consumer|1|0", indexes);
            Assert.Contains("IX_BusinessEventDispatch_Pending|0|1", indexes);

            // ---- three passes must not have produced duplicates under different auto-names ----
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('BusinessEvents') AND name = 'UX_BusinessEvents_DedupKey';"));
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('BusinessEventDispatch');"));
            // Visibility + PayloadVersion always; PayloadJson only on SQL Server 2016+ (ISJSON), so the expected
            // total is engine-dependent. Asserting the exact total is what would catch a duplicate created under an
            // auto-generated name by a second pass.
            int major = await _sql.ScalarAsync<int>("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT);");
            Assert.Equal(major >= 13 ? 3 : 2, await CountAsync(
                "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('BusinessEvents');"));
        }

        // ---- The claiming index really is filtered on Status <> 'Done' ----
        [SkippableFact]
        public async Task The_claiming_index_is_filtered_so_finished_work_leaves_it()
        {
            Ready();
            var filter = await _sql.ScalarAsync<string>(
                "SELECT filter_definition FROM sys.indexes WHERE name = 'IX_BusinessEventDispatch_Pending';");

            Assert.False(string.IsNullOrWhiteSpace(filter));
            Assert.Contains("Done", filter!);
            // A Done row must be OUT of the index, otherwise the index grows with the event log forever.
            Assert.Contains("<>", filter!.Replace("!=", "<>"));
        }

        // ---- The version-gated JSON constraint: present on 2016+, and genuinely enforced ----
        [SkippableFact]
        public async Task The_payload_json_constraint_is_present_and_enforced_on_a_supported_engine()
        {
            Ready();
            int major = await _sql.ScalarAsync<int>("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT);");
            int constraintCount = await CountAsync(
                "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_BusinessEvents_PayloadJson';");

            if (major < 13)
            {
                // Documented behaviour, not a failure: pre-2016 has no ISJSON, so the payload contract is enforced
                // by BusinessEventService alone. The test states this rather than skipping silently.
                Assert.Equal(0, constraintCount);
                return;
            }

            Assert.Equal(1, constraintCount);

            await _sql.ResetAsync();
            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();

            var bad = await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var command = new SqlCommand(
                    @"INSERT INTO BusinessEvents (EventUid, CompanyID, EntityType, EntityId, EventType, PayloadVersion, Visibility, Payload, CreatedAt)
                      VALUES (NEWID(), 1, 'SalesInvoice', 1, 'SalesInvoice.Created', 1, 'Internal', 'not json at all', SYSUTCDATETIME());", connection);
                await command.ExecuteNonQueryAsync();
            });
            Assert.Contains("CK_BusinessEvents_PayloadJson", bad.Message);

            // NULL and real JSON both pass — the constraint must not block a payload-free event.
            await using (var ok = new SqlCommand(
                @"INSERT INTO BusinessEvents (EventUid, CompanyID, EntityType, EntityId, EventType, PayloadVersion, Visibility, Payload, CreatedAt)
                  VALUES (NEWID(), 1, 'SalesInvoice', 1, 'SalesInvoice.Created', 1, 'Internal', NULL, SYSUTCDATETIME()),
                         (NEWID(), 1, 'SalesInvoice', 2, 'SalesInvoice.Created', 1, 'Internal', '{""a"":1}', SYSUTCDATETIME());", connection))
            {
                Assert.Equal(2, await ok.ExecuteNonQueryAsync());
            }
            await _sql.ResetAsync();
        }

        // ---- PayloadVersion and Attempts cannot go below their floors ----
        [SkippableFact]
        public async Task The_numeric_floor_constraints_are_enforced_not_decorative()
        {
            Ready();
            await _sql.ResetAsync();

            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();

            var badVersion = await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var command = new SqlCommand(
                    @"INSERT INTO BusinessEvents (EventUid, CompanyID, EntityType, EntityId, EventType, PayloadVersion, Visibility, CreatedAt)
                      VALUES (NEWID(), 1, 'SalesInvoice', 1, 'SalesInvoice.Created', 0, 'Internal', SYSUTCDATETIME());", connection);
                await command.ExecuteNonQueryAsync();
            });
            Assert.Contains("CK_BusinessEvents_PayloadVersion", badVersion.Message);

            await SqlServerFixture.InsertEventWithIdentityAsync(connection, null, null, "SalesInvoice", 1);
            var eventId = await _sql.ScalarAsync<long>("SELECT MAX(EventId) FROM BusinessEvents;");

            var badAttempts = await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var command = new SqlCommand(
                    "INSERT INTO BusinessEventDispatch (EventId, Consumer, Status, Attempts) VALUES (@e, 'TimelineProjection', 'Pending', -1);", connection);
                command.Parameters.AddWithValue("@e", eventId);
                await command.ExecuteNonQueryAsync();
            });
            Assert.Contains("CK_BusinessEventDispatch_Attempts", badAttempts.Message);

            await _sql.ResetAsync();
        }

        // ---- The dedup index is unique PER COMPANY and ignores NULL keys ----
        [SkippableFact]
        public async Task The_dedup_index_is_unique_per_company_and_ignores_null_keys()
        {
            Ready();
            await _sql.ResetAsync();

            await using var connection = new SqlConnection(_sql.TestConnectionString);
            await connection.OpenAsync();

            async Task<int> InsertAsync(int company, string? dedupKey)
            {
                await using var command = new SqlCommand(
                    @"INSERT INTO BusinessEvents (EventUid, CompanyID, EntityType, EntityId, EventType, PayloadVersion, Visibility, DedupKey, CreatedAt)
                      VALUES (NEWID(), @c, 'SalesInvoice', 1, 'SalesInvoice.Created', 1, 'Internal', @k, SYSUTCDATETIME());", connection);
                command.Parameters.AddWithValue("@c", company);
                command.Parameters.AddWithValue("@k", (object?)dedupKey ?? DBNull.Value);
                return await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(1, await InsertAsync(1, "SalesInvoice.Created:1"));
            // Same key, DIFFERENT company: allowed. Idempotency is per tenant, so this must not collide.
            Assert.Equal(1, await InsertAsync(2, "SalesInvoice.Created:1"));
            // Same key, same company: rejected by the unique index — that IS the idempotency guarantee.
            var duplicate = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(1, "SalesInvoice.Created:1"));
            Assert.Contains("UX_BusinessEvents_DedupKey", duplicate.Message);
            // Any number of NULL keys coexist, because the index is filtered.
            Assert.Equal(1, await InsertAsync(1, null));
            Assert.Equal(1, await InsertAsync(1, null));

            await _sql.ResetAsync();
        }

        // =======================================================================================
        // Slice 2 — Notifications entity addressing
        // =======================================================================================

        // ---- Batch B item 46: slice 2 applied twice adds the columns and indexes exactly once ----
        [SkippableFact]
        public async Task Slice2_is_idempotent_and_makes_notifications_entity_addressable()
        {
            Ready();
            await _sql.EnsureNotificationsAndSlice2Async(passes: 2);

            // ---- the two additive columns, nullable so every pre-existing row stays valid ----
            var columns = await _sql.StringsAsync(@"
SELECT c.name + '|' + t.name + '|' + CAST(c.is_nullable AS VARCHAR(1)) + '|' + CAST(c.max_length AS VARCHAR(10))
  FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
 WHERE c.object_id = OBJECT_ID('dbo.Notifications') AND c.name IN ('EntityType','EntityId')
 ORDER BY c.name;");

            Assert.Equal(2, columns.Count);
            Assert.Contains("EntityId|int|1|4", columns);
            Assert.Contains("EntityType|nvarchar|1|120", columns);   // nvarchar(60) => 120 bytes

            // ---- both indexes, both filtered, neither unique ----
            var indexes = await _sql.StringsAsync(@"
SELECT i.name + '|' + CAST(i.is_unique AS VARCHAR(1)) + '|' + CAST(i.has_filter AS VARCHAR(1))
  FROM sys.indexes i
 WHERE i.object_id = OBJECT_ID('dbo.Notifications')
   AND i.name IN ('IX_Notifications_Recipient_DedupKey','IX_Notifications_Entity')
 ORDER BY i.name;");

            Assert.Equal(2, indexes.Count);
            // NOT unique, deliberately: a duplicate dedup key must be a suppression concern, never a hard insert
            // failure inside the dispatch worker.
            Assert.Contains("IX_Notifications_Recipient_DedupKey|0|1", indexes);
            Assert.Contains("IX_Notifications_Entity|0|1", indexes);

            // ---- the second pass added nothing ----
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Notifications') AND name = 'EntityType';"));
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Notifications') AND name = 'IX_Notifications_Entity';"));

            // ---- and slice 2 did NOT touch the kernel tables. That is its whole claim: the second consumer is a
            // CODE change, because slice 1's dispatch schema already expresses per-consumer state.
            Assert.Equal(0, await CountAsync(@"
SELECT COUNT(*) FROM sys.columns
 WHERE object_id = OBJECT_ID('BusinessEventDispatch')
   AND name NOT IN ('ID','EventId','Consumer','Status','Attempts','Error','UpdatedAt');"));
        }

        // ---- Slice 2 writes no data: it is schema only ----
        [SkippableFact]
        public async Task Slice2_performs_no_backfill()
        {
            Ready();
            await _sql.EnsureNotificationsAndSlice2Async(passes: 1);

            await using (var connection = new SqlConnection(_sql.TestConnectionString))
            {
                await connection.OpenAsync();
                await using var seed = new SqlCommand(
                    @"DELETE FROM dbo.Notifications;
                      INSERT INTO dbo.Notifications (RecipientEmployeeID, TitleEn, Type, RefId, IsRead, CompanyID, DedupKey, CreatedAt)
                      VALUES (7, 'legacy row', 'purchase_invoice', 42, 0, 1, 'legacy:42', SYSUTCDATETIME());", connection);
                await seed.ExecuteNonQueryAsync();
            }

            // Re-apply. A script that guessed a mapping from (Type, RefId) onto a canonical entity code would show
            // up here as a populated EntityType. It must stay NULL — the columns are read for kernel rows only.
            await _sql.EnsureNotificationsAndSlice2Async(passes: 1);

            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Notifications;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Notifications WHERE EntityType IS NULL AND EntityId IS NULL;"));
            // The legacy pair is untouched too — no column was rewritten or renamed.
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Notifications WHERE Type = 'purchase_invoice' AND RefId = 42;"));
        }

        // =======================================================================================
        // Slice 3 — the email outbox (Stage 0 Batch A script, re-verified here alongside the others)
        // =======================================================================================

        // ---- Batch B item 47: slice 3 applied twice adds ClaimedAt, the filtered index and both checks ----
        [SkippableFact]
        public async Task Slice3_comm_outbox_is_idempotent_and_adds_only_what_it_claims()
        {
            Ready();
            // EnsureCommOutboxAsync builds the PRE-slice-3 table then applies the real script twice.
            await _sql.EnsureCommOutboxAsync();

            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('CommMessages') AND name = 'ClaimedAt';"));
            // UpdatedAt is INHERITED from BaseEntity and already exists in the base table — the script must not add a
            // second one, which is exactly the mistake that produced CS0108 in the entity.
            Assert.Equal(1, await CountAsync(
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('CommMessages') AND name = 'UpdatedAt';"));

            var index = await _sql.ScalarAsync<string>(
                "SELECT filter_definition FROM sys.indexes WHERE name = 'IX_CommMessages_Dispatch';");
            Assert.False(string.IsNullOrWhiteSpace(index));
            Assert.Contains("Sent", index!);   // finished mail leaves the claiming index

            foreach (var name in new[] { "CK_CommMessages_Status", "CK_CommMessages_Attempts" })
                Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM sys.check_constraints WHERE name = '{name}';"));
        }

        // =======================================================================================
        // Cross-cutting: the scripts do not touch financial tables
        // =======================================================================================

        // ---- The kernel owns three tables and no others ----
        [SkippableFact]
        public async Task The_platform_scripts_create_no_table_outside_the_kernel_and_its_outbox()
        {
            Ready();
            await _sql.ReapplySlice1Async(1);
            await _sql.EnsureNotificationsAndSlice2Async(passes: 1);
            await _sql.EnsureCommOutboxAsync();

            var tables = await _sql.StringsAsync("SELECT name FROM sys.tables ORDER BY name;");

            // Notifications / CommMessages / CommAttachments are created BY THE FIXTURE to stand in for tables that
            // already exist in production; the scripts only alter them. Everything else here is kernel-owned.
            var expected = new[]
            {
                "BusinessEventDispatch", "BusinessEvents",
                "CommAttachments", "CommMessages", "Notifications",
            };
            Assert.Equal(expected.OrderBy(t => t), tables.OrderBy(t => t));

            // No journal, stock or invoice table appeared. The kernel reads financial data AsNoTracking and writes
            // none of it — a script that created one would be a two-writers-rule breach.
            Assert.DoesNotContain(tables, t => t.Contains("Journal", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(tables, t => t.Contains("Stock", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(tables, t => t.Contains("Invoice", StringComparison.OrdinalIgnoreCase));
        }
    }
}
