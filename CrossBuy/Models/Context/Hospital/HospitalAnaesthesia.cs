using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalAnaesthesiaTechnique
	{
		public const string General = "General";
		public const string Spinal = "Spinal";
		public const string Epidural = "Epidural";
		public const string Regional = "Regional";
		public const string Sedation = "Sedation";
		public const string Local = "Local";
		public static readonly IReadOnlyList<string> All = new[] { General, Spinal, Epidural, Regional, Sedation, Local };
	}

	public static class HospitalAnaesthesiaEntryKind
	{
		public const string Event = "Event";
		public const string Drug = "Drug";
		public const string Vitals = "Vitals";
		public const string Fluid = "Fluid";
		public const string Aldrete = "Aldrete";
		public static readonly IReadOnlyList<string> All = new[] { Event, Drug, Vitals, Fluid, Aldrete };
	}

	/// The anaesthetic's milestones, in the order they happen.
	public static class HospitalAnaesthesiaEvent
	{
		public const string Induction = "Induction";
		public const string Airway = "Airway";
		public const string Incision = "Incision";
		public const string SurgeryEnd = "SurgeryEnd";
		public const string Emergence = "Emergence";
		public static readonly IReadOnlyList<string> All = new[] { Induction, Airway, Incision, SurgeryEnd, Emergence };
	}

	// HOSPITAL H202 — one operation's anaesthesia record: the pre-anaesthetic assessment (ASA, airway, fasting, the planned
	// technique), the recovery room (arrival, leaving) — and, in HospitalAnaesthesiaEntries, what happened in between.
	// Times are the theatre's (local), as the operation's own StartedAt/EndedAt are.
	public class HospitalAnaesthesia
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SurgeryId { get; set; }
		public int PatientId { get; set; }
		/// ASA physical status 1–6; Emergency is its "E".
		public int Asa { get; set; }
		public bool Emergency { get; set; }
		public decimal? WeightKg { get; set; }
		public int? Mallampati { get; set; }
		public decimal? MouthOpeningCm { get; set; }
		public decimal? ThyromentalCm { get; set; }
		/// Predicted from the airway findings when the assessment is saved (the team prepares for it).
		public bool DifficultAirway { get; set; }
		public DateTime? LastSolidsAt { get; set; }
		public DateTime? LastMilkAt { get; set; }
		public DateTime? LastClearAt { get; set; }
		/// An emergency started before fasting is complete: why (required then).
		public string? FastingOverride { get; set; }
		public string Technique { get; set; } = HospitalAnaesthesiaTechnique.General;
		public string? PlanText { get; set; }
		public DateTime AssessedAt { get; set; }
		public int? AssessedByEmployeeId { get; set; }
		public DateTime? PacuInAt { get; set; }
		public DateTime? PacuOutAt { get; set; }
		public int? PacuOutByEmployeeId { get; set; }
	}

	public class HospitalAnaesthesiaEntry
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AnaesthesiaId { get; set; }
		public DateTime At { get; set; }
		public string Kind { get; set; } = HospitalAnaesthesiaEntryKind.Event;
		/// Event: its code · Drug / Fluid: the name · Aldrete: the five item scores "2,2,1,2,2".
		public string? Code { get; set; }
		public decimal? Dose { get; set; }
		public string? Unit { get; set; }
		public string? Route { get; set; }
		public int? HeartRate { get; set; }
		public int? SystolicBp { get; set; }
		public int? DiastolicBp { get; set; }
		public int? SpO2 { get; set; }
		public int? EtCo2 { get; set; }
		public int? Score { get; set; }
		public string? Note { get; set; }
		public DateTime RecordedAt { get; set; }
		public int? RecordedByEmployeeId { get; set; }
	}

	public static class HospitalAnaesthesiaModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalAnaesthesia>(e =>
			{
				e.ToTable("HospitalAnaesthesias");   // no DbSet: it names its table
				e.Property(x => x.WeightKg).HasPrecision(5, 1);
				e.Property(x => x.MouthOpeningCm).HasPrecision(4, 1);
				e.Property(x => x.ThyromentalCm).HasPrecision(4, 1);
				e.Property(x => x.FastingOverride).HasMaxLength(300);
				e.Property(x => x.Technique).HasMaxLength(20);
				e.Property(x => x.PlanText).HasMaxLength(1000);
				e.HasIndex(x => new { x.CompanyId, x.SurgeryId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalAnaesthesiaEntry>(e =>
			{
				e.ToTable("HospitalAnaesthesiaEntries");
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Code).HasMaxLength(100);
				e.Property(x => x.Dose).HasPrecision(9, 3);
				e.Property(x => x.Unit).HasMaxLength(20);
				e.Property(x => x.Route).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AnaesthesiaId, x.At });
			});
		}
	}
}
