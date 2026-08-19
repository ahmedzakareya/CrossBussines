namespace CrossBuy.Models.Context.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-035) — comment aggregate: the comment, its revisions, its attachments,
    // its reactions. Company-isolation and soft-delete notes: see CommThreadEntities.cs.
    // =============================================================================================

    // One comment, reply, internal note or public note. There is exactly ONE comment table — internal and
    // public notes differ by Visibility, replies differ by ParentCommentId. Modelling them as separate
    // tables would duplicate revisions, mentions, attachments and reactions three times over.
    public class CommComment : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long ThreadId { get; set; }

        // Denormalised anchor. It duplicates CommThread.EntityType/EntityId ON PURPOSE: the mention-history
        // and inbox reads answer "which record was I pulled into" and would otherwise join CommThreads on
        // every row. The pair is written once at insert and never updated (a comment cannot move records),
        // so there is no divergence window.
        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }

        // Null for a top-level comment. Depth is stored rather than walked because the read path pages over
        // comments and cannot afford a recursive CTE per page; it is bounded by MaxReplyDepth on write.
        public long? ParentCommentId { get; set; }
        public int Depth { get; set; }

        // The AUTHORED body in its authored format. Never pre-rendered HTML — see CommBodyFormat for why
        // accepting HTML would mean owning a sanitizer forever.
        public string Body { get; set; } = "";
        public string BodyFormat { get; set; } = "";      // CommBodyFormat
        public string Visibility { get; set; } = "";      // CommVisibility

        public int AuthorEmployeeId { get; set; }

        // Edit history summary (modules 28, 30). RevisionCount is 0 for a never-edited comment; each edit
        // writes a CommCommentRevision holding the PREVIOUS body and increments this.
        public int RevisionCount { get; set; }
        public DateTime? EditedAt { get; set; }
        public int? EditedBy { get; set; }

        // Soft delete (module 29). The row survives so the audit trail, revisions and the reply chain below
        // it stay intact — deleting a parent comment must not orphan its replies.
        public DateTime? DeletedAt { get; set; }
        public int? DeletedBy { get; set; }

        // Structural inventory of the body, computed once at write time by ICommBodyPolicy and stored as
        // JSON. Read by list screens so they never re-parse 50 markdown bodies to say "2 images, 1 table".
        public string? BodyAnalysisJson { get; set; }

        // Denormalised counters, maintained in the writing transaction.
        public int MentionCount { get; set; }
        public int AttachmentCount { get; set; }
        public int ReactionCount { get; set; }
        public int ReplyCount { get; set; }

        // Caller-supplied idempotency key, unique per company where present. A retried POST returns the
        // ORIGINAL comment — the same contract BusinessEventRecord.DedupKey offers, and the reason a mobile
        // client on a flaky connection cannot double-post.
        public string? DedupKey { get; set; }
    }

    // One PREVIOUS version of a comment body (modules 28, 30).
    //
    // Append-only, and it stores the body as it was BEFORE the edit that created this row. Storing the new
    // body instead would make the current comment's text appear twice and leave the original unrecoverable —
    // the opposite of what an edit history is for.
    public class CommCommentRevision : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long CommentId { get; set; }

        // 1 for the first edit's snapshot (i.e. the original text), incrementing thereafter.
        public int RevisionNo { get; set; }

        public string Body { get; set; } = "";
        public string BodyFormat { get; set; } = "";

        // The visibility the comment had at that revision. Included because a visibility change is an edit
        // that matters more than a wording change, and an audit reader needs to see it in the same place.
        public string Visibility { get; set; } = "";

        public int EditedByEmployeeId { get; set; }
        public DateTime? EditedAt { get; set; }
        public string? Reason { get; set; }
    }

    // An attachment on a comment (module 7).
    //
    // THIS PLATFORM STORES NO BYTES. StorageKey is an opaque handle into whatever file store the deployment
    // uses; resolving it to a URL, streaming it, and enforcing download authorization belong to that store.
    // Keeping bytes out is what lets this table live next to FileManagerService without competing with it.
    //
    // NOTE: the name is CommCommentAttachment, not CommAttachment, because Models/Context/Comm already has a
    // CommAttachment (an EMAIL attachment on CommMessage, owned by the parallel team). Two different things
    // must not share a name in one context.
    public class CommCommentAttachment : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long CommentId { get; set; }
        public long ThreadId { get; set; }

        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long SizeBytes { get; set; }

        public string StorageKey { get; set; } = "";

        // Preview classification (module 11), computed at write time by ICommFilePreviewProvider. Stored so
        // a list screen does not re-classify, and so a later change to the classifier does not silently
        // restate what was already shown to users.
        public string PreviewKind { get; set; } = "";     // CommPreviewKind
        public bool CanInline { get; set; }
        public string? ThumbnailStorageKey { get; set; }

        public int UploadedByEmployeeId { get; set; }

        public DateTime? DeletedAt { get; set; }
        public int? DeletedBy { get; set; }
    }

    // One reaction by one employee on one comment (module 6).
    //
    // Unique on (CommentId, EmployeeId, ReactionKey): the same person may add "like" and "question" but not
    // "like" twice. ReactionKey is validated against the frozen CommReactionKeys set, not free-text emoji —
    // see that class for why an open unicode range costs a reporting column.
    public class CommReaction : BaseEntity
    {
        public long Id { get; set; }
        public int CompanyID { get; set; }
        public long CommentId { get; set; }
        public long ThreadId { get; set; }

        public int EmployeeId { get; set; }
        public string ReactionKey { get; set; } = "";     // CommReactionKeys
    }
}