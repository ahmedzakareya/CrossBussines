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
		private readonly IBrandService _brands;
		private readonly IWebHostEnvironment _env;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		// The validated company source, same resolver the remediated controllers use.
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		public BrandController(IBrandService brands, IWebHostEnvironment env, IStringLocalizer<CrossBuy.SharedResources> localizer, CrossBuy.BL.Platform.IRequestCompanyResolver company) { _brands = brands; _env = env; L = localizer; _company = company; }

		// WHAT WAS HERE: `private const int DefaultCompanyId = 1;`. Every brand read, save, delete and
		// the default-brand activation named company 1, so a company-2 administrator managed company 1's
		// trade names and logos — and SaveBrand STAMPED CompanyId = 1 onto the row it saved, which is a
		// cross-company write, not a display fault.
		//
		// Same cached-resolution shape as AccountingController and TasksController: 0 when nothing
		// resolves, no row holds company 0, and each action refuses explicitly on top of that floor.
		private int? _companyId;

		private async Task<int> CompanyIdAsync()
		{
			if (_companyId.HasValue) return _companyId.Value;
			var scope = await _company.ResolveAsync();
			_companyId = scope.Ok ? scope.CompanyId : 0;
			return _companyId.Value;
		}

		private IActionResult CompanyRefused()
		{
			TempData["BrandErr"] = L["You do not have permission to perform this action"].Value;
			return RedirectToAction(nameof(Brands));
		}

		[HttpGet]
		public async Task<IActionResult> Brands(string? q, string? status)
		{
			var co = await CompanyIdAsync();
			// Brands is this controller's own refusal target, so an unresolved caller cannot be redirected
			// here without looping. It is told instead, and the company floor (0) yields the empty list.
			if (co == 0) TempData["BrandErr"] = L["You do not have permission to perform this action"].Value;
			bool? active = status == "active" ? true : status == "inactive" ? false : (bool?)null;
			ViewBag.Q = q; ViewBag.Status = status;
			return View(await _brands.GetBrandsAsync(co, q, active));
		}

		[HttpGet]
		public async Task<IActionResult> BrandEditor(int? id)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefused();
			var model = (id.HasValue && id.Value > 0 ? await _brands.GetBrandAsync(co, id.Value) : null)
				?? new Brand { IsActive = true, CompanyId = co };
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveBrand(Brand model, IFormFile? logo, string? logo_remove)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefused();
			// Stamped from the RESOLVED company, never from the posted model: a caller could otherwise
			// hand us another company's id and have it saved.
			model.CompanyId = co;
			var (ok, err, id) = await _brands.SaveBrandAsync(model, logo, _env.WebRootPath, logo_remove == "1");
			TempData[ok ? "BrandMsg" : "BrandErr"] = ok ? L["Brand saved"].Value : err;
			return ok ? RedirectToAction(nameof(Brands)) : RedirectToAction(nameof(BrandEditor), new { id = model.ID });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteBrand(int id)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefused();
			var (ok, err) = await _brands.DeleteBrandAsync(co, id);
			TempData[ok ? "BrandMsg" : "BrandErr"] = ok ? L["Brand deleted"].Value : err;
			return RedirectToAction(nameof(Brands));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ActivateDefaultBrand()
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefused();
			var (brandId, linked) = await _brands.EnsureDefaultBrandAsync(co);
			TempData["BrandMsg"] = L["Brands activated — linked {0} branches to the default brand", linked].Value;
			return RedirectToAction(nameof(Brands));
		}
	}
}
