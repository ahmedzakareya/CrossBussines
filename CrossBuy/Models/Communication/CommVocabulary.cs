using CrossBuy.Models.Platform;

namespace CrossBuy.Models.Communication
{
    // =============================================================================================
    // Communication Platform (CPS-001 / ADR-030) — the FROZEN vocabularies.
    //
    // Every one of these sets is mirrored by a CHECK constraint in
    // deploy/sql/communication_platform_slice_001.sql. That mirroring is the whole point: the platform
    // kernel learned the hard way (ADR-002) that an unconstrained string column forks into three
    // disagreeing vocabularies within a year — DocComment.EntityType was free text, NotificationTypes was
    // snake_case, TaskLinkResolver keys were PascalCase, and nothing could join them.
    //
    // RULE: a value that is not in one of these sets never reaches a column. Services validate on WRITE
    // and reject; reads tolerate whatever is stored so history keeps rendering (the same write-only
    // validation stance DocCommentService took in Slice-003).
    // =============================================================================================

    // ---------------------------------------------------------------------------------------------
    // Visibility of a thread / comment / note.
    //
    // This is NOT BusinessEventVisibility, and the difference is deliberate. The kernel's set is frozen at
    // Internal | Confidential | Restricted | System (ADR-004) and has NO tier for "a customer may read
    // this" — it never needed one, because a business event is always staff-facing. Communication does need
    // one: an internal note and a public note are the SAME feature with different audiences, and modelling
    // them as two tables would duplicate every comment behaviour (edit history, mentions, reactions,
    // attachments) twice.
    //
    // So Communication owns its own set with Public added at the bottom, plus an explicit downward mapping
    // to the kernel's set for when an event is bridged (ADR-030 §7). Public maps to Internal, not to
    // something new — adding a value to the kernel's frozen set from outside the kernel is exactly the kind
    // of cross-team edit CLAUDE.md forbids.
    // ---------------------------------------------------------------------------------------------
    public static class CommVisibility
    {
        // Readable by an external audience (customer portal, supplier portal, printed document).
        // The ONLY tier that may ever leave the company's staff.
        public const string Public = "Public";

        // Any user who may VIEW the anchor entity may read it. The default.
        public const string Internal = "Internal";

        // Requires elevated module rights on the anchor entity (e.g. accounting post) — margins, credit
        // decisions, pricing rationale.
        public const string Confidential = "Confidential";

        // Readable only by the author, an explicitly permitted participant, or a module manager —
        // disciplinary notes, HR case notes.
        public const string Restricted = "Restricted";

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { Public, Internal, Confidential, Restricted };

        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;

        // The kernel tier a bridged event carries. Public collapses to Internal because the kernel has no
        // external tier; that is a NARROWING (a public note's event is staff-visible, not portal-visible),
        // which is the safe direction. Never widen here.
        public static string ToBusinessEventVisibility(string? commVisibility) => commVisibility switch
        {
            Confidential => BusinessEventVisibility.Confidential,
            Restricted => BusinessEventVisibility.Restricted,
            _ => BusinessEventVisibility.Internal,
        };

        // Ordered least- to most-restricted. Used to answer "is X at least as open as Y" without a
        // scattered chain of string comparisons.
        public static int Rank(string? value) => value switch
        {
            Public => 0,
            Internal => 1,
            Confidential => 2,
            Restricted => 3,
            _ => int.MaxValue,   // unknown is treated as the most restrictive — fail closed
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Thread kinds. A single anchor entity may carry SEVERAL threads (module 2, "Discussion Threads"):
    // the default discussion, a notes stream, and named topic threads distinguished by ThreadKey.
    // ---------------------------------------------------------------------------------------------
    public static class CommThreadKind
    {
        // The entity's main conversation. One per (entity, ThreadKey).
        public const string Discussion = "Discussion";

        // The notes stream — internal notes and public notes both live here, separated by Visibility,
        // not by kind. See CommVisibility for why.
        public const string Notes = "Notes";

        // A review/approval conversation: same mechanics, kept separate so a review thread can be locked
        // when the review closes without locking the entity's ordinary discussion.
        public const string Review = "Review";

        private static readonly HashSet<string> All = new(StringComparer.Ordinal) { Discussion, Notes, Review };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // ---------------------------------------------------------------------------------------------
    // Body format.
    //
    // HTML IS DELIBERATELY ABSENT. Accepting authored HTML means owning an HTML sanitizer forever, and a
    // sanitizer bug in a comment body is a stored-XSS hole on every screen that renders a timeline. Markdown
    // is a superset of everything the requirement asks for — rich text, images, links, code blocks, tables —
    // and it is safe to STORE unrendered, which is what this platform does (rendering is a UI concern and
    // this phase ships no UI, ADR-030 §9).
    // ---------------------------------------------------------------------------------------------
    public static class CommBodyFormat
    {
        // CommonMark subset: headings, emphasis, lists, links, images, fenced code, tables, blockquotes.
        public const string Markdown = "Markdown";

        // Stored and rendered verbatim, no markup interpretation. Used by machine-authored comments.
        public const string PlainText = "PlainText";

        private static readonly HashSet<string> All = new(StringComparer.Ordinal) { Markdown, PlainText };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // ---------------------------------------------------------------------------------------------
    // Participation.
    //
    // Follower vs Watcher is a REAL distinction, not two names for one thing (modules 4 and 5):
    //   Follower — asked to be told. Gets a notification per activity, subject to preferences.
    //   Watcher  — asked to keep an eye. Gets NO per-activity notification; the thread appears in their
    //              "watching" list and in the digest. This is the tier an auto-follow rule may assign
    //              without spamming somebody who never opted in.
    // Participant is derived, not chosen: anyone who has authored or been mentioned. Owner may moderate.
    // ---------------------------------------------------------------------------------------------
    public static class CommParticipantRole
    {
        public const string Owner = "Owner";
        public const string Participant = "Participant";
        public const string Follower = "Follower";
        public const string Watcher = "Watcher";

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { Owner, Participant, Follower, Watcher };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;

        // Roles that receive a per-activity notification. A Watcher is deliberately excluded.
        public static bool NotifiedPerActivity(string? role) =>
            role is Owner or Participant or Follower;
    }

    public static class CommParticipationSource
    {
        public const string Author = "Author";           // wrote a comment in the thread
        public const string Mention = "Mention";         // was @mentioned
        public const string Explicit = "Explicit";       // pressed Follow / was added by a moderator
        public const string Auto = "Auto";               // an auto-follow rule (assignee, owner of the record)

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { Author, Mention, Explicit, Auto };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // ---------------------------------------------------------------------------------------------
    // Mention target kinds. Employee / Team / Department are supported; Role is DECLARED-NOT-WIRED.
    //
    // Role is declared here so the vocabulary and the CHECK constraint are frozen now — the value cannot be
    // spelled differently by whoever wires it. It is refused at write time until a target provider exists
    // (see ADR-032 §5: IPlatformRoleDirectory answers "what does THIS principal hold" and has no reverse
    // "who holds this role" query, so a role provider needs a new read that does not exist yet).
    // ---------------------------------------------------------------------------------------------
    public static class CommMentionTargetKind
    {
        public const string Employee = "Employee";
        public const string Team = "Team";             // a manager's direct + indirect reports (IOrgHierarchy)
        public const string Department = "Department";  // a Hierarchical org node and everyone beneath it
        public const string Role = "Role";             // declared, not wired — see above

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { Employee, Team, Department, Role };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // ---------------------------------------------------------------------------------------------
    // Thread-level permission grants (modules 23 and 24).
    // ---------------------------------------------------------------------------------------------
    public static class CommPermissionLevel
    {
        public const string Read = "Read";
        public const string Comment = "Comment";
        public const string Moderate = "Moderate";   // lock, delete others' comments, manage participants

        private static readonly HashSet<string> All = new(StringComparer.Ordinal) { Read, Comment, Moderate };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;

        public static int Rank(string? value) => value switch
        {
            Read => 1, Comment => 2, Moderate => 3, _ => 0,
        };

        // Does a held level satisfy a required one?
        public static bool Satisfies(string? held, string required) => Rank(held) >= Rank(required);
    }

    public static class CommPrincipalKind
    {
        public const string Employee = "Employee";
        public const string Team = "Team";
        public const string Department = "Department";
        public const string Role = "Role";

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { Employee, Team, Department, Role };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // ---------------------------------------------------------------------------------------------
    // Reactions (module 6). A FROZEN key set, not free-text emoji.
    //
    // Free-text emoji looks generous and costs a reporting column: "how many people flagged a concern on
    // this invoice" is answerable over six keys and unanswerable over an open unicode range. A UI renders
    // whatever glyph it likes per key; the STORED value stays enumerable.
    // ---------------------------------------------------------------------------------------------
    public static class CommReactionKeys
    {
        public const string Like = "like";
        public const string Celebrate = "celebrate";
        public const string Insightful = "insightful";
        public const string Thanks = "thanks";
        public const string Question = "question";
        public const string Concern = "concern";

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { Like, Celebrate, Insightful, Thanks, Question, Concern };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // ---------------------------------------------------------------------------------------------
    // Notification channels (modules 17-21).
    //
    // WhatsApp is DECLARED-NOT-WIRED, same discipline as the Role mention kind: the vocabulary and the
    // CHECK constraint are frozen now, and a delivery row addressed to a channel with no registered adapter
    // is parked as Skipped with a reason rather than sitting Pending forever (ADR-034 §6). The kernel's own
    // BusinessEventConsumers comment states the inverse rule — never register a consumer with no
    // implementation — and Skipped is how this platform obeys the same rule for channels.
    // ---------------------------------------------------------------------------------------------
    public static class CommChannel
    {
        public const string InApp = "InApp";
        public const string Email = "Email";
        public const string Push = "Push";
        public const string WhatsApp = "WhatsApp";

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { InApp, Email, Push, WhatsApp };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // Delivery state of ONE (notification, channel) pair. Mirrors BusinessEventDispatchStatus (ADR-003)
    // deliberately — an operator who has learned to read one queue can read the other — plus Skipped,
    // which the event dispatcher has no equivalent for because it never declines to deliver.
    public static class CommDeliveryStatus
    {
        public const string Pending = "Pending";
        public const string Claimed = "Claimed";
        public const string Sent = "Sent";
        public const string Failed = "Failed";
        public const string Skipped = "Skipped";   // preference off, channel unwired, or recipient unreachable

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { Pending, Claimed, Sent, Failed, Skipped };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;

        // A terminal row is never claimed again. Failed is NOT terminal — it is retried until the attempt
        // cap, exactly as BusinessEventDispatch does.
        public static bool IsTerminal(string? status) => status is Sent or Skipped;
    }

    // Per-recipient delivery preference (module 22).
    public static class CommPreferenceMode
    {
        public const string Immediate = "Immediate";
        public const string Digest = "Digest";
        public const string Off = "Off";

        private static readonly HashSet<string> All = new(StringComparer.Ordinal) { Immediate, Digest, Off };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // ---------------------------------------------------------------------------------------------
    // Audit actions (module 31). Append-only; every mutating service operation names exactly one.
    // ---------------------------------------------------------------------------------------------
    public static class CommAuditActions
    {
        public const string ThreadCreated = "ThreadCreated";
        public const string ThreadLocked = "ThreadLocked";
        public const string ThreadUnlocked = "ThreadUnlocked";
        public const string CommentAdded = "CommentAdded";
        public const string CommentEdited = "CommentEdited";
        public const string CommentDeleted = "CommentDeleted";
        public const string CommentRestored = "CommentRestored";
        public const string MentionCreated = "MentionCreated";
        public const string ReactionAdded = "ReactionAdded";
        public const string ReactionRemoved = "ReactionRemoved";
        public const string AttachmentAdded = "AttachmentAdded";
        public const string AttachmentRemoved = "AttachmentRemoved";
        public const string ParticipantAdded = "ParticipantAdded";
        public const string ParticipantRemoved = "ParticipantRemoved";
        public const string ParticipantMuted = "ParticipantMuted";
        public const string PermissionGranted = "PermissionGranted";
        public const string PermissionRevoked = "PermissionRevoked";
        public const string ReadStatusUpdated = "ReadStatusUpdated";
        public const string NotificationQueued = "NotificationQueued";
        public const string NotificationDelivered = "NotificationDelivered";
        public const string NotificationSkipped = "NotificationSkipped";
        public const string NotificationFailed = "NotificationFailed";
        public const string AccessDenied = "AccessDenied";

        private static readonly HashSet<string> All = new(StringComparer.Ordinal)
        {
            ThreadCreated, ThreadLocked, ThreadUnlocked,
            CommentAdded, CommentEdited, CommentDeleted, CommentRestored,
            MentionCreated, ReactionAdded, ReactionRemoved,
            AttachmentAdded, AttachmentRemoved,
            ParticipantAdded, ParticipantRemoved, ParticipantMuted,
            PermissionGranted, PermissionRevoked, ReadStatusUpdated,
            NotificationQueued, NotificationDelivered, NotificationSkipped, NotificationFailed,
            AccessDenied,
        };

        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }

    // ---------------------------------------------------------------------------------------------
    // File preview kinds (module 11). Classification only — this platform stores no bytes and renders
    // nothing; it says what KIND of preview an attachment could have so a UI can decide.
    // ---------------------------------------------------------------------------------------------
    public static class CommPreviewKind
    {
        public const string Image = "Image";
        public const string Pdf = "Pdf";
        public const string Text = "Text";
        public const string Office = "Office";
        public const string Archive = "Archive";
        public const string None = "None";

        private static readonly HashSet<string> All =
            new(StringComparer.Ordinal) { Image, Pdf, Text, Office, Archive, None };
        public static bool IsValid(string? value) => value != null && All.Contains(value);
        public static IReadOnlyCollection<string> Values => All;
    }
}