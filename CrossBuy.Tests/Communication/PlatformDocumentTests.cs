using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Central Document Platform — behavioural proof.
    //
    // These run the REAL PlatformDocumentService over a real DbContext and a real in-memory storage
    // implementation. The only thing doubled is the ACCESS RESOLVER, and it is doubled as a
    // *recorder* rather than a yes-machine: it records every question asked, so a test can assert
    // that the service asked at all. A service that forgot to authorize would pass a test that only
    // checked the answer — so these check the question too.
    //
    // The registry is a stub for one reason: Employee currently ships SupportsFiles = false and that
    // flag lives in a file this work stream does not own. Stubbing it lets the capability gate be
    // proven in BOTH directions now, and leaves the production flip as a one-line handoff.
    // =============================================================================================

    internal sealed class InMemoryDocumentStorage : IDocumentStorage
    {
        public readonly Dictionary<string, byte[]> Blobs = new(StringComparer.Ordinal);

        public Task<StorageKey> StoreAsync(Stream content, string? suggestedName = null, CancellationToken ct = default)
        {
            var key = StorageKey.New();
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            Blobs[key.Value] = ms.ToArray();
            return Task.FromResult(key);
        }
        public Task<Stream?> OpenReadAsync(StorageKey key, CancellationToken ct = default)
            => Task.FromResult<Stream?>(Blobs.TryGetValue(key.Value, out var b) ? new MemoryStream(b) : null);
        public Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default)
            => Task.FromResult(Blobs.ContainsKey(key.Value));
        public Task<bool> DeleteAsync(StorageKey key, CancellationToken ct = default)
            => Task.FromResult(Blobs.Remove(key.Value));
    }

    internal sealed class RecordingAccessResolver : IDocumentAccessResolver
    {
        public readonly List<(string EntityType, int EntityId, DocumentAction Action, int CompanyId, string? Confidentiality)> Asked = new();

        /// Returns the decision for a question. Default allows; a test narrows it to model a module
        /// that says no, a company mismatch, or a confidentiality refusal.
        public Func<string, int, DocumentAction, int, string?, DocumentAccessDecision> Decide { get; set; }
            = (_, _, _, _, _) => new DocumentAccessDecision(true, DocumentAccessReasons.Allowed);

        public Task<DocumentAccessDecision> AuthorizeAsync(BusinessContext? context, DocumentOwnerRef owner,
            DocumentAction action, int documentCompanyId, string? confidentiality = null, CancellationToken ct = default)
        {
            Asked.Add((owner.EntityType, owner.EntityId, action, documentCompanyId, confidentiality));
            if (context == null || context.CompanyId <= 0)
                return Task.FromResult(new DocumentAccessDecision(false, DocumentAccessReasons.CompanyUnresolved));
            return Task.FromResult(Decide(owner.EntityType, owner.EntityId, action, documentCompanyId, confidentiality));
        }
    }

    internal sealed class StubRegistry : IEntityRegistry
    {
        public HashSet<string> FilesEnabled { get; } = new(StringComparer.Ordinal) { "Employee" };

        public bool TryGetDefinition(string? entityCode, out EntityDefinition? definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(entityCode)) return false;
            definition = new EntityDefinition
            {
                Code = entityCode, DisplayNameAr = entityCode, DisplayNameEn = entityCode,
                Module = "Test", Icon = "i", Color = "c", PermissionScope = "hr",
                SupportsFiles = FilesEnabled.Contains(entityCode),
            };
            return true;
        }
        public IReadOnlyList<EntityDefinition> GetDefinitions() => Array.Empty<EntityDefinition>();
        public EntityDefinition GetDefinition(string entityCode)
            => TryGetDefinition(entityCode, out var d) && d != null ? d : throw new EntityCodeNotRegisteredException(entityCode);
        public bool IsValid(string? entityCode) => TryGetDefinition(entityCode, out _);
        public Task<List<EntitySearchResult>> SearchAsync(string entityCode, string? query, BusinessContext context, CancellationToken ct = default)
            => Task.FromResult(new List<EntitySearchResult>());
        public Task<EntityResolveResult> ResolveAsync(string entityCode, int entityId, BusinessContext context, CancellationToken ct = default)
            => throw new NotSupportedException();
        public string? BuildUrl(string entityCode, int entityId) => null;
    }

    internal sealed class DocHost : IDisposable
    {
        public readonly CommunicationTestHost Platform = new();
        public readonly InMemoryDocumentStorage Storage = new();
        public readonly RecordingAccessResolver Access = new();
        public readonly StubRegistry Registry = new();
        public readonly SwitchableContext Contexts = new();

        public const int CompanyA = 1, CompanyB = 2, Alice = 11, Bob = 12;

        public DocHost() => ActAs(Alice, CompanyA);

        public void ActAs(int employeeId, int companyId)
            => Contexts.Current = new BusinessContext { CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId };
        public void ActAsNobody() => Contexts.Current = null;

        public PlatformDocumentService Service()
            => new(Platform.Db, Contexts, Access, Storage, Registry);

        public static Stream Bytes(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

        public DocumentUploadRequest Upload(string entityType = "Employee", int entityId = 500,
            long? typeId = null, string? confidentiality = null, string? metadata = null,
            DateTime? issue = null, DateTime? expiry = null, string fileName = "passport.pdf")
            => new()
            {
                EntityType = entityType, EntityId = entityId, DocumentTypeId = typeId,
                Confidentiality = confidentiality, Metadata = metadata,
                IssueDate = issue, ExpiryDate = expiry,
                FileName = fileName, ContentType = "application/pdf",
            };

        public long SeedType(string code, string appliesTo, bool requiresExpiry = false,
            string? schema = null, string? allowedExt = null, string conf = "Internal", int? companyId = null)
        {
            var t = new PlatformDocumentType
            {
                CompanyID = companyId, Code = code, NameAr = code, NameEn = code,
                AppliesToEntityTypes = appliesTo, RequiresExpiryDate = requiresExpiry,
                MetadataSchema = schema, AllowedExtensions = allowedExt,
                DefaultConfidentiality = conf, IsActive = true,
            };
            Platform.Db.Set<PlatformDocumentType>().Add(t);
            Platform.Db.SaveChanges();
            return t.Id;
        }

        public void Dispose() => Platform.Dispose();
    }

    public class PlatformDocumentTests
    {
        // ---- the happy path, and proof the question was asked -----------------------------------
        [Fact]
        public async Task An_authorized_upload_stores_the_document_its_first_version_and_the_blob()
        {
            using var h = new DocHost();
            var result = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("V1 CONTENT"));

            Assert.True(result.Ok, result.ReasonCode);
            var doc = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync();
            var version = await h.Platform.Db.Set<PlatformDocumentVersion>().AsNoTracking().SingleAsync();

            Assert.Equal(DocHost.CompanyA, doc.CompanyID);
            Assert.Equal("Employee", doc.EntityType);
            Assert.Equal(500, doc.EntityId);
            Assert.Equal(version.Id, doc.CurrentVersionId);
            Assert.Equal(1, version.VersionNo);
            Assert.Single(h.Storage.Blobs);

            // The service ASKED, with the upload verb and the resolved company.
            Assert.Contains(h.Access.Asked, a => a.Action == DocumentAction.Upload && a.CompanyId == DocHost.CompanyA);
        }

        [Fact]
        public async Task The_owning_module_can_refuse_and_then_nothing_is_written()
        {
            using var h = new DocHost();
            h.Access.Decide = (_, _, _, _, _) => new DocumentAccessDecision(false, DocumentAccessReasons.ModuleDenied);

            var result = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("SHOULD NOT LAND"));

            Assert.False(result.Ok);
            Assert.Equal(DocumentAccessReasons.ModuleDenied, result.ReasonCode);
            Assert.Empty(await h.Platform.Db.Set<PlatformDocument>().ToListAsync());
        }

        // ---- company isolation ------------------------------------------------------------------
        [Fact]
        public async Task Another_company_cannot_read_the_document_even_with_its_exact_id()
        {
            using var h = new DocHost();
            var created = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("COMPANY-A-SECRET"));
            Assert.True(created.Ok);

            h.ActAs(DocHost.Bob, DocHost.CompanyB);          // same id, different company
            Assert.Null(await h.Service().OpenCurrentAsync(created.DocumentId));
            Assert.Empty(await h.Service().HistoryAsync(created.DocumentId));
            Assert.Empty(await h.Service().ListForEntityAsync("Employee", 500));
        }

        [Fact]
        public async Task A_foreign_document_and_a_missing_document_refuse_identically()
        {
            using var h = new DocHost();
            var created = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("x"));
            h.ActAs(DocHost.Bob, DocHost.CompanyB);

            var foreign = await h.Service().ReplaceAsync(created.DocumentId, DocHost.Bytes("y"), "f.pdf", "application/pdf", null);
            var missing = await h.Service().ReplaceAsync(999999, DocHost.Bytes("y"), "f.pdf", "application/pdf", null);

            Assert.False(foreign.Ok);
            Assert.False(missing.Ok);
            Assert.Equal(missing.ReasonCode, foreign.ReasonCode);   // indistinguishable, by construction
        }

        [Fact]
        public async Task An_unresolved_business_context_fails_closed_on_every_operation()
        {
            using var h = new DocHost();
            var created = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("x"));
            h.ActAsNobody();
            var svc = h.Service();

            Assert.False((await svc.UploadAsync(h.Upload(), DocHost.Bytes("y"))).Ok);
            Assert.False((await svc.ReplaceAsync(created.DocumentId, DocHost.Bytes("y"), "f.pdf", "application/pdf", null)).Ok);
            Assert.Null(await svc.OpenCurrentAsync(created.DocumentId));
            Assert.Empty(await svc.ListForEntityAsync("Employee", 500));
            Assert.Empty(await svc.HistoryAsync(created.DocumentId));
        }

        [Fact]
        public async Task A_company_mismatch_reported_by_the_spine_refuses_the_upload()
        {
            // The spine resolves the OWNING record's company; when it disagrees with the document's,
            // the relation is a mismatch and must not be created.
            using var h = new DocHost();
            h.Access.Decide = (_, _, _, _, _) => new DocumentAccessDecision(false, DocumentAccessReasons.CompanyMismatch);

            var result = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("x"));

            Assert.Equal(DocumentAccessReasons.CompanyMismatch, result.ReasonCode);
            Assert.Empty(await h.Platform.Db.Set<PlatformDocument>().ToListAsync());
        }

        // ---- StorageKey is not a capability -----------------------------------------------------
        [Fact]
        public async Task Holding_a_StorageKey_grants_nothing()
        {
            using var h = new DocHost();
            var created = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("COMPANY-A-SECRET"));
            var key = (await h.Platform.Db.Set<PlatformDocumentVersion>().AsNoTracking().SingleAsync()).StorageKey;

            // The key is real and the blob is really there...
            Assert.True(h.Storage.Blobs.ContainsKey(key));

            // ...and it buys nothing: the service exposes no key-addressed read at all, and the
            // document-addressed read still refuses for another company.
            h.ActAs(DocHost.Bob, DocHost.CompanyB);
            Assert.Null(await h.Service().OpenCurrentAsync(created.DocumentId));

            var api = typeof(IPlatformDocumentService).GetMethods();
            Assert.DoesNotContain(api, m => m.GetParameters().Any(p => p.ParameterType == typeof(StorageKey)));
            Assert.DoesNotContain(api, m => m.Name.Contains("StorageKey", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_SAME_COMPANY_caller_the_module_refuses_cannot_download()
        {
            // The gap a mutation found: the cross-company tests pass because of the company predicate on
            // the load, so on their own they never exercise the download authorization at all. Deleting
            // that authorization left them all green. This is the test that goes red instead.
            using var h = new DocHost();
            var created = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("PAYROLL"));
            Assert.True(created.Ok);

            h.Access.Decide = (_, _, action, _, _) =>
                action == DocumentAction.Download
                    ? new DocumentAccessDecision(false, DocumentAccessReasons.ModuleDenied)
                    : new DocumentAccessDecision(true, DocumentAccessReasons.Allowed);

            Assert.Null(await h.Service().OpenCurrentAsync(created.DocumentId));
            Assert.Null(await h.Service().OpenVersionAsync(created.DocumentId, 1));
        }

        [Fact]
        public async Task A_confidential_document_is_not_downloadable_by_someone_refused_that_tier()
        {
            using var h = new DocHost();
            var created = await h.Service().UploadAsync(
                h.Upload(confidentiality: DocumentConfidentiality.Confidential), DocHost.Bytes("PAYROLL"));
            Assert.True(created.Ok);

            // May view the employee; may not see confidential material about them.
            h.Access.Decide = (_, _, _, _, conf) =>
                conf == DocumentConfidentiality.Confidential
                    ? new DocumentAccessDecision(false, DocumentAccessReasons.ConfidentialityDenied)
                    : new DocumentAccessDecision(true, DocumentAccessReasons.Allowed);

            Assert.Null(await h.Service().OpenCurrentAsync(created.DocumentId));
            Assert.Empty(await h.Service().HistoryAsync(created.DocumentId));
        }

        // ---- versioning --------------------------------------------------------------------------
        [Fact]
        public async Task Replacing_adds_V2_moves_the_pointer_and_keeps_V1_readable()
        {
            using var h = new DocHost();
            var created = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("V1 CONTENT"));
            var replaced = await h.Service().ReplaceAsync(created.DocumentId, DocHost.Bytes("V2 CONTENT"),
                "passport-2030.pdf", "application/pdf", "renewed");

            Assert.True(replaced.Ok, replaced.ReasonCode);

            var doc = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync();
            Assert.Equal(replaced.VersionId, doc.CurrentVersionId);

            var history = await h.Service().HistoryAsync(created.DocumentId);
            Assert.Equal(2, history.Count);
            Assert.Equal(new[] { 1, 2 }, history.Select(v => v.VersionNo).ToArray());
            Assert.Equal("renewed", history[1].Reason);
            Assert.Equal(history[0].Id, history[1].ReplacesVersionId);   // the chain is walkable

            // The current read serves V2...
            var current = await h.Service().OpenCurrentAsync(created.DocumentId);
            Assert.Equal("V2 CONTENT", Read(current!));
            Assert.Equal("passport-2030.pdf", current!.FileName);

            // ...and V1 is STILL READABLE. Nothing was overwritten.
            var v1 = await h.Service().OpenVersionAsync(created.DocumentId, 1);
            Assert.Equal("V1 CONTENT", Read(v1!));
            Assert.Equal(2, h.Storage.Blobs.Count);                     // two distinct blobs on disk
        }

        [Fact]
        public async Task Version_history_is_refused_to_another_company()
        {
            using var h = new DocHost();
            var created = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("V1"));
            await h.Service().ReplaceAsync(created.DocumentId, DocHost.Bytes("V2"), "f.pdf", "application/pdf", null);

            h.ActAs(DocHost.Bob, DocHost.CompanyB);
            Assert.Null(await h.Service().OpenVersionAsync(created.DocumentId, 1));
            Assert.Empty(await h.Service().HistoryAsync(created.DocumentId));
        }

        // ---- confidentiality ---------------------------------------------------------------------
        [Fact]
        public async Task Confidentiality_is_carried_to_the_resolver_and_can_refuse_a_single_document()
        {
            using var h = new DocHost();
            await h.Service().UploadAsync(h.Upload(confidentiality: "Internal"), DocHost.Bytes("ordinary"));
            await h.Service().UploadAsync(h.Upload(confidentiality: "Confidential"), DocHost.Bytes("payroll"));

            // A caller who may view the employee but not confidential material about them.
            h.Access.Decide = (_, _, _, _, conf) =>
                conf == DocumentConfidentiality.Confidential
                    ? new DocumentAccessDecision(false, DocumentAccessReasons.ConfidentialityDenied)
                    : new DocumentAccessDecision(true, DocumentAccessReasons.Allowed);

            var listed = await h.Service().ListForEntityAsync("Employee", 500);

            Assert.Single(listed);
            Assert.Equal(DocumentConfidentiality.Internal, listed[0].Confidentiality);
        }

        [Fact]
        public async Task An_unknown_confidentiality_tier_is_refused()
        {
            using var h = new DocHost();
            var result = await h.Service().UploadAsync(h.Upload(confidentiality: "TopSecret"), DocHost.Bytes("x"));
            Assert.Equal(DocumentAccessReasons.UnknownConfidentiality, result.ReasonCode);
        }

        [Fact]
        public async Task A_type_default_supplies_the_tier_when_the_caller_names_none()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", conf: DocumentConfidentiality.Confidential);

            var result = await h.Service().UploadAsync(h.Upload(typeId: typeId), DocHost.Bytes("x"));

            Assert.True(result.Ok, result.ReasonCode);
            var doc = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync();
            Assert.Equal(DocumentConfidentiality.Confidential, doc.Confidentiality);
        }

        // ---- document types -----------------------------------------------------------------------
        [Fact]
        public async Task A_type_may_not_be_attached_to_a_family_it_does_not_apply_to()
        {
            using var h = new DocHost();
            h.Registry.FilesEnabled.Add("PurchaseInvoice");
            var passport = h.SeedType("PASSPORT", "Employee");

            var ok = await h.Service().UploadAsync(h.Upload(typeId: passport), DocHost.Bytes("x"));
            var wrong = await h.Service().UploadAsync(
                h.Upload(entityType: "PurchaseInvoice", entityId: 900, typeId: passport), DocHost.Bytes("x"));

            Assert.True(ok.Ok);
            Assert.Equal("type_entity_mismatch", wrong.ReasonCode);
        }

        [Fact]
        public async Task A_type_that_requires_an_expiry_date_refuses_a_document_without_one()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", requiresExpiry: true);

            var without = await h.Service().UploadAsync(h.Upload(typeId: typeId), DocHost.Bytes("x"));
            var with = await h.Service().UploadAsync(
                h.Upload(typeId: typeId, expiry: DateTime.UtcNow.AddYears(5)), DocHost.Bytes("x"));

            Assert.Equal("expiry_date_required", without.ReasonCode);
            Assert.True(with.Ok, with.ReasonCode);
        }

        [Fact]
        public async Task Another_companys_private_type_is_invisible()
        {
            using var h = new DocHost();
            var theirs = h.SeedType("THEIR_TYPE", "Employee", companyId: DocHost.CompanyB);

            var result = await h.Service().UploadAsync(h.Upload(typeId: theirs), DocHost.Bytes("x"));

            Assert.Equal("unknown_document_type", result.ReasonCode);
        }

        [Fact]
        public async Task A_disallowed_file_extension_is_refused()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", allowedExt: ".pdf,.png");

            var bad = await h.Service().UploadAsync(h.Upload(typeId: typeId, fileName: "passport.exe"), DocHost.Bytes("x"));
            var good = await h.Service().UploadAsync(h.Upload(typeId: typeId, fileName: "passport.pdf"), DocHost.Bytes("x"));

            Assert.Equal("file_type_refused", bad.ReasonCode);
            Assert.True(good.Ok, good.ReasonCode);
        }

        // ---- metadata -------------------------------------------------------------------------------
        [Fact]
        public async Task Metadata_must_be_a_json_object_and_must_satisfy_the_types_required_keys()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee",
                schema: "{\"issuingCountry\":\"required\",\"pages\":\"optional\"}");

            var notJson = await h.Service().UploadAsync(h.Upload(typeId: typeId, metadata: "not json"), DocHost.Bytes("x"));
            var notObject = await h.Service().UploadAsync(h.Upload(typeId: typeId, metadata: "[1,2]"), DocHost.Bytes("x"));
            var missingKey = await h.Service().UploadAsync(h.Upload(typeId: typeId, metadata: "{\"pages\":32}"), DocHost.Bytes("x"));
            var valid = await h.Service().UploadAsync(
                h.Upload(typeId: typeId, metadata: "{\"issuingCountry\":\"KW\"}"), DocHost.Bytes("x"));

            Assert.Equal("metadata_invalid_json", notJson.ReasonCode);
            Assert.Equal("metadata_not_an_object", notObject.ReasonCode);
            Assert.Equal("metadata_missing_issuingCountry", missingKey.ReasonCode);
            Assert.True(valid.Ok, valid.ReasonCode);

            var stored = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync();
            Assert.Contains("KW", stored.Metadata);
        }

        // ---- the registry capability gate ------------------------------------------------------------
        [Fact]
        public async Task A_family_whose_registry_definition_does_not_carry_files_is_refused()
        {
            // Both directions, because a gate that only ever says yes is not a gate. This is also the
            // exact production switch: Employee ships SupportsFiles = false today.
            using var h = new DocHost();

            var enabled = await h.Service().UploadAsync(h.Upload(entityType: "Employee"), DocHost.Bytes("x"));
            Assert.True(enabled.Ok, enabled.ReasonCode);

            h.Registry.FilesEnabled.Remove("Employee");
            var disabled = await h.Service().UploadAsync(h.Upload(entityType: "Employee"), DocHost.Bytes("x"));

            Assert.False(disabled.Ok);
            Assert.Equal(DocumentAccessReasons.UnknownEntityType, disabled.ReasonCode);
            Assert.Single(await h.Platform.Db.Set<PlatformDocument>().ToListAsync());
        }

        // ---- expiry semantics --------------------------------------------------------------------------
        [Fact]
        public async Task Expiry_is_stored_as_an_indexed_column_so_it_can_be_swept_per_company()
        {
            using var h = new DocHost();
            var soon = DateTime.UtcNow.AddDays(30);
            var far = DateTime.UtcNow.AddYears(5);
            await h.Service().UploadAsync(h.Upload(expiry: soon), DocHost.Bytes("expiring"));
            await h.Service().UploadAsync(h.Upload(entityId: 501, expiry: far), DocHost.Bytes("later"));
            await h.Service().UploadAsync(h.Upload(entityId: 502), DocHost.Bytes("no expiry"));

            // The shape a platform expiry worker would use: company-scoped, window-bounded, no scan of
            // documents that never expire. Asserted against the store rather than a worker, because no
            // worker ships in this batch.
            var window = DateTime.UtcNow.AddDays(60);
            var due = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking()
                .Where(d => d.CompanyID == DocHost.CompanyA && d.ExpiryDate != null
                            && d.ExpiryDate <= window && d.Status == "Active")
                .ToListAsync();

            Assert.Single(due);
            Assert.Equal(500, due[0].EntityId);
        }

        // ---- listing authorizes before it reads ----------------------------------------------------------
        [Fact]
        public async Task Listing_asks_for_authorization_before_returning_anything()
        {
            using var h = new DocHost();
            await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("x"));
            h.Access.Asked.Clear();
            h.Access.Decide = (_, _, _, _, _) => new DocumentAccessDecision(false, DocumentAccessReasons.ModuleDenied);

            var listed = await h.Service().ListForEntityAsync("Employee", 500);

            Assert.Empty(listed);
            Assert.Contains(h.Access.Asked, a => a.Action == DocumentAction.View);   // it asked
        }

        private static string Read(DocumentContent content)
        {
            using var reader = new StreamReader(content.Content);
            return reader.ReadToEnd();
        }
    }
}
