using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Effective brand identity for a location (Brand values, falling back to the Company).
	public class BrandIdentity
	{
		public int? BrandId { get; set; }
		public string Source { get; set; } = "company";   // brand | company
		public string? Name { get; set; }
		public string? TradeName { get; set; }
		public string? LogoPath { get; set; }
		public string? ColorPrimary { get; set; }
		public string? ColorSecondary { get; set; }
		public string? ColorAccent { get; set; }
		public string? Address { get; set; }
		public string? Phone { get; set; }
		public string? Email { get; set; }
		public string? Website { get; set; }
		public string? ReceiptFooterAr { get; set; }
		public string? ReceiptFooterEn { get; set; }
	}

	public interface IBrandService
	{
		Task<List<Brand>> GetBrandsAsync(int companyId, string? q, bool? active);
		Task<Brand?> GetBrandAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveBrandAsync(Brand dto, IFormFile? logo, string? webRootPath, bool removeLogo = false);
		Task<(bool ok, string? error)> DeleteBrandAsync(int companyId, int id);
		/// First-time activation: create a default brand (if none) and link all unbranded branches to it.
		Task<(int brandId, int linked)> EnsureDefaultBrandAsync(int companyId);
		/// Resolve the effective identity for a location (branch) — brand values with company fallback.
		Task<BrandIdentity> ResolveIdentityAsync(int branchId);
	}

	public class BrandService : IBrandService
	{
		private const int CompanyNodeType = 1, BrandNodeType = 6;
		private readonly CrossDbContext _db;
		public BrandService(CrossDbContext db) { _db = db; }

		public Task<List<Brand>> GetBrandsAsync(int companyId, string? q, bool? active)
		{
			var query = _db.Brands.AsNoTracking().Where(b => b.CompanyId == companyId);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				query = query.Where(b => b.Code.Contains(t) || b.Name.Contains(t) || (b.NameEn != null && b.NameEn.Contains(t)) || (b.TradeName != null && b.TradeName.Contains(t)));
			}
			if (active.HasValue) query = query.Where(b => b.IsActive == active.Value);
			return query.OrderBy(b => b.Name).ToListAsync();
		}

		public Task<Brand?> GetBrandAsync(int companyId, int id) =>
			_db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.ID == id && b.CompanyId == companyId);

		public async Task<(bool ok, string? error, int id)> SaveBrandAsync(Brand dto, IFormFile? logo, string? webRootPath, bool removeLogo = false)
		{
			if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name)) return (false, "الكود والاسم مطلوبان", 0);
			var dup = await _db.Brands.AnyAsync(b => b.CompanyId == dto.CompanyId && b.Code == dto.Code && b.ID != dto.ID);
			if (dup) return (false, "كود العلامة مستخدم من قبل", 0);

			Brand e;
			bool isNew = dto.ID <= 0;
			if (!isNew) e = await _db.Brands.FirstOrDefaultAsync(b => b.ID == dto.ID && b.CompanyId == dto.CompanyId) ?? throw new InvalidOperationException("العلامة غير موجودة");
			else { e = new Brand { CompanyId = dto.CompanyId, CreatedAt = DateTime.UtcNow }; _db.Brands.Add(e); }

			e.Code = dto.Code.Trim(); e.Name = dto.Name.Trim(); e.NameEn = dto.NameEn; e.IsActive = dto.IsActive;
			e.ColorPrimary = dto.ColorPrimary; e.ColorSecondary = dto.ColorSecondary; e.ColorAccent = dto.ColorAccent;
			e.TradeName = dto.TradeName; e.Address = dto.Address; e.Phone = dto.Phone; e.Email = dto.Email; e.Website = dto.Website;
			e.ReceiptFooterAr = dto.ReceiptFooterAr; e.ReceiptFooterEn = dto.ReceiptFooterEn;

			if (logo != null && logo.Length > 0 && !string.IsNullOrEmpty(webRootPath))
			{
				var dir = System.IO.Path.Combine(webRootPath, "uploads", "brands");
				if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
				var fileName = Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(logo.FileName);
				using (var stream = System.IO.File.Create(System.IO.Path.Combine(dir, fileName))) await logo.CopyToAsync(stream);
				e.LogoPath = "/uploads/brands/" + fileName;
			}
			else if (removeLogo) e.LogoPath = null;
			await _db.SaveChangesAsync();

			// additive org-tree node (H_Type=6) under the company node — never reparents existing branches
			if (isNew)
			{
				var companyNode = await _db.Hierarchicals.FirstOrDefaultAsync(h => h.H_Type == CompanyNodeType && h.H_ObjectID == dto.CompanyId);
				if (companyNode != null && !await _db.Hierarchicals.AnyAsync(h => h.H_Type == BrandNodeType && h.H_ObjectID == e.ID))
				{
					_db.Hierarchicals.Add(new Hierarchical { H_Type = BrandNodeType, H_ObjectID = e.ID, H_Parent = companyNode.H_ID, H_Name = e.Name, H_NameEn = e.NameEn, IsActive = true, CreatedAt = DateTime.UtcNow });
					await _db.SaveChangesAsync();
				}
			}
			return (true, null, e.ID);
		}

		public async Task<(bool ok, string? error)> DeleteBrandAsync(int companyId, int id)
		{
			var e = await _db.Brands.FirstOrDefaultAsync(b => b.ID == id && b.CompanyId == companyId);
			if (e == null) return (false, "العلامة غير موجودة");
			if (await _db.Branches.AnyAsync(b => b.BrandId == id)) return (false, "لا يمكن الحذف: توجد فروع مرتبطة بهذه العلامة");
			var node = await _db.Hierarchicals.FirstOrDefaultAsync(h => h.H_Type == BrandNodeType && h.H_ObjectID == id);
			if (node != null) _db.Hierarchicals.Remove(node);
			_db.Brands.Remove(e);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(int brandId, int linked)> EnsureDefaultBrandAsync(int companyId)
		{
			var brand = await _db.Brands.FirstOrDefaultAsync(b => b.CompanyId == companyId);
			if (brand == null)
			{
				var cname = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == companyId).Select(c => c.ComoanyNameAr ?? c.CompanyName).FirstOrDefaultAsync();
				brand = new Brand { CompanyId = companyId, Code = "DEFAULT", Name = string.IsNullOrWhiteSpace(cname) ? "العلامة الافتراضية" : cname!, IsActive = true, CreatedAt = DateTime.UtcNow };
				_db.Brands.Add(brand);
				await _db.SaveChangesAsync();
			}
			var unbranded = await _db.Branches.Where(b => b.CompanyID == companyId && b.BrandId == null).ToListAsync();
			foreach (var br in unbranded) br.BrandId = brand.ID;
			if (unbranded.Count > 0) await _db.SaveChangesAsync();
			return (brand.ID, unbranded.Count);
		}

		public async Task<BrandIdentity> ResolveIdentityAsync(int branchId)
		{
			var branch = await _db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == branchId);
			var company = branch != null ? await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyID == branch.CompanyID) : null;
			var brand = (branch?.BrandId != null) ? await _db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.ID == branch.BrandId.Value) : null;

			string? companyName = company?.ComoanyNameAr ?? company?.CompanyName;
			var id = new BrandIdentity { BrandId = brand?.ID, Source = brand != null ? "brand" : "company" };
			// each field: brand value first, then company fallback (only name/logo have a company equivalent)
			id.Name = brand?.Name ?? companyName;
			id.TradeName = brand?.TradeName ?? brand?.Name ?? companyName;
			id.LogoPath = !string.IsNullOrEmpty(brand?.LogoPath) ? brand!.LogoPath : company?.CompanyImage;
			id.ColorPrimary = brand?.ColorPrimary;
			id.ColorSecondary = brand?.ColorSecondary;
			id.ColorAccent = brand?.ColorAccent;
			id.Address = brand?.Address;
			id.Phone = brand?.Phone;
			id.Email = brand?.Email;
			id.Website = brand?.Website;
			id.ReceiptFooterAr = brand?.ReceiptFooterAr;
			id.ReceiptFooterEn = brand?.ReceiptFooterEn;
			return id;
		}
	}
}
