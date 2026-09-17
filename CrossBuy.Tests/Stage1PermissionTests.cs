using CrossBuy.BL;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Pos;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch A — session-free permission evaluation.
    //
    // Every test in this file constructs the access services with **no HttpContext at all**
    // (`new HttpContextAccessor()` with HttpContext = null). That is the whole point: before Stage 1 the
    // accounting, inventory and CRM services resolved the current employee from Session["Employee"] and the
    // company from `const int CompanyId = 1`, so outside a request they could not answer a question about
    // anyone — and inside one they answered about the wrong company on a multi-company install.
    public class Stage1PermissionTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        private const int ChiefId = 11;      // ChiefAccountant in company 1
        private const int ClerkId = 12;      // no accounting role, company 1
        private const int OtherCoChiefId = 13;   // ChiefAccountant row in company 2

        private static Employee Emp(int id, int companyId, int? branchId = null) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "T", FullNameEn = "T",
            EmpCompanyID = companyId, BranchID = branchId, IsActive = true,
            Address = "-", PhoneNumber = "-", Email = $"e{id}@example.com", ProfileImage = "-",
            Gender = "M", MaritalStatus = "S", UserId = "user-" + id,
        };

        // No HttpContext. If any of these services still reached for the session, every test here would fail.
        private static IHttpContextAccessor NoHttp() => new HttpContextAccessor();

        private static AccountingAccessService Accounting(PlatformTestHost host)
            => new(host.Db, NoHttp(), new BusinessContextAccessor(host.Contexts(http: NoHttp())),
                   B6TestWiring.Policies(host.Db), B6TestWiring.Log<AccountingAccessService>());

        private static InventoryAccessService Inventory(PlatformTestHost host)
            => new(host.Db, NoHttp(), new BusinessContextAccessor(host.Contexts(http: NoHttp())),
                   B6TestWiring.Policies(host.Db), B6TestWiring.Log<InventoryAccessService>());

        private static CrmAccessService Crm(PlatformTestHost host)
            => new(host.Db, NoHttp(), new BusinessContextAccessor(host.Contexts(http: NoHttp())),
                new OrgHierarchy(host.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrgHierarchy>.Instance),
                B6TestWiring.Policies(host.Db), NullLogger<CrmAccessService>.Instance);

        private static PosAccessService Pos(PlatformTestHost host) => new(host.Db);

        private static BusinessContext Ctx(int employeeId, int companyId, int? branchId = null, string? userId = null) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, BranchId = branchId,
            UserId = userId ?? ("user-" + employeeId), Source = BusinessContextSource.Test,
        };

        // =====================================================================================
        // A8/6 — Accounting without HTTP Session
        // =====================================================================================

        [Fact]
        public async Task Accounting_permission_is_evaluated_without_an_http_session()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.AddRange(Emp(ChiefId, CompanyOne), Emp(ClerkId, CompanyOne));
            host.Db.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var accounting = Accounting(host);

            // The chief and the clerk get DIFFERENT answers, from the same service, in the same process, with
            // no session. That is the capability Stage 1 exists to add.
            Assert.True(await accounting.CanAsync(Ctx(ChiefId, CompanyOne), "manage"));
            Assert.True(await accounting.CanAsync(Ctx(ChiefId, CompanyOne), "post"));
            Assert.False(await accounting.CanAsync(Ctx(ClerkId, CompanyOne), "manage"));
            Assert.False(await accounting.CanAsync(Ctx(ClerkId, CompanyOne), "post"));
            // "read" is the module's own decision that any authenticated user may look.
            Assert.True(await accounting.CanAsync(Ctx(ClerkId, CompanyOne), "read"));
        }

        [Fact]
        public async Task Accounting_roles_are_read_for_the_contexts_company_not_company_1()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.AddRange(Emp(ChiefId, CompanyOne), Emp(OtherCoChiefId, CompanyTwo));
            // Company 1 has a configured chief; company 2 has its own chief row.
            host.Db.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyTwo, EmployeeId = OtherCoChiefId, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var accounting = Accounting(host);

            Assert.True(await accounting.CanAsync(Ctx(OtherCoChiefId, CompanyTwo), "manage"));
            // The company-1 chief has NO role in company 2 — before Stage 1 the const would have looked up
            // company 1's rows for this call and wrongly granted it.
            Assert.False(await accounting.CanAsync(Ctx(ChiefId, CompanyTwo), "manage"));
        }

        [Fact]
        // B6 TRANSITION. Per-company evaluation still holds; what changed is that bootstrap no longer opens
        // `manage`. The company-1 half is PRESERVED unchanged — it is the role-preservation assertion.
        public async Task Per_company_evaluation_holds_and_bootstrap_no_longer_opens_manage()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.AddRange(Emp(ChiefId, CompanyOne), Emp(OtherCoChiefId, CompanyTwo));
            // ONLY company 1 has accounting roles configured.
            host.Db.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            var accounting = Accounting(host);

            // Company 1 is configured, so its RBAC is live and a roleless user is denied.
            Assert.False(await accounting.CanAsync(Ctx(ClerkId, CompanyOne), "manage"));
            // B6 TRANSITION. Company 2 configured nothing and used to stay OPEN. Accounting.manage is
            // Never-Bootstrap-Open — it gates AssignAccRole and is PlatformOpsAttribute's fallback authority — so
            // an unconfigured company no longer receives it.
            Assert.True(NeverBootstrapOpen.Contains("Accounting", "manage"));
            Assert.False(await accounting.CanAsync(Ctx(OtherCoChiefId, CompanyTwo), "manage"));
        }

        // =====================================================================================
        // A8/7 — Inventory without HTTP Session, including the warehouse gate
        // =====================================================================================

        [Fact]
        public async Task Inventory_permission_is_evaluated_without_an_http_session()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.AddRange(Emp(21, CompanyOne), Emp(22, CompanyOne));
            host.Db.InventoryUserRoles.Add(new InventoryUserRole { CompanyID = CompanyOne, EmployeeId = 21, Role = "InventoryManager" });
            await host.Db.SaveChangesAsync();

            var inventory = Inventory(host);

            Assert.True(await inventory.CanAsync(Ctx(21, CompanyOne), "manage"));
            Assert.True(await inventory.CanAsync(Ctx(21, CompanyOne), "doc"));
            Assert.False(await inventory.CanAsync(Ctx(22, CompanyOne), "doc"));
            Assert.True(await inventory.CanAsync(Ctx(22, CompanyOne), "read"));
        }

        [Fact]
        public async Task A_warehouse_keeper_is_scoped_to_their_branch_and_company()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(31, CompanyOne));
            host.Db.InventoryUserRoles.Add(new InventoryUserRole
            { CompanyID = CompanyOne, EmployeeId = 31, Role = "WarehouseKeeper", ScopeBranchId = 500 });
            await host.Db.SaveChangesAsync();

            // B4: one of these warehouses belongs to ANOTHER company — that is the whole point of the test — so the
            // three are arranged through the authorized cross-company context. The permission checks below still run
            // in the company-1 scope, which is what is being proven.
            host.Seed.Warehouses.AddRange(
                new Warehouse { ID = 1, CompanyID = CompanyOne, Name = "in scope", Code = "A", BranchHierarchicalId = 500 },
                new Warehouse { ID = 2, CompanyID = CompanyOne, Name = "other branch", Code = "B", BranchHierarchicalId = 501 },
                new Warehouse { ID = 3, CompanyID = CompanyTwo, Name = "other company", Code = "C", BranchHierarchicalId = 500 });
            await host.Seed.SaveChangesAsync();

            var inventory = Inventory(host);
            var context = Ctx(31, CompanyOne);

            Assert.True(await inventory.CanUseWarehouseAsync(context, 1));
            Assert.False(await inventory.CanUseWarehouseAsync(context, 2));
            // Same branch id, DIFFERENT company: before Stage 1 the const restricted the lookup to company 1,
            // so a keeper could match a warehouse by branch id regardless of tenancy.
            Assert.False(await inventory.CanUseWarehouseAsync(context, 3));

            // And the gate is reachable through the single canonical question, with a target.
            Assert.True(await inventory.CanAsync(context, "doc", PermissionTarget.ForWarehouse(1)));
            Assert.False(await inventory.CanAsync(context, "doc", PermissionTarget.ForWarehouse(2)));
        }

        // =====================================================================================
        // A8/8 — CRM without HTTP Session, including record ownership
        // =====================================================================================

        [Fact]
        public async Task Crm_permission_and_record_ownership_are_evaluated_without_an_http_session()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.AddRange(Emp(41, CompanyOne), Emp(42, CompanyOne));
            host.Db.CrmUserRoles.AddRange(
                new CrmUserRole { CompanyID = CompanyOne, EmployeeId = 41, Role = "SalesRep" },
                new CrmUserRole { CompanyID = CompanyOne, EmployeeId = 42, Role = "SalesRep" });
            await host.Db.SaveChangesAsync();

            var crm = Crm(host);
            var rep = Ctx(41, CompanyOne);

            Assert.True(await crm.CanAsync(rep, "edit"));
            Assert.False(await crm.CanAsync(rep, "manage"));

            // A SalesRep sees only their OWN records — the one real record-owner rule in the codebase, now
            // answerable for an arbitrary employee outside a request.
            var visible = await crm.VisibleOwnerIdsAsync(rep);
            Assert.NotNull(visible);
            Assert.Equal(new[] { 41 }, visible!.OrderBy(x => x));

            Assert.True(await crm.CanAsync(rep, "edit", PermissionTarget.ForOwner(41)));
            Assert.False(await crm.CanAsync(rep, "edit", PermissionTarget.ForOwner(42)));
        }

        [Fact]
        public async Task Crm_marketing_and_viewer_remain_unrestricted_by_owner()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(43, CompanyOne));
            host.Db.CrmUserRoles.Add(new CrmUserRole { CompanyID = CompanyOne, EmployeeId = 43, Role = "Marketing" });
            await host.Db.SaveChangesAsync();

            // null = unrestricted, unchanged from before Stage 1.
            Assert.Null(await Crm(host).VisibleOwnerIdsAsync(Ctx(43, CompanyOne)));
        }

        // =====================================================================================
        // A8/9 — POS semantics preserved
        // =====================================================================================

        [Fact]
        public async Task Pos_adapter_preserves_the_existing_role_predicates()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(51, CompanyOne, branchId: 900));
            host.Db.Branches.Add(new Branch
            {
                ID = 900, CompanyID = 71, Name = "b900", NameAr = "ب", Location = "-", CountryID = 1,
                PhoneNumber = "-", Email = "-", Description = "-",
            });
            host.Db.BranchUserRoles.Add(new BranchUserRole { BranchId = 900, EmployeeId = 51, PosRole = "pos-cashier", IsActive = true });
            await host.Db.SaveChangesAsync();

            var pos = Pos(host);
            var context = Ctx(51, CompanyOne, userId: "user-51");

            // The predicates themselves are untouched — asserted directly, then through the canonical method.
            Assert.True(pos.CanSell(new[] { "pos-cashier" }));
            Assert.False(pos.IsManager(new[] { "pos-cashier" }));

            Assert.True(await pos.CanAsync(context, "view"));
            Assert.True(await pos.CanAsync(context, "sell"));
            Assert.True(await pos.CanAsync(context, "order"));
            Assert.False(await pos.CanAsync(context, "manage"));
            Assert.False(await pos.CanAsync(context, "kitchen"));
        }

        [Fact]
        public async Task Pos_resolution_reports_both_the_catalog_company_and_the_branch_company()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(52, CompanyOne, branchId: 901));
            host.Db.Branches.Add(new Branch
            {
                ID = 901, CompanyID = 71, Name = "hyper", NameAr = "ه", Location = "-", CountryID = 1,
                PhoneNumber = "-", Email = "-", Description = "-",
            });
            host.Db.BranchUserRoles.Add(new BranchUserRole { BranchId = 901, EmployeeId = 52, PosRole = "pos-manager", IsActive = true });
            await host.Db.SaveChangesAsync();

            var resolved = await Pos(host).ResolveByUserIdAsync("user-52");

            Assert.NotNull(resolved);
            // The catalog company stays 1 — deliberately, per PosCompanyPolicy. Changing it would change POS
            // accounting, which Stage 1 does not touch.
            Assert.Equal(PosCompanyPolicy.CatalogCompanyId, resolved!.CompanyId);
            // The branch's REAL company is now reported too, which is what the session blob and the business
            // context need in order to state tenancy honestly.
            Assert.Equal(71, resolved.BranchCompanyId);
        }

        [Fact]
        public async Task A_pos_user_is_confined_to_their_own_branch()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(53, CompanyOne, branchId: 902));
            host.Db.Branches.Add(new Branch
            {
                ID = 902, CompanyID = 71, Name = "b902", NameAr = "ب", Location = "-", CountryID = 1,
                PhoneNumber = "-", Email = "-", Description = "-",
            });
            host.Db.BranchUserRoles.Add(new BranchUserRole { BranchId = 902, EmployeeId = 53, PosRole = "pos-cashier", IsActive = true });
            await host.Db.SaveChangesAsync();

            var pos = Pos(host);
            var context = Ctx(53, CompanyOne, userId: "user-53");

            Assert.True(await pos.CanAsync(context, "sell", new PermissionTarget { BranchId = 902 }));
            Assert.False(await pos.CanAsync(context, "sell", new PermissionTarget { BranchId = 903 }));
        }

        // =====================================================================================
        // A8/10, A8/11 — unknown action / unknown entity are denied
        // =====================================================================================

        [Fact]
        public async Task An_unknown_module_action_is_denied_by_every_service()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(ChiefId, CompanyOne));
            host.Db.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" });
            host.Db.InventoryUserRoles.Add(new InventoryUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "InventoryManager" });
            host.Db.CrmUserRoles.Add(new CrmUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "SalesManager" });
            await host.Db.SaveChangesAsync();

            var context = Ctx(ChiefId, CompanyOne);

            // A ChiefAccountant / InventoryManager / SalesManager — the strongest role in each module — still
            // gets a deny for a vocabulary the module does not define. Unknown must never fall through.
            Assert.False(await Accounting(host).CanAsync(context, "delete"));
            Assert.False(await Inventory(host).CanAsync(context, "approve"));
            Assert.False(await Crm(host).CanAsync(context, "post"));
            Assert.False(await Accounting(host).CanAsync(context, ""));
            Assert.False(await Accounting(host).CanAsync(context, "   "));
        }

        [Fact]
        public async Task An_unregistered_entity_type_is_denied_by_the_provider()
        {
            using var host = new PlatformTestHost();
            var provider = Provider(host);

            var decision = await provider.CanAsync(Ctx(ChiefId, CompanyOne), "NotAnEntity", 1, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.Contains("not registered", decision.Reason!);
        }

        [Fact]
        public async Task A_scope_with_no_registered_module_service_is_denied()
        {
            using var host = new PlatformTestHost();
            // An accounting adapter with NO module services registered at all.
            var adapter = new AccountingPermissionAdapter(Array.Empty<IModuleAccessService>());

            var decision = await adapter.CanAsync(new PermissionCheckRequest
            {
                Context = Ctx(ChiefId, CompanyOne), EntityType = EntityRegistry.SalesInvoice,
                EntityId = 1, Action = PlatformActions.View,
            });

            // Deny, not allow — a missing module is as broken as a missing adapter and must fail the same way.
            Assert.False(decision.Allowed);
            Assert.Contains("No IModuleAccessService is registered", decision.Reason!);
        }

        // =====================================================================================
        // A8/12–14 — visibility tiers, and A8/15 — cross-company
        // =====================================================================================

        private static IPlatformPermissionProvider Provider(PlatformTestHost host)
        {
            var modules = new List<IModuleAccessService>
            {
                Accounting(host), Inventory(host), Crm(host), Pos(host),
            };
            var adapters = new List<IModulePermissionAdapter>
            {
                new AccountingPermissionAdapter(modules),
                new InventoryPermissionAdapter(modules),
                new ManufacturingPermissionAdapter(modules),
                new CrmPermissionAdapter(modules),
                new PosPermissionAdapter(modules),
                new DefaultPermissionAdapter(),
            };
            return new PlatformPermissionProvider(host.Registry(), adapters, NullLogger<PlatformPermissionProvider>.Instance);
        }

        private static async Task<int> AddInvoiceAsync(PlatformTestHost host, int companyId)
        {
            var invoice = new SalesInvoice
            {
                CompanyID = companyId, InvoiceNo = "SI-" + companyId, InvoiceDate = new DateTime(2026, 1, 1),
                Status = "Posted", CustomerId = 1,
            };
            // B4: this helper is called with another company's id by the isolation tests, so it arranges through
            // the authorized cross-company context. The permission checks under test still run in their own scope.
            host.Seed.SalesInvoices.Add(invoice);
            await host.Seed.SaveChangesAsync();
            return invoice.ID;
        }

        [Fact]
        public async Task Confidential_and_restricted_tiers_are_enforced_per_employee_without_a_session()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.AddRange(Emp(ChiefId, CompanyOne), Emp(61, CompanyOne), Emp(ClerkId, CompanyOne));
            host.Db.AccountingUserRoles.AddRange(
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" },
                new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = 61, Role = "Accountant" });
            await host.Db.SaveChangesAsync();
            int invoiceId = await AddInvoiceAsync(host, CompanyOne);

            var provider = Provider(host);

            // View: everyone in the company.
            Assert.True((await provider.CanAsync(Ctx(ClerkId, CompanyOne), EntityRegistry.SalesInvoice, invoiceId, PlatformActions.View)).Allowed);

            // Confidential maps to accounting "post": Accountant and Chief yes, roleless clerk no.
            Assert.True((await provider.CanAsync(Ctx(61, CompanyOne), EntityRegistry.SalesInvoice, invoiceId, PlatformActions.ViewConfidential)).Allowed);
            Assert.True((await provider.CanAsync(Ctx(ChiefId, CompanyOne), EntityRegistry.SalesInvoice, invoiceId, PlatformActions.ViewConfidential)).Allowed);
            Assert.False((await provider.CanAsync(Ctx(ClerkId, CompanyOne), EntityRegistry.SalesInvoice, invoiceId, PlatformActions.ViewConfidential)).Allowed);

            // Restricted maps to "manage": Chief only. An Accountant holding a module role does NOT get
            // restricted content — the A5 rule that a module role alone must not expose it.
            Assert.True((await provider.CanAsync(Ctx(ChiefId, CompanyOne), EntityRegistry.SalesInvoice, invoiceId, PlatformActions.ViewRestricted)).Allowed);
            Assert.False((await provider.CanAsync(Ctx(61, CompanyOne), EntityRegistry.SalesInvoice, invoiceId, PlatformActions.ViewRestricted)).Allowed);
        }

        [Fact]
        public async Task A_system_context_gets_only_the_declared_actions_and_never_the_elevated_tiers()
        {
            using var host = new PlatformTestHost();
            int invoiceId = await AddInvoiceAsync(host, CompanyOne);
            var provider = Provider(host);
            var system = BusinessContext.ForSystem(CompanyOne);

            // The policy is short and explicit: View only.
            Assert.Equal(new[] { PlatformActions.View }, SystemContextPolicy.AllowedActions);

            Assert.True((await provider.CanAsync(system, EntityRegistry.SalesInvoice, invoiceId, PlatformActions.View)).Allowed);

            // These two were ALLOWED before Stage 1, by a single `if (context.IsSystem) return Allow(...)`.
            var confidential = await provider.CanAsync(system, EntityRegistry.SalesInvoice, invoiceId, PlatformActions.ViewConfidential);
            var restricted = await provider.CanAsync(system, EntityRegistry.SalesInvoice, invoiceId, PlatformActions.ViewRestricted);
            Assert.False(confidential.Allowed);
            Assert.False(restricted.Allowed);
            Assert.Contains("system context may perform only", restricted.Reason!);
        }

        [Fact]
        public async Task A_system_context_is_still_confined_to_its_own_company()
        {
            using var host = new PlatformTestHost();
            int companyTwoInvoice = await AddInvoiceAsync(host, CompanyTwo);
            var provider = Provider(host);

            // Trusted code is scoped to ONE company by its caller; reading another company's record is a
            // tenancy breach regardless of trust.
            var decision = await provider.CanAsync(
                BusinessContext.ForSystem(CompanyOne), EntityRegistry.SalesInvoice, companyTwoInvoice, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.Contains("company", decision.Reason!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_worker_context_is_not_treated_as_a_system_context()
        {
            using var host = new PlatformTestHost();
            int invoiceId = await AddInvoiceAsync(host, CompanyOne);
            var provider = Provider(host);

            // A worker has no employee identity, so it is not authenticated and must be denied — it does NOT
            // inherit SystemContextPolicy's grant. A worker that needs a decision uses ForEmployeeAsync.
            var decision = await provider.CanAsync(
                BusinessContext.ForWorker(CompanyOne), EntityRegistry.SalesInvoice, invoiceId, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.Contains("No authenticated user", decision.Reason!);
        }

        [Fact]
        public async Task A_cross_company_permission_check_is_denied_before_the_module_is_consulted()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(OtherCoChiefId, CompanyTwo));
            // A genuine ChiefAccountant — in company 2.
            host.Db.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyTwo, EmployeeId = OtherCoChiefId, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();
            int companyOneInvoice = await AddInvoiceAsync(host, CompanyOne);

            var provider = Provider(host);

            // Holding the strongest accounting role in company 2 must not reach a company-1 invoice. This is
            // why company isolation is checked BEFORE the module: the module's role check knows nothing about
            // which record is being asked about.
            var decision = await provider.CanAsync(
                Ctx(OtherCoChiefId, CompanyTwo), EntityRegistry.SalesInvoice, companyOneInvoice, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.Contains($"not in company {CompanyTwo}", decision.Reason!);
        }

        [Fact]
        public async Task An_unauthenticated_context_is_denied()
        {
            using var host = new PlatformTestHost();
            int invoiceId = await AddInvoiceAsync(host, CompanyOne);

            var decision = await Provider(host).CanAsync(
                new BusinessContext { CompanyId = CompanyOne, Source = BusinessContextSource.Test },
                EntityRegistry.SalesInvoice, invoiceId, PlatformActions.View);

            Assert.False(decision.Allowed);
            Assert.Contains("No authenticated user", decision.Reason!);
        }

        // =====================================================================================
        // A8/18 — the legacy surface is intact
        // =====================================================================================

        [Fact]
        public async Task The_legacy_session_based_methods_deny_when_nothing_can_be_resolved()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(ChiefId, CompanyOne));
            host.Db.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = CompanyOne, EmployeeId = ChiefId, Role = "ChiefAccountant" });
            await host.Db.SaveChangesAsync();

            // The legacy signatures still exist and are still callable — this is what the 168 permission
            // attributes and 34 call sites depend on. With no HttpContext they DENY, where before Stage 1 they
            // would have evaluated against a company-1 context.
            var accounting = Accounting(host);
            Assert.False(await accounting.CanAsync("manage"));
            Assert.Empty(await accounting.MyRolesAsync());
            Assert.Null(accounting.CurrentEmployeeId());
            Assert.Equal("Viewer", await accounting.RoleLabelAsync(isAr: false));

            var inventory = Inventory(host);
            Assert.False(await inventory.CanAsync("doc"));
            Assert.False(await inventory.CanUseWarehouseAsync(1));

            var crm = Crm(host);
            Assert.False(await crm.CanAsync("edit"));
            // An unresolved caller sees NOTHING, rather than the "null = unrestricted" answer it used to get
            // by way of the company-1 context.
            var visible = await crm.VisibleOwnerIdsAsync();
            Assert.NotNull(visible);
            Assert.Empty(visible!);
        }

        [Fact]
        public void The_module_vocabularies_are_published_and_match_the_documented_actions()
        {
            using var host = new PlatformTestHost();

            Assert.Equal(new[] { "read", "post", "pay", "manage", "currency-override" }, Accounting(host).Actions);
            Assert.Equal(new[] { "read", "doc", "purchase", "manage" }, Inventory(host).Actions);
            Assert.Equal(new[] { "read", "edit", "manage" }, Crm(host).Actions);
            Assert.Equal(new[] { "view", "sell", "order", "kitchen", "manage" }, Pos(host).Actions);

            Assert.Equal(EntityRegistry.ScopeAccounting, Accounting(host).Scope);
            Assert.Equal(EntityRegistry.ScopeInventory, Inventory(host).Scope);
            Assert.Equal(EntityRegistry.ScopeCrm, Crm(host).Scope);
            Assert.Equal(EntityRegistry.ScopePos, Pos(host).Scope);
        }

        // ---- Source-level guard: the canonical permission path must not read the session ----
        [Fact]
        public void No_canonical_permission_method_reads_the_http_session()
        {
            var root = FindRepoRoot();
            foreach (var file in new[] { "AccountingAccessService.cs", "InventoryAccessService.cs", "CrmAccessService.cs", "PosAccessService.cs" })
            {
                var path = Path.Combine(root, "CrossBuy", "BL", file);
                var code = File.ReadAllText(path);

                // Session may still be read by exactly ONE member: the legacy synchronous CurrentEmployeeId(),
                // which 26 view/controller call sites use for display and which is no longer consulted by any
                // permission decision. Anything more than one occurrence means the session crept back in.
                int occurrences = System.Text.RegularExpressions.Regex.Matches(code, @"Session\.GetString").Count;
                Assert.True(occurrences <= 1,
                    $"{file} reads Session.GetString {occurrences} times; only the legacy CurrentEmployeeId() may.");
            }
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrossBuy", "BL", "AccountingAccessService.cs")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
        }
    }
}
