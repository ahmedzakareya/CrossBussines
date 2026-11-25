using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H270 — the patient's allergies as a coded list. The owner (2026-11-25): "we write the penicillin allergy
	// as free text — how will you link it? we need to change the business". One row per allergy:
	//   · AllergenCode — an entry of HospitalAllergenCatalog (its family is what the drug check links to), or NULL for an
	//     allergen the list does not hold, named in Text ("not coded", shown for review);
	//   · "NKA" alone — the patient was ASKED and has no known allergy (different from nobody having asked);
	//   · Reaction and Severity — what happened and how bad.
	// A removed allergy keeps its row (RemovedAt): the file's history of what was believed stays whole.
	// HospitalPatients.Allergies stays the text every screen, band and report reads; it is rewritten from the active rows
	// on every save, so the list is the truth and the text its print.
	// CrossBuy/deploy/sql/hospital_h270_patient_allergies.sql builds the table.
	// ==========================================================================================
	public class HospitalPatientAllergy
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string? AllergenCode { get; set; }
		public string? Text { get; set; }
		public string? Reaction { get; set; }
		public string? Severity { get; set; }
		public DateTime RecordedAt { get; set; }
		public int? RecordedByEmployeeId { get; set; }
		public DateTime? RemovedAt { get; set; }
		public int? RemovedByEmployeeId { get; set; }
	}

	public static class HospitalPatientAllergyModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalPatientAllergy>(e =>
			{
				e.ToTable("HospitalPatientAllergies");   // no DbSet: it names its table
				e.Property(x => x.AllergenCode).HasMaxLength(20);
				e.Property(x => x.Text).HasMaxLength(100);
				e.Property(x => x.Reaction).HasMaxLength(20);
				e.Property(x => x.Severity).HasMaxLength(10);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
