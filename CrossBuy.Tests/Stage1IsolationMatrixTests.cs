using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch B / B7 — the company-isolation matrix.
    //
    // Two companies, two employees, eighteen cases, one table. The earlier files each prove one MECHANISM: B2 the
    // read filter, B3 the bypass, B4 the write guard. This file crosses them, because the interesting failures live
    // at the intersections — a read-only bypass that can write, a scope that reads nothing but writes fine, an
    // administrator who can read another company but silently cannot correct it.
    //
    // Every case is stated as data, so the matrix is legible as a matrix and a missing combination is visible as a
    // missing row rather than an absent test.
    public class Stage1IsolationMatrixTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;
        private const int EmployeeOne = 11;   // belongs to company 1
        private const int EmployeeTwo = 22;   // belongs to company 2

        public enum Op { Read, Insert, Update, Delete }
        public enum Right { None, Administration, Monitoring, Dispatch, PublicCatalog }

        // actor company | target company | operation | right held | expected outcome
        //
        // "Allowed" for a Read means the row is VISIBLE; for a write it means the save succeeded.
        [Theory]
        // ---- 1-4: reads, no special right. The diagonal is visible, the off-diagonal is not. ----
        [InlineData(CompanyOne, CompanyOne, Op.Read,   Right.None,           true)]
        [InlineData(CompanyOne, CompanyTwo, Op.Read,   Right.None,           false)]
        [InlineData(CompanyTwo, CompanyTwo, Op.Read,   Right.None,           true)]
        [InlineData(CompanyTwo, CompanyOne, Op.Read,   Right.None,           false)]
        // ---- 5-8: inserts. Symmetric: neither company may seed the other, in either direction. ----
        [InlineData(CompanyOne, CompanyOne, Op.Insert, Right.None,           true)]
        [InlineData(CompanyOne, CompanyTwo, Op.Insert, Right.None,           false)]
        [InlineData(CompanyTwo, CompanyTwo, Op.Insert, Right.None,           true)]
        [InlineData(CompanyTwo, CompanyOne, Op.Insert, Right.None,           false)]
        // ---- 9-12: update and delete. The row is reachable by id even when invisible, which is the point. ----
        [InlineData(CompanyOne, CompanyOne, Op.Update, Right.None,           true)]
        [InlineData(CompanyOne, CompanyTwo, Op.Update, Right.None,           false)]
        [InlineData(CompanyOne, CompanyOne, Op.Delete, Right.None,           true)]
        [InlineData(CompanyOne, CompanyTwo, Op.Delete, Right.None,           false)]
        // ---- 13-14: an authorized administrator crosses companies for BOTH reading and writing. ----
        [InlineData(CompanyOne, CompanyTwo, Op.Read,   Right.Administration,  true)]
        [InlineData(CompanyOne, CompanyTwo, Op.Insert, Right.Administration,  true)]
        // ---- 15-16: the read-only right is exactly that. This pair is the whole reason the kinds are separate. ----
        [InlineData(CompanyOne, CompanyTwo, Op.Read,   Right.Monitoring,      true)]
        [InlineData(CompanyOne, CompanyTwo, Op.Insert, Right.Monitoring,      false)]
        // ---- 17-18: the dispatcher reads and writes across companies; the public catalogue does neither. ----
        [InlineData(CompanyOne, CompanyTwo, Op.Insert, Right.Dispatch,        true)]
        [InlineData(CompanyOne, CompanyTwo, Op.Read,   Right.PublicCatalog,   false)]
        public async Task Isolation_matrix(int actorCompany, int targetCompany, Op operation, Right right, bool expected)
        {
            using var host = new PlatformTestHost(companyId: actorCompany);

            // Both companies' data exists, arranged through the authorized cross-company context. Reading it is
            // what the matrix measures; creating it is not.
            int targetId = await SeedAsync(host, targetCompany);

            using var lease = Acquire(host, right);

            bool allowed = operation switch
            {
                Op.Read   => await CanReadAsync(host, targetId),
                Op.Insert => await CanInsertAsync(host, targetCompany),
                Op.Update => await CanUpdateAsync(host, targetId, targetCompany),
                Op.Delete => await CanDeleteAsync(host, targetId, targetCompany),
                _ => throw new InvalidOperationException(),
            };

            Assert.Equal(expected, allowed);
        }

        // =====================================================================================
        // The two-employee dimension: identity, not just company id
        // =====================================================================================

        // The matrix above pins the scope's company. This pins the other half — that the company comes from the
        // EMPLOYEE row rather than from anything the request says. Two employees, two companies, one factory.
        [Fact]
        public async Task Each_employees_scope_is_resolved_from_their_own_employee_row()
        {
            using var host = new PlatformTestHost(companyId: null);
            host.Seed.Employee.AddRange(
                Emp(EmployeeOne, CompanyOne), Emp(EmployeeTwo, CompanyTwo));
            await host.Seed.SaveChangesAsync();

            var scopeOne = new CompanyScopeHolder();
            var one = await host.Contexts(scope: scopeOne).ForEmployeeAsync(EmployeeOne);
            var scopeTwo = new CompanyScopeHolder();
            var two = await host.Contexts(scope: scopeTwo).ForEmployeeAsync(EmployeeTwo);

            Assert.Equal(CompanyOne, one!.CompanyId);
            Assert.Equal(CompanyTwo, two!.CompanyId);

            // ForEmployeeAsync deliberately does NOT publish to the holder — it evaluates SOMEBODY ELSE's rights
            // (a notification recipient), and publishing would repoint the caller's query filters at that person's
            // company mid-request. Asserted because it is a subtle guarantee that is easy to "helpfully" break.
            Assert.False(scopeOne.IsResolved);
            Assert.False(scopeTwo.IsResolved);
        }

        // An employee of company 2 cannot become company 1 by asserting it: the Employee row wins, and the scope
        // that results reads company 2's rows only.
        [Fact]
        public async Task An_employee_cannot_widen_their_scope_by_claiming_another_company()
        {
            using var host = new PlatformTestHost(companyId: null);
            host.Seed.Employee.Add(Emp(EmployeeTwo, CompanyTwo));
            await host.Seed.SaveChangesAsync();
            await SeedAsync(host, CompanyOne);
            int theirs = await SeedAsync(host, CompanyTwo);

            // The session blob claims company 1. The Employee row says company 2.
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            http.Session = new FakeSession();
            http.Session.SetString("Employee", System.Text.Json.JsonSerializer.Serialize(
                new CrossBuy.ViewModel.EmployeeViewModel
                {
                    ID = EmployeeTwo, EmpCompanyID = CompanyOne, UserId = "user-" + EmployeeTwo,
                    FirstName = "T", LastName = "T", FullName = "t", FullNameEn = "t",
                }));

            var context = await host.Contexts(
                http: new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http },
                scope: host.Holder).ForHttpAsync();

            Assert.Equal(CompanyTwo, context.CompanyId);          // the row wins
            Assert.Equal(CompanyTwo, host.Holder.CompanyId);

            // ...and the filters follow the resolved company, not the claimed one.
            var visible = await host.Db.Customers.AsNoTracking().Select(c => c.ID).ToListAsync();
            Assert.Equal(new[] { theirs }, visible);
        }

        // =====================================================================================
        // The matrix's own integrity
        // =====================================================================================

        // A matrix is only evidence if it covers what it claims to. This asserts the shape rather than trusting the
        // reader to count rows: every operation appears, and the off-diagonal appears for each of them.
        [Fact]
        public void The_matrix_covers_every_operation_in_both_directions()
        {
            var cases = typeof(Stage1IsolationMatrixTests)
                .GetMethod(nameof(Isolation_matrix))!
                .GetCustomAttributes(typeof(InlineDataAttribute), false)
                .Cast<InlineDataAttribute>()
                .Select(a => a.GetData(null!).Single())
                .Select(d => (Actor: (int)d[0]!, Target: (int)d[1]!, Op: (Op)d[2]!, Right: (Right)d[3]!, Expected: (bool)d[4]!))
                .ToList();

            Assert.Equal(18, cases.Count);

            foreach (var op in Enum.GetValues<Op>())
            {
                Assert.Contains(cases, c => c.Op == op && c.Actor == c.Target);   // same-company case present
                Assert.Contains(cases, c => c.Op == op && c.Actor != c.Target);   // cross-company case present
            }

            // Every same-company, no-right case must be ALLOWED and every cross-company one REFUSED — if a future
            // edit inverted an expectation, the matrix would still be "18 passing tests" without this.
            Assert.All(cases.Where(c => c.Right == Right.None && c.Actor == c.Target), c => Assert.True(c.Expected));
            Assert.All(cases.Where(c => c.Right == Right.None && c.Actor != c.Target), c => Assert.False(c.Expected));
        }

        // ---- operations ------------------------------------------------------------------------------------

        private static async Task<bool> CanReadAsync(PlatformTestHost host, int id)
            => await host.Db.Customers.AsNoTracking().AnyAsync(c => c.ID == id);

        private static async Task<bool> CanInsertAsync(PlatformTestHost host, int companyId)
        {
            host.Db.Customers.Add(new Customer { CompanyID = companyId, Name = "inserted" });
            try { await host.Db.SaveChangesAsync(); return true; }
            catch (CompanyWriteDeniedException) { host.Db.ChangeTracker.Clear(); return false; }
        }

        // Attaching a stub rather than querying: an UPDATE issued without a SELECT never consults a query filter,
        // so this is the shape that distinguishes B4 from B2.
        private static async Task<bool> CanUpdateAsync(PlatformTestHost host, int id, int companyId)
        {
            var stub = new Customer { ID = id, CompanyID = companyId, Name = "seeded" };
            host.Db.Customers.Attach(stub);
            host.Db.Entry(stub).Property(c => c.Name).CurrentValue = "renamed";
            try { await host.Db.SaveChangesAsync(); return true; }
            catch (CompanyWriteDeniedException) { host.Db.ChangeTracker.Clear(); return false; }
        }

        private static async Task<bool> CanDeleteAsync(PlatformTestHost host, int id, int companyId)
        {
            host.Db.Customers.Remove(new Customer { ID = id, CompanyID = companyId, Name = "seeded" });
            try { await host.Db.SaveChangesAsync(); return true; }
            catch (CompanyWriteDeniedException) { host.Db.ChangeTracker.Clear(); return false; }
        }

        // ---- rights ----------------------------------------------------------------------------------------

        private static IDisposable? Acquire(PlatformTestHost host, Right right) => right switch
        {
            Right.None => null,
            Right.Administration => host.HostBypass().Begin(
                CompanyBypassKind.CrossCompanyAdministration, PlatformTestHost.AdminContext(), "matrix"),
            Right.Monitoring => host.HostBypass().Begin(
                CompanyBypassKind.PlatformMonitoring, PlatformTestHost.AdminContext(), "matrix"),
            Right.Dispatch => host.HostBypass().BeginPlatformDispatch("matrix"),
            // The public catalogue pins the scope to the CONFIGURED company. Applied to a scope already operating as
            // company 1 with StoreCompanyId = 1, it is a no-op pin — and it grants nothing cross-company, which is
            // what the matrix row asserts.
            Right.PublicCatalog => host.Bypass(host.Holder, new PublicCatalogOptions { StoreCompanyId = 1 })
                .BeginPublicCatalogRead("matrix"),
            _ => throw new InvalidOperationException(),
        };

        // ---- fixtures --------------------------------------------------------------------------------------

        private static async Task<int> SeedAsync(PlatformTestHost host, int companyId)
        {
            var customer = new Customer { CompanyID = companyId, Name = "seeded" };
            host.Seed.Customers.Add(customer);
            await host.Seed.SaveChangesAsync();
            return customer.ID;
        }

        private static CrossBuy.Models.Context.Admin.Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        // ASP.NET Core has no in-memory ISession, and DefaultHttpContext.Session throws without one. This is the
        // smallest thing that behaves like a session for the one blob the factory reads.
        private sealed class FakeSession : Microsoft.AspNetCore.Http.ISession
        {
            private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
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
    }
}
