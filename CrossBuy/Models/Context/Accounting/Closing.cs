namespace CrossBuy.Models.Context.Accounting
{
	// Phase-9 Year-end closing record (one per closed fiscal year).
	public class YearEndClosing
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int FiscalYearId { get; set; }
		public DateTime CloseDate { get; set; }
		public decimal TotalRevenue { get; set; }
		public decimal TotalExpense { get; set; }
		public decimal NetResult { get; set; }   // Revenue - Expense (>0 profit)
		public int RetainedEarningsAccountId { get; set; }
		public int? JournalEntryId { get; set; }
		public string Status { get; set; } = "Closed";   // Closed / Reopened
		public DateTime? CreatedAt { get; set; }
	}
}
