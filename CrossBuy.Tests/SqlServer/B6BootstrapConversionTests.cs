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
    // CrossBusiness Platform — Stage 2A Batch B — B6: the three approved Mechanism A conversions.
    //
    // WRITTEN BEFORE THE PRODUCTION CHANGE, as the brief requires. These are the highest-risk
    // authorization paths in the system — who may post a journal entry, take a payment, move stock —
    // so the expectations are pinned first and the code is changed to satisfy them.
    //
    // THE THREE SITES:
    //   1. AccountingAccessService.cs:60   module bootstrap
    //   2. InventoryAccessService.cs:48    module bootstrap
    //   3. InventoryAccessService.cs:91    warehouse-scope bootstrap  (NARROWING - no seed row)
    //
    // The two CRM sites are NOT converted and are asserted unchanged at the end of this file.
    //
    // Real SQL Server only: the policy store's filtered unique index and CHECK constraints are the
    // mechanism, and SQLite has neither.
    // =============================================================================================
    [Collection(SqlServerCollection.Name)]
    public sealed class B6BootstrapConversionTests : IAsyncLifetime
    {
        private readonly SqlServerFixture _sql;
        private SqlServerFixture.ProbeDatabase? _probe;

        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int Employee = 7001;      // holds no role unless a test grants one
        private const int Branch = 7501;
        private const int OtherBranch = 7502;
        private const int Warehouse = 7601;
        private const int OtherWarehouse = 7602;

        public B6BootstrapConversionTests(SqlServerFixture sql) => _sql = sql;

        private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

        public async Task InitializeAsync()
        {
            if (!_sql.Available) return;
            _probe = await _sql.CreateProbeDatabaseAsync("B6Conversion");
            await SeedInfrastructureAsync();
        }

        public async Task DisposeAsync()
        {
            if (_probe == null) return;
            SqlConnection.ClearAllPools();
            await _sql.DropProbeDatabaseAsync(_probe);
        }

        private CrossDbContext Db(int companyId = CompanyOne) => _sql.ContextFor(_probe!, companyId);

        private static BusinessContext Ctx(int companyId = CompanyOne, int? employeeId = Employee) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "u", Source = BusinessContextSource.Http,
        };

        // -----------------------------------------------------------------------------------------
        // wiring — the REAL services with the REAL policy reader
        // -----------------------------------------------------------------------------------------

        private sealed class NoContext : IBusinessContextAccessor
        {
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) =>
                throw new BusinessContextUnresolvedException("no request context in this test");
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) =>
                Task.FromResult<BusinessContext?>(null);
        }

        private AccountingAccessService Accounting(CrossDbContext db) =>
            new(db, new Microsoft.AspNetCore.Http.HttpContextAccessor(), new NoContext(),
                new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<AccountingAccessService>.Instance);

        private InventoryAccessService Inventory(CrossDbContext db) =>
            new(db, new Microsoft.AspNetCore.Http.HttpContextAccessor(), new NoContext(),
                new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<InventoryAccessService>.Instance);

        // =========================================================================================
        // SITE 1 — ACCOUNTING
        // =========================================================================================

        [SkippableFact]
        public async Task Accounting_read_is_PRESERVED_through_the_seeded_compatibility_policy()
        {
            Ready();
            await using var db = Db();
            await PolicyAsync(db, "Accounting", "read", BootstrapPolicyStates.LegacyCompatibility);

            // Behaviour preservation: an unconfigured company still permits read — but now because a policy
            // says so, not because a line of code returned true before the action switch.
            Assert.True(await Accounting(db).CanAsync(Ctx(), "read"));
        }

        [SkippableFact]
        public async Task Accounting_read_decision_names_BootstrapLegacyCompatibility_and_the_policy_id()
        {
            Ready();
            await using var db = Db();
            var policy = await PolicyAsync(db, "Accounting", "read", BootstrapPolicyStates.LegacyCompatibility);

            var decision = await Accounting(db).DecideAsync(Ctx(), "read");

            Assert.True(decision.IsAllowed);
            Assert.Equal(AuthorizationDecisionSources.BootstrapLegacyCompatibility, decision.DecisionSource);
            Assert.Equal(policy.ID, decision.BootstrapPolicyId);
            Assert.True(decision.IsBootstrap);
            Assert.False(decision.IsDirectGrant);   // never mistaken for role authorization
        }

        [SkippableTheory]
        [InlineData("post")]
        [InlineData("pay")]
        [InlineData("manage")]
        [InlineData("currency-override")]
        public async Task Accounting_Never_actions_DENY_with_no_role_configured(string action)
        {
            Ready();
            await using var db = Db();
            // Even with a permitting read policy present, the Never actions deny.
            await PolicyAsync(db, "Accounting", "read", BootstrapPolicyStates.LegacyCompatibility);

            var decision = await Accounting(db).DecideAsync(Ctx(), action);

            Assert.False(decision.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.NeverBootstrapOpen, decision.ReasonCode);
            Assert.False(await Accounting(db).CanAsync(Ctx(), action));
        }

        [SkippableFact]
        public async Task Accounting_a_hand_inserted_policy_cannot_open_post()
        {
            Ready();
            await using (var connection = new SqlConnection(_probe!.ConnectionString))
            {
                await connection.OpenAsync();
                await using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO dbo.BootstrapAccessPolicies (CompanyID, Scope, ActionCode, State, IsActive, CreatedAt)" +
                    " VALUES (@c, N'Accounting', N'post', N'ExplicitlyAllowed', 1, SYSUTCDATETIME());";
                cmd.Parameters.AddWithValue("@c", CompanyOne);
                await cmd.ExecuteNonQueryAsync();
            }

            await using var db = Db();
            Assert.False(await Accounting(db).CanAsync(Ctx(), "post"));
        }

        [SkippableFact]
        public async Task Accounting_read_DENIES_when_the_policy_is_missing_expired_or_disabled()
        {
            Ready();
            await using var db = Db();

            // missing
            Assert.False(await Accounting(db).CanAsync(Ctx(), "read"));

            // expired
            var policy = await PolicyAsync(db, "Accounting", "read", BootstrapPolicyStates.Temporary,
                expires: DateTime.UtcNow.AddMinutes(-1), enabled: DateTime.UtcNow.AddDays(-1));
            Assert.False(await Accounting(db).CanAsync(Ctx(), "read"));

            // disabled
            policy.State = BootstrapPolicyStates.Disabled;
            policy.ExpiresAt = null;
            await db.SaveChangesAsync();
            await using var fresh = Db();
            Assert.False(await Accounting(fresh).CanAsync(Ctx(), "read"));
        }

        [SkippableFact]
        public async Task Accounting_configured_roles_behave_EXACTLY_as_before()
        {
            Ready();
            await using var db = Db();

            // No policy at all — once a role is configured the policy is irrelevant, which is the preservation
            // guarantee that matters most: configuring a role must not change what a role holder may do.
            await AccountingRoleAsync(db, "ChiefAccountant");
            var chief = Accounting(db);

            Assert.True(await chief.CanAsync(Ctx(), "read"));
            Assert.True(await chief.CanAsync(Ctx(), "post"));
            Assert.True(await chief.CanAsync(Ctx(), "pay"));
            Assert.True(await chief.CanAsync(Ctx(), "manage"));
            Assert.True(await chief.CanAsync(Ctx(), "currency-override"));

            var decision = await chief.DecideAsync(Ctx(), "post");
            Assert.Equal(AuthorizationDecisionSources.LegacyRole, decision.DecisionSource);
            Assert.Contains("ChiefAccountant", decision.RoleEvidence);
        }

        [SkippableFact]
        public async Task Accounting_a_Cashier_keeps_pay_and_is_still_refused_post_and_manage()
        {
            Ready();
            await using var db = Db();
            await AccountingRoleAsync(db, "Cashier");
            var cashier = Accounting(db);

            Assert.True(await cashier.CanAsync(Ctx(), "read"));
            Assert.True(await cashier.CanAsync(Ctx(), "pay"));
            Assert.False(await cashier.CanAsync(Ctx(), "post"));
            Assert.False(await cashier.CanAsync(Ctx(), "manage"));
            Assert.False(await cashier.CanAsync(Ctx(), "currency-override"));
        }

        [SkippableFact]
        public async Task Accounting_an_Auditor_keeps_read_only()
        {
            Ready();
            await using var db = Db();
            await AccountingRoleAsync(db, "Auditor");
            var auditor = Accounting(db);

            Assert.True(await auditor.CanAsync(Ctx(), "read"));
            Assert.False(await auditor.CanAsync(Ctx(), "post"));
            Assert.False(await auditor.CanAsync(Ctx(), "pay"));

            var decision = await auditor.DecideAsync(Ctx(), "read");
            Assert.Equal(AuthorizationDecisionSources.LegacyRole, decision.DecisionSource);
        }

        [SkippableFact]
        public async Task Accounting_company_mismatch_and_unresolved_context_DENY_with_no_company_1_fallback()
        {
            Ready();
            await using var db = Db();
            await PolicyAsync(db, "Accounting", "read", BootstrapPolicyStates.LegacyCompatibility);
            var service = Accounting(db);

            // Company 2 has no policy. Company 1's must not answer for it — and there is no fallback to 1.
            var other = await service.DecideAsync(Ctx(CompanyTwo), "read");
            Assert.False(other.IsAllowed);
            Assert.Equal(CompanyTwo, other.CompanyID);

            var unresolved = await service.DecideAsync(new BusinessContext { CompanyId = 0 }, "read");
            Assert.False(unresolved.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.CompanyUnresolved, unresolved.ReasonCode);
        }

        [SkippableFact]
        public async Task Accounting_an_unknown_action_denies_and_never_falls_through_to_read()
        {
            Ready();
            await using var db = Db();
            await PolicyAsync(db, "Accounting", "read", BootstrapPolicyStates.LegacyCompatibility);

            Assert.False(await Accounting(db).CanAsync(Ctx(), "not-an-action"));
        }

        [SkippableFact]
        public async Task PlatformOps_is_Never_and_cannot_be_reached_through_Accounting_compatibility()
        {
            Ready();

            // RISK-042. PlatformOpsAttribute falls back to acc.CanAsync("manage"), and `manage` is Never — so a
            // compatibility policy on Accounting cannot hand out platform operations.
            Assert.True(NeverBootstrapOpen.Contains("Platform", "PlatformOps"));
            Assert.True(NeverBootstrapOpen.Contains("Accounting", "manage"));

            await using var db = Db();
            await PolicyAsync(db, "Accounting", "read", BootstrapPolicyStates.LegacyCompatibility);
            await PolicyAsync(db, "Accounting", "manage", BootstrapPolicyStates.LegacyCompatibility, expectValid: false);

            Assert.False(await Accounting(db).CanAsync(Ctx(), "manage"));
        }

        // =========================================================================================
        // SITE 2 — INVENTORY MODULE
        // =========================================================================================

        [SkippableFact]
        public async Task Inventory_read_is_PRESERVED_through_the_seeded_compatibility_policy()
        {
            Ready();
            await using var db = Db();
            var policy = await PolicyAsync(db, "Inventory", "read", BootstrapPolicyStates.LegacyCompatibility);

            Assert.True(await Inventory(db).CanAsync(Ctx(), "read"));

            var decision = await Inventory(db).DecideAsync(Ctx(), "read");
            Assert.Equal(AuthorizationDecisionSources.BootstrapLegacyCompatibility, decision.DecisionSource);
            Assert.Equal(policy.ID, decision.BootstrapPolicyId);
        }

        [SkippableTheory]
        [InlineData("doc")]
        [InlineData("purchase")]
        [InlineData("manage")]
        public async Task Inventory_Never_actions_DENY_with_no_role_configured(string action)
        {
            Ready();
            await using var db = Db();
            await PolicyAsync(db, "Inventory", "read", BootstrapPolicyStates.LegacyCompatibility);

            var decision = await Inventory(db).DecideAsync(Ctx(), action);

            Assert.False(decision.IsAllowed);
            Assert.Equal(AuthorizationReasonCodes.NeverBootstrapOpen, decision.ReasonCode);
        }

        [SkippableFact]
        public async Task Inventory_purchase_stays_ONE_closed_action_preserving_the_vocabulary_defect_decision()
        {
            Ready();
            await using var db = Db();

            // The accepted decision: `purchase` gates a PO draft, a goods receipt (stock) and a PO-to-invoice
            // (accounting). It stays closed WHOLE rather than being split in this increment.
            Assert.True(NeverBootstrapOpen.Contains("Inventory", "purchase"));
            Assert.False(await Inventory(db).CanAsync(Ctx(), "purchase"));

            var entry = NeverBootstrapOpen.Find("Inventory", "purchase");
            Assert.Contains("VOCABULARY DEFECT", entry!.Reason);
        }

        [SkippableFact]
        public async Task Inventory_configured_roles_behave_EXACTLY_as_before()
        {
            Ready();
            await using var db = Db();
            await InventoryRoleAsync(db, "InventoryManager");
            var mgr = Inventory(db);

            Assert.True(await mgr.CanAsync(Ctx(), "read"));
            Assert.True(await mgr.CanAsync(Ctx(), "manage"));
            Assert.True(await mgr.CanAsync(Ctx(), "doc"));
            Assert.True(await mgr.CanAsync(Ctx(), "purchase"));

            var decision = await mgr.DecideAsync(Ctx(), "doc");
            Assert.Equal(AuthorizationDecisionSources.LegacyRole, decision.DecisionSource);
        }

        [SkippableFact]
        public async Task Inventory_a_PurchasingOfficer_keeps_purchase_and_is_refused_doc_and_manage()
        {
            Ready();
            await using var db = Db();
            await InventoryRoleAsync(db, "PurchasingOfficer");
            var purch = Inventory(db);

            Assert.True(await purch.CanAsync(Ctx(), "purchase"));
            Assert.False(await purch.CanAsync(Ctx(), "doc"));
            Assert.False(await purch.CanAsync(Ctx(), "manage"));
            Assert.True(await purch.CanAsync(Ctx(), "read"));
        }

        [SkippableFact]
        public async Task Inventory_read_DENIES_when_the_policy_is_missing_or_expired()
        {
            Ready();
            await using var db = Db();

            Assert.False(await Inventory(db).CanAsync(Ctx(), "read"));

            await PolicyAsync(db, "Inventory", "read", BootstrapPolicyStates.Temporary,
                expires: DateTime.UtcNow.AddMinutes(-1), enabled: DateTime.UtcNow.AddDays(-1));
            await using var fresh = Db();
            Assert.False(await Inventory(fresh).CanAsync(Ctx(), "read"));
        }

        // =========================================================================================
        // SITE 3 — INVENTORY WAREHOUSE SCOPE (narrowing; no seed row)
        // =========================================================================================

        [SkippableFact]
        public async Task Warehouse_access_is_DENIED_with_no_configured_role()
        {
            Ready();
            await using var db = Db();

            // THE NARROWING. Previously an unconfigured company returned true here — company-wide warehouse
            // access. warehouse-access is Never-Bootstrap-Open, so it now denies.
            Assert.False(await Inventory(db).CanUseWarehouseAsync(Ctx(), Warehouse));
        }

        [SkippableTheory]
        [InlineData(BootstrapPolicyStates.LegacyCompatibility)]
        [InlineData(BootstrapPolicyStates.Installation)]
        [InlineData(BootstrapPolicyStates.ExplicitlyAllowed)]
        [InlineData(BootstrapPolicyStates.ReviewRequired)]
        public async Task NO_policy_state_can_open_warehouse_access(string state)
        {
            Ready();

            // Inserted with raw SQL, bypassing Validate(), so the row genuinely claims to permit a Never action.
            await using (var connection = new SqlConnection(_probe!.ConnectionString))
            {
                await connection.OpenAsync();
                await using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "DELETE FROM dbo.BootstrapAccessPolicies WHERE Scope = N'Inventory' AND ActionCode = N'warehouse-access';" +
                    "INSERT INTO dbo.BootstrapAccessPolicies (CompanyID, Scope, ActionCode, State, IsActive, CreatedAt)" +
                    " VALUES (@c, N'Inventory', N'warehouse-access', @s, 1, SYSUTCDATETIME());";
                cmd.Parameters.AddWithValue("@c", CompanyOne);
                cmd.Parameters.AddWithValue("@s", state);
                await cmd.ExecuteNonQueryAsync();
            }

            await using var db = Db();
            Assert.False(await Inventory(db).CanUseWarehouseAsync(Ctx(), Warehouse));
        }

        [SkippableFact]
        public async Task An_InventoryManager_keeps_unrestricted_warehouse_access__role_path_1_unchanged()
        {
            Ready();
            await using var db = Db();
            await InventoryRoleAsync(db, "InventoryManager");

            // ROLE-BASED ALLOW PATH 1 — must remain exactly as it was.
            Assert.True(await Inventory(db).CanUseWarehouseAsync(Ctx(), Warehouse));
            Assert.True(await Inventory(db).CanUseWarehouseAsync(Ctx(), OtherWarehouse));
        }

        [SkippableFact]
        public async Task An_UNSCOPED_WarehouseKeeper_keeps_access_to_any_warehouse__role_path_2_unchanged()
        {
            Ready();
            await using var db = Db();
            await InventoryRoleAsync(db, "WarehouseKeeper", scopeBranchId: null);

            // ROLE-BASED ALLOW PATH 2 — the unscoped keeper. Must remain exactly as it was.
            Assert.True(await Inventory(db).CanUseWarehouseAsync(Ctx(), Warehouse));
        }

        [SkippableFact]
        public async Task A_BRANCH_SCOPED_WarehouseKeeper_reaches_only_its_own_branch_exactly()
        {
            Ready();
            await using var db = Db();
            await InventoryRoleAsync(db, "WarehouseKeeper", scopeBranchId: Branch);

            Assert.True(await Inventory(db).CanUseWarehouseAsync(Ctx(), Warehouse));        // in Branch
            Assert.False(await Inventory(db).CanUseWarehouseAsync(Ctx(), OtherWarehouse));  // in OtherBranch
        }

        [SkippableFact]
        public async Task A_keeper_cannot_reach_a_warehouse_in_another_company()
        {
            Ready();
            await using var db = Db();
            await InventoryRoleAsync(db, "WarehouseKeeper", scopeBranchId: null);

            // The warehouse lookup is company-intersected, so a foreign warehouse id is refused even for an
            // unscoped keeper.
            Assert.False(await Inventory(db).CanUseWarehouseAsync(Ctx(CompanyTwo), Warehouse));
        }

        // =========================================================================================
        // THE TWO CRM SITES ARE NOT CONVERTED
        // =========================================================================================

        [SkippableFact]
        public async Task CRM_remains_on_Mechanism_A_and_is_asserted_unchanged()
        {
            Ready();

            // CrmAccessService must NOT have gained the policy reader: its two sites are blocked on unresolved
            // business decisions. Asserted structurally so a premature conversion fails here rather than being
            // noticed in review.
            var ctor = typeof(CrmAccessService).GetConstructors().Single();
            var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

            Assert.DoesNotContain(typeof(IBootstrapAccessPolicyReader), parameterTypes);

            // And VisibleOwnerIdsAsync still returns the legacy nullable shape.
            var method = typeof(ICrmAccessService).GetMethod("VisibleOwnerIdsAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<HashSet<int>?>), method!.ReturnType);

            await Task.CompletedTask;
        }

        // =========================================================================================
        // helpers
        // =========================================================================================

        private async Task<BootstrapAccessPolicy> PolicyAsync(
            CrossDbContext db, string scope, string action, string state,
            DateTime? expires = null, DateTime? enabled = null, bool expectValid = true)
        {
            var policy = new BootstrapAccessPolicy
            {
                CompanyID = CompanyOne, Scope = scope, ActionCode = action, State = state,
                Reason = "B6 conversion evidence", EnabledAt = enabled ?? DateTime.UtcNow.AddDays(-1),
                ExpiresAt = expires, CreatedAt = DateTime.UtcNow,
                SourceSystem = BootstrapPolicySources.BehaviourPreservingSeed, IsActive = true,
            };

            if (expectValid) Assert.Empty(policy.Validate());

            db.BootstrapAccessPolicies.Add(policy);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException) when (!expectValid)
            {
                // A Never action cannot be stored as a permitting policy — the point of expectValid: false.
                db.Entry(policy).State = EntityState.Detached;
            }

            return policy;
        }

        private async Task AccountingRoleAsync(CrossDbContext db, string role)
        {
            db.AccountingUserRoles.Add(new Models.Context.Accounting.AccountingUserRole
            { CompanyID = CompanyOne, EmployeeId = Employee, Role = role, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        private async Task InventoryRoleAsync(CrossDbContext db, string role, int? scopeBranchId = null)
        {
            db.InventoryUserRoles.Add(new Models.Context.Inventory.InventoryUserRole
            {
                CompanyID = CompanyOne, EmployeeId = Employee, Role = role,
                ScopeBranchId = scopeBranchId, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedInfrastructureAsync()
        {
            await using var db = Db();
            await db.Database.OpenConnectionAsync();
            try
            {
                await FillAsync(db, "dbo.Companies", () =>
                {
                    db.Companies.Add(new Models.Context.Admin.Companies
                    { CompanyID = CompanyOne, CompanyName = "ZZ B6 One" });
                    db.Companies.Add(new Models.Context.Admin.Companies
                    { CompanyID = CompanyTwo, CompanyName = "ZZ B6 Two" });
                });

                await FillAsync(db, "dbo.Employee", () =>
                    db.Employee.Add(new Models.Context.Admin.Employee
                    {
                        ID = Employee, EmpCompanyID = CompanyOne, IsActive = true, UserId = "u",
                        FullName = "ZZ B6", FirstName = "ZZ", LastName = "B6",
                    }));

                await FillAsync(db, "dbo.Warehouses", () =>
                {
                    db.Warehouses.Add(new Models.Context.Inventory.Warehouse
                    { ID = Warehouse, CompanyID = CompanyOne, BranchHierarchicalId = Branch, Name = "ZZ WH1" });
                    db.Warehouses.Add(new Models.Context.Inventory.Warehouse
                    { ID = OtherWarehouse, CompanyID = CompanyOne, BranchHierarchicalId = OtherBranch, Name = "ZZ WH2" });
                });
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
