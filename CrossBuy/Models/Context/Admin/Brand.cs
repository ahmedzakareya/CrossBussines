namespace CrossBuy.Models.Context.Admin
{
	// Multi-activity platform FOUNDATION — Brand = a trade name under a single Company (NOT a legal entity).
	// One ledger + analytic dimension only. BrandId is nullable everywhere; it never changes who writes GL/stock.
	// Identity fields inherit from the Company when left null (see BrandService.ResolveIdentityAsync).
	public class Brand
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }

		// ---- visual identity ----
		public string? LogoPath { get; set; }          // /uploads/brands/xxx
		public string? ColorPrimary { get; set; }       // hex
		public string? ColorSecondary { get; set; }     // hex
		public string? ColorAccent { get; set; }        // hex

		// ---- document identity (shown on receipts / storefront) ----
		public string? TradeName { get; set; }          // commercial display name (NOT a separate tax entity)
		public string? Address { get; set; }
		public string? Phone { get; set; }
		public string? Email { get; set; }
		public string? Website { get; set; }
		public string? ReceiptFooterAr { get; set; }
		public string? ReceiptFooterEn { get; set; }
	}
}
