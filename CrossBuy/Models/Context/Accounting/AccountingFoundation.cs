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
		public string Status { get; set; } = "Open";      // Open/SoftClosed/Closed
	}
}
