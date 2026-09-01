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
    /// A self-service submission. Note what it CANNOT carry: no company, no confidentiality, no
    /// status, no verifier, no decision. Those are not validated away — they are absent, which is a
    /// stronger guarantee than a check somebody could later relax.
    public sealed record DocumentSubmissionRequest
    {
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }
        public required long DocumentTypeId { get; init; }
        public string? DocumentNumber { get; init; }
        public DateTime? IssueDate { get; init; }
        public DateTime? EffectiveFrom { get; init; }
        public DateTime? ExpiryDate { get; init; }
        public string? Metadata { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public string? Reason { get; init; }
    }

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


    /// What the platform means by "this record currently has a valid document of this type".
    ///
    /// It exists so a consumer never has to reassemble the rule. HR onboarding asking "does this
    /// employee have a valid passport" must not reimplement company scoping, entity matching, status,
    /// current-version existence and expiry — five predicates that would drift the moment a sixth is
    /// added here.
    public sealed record DocumentValidity(
        bool IsValid, string ReasonCode, long DocumentId = 0, DateTime? ExpiryDate = null)
    {
        public static DocumentValidity No(string code) => new(false, code);
        public static DocumentValidity Yes(long documentId, DateTime? expiry) =>
            new(true, DocumentValidityReasons.Valid, documentId, expiry);
    }

    /// THE OPERATIONAL STATES, which are a superset of "is it valid".
    ///
    /// Validity answers one question - may this document be relied on TODAY - and onboarding needs
    /// exactly that. Operations needs more: a passport that expires in three weeks is perfectly valid
    /// and somebody should already be renewing it. Both answers come out of ONE evaluation, because the
    /// moment they come out of two the day the second one disagrees is only a matter of time.
    public enum DocumentLifecycleState
    {
        /// No document, or the caller may not be told whether there is one.
        Missing = 0,
        /// Filed, awaiting a decision. Includes a renewal awaiting verification.
        Submitted = 1,
        /// Looked at and refused. The document still exists and can be corrected.
        Rejected = 2,
        /// Not yet filed.
        Draft = 3,
        /// Out of circulation.
        Archived = 4,
        /// Active, but missing something its type demands, or with no bytes.
        Incomplete = 5,
        /// Active, in date, and not near its expiry - or with no expiry at all.
        Valid = 6,
        /// Active and STILL VALID, but inside its type's warning window.
        ExpiringSoon = 7,
        /// Past its expiry date. Not valid.
        Expired = 8,
    }

    /// The full answer, from the one evaluator. `ReasonCode` is the validity vocabulary, unchanged, so
    /// every existing caller keeps its exact behaviour.
    public sealed record DocumentLifecycle(
        DocumentLifecycleState State,
        string ReasonCode,
        long DocumentId = 0,
        DateTime? ExpiryDate = null,
        int? DaysRemaining = null,
        int WarningDays = 0)
    {
        /// ExpiringSoon IS valid. Saying otherwise would refuse somebody on a document they still hold,
        /// which is the same mistake as treating an expiry date as a deadline that has already passed.
        public bool CountsAsValid =>
            State is DocumentLifecycleState.Valid or DocumentLifecycleState.ExpiringSoon;

        public bool NeedsAttention =>
            State is DocumentLifecycleState.ExpiringSoon or DocumentLifecycleState.Expired;
    }

    // A READ API FOR THIS - "give me every document of this employee's with its state" - is written and
    // deliberately NOT landed in this batch. Its only consumers are the documents workspace (§13) and the
    // reporting datasets (§14), and both live behind paths this tab does not own, so it would have landed
    // as dead code.
    //
    // That matters more than usual here. IPlatformDocumentService is a DECLARED AUTHORITY in the
    // analyzer's surface, credited because every one of its members was verified to authorize, and
    // DocumentAuthorityMemberTests reflectively fails the moment a member is added that its table does not
    // cover. Widening that interface is therefore a governance act, not an edit - and doing it for members
    // nothing can call yet would spend somebody else's guarantee for no delivered behaviour. It goes with
    // the UI, as one unit, in the handoff.

    /// The lead-time policy, resolved in ONE place.
    public static class DocumentExpiryPolicy
    {
        /// Used when a type has not configured its own. NOT zero and NOT "never": a type nobody has got
        /// round to configuring must still give notice, because the failure it prevents - a document
        /// expiring in silence - is much worse than a warning that came earlier than someone wanted.
        public const int DefaultWarningDays = 30;

        /// Clamped at both ends. A negative lead time would mean warning AFTER expiry, which is what
        /// the Expired state is for; an absurd one would put every document in the window forever and
        /// the warning would stop meaning anything.
        public const int MaxWarningDays = 365;

        public static int WarningDaysFor(int? configured)
        {
            if (configured is not int days) return DefaultWarningDays;
            if (days < 0) return 0;
            return days > MaxWarningDays ? MaxWarningDays : days;
        }
    }

    public static class DocumentValidityReasons
    {
        public const string Valid = "valid";
        /// Refusal to ANSWER, not an answer. Deliberately identical to the code returned when nothing
        /// matches, so an unauthorized caller cannot tell "you may not ask" from "there is none" and
        /// use the query to discover that an employee holds a passport.
        public const string NotFound = "not_found";
        public const string Expired = "expired";
        public const string NoCurrentVersion = "no_current_version";
        public const string NotActive = "not_active";
        public const string MissingRequiredIssueDate = "missing_required_issue_date";
        public const string MissingRequiredExpiryDate = "missing_required_expiry_date";
    }

    public interface IPlatformDocumentService
    {
        Task<DocumentResult> UploadAsync(DocumentUploadRequest request, Stream content, CancellationToken ct = default);
        Task<DocumentResult> ReplaceAsync(long documentId, Stream content, string fileName, string contentType, string? reason, CancellationToken ct = default);
        Task<DocumentContent?> OpenCurrentAsync(long documentId, CancellationToken ct = default);
        Task<DocumentContent?> OpenVersionAsync(long documentId, int versionNo, CancellationToken ct = default);
        Task<IReadOnlyList<DocumentListItem>> ListForEntityAsync(string entityType, int entityId, CancellationToken ct = default);
        Task<IReadOnlyList<PlatformDocumentVersion>> HistoryAsync(long documentId, CancellationToken ct = default);

        /// "Does this record currently hold a valid document of this type?"
        /// Authorization first: an unauthorized caller gets the same not_found a caller with no such
        /// document gets, so the query cannot be used to probe.
        Task<DocumentValidity> FindValidDocumentAsync(string entityType, int entityId, long documentTypeId, CancellationToken ct = default);

        Task<bool> HasValidDocumentAsync(string entityType, int entityId, long documentTypeId, CancellationToken ct = default);

        /// Self-service: a person hands in their own evidence. Produces Submitted, never Active.
        Task<DocumentResult> SubmitAsync(DocumentSubmissionRequest request, Stream content, CancellationToken ct = default);

        /// HR looks at a pending document and accepts it. Requires employee-manage, so the submitter
        /// cannot verify their own.
        Task<DocumentResult> VerifyAsync(long documentId, string? note, CancellationToken ct = default);

        /// HR refuses it, with a reason. The document stays, so the person can correct it.
        Task<DocumentResult> RejectAsync(long documentId, string note, CancellationToken ct = default);

    }

    public sealed class PlatformDocumentService : IPlatformDocumentService
    {
        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _contexts;
        private readonly IDocumentAccessResolver _access;
        private readonly IDocumentStorage _storage;
        private readonly IEntityRegistry _registry;

        /// THE CLOCK, and deliberately not a bespoke one.
        ///
        /// Validity turns on "is the expiry date before today", which was DateTime.UtcNow.Date inline
        /// and therefore untestable at the boundary that matters: a document expiring TODAY is valid
        /// and one that expired YESTERDAY is not, and those two cases are a day apart in real time.
        ///
        /// .NET 8 ships TimeProvider, so there is no reason to invent IPlatformClock. Production gets
        /// TimeProvider.System; a test supplies a fixed one. No framework, one dependency, and the
        /// abstraction is the platform's rather than ours to maintain.
        private readonly TimeProvider _clock;

        /// THE LIFECYCLE EVENT SEAM, and why it is optional.
        ///
        /// Submitted / Verified / Rejected are the three facts about a document that another module has
        /// a legitimate reason to hear about - an onboarding checklist wants to know the passport
        /// arrived, without the document platform having to know that onboarding exists. That is what
        /// the kernel's Business Events are for, so this raises them rather than growing a second
        /// notification path.
        ///
        /// It is NULLABLE because the kernel refuses an event recorded outside an ambient transaction
        /// (ADR-001) and a document write is not always run inside one host. Absent, the platform still
        /// works and simply announces nothing - which is honest. Present, the event shares the fate of
        /// the row: it is recorded inside the same transaction, so there is no state in which the
        /// document says verified and the timeline disagrees.
        private readonly IBusinessEventService? _events;

        public PlatformDocumentService(CrossDbContext db, IBusinessContextAccessor contexts,
            IDocumentAccessResolver access, IDocumentStorage storage, IEntityRegistry registry,
            TimeProvider? clock = null, IBusinessEventService? events = null)
        { _db = db; _contexts = contexts; _access = access; _storage = storage; _registry = registry;
          _clock = clock ?? TimeProvider.System; _events = events; }

        private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

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

            var stored = await StoreAsync(content, request.FileName, ct);
            if (stored == null) return DocumentResult.Refused("store_failed");
            var key = stored.Value;
            var now = UtcNow;

            // BEGIN INSIDE THE TRY. It used to sit outside, and a test found what that costs: if
            // opening the transaction itself fails — a dropped connection, a disposed context — the
            // exception escapes before the catch, and the blob written moments earlier is never
            // compensated. The whole DB phase has to be inside the guarded region, not just the part
            // that looked risky.
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
            try
            {
                tx = await _db.Database.BeginTransactionAsync(ct);
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

                var version = NewVersion(doc, 1, key, request.FileName, request.ContentType, content,
                    request.Reason, null, ctx, now,
                    doc.DocumentNumber, doc.IssueDate, doc.ExpiryDate);
                Versions.Add(version);
                await _db.SaveChangesAsync(ct);

                doc.CurrentVersionId = version.Id;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                await tx.DisposeAsync();
                return DocumentResult.Success(doc.Id, version.Id);
            }
            catch (Exception)
            {
                // Compensated, not merely rolled back: batch 2 recorded that a failed commit here left
                // the freshly written blob unreferenced on disk. See RollbackAndCompensateAsync.
                await RollbackAndCompensateAsync(tx, key, ct);
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

            var stored = await StoreAsync(content, fileName, ct);
            if (stored == null) return DocumentResult.Refused("store_failed");
            var key = stored.Value;
            var now = UtcNow;

            // BEGIN INSIDE THE TRY. It used to sit outside, and a test found what that costs: if
            // opening the transaction itself fails — a dropped connection, a disposed context — the
            // exception escapes before the catch, and the blob written moments earlier is never
            // compensated. The whole DB phase has to be inside the guarded region, not just the part
            // that looked risky.
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
            try
            {
                tx = await _db.Database.BeginTransactionAsync(ct);
                // The next number is read inside the transaction and the unique index settles a race,
                // so two simultaneous renewals cannot both become V2 and lose one another's file.
                var last = await Versions.AsNoTracking()
                    .Where(v => v.DocumentId == doc.Id)
                    .OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);

                // A REPLACEMENT IS NOT A RENEWAL. The bytes change - a better scan of the same
                // passport - and the instrument does not, so the new version records the SAME number
                // and dates the document already carries.
                var version = NewVersion(doc, (last?.VersionNo ?? 0) + 1, key, fileName, contentType,
                    content, reason, last?.Id, ctx, now,
                    doc.DocumentNumber, doc.IssueDate, doc.ExpiryDate);
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
                await tx.DisposeAsync();
                return DocumentResult.Success(doc.Id, version.Id);
            }
            catch (Exception)
            {
                // Compensated, not merely rolled back: batch 2 recorded that a failed commit here left
                // the freshly written blob unreferenced on disk. See RollbackAndCompensateAsync.
                await RollbackAndCompensateAsync(tx, key, ct);
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



        // ---- self-service submission ------------------------------------------------------------
        //
        // SUBMIT IS NOT UPLOAD. Upload is an HR act authorized by employee-manage: whoever performs it
        // has authority over the record, so what they attach IS the record. Submit is a person handing
        // in their own evidence — authorized by employee-request, which HrAccessService answers purely
        // on whether the target is the caller. So this path is self-only without containing a rule
        // about selfness, and it produces something RECEIVED rather than something established.
        //
        // WHAT THE SUBMITTER CANNOT CHOOSE, and why each one is taken away rather than validated:
        //   * the employee — the resolver's target IS the caller, so another id simply fails the ask
        //   * the company — taken from the resolved context, never from the request
        //   * the confidentiality — forced to the TYPE's default, so nobody files their own payslip
        //     as Internal or their own passport as Restricted to hide it from HR
        //   * the status — always Submitted; Active is not reachable from this method at all
        //   * the decision — DecidedBy/At/Note are untouched here, so a submitter cannot pre-approve
        public async Task<DocumentResult> SubmitAsync(DocumentSubmissionRequest request, Stream content, CancellationToken ct = default)
        {
            // TOTAL BY CONSTRUCTION. Everything below can touch the database, and a service that lets a
            // raw EF exception reach a controller turns an infrastructure blip into a stack trace on a
            // page. Every failure leaves through the same door as a policy refusal.
            try { return await SubmitCoreAsync(request, content, ct); }
            catch (Exception) { return DocumentResult.Refused("store_failed"); }
        }

        private async Task<DocumentResult> SubmitCoreAsync(DocumentSubmissionRequest request, Stream content, CancellationToken ct)
        {
            var ctx = await ScopeAsync(ct);
            if (ctx == null) return DocumentResult.Refused(DocumentAccessReasons.CompanyUnresolved);
            if (request.EntityId <= 0 || string.IsNullOrWhiteSpace(request.EntityType))
                return DocumentResult.Refused(DocumentAccessReasons.UnknownEntityType);
            if (!FamilyCarriesDocuments(request.EntityType))
                return DocumentResult.Refused(DocumentAccessReasons.UnknownEntityType);

            // A submission ALWAYS names a type. An ad-hoc self-submission would have no policy to
            // enforce — no allow-list, no size cap, no confidentiality default — so it is refused.
            var type = await Types.AsNoTracking().FirstOrDefaultAsync(
                t => t.Id == request.DocumentTypeId && t.IsActive
                     && (t.CompanyID == null || t.CompanyID == ctx.CompanyId), ct);
            if (type == null) return DocumentResult.Refused("unknown_document_type");

            // The gate that says a person may file this KIND of document about themselves at all.
            // Configuration, not code: nothing here knows what a passport is.
            if (!type.SelfServiceAllowed) return DocumentResult.Refused("self_service_not_allowed");
            if (!AppliesTo(type, request.EntityType)) return DocumentResult.Refused("type_entity_mismatch");
            if (type.RequiresIssueDate && request.IssueDate == null) return DocumentResult.Refused("issue_date_required");
            if (type.RequiresExpiryDate && request.ExpiryDate == null) return DocumentResult.Refused("expiry_date_required");
            if (!ExtensionAllowed(type, request.FileName)) return DocumentResult.Refused("file_type_refused");
            if (!MetadataValid(type, request.Metadata, out var metadataError)) return DocumentResult.Refused(metadataError);

            // Size is checked BEFORE anything is stored, when the stream can tell us. A cap enforced
            // after the write has already spent the disk it was meant to protect.
            if (type.MaxSizeBytes is long cap && content.CanSeek && content.Length > cap)
                return DocumentResult.Refused("file_too_large");

            // THE authorization. Submit maps to employee-request, so this is where "own employee only"
            // is decided — by HR's own rule, not by a comparison in this file.
            var decision = await AuthorizeAsync(ctx, request.EntityType, request.EntityId,
                DocumentAction.Submit, ctx.CompanyId, type.DefaultConfidentiality, ct);
            if (!decision.Allowed) return DocumentResult.Refused(decision.ReasonCode);

            // A resubmission is a NEW VERSION of the existing document for this type, not a second
            // document — otherwise a rejected passport and its correction become two rows and the
            // validity rule has to guess which one counts.
            var existing = await Documents.AsNoTracking().FirstOrDefaultAsync(
                d => d.CompanyID == ctx.CompanyId && d.EntityType == request.EntityType
                     && d.EntityId == request.EntityId && d.DocumentTypeId == type.Id
                     && d.Status != "Archived", ct);

            return existing == null
                ? await CreateSubmittedAsync(ctx, request, type, content, ct)
                : await ResubmitAsync(ctx, existing.Id, request, content, ct);
        }

        private async Task<DocumentResult> CreateSubmittedAsync(BusinessContext ctx,
            DocumentSubmissionRequest request, PlatformDocumentType type, Stream content, CancellationToken ct)
        {
            var stored = await StoreAsync(content, request.FileName, ct);
            if (stored == null) return DocumentResult.Refused("store_failed");
            var key = stored.Value;
            var now = UtcNow;

            // BEGIN INSIDE THE TRY. It used to sit outside, and a test found what that costs: if
            // opening the transaction itself fails — a dropped connection, a disposed context — the
            // exception escapes before the catch, and the blob written moments earlier is never
            // compensated. The whole DB phase has to be inside the guarded region, not just the part
            // that looked risky.
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
            try
            {
                tx = await _db.Database.BeginTransactionAsync(ct);
                var doc = new PlatformDocument
                {
                    CompanyID = ctx.CompanyId,
                    EntityType = request.EntityType,
                    EntityId = request.EntityId,
                    DocumentTypeId = type.Id,
                    // Forced, not chosen. See the header.
                    Confidentiality = type.DefaultConfidentiality,
                    DocumentNumber = request.DocumentNumber,
                    IssueDate = request.IssueDate,
                    EffectiveFrom = request.EffectiveFrom,
                    ExpiryDate = request.ExpiryDate,
                    Metadata = request.Metadata,
                    Status = "Submitted",
                    CreatedBy = ctx.EmployeeId!.Value,
                    CreatedAt = now,
                };
                Documents.Add(doc);
                await _db.SaveChangesAsync(ct);

                var version = NewVersion(doc, 1, key, request.FileName, request.ContentType, content,
                    request.Reason ?? "submitted", null, ctx, now,
                    doc.DocumentNumber, doc.IssueDate, doc.ExpiryDate);
                Versions.Add(version);
                await _db.SaveChangesAsync(ct);

                doc.CurrentVersionId = version.Id;
                await _db.SaveChangesAsync(ct);

                // BEFORE the commit, inside the transaction. If the event cannot be recorded the
                // document is not submitted either, and the catch below compensates the blob.
                await RaiseAsync(doc, type, DocumentEvents.Submitted, version.Id, ct);

                await tx.CommitAsync(ct);
                await tx.DisposeAsync();
                return DocumentResult.Success(doc.Id, version.Id);
            }
            catch (Exception)
            {
                await RollbackAndCompensateAsync(tx, key, ct);
                return DocumentResult.Refused("store_failed");
            }
        }

        /// A correction. Adds a version and returns the document to Submitted, clearing the previous
        /// decision — a corrected document has not been judged yet, and leaving a stale "rejected"
        /// note on it would tell HR the wrong thing about the file in front of them.
        private async Task<DocumentResult> ResubmitAsync(BusinessContext ctx, long documentId,
            DocumentSubmissionRequest request, Stream content, CancellationToken ct)
        {
            var stored = await StoreAsync(content, request.FileName, ct);
            if (stored == null) return DocumentResult.Refused("store_failed");
            var key = stored.Value;
            var now = UtcNow;

            // BEGIN INSIDE THE TRY. It used to sit outside, and a test found what that costs: if
            // opening the transaction itself fails — a dropped connection, a disposed context — the
            // exception escapes before the catch, and the blob written moments earlier is never
            // compensated. The whole DB phase has to be inside the guarded region, not just the part
            // that looked risky.
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
            try
            {
                tx = await _db.Database.BeginTransactionAsync(ct);
                var last = await Versions.AsNoTracking().Where(v => v.DocumentId == documentId)
                    .OrderByDescending(v => v.VersionNo).FirstOrDefaultAsync(ct);

                var doc = await Documents.FirstAsync(d => d.Id == documentId, ct);

                // THIS IS THE RENEWAL PATH, and it is the same path as a correction on purpose - see
                // the renewal decision in the header. What arrives may be a corrected scan of the SAME
                // passport or a brand-new passport; the platform cannot tell and does not need to,
                // because both are "the next version of this governed document".
                //
                // The instrument the NEW version carries is resolved BEFORE anything is written, so the
                // version row and the document row cannot disagree about which passport this is.
                var newNumber = request.DocumentNumber ?? doc.DocumentNumber;
                var newIssue = request.IssueDate ?? doc.IssueDate;
                var newExpiry = request.ExpiryDate ?? doc.ExpiryDate;

                var version = NewVersion(doc, (last?.VersionNo ?? 0) + 1, key, request.FileName,
                    request.ContentType, content, request.Reason ?? "resubmitted", last?.Id, ctx, now,
                    newNumber, newIssue, newExpiry);
                Versions.Add(version);
                await _db.SaveChangesAsync(ct);

                doc.CurrentVersionId = version.Id;
                doc.Status = "Submitted";
                doc.DecidedBy = null; doc.DecidedAt = null; doc.DecisionNote = null;
                // The submitter may correct the dates that came with the new copy; they may not touch
                // confidentiality, which stays whatever the type decided when the document was created.
                //
                // THE PREVIOUS VERSION KEEPS ITS OWN NUMBER AND DATES. That is what makes this a
                // renewal rather than an overwrite: the document row moves on to the current
                // instrument, and V1 still says exactly what the expired passport said.
                doc.DocumentNumber = newNumber;
                doc.IssueDate = newIssue;
                doc.ExpiryDate = newExpiry;
                doc.UpdatedBy = ctx.EmployeeId!.Value;
                doc.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);

                var resubmittedType = await Types.AsNoTracking()
                    .FirstAsync(t => t.Id == doc.DocumentTypeId, ct);
                await RaiseAsync(doc, resubmittedType, DocumentEvents.Submitted, version.Id, ct);

                await tx.CommitAsync(ct);
                await tx.DisposeAsync();
                return DocumentResult.Success(doc.Id, version.Id);
            }
            catch (Exception)
            {
                await RollbackAndCompensateAsync(tx, key, ct);
                return DocumentResult.Refused("store_failed");
            }
        }

        // ---- verification -----------------------------------------------------------------------
        //
        // WHY THIS IS NOT THE APPROVAL PLATFORM. I looked: ApprovalInboxService and
        // InventoryApprovalService model a REQUEST that travels — submitted for approval, routed to a
        // step, acted on, and the outcome then drives the original transaction. A document
        // verification has no route and no steps: one person with employee-manage looks at a file and
        // says yes or no, and the outcome is a property of the document itself rather than of a
        // travelling request.
        //
        // Wrapping that in an approval request would create a second object whose lifecycle has to be
        // kept in step with the document's, and the first bug would be a verified approval attached to
        // a document that had since been resubmitted. So verification stays a narrow governed
        // transition ON the document — and if a deployment later wants multi-step document approval,
        // the approval platform can drive THIS transition rather than duplicate it.
        //
        // Authorization is employee-manage (Replace), NOT Submit: the person who filed a document must
        // not be able to verify it, and Submit is the only verb their own authority satisfies.
        public async Task<DocumentResult> VerifyAsync(long documentId, string? note, CancellationToken ct = default)
            => await DecideAsync(documentId, "Active", note, requireNote: false, ct);

        /// A rejection REQUIRES a reason. Telling somebody their document was refused without saying
        /// why is not a decision, it is an obstacle — and the person then resubmits the same file.
        public async Task<DocumentResult> RejectAsync(long documentId, string note, CancellationToken ct = default)
            => await DecideAsync(documentId, "Rejected", note, requireNote: true, ct);

        private async Task<DocumentResult> DecideAsync(long documentId, string outcome, string? note,
            bool requireNote, CancellationToken ct)
        {
            var ctx = await ScopeAsync(ct);
            if (ctx == null) return DocumentResult.Refused(DocumentAccessReasons.CompanyUnresolved);
            if (requireNote && string.IsNullOrWhiteSpace(note)) return DocumentResult.Refused("decision_note_required");

            var doc = await LoadAsync(documentId, ctx, ct);
            if (doc == null) return DocumentResult.Refused(DocumentAccessReasons.OwnerNotFound);

            // Replace maps to employee-manage. A submitter holds only employee-request, so they cannot
            // reach this method for their own document — which is the separation that makes a
            // "verified" status mean something.
            var decision = await AuthorizeAsync(ctx, doc.EntityType, doc.EntityId,
                DocumentAction.Replace, doc.CompanyID, doc.Confidentiality, ct);
            if (!decision.Allowed) return DocumentResult.Refused(decision.ReasonCode);

            // Only a pending document is decidable. Re-verifying an Active one, or judging an Archived
            // one, are both no-ops dressed as actions.
            if (!string.Equals(doc.Status, "Submitted", StringComparison.Ordinal)
                && !string.Equals(doc.Status, "Rejected", StringComparison.Ordinal))
                return DocumentResult.Refused("not_pending");

            // A document cannot become Active without bytes to be active ABOUT.
            if (string.Equals(outcome, "Active", StringComparison.Ordinal) && doc.CurrentVersionId == null)
                return DocumentResult.Refused(DocumentValidityReasons.NoCurrentVersion);

            // A TRANSACTION AROUND A ONE-ROW UPDATE, on purpose. The status change and the event that
            // announces it must land together: an event saying "verified" with no verified row would
            // send every subscriber chasing a decision that never happened, and a verified row with no
            // event would leave onboarding waiting forever. There is no blob here, so nothing to
            // compensate — the rollback is the whole remedy.
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
            try
            {
                tx = await _db.Database.BeginTransactionAsync(ct);

                var tracked = await Documents.FirstAsync(d => d.Id == doc.Id, ct);
                tracked.Status = outcome;
                tracked.DecidedBy = ctx.EmployeeId!.Value;
                tracked.DecidedAt = UtcNow;
                tracked.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
                tracked.UpdatedBy = ctx.EmployeeId!.Value;
                tracked.UpdatedAt = tracked.DecidedAt;
                await _db.SaveChangesAsync(ct);

                var type = await Types.AsNoTracking().FirstAsync(t => t.Id == tracked.DocumentTypeId, ct);
                await RaiseAsync(tracked, type,
                    string.Equals(outcome, "Active", StringComparison.Ordinal) ? DocumentEvents.Verified : DocumentEvents.Rejected,
                    tracked.CurrentVersionId, ct);

                await tx.CommitAsync(ct);
                await tx.DisposeAsync();
                return DocumentResult.Success(tracked.Id, tracked.CurrentVersionId ?? 0);
            }
            catch (Exception)
            {
                if (tx != null)
                {
                    try { await tx.RollbackAsync(ct); } catch (Exception) { }
                    try { await tx.DisposeAsync(); } catch (Exception) { }
                }
                try { _db.ChangeTracker.Clear(); } catch (Exception) { }
                return DocumentResult.Refused("decision_failed");
            }
        }

        // ---- lifecycle events -------------------------------------------------------------------
        //
        // WHAT THE PAYLOAD MAY SAY, and the reasoning behind every exclusion. The kernel's own rule is
        // that a payload never carries files, secrets or unrestricted employee data, and a document is
        // made almost entirely of things that fail that test:
        //
        //   StorageKey  - EXCLUDED. It is the one value that must never travel, because a subscriber
        //                 that learned it would be holding a handle the access resolver never issued.
        //   file path / bytes / file name - EXCLUDED. A payload is not a delivery channel, and a name
        //                 like "termination-letter.pdf" leaks the content it names.
        //   DocumentNumber - EXCLUDED. This is the passport or civil-ID number itself. It is the most
        //                 sensitive field on the row and it has no business in an event log.
        //   DecisionNote - EXCLUDED. A rejection reason is free text a human typed about a person; the
        //                 event says a decision happened, and the document says what it was.
        //
        // What is left is the shape of the fact: which document, of which type, in which state. That is
        // enough for a subscriber to react and to come back through the authorized read path for more,
        // which is exactly the amount of trust an event deserves.
        //
        // THE EVENT IS ADDRESSED TO THE OWNING ENTITY, not to the document. The employee is what the
        // permission model, the timeline and every subscriber already understand; a document id as the
        // subject would be a second addressing scheme nobody else speaks. The type code travels so a
        // consumer can tell a passport from a contract WITHOUT hardcoding either - it reads the code it
        // was configured with.
        //
        // VISIBILITY IS THE DOCUMENT'S CONFIDENTIALITY. The two vocabularies happen to coincide, but
        // the mapping is written out rather than cast, because a silent numeric coincidence between an
        // access classification and an event classification is not something to bet a Restricted
        // medical document on. Anything unrecognised becomes Restricted - the narrowest, not the
        // default.
        /// Delegates to DocumentEvents, which is the ONE payload policy - shared with the expiry
        /// projection so the two producers cannot drift about what may be said or how loudly.
        private Task RaiseAsync(PlatformDocument doc, PlatformDocumentType? type, string action,
            long? versionId, CancellationToken ct, int? versionNo = null)
            => DocumentEvents.RaiseAsync(_events, doc, type, action,
                DocumentEvents.LifecycleKey(doc.Id, versionId, action), ct, versionNo);

        /// The three actions, named once. "Document" prefixes each because the event is addressed to the
        /// EMPLOYEE: "Employee.Submitted" would be a fact about the person, not about their file.

        // ---- storage compensation ----------------------------------------------------------------
        //
        // THE FILESYSTEM AND SQL ARE NOT ONE TRANSACTION, and batch 2 recorded the consequence
        // honestly: bytes land first, so a failed DB commit left an unreferenced blob behind.
        //
        // The write order is deliberate and stays. The alternative — row first, bytes second — would
        // leave a version row pointing at bytes that never arrived, and a document that 404s forever
        // is worse than a file nobody can reach. So the exposure is closed by COMPENSATION instead.
        //
        // WHAT MAKES THE DELETE SAFE: the key being removed was generated moments earlier inside this
        // method and has not been committed to any version row — the transaction that would have
        // referenced it has just been rolled back. Nothing else can hold it, because a StorageKey is
        // generated per store and never reused. A committed version's blob is therefore unreachable
        // from here by construction, not by a check.
        private async Task<StorageKey?> StoreAsync(Stream content, string? fileName, CancellationToken ct)
        {
            try { return await _storage.StoreAsync(content, fileName, ct); }
            catch (Exception) { return null; }
        }

        private async Task RollbackAndCompensateAsync(
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx, StorageKey key, CancellationToken ct)
        {
            // THE BLOB GOES FIRST, and that ordering is the fix for a bug a test found. Tidying the DB
            // first looks natural, but ChangeTracker.Clear() throws on a disposed context — so on the
            // one failure this method exists for, the cleanup died before reaching the delete and the
            // orphan survived. Compensation must not depend on the database still being alive: the
            // whole reason we are here is that it is not.
            //
            // Safe because the key was generated moments ago inside the calling method and no committed
            // version row references it — the transaction that would have has just failed. A StorageKey
            // is generated per store and never reused, so a committed blob is unreachable from here by
            // construction rather than by a check.
            //
            // Best effort and silent: a failed cleanup must not become a message that names a
            // StorageKey. An orphan is housekeeping, not the caller's business.
            try { await _storage.DeleteAsync(key, ct); } catch (Exception) { }

            if (tx != null)
            {
                try { await tx.RollbackAsync(ct); } catch (Exception) { /* the connection is already lost */ }
                try { await tx.DisposeAsync(); } catch (Exception) { }
            }
            try { _db.ChangeTracker.Clear(); } catch (Exception) { /* disposed context */ }
        }

        // ---- validity -----------------------------------------------------------------------------
        //
        // WHAT "VALID" MEANS, stated once so no consumer has to restate it:
        //
        //   the document belongs to the RESOLVED company              (company predicate in the query)
        //   and to this entity type and this entity id                (the relation, not a filename)
        //   and to this document type                                 (not "a document that looks like one")
        //   and its Status is Active                                  (Draft/Expired/Archived are not valid)
        //   and it has a current version                              (a row with no bytes is not a document)
        //   and, if the TYPE requires dates, they are present         (the catalogue decides, not the caller)
        //   and it is not past its expiry                             (when an expiry is recorded)
        //
        // Nothing here reads a filename or a StorageKey. A document is valid because of what the
        // platform recorded about it, never because of what a file happens to be called.
        public async Task<DocumentValidity> FindValidDocumentAsync(
            string entityType, int entityId, long documentTypeId, CancellationToken ct = default)
        {
            var ctx = await ScopeAsync(ct);
            if (ctx == null) return DocumentValidity.No(DocumentValidityReasons.NotFound);
            if (entityId <= 0 || documentTypeId <= 0 || string.IsNullOrWhiteSpace(entityType))
                return DocumentValidity.No(DocumentValidityReasons.NotFound);

            // AUTHORIZE BEFORE READING. "Employee 42 has a passport" is itself sensitive, so an
            // unauthorized caller is refused with the SAME code an authorized caller gets when there is
            // no such document — otherwise the query becomes an oracle for what people hold.
            var decision = await AuthorizeAsync(ctx, entityType, entityId, DocumentAction.View, ctx.CompanyId, null, ct);
            if (!decision.Allowed) return DocumentValidity.No(DocumentValidityReasons.NotFound);

            var type = await Types.AsNoTracking().FirstOrDefaultAsync(
                t => t.Id == documentTypeId && (t.CompanyID == null || t.CompanyID == ctx.CompanyId), ct);
            if (type == null) return DocumentValidity.No(DocumentValidityReasons.NotFound);

            var candidates = await Documents.AsNoTracking()
                .Where(d => d.CompanyID == ctx.CompanyId
                            && d.EntityType == entityType
                            && d.EntityId == entityId
                            && d.DocumentTypeId == documentTypeId)
                .OrderByDescending(d => d.Id)
                .ToListAsync(ct);

            if (candidates.Count == 0) return DocumentValidity.No(DocumentValidityReasons.NotFound);

            // The most specific refusal among the candidates, so a caller learns "expired" rather than a
            // flat "no" when a document exists but has lapsed — the distinction onboarding needs to tell
            // "never supplied" from "needs renewing". Ordered newest-first, so the freshest wins.
            string worst = DocumentValidityReasons.NotFound;
            foreach (var d in candidates)
            {
                var reason = EvaluateOne(d, type, out var confidentialityToCheck);
                if (reason != DocumentValidityReasons.Valid) { worst = Prefer(worst, reason); continue; }

                // Confidentiality is asked PER DOCUMENT before it is reported as held: a caller who may
                // see the employee but not confidential material must not learn one exists.
                var perDoc = await AuthorizeAsync(ctx, entityType, entityId, DocumentAction.View,
                    d.CompanyID, confidentialityToCheck, ct);
                if (!perDoc.Allowed) { worst = Prefer(worst, DocumentValidityReasons.NotFound); continue; }

                return DocumentValidity.Yes(d.Id, d.ExpiryDate);
            }
            return DocumentValidity.No(worst);
        }

        public async Task<bool> HasValidDocumentAsync(
            string entityType, int entityId, long documentTypeId, CancellationToken ct = default)
            => (await FindValidDocumentAsync(entityType, entityId, documentTypeId, ct)).IsValid;

        /// THE CANONICAL EVALUATOR. Every date predicate in the document platform is in this method,
        /// and there is deliberately no second one anywhere - not in the worker, not in a report, not
        /// in a view, not in onboarding. `EvaluateOne` below is a projection of this, which is why
        /// validity and operational status cannot drift apart: they are the same computation.
        ///
        /// `type` is nullable because an ad-hoc document has no catalogue entry. A document with no type
        /// still expires if somebody recorded an expiry date - the date is a statement about the
        /// document, and having no type is not a reason to ignore it.
        /// The instance form: evaluates against THIS service's clock.
        private DocumentLifecycle EvaluateLifecycle(PlatformDocument d, PlatformDocumentType? type)
            => EvaluateAt(d, type, UtcNow.Date);

        /// THE CANONICAL EVALUATOR, exposed statically so the expiry projection and its worker use the
        /// SAME rule rather than a sympathetic copy of it.
        ///
        /// `today` is a parameter rather than a clock read, which is what makes one rule serve a
        /// request-scoped service and a background sweep without either of them owning the calendar.
        /// It is the only reason this is public: nothing outside the platform should be computing a
        /// document's state, and now nothing has to.
        public static DocumentLifecycle EvaluateAt(PlatformDocument d, PlatformDocumentType? type, DateTime today)
        {
            today = today.Date;
            var warningDays = DocumentExpiryPolicy.WarningDaysFor(type?.ExpiryWarningDays);

            DocumentLifecycle At(DocumentLifecycleState state, string reason, int? days = null)
                => new(state, reason, d.Id, d.ExpiryDate, days, warningDays);

            if (!string.Equals(d.Status, "Active", StringComparison.Ordinal))
            {
                // NOT ACTIVE is one validity answer but several operational ones. The reason code stays
                // exactly what it always was, so no existing caller changes behaviour; the STATE is what
                // lets an operator tell "awaiting verification" from "refused".
                var state = d.Status switch
                {
                    "Submitted" => DocumentLifecycleState.Submitted,
                    "Rejected" => DocumentLifecycleState.Rejected,
                    "Draft" => DocumentLifecycleState.Draft,
                    "Archived" => DocumentLifecycleState.Archived,
                    // Including a stored "Expired". The platform does not WRITE that status - expiry is
                    // derived from the date, and a worker that stamped it would create a second source
                    // of truth that goes stale between ticks - but a legacy row carrying it is read
                    // honestly rather than silently treated as active.
                    "Expired" => DocumentLifecycleState.Expired,
                    _ => DocumentLifecycleState.Incomplete,
                };
                return At(state, DocumentValidityReasons.NotActive);
            }

            if (d.CurrentVersionId == null)
                return At(DocumentLifecycleState.Incomplete, DocumentValidityReasons.NoCurrentVersion);
            if (type != null && type.RequiresIssueDate && d.IssueDate == null)
                return At(DocumentLifecycleState.Incomplete, DocumentValidityReasons.MissingRequiredIssueDate);
            if (type != null && type.RequiresExpiryDate && d.ExpiryDate == null)
                return At(DocumentLifecycleState.Incomplete, DocumentValidityReasons.MissingRequiredExpiryDate);

            // Expiry is honoured whenever one is RECORDED, even if the type does not demand one: a date
            // someone took the trouble to enter is a statement about the document, not decoration.
            if (d.ExpiryDate is DateTime expiry)
            {
                // ONE subtraction, and every boundary in the platform is decided by it.
                //
                // STRICTLY NEGATIVE IS EXPIRED. A document whose expiry IS today has days == 0 and is
                // still valid: a passport does not stop being a passport at midnight of its printed
                // date, and treating it as expired would refuse somebody on the last day they were
                // entitled to be accepted.
                //
                // The warning window is INCLUSIVE at both ends: days == 0 (expires today) and
                // days == warningDays (the first day of notice) are both ExpiringSoon. An exclusive
                // upper bound would mean a 30-day policy first warns at 29 days, which is not what
                // anybody configuring "30" means.
                int days = (expiry.Date - today).Days;
                if (days < 0)
                    return At(DocumentLifecycleState.Expired, DocumentValidityReasons.Expired, days);
                if (days <= warningDays)
                    return At(DocumentLifecycleState.ExpiringSoon, DocumentValidityReasons.Valid, days);
                return At(DocumentLifecycleState.Valid, DocumentValidityReasons.Valid, days);
            }

            return At(DocumentLifecycleState.Valid, DocumentValidityReasons.Valid);
        }

        /// The validity projection of the evaluator above. Kept as its own method because every existing
        /// caller asks the validity question, and because a caller that only needs a yes/no should not
        /// have to know the operational vocabulary exists.
        private string EvaluateOne(PlatformDocument d, PlatformDocumentType type, out string confidentiality)
        {
            confidentiality = d.Confidentiality;
            return EvaluateLifecycle(d, type).ReasonCode;
        }

        /// "Expired" is more informative than "not_found", so it survives when both occur.
        private static string Prefer(string current, string candidate)
            => current == DocumentValidityReasons.NotFound ? candidate : current;

        // ---- helpers ---------------------------------------------------------------------------

        /// The company predicate is IN THE QUERY. A foreign document is never materialised, so
        /// "belongs to another company" and "does not exist" are the same outcome by construction.
        private Task<PlatformDocument?> LoadAsync(long documentId, BusinessContext ctx, CancellationToken ct)
            => Documents.AsNoTracking().FirstOrDefaultAsync(
                d => d.Id == documentId && d.CompanyID == ctx.CompanyId, ct);

        /// THE INSTRUMENT IS PASSED IN, not read off `doc`, and that is deliberate.
        ///
        /// Reading it from the document would make this method's correctness depend on WHERE it is
        /// called relative to the statements that move the document's dates. In the renewal path the
        /// version is created before those statements run, so a `doc`-reading version would have
        /// recorded the OLD passport's number against the NEW passport's bytes - history that is worse
        /// than no history, because it looks authoritative. An explicit parameter makes each call site
        /// say what the version IS.
        private static PlatformDocumentVersion NewVersion(PlatformDocument doc, int versionNo, StorageKey key,
            string fileName, string contentType, Stream content, string? reason, long? replaces,
            BusinessContext ctx, DateTime now,
            string? documentNumber, DateTime? issueDate, DateTime? expiryDate)
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
                DocumentNumber = documentNumber,
                IssueDate = issueDate,
                ExpiryDate = expiryDate,
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
