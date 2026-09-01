using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Phase-0 foundation entities for the accounting module.
	// Plain POCOs (scalar only) — the tree is assembled in the service layer (like Hierarchicals),
	// so EF infers no cross-table relationships. Schema is created manually via SQL (migrations are broken).

	/// نوع الحساب وطبيعته (مدين/دائن) — Account type & normal balance.
	public class AccountType
	{
		public int ID { get; set; }
		public string Code { get; set; } = "";           // ASSET/LIAB/EQUITY/REV/EXP
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string NormalBalance { get; set; } = "D";  // 'D' or 'C'
		public string StatementType { get; set; } = "";   // BalanceSheet / IncomeStatement
	}

	/// شجرة الحسابات (Chart of Accounts) — self-referencing via ParentId.
	public class Account
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int AccountTypeId { get; set; }
		public int? ParentId { get; set; }
		public bool IsPostable { get; set; }              // leaf accounts only accept entries
		public bool IsActive { get; set; } = true;
		public int? CurrencyId { get; set; }
		public bool RequireCostCenter { get; set; }
		public bool RequireProject { get; set; }
		public string? CashFlowCategory { get; set; }     // Operating/Investing/Financing
		public int? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		public int? ModifiedBy { get; set; }
		public DateTime? ModifiedAt { get; set; }
	}

	/// العملة — Currency.
	public class Currency
	{
		public int ID { get; set; }
		public string Code { get; set; } = "";           // ISO e.g. EGP/USD
		public string? Symbol { get; set; }
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public byte DecimalPlaces { get; set; } = 2;
	}

	/// سعر الصرف مقابل العملة الوظيفية — Exchange rate vs functional currency.
	public class ExchangeRate
	{
		public int ID { get; set; }
		public int CurrencyId { get; set; }
		public DateTime RateDate { get; set; }
		public decimal Rate { get; set; }
		public string RateType { get; set; } = "Standard";
	}

	/// السنة المالية — Fiscal year.
	public class FiscalYear
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public DateTime StartDate { get; set; }
		public DateTime EndDate { get; set; }
		public string Status { get; set; } = "Open";      // Open/Closed
	}

	/// الفترة المالية — Fiscal period (12 monthly + 1 adjustment).
	public class FiscalPeriod
	{
		public int ID { get; set; }
		public int FiscalYearId { get; set; }
		public byte PeriodNo { get; set; }                // 1..13
		public DateTime StartDate { get; set; }
		public DateTime EndDate { get; set; }
		public string Status { get; set; } = AccountingPeriodStatuses.Open;

		// ---- close / reopen evidence ----
		//
		// A period's state is a FINANCIAL CONTROL, so who moved it and when is part of the record rather
		// than a trail beside it. Reopen additionally carries a reason: closing is routine and explains
		// itself, but re-admitting posting to a closed period never does.
		public int? ClosedBy { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ReopenedBy { get; set; }
		public DateTime? ReopenedAt { get; set; }
		public string? ReopenReason { get; set; }
	}

	// The period lifecycle.
	//
	// SoftClosed is the EXISTING name for the middle state - it is already in the model comment, already
	// accepted by FiscalPeriodService.SetStatusAsync, and already written to rows. The brief calls that
	// state "Closing"; introducing a second spelling beside a live one would give the system two
	// vocabularies for one thing, so the existing name is kept and given meaning instead.
	public static class AccountingPeriodStatuses
	{
		public const string Open = "Open";              // ordinary posting permitted
		public const string SoftClosed = "SoftClosed";  // "Closing": ordinary posting refused, still reversible without a reopen
		public const string Closed = "Closed";          // posting refused; only a recorded reopen re-admits it

		public static readonly IReadOnlyList<string> All = new[] { Open, SoftClosed, Closed };

		// The single fact every posting path depends on. Expressed once, here, so a caller cannot
		// accidentally test for one closed state and miss the other.
		public static bool BlocksPosting(string? status) =>
			status == SoftClosed || status == Closed;

		public static bool IsKnown(string? status) => status != null && All.Contains(status, StringComparer.Ordinal);
	}

	// Every close, soft-close and reopen, kept as rows rather than as the latest values on the period.
	// A period that was closed, reopened, corrected and closed again has a story, and the control is only
	// worth having if that story survives.
	public class AccountingPeriodAudit
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int FiscalPeriodId { get; set; }
		public string FromStatus { get; set; } = "";
		public string ToStatus { get; set; } = "";
		public int ActorEmployeeId { get; set; }
		public DateTime OccurredAt { get; set; }
		public string? Reason { get; set; }
	}
}
