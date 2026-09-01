using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch A — BusinessContext resolution.
    //
    // The single most important assertion in this file is that a missing company does NOT become company 1.
    // That fallback existed in BusinessContextAccessor and fired on a real production path (the POS KDS and
    // delivery screens wrote a session blob with no ids at all), so every test that pins its absence is
    // guarding a defect that was live, not a hypothetical.
    public class Stage1ContextTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        private static Employee Emp(int id, int companyId, int? branchId = null, bool active = true, string? userId = null) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "T", FullNameEn = "T",
            EmpCompanyID = companyId, BranchID = branchId, IsActive = active,
            Address = "-", PhoneNumber = "-", Email = $"e{id}@example.com", ProfileImage = "-",
            Gender = "M", MaritalStatus = "S", UserId = userId ?? ("user-" + id),
        };

        private static Branch Branch(int id, int companyId) => new()
        {
            ID = id, CompanyID = companyId, Name = "b" + id, NameAr = "ب" + id, Location = "-",
            CountryID = 1, PhoneNumber = "-", Email = "-", Description = "-",
        };

        // A real HttpContext with a real session, so the resolution path under test is the production one.
        private sealed class FakeSession : ISession
        {
            private readonly Dictionary<string, byte[]> _store = new();
            public bool IsAvailable => true;
            public string Id => "test-session";
            public IEnumerable<string> Keys => _store.Keys;
            public void Clear() => _store.Clear();
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Remove(string key) => _store.Remove(key);
            public void Set(string key, byte[] value) => _store[key] = value;
            public bool TryGetValue(string key, out byte[] value) => _store.TryGetValue(key, out value!);
        }

        private static IHttpContextAccessor Http(EmployeeViewModel? sessionBlob, string? nameIdentifier = null)
        {
            var http = new DefaultHttpContext { Session = new FakeSession() };
            if (sessionBlob != null)
                http.Session.SetString("Employee", JsonSerializer.Serialize(sessionBlob));
            if (nameIdentifier != null)
                http.User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(new[]
                    {
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, nameIdentifier),
                    }, "test"));
            return new HttpContextAccessor { HttpContext = http };
        }

        // ---- A8/1: HTTP context resolves the authenticated employee and company ----
        [Fact]
        public async Task Http_context_resolves_the_authenticated_employee_and_company()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, CompanyTwo, branchId: null));
            await host.Db.SaveChangesAsync();

            var scope = new CompanyScopeHolder();
            var factory = host.Contexts(http: Http(new EmployeeViewModel { ID = 7, EmpCompanyID = CompanyTwo, UserId = "user-7" }), scope: scope);

            var context = await factory.ForHttpAsync();

            Assert.Equal(7, context.EmployeeId);
            Assert.Equal(CompanyTwo, context.CompanyId);
            Assert.Equal("user-7", context.UserId);
            Assert.Equal(BusinessContextSource.Http, context.Source);
            Assert.False(context.IsSystem);
            Assert.True(context.IsAuthenticated);
            Assert.NotNull(context.CorrelationId);

            // The scope holder Batch B's query filters will read is populated by the same act.
            Assert.True(scope.IsResolved);
            Assert.Equal(CompanyTwo, scope.CompanyId);
        }

        // ---- A8/1b: identity resolvable from claims alone, with no session blob ----
        [Fact]
        public async Task Http_context_resolves_from_claims_when_there_is_no_session_blob()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(9, CompanyTwo, userId: "aspnet-9"));
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts(http: Http(sessionBlob: null, nameIdentifier: "aspnet-9"));

            var context = await factory.ForHttpAsync();

            Assert.Equal(9, context.EmployeeId);
            Assert.Equal(CompanyTwo, context.CompanyId);
        }

        // ---- A8/2: an invalid selected company is rejected — the Employee row wins ----
        [Fact]
        public async Task A_session_blob_claiming_another_company_does_not_win()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, CompanyTwo));
            await host.Db.SaveChangesAsync();

            // Tampered or stale blob: the employee really belongs to company 2.
            var factory = host.Contexts(http: Http(new EmployeeViewModel { ID = 7, EmpCompanyID = 99, UserId = "user-7" }));

            var context = await factory.ForHttpAsync();

            Assert.Equal(CompanyTwo, context.CompanyId);
            Assert.NotEqual(99, context.CompanyId);
        }

        // ---- A8/2b: a session branch that is not a branch of this company is rejected ----
        [Fact]
        public async Task A_session_branch_from_another_company_is_rejected()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, CompanyOne, branchId: 10));
            host.Db.Branches.Add(Branch(10, CompanyOne));
            host.Db.Branches.Add(Branch(20, companyId: 77));
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts(http: Http(new EmployeeViewModel
            {
                ID = 7, EmpCompanyID = CompanyOne, BranchID = 20, UserId = "user-7",
            }));

            var context = await factory.ForHttpAsync();

            Assert.Equal(10, context.BranchId);      // fell back to the employee's own branch
            Assert.NotEqual(20, context.BranchId);
        }

        // ---- A8/2c: a session branch that DOES belong to this company is honoured (POS lanes need this) ----
        [Fact]
        public async Task A_session_branch_within_the_same_company_is_honoured()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, CompanyOne, branchId: 10));
            foreach (var id in new[] { 10, 11 }) host.Db.Branches.Add(Branch(id, CompanyOne));
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts(http: Http(new EmployeeViewModel
            {
                ID = 7, EmpCompanyID = CompanyOne, BranchID = 11, UserId = "user-7",
            }));

            Assert.Equal(11, (await factory.ForHttpAsync()).BranchId);
        }

        // =====================================================================================
        // A8/3 — the headline: a missing company NEVER becomes company 1
        // =====================================================================================

        // This is the exact POS KDS / delivery blob as it was written before Stage 1: a name and nothing else.
        [Fact]
        public async Task An_incomplete_session_blob_does_not_resolve_to_company_1()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, CompanyOne));
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts(http: Http(new EmployeeViewModel
            {
                FullName = "Kitchen user", Email = "", ProfileImage = "",   // no ID, no company, no branch
            }));

            Assert.Null(await factory.TryForHttpAsync());
            var ex = await Assert.ThrowsAsync<BusinessContextUnresolvedException>(() => factory.ForHttpAsync());
            Assert.Contains("no default company", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_anonymous_request_does_not_resolve_to_company_1()
        {
            using var host = new PlatformTestHost();
            var factory = host.Contexts(http: Http(sessionBlob: null));

            Assert.Null(await factory.TryForHttpAsync());
            await Assert.ThrowsAsync<BusinessContextUnresolvedException>(() => factory.ForHttpAsync());
        }

        [Fact]
        public async Task An_employee_with_no_company_does_not_resolve_to_company_1()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, companyId: 0));   // data defect: no company on the row
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts(http: Http(new EmployeeViewModel { ID = 7, UserId = "user-7" }));

            Assert.Null(await factory.TryForHttpAsync());
        }

        [Fact]
        public async Task An_inactive_employee_does_not_get_a_context()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, CompanyOne, active: false));
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts(http: Http(new EmployeeViewModel { ID = 7, EmpCompanyID = CompanyOne, UserId = "user-7" }));

            Assert.Null(await factory.TryForHttpAsync());
        }

        // ---- A8/20: the fallback constant is GONE from the source, not merely unused ----
        //
        // A source-level guard, in the spirit of Stage 0's worker test: an unused private constant is one
        // careless edit away from being used again, and the whole point of Stage 1 is that this default cannot
        // come back by accident in shared platform code.
        [Fact]
        public void No_company_1_fallback_remains_in_shared_platform_or_security_code()
        {
            var root = FindRepoRoot();
            var files = new[]
            {
                Path.Combine(root, "CrossBuy", "BL", "Platform", "BusinessContextAccessor.cs"),
                Path.Combine(root, "CrossBuy", "BL", "Platform", "BusinessContextFactory.cs"),
                Path.Combine(root, "CrossBuy", "BL", "AccountingAccessService.cs"),
                Path.Combine(root, "CrossBuy", "BL", "InventoryAccessService.cs"),
                Path.Combine(root, "CrossBuy", "BL", "CrmAccessService.cs"),
                Path.Combine(root, "CrossBuy", "BL", "InventoryApprovalService.cs"),
            };

            foreach (var file in files)
            {
                Assert.True(File.Exists(file), $"expected to inspect {file}");
                var code = StripComments(File.ReadAllText(file));

                Assert.DoesNotContain("FallbackCompanyId", code);
                // `const int CompanyId = 1` / `CompanyID = 1` in executable code.
                Assert.DoesNotMatch(@"const\s+int\s+\w*CompanyI[dD]\s*=\s*1\s*;", code);
                Assert.DoesNotMatch(@"CompanyI[dD]\s*=\s*1\s*[;,)]", code);
            }
        }

        // PosCompanyPolicy is the ONE remaining company-1 constant in this area, and it is deliberate: POS
        // catalog and cash accounts live under company 1 while branches sit under 65–79. The test pins that it
        // stays contained in its named policy and does not spread back into the access service's own logic.
        [Fact]
        public void The_pos_catalog_company_constant_lives_only_in_its_named_policy()
        {
            var root = FindRepoRoot();
            var code = StripComments(File.ReadAllText(Path.Combine(root, "CrossBuy", "BL", "PosAccessService.cs")));

            Assert.Contains("PosCompanyPolicy.CatalogCompanyId", code);
            // Exactly one literal assignment, inside the policy class itself.
            var literals = System.Text.RegularExpressions.Regex.Matches(code, @"CatalogCompanyId\s*=\s*1\s*;");
            Assert.Equal(1, literals.Count);
        }

        // =====================================================================================
        // A8/4 and A8/5 — worker and system contexts
        // =====================================================================================

        [Fact]
        public void A_worker_context_requires_an_explicit_company()
        {
            Assert.Throws<BusinessContextUnresolvedException>(() => BusinessContext.ForWorker(0));
            Assert.Throws<BusinessContextUnresolvedException>(() => BusinessContext.ForSystem(0));

            var worker = BusinessContext.ForWorker(CompanyTwo);
            Assert.Equal(CompanyTwo, worker.CompanyId);
            Assert.Equal(BusinessContextSource.Worker, worker.Source);
            // A worker is NOT a system context: it must be authorized like any other caller.
            Assert.False(worker.IsSystem);

            var system = BusinessContext.ForSystem(CompanyTwo);
            Assert.Equal(BusinessContextSource.System, system.Source);
            Assert.True(system.IsSystem);
        }

        [Fact]
        public void A_worker_context_has_no_session_dependency()
        {
            using var host = new PlatformTestHost();
            // No HttpContext at all — HttpContextAccessor.HttpContext is null.
            var factory = host.Contexts(http: new HttpContextAccessor());

            var context = factory.ForWorker(CompanyTwo);

            Assert.Equal(CompanyTwo, context.CompanyId);
            Assert.Equal(BusinessContextSource.Worker, context.Source);
        }

        [Fact]
        public void Context_does_not_leak_between_scopes()
        {
            using var host = new PlatformTestHost();

            var scopeA = new CompanyScopeHolder();
            var scopeB = new CompanyScopeHolder();
            var factoryA = host.Contexts(scope: scopeA);
            var factoryB = host.Contexts(scope: scopeB);

            var a = factoryA.ForWorker(CompanyOne);
            var b = factoryB.ForWorker(CompanyTwo);

            Assert.Equal(CompanyOne, a.CompanyId);
            Assert.Equal(CompanyTwo, b.CompanyId);
            Assert.Equal(CompanyOne, scopeA.CompanyId);
            Assert.Equal(CompanyTwo, scopeB.CompanyId);
            // Distinct correlation ids: one per scope, so events from two jobs are never conflated.
            Assert.NotEqual(a.CorrelationId, b.CorrelationId);
        }

        [Fact]
        public void One_scope_cannot_be_repointed_at_a_second_company()
        {
            var scope = new CompanyScopeHolder();
            scope.Set(CompanyOne, null);

            // Idempotent for the same company…
            scope.Set(CompanyOne, 5);
            Assert.Equal(CompanyOne, scope.CompanyId);

            // …but a CONFLICTING company in one scope means two tenants shared a DbContext. That is a bug and
            // it must be loud, because Batch B's query filters will read this value.
            var ex = Assert.Throws<InvalidOperationException>(() => scope.Set(CompanyTwo, null));
            Assert.Contains("must never share", ex.Message);
        }

        [Fact]
        public void A_company_scope_rejects_a_non_company()
        {
            var scope = new CompanyScopeHolder();
            Assert.Throws<ArgumentOutOfRangeException>(() => scope.Set(0, null));
            Assert.False(scope.IsResolved);
            Assert.Null(scope.CompanyId);
        }

        // =====================================================================================
        // ForEmployeeAsync — the method that makes background permission checks per-recipient
        // =====================================================================================

        [Fact]
        public async Task An_employee_context_is_resolved_without_any_http_context()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, CompanyTwo, branchId: 3));
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts(http: new HttpContextAccessor());   // no HttpContext

            var context = await factory.ForEmployeeAsync(7);

            Assert.NotNull(context);
            Assert.Equal(7, context!.EmployeeId);
            Assert.Equal(CompanyTwo, context.CompanyId);
            Assert.Equal(3, context.BranchId);
            Assert.Equal("user-7", context.UserId);
            // Integration, NOT System — this represents a real employee's rights and must be authorized.
            Assert.Equal(BusinessContextSource.Integration, context.Source);
            Assert.False(context.IsSystem);
        }

        [Fact]
        public async Task An_employee_context_is_null_for_a_missing_or_inactive_employee()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(8, CompanyOne, active: false));
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts();

            Assert.Null(await factory.ForEmployeeAsync(999));   // no such employee
            Assert.Null(await factory.ForEmployeeAsync(8));     // inactive
            Assert.Null(await factory.ForEmployeeAsync(0));     // not an id
        }

        [Fact]
        public async Task An_orphan_employee_row_uses_only_an_explicit_company_never_a_default()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, companyId: 0));   // data defect
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts();

            // No explicit company supplied ⇒ excluded, NOT resolved to 1.
            Assert.Null(await factory.ForEmployeeAsync(7));

            // The caller may supply the company it is already processing. That is an argument, not an ambient
            // default, and it is the only permitted substitution.
            var context = await factory.ForEmployeeAsync(7, fallbackCompanyIdForOrphanRow: CompanyTwo);
            Assert.NotNull(context);
            Assert.Equal(CompanyTwo, context!.CompanyId);
        }

        [Fact]
        public async Task An_employee_context_does_not_repoint_the_scope_holder()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, CompanyTwo));
            await host.Db.SaveChangesAsync();

            var scope = new CompanyScopeHolder();
            var factory = host.Contexts(scope: scope);

            // The dispatch pass is operating as company 1…
            factory.ForWorker(CompanyOne);
            Assert.Equal(CompanyOne, scope.CompanyId);

            // …and evaluating a recipient who belongs to company 2 must NOT move the scope, or Batch B's query
            // filters would start filtering the dispatcher's own reads by someone else's company midway
            // through the pass.
            await factory.ForEmployeeAsync(7);
            Assert.Equal(CompanyOne, scope.CompanyId);
        }

        // ---- The accessor caches per scope, including the negative answer ----
        [Fact]
        public async Task The_accessor_caches_the_resolution_for_the_scope()
        {
            using var host = new PlatformTestHost();
            host.Db.Employee.Add(Emp(7, CompanyTwo));
            await host.Db.SaveChangesAsync();

            var factory = host.Contexts(http: Http(new EmployeeViewModel { ID = 7, EmpCompanyID = CompanyTwo, UserId = "user-7" }));
            var accessor = new BusinessContextAccessor(factory);

            var first = await accessor.GetCurrentAsync();
            var second = await accessor.GetCurrentAsync();

            Assert.Same(first, second);
            Assert.Equal(first.CorrelationId, second.CorrelationId);
        }

        [Fact]
        public async Task The_accessor_throws_only_on_the_strict_method()
        {
            using var host = new PlatformTestHost();
            var accessor = new BusinessContextAccessor(host.Contexts(http: Http(sessionBlob: null)));

            Assert.Null(await accessor.TryGetCurrentAsync());
            await Assert.ThrowsAsync<BusinessContextUnresolvedException>(() => accessor.GetCurrentAsync());
        }

        // ---- helpers ----

        private static string StripComments(string code)
        {
            code = System.Text.RegularExpressions.Regex.Replace(code, @"/\*.*?\*/", " ",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(code, @"//.*?$", " ",
                System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CrossBuy", "BL", "Platform", "BusinessContextFactory.cs")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
        }
    }
}
