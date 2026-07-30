namespace CrossBuy.Models.Context.Accounting
{
	// Progressive income-tax bracket (annual taxable income). Ordered by Ordinal.
	// ToAmount null = open-ended top bracket. Rate is a percentage (e.g. 20 = 20%).
	public class PayrollTaxBracket
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int Ordinal { get; set; }
		public decimal FromAmount { get; set; }
		public decimal? ToAmount { get; set; }
		public decimal Rate { get; set; }
		public DateTime? CreatedAt { get; set; }
	}
}
