namespace CrossBuy.Models.Context.Admin
{
	// HR1 — employee record 2.0 (deploy/sql/hr1_employee_record.sql, 2026-11-08).

	/// A dependant of an employee (spouse, child, parent), company-scoped.
	public class EmployeeDependant
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public string FullName { get; set; } = "";
		public string? FullNameEn { get; set; }
		public string Relation { get; set; } = "Other";     // Spouse | Son | Daughter | Father | Mother | Other
		public DateTime? DateOfBirth { get; set; }
		public string? CivilIdNumber { get; set; }
		public bool IsSponsored { get; set; }
		public DateTime CreatedAt { get; set; }
	}

	/// One change of a tracked field, with the date it takes effect. Written on save; never updated or deleted, so the
	/// past is never overwritten.
	public class EmployeeHistory
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public Employee? Employee { get; set; }
		public string Field { get; set; } = "";             // Hire | JobTitle | Branch | Department | EmploymentType
		public string? OldValue { get; set; }
		public string? NewValue { get; set; }
		public DateTime EffectiveFrom { get; set; }
		public int? ChangedByEmployeeId { get; set; }
		public DateTime ChangedAt { get; set; }
	}
}
