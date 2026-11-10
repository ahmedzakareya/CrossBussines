using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL EMERGENCY (unit H13).
	//
	// An ER VISIT starts at the door: a known patient, or an unknown one registered quickly by a label
	// ("unknown male, ~40") and linked to the patient file later. It is triaged on the ESI scale, 1 (most
	// urgent) to 5, with its vital signs; it moves along the board — triage, waiting for the doctor, in
	// treatment, observation, disposition — in a bay of the department (its own list: the facility's
	// beds belong to admission, H3) with a doctor (H4),
	// and closes with its disposition (discharged, admitted, transferred, left without being seen, died).
	// The visit keeps the times the department is measured by: door-to-triage and door-to-doctor.
	// CrossBuy/deploy/sql/hospital_h13_emergency.sql builds the tables (prepared, not applied).
	// ==========================================================================================

	public static class HospitalErStage
	{
		public const string Triage = "Triage";
		public const string Waiting = "Waiting";
		public const string Treatment = "Treatment";
		public const string Observation = "Observation";
		public const string Disposition = "Disposition";
		public const string Closed = "Closed";
		/// The board's lanes, in order.
		public static readonly IReadOnlyList<string> Board = new[] { Triage, Waiting, Treatment, Observation, Disposition };
		public static readonly IReadOnlyList<string> All = new[] { Triage, Waiting, Treatment, Observation, Disposition, Closed };
	}

	public static class HospitalErArrival
	{
		public const string WalkIn = "WalkIn";
		public const string Ambulance = "Ambulance";
		public const string Referral = "Referral";
		public static readonly IReadOnlyList<string> All = new[] { WalkIn, Ambulance, Referral };
	}

	public static class HospitalErOutcome
	{
		public const string Discharged = "Discharged";
		public const string Admitted = "Admitted";
		public const string Transferred = "Transferred";
		/// Left without being seen by a doctor — the department's own failure measure.
		public const string LeftWithoutBeingSeen = "LeftWithoutBeingSeen";
		public const string Deceased = "Deceased";
		public static readonly IReadOnlyList<string> All = new[] { Discharged, Admitted, Transferred, LeftWithoutBeingSeen, Deceased };
	}

	public static class HospitalErZone
	{
		public const string Resus = "Resus";
		public const string Acute = "Acute";
		public const string Minor = "Minor";
		public static readonly IReadOnlyList<string> All = new[] { Resus, Acute, Minor };
	}

	/// A place in the department a patient is treated in. Not a facility bed: those are admission's (H3), and a
	/// bay must never appear among the beds a ward can accommodate a patient in.
	public class HospitalErBay
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Zone { get; set; } = HospitalErZone.Acute;
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public class HospitalErVisit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string VisitNo { get; set; } = "";
		/// Null while the patient is unknown; UnknownLabel then says who is in the bay.
		public int? PatientId { get; set; }
		public string? UnknownLabel { get; set; }
		public string ArrivalMode { get; set; } = HospitalErArrival.WalkIn;
		public string Complaint { get; set; } = "";
		/// 1 (resuscitation) … 5 (non-urgent); null until triaged.
		public int? Esi { get; set; }
		public string Stage { get; set; } = HospitalErStage.Triage;
		/// The bay the patient is in.
		public int? BayId { get; set; }
		public int? PractitionerId { get; set; }
		public int? BpSystolic { get; set; }
		public int? BpDiastolic { get; set; }
		public int? HeartRate { get; set; }
		public int? RespRate { get; set; }
		public decimal? TemperatureC { get; set; }
		public int? SpO2 { get; set; }
		public int? PainScore { get; set; }
		public string? TriageNote { get; set; }
		public DateTime ArrivedAt { get; set; }
		public DateTime? TriagedAt { get; set; }
		public int? TriagedByEmployeeId { get; set; }
		/// When a doctor took the patient: the end of door-to-doctor.
		public DateTime? SeenAt { get; set; }
		public string? Outcome { get; set; }
		public string? OutcomeNote { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalEmergencyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalErBay>(e =>
			{
				e.ToTable("HospitalErBays");   // no DbSet: without this EF would look for dbo.HospitalErBay
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalErVisit>(e =>
			{
				e.ToTable("HospitalErVisits");   // no DbSet: without this EF would look for dbo.HospitalErVisit
				e.Property(x => x.TemperatureC).HasPrecision(4, 1);
				// the slice stores the small numbers in small columns: read and write them as such
				foreach (var p in new[] { nameof(HospitalErVisit.Esi), nameof(HospitalErVisit.SpO2), nameof(HospitalErVisit.PainScore) })
					e.Property<int?>(p).HasColumnType("tinyint").HasConversion<byte?>();
				foreach (var p in new[] { nameof(HospitalErVisit.BpSystolic), nameof(HospitalErVisit.BpDiastolic), nameof(HospitalErVisit.HeartRate), nameof(HospitalErVisit.RespRate) })
					e.Property<int?>(p).HasColumnType("smallint").HasConversion<short?>();
				e.HasIndex(x => new { x.CompanyId, x.VisitNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Stage });
				// one open visit per bay, and one open visit per known patient
				e.HasOne<HospitalErBay>().WithMany().HasForeignKey(x => x.BayId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => x.BayId, "UX_HospitalErVisits_OpenBay").IsUnique().HasFilter("[BayId] IS NOT NULL AND [Stage] <> 'Closed'");
				e.HasIndex(x => new { x.CompanyId, x.PatientId }, "UX_HospitalErVisits_OpenPatient").IsUnique().HasFilter("[PatientId] IS NOT NULL AND [Stage] <> 'Closed'");
			});
		}
	}
}
