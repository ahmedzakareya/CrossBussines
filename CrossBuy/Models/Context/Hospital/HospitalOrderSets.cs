using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H74 — ORDER SETS (phase 1 "patient safety" of the 2026-11-09 gap research; the big systems' "order sets"
	// / "PowerPlans"). A named protocol the hospital agrees once — "chest pain, admission", "adult pre-op" — holding the
	// medicines to give and the nursing tasks to do, each pre-ticked or not. A doctor opens a set for an inpatient,
	// unticks what does not fit, and places the rest in one step: every medicine through the drug-safety check (H73), a
	// stopping alert answered with one reason, the tasks put on the ward board, the use recorded. Lab tests and imaging
	// join the sets when inpatient diagnostic ordering exists (today those orders are born from a signed visit only).
	// CrossBuy/deploy/sql/hospital_h74_order_sets.sql builds the tables.
	// ==========================================================================================
	public static class HospitalOrderSetItemKind
	{
		public const string Medication = "Med";
		public const string Task = "Task";
		/// H75: a lab test, and an imaging exam (a catalogue service), ordered for the inpatient when the set is placed.
		public const string Lab = "Lab";
		public const string Imaging = "Rad";
		public static readonly IReadOnlyList<string> All = new[] { Medication, Task, Lab, Imaging };
	}

	public class HospitalOrderSet
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		/// The department it belongs to (null: any).
		public int? DepartmentId { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalOrderSetItem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SetId { get; set; }
		public int SortOrder { get; set; }
		public string Kind { get; set; } = HospitalOrderSetItemKind.Medication;
		// a medicine, as the nursing order takes it
		public string? Drug { get; set; }
		public string? Dose { get; set; }
		public string? Route { get; set; }
		public string? Hours { get; set; }
		public bool AsNeeded { get; set; }
		public string? Note { get; set; }
		// a nursing task, said in both languages, due so many hours after the set is placed
		public string? TitleAr { get; set; }
		public string? TitleEn { get; set; }
		public int? DueInHours { get; set; }
		/// Ticked when the set is opened.
		public bool Preselected { get; set; } = true;
		/// H75: a lab test or an imaging exam — the catalogue service.
		public int? ServiceId { get; set; }
	}

	/// A set placed for an admission — how much of it, by whom, when.
	public class HospitalOrderSetUse
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SetId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public int ItemsPlaced { get; set; }
		public int ItemsInSet { get; set; }
		public DateTime PlacedAt { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalOrderSetModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalOrderSet>(e =>
			{
				e.ToTable("HospitalOrderSets");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(20);
				e.Property(x => x.Name).HasMaxLength(150);
				e.Property(x => x.NameEn).HasMaxLength(150);
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalOrderSetItem>(e =>
			{
				e.ToTable("HospitalOrderSetItems");
				e.Property(x => x.Kind).HasMaxLength(5);
				e.Property(x => x.Drug).HasMaxLength(200);
				e.Property(x => x.Dose).HasMaxLength(60);
				e.Property(x => x.Route).HasMaxLength(10);
				e.Property(x => x.Hours).HasMaxLength(60);
				e.Property(x => x.Note).HasMaxLength(300);
				e.Property(x => x.TitleAr).HasMaxLength(90);
				e.Property(x => x.TitleEn).HasMaxLength(90);
				e.HasIndex(x => new { x.CompanyId, x.SetId });
			});
			b.Entity<HospitalOrderSetUse>(e =>
			{
				e.ToTable("HospitalOrderSetUses");
				e.HasIndex(x => new { x.CompanyId, x.SetId });
			});
		}
	}
}
