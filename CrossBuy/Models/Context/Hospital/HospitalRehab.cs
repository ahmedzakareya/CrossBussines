using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H89 — PHYSIOTHERAPY / REHABILITATION (phase 4, after oncology).
	//   * An EPISODE starts as a REFERRAL from a doctor (the problem, the region, the precautions, how many sessions and
	//     how often). The therapist ASSESSES it: the pain (NRS 0–10) and one standard outcome measure with its baseline
	//     score — Oswestry, Neck Disability Index, LEFS, DASH or Barthel — and the episode becomes active. It ends
	//     DISCHARGED with a reason and a summary, or cancelled before it began.
	//   * GOALS are what the patient should reach, by a date; each is met, not met, or still open.
	//   * A SESSION is attended (minutes, what was done, the pain before and after, the measure re-scored now and then),
	//     or a no-show, or cancelled.
	//   * The progress is COMPUTED: the change from the baseline against the measure's minimal clinically important
	//     difference, the attendance, two no-shows in a row, no progress after six sessions, more sessions than planned.
	// CrossBuy/deploy/sql/hospital_h89_rehab.sql builds the tables.
	// ==========================================================================================
	public static class HospitalRehabStatus
	{
		public const string Referred = "Referred";
		public const string Active = "Active";
		public const string Discharged = "Discharged";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Referred, Active, Discharged, Cancelled };
	}

	public static class HospitalRehabRegion
	{
		public static readonly IReadOnlyList<string> All = new[] { "Neck", "Shoulder", "Elbow", "Hand", "Back", "Hip", "Knee", "Ankle", "Neuro", "Cardio", "General" };
	}

	/// A standard outcome measure: its range, which way is better, and its minimal clinically important difference.
	public sealed record HospitalRehabMeasureDef(string Code, int Min, int Max, bool HigherIsBetter, decimal Mcid);

	public static class HospitalRehabMeasure
	{
		public const string Oswestry = "ODI";
		public const string Ndi = "NDI";
		public const string Lefs = "LEFS";
		public const string Dash = "DASH";
		public const string Barthel = "BI";
		public static readonly IReadOnlyList<HospitalRehabMeasureDef> All = new[]
		{
			new HospitalRehabMeasureDef(Oswestry, 0, 100, false, 10m),   // Ostelo 2008
			new HospitalRehabMeasureDef(Ndi, 0, 100, false, 10m),        // in % (Young 2009: 7.5–19)
			new HospitalRehabMeasureDef(Lefs, 0, 80, true, 9m),          // Binkley 1999
			new HospitalRehabMeasureDef(Dash, 0, 100, false, 10.8m),     // Gummesson 2003
			new HospitalRehabMeasureDef(Barthel, 0, 100, true, 10m),     // a 10-point step is the smallest real change on its 5-point items
		};
		public static HospitalRehabMeasureDef? Of(string? code) => All.FirstOrDefault(m => m.Code == code);
		/// The pain scale's MCID (Farrar 2001).
		public const decimal PainMcid = 2m;
	}

	public static class HospitalRehabAttendance
	{
		public const string Attended = "Attended";
		public const string NoShow = "NoShow";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Attended, NoShow, Cancelled };
	}

	public static class HospitalRehabIntervention
	{
		public static readonly IReadOnlyList<string> All = new[] { "Exercise", "Manual", "Electro", "Heat", "Gait", "Balance", "Hydro", "Education", "Taping", "Breathing" };
	}

	public static class HospitalRehabGoalStatus
	{
		public const string Open = "Open";
		public const string Met = "Met";
		public const string NotMet = "NotMet";
		public static readonly IReadOnlyList<string> All = new[] { Open, Met, NotMet };
	}

	public static class HospitalRehabDischarge
	{
		public static readonly IReadOnlyList<string> All = new[] { "GoalsMet", "Plateau", "PatientRequest", "NonAttendance", "Referred", "Medical" };
	}

	public class HospitalRehabEpisode
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string EpisodeNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? AdmissionId { get; set; }
		public string Status { get; set; } = HospitalRehabStatus.Referred;
		public string Region { get; set; } = "General";
		public string Problem { get; set; } = "";
		public string? Precautions { get; set; }
		public int PlannedSessions { get; set; }
		public int PerWeek { get; set; }
		public DateTime ReferredAt { get; set; }
		public int? ReferredByEmployeeId { get; set; }
		public DateTime? AssessedAt { get; set; }
		public int? TherapistEmployeeId { get; set; }
		public string? MeasureCode { get; set; }
		public decimal? BaselineScore { get; set; }
		public int? BaselinePain { get; set; }
		public string? Assessment { get; set; }
		public DateTime? EndedAt { get; set; }
		public string? EndReason { get; set; }
		public string? Summary { get; set; }
	}

	public class HospitalRehabGoal
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EpisodeId { get; set; }
		public int PatientId { get; set; }
		public string Text { get; set; } = "";
		public DateTime? TargetDate { get; set; }
		public string Status { get; set; } = HospitalRehabGoalStatus.Open;
		public DateTime CreatedAt { get; set; }
		public DateTime? ClosedAt { get; set; }
	}

	public class HospitalRehabSession
	{
		public int ID { get; set; }
		/// H119: the bill line this session was charged on (null = not charged: not done, or no price yet).
		public int? BillLineId { get; set; }
		public int CompanyId { get; set; }
		public int EpisodeId { get; set; }
		public int PatientId { get; set; }
		public DateTime SessionDate { get; set; }
		public string Attendance { get; set; } = HospitalRehabAttendance.Attended;
		public int? Minutes { get; set; }
		/// Comma-separated HospitalRehabIntervention codes.
		public string? Interventions { get; set; }
		public int? PainBefore { get; set; }
		public int? PainAfter { get; set; }
		public decimal? Score { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalRehabModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalRehabEpisode>(e =>
			{
				e.ToTable("HospitalRehabEpisodes");   // no DbSet: each names its table
				e.Property(x => x.EpisodeNo).HasMaxLength(20);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.Region).HasMaxLength(10);
				e.Property(x => x.Problem).HasMaxLength(300);
				e.Property(x => x.Precautions).HasMaxLength(300);
				e.Property(x => x.MeasureCode).HasMaxLength(5);
				e.Property(x => x.BaselineScore).HasPrecision(5, 1);
				e.Property(x => x.Assessment).HasMaxLength(1000);
				e.Property(x => x.EndReason).HasMaxLength(15);
				e.Property(x => x.Summary).HasMaxLength(1000);
				e.HasIndex(x => new { x.CompanyId, x.EpisodeNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalRehabGoal>(e =>
			{
				e.ToTable("HospitalRehabGoals");
				e.Property(x => x.Text).HasMaxLength(300);
				e.Property(x => x.TargetDate).HasColumnType("date");
				e.Property(x => x.Status).HasMaxLength(6);
				e.HasIndex(x => new { x.CompanyId, x.EpisodeId });
			});
			b.Entity<HospitalRehabSession>(e =>
			{
				e.ToTable("HospitalRehabSessions");
				e.Property(x => x.SessionDate).HasColumnType("date");
				e.Property(x => x.Attendance).HasMaxLength(9);
				e.Property(x => x.Interventions).HasMaxLength(120);
				e.Property(x => x.Score).HasPrecision(5, 1);
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.EpisodeId, x.SessionDate });
			});
		}
	}
}
