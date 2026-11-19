namespace CrossBuy.Models.Context.Accounting
{
	/// A customer's cheque behind a receipt voucher (deploy/sql/cheques_under_collection.sql). The receipt debited
	/// 1106 «شيكات تحت التحصيل»; the cheque is then Cleared to a bank account, or Bounced (the receipt reversed).
	public class ReceiptCheque
	{
		public const string InHand = "InHand", Cleared = "Cleared", Bounced = "Bounced";

		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ReceiptID { get; set; }
		public string ChequeNo { get; set; } = "";
		public DateTime DueDate { get; set; }
		public string? Bank { get; set; }
		public string Status { get; set; } = InHand;
		public DateTime? ClearedAt { get; set; }
		public int? ClearedToAccountID { get; set; }
		public int? ClearingEntryID { get; set; }
		public DateTime? BouncedAt { get; set; }
		public string? BounceReason { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	}
}
