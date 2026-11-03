using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL BILLING — the service catalogue, billing settings, bills, consultation orders,
	// payments and cashier shifts (unit H6).
	//
	// A BILL belongs to one outpatient visit (EncounterId) or one inpatient stay (AdmissionId).
	// Its lines are snapshots — code, names, price, tax — so a later price change never rewrites a
	// bill. Closing a bill posts it to the general ledger through the accounting module's journal
	// engine; every payment posts too, and is taken on the cashier's open SHIFT, which is counted
	// and closed at the end of the day.
	// deploy/sql/hospital_h6_billing.sql builds the tables.
	// ==========================================================================================

	public static class HospitalServiceCategories
	{
		public const string Consultation = "Consultation";
		public const string Lab = "Lab";
		public const string Radiology = "Radiology";
		public const string Procedure = "Procedure";
		public const string Accommodation = "Accommodation";
		public const string Pharmacy = "Pharmacy";
		public const string Other = "Other";
		public static readonly IReadOnlyList<string> All = new[] { Consultation, Lab, Radiology, Procedure, Accommodation, Pharmacy, Other };
		/// What a doctor can order from the consultation screen.
		public static readonly IReadOnlyList<string> Orderable = new[] { Lab, Radiology, Procedure };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class HospitalBillStatus
	{
		public const string Open = "Open";
		public const string Closed = "Closed";
	}

	public static class HospitalBillLineSources
	{
		public const string Consultation = "consultation";
		public const string Order = "order";
		public const string Accommodation = "accommodation";
		public const string Manual = "manual";
		public const string Pharmacy = "pharmacy";
	}

	public static class HospitalPaymentMethods
	{
		public const string Cash = "Cash";
		public const string Card = "Card";
		public const string Transfer = "Transfer";
		public const string Wallet = "Wallet";
		public static readonly IReadOnlyList<string> All = new[] { Cash, Card, Transfer, Wallet };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public static class HospitalShiftStatus
	{
		public const string Open = "Open";
		public const string Closed = "Closed";
	}

	public class HospitalBillingSettings
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public decimal VatRatePercent { get; set; }
		public int? CashAccountId { get; set; }
		public int? BankAccountId { get; set; }
		public int? ReceivableAccountId { get; set; }
		public int? RevenueAccountId { get; set; }
		public int? VatAccountId { get; set; }
		public int? CashOverShortAccountId { get; set; }
		/// H7: gains and losses when a foreign-currency bill is paid at a rate other than its closing rate.
		public int? FxDifferenceAccountId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalService
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Category { get; set; } = HospitalServiceCategories.Other;
		public int? DepartmentId { get; set; }
		public decimal Price { get; set; }
		public bool Taxable { get; set; }
		public int? RevenueAccountId { get; set; }
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}

	public class HospitalEncounterOrder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int EncounterId { get; set; }
		public int ServiceId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Category { get; set; } = "";
		public decimal Price { get; set; }
	}

	public class HospitalBill
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string BillNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? EncounterId { get; set; }
		public int? AdmissionId { get; set; }
		public string Status { get; set; } = HospitalBillStatus.Open;
		public DateTime OpenedAt { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
		public int? JournalEntryId { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		/// H7: the list that priced it (null = the main list) and its currency (null = the facility's).
		public int? PriceListId { get; set; }
		public int? CurrencyId { get; set; }
		/// Bill currency → facility currency, fixed when a foreign-currency bill closes.
		public decimal? ExchangeRate { get; set; }
	}

	public class HospitalBillLine
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int BillId { get; set; }
		public int? ServiceId { get; set; }
		public string Code { get; set; } = "";
		public string Description { get; set; } = "";
		public string DescriptionEn { get; set; } = "";
		public string Category { get; set; } = HospitalServiceCategories.Other;
		public int? DepartmentId { get; set; }
		public decimal Quantity { get; set; } = 1;
		public decimal UnitPrice { get; set; }
		public bool Taxable { get; set; }
		public decimal VatRatePercent { get; set; }
		public int? RevenueAccountId { get; set; }
		public string Source { get; set; } = HospitalBillLineSources.Manual;
		/// H7: the list does not cover it, so it was priced from the main list.
		public bool OutsideList { get; set; }
		public DateTime AddedAt { get; set; }
		public int? AddedByEmployeeId { get; set; }
	}

	public class HospitalCashierShift
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Desk { get; set; } = "";
		public int CashierEmployeeId { get; set; }
		public string Status { get; set; } = HospitalShiftStatus.Open;
		public DateTime OpenedAt { get; set; }
		public decimal OpeningFloat { get; set; }
		public DateTime? ClosedAt { get; set; }
		public decimal? ExpectedCash { get; set; }
		public decimal? CountedCash { get; set; }
		public decimal? Variance { get; set; }
		public string? CountJson { get; set; }
		public string? HandoverTo { get; set; }
		public int? JournalEntryId { get; set; }
	}

	public class HospitalPayment
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int BillId { get; set; }
		public int ShiftId { get; set; }
		public string ReceiptNo { get; set; } = "";
		public decimal Amount { get; set; }
		public string Method { get; set; } = HospitalPaymentMethods.Cash;
		public DateTime ReceivedAt { get; set; }
		public int CashierEmployeeId { get; set; }
		public int? JournalEntryId { get; set; }
		/// H7: what was taken in the facility's currency (null = Amount: the bill is in the facility's currency).
		public decimal? BaseAmount { get; set; }
	}

	public static class HospitalBillingModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalBillingSettings>(e => { e.Property(x => x.VatRatePercent).HasPrecision(5, 2); e.HasIndex(x => x.CompanyId).IsUnique(); });
			b.Entity<HospitalService>(e => { e.Property(x => x.Price).HasPrecision(18, 4); e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique(); });
			b.Entity<HospitalEncounterOrder>(e =>
			{
				e.Property(x => x.Price).HasPrecision(18, 4);
				e.HasOne<HospitalEncounter>().WithMany().HasForeignKey(x => x.EncounterId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.EncounterId });
			});
			b.Entity<HospitalBill>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.BillNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.EncounterId }).IsUnique().HasFilter("[EncounterId] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId }).IsUnique().HasFilter("[AdmissionId] IS NOT NULL");
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
				e.Property(x => x.ExchangeRate).HasPrecision(18, 8);
			});
			b.Entity<HospitalBillLine>(e =>
			{
				e.Property(x => x.Quantity).HasPrecision(9, 2);
				e.Property(x => x.UnitPrice).HasPrecision(18, 4);
				e.Property(x => x.VatRatePercent).HasPrecision(5, 2);
				e.HasOne<HospitalBill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.BillId });
			});
			b.Entity<HospitalCashierShift>(e =>
			{
				e.Property(x => x.OpeningFloat).HasPrecision(18, 4);
				e.Property(x => x.ExpectedCash).HasPrecision(18, 4);
				e.Property(x => x.CountedCash).HasPrecision(18, 4);
				e.Property(x => x.Variance).HasPrecision(18, 4);
				e.HasIndex(x => new { x.CompanyId, x.CashierEmployeeId }).IsUnique().HasFilter("[Status] = 'Open'");
				e.HasIndex(x => new { x.CompanyId, x.OpenedAt });
			});
			b.Entity<HospitalPayment>(e =>
			{
				e.Property(x => x.Amount).HasPrecision(18, 4);
				e.Property(x => x.BaseAmount).HasPrecision(18, 4);
				e.HasOne<HospitalBill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.NoAction);
				e.HasOne<HospitalCashierShift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.ReceiptNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.BillId });
				e.HasIndex(x => new { x.CompanyId, x.ShiftId });
			});
		}
	}
}
