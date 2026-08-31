using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.Communication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // THE PRICE OF DECLARING A SERVICE AS AN AUTHORITY.
    //
    // IPlatformDocumentService is now in AuthorizationSurface.AuthorityTypes, which means CBA001
    // credits any endpoint that reaches it. That credit is honest only while EVERY member really does
    // take an authorization decision — and CORRECTION-004 is the record of what happens when a credit
    // outlives the check behind it: PosLaneActivityGuard sat on a permission list, checked no role,
    // and silently marked 44 mutating POS actions protected.
    //
    // So the declaration does not stand on a code review. It stands on this file, which
    //
    //   * enumerates the interface BY REFLECTION, so a member added tomorrow is not quietly exempt —
    //     an uncovered member fails the coverage test by name; and
    //   * DRIVES each member through a resolver that records every question, so "it authorizes" is
    //     measured rather than read.
    //
    // WHY DRIVING MATTERS MORE THAN READING. Four of these members load the document before they
    // authorize, and a document that does not exist short-circuits the call — so a lazy test that
    // passed a made-up id would record no question and prove the opposite of what it claimed. Every
    // case below therefore acts on a REAL seeded document in the caller's own company.
    // =============================================================================================
    public class DocumentAuthorityMemberTests
    {
        /// One invocation per interface member. The KEY is the member name, and the coverage test
        /// compares these keys against the interface itself — that comparison is the whole guard.
        private static IReadOnlyDictionary<string, Func<IPlatformDocumentService, long, Task>> Drivers() =>
            new Dictionary<string, Func<IPlatformDocumentService, long, Task>>(StringComparer.Ordinal)
            {
                ["UploadAsync"] = (s, _) => s.UploadAsync(
                    new DocumentUploadRequest
                    {
                        EntityType = EntityRegistry.Employee, EntityId = Subject,
                        FileName = "a.pdf", ContentType = "application/pdf",
                    }, DocHost.Bytes("x")),

                ["ReplaceAsync"] = (s, id) => s.ReplaceAsync(id, DocHost.Bytes("y"), "b.pdf", "application/pdf", "r"),

                ["OpenCurrentAsync"] = (s, id) => s.OpenCurrentAsync(id),
                ["OpenVersionAsync"] = (s, id) => s.OpenVersionAsync(id, 1),

                ["ListForEntityAsync"] = (s, _) => s.ListForEntityAsync(EntityRegistry.Employee, Subject),
                ["HistoryAsync"] = (s, id) => s.HistoryAsync(id),

                ["FindValidDocumentAsync"] = (s, _) => s.FindValidDocumentAsync(EntityRegistry.Employee, Subject, TypeId),
                ["HasValidDocumentAsync"] = (s, _) => s.HasValidDocumentAsync(EntityRegistry.Employee, Subject, TypeId),

                ["SubmitAsync"] = (s, _) => s.SubmitAsync(
                    new DocumentSubmissionRequest
                    {
                        EntityType = EntityRegistry.Employee, EntityId = Subject, DocumentTypeId = TypeId,
                        FileName = "c.pdf", ContentType = "application/pdf",
                    }, DocHost.Bytes("z")),

                ["VerifyAsync"] = (s, id) => s.VerifyAsync(id, "ok"),
                ["RejectAsync"] = (s, id) => s.RejectAsync(id, "not legible"),
            };

        private const int Subject = 500;
        private static long TypeId;

        // -----------------------------------------------------------------------------------------
        // THE COVERAGE GUARD. This is the test that keeps the declaration honest over time.
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Every_member_of_the_declared_authority_is_covered_by_this_file()
        {
            var declared = typeof(IPlatformDocumentService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name)
                .ToHashSet(StringComparer.Ordinal);

            var driven = Drivers().Keys.ToHashSet(StringComparer.Ordinal);

            var uncovered = declared.Except(driven).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var stale = driven.Except(declared).OrderBy(n => n, StringComparer.Ordinal).ToList();

            // A NEW MEMBER LANDS HERE FIRST. If this fails, the interface grew and nothing has yet
            // proven the new member authorizes — which means the analyzer is crediting an endpoint on
            // a promise. Add a driver, watch the next test prove it, and only then land the member.
            Assert.True(uncovered.Count == 0,
                "IPlatformDocumentService is a DECLARED AUTHORITY. These members are not driven through " +
                "the recording resolver, so nothing proves they authorize: " + string.Join(", ", uncovered));

            Assert.True(stale.Count == 0,
                "these drivers name members the interface no longer has: " + string.Join(", ", stale));
        }

        // -----------------------------------------------------------------------------------------
        // THE BEHAVIOURAL PROOF, one case per member.
        // -----------------------------------------------------------------------------------------

        public static TheoryData<string> Members()
        {
            var data = new TheoryData<string>();
            foreach (var name in Drivers().Keys.OrderBy(n => n, StringComparer.Ordinal)) data.Add(name);
            return data;
        }

        [Theory]
        [MemberData(nameof(Members))]
        public async Task Every_member_asks_the_access_resolver_before_it_acts(string member)
        {
            using var host = new DocHost();
            var documentId = Seed(host);

            // The recorder starts EMPTY after seeding, so anything counted below belongs to the member
            // under test and not to the fixture that built the row.
            host.Access.Asked.Clear();

            var service = host.Service();
            await Drivers()[member](service, documentId);

            Assert.True(host.Access.Asked.Count > 0,
                member + " reached the end of its work without asking IDocumentAccessResolver. It is a " +
                "member of a DECLARED AUTHORITY, so the analyzer credits every endpoint that calls it — " +
                "and this is the exact shape CORRECTION-004 exists to prevent.");
        }

        [Theory]
        [MemberData(nameof(Members))]
        public async Task A_refusal_from_the_resolver_stops_every_member(string member)
        {
            using var host = new DocHost();
            var documentId = Seed(host);

            // The other half of the proof. Asking is not authorizing if the answer is ignored: a member
            // that called the resolver and then wrote anyway would pass the test above and be a hole.
            var blobsBefore = host.Storage.Blobs.Count;
            var docsBefore = host.Platform.Db.Set<PlatformDocument>().Count();
            var versionsBefore = host.Platform.Db.Set<PlatformDocumentVersion>().Count();
            var statusBefore = host.Platform.Db.Set<PlatformDocument>().Single(d => d.Id == documentId).Status;

            host.Access.Decide = (_, _, _, _, _) =>
                new DocumentAccessDecision(false, DocumentAccessReasons.ModuleDenied);

            await Drivers()[member](host.Service(), documentId);

            Assert.Equal(docsBefore, host.Platform.Db.Set<PlatformDocument>().Count());
            Assert.Equal(versionsBefore, host.Platform.Db.Set<PlatformDocumentVersion>().Count());
            Assert.Equal(statusBefore,
                host.Platform.Db.Set<PlatformDocument>().Single(d => d.Id == documentId).Status);

            // A refused write must not leave bytes behind either. A blob nobody may reach is still a
            // copy of somebody's passport sitting on a disk.
            Assert.Equal(blobsBefore, host.Storage.Blobs.Count);
        }

        /// A real document in the caller's own company, Submitted so the decision members have
        /// something decidable, with a real version so the read members have bytes to reach for.
        private static long Seed(DocHost host)
        {
            TypeId = host.SeedType("PASSPORT", EntityRegistry.Employee, selfService: true);

            var doc = new PlatformDocument
            {
                CompanyID = DocHost.CompanyA,
                EntityType = EntityRegistry.Employee,
                EntityId = Subject,
                DocumentTypeId = TypeId,
                Confidentiality = DocumentConfidentiality.Internal,
                Status = "Submitted",
                CreatedBy = DocHost.Alice,
                CreatedAt = DateTime.UtcNow,
            };
            host.Platform.Db.Set<PlatformDocument>().Add(doc);
            host.Platform.Db.SaveChanges();

            var key = StorageKey.New();
            host.Storage.Blobs[key.Value] = new byte[] { 1, 2, 3 };
            var version = new PlatformDocumentVersion
            {
                CompanyID = DocHost.CompanyA,
                DocumentId = doc.Id,
                VersionNo = 1,
                StorageKey = key.Value,
                FileName = "seed.pdf",
                ContentType = "application/pdf",
                SizeBytes = 3,
                Reason = "seed",
                UploadedBy = DocHost.Alice,
                UploadedAt = DateTime.UtcNow,
            };
            host.Platform.Db.Set<PlatformDocumentVersion>().Add(version);
            host.Platform.Db.SaveChanges();

            doc.CurrentVersionId = version.Id;
            host.Platform.Db.SaveChanges();
            return doc.Id;
        }

        // -----------------------------------------------------------------------------------------
        // THE ONE PLACE THE SELF-VERIFICATION RULE IS WEAKER THAN IT READS, pinned deliberately.
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task On_a_CONFIGURED_company_a_submitter_cannot_verify_their_own_document()
        {
            // The property the lifecycle rests on. Verify maps to employee-manage; a submitter holds
            // only employee-request; so verification of one's own document is out of reach and
            // "Verified" keeps its meaning.
            using var host = RealHr();
            GiveHrRole(host, CompanyA, Officer, HrRoles.HrOfficer);   // configures the company
            var resolver = BuildRealResolver(host);

            var alice = Ctx(CompanyA, Alice);
            var own = new DocumentOwnerRef(EntityRegistry.Employee, Alice);

            Assert.True((await resolver.AuthorizeAsync(alice, own, DocumentAction.Submit, CompanyA)).Allowed);
            Assert.False((await resolver.AuthorizeAsync(alice, own, DocumentAction.Replace, CompanyA)).Allowed);
        }

        [Fact]
        public async Task On_a_BOOTSTRAP_OPEN_company_the_same_caller_reaches_the_manage_tier_and_that_is_recorded()
        {
            // NOT A REGRESSION AND NOT AN ENDORSEMENT — a fact worth pinning where somebody will read it.
            //
            // A company that has configured no HR role runs bootstrap-open, where HrAccessService grants
            // employee-manage to a caller about their OWN record. Separation of submitter from verifier
            // therefore does not hold on such a company, and it does not hold for any other HR write
            // either: bootstrap-open is a compatibility mode, not a permission model.
            //
            // It is pinned here so the weakness cannot be discovered as a surprise, and so that closing
            // it later — by adding ("Hr","employee-manage") to NeverBootstrapOpen — announces itself as a
            // deliberate change to this assertion rather than passing unnoticed.
            using var host = RealHr();          // no HR role assigned anywhere: bootstrap-open
            var resolver = BuildRealResolver(host);

            var alice = Ctx(CompanyA, Alice);
            var own = new DocumentOwnerRef(EntityRegistry.Employee, Alice);

            Assert.True((await resolver.AuthorizeAsync(alice, own, DocumentAction.Replace, CompanyA)).Allowed);

            // AND THE LIMIT OF IT, which is why this is a narrower hole than it first looks: a document
            // type whose default tier is above Internal demands confidential-view, and confidential-view
            // is on NeverBootstrapOpen — so bootstrap never confers it, about oneself or anyone else.
            Assert.False((await resolver.AuthorizeAsync(
                alice, own, DocumentAction.Replace, CompanyA, DocumentConfidentiality.Confidential)).Allowed);
        }

        private const int CompanyA = 41;
        private const int Alice = 5;
        private const int Officer = 7;

        private static BusinessContext Ctx(int companyId, int employeeId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = "u" + employeeId,
            Source = BusinessContextSource.Http,
        };

        private static PlatformTestHost RealHr()
        {
            var host = new PlatformTestHost();
            foreach (var id in new[] { Alice, Officer })
            {
                host.Db.Employee.Add(new Employee
                {
                    ID = id, EmpCompanyID = CompanyA, IsActive = true,
                    FirstName = "E" + id, LastName = "T", FullName = "E" + id,
                    Address = "-", PhoneNumber = "-", Email = "-", ProfileImage = "-",
                    Gender = "-", MaritalStatus = "-", UserId = "u" + id,
                });
                host.Db.SaveChanges();
            }
            return host;
        }

        private static IDocumentAccessResolver BuildRealResolver(PlatformTestHost host)
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
