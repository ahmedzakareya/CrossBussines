using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H73: the drug-safety knowledge base the pharmacy keeps — interacting pairs, dose limits — and the record
	// of the alerts doctors went past. One screen, three tabs; each list is searched and paged on the server.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalDrugSafetyService? _drugSafetySvc;
		private HospitalDrugSafetyService DrugSafetySvc => _drugSafetySvc ??= ActivatorUtilities.CreateInstance<HospitalDrugSafetyService>(HttpContext.RequestServices);

		private static readonly string[] DrugSafetyTabs = { "interactions", "limits", "overrides" };

		[HttpGet("DrugSafety")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DrugSafety(string? tab, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, pairs, ready) = await DrugSafetySvc.InteractionsAsync(ctx, null, null, 1, 1, ct);
			var (_, limits) = await DrugSafetySvc.LimitsAsync(ctx, null, 1, 1, ct);
			var (_, overrides) = await DrugSafetySvc.OverridesAsync(ctx, null, 1, 1, ct);
			ViewBag.Ready = ready;
			ViewBag.Counts = (pairs, limits, overrides);
			ViewBag.Tab = DrugSafetyTabs.Contains(tab) ? tab : "interactions";
			ViewBag.CanKeep = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View();
		}

		[HttpGet("DrugSafety/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DrugSafetyData(string? tab, string? q, string? severity, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			int total; IActionResult view;
			ViewBag.CanKeep = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			if (tab == "limits") { var (rows, t) = await DrugSafetySvc.LimitsAsync(ctx, q, page, pageSize, ct); total = t; view = PartialView("_DoseLimitRows", rows); }
			else if (tab == "overrides") { var (rows, t) = await DrugSafetySvc.OverridesAsync(ctx, q, page, pageSize, ct); total = t; view = PartialView("_AlertOverrideRows", rows); }
			else { var (rows, t, _) = await DrugSafetySvc.InteractionsAsync(ctx, q, string.IsNullOrEmpty(severity) ? null : severity, page, pageSize, ct); total = t; view = PartialView("_InteractionRows", rows); }
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)).ToString();
			return view;
		}

		[HttpPost("DrugSafety/Interactions"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveInteraction([FromBody] HospitalInteractionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DrugSafetySvc.SaveInteractionAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("DrugSafety/Limits"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveDoseLimit([FromBody] HospitalDoseLimitInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DrugSafetySvc.SaveLimitAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// An alert as the reader sees it: its sentence in their language, the interaction's effect in theirs.
		private object AlertJson(HospitalDrugAlert a)
		{
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("ar");
			return new
			{
				kind = a.Kind, severity = a.Severity, blocks = a.Blocks, severityText = _t["sev:" + a.Severity].Value,
				text = string.Format(_t[a.Key].Value, a.Args.Cast<object>().ToArray()) + (a.EffectEn != null ? " — " + (ar ? a.EffectAr : a.EffectEn) : ""),
			};
		}
	}
}
