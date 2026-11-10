using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H74: order sets. The administrator's list (searched and paged) and one set's lines; the nursing
	// station's window lists the active sets, and a doctor places one for an inpatient (a stopping alert comes back for
	// one reason, as a single order does).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalOrderSetService? _orderSetSvc;
		private HospitalOrderSetService OrderSetSvc => _orderSetSvc ??= ActivatorUtilities.CreateInstance<HospitalOrderSetService>(HttpContext.RequestServices);

		[HttpGet("OrderSets")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> OrderSets(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, ready) = await OrderSetSvc.ListAsync(ctx, null, false, 1, 1, ct);
			ViewBag.Ready = ready;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Departments = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.SortOrder).ToListAsync(ct);
			return View();
		}

		[HttpGet("OrderSets/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> OrderSetsData(string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total, _) = await OrderSetSvc.ListAsync(ctx, q, false, page, pageSize, ct);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return PartialView("_OrderSetRows", rows);
		}

		[HttpGet("OrderSets/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> OrderSet(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var view = await OrderSetSvc.SetAsync(ctx, id, ct);
			if (view == null) return NotFound();
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Catalog = await InpatientOrderSvc.CatalogAsync(ctx, ct);
			return View(view);
		}

		[HttpPost("OrderSets"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveOrderSet([FromBody] HospitalOrderSetInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OrderSetSvc.SaveSetAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("OrderSets/Items"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveOrderSetItem([FromBody] HospitalOrderSetItemInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OrderSetSvc.SaveItemAsync(ctx, input, ct));
		}

		[HttpPost("OrderSets/Items/{itemId:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> RemoveOrderSetItem(int itemId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await OrderSetSvc.RemoveItemAsync(ctx, itemId, ct));
		}

		/// The active sets and their lines, for the nursing station's window (the reader's language chosen here).
		[HttpGet("OrderSets/Active")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ActiveOrderSets(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("ar");
			var sets = await OrderSetSvc.ActiveAsync(ctx, ct);
			var svcIds = sets.SelectMany(x => x.Items).Where(i => i.ServiceId != null).Select(i => i.ServiceId!.Value).Distinct().ToList();
			var svcNames = await _db.HospitalServices.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && svcIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => ar ? x.Name : x.NameEn, ct);
			return Json(sets.Select(s => new
			{
				id = s.Set.ID, code = s.Set.Code, name = ar ? s.Set.Name : s.Set.NameEn,
				items = s.Items.Select(i => new
				{
					id = i.ID, kind = i.Kind, preselected = i.Preselected,
					text =
						i.ServiceId is int sid ? (svcNames.GetValueOrDefault(sid) ?? "—") :
						i.Kind == Models.Context.Hospital.HospitalOrderSetItemKind.Medication
						? string.Join(" ", new[] { i.Drug, i.Dose, i.Route, i.AsNeeded ? "PRN" : i.Hours }.Where(x => !string.IsNullOrWhiteSpace(x)))
						: (ar ? (i.TitleAr ?? i.TitleEn) : (i.TitleEn ?? i.TitleAr)) + " (+" + i.DueInHours + "h)",
					note = i.Note,
				}),
			}));
		}

		[HttpPost("OrderSets/{id:int}/Apply"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> ApplyOrderSet(int id, [FromBody] HospitalOrderSetApplyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
						var (r, alerts) = await OrderSetSvc.ApplyAsync(ctx, id, input, NursingSvc, DrugSafetySvc, ctx.EmployeeId, DateTime.UtcNow, ct, InpatientOrderSvc);
			if (!r.Ok && r.Error == HospitalOrderSetService.MeetsAlert)
				return Json(new { ok = false, code = "clinical_alert", error = _t[HospitalOrderSetService.MeetsAlert].Value, alerts = alerts.Select(a => AlertJson(a.Alert)) });
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, placed = r.Id, warnings = alerts.Where(a => !a.Alert.Blocks).Select(a => AlertJson(a.Alert)) });
		}
	}
}
