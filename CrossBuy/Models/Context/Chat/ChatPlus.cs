using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Chat
{
	// ==========================================================================================
	// COMMUNICATION v2 (owner, 2026-11-18: "build every gap, completely") — what the leading team-chat systems have and
	// ours did not: saved messages, scheduled messages, reminders, polls, a person's status and do-not-disturb, and the
	// governance of messages (retention, the sensitive-data rule, legal holds, compliance exports).
	// Tables: deploy/sql/comm_messaging_v2.sql. Configured by CommunicationModel.Configure (from HospitalCallModel).
	// ==========================================================================================
	/// Call summaries (owner, 2026-11-19): the words and summary of a RECORDED call, made once in the background on this
	/// machine (comm_call_summaries.sql). Status: working | done | failed.
	public class CallSummary
	{
		public int RecordingId { get; set; }
		public int CompanyId { get; set; }
		public int CallId { get; set; }
		public string Status { get; set; } = "working";
		public string? JobId { get; set; }
		public decimal? Progress { get; set; }
		public string? Transcript { get; set; }
		public string? SummaryJson { get; set; }
		public string? Language { get; set; }
		public string? Model { get; set; }
		public decimal? AudioSeconds { get; set; }
		public decimal? WorkSeconds { get; set; }
		public string? Error { get; set; }
		public int RequestedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	/// Message translation (owner, 2026-11-19): one message in the other language, made once (comm_translations.sql).
	/// An incoming webhook of a group or channel (comm_webhooks.sql): another system posts into the conversation with the
	/// secret token, in the name of the member who made it. The token is kept only as its SHA-256.
	public class ChatWebhook
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ConversationId { get; set; }
		public string Name { get; set; } = "";
		public string TokenHash { get; set; } = "";
		public int CreatedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime? LastUsedAt { get; set; }
		public int UseCount { get; set; }
		public DateTime? RevokedAt { get; set; }
		public int? RevokedByEmployeeId { get; set; }
	}
	/// A shared document of one conversation (comm_docs.sql): every member reads and edits it; Version counts the saves.
	public class ChatDoc
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ConversationId { get; set; }
		public string Title { get; set; } = "";
		public string Content { get; set; } = "";
		public int Version { get; set; }
		public int? MessageId { get; set; }
		public string? FilePath { get; set; }
		public int CreatedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int UpdatedByEmployeeId { get; set; }
		public DateTime UpdatedAt { get; set; }
		public DateTime? DeletedAt { get; set; }
	}
	/// Every saved version of a shared document: its history, and the base a concurrent save is merged against.
	public class ChatDocVersion
	{
		public int DocId { get; set; }
		public int Version { get; set; }
		public string Content { get; set; } = "";
		public string Title { get; set; } = "";
		public int EmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		public bool Merged { get; set; }
	}
	/// A guest from outside the company in one group conversation (comm_guests.sql): reads from the moment they were
	/// invited, writes in it, nothing else. The link's secret is kept only as its SHA-256.
	public class ChatGuest
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ConversationId { get; set; }
		public string Name { get; set; } = "";
		public string TokenHash { get; set; } = "";
		public int InvitedByEmployeeId { get; set; }
		public int FirstMessageId { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime ExpiresAt { get; set; }
		public DateTime? LastSeenAt { get; set; }
		public DateTime? RevokedAt { get; set; }
		public int? RevokedByEmployeeId { get; set; }
	}
	/// Which message a guest wrote.
	public class ChatGuestPost
	{
		public int MessageId { get; set; }
		public int GuestId { get; set; }
		public int CompanyId { get; set; }
		public DateTime CreatedAt { get; set; }
	}
	/// Which message came through which webhook (the chat shows "via ...").
	public class ChatWebhookPost
	{
		public int MessageId { get; set; }
		public int WebhookId { get; set; }
		public int CompanyId { get; set; }
		public DateTime CreatedAt { get; set; }
	}
	public class ChatTranslation
	{
		public int MessageId { get; set; }
		public string Lang { get; set; } = "ar";
		public int CompanyId { get; set; }
		public string Text { get; set; } = "";
		public int RequestedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	/// Speech to text (owner, 2026-11-19): the words of a voice message, transcribed once on request and shown to
	/// everyone who may read the message (comm_transcripts.sql). Status: done | failed | too_long.
	public class ChatTranscript
	{
		public int MessageId { get; set; }
		public int CompanyId { get; set; }
		public string Status { get; set; } = "done";
		public string? Text { get; set; }
		public string? Language { get; set; }
		public string? Model { get; set; }
		public decimal? AudioSeconds { get; set; }
		public decimal? WorkSeconds { get; set; }
		public string? Error { get; set; }
		public int RequestedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class ChatSaved
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EmployeeId { get; set; }
		public int MessageId { get; set; }
		public DateTime SavedAt { get; set; }
	}

	public class ChatScheduled
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ConversationId { get; set; }
		public int EmployeeId { get; set; }
		public string Body { get; set; } = "";
		public DateTime SendAt { get; set; }
		public DateTime? SentAt { get; set; }
		public int? SentMessageId { get; set; }
		public DateTime? CancelledAt { get; set; }
		public string? Failure { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class ChatReminder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EmployeeId { get; set; }
		public int ConversationId { get; set; }
		public int MessageId { get; set; }
		public DateTime RemindAt { get; set; }
		public DateTime? NotifiedAt { get; set; }
		public DateTime? DoneAt { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class ChatPoll
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int MessageId { get; set; }
		public string Question { get; set; } = "";
		public bool Multi { get; set; }
		public bool Anonymous { get; set; }
		public DateTime? ClosesAt { get; set; }
		public DateTime? ClosedAt { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class ChatPollOption
	{
		public int ID { get; set; }
		public int PollId { get; set; }
		public string Text { get; set; } = "";
		public int Sort { get; set; }
	}

	public class ChatPollVote
	{
		public int ID { get; set; }
		public int PollId { get; set; }
		public int OptionId { get; set; }
		public int EmployeeId { get; set; }
		public DateTime At { get; set; }
	}

	public class ChatStatus
	{
		public int EmployeeId { get; set; }
		public int CompanyId { get; set; }
		public string? Emoji { get; set; }
		public string? Text { get; set; }
		public DateTime? Until { get; set; }
		public bool Dnd { get; set; }
		public DateTime? DndUntil { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	public class ChatSettings
	{
		public int CompanyId { get; set; }
		/// 0 = messages are kept; otherwise a message older than this many days loses its content (legal holds excepted).
		public int RetentionDays { get; set; }
		/// off | warn | block — what sending a card number, an IBAN, a national id or a listed word does.
		public string DlpMode { get; set; } = "off";
		public bool DlpCards { get; set; } = true;
		public bool DlpIban { get; set; } = true;
		public bool DlpNationalId { get; set; } = true;
		/// Further words or phrases, one per line.
		public string? DlpWords { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class ChatLegalHold
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? EmployeeId { get; set; }
		public int? ConversationId { get; set; }
		public string Reason { get; set; } = "";
		public int CreatedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime? ReleasedAt { get; set; }
		public int? ReleasedByEmployeeId { get; set; }
	}

	public class ChatExport
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ByEmployeeId { get; set; }
		public string Scope { get; set; } = "";
		public DateTime? FromUtc { get; set; }
		public DateTime? ToUtc { get; set; }
		public int Rows { get; set; }
		public DateTime At { get; set; }
	}

	public static class CommunicationModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<CallSummary>(e => { e.ToTable("CallSummaries"); e.HasKey(x => x.RecordingId); e.Property(x => x.RecordingId).ValueGeneratedNever(); e.Property(x => x.Status).HasMaxLength(16); e.Property(x => x.JobId).HasMaxLength(32); e.Property(x => x.Language).HasMaxLength(8); e.Property(x => x.Model).HasMaxLength(120); e.Property(x => x.Error).HasMaxLength(200); e.Property(x => x.Progress).HasPrecision(5, 3); e.Property(x => x.AudioSeconds).HasPrecision(9, 1); e.Property(x => x.WorkSeconds).HasPrecision(9, 1); });
			b.Entity<Comm.CommMailboxItem>(e => { e.ToTable("CommMailboxItems"); e.Property(x => x.Box).HasMaxLength(10); e.Property(x => x.Address).HasMaxLength(260); e.HasIndex(x => new { x.MessageId, x.EmployeeId, x.Box }).IsUnique().HasDatabaseName("UX_CommMailboxItems_Message_Employee_Box"); });
			b.Entity<ChatDoc>(e => { e.ToTable("ChatDocs"); e.Property(x => x.Title).HasMaxLength(150); e.Property(x => x.FilePath).HasMaxLength(400); });
			b.Entity<ChatDocVersion>(e => { e.ToTable("ChatDocVersions"); e.HasKey(x => new { x.DocId, x.Version }); e.Property(x => x.Title).HasMaxLength(150); });
			b.Entity<ChatGuest>(e => { e.ToTable("ChatGuests"); e.Property(x => x.Name).HasMaxLength(80); e.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).IsFixedLength(); e.HasIndex(x => x.TokenHash).IsUnique(); });
			b.Entity<ChatGuestPost>(e => { e.ToTable("ChatGuestPosts"); e.HasKey(x => x.MessageId); e.Property(x => x.MessageId).ValueGeneratedNever(); });
			b.Entity<ChatWebhook>(e => { e.ToTable("ChatWebhooks"); e.Property(x => x.Name).HasMaxLength(60); e.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).IsFixedLength(); e.HasIndex(x => x.TokenHash).IsUnique(); });
			b.Entity<ChatWebhookPost>(e => { e.ToTable("ChatWebhookPosts"); e.HasKey(x => x.MessageId); e.Property(x => x.MessageId).ValueGeneratedNever(); });
			b.Entity<ChatTranslation>(e => { e.ToTable("ChatTranslations"); e.HasKey(x => new { x.MessageId, x.Lang }); e.Property(x => x.Lang).HasMaxLength(8); });
			b.Entity<ChatTranscript>(e => { e.ToTable("ChatTranscripts"); e.HasKey(x => x.MessageId); e.Property(x => x.MessageId).ValueGeneratedNever(); e.Property(x => x.Status).HasMaxLength(16); e.Property(x => x.Language).HasMaxLength(8); e.Property(x => x.Model).HasMaxLength(40); e.Property(x => x.Error).HasMaxLength(200); e.Property(x => x.AudioSeconds).HasPrecision(9, 2); e.Property(x => x.WorkSeconds).HasPrecision(9, 2); });
			b.Entity<ChatSaved>(e => { e.ToTable("ChatSaved"); e.HasIndex(x => new { x.EmployeeId, x.MessageId }).IsUnique(); });
			b.Entity<ChatScheduled>(e => { e.ToTable("ChatScheduled"); e.Property(x => x.Body).HasMaxLength(4000); e.Property(x => x.Failure).HasMaxLength(200); });
			b.Entity<ChatReminder>(e => e.ToTable("ChatReminders"));
			b.Entity<ChatPoll>(e => { e.ToTable("ChatPolls"); e.Property(x => x.Question).HasMaxLength(300); });
			b.Entity<ChatPollOption>(e => { e.ToTable("ChatPollOptions"); e.Property(x => x.Text).HasMaxLength(150); });
			b.Entity<ChatPollVote>(e => e.ToTable("ChatPollVotes"));
			b.Entity<ChatStatus>(e => { e.ToTable("ChatStatus"); e.HasKey(x => x.EmployeeId); e.Property(x => x.EmployeeId).ValueGeneratedNever(); e.Property(x => x.Emoji).HasMaxLength(16); e.Property(x => x.Text).HasMaxLength(100); });
			b.Entity<ChatSettings>(e => { e.ToTable("ChatSettings"); e.HasKey(x => x.CompanyId); e.Property(x => x.CompanyId).ValueGeneratedNever(); e.Property(x => x.DlpMode).HasMaxLength(8).IsUnicode(false); e.Property(x => x.DlpWords).HasMaxLength(1000); });
			b.Entity<ChatLegalHold>(e => { e.ToTable("ChatLegalHolds"); e.Property(x => x.Reason).HasMaxLength(300); });
			b.Entity<ChatExport>(e => { e.ToTable("ChatExports"); e.Property(x => x.Scope).HasMaxLength(200); });
		}
	}
}
