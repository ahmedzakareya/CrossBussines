using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // CENTRAL DOCUMENT PLATFORM — the P0 security spine.
    //
    // THE DEFECT THESE PIN. PrivateFileGate closed the anonymous hole and says openly that it does not
    // enforce a company match: it declares a RequireCompanyMatch policy applied to no prefix, because
    // the gate runs before UseSession() and cannot resolve a BusinessContext at all. So today, for
    // every protected prefix, knowing /uploads/hr-docs/<guid>.pdf and being signed in as ANYONE is
    // enough. Two companies' documents, one authenticated caller, no boundary.
    //
    // Every test below uses the EXACT identifier of a real document. None of them relies on the caller
    // failing to guess something - guessing resistance is obscurity, and obscurity is what the old path
    // already had. The question is whether possession of the identifier is refused.
    // ============================================================================================
    public class DocumentAccessFoundationTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int AliceOfA = 5;       // owns the document
        private const int ColleagueOfA = 6;   // same company, no HR authority
        private const int HrOfficerOfA = 7;   // same company, HR authority
        private const int MalloryOfB = 900;   // another company entirely

        // ---- 27. cross-tenant, with the exact key ---------------------------------------------------

        [Fact]
        public async Task An_employee_of_another_company_holding_the_exact_identifier_is_refused()
        {
            using var host = Seeded();
            var resolver = Build(host);

            // Company B's employee knows precisely which document this is - entity type, entity id and the
            // owning company. Not a guess, not enumeration: the exact resource.
            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyB, MalloryOfB),
                new DocumentOwnerRef(EntityRegistry.Employee, AliceOfA),
                DocumentAction.Download,
                documentCompanyId: CompanyA);

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.CompanyMismatch, decision.ReasonCode);
        }

        // ---- 28. same company, no business authority ------------------------------------------------

        [Fact]
        public async Task A_colleague_in_the_same_company_without_the_owning_authority_is_refused()
        {
            using var host = Seeded();
            Configure(host, CompanyA);                       // the company HAS HR roles, so bootstrap is over
            var resolver = Build(host);

            // Same tenant, correct document company, exact identifier - and still no. Company equality is
            // where the old model stopped; it is not an authority over a colleague's personnel file.
            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyA, ColleagueOfA),
                new DocumentOwnerRef(EntityRegistry.Employee, AliceOfA),
                DocumentAction.Download,
                documentCompanyId: CompanyA);

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.ModuleDenied, decision.ReasonCode);
        }

        [Fact]
        public async Task Confidentiality_is_applied_on_top_of_the_record_rule_and_not_instead_of_it()
        {
            using var host = Seeded();
            GiveHrRole(host, CompanyA, HrOfficerOfA, HrRoles.HrOfficer);
            var resolver = Build(host);
            var officer = Employee(CompanyA, HrOfficerOfA);
            var alice = new DocumentOwnerRef(EntityRegistry.Employee, AliceOfA);

            // An HR officer administers employees, so an ordinary document is theirs to read...
            Assert.True((await resolver.AuthorizeAsync(
                officer, alice, DocumentAction.Download, CompanyA,
                DocumentConfidentiality.Internal)).Allowed);

            // ...and a Confidential one is not: HR separates confidential-view from employee-manage
            // precisely because administering someone is not entitlement to their disciplinary file.
            var confidential = await resolver.AuthorizeAsync(
                officer, alice, DocumentAction.Download, CompanyA, DocumentConfidentiality.Confidential);

            Assert.False(confidential.Allowed);
            Assert.Equal(DocumentAccessReasons.ConfidentialityDenied, confidential.ReasonCode);
        }

        // ---- 29. the authorized case ----------------------------------------------------------------

        [Fact]
        public async Task The_right_company_with_the_owning_authority_is_allowed()
        {
            using var host = Seeded();
            GiveHrRole(host, CompanyA, HrOfficerOfA, HrRoles.HrOfficer);
            var resolver = Build(host);

            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyA, HrOfficerOfA),
                new DocumentOwnerRef(EntityRegistry.Employee, AliceOfA),
                DocumentAction.Download,
                documentCompanyId: CompanyA);

            // Without this the suite would be satisfied by a resolver that refuses everything.
            Assert.True(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.Allowed, decision.ReasonCode);
        }

        [Fact]
        public async Task An_employee_reaches_their_own_document()
        {
            using var host = Seeded();
            var resolver = Build(host);

            // HR's self-access rule, arriving through the module rather than being restated here: an
            // employee may view their own non-confidential record with no HR role at all.
            Assert.True((await resolver.AuthorizeAsync(
                Employee(CompanyA, AliceOfA),
                new DocumentOwnerRef(EntityRegistry.Employee, AliceOfA),
                DocumentAction.Download,
                documentCompanyId: CompanyA)).Allowed);
        }

        // ---- 30. the mismatched relation ------------------------------------------------------------

        [Fact]
        public async Task A_document_claiming_one_company_while_its_owner_belongs_to_another_is_refused()
        {
            using var host = Seeded();
            GiveHrRole(host, CompanyA, HrOfficerOfA, HrRoles.HrOfficer);
            var resolver = Build(host);

            // THE SPOOF THIS CLOSES. The row says company A, the caller IS company A with real HR
            // authority, so every company check passes - but the entity it points at is company B's
            // employee. A resolver that trusted the document's own CompanyID would hand over a foreign
            // personnel file here and every tenant-isolation assertion in the suite would still be green.
            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyA, HrOfficerOfA),
                new DocumentOwnerRef(EntityRegistry.Employee, MalloryOfB),
                DocumentAction.Download,
                documentCompanyId: CompanyA);

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.RelationMismatch, decision.ReasonCode);
        }

        [Fact]
        public async Task An_owner_that_does_not_exist_is_refused_the_same_way_an_absent_one_is()
        {
            using var host = Seeded();
            GiveHrRole(host, CompanyA, HrOfficerOfA, HrRoles.HrOfficer);
            var resolver = Build(host);

            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyA, HrOfficerOfA),
                new DocumentOwnerRef(EntityRegistry.Employee, 424242),
                DocumentAction.Download,
                documentCompanyId: CompanyA);

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.OwnerNotFound, decision.ReasonCode);
        }

        // ---- fail-closed by construction ------------------------------------------------------------

        [Fact]
        public async Task A_family_with_no_owner_resolver_is_refused_rather_than_assumed_safe()
        {
            using var host = Seeded();
            var resolver = Build(host);

            // Quotation is a real registry code with no document owner resolver yet. Onboarding a family
            // has to be a deliberate act: a document platform that failed OPEN on unclassified families
            // would be worse than the hole it replaces.
            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyA, HrOfficerOfA),
                new DocumentOwnerRef(EntityRegistry.Quotation, 1),
                DocumentAction.Download,
                documentCompanyId: CompanyA);

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.NoOwnerResolver, decision.ReasonCode);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task An_unresolved_company_is_refused_and_never_becomes_company_one(int companyId)
        {
            using var host = Seeded();
            var resolver = Build(host);

            var decision = await resolver.AuthorizeAsync(
                Employee(companyId, AliceOfA),
                new DocumentOwnerRef(EntityRegistry.Employee, AliceOfA),
                DocumentAction.Download,
                documentCompanyId: CompanyA);

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.CompanyUnresolved, decision.ReasonCode);
        }

        [Fact]
        public async Task A_worker_context_holds_no_document_authority()
        {
            using var host = Seeded();
            var resolver = Build(host);

            // A worker names a company but has no employee, so no record-level relationship exists to
            // judge. It is refused here rather than reaching a module rule that only checks the company.
            var decision = await resolver.AuthorizeAsync(
                new BusinessContext { CompanyId = CompanyA, EmployeeId = null, Source = BusinessContextSource.Worker },
                new DocumentOwnerRef(EntityRegistry.Employee, AliceOfA),
                DocumentAction.Download,
                documentCompanyId: CompanyA);

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.NoEmployeeIdentity, decision.ReasonCode);
        }

        [Fact]
        public async Task An_unknown_confidentiality_label_is_refused_rather_than_treated_as_internal()
        {
            using var host = Seeded();
            GiveHrRole(host, CompanyA, HrOfficerOfA, HrRoles.HrOfficer);
            var resolver = Build(host);

            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyA, HrOfficerOfA),
                new DocumentOwnerRef(EntityRegistry.Employee, AliceOfA),
                DocumentAction.Download,
                CompanyA,
                confidentiality: "Public-ish");

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.UnknownConfidentiality, decision.ReasonCode);
        }

        // ---- 31. a path is not an identifier --------------------------------------------------------

        [Fact]
        public void A_storage_key_cannot_carry_a_path()
        {
            // The key shape is the containment. Nothing that could reach outside a directory survives
            // TryParse, so LocalDocumentStorage never has to sanitise anything a caller supplied.
            foreach (var hostile in new[]
                     {
                         "../../appsettings.Production.json",
                         @"C:\Windows\win.ini",
                         "/uploads/hr-docs/x.pdf",
                         "..",
                         "",
                         "0123456789abcdef0123456789abcdeG",   // right length, not hex
                         "0123456789abcdef0123456789abcde",    // one short
                     })
            {
                Assert.False(StorageKey.TryParse(hostile, out _), hostile);
            }

            // ...while a key this storage actually issued round-trips.
            var issued = StorageKey.New();
            Assert.True(StorageKey.TryParse(issued.Value, out var parsed));
            Assert.Equal(issued.Value, parsed.Value);
        }

        [Fact]
        public async Task Stored_content_is_reachable_only_through_the_key_and_lives_outside_the_web_root()
        {
            var root = Path.Combine(Path.GetTempPath(), "cb-doc-" + Guid.NewGuid().ToString("n"));
            try
            {
                var storage = new LocalDocumentStorage(root);

                using var content = new MemoryStream(new byte[] { 1, 2, 3, 4 });
                var key = await storage.StoreAsync(content, "payslip.pdf");

                Assert.True(await storage.ExistsAsync(key));

                // The suggested name is metadata, not location: nothing under the root is named after the
                // business document, so the store leaks no filenames even to someone who can list it.
                var written = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                Assert.Single(written);
                Assert.DoesNotContain("payslip", written[0], StringComparison.OrdinalIgnoreCase);
                Assert.Contains(key.Value, written[0], StringComparison.Ordinal);

                await using (var read = await storage.OpenReadAsync(key))
                {
                    Assert.NotNull(read);
                    Assert.Equal(4, read!.Length);
                }

                Assert.True(await storage.DeleteAsync(key));
                Assert.False(await storage.ExistsAsync(key));

                // A key that names nothing answers null, not an exception: missing and refused must be
                // equally uninformative to whatever is upstream.
                Assert.Null(await storage.OpenReadAsync(key));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void The_private_file_gate_still_refuses_a_traversal_and_covers_the_document_prefixes()
        {
            // The gate remains the anonymous backstop while legacy URLs exist; this pins that the P0 work
            // did not loosen it. A surviving dot-segment is refused rather than classified.
            Assert.Equal(PrivateFilePolicy.RequireAuthenticated,
                PrivateFileGate.PolicyFor("/uploads/hr-docs/../../appsettings.json"));

            foreach (var prefix in new[] { "/uploads/hr-docs", "/uploads/applicants", "/files", "/uploads/library" })
                Assert.NotEqual(PrivateFilePolicy.Public, PrivateFileGate.PolicyFor(prefix + "/x.pdf"));

            // And a sibling folder whose name merely starts the same way is not swept in.
            Assert.Equal(PrivateFilePolicy.Public, PrivateFileGate.PolicyFor("/uploads/chatter-public/x.png"));
        }

        // ---- the Employee family is onboarded, and onboarding is per-family ------------------------

        [Fact]
        public void Employee_carries_documents_and_the_flag_was_not_turned_on_in_bulk()
        {
            using var host = new PlatformTestHost();
            var registry = new EntityRegistry(host.Db);

            Assert.True(registry.TryGetDefinition(EntityRegistry.Employee, out var employee));
            Assert.True(employee!.SupportsFiles);

            // ...and it routes to a module that can actually answer, which is the reason the flag could
            // be turned on at all. ScopeNone would put it back on DefaultPermissionAdapter, where View is
            // granted to any authenticated same-company caller.
            Assert.Equal(EntityRegistry.ScopeHr, employee.PermissionScope);

            // ONBOARDING IS DELIBERATE, ONE FAMILY AT A TIME. Every other family that has no document
            // owner resolver must still be false: a SupportsFiles that drifted true in bulk would invite
            // callers to attach documents to records the platform cannot authorize.
            foreach (var code in new[]
                     {
                         EntityRegistry.SalesInvoice, EntityRegistry.PurchaseInvoice, EntityRegistry.Quotation,
                         EntityRegistry.Customer, EntityRegistry.Supplier, EntityRegistry.Project,
                         EntityRegistry.Item, EntityRegistry.JournalEntry,
                     })
            {
                Assert.True(registry.TryGetDefinition(code, out var d), code);
                Assert.False(d!.SupportsFiles, code + " gained SupportsFiles without an owner resolver");
            }
        }

        [Fact]
        public async Task The_flag_grants_nothing_on_its_own()
        {
            using var host = Seeded();
            var resolver = Build(host);

            // SupportsFiles says the family HAS documents. It is not an authority, and the proof is that
            // the cross-company answer is unchanged by it: company B's employee, exact identifier, refused.
            var decision = await resolver.AuthorizeAsync(
                Employee(CompanyB, MalloryOfB),
                new DocumentOwnerRef(EntityRegistry.Employee, AliceOfA),
                DocumentAction.Download,
                documentCompanyId: CompanyA);

            Assert.False(decision.Allowed);
            Assert.Equal(DocumentAccessReasons.CompanyMismatch, decision.ReasonCode);
        }

        // ---- fixture ---------------------------------------------------------------------------------

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
            Add(host, AliceOfA, CompanyA);
            Add(host, ColleagueOfA, CompanyA);
            Add(host, HrOfficerOfA, CompanyA);
            Add(host, MalloryOfB, CompanyB);
            return host;
        }

        private static void Add(PlatformTestHost host, int id, int companyId)
        {
            host.Db.Employee.Add(new Employee
            {
                ID = id, EmpCompanyID = companyId, IsActive = true,
                FirstName = "E" + id, LastName = "T", FullName = "E" + id,
                Address = "-", PhoneNumber = "-", Email = "-", ProfileImage = "-",
                Gender = "-", MaritalStatus = "-", UserId = "u" + id,
            });
            host.Db.SaveChanges();
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

        // Makes the company configured without granting the CALLER anything - the grant belongs to
        // somebody else, so bootstrap-open is over and the caller still holds no HR role.
        private static void Configure(PlatformTestHost host, int companyId)
            => GiveHrRole(host, companyId, HrOfficerOfA, HrRoles.HrOfficer);
    }
}
