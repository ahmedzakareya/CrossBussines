using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Documents
{
    // =============================================================================================
    // CENTRAL DOCUMENT PLATFORM — the service.
    //
    // Every operation runs the SAME sequence, and the sequence is the product:
    //
    //   resolved BusinessContext
    //     -> the document row, read WITH the company predicate in the query
    //     -> IDocumentAccessResolver (company match, owner resolution, owning-module authority,
    //        confidentiality) — TAB-1's spine, asked, never re-implemented
    //     -> only then: storage
    //
    // POSSESSION OF A StorageKey GRANTS NOTHING. That is the single rule this file exists to enforce.
    // A key is an opaque handle to a byte stream; it is not a capability, it is not a URL, and it is
    // never accepted from a caller as a way to reach a document. Downloads name a DOCUMENT, and the
    // platform resolves the key only after the caller has been authorized for that document's owner.
    //
    // WHAT THIS FILE DOES NOT DO. It does not decide permissions (the owner's module does, through the
    // resolver), it does not store bytes (IDocumentStorage does), it does not emit its own audit engine
    // or notification channel, and it does not know what a Passport is — that lives in the type
    // catalogue, so HR never hardcodes document rules again.
    // =============================================================================================

    public sealed record DocumentUploadRequest
    {
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }
        public long? DocumentTypeId { get; init; }
        public string? Confidentiality { get; init; }
        public string? DocumentNumber { get; init; }
        public DateTime? IssueDate { get; init; }
        public DateTime? EffectiveFrom { get; init; }
        public DateTime? ExpiryDate { get; init; }
        public string? Metadata { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public string? Reason { get; init; }
    }

    /// A refusal carries a CODE, never a sentence and never a detail. "not found" and "you may not"
    /// and "another company" are deliberately the same answer: anything else lets a caller probe.
    public sealed record DocumentResult(bool Ok, string ReasonCode, long DocumentId = 0, long VersionId = 0)
    {
        public static DocumentResult Refused(string code) => new(false, code);
        public static DocumentResult Success(long documentId, long versionId) =>
            new(true, DocumentAccessReasons.Allowed, documentId, versionId);
    }

    public sealed record DocumentContent(Stream Content, string FileName, string ContentType);

    public sealed record DocumentListItem(
        long Id, string EntityType, int EntityId, long? DocumentTypeId, string Confidentiality,
        string? DocumentNumber, DateTime? IssueDate, DateTime? EffectiveFrom, DateTime? ExpiryDate,
        string Status, int VersionCount, DateTime CreatedAt);

    public interface IPlatformDocumentService
    {
        Task<DocumentResult> UploadAsync(DocumentUploadRequest request, Stream content, CancellationToken ct = default);
        Task<DocumentResult> ReplaceAsync(long documentId, Stream content, string fileName, string contentType, string? reason, CancellationToken ct = default);
        Task<DocumentContent?> OpenCurrentAsync(long documentId, CancellationToken ct = default);
        Task<DocumentContent?> OpenVersionAsync(long documentId, int versionNo, CancellationToken ct = default);
        Task<IReadOnlyList<DocumentListItem>> ListForEntityAsync(string entityType, int entityId, CancellationToken ct = default);
        Task<IReadOnlyList<PlatformDocumentVersion>> HistoryAsync(long documentId, CancellationToken ct = default);
    }

    public sealed class PlatformDocumentService : IPlatformDocumentService
    {
        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _contexts;
        private readonly IDocumentAccessResolver _access;
        private readonly IDocumentStorage _storage;
        private readonly IEntityRegistry _registry;

        public PlatformDocumentService(CrossDbContext db, IBusinessContextAccessor contexts,
            IDocumentAccessResolver access, IDocumentStorage storage, IEntityRegistry registry)
        { _db = db; _contexts = contexts; _access = access; _storage = storage; _registry = registry; }

        private DbSet<PlatformDocument> Documents => _db.Set<PlatformDocument>();
        private DbSet<PlatformDocumentVersion> Versions => _db.Set<PlatformDocumentVersion>();
        private DbSet<PlatformDocumentType> Types => _db.Set<PlatformDocumentType>();

        /// Null means REFUSE. An unresolved context is never a licence to proceed unscoped, and a
        /// resolver that throws is a refusal rather than an exemption.
        private async Task<BusinessContext?> ScopeAsync(CancellationToken ct)
        {
            try
            {
                var ctx = await _contexts.TryGetCurrentAsync(ct);
                if (ctx == null || ctx.CompanyId <= 0 || ctx.EmployeeId is not > 0) return null;
                return ctx;
            }
            catch (Exception) { return null; }
        }

        private async Task<DocumentAccessDecision> AuthorizeAsync(
            BusinessContext ctx, string entityType, int entityId, DocumentAction action,
            int documentCompanyId, string? confidentiality, CancellationToken ct)
        {
            try
            {
                return await _access.AuthorizeAsync(ctx,
                    new DocumentOwnerRef(entityType, entityId), action, documentCompanyId, confidentiality, ct);
            }
            catch (Exception)
            {
                // A spine that cannot answer is not permission.
                return new DocumentAccessDecision(false, DocumentAccessReasons.NoModuleAuthority);
            }
        }

        /// The registry decides whether a family carries documents at all — this service does not keep
        /// its own list of what may be attached to what. An entity whose definition says
        /// SupportsFiles = false is refused here, before any owner or permission work happens.
        private bool FamilyCarriesDocuments(string entityType)
            => _registry.TryGetDefinition(entityType, out var def) && def != null && def.SupportsFiles;

        // ---- upload ---------------------------------------------------------------------------
        public async Task<DocumentResult> UploadAsync(DocumentUploadRequest request, Stream content, CancellationToken ct = default)
        {
            var ctx = await ScopeAsync(ct);
            if (ctx == null) return DocumentResult.Refused(DocumentAccessReasons.CompanyUnresolved);
            if (request.EntityId <= 0 || string.IsNullOrWhiteSpace(request.EntityType))
                return DocumentResult.Refused(DocumentAccessReasons.UnknownEntityType);
            if (!FamilyCarriesDocuments(request.EntityType))
                return DocumentResult.Refused(DocumentAccessReasons.UnknownEntityType);

            // The type, if named, must exist, be active, be visible to this company and APPLY to this
            // family. A Passport attached to a Purchase Invoice is refused by the catalogue, not by a
            // reviewer noticing later.
            PlatformDocumentType? type = null;
            if (request.DocumentTypeId is long typeId)
            {
                type = await Types.AsNoTracking().FirstOrDefaultAsync(
                    t => t.Id == typeId && t.IsActive && (t.CompanyID == null || t.CompanyID == ctx.CompanyId), ct);
                if (type == null) return DocumentResult.Refused("unknown_document_type");
                if (!AppliesTo(type, request.EntityType)) return DocumentResult.Refused("type_entity_mismatch");
                if (type.RequiresIssueDate && request.IssueDate == null) return DocumentResult.Refused("issue_date_required");
                if (type.RequiresExpiryDate && request.ExpiryDate == null) return DocumentResult.Refused("expiry_date_required");
                if (!ExtensionAllowed(type, request.FileName)) return DocumentResult.Refused("file_type_refused");
            }

            var confidentiality = Normalise(request.Confidentiality) ?? type?.DefaultConfidentiality ?? DocumentConfidentiality.Internal;
            if (!DocumentConfidentiality.IsKnown(confidentiality))
                return DocumentResult.Refused(DocumentAccessReasons.UnknownConfidentiality);

            if (!MetadataValid(type, request.Metadata, out var metadataError))
                return DocumentResult.Refused(metadataError);

            // The company written on the document is the RESOLVED one, and the spine independently
            // confirms the owning record lives in that same company — which is what makes an
            // entity/company mismatch impossible rather than merely unlikely.
            var decision = await AuthorizeAsync(ctx, request.EntityType, request.EntityId,
                DocumentAction.Upload, ctx.CompanyId, confidentiality, ct);
            if (!decision.Allowed) return DocumentResult.Refused(decision.ReasonCode);

            if (type?.MaxSizeBytes is long cap && content.CanSeek && content.Length > cap)
                return DocumentResult.Refused("file_too_large");

            var key = await _storage.StoreAsync(content, request.FileName, ct);
            var now = DateTime.UtcNow;

            using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var doc = new PlatformDocument
                {
                    CompanyID = ctx.CompanyId,
                    EntityType = request.EntityType,
                    EntityId = request.EntityId,
                    DocumentTypeId = request.DocumentTypeId,
                    Confidentiality = confidentiality,
                    DocumentNumber = request.DocumentNumber,
                    IssueDate = request.IssueDate,
                    EffectiveFrom = request.EffectiveFrom,
                    ExpiryDate = request.ExpiryDate,
                    Metadata = request.Metadata,
                    Status = "Active",
                    CreatedBy = ctx.EmployeeId!.Value,
                    CreatedAt = now,
                };
                Documents.Add(doc);
                await _db.SaveChangesAsync(ct);

                var version = NewVersion(doc, 1, key, request.FileName, request.ContentType, content, request.Reason, null, ctx, now);
                Versions.Add(version);
                await _db.SaveChangesAsync(ct);

                doc.CurrentVersionId = version.Id;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return DocumentResult.Success(doc.Id, version.Id);
            }
            catch (Exception)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                return DocumentResult.Refused("store_failed");
            }
        }

        // ---- replace / renew ------------------------------------------------------------------
        public async Task<DocumentResult> ReplaceAsync(long documentId, Stream content, string fileName,
            string contentType, string? reason, CancellationToken ct = default)
        {
            var ctx = await ScopeAsync(ct);
            if (ctx == null) return DocumentResult.Refused(DocumentAccessReasons.CompanyUnresolved);

            var doc = await LoadAsync(documentId, ctx, ct);
            if (doc == null) return DocumentResult.Refused(DocumentAccessReasons.OwnerNotFound);

            var decision = await AuthorizeAsync(ctx, doc.EntityType, doc.EntityId,
                DocumentAction.Replace, doc.CompanyID, doc.Confidentiality, ct);
            if (!decision.Allowed) return DocumentResult.Refused(decision.ReasonCode);

            var key = await _storage.StoreAsync(content, fileName, ct);
            var now = DateTime.UtcNow;

            using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                // The next number is read inside the transaction and the unique index settles a race,
                // so two simultaneous renewals cannot both become V2 and lose one another's file.
                var last = await Versions.AsNoTracking()
                    .Where(v => v.DocumentId == doc.Id)
                    .OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);

                var version = NewVersion(doc, (last?.VersionNo ?? 0) + 1, key, fileName, contentType,
                    content, reason, last?.Id, ctx, now);
                Versions.Add(version);
                await _db.SaveChangesAsync(ct);

                // ONLY the pointer moves. The previous row keeps its own StorageKey, so the old file is
                // still there and still readable through the history — no overwrite, ever.
                var tracked = await Documents.FirstAsync(d => d.Id == doc.Id, ct);
                tracked.CurrentVersionId = version.Id;
                tracked.UpdatedBy = ctx.EmployeeId!.Value;
                tracked.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);
                return DocumentResult.Success(doc.Id, version.Id);
            }
            catch (Exception)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                return DocumentResult.Refused("store_failed");
            }
        }

        // ---- read -----------------------------------------------------------------------------
        public async Task<DocumentContent?> OpenCurrentAsync(long documentId, CancellationToken ct = default)
        {
            var resolved = await AuthorizedVersionAsync(documentId, null, ct);
            return resolved == null ? null : await OpenAsync(resolved);
        }

        public async Task<DocumentContent?> OpenVersionAsync(long documentId, int versionNo, CancellationToken ct = default)
        {
            var resolved = await AuthorizedVersionAsync(documentId, versionNo, ct);
            return resolved == null ? null : await OpenAsync(resolved);
        }

        /// The whole download flow in one place, so a second entry point cannot skip a step.
        private async Task<PlatformDocumentVersion?> AuthorizedVersionAsync(long documentId, int? versionNo, CancellationToken ct)
        {
            var ctx = await ScopeAsync(ct);
            if (ctx == null) return null;

            var doc = await LoadAsync(documentId, ctx, ct);
            if (doc == null) return null;

            var decision = await AuthorizeAsync(ctx, doc.EntityType, doc.EntityId,
                DocumentAction.Download, doc.CompanyID, doc.Confidentiality, ct);
            if (!decision.Allowed) return null;

            // The version is fetched by DOCUMENT, never by a caller-supplied version id: a version id
            // would be a second identifier to authorize, and one of the two would eventually be missed.
            var q = Versions.AsNoTracking().Where(v => v.DocumentId == doc.Id && v.CompanyID == doc.CompanyID);
            return versionNo is int n
                ? await q.FirstOrDefaultAsync(v => v.VersionNo == n, ct)
                : await q.FirstOrDefaultAsync(v => v.Id == doc.CurrentVersionId, ct);
        }

        private async Task<DocumentContent?> OpenAsync(PlatformDocumentVersion version)
        {
            if (!StorageKey.TryParse(version.StorageKey, out var key)) return null;
            var stream = await _storage.OpenReadAsync(key);
            return stream == null ? null : new DocumentContent(stream, version.FileName, version.ContentType);
        }

        public async Task<IReadOnlyList<DocumentListItem>> ListForEntityAsync(string entityType, int entityId, CancellationToken ct = default)
        {
            var ctx = await ScopeAsync(ct);
            if (ctx == null) return Array.Empty<DocumentListItem>();
            if (entityId <= 0 || string.IsNullOrWhiteSpace(entityType)) return Array.Empty<DocumentListItem>();

            // AUTHORIZE FIRST, THEN READ. Nothing is fetched for an entity the caller may not view, so
            // a listing can never become the leak the document reads are guarded against.
            var decision = await AuthorizeAsync(ctx, entityType, entityId, DocumentAction.View, ctx.CompanyId, null, ct);
            if (!decision.Allowed) return Array.Empty<DocumentListItem>();

            var rows = await Documents.AsNoTracking()
                .Where(d => d.CompanyID == ctx.CompanyId && d.EntityType == entityType && d.EntityId == entityId
                            && d.Status != "Archived")
                .OrderByDescending(d => d.Id).ToListAsync(ct);

            // Confidentiality is re-asked PER DOCUMENT: "may view the employee" is not "may view every
            // document about the employee", which is the whole point of the tier.
            var visible = new List<DocumentListItem>();
            foreach (var d in rows)
            {
                var perDoc = await AuthorizeAsync(ctx, d.EntityType, d.EntityId, DocumentAction.View, d.CompanyID, d.Confidentiality, ct);
                if (!perDoc.Allowed) continue;
                var count = await Versions.AsNoTracking().CountAsync(v => v.DocumentId == d.Id, ct);
                visible.Add(new DocumentListItem(d.Id, d.EntityType, d.EntityId, d.DocumentTypeId, d.Confidentiality,
                    d.DocumentNumber, d.IssueDate, d.EffectiveFrom, d.ExpiryDate, d.Status, count, d.CreatedAt));
            }
            return visible;
        }

        public async Task<IReadOnlyList<PlatformDocumentVersion>> HistoryAsync(long documentId, CancellationToken ct = default)
        {
            var ctx = await ScopeAsync(ct);
            if (ctx == null) return Array.Empty<PlatformDocumentVersion>();
            var doc = await LoadAsync(documentId, ctx, ct);
            if (doc == null) return Array.Empty<PlatformDocumentVersion>();

            var decision = await AuthorizeAsync(ctx, doc.EntityType, doc.EntityId,
                DocumentAction.View, doc.CompanyID, doc.Confidentiality, ct);
            if (!decision.Allowed) return Array.Empty<PlatformDocumentVersion>();

            return await Versions.AsNoTracking().Where(v => v.DocumentId == doc.Id)
                .OrderBy(v => v.VersionNo).ToListAsync(ct);
        }

        // ---- helpers ---------------------------------------------------------------------------

        /// The company predicate is IN THE QUERY. A foreign document is never materialised, so
        /// "belongs to another company" and "does not exist" are the same outcome by construction.
        private Task<PlatformDocument?> LoadAsync(long documentId, BusinessContext ctx, CancellationToken ct)
            => Documents.AsNoTracking().FirstOrDefaultAsync(
                d => d.Id == documentId && d.CompanyID == ctx.CompanyId, ct);

        private static PlatformDocumentVersion NewVersion(PlatformDocument doc, int versionNo, StorageKey key,
            string fileName, string contentType, Stream content, string? reason, long? replaces,
            BusinessContext ctx, DateTime now)
            => new()
            {
                CompanyID = doc.CompanyID,
                DocumentId = doc.Id,
                VersionNo = versionNo,
                StorageKey = key.Value,
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = content.CanSeek ? content.Length : 0,
                Reason = reason,
                ReplacesVersionId = replaces,
                UploadedBy = ctx.EmployeeId!.Value,
                UploadedAt = now,
            };

        private static bool AppliesTo(PlatformDocumentType type, string entityType)
            => type.AppliesToEntityTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(c => string.Equals(c, entityType, StringComparison.Ordinal));

        private static bool ExtensionAllowed(PlatformDocumentType type, string fileName)
        {
            if (string.IsNullOrWhiteSpace(type.AllowedExtensions)) return true;
            var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) return false;
            return type.AllowedExtensions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(a => string.Equals(a.ToLowerInvariant(), ext, StringComparison.Ordinal));
        }

        private static string? Normalise(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// Metadata is validated against the type's schema when one is declared. The schema is a flat
        /// JSON object of key -> "required"/"optional"; anything richer would be a schema language, and
        /// this platform does not need one yet.
        private static bool MetadataValid(PlatformDocumentType? type, string? metadata, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(metadata))
            {
                if (type?.MetadataSchema == null) return true;
                return RequiredKeys(type.MetadataSchema).Count == 0 || Fail("metadata_required", out error);
            }

            JsonElement parsed;
            try { parsed = JsonDocument.Parse(metadata).RootElement; }
            catch (JsonException) { return Fail("metadata_invalid_json", out error); }
            if (parsed.ValueKind != JsonValueKind.Object) return Fail("metadata_not_an_object", out error);

            if (type?.MetadataSchema == null) return true;
            foreach (var key in RequiredKeys(type.MetadataSchema))
                if (!parsed.TryGetProperty(key, out _)) return Fail("metadata_missing_" + key, out error);
            return true;
        }

        private static bool Fail(string code, out string error) { error = code; return false; }

        private static List<string> RequiredKeys(string schema)
        {
            var keys = new List<string>();
            try
            {
                var root = JsonDocument.Parse(schema).RootElement;
                if (root.ValueKind != JsonValueKind.Object) return keys;
                foreach (var p in root.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String &&
                        string.Equals(p.Value.GetString(), "required", StringComparison.OrdinalIgnoreCase))
                        keys.Add(p.Name);
            }
            catch (JsonException) { /* a malformed schema requires nothing rather than refusing everything */ }
            return keys;
        }
    }
}
