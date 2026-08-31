using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // DOCUMENT SELF-SERVICE SUBMISSION — the authorization contract, ahead of the endpoint.
    //
    // This batch establishes who MAY submit, not how they do it: there is no upload endpoint yet, on
    // purpose. Landing the contract first means the endpoint, when it arrives, has nothing left to
    // decide — and it means the properties below are pinned before any code depends on them.
    //
    // SUBMIT IS NOT UPLOAD. Upload is an HR act: someone with authority over the record puts a
    // document on it and what they put there IS the record. Submit is a person handing in their own
    // evidence, which is received rather than established. Collapsing the two would let an employee
    // attach their own passport scan and have it count as verified because they attached it.
    //
    // The self-only property is not written here or in the resolver. It falls out of the mapping:
    // Submit resolves to HrActions.EmployeeRequest, which HrAccessService answers above both the
    // bootstrap branch and the role rules, purely on whether the target IS the caller.
    // ============================================================================================
    public class DocumentSubmitContractTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int Alice = 5;        // an ordinary employee
        private const int Bob = 6;          // a colleague in the same company
        private const int Officer = 7;      // holds the HR role
        private const int Foreign = 900;    // another company entirely

        [Fact]
        public void Submit_is_its_own_verb_and_maps_to_the_self_service_capability()
        {
            using var host = Seeded();
            var resolver = new EmployeeDocumentOwnerResolver(host.Db);

            Assert.Equal(HrActions.EmployeeRequest, resolver.ModuleActionFor(DocumentAction.Submit));

            // ...and it is emphatically NOT the manage-tier verbs, which is the distinction the whole
            // contract rests on.
            Assert.Equal(HrActions.EmployeeManage, resolver.ModuleActionFor(DocumentAction.Upload));
            Assert.Equal(HrActions.EmployeeManage, resolver.ModuleActionFor(DocumentAction.Replace));
            Assert.Equal(HrActions.EmployeeManage, resolver.ModuleActionFor(DocumentAction.Delete));
            Assert.Equal(HrActions.ConfidentialView, resolver.ModuleActionFor(DocumentAction.Manage));

            Assert.NotEqual(resolver.ModuleActionFor(DocumentAction.Upload),
                            resolver.ModuleActionFor(DocumentAction.Submit));
        }

        [Fact]
        public async Task An_employee_may_submit_for_themselves()
        {
            using var host = Seeded();
            var resolver = Build(host);

            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyA, Alice),
                new DocumentOwnerRef(EntityRegistry.Employee, Alice),
                DocumentAction.Submit,
                documentCompanyId: CompanyA);

            Assert.True(decision.Allowed, decision.ReasonCode);
        }

        [Fact]
        public async Task An_employee_may_not_submit_for_a_colleague()
        {
            using var host = Seeded();
            var resolver = Build(host);

            // THE READING THIS MUST NEVER CARRY. Submitting is not a company-wide right, so holding it
            // grants nothing over Bob's record.
            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyA, Alice),
                new DocumentOwnerRef(EntityRegistry.Employee, Bob),
                DocumentAction.Submit,
                documentCompanyId: CompanyA);

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.ModuleDenied, decision.ReasonCode);
        }

        [Fact]
        public async Task Not_even_an_HR_officer_may_submit_as_somebody_else()
        {
            using var host = Seeded();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var resolver = Build(host);
            var officer = Employee(CompanyA, Officer);

            // The officer genuinely administers Alice - Upload is theirs...
            Assert.True((await resolver.AuthorizeAsync(
                officer, new DocumentOwnerRef(EntityRegistry.Employee, Alice),
                DocumentAction.Upload, CompanyA)).Allowed);

            // ...and submitting AS Alice is still not, because that would be impersonation wearing an
            // administrative hat. An HR role does not widen employee-request.
            Assert.False((await resolver.AuthorizeAsync(
                officer, new DocumentOwnerRef(EntityRegistry.Employee, Alice),
                DocumentAction.Submit, CompanyA)).Allowed);

            // Their own submission still works — the capability is not disabled for role holders.
            Assert.True((await resolver.AuthorizeAsync(
                officer, new DocumentOwnerRef(EntityRegistry.Employee, Officer),
                DocumentAction.Submit, CompanyA)).Allowed);
        }

        [Fact]
        public async Task Submitting_implies_no_authority_to_manage_verify_or_read_confidential()
        {
            using var host = Seeded();

            // THE COMPANY MUST BE CONFIGURED for this question to mean anything. On a bootstrap-open
            // company employee-manage is granted to everyone about themselves, so asserting that
            // Upload is refused would be testing bootstrap rather than the implication. Someone ELSE
            // holds the HR role, which configures the company while leaving Alice with none.
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);

            var resolver = Build(host);
            var alice = Employee(CompanyA, Alice);
            var own = new DocumentOwnerRef(EntityRegistry.Employee, Alice);

            Assert.True((await resolver.AuthorizeAsync(alice, own, DocumentAction.Submit, CompanyA)).Allowed);

            // Holding Submit over your own record grants none of the tiers that would let you make the
            // document count: activating it, replacing it, deleting it, or reading a confidential one.
            foreach (var escalation in new[]
                     {
                         DocumentAction.Upload, DocumentAction.Replace,
                         DocumentAction.Delete, DocumentAction.Manage,
                     })
                Assert.False((await resolver.AuthorizeAsync(alice, own, escalation, CompanyA)).Allowed,
                    escalation.ToString());

            // And a Confidential document about themselves stays out of reach.
            Assert.False((await resolver.AuthorizeAsync(
                alice, own, DocumentAction.Download, CompanyA,
                DocumentConfidentiality.Confidential)).Allowed);
        }

        [Fact]
        public async Task Submission_across_a_company_boundary_is_refused_with_the_exact_identifier()
        {
            using var host = Seeded();
            var resolver = Build(host);

            // Company B's employee, naming company A's document precisely.
            Assert.False((await resolver.AuthorizeAsync(
                Employee(CompanyB, Foreign),
                new DocumentOwnerRef(EntityRegistry.Employee, Alice),
                DocumentAction.Submit,
                documentCompanyId: CompanyA)).Allowed);

            // And a document claiming company A whose owner is company B's employee - the relation spoof.
            var spoof = await resolver.AuthorizeAsync(
                Employee(CompanyA, Alice),
                new DocumentOwnerRef(EntityRegistry.Employee, Foreign),
                DocumentAction.Submit,
                documentCompanyId: CompanyA);
            Assert.False(spoof.Allowed);
            Assert.Equal(DocumentAccessReasons.RelationMismatch, spoof.ReasonCode);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task Submission_fails_closed_without_a_resolved_company(int companyId)
        {
            using var host = Seeded();
            var resolver = Build(host);

            Assert.False((await resolver.AuthorizeAsync(
                new BusinessContext { CompanyId = companyId, EmployeeId = Alice, Source = BusinessContextSource.Http },
                new DocumentOwnerRef(EntityRegistry.Employee, Alice),
                DocumentAction.Submit,
                documentCompanyId: CompanyA)).Allowed);
        }

        // ---- fixture ---------------------------------------------------------------------------

        private static IDocumentAccessResolver Build(PlatformTestHost host)
        {
            var db = host.Db;
            var hr = new HrAccessService(
                db,
                new PlatformRoleDirectory(db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(db, NullLogger<OrgHierarchy>.Instance),
                new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<HrAccessService>.Instance);

            return new DocumentAccessResolver(
                new IDocumentOwnerResolver[] { new EmployeeDocumentOwnerResolver(db) },
                new IModuleAccessService[] { hr },
                new EntityRegistry(db),
                NullLogger<DocumentAccessResolver>.Instance);
        }

        private static BusinessContext Employee(int companyId, int employeeId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = "u" + employeeId,
            Source = BusinessContextSource.Http,
        };

        private static PlatformTestHost Seeded()
        {
            var host = new PlatformTestHost();
            foreach (var (id, company) in new[]
                     { (Alice, CompanyA), (Bob, CompanyA), (Officer, CompanyA), (Foreign, CompanyB) })
            {
                host.Db.Employee.Add(new Employee
                {
                    ID = id, EmpCompanyID = company, IsActive = true,
                    FirstName = "E" + id, LastName = "T", FullName = "E" + id,
                    Address = "-", PhoneNumber = "-", Email = "-", ProfileImage = "-",
                    Gender = "-", MaritalStatus = "-", UserId = "u" + id,
                });
                host.Db.SaveChanges();
            }
            return host;
        }

        private static void GiveHrRole(PlatformTestHost host, int companyId, int employeeId, string role)
        {
            host.Db.Set<CrossBuy.Models.Context.Platform.PlatformRoleAssignment>().Add(new()
            {
                CompanyID = companyId,
                Scope = EntityRegistry.ScopeHr,
                PrincipalType = CrossBuy.Models.Context.Platform.PlatformPrincipalTypes.Employee,
                PrincipalId = employeeId,
                Role = role,
                IsActive = true,
            });
            host.Db.SaveChanges();
        }
    }
}
