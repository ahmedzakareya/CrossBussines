using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Portal;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Portal
{
    // =============================================================================================
    // PUBLISHED CLIENT DOCUMENTS — the external read path, DOC1.
    //
    // THE CHAIN, IN ORDER, AND EVERY LINK SERVER-SIDE:
    //
    //   CrossBuy.Portal cookie          (the controller's [PortalAuthorize])
    //   → PortalContext resolved        (IPortalContextAccessor, from the ACTIVE PortalUsers row)
    //   → portal-documents capability   (a grant, never inferred from having logged in)
    //   → publication row               (same company AND same customer AND not revoked)
    //   → document                      (same company AND Status = Active)
    //   → current version               (resolved from the DOCUMENT, never from a parameter)
    //   → IDocumentStorage              (the platform's own abstraction, never a disk path)
    //
    // EVERY STEP IS NECESSARY AND NONE IS SUFFICIENT. That is the whole design. In particular a
    // publication row ALONE does not admit: the document must still be Active, because publishing
    // cannot override a lifecycle, and a Submitted document is one that was RECEIVED rather than
    // accepted - handing a customer an unverified file as though the company stood behind it is
    // worse than showing them nothing.
    //
    // NOTHING HERE TAKES AN IDENTITY OR A LOCATION FROM THE CALLER. There is no companyId,
    // customerId, portalUserId, documentId, versionNo or path parameter anywhere in this file. The
    // only caller-supplied value is a PUBLICATION ID, and that is deliberate: a publication id names
    // a grant that already carries its company and its customer, so a guessed one fails against its
    // own row. Exposing the internal document id instead would invite a caller to try the next one.
    //
    // NO BYTES ARE COPIED AND NO STATIC FILE IS INVOLVED. The document platform stores content under
    // a root OUTSIDE wwwroot, where it has no URL at all, so this path has no relationship to
    // /Files, /uploads or PrivateFileGate - which is why DOC1 depends on none of them.
    // =============================================================================================

    /// One published document as the customer sees it. No storage key, no path, no URL, no internal
    /// document id, no uploader, no verifier and no publication note - a note written for colleagues
    /// is internal information, and an id is an invitation to try another.
    public sealed class PortalDocumentDto
    {
        /// The ONLY handle the customer ever holds. It addresses the GRANT, not the document.
        public required long PublicationId { get; init; }

        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public required long SizeBytes { get; init; }

        public DateTime? IssueDate { get; init; }
        public DateTime? ExpiryDate { get; init; }
        public required DateTime PublishedAt { get; init; }
    }

    /// The resolved right to stream ONE document, produced only after the whole chain passed.
    public sealed class PortalDocumentContent
    {
        public required Stream Content { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
    }

    public interface IPortalDocumentService
    {
        /// The customer's published documents. Empty for an unresolved identity, for a portal user
        /// without the capability, and for a customer with nothing published - all three answer the
        /// same way, because distinguishing them would tell an outsider which case they are in.
        Task<IReadOnlyList<PortalDocumentDto>> ListAsync(CancellationToken cancellationToken = default);

        /// The bytes, or NULL. Null covers absent, revoked, another company's, another customer's,
        /// an inactive document, a missing version and missing content alike - one answer for all
        /// seven, so a publication id cannot be walked to discover which grants exist.
        Task<PortalDocumentContent?> OpenAsync(long publicationId, CancellationToken cancellationToken = default);
    }

    public sealed class PortalDocumentService : IPortalDocumentService
    {
        private readonly CrossDbContext _db;
        private readonly IPortalContextAccessor _contexts;
        private readonly IDocumentStorage _storage;

        public PortalDocumentService(
            CrossDbContext db, IPortalContextAccessor contexts, IDocumentStorage storage)
        {
            _db = db;
            _contexts = contexts;
            _storage = storage;
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<IReadOnlyList<PortalDocumentDto>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            var portal = await AuthorizedContextAsync(cancellationToken);
            if (portal == null) return Array.Empty<PortalDocumentDto>();

            // ONE server-side query. Publication is the DRIVING table, so the set starts from grants
            // made to THIS customer and is narrowed from there; starting from documents and filtering
            // afterwards would mean the database briefly considered every document in the company.
            //
            // The joins carry the rest of the chain: the document's own company is compared again
            // (a publication naming a document of another company is a data fault, not a grant), its
            // Status must be Active, and the version is joined through the DOCUMENT's CurrentVersionId
            // rather than by any id a caller could name.
            return await (
                from p in _db.Set<ClientDocumentPublication>().AsNoTracking()
                where p.CompanyID == portal.CompanyId
                      && p.CustomerId == portal.CustomerId
                      && p.RevokedAt == null
                join d in _db.Set<PlatformDocument>().AsNoTracking()
                    on p.PlatformDocumentId equals d.Id
                where d.CompanyID == portal.CompanyId
                      && d.EntityType == EntityRegistry.Customer
                      && d.EntityId == portal.CustomerId
                      && d.Confidentiality == DocumentConfidentiality.Internal
                      && d.Status == ActiveStatus
                      && d.CurrentVersionId != null
                join v in _db.Set<PlatformDocumentVersion>().AsNoTracking()
                    on d.CurrentVersionId equals v.Id
                where v.CompanyID == portal.CompanyId && v.DocumentId == d.Id
                orderby p.PublishedAt descending, p.Id descending
                select new PortalDocumentDto
                {
                    PublicationId = p.Id,
                    FileName = v.FileName,
                    ContentType = v.ContentType,
                    SizeBytes = v.SizeBytes,
                    IssueDate = d.IssueDate,
                    ExpiryDate = d.ExpiryDate,
                    PublishedAt = p.PublishedAt,
                })
                .ToListAsync(cancellationToken);
        }

        // ---------------------------------------------------------------------------------------------
        public async Task<PortalDocumentContent?> OpenAsync(
            long publicationId, CancellationToken cancellationToken = default)
        {
            var portal = await AuthorizedContextAsync(cancellationToken);
            if (portal == null) return null;

            // THE SAME CHAIN AS THE LIST, RE-PROVEN. It is not enough that the document appeared in a
            // listing a moment ago: a grant revoked between the two requests must refuse on the
            // second, and a publication id typed into the address bar has never been through the
            // first at all. Re-asking costs one indexed read and removes every ordering of calls
            // that could grant something.
            var row = await (
                from p in _db.Set<ClientDocumentPublication>().AsNoTracking()
                where p.Id == publicationId
                      && p.CompanyID == portal.CompanyId
                      && p.CustomerId == portal.CustomerId
                      && p.RevokedAt == null
                join d in _db.Set<PlatformDocument>().AsNoTracking()
                    on p.PlatformDocumentId equals d.Id
                where d.CompanyID == portal.CompanyId
                      && d.EntityType == EntityRegistry.Customer
                      && d.EntityId == portal.CustomerId
                      && d.Confidentiality == DocumentConfidentiality.Internal
                      && d.Status == ActiveStatus
                      && d.CurrentVersionId != null
                join v in _db.Set<PlatformDocumentVersion>().AsNoTracking()
                    on d.CurrentVersionId equals v.Id
                where v.CompanyID == portal.CompanyId && v.DocumentId == d.Id
                select new { v.StorageKey, v.FileName, v.ContentType })
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null) return null;

            // THE KEY IS REHYDRATED, NEVER TRUSTED AS TEXT. StorageKey.TryParse refuses anything that
            // is not the exact issued shape - 32 lowercase hex characters - so a column that somehow
            // held a path, a separator or a dot-segment cannot reach a filesystem call. A key that
            // needs cleaning up is not a key.
            if (!StorageKey.TryParse(row.StorageKey, out var key)) return null;

            var content = await _storage.OpenReadAsync(key, cancellationToken);
            if (content == null) return null;   // missing content answers exactly like a refusal

            return new PortalDocumentContent
            {
                Content = content,
                FileName = row.FileName,
                ContentType = row.ContentType,
            };
        }

        // ---------------------------------------------------------------------------------------------
        /// A document that was RECEIVED is not a document that was accepted. PlatformDocument's own
        /// comment is explicit that Submitted is not Active and that something has to move it; DOC1
        /// serves only what that something already approved.
        private const string ActiveStatus = "Active";

        /// The identity AND the capability, together, because either alone is a different answer to a
        /// different question. The context comes from the authenticated cookie and the ACTIVE
        /// PortalUsers row - a revoked link yields null here even though the cookie is still valid -
        /// and the capability is a GRANT somebody made, never something inferred from having logged in.
        private async Task<PortalContext?> AuthorizedContextAsync(CancellationToken cancellationToken)
        {
            var portal = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (portal == null) return null;
            if (portal.CompanyId <= 0 || portal.CustomerId <= 0) return null;

            // portal-documents is deliberately NOT in PortalCapabilities.Default: an existing portal
            // account must not silently acquire document access on the day this ships.
            return portal.Can(PortalCapabilities.ViewDocuments) ? portal : null;
        }
    }
}
