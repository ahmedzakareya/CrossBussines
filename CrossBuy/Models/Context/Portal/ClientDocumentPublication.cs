using System;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Portal
{
    // =============================================================================================
    // A PUBLICATION IS A GRANT, NOT A COPY — Client Portal DOC1.
    //
    // THE ONE SENTENCE THIS TABLE EXISTS FOR: "this company deliberately gave THIS document to THIS
    // customer." Nothing else in the product can say that.
    //
    // WHY A ROW RATHER THAN A FLAG OR A NEW CONFIDENTIALITY TIER, measured rather than preferred:
    //
    //   * DocumentConfidentiality is frozen at Internal | Confidential | Restricted | System. All
    //     four are STAFF tiers - the vocabulary has no way to say "a customer may read this", and
    //     adding a fifth would be the wrong repair anyway: confidentiality answers HOW SENSITIVE,
    //     not WHO OUTSIDE THE COMPANY WAS GIVEN IT. A tier cannot name a customer.
    //   * A boolean "published" flag on the document has the same defect one step further on: with
    //     two customers of one company it degrades to "same CompanyId ⇒ visible", which is exactly
    //     the failure this slice exists to prevent. It also cannot be revoked per customer and
    //     cannot record who decided.
    //   * Anchoring documents to the Customer record is not available: Customer.SupportsFiles is
    //     FALSE in the registry and PlatformDocumentService refuses such an entity before any owner
    //     or permission work happens. Changing that is a TAB-1 registry decision and DOC1 does not
    //     need it - the document keeps its own anchor (a quotation, an invoice, a delivery note) and
    //     this row carries the customer.
    //
    // NO BYTES, NO PATH, NO URL, NO SECOND STORE. The row points at an existing PlatformDocument by
    // id. The document platform already keeps its content under a root OUTSIDE wwwroot, where it has
    // no URL at all; duplicating a byte here would create a second copy with its own lifecycle and
    // its own way of going stale.
    //
    // REVOCATION, NEVER DELETION. RevokedAt ends a grant and keeps the history: "was this customer
    // ever given the March invoice, and who decided" is a real question, and a deleted row answers
    // it with silence.
    // =============================================================================================
    public class ClientDocumentPublication
    {
        public long Id { get; set; }

        /// The tenant. EXPLICIT, because the measured global query filter covers fourteen entities
        /// and will not cover this one - so every read states this predicate rather than trusting a
        /// filter that was never extended. A publication visible across tenants would hand one
        /// company's documents to another's customer.
        ///
        /// Written from the publishing employee's RESOLVED BusinessContext, never from a request.
        public int CompanyID { get; set; }

        /// WHO IT WAS GIVEN TO, and the whole reason this table exists. Same company is NOT
        /// sufficient: two customers of one company must not see each other's documents, so this is
        /// compared against the CustomerId the portal context resolved from the PortalUsers row.
        ///
        /// No foreign key, matching PortalUser.CustomerId which has none either: tenancy here is
        /// enforced by predicate on every read, and a cross-module FK would couple the portal's
        /// deployment to the customer master's.
        public int CustomerId { get; set; }

        /// The document being granted. A real PlatformDocument that already exists and keeps its own
        /// anchor, lifecycle, confidentiality and versions - none of which this row overrides.
        public long PlatformDocumentId { get; set; }

        // ---- who decided, and when ----------------------------------------------------------------
        //
        // Stored rather than inferred from an audit trail, for the same reason PlatformDocument
        // stores its own decision evidence: "who gave the customer this" is a question the business
        // has to answer directly, and reconstructing it from events would make it depend on a replay.

        public int PublishedByEmployeeId { get; set; }
        public DateTime PublishedAt { get; set; }

        /// Set when the grant is withdrawn. The row survives; the customer stops seeing the document
        /// on the very next request, because every read filters on this being null.
        public DateTime? RevokedAt { get; set; }
        public int? RevokedByEmployeeId { get; set; }

        /// Optional, and for the STAFF side only - why this was shared. It is never rendered to the
        /// customer: a note written for colleagues is internal information.
        public string? Note { get; set; }

        /// True only while the grant stands. A computed convenience for readers; it is NOT the
        /// control - the control is the RevokedAt predicate inside each query, because a property
        /// evaluated in memory would mean loading rows the caller may not have.
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public bool IsLive => RevokedAt == null;
    }

    /// EF mapping, in the file this slice owns, so CrossDbContext keeps its zero-line footprint —
    /// the pattern PortalUser, the Communication platform and the Document platform already use.
    /// PortalModel.Configure calls this, and PortalModel is already wired into the context, so DOC1
    /// needs no edit to that shared file at all.
    public static class ClientDocumentPublicationModel
    {
        public const int NoteLength = 400;

        public static void Configure(ModelBuilder builder)
        {
            builder.Entity<ClientDocumentPublication>(e =>
            {
                e.ToTable("ClientDocumentPublications");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Note).HasMaxLength(NoteLength);

                // ONE LIVE GRANT PER (document, customer), filtered on RevokedAt so publish → revoke →
                // publish keeps the full history while only one row is ever in force. Without the
                // filter a re-publication would collide with the withdrawn row and look like a bug.
                e.HasIndex(x => new { x.PlatformDocumentId, x.CustomerId })
                    .IsUnique()
                    .HasDatabaseName("UX_ClientDocumentPublications_Live")
                    .HasFilter("[RevokedAt] IS NULL");

                // THE PORTAL LIST READ, in the order the predicate is applied: company first, because
                // every external read is company-scoped before it is anything else, then customer,
                // then the live filter.
                e.HasIndex(x => new { x.CompanyID, x.CustomerId, x.PublishedAt })
                    .HasDatabaseName("IX_ClientDocumentPublications_Customer")
                    .HasFilter("[RevokedAt] IS NULL");

                // RESTRICT, NEVER CASCADE. Removing a publication must never remove the document it
                // published, and deleting a governed document out from under a live grant must fail
                // loudly rather than silently orphan the customer's view of it. Both directions of
                // that mistake are closed by the same choice.
                e.HasOne<CrossBuy.Models.Context.Documents.PlatformDocument>()
                    .WithMany()
                    .HasForeignKey(x => x.PlatformDocumentId)
                    .OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}
