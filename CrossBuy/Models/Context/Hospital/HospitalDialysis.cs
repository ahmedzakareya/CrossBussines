using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H87 — HAEMODIALYSIS (phase 4, after maternity). A chronic patient comes on fixed days in a fixed shift.
	//   * A PLAN is the patient's standing prescription: dry weight, vascular access, hours, blood and dialysate flow,
	//     the dialyser, the anticoagulation, the weekdays and the shift. One active plan per patient; it changes in place,
	//     and every session copies what it needs (dry weight, planned minutes), so the history never moves under it.
	//   * A SESSION is one treatment on one day: weighed and measured before, observed every hour or so during, weighed,
	//     measured and (monthly) drawn for urea after. A scheduled patient who did not come is a session marked missed.
	//   * The adequacy and the risks are COMPUTED, never ticked: the weight gained since the last session, the
	//     ultrafiltration rate, the urea reduction ratio and spKt/V (Daugirdas II), a fall in blood pressure, a session cut
	//     short, a patient left above the dry weight.
	// CrossBuy/deploy/sql/hospital_h87_dialysis.sql builds the tables.
	// ==========================================================================================
	public static class HospitalDialysisPlanStatus
	{
		public const string Active = "Active";
		public const string Stopped = "Stopped";
	}

	public static class HospitalDialysisAccess
	{
		public const string Fistula = "AVF";
		public const string Graft = "AVG";
		public const string Catheter = "CVC";
		public static readonly IReadOnlyList<string> All = new[] { Fistula, Graft, Catheter };
	}

	public static class HospitalDialysisSessionStatus
	{
		public const string Running = "Running";
		public const string Done = "Done";
		public const string Missed = "Missed";
		public static readonly IReadOnlyList<string> All = new[] { Running, Done, Missed };
	}

	/// What went wrong during a session, ticked by the nurse at the end (the computed warnings are separate).
	public static class HospitalDialysisComplication
	{
		public const string Cramps = "Cramps";
		public const string Nausea = "Nausea";
		public const string ChestPain = "ChestPain";
		public const string Headache = "Headache";
		public const string Clotting = "Clotting";
		public const string AccessProblem = "Access";
		public const string Fever = "Fever";
		public static readonly IReadOnlyList<string> All = new[] { Cramps, Nausea, ChestPain, Headache, Clotting, AccessProblem, Fever };
	}

	public class HospitalDialysisPlan
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Status { get; set; } = HospitalDialysisPlanStatus.Active;
		public decimal DryWeightKg { get; set; }
		public string Access { get; set; } = HospitalDialysisAccess.Fistula;
		public int DurationMin { get; set; }
		public int BloodFlow { get; set; }
		public int? DialysateFlow { get; set; }
		public string? Dialyzer { get; set; }
		public string? Anticoagulation { get; set; }
		/// The weekdays, one bit each: Sunday = 1, Monday = 2 … Saturday = 64.
		public int DaysMask { get; set; }
		public int Shift { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
		public DateTime? StoppedAt { get; set; }
	}

	public class HospitalDialysisSession
	{
		public int ID { get; set; }
		/// H119: the bill line this session was charged on (null = not charged: not done, or no price yet).
		public int? BillLineId { get; set; }
		public int CompanyId { get; set; }
		public int PlanId { get; set; }
		public int PatientId { get; set; }
		public DateTime SessionDate { get; set; }
		public int Shift { get; set; }
		public string? Station { get; set; }
		public string Status { get; set; } = HospitalDialysisSessionStatus.Running;
		/// Copied from the plan when the session starts.
		public decimal DryWeightKg { get; set; }
		public int PlannedMin { get; set; }
		public DateTime? StartedAt { get; set; }
		public DateTime? EndedAt { get; set; }
		public decimal? PreWeightKg { get; set; }
		public int? PreSbp { get; set; }
		public int? PreDbp { get; set; }
		public int? PrePulse { get; set; }
		public int? UfGoalMl { get; set; }
		public decimal? PostWeightKg { get; set; }
		public int? PostSbp { get; set; }
		public int? PostDbp { get; set; }
		/// The volume the machine removed.
		public int? UfMl { get; set; }
		public decimal? PreUrea { get; set; }
		public decimal? PostUrea { get; set; }
		/// Comma-separated HospitalDialysisComplication codes.
		public string? Complications { get; set; }
		public string? MissedReason { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public int? ClosedByEmployeeId { get; set; }
	}

	public class HospitalDialysisObservation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SessionId { get; set; }
		public int PatientId { get; set; }
		public DateTime At { get; set; }
		public int? Sbp { get; set; }
		public int? Dbp { get; set; }
		public int? Pulse { get; set; }
		public int? BloodFlow { get; set; }
		public int? VenousPressure { get; set; }
		/// The volume removed so far.
		public int? UfMl { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalDialysisModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDialysisPlan>(e =>
			{
				e.ToTable("HospitalDialysisPlans");   // no DbSet: each names its table
				e.Property(x => x.Status).HasMaxLength(8);
				e.Property(x => x.DryWeightKg).HasPrecision(5, 1);
				e.Property(x => x.Access).HasMaxLength(3);
				e.Property(x => x.Dialyzer).HasMaxLength(60);
				e.Property(x => x.Anticoagulation).HasMaxLength(100);
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalDialysisSession>(e =>
			{
				e.ToTable("HospitalDialysisSessions");
				e.Property(x => x.SessionDate).HasColumnType("date");
				e.Property(x => x.Station).HasMaxLength(20);
				e.Property(x => x.Status).HasMaxLength(8);
				e.Property(x => x.DryWeightKg).HasPrecision(5, 1);
				e.Property(x => x.PreWeightKg).HasPrecision(5, 1);
				e.Property(x => x.PostWeightKg).HasPrecision(5, 1);
				e.Property(x => x.PreUrea).HasPrecision(6, 1);
				e.Property(x => x.PostUrea).HasPrecision(6, 1);
				e.Property(x => x.Complications).HasMaxLength(100);
				e.Property(x => x.MissedReason).HasMaxLength(200);
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.SessionDate }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.SessionDate });
			});
			b.Entity<HospitalDialysisObservation>(e =>
			{
				e.ToTable("HospitalDialysisObservations");
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.SessionId, x.At });
			});
		}
	}
}
