using CrossBuy.BL.Platform;
using CrossBuy.Controllers.Api;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CrossBuy.Tests.Hr
{
    // ============================================================================================
    // THE THREE HR APIs THAT CROSSED THE TENANT BOUNDARY.
    //
    // These are BEHAVIOURAL tests: each one calls the real action on a real DbContext holding two
    // companies' employees and asserts on what comes back. None of them inspects source shape, and none
    // asserts that a particular method was called — a test that only proves "a filter exists somewhere"
    // passes just as happily against a filter applied to the wrong column.
    //
    // WHY THE FIXTURE HAS TWO COMPANIES AND WHY THE NEIGHBOUR IS NOT EMPTY. A single-company fixture
    // cannot fail an isolation assertion: every row is in scope, so a completely missing predicate looks
    // identical to a correct one. Company B therefore holds employees whose names, ids and personal
    // fields are distinctive, and every assertion below names them.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // WHAT WAS WRONG, so a later reader can tell these tests from ceremony:
    //
    //   List    took `int? companyId` from the QUERY STRING and used it as the filter. Omit it and the
    //           `if (companyId.HasValue)` never ran, so the directory returned EVERY employee of EVERY
    //           company — names, emails, phone numbers, company and branch.
    //   Detail  had no company predicate of any kind. `WHERE Id = @id` alone, projecting date of birth,
    //           address, marital status and phone. An integer was the whole authorisation.
    //   Summary counted `_context.Employee`, `Companies`, `Branches`, `JobTitles` and `Hierarchicals`
    //           with no predicate at all, so one tenant's dashboard reported the whole installation.
    //
    // Employee is deliberately excluded from the platform's global company query filters — the company is
    // resolved FROM it, so filtering it is circular (CompanyQueryFilters.DeliberatelyUnfiltered). That is
    // a sound decision, and it is exactly why these three endpoints had nothing to catch them: there is no
    // ambient net under Employee reads, so each read must carry its own predicate.
    // ============================================================================================
    public class HrApiCompanyIsolationTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        // Ids are far apart and stated as constants so a failure message names a specific person rather
        // than "expected 2, got 5".
        private const int AliceOfA = 501;
        private const int BobOfA = 502;
        private const int MalloryOfB = 901;

        private static PlatformTestHost SeedTwoCompanies()
        {
            var host = new PlatformTestHost();
            var db = host.Db;

            // JobTitleID and EmpCompanyID are NON-NULLABLE, so both projections INNER JOIN JobTitle,
            // Companies and Branch. Without these rows every employee is joined away and the endpoint
            // returns nothing — which would make every isolation assertion below pass for the wrong
            // reason. The first draft of this fixture did exactly that: the reproduction went GREEN
            // against unfixed code. Seeding them is what makes these tests mean anything.
            db.JobTitles.Add(new JobTitle { ID = 1, Title = "Dev", TitleAr = "مطور", Description = "" });
            db.Companies.AddRange(
                new Companies { CompanyID = CompanyA, CompanyName = "Co A", ComoanyNameAr = "شركة أ", Address = "", PhoneNumber = "", Email = "" },
                new Companies { CompanyID = CompanyB, CompanyName = "Co B", ComoanyNameAr = "شركة ب", Address = "", PhoneNumber = "", Email = "" });
            db.SaveChanges();

            db.Employee.AddRange(
                Person(AliceOfA, CompanyA, "أليس", "Alice A", "alice@a.local", "0100000001"),
                Person(BobOfA, CompanyA, "بوب", "Bob A", "bob@a.local", "0100000002"),
                Person(MalloryOfB, CompanyB, "مالوري", "Mallory B", "mallory@b.local", "0999999999"));

            db.SaveChanges();
            return host;
        }

        private static Employee Person(int id, int companyId, string nameAr, string nameEn,
            string email, string phone) => new()
            {
                ID = id,
                EmpCompanyID = companyId,
                IsActive = true,
                FullName = nameAr,
                FullNameEn = nameEn,
                FirstName = nameEn.Split(' ')[0],
                LastName = "T",
                Email = email,
                PhoneNumber = phone,

                // The sensitive fields Detail projects. Distinctive so a leak is unmistakable in a diff.
                Address = $"{nameEn} street, company {companyId}",
                DateOfBirth = new DateTime(1990, 1, 1).AddDays(id),
                MaritalStatus = "Single",
                Gender = "F",
                ProfileImage = "",
                UserId = $"user-{id}",
                JobTitleID = 1,
            };

        // A resolved context for one company. Source = Worker because these tests have no HTTP request;
        // what matters is that CompanyId is the resolved value and not something a caller supplied.
        private static IBusinessContextAccessor As(int companyId, int employeeId) =>
            new StubContextAccessor(new BusinessContext
            {
                CompanyId = companyId,
                EmployeeId = employeeId,
                UserId = $"user-{employeeId}",
                Source = BusinessContextSource.Worker,
            });

        private static IBusinessContextAccessor Unresolved() => StubContextAccessor.Unresolved();

        // HrApiController gained an IHrAccessService when the job-title mutations were gated. Summary does
        // not consult it, but the constructor requires it, so the tests build the real service the same way
        // HrBootstrapPolicyTests does — a stub would let a future change to Summary's authority go unnoticed.
        private static CrossBuy.BL.HrAccessService HrAccess(PlatformTestHost host) =>
            new(host.Db,
                new PlatformRoleDirectory(host.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger<OrgHierarchy>.Instance),
                new BootstrapAccessPolicyReader(host.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BootstrapAccessPolicyReader>.Instance),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CrossBuy.BL.HrAccessService>.Instance);

        // The shapes the actions return are anonymous types, so the assertions read them back as JSON.
        // That is deliberate: it asserts on what a CALLER actually receives over the wire, not on an
        // internal DTO a refactor could rename without changing the exposure.
        private static string Json(IActionResult result)
        {
            var ok = Assert.IsType<OkObjectResult>(result);
            return System.Text.Json.JsonSerializer.Serialize(ok.Value);
        }

        // =========================================================================================
        // LIST
        // =========================================================================================

        [Fact] // §22.1
        public async Task Company_A_employee_list_excludes_company_B()
        {
            using var host = SeedTwoCompanies();
            var api = new EmployeesApiController(host.Db, As(CompanyA, AliceOfA));

            var json = Json(await api.List());

            Assert.Contains("Alice A", json);
            Assert.Contains("Bob A", json);
            Assert.DoesNotContain("Mallory B", json);
            Assert.DoesNotContain("0999999999", json);
        }

        [Fact] // §22.2
        public async Task Omitting_companyId_cannot_broaden_the_employee_list()
        {
            using var host = SeedTwoCompanies();
            var api = new EmployeesApiController(host.Db, As(CompanyA, AliceOfA));

            // THE ORIGINAL DEFECT, exactly: no companyId supplied. The old code's `if (companyId.HasValue)`
            // was skipped and the whole installation came back.
            var json = Json(await api.List(search: null, companyId: null));

            Assert.DoesNotContain("Mallory B", json);
        }

        [Fact] // §22.3
        public async Task A_supplied_foreign_companyId_cannot_broaden_the_employee_list()
        {
            using var host = SeedTwoCompanies();
            var api = new EmployeesApiController(host.Db, As(CompanyA, AliceOfA));

            // Asking, in the clearest possible terms, for somebody else's company.
            var json = Json(await api.List(search: null, companyId: CompanyB));

            // The neighbour never appears — that is the security property.
            Assert.DoesNotContain("Mallory B", json);

            // AND NEITHER DOES ALICE. The parameter narrows within the resolved company, so asking for a
            // company that is not yours selects nothing. The alternative — silently ignoring the filter
            // and returning your own company instead — is worse than useless: the caller believes it is
            // looking at company 77 and is actually reading company 41, which is how a support engineer
            // reads one tenant's directory while thinking they are in another's. Empty is unambiguous.
            Assert.DoesNotContain("Alice A", json);
            Assert.Contains("\"count\":0", json);
        }

        [Fact]
        public async Task A_supplied_companyId_cannot_be_used_to_search_across_the_boundary()
        {
            using var host = SeedTwoCompanies();
            var api = new EmployeesApiController(host.Db, As(CompanyA, AliceOfA));

            // Search is the other way in: a term that matches ONLY the neighbour must still return nothing.
            var json = Json(await api.List(search: "Mallory", companyId: CompanyB));

            Assert.DoesNotContain("Mallory", json);
            Assert.DoesNotContain("0999999999", json);
        }

        // =========================================================================================
        // DETAIL
        // =========================================================================================

        [Fact] // §22.4
        public async Task Company_A_cannot_detail_read_a_company_B_employee()
        {
            using var host = SeedTwoCompanies();
            var api = new EmployeesApiController(host.Db, As(CompanyA, AliceOfA));

            var result = await api.Detail(MalloryOfB);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task A_cross_company_detail_read_leaks_no_sensitive_field()
        {
            using var host = SeedTwoCompanies();
            var api = new EmployeesApiController(host.Db, As(CompanyA, AliceOfA));

            var result = await api.Detail(MalloryOfB);
            var body = System.Text.Json.JsonSerializer.Serialize(
                (result as ObjectResult)?.Value ?? new { });

            // Named individually rather than asserting "not OK", because the failure that matters is a
            // 200 carrying one of these, and a status assertion alone would not say which field escaped.
            Assert.DoesNotContain("Mallory", body);
            Assert.DoesNotContain("0999999999", body);
            Assert.DoesNotContain("street, company 77", body);
            Assert.DoesNotContain("mallory@b.local", body);
        }

        [Fact] // §22.5
        public async Task A_foreign_employee_and_a_missing_employee_refuse_identically()
        {
            using var host = SeedTwoCompanies();
            var api = new EmployeesApiController(host.Db, As(CompanyA, AliceOfA));

            var foreign = await api.Detail(MalloryOfB);
            var missing = await api.Detail(123456);

            // SAME TYPE AND SAME BODY. If a foreign id said "forbidden" while a missing one said "not
            // found", the endpoint would answer "does an employee with this id exist somewhere in the
            // installation?" — a tenant-existence oracle that needs no further exploit.
            Assert.Equal(foreign.GetType(), missing.GetType());
            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize((foreign as ObjectResult)?.Value),
                System.Text.Json.JsonSerializer.Serialize((missing as ObjectResult)?.Value));
        }

        [Fact]
        public async Task An_own_company_employee_is_still_readable()
        {
            using var host = SeedTwoCompanies();
            var api = new EmployeesApiController(host.Db, As(CompanyA, AliceOfA));

            // The counterweight: isolation that also refuses legitimate reads is not isolation, it is an
            // outage. Without this, returning NotFound unconditionally would pass every test above.
            var json = Json(await api.Detail(BobOfA));

            Assert.Contains("Bob A", json);
            Assert.Contains("0100000002", json);
        }

        // =========================================================================================
        // SUMMARY
        // =========================================================================================

        [Fact] // §22.6
        public async Task Hr_summary_counts_only_the_resolved_company()
        {
            using var host = SeedTwoCompanies();
            var api = new HrApiController(host.Db, As(CompanyA, AliceOfA), HrAccess(host));

            var json = Json(await api.Summary());

            // Two in A, one in B. A summary that says three is counting the installation.
            Assert.Contains("\"employees\":2", json);
        }

        [Fact] // §22.7 — the mutation-sensitive one
        public async Task Adding_company_B_employees_does_not_change_company_A_summary()
        {
            using var host = SeedTwoCompanies();
            var api = new HrApiController(host.Db, As(CompanyA, AliceOfA), HrAccess(host));

            var before = Json(await api.Summary());

            host.Db.Employee.AddRange(
                Person(902, CompanyB, "ب٢", "Bee Two", "b2@b.local", "0999999902"),
                Person(903, CompanyB, "ب٣", "Bee Three", "b3@b.local", "0999999903"));
            host.Db.SaveChanges();

            var after = Json(await api.Summary());

            // THE STRONGEST ASSERTION IN THIS FILE. It does not check a number against a constant — it
            // changes the neighbour's data and requires this tenant's answer to be byte-identical. A
            // predicate on the wrong column, or a filter that silently matched everything, moves this.
            Assert.Equal(before, after);
        }

        // =========================================================================================
        // FAIL-CLOSED
        // =========================================================================================

        [Fact] // §22.8
        public async Task An_unresolved_business_context_fails_closed_on_every_endpoint()
        {
            using var host = SeedTwoCompanies();
            var employees = new EmployeesApiController(host.Db, Unresolved());
            var hr = new HrApiController(host.Db, Unresolved(), HrAccess(host));

            // Empty, not "everything". An unresolved company is the state a background call or a broken
            // session lands in, and the safe answer there is nothing at all.
            Assert.DoesNotContain("Alice A", Json(await employees.List()));
            Assert.DoesNotContain("Mallory B", Json(await employees.List()));
            Assert.IsType<NotFoundObjectResult>(await employees.Detail(AliceOfA));
            Assert.Contains("\"employees\":0", Json(await hr.Summary()));
        }

        [Fact] // §22.9
        public async Task There_is_no_company_1_fallback_when_the_context_is_unresolved()
        {
            using var host = new PlatformTestHost();
            host.Db.JobTitles.Add(new JobTitle { ID = 1, Title = "Dev", TitleAr = "مطور", Description = "" });
            host.Db.Companies.Add(new Companies { CompanyID = 1, CompanyName = "Default Co", ComoanyNameAr = "الافتراضية", Address = "", PhoneNumber = "", Email = "" });
            host.Db.SaveChanges();

            // Company 1 is the historical default this codebase has fallen back to; if any of the three
            // endpoints still did, THIS employee would appear.
            host.Db.Employee.Add(Person(1001, 1, "افتراضي", "Default One", "d1@one.local", "0111111111"));
            host.Db.SaveChanges();

            var employees = new EmployeesApiController(host.Db, Unresolved());

            Assert.DoesNotContain("Default One", Json(await employees.List()));
            Assert.IsType<NotFoundObjectResult>(await employees.Detail(1001));
        }
    }
}
