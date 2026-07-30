namespace CrossBuy.Models.Context.Admin
{
	// HR-10 — training records (courses + enrollments). HR record only; no GL impact.
	public class TrainingCourse
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Title { get; set; } = "";
		public string? TitleEn { get; set; }
		public string? Provider { get; set; }
		public string? ProviderEn { get; set; }       // English twin (shown when UI is not Arabic; falls back to Provider)
		public string? Category { get; set; }
		public decimal Cost { get; set; }             // informational (per seat); not posted to GL
		public decimal Hours { get; set; }
		public DateTime? StartDate { get; set; }
		public DateTime? EndDate { get; set; }
		public bool IsActive { get; set; } = true;
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class TrainingEnrollment
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int CourseId { get; set; }
		public int EmployeeID { get; set; }
		public string Status { get; set; } = "Planned";   // Planned | Attended | Completed | Cancelled
		public decimal? Score { get; set; }
		public string? Certificate { get; set; }
		public DateTime? CompletedAt { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
