using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// Brand foundation — trade-name management (identity + logo). No platform screens here.
	[SessionValidation]
	public class BrandController : Controller
	{
		private const int DefaultCompanyId = 1;
		private readonly IBrandService _brands;
		private readonly IWebHostEnvironment _env;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public BrandController(IBrandService brands, IWebHostEnvironment env, IStringLocalizer<CrossBuy.SharedResources> localizer) { _brands = brands; _env = env; L = localizer; }

		[HttpGet]
		public async Task<IActionResult> Brands(string? q, string? status)
		{
			bool? active = status == "active" ? true : status == "inactive" ? false : (bool?)null;
			ViewBag.Q = q; ViewBag.Status = status;
			return View(await _brands.GetBrandsAsync(DefaultCompanyId, q, active));
		}

		[HttpGet]
		public async Task<IActionResult> BrandEditor(int? id)
		{
			var model = (id.HasValue && id.Value > 0 ? await _brands.GetBrandAsync(DefaultCompanyId, id.Value) : null)
				?? new Brand { IsActive = true, CompanyId = DefaultCompanyId };
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveBrand(Brand model, IFormFile? logo, string? logo_remove)
		{
			model.CompanyId = DefaultCompanyId;
			var (ok, err, id) = await _brands.SaveBrandAsync(model, logo, _env.WebRootPath, logo_remove == "1");
			TempData[ok ? "BrandMsg" : "BrandErr"] = ok ? L["Brand saved"].Value : err;
			return ok ? RedirectToAction(nameof(Brands)) : RedirectToAction(nameof(BrandEditor), new { id = model.ID });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteBrand(int id)
		{
			var (ok, err) = await _brands.DeleteBrandAsync(DefaultCompanyId, id);
			TempData[ok ? "BrandMsg" : "BrandErr"] = ok ? L["Brand deleted"].Value : err;
			return RedirectToAction(nameof(Brands));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ActivateDefaultBrand()
		{
			var (brandId, linked) = await _brands.EnsureDefaultBrandAsync(DefaultCompanyId);
			TempData["BrandMsg"] = L["Brands activated — linked {0} branches to the default brand", linked].Value;
			return RedirectToAction(nameof(Brands));
		}
	}
}
