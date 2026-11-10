using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H79 — THE PROBLEM LIST AND THE NURSING CARE PLAN (phase 2 of the 2026-11-09 gap research; JCI COP / AOP and
	// every large HIS keep both).
	//   * A PROBLEM belongs to the patient, not to a visit: diabetes, hypertension, a drug allergy's consequence, a post-op
	//     wound. It is chronic or acute, active until someone resolves it, and may carry its ICD-10 code. It follows the
	//     patient from clinic to ward and back.
	//   * A CARE PLAN item belongs to a stay: a nursing need (falls, pressure injury, pain…), its goal, the interventions
	//     and when to review it; closed as met or discontinued with the outcome. A high Morse or an at-risk Braden with no
	//     open plan for that need is suggested by the system — the nurse still writes the plan.
	// CrossBuy/deploy/sql/hospital_h79_care_plans.sql builds the tables.
	// ==========================================================================================
	public static class HospitalProblemKind
	{
		public const string Chronic = "Chronic";
		public const string Acute = "Acute";
		public static readonly IReadOnlyList<string> All = new[] { Chronic, Acute };
	}

	public static class HospitalProblemStatus
	{
		public const string Active = "Active";
		public const string Resolved = "Resolved";
		public static readonly IReadOnlyList<string> All = new[] { Active, Resolved };
	}

	public static class HospitalCareNeed
	{
		public const string Falls = "Falls";
		public const string PressureInjury = "PressureInjury";
		public const string Pain = "Pain";
		public const string Infection = "Infection";
		public const string Nutrition = "Nutrition";
		public const string Mobility = "Mobility";
		public const string Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Falls, PressureInjury, Pain, Infection, Nutrition, Mobility, Other };
	}

	public static class HospitalCarePlanStatus
	{
		public const string Active = "Active";
		public const string Met = "Met";
		public const string Discontinued = "Discontinued";
		public static readonly IReadOnlyList<string> All = new[] { Active, Met, Discontinued };
	}

	public class HospitalProblem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		/// ICD-10, when chosen from the diagnosis list.
		public string? Code { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string Kind { get; set; } = HospitalProblemKind.Chronic;
		public string Status { get; set; } = HospitalProblemStatus.Active;
		public DateTime? OnsetDate { get; set; }
		public string? Note { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? ResolvedAt { get; set; }
		public int? ResolvedByEmployeeId { get; set; }
	}

	public class HospitalCarePlanItem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Need { get; set; } = HospitalCareNeed.Other;
		/// The need in the nurse's words (required for Other, optional detail for the rest).
		public string? NeedText { get; set; }
		public string Goal { get; set; } = "";
		public string Interventions { get; set; } = "";
		/// Ward-local date the plan is reviewed by.
		public DateTime ReviewBy { get; set; }
		public string Status { get; set; } = HospitalCarePlanStatus.Active;
		public string? Outcome { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
	}

	public static class HospitalCarePlanModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalProblem>(e =>
			{
				e.ToTable("HospitalProblems");   // no DbSet: each names its table
				e.Property(x => x.Code).HasMaxLength(10);
				e.Property(x => x.Name).HasMaxLength(200);
				e.Property(x => x.NameEn).HasMaxLength(200);
				e.Property(x => x.Kind).HasMaxLength(8);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.OnsetDate).HasColumnType("date");
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalCarePlanItem>(e =>
			{
				e.ToTable("HospitalCarePlanItems");
				e.Property(x => x.Need).HasMaxLength(16);
				e.Property(x => x.NeedText).HasMaxLength(200);
				e.Property(x => x.Goal).HasMaxLength(500);
				e.Property(x => x.Interventions).HasMaxLength(2000);
				e.Property(x => x.ReviewBy).HasColumnType("date");
				e.Property(x => x.Status).HasMaxLength(14);
				e.Property(x => x.Outcome).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
			});
		}
	}
}
