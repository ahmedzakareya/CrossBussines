using CrossBuy.BL;
using CrossBuy.BL.Hr;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Hr;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Hr
{
    // ============================================================================================
    // EMPLOYEE ONBOARDING — behaviour, against a real database and the real document platform.
    //
    // Nothing here is stubbed except the clock, and that only so a document can be made to expire
    // without waiting for it. HrAccessService is the real one, PlatformDocument rows are real rows,
    // and every assertion is about what the service returns rather than about which method it called.
    //
    // THE FIXTURE HAS TWO COMPANIES AND THE NEIGHBOUR IS NOT EMPTY. A single-company fixture cannot
    // fail an isolation assertion: every row is in scope, so a missing predicate looks exactly like a
    // correct one. Company B therefore has its own employee, its own plan and its own documents, and
    // the isolation tests name them.
    //
    // A NOTE ON WHY THE DOCUMENT TESTS MATTER MOST. The easy version of this feature is a checklist
    // with tick boxes, and a tick box is an opinion. The tests below remove one condition at a time
    // from a satisfying document — wrong type, wrong employee, wrong company, expired, inactive — and
    // require the requirement to go unsatisfied each time. That is the difference between a checklist
    // and a control.
    // ============================================================================================
    public class EmployeeOnboardingTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int Alice = 501;    // company A, the new hire
        private const int Officer = 510;  // company A, HrOfficer  — may manage, may not waive
        private const int Manager = 520;  // company A, HrManager  — may waive
        private const int Mallory = 901;  // company B

        // ANCHORED TO THE REAL UTC DATE, and that is a consequence of convergence rather than a
        // preference. Onboarding no longer owns the expiry rule: the platform does, and its rule reads
        // DateTime.UtcNow.Date inline with no clock seam. A fixture pinned to a fixed date therefore
        // disagrees with the authority it is testing - a document expiring on that pinned date is, by
        // the platform's clock, months expired.
        //
        // Every date below is relative to this, so the boundary the tests care about - "expires TODAY
        // is still valid" - still means what it says. The platform having no injectable clock is a real
        // gap and is reported as such; it is not this batch's to close.
        private static readonly DateTime Today = DateTime.UtcNow.Date;

        private sealed class FixedClock : IReportClockShim
        {
            public DateTime Now { get; init; } = Today;
        }

        // ---- fixture ---------------------------------------------------------------------------
        private static PlatformTestHost Seed()
        {
            var host = new PlatformTestHost();
            var db = host.Db;

            db.JobTitles.Add(new JobTitle { ID = 1, Title = "Dev", TitleAr = "مطور", Description = "" });
            db.Employee.AddRange(
                Person(Alice, CompanyA), Person(Officer, CompanyA),
                Person(Manager, CompanyA), Person(Mallory, CompanyB));
            db.SaveChanges();
            return host;
        }

        private static Employee Person(int id, int companyId) => new()
        {
            ID = id, EmpCompanyID = companyId, IsActive = true,
            FullName = "E" + id, FullNameEn = "E" + id, FirstName = "E", LastName = id.ToString(),
            Email = id + "@x.local", PhoneNumber = "09" + id, Address = "", Gender = "F",
            MaritalStatus = "Single", ProfileImage = "", UserId = "u" + id, JobTitleID = 1,
            DateOfJoining = new DateTime(2026, 6, 1),
        };

        private static void GiveHrRole(PlatformTestHost host, int companyId, int employeeId, string role)
        {
            host.Db.Set<PlatformRoleAssignment>().Add(new()
            {
                CompanyID = companyId, Scope = EntityRegistry.ScopeHr,
                PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = employeeId,
                Role = role, IsActive = true,
            });
            host.Db.SaveChanges();
        }

        // Http, not Worker: ModuleAccessServiceBase refuses a Worker outright, which would make every
        // refusal below pass without exercising anything.
        private static BusinessContext Ctx(int companyId, int? employeeId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId,
            UserId = "u" + employeeId, Source = BusinessContextSource.Http,
        };

        // ---- validity convergence ------------------------------------------------------------
        //
        // Onboarding used to answer "is this document valid" itself, with five predicates copied from
        // the platform. The copy had drifted: it never asked for CurrentVersionId. A PlatformDocument
        // row with no current version is a document SHELL - a record saying a passport exists, with no
        // file behind it - and onboarding accepted one as satisfying a mandatory requirement, then
        // reported the employee ready to work.
        //
        // Nothing caught it because the test fixture built shells too, so the fixture and the defect
        // agreed with each other. These tests are the discriminating cases.

        [Fact]
        public async Task A_document_with_no_current_version_does_not_satisfy_a_requirement()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "PASSPORT");
            AddTemplate(host, CompanyA, ("collect-passport", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            // Right company, right employee, right type, Active, unexpired - and no version. Before the
            // convergence this completed the requirement.
            AddDoc(host, CompanyA, EntityRegistry.Employee, Alice, typeId, withVersion: false);

            Assert.False((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);

            // ...and the same document with its version present does satisfy, so the refusal above is
            // about the missing version and nothing else.
            AddDoc(host, CompanyA, EntityRegistry.Employee, Alice, typeId);
            Assert.True((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);
        }

        [Fact]
        public async Task A_submitted_document_is_received_evidence_and_does_not_satisfy()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "CONTRACT");
            AddTemplate(host, CompanyA, ("collect-contract", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            // Submitted is a person having handed something IN. It is not verified, so it cannot be
            // what makes an employee ready - that is the whole reason Submitted is not Active.
            AddDoc(host, CompanyA, EntityRegistry.Employee, Alice, typeId, status: "Submitted");
            Assert.False((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);
        }

        [Fact]
        public async Task Onboarding_asks_the_platform_rather_than_carrying_its_own_predicate()
        {
            var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "CrossBuy", "BL", "Hr", "EmployeeOnboardingService.cs"));
            var code = string.Join("\n", source.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", System.StringComparison.Ordinal)));

            // The copy is gone in BOTH paths - the checklist resolver and the completion check each had
            // their own, independently drifted, version of the same rule.
            Assert.DoesNotContain("d.Status == \"Active\"", code, System.StringComparison.Ordinal);
            Assert.Contains("FindValidDocumentAsync", code, System.StringComparison.Ordinal);
            Assert.Contains("_documents.HasValidDocumentAsync", code, System.StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "CrossBuy.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static EmployeeOnboardingService Svc(PlatformTestHost host, BusinessContext? ctx)
        {
            var hr = new HrAccessService(host.Db,
                new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
                new BootstrapAccessPolicyReader(host.Db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<HrAccessService>.Instance);

            var accessor = ctx == null ? StubContextAccessor.Unresolved() : new StubContextAccessor(ctx);

            // The REAL platform document service, not a stub. Onboarding no longer carries its own
            // validity predicate, so these tests only mean something if the thing answering is the
            // thing production asks: a stub here would let the drift this batch removed reappear
            // without a single test noticing.
            var documents = new CrossBuy.BL.Documents.PlatformDocumentService(
                host.Db,
                accessor,
                new DocumentAccessResolver(
                    new IDocumentOwnerResolver[] { new EmployeeDocumentOwnerResolver(host.Db) },
                    new IModuleAccessService[] { hr },
                    new EntityRegistry(host.Db),
                    NullLogger<DocumentAccessResolver>.Instance),
                new LocalDocumentStorage(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "cb-onb-" + System.Guid.NewGuid().ToString("n"))),
                new EntityRegistry(host.Db));
            return new EmployeeOnboardingService(host.Db, accessor, hr, new FixedClock(), documents);
        }

        private static long AddDocType(PlatformTestHost host, int? companyId, string code)
        {
            var t = new PlatformDocumentType
            {
                CompanyID = companyId, Code = code, NameAr = code, NameEn = code,
                AppliesToEntityTypes = EntityRegistry.Employee,
                DefaultConfidentiality = DocumentConfidentiality.Internal,
            };
            host.Db.Set<PlatformDocumentType>().Add(t);
            host.Db.SaveChanges();
            return t.Id;
        }

        // A REAL document: a row AND the version that holds its bytes.
        //
        // This helper used to create the row alone, leaving CurrentVersionId null - a document shell
        // with no file behind it. Onboarding accepted those because its own validity predicate never
        // asked, which is exactly the drift this batch removed. Now that the canonical service
        // answers, a shell is correctly refused, so the fixture has to build what production builds.
        //
        // `withVersion: false` deliberately keeps the old shape available - one test uses it to prove
        // the shell is refused, which is the whole point.
        private static PlatformDocument AddDoc(PlatformTestHost host, int companyId, string entityType,
            int entityId, long? typeId, DateTime? expiry = null, string status = "Active",
            bool withVersion = true)
        {
            var d = new PlatformDocument
            {
                CompanyID = companyId, EntityType = entityType, EntityId = entityId,
                DocumentTypeId = typeId, Confidentiality = DocumentConfidentiality.Internal,
                ExpiryDate = expiry, Status = status, CreatedBy = 1, CreatedAt = Today,
            };
            host.Db.Set<PlatformDocument>().Add(d);
            host.Db.SaveChanges();

            if (!withVersion) return d;

            var v = new PlatformDocumentVersion
            {
                CompanyID = companyId, DocumentId = d.Id, VersionNo = 1,
                StorageKey = System.Guid.NewGuid().ToString("n"),
                FileName = "scan.pdf", ContentType = "application/pdf", SizeBytes = 4,
                UploadedBy = 1, UploadedAt = Today,
            };
            host.Db.Set<PlatformDocumentVersion>().Add(v);
            host.Db.SaveChanges();

            d.CurrentVersionId = v.Id;
            host.Db.SaveChanges();
            return d;
        }

        private static int AddTemplate(PlatformTestHost host, int companyId, params (string key, bool mandatory, long? docType)[] items)
        {
            var t = new OnboardingTemplate
            {
                CompanyID = companyId, NameAr = "قالب", NameEn = "Template",
                IsActive = true, IsDefault = true, DefaultDurationDays = 30,
            };
            int i = 0;
            foreach (var (key, mandatory, docType) in items)
            {
                t.Items.Add(new OnboardingTemplateItem
                {
                    CompanyID = companyId, ItemKey = key, TitleAr = key, TitleEn = key,
                    IsMandatory = mandatory, RequiredDocumentTypeID = docType,
                    Responsibility = OnboardingResponsibility.Hr, SortOrder = i++, DueOffsetDays = 7,
                });
            }
            host.Db.OnboardingTemplates.Add(t);
            host.Db.SaveChanges();
            return t.ID;
        }

        // =========================================================================================
        // §27 — DOMAIN
        // =========================================================================================

        [Fact]
        public async Task An_authorized_hr_officer_can_start_onboarding()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-id", true, null), ("issue-laptop", false, null));

            var result = await Svc(host, Ctx(CompanyA, Officer)).StartAsync(Alice, null, null);

            Assert.True(result.Ok);
            Assert.Equal(OnboardingStatus.InProgress, result.Plan!.Status);
            Assert.Equal(2, result.Plan.Items.Count);

            // The template's day-offset became a real date, anchored on the joining date, once.
            Assert.All(result.Plan.Items, i => Assert.Equal(new DateTime(2026, 6, 8), i.DueDate));
        }

        [Fact]
        public async Task Starting_twice_returns_the_same_plan_rather_than_creating_a_second()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-id", true, null));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var first = await svc.StartAsync(Alice, null, null);
            var second = await svc.StartAsync(Alice, null, null);

            Assert.True(first.Ok && second.Ok);
            Assert.Equal(first.Plan!.ID, second.Plan!.ID);
            Assert.Single(host.Db.EmployeeOnboardings.Where(o => o.EmployeeID == Alice));
        }

        [Fact]
        public async Task An_unresolved_business_context_fails_closed()
        {
            using var host = Seed();
            AddTemplate(host, CompanyA, ("collect-id", true, null));

            var svc = Svc(host, null);
            Assert.False((await svc.StartAsync(Alice, null, null)).Ok);
            Assert.Null(await svc.GetAsync(Alice));
        }

        [Fact]
        public async Task Company_A_cannot_read_company_B_onboarding_by_exact_employee_id()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            GiveHrRole(host, CompanyB, Mallory, HrRoles.HrOfficer);
            AddTemplate(host, CompanyB, ("collect-id", true, null));

            // Company B genuinely has a plan — so a missing predicate would return it.
            var made = await Svc(host, Ctx(CompanyB, Mallory)).StartAsync(Mallory, null, null);
            Assert.True(made.Ok);

            Assert.Null(await Svc(host, Ctx(CompanyA, Officer)).GetAsync(Mallory));
        }

        [Fact]
        public async Task A_cross_company_employee_cannot_be_attached_to_onboarding()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-id", true, null));

            // Mallory is a real, active employee — of somebody else's company.
            var result = await Svc(host, Ctx(CompanyA, Officer)).StartAsync(Mallory, null, null);

            Assert.False(result.Ok);
            Assert.Empty(host.Db.EmployeeOnboardings.Where(o => o.EmployeeID == Mallory));
        }

        [Fact]
        public async Task An_employee_with_no_hr_authority_is_denied()
        {
            using var host = Seed();
            // A configured role for somebody ELSE, so the module is not bootstrap-open.
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-id", true, null));

            // Alice holds nothing. She is a real employee of the right company: only the right is missing.
            Assert.False((await Svc(host, Ctx(CompanyA, Alice)).StartAsync(Alice, null, null)).Ok);
        }

        [Fact]
        public async Task A_plan_cannot_complete_while_a_mandatory_item_is_outstanding()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-id", true, null), ("optional-tour", false, null));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            var result = await svc.CompleteAsync(plan.ID);

            Assert.False(result.Ok);
            Assert.Equal("mandatory_items_outstanding", result.Error);
            Assert.Equal(OnboardingStatus.InProgress,
                host.Db.EmployeeOnboardings.Single(o => o.ID == plan.ID).Status);
        }

        [Fact]
        public async Task A_plan_completes_once_every_mandatory_item_is_satisfied()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-id", true, null), ("optional-tour", false, null));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;
            var mandatory = plan.Items.Single(i => i.IsMandatory);

            Assert.True((await svc.CompleteItemAsync(mandatory.ID, null)).Ok);

            var done = await svc.CompleteAsync(plan.ID);

            // The OPTIONAL item is still Pending and does not block. That is what optional means.
            Assert.True(done.Ok);
            Assert.Equal(OnboardingStatus.Completed,
                host.Db.EmployeeOnboardings.Single(o => o.ID == plan.ID).Status);
        }

        [Fact]
        public async Task Completion_records_the_actor_and_the_moment_from_server_identity()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-id", true, null));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;
            await svc.CompleteItemAsync(plan.Items.First().ID, "done");

            var item = host.Db.EmployeeOnboardingItems.Single(i => i.OnboardingID == plan.ID);
            Assert.Equal(Officer, item.CompletedBy);   // from BusinessContext, not from any parameter
            Assert.Equal(Today, item.CompletedAt);
        }

        // ---- waiver ------------------------------------------------------------------------------

        [Fact]
        public async Task A_waiver_requires_a_reason()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Manager, HrRoles.HrManager);
            AddTemplate(host, CompanyA, ("collect-id", true, null));

            var svc = Svc(host, Ctx(CompanyA, Manager));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            var result = await svc.WaiveItemAsync(plan.Items.First().ID, "   ");

            Assert.False(result.Ok);
            Assert.Equal("waiver_reason_required", result.Error);
            Assert.Equal(OnboardingItemStatus.Pending,
                host.Db.EmployeeOnboardingItems.Single().Status);
        }

        [Fact]
        public async Task A_waiver_requires_more_authority_than_completing_an_item()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-id", true, null));

            var officer = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await officer.StartAsync(Alice, null, null)).Plan!;
            var itemId = plan.Items.First().ID;

            // The officer may COMPLETE — proving the denial below is about the waiver, not about the
            // officer being unable to touch onboarding at all.
            var waive = await officer.WaiveItemAsync(itemId, "joiner exempt");
            Assert.False(waive.Ok);

            GiveHrRole(host, CompanyA, Manager, HrRoles.HrManager);
            var manager = Svc(host, Ctx(CompanyA, Manager));
            Assert.True((await manager.WaiveItemAsync(itemId, "joiner exempt")).Ok);

            var item = host.Db.EmployeeOnboardingItems.Single();
            Assert.Equal(OnboardingItemStatus.Waived, item.Status);
            Assert.Equal(Manager, item.WaivedBy);
            Assert.Equal("joiner exempt", item.WaiverReason);
            Assert.Equal(Today, item.WaivedAt);
        }

        // =========================================================================================
        // §28 — DOCUMENTS, against the real platform
        // =========================================================================================

        [Fact]
        public void Employee_supports_files_in_the_entity_registry()
        {
            using var host = new PlatformTestHost();
            // The premise everything below rests on. If Employee stopped supporting files, the document
            // requirements would be unreachable and these tests would be asserting about a dead path.
            var registry = new EntityRegistry(host.Db);
            Assert.True(registry.TryGetDefinition(EntityRegistry.Employee, out var entry));
            Assert.True(entry!.SupportsFiles);
            Assert.Equal(EntityRegistry.ScopeHr, entry.PermissionScope);
        }

        [Fact]
        public async Task A_document_item_stays_incomplete_while_no_document_exists()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "CIVIL_ID");
            AddTemplate(host, CompanyA, ("collect-civil-id", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            // THE TICK BOX IS REFUSED. This is the line between a checklist and a control.
            var attempt = await svc.CompleteItemAsync(plan.Items.First().ID, null);
            Assert.False(attempt.Ok);
            Assert.Equal("required_document_missing", attempt.Error);

            var view = await svc.GetAsync(Alice);
            Assert.False(view!.IsReady);
            Assert.Equal(0, view.DocumentsPresent);
            Assert.Equal(1, view.DocumentsRequired);
        }

        [Fact]
        public async Task The_correct_document_satisfies_the_requirement()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "CIVIL_ID");
            AddTemplate(host, CompanyA, ("collect-civil-id", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;
            AddDoc(host, CompanyA, EntityRegistry.Employee, Alice, typeId);

            Assert.True((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);

            var view = await svc.GetAsync(Alice);
            Assert.Equal(1, view!.DocumentsPresent);
            Assert.True(view.IsReady);
        }

        [Fact]
        public async Task A_document_of_the_wrong_type_does_not_satisfy()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var civilId = AddDocType(host, CompanyA, "CIVIL_ID");
            var passport = AddDocType(host, CompanyA, "PASSPORT");
            AddTemplate(host, CompanyA, ("collect-civil-id", true, civilId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;
            AddDoc(host, CompanyA, EntityRegistry.Employee, Alice, passport);

            Assert.False((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);
            Assert.Equal(0, (await svc.GetAsync(Alice))!.DocumentsPresent);
        }

        [Fact]
        public async Task A_document_attached_to_another_employee_does_not_satisfy()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "CIVIL_ID");
            AddTemplate(host, CompanyA, ("collect-civil-id", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            // Same company, same type, right entity TYPE — wrong person.
            AddDoc(host, CompanyA, EntityRegistry.Employee, Officer, typeId);

            Assert.False((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);
        }

        [Fact]
        public async Task A_document_from_another_company_does_not_satisfy()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "CIVIL_ID");
            AddTemplate(host, CompanyA, ("collect-civil-id", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            // Right type, right employee id — but stamped with the neighbour's company.
            AddDoc(host, CompanyB, EntityRegistry.Employee, Alice, typeId);

            Assert.False((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);
        }

        [Fact]
        public async Task An_expired_document_does_not_satisfy()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "CIVIL_ID");
            AddTemplate(host, CompanyA, ("collect-civil-id", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            // Everything correct except that it lapsed yesterday. Nobody edited the onboarding item;
            // the requirement simply stopped being satisfied, which is the behaviour expiry must have.
            AddDoc(host, CompanyA, EntityRegistry.Employee, Alice, typeId, expiry: Today.AddDays(-1));

            Assert.False((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);

            // And a document expiring TODAY is still valid — the boundary, stated rather than assumed.
            AddDoc(host, CompanyA, EntityRegistry.Employee, Alice, typeId, expiry: Today);
            Assert.True((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);
        }

        [Fact]
        public async Task A_superseded_document_does_not_satisfy()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "CIVIL_ID");
            AddTemplate(host, CompanyA, ("collect-civil-id", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;
            AddDoc(host, CompanyA, EntityRegistry.Employee, Alice, typeId, status: "Superseded");

            Assert.False((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);
        }

        [Fact]
        public async Task A_document_on_a_different_entity_type_does_not_satisfy()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "CIVIL_ID");
            AddTemplate(host, CompanyA, ("collect-civil-id", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            // Same company, same numeric id, same type — but it is a document about a Project, not an
            // Employee. Without the EntityType predicate the id collision alone would satisfy it.
            AddDoc(host, CompanyA, EntityRegistry.Project, Alice, typeId);

            Assert.False((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);
        }

        [Fact]
        public async Task A_legacy_employee_document_does_not_satisfy_a_governed_requirement()
        {
            using var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            var typeId = AddDocType(host, CompanyA, "CIVIL_ID");
            AddTemplate(host, CompanyA, ("collect-civil-id", true, typeId));

            var svc = Svc(host, Ctx(CompanyA, Officer));
            var plan = (await svc.StartAsync(Alice, null, null)).Plan!;

            // The employee HAS a civil ID in the legacy HR table. There is no deterministic bridge from
            // a legacy DocType string to a governed PlatformDocumentType, so counting it would be a
            // guess dressed as compliance. It does not satisfy, and the report says so.
            host.Db.EmployeeDocuments.Add(new EmployeeDocument
            {
                CompanyID = CompanyA, EmployeeID = Alice, DocType = "CIVIL_ID",
            });
            host.Db.SaveChanges();

            Assert.False((await svc.CompleteItemAsync(plan.Items.First().ID, null)).Ok);
        }

        // =========================================================================================
        // §13 SELF-SERVICE. Alice has NO HR role at all — she is an ordinary employee.
        //
        // These two tests exist because I claimed the self-service read path "already works by
        // construction" after reading HrAccessService, and a claim read off a gate is not a result.
        // The asymmetry is the whole point: an employee may SEE what is being asked of her, and may
        // change none of it.
        // =========================================================================================

        [Fact]
        public async Task An_employee_with_no_hr_role_can_read_their_own_onboarding()
        {
            var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-civil-id", true, null), ("issue-laptop", false, null));
            await Svc(host, Ctx(CompanyA, Officer)).StartAsync(Alice, null, null);

            // Alice, about Alice. No role assignment anywhere.
            var mine = await Svc(host, Ctx(CompanyA, Alice)).GetAsync(Alice);

            Assert.NotNull(mine);
            Assert.Equal(2, mine!.Requirements.Count);
        }

        [Fact]
        public async Task An_employee_cannot_read_a_colleagues_onboarding()
        {
            var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-civil-id", true, null));
            await Svc(host, Ctx(CompanyA, Officer)).StartAsync(Manager, null, null);

            // Same company, no role, someone else's record.
            Assert.Null(await Svc(host, Ctx(CompanyA, Alice)).GetAsync(Manager));
        }

        [Fact]
        public async Task An_employee_cannot_complete_or_waive_their_own_requirements()
        {
            var host = Seed();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);
            AddTemplate(host, CompanyA, ("collect-civil-id", true, null));
            var plan = (await Svc(host, Ctx(CompanyA, Officer)).StartAsync(Alice, null, null)).Plan!;
            var itemId = plan.Items.First().ID;

            var asAlice = Svc(host, Ctx(CompanyA, Alice));

            // Reading it is allowed (test above). Ticking it off is not: completing is an HR act, and
            // "it is my own onboarding" is not a claim to authority over it.
            Assert.False((await asAlice.CompleteItemAsync(itemId, null)).Ok);
            Assert.False((await asAlice.WaiveItemAsync(itemId, "I do not have one")).Ok);
            Assert.False((await asAlice.CompleteAsync(plan.ID)).Ok);

            // And nothing moved.
            var after = await Svc(host, Ctx(CompanyA, Officer)).GetAsync(Alice);
            Assert.Equal(OnboardingItemStatus.Pending, after!.Requirements.Single().Item.Status);
            Assert.False(after.IsReady);
        }
    }
}
