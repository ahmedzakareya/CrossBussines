using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — the theatre schedule (unit H29), as the approved prototype draws it (#/or): a timeline per
	// operating theatre, the case in progress with its surgeon and anaesthetist, and the WHO surgical safety
	// checklist.
	//
	//   · A CASE is one operation: a patient, a procedure (a catalogue service), a theatre (a facility room of
	//     kind Theatre), a surgeon (privilege "surg") and an anaesthetist (privilege "anes"), at a time and for a
	//     length. A theatre, a surgeon and an anaesthetist are each in one case at a time.
	//   · It moves Planned → InProgress → Done, or Planned → Cancelled (with the reason). Starting needs the
	//     checklist's "before induction" items; closing needs all of them (the instrument count among them).
	//   · Done charges the procedure to the patient's bill: the stay's bill when admitted, otherwise their own
	//     open bill — once (the case keeps the bill line it made).
	// CrossBuy/deploy/sql/hospital_h29_theatre.sql builds the tables.
	// ==========================================================================================

	public static class HospitalSurgeryStatus
	{
		public const string Planned = "Planned";
		public const string InProgress = "InProgress";
		public const string Done = "Done";
		public const string Cancelled = "Cancelled";
		public static readonly IReadOnlyList<string> All = new[] { Planned, InProgress, Done, Cancelled };
		/// Holds its theatre, surgeon and anaesthetist.
		public static bool Holds(string s) => s is Planned or InProgress;
	}

	/// The WHO surgical safety checklist, as the prototype lists it. The first four are checked before induction
	/// (they let the case start); the last two before the patient leaves the theatre (they let it close).
	public static class HospitalSurgeryChecklist
	{
		public const string Identity = "Identity";
		public const string Consent = "Consent";
		public const string Anaesthesia = "Anaesthesia";
		public const string Allergies = "Allergies";
		public const string Count = "Count";
		public const string Specimens = "Specimens";
		public static readonly IReadOnlyList<string> BeforeStart = new[] { Identity, Consent, Anaesthesia, Allergies };
		public static readonly IReadOnlyList<string> All = new[] { Identity, Consent, Anaesthesia, Allergies, Count, Specimens };
	}

	public class HospitalSurgery
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CaseNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? AdmissionId { get; set; }
		public int RoomId { get; set; }
		public int ServiceId { get; set; }
		/// The procedure as it was booked (its code is the CPT the case card shows).
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int SurgeonId { get; set; }
		public int? AnaesthetistId { get; set; }
		public DateTime CaseDate { get; set; }
		public int StartMinute { get; set; }
		public int DurationMinutes { get; set; }
		public string Status { get; set; } = HospitalSurgeryStatus.Planned;
		public DateTime? StartedAt { get; set; }
		public DateTime? EndedAt { get; set; }
		public string? CancelReason { get; set; }
		public string? Note { get; set; }
		/// The bill line Done made (so a case is charged once).
		public int? BillLineId { get; set; }
		/// H37: when the procedure's kit left the theatre store (null until it has — never twice).
		public DateTime? KitIssuedAt { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalSurgeryCheck
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SurgeryId { get; set; }
		public string Item { get; set; } = "";
		public DateTime CheckedAt { get; set; }
		public int? CheckedByEmployeeId { get; set; }
	}

	/// H37: the kit a procedure consumes — an inventory item whose bill of materials lists the consumables.
	public class HospitalProcedureKit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int ServiceId { get; set; }
		public int KitItemId { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	/// H37: the theatre's setting — the store a case's kit is drawn from.
	public class HospitalTheatreSetting
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int? StoreWarehouseId { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	public static class HospitalTheatreModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalSurgery>(e =>
			{
				e.ToTable("HospitalSurgeries");   // no DbSet: each names its table
				e.Property(x => x.CaseNo).HasMaxLength(30);
				e.Property(x => x.Code).HasMaxLength(50);
				e.Property(x => x.Name).HasMaxLength(200);
				e.Property(x => x.NameEn).HasMaxLength(200);
				e.Property(x => x.Status).HasMaxLength(20);
				e.Property(x => x.CancelReason).HasMaxLength(300);
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.CaseNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.CaseDate, x.RoomId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalSurgeryCheck>(e =>
			{
				e.ToTable("HospitalSurgeryChecks");
				e.Property(x => x.Item).HasMaxLength(20);
				e.HasIndex(x => new { x.SurgeryId, x.Item }).IsUnique();   // each item is ticked once
			});
			b.Entity<HospitalProcedureKit>(e =>
			{
				e.ToTable("HospitalProcedureKits");
				e.HasIndex(x => new { x.CompanyId, x.ServiceId }).IsUnique();   // one kit per procedure
			});
			b.Entity<HospitalTheatreSetting>(e =>
			{
				e.ToTable("HospitalTheatreSettings");
				e.HasIndex(x => x.CompanyId).IsUnique();
			});
		}
	}
}
