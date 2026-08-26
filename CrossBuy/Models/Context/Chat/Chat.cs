namespace CrossBuy.Models.Context.Chat
{
    // Communication Hub P3 — internal Teams-style chat (imitation). All internal (employee↔employee).
    // A conversation is either a 1:1 Direct or a Group/Channel. No GL/business impact.

    public class Conversation
    {
        public int ID { get; set; }
        public int CompanyID { get; set; }
        public string Kind { get; set; } = "Direct";        // Direct | Group
        public string? Title { get; set; }                   // group name (Direct → derived from the other member)
        public int CreatedByEmployeeId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastMessageAt { get; set; }         // for sorting the conversation list
        public string? LastMessagePreview { get; set; }      // short snippet for the list

        // ---- Direct-conversation identity (F4) -------------------------------------------------
        // A Direct conversation between A and B is ONE conversation per company, and that has to be a
        // database fact rather than a service convention: find-then-insert loses a race, and two threads
        // for one pair splits a conversation in half with no way to tell which is real.
        //
        // The pair is stored ORDERED — Low is always the smaller employee id — so {A,B} and {B,A} produce
        // the same key and the symmetry is structural instead of something every caller has to remember.
        // A filtered UNIQUE index over (CompanyID, DirectKeyLow, DirectKeyHigh) WHERE Kind = 'Direct'
        // then makes the duplicate physically impossible; see deploy/sql/comm_chat_identity_001.sql.
        //
        // Nullable, and null for groups: a Group has a membership list, not a pair, and the filtered index
        // deliberately ignores those rows so group membership stays free to change.
        public int? DirectKeyLow { get; set; }
        public int? DirectKeyHigh { get; set; }

        // Orders a pair into the (Low, High) form the unique index expects. One place, so a caller cannot
        // accidentally key {B,A} differently from {A,B}.
        public static (int Low, int High) DirectKey(int employeeA, int employeeB)
            => employeeA <= employeeB ? (employeeA, employeeB) : (employeeB, employeeA);
    }

    public class ConversationMember
    {
        public int ID { get; set; }
        public int ConversationId { get; set; }
        public int EmployeeId { get; set; }
        public string Role { get; set; } = "Member";         // Owner | Member
        public int LastReadMessageId { get; set; } = 0;      // read high-water mark → unread = msgs with Id > this
        public DateTime? MutedUntil { get; set; }
        public DateTime JoinedAt { get; set; }
    }

    public class ChatMessage
    {
        public int ID { get; set; }
        public int ConversationId { get; set; }
        public int SenderEmployeeId { get; set; }
        public string? Body { get; set; }                    // null when attachment-only
        public string? AttachmentPath { get; set; }          // /uploads/chat/...
        public string? AttachmentName { get; set; }
        public string? AttachmentType { get; set; }          // image | file
        public int? ReplyToId { get; set; }
        public DateTime? EditedAt { get; set; }
        public DateTime? DeletedAt { get; set; }             // soft delete
        public DateTime CreatedAt { get; set; }
    }

    public class ChatReaction
    {
        public int ID { get; set; }
        public int MessageId { get; set; }
        public int EmployeeId { get; set; }
        public string Emoji { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}