using System;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Documents
{
    // =============================================================================================
    // CENTRAL DOCUMENT PLATFORM — entities and EF mapping.
    //
    // OWN NAMESPACE, ON PURPOSE — and the test suite is what insisted.
    //
    // These types first lived in CrossBuy.Models.Context.Communication, because that is a path this
    // work stream owns outright and it kept the batch free of shared-file edits. Two guards rejected
    // that in turn, and both were right:
    //
    //   * CommunicationDiWiringTests failed 32 ways when the service was registered inside
    //     AddCommunicationPlatform - the Communication platform had grown a dependency on the document
    //     spine, which it neither owns nor should carry.
    //   * CommunicationSchemaParityTests then failed because it selects the platform's tables BY CLR
    //     NAMESPACE, with the comment "the namespace is the actual statement of ownership". Declaring
    //     that namespace was claiming to be Communication Platform tables. These are not.
    //
    // So the document platform states its own ownership: its own namespace, its own model entry point,
    // its own registration extension. Governance follows the code rather than shaping it - the path is
    // currently unclaimed and a narrow claim is part of this batch's handoff.
    //
    // WHAT THIS IS NOT. It is not a file manager and not an attachment bag. A PlatformDocument is a
    // GOVERNED document: it has a type, a lifecycle, validity dates, a confidentiality tier and an
    // append-only version history. The binary lives behind an opaque StorageKey that this model never
    // resolves to a path — see ADR-040 and IDocumentStorage.
    // =============================================================================================

    /// A governed document belonging to exactly one business record.
    ///
    /// IDENTITY IS (CompanyId, EntityType, EntityId). The same triple the Communication platform uses
    /// for threads, and for the same reason: a document id alone must never be enough to reach a
    /// document, because ids are guessable and companies are not allowed to see each other.
    public class PlatformDocument
    {
        public long Id { get; set; }

        /// The owning company. Written from the RESOLVED BusinessContext, never from a caller value,
        /// and re-checked against the owning entity's own company before the row is created.
        public int CompanyID { get; set; }

        /// EntityRegistry code of the record this document belongs to (Employee, Task, ...).
        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }

        /// The catalogue entry that says what kind of document this is. Nullable so an ad-hoc upload is
        /// still possible without inventing a synthetic "Other" type row per company.
        public long? DocumentTypeId { get; set; }

        /// The version a reader should be served. Null only between INSERT and the first version, which
        /// happens inside one transaction, so a committed document always has a current version.
        public long? CurrentVersionId { get; set; }

        /// Internal | Confidential | Restricted | System — the vocabulary the platform already ships
        /// (ADR-040 reuses Communication's rather than inventing a second meaning for "Confidential").
        public string Confidentiality { get; set; } = "Internal";

        // ---- the fixed, searchable business fields -------------------------------------------------
        // These four are columns rather than JSON because every governed document in this product has
        // needed them — EmployeeDocument and ApplicationDocument both already carry all four — and
        // because expiry has to be answerable by an indexed query, not by parsing every document's JSON.
        public string? DocumentNumber { get; set; }
        public DateTime? IssueDate { get; set; }
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? ExpiryDate { get; set; }

        /// Everything type-specific, validated against the type's MetadataSchema on write.
        /// Hybrid rather than EAV: this repository deploys through idempotent SQL slices with EF
        /// migrations disabled, so a normalised attribute table would mean a schema slice for every new
        /// document type — the exact cost the type catalogue exists to avoid.
        public string? Metadata { get; set; }

        /// Draft | Submitted | Rejected | Active | Expired | Archived.
        ///
        /// SUBMITTED IS NOT ACTIVE, and that distinction is the point. A document the subject filed
        /// themselves has been RECEIVED, not accepted: it is not evidence that HR verified anything, and
        /// FindValidDocumentAsync deliberately does not count it. Something has to move it to Active,
        /// and deciding what that something is belongs to whoever owns verification — this platform
        /// only refuses to pretend the question was already answered.
        ///
        /// REJECTED IS A STATE, NOT A DELETION. A refused submission must not simply disappear: the
        /// person who filed it needs to see that it was looked at and why, and the next submission is a
        /// new VERSION of the same document rather than a fresh mystery row.
        ///
        /// Archived is how a document leaves circulation; nothing here deletes a governed document.
        public string Status { get; set; } = "Active";

        // ---- lifecycle evidence (batch 3) ----------------------------------------------------------
        // WHO decided, WHEN, and WHY. Stored on the document rather than inferred from an audit trail,
        // because "is this verified" is a question the validity rule has to answer on every read, and
        // reconstructing it from events would make a checklist render depend on an event replay.
        //
        // A note is REQUIRED for a rejection and optional for a verification: telling somebody their
        // passport was refused without saying why is not a decision, it is an obstacle.
        public int? DecidedBy { get; set; }
        public DateTime? DecidedAt { get; set; }
        public string? DecisionNote { get; set; }

        public int CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    /// One immutable version of a document's binary. APPEND ONLY.
    ///
    /// A renewal or replacement never overwrites: it adds the next VersionNo and repoints the
    /// document's CurrentVersionId. The previous row — and the previous StorageKey — stay exactly as
    /// they were, which is what makes "what did the passport say in 2024" answerable at all.
    public class PlatformDocumentVersion
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long DocumentId { get; set; }

        /// 1-based and contiguous per document.
        public int VersionNo { get; set; }

        /// The opaque handle. NOT a path, and never treated as an authorization: possession of a
        /// StorageKey grants nothing, because every read re-runs the full owner authorization.
        public string StorageKey { get; set; } = "";

        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long SizeBytes { get; set; }

        /// Why this version exists — "renewed", "corrected scan", and so on. Free text on purpose: the
        /// reason is for a human reading the history, not for the platform to branch on.
        public string? Reason { get; set; }

        /// The version this one replaced, so a renewal chain is walkable in both directions.
        public long? ReplacesVersionId { get; set; }

        public int UploadedBy { get; set; }
        public DateTime UploadedAt { get; set; }
    }

    /// The configurable catalogue. What a "Passport" is belongs HERE, not in HR code.
    public class PlatformDocumentType
    {
        public long Id { get; set; }

        /// Null = a platform-wide type available to every company. Non-null = a company's own type.
        /// Nullable rather than duplicated per company so the shipped catalogue is not copied N times.
        public int? CompanyID { get; set; }

        public string Code { get; set; } = "";
        public string NameAr { get; set; } = "";
        public string NameEn { get; set; } = "";

        /// Comma-separated EntityRegistry codes this type may be attached to. A Passport belongs to an
        /// Employee and not to a Purchase Invoice, and the platform refuses the mismatch rather than
        /// trusting the caller to only ask sensible questions.
        public string AppliesToEntityTypes { get; set; } = "";

        public bool IsMandatory { get; set; }

        /// Whether the platform should require, and then act on, validity dates for this type.
        public bool RequiresIssueDate { get; set; }
        public bool RequiresExpiryDate { get; set; }

        /// The tier a document of this type gets unless the caller asks for a stricter one. A passport
        /// should not become Internal because whoever uploaded it left the field blank.
        public string DefaultConfidentiality { get; set; } = "Internal";

        /// Comma-separated lowercase extensions (".pdf,.png"). Empty = the platform default allow-list.
        public string? AllowedExtensions { get; set; }
        public long? MaxSizeBytes { get; set; }

        /// JSON object describing the type-specific metadata keys. Validated on write.
        public string? MetadataSchema { get; set; }

        /// May the SUBJECT of the record submit this type about themselves?
        ///
        /// Configuration, not code. "An employee may upload their own passport but not their own
        /// disciplinary letter" is a policy decision per document type, and putting it here is what
        /// stops the words Passport and CivilID appearing in a service. Default false: a type is
        /// HR-only until somebody deliberately opens it, because the safe default for a document
        /// nobody has classified is that its subject may not file it themselves.
        public bool SelfServiceAllowed { get; set; }

        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
    }

    /// The EF mapping, called from CommunicationModel.Configure so CrossDbContext keeps its one line.
    public static class PlatformDocumentModel
    {
        private const int CodeLength = 64;
        private const int VocabularyLength = 32;
        private const int NameLength = 200;
        private const int PathishLength = 400;

        public static void Configure(ModelBuilder builder)
        {
            builder.Entity<PlatformDocument>(e =>
            {
                e.ToTable("PlatformDocuments");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.EntityType).HasMaxLength(CodeLength).IsRequired();
                e.Property(x => x.Confidentiality).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.Status).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.DecisionNote).HasMaxLength(PathishLength);
                e.Property(x => x.DocumentNumber).HasMaxLength(NameLength);

                // The lookup every read performs: "the documents of THIS record, in THIS company".
                // Company leads because it is the boundary that must be cheap to enforce.
                e.HasIndex(x => new { x.CompanyID, x.EntityType, x.EntityId });

                // Expiry has to be answerable without scanning: the worker asks for documents expiring
                // in a window, per company, and a filtered index keeps that a seek rather than a sweep.
                e.HasIndex(x => new { x.CompanyID, x.ExpiryDate });
            });

            builder.Entity<PlatformDocumentVersion>(e =>
            {
                e.ToTable("PlatformDocumentVersions");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.StorageKey).HasMaxLength(CodeLength).IsRequired();
                e.Property(x => x.FileName).HasMaxLength(NameLength).IsRequired();
                e.Property(x => x.ContentType).HasMaxLength(NameLength).IsRequired();
                e.Property(x => x.Reason).HasMaxLength(PathishLength);

                // One row per (document, version). The DATABASE settles a concurrent double-replace, so
                // two simultaneous renewals cannot both claim V2 and silently lose one of the files.
                e.HasIndex(x => new { x.DocumentId, x.VersionNo }).IsUnique();
            });

            builder.Entity<PlatformDocumentType>(e =>
            {
                e.ToTable("PlatformDocumentTypes");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Code).HasMaxLength(CodeLength).IsRequired();
                e.Property(x => x.NameAr).HasMaxLength(NameLength).IsRequired();
                e.Property(x => x.NameEn).HasMaxLength(NameLength).IsRequired();
                e.Property(x => x.AppliesToEntityTypes).HasMaxLength(PathishLength).IsRequired();
                e.Property(x => x.DefaultConfidentiality).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.AllowedExtensions).HasMaxLength(PathishLength);
                e.Property(x => x.SelfServiceAllowed).HasDefaultValue(false);

                // A code is unique WITHIN its scope: one platform-wide "PASSPORT", and at most one
                // company override of that code per company.
                e.HasIndex(x => new { x.CompanyID, x.Code }).IsUnique();
            });
        }
    }
}
