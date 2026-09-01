using CrossBuy.BL;
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
    // CrossBusiness Platform — Stage 2A B6 — Accounting.read path diagnosis, and the valid Mutation H.
    //
    // WHY THIS FILE EXISTS. Mutation H was attempted twice and neither attempt produced usable evidence:
    //   attempt 1 — removing the policy call: the build FAILED, so `--no-build` ran a stale assembly;
    //   attempt 2 — policy set to Disabled: appeared to compile, yet the preservation test still passed.
    //
    // A mutation that does not fail is either a hole in the production conversion or a test that never
    // reaches the path it claims to. Guessing which would be worthless, so this file INSTRUMENTS the path
    // and asserts every precondition before mutating anything.
    //
    // The instrument is a RECORDING READER that wraps the REAL BootstrapAccessPolicyReader — not a stub
    // that invents an answer. A stub could make either outcome appear, which is exactly the failure mode
    // being investigated.
    // =============================================================================================
    [Collection(SqlServerCollection.Name)]
    public sealed class B6AccountingReadPathDiagnosisTests : IAsyncLifetime
    {
        private readonly SqlServerFixture _sql;
        private SqlServerFixture.ProbeDatabase? _probe;

        private const int CompanyOne = 1;
        private const int Employee = 8101;

        public B6AccountingReadPathDiagnosisTests(SqlServerFixture sql) => _sql = sql;

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        public async Task InitializeAsync()
        {
            if (!_sql.Available) return;
            _probe = await _sql.CreateProbeDatabaseAsync("B6ReadPath");
            await SeedAsync();
        }

        public async Task DisposeAsync()
        {
            if (_probe == null) return;
            SqlConnection.ClearAllPools();
            await _sql.DropProbeDatabaseAsync(_probe);
        }

        private CrossDbContext Db() => _sql.ContextFor(_probe!, CompanyOne);

        private static BusinessContext Ctx() => new()
        {
            CompanyId = CompanyOne, EmployeeId = Employee, UserId = "u", Source = BusinessContextSource.Http,
        };

        private sealed class NoContext : IBusinessContextAccessor
        {
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) =>
                throw new BusinessContextUnresolvedException("none");
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) =>
                Task.FromResult<BusinessContext?>(null);
        }

        /// <summary>
        /// Wraps the REAL reader and records every call. Delegation, not simulation: the decision returned is the
        /// production decision, so this can prove what the reader was asked AND what it answered without changing
        /// either.
        /// </summary>
        private sealed class RecordingReader : IBootstrapAccessPolicyReader
        {
            private readonly IBootstrapAccessPolicyReader _inner;
            public RecordingReader(IBootstrapAccessPolicyReader inner) => _inner = inner;

            public List<(int Company, string Scope, string Action)> Calls { get; } = new();
            public List<AuthorizationDecision> Decisions { get; } = new();

            public async Task<AuthorizationDecision> ResolveDecisionAsync(
                BusinessContext context, string scope, string actionCode, CancellationToken ct = default)
            {
                Calls.Add((context.CompanyId, scope, actionCode));
                var decision = await _inner.ResolveDecisionAsync(context, scope, actionCode, ct);
                Decisions.Add(decision);
                return decision;
            }

            public Task<BootstrapAccessPolicy?> GetEffectivePolicyAsync(
                BusinessContext c, string s, string a, CancellationToken ct = default) =>
                _inner.GetEffectivePolicyAsync(c, s, a, ct);
            public Task<IReadOnlyList<BootstrapAccessPolicy>> ListPoliciesAsync(
                BusinessContext c, string? s = null, bool i = false, CancellationToken ct = default) =>
                _inner.ListPoliciesAsync(c, s, i, ct);
            public Task<BootstrapDecisionMetadata> GetDecisionMetadataAsync(
                BusinessContext c, string s, string a, CancellationToken ct = default) =>
                _inner.GetDecisionMetadataAsync(c, s, a, ct);
            public Task<bool> IsNeverBootstrapOpenAsync(string s, string a, CancellationToken ct = default) =>
                _inner.IsNeverBootstrapOpenAsync(s, a, ct);
        }

        private (AccountingAccessService service, RecordingReader spy) Build(CrossDbContext db)
        {
            var spy = new RecordingReader(
                new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance));

            return (new AccountingAccessService(
                db, new Microsoft.AspNetCore.Http.HttpContextAccessor(), new NoContext(), spy,
                NullLogger<AccountingAccessService>.Instance), spy);
        }

        // =========================================================================================
        // PHASE 3 — the fifteen preconditions, asserted before any mutation
        // =========================================================================================

        [SkippableFact]
        public async Task The_Accounting_read_path_reaches_the_reader_exactly_once_with_the_expected_arguments()
        {
            Ready();
            await using var db = Db();
            await PolicyAsync(db, BootstrapPolicyStates.LegacyCompatibility);

            // 4. no Accounting role exists for the tested principal — asserted against the DATABASE, not assumed
            Assert.Equal(0, await db.AccountingUserRoles.CountAsync());

            // 5. AnyRoleConfiguredAsync must be false. Read through the private method by reflection, so the
            //    assertion is about the production helper rather than a re-implementation of it.
            Assert.False(await AnyRoleConfiguredAsync(db));

            // 14. the isolated database contains exactly the intended policy row
            var rows = await db.BootstrapAccessPolicies.AsNoTracking()
                .Where(p => p.Scope == "Accounting" && p.ActionCode == "read" && p.IsActive).ToListAsync();
            Assert.Single(rows);
            Assert.Equal(BootstrapPolicyStates.LegacyCompatibility, rows[0].State);

            var (service, spy) = Build(db);

            // 1, 2, 3, 11 — the production service, the exact action, the intended company
            bool allowed = await service.CanAsync(Ctx(), "read");
            var decision = await service.DecideAsync(Ctx(), "read");

            // 7, 8, 9 — the reader was reached, with exactly the right arguments
            Assert.Equal(2, spy.Calls.Count);                       // once per call above
            Assert.All(spy.Calls, c =>
            {
                Assert.Equal(CompanyOne, c.Company);
                Assert.Equal("Accounting", c.Scope);
                Assert.Equal("read", c.Action);
            });

            // 10 — the baseline decision source
            Assert.True(allowed);
            Assert.Equal(AuthorizationDecisionSources.BootstrapLegacyCompatibility, decision.DecisionSource);
            Assert.Equal(rows[0].ID, decision.BootstrapPolicyId);

            // 11, 12 — the bool is the decision, with no later branch overriding it
            Assert.Equal(decision.IsAllowed, allowed);
        }

        [SkippableFact]
        public async Task A_configured_role_row_would_BYPASS_the_reader__the_candidate_cause_pinned()
        {
            Ready();
            await using var db = Db();
            await PolicyAsync(db, BootstrapPolicyStates.Disabled);   // a policy that DENIES

            // The hypothesis for the earlier anomaly: any accounting role row closes bootstrap-open, so `read` is
            // granted by the role switch (`"read" => true`) and the reader is never consulted. Pinned here so the
            // mechanism is documented rather than suspected.
            db.AccountingUserRoles.Add(new Models.Context.Accounting.AccountingUserRole
            { CompanyID = CompanyOne, EmployeeId = 9999, Role = "Auditor", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            Assert.True(await AnyRoleConfiguredAsync(db));

            var (service, spy) = Build(db);
            bool allowed = await service.CanAsync(Ctx(), "read");

            // read is allowed by the ROLE path even though the policy is Disabled — and the reader is never called.
            Assert.True(allowed);
            Assert.Empty(spy.Calls);

            var decision = await service.DecideAsync(Ctx(), "read");
            Assert.Equal(AuthorizationDecisionSources.LegacyRole, decision.DecisionSource);
            Assert.False(decision.IsBootstrap);
        }

        // =========================================================================================
        // PHASE 4 — the VALID Mutation H
        // =========================================================================================

        [SkippableFact]
        public async Task MUTATION_H_a_Disabled_policy_DENIES_Accounting_read_with_no_role_configured()
        {
            Ready();
            await using var db = Db();

            // The mutation: the same row the preservation test relies on, but Disabled.
            await PolicyAsync(db, BootstrapPolicyStates.Disabled);

            // ---- verify every precondition through SQL before invoking production code ----
            await using (var connection = new SqlConnection(_probe!.ConnectionString))
            {
                await connection.OpenAsync();
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
SELECT (SELECT COUNT(*) FROM dbo.BootstrapAccessPolicies
          WHERE CompanyID = @c AND Scope = N'Accounting' AND ActionCode = N'read' AND IsActive = 1),
       (SELECT COUNT(*) FROM dbo.BootstrapAccessPolicies
          WHERE CompanyID = @c AND Scope = N'Accounting' AND ActionCode = N'read' AND IsActive = 1
            AND State = N'Disabled'),
       (SELECT COUNT(*) FROM dbo.AccountingUserRoles WHERE CompanyID = @c);";
                cmd.Parameters.AddWithValue("@c", CompanyOne);

                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt32(0));   // exactly one active policy
                Assert.Equal(1, reader.GetInt32(1));   // and it is Disabled
                Assert.Equal(0, reader.GetInt32(2));   // no accounting role in this company
            }

            Assert.False(await AnyRoleConfiguredAsync(db));

            // ---- the reader independently denies ----
            var standalone = new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance);
            var readerDecision = await standalone.ResolveDecisionAsync(Ctx(), "Accounting", "read");

            Assert.False(readerDecision.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.PolicyDisabled, readerDecision.ReasonCode);

            // ---- and the PRODUCTION service returns that denial ----
            var (service, spy) = Build(db);
            bool allowed = await service.CanAsync(Ctx(), "read");

            Assert.Single(spy.Calls);                                   // the reader WAS consulted
            Assert.False(spy.Decisions[0].IsAllowed);                   // it denied
            Assert.False(allowed);                                      // and the service returned the denial

            // THIS is Mutation H's proof: with the compatibility policy Disabled, Accounting.read is DENIED.
            // The preservation test asserts True on the same call, so it fails under this condition — which is
            // what makes it a real behaviour-preservation test rather than a tautology.
        }

        // =========================================================================================

        /// <summary>Calls the PRODUCTION private helper by reflection, so the assertion is about its real result.</summary>
        private static async Task<bool> AnyRoleConfiguredAsync(CrossDbContext db)
        {
            var service = new AccountingAccessService(
                db, new Microsoft.AspNetCore.Http.HttpContextAccessor(), new NoContext(),
                new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<AccountingAccessService>.Instance);

            var method = typeof(AccountingAccessService).GetMethod(
                "AnyRoleConfiguredAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            return await (Task<bool>)method!.Invoke(service, new object[] { CompanyOne, CancellationToken.None })!;
        }

        private async Task PolicyAsync(CrossDbContext db, string state)
        {
            db.BootstrapAccessPolicies.Add(new BootstrapAccessPolicy
            {
                CompanyID = CompanyOne, Scope = "Accounting", ActionCode = "read", State = state,
                Reason = "read-path diagnosis", EnabledAt = DateTime.UtcNow.AddDays(-1),
                CreatedAt = DateTime.UtcNow, SourceSystem = BootstrapPolicySources.BehaviourPreservingSeed,
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedAsync()
        {
            await using var db = Db();
            await db.Database.OpenConnectionAsync();
            try
            {
                await FillAsync(db, "dbo.Companies", () =>
                    db.Companies.Add(new Models.Context.Admin.Companies
                    { CompanyID = CompanyOne, CompanyName = "ZZ ReadPath" }));

                await FillAsync(db, "dbo.Employee", () =>
                    db.Employee.Add(new Models.Context.Admin.Employee
                    {
                        ID = Employee, EmpCompanyID = CompanyOne, IsActive = true, UserId = "u",
                        FullName = "ZZ RP", FirstName = "ZZ", LastName = "RP",
                    }));
            }
            finally { await db.Database.CloseConnectionAsync(); }
        }

        private static async Task FillAsync(CrossDbContext db, string table, Action add)
        {
            await db.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} ON;");
            try
            {
                add();
                foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added))
                    foreach (var p in entry.Properties)
                        if (p.Metadata.ClrType == typeof(string) && !p.Metadata.IsNullable && p.CurrentValue == null)
                            p.CurrentValue = "ZZ";
                await db.SaveChangesAsync();
            }
            finally { await db.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT {table} OFF;"); }
        }
    }
}
