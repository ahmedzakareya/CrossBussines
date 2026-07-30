namespace CrossBuy.ViewModel
{
	// E-commerce storefront: catalog page model. Data now comes from the real inventory Items + ItemCategories
	// (display only — no stock/GL/transaction). These are lightweight view DTOs so the view stays decoupled.
	public class StoreCategoryVM
	{
		public int Id { get; set; }
		public string Token { get; set; } = "";   // encrypted Id used in public URLs (raw Id never rendered)
		public string Name { get; set; } = "";
		public string? Icon { get; set; }
		public int ItemsCount { get; set; }
	}

	public class StoreProductVM
	{
		public int Id { get; set; }
		public string Token { get; set; } = "";   // encrypted Id used in public URLs (raw Id never rendered)
		public string Name { get; set; } = "";
		public string? Category { get; set; }
		public decimal Price { get; set; }
		public decimal? OldPrice { get; set; }
		public string? DefaultImage { get; set; }
		public string? HoverImage { get; set; }
		public string? Badge { get; set; }
		public decimal Rating { get; set; }
		public string? Vendor { get; set; }
	}

	public class StoreCatalogVM
	{
		public List<StoreCategoryVM> Categories { get; set; } = new();
		public List<StoreProductVM> Products { get; set; } = new();
	}

	// Single product-detail page (route /Store/Product/{id}). DISPLAY ONLY — read from the real Item (STORE-*) master row.
	// No cart/stock/GL/transaction; the theme's decorative fields (badge/rating/vendor/old price) come straight from the Item.
	public class StoreProductDetailVM
	{
		public int Id { get; set; }
		public string Name { get; set; } = "";
		public string Sku { get; set; } = "";        // = ItemCode
		public string? Category { get; set; }
		public decimal Price { get; set; }
		public decimal? OldPrice { get; set; }
		public int SavePct { get; set; }             // computed discount % (0 if no old price)
		public string? DefaultImage { get; set; }
		public string? HoverImage { get; set; }
		public string? Badge { get; set; }
		public decimal Rating { get; set; }          // 0..5
		public string? Vendor { get; set; }
		public List<string> Images { get; set; } = new();   // gallery: primary + extra ItemImages (fallback to default/hover)
		public List<StoreProductVM> Related { get; set; } = new();
		public List<StoreCategoryVM> Categories { get; set; } = new();   // sidebar widget
	}

	// Category products-listing page (route /Store/Category/{id}) — theme design shop-grid-right.html. DISPLAY ONLY.
	// Shows the storefront Items (STORE-*) that belong to one featured ItemCategory; each product links to its detail page.
	public class StoreCategoryPageVM
	{
		public int Id { get; set; }
		public string Token { get; set; } = "";   // encrypted Id used in public URLs (raw Id never rendered)
		public string Name { get; set; } = "";
		public string? Icon { get; set; }
		public List<StoreProductVM> Products { get; set; } = new();
		public List<StoreCategoryVM> Categories { get; set; } = new();   // sidebar widget (all featured categories)
	}
}
