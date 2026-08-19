namespace CrossBuy.Models.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-036) — the AGGREGATED TIMELINE contracts (modules 12, 32, 35).
    //
    // The platform kernel already owns ONE timeline: ITimelineProjectionService, which reads BusinessEvents
    // and merges legacy adapters. This platform does NOT replace it and does NOT read BusinessEvents
    // directly — doing either would put a second, disagreeing timeline in the product, which is the exact
    // failure ADR-002 was written about (three vocabularies for one concept).
    //
    // Instead: ICommTimelineAggregator merges N ICommTimelineSource contributors, and the kernel is ONE of
    // them, behind an adapter that calls the kernel's own service so the kernel's four filters (company,
    // branch, permission, visibility) still apply. Comments/mentions/attachments are another. A future
    // module adds a source; nothing else changes.
    // =============================================================================================

    // Where a timeline item came from. Frozen so a UI can group and filter by source, and so an operator
    // reading a merged timeline knows which subsystem to look in.
    public static class CommTimelineSourceKinds
    {
        // A durable business fact from the platform kernel (BusinessEvents), read through
        // ITimelineProjectionService. Only available for entities whose registry definition sets
        // SupportsTimeline — the kernel throws otherwise, so the adapter checks first and contributes nothing.
        public const string BusinessEvent = "BusinessEvent";

        // A comment, reply or note authored in this platform.
        public const string Comment = "Comment";

        // An @mention, surfaced as its own item so "who was pulled into this record, and when" is answerable
        // without reading every comment body.
        public const string Mention = "Mention";

        // An attachment added to the record's conversation.
        public const string Attachment = "Attachment";

        // A communication audit fact worth showing on the record (thread locked, comment deleted).
        // Deliberately NOT the whole audit trail: most audit rows are for an auditor, not a record screen.
        public const string CommAudit = "CommAudit";

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { BusinessEvent, Comment, Mention, Attachment, CommAudit };

        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    public sealed class CommTimelineQuery
    {
        public required CommEntityRef Entity { get; init; }

        // Null = every registered source. A named subset lets a screen render "comments only" without the
        // aggregator having to fetch and discard the rest.
        public IReadOnlyList<string>? Sources { get; init; }

        public int? Take { get; init; }

        // Exclusive upper bound on OccurredAt — the cursor for newest-first paging. A timestamp rather than
        // an id because the merged stream has no single id space: two sources number their rows
        // independently, so an id cursor is meaningless across them.
        public DateTime? Before { get; init; }

        // Tie-break companion to Before. Two items sharing a timestamp (a comment and its mention, written
        // in one transaction) would otherwise page inconsistently.
        public string? BeforeKey { get; init; }

        public bool IncludeDeleted { get; init; } = false;
    }

    // ONE merged item. Deliberately shaped like the kernel's TimelineItemViewModel (bilingual title,
    // description, actor, icon, colour, url) so a single UI component can render both streams — but it is a
    // SEPARATE type, because inheriting or reusing the kernel's would couple this platform's read contract
    // to a file another team owns.
    public sealed class CommTimelineItem
    {
        // Stable identity within the merged stream: "<Source>:<sourceRowId>". Used as the paging tie-break
        // key and for de-duplication when two sources describe the same fact.
        public required string ItemKey { get; init; }

        public required string Source { get; init; }            // CommTimelineSourceKinds
        public required CommEntityRef Entity { get; init; }

        // The source's own type string — a kernel event type, a CommEventTypes value, or an audit action.
        // Kept raw so a caller can key behaviour off it without the aggregator having to flatten three
        // vocabularies into one lossy enum.
        public required string ItemType { get; init; }

        public string? TitleAr { get; init; }
        public string? TitleEn { get; init; }
        public string? DescriptionAr { get; init; }
        public string? DescriptionEn { get; init; }

        // Present ONLY for Comment items and ONLY when the caller may read that comment. The aggregator
        // filters before it projects, so a body here is already authorized.
        public string? Body { get; init; }
        public string? BodyFormat { get; init; }

        public int? ActorEmployeeId { get; init; }
        public string? ActorDisplayName { get; init; }

        public string? Icon { get; init; }
        public string? Color { get; init; }

        public required DateTime OccurredAt { get; init; }

        public string? Visibility { get; init; }

        // Deep link to the anchor record, from IEntityRegistry.BuildUrl. Null when the entity has no screen.
        public string? Url { get; init; }

        public long? ThreadId { get; init; }
        public long? CommentId { get; init; }

        public bool IsDeleted { get; init; }
    }

    public sealed class CommTimelineResult
    {
        public required IReadOnlyList<CommTimelineItem> Items { get; init; }

        // Pass back as (Before, BeforeKey). Null when the merged stream is exhausted.
        public DateTime? NextBefore { get; init; }
        public string? NextBeforeKey { get; init; }

        // Per-source diagnostics. Present because a merged timeline that silently returns nothing from one
        // source is indistinguishable from a source that had nothing to say — and CLAUDE.md's rule about
        // unbound workers ("passes by examining zero rows") is the same failure in a different subsystem.
        public required IReadOnlyList<CommTimelineSourceReport> SourceReports { get; init; }

        public static CommTimelineResult Empty() => new()
        {
            Items = Array.Empty<CommTimelineItem>(),
            SourceReports = Array.Empty<CommTimelineSourceReport>(),
        };
    }

    public sealed class CommTimelineSourceReport
    {
        public required string Source { get; init; }
        public required bool Contributed { get; init; }
        public required int ItemCount { get; init; }

        // Why a source contributed nothing: "not applicable" (entity has no kernel timeline), "denied"
        // (caller lacks the right), "empty" (nothing to say), or an error message. A source that THROWS does
        // not fail the whole timeline — it is reported and skipped, because one broken contributor must not
        // blank a record's history.
        public string? Note { get; init; }
    }
}