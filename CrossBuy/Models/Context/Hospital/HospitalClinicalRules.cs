using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H171 — the clinical rule register. A rule the software applies to a patient (a stage, a dose, a referral,
	// a restart) names the guideline it follows and the VERSION of that guideline it implements (the register in code,
	// HospitalClinicalRuleCatalog). The clinical lead approves a version — with the clinic's own values where the rule
	// has them — and every approval is a row kept for good: a rule whose code moves to a new version is "awaiting
	// approval" again until someone approves it. deploy/sql/hospital_h171_clinical_rules.sql.
	// ==========================================================================================
	public class HospitalClinicalRuleApproval
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// The rule's code in the register (kdigo, egfr-dosing, burns, labour, af-stroke, carboplatin, immunotherapy).
		public string Code { get; set; } = "";
		/// The version of the rule this approval covers.
		public string Version { get; set; } = "";
		/// The clinic's values for the rule's parameters, {"key": value}; null when it has none.
		public string? ParamsJson { get; set; }
		public string? Note { get; set; }
		public int ApprovedByEmployeeId { get; set; }
		public DateTime ApprovedAt { get; set; }
	}

	public static class HospitalClinicalRulesModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalClinicalRuleApproval>(e =>
			{
				e.ToTable("HospitalClinicalRuleApprovals");   // no DbSet: it names its table
				e.Property(x => x.Code).HasMaxLength(40);
				e.Property(x => x.Version).HasMaxLength(20);
				e.Property(x => x.ParamsJson).HasMaxLength(1000);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.Code, x.ApprovedAt });
			});
		}
	}
}
