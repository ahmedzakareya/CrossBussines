using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H92 — THE UNIDENTIFIED PATIENT. A patient who arrives unconscious, confused or without papers gets a
	// REAL file at once — a temporary, unmistakable name ("Unknown male 2026-11-11 #01"), an estimated age, the sex —
	// so every order, medicine, blood unit, admission and charge has a file to land on from the first minute (the
	// practice of Epic and Cerner trauma/alias registration). This row says the file is a temporary identity: how the
	// patient came, what they looked like, the police case, and how the identity was settled:
	//   * RENAMED — nobody had a file: the temporary file becomes the person's own (the real name, birth date, ID).
	//   * MERGED — the person already had a file: the temporary one is merged into it (H57), everything moving across.
	// CrossBuy/deploy/sql/hospital_h92_unidentified.sql builds the table.
	// ==========================================================================================
	public static class HospitalUnidentifiedResolution
	{
		public const string Renamed = "Renamed";
		public const string Merged = "Merged";
		public static readonly IReadOnlyList<string> All = new[] { Renamed, Merged };
	}

	public class HospitalUnidentified
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		/// The day's sequence of the temporary name (#01, #02 …).
		public int Seq { get; set; }
		public DateTime ArrivedOn { get; set; }
		public int EstimatedAge { get; set; }
		public string Description { get; set; } = "";
		public string? BroughtBy { get; set; }
		public bool MedicoLegal { get; set; }
		public string? PoliceRef { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? IdentifiedAt { get; set; }
		public int? IdentifiedByEmployeeId { get; set; }
		public string? Resolution { get; set; }
		/// Merged: the person's own file the temporary one went into.
		public int? MergedIntoPatientId { get; set; }
		public string? IdentifiedHow { get; set; }
	}

	public static class HospitalUnidentifiedModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalUnidentified>(e =>
			{
				e.ToTable("HospitalUnidentifiedPatients");   // no DbSet: it names its table
				e.Property(x => x.ArrivedOn).HasColumnType("date");
				e.Property(x => x.Description).HasMaxLength(500);
				e.Property(x => x.BroughtBy).HasMaxLength(150);
				e.Property(x => x.PoliceRef).HasMaxLength(60);
				e.Property(x => x.Resolution).HasMaxLength(8);
				e.Property(x => x.IdentifiedHow).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
				e.HasIndex(x => new { x.CompanyId, x.ArrivedOn, x.Seq }).IsUnique();
			});
		}
	}
}
