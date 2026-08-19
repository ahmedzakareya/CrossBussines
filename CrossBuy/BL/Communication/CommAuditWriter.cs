using System.Text.Json;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-035) — THE AUDIT WRITER (module 31).
    //
    // TRANSACTION CONTRACT, and it is the important part of this file:
    //
    // Append() ADDS A ROW TO THE CHANGE TRACKER AND DOES NOT SAVE. The calling service's single
    // SaveChangesAsync persists the business row and its audit row together, so the two share one fate. An
    // audit writer that saved on its own would produce the failure it exists to prevent: a rolled-back
    // comment with a committed audit line claiming it was posted.
    //
    // This is the same in-transaction rule ADR-001 imposes on the kernel's RecordAsync, arrived at for the
    // same reason and implemented the same way — enrol, never own.
    //
    // A DELIBERATE ASYMMETRY WITH THE KERNEL: RecordAsync THROWS when there is no ambient transaction,
    // because its caller is always a financial transaction that must fail with it. This writer does not,
    // because its callers open their own transaction when none exists (see CommTransaction) and a comment has
    // no financial caller to fail. The invariant is still "one SaveChanges for the business row and its audit
    // row", enforced by CommAuditTransactionTests rather than by a throw.
    // =============================================================================================
    public interface ICommAuditWriter
    {
        // Appends one append-only audit row. Never saves — see the transaction contract above.
        CommAuditEntry Append(CommAuditRequest request);

        // Convenience for the very common "record what this event did" case: derives Action from the event
        // type and copies the ids, so a caller does not restate them.
        CommAuditEntry AppendForEvent(CommEvent commEvent, string? detailJson = null, int? subjectEmployeeId = null);
    }

    public sealed class CommAuditRequest
    {
        public required string Action { get; init; }              // CommAuditActions
        public string? EventType { get; init; }                   // CommEventTypes, when there is one
        public required CommEntityRef Entity { get; init; }
        public required int CompanyId { get; init; }
        public int? BranchId { get; init; }

        public long? ThreadId { get; init; }
        public long? CommentId { get; init; }
        public long? MentionId { get; init; }
        public long? NotificationId { get; init; }

        public int? ActorEmployeeId { get; init; }
        public int? SubjectEmployeeId { get; init; }
        public string? Visibility { get; init; }

        // Anything JSON-serializable, or a pre-serialized string. NEVER a comment body and NEVER a
        // StorageKey — see CommAuditEntry for why.
        public object? Detail { get; init; }

        public Guid? CorrelationId { get; init; }
        public string? DedupKey { get; init; }
        public DateTime? OccurredAt { get; init; }
    }

    public sealed class CommAuditWriter : ICommAuditWriter
    {
        // 8 KB. An eighth of the kernel's event-payload budget, on purpose: an audit detail is a handful of
        // ids and counts, and anything approaching a document means content has leaked into a place read by
        // more people than the content is.
        public const int MaxDetailBytes = 8 * 1024;

        private static readonly JsonSerializerOptions DetailJson = new(JsonSerializerDefaults.Web);

        // Keys that must never appear in a serialized detail. Enforced rather than documented, because
        // "don't put the body in the audit row" is the kind of rule that survives exactly as long as the
        // person who wrote it.
        private static readonly string[] ForbiddenDetailKeys =
        {
            "\"body\":", "\"Body\":", "\"storageKey\":", "\"StorageKey\":",
            "\"thumbnailStorageKey\":", "\"ThumbnailStorageKey\":",
        };

        private readonly CommDb _db;

        public CommAuditWriter(CrossDbContext db) => _db = new CommDb(db);

        public CommAuditEntry Append(CommAuditRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            // An invalid action is a programming error, not a runtime condition: it would put an
            // unclassifiable row in a permanent, append-only log. Same stance BusinessEventService takes on a
            // malformed event type.
            if (!CommAuditActions.IsValid(request.Action))
                throw new InvalidOperationException(
                    $"'{request.Action}' is not a CommAuditActions value. An audit row with an unknown action " +
                    "cannot be queried or reported on, and the log is append-only, so it can never be fixed.");

            if (request.EventType != null && !CommEventTypes.IsValid(request.EventType))
                throw new InvalidOperationException(
                    $"'{request.EventType}' is not a CommEventTypes value.");

            if (request.CompanyId <= 0)
                throw new InvalidOperationException(
                    "An audit row needs a company. A row with no tenant is invisible to every company-scoped " +
                    "read, which is indistinguishable from never having been written.");

            var row = new CommAuditEntry
            {
                CompanyID = request.CompanyId,
                BranchID = request.BranchId,
                Action = request.Action,
                EventType = request.EventType,
                EntityType = request.Entity.EntityCode,
                EntityId = request.Entity.EntityId,
                ThreadId = request.ThreadId,
                CommentId = request.CommentId,
                MentionId = request.MentionId,
                NotificationId = request.NotificationId,
                ActorEmployeeId = request.ActorEmployeeId,
                SubjectEmployeeId = request.SubjectEmployeeId,
                Visibility = request.Visibility,
                DetailJson = Serialize(request.Detail, request.Action),
                CorrelationId = request.CorrelationId,
                DedupKey = string.IsNullOrWhiteSpace(request.DedupKey) ? null : request.DedupKey!.Trim(),
                CreatedAt = request.OccurredAt ?? DateTime.UtcNow,
            };

            _db.Audit.Add(row);
            return row;
        }

        public CommAuditEntry AppendForEvent(CommEvent commEvent, string? detailJson = null, int? subjectEmployeeId = null)
        {
            ArgumentNullException.ThrowIfNull(commEvent);

            return Append(new CommAuditRequest
            {
                Action = ActionForEvent(commEvent.EventType),
                EventType = commEvent.EventType,
                Entity = commEvent.Entity,
                CompanyId = commEvent.CompanyId,
                BranchId = commEvent.BranchId,
                ThreadId = commEvent.ThreadId,
                CommentId = commEvent.CommentId,
                MentionId = commEvent.MentionId,
                ActorEmployeeId = commEvent.ActorEmployeeId,
                SubjectEmployeeId = subjectEmployeeId,
                Visibility = commEvent.Visibility,
                Detail = detailJson ?? SerializeOrNull(commEvent.Payload),
                CorrelationId = commEvent.CorrelationId,
                DedupKey = commEvent.DedupKey,
                OccurredAt = commEvent.OccurredAt,
            });
        }

        // The event -> audit action map. TOTAL by construction: every CommEventTypes value has an entry, and
        // an unmapped one throws rather than defaulting, so adding an event type without deciding how it is
        // audited fails at the first call instead of producing untraceable rows.
        public static string ActionForEvent(string eventType) => eventType switch
        {
            CommEventTypes.ThreadCreated => CommAuditActions.ThreadCreated,
            CommEventTypes.ThreadLocked => CommAuditActions.ThreadLocked,
            CommEventTypes.ThreadUnlocked => CommAuditActions.ThreadUnlocked,
            CommEventTypes.CommentAdded => CommAuditActions.CommentAdded,
            CommEventTypes.CommentEdited => CommAuditActions.CommentEdited,
            CommEventTypes.CommentDeleted => CommAuditActions.CommentDeleted,
            CommEventTypes.CommentRestored => CommAuditActions.CommentRestored,
            CommEventTypes.MentionCreated => CommAuditActions.MentionCreated,
            CommEventTypes.ReactionAdded => CommAuditActions.ReactionAdded,
            CommEventTypes.ReactionRemoved => CommAuditActions.ReactionRemoved,
            CommEventTypes.AttachmentAdded => CommAuditActions.AttachmentAdded,
            CommEventTypes.AttachmentRemoved => CommAuditActions.AttachmentRemoved,
            CommEventTypes.ParticipantAdded => CommAuditActions.ParticipantAdded,
            CommEventTypes.ParticipantRemoved => CommAuditActions.ParticipantRemoved,
            CommEventTypes.PermissionGranted => CommAuditActions.PermissionGranted,
            CommEventTypes.PermissionRevoked => CommAuditActions.PermissionRevoked,
            CommEventTypes.NotificationQueued => CommAuditActions.NotificationQueued,
            _ => throw new InvalidOperationException(
                $"Communication event '{eventType}' has no audit action mapping. Every event must be auditable — " +
                "add it to CommAuditWriter.ActionForEvent."),
        };

        private static string? SerializeOrNull(object? detail)
            => detail == null ? null : JsonSerializer.Serialize(detail, DetailJson);

        private static string? Serialize(object? detail, string action)
        {
            if (detail == null) return null;

            var json = detail as string ?? JsonSerializer.Serialize(detail, DetailJson);

            foreach (var forbidden in ForbiddenDetailKeys)
            {
                if (json.Contains(forbidden, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Audit detail for '{action}' contains '{forbidden.Trim(':', '"')}'. An audit row carries ids " +
                        "and counts, never content and never a file handle: it is read by more people than the " +
                        "content is, and a StorageKey in an audit row is a quiet privilege escalation.");
            }

            int bytes = System.Text.Encoding.UTF8.GetByteCount(json);
            if (bytes > MaxDetailBytes)
                throw new InvalidOperationException(
                    $"Audit detail for '{action}' is {bytes} bytes, over the {MaxDetailBytes}-byte budget. " +
                    "Reference the row that holds the detail instead of copying it here.");

            return json;
        }
    }
}