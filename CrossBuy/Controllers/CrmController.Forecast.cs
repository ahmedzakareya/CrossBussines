using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// CRM C5 — quotas and forecasting. /Crm/Forecast replaces the old monthly weighted sum (its trend is kept as the
	// months strip on the new screen); /Crm/Quotas is where the targets are set.
	public partial class CrmController
	{
		private ICrmForecastService ForecastSvc => _forecastSvc ??= ActivatorUtilities.CreateInstance<CrmForecastService>(HttpContext.RequestServices);
		private ICrmForecastService? _forecastSvc;

		[HttpGet] public async Task<IActionResult> Forecast(string? period, int? person)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.CanEdit = await _access.CanAsync("edit");
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(await ForecastSvc.BoardAsync(cid, await _access.VisibleOwnerIdsAsync(), _access.CurrentEmployeeId(), period, person));
		}

		[HttpGet] public async Task<IActionResult> ForecastDeals(string? period, int? owner, string? category, string? q, int page = 1, int pageSize = 25)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (rows, total) = await ForecastSvc.DealsAsync(cid, await _access.VisibleOwnerIdsAsync(), _access.CurrentEmployeeId(), await _access.CanAsync("edit"), period, owner, category, q, page, pageSize);
			SetPaging(total, page, pageSize);
			ViewBag.Assignable = await ForecastSvc.AssignableAsync(cid, _access.CurrentEmployeeId(), await _access.CanAsync("manage"));
			return PartialView("_ForecastDeals", rows);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> ForecastCategory(int id, string category)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e) = await ForecastSvc.SetCategoryAsync(cid, await _access.VisibleOwnerIdsAsync(), _access.CurrentEmployeeId(), id, category);
			return Done(d, e);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> ForecastOwner(int id, int owner)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e) = await ForecastSvc.SetOwnerAsync(cid, await _access.VisibleOwnerIdsAsync(), _access.CurrentEmployeeId(), await _access.CanAsync("manage"), id, owner);
			return Done(d, e);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> ForecastSubmit(string period, string? note)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e) = await ForecastSvc.SubmitAsync(cid, _access.CurrentEmployeeId(), period, note);
			return Done(d, e);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> ForecastReview(int id, bool approve, decimal? adjustedCommit, string? note)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e) = await ForecastSvc.ReviewAsync(cid, _access.CurrentEmployeeId(), id, approve, adjustedCommit, note);
			return Done(d, e);
		}

		[HttpGet] public async Task<IActionResult> Quotas(int? year, string? q)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var y = year is >= 2000 and <= 2100 ? year.Value : DateTime.Today.Year;
			ViewBag.Year = y; ViewBag.Q = q;
			return View(await ForecastSvc.QuotasAsync(cid, await _access.VisibleOwnerIdsAsync(), _access.CurrentEmployeeId(), await _access.CanAsync("manage"), y, q));
		}

		/// The sheet posts one field per changed cell: q_{employee}_{yyyy-MM} = amount.
		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> SaveQuotas(int year)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var values = new Dictionary<(int, string), decimal>();
			foreach (var (k, v) in Request.Form)
			{
				if (!k.StartsWith("q_")) continue;
				var parts = k.Split('_');
				if (parts.Length != 3 || !int.TryParse(parts[1], out var emp)) return Done(false, "Could not save. Refresh the page and try again.");
				var raw = v.ToString().Replace(",", "").Trim();
				if (raw.Length == 0) raw = "0";
				if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount)) return Done(false, "A target must be a number");
				values[(emp, parts[2])] = amount;
			}
			var (d, e, n) = await ForecastSvc.SaveQuotasAsync(cid, _access.CurrentEmployeeId(), await _access.CanAsync("manage"), year, values);
			return Done(d, e, n);
		}
	}
}
