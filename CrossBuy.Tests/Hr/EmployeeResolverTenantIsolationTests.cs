using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests.Hr
{
    // ============================================================================================
    // THE EMPLOYEE ARM OF THE PLATFORM ENTITY RESOLVER CROSSES THE TENANT BOUNDARY.
    //
    // These are FAILING-BY-DESIGN regression tests. They are written against the behaviour the
    // platform should have, not the behaviour it has, so they are red until TAB-1 applies the patch
    // specified in the audit (EntityRegistry.cs lines 828 and 1082). A test that passed today would
    // be proof of nothing; these exist so the fix cannot be called done without them turning green,
    // and so a later refactor cannot quietly reopen the hole.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // WHAT IS WRONG, exactly:
    //
    //   EntityRegistry.ResolveAsync, case Employee (line 1082)
    //       .Where(e => e.ID == entityId)                       <-- no CompanyID predicate
    //       // comment on 1083 calls it "the same deliberate deviation ... (TM-2 behaviour)"
    //
    //   EntityRegistry.SearchAsync, Employee arm (line 828)
    //       .Where(e => e.FullName != null && e.FullName != "") <-- no CompanyID predicate
    //
    // Every neighbouring arm filters on companyId. Project, three lines below Resolve's Employee
    // case, reads `.Where(p => p.ID == entityId && p.CompanyID == companyId)`.
    //
    // WHY IT MATTERS BEYOND THE LABEL. PlatformPermissionProvider.BelongsToCompanyAsync (line 121)
    // decides tenant membership by asking this resolver and returning `resolved.Found`. It runs
    // BEFORE the module adapter, and its own comment states it is what stops "a module role
    // reaching across companies". For Employee it returns true for a foreign id, so the platform's
    // company gate does not hold for the one entity type whose rows ARE the company definition.
    //
    // WHAT STILL CONTAINS IT, stated so these tests are not read as proof of a full breach:
    // HrAccessService.SubjectIsInScopeAsync (line 227) re-reads the subject's EmpCompanyID from the
    // Employee row and refuses a foreign subject. So the HR *permission* answer stays correct. What
    // leaks is everything that consults the registry WITHOUT going through the permission provider
    // — the record picker being the reachable one (TaskLinkResolver.SearchAsync passes straight
    // through, BL/TaskLinkResolver.cs:46).
    //
    // Employee is deliberately excluded from the global company query filters
    // (CompanyQueryFilters.DeliberatelyUnfiltered) because the company is resolved FROM it. That is
    // sound, and it is precisely why each Employee read must carry its own predicate: there is no
    // ambient net underneath. The same reasoning is already written at the head of
    // HrApiCompanyIsolationTests, which closed three endpoints with this identical defect.
    // ============================================================================================
    public class EmployeeResolverTenantIsolationTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int AliceOfA = 501;
        private const int MalloryOfB = 901;

        // Distinctive enough that a leak is unmistakable in a failure message, and sharing no
        // substring with the company-A names so a `Contains` assertion cannot pass by accident.
        private const string MalloryNameEn = "Mallory Neighbour";
        private const string MalloryNameAr = "مالوري الجارة";

        // TWO COMPANIES, AND THE NEIGHBOUR IS NOT EMPTY. A single-company fixture cannot fail an
        // isolation assertion: every row is in scope, so a missing predicate looks identical to a
        // correct one. The counts below are deliberately ASYMMETRIC (one employee in A, one in B)
        // so an assertion encodes WHICH row survived rather than how many did.
        private static PlatformTestHost SeedTwoCompanies()
        {
            var host = new PlatformTestHost();
            var db = host.Db;

            db.JobTitles.Add(new JobTitle { ID = 1, Title = "Dev", TitleAr = "مطور", Description = "" });
            db.Companies.AddRange(
                new Companies { CompanyID = CompanyA, CompanyName = "Co A", ComoanyNameAr = "شركة أ", Address = "", PhoneNumber = "", Email = "" },
                new Companies { CompanyID = CompanyB, CompanyName = "Co B", ComoanyNameAr = "شركة ب", Address = "", PhoneNumber = "", Email = "" });
            db.SaveChanges();

            db.Employee.AddRange(
                Person(AliceOfA, CompanyA, "أليس", "Alice Local"),
                Person(MalloryOfB, CompanyB, MalloryNameAr, MalloryNameEn));
            db.SaveChanges();
            return host;
        }

        private static Employee Person(int id, int companyId, string nameAr, string nameEn) => new()
        {
            ID = id,
            EmpCompanyID = companyId,
            IsActive = true,
            FullName = nameAr,
            FullNameEn = nameEn,
            FirstName = nameEn.Split(' ')[0],
            LastName = "T",
            Email = $"user{id}@local",
            PhoneNumber = $"010000{id}",
            Address = "",
            ProfileImage = "",
            UserId = $"user-{id}",
            JobTitleID = 1,

            // EVERY NON-NULLABLE COLUMN, and the reason this list is exhaustive rather than
            // minimal: the first run of these tests failed 6/6 on
            // "NOT NULL constraint failed: Employee.Gender" — a BROKEN FIXTURE, not the defect
            // under test. Six red tests that look like the finding but prove nothing are worse
            // than none, because the next reader believes them. Employee declares sixteen
            // non-nullable scalars (ID, FirstName, LastName, FullName, Address, PhoneNumber,
            // Email, JobTitleID, EmpCompanyID, ProfileImage, DateOfBirth, Gender, MaritalStatus,
            // DateOfJoining, IsActive, UserId); all sixteen are set here.
            DateOfBirth = new DateTime(1990, 1, 1).AddDays(id),
            DateOfJoining = new DateTime(2020, 1, 1).AddDays(id),
            Gender = "F",
            MaritalStatus = "Single",
        };

        // Resolved, never request-supplied: Source = Worker because these tests have no HTTP request
        // and what matters is that CompanyId is the resolved value.
        private static BusinessContext As(int companyId, int employeeId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = $"user-{employeeId}",
            Source = BusinessContextSource.Worker,
        };

        private static EntityRegistry Registry(PlatformTestHost host) => new(host.Db);

        // =========================================================================================
        // 1. RESOLVE — a foreign employee id must not resolve, and must not carry a name.
        // =========================================================================================
        [Fact]
        public async Task ResolveAsync_ForeignCompanyEmployee_IsNotFound()
        {
            using var host = SeedTwoCompanies();

            var resolved = await Registry(host).ResolveAsync(
                EntityRegistry.Employee, MalloryOfB, As(CompanyA, AliceOfA));

            Assert.False(resolved.Found,
                "An employee of company B resolved for a caller scoped to company A. " +
                "PlatformPermissionProvider.BelongsToCompanyAsync returns this very flag as its " +
                "tenant decision, so Found == true means the platform company gate did not hold.");
        }

        [Fact]
        public async Task ResolveAsync_ForeignCompanyEmployee_DoesNotLeakTheName()
        {
            using var host = SeedTwoCompanies();

            var resolved = await Registry(host).ResolveAsync(
                EntityRegistry.Employee, MalloryOfB, As(CompanyA, AliceOfA));

            // Asserted on the LABEL rather than on Found, and separately from the test above: a fix
            // that set Found = false while still projecting the name would close the authorization
            // hole and leave the PII one open.
            var label = resolved.Label;
            Assert.False(label.Contains(MalloryNameEn, System.StringComparison.Ordinal)
                      || label.Contains(MalloryNameAr, System.StringComparison.Ordinal),
                $"The resolved label for a foreign employee was '{label}', which carries a name " +
                "belonging to another tenant.");
        }

        [Fact]
        public async Task ResolveAsync_OwnCompanyEmployee_StillResolves()
        {
            using var host = SeedTwoCompanies();

            var resolved = await Registry(host).ResolveAsync(
                EntityRegistry.Employee, AliceOfA, As(CompanyA, AliceOfA));

            // THE CONTROL. Without it a patch that simply returned NotFound for every Employee
            // would turn the two tests above green while breaking every record picker in the
            // product. An isolation suite that cannot fail on over-blocking proves half a thing.
            Assert.True(resolved.Found, "An employee of the caller's OWN company must still resolve.");
        }

        // =========================================================================================
        // 2. SEARCH — the record picker must not list another tenant's staff.
        // =========================================================================================
        [Fact]
        public async Task SearchAsync_Employee_DoesNotReturnForeignCompanyStaff()
        {
            using var host = SeedTwoCompanies();

            // `term` is a single letter present in both names, which is how the picker is actually
            // used: the browser sends whatever the user has typed so far.
            var rows = await Registry(host).SearchAsync(
                EntityRegistry.Employee, "a", As(CompanyA, AliceOfA));

            Assert.DoesNotContain(rows, r => r.EntityId == MalloryOfB);
        }

        [Fact]
        public async Task SearchAsync_Employee_StillReturnsOwnCompanyStaff()
        {
            using var host = SeedTwoCompanies();

            var rows = await Registry(host).SearchAsync(
                EntityRegistry.Employee, "a", As(CompanyA, AliceOfA));

            // The same control as above, for the same reason.
            Assert.Contains(rows, r => r.EntityId == AliceOfA);
        }

        // =========================================================================================
        // 3. THE REACHABLE PATH — /Tasks/LinkSearch?entityType=Employee goes through this resolver.
        // =========================================================================================
        [Fact]
        public async Task TaskLinkResolver_EmployeeSearch_DoesNotCrossTheTenantBoundary()
        {
            using var host = SeedTwoCompanies();

            // TaskLinkResolver is the layer the picker endpoint actually calls, and it passes
            // straight through (BL/TaskLinkResolver.cs:46). Asserting here as well as on the
            // registry states that the leak is REACHABLE, not merely present in a library.
            var links = new CrossBuy.BL.TaskLinkResolver(Registry(host));
            var options = await links.SearchAsync(CompanyA, EntityRegistry.Employee, "a");

            Assert.DoesNotContain(options, o => o.Id == MalloryOfB);
        }
    }
}
