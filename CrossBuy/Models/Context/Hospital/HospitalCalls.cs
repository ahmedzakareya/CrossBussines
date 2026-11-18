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
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
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
			});
		}
	}
}
