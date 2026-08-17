using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrossBuy.Tests
{
    // Shared fixture for the Platform Kernel tests: one SQLite in-memory database per test, with the real
    // CrossDbContext model (so the BusinessEvents / BusinessEventDispatch mapping under test is the
    // production mapping) and real kernel services wired by hand.
    //
    // SQLite is used because it honours transactions; the EF InMemory provider ignores them, which would
    // let the rollback tests pass without proving anything. Where SQL Server semantics cannot be
    // reproduced in-process (UPDLOCK/READPAST skip-locked), the test says so explicitly.
    public sealed class PlatformTestHost : IDisposable
    {
        private readonly SqliteConnection _connection;

        // Stage 1 Batch B / B2 — the holder `Db` is filtered by. Exposed because a filtered context and the
        // scope it reads are now one unit: a test that takes a bypass must take it on THIS holder, or the bypass
        // and the query filter would be talking about different scopes.
        public CompanyScopeHolder Holder { get; } = new();

        // companyId: the company `Db` operates as. Defaults to 1 because that is what these tests have always
        // simulated — a signed-in company-1 request — and because every pilot row in the live database is
        // company 1. Pass null to simulate a request that resolved NOBODY: pilot entities then read nothing.
        public PlatformTestHost(int? companyId = 1)
        {
            // A shared in-memory connection: the database lives as long as the connection does, so several
            // DbContexts can see the same data (needed by the concurrent-claim test).
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            if (companyId is > 0) Holder.Set(companyId.Value, null);

            Db = NewContext();
            Db.Database.EnsureCreated();

            // Test-host concession, stated openly: referential integrity is switched OFF for this in-memory
            // database. The kernel tables under test sit at the end of long FK chains in the real schema
            // (Employee -> AspNetUsers -> ..., Employee -> Companies -> CompanyTypes), and seeding those
            // chains would add dozens of rows that have nothing to do with what any of these tests assert.
            // The FK that matters to this slice — BusinessEventDispatch.EventId -> BusinessEvents.EventId —
            // is created and enforced by deploy/sql/platform_business_events.sql in the real database.
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = OFF;";
                command.ExecuteNonQuery();
            }
        }

        public CrossBuy.Models.Context.CrossDbContext Db { get; }

        // A second context over the SAME database. `scope` defaults to this host's holder, so a context obtained
        // here is filtered exactly as `Db` is; pass a different holder to simulate a different request.
        public CrossBuy.Models.Context.CrossDbContext NewContext(ICompanyScopeHolder? scope = null)
        {
            var options = new DbContextOptionsBuilder<CrossBuy.Models.Context.CrossDbContext>()
                .UseSqlite(_connection)
                .EnableSensitiveDataLogging()
                // B4: the write guard is part of the production DbContext configuration, so the tests run with it.
                // Without it the suite would be testing a context the application never creates.
                .AddInterceptors(new CompanyWriteGuardInterceptor(
                    NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            return new CrossBuy.Models.Context.CrossDbContext(options, scope ?? Holder);
        }

        // B2 — a SECOND request scope over the same database: its own holder, its own filtered context.
        //
        // Before B2, a test could simulate "the same operation as company 2" by passing a BusinessContext naming
        // company 2 while reusing `Db`. With filters on, that combination is no longer coherent — the ambient
        // scope and the passed context would disagree, and the filter (correctly) obeys the scope. Production
        // never has that combination: one DI scope holds one company, and the context handed to a service is the
        // one that resolved it. This helper is that arrangement.
        public sealed record RequestScope(CompanyScopeHolder Holder, CrossBuy.Models.Context.CrossDbContext Db);

        public RequestScope Request(int companyId)
        {
            var holder = new CompanyScopeHolder();
            holder.Set(companyId, null);
            return new RequestScope(holder, NewContext(holder));
        }

        // B2 — a context that can SEE every company, for test ARRANGEMENT and VERIFICATION only.
        //
        // It exists because `Db` is now filtered: a test that seeds another company's row and then reads it back
        // to prove it is there needs an unfiltered read for that one assertion. It goes through the REAL bypass
        // with a REAL admin context, so there is no test-only door into unfiltered reads; the lease is
        // deliberately left open for the lifetime of the returned context, whose holder nothing else shares.
        public CrossBuy.Models.Context.CrossDbContext AllCompanies()
        {
            var holder = new CompanyScopeHolder();
            Bypass(holder).Begin(
                CompanyBypassKind.CrossCompanyAdministration, AdminContext(), "test arrangement and verification");
            return NewContext(holder);
        }

        // B4 — the ARRANGEMENT context, cached for the host's lifetime.
        //
        // With the write guard active, `Db` (one company) can no longer seed another company's row — correctly, and
        // that refusal is itself tested in Stage1WriteGuardTests. But a test that wants to prove isolation must
        // first CREATE the other company's data, which in reality was written by that company's own request.
        //
        // `Seed` is that arrangement: one long-lived authorized cross-company context, used for setting the stage
        // and for verifying across companies. Tests still ASSERT through `Db`, so nothing here weakens what they
        // prove — the data arrives the way it would in production, and the reading is still filtered.
        private CrossBuy.Models.Context.CrossDbContext? _seed;
        public CrossBuy.Models.Context.CrossDbContext Seed => _seed ??= AllCompanies();

        public IEntityRegistry Registry(CrossBuy.Models.Context.CrossDbContext? db = null)
            => new EntityRegistry(db ?? Db);

        public IBusinessEventService Events(
            CrossBuy.Models.Context.CrossDbContext? db = null,
            BusinessContext? context = null)
        {
            var ctx = db ?? Db;
            return new BusinessEventService(
                ctx,
                Registry(ctx),
                new StubContextAccessor(context ?? DefaultContext()),
                NullLogger<BusinessEventService>.Instance);
        }

        // Stage 1 Batch B / B3 — the real bypass over a real scope holder. One holder per call, mirroring the
        // Scoped registration: a test that wants two "requests" asks for two.
        public ICompanyScopeHolder Scope() => new CompanyScopeHolder();

        // B2: a bypass over THIS host's holder, so taking it actually unfilters `Db`. Use this — not Bypass() —
        // whenever the assertion reads across companies through `host.Db`.
        public ICompanyIsolationBypass HostBypass() => Bypass(Holder);

        public ICompanyIsolationBypass Bypass(
            ICompanyScopeHolder? scope = null,
            CrossBuy.Models.Platform.PublicCatalogOptions? catalog = null,
            ICompanyBypassAudit? audit = null,
            ICompanyBypassPolicy? policy = null)
            => new CompanyIsolationBypass(
                scope ?? new CompanyScopeHolder(),
                policy ?? new CompanyBypassPolicy(),
                audit ?? new LoggingCompanyBypassAudit(NullLogger<LoggingCompanyBypassAudit>.Instance),
                Options.Create(catalog ?? new CrossBuy.Models.Platform.PublicCatalogOptions()));

        // Stage 1 Batch A — the real context factory over the in-memory database. No HttpContext, which is the
        // point: every method except ForHttpAsync must work without one.
        public IBusinessContextFactory Contexts(
            CrossBuy.Models.Context.CrossDbContext? db = null,
            Microsoft.AspNetCore.Http.IHttpContextAccessor? http = null,
            ICompanyScopeHolder? scope = null)
            => new BusinessContextFactory(
                http ?? new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                db ?? Db,
                scope ?? Holder,
                NullLogger<BusinessContextFactory>.Instance);

        public IEventDispatchStore DispatchStore(
            CrossBuy.Models.Context.CrossDbContext? db = null,
            BusinessEventDispatchOptions? options = null)
            => new SqlEventDispatchStore(db ?? Db, Options.Create(options ?? new BusinessEventDispatchOptions()));

        // Stage 0 Batch B — the Business Event Monitor's read model. Given the same options instance as the store so
        // the screen's retry-eligibility rules and the store's cannot silently disagree in a test.
        public IBusinessEventMonitorService Monitor(
            CrossBuy.Models.Context.CrossDbContext? db = null,
            BusinessEventDispatchOptions? options = null)
        {
            var ctx = db ?? Db;
            var opts = Options.Create(options ?? new BusinessEventDispatchOptions());
            return new BusinessEventMonitorService(
                ctx, Registry(ctx), new SqlEventDispatchStore(ctx, opts), opts,
                HostBypass(),
                NullLogger<BusinessEventMonitorService>.Instance);
        }

        // Stage 1 Batch B / B3 — a context that genuinely holds the platform-admin right, the same way the
        // Business Event Monitor controller reaches crossCompany: true (PlatformOpsAttribute.AdminRoles).
        //
        // CompanyBypassPolicy carries NO test-only allowance, so a test that wants the elevated view must
        // present the real right. Any test still passing DefaultContext with crossCompany: true is refused —
        // which is the intended behaviour, not a fixture inconvenience.
        public static BusinessContext AdminContext(int companyId = 1, int? employeeId = 7)
            => new()
            {
                CompanyId = companyId,
                EmployeeId = employeeId,
                UserId = "user-" + (employeeId ?? 0),
                Roles = new[] { "PlatformOps" },
                CorrelationId = Guid.NewGuid(),
            };

        public static BusinessContext DefaultContext(int companyId = 1, int? employeeId = 7, int? branchId = null)
            => new()
            {
                CompanyId = companyId,
                BranchId = branchId,
                EmployeeId = employeeId,
                UserId = "user-" + (employeeId ?? 0),
                Roles = Array.Empty<string>(),
                CorrelationId = Guid.NewGuid(),
            };

        public void Dispose()
        {
            _seed?.Dispose();
            Db.Dispose();
            _connection.Dispose();
        }
    }

    // The accessor's own job (reading session/claims) is not what these tests exercise, so it is stubbed to
    // return a fixed context. Everything downstream receives the real BusinessContext type.
    public sealed class StubContextAccessor : IBusinessContextAccessor
    {
        private readonly BusinessContext? _context;

        public StubContextAccessor(BusinessContext context) { _context = context; }

        // Stage 1: a stub that resolves NOTHING, so a test can drive the "unresolved request" path — which is
        // now a deny rather than a silent company-1 context.
        public static StubContextAccessor Unresolved() => new(null);
        private StubContextAccessor(BusinessContext? context, bool _ = false) { _context = context; }

        public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
            => _context != null
                ? Task.FromResult(_context)
                : throw new BusinessContextUnresolvedException("Stub accessor has no context.");

        public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_context);
    }

    // Lets a test drive visibility filtering deterministically instead of standing up four module RBAC
    // services and their role tables.
    public sealed class StubPermissionProvider : IPlatformPermissionProvider
    {
        private readonly HashSet<string> _allowed;
        public StubPermissionProvider(params string[] allowedActions) => _allowed = new HashSet<string>(allowedActions, StringComparer.Ordinal);

        public Task<PermissionDecision> CanAsync(
            BusinessContext context, string entityType, int entityId, string action, CancellationToken cancellationToken = default)
            => Task.FromResult(_allowed.Contains(action)
                ? PermissionDecision.Allow("stub")
                : PermissionDecision.Deny("stub denies " + action));
    }
}