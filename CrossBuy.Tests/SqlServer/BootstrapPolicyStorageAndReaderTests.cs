using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.SqlServer
{
    // =============================================================================================
    // Stage 2A Batch B — B2 storage + B4 reader evidence, on a real SQL Server probe.
    //
    // SQLite is not acceptable evidence here and the reason is specific: every property under test is a
    // SQL Server construct — a FILTERED unique index, six CHECK constraints, and the interaction between
    // them. SQLite has no filtered indexes and does not enforce these CHECKs the same way, so an
    // in-process version of this file would pass while proving nothing. That is the exact hazard the SQL
    // evidence suite exists to prevent.
    //
    // THE MOST IMPORTANT TEST IN THIS FILE is
    // A_hand_inserted_allowing_policy_for_a_Never_action_still_DENIES. Everything else verifies that the
    // rules work when used correctly; that one verifies the rule holds when the database is subverted.
    // =============================================================================================
    [Collection(SqlServerCollection.Name)]
    public sealed class BootstrapPolicyStorageAndReaderTests : IAsyncLifetime
    {
        private readonly SqlServerFixture _sql;
        private SqlServerFixture.ProbeDatabase? _probe;

        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        public BootstrapPolicyStorageAndReaderTests(SqlServerFixture sql) => _sql = sql;

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        public async Task InitializeAsync()
        {
            if (!_sql.Available) return;
            _probe = await _sql.CreateProbeDatabaseAsync("BootstrapPolicy");
            await ApplySliceAsync();   // pass 1
        }

        public async Task DisposeAsync()
        {
            if (_probe == null) return;
            SqlConnection.ClearAllPools();
            await _sql.DropProbeDatabaseAsync(_probe);
        }

        private CrossDbContext Db(int companyId = CompanyOne) => _sql.ContextFor(_probe!, companyId);

        private IBootstrapAccessPolicyReader Reader(CrossDbContext db) =>
            new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance);

        private static BusinessContext Ctx(int companyId = CompanyOne, int? employeeId = 500) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "u", Source = BusinessContextSource.Http,
        };

        // ---------------------------------------------------------------------------------------------
        // the slice
        // ---------------------------------------------------------------------------------------------

        private async Task ApplySliceAsync()
        {
            // The probe's schema comes from the EF model, which already carries the table. Applying the real
            // deployment script ON TOP is the point: it proves the SCRIPT is idempotent against an existing
            // compatible object, which is exactly what a redeploy does in production.
            await _sql.RunScriptAsync(await OpenAsync(), "bootstrap_access_policies.sql");
        }

        private async Task<SqlConnection> OpenAsync()
        {
            var connection = new SqlConnection(_probe!.ConnectionString);
            await connection.OpenAsync();
            return connection;
        }

        [SkippableFact]
        public async Task The_slice_applies_a_SECOND_time_without_error_and_detects_no_drift()
        {
            Ready();

            // Pass 1 ran in InitializeAsync. This is pass 2 — and the script's drift detection THROWs rather
            // than proceeding if the index key or constraint count has changed, so a silent pass here is a real
            // statement about the schema, not just "no exception".
            await ApplySliceAsync();

            await using var connection = await OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT
  (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.BootstrapAccessPolicies')),
  (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.BootstrapAccessPolicies')),
  (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.BootstrapAccessPolicies')
     AND name = 'UX_BootstrapAccessPolicies_ActivePolicy' AND is_unique = 1 AND has_filter = 1);";

            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            Assert.Equal(20, reader.GetInt32(0));   // the 20 declared columns
            Assert.True(reader.GetInt32(1) >= 6, "the six CHECK constraints must be present");
            Assert.Equal(1, reader.GetInt32(2));    // unique AND filtered
        }

        [SkippableFact]
        public async Task Every_declared_column_exists_with_no_silent_omission()
        {
            Ready();

            var expected = new[]
            {
                "ID","CompanyID","Scope","ActionCode","State","Reason","EnabledAt","EnabledBy","ExpiresAt",
                "ReviewedAt","ReviewedBy","AcknowledgedAt","AcknowledgedBy","CreatedBy","CreatedAt","UpdatedBy",
                "UpdatedAt","SourceSystem","MigrationBatchId","IsActive",
            };

            await using var connection = await OpenAsync();
            var found = new List<string>();

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.BootstrapAccessPolicies');";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) found.Add(reader.GetString(0));
            }

            var missing = expected.Except(found, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(missing.Count == 0, "columns missing from the deployed table: " + string.Join(", ", missing));
        }

        // ---------------------------------------------------------------------------------------------
        // B2 — the database's own guarantees
        // ---------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task A_duplicate_ACTIVE_policy_is_refused_by_the_DATABASE()
        {
            Ready();
            await using var db = Db();

            db.BootstrapAccessPolicies.Add(Policy(CompanyOne, "Accounting", "read"));
            await db.SaveChangesAsync();

            db.BootstrapAccessPolicies.Add(Policy(CompanyOne, "Accounting", "read"));

            // Application-level checking cannot be the guarantee: two concurrent seeds both read "not found".
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("UX_BootstrapAccessPolicies_ActivePolicy", ex.InnerException!.Message);
        }

        [SkippableFact]
        public async Task Disabling_a_policy_RETAINS_history_and_allows_a_new_active_row()
        {
            Ready();
            await using var db = Db();

            var first = Policy(CompanyOne, "Accounting", "read");
            db.BootstrapAccessPolicies.Add(first);
            await db.SaveChangesAsync();

            // Supersede rather than delete. The old row is the answer to "why was this open in March".
            first.IsActive = false;
            first.State = BootstrapPolicyStates.Disabled;
            first.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var second = Policy(CompanyOne, "Accounting", "read");
            db.BootstrapAccessPolicies.Add(second);
            await db.SaveChangesAsync();      // the FILTER is what makes this legal

            await using var fresh = Db();
            var all = await fresh.BootstrapAccessPolicies.AsNoTracking()
                .Where(p => p.CompanyID == CompanyOne && p.Scope == "Accounting" && p.ActionCode == "read")
                .ToListAsync();

            Assert.Equal(2, all.Count);                       // history retained, no hard delete
            Assert.Single(all.Where(p => p.IsActive));        // exactly one active
        }

        [SkippableFact]
        public async Task A_Temporary_policy_with_no_expiry_is_refused_by_the_DATABASE()
        {
            Ready();
            await using var db = Db();

            var policy = Policy(CompanyOne, "Accounting", "read");
            policy.State = BootstrapPolicyStates.Temporary;
            policy.ExpiresAt = null;
            db.BootstrapAccessPolicies.Add(policy);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("CK_BootstrapAccessPolicies_TemporaryExpiry", ex.InnerException!.Message);
        }

        [SkippableFact]
        public async Task An_expiry_before_the_enable_date_is_refused_by_the_DATABASE()
        {
            Ready();
            await using var db = Db();

            var policy = Policy(CompanyOne, "Accounting", "read");
            policy.EnabledAt = DateTime.UtcNow;
            policy.ExpiresAt = DateTime.UtcNow.AddDays(-1);
            db.BootstrapAccessPolicies.Add(policy);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("CK_BootstrapAccessPolicies_Dates", ex.InnerException!.Message);
        }

        [SkippableFact]
        public async Task A_POS_policy_is_refused_by_the_DATABASE()
        {
            Ready();
            await using var db = Db();

            db.BootstrapAccessPolicies.Add(Policy(CompanyOne, "Pos", "sell"));

            // POS fails closed with no role and has no bootstrap path. A policy row would imply otherwise.
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("CK_BootstrapAccessPolicies_NotPos", ex.InnerException!.Message);
        }

        [SkippableFact]
        public async Task An_unknown_state_is_refused_by_the_DATABASE()
        {
            Ready();
            await using var db = Db();

            var policy = Policy(CompanyOne, "Accounting", "read");
            policy.State = "SomethingNobodyCanEvaluate";
            db.BootstrapAccessPolicies.Add(policy);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("CK_BootstrapAccessPolicies_State", ex.InnerException!.Message);
        }

        [SkippableFact]
        public async Task Audit_fields_persist_and_two_companies_stay_independent()
        {
            Ready();
            await using var db = Db();

            var batch = Guid.NewGuid();
            var one = Policy(CompanyOne, "Accounting", "read");
            one.EnabledBy = 77; one.Reason = "company one compatibility"; one.MigrationBatchId = batch;
            var two = Policy(CompanyTwo, "Accounting", "read");
            two.EnabledBy = 88; two.Reason = "company two compatibility"; two.MigrationBatchId = batch;

            db.BootstrapAccessPolicies.AddRange(one, two);
            await db.SaveChangesAsync();   // same scope+action, different companies: legal

            await using var fresh = Db();
            var rows = await fresh.BootstrapAccessPolicies.AsNoTracking()
                .Where(p => p.MigrationBatchId == batch).OrderBy(p => p.CompanyID).ToListAsync();

            Assert.Equal(2, rows.Count);
            Assert.Equal(77, rows[0].EnabledBy);
            Assert.Equal(88, rows[1].EnabledBy);
            Assert.All(rows, r => Assert.Equal(BootstrapPolicySources.BehaviourPreservingSeed, r.SourceSystem));
            Assert.All(rows, r => Assert.NotEqual(default, r.CreatedAt));
        }

        [SkippableFact]
        public async Task A_rolled_back_transaction_leaves_no_partial_row()
        {
            Ready();
            await using var db = Db();

            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.BootstrapAccessPolicies.Add(Policy(CompanyOne, "Hr", "read"));
                await db.SaveChangesAsync();
                await tx.RollbackAsync();
            }

            await using var fresh = Db();
            Assert.False(await fresh.BootstrapAccessPolicies.AnyAsync(p => p.Scope == "Hr"));
        }

        [SkippableFact]
        public async Task Concurrent_equivalent_inserts_produce_exactly_one_active_policy()
        {
            Ready();

            using var barrier = new Barrier(4);
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await using var db = Db();
                var policy = Policy(CompanyOne, "Projects", "read");
                db.BootstrapAccessPolicies.Add(policy);
                barrier.SignalAndWait();
                try { await db.SaveChangesAsync(); return true; }
                catch (DbUpdateException) { return false; }
            })).ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(ok => ok));

            await using var fresh = Db();
            Assert.Equal(1, await fresh.BootstrapAccessPolicies
                .CountAsync(p => p.CompanyID == CompanyOne && p.Scope == "Projects"
                              && p.ActionCode == "read" && p.IsActive));
        }

        // ---------------------------------------------------------------------------------------------
        // B2 — the entity's own validation, independent of the database
        // ---------------------------------------------------------------------------------------------

        [SkippableFact]
        public void Validate_refuses_a_permitting_policy_for_a_Never_action()
        {
            var policy = Policy(CompanyOne, "Accounting", "post");   // post is Never
            var errors = policy.Validate();

            Assert.Contains(errors, e => e.Contains("Never-Bootstrap-Open"));
        }

        [SkippableTheory]
        [InlineData(0, "Accounting", "read", "company is required")]
        [InlineData(CompanyOne, "NotAScope", "read", "not a known permission scope")]
        [InlineData(CompanyOne, "Pos", "read", "POS has no bootstrap behaviour")]
        [InlineData(CompanyOne, "Accounting", "", "action code is required")]
        public void Validate_rejects_structurally_invalid_policies(
            int companyId, string scope, string action, string expected)
        {
            var errors = Policy(companyId, scope, action).Validate();
            Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
        }

        // ---------------------------------------------------------------------------------------------
        // B4 — the reader's decisions
        // ---------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task A_hand_inserted_allowing_policy_for_a_Never_action_still_DENIES()
        {
            Ready();

            // THE TEST THAT MATTERS. Insert directly with raw SQL, bypassing Validate(), so the row exists in
            // the database claiming to permit Accounting.post — the single most dangerous action in the system.
            // The reader must refuse it because it evaluates the Never classification BEFORE it queries.
            await using (var connection = await OpenAsync())
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO dbo.BootstrapAccessPolicies (CompanyID, Scope, ActionCode, State, Reason, IsActive, CreatedAt)
VALUES (@c, N'Accounting', N'post', N'ExplicitlyAllowed', N'subverted row', 1, SYSUTCDATETIME());";
                cmd.Parameters.AddWithValue("@c", CompanyOne);
                await cmd.ExecuteNonQueryAsync();
            }

            await using var db = Db();

            // The row is really there — otherwise this test would pass by proving nothing.
            Assert.True(await db.BootstrapAccessPolicies
                .AnyAsync(p => p.Scope == "Accounting" && p.ActionCode == "post" && p.IsActive));

            var decision = await Reader(db).ResolveDecisionAsync(Ctx(), "Accounting", "post");

            Assert.False(decision.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.NeverBootstrapOpen, decision.ReasonCode);
            Assert.Equal(AuthorizationDecisionSources.Denied, decision.DecisionSource);

            // And no policy id is attributed — the decision never consulted the row.
            Assert.Null(decision.BootstrapPolicyId);
        }

        [SkippableTheory]
        [InlineData("Accounting", "post")]
        [InlineData("Accounting", "pay")]
        [InlineData("Accounting", "manage")]
        [InlineData("Accounting", "currency-override")]
        [InlineData("Inventory", "doc")]
        [InlineData("Inventory", "purchase")]
        [InlineData("Inventory", "manage")]
        [InlineData("Inventory", "warehouse-access")]
        [InlineData("Crm", "manage")]
        [InlineData("Projects", "billing")]
        [InlineData("Hr", "payroll-manage")]
        [InlineData("Hr", "confidential-view")]
        [InlineData("Tasks", "manage")]
        [InlineData("Communication", "outbox-manage")]
        public async Task Every_Never_action_denies_with_the_Never_reason_code(string scope, string action)
        {
            Ready();
            await using var db = Db();

            var decision = await Reader(db).ResolveDecisionAsync(Ctx(), scope, action);

            Assert.False(decision.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.NeverBootstrapOpen, decision.ReasonCode);
        }

        [SkippableFact]
        public void The_Never_list_holds_exactly_the_confirmed_fifteen_entries()
        {
            // Pinned so a change to the classification is a deliberate, visible edit rather than a drift. The
            // count differing from the frozen matrix's 14 was resolved by owner decision, not by forcing a total.
            Assert.Equal(15, NeverBootstrapOpen.Count);

            Assert.Equal(4, NeverBootstrapOpen.All.Count(e => e.Scope == "Accounting"));
            Assert.Equal(4, NeverBootstrapOpen.All.Count(e => e.Scope == "Inventory"));
            Assert.Equal(1, NeverBootstrapOpen.All.Count(e => e.Scope == "Crm"));
            Assert.Equal(1, NeverBootstrapOpen.All.Count(e => e.Scope == "Platform"));
            Assert.Equal(1, NeverBootstrapOpen.All.Count(e => e.Scope == "Projects"));
            Assert.Equal(2, NeverBootstrapOpen.All.Count(e => e.Scope == "Hr"));
            Assert.Equal(1, NeverBootstrapOpen.All.Count(e => e.Scope == "Tasks"));
            Assert.Equal(1, NeverBootstrapOpen.All.Count(e => e.Scope == "Communication"));

            // Every entry carries a reason. A classification with no stated reason cannot be reviewed.
            Assert.All(NeverBootstrapOpen.All, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
        }

        [SkippableTheory]
        [InlineData(BootstrapPolicyStates.LegacyCompatibility, AuthorizationDecisionSources.BootstrapLegacyCompatibility)]
        [InlineData(BootstrapPolicyStates.Installation, AuthorizationDecisionSources.BootstrapInstallation)]
        [InlineData(BootstrapPolicyStates.ExplicitlyAllowed, AuthorizationDecisionSources.BootstrapExplicitlyAllowed)]
        [InlineData(BootstrapPolicyStates.ReviewRequired, AuthorizationDecisionSources.BootstrapLegacyCompatibility)]
        public async Task A_permitting_state_allows_and_names_its_decision_source(string state, string expectedSource)
        {
            Ready();
            await using var db = Db();

            var policy = Policy(CompanyOne, "Accounting", "read");
            policy.State = state;
            db.BootstrapAccessPolicies.Add(policy);
            await db.SaveChangesAsync();

            var decision = await Reader(db).ResolveDecisionAsync(Ctx(), "Accounting", "read");

            Assert.True(decision.IsAllowed);
            Assert.Equal(expectedSource, decision.DecisionSource);
            Assert.True(decision.IsBootstrap);
            Assert.False(decision.IsDirectGrant);          // never mistaken for role authorization
            Assert.Equal(policy.ID, decision.BootstrapPolicyId);
            Assert.Equal(AuthorizationReasonCodes.PolicyPermits, decision.ReasonCode);
        }

        [SkippableFact]
        public async Task Temporary_allows_before_expiry_and_denies_after_it()
        {
            Ready();
            await using var db = Db();

            var live = Policy(CompanyOne, "Accounting", "read");
            live.State = BootstrapPolicyStates.Temporary;
            live.EnabledAt = DateTime.UtcNow.AddDays(-1);
            live.ExpiresAt = DateTime.UtcNow.AddHours(1);
            db.BootstrapAccessPolicies.Add(live);
            await db.SaveChangesAsync();

            var allowed = await Reader(db).ResolveDecisionAsync(Ctx(), "Accounting", "read");
            Assert.True(allowed.IsAllowed);
            Assert.Equal(AuthorizationDecisionSources.BootstrapTemporary, allowed.DecisionSource);

            // Move the window into the past. RISK-045: a temporary exception must actually end.
            live.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();

            await using var fresh = Db();
            var expired = await Reader(fresh).ResolveDecisionAsync(Ctx(), "Accounting", "read");

            Assert.False(expired.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.PolicyExpired, expired.ReasonCode);
        }

        [SkippableFact]
        public async Task Disabled_denies_and_an_inactive_row_is_ignored()
        {
            Ready();
            await using var db = Db();

            var disabled = Policy(CompanyOne, "Accounting", "read");
            disabled.State = BootstrapPolicyStates.Disabled;
            db.BootstrapAccessPolicies.Add(disabled);
            await db.SaveChangesAsync();

            var d1 = await Reader(db).ResolveDecisionAsync(Ctx(), "Accounting", "read");
            Assert.False(d1.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.PolicyDisabled, d1.ReasonCode);

            // Now make it inactive history AND permitting. An inactive row must not resurrect an allowance.
            disabled.IsActive = false;
            disabled.State = BootstrapPolicyStates.LegacyCompatibility;
            await db.SaveChangesAsync();

            await using var fresh = Db();
            var d2 = await Reader(fresh).ResolveDecisionAsync(Ctx(), "Accounting", "read");
            Assert.False(d2.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.NoPolicyConfigured, d2.ReasonCode);
        }

        [SkippableFact]
        public async Task A_missing_policy_DENIES_which_is_the_inversion_Batch_B_exists_to_make()
        {
            Ready();
            await using var db = Db();

            var decision = await Reader(db).ResolveDecisionAsync(Ctx(), "Accounting", "read");

            // Today the ABSENCE of configuration means open (Mechanism A). Once B6 consults this reader,
            // absence means closed and compatibility must be written down. The B3 seed is what keeps that safe.
            Assert.False(decision.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.NoPolicyConfigured, decision.ReasonCode);
        }

        [SkippableFact]
        public async Task A_Temporary_row_without_expiry_that_bypassed_the_constraint_is_a_ConfigurationError()
        {
            Ready();

            // Written with the CHECK temporarily disabled — a row that predates the constraint, or arrived by
            // another route. It must not be honoured, and it must not read as an ordinary denial either: an
            // administrator has to fix this and no role assignment will.
            await using (var connection = await OpenAsync())
            {
                await using var off = connection.CreateCommand();
                off.CommandText =
                    "ALTER TABLE dbo.BootstrapAccessPolicies NOCHECK CONSTRAINT CK_BootstrapAccessPolicies_TemporaryExpiry;" +
                    "INSERT INTO dbo.BootstrapAccessPolicies (CompanyID, Scope, ActionCode, State, IsActive, CreatedAt)" +
                    " VALUES (@c, N'Accounting', N'read', N'Temporary', 1, SYSUTCDATETIME());" +
                    "ALTER TABLE dbo.BootstrapAccessPolicies WITH NOCHECK CHECK CONSTRAINT CK_BootstrapAccessPolicies_TemporaryExpiry;";
                off.Parameters.AddWithValue("@c", CompanyOne);
                await off.ExecuteNonQueryAsync();
            }

            await using var db = Db();
            var decision = await Reader(db).ResolveDecisionAsync(Ctx(), "Accounting", "read");

            Assert.False(decision.IsAllowed);
            Assert.Equal(AuthorizationDecisionSources.ConfigurationError, decision.DecisionSource);
            Assert.Equal(AuthorizationReasonCodes.TemporaryWithoutExpiry, decision.ReasonCode);
        }

        [SkippableFact]
        public async Task Company_isolation_holds_a_policy_in_one_company_does_not_answer_another()
        {
            Ready();
            await using var db = Db();

            db.BootstrapAccessPolicies.Add(Policy(CompanyTwo, "Accounting", "read"));
            await db.SaveChangesAsync();

            // Company 1 asks. Company 2's policy must not answer.
            var decision = await Reader(db).ResolveDecisionAsync(Ctx(CompanyOne), "Accounting", "read");

            Assert.False(decision.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.NoPolicyConfigured, decision.ReasonCode);
            Assert.Equal(CompanyOne, decision.CompanyID);
        }

        [SkippableFact]
        public async Task An_unresolved_context_an_unknown_scope_and_an_unknown_action_all_DENY()
        {
            Ready();
            await using var db = Db();
            var reader = Reader(db);

            var noCompany = await reader.ResolveDecisionAsync(
                new BusinessContext { CompanyId = 0 }, "Accounting", "read");
            Assert.False(noCompany.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.CompanyUnresolved, noCompany.ReasonCode);

            var badScope = await reader.ResolveDecisionAsync(Ctx(), "NotAScope", "read");
            Assert.Equal(AuthorizationReasonCodes.UnknownScope, badScope.ReasonCode);

            var noAction = await reader.ResolveDecisionAsync(Ctx(), "Accounting", "");
            Assert.Equal(AuthorizationReasonCodes.UnknownAction, noAction.ReasonCode);

            var pos = await reader.ResolveDecisionAsync(Ctx(), "Pos", "sell");
            Assert.False(pos.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.PosHasNoBootstrap, pos.ReasonCode);
        }

        [SkippableFact]
        public async Task Decision_metadata_reports_review_state_and_carries_no_confidential_payload()
        {
            Ready();
            await using var db = Db();

            var policy = Policy(CompanyOne, "Accounting", "read");
            policy.State = BootstrapPolicyStates.ReviewRequired;
            policy.Reason = "seeded compatibility";
            db.BootstrapAccessPolicies.Add(policy);
            await db.SaveChangesAsync();

            var metadata = await Reader(db).GetDecisionMetadataAsync(Ctx(), "Accounting", "read");

            Assert.True(metadata.Decision.IsAllowed);
            Assert.True(metadata.RequiresReview);
            Assert.Equal(policy.ID, metadata.PolicyId);
            Assert.Equal(BootstrapPolicyStates.ReviewRequired, metadata.State);
            Assert.False(metadata.IsNeverBootstrapOpen);
            Assert.Equal(BootstrapPolicySources.BehaviourPreservingSeed, metadata.SourceSystem);

            // A Never action's metadata names WHY it is closed, which is a classification reason and not data.
            var never = await Reader(db).GetDecisionMetadataAsync(Ctx(), "Accounting", "post");
            Assert.True(never.IsNeverBootstrapOpen);
            Assert.False(never.Decision.IsAllowed);
            Assert.Contains("general ledger", never.NeverReason!);
        }

        [SkippableFact]
        public async Task The_reader_needs_no_Session_and_lists_policies_per_company()
        {
            Ready();
            await using var db = Db();

            db.BootstrapAccessPolicies.AddRange(
                Policy(CompanyOne, "Accounting", "read"),
                Policy(CompanyOne, "Hr", "read"),
                Policy(CompanyTwo, "Accounting", "read"));
            await db.SaveChangesAsync();

            var reader = Reader(db);

            // A plain BusinessContext, constructed in a test with no HttpContext anywhere. That IS the proof
            // that the reader is session-free: it could not run at all if it needed one.
            var all = await reader.ListPoliciesAsync(Ctx(CompanyOne));
            Assert.Equal(2, all.Count);
            Assert.All(all, p => Assert.Equal(CompanyOne, p.CompanyID));

            var scoped = await reader.ListPoliciesAsync(Ctx(CompanyOne), "Hr");
            Assert.Single(scoped);
        }

        // ---------------------------------------------------------------------------------------------

        private static BootstrapAccessPolicy Policy(int companyId, string scope, string action) => new()
        {
            CompanyID = companyId,
            Scope = scope,
            ActionCode = action,
            State = BootstrapPolicyStates.LegacyCompatibility,
            Reason = "storage/reader evidence",
            EnabledAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow,
            SourceSystem = BootstrapPolicySources.BehaviourPreservingSeed,
            IsActive = true,
        };
    }
}
