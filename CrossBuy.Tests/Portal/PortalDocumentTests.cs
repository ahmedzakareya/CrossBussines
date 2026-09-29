using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Portal;
using CrossBuy.Controllers;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // CLIENT PORTAL DOC1 — PUBLISHED CLIENT DOCUMENTS.
    //
    // These run the REAL PortalDocumentService over a real SQLite database and a real storage
    // implementation; only the portal context accessor is doubled, and only so a test can BE
    // somebody. The whole chain is exercised, not described:
    //
    //   identity → portal-documents capability → publication row (company AND customer AND live)
    //   → document Active → current version resolved from the document → storage → bytes
    //
    // EVERY REFUSAL HAS AN ALLOW BESIDE IT. Sixteen of these assert that something is denied, and a
    // service that returned nothing to everyone would pass all sixteen. The control tests come
    // first and the rest depend on them - this programme has already shipped one authorization test
    // that passed because no principal existed at all, and once more because a mutation ran against
    // a stale binary. The controls are what make the denials mean anything.
    // =============================================================================================

    /// Storage that really reads and writes, in a temp directory that is NOT inside wwwroot - the
    /// same property the production LocalDocumentStorage relies on. A stub returning a canned stream
    /// would not prove that the key round-trips.
    internal sealed class TempDocumentStorage : IDisposable
    {
        public readonly string Root = Path.Combine(
            Path.GetTempPath(), "cb-doc1-" + Guid.NewGuid().ToString("n"));

        public LocalDocumentStorage Storage { get; }

        public TempDocumentStorage()
        {
            Directory.CreateDirectory(Root);
            Storage = new LocalDocumentStorage(Root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { /* a temp directory that outlives the test is not a test failure */ }
        }
    }

    internal sealed class DocumentPortalHost : IDisposable
    {
        public const int CompanyA = 1, CompanyB = 2;
        public const int CustomerAcme = 100, CustomerBeta = 200;
        public const int PortalAcme = 10, PortalBeta = 20;
        public const int PublisherEmployee = 7;

        public readonly PlatformTestHost Platform = new(companyId: null);
        private readonly StubPortalContext _contexts = new();
        private readonly TempDocumentStorage _storage = new();

        public CrossBuy.Models.Context.CrossDbContext Db => Platform.Seed;

        /// The default identity HOLDS the capability. Tests that need it withheld say so explicitly,
        /// so a reader can tell "denied for lack of capability" from "denied for everything else".
        public void ActAs(int portalUserId = PortalAcme, int customerId = CustomerAcme,
                          int companyId = CompanyA, params string[] capabilities)
            => _contexts.Current = new PortalContext(
                companyId, customerId, portalUserId,
                capabilities.Length > 0 ? capabilities : new[] { PortalCapabilities.ViewDocuments });

        public void ActAsNobody() => _contexts.Current = null;

        public PortalDocumentService Service() => new(Db, _contexts, _storage.Storage);

        public ClientPortalController Controller()
        {
            var options = Microsoft.Extensions.Options.Options.Create(
                new CrossBuy.Models.Communication.CommunicationPlatformOptions());
            var conversations = new CrossBuy.BL.Communication.CommPortalService(
                Db, new CrossBuy.BL.Communication.CommExternalAccessPolicy(Db),
                new CrossBuy.BL.Communication.CommBodyPolicy(options), options);

            // Constructed whole — see PortalConversationUiTests for why a null is not used here.
            var photos = PortalPhotoTest.Service(Db, _contexts, new TempContentRoot());

            return new ClientPortalController(
                new PortalDataService(Db, _contexts), conversations, _contexts, Service(), photos);
        }

        /// A document with real stored bytes and a current version. Returns the document id.
        public long SeedDocument(int companyId, string status = "Active",
                                 string fileName = "invoice.pdf", string contentType = "application/pdf",
                                 string body = "hello-doc1")
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var key = _storage.Storage.StoreAsync(new MemoryStream(bytes), fileName)
                .GetAwaiter().GetResult();

            var doc = new PlatformDocument
            {
                CompanyID = companyId,
                EntityType = "SalesInvoice",
                EntityId = 1,
                Confidentiality = "Internal",
                Status = status,
                IssueDate = DateTime.UtcNow.AddDays(-10),
                CreatedBy = PublisherEmployee,
                CreatedAt = DateTime.UtcNow,
            };
            Db.Set<PlatformDocument>().Add(doc);
            Db.SaveChanges();

            var version = new PlatformDocumentVersion
            {
                CompanyID = companyId,
                DocumentId = doc.Id,
                VersionNo = 1,
                StorageKey = key.Value,
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = bytes.Length,
                UploadedBy = PublisherEmployee,
                UploadedAt = DateTime.UtcNow,
            };
            Db.Set<PlatformDocumentVersion>().Add(version);
            Db.SaveChanges();

            doc.CurrentVersionId = version.Id;
            Db.SaveChanges();
            return doc.Id;
        }

        public long Publish(long documentId, int customerId, int companyId = CompanyA, bool revoked = false)
        {
            var row = new ClientDocumentPublication
            {
                CompanyID = companyId,
                CustomerId = customerId,
                PlatformDocumentId = documentId,
                PublishedByEmployeeId = PublisherEmployee,
                PublishedAt = DateTime.UtcNow,
                RevokedAt = revoked ? DateTime.UtcNow : null,
                RevokedByEmployeeId = revoked ? PublisherEmployee : null,
            };
            Db.Set<ClientDocumentPublication>().Add(row);
            Db.SaveChanges();
            return row.Id;
        }

        public void Dispose() { Platform.Dispose(); _storage.Dispose(); }
    }

    // =============================================================================================
    public class PortalDocumentTests
    {
        private static async Task<string> ReadAll(Stream s)
        {
            using var reader = new StreamReader(s);
            return await reader.ReadToEndAsync();
        }

        // ---- CONTROLS. Read these before trusting any refusal below. -------------------------------

        [Fact]
        public async Task A_published_document_is_listed_for_the_customer_it_was_published_to()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);
            host.ActAs();

            var list = await host.Service().ListAsync();

            var one = Assert.Single(list);
            Assert.Equal(pub, one.PublicationId);
            Assert.Equal("invoice.pdf", one.FileName);
        }

        [Fact] // §23 — the bytes, not merely the metadata
        public async Task The_authorized_current_version_bytes_are_streamed()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA, body: "the-real-content");
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);
            host.ActAs();

            var content = await host.Service().OpenAsync(pub);

            Assert.NotNull(content);
            Assert.Equal("the-real-content", await ReadAll(content!.Content));
            Assert.Equal("invoice.pdf", content.FileName);
            Assert.Equal("application/pdf", content.ContentType);
        }

        [Fact] // the CURRENT version, after a new one supersedes the first
        public async Task A_superseded_version_is_never_served()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA, body: "version-one");
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);

            // A second version arrives and becomes current, exactly as the platform does it.
            using var extra = new TempDocumentStorage();
            var row = host.Db.Set<PlatformDocument>().Single(d => d.Id == doc);
            var v2 = new PlatformDocumentVersion
            {
                CompanyID = DocumentPortalHost.CompanyA, DocumentId = doc, VersionNo = 2,
                StorageKey = new string('a', 32), FileName = "invoice-v2.pdf",
                ContentType = "application/pdf", SizeBytes = 1,
                UploadedBy = 7, UploadedAt = DateTime.UtcNow,
            };
            host.Db.Set<PlatformDocumentVersion>().Add(v2);
            host.Db.SaveChanges();
            row.CurrentVersionId = v2.Id;
            host.Db.SaveChanges();
            host.ActAs();

            // The service follows CurrentVersionId, so it now resolves v2 - whose key names no stored
            // object, and a missing object answers exactly like a refusal. What it must NEVER do is
            // fall back to version one because that one happens to exist.
            Assert.Null(await host.Service().OpenAsync(pub));

            // And the LIST reports the current version's metadata, not the superseded one's.
            var listed = Assert.Single(await host.Service().ListAsync());
            Assert.Equal("invoice-v2.pdf", listed.FileName);
        }

        // ---- CUSTOMER ISOLATION (§19) --------------------------------------------------------------

        [Fact]
        public async Task Customer_A_publication_is_invisible_to_customer_B()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA);
            host.Publish(doc, DocumentPortalHost.CustomerAcme);

            // Same company, valid portal identity, capability held — only the customer differs.
            host.ActAs(portalUserId: DocumentPortalHost.PortalBeta, customerId: DocumentPortalHost.CustomerBeta);

            Assert.Empty(await host.Service().ListAsync());
        }

        [Fact]
        public async Task Customer_B_cannot_download_customer_A_publication_by_id()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);

            host.ActAs(portalUserId: DocumentPortalHost.PortalBeta, customerId: DocumentPortalHost.CustomerBeta);

            // The id is known and guessed correctly; it still yields nothing, because the predicate
            // is on the ROW and not on whether the caller could name it.
            Assert.Null(await host.Service().OpenAsync(pub));
        }

        [Fact]
        public async Task Another_companys_publication_is_refused()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyB);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme, companyId: DocumentPortalHost.CompanyB);

            host.ActAs(companyId: DocumentPortalHost.CompanyA);

            Assert.Empty(await host.Service().ListAsync());
            Assert.Null(await host.Service().OpenAsync(pub));
        }

        [Fact] // company is checked on BOTH rows, so a mismatched pair is refused either way round
        public async Task A_publication_naming_a_document_of_another_company_is_refused()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyB);          // document in B
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme,
                                   companyId: DocumentPortalHost.CompanyA);    // publication claims A
            host.ActAs(companyId: DocumentPortalHost.CompanyA);

            Assert.Empty(await host.Service().ListAsync());
            Assert.Null(await host.Service().OpenAsync(pub));
        }

        // ---- PUBLICATION AUTHORITY -----------------------------------------------------------------

        [Fact]
        public async Task An_unpublished_document_is_invisible()
        {
            using var host = new DocumentPortalHost();
            host.SeedDocument(DocumentPortalHost.CompanyA);   // no publication row at all
            host.ActAs();

            // Same company, same customer scope, capability held. Only the grant is missing, and
            // that alone is the difference between visible and not.
            Assert.Empty(await host.Service().ListAsync());
        }

        [Fact]
        public async Task A_revoked_publication_stops_being_visible_immediately()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);
            host.ActAs();

            Assert.Single(await host.Service().ListAsync());   // it IS visible first

            var row = host.Db.Set<ClientDocumentPublication>().Single(p => p.Id == pub);
            row.RevokedAt = DateTime.UtcNow;
            host.Db.SaveChanges();

            Assert.Empty(await host.Service().ListAsync());
            Assert.Null(await host.Service().OpenAsync(pub));
        }

        [Fact]
        public async Task A_missing_publication_id_is_refused_without_leaking_that_it_is_missing()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);
            host.ActAs(portalUserId: DocumentPortalHost.PortalBeta, customerId: DocumentPortalHost.CustomerBeta);

            // A publication that exists but is not theirs, and one that does not exist at all, must
            // be indistinguishable - otherwise the id becomes an oracle for which grants exist.
            Assert.Null(await host.Service().OpenAsync(pub));
            Assert.Null(await host.Service().OpenAsync(999_999));
        }

        // ---- DOCUMENT STATE (§22) ------------------------------------------------------------------

        [Theory]
        [InlineData("Submitted")]   // received, NOT accepted
        [InlineData("Rejected")]
        [InlineData("Archived")]
        public async Task A_document_that_is_not_active_is_refused_even_when_published(string status)
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA, status: status);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);
            host.ActAs();

            // Publication does NOT override lifecycle. A Submitted document was received, not
            // verified, and handing it to a customer would imply the company stood behind it.
            Assert.Empty(await host.Service().ListAsync());
            Assert.Null(await host.Service().OpenAsync(pub));
        }

        [Fact]
        public async Task A_document_with_no_current_version_is_refused_safely()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);

            var row = host.Db.Set<PlatformDocument>().Single(d => d.Id == doc);
            row.CurrentVersionId = null;
            host.Db.SaveChanges();
            host.ActAs();

            // No exception, no partial answer - the same null every other refusal produces.
            Assert.Empty(await host.Service().ListAsync());
            Assert.Null(await host.Service().OpenAsync(pub));
        }

        [Fact]
        public async Task A_storage_key_that_is_not_the_issued_shape_is_refused_rather_than_sanitised()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);

            // A column holding something path-like must never reach a filesystem call.
            var version = host.Db.Set<PlatformDocumentVersion>().Single(v => v.DocumentId == doc);
            version.StorageKey = @"..\..\web.config";
            host.Db.SaveChanges();
            host.ActAs();

            Assert.Null(await host.Service().OpenAsync(pub));
        }

        // ---- CAPABILITY (§20) ----------------------------------------------------------------------

        [Fact]
        public async Task Login_alone_is_not_enough_without_the_portal_documents_capability()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);

            // A fully valid portal identity for the RIGHT customer, holding the default capabilities
            // — which deliberately do NOT include portal-documents.
            host.ActAs(capabilities: PortalCapabilities.Default);

            Assert.Empty(await host.Service().ListAsync());
            Assert.Null(await host.Service().OpenAsync(pub));
        }

        [Fact]
        public void The_documents_capability_is_not_granted_by_default()
        {
            // If it ever joined Default, every existing portal account would silently acquire
            // document access on deploy day.
            Assert.DoesNotContain(PortalCapabilities.ViewDocuments, PortalCapabilities.Default);
            Assert.Contains(PortalCapabilities.ViewDocuments, PortalCapabilities.All);
        }

        [Fact]
        public async Task An_unresolved_portal_identity_gets_nothing()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA);
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);
            host.ActAsNobody();

            Assert.Empty(await host.Service().ListAsync());
            Assert.Null(await host.Service().OpenAsync(pub));
        }

        // ---- THE ENDPOINTS -------------------------------------------------------------------------

        [Fact]
        public async Task The_download_endpoint_streams_for_an_entitled_customer_and_404s_otherwise()
        {
            using var host = new DocumentPortalHost();
            var doc = host.SeedDocument(DocumentPortalHost.CompanyA, body: "endpoint-bytes");
            var pub = host.Publish(doc, DocumentPortalHost.CustomerAcme);

            host.ActAs();
            var ok = Assert.IsType<FileStreamResult>(await host.Controller().DocumentContent(pub));
            Assert.Equal("application/pdf", ok.ContentType);
            Assert.Equal("invoice.pdf", ok.FileDownloadName);
            Assert.Equal("endpoint-bytes", await ReadAll(ok.FileStream));

            host.ActAs(portalUserId: DocumentPortalHost.PortalBeta, customerId: DocumentPortalHost.CustomerBeta);
            Assert.IsType<NotFoundResult>(await host.Controller().DocumentContent(pub));
        }

        [Fact] // §21 — the portal surface stays bound to the portal policy, never bare [Authorize]
        public void The_portal_controller_is_bound_to_the_portal_policy_only()
        {
            var attributes = typeof(ClientPortalController)
                .GetCustomAttributes(inherit: true)
                .OfType<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .ToList();

            Assert.Contains(attributes, a => a is PortalAuthorizeAttribute);
            Assert.All(attributes, a => Assert.Equal(PortalAuthenticationDefaults.PolicyName, a.Policy));
        }

        [Fact] // §13 — read-only, and the absence is structural
        public void DOC1_exposes_no_write_operation_anywhere()
        {
            var serviceMembers = typeof(IPortalDocumentService).GetMethods().Select(m => m.Name).ToList();
            var actions = typeof(ClientPortalController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("Document", StringComparison.Ordinal))
                .ToList();

            foreach (var forbidden in new[] { "Upload", "Replace", "Delete", "Rename", "Move", "Share", "Publish", "Revoke" })
            {
                Assert.DoesNotContain(serviceMembers, m => m.Contains(forbidden, StringComparison.Ordinal));
                Assert.DoesNotContain(actions, a => a.Name.Contains(forbidden, StringComparison.Ordinal));
            }

            // Both DOC1 actions are GET: there is no mutating portal document endpoint at all.
            Assert.All(actions, a => Assert.NotNull(a.GetCustomAttribute<HttpGetAttribute>()));
            Assert.All(actions, a => Assert.Null(a.GetCustomAttribute<HttpPostAttribute>()));
        }

        [Fact] // §17, §18 — no static path, no disk path, no PrivateFileGate dependency
        public void DOC1_reaches_bytes_only_through_the_storage_abstraction()
        {
            // COMMENTS ARE STRIPPED FIRST. A comment that NAMES /Files in order to record that DOC1
            // deliberately does not touch it is documentation, not a dependency - and an assertion
            // that cannot tell the two apart punishes the explanation rather than the defect. What
            // is under test is the CODE.
            var service = StripComments(File.ReadAllText(Repo("CrossBuy/BL/Portal/PortalDocumentService.cs")));
            var view = StripComments(File.ReadAllText(Repo("CrossBuy/Views/ClientPortal/Documents.cshtml")));

            foreach (var forbidden in new[] { "/Files", "/uploads", "PrivateFileGate", "WebRootPath",
                                              "File.OpenRead", "Directory.", "Path.Combine" })
            {
                Assert.DoesNotContain(forbidden, service, StringComparison.Ordinal);
                Assert.DoesNotContain(forbidden, view, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains("IDocumentStorage", service);

            // The customer-facing DTO carries no key, path or URL - asserted on the TYPE, so a later
            // field cannot quietly reintroduce one.
            var names = typeof(PortalDocumentDto).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
            Assert.DoesNotContain(names, n => n.Contains("storage") || n.Contains("key")
                                              || n.Contains("path") || n.Contains("url") || n.Contains("documentid"));
        }

        [Fact] // §24, §25 — no Task authority, no new event family
        public void DOC1_creates_no_task_and_no_business_event()
        {
            foreach (var path in new[] { "CrossBuy/BL/Portal/PortalDocumentService.cs",
                                         "CrossBuy/Models/Context/Portal/ClientDocumentPublication.cs" })
            {
                var text = File.ReadAllText(Repo(path));
                foreach (var forbidden in new[] { "ITaskService", "TaskOrchestration", "TaskItem",
                                                  "IBusinessEventService", "BusinessEventRecord", "INotificationService" })
                    Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }

        [Fact] // §14 — the deploy script's own contract
        public void The_deploy_script_is_additive_idempotent_and_never_cascades()
        {
            var sql = File.ReadAllText(Repo("CrossBuy/deploy/sql/client_document_publication_doc1.sql"));

            Assert.Contains("ON DELETE NO_ACTION", sql);
            Assert.DoesNotContain("ON DELETE CASCADE", sql, StringComparison.OrdinalIgnoreCase);

            foreach (var line in sql.Split('\n').Select(l => l.Trim())
                         .Where(l => !l.StartsWith("--", StringComparison.Ordinal)))
            {
                var upper = line.ToUpperInvariant();
                foreach (var forbidden in new[] { "DROP ", "TRUNCATE ", "UPDATE ", "DELETE ", "INSERT ", "ALTER TABLE " })
                    Assert.False(upper.StartsWith(forbidden, StringComparison.Ordinal), $"destructive: {line}");
            }

            // Idempotent: both the table and both indexes are guarded.
            Assert.Contains("IF OBJECT_ID('dbo.ClientDocumentPublications', 'U') IS NULL", sql);
            Assert.Equal(2, System.Text.RegularExpressions.Regex
                .Matches(sql, @"IF NOT EXISTS \(SELECT 1 FROM sys\.indexes").Count);
        }

        /// Removes C# line comments and Razor comment blocks. Neither reaches a running program,
        /// so a comment that NAMES a forbidden thing in order to record its absence is
        /// documentation - and this assertion is about the CODE.
        private static string StripComments(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text, "@" + "\\*.*?\\*" + "@", "",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            var kept = new System.Collections.Generic.List<string>();
            foreach (var line in text.Split('\n'))
            {
                int i = line.IndexOf("//", StringComparison.Ordinal);
                kept.Add(i >= 0 ? line.Substring(0, i) : line);
            }
            return string.Join("\n", kept);
        }

        private static string Repo(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "CrossBuy", "BL")))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.NotNull(dir);
            return Path.Combine(dir!, relative.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
