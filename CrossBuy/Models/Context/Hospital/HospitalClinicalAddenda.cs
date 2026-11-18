using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H177 — addenda and corrections. A signed clinical record (a visit, a drawing) or a closed labour is never
	// edited: anything learned or found wrong afterwards is ADDED beside it — an addendum (more information) or a
	// correction (what was wrong, what is right, and why) — dated, signed by its author, and never edited or deleted.
	// The record shows them under it, and its paper prints them. deploy/sql/hospital_h177_clinical_addenda.sql.
	// ==========================================================================================
	public static class HospitalAddendumSubject
	{
		public const string Encounter = "Encounter";
		public const string Canvas = "Canvas";
		public const string Labour = "Labour";
		public static readonly IReadOnlyList<string> All = new[] { Encounter, Canvas, Labour };
	}

	public static class HospitalAddendumKind
	{
		public const string Addendum = "Addendum";
		public const string Correction = "Correction";
		public static readonly IReadOnlyList<string> All = new[] { Addendum, Correction };
	}

	public static class HospitalAddendumSection
	{
		/// The part of the record an addendum is about (a visit's SOAP parts; "Other" for the rest).
		public static readonly IReadOnlyList<string> All = new[] { "ChiefComplaint", "History", "Examination", "Assessment", "Plan", "Results", "Other" };
	}

	public class HospitalClinicalAddendum
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Subject { get; set; } = HospitalAddendumSubject.Encounter;
		public int SubjectId { get; set; }
		public string Kind { get; set; } = HospitalAddendumKind.Addendum;
		public string? Section { get; set; }
		public string Text { get; set; } = "";
		/// Why the record was wrong (a correction must say).
		public string? Reason { get; set; }
		public DateTime CreatedAt { get; set; }
		public int CreatedByEmployeeId { get; set; }
	}

	public static class HospitalClinicalAddendaModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalClinicalAddendum>(e =>
			{
				e.ToTable("HospitalClinicalAddenda");   // no DbSet: it names its table
				e.Property(x => x.Subject).HasMaxLength(20);
				e.Property(x => x.Kind).HasMaxLength(12);
				e.Property(x => x.Section).HasMaxLength(20);
				e.Property(x => x.Text).HasMaxLength(2000);
				e.Property(x => x.Reason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.Subject, x.SubjectId });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
