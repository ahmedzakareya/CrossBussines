using System.Text.Json;
using System.Text.Json.Nodes;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform.Ai
{
    // AI Foundation — Increment 2. The closed registry of retrievable shapes, and the only place stored
    // payload JSON is parsed.
    //
    // PayloadJson was written by a trusted builder, but the read side treats it as UNTRUSTED STORED DATA
    // anyway. Between write and read sit a database, a backup/restore, a migration and anyone with table
    // access; "we wrote it correctly" is an argument about the past, not a property of the bytes now.
    //
    // Deserialization is deliberately NOT to a .NET type. Parsing to JsonNode and re-selecting declared
    // field names means no type resolution ever happens from data: there is no converter to pick, no
    // polymorphism to abuse, and TypeNameHandling has no analogue to enable. A payload naming a .NET type
    // is just a string that matches no declared field and is dropped.
    public sealed class AiProjectionShape
    {
        public required string ProjectionType { get; init; }
        public required int Version { get; init; }

        // The ONLY keys that may leave the store. A stored payload carrying anything else has those keys
        // dropped — the field list is applied on the way OUT as well as on the way in, so a row written
        // by an older or tampered writer cannot widen the corpus.
        public required IReadOnlyList<string> Fields { get; init; }

        // The entity code this shape describes, used to resolve and re-authorize the source record.
        public required string EntityType { get; init; }
    }

    public interface IAiProjectionShapeRegistry
    {
        // Null when the (type, version) pair is not supported. There is NO "latest" fallback: an
        // unsupported version fails closed rather than being served by a shape that may mean something
        // different. Silently upgrading a caller to a newer shape is how a retired field comes back.
        AiProjectionShape? Find(string projectionType, int version);

        bool IsKnownType(string projectionType);

        IReadOnlyList<AiProjectionShape> All { get; }
    }

    public sealed class AiProjectionShapeRegistry : IAiProjectionShapeRegistry
    {
        // Field lists mirror the builders exactly. They are repeated here rather than derived from the
        // builder because the two answer different questions — "what may be written" and "what may be
        // read" — and a shape can be RETIRED for reading while its rows still exist. A test asserts they
        // agree today, so the duplication cannot drift silently.
        private static readonly AiProjectionShape[] Shapes =
        {
            new()
            {
                ProjectionType = AiConsumerGrants.TaskLifecycleProjection,
                Version = AiConsumerGrants.TaskLifecycleVersion,
                EntityType = EntityRegistry.Task,
                Fields = new[]
                {
                    "taskId", "action", "previousStatus", "newStatus", "priority",
                    "assigneeEmployeeId", "previousAssigneeEmployeeId",
                    "dueAtUtc", "previousDueAtUtc", "completedAtUtc",
                    "linkedEntityCode", "linkedEntityId", "sourceModule",
                },
            },
            new()
            {
                ProjectionType = AiConsumerGrants.CalendarSchedulingProjection,
                Version = AiConsumerGrants.CalendarSchedulingVersion,
                EntityType = EntityRegistry.CalendarEvent,
                Fields = new[]
                {
                    "calendarEventId", "action", "organizerEmployeeId",
                    "startUtc", "endUtc", "previousStartUtc", "previousEndUtc",
                    "isAllDay", "timeZoneId", "scope", "attendeeCount", "sourceModule",
                },
            },
        };

        public IReadOnlyList<AiProjectionShape> All => Shapes;

        public AiProjectionShape? Find(string projectionType, int version)
            => Array.Find(Shapes, s =>
                string.Equals(s.ProjectionType, projectionType, StringComparison.Ordinal) && s.Version == version);

        public bool IsKnownType(string projectionType)
            => Array.Exists(Shapes, s => string.Equals(s.ProjectionType, projectionType, StringComparison.Ordinal));
    }

    // The single parse point. Returns null on ANY doubt — that is the whole contract.
    public static class AiPayloadReader
    {
        public static IReadOnlyDictionary<string, object?>? TryRead(string? payloadJson, AiProjectionShape shape)
        {
            if (string.IsNullOrWhiteSpace(payloadJson)) return null;

            // Size ceiling BEFORE parsing: a hostile or corrupt payload must not be allocated first and
            // judged second. Measured in bytes, not characters, because that is what the limit protects.
            if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > AiRetrievalLimits.MaxPayloadBytes)
                return null;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(payloadJson);
            }
            catch (JsonException)
            {
                return null;                     // malformed ⇒ fail closed, never a partial read
            }

            if (node is not JsonObject obj) return null;   // an array or scalar is not a projection

            // Re-select by DECLARED NAME. Iterating the stored object instead would let an extra key
            // through the moment one appeared, which is precisely the widening this guards.
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in shape.Fields)
            {
                if (!obj.TryGetPropertyValue(field, out var value) || value is null)
                {
                    result[field] = null;        // absent is a legitimate answer for a partial shape
                    continue;
                }
                result[field] = value is JsonValue v ? Scalar(v) : null;
                // A nested object or array under an approved name is dropped to null rather than
                // returned: every declared field is scalar by design, so a structure there means the
                // payload is not what this shape describes.
            }
            return result;
        }

        // Scalars only. No dynamic type construction anywhere in this path.
        private static object? Scalar(JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<double>(out var f)) return f;
            return null;
        }
    }
}
