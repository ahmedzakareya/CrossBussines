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

        /// Lets a test model the filesystem refusing, which is the other half of the
        /// "database and disk are not one transaction" problem.
        public bool FailNextStore;

        /// Runs immediately AFTER a successful store. A test uses it to break the database at the one
        /// moment that matters: the bytes are on disk and the commit has not happened yet. Modelling it
        /// any earlier just fails a read and writes nothing, which proves nothing about compensation.
        public Action? OnStored;

        public Task<StorageKey> StoreAsync(Stream content, string? suggestedName = null, CancellationToken ct = default)
        {
            if (FailNextStore) { FailNextStore = false; throw new IOException("storage unavailable"); }
            var key = StorageKey.New();
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            Blobs[key.Value] = ms.ToArray();
            OnStored?.Invoke();
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
            string? schema = null, string? allowedExt = null, string conf = "Internal", int? companyId = null,
            bool selfService = false, long? maxBytes = null)
        {
            var t = new PlatformDocumentType
            {
                CompanyID = companyId, Code = code, NameAr = code, NameEn = code,
                AppliesToEntityTypes = appliesTo, RequiresExpiryDate = requiresExpiry,
                MetadataSchema = schema, AllowedExtensions = allowedExt,
                DefaultConfidentiality = conf, IsActive = true, SelfServiceAllowed = selfService, MaxSizeBytes = maxBytes,
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


        // =========================================================================================
        // BATCH 2 — secure delivery, validity, and the self-service policy surface.
        // =========================================================================================

        // ---- secure delivery: the service side the controller depends on -----------------------
        [Fact]
        public async Task A_version_number_belonging_to_another_document_is_not_found()
        {
            // The version is addressed by its NUMBER WITHIN a document, so a number that exists in a
            // different document cannot be borrowed. Two documents both have a V1; asking document A
            // for V1 must never serve document B's bytes.
            using var h = new DocHost();
            var a = await h.Service().UploadAsync(h.Upload(entityId: 500), DocHost.Bytes("DOC-A-V1"));
            var b = await h.Service().UploadAsync(h.Upload(entityId: 501), DocHost.Bytes("DOC-B-V1"));
            Assert.True(a.Ok && b.Ok);

            var fromA = await h.Service().OpenVersionAsync(a.DocumentId, 1);
            Assert.Equal("DOC-A-V1", ReadAll(fromA!));

            // Document A has no V2; B's second version must not leak through A's route.
            await h.Service().ReplaceAsync(b.DocumentId, DocHost.Bytes("DOC-B-V2"), "b2.pdf", "application/pdf", null);
            Assert.Null(await h.Service().OpenVersionAsync(a.DocumentId, 2));
        }

        [Fact]
        public async Task An_archived_document_is_no_longer_served_or_counted()
        {
            using var h = new DocHost();
            var created = await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("x"));
            var doc = await h.Platform.Db.Set<PlatformDocument>().SingleAsync();
            doc.Status = "Archived";
            await h.Platform.Db.SaveChangesAsync();

            // Listing drops it, and it is not a valid document any more.
            Assert.Empty(await h.Service().ListForEntityAsync("Employee", 500));
            Assert.True(created.Ok);
        }

        [Fact]
        public async Task Stored_metadata_never_carries_a_path_or_a_key_out_to_a_caller()
        {
            using var h = new DocHost();
            await h.Service().UploadAsync(h.Upload(), DocHost.Bytes("x"));

            var listed = Assert.Single(await h.Service().ListForEntityAsync("Employee", 500));

            // The listing DTO has no key and no path property at all - the strongest form of "never
            // returned" is "cannot be returned".
            var props = listed.GetType().GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(props, n => n.Contains("Storage", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(props, n => n.Contains("Path", StringComparison.OrdinalIgnoreCase));
        }

        // ---- validity ---------------------------------------------------------------------------
        [Fact]
        public async Task A_current_active_document_of_the_right_type_is_valid()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            var created = await h.Service().UploadAsync(
                h.Upload(typeId: typeId, expiry: DateTime.UtcNow.AddYears(5)), DocHost.Bytes("x"));

            var validity = await h.Service().FindValidDocumentAsync("Employee", 500, typeId);

            Assert.True(validity.IsValid, validity.ReasonCode);
            Assert.Equal(created.DocumentId, validity.DocumentId);
            Assert.True(await h.Service().HasValidDocumentAsync("Employee", 500, typeId));
        }

        [Fact]
        public async Task Validity_is_refused_for_the_wrong_company_entity_type_entity_id_or_document_type()
        {
            using var h = new DocHost();
            var passport = h.SeedType("PASSPORT", "Employee");
            var visa = h.SeedType("VISA", "Employee");
            h.Registry.FilesEnabled.Add("Task");
            await h.Service().UploadAsync(h.Upload(typeId: passport), DocHost.Bytes("x"));

            Assert.False((await h.Service().FindValidDocumentAsync("Employee", 500, visa)).IsValid);      // wrong type
            Assert.False((await h.Service().FindValidDocumentAsync("Employee", 999, passport)).IsValid);  // wrong id
            Assert.False((await h.Service().FindValidDocumentAsync("Task", 500, passport)).IsValid);      // wrong family

            h.ActAs(DocHost.Bob, DocHost.CompanyB);
            Assert.False((await h.Service().FindValidDocumentAsync("Employee", 500, passport)).IsValid);  // wrong company
        }

        [Fact]
        public async Task An_expired_document_is_not_valid_and_says_so()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await h.Service().UploadAsync(
                h.Upload(typeId: typeId, expiry: DateTime.UtcNow.AddDays(-1)), DocHost.Bytes("x"));

            var validity = await h.Service().FindValidDocumentAsync("Employee", 500, typeId);

            Assert.False(validity.IsValid);
            // "Expired" rather than "not_found": onboarding needs to tell "never supplied" from
            // "needs renewing", and those are different conversations with the employee.
            Assert.Equal(DocumentValidityReasons.Expired, validity.ReasonCode);
        }

        [Fact]
        public async Task A_document_with_no_current_version_is_not_valid()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await h.Service().UploadAsync(h.Upload(typeId: typeId), DocHost.Bytes("x"));

            var doc = await h.Platform.Db.Set<PlatformDocument>().SingleAsync();
            doc.CurrentVersionId = null;                    // a row with no bytes is not a document
            await h.Platform.Db.SaveChangesAsync();

            var validity = await h.Service().FindValidDocumentAsync("Employee", 500, typeId);
            Assert.False(validity.IsValid);
            Assert.Equal(DocumentValidityReasons.NoCurrentVersion, validity.ReasonCode);
        }

        [Fact]
        public async Task A_submitted_document_is_received_but_NOT_yet_valid()
        {
            // Submission is not approval. An employee filing a passport must not tick an onboarding
            // requirement on its own - something has to move it to Active first.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await h.Service().UploadAsync(h.Upload(typeId: typeId), DocHost.Bytes("x"));

            var doc = await h.Platform.Db.Set<PlatformDocument>().SingleAsync();
            doc.Status = "Submitted";
            await h.Platform.Db.SaveChangesAsync();

            var validity = await h.Service().FindValidDocumentAsync("Employee", 500, typeId);
            Assert.False(validity.IsValid);
            Assert.Equal(DocumentValidityReasons.NotActive, validity.ReasonCode);
        }

        [Fact]
        public async Task An_unauthorized_caller_cannot_probe_validity_and_gets_the_SAME_answer_as_absence()
        {
            // The important one. "Employee 500 has a passport" is itself sensitive, so a refusal must be
            // indistinguishable from "there is none" - otherwise the query is an oracle for what people
            // hold, which is exactly what a confidentiality tier exists to prevent.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await h.Service().UploadAsync(h.Upload(typeId: typeId), DocHost.Bytes("x"));

            h.Access.Decide = (_, _, _, _, _) => new DocumentAccessDecision(false, DocumentAccessReasons.ModuleDenied);
            var refused = await h.Service().FindValidDocumentAsync("Employee", 500, typeId);
            var absent = await h.Service().FindValidDocumentAsync("Employee", 777, typeId);

            Assert.False(refused.IsValid);
            Assert.Equal(absent.ReasonCode, refused.ReasonCode);
            Assert.Equal(0, refused.DocumentId);            // and it reveals no id either
        }

        [Fact]
        public async Task Validity_is_refused_at_the_ENTITY_gate_even_when_the_document_tier_would_allow_it()
        {
            // Isolates the entity-level authorization from the per-document one. The earlier probe test
            // denies every question, so it passed even with the entity gate deleted - a mutation proved
            // it. Here only the entity-level ask (the one with no confidentiality) is refused, so if that
            // gate goes missing the caller learns the employee holds a passport.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await h.Service().UploadAsync(h.Upload(typeId: typeId), DocHost.Bytes("x"));

            h.Access.Decide = (_, _, _, _, conf) =>
                conf == null
                    ? new DocumentAccessDecision(false, DocumentAccessReasons.ModuleDenied)   // may not see the employee
                    : new DocumentAccessDecision(true, DocumentAccessReasons.Allowed);        // tier itself is fine

            var validity = await h.Service().FindValidDocumentAsync("Employee", 500, typeId);

            Assert.False(validity.IsValid);
            Assert.Equal(DocumentValidityReasons.NotFound, validity.ReasonCode);
            Assert.Equal(0, validity.DocumentId);
        }

        [Fact]
        public async Task A_confidential_document_is_not_reported_as_held_to_someone_refused_that_tier()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", conf: DocumentConfidentiality.Confidential);
            await h.Service().UploadAsync(h.Upload(typeId: typeId), DocHost.Bytes("x"));

            h.Access.Decide = (_, _, _, _, conf) =>
                conf == DocumentConfidentiality.Confidential
                    ? new DocumentAccessDecision(false, DocumentAccessReasons.ConfidentialityDenied)
                    : new DocumentAccessDecision(true, DocumentAccessReasons.Allowed);

            Assert.False((await h.Service().FindValidDocumentAsync("Employee", 500, typeId)).IsValid);
        }

        [Fact]
        public async Task Validity_fails_closed_when_the_business_context_is_unresolved()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await h.Service().UploadAsync(h.Upload(typeId: typeId), DocHost.Bytes("x"));

            h.ActAsNobody();
            Assert.False((await h.Service().FindValidDocumentAsync("Employee", 500, typeId)).IsValid);
            Assert.False(await h.Service().HasValidDocumentAsync("Employee", 500, typeId));
        }

        [Fact]
        public async Task The_newest_document_decides_so_a_renewal_supersedes_a_lapsed_one()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await h.Service().UploadAsync(
                h.Upload(typeId: typeId, expiry: DateTime.UtcNow.AddDays(-1)), DocHost.Bytes("old"));
            var fresh = await h.Service().UploadAsync(
                h.Upload(typeId: typeId, expiry: DateTime.UtcNow.AddYears(2)), DocHost.Bytes("new"));

            var validity = await h.Service().FindValidDocumentAsync("Employee", 500, typeId);

            Assert.True(validity.IsValid, validity.ReasonCode);
            Assert.Equal(fresh.DocumentId, validity.DocumentId);
        }

        // ---- self-service policy surface ---------------------------------------------------------
        [Fact]
        public void A_document_type_is_HR_only_until_somebody_deliberately_opens_it()
        {
            // The safe reading of silence. A type nobody has classified must not be self-servable,
            // because the failure mode of the other default is an employee filing their own appraisal.
            var fresh = new PlatformDocumentType
            {
                Code = "UNCLASSIFIED", NameAr = "x", NameEn = "x", AppliesToEntityTypes = "Employee",
            };
            Assert.False(fresh.SelfServiceAllowed);
        }

        [Fact]
        public async Task The_self_service_flag_is_persisted_per_type_and_defaults_off()
        {
            using var h = new DocHost();
            var hrOnly = h.SeedType("DISCIPLINARY", "Employee");
            var openToStaff = h.SeedType("PASSPORT", "Employee", selfService: true);

            var stored = await h.Platform.Db.Set<PlatformDocumentType>().AsNoTracking().ToListAsync();
            Assert.False(stored.Single(t => t.Id == hrOnly).SelfServiceAllowed);
            Assert.True(stored.Single(t => t.Id == openToStaff).SelfServiceAllowed);
        }

        private static string ReadAll(DocumentContent content)
        {
            using var reader = new StreamReader(content.Content);
            return reader.ReadToEnd();
        }


        // =========================================================================================
        // BATCH 3 — self-service submission, verification, compensation, and a fixed clock.
        // =========================================================================================

        /// A clock a test can stand still. TimeProvider is .NET's own abstraction, so this is the whole
        /// of the "time framework" — one override.
        private sealed class FixedClock : TimeProvider
        {
            private readonly DateTimeOffset _now;
            public FixedClock(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
        }

        private static readonly DateTime Today = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

        private static PlatformDocumentService ServiceAt(DocHost h, DateTime utc)
            => new(h.Platform.Db, h.Contexts, h.Access, h.Storage, h.Registry, new FixedClock(utc));

        // ---- the clock seam --------------------------------------------------------------------
        [Fact]
        public async Task A_document_expiring_TODAY_is_still_valid()
        {
            // The boundary the inline DateTime.UtcNow made untestable. A passport does not stop being a
            // passport at midnight of its printed date, and refusing somebody on the last day they were
            // entitled to be accepted is the kind of bug nobody notices until it happens to a person.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await ServiceAt(h, Today).UploadAsync(
                h.Upload(typeId: typeId, expiry: Today.Date), DocHost.Bytes("x"));

            var validity = await ServiceAt(h, Today).FindValidDocumentAsync("Employee", 500, typeId);

            Assert.True(validity.IsValid, validity.ReasonCode);
        }

        [Fact]
        public async Task A_document_that_expired_YESTERDAY_is_expired()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await ServiceAt(h, Today).UploadAsync(
                h.Upload(typeId: typeId, expiry: Today.Date.AddDays(-1)), DocHost.Bytes("x"));

            var validity = await ServiceAt(h, Today).FindValidDocumentAsync("Employee", 500, typeId);

            Assert.False(validity.IsValid);
            Assert.Equal(DocumentValidityReasons.Expired, validity.ReasonCode);
        }

        [Fact]
        public async Task The_SAME_document_becomes_expired_only_when_the_clock_moves_past_it()
        {
            // One document, two clocks. Nothing about the row changes, which is what proves the rule
            // is reading the clock rather than something stored at write time.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee");
            await ServiceAt(h, Today).UploadAsync(
                h.Upload(typeId: typeId, expiry: Today.Date), DocHost.Bytes("x"));

            Assert.True((await ServiceAt(h, Today).FindValidDocumentAsync("Employee", 500, typeId)).IsValid);
            Assert.False((await ServiceAt(h, Today.AddDays(1)).FindValidDocumentAsync("Employee", 500, typeId)).IsValid);
        }

        // ---- self-service submission -----------------------------------------------------------
        private DocumentSubmissionRequest Submission(long typeId, string entityType = "Employee",
            int entityId = 500, string fileName = "passport.pdf", DateTime? expiry = null, string? metadata = null)
            => new()
            {
                EntityType = entityType, EntityId = entityId, DocumentTypeId = typeId,
                FileName = fileName, ContentType = "application/pdf", ExpiryDate = expiry, Metadata = metadata,
            };

        [Fact]
        public async Task An_employee_submits_their_own_document_and_it_lands_as_Submitted()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);

            var result = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("scan"));

            Assert.True(result.Ok, result.ReasonCode);
            var doc = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync();
            Assert.Equal("Submitted", doc.Status);
            Assert.NotNull(doc.CurrentVersionId);
            // A submitter cannot pre-approve their own filing.
            Assert.Null(doc.DecidedBy);
            Assert.Null(doc.DecidedAt);
            // The verb asked for was Submit, not Upload.
            Assert.Contains(h.Access.Asked, a => a.Action == DocumentAction.Submit);
        }

        [Fact]
        public async Task Submitting_for_a_COLLEAGUE_is_refused_by_the_self_only_authority()
        {
            // The resolver maps Submit to employee-request, which HR answers only about the caller. The
            // stub models that: Submit is allowed for one's own employee id and nobody else's.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            h.Access.Decide = (_, entityId, action, _, _) =>
                action == DocumentAction.Submit && entityId != 500
                    ? new DocumentAccessDecision(false, DocumentAccessReasons.ModuleDenied)
                    : new DocumentAccessDecision(true, DocumentAccessReasons.Allowed);

            var mine = await ServiceAt(h, Today).SubmitAsync(Submission(typeId, entityId: 500), DocHost.Bytes("x"));
            var theirs = await ServiceAt(h, Today).SubmitAsync(Submission(typeId, entityId: 501), DocHost.Bytes("x"));

            Assert.True(mine.Ok, mine.ReasonCode);
            Assert.False(theirs.Ok);
            Assert.Equal(1, await h.Platform.Db.Set<PlatformDocument>().CountAsync());
        }

        [Fact]
        public async Task A_type_that_is_not_self_servable_is_refused_however_it_is_asked()
        {
            using var h = new DocHost();
            var hrOnly = h.SeedType("DISCIPLINARY", "Employee");                       // default: false
            var open = h.SeedType("PASSPORT", "Employee", selfService: true);

            Assert.Equal("self_service_not_allowed",
                (await ServiceAt(h, Today).SubmitAsync(Submission(hrOnly), DocHost.Bytes("x"))).ReasonCode);
            Assert.True((await ServiceAt(h, Today).SubmitAsync(Submission(open), DocHost.Bytes("x"))).Ok);
        }

        [Fact]
        public async Task A_submission_cannot_choose_its_own_confidentiality_it_takes_the_types()
        {
            // Nobody files their own payslip as Internal, and nobody hides their own passport as
            // Restricted. The request record has no confidentiality field at all — the strongest form
            // of "cannot choose" is "cannot express".
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true, conf: DocumentConfidentiality.Confidential);

            await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("x"));

            var doc = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync();
            Assert.Equal(DocumentConfidentiality.Confidential, doc.Confidentiality);
            Assert.DoesNotContain("Confidentiality",
                typeof(DocumentSubmissionRequest).GetProperties().Select(p => p.Name));
        }

        [Fact]
        public async Task Type_policy_is_enforced_on_submission_extension_dates_and_family()
        {
            using var h = new DocHost();
            h.Registry.FilesEnabled.Add("Task");
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true, requiresExpiry: true, allowedExt: ".pdf");

            Assert.Equal("file_type_refused",
                (await ServiceAt(h, Today).SubmitAsync(Submission(typeId, fileName: "scan.exe", expiry: Today.AddYears(1)), DocHost.Bytes("x"))).ReasonCode);
            Assert.Equal("expiry_date_required",
                (await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("x"))).ReasonCode);
            Assert.Equal("type_entity_mismatch",
                (await ServiceAt(h, Today).SubmitAsync(Submission(typeId, entityType: "Task", entityId: 900, expiry: Today.AddYears(1)), DocHost.Bytes("x"))).ReasonCode);
            Assert.Equal("unknown_document_type",
                (await ServiceAt(h, Today).SubmitAsync(Submission(999999, expiry: Today.AddYears(1)), DocHost.Bytes("x"))).ReasonCode);
        }

        [Fact]
        public async Task An_oversized_submission_is_refused_by_the_types_own_cap()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true, maxBytes: 8);

            var tooBig = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("this is definitely longer than eight bytes"));
            var fits = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("tiny"));

            Assert.Equal("file_too_large", tooBig.ReasonCode);
            Assert.True(fits.Ok, fits.ReasonCode);
            // Nothing was stored for the refused one: the cap is checked before the write.
            Assert.Single(h.Storage.Blobs);
        }

        [Fact]
        public async Task An_unresolved_context_cannot_submit()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            h.ActAsNobody();

            var result = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("x"));

            Assert.False(result.Ok);
            Assert.Empty(h.Storage.Blobs);
            Assert.Equal(0, await h.Platform.Db.Set<PlatformDocument>().CountAsync());
        }

        // ---- Submitted is not valid --------------------------------------------------------------
        [Fact]
        public async Task A_submitted_document_does_not_satisfy_validity_or_an_onboarding_requirement()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("x"));

            var validity = await ServiceAt(h, Today).FindValidDocumentAsync("Employee", 500, typeId);

            Assert.False(validity.IsValid);
            Assert.Equal(DocumentValidityReasons.NotActive, validity.ReasonCode);
            Assert.False(await ServiceAt(h, Today).HasValidDocumentAsync("Employee", 500, typeId));
        }

        // ---- verification ------------------------------------------------------------------------
        [Fact]
        public async Task HR_verification_activates_the_document_and_records_who_and_when()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var submitted = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("x"));

            var verified = await ServiceAt(h, Today).VerifyAsync(submitted.DocumentId, "seen in person");

            Assert.True(verified.Ok, verified.ReasonCode);
            var doc = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync();
            Assert.Equal("Active", doc.Status);
            Assert.Equal(DocHost.Alice, doc.DecidedBy);
            Assert.Equal(Today, doc.DecidedAt);
            Assert.Equal("seen in person", doc.DecisionNote);

            // And NOW it satisfies the requirement.
            Assert.True(await ServiceAt(h, Today).HasValidDocumentAsync("Employee", 500, typeId));
        }

        [Fact]
        public async Task A_submitter_cannot_verify_their_own_document()
        {
            // The separation that makes "verified" mean anything. Submit is satisfied by
            // employee-request; verification asks for Replace, which is employee-manage.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            h.Access.Decide = (_, _, action, _, _) =>
                action == DocumentAction.Submit
                    ? new DocumentAccessDecision(true, DocumentAccessReasons.Allowed)
                    : new DocumentAccessDecision(false, DocumentAccessReasons.ModuleDenied);

            var submitted = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("x"));
            Assert.True(submitted.Ok);

            var attempted = await ServiceAt(h, Today).VerifyAsync(submitted.DocumentId, null);

            Assert.False(attempted.Ok);
            Assert.Equal("Submitted", (await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync()).Status);
        }

        [Fact]
        public async Task Another_company_cannot_verify_the_document_with_its_exact_id()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var submitted = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("x"));

            h.ActAs(DocHost.Bob, DocHost.CompanyB);
            var attempted = await ServiceAt(h, Today).VerifyAsync(submitted.DocumentId, "not mine to judge");

            Assert.False(attempted.Ok);
            Assert.Equal("Submitted", (await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync()).Status);
        }

        [Fact]
        public async Task An_already_active_document_is_not_decidable_again()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var submitted = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("x"));
            await ServiceAt(h, Today).VerifyAsync(submitted.DocumentId, null);

            var again = await ServiceAt(h, Today).VerifyAsync(submitted.DocumentId, null);

            Assert.False(again.Ok);
            Assert.Equal("not_pending", again.ReasonCode);
        }

        // ---- rejection and correction ------------------------------------------------------------
        [Fact]
        public async Task A_rejection_requires_a_reason_and_keeps_the_document()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var submitted = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("blurry"));

            var noReason = await ServiceAt(h, Today).RejectAsync(submitted.DocumentId, "   ");
            Assert.Equal("decision_note_required", noReason.ReasonCode);

            var rejected = await ServiceAt(h, Today).RejectAsync(submitted.DocumentId, "page 2 is unreadable");
            Assert.True(rejected.Ok, rejected.ReasonCode);

            var doc = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync();
            Assert.Equal("Rejected", doc.Status);                       // it did not disappear
            Assert.Equal("page 2 is unreadable", doc.DecisionNote);
            Assert.False(await ServiceAt(h, Today).HasValidDocumentAsync("Employee", 500, typeId));
        }

        [Fact]
        public async Task A_corrected_resubmission_adds_a_version_clears_the_decision_and_keeps_V1()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var first = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("BLURRY SCAN"));
            await ServiceAt(h, Today).RejectAsync(first.DocumentId, "unreadable");

            var second = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("CLEAR SCAN"));

            Assert.True(second.Ok, second.ReasonCode);
            // ONE document, TWO versions — a correction is not a new mystery row.
            Assert.Equal(1, await h.Platform.Db.Set<PlatformDocument>().CountAsync());
            var history = await ServiceAt(h, Today).HistoryAsync(first.DocumentId);
            Assert.Equal(new[] { 1, 2 }, history.Select(v => v.VersionNo).ToArray());

            var doc = await h.Platform.Db.Set<PlatformDocument>().AsNoTracking().SingleAsync();
            Assert.Equal("Submitted", doc.Status);
            Assert.Null(doc.DecidedBy);                                  // the stale rejection is cleared
            Assert.Null(doc.DecisionNote);

            // The rejected original is still readable to an authorized caller.
            var v1 = await ServiceAt(h, Today).OpenVersionAsync(first.DocumentId, 1);
            Assert.Equal("BLURRY SCAN", ReadAll(v1!));
            Assert.Equal(2, h.Storage.Blobs.Count);
        }

        [Fact]
        public async Task The_full_onboarding_lifecycle_holds_end_to_end()
        {
            // missing -> submitted (still missing) -> verified (satisfied) -> expired (not satisfied)
            // -> renewal submitted (still not) -> renewal verified (satisfied again).
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var day = Today;

            Assert.False(await ServiceAt(h, day).HasValidDocumentAsync("Employee", 500, typeId));

            var submitted = await ServiceAt(h, day).SubmitAsync(
                Submission(typeId, expiry: day.Date.AddDays(10)), DocHost.Bytes("v1"));
            Assert.False(await ServiceAt(h, day).HasValidDocumentAsync("Employee", 500, typeId));

            await ServiceAt(h, day).VerifyAsync(submitted.DocumentId, null);
            Assert.True(await ServiceAt(h, day).HasValidDocumentAsync("Employee", 500, typeId));

            var later = day.AddDays(11);
            Assert.False(await ServiceAt(h, later).HasValidDocumentAsync("Employee", 500, typeId));

            var renewed = await ServiceAt(h, later).SubmitAsync(
                Submission(typeId, expiry: later.Date.AddYears(5)), DocHost.Bytes("v2"));
            Assert.True(renewed.Ok, renewed.ReasonCode);
            Assert.False(await ServiceAt(h, later).HasValidDocumentAsync("Employee", 500, typeId));

            await ServiceAt(h, later).VerifyAsync(renewed.DocumentId, null);
            Assert.True(await ServiceAt(h, later).HasValidDocumentAsync("Employee", 500, typeId));
        }

        // ---- storage compensation ----------------------------------------------------------------
        [Fact]
        public async Task A_DB_failure_after_storage_removes_the_orphan_blob()
        {
            // The P1 batch 2 recorded honestly. The DB is made to fail by disposing the underlying
            // connection, which is the closest a test can get to "the commit did not happen".
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            Assert.Empty(h.Storage.Blobs);

            // Stored, THEN the database goes away - the exact window batch 2 recorded as leaking.
            h.Storage.OnStored = () => h.Platform.Db.Dispose();

            var result = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("orphan"));

            Assert.False(result.Ok);
            Assert.Equal("store_failed", result.ReasonCode);
            // Compensated: nothing unreferenced left behind.
            Assert.Empty(h.Storage.Blobs);
        }

        [Fact]
        public async Task Compensation_never_removes_a_blob_a_committed_version_references()
        {
            // Two documents. The second fails; the first is already committed and must be untouched.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var committed = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("KEEP ME"));
            Assert.True(committed.Ok);
            var keptKey = (await h.Platform.Db.Set<PlatformDocumentVersion>().AsNoTracking().SingleAsync()).StorageKey;
            Assert.Single(h.Storage.Blobs);

            h.Storage.OnStored = () => h.Platform.Db.Dispose();
            await ServiceAt(h, Today).SubmitAsync(Submission(typeId, entityId: 501), DocHost.Bytes("DOOMED"));

            // The committed blob survives, and it is still the one the version names.
            Assert.True(h.Storage.Blobs.ContainsKey(keptKey));
            Assert.Single(h.Storage.Blobs);
        }

        [Fact]
        public async Task A_storage_failure_refuses_without_naming_a_StorageKey()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            h.Storage.FailNextStore = true;

            var result = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("x"));

            Assert.False(result.Ok);
            Assert.Equal("store_failed", result.ReasonCode);
            // The code is a code. Nothing in it could be turned into a URL.
            Assert.DoesNotContain("/", result.ReasonCode);
            Assert.Equal(0, await h.Platform.Db.Set<PlatformDocument>().CountAsync());
        }

        // ---- lifecycle business events -----------------------------------------------------------
        //
        // The double is a RECORDER, not a yes-machine, and it enforces two of the kernel's own rules on
        // the way through: the event type must be canonical, and there must be an ambient transaction.
        // A service that raised a well-shaped event AFTER its commit would satisfy a test that only
        // counted events, so this one refuses to let that pass.
        private sealed class RecordingEvents : IBusinessEventService
        {
            private readonly Models.Context.CrossDbContext _db;
            public readonly List<BusinessEventRecord> Records = new();
            public readonly List<bool> InsideTransaction = new();
            public Exception? FailWith;

            public RecordingEvents(Models.Context.CrossDbContext db) => _db = db;

            public Task<long> RecordAsync(BusinessEventRecord record, CancellationToken ct = default)
            {
                InsideTransaction.Add(_db.Database.CurrentTransaction != null);
                // The kernel's real validator, not a copy of its rules.
                BusinessEventTypes.Validate(record.EventType, record.EntityCode);
                if (FailWith != null) throw FailWith;
                Records.Add(record);
                return Task.FromResult((long)Records.Count);
            }

            public BusinessEventEnvelope BuildEnvelope(Models.Context.Platform.BusinessEvent stored)
                => throw new NotSupportedException("Not exercised: the document platform records, it does not dispatch.");
        }

        private static PlatformDocumentService ServiceWithEvents(DocHost h, RecordingEvents events)
            => new(h.Platform.Db, h.Contexts, h.Access, h.Storage, h.Registry, new FixedClock(Today), events);

        private static string PayloadJson(BusinessEventRecord record)
            => System.Text.Json.JsonSerializer.Serialize(record.Payload);

        [Fact]
        public async Task Submitting_raises_a_canonical_lifecycle_event_inside_the_transaction()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var events = new RecordingEvents(h.Platform.Db);

            var result = await ServiceWithEvents(h, events).SubmitAsync(Submission(typeId), DocHost.Bytes("scan"));
            Assert.True(result.Ok, result.ReasonCode);

            var e = Assert.Single(events.Records);
            // Addressed to the EMPLOYEE, because that is the subject every subscriber already understands.
            Assert.Equal("Employee", e.EntityCode);
            Assert.Equal(500, e.EntityId);
            Assert.Equal("Employee.DocumentSubmitted", e.EventType);
            // ADR-001: inside the business transaction, never after the commit.
            Assert.Equal(new[] { true }, events.InsideTransaction);
            // The type travels as its CODE, so a consumer can recognise a passport without hardcoding one.
            Assert.Contains("PASSPORT", PayloadJson(e));
        }

        [Fact]
        public async Task Verification_and_rejection_each_raise_their_own_event()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);

            var verified = new RecordingEvents(h.Platform.Db);
            var submit = await ServiceWithEvents(h, verified).SubmitAsync(Submission(typeId), DocHost.Bytes("scan"));
            h.ActAs(DocHost.Bob, DocHost.CompanyA);
            Assert.True((await ServiceWithEvents(h, verified).VerifyAsync(submit.DocumentId, "checked")).Ok);
            Assert.Equal(
                new[] { "Employee.DocumentSubmitted", "Employee.DocumentVerified" },
                verified.Records.Select(r => r.EventType).ToArray());

            using var h2 = new DocHost();
            var typeId2 = h2.SeedType("PASSPORT", "Employee", selfService: true);
            var rejected = new RecordingEvents(h2.Platform.Db);
            var submit2 = await ServiceWithEvents(h2, rejected).SubmitAsync(Submission(typeId2), DocHost.Bytes("scan"));
            h2.ActAs(DocHost.Bob, DocHost.CompanyA);
            Assert.True((await ServiceWithEvents(h2, rejected).RejectAsync(submit2.DocumentId, "illegible")).Ok);
            Assert.Equal("Employee.DocumentRejected", rejected.Records.Last().EventType);
            Assert.All(rejected.InsideTransaction, Assert.True);
        }

        [Fact]
        public async Task An_event_payload_never_carries_the_storage_key_the_file_name_the_number_or_the_note()
        {
            // The whole point of the exclusion list. A subscriber that learned the StorageKey would hold a
            // handle the access resolver never issued; a payload carrying "passport-scan.pdf" and the
            // passport NUMBER would have put the document's contents into an event log.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var events = new RecordingEvents(h.Platform.Db);

            var request = Submission(typeId, fileName: "alice-passport-scan.pdf") with
            {
                DocumentNumber = "P9911SECRET",
            };
            var submit = await ServiceWithEvents(h, events).SubmitAsync(request, DocHost.Bytes("scan"));
            Assert.True(submit.Ok, submit.ReasonCode);

            h.ActAs(DocHost.Bob, DocHost.CompanyA);
            Assert.True((await ServiceWithEvents(h, events).RejectAsync(submit.DocumentId, "REASONTEXTPRIVATE")).Ok);

            var storageKey = h.Storage.Blobs.Keys.Single().ToString();
            Assert.Equal(2, events.Records.Count);
            foreach (var e in events.Records)
            {
                var json = PayloadJson(e);
                Assert.DoesNotContain(storageKey, json);
                Assert.DoesNotContain("alice-passport-scan", json);
                Assert.DoesNotContain("P9911SECRET", json);
                Assert.DoesNotContain("REASONTEXTPRIVATE", json);
            }
            // It says a note EXISTS without repeating it - enough to react to, nothing to leak.
            Assert.Contains("hasDecisionNote", PayloadJson(events.Records.Last()));
        }

        [Fact]
        public async Task A_restricted_documents_event_is_restricted_not_internal()
        {
            // The two vocabularies coincide by luck, not by contract. If this mapping ever silently
            // degrades, a disciplinary document announces itself to everyone who may read the employee.
            using var h = new DocHost();
            var typeId = h.SeedType("MEDICAL", "Employee", selfService: true,
                conf: DocumentConfidentiality.Restricted);
            var events = new RecordingEvents(h.Platform.Db);

            Assert.True((await ServiceWithEvents(h, events).SubmitAsync(Submission(typeId), DocHost.Bytes("x"))).Ok);
            Assert.Equal(BusinessEventVisibility.Restricted, Assert.Single(events.Records).Visibility);
        }

        [Fact]
        public async Task A_resubmission_is_a_new_fact_with_its_own_dedup_key()
        {
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var events = new RecordingEvents(h.Platform.Db);

            var first = await ServiceWithEvents(h, events).SubmitAsync(Submission(typeId), DocHost.Bytes("V1"));
            var second = await ServiceWithEvents(h, events).SubmitAsync(Submission(typeId), DocHost.Bytes("V2"));
            Assert.True(second.Ok, second.ReasonCode);
            Assert.Equal(first.DocumentId, second.DocumentId);           // same document, new version

            Assert.Equal(2, events.Records.Count);
            Assert.All(events.Records, r => Assert.Equal("Employee.DocumentSubmitted", r.EventType));
            // Distinct keys, or the kernel would deduplicate the correction away and onboarding would
            // never hear that the readable copy had arrived.
            Assert.Equal(2, events.Records.Select(r => r.DedupKey).Distinct().Count());
        }

        [Fact]
        public async Task An_event_that_cannot_be_recorded_rolls_the_submission_back_and_leaves_no_orphan_blob()
        {
            // The event shares the fate of the row (ADR-001). What must NOT happen is the third outcome:
            // no document, no event, and a blob on disk nobody can reach.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);
            var events = new RecordingEvents(h.Platform.Db)
            {
                FailWith = new InvalidOperationException("event store down"),
            };

            var result = await ServiceWithEvents(h, events).SubmitAsync(Submission(typeId), DocHost.Bytes("scan"));

            Assert.False(result.Ok);
            Assert.Equal("store_failed", result.ReasonCode);
            Assert.Equal(0, await h.Platform.Db.Set<PlatformDocument>().CountAsync());
            Assert.Empty(h.Storage.Blobs);          // compensated, not leaked
        }

        [Fact]
        public async Task The_platform_still_works_with_no_event_service_at_all()
        {
            // The seam is optional on purpose: a host that has not wired the kernel gets a working
            // document platform that announces nothing, rather than a broken one.
            using var h = new DocHost();
            var typeId = h.SeedType("PASSPORT", "Employee", selfService: true);

            var submit = await ServiceAt(h, Today).SubmitAsync(Submission(typeId), DocHost.Bytes("scan"));
            Assert.True(submit.Ok, submit.ReasonCode);
            h.ActAs(DocHost.Bob, DocHost.CompanyA);
            Assert.True((await ServiceAt(h, Today).VerifyAsync(submit.DocumentId, "ok")).Ok);
        }

        private static string Read(DocumentContent content)
        {
            using var reader = new StreamReader(content.Content);
            return reader.ReadToEnd();
        }
    }
}
