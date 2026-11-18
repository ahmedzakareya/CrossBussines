using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H176 — internal calls: voice and video between staff, on the hospital's own network only.
	//
	// The call itself never touches the server: audio and video go straight between the two browsers (WebRTC), or
	// through the hospital's own relay (TURN) when they sit on different department networks. What the server keeps is
	// the RECORD of the call — who called whom, when, how long, about which patient, and the note written during it —
	// never its audio or picture. CrossBuy/deploy/sql/hospital_h176_calls.sql builds both tables.
	// ==========================================================================================
	public static class HospitalCallStatus
	{
		/// Sent, the callee's screen is ringing.
		public const string Ringing = "Ringing";
		/// Answered and still going.
		public const string Answered = "Answered";
		/// Answered, then hung up by either side.
		public const string Ended = "Ended";
		/// Not answered in the ring time, or the callee was offline or in another call.
		public const string Missed = "Missed";
		/// The callee refused it (optionally with a quick reply).
		public const string Declined = "Declined";
		/// The caller hung up before it was answered.
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Ringing, Answered, Ended, Missed, Declined, Cancelled };
	}

	public class HospitalCall
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CallerEmployeeId { get; set; }
		public int CalleeEmployeeId { get; set; }
		public bool IsVideo { get; set; }
		/// A group call: the caller is its host and HospitalCallParticipants holds everyone in it.
		public bool IsGroup { get; set; }
		/// The patient the call is about, when it was started from a record or linked during it.
		public int? PatientId { get; set; }
		public string? Reason { get; set; }
		public string Status { get; set; } = HospitalCallStatus.Ringing;
		/// Why it was missed: "offline", "busy" or "no-answer" (null otherwise).
		public string? MissedWhy { get; set; }
		/// UTC.
		public DateTime StartedAt { get; set; }
		public DateTime? AnsweredAt { get; set; }
		public DateTime? EndedAt { get; set; }
		public int? DurationSeconds { get; set; }
		/// The quick reply the callee sent when declining.
		public string? Reply { get; set; }
		/// Written during the call by either participant; the last writer is kept.
		public string? Note { get; set; }
		public int? NoteByEmployeeId { get; set; }
		/// The callee has seen this missed call (it stops counting as waiting for a call back).
		public DateTime? SeenAt { get; set; }
	}

	/// One row per company: what calls may do here and how the relay is reached.
	public class HospitalCallSettings
	{
		public int CompanyId { get; set; }
		public bool VideoEnabled { get; set; } = true;
		public bool ScreenShareEnabled { get; set; } = true;
		public int RingSeconds { get; set; } = 30;
		/// 480, 720 or 1080.
		public int MaxVideoHeight { get; set; } = 720;
		/// The hospital's own STUN/TURN addresses, comma separated (turn:10.0.5.20:3478, turns:…). Empty = same network only.
		public string? TurnUrls { get; set; }
		/// coturn's static-auth-secret: the server mints a short-lived user/password from it for each call. Never sent to a browser.
		public string? TurnSecret { get; set; }
		/// Who may call whom: "caller>callee" pairs of HospitalCallGroups, comma separated. Null = the published default.
		public string? Policy { get; set; }
		/// Recording (with every side's consent inside the call). Off until the administrator turns it on.
		public bool RecordingEnabled { get; set; }
		/// "voice" or "both" (voice, and the picture when the call is a video call).
		public string RecordingMode { get; set; } = HospitalCallRecordingMode.Both;
		/// Days a recording is kept before it is deleted automatically; 0 = never deleted automatically.
		public int RecordingDays { get; set; } = 90;
		/// Group calls (each device connected to each other one): on, and how many people at most, the host included.
		public bool GroupEnabled { get; set; } = true;
		public int GroupMax { get; set; } = 4;
		/// The people NAMED to open every call recording (employee ids, comma separated) — an explicit list, never a
		/// default: besides them only the call's own participants open a recording.
		public string? RecordingReviewers { get; set; }
		/// The hospital's internal media server (LiveKit) for group calls of more than four: its address (wss://…), API key
		/// and secret. The secret never reaches a browser: the server mints a short-lived room token per person.
		public string? SfuUrl { get; set; }
		public string? SfuApiKey { get; set; }
		public string? SfuApiSecret { get; set; }
		/// The staff groups that may ASK to record, comma separated. Null = doctors and administration.
		public string? RecordingGroups { get; set; }
		/// Communication (2026-11-18, comm_call_settings.sql): write each call into the conversation between the two people.
		public bool CallsInChat { get; set; } = true;
		/// Hospital only: a call may be linked to a patient, shown before answering.
		public bool PatientLink { get; set; } = true;
		/// Hospital only: who is on call now, at the top of People.
		public bool OnCallList { get; set; } = true;
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalCallRecordingMode
	{
		public const string Voice = "voice";
		public const string Both = "both";
	}

	public static class HospitalCallRecordingStatus
	{
		/// Asked; the other side has not answered yet.
		public const string Asked = "Asked";
		public const string Refused = "Refused";
		/// Both agreed; the requester's browser is recording and sending it in pieces.
		public const string Recording = "Recording";
		/// The whole file is on the document platform.
		public const string Saved = "Saved";
		/// The browser never finished it (closed, crashed) and nothing usable arrived.
		public const string Failed = "Failed";
		/// Its retention ran out: the bytes are gone, the record of it stays.
		public const string Deleted = "Deleted";
	}

	/// H176 — one recording of a call: who asked, who agreed and when, and where the file is. The audio and picture are
	/// recorded in the REQUESTER's browser (it holds both sides), sent in pieces to a private temporary file, then kept as
	/// a document on the HospitalCall record of the document platform.
	public class HospitalCallRecording
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CallId { get; set; }
		public int RequestedByEmployeeId { get; set; }
		/// Whose browser records and sends the file, when not the requester's: a phone cannot mix both sides into one
		/// file, so when the request comes from the app the browser at the other end records it. Null = the requester.
		public int? RecorderEmployeeId { get; set; }
		public DateTime RequestedAt { get; set; }
		public int? AgreedByEmployeeId { get; set; }
		public DateTime? AgreedAt { get; set; }
		public string Status { get; set; } = HospitalCallRecordingStatus.Asked;
		/// "voice" or "video" — what is really in the file.
		public string Kind { get; set; } = "voice";
		public DateTime? StartedAt { get; set; }
		public DateTime? EndedAt { get; set; }
		public int? StoppedByEmployeeId { get; set; }
		public int? DurationSeconds { get; set; }
		public long? SizeBytes { get; set; }
		/// Pieces received so far (they must arrive in order).
		public int Chunks { get; set; }
		/// The private temporary file's name while recording (a GUID, never a path from the client).
		public string? TempName { get; set; }
		public long? DocumentId { get; set; }
		public DateTime? DeleteAfter { get; set; }
		public DateTime? DeletedAt { get; set; }
	}

	/// Every opening of a recording: who and when.
	public class HospitalCallRecordingView
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int RecordingId { get; set; }
		public int EmployeeId { get; set; }
		public DateTime At { get; set; }
	}

	public static class HospitalCallParticipantState
	{
		public const string Host = "Host";
		public const string Invited = "Invited";
		public const string Joined = "Joined";
		public const string Declined = "Declined";
		/// Not answered in the ring time.
		public const string Missed = "Missed";
		/// Offline or in another call when invited.
		public const string Busy = "Busy";
		public const string Left = "Left";
		public const string Removed = "Removed";
	}

	/// H176 — everyone in a group call: who was invited, who joined and when, who left or was removed.
	public class HospitalCallParticipant
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CallId { get; set; }
		public int EmployeeId { get; set; }
		public string State { get; set; } = HospitalCallParticipantState.Invited;
		public DateTime InvitedAt { get; set; }
		public DateTime? JoinedAt { get; set; }
		public DateTime? LeftAt { get; set; }
		public int? RemovedByEmployeeId { get; set; }
	}

	/// H176 — each participant's consent to a group recording: pressed "I agree", or joined a call already being recorded.
	public class HospitalCallRecordingConsent
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int RecordingId { get; set; }
		public int EmployeeId { get; set; }
		public DateTime At { get; set; }
		/// "agreed" or "joined".
		public string Via { get; set; } = "agreed";
	}

	public static class HospitalCallModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalCall>(e =>
			{
				e.ToTable("HospitalCalls");   // no DbSet: it names its table
				e.Property(x => x.Reason).HasMaxLength(200);
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.MissedWhy).HasMaxLength(12);
				e.Property(x => x.Reply).HasMaxLength(120);
				e.Property(x => x.Note).HasMaxLength(1000);
				e.HasIndex(x => new { x.CompanyId, x.CallerEmployeeId, x.StartedAt });
				e.HasIndex(x => new { x.CompanyId, x.CalleeEmployeeId, x.StartedAt });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalCallSettings>(e =>
			{
				e.ToTable("HospitalCallSettings");
				e.HasKey(x => x.CompanyId);
				e.Property(x => x.CompanyId).ValueGeneratedNever();
				e.Property(x => x.TurnUrls).HasMaxLength(300);
				e.Property(x => x.TurnSecret).HasMaxLength(200);
				e.Property(x => x.Policy).HasMaxLength(600);
				e.Property(x => x.RecordingMode).HasMaxLength(8);
				e.Property(x => x.RecordingGroups).HasMaxLength(200);
				e.Property(x => x.RecordingReviewers).HasMaxLength(400);
				e.Property(x => x.SfuUrl).HasMaxLength(300);
				e.Property(x => x.SfuApiKey).HasMaxLength(100);
				e.Property(x => x.SfuApiSecret).HasMaxLength(200);
			});
			b.Entity<HospitalCallRecording>(e =>
			{
				e.ToTable("HospitalCallRecordings");
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.Kind).HasMaxLength(8);
				e.Property(x => x.TempName).HasMaxLength(40);
				e.HasIndex(x => new { x.CompanyId, x.CallId });
				e.HasIndex(x => new { x.CompanyId, x.Status, x.DeleteAfter });
			});
			b.Entity<HospitalCallParticipant>(e =>
			{
				e.ToTable("HospitalCallParticipants");
				e.Property(x => x.State).HasMaxLength(10);
				e.HasIndex(x => new { x.CallId, x.EmployeeId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.EmployeeId });
			});
			b.Entity<HospitalCallRecordingConsent>(e =>
			{
				e.ToTable("HospitalCallRecordingConsents");
				e.Property(x => x.Via).HasMaxLength(8);
				e.HasIndex(x => new { x.RecordingId, x.EmployeeId }).IsUnique();
			});
			b.Entity<HospitalCallRecordingView>(e =>
			{
				e.ToTable("HospitalCallRecordingViews");
				e.HasIndex(x => new { x.CompanyId, x.RecordingId });
			});
		}
	}
}
