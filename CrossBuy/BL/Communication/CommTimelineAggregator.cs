using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-036) — THE TIMELINE AGGREGATOR (modules 12, 32, 35).
    //
    // IT DOES NOT READ BusinessEvents. That is the single most important thing about this file.
    //
    // The platform kernel already owns one timeline (ITimelineProjectionService) and applies four filters —
    // company, branch, module permission, event visibility. Reading BusinessEvents directly here would
    // duplicate all four, and the copy would drift: the day the kernel adds a fifth filter, this platform
    // would quietly keep showing what the kernel had decided to hide. So the kernel is ONE SOURCE among
    // several, reached through its own service, and its filters keep applying.
    //
    // NO PROJECTION TABLE. A timeline is a READ over facts that already exist. Materialising rows would be a
    // second copy of the same truth, free to disagree with it — the kernel avoided this by returning "a
    // PROJECTION, not events", and this platform holds the same line.
    //
    // A BROKEN SOURCE MUST NOT BLANK A RECORD'S HISTORY. Each source is awaited independently; a throw is
    // caught, reported in SourceReports, and the other sources still render. The alternative — one failing
    // contributor producing an empty timeline — looks exactly like "this record has no history", which is the
    // "passes by examining zero rows" failure CLAUDE.md records for unbound workers, in a different subsystem.
    // =============================================================================================
    public interface ICommTimelineAggregator
    {
        Task<CommTimelineResult> GetAsync(
            BusinessContext context, CommTimelineQuery query, CancellationToken cancellationToken = default);
    }

    // ---------------------------------------------------------------------------------------------
    // THE EXTENSION POINT. Injected as IEnumerable, the same shape the kernel uses for
    // ILegacyTimelineAdapter — a new contributor is one class and one registration.
    // ---------------------------------------------------------------------------------------------
    public interface ICommTimelineSource
    {
        // One of CommTimelineSourceKinds.
        string Source { get; }

        // Return AT MOST `take` items strictly older than `before` (when supplied), newest first.
        // May return empty. MUST NOT throw for an authorization condition — return empty with a note instead;
        // a throw is for an infrastructure failure.
        Task<CommTimelineSourceResult> GetAsync(
            BusinessContext context, CommEntityRef entity, DateTime? before, int take, bool includeDeleted,
            CancellationToken cancellationToken = default);
    }

    public sealed class CommTimelineSourceResult
    {
        public required IReadOnlyList<CommTimelineItem> Items { get; init; }

        // Why the source contributed nothing, when it did. Surfaced verbatim in CommTimelineSourceReport.
        public string? Note { get; init; }

        public static CommTimelineSourceResult Of(IReadOnlyList<CommTimelineItem> items, string? note = null)
            => new() { Items = items, Note = note };

        public static CommTimelineSourceResult None(string note)
            => new() { Items = Array.Empty<CommTimelineItem>(), Note = note };
    }

    // ---------------------------------------------------------------------------------------------
    public sealed class CommTimelineAggregator : ICommTimelineAggregator
    {
        private readonly IEnumerable<ICommTimelineSource> _sources;
        private readonly ICommAccessPolicy _access;
        private readonly CommunicationPlatformOptions _options;
        private readonly ILogger<CommTimelineAggregator> _log;

        public CommTimelineAggregator(
            IEnumerable<ICommTimelineSource> sources,
            ICommAccessPolicy access,
            IOptions<CommunicationPlatformOptions> options,
            ILogger<CommTimelineAggregator> log)
        {
            _sources = sources;
            _access = access;
            _options = options.Value;
            _log = log;
        }

        public async Task<CommTimelineResult> GetAsync(
            BusinessContext context, CommTimelineQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(query);

            if (!query.Entity.IsWellFormed) return CommTimelineResult.Empty();

            // The ENTITY gate is applied once, here, before any source runs. Each source still applies its own
            // finer rules (visibility, thread grants, the kernel's module permission), but none of them has to
            // re-answer "may this caller see this record at all".
            var entityDecision = await _access.CanAccessEntityAsync(
                context, query.Entity, CommCapabilities.Comments, cancellationToken);

            // A denied entity returns EMPTY with a report rather than throwing. A record timeline is a widget on
            // a page; a caller who may not see it should get an empty widget, and the reason belongs in the
            // report, not in an exception a controller has to translate.
            if (!entityDecision.Allowed)
                return new CommTimelineResult
                {
                    Items = Array.Empty<CommTimelineItem>(),
                    SourceReports = new[]
                    {
                        new CommTimelineSourceReport
                        {
                            Source = "*", Contributed = false, ItemCount = 0,
                            Note = "denied: " + entityDecision.Reason,
                        },
                    },
                };

            int take = _options.ClampPageSize(query.Take);
            var requested = query.Sources;

            var selected = _sources
                .Where(s => requested == null || requested.Contains(s.Source, StringComparer.Ordinal))
                .GroupBy(s => s.Source, StringComparer.Ordinal)

                // LAST registration wins per source kind, so a deployment can replace a built-in contributor by
                // registering its own after ours — the standard override idiom. Two contributors for one kind
                // would double every item.
                .Select(g => g.Last())
                .ToList();

            var reports = new List<CommTimelineSourceReport>(selected.Count);
            var items = new List<CommTimelineItem>();

            foreach (var source in selected)
            {
                try
                {
                    // Each source is asked for a FULL PAGE, not a share of one. The merged page is then cut to
                    // `take`. Asking each for take/N would drop items from a busy source whenever a quiet one
                    // had nothing — the merge has to choose from a full candidate set to be correct.
                    var result = await source.GetAsync(
                        context, query.Entity, query.Before, take, query.IncludeDeleted, cancellationToken);

                    items.AddRange(result.Items);
                    reports.Add(new CommTimelineSourceReport
                    {
                        Source = source.Source,
                        Contributed = result.Items.Count > 0,
                        ItemCount = result.Items.Count,
                        Note = result.Note ?? (result.Items.Count == 0 ? "empty" : null),
                    });
                }
                catch (Exception ex)
                {
                    // ONE BROKEN SOURCE MUST NOT BLANK THE TIMELINE. Logged as a warning and reported, because a
                    // source that silently contributed nothing is indistinguishable from a record with no history.
                    _log.LogWarning(ex,
                        "Timeline source {Source} failed for {Entity}; the remaining sources still rendered.",
                        source.Source, query.Entity.Key);

                    reports.Add(new CommTimelineSourceReport
                    {
                        Source = source.Source, Contributed = false, ItemCount = 0,
                        Note = "error: " + ex.GetType().Name + ": " + ex.Message,
                    });
                }
            }

            // Newest first, with ItemKey as the tie-break. The tie-break is not cosmetic: a comment and its
            // mention are written in ONE transaction and can share a timestamp to the tick, so without a
            // deterministic second key the two would swap places between pages and the cursor would skip or
            // repeat one of them.
            var ordered = items
                .OrderByDescending(i => i.OccurredAt)
                .ThenByDescending(i => i.ItemKey, StringComparer.Ordinal)
                .ToList();

            // The cursor is applied AFTER the merge as well as inside each source. A source that ignores
            // `before` (a third-party contributor) cannot then leak already-seen items into a later page.
            if (query.Before.HasValue)
                ordered = ordered
                    .Where(i => i.OccurredAt < query.Before.Value
                                || (i.OccurredAt == query.Before.Value
                                    && string.Compare(i.ItemKey, query.BeforeKey ?? "", StringComparison.Ordinal) < 0))
                    .ToList();

            bool hasMore = ordered.Count > take;
            var page = ordered.Take(take).ToList();

            return new CommTimelineResult
            {
                Items = page,
                NextBefore = hasMore && page.Count > 0 ? page[^1].OccurredAt : null,
                NextBeforeKey = hasMore && page.Count > 0 ? page[^1].ItemKey : null,
                SourceReports = reports,
            };
        }
    }

    // =============================================================================================
    // SOURCE 1 — the platform kernel's business events, through the kernel's OWN service.
    // =============================================================================================
    public sealed class PlatformEventTimelineSource : ICommTimelineSource
    {
        private readonly ITimelineProjectionService _kernel;
        private readonly IEntityRegistry _registry;

        public PlatformEventTimelineSource(ITimelineProjectionService kernel, IEntityRegistry registry)
        { _kernel = kernel; _registry = registry; }

        public string Source => CommTimelineSourceKinds.BusinessEvent;

        public async Task<CommTimelineSourceResult> GetAsync(
            BusinessContext context, CommEntityRef entity, DateTime? before, int take, bool includeDeleted,
            CancellationToken cancellationToken = default)
        {
            // ITimelineProjectionService THROWS for an entity whose SupportsTimeline is false. Only four codes
            // qualify today, so asking first is the difference between "this source does not apply" and an
            // exception on most records in the product.
            //
            // THE GUARD READS THE REGISTRY DIRECTLY, NOT ICommEntitySurface — and that distinction is the whole
            // point. The surface deliberately treats CommunicationPlatform:EnabledEntityCodes as an ADDITIVE
            // onboarding path for THIS platform's own tables (ADR-031). It has no authority over the kernel: a
            // configuration entry cannot make ITimelineProjectionService accept an entity the kernel has not
            // onboarded. Routing this check through the surface made a configured-but-not-registered entity look
            // eligible and turned the kernel's refusal into a caught exception reported as "error:" — which is
            // a working timeline reported as broken. The tests caught it.
            if (!_registry.TryGetDefinition(entity.EntityCode, out var definition) || definition == null)
                return CommTimelineSourceResult.None(
                    $"not applicable: '{entity.EntityCode}' is not registered in IEntityRegistry");

            if (!definition.SupportsTimeline)
                return CommTimelineSourceResult.None(
                    "not applicable: IEntityRegistry.SupportsTimeline is false for this entity, so the kernel " +
                    "timeline would refuse the read");

            try
            {
                var kernelItems = await _kernel.GetAsync(entity.EntityCode, entity.EntityId, context, take, cancellationToken);

                var items = kernelItems
                    .Where(i => !before.HasValue || i.CreatedAt < before.Value)
                    .Select(i => new CommTimelineItem
                    {
                        // The event's own UID makes the key stable across pages and across processes.
                        ItemKey = CommTimelineSourceKinds.BusinessEvent + ":" + i.EventUid.ToString("N"),
                        Source = CommTimelineSourceKinds.BusinessEvent,
                        Entity = entity,
                        ItemType = i.EventType,
                        TitleAr = i.TitleAr,
                        TitleEn = i.TitleEn,
                        DescriptionAr = i.DescriptionAr,
                        DescriptionEn = i.DescriptionEn,
                        ActorEmployeeId = i.ActorEmployeeId,
                        ActorDisplayName = i.ActorDisplayName,
                        Icon = i.Icon,
                        Color = i.Color,
                        OccurredAt = i.CreatedAt,

                        // The kernel's visibility vocabulary is carried through UNTRANSLATED. Mapping it into
                        // CommVisibility would claim a tier the kernel never assigned — and the two sets are
                        // deliberately different (the kernel has System, this platform has Public).
                        Visibility = i.Visibility,
                        Url = i.Url,
                    })
                    .ToList();

                return CommTimelineSourceResult.Of(items);
            }
            catch (PlatformAccessDeniedException ex)
            {
                // The kernel refuses the whole entity. Reported, not thrown: the caller may still be entitled to
                // the comment stream, and one denied source must not blank the others.
                return CommTimelineSourceResult.None("denied by the kernel: " + ex.Message);
            }
        }
    }

    // =============================================================================================
    // SOURCE 2 — comments authored in this platform.
    // =============================================================================================
    public sealed class CommCommentTimelineSource : ICommTimelineSource
    {
        private readonly CommDb _db;
        private readonly ICommAccessPolicy _access;
        private readonly ICommActorDirectory _actors;
        private readonly ICommBodyPolicy _body;

        public CommCommentTimelineSource(
            CrossDbContext db, ICommAccessPolicy access, ICommActorDirectory actors, ICommBodyPolicy body)
        {
            _db = new CommDb(db);
            _access = access;
            _actors = actors;
            _body = body;
        }

        public string Source => CommTimelineSourceKinds.Comment;

        public async Task<CommTimelineSourceResult> GetAsync(
            BusinessContext context, CommEntityRef entity, DateTime? before, int take, bool includeDeleted,
            CancellationToken cancellationToken = default)
        {
            var visibilities = await _access.ResolveVisibilitiesAsync(context, entity, cancellationToken);
            if (visibilities.Allowed.Count == 0)
                return CommTimelineSourceResult.None("no readable visibility tier for this caller");

            var allowed = visibilities.Allowed.ToList();

            var rows = await _db.Comments.AsNoTracking()
                .Where(c => c.CompanyID == context.CompanyId
                            && c.EntityType == entity.EntityCode
                            && c.EntityId == entity.EntityId
                            && allowed.Contains(c.Visibility)
                            && (includeDeleted || c.DeletedAt == null)
                            && (before == null || c.CreatedAt < before))
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Id)
                .Take(take)
                .ToListAsync(cancellationToken);

            // The row-level own-author pass for Restricted. The SQL filter lets Restricted through so the
            // caller's OWN comments are fetched; this drops everybody else's.
            rows = rows.Where(c => _access.CanReadComment(visibilities, c, context)).ToList();
            if (rows.Count == 0) return CommTimelineSourceResult.None("empty");

            var actors = await _actors.ResolveAsync(rows.Select(r => (int?)r.AuthorEmployeeId), cancellationToken);
            bool arabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

            var items = rows.Select(c =>
            {
                var author = _actors.Get(actors, c.AuthorEmployeeId);
                bool deleted = c.DeletedAt != null;

                return new CommTimelineItem
                {
                    ItemKey = CommTimelineSourceKinds.Comment + ":" + c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Source = CommTimelineSourceKinds.Comment,
                    Entity = entity,
                    ItemType = CommEventTypes.CommentAdded,
                    TitleAr = author.Display(arabic: true) + " أضاف تعليقًا",
                    TitleEn = author.Display(arabic: false) + " commented",

                    // The DESCRIPTION is an excerpt, not the body — a timeline is a scan, and a 32 KB comment
                    // would make one row fill the screen. The full body is in Body below.
                    DescriptionAr = deleted ? null : _body.Excerpt(c.Body, c.BodyFormat, 200),
                    DescriptionEn = deleted ? null : _body.Excerpt(c.Body, c.BodyFormat, 200),

                    // Present because every row here already passed the visibility filter AND the own-author
                    // pass — a body in this list is authorized by construction. Blanked for a deleted comment,
                    // matching CommCommentService's projection.
                    Body = deleted ? null : c.Body,
                    BodyFormat = c.BodyFormat,

                    ActorEmployeeId = c.AuthorEmployeeId,
                    ActorDisplayName = author.Display(arabic),
                    Icon = "ki-outline ki-message-text-2",
                    Color = "primary",
                    OccurredAt = c.CreatedAt ?? DateTime.UtcNow,
                    Visibility = c.Visibility,
                    ThreadId = c.ThreadId,
                    CommentId = c.Id,
                    IsDeleted = deleted,
                };
            }).ToList();

            return CommTimelineSourceResult.Of(items);
        }
    }

    // =============================================================================================
    // SOURCE 3 — mentions, as their own timeline rows.
    //
    // Separate from comments so "who was pulled into this record, and when" is answerable without reading every
    // comment body — which is a question an auditor asks and a comment stream answers badly.
    // =============================================================================================
    public sealed class CommMentionTimelineSource : ICommTimelineSource
    {
        private readonly CommDb _db;
        private readonly ICommAccessPolicy _access;
        private readonly ICommActorDirectory _actors;

        public CommMentionTimelineSource(CrossDbContext db, ICommAccessPolicy access, ICommActorDirectory actors)
        {
            _db = new CommDb(db);
            _access = access;
            _actors = actors;
        }

        public string Source => CommTimelineSourceKinds.Mention;

        public async Task<CommTimelineSourceResult> GetAsync(
            BusinessContext context, CommEntityRef entity, DateTime? before, int take, bool includeDeleted,
            CancellationToken cancellationToken = default)
        {
            var visibilities = await _access.ResolveVisibilitiesAsync(context, entity, cancellationToken);
            if (visibilities.Allowed.Count == 0)
                return CommTimelineSourceResult.None("no readable visibility tier for this caller");

            var allowed = visibilities.Allowed.ToList();

            // A mention inherits the VISIBILITY OF ITS COMMENT, so the join is required rather than convenient:
            // showing "X mentioned the finance department" from a Confidential note would leak that the note
            // exists, who wrote it and who it concerns, without showing a word of it.
            var rows = await (
                from m in _db.Mentions.AsNoTracking()
                join c in _db.Comments.AsNoTracking() on m.CommentId equals c.Id
                where m.CompanyID == context.CompanyId
                      && m.EntityType == entity.EntityCode
                      && m.EntityId == entity.EntityId
                      && allowed.Contains(c.Visibility)
                      && (includeDeleted || c.DeletedAt == null)
                      && (before == null || m.CreatedAt < before)
                orderby m.CreatedAt descending, m.Id descending
                select new
                {
                    m.Id, m.ThreadId, m.CommentId, m.TargetKind, m.LabelAr, m.LabelEn,
                    m.ResolvedRecipientCount, m.MentionedByEmployeeId, m.CreatedAt,
                    CommentVisibility = c.Visibility, c.AuthorEmployeeId, CommentDeleted = c.DeletedAt,
                })
                .Take(take)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0) return CommTimelineSourceResult.None("empty");

            // The own-author pass, applied against the COMMENT's author — the same rule the comment source uses,
            // because it is the comment's visibility being enforced.
            rows = rows.Where(r =>
                r.CommentVisibility != CommVisibility.Restricted
                || visibilities.MayReadAnyRestricted
                || r.AuthorEmployeeId == (context.EmployeeId ?? 0)).ToList();

            if (rows.Count == 0) return CommTimelineSourceResult.None("empty after the restricted own-author pass");

            var actors = await _actors.ResolveAsync(rows.Select(r => (int?)r.MentionedByEmployeeId), cancellationToken);
            bool arabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

            var items = rows.Select(r =>
            {
                var actor = _actors.Get(actors, r.MentionedByEmployeeId);
                var targetAr = r.LabelAr ?? r.TargetKind;
                var targetEn = r.LabelEn ?? r.TargetKind;

                return new CommTimelineItem
                {
                    ItemKey = CommTimelineSourceKinds.Mention + ":" + r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Source = CommTimelineSourceKinds.Mention,
                    Entity = entity,
                    ItemType = CommEventTypes.MentionCreated,
                    TitleAr = $"{actor.Display(arabic: true)} أشار إلى {targetAr}",
                    TitleEn = $"{actor.Display(arabic: false)} mentioned {targetEn}",
                    DescriptionAr = r.ResolvedRecipientCount > 1 ? $"وصل إلى {r.ResolvedRecipientCount} أشخاص" : null,
                    DescriptionEn = r.ResolvedRecipientCount > 1 ? $"reached {r.ResolvedRecipientCount} people" : null,
                    ActorEmployeeId = r.MentionedByEmployeeId,
                    ActorDisplayName = actor.Display(arabic),
                    Icon = "ki-outline ki-user-tick",
                    Color = "info",
                    OccurredAt = r.CreatedAt ?? DateTime.UtcNow,
                    Visibility = r.CommentVisibility,
                    ThreadId = r.ThreadId,
                    CommentId = r.CommentId,
                    IsDeleted = r.CommentDeleted != null,
                };
            }).ToList();

            return CommTimelineSourceResult.Of(items);
        }
    }

    // =============================================================================================
    // SOURCE 4 — communication audit facts worth showing ON THE RECORD.
    //
    // Deliberately NOT the whole audit trail: most audit rows are for an auditor, not a record screen. The
    // allow-list below is the short list of moderation acts a reader of the record needs to understand a gap in
    // the conversation — a deleted comment, a locked thread. Reactions, participation and read state are audited
    // but never surfaced here.
    // =============================================================================================
    public sealed class CommAuditTimelineSource : ICommTimelineSource
    {
        private static readonly string[] SurfacedActions =
        {
            CommAuditActions.ThreadLocked,
            CommAuditActions.ThreadUnlocked,
            CommAuditActions.CommentDeleted,
            CommAuditActions.CommentRestored,
        };

        private readonly CommDb _db;
        private readonly ICommAccessPolicy _access;
        private readonly ICommActorDirectory _actors;

        public CommAuditTimelineSource(CrossDbContext db, ICommAccessPolicy access, ICommActorDirectory actors)
        {
            _db = new CommDb(db);
            _access = access;
            _actors = actors;
        }

        public string Source => CommTimelineSourceKinds.CommAudit;

        public async Task<CommTimelineSourceResult> GetAsync(
            BusinessContext context, CommEntityRef entity, DateTime? before, int take, bool includeDeleted,
            CancellationToken cancellationToken = default)
        {
            var visibilities = await _access.ResolveVisibilitiesAsync(context, entity, cancellationToken);
            if (visibilities.Allowed.Count == 0)
                return CommTimelineSourceResult.None("no readable visibility tier for this caller");

            var allowed = visibilities.Allowed.ToList();

            var rows = await _db.Audit.AsNoTracking()
                .Where(a => a.CompanyID == context.CompanyId
                            && a.EntityType == entity.EntityCode
                            && a.EntityId == entity.EntityId
                            && SurfacedActions.Contains(a.Action)

                            // An audit row with NO visibility (a thread-level act) is treated as readable by
                            // anyone who may read the record; one that names a tier is filtered by it. Excluding
                            // null-visibility rows would hide every lock event, which carries no tier.
                            && (a.Visibility == null || allowed.Contains(a.Visibility))
                            && (before == null || a.CreatedAt < before))
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Take(take)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0) return CommTimelineSourceResult.None("empty");

            // The own-author pass cannot apply here: an audit row records what a MODERATOR did to somebody
            // else's comment, so "is it mine" is not the right question. A Restricted moderation act is
            // therefore visible only to a caller who may read Restricted at all.
            rows = rows.Where(a =>
                a.Visibility != CommVisibility.Restricted || visibilities.MayReadAnyRestricted).ToList();

            if (rows.Count == 0) return CommTimelineSourceResult.None("empty after the restricted pass");

            var actors = await _actors.ResolveAsync(rows.Select(r => r.ActorEmployeeId), cancellationToken);
            bool arabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

            var items = rows.Select(a =>
            {
                var actor = _actors.Get(actors, a.ActorEmployeeId);
                var (titleAr, titleEn, icon, color) = Present(a.Action);

                return new CommTimelineItem
                {
                    ItemKey = CommTimelineSourceKinds.CommAudit + ":" + a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Source = CommTimelineSourceKinds.CommAudit,
                    Entity = entity,
                    ItemType = a.EventType ?? a.Action,
                    TitleAr = actor.Display(arabic: true) + " " + titleAr,
                    TitleEn = actor.Display(arabic: false) + " " + titleEn,
                    ActorEmployeeId = a.ActorEmployeeId,
                    ActorDisplayName = actor.Display(arabic),
                    Icon = icon,
                    Color = color,
                    OccurredAt = a.CreatedAt,
                    Visibility = a.Visibility,
                    ThreadId = a.ThreadId,
                    CommentId = a.CommentId,
                };
            }).ToList();

            return CommTimelineSourceResult.Of(items);
        }

        private static (string Ar, string En, string Icon, string Color) Present(string action) => action switch
        {
            CommAuditActions.ThreadLocked => ("أغلق المحادثة", "locked the conversation", "ki-outline ki-lock", "warning"),
            CommAuditActions.ThreadUnlocked => ("أعاد فتح المحادثة", "reopened the conversation", "ki-outline ki-lock-2", "success"),
            CommAuditActions.CommentDeleted => ("حذف تعليقًا", "removed a comment", "ki-outline ki-trash", "danger"),
            CommAuditActions.CommentRestored => ("استعاد تعليقًا", "restored a comment", "ki-outline ki-arrows-circle", "success"),
            _ => (action, action, "ki-outline ki-information", "secondary"),
        };
    }
}
