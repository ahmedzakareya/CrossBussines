using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL NURSING STATION (unit H14).
	//
	// An inpatient MEDICATION ORDER belongs to an admission (H3): the drug (an inventory item when the
	// pharmacy stocks it, H10), its dose and route, the hours of the day it is given (or "as needed"), and
	// whether it is a controlled drug. The MEDICATION ADMINISTRATION RECORD (MAR) keeps one entry per
	// scheduled dose — given, held or missed — with who recorded it, the reason when it was not given, and
	// for a controlled drug the witness who signed with them. NURSING TASKS are the ward's to-do list per
	// patient; a SHIFT HANDOVER is one note per ward, day and shift.
	// CrossBuy/deploy/sql/hospital_h14_nursing.sql builds the tables (prepared, not applied).
	// ==========================================================================================

	public static class HospitalMedRoute
	{
		public const string Oral = "PO";
		public const string Intravenous = "IV";
		public const string Intramuscular = "IM";
		public const string Subcutaneous = "SC";
		public const string Inhaled = "INH";
		public const string Topical = "TOP";
		public static readonly IReadOnlyList<string> All = new[] { Oral, Intravenous, Intramuscular, Subcutaneous, Inhaled, Topical };
	}

	public static class HospitalMedOrderStatus
	{
		public const string Active = "Active";
		public const string Stopped = "Stopped";
	}

	public static class HospitalMarStatus
	{
		public const string Given = "Given";
		public const string Held = "Held";
		public const string Missed = "Missed";
		public static readonly IReadOnlyList<string> All = new[] { Given, Held, Missed };
	}

	public static class HospitalShift
	{
		public const string Day = "Day";
		public const string Evening = "Evening";
		public const string Night = "Night";
		public static readonly IReadOnlyList<string> All = new[] { Day, Evening, Night };
	}

	public class HospitalMedOrder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Drug { get; set; } = "";
		public int? ItemId { get; set; }
		/// "1 g", "500 mg", "10 units" — as the doctor writes it.
		public string Dose { get; set; } = "";
		public string Route { get; set; } = HospitalMedRoute.Oral;
		/// The ward-clock hours of the doses, "06,14,22"; empty for an as-needed order.
		public string? Hours { get; set; }
		public bool AsNeeded { get; set; }
		public bool Controlled { get; set; }
		public string? Note { get; set; }
		public string Status { get; set; } = HospitalMedOrderStatus.Active;
		public DateTime StartAt { get; set; }
		public DateTime? StoppedAt { get; set; }
		public int? OrderedByEmployeeId { get; set; }
		public int? StoppedByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	public class HospitalMarEntry
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int OrderId { get; set; }
		public int PatientId { get; set; }
		/// The scheduled dose this entry answers (UTC); null for an as-needed dose.
		public DateTime? DueAt { get; set; }
		public string Status { get; set; } = HospitalMarStatus.Given;
		public string? Reason { get; set; }
		public DateTime RecordedAt { get; set; }
		public int? RecordedByEmployeeId { get; set; }
		/// A controlled drug's second signature.
		public int? WitnessEmployeeId { get; set; }
	}

	public class HospitalNursingTask
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Title { get; set; } = "";
		public DateTime DueAt { get; set; }
		public DateTime? DoneAt { get; set; }
		public int? DoneByEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalShiftHandover
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int WardId { get; set; }
		public DateTime ShiftDate { get; set; }
		public string Shift { get; set; } = HospitalShift.Day;
		public string Note { get; set; } = "";
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public static class HospitalNursingModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalAcuityModel.Configure(b);   // H246: acuity classification and ward staffing
			HospitalBedsideScanModel.Configure(b);   // H205: every bedside scan, the ones that stopped a step included
			HospitalRestraintModel.Configure(b);   // H213: restraint and seclusion, with their checks
			b.Entity<HospitalMedOrder>(e =>
			{
				e.ToTable("HospitalMedOrders");   // no DbSet: without this EF would look for dbo.HospitalMedOrder
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.Status });
			});
			b.Entity<HospitalMarEntry>(e =>
			{
				e.ToTable("HospitalMarEntries");
				e.HasOne<HospitalMedOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.NoAction);
				// one answer per scheduled dose: two nurses recording the same dose at once cannot both land
				e.HasIndex(x => new { x.OrderId, x.DueAt }, "UX_HospitalMarEntries_Dose").IsUnique().HasFilter("[DueAt] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.RecordedAt });
			});
			b.Entity<HospitalNursingTask>(e =>
			{
				e.ToTable("HospitalNursingTasks");
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.DueAt });
			});
			b.Entity<HospitalShiftHandover>(e =>
			{
				e.ToTable("HospitalShiftHandovers");
				e.Property(x => x.ShiftDate).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.WardId, x.ShiftDate, x.Shift }).IsUnique();
			});
		}
	}
}
