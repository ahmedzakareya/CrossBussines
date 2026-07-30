using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Admin
{
	// HR-9 — Performance appraisal (template-based, weighted scoring). No GL impact.
	// Flow: HR/manager creates an appraisal for an employee in a cycle using a template →
	// scores each criterion → submits → the employee acknowledges. Weighted score = Σ(score/max × weight) normalized to 100.

	public class AppraisalCycle
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int Year { get; set; }
		public DateTime? StartDate { get; set; }
		public DateTime? EndDate { get; set; }
		public string Status { get; set; } = "Open";   // Open | Closed
		public DateTime? CreatedAt { get; set; }
	}

	public class AppraisalTemplate
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
		[NotMapped] public List<AppraisalCriterion> Criteria { get; set; } = new();
	}

	public class AppraisalCriterion
	{
		public int ID { get; set; }
		public int TemplateId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public decimal Weight { get; set; } = 0;        // relative weight (normalized across the template)
		public decimal MaxScore { get; set; } = 5;      // rating scale ceiling
		public int SortOrder { get; set; }
	}

	public class Appraisal
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int CycleId { get; set; }
		public int TemplateId { get; set; }
		public int EmployeeID { get; set; }
		public int ManagerEmployeeID { get; set; }
		public int Status { get; set; } = 0;            // 0 draft / 1 submitted / 2 acknowledged
		public decimal TotalScore { get; set; }         // weighted 0..100
		public string? ManagerComment { get; set; }
		public string? EmployeeComment { get; set; }
		public DateTime? SubmittedAt { get; set; }
		public DateTime? AcknowledgedAt { get; set; }
		public DateTime? CreatedAt { get; set; }
		public int? CreatedBy { get; set; }
		[NotMapped] public List<AppraisalLine> Lines { get; set; } = new();
	}

	public class AppraisalLine
	{
		public int ID { get; set; }
		public int AppraisalId { get; set; }
		public int CriterionId { get; set; }
		public decimal Score { get; set; }
		public string? Note { get; set; }
	}
}
