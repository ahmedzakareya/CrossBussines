using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H7: the catalogue (services, medicines, consumables) and the price lists. The main
	// list is the selling price on each card; every other list has one currency and a discount rule,
	// and is reviewed line by line before it is approved. Every rule is HospitalPricingService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalKeysInput { public List<string>? Keys { get; set; } }
		public sealed class HospitalIdsInput { public List<int>? Ids { get; set; } }
		public sealed class HospitalPriceInput { public decimal? Price { get; set; } }
		public sealed class HospitalCategoryMapInput { public Dictionary<string, string> Map { get; set; } = new(); }
		/// A patient on a list (a named record: a view reading an anonymous type through dynamic throws).
		public sealed record HospitalListPatient(int Id, string Mrn, string Name, string NameEn, string? PhotoPath);

		/// The approved lists a patient can be put on (PatientEdit).
		private async Task<List<HospitalChoice>> ApprovedPriceListsAsync(BusinessContext ctx, CancellationToken ct)
			=> await _db.HospitalPriceLists.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.Status == HospitalPriceListStatus.Active)
				.OrderBy(l => l.Code).Select(l => new HospitalChoice(l.ID, l.Name, l.NameEn, l.Code)).ToListAsync(ct);

		// ---------------- the catalogue ----------------

		[HttpGet("Catalog")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Catalog(string? tab, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Tab = HospitalPriceGroups.All.Contains(tab ?? "") ? tab : HospitalPriceGroups.Service;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Facility = await _pricing.FacilityCurrencyAsync(ctx, ct);
			ViewBag.Lists = await _pricing.ListsAsync(ctx, ct);
			ViewBag.Mapped = await _pricing.ItemCategoriesAsync(ctx, ct);
			ViewBag.Categories = await _db.ItemCategories.AsNoTracking().Where(c => c.CompanyID == ctx.CompanyId).OrderBy(c => c.Code)
				.Select(c => new HospitalChoice(c.ID, c.Name, c.NameEn, c.Code)).ToListAsync(ct);
			ViewBag.LineState = await CatalogLineStateAsync(ctx, ct);
			return View(await _pricing.CatalogAsync(ctx, ct));
		}

		/// For each catalogue key: on how many other lists it is, and on how many it is not updated.
		private async Task<Dictionary<string, (int On, int Stale)>> CatalogLineStateAsync(BusinessContext ctx, CancellationToken ct)
		{
			var map = new Dictionary<string, (int, int)>();
			foreach (var row in await _pricing.ListsAsync(ctx, ct))
			{
				if (row.List.Status == HospitalPriceListStatus.Archived) continue;
				var v = await _pricing.ListAsync(ctx, row.List.ID, ct);
				foreach (var l in v!.Lines)
				{
					var cur = map.GetValueOrDefault(l.Entry.Key);
					map[l.Entry.Key] = (cur.Item1 + 1, cur.Item2 + (l.Stale ? 1 : 0));
				}
			}
			return map;
		}

		/// One catalogue entry's card: its selling price (the main list) and its price on every list.
		[HttpGet("Catalog/{key}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CatalogEntry(string key, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var entry = (await _pricing.CatalogAsync(ctx, ct)).FirstOrDefault(e => e.Key == key);
			if (entry == null) return NotFound();
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Facility = await _pricing.FacilityCurrencyAsync(ctx, ct);
			var lists = new List<HospitalPriceListView>();
			foreach (var row in await _pricing.ListsAsync(ctx, ct))
				if (row.List.Status != HospitalPriceListStatus.Archived) lists.Add((await _pricing.ListAsync(ctx, row.List.ID, ct))!);
			ViewBag.Lists = lists;
			// H15: a service may be a dental procedure; its profile is edited on this card
			if (entry.ServiceId is int sid) ViewBag.DentalProfile = await DentalSvc.ProcedureOfAsync(ctx, sid, ct);
			return View(entry);
		}

		[HttpPost("Catalog/Service/{id:int}/Price"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveServicePrice(int id, [FromBody] HospitalPriceInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.SaveServicePriceAsync(ctx, id, input?.Price ?? -1, ctx.EmployeeId, ct));
		}

		[HttpPost("Catalog/Item/{id:int}/PublicPrice"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePublicPrice(int id, [FromBody] HospitalPriceInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.SavePublicPriceAsync(ctx, id, input?.Price, ctx.EmployeeId, ct));
		}

		[HttpPost("Catalog/Categories"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveItemCategories([FromBody] HospitalCategoryMapInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var map = new Dictionary<int, string>();
			foreach (var kv in input?.Map ?? new()) if (int.TryParse(kv.Key, out var id) && !string.IsNullOrEmpty(kv.Value)) map[id] = kv.Value;
			return Result(await _pricing.SaveItemCategoriesAsync(ctx, map, ct));
		}

		// ---------------- price lists ----------------

		[HttpGet("PriceLists")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PriceLists(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Facility = await _pricing.FacilityCurrencyAsync(ctx, ct);
			ViewBag.Currencies = await _pricing.CurrenciesAsync(ct);
			ViewBag.Catalog = await _pricing.CatalogAsync(ctx, ct);
			return View(await _pricing.ListsAsync(ctx, ct));
		}

		[HttpGet("PriceList/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> PriceList(int id, string? tab, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var v = await _pricing.ListAsync(ctx, id, ct);
			if (v == null) return NotFound();
			Chrome();
			ViewBag.Tab = HospitalPriceGroups.All.Contains(tab ?? "") ? tab : HospitalPriceGroups.Service;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Versions = await _db.HospitalPriceLists.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.Code == v.List.Code).OrderByDescending(l => l.Version).ToListAsync(ct);
			ViewBag.Patients = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.PriceListId == id).OrderBy(p => p.Mrn).Take(50)
				.Select(p => new HospitalListPatient(p.ID, p.Mrn, p.Name, p.NameEn, p.PhotoPath)).ToListAsync(ct);
			ViewBag.Categories = await _pricing.CategoryChoicesAsync(ctx, id, ct);
			return View(v);
		}

		[HttpPost("PriceLists/Create"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CreatePriceList([FromBody] HospitalPriceListInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.CreateAsync(ctx, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("PriceList/{id:int}/Update"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> UpdatePriceList(int id, [FromBody] HospitalPriceListInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.UpdateAsync(ctx, id, input ?? new(), ct));
		}

		[HttpPost("PriceList/{id:int}/AddLines"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> AddPriceListLines(int id, [FromBody] HospitalKeysInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.AddLinesAsync(ctx, id, input?.Keys, DateTime.UtcNow, ct));
		}

		// ---------------- H22: any inventory item, or a whole category ----------------

		public sealed class HospitalItemPicksInput { public List<HospitalItemPick>? Items { get; set; } }
		public sealed class HospitalCategoryAddInput { public int CategoryId { get; set; } public string? Kind { get; set; } public bool KeepLinked { get; set; } }

		[HttpGet("PriceList/{id:int}/ItemSearch")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> PriceListItemSearch(int id, string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var hits = await _pricing.SearchItemsAsync(ctx, id, q, ct);
			return Json(new { ok = true, items = hits.Select(h => new { id = h.ItemId, code = h.Code, name = DisplayName.Of(h.Name, h.NameEn), category = DisplayName.Of(h.Category, h.CategoryEn), price = h.MainPrice, kind = h.Kind, onList = h.OnList }) });
		}

		[HttpGet("Catalog/ItemSearch")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CatalogItemSearch(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var hits = await _pricing.SearchItemsAsync(ctx, null, q, ct);
			return Json(new { ok = true, items = hits.Select(h => new { id = h.ItemId, code = h.Code, name = DisplayName.Of(h.Name, h.NameEn), category = DisplayName.Of(h.Category, h.CategoryEn), price = h.MainPrice, kind = h.Kind, onList = h.OnList }) });
		}

		[HttpPost("Catalog/AddItems"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CatalogAddItems([FromBody] HospitalItemPicksInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.JoinCatalogAsync(ctx, input?.Items, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("PriceList/{id:int}/AddItems"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> AddPriceListItems(int id, [FromBody] HospitalItemPicksInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.AddItemsAsync(ctx, id, input?.Items, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("PriceList/{id:int}/AddCategory"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> AddPriceListCategory(int id, [FromBody] HospitalCategoryAddInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.AddCategoryAsync(ctx, id, input.CategoryId, input.Kind, input.KeepLinked, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("PriceList/{id:int}/ApproveLines"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ApprovePriceListLines(int id, [FromBody] HospitalIdsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.ApproveLinesAsync(ctx, id, input?.Ids, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("PriceList/{id:int}/Approve"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ApprovePriceList(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.ApproveListAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("PriceList/{id:int}/Archive"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ArchivePriceList(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.ArchiveAsync(ctx, id, ct));
		}

		[HttpPost("PriceList/{id:int}/Copy"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CopyPriceList(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.CopyVersionAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("PriceListLine/{id:int}/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePriceListLine(int id, [FromBody] HospitalPriceLineInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _pricing.SaveLineAsync(ctx, id, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// "Try the pricing": which list prices this service for this patient today, and at what.
		[HttpGet("PriceLists/Quote")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> QuotePrice(int patientId, int serviceId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var s = await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(x => x.ID == serviceId && x.CompanyId == ctx.CompanyId, ct);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == patientId && x.CompanyId == ctx.CompanyId, ct);
			if (s == null || patient == null) return NotFound(new { ok = false, code = "not_found" });
			var pl = await _pricing.ListForPatientAsync(ctx, patientId, DateTime.UtcNow, ct);
			var q = await _pricing.QuoteServiceAsync(ctx, pl?.ID, s, ct);
			var cur = await _pricing.CurrencyOfAsync(ctx, q.CurrencyId, ct);
			var fac = await _pricing.FacilityCurrencyAsync(ctx, ct);
			var (rok, rate) = await _pricing.RateToFacilityAsync(ctx, q.CurrencyId, DateTime.UtcNow, ct);
			bool assigned = patient.PriceListId != null;
			return Json(new
			{
				ok = true, list = pl == null ? null : new { id = pl.ID, code = pl.Code, name = DisplayName.Of(pl.Name, pl.NameEn) },
				assignedButUnusable = assigned && pl == null,
				price = cur.Format(q.Price), currency = cur.Label(DisplayName.IsArabic), currencyCode = cur.Code, foreign = q.CurrencyId != null,
				facility = rok && q.CurrencyId != null ? fac.Format(Math.Round(q.Price * rate, fac.Decimals)) + " " + fac.Label(DisplayName.IsArabic) : null,
				rate = rok && q.CurrencyId != null ? rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : null,
				source = q.Source, outside = q.OutsideList,
			});
		}
	}
}
