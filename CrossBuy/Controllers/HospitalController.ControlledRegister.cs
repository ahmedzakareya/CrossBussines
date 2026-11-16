using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H137: the controlled-drug register of a store. Three tabs:
	//   · balances, each drug with its last count;
	//   · one drug's ledger with its running balance;
	//   · counts and their discrepancies.
	// A blind count and a discrepancy's resolution are both done from the theme's modals.
	// ==========================================================================================
	public partial class HospitalController
	{
		public static class CdTabs
		{
			public const string Balances = "balances", Ledger = "ledger", Counts = "counts";
			public static readonly IReadOnlyList<string> All = new[] { Balances, Ledger, Counts };
		}

		public sealed class HospitalCdCountInput { public int StoreId { get; set; } public int ItemId { get; set; } public decimal Counted { get; set; } public int? WitnessId { get; set; } public string? Password { get; set; } }

		private HospitalControlledCountService? _cdCount;
		private HospitalControlledCountService CdCountSvc => _cdCount ??= ActivatorUtilities.CreateInstance<HospitalControlledCountService>(HttpContext.RequestServices,
			_pharmacy, (IHospitalWitness)ActivatorUtilities.CreateInstance<HospitalPasswordWitness>(HttpContext.RequestServices));

		/// The store asked for, else the pharmacy's own, else the first.
		private static HospitalCdStore? PickStore(IReadOnlyList<HospitalCdStore> stores, int? store)
			=> stores.FirstOrDefault(s => s.Id == store) ?? stores.FirstOrDefault(s => s.Kind == HospitalCdStoreKind.Pharmacy) ?? stores.FirstOrDefault();

		[HttpGet("Pharmacy/Controlled")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ControlledRegister(int? store, string? tab, int? item, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var stores = await CdCountSvc.StoresAsync(ctx, ct);
			var s = PickStore(stores, store);
			ViewBag.Stores = stores; ViewBag.Store = s;
			ViewBag.Tab = CdTabs.All.Contains(tab ?? "") ? tab : CdTabs.Balances;
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			var balances = s == null ? Array.Empty<HospitalCdBalance>() : await CdCountSvc.BalancesAsync(ctx, s.Id, DateTime.UtcNow, ct);
			ViewBag.Drugs = balances;
			ViewBag.Item = balances.FirstOrDefault(b => b.ItemId == item)?.ItemId ?? balances.FirstOrDefault(b => b.OnHand != 0)?.ItemId ?? balances.FirstOrDefault()?.ItemId;
			if (ViewBag.CanPharmacy == true) ViewBag.Witnesses = await WitnessesAsync(ctx, ct);
			return View(s == null ? new HospitalCdStats(0, 0, 0, 0, 0) : await CdCountSvc.StatsAsync(ctx, s.Id, DateTime.UtcNow, ct));
		}

		[HttpGet("Pharmacy/Controlled/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ControlledRegisterData(int store, string? tab, int? item, string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			ViewBag.CanPharmacy = await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct);
			ViewBag.StoreId = store;
			if (!(await CdCountSvc.StoresAsync(ctx, ct)).Any(s => s.Id == store)) { PageHeaders(0, 1, pageSize); return PartialView("_CdBalanceRows", Array.Empty<HospitalCdBalance>()); }
			switch (tab)
			{
				case CdTabs.Ledger:
				{
					var (rows, total) = item is int i ? await CdCountSvc.LedgerAsync(ctx, store, i, page, pageSize, ct, filter == "in" ? (short)1 : filter == "out" ? (short)-1 : null) : (Array.Empty<HospitalCdLedgerRow>(), 0);
					PageHeaders(total, page, pageSize);
					return PartialView("_CdLedgerRows", rows);
				}
				case CdTabs.Counts:
				{
					var (rows, total) = await CdCountSvc.CountsAsync(ctx, store, filter, page, pageSize, DateTime.UtcNow, ct);
					PageHeaders(total, page, pageSize);
					return PartialView("_CdCountRows", rows);
				}
				default:
				{
					IEnumerable<HospitalCdBalance> rows = await CdCountSvc.BalancesAsync(ctx, store, DateTime.UtcNow, ct);
					if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); rows = rows.Where(r => new[] { r.Code, r.Name, r.NameEn, r.Drug?.GenericName, r.Drug?.GenericNameEn, r.Drug?.TallMan }.Any(x => x != null && x.Contains(t, StringComparison.OrdinalIgnoreCase))); }
					if (filter == "stock") rows = rows.Where(r => r.OnHand != 0);
					if (filter == "open") rows = rows.Where(r => r.OpenDiscrepancy);
					var list = rows.ToList();
					PageHeaders(list.Count, page, pageSize);
					return PartialView("_CdBalanceRows", list.Skip((page - 1) * pageSize).Take(pageSize).ToList());
				}
			}
		}

		/// A blind count: the answer says only whether it matched — never the expected quantity.
		[HttpPost("Pharmacy/Controlled/Count"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> ControlledCount([FromBody] HospitalCdCountInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await CdCountSvc.CountAsync(ctx, input?.StoreId ?? 0, input?.ItemId ?? 0, input?.Counted ?? -1, input?.WitnessId, input?.Password, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			var status = await _db.Set<HospitalControlledCount>().AsNoTracking().Where(c => c.ID == r.Id && c.CompanyId == ctx.CompanyId).Select(c => c.Status).FirstAsync(ct);
			return Json(new { ok = true, id = r.Id, matched = status == HospitalCountStatus.Matched });
		}

		[HttpPost("Pharmacy/Controlled/Counts/{id:int}/Resolve"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> ControlledResolve(int id, [FromBody] HospitalCdResolveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CdCountSvc.ResolveAsync(ctx, id, input, ctx.EmployeeId, ctx.UserId, DateTime.UtcNow, ct));
		}
	}
}
