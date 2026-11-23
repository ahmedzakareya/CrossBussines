using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H260 — the STRUCTURED OPERATIVE NOTE of a theatre case (JCI ASC: written and signed before the patient
	// leaves recovery). One note per case: the diagnoses before and after, the procedure performed, the indication,
	// the findings, the technique, specimens, estimated blood loss, drains, complications (written "none" when there
	// were none) and the post-operative plan. Anyone on the team drafts it; only the case's surgeon signs it, and a
	// signed note is locked. The case is not closed while its note is unsigned.
	// deploy/sql/hospital_h260_operative_notes.sql builds the table.
	// ==========================================================================================
	public class HospitalOperativeNote
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int SurgeryId { get; set; }
		public int PatientId { get; set; }
		public string? PreOpDiagnosis { get; set; }
		public string? PostOpDiagnosis { get; set; }
		public string? ProcedurePerformed { get; set; }
		public string? Indication { get; set; }
		public string? Findings { get; set; }
		public string? Technique { get; set; }
		public string? Specimens { get; set; }
		public int? BloodLossMl { get; set; }
		public string? Drains { get; set; }
		public string? Complications { get; set; }
		public string? PostOpPlan { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
		public DateTime? SignedAt { get; set; }
		public int? SignedByEmployeeId { get; set; }
	}

	public static class HospitalOperativeNoteModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalOperativeNote>(e =>
			{
				e.ToTable("HospitalOperativeNotes");
				e.Property(x => x.PreOpDiagnosis).HasMaxLength(300); e.Property(x => x.PostOpDiagnosis).HasMaxLength(300); e.Property(x => x.ProcedurePerformed).HasMaxLength(300);
				e.Property(x => x.Indication).HasMaxLength(1000); e.Property(x => x.Findings).HasMaxLength(2000); e.Property(x => x.Technique).HasMaxLength(4000);
				e.Property(x => x.Specimens).HasMaxLength(500); e.Property(x => x.Drains).HasMaxLength(300); e.Property(x => x.Complications).HasMaxLength(1000); e.Property(x => x.PostOpPlan).HasMaxLength(2000);
				e.HasIndex(x => new { x.CompanyId, x.SurgeryId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
