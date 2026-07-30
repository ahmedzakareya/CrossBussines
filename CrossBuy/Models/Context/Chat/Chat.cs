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