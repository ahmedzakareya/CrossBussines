using CrossBuy.Models.Context;
using CrossBuy.ViewModel;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// E-commerce storefront (DISPLAY ONLY): reads the catalog from the real inventory Items + ItemCategories for the public
	// page. Read-only — no stock, no GL, no transaction. Storefront products = Items whose code starts with "STORE-";
	// featured categories = ItemCategories that carry a StoreIcon. Maps to view DTOs.
	public interface IStoreCatalogService
	{
		Task<List<StoreCategoryVM>> GetCategoriesAsync(int companyId);
		Task<List<StoreProductVM>> GetProductsAsync(int companyId);
		Task<StoreProductDetailVM?> GetProductAsync(int companyId, int id);
		Task<StoreCategoryPageVM?> GetCategoryAsync(int companyId, int categoryId);
	}

	public class StoreCatalogService : IStoreCatalogService
	{
		private readonly CrossDbContext _db;
		private readonly IIdProtector _ids;
		public StoreCatalogService(CrossDbContext db, IIdProtector ids) { _db = db; _ids = ids; }

		// featured-categories carousel = the ItemCategories flagged for the storefront (have an icon)
		public async Task<List<StoreCategoryVM>> GetCategoriesAsync(int companyId)
		{
			var arabic = DisplayName.IsArabic;   // captured per call: the culture must not be frozen into a cached query
			var list = await _db.ItemCategories.AsNoTracking()
				.Where(c => c.CompanyID == companyId && c.IsActive && c.StoreIcon != null)
				.OrderBy(c => c.ID)
				.Select(c => new StoreCategoryVM { Id = c.ID, Name = arabic ? c.Name : ((c.NameEn != null && c.NameEn != "") ? c.NameEn : c.Name), Icon = c.StoreIcon, ItemsCount = c.StoreItemsCount ?? 0 })
				.ToListAsync();
			foreach (var c in list) c.Token = _ids.Protect(c.Id);   // encrypt Id for the URL (in memory — can't run in SQL)
			return list;
		}

		// products = storefront Items (ItemCode STORE-*), with the category name + the display extras
		public async Task<List<StoreProductVM>> GetProductsAsync(int companyId)
		{
			var arabic = DisplayName.IsArabic;   // captured per call: the culture must not be frozen into a cached query
			var list = await (from p in _db.Items.AsNoTracking()
				 where p.CompanyID == companyId && p.IsActive && p.ItemCode.StartsWith("STORE-")
				 join c in _db.ItemCategories.AsNoTracking() on p.ItemCategoryId equals c.ID into cj
				 from c in cj.DefaultIfEmpty()
				 orderby p.ItemCode
				 select new StoreProductVM
				 {
					 Id = p.ID,
					 Name = arabic ? p.Name : ((p.NameEn != null && p.NameEn != "") ? p.NameEn : p.Name),
					 Category = c == null ? null : (arabic ? c.Name : ((c.NameEn != null && c.NameEn != "") ? c.NameEn : c.Name)),
					 Price = p.SalesPrice ?? 0,
					 OldPrice = p.StoreOldPrice,
					 DefaultImage = p.ImagePath,
					 HoverImage = p.StoreHoverImage,
					 Badge = p.StoreBadge,
					 Rating = p.StoreRating ?? 0,
					 Vendor = p.StoreVendor
				 }).ToListAsync();
			foreach (var p in list) p.Token = _ids.Protect(p.Id);
			return list;
		}

		// single product detail (display only): the Item + its category name + a few related storefront items + sidebar categories
		public async Task<StoreProductDetailVM?> GetProductAsync(int companyId, int id)
		{
			var d = await (from p in _db.Items.AsNoTracking()
						   where p.CompanyID == companyId && p.IsActive && p.ID == id && p.ItemCode.StartsWith("STORE-")
						   join c in _db.ItemCategories.AsNoTracking() on p.ItemCategoryId equals c.ID into cj
						   from c in cj.DefaultIfEmpty()
						   select new StoreProductDetailVM
						   {
							   Id = p.ID,
							   Name = p.Name,
							   Sku = p.ItemCode,
							   Category = c != null ? c.Name : null,
							   Price = p.SalesPrice ?? 0,
							   OldPrice = p.StoreOldPrice,
							   DefaultImage = p.ImagePath,
							   HoverImage = p.StoreHoverImage,
							   Badge = p.StoreBadge,
							   Rating = p.StoreRating ?? 0,
							   Vendor = p.StoreVendor
						   }).FirstOrDefaultAsync();
			if (d == null) return null;

			// discount % from old vs current price
			if (d.OldPrice.HasValue && d.OldPrice.Value > 0 && d.OldPrice.Value > d.Price)
				d.SavePct = (int)Math.Round((d.OldPrice.Value - d.Price) / d.OldPrice.Value * 100m);

			// gallery = primary image + extra ItemImages; fall back to default/hover so it's never empty
			var extra = await _db.ItemImages.AsNoTracking()
				.Where(x => x.CompanyID == companyId && x.ItemId == id)
				.OrderBy(x => x.SortOrder).ThenBy(x => x.ID)
				.Select(x => x.Path).ToListAsync();
			var imgs = new List<string>();
			if (!string.IsNullOrWhiteSpace(d.DefaultImage)) imgs.Add(d.DefaultImage!);
			imgs.AddRange(extra.Where(p => !string.IsNullOrWhiteSpace(p)));
			if (imgs.Count == 0 && !string.IsNullOrWhiteSpace(d.HoverImage)) imgs.Add(d.HoverImage!);
			d.Images = imgs.Distinct().ToList();

			// related = other storefront items (prefer same category), max 4
			d.Related = await (from p in _db.Items.AsNoTracking()
							   where p.CompanyID == companyId && p.IsActive && p.ID != id && p.ItemCode.StartsWith("STORE-")
							   join c in _db.ItemCategories.AsNoTracking() on p.ItemCategoryId equals c.ID into cj
							   from c in cj.DefaultIfEmpty()
							   orderby (c != null && c.Name == d.Category) ? 0 : 1, p.ItemCode
							   select new StoreProductVM
							   {
								   Id = p.ID,
								   Name = p.Name,
								   Category = c != null ? c.Name : null,
								   Price = p.SalesPrice ?? 0,
								   OldPrice = p.StoreOldPrice,
								   DefaultImage = p.ImagePath,
								   HoverImage = p.StoreHoverImage,
								   Badge = p.StoreBadge,
								   Rating = p.StoreRating ?? 0,
								   Vendor = p.StoreVendor
							   }).Take(4).ToListAsync();
			foreach (var r in d.Related) r.Token = _ids.Protect(r.Id);

			d.Categories = await GetCategoriesAsync(companyId);
			return d;
		}

		// one featured category + its storefront products (STORE-* items in that category). Read-only.
		public async Task<StoreCategoryPageVM?> GetCategoryAsync(int companyId, int categoryId)
		{
			var cat = await _db.ItemCategories.AsNoTracking()
				.Where(c => c.CompanyID == companyId && c.ID == categoryId && c.StoreIcon != null)
				.Select(c => new StoreCategoryPageVM { Id = c.ID, Name = c.Name, Icon = c.StoreIcon })
				.FirstOrDefaultAsync();
			if (cat == null) return null;

			cat.Products = await (from p in _db.Items.AsNoTracking()
								  where p.CompanyID == companyId && p.IsActive && p.ItemCategoryId == categoryId && p.ItemCode.StartsWith("STORE-")
								  orderby p.ItemCode
								  select new StoreProductVM
								  {
									  Id = p.ID,
									  Name = p.Name,
									  Category = cat.Name,
									  Price = p.SalesPrice ?? 0,
									  OldPrice = p.StoreOldPrice,
									  DefaultImage = p.ImagePath,
									  HoverImage = p.StoreHoverImage,
									  Badge = p.StoreBadge,
									  Rating = p.StoreRating ?? 0,
									  Vendor = p.StoreVendor
								  }).ToListAsync();
			cat.Token = _ids.Protect(cat.Id);
			foreach (var p in cat.Products) p.Token = _ids.Protect(p.Id);

			cat.Categories = await GetCategoriesAsync(companyId);
			return cat;
		}
	}
}
