using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Accounting
{
	// Phase-3 AR/AP entities.

	public class Customer
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? TaxRegNo { get; set; }
		public string? Address { get; set; }
		public int ControlAccountId { get; set; }
		public int? CurrencyId { get; set; }
		public int? PaymentTermsDays { get; set; }
		public decimal? CreditLimit { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
		// P3-1 enrichment
		public string? Phone { get; set; }
		public string? Email { get; set; }
		public string? ContactPerson { get; set; }
		public string? Segment { get; set; }            // classification (e.g. VIP / Wholesale / Retail)
		public string? ShippingAddress { get; set; }
	}

	public class Vendor
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? TaxRegNo { get; set; }
		public string? Address { get; set; }
		public int ControlAccountId { get; set; }
		public int? CurrencyId { get; set; }
		public int? PaymentTermsDays { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
		// P3-1 enrichment
		public string? Phone { get; set; }
		public string? Email { get; set; }
		public string? ContactPerson { get; set; }
		public string? Segment { get; set; }
	}

	public class SalesInvoice
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? InvoiceNo { get; set; }
		public DateTime InvoiceDate { get; set; }
		public int CustomerId { get; set; }
		public int? CurrencyId { get; set; }
		public decimal SubTotal { get; set; }
		public decimal TaxTotal { get; set; }
		public decimal GrandTotal { get; set; }
		// Multi-Currency (1-1): rate at document date + totals translated to branch functional currency
		public decimal? ExchangeRate { get; set; }
		public decimal? SubTotalBase { get; set; }
		public decimal? TaxTotalBase { get; set; }
		public decimal? GrandTotalBase { get; set; }
		public string Status { get; set; } = "Draft";   // Draft/Posted/Cancelled
		public int? ProjectId { get; set; }              // analytic dimension (flows to JE lines incl. COGS)
		public int? JournalEntryId { get; set; }
		public string? EtaUuid { get; set; }
		public string? EtaStatus { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
		// HM-8: DISPLAY-ONLY beneficiary for the official invoice of a walk-in sale (set-once, audited). The posting is
		// UNTOUCHED — CustomerId/control account/JE/amounts unchanged. Allowed ONLY when TaxTotal == 0 (a taxed invoice's
		// beneficiary must equal the ledger account holder). NO financial meaning; the document shows it, the ledger does not.
		public string? CustomerNameOverride { get; set; }
		public string? CustomerTaxNoOverride { get; set; }
		public string? CustomerOverrideBy { get; set; }
		public DateTime? CustomerOverrideAt { get; set; }
		public ICollection<SalesInvoiceLine> Lines { get; set; } = new List<SalesInvoiceLine>();
	}

	public class SalesInvoiceLine
	{
		public int ID { get; set; }
		public int SalesInvoiceId { get; set; }
		public int LineNo { get; set; }
		public string ItemDescription { get; set; } = "";
		public string? ItemDescriptionEn { get; set; }   // English line description (shown when UI is not Arabic)
		public string? ItemCode { get; set; }
		public decimal Qty { get; set; } = 1;
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
		public int RevenueAccountId { get; set; }
		public decimal LineTotal { get; set; }
		public int? ItemId { get; set; }          // inventory link → stock-out + COGS on post
		public int? WarehouseId { get; set; }
		public int? UoMId { get; set; }           // HM-2: sold unit (null = base) — used to convert qty to base at stock-out
		[ForeignKey(nameof(SalesInvoiceId))] public SalesInvoice? Invoice { get; set; }
	}

	// P3-3: Sales return / credit note (reverses AR + revenue + VAT, returns stock at cost).
	public class SalesReturn
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? ReturnNo { get; set; }
		public DateTime ReturnDate { get; set; }
		public int CustomerId { get; set; }
		public int? OriginalInvoiceId { get; set; }
		public int? WarehouseId { get; set; }
		public int? CurrencyId { get; set; }
		public decimal SubTotal { get; set; }
		public decimal TaxTotal { get; set; }
		public decimal GrandTotal { get; set; }
		// Multi-Currency (1-1)
		public decimal? ExchangeRate { get; set; }
		public decimal? SubTotalBase { get; set; }
		public decimal? TaxTotalBase { get; set; }
		public decimal? GrandTotalBase { get; set; }
		public string Status { get; set; } = "Posted";
		public int? JournalEntryId { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<SalesReturnLine> Lines { get; set; } = new List<SalesReturnLine>();
	}

	public class SalesReturnLine
	{
		public int ID { get; set; }
		public int SalesReturnId { get; set; }
		public int LineNo { get; set; }
		public string ItemDescription { get; set; } = "";
		public decimal Qty { get; set; } = 1;
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
		public int RevenueAccountId { get; set; }
		public decimal LineTotal { get; set; }
		public int? ItemId { get; set; }
		public int? WarehouseId { get; set; }
		[ForeignKey(nameof(SalesReturnId))] public SalesReturn? Return { get; set; }
	}

	public class PurchaseInvoice
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? InvoiceNo { get; set; }
		public DateTime InvoiceDate { get; set; }
		public int VendorId { get; set; }
		public int? CurrencyId { get; set; }
		public decimal SubTotal { get; set; }
		public decimal TaxTotal { get; set; }
		public decimal GrandTotal { get; set; }
		// Multi-Currency (1-1)
		public decimal? ExchangeRate { get; set; }
		public decimal? SubTotalBase { get; set; }
		public decimal? TaxTotalBase { get; set; }
		public decimal? GrandTotalBase { get; set; }
		public string Status { get; set; } = "Draft";
		public int? ProjectId { get; set; }              // analytic dimension (flows to JE lines)
		public int? JournalEntryId { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<PurchaseInvoiceLine> Lines { get; set; } = new List<PurchaseInvoiceLine>();
	}

	public class PurchaseInvoiceLine
	{
		public int ID { get; set; }
		public int PurchaseInvoiceId { get; set; }
		public int LineNo { get; set; }
		public string ItemDescription { get; set; } = "";
		public string? ItemDescriptionEn { get; set; }   // English line description (shown when UI is not Arabic)
		public decimal Qty { get; set; } = 1;
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
		public int ExpenseAccountId { get; set; }
		public int? CostCenterId { get; set; }
		public decimal LineTotal { get; set; }
		public int? ItemId { get; set; }          // inventory link → stock-in (cost tracking) on post
		public int? WarehouseId { get; set; }
		[ForeignKey(nameof(PurchaseInvoiceId))] public PurchaseInvoice? Invoice { get; set; }
	}

	// P3-3b: Purchase return / debit note (valued at item AVG cost to preserve the 1103=stock invariant).
	public class PurchaseReturn
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? ReturnNo { get; set; }
		public DateTime ReturnDate { get; set; }
		public int VendorId { get; set; }
		public int? OriginalInvoiceId { get; set; }
		public int? WarehouseId { get; set; }
		public int? CurrencyId { get; set; }
		public decimal SubTotal { get; set; }
		public decimal TaxTotal { get; set; }
		public decimal GrandTotal { get; set; }
		// Multi-Currency (1-1)
		public decimal? ExchangeRate { get; set; }
		public decimal? SubTotalBase { get; set; }
		public decimal? TaxTotalBase { get; set; }
		public decimal? GrandTotalBase { get; set; }
		public string Status { get; set; } = "Posted";
		public int? JournalEntryId { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
		public ICollection<PurchaseReturnLine> Lines { get; set; } = new List<PurchaseReturnLine>();
	}

	public class PurchaseReturnLine
	{
		public int ID { get; set; }
		public int PurchaseReturnId { get; set; }
		public int LineNo { get; set; }
		public int ItemId { get; set; }
		public string ItemDescription { get; set; } = "";
		public decimal Qty { get; set; } = 1;
		public int WarehouseId { get; set; }
		public decimal TaxRate { get; set; }
		public decimal UnitCost { get; set; }     // avg cost at return time
		public decimal LineTotal { get; set; }    // = Qty × UnitCost
		[ForeignKey(nameof(PurchaseReturnId))] public PurchaseReturn? Return { get; set; }
	}

	public class Receipt   // سند قبض من عميل
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? ReceiptNo { get; set; }
		public DateTime ReceiptDate { get; set; }
		public int? CustomerId { get; set; }
		public decimal Amount { get; set; }
		public int? CurrencyId { get; set; }
		// Multi-Currency (1-1): rate at receipt date + amount in branch functional currency
		public decimal? ExchangeRate { get; set; }
		public decimal? AmountBase { get; set; }
		public string Method { get; set; } = "Cash";
		public int CashAccountId { get; set; }
		public string Status { get; set; } = "Posted";
		public int? JournalEntryId { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class Payment   // سند دفع لمورد
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string? PaymentNo { get; set; }
		public DateTime PaymentDate { get; set; }
		public int? VendorId { get; set; }
		public decimal Amount { get; set; }
		public int? CurrencyId { get; set; }
		// Multi-Currency (1-1): rate at payment date + amount in branch functional currency
		public decimal? ExchangeRate { get; set; }
		public decimal? AmountBase { get; set; }
		public string Method { get; set; } = "Cash";
		public int CashAccountId { get; set; }
		public string Status { get; set; } = "Posted";
		public int? JournalEntryId { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
