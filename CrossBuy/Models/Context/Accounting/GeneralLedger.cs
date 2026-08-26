using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Phase-1 General Ledger entities. Schema created manually via SQL.

	/// رأس القيد — Journal entry header.
	/// Status: Draft / Submitted / Posted / Reversed. Posted entries are immutable (correct via reversal).
	public class JournalEntry
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		// NULLABLE BY LIFECYCLE, not by accident. A journal entry is created as a DRAFT with no number and is
		// numbered on POST (JournalEntryService.cs:84 creates it with `EntryNo = null!`, :273 reserves the real
		// number), and a filtered unique index ignores the NULLs so drafts do not collide. The live column
		// agrees: CrossBuyDev has EntryNo as nvarchar(40) NULL with UX_JE_Company_EntryNo.
		//
		// The model previously declared it non-nullable, so model-generated DDL emitted NOT NULL and the draft
		// insert died with "Cannot insert the value NULL into column 'EntryNo'". The runtime and the database
		// were right and the model was wrong; this aligns the model rather than forcing the database to
		// contradict the posting lifecycle. Numbering semantics are untouched.
		public string? EntryNo { get; set; }               // null while draft; e.g. JV-2026-000123 once posted
		public DateTime EntryDate { get; set; }
		public int FiscalPeriodId { get; set; }
		public string JournalType { get; set; } = "Manual"; // Manual/Auto/Recurring/Reversing/Opening/Closing
		public string? SourceType { get; set; }             // Payroll/SalesInvoice/...
		public int? SourceId { get; set; }
		public int CurrencyId { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }
		public string Status { get; set; } = "Draft";
		public int? ReversedByEntryId { get; set; }
		public int? PostedBy { get; set; }
		public DateTime? PostedAt { get; set; }
		public int? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public int? ModifiedBy { get; set; }
		public DateTime? ModifiedAt { get; set; }

		public ICollection<JournalEntryLine> Lines { get; set; } = new List<JournalEntryLine>();
	}

	/// سطر القيد — Journal entry line. A line is either a debit OR a credit (DB CHECK enforces).
	public class JournalEntryLine
	{
		public int ID { get; set; }
		public int JournalEntryId { get; set; }
		public int LineNo { get; set; }
		public int AccountId { get; set; }
		public decimal Debit { get; set; }
		public decimal Credit { get; set; }
		public int? CostCenterId { get; set; }
		public int? ProjectId { get; set; }
		public int? EmployeeId { get; set; }
		public int? CurrencyId { get; set; }
		public decimal? ForeignAmount { get; set; }
		public decimal? ExchangeRate { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }      // optional English line note (shown when UI is not Arabic)

		[ForeignKey(nameof(JournalEntryId))]
		public JournalEntry? JournalEntry { get; set; }
	}

	/// تسلسل ترقيم المستندات — per company, per key, (optionally) per fiscal year.
	public class NumberSequence
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string SequenceKey { get; set; } = "";       // JV / SV / PV / RC / PY ...
		public int? FiscalYearId { get; set; }
		public string? Prefix { get; set; }
		public int NextNumber { get; set; } = 1;
		public byte PadLength { get; set; } = 6;
	}
}
