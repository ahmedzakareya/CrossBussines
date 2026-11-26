using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Crm;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Hr
{
    // ============================================================================================
    // WAVE 2 — THE AUTHORITY BEHIND A CONVERSATION GATE, for Accounting and CRM.
    //
    // ─── WHY THIS FILE LIVES UNDER Hr/ ──────────────────────────────────────────────────────────
    // It is not misfiled. TAB-2's only owned test directory is CrossBuy.Tests/Hr/**; the named
    // Reporting*.cs files are the rest of its claim. CrossBuy.Tests/Communication/ is UNCLAIMED and
    // CrossBuy.Tests/ root is unclaimed, so neither is "explicitly owned or approved". Until TAB-2
    // is granted a cross-module security test area, this is the only legitimate home. That is a
    // governance gap, recorded in the Wave 2 deliverable rather than worked around silently.
    //
    // ─── WHAT THESE TESTS DO AND DELIBERATELY DO NOT DO ─────────────────────────────────────────
    // They exercise the MODULE AUTHORITY that the conversation gates consult — the real
    // AccountingAccessService and CrmAccessService against a real DbContext — not the controllers.
    // AccountingController and CrmController are foreign-owned and are not constructed here.
    //
    // That boundary is honest about what is proven and what is cited:
    //   PROVEN AT RUNTIME here  : what the authority answers for a given context, action and target.
    //   CITED FROM SOURCE       : that the gate passes "read" for writes, and passes
    //                             PermissionTarget.ForEntity(code, id).
    //                             AccountingController.cs:3341 ConversationGateAsync has NO action
    //                             parameter; InvoiceConversationAdd (3576) and
    //                             InvoiceConversationReact (3687) call that same read-gate.
    //                             CrmTicket takes crm.CanAsync("read") — the UNTARGETED,
    //                             session-based overload — at AccountingController.cs:3370.
    //
    // A test that constructed the controller would prove the whole chain, and would also mean
    // standing up ~20 foreign dependencies whose churn would make this file red for reasons that
    // have nothing to do with security. The authority is where the decision is actually made.
    //
    // ─── READING A FAILURE ──────────────────────────────────────────────────────────────────────
    // Tests named *_IsOpenToEveryEmployee_* are CHARACTERISATION: they assert what the system does
    // today and are expected to PASS. They exist so that a later tightening cannot land unnoticed.
    // Tests named *_MustBe* assert the behaviour the product should have and are expected to FAIL
    // until the owner-specific fix lands. Positive controls are named *_Control_*.
    // ============================================================================================
    public class CrossModuleConversationAuthorityTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int RolelessOfA = 601;      // an employee with NO module role at all
        private const int ChiefOfA = 602;         // holds ChiefAccountant, so the company is "configured"
        private const int SalesRepOfA = 603;      // holds SalesRep, so CRM is "configured"

        // An invoice id that is NOT seeded. If an authority honoured its target it could not grant
        // View on a record that does not exist, so this number is the whole point of test A3.
        private const int UnseededInvoice = 999_999;

        private static PlatformTestHost Seeded()
        {
            var host = new PlatformTestHost();
            var db = host.Db;

            db.JobTitles.Add(new JobTitle { ID = 1, Title = "Dev", TitleAr = "مطور", Description = "" });
            db.Companies.AddRange(
                new Companies { CompanyID = CompanyA, CompanyName = "Co A", ComoanyNameAr = "شركة أ", Address = "", PhoneNumber = "", Email = "" },
                new Companies { CompanyID = CompanyB, CompanyName = "Co B", ComoanyNameAr = "شركة ب", Address = "", PhoneNumber = "", Email = "" });
            db.SaveChanges();

            db.Employee.AddRange(
                Person(RolelessOfA, CompanyA, "بلا دور", "Roleless A"),
                Person(ChiefOfA, CompanyA, "رئيس الحسابات", "Chief A"),
                Person(SalesRepOfA, CompanyA, "مندوب", "Rep A"));
            db.SaveChanges();

            // THE MODULE MUST BE "CONFIGURED" OR NONE OF THIS IS REACHED. Both services gate their
            // read-open switch behind AnyRoleConfiguredAsync — AccountingAccessService.cs:159 reads
            // AccountingUserRoles, CrmAccessService.cs:171 reads CrmUserRoles. With an empty table the
            // call falls through to the bootstrap policy reader instead and these tests would be
            // measuring a different code path. One role row each, held by somebody OTHER than the
            // roleless subject, is what puts the company in the configured state.
            db.AccountingUserRoles.Add(new AccountingUserRole
            { ID = 1, CompanyID = CompanyA, EmployeeId = ChiefOfA, Role = "ChiefAccountant" });
            db.CrmUserRoles.Add(new CrmUserRole
            { ID = 1, CompanyID = CompanyA, EmployeeId = SalesRepOfA, Role = "SalesRep" });
            db.SaveChanges();

            return host;
        }

        // All sixteen non-nullable Employee scalars. The Wave 1 run of the sibling file failed 6/6 on
        // "NOT NULL constraint failed: Employee.Gender" — a broken fixture masquerading as a finding.
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
            DateOfBirth = new DateTime(1990, 1, 1).AddDays(id),
            DateOfJoining = new DateTime(2020, 1, 1).AddDays(id),
            Gender = "F",
            MaritalStatus = "Single",
        };

        private static BusinessContext Ctx(int companyId, int employeeId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = $"user-{employeeId}",
            Source = BusinessContextSource.Worker,
        };

        private static AccountingAccessService Accounting(PlatformTestHost host) =>
            new(host.Db,
                new HttpContextAccessor(),
                new StubContextAccessor(Ctx(CompanyA, RolelessOfA)),
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<AccountingAccessService>.Instance);

        private static CrmAccessService Crm(PlatformTestHost host) =>
            new(host.Db,
                new HttpContextAccessor(),
                new StubContextAccessor(Ctx(CompanyA, RolelessOfA)),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<CrmAccessService>.Instance);

        // =========================================================================================
        // CASE A — ACCOUNTING
        // =========================================================================================

        // A1 — CHARACTERISATION. Expected to PASS. This is the premise everything else rests on:
        // "record read permission" is not a permission anybody has to be given.
        [Fact]
        public async Task A1_AccountingRead_IsOpenToEveryEmployee_OfAConfiguredCompany()
        {
            using var host = Seeded();

            bool allowed = await Accounting(host).CanAsync(
                Ctx(CompanyA, RolelessOfA), "read",
                PermissionTarget.ForEntity(EntityRegistry.SalesInvoice, 1));

            Assert.True(allowed,
                "Expected the documented behaviour: inside a configured company, Accounting 'read' " +
                "grants every authenticated employee. If this now fails, the tier has been tightened " +
                "and GAP-02's severity must be re-assessed rather than assumed.");
        }

        // A2 — POSITIVE CONTROL. Expected to PASS. The service DOES discriminate tiers, which is what
        // makes GAP-02 a defect of the GATE (it asks for "read" when a write is happening) rather
        // than a missing capability in the module.
        [Fact]
        public async Task A2_Control_AccountingWriteTier_IsRefusedToARolelessEmployee()
        {
            using var host = Seeded();

            bool allowed = await Accounting(host).CanAsync(
                Ctx(CompanyA, RolelessOfA), "post",
                PermissionTarget.ForEntity(EntityRegistry.SalesInvoice, 1));

            Assert.False(allowed,
                "A roleless employee was granted a write-tier accounting action. If this fails the " +
                "module itself is open and the finding is far larger than the conversation gate.");
        }

        // A3 — SECURITY. Expected to FAIL until the owner fix lands.
        // The sharpest statement of the gap: authority is granted for a record that DOES NOT EXIST,
        // which can only mean the target was never consulted.
        [Fact]
        public async Task A3_AccountingRead_MustNotGrantAccessToARecordThatDoesNotExist()
        {
            using var host = Seeded();

            bool allowed = await Accounting(host).CanAsync(
                Ctx(CompanyA, RolelessOfA), "read",
                PermissionTarget.ForEntity(EntityRegistry.SalesInvoice, UnseededInvoice));

            Assert.False(allowed,
                $"Accounting granted 'read' on SalesInvoice#{UnseededInvoice}, which was never seeded. " +
                "PermissionTarget.ForEntity was passed and ignored (grep \"target.\" in " +
                "AccountingAccessService.cs returns 0 hits), so the conversation gate's step 2 " +
                "contributes no record-level authorization at all — step 3's existence check is the " +
                "only thing standing between a caller and any document in their company.");
        }

        // A4 — SECURITY. Expected to FAIL. Record TYPE is ignored along with record id.
        [Fact]
        public async Task A4_AccountingRead_MustDistinguishBetweenRecordTypes()
        {
            using var host = Seeded();
            var acc = Accounting(host);
            var ctx = Ctx(CompanyA, RolelessOfA);

            bool invoice = await acc.CanAsync(ctx, "read", PermissionTarget.ForEntity(EntityRegistry.SalesInvoice, 1));
            bool journal = await acc.CanAsync(ctx, "read", PermissionTarget.ForEntity(EntityRegistry.JournalEntry, 1));

            // A journal entry is a stricter record than a sales invoice in every accounting model, and
            // both are reachable through the same conversation endpoints (ConversationFamilies carries
            // all eight codes). Identical answers mean the entity code is decoration.
            Assert.False(invoice && journal,
                "SalesInvoice and JournalEntry returned the same answer for the same roleless caller, " +
                "so the entity code in the target has no effect on the decision.");
        }

        // =========================================================================================
        // CASE B — CRM TICKETS
        // =========================================================================================

        // B1 — CHARACTERISATION. Expected to PASS.
        [Fact]
        public async Task B1_CrmRead_IsOpenToEveryEmployee_OfAConfiguredCompany()
        {
            using var host = Seeded();

            bool allowed = await Crm(host).CanAsync(
                Ctx(CompanyA, RolelessOfA), "read",
                PermissionTarget.ForEntity(EntityRegistry.CrmTicket, 1));

            Assert.True(allowed,
                "Expected the documented behaviour (CrmAccessService.cs:116, inside the " +
                "AnyRoleConfiguredAsync gate): CRM 'read' grants every employee of a configured company.");
        }

        // B2 — POSITIVE CONTROL. Expected to PASS.
        [Fact]
        public async Task B2_Control_CrmManage_IsRefusedToARolelessEmployee()
        {
            using var host = Seeded();

            bool allowed = await Crm(host).CanAsync(
                Ctx(CompanyA, RolelessOfA), "manage",
                PermissionTarget.ForEntity(EntityRegistry.CrmTicket, 1));

            Assert.False(allowed, "A roleless employee was granted CRM 'manage'.");
        }

        // B3 — SECURITY. Expected to FAIL until the owner fix lands.
        // The support desk routes a ticket to a DEPARTMENT (the owner's rule: units, never people).
        // Nothing in the authority consulted by the conversation gate knows that a department exists.
        [Fact]
        public async Task B3_CrmTicketRead_MustNotIgnoreTheTicketsDepartmentRouting()
        {
            using var host = Seeded();

            bool unseeded = await Crm(host).CanAsync(
                Ctx(CompanyA, RolelessOfA), "read",
                PermissionTarget.ForEntity(EntityRegistry.CrmTicket, UnseededInvoice));

            Assert.False(unseeded,
                $"CRM granted 'read' on CrmTicket#{UnseededInvoice}, which was never seeded. The " +
                "conversation gate additionally calls the UNTARGETED overload crm.CanAsync(\"read\") " +
                "(AccountingController.cs:3370), so not even this target reaches the authority on the " +
                "ticket path — department routing cannot influence the decision.");
        }

        // B4 — SECURITY. Expected to FAIL.
        // A caller whose company is NOT the ticket's company must be refused by the authority itself,
        // not only by the gate's separate row-existence query.
        [Fact]
        public async Task B4_CrmRead_MustRefuseACallerFromAnotherCompany()
        {
            using var host = Seeded();

            // CompanyB has no CrmUserRoles row, so it is UNCONFIGURED and falls through to the policy
            // reader. The assertion is that an unconfigured tenant is not handed CRM read for free.
            bool allowed = await Crm(host).CanAsync(
                Ctx(CompanyB, RolelessOfA), "read",
                PermissionTarget.ForEntity(EntityRegistry.CrmTicket, 1));

            Assert.False(allowed,
                "An employee context scoped to company B was granted CRM read. Company B has no CRM " +
                "roles configured, so this must resolve through the bootstrap policy reader and be " +
                "refused for want of an explicit, active, in-date policy row.");
        }
    }
}
