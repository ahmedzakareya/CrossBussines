namespace CrossBuy.Models.Context.Accounting
{
	// Phase-4 banking entities.

	public class BankAccount
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string BankName { get; set; } = "";
		public string? BankNameEn { get; set; }
		public string? AccountNumber { get; set; }
		public string? IBAN { get; set; }
		public int? CurrencyId { get; set; }
		public int GlAccountId { get; set; }          // linked GL cash/bank account
		public decimal OpeningBalance { get; set; }
		// Multi-Currency: period-end balance in the account's OWN foreign currency (from the bank statement),
		// used by FX revaluation to restate the GL carrying value to the closing rate. Null = not revalued.
		public decimal? ForeignBalance { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	public class CashBox
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int? CustodianEmployeeId { get; set; }
		public int GlAccountId { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	/// تسوية بنكية — Bank reconciliation header.
	public class BankReconciliation
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int BankAccountId { get; set; }
		public DateTime StatementDate { get; set; }
		public decimal StatementBalance { get; set; }
		public decimal BookBalance { get; set; }
		public decimal ClearedBalance { get; set; }
		public decimal Difference { get; set; }
		public string Status { get; set; } = "Pending";   // Pending / Reconciled
		public DateTime? CreatedAt { get; set; }
	}

	public class BankReconciliationLine
	{
		public int ID { get; set; }
		public int BankReconciliationId { get; set; }
		public int JournalEntryLineId { get; set; }
		public bool Cleared { get; set; } = true;
	}
}
