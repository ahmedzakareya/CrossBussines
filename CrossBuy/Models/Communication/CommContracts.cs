namespace CrossBuy.Models.Communication
{
    // =============================================================================================
    // Communication Platform (CPS-001 §4) — the DTOs (module 25).
    //
    // These are the platform's OUTER contract. Nothing here is an EF entity: a caller never receives a
    // tracked row, and never receives a column it has no right to (a Restricted comment is filtered before
    // a DTO exists, not hidden by the UI). The split exists for the reason CLAUDE.md states as a permanent
    // rule — "Reads are DB truth… never assert from a tracked entity" — and because the entity shape is
    // free to change behind an unchanged DTO.
    //
    // Bilingual display text follows the parallel team's feature-module convention (Ar + En twin fields)
    // rather than a single culture-resolved string, so ONE query result can serve an Arabic and an English
    // caller and a cached DTO is not culture-poisoned.
    // =============================================================================================

    // ---------------------------------------------------------------------------------------------
    // Paging. A cursor, not an offset.
    //
    // Offset paging over an append-heavy table double-shows and skips rows: a comment posted between page 1
    // and page 2 shifts every later row by one. The cursor is the last seen id, which is stable under
    // insertion. Same reasoning ADR-003 gives for banning EventId cursors in the DISPATCHER — note the
    // difference: there the cursor was wrong because ids become visible out of order at COMMIT; here the
    // read is of already-committed rows ordered by id, which is exactly what a cursor is safe for.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommPage<T>
    {
        public required IReadOnlyList<T> Items { get; init; }

        // Pass back as `AfterId` to get the next page. Null when the last page has been reached.
        public long? NextCursor { get; init; }

        public bool HasMore => NextCursor.HasValue;

        public static CommPage<T> Empty() => new() { Items = Array.Empty<T>(), NextCursor = null };
    }

    public sealed class CommPageRequest
    {
        // Exclusive lower bound on the sort key. Null starts at the beginning.
        public long? AfterId { get; init; }

        // Clamped by CommunicationPlatformOptions.ClampPageSize — a caller cannot ask for everything.
        public int? PageSize { get; init; }

        // Newest-first is the timeline default; oldest-first is the comment-thread default (a conversation
        // reads downwards). Each service documents which it uses when this is null.
        public bool? NewestFirst { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // Actors and principals
    // ---------------------------------------------------------------------------------------------

    // A person as the platform displays them. Resolved ONCE per read batch, never per row.
    public sealed class CommActorDto
    {
        public required int EmployeeId { get; init; }
        public string? NameAr { get; init; }
        public string? NameEn { get; init; }
        public string? AvatarUrl { get; init; }

        // Culture-resolved convenience for a caller that does not want to choose. Falls back across
        // languages before falling back to "#id", so a row never renders as blank.
        public string Display(bool arabic) =>
            (arabic ? (NameAr ?? NameEn) : (NameEn ?? NameAr)) ?? ("#" + EmployeeId);
    }

    public sealed class CommPrincipalRef
    {
        public required string Kind { get; init; }        // CommPrincipalKind
        public int? Id { get; init; }                     // employee / manager / org-node id
        public string? Key { get; init; }                 // role name, for Kind = Role
        public string? Label { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // Threads (modules 1, 2, 34, 35)
    // ---------------------------------------------------------------------------------------------
    public sealed class CommThreadDto
    {
        public required long ThreadId { get; init; }
        public required CommEntityRef Entity { get; init; }
        public required string Kind { get; init; }               // CommThreadKind
        public required string ThreadKey { get; init; }          // "" for the entity's default thread
        public string? SubjectAr { get; init; }
        public string? SubjectEn { get; init; }
        public required string Visibility { get; init; }         // CommVisibility — the thread's CEILING
        public required bool IsLocked { get; init; }
        public string? LockedReason { get; init; }
        public required int CommentCount { get; init; }
        public required int ParticipantCount { get; init; }
        public DateTime? LastActivityAt { get; init; }
        public DateTime? CreatedAt { get; init; }
        public CommActorDto? CreatedBy { get; init; }

        // What the CALLER may do here, resolved by ICommAccessPolicy. Present so a UI never has to guess,
        // and so "hiding a control" is a rendering consequence of a real decision rather than the control
        // itself — CLAUDE.md: "Hiding a UI control is not a control."
        public required CommThreadCapabilities Capabilities { get; init; }
    }

    public sealed class CommThreadCapabilities
    {
        public required bool CanRead { get; init; }
        public required bool CanComment { get; init; }
        public required bool CanModerate { get; init; }

        // EXACTLY the visibilities this caller may post at in this thread, so a UI offers a list rather than
        // deriving one.
        //
        // TWO INDEPENDENT BOUNDS produce this set, and they pull in OPPOSITE directions — which is why one
        // "max" value cannot express it:
        //   * NOT MORE OPEN THAN THE THREAD. A Confidential thread may not carry an Internal comment: that
        //     would break the promise made to whoever opened it at that tier.
        //   * NOT MORE RESTRICTED THAN THE CALLER CAN READ. Offering Confidential to somebody who cannot read
        //     Confidential would let them write something they immediately lose sight of.
        // Collapsing the two into a single bound rejects the ordinary case (an Internal comment on an Internal
        // thread), which is exactly what happened before this was split.
        public required IReadOnlyList<string> AuthorVisibilities { get; init; }

        // The most OPEN member of AuthorVisibilities — the sensible default for a compose box.
        public required string MostOpenAuthorVisibility { get; init; }

        public static CommThreadCapabilities None() => new()
        {
            CanRead = false, CanComment = false, CanModerate = false,
            AuthorVisibilities = Array.Empty<string>(),
            MostOpenAuthorVisibility = CommVisibility.Internal,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Comments (modules 1, 8, 9, 10, 28, 29, 30)
    // ---------------------------------------------------------------------------------------------
    public sealed class CommCommentDto
    {
        public required long CommentId { get; init; }
        public required long ThreadId { get; init; }
        public long? ParentCommentId { get; init; }
        public required int Depth { get; init; }

        // The AUTHORED body, in its authored format. Never pre-rendered HTML — see CommBodyFormat.
        public required string Body { get; init; }
        public required string BodyFormat { get; init; }
        public required string Visibility { get; init; }

        public required CommActorDto Author { get; init; }
        public DateTime? CreatedAt { get; init; }

        // Edit history summary (modules 28, 30). RevisionCount is 0 for a never-edited comment, so a UI can
        // render "edited" without loading the revisions.
        public DateTime? EditedAt { get; init; }
        public required int RevisionCount { get; init; }
        public CommActorDto? LastEditedBy { get; init; }

        // Soft delete (module 29). A deleted comment is RETURNED, with Body blanked, when the caller may
        // moderate — a thread that silently loses a row reads as corruption, and the audit trail already
        // knows it existed. Ordinary readers never see the row at all.
        public required bool IsDeleted { get; init; }
        public DateTime? DeletedAt { get; init; }
        public CommActorDto? DeletedBy { get; init; }

        public required IReadOnlyList<CommMentionDto> Mentions { get; init; }
        public required IReadOnlyList<CommAttachmentDto> Attachments { get; init; }
        public required IReadOnlyList<CommReactionSummaryDto> Reactions { get; init; }

        // Structural inventory of the body, computed by ICommBodyPolicy at write time and stored. Lets a
        // list screen show "3 images, 1 table" without parsing every body on read.
        public CommBodyAnalysisDto? BodyAnalysis { get; init; }

        public required CommCommentCapabilities Capabilities { get; init; }
    }

    public sealed class CommCommentCapabilities
    {
        public required bool CanEdit { get; init; }
        public required bool CanDelete { get; init; }
        public required bool CanRestore { get; init; }
        public required bool CanReact { get; init; }
        public required bool CanReply { get; init; }
    }

    public sealed class CommCommentRevisionDto
    {
        public required long RevisionId { get; init; }
        public required long CommentId { get; init; }
        public required int RevisionNo { get; init; }
        public required string Body { get; init; }
        public required string BodyFormat { get; init; }
        public required CommActorDto EditedBy { get; init; }
        public DateTime? EditedAt { get; init; }
        public string? Reason { get; init; }
    }

    // Structural facts about a markdown body (module 10 + the "Images / Files / Links / Code Blocks /
    // Tables" requirement). Counts, never content — this is metadata for a list screen, not a parse tree.
    public sealed class CommBodyAnalysisDto
    {
        public required int Bytes { get; init; }
        public required int LinkCount { get; init; }
        public required int ImageCount { get; init; }
        public required int CodeBlockCount { get; init; }
        public required int TableCount { get; init; }
        public required int MentionTokenCount { get; init; }
        public required bool HasHeading { get; init; }
        public required bool HasList { get; init; }
        public required bool HasBlockQuote { get; init; }

        // External hosts referenced by links/images, deduped. Recorded because a comment body is a vector
        // for exfiltration-by-image-src, and a deployment that wants to allow-list hosts needs to know
        // which ones appear before it can.
        public required IReadOnlyList<string> LinkHosts { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // Mentions (modules 3, 13, 15)
    // ---------------------------------------------------------------------------------------------
    public sealed class CommMentionDto
    {
        public required long MentionId { get; init; }
        public required long CommentId { get; init; }
        public required string TargetKind { get; init; }     // CommMentionTargetKind
        public int? TargetId { get; init; }
        public string? TargetKey { get; init; }              // role name, for Kind = Role
        public string? LabelAr { get; init; }
        public string? LabelEn { get; init; }

        // How many employees the target resolved to at AUTHORING time. Stored, not recomputed: a department
        // that gained ten members next month did not receive this mention, and a recomputed number would
        // claim it did.
        public required int ResolvedRecipientCount { get; init; }
        public DateTime? CreatedAt { get; init; }
    }

    // One row of "where have I been mentioned" (module 15).
    public sealed class CommMentionHistoryItemDto
    {
        public required long MentionId { get; init; }
        public required long CommentId { get; init; }
        public required long ThreadId { get; init; }
        public required CommEntityRef Entity { get; init; }
        public required string TargetKind { get; init; }

        // How this employee came to be a recipient: directly, or via the team/department that was mentioned.
        public required string ViaKind { get; init; }
        public required CommActorDto MentionedBy { get; init; }

        // A short plain-text lead-in, not the whole body — a mention list must not become a way to read
        // comments the caller cannot open. Access is re-checked when the caller navigates to the thread.
        public string? Excerpt { get; init; }
        public DateTime? CreatedAt { get; init; }
        public DateTime? ReadAt { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // Reactions, attachments, participation, read status (modules 4, 5, 6, 7, 11, 14)
    // ---------------------------------------------------------------------------------------------
    public sealed class CommReactionSummaryDto
    {
        public required string ReactionKey { get; init; }
        public required int Count { get; init; }
        public required bool Mine { get; init; }

        // Capped sample for a tooltip. Never the full list — a 200-reaction comment must not return 200
        // employee names to render a hover.
        public required IReadOnlyList<CommActorDto> Sample { get; init; }
    }

    public sealed class CommAttachmentDto
    {
        public required long AttachmentId { get; init; }
        public required long CommentId { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public required long SizeBytes { get; init; }

        // Opaque handle into whatever file store the deployment uses. This platform stores NO bytes and
        // resolves NO url — see ADR-030 §8. A UI turns this into a download link through the file module.
        public required string StorageKey { get; init; }

        public required CommFilePreviewDto Preview { get; init; }
        public required CommActorDto UploadedBy { get; init; }
        public DateTime? UploadedAt { get; init; }
        public required bool IsDeleted { get; init; }
    }

    // Module 11. Classification, not rendering: what KIND of preview is possible and whether the platform
    // believes it is safe to inline.
    public sealed class CommFilePreviewDto
    {
        public required string Kind { get; init; }               // CommPreviewKind
        public required bool CanInline { get; init; }
        public string? ThumbnailStorageKey { get; init; }
        public string? Reason { get; init; }                     // why not, when CanInline is false
    }

    public sealed class CommParticipantDto
    {
        public required long ParticipantId { get; init; }
        public required long ThreadId { get; init; }
        public required CommActorDto Employee { get; init; }
        public required string Role { get; init; }               // CommParticipantRole
        public required string Source { get; init; }             // CommParticipationSource
        public required bool IsMuted { get; init; }
        public DateTime? CreatedAt { get; init; }
    }

    public sealed class CommReadStatusDto
    {
        public required long ThreadId { get; init; }
        public required int EmployeeId { get; init; }
        public long? LastReadCommentId { get; init; }
        public DateTime? LastReadAt { get; init; }

        // Unread count is computed against what the caller may READ, not against the raw row count —
        // otherwise a Confidential comment the caller cannot see would show as an unread they can never clear.
        public required int UnreadCount { get; init; }
        public required int UnreadMentionCount { get; init; }
    }

    public sealed class CommThreadPermissionDto
    {
        public required long PermissionId { get; init; }
        public required long ThreadId { get; init; }
        public required CommPrincipalRef Principal { get; init; }
        public required string Level { get; init; }              // CommPermissionLevel
        public required CommActorDto GrantedBy { get; init; }
        public DateTime? GrantedAt { get; init; }
    }

    // ---------------------------------------------------------------------------------------------
    // Write requests. Separate types from the DTOs, so a caller cannot post a server-owned field
    // (author, timestamps, counts) by reusing the read shape.
    // ---------------------------------------------------------------------------------------------
    public sealed class CommThreadRequest
    {
        public required CommEntityRef Entity { get; init; }
        public string Kind { get; init; } = CommThreadKind.Discussion;

        // "" is the entity's default thread. A named key creates a second, independent thread on the same
        // entity — that is how module 2 (Discussion Threads) exists without a second table.
        public string ThreadKey { get; init; } = "";

        public string? SubjectAr { get; init; }
        public string? SubjectEn { get; init; }

        // The thread's CEILING, not a default: no comment in it may be more open than this.
        public string Visibility { get; init; } = CommVisibility.Internal;
    }

    public sealed class CommCommentRequest
    {
        public required CommEntityRef Entity { get; init; }

        // Either an existing thread, or (Kind, ThreadKey) for get-or-create. Supplying both is an error the
        // service reports rather than silently preferring one.
        public long? ThreadId { get; init; }
        public string? ThreadKind { get; init; }
        public string? ThreadKey { get; init; }

        public long? ParentCommentId { get; init; }

        public required string Body { get; init; }
        public string BodyFormat { get; init; } = CommBodyFormat.Markdown;
        public string Visibility { get; init; } = CommVisibility.Internal;

        // Mentions may be supplied EXPLICITLY here, parsed from the body, or both — the service unions and
        // dedupes them. Explicit mentions exist because a picker UI knows the target id without the author
        // having typed a token.
        public IReadOnlyList<CommMentionRequest>? Mentions { get; init; }

        public IReadOnlyList<CommAttachmentRequest>? Attachments { get; init; }

        // Caller-supplied idempotency key, scoped per company. A retried POST with the same key returns the
        // ORIGINAL comment instead of a duplicate — the same contract BusinessEventRecord.DedupKey offers.
        // A mobile client on a flaky connection is the reason this exists.
        public string? DedupKey { get; init; }
    }

    public sealed class CommMentionRequest
    {
        public required string TargetKind { get; init; }
        public int? TargetId { get; init; }
        public string? TargetKey { get; init; }
    }

    public sealed class CommAttachmentRequest
    {
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public required long SizeBytes { get; init; }

        // The file must ALREADY be in the deployment's file store; this platform never receives bytes.
        public required string StorageKey { get; init; }
    }

    public sealed class CommCommentEditRequest
    {
        public required long CommentId { get; init; }
        public required string Body { get; init; }
        public string? BodyFormat { get; init; }

        // Null leaves the visibility unchanged. Changing it is a moderator action when the comment is not
        // the caller's own.
        public string? Visibility { get; init; }

        public string? Reason { get; init; }
    }
}