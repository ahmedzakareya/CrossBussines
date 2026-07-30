namespace CrossBuy.Models.Context.Accounting
{
	// Phase-7 Taxes & ETA entities.

	public class TaxCode
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string Kind { get; set; } = "VAT";   // VAT / WHT
		public decimal Rate { get; set; }            // percent, e.g. 14
		public bool IsActive { get; set; } = true;
		public bool IsDefault { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class VatReturn
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public DateTime PeriodStart { get; set; }
		public DateTime PeriodEnd { get; set; }
		public decimal OutputVat { get; set; }   // ضريبة المخرجات (مبيعات)
		public decimal InputVat { get; set; }    // ضريبة المدخلات (مشتريات)
		public decimal NetDue { get; set; }      // Output - Input (>0 يُسدَّد للمصلحة)
		public string Status { get; set; } = "Filed";   // Filed / Settled
		public int? JournalEntryId { get; set; }
		public DateTime? FiledAt { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class EtaSettings
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public bool Enabled { get; set; }                 // deferred → false
		public string Environment { get; set; } = "Preprod";  // Preprod / Production
		public string? ClientId { get; set; }
		public string? TaxpayerRin { get; set; }          // الرقم الضريبي للممول
		public string? ActivityCode { get; set; }
		public DateTime? UpdatedAt { get; set; }
	}
}
