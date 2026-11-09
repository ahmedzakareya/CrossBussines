namespace CrossBuy.Models.Context.Admin
{
	/// HR4 (2026-11-08) — a loan or salary advance to an employee, repaid by payroll deduction (hr4_employee_loans.sql).
	/// Outstanding = Principal − the deductions actually posted; it is Settled when that reaches zero.
	public class EmployeeLoan
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeID { get; set; }
		public string Kind { get; set; } = "Loan";          // Loan | Advance
		public decimal Principal { get; set; }
		public decimal Installment { get; set; }
		public int FirstYear { get; set; }
		public int FirstMonth { get; set; }
		public string Status { get; set; } = "Active";      // Active | Settled | Cancelled
		public string? Reason { get; set; }
		public DateTime DisbursedOn { get; set; }
		public int? PaidFromAccountId { get; set; }
		public int? DisbursementEntryId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public int? SourceRequestId { get; set; }   // HR9: the approved self-service advance request it was paid out against
	}

	/// One loan's deduction in one payroll month, written when that payroll is POSTED.
	public class EmployeeLoanDeduction
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int LoanID { get; set; }
		public int Year { get; set; }
		public int Month { get; set; }
		public decimal Amount { get; set; }
		public int? JournalEntryId { get; set; }
		public DateTime CreatedAt { get; set; }
	}
}
