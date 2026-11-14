using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H93: the concept dictionary (searched, paged, edited in a modal) and the template designer (the
	// templates, the version on the canvas with its sections and fields, the field's properties, the versions) —
	// the approved prototype screens sc-concepts and sc-designer.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalClinicalContentService? _contentSvc;
		private HospitalClinicalContentService ContentSvc => _contentSvc ??= ActivatorUtilities.CreateInstance<HospitalClinicalContentService>(HttpContext.RequestServices);

		[HttpGet("Concepts")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Concepts(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, total, ready) = await ContentSvc.ConceptsAsync(ctx, null, 1, 1, ct);
			ViewBag.Ready = ready;
			ViewBag.Total = total;
			ViewBag.CanManage = await ContentSvc.CanManageAsync(ctx, ct);
			return View();
		}

		[HttpGet("Concepts/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ConceptsData(string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total, _) = await ContentSvc.ConceptsAsync(ctx, q, page, pageSize, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.CanManage = await ContentSvc.CanManageAsync(ctx, ct);
			ViewBag.RangeCounts = await RangeCountsAsync(ctx, rows.Select(r => r.Concept.ID).ToList(), ct);   // H115
			return PartialView("_ConceptRows", rows);
		}

		// ---------------- H115: ranges by who the patient is ----------------
		private HospitalConceptRangeService? _rangeSvc;
		private HospitalConceptRangeService RangeSvc => _rangeSvc ??= ActivatorUtilities.CreateInstance<HospitalConceptRangeService>(HttpContext.RequestServices);

		private async Task<Dictionary<int, int>> RangeCountsAsync(BusinessContext ctx, List<int> ids, CancellationToken ct)
		{
			try
			{
				return await _db.Set<HospitalConceptRange>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && ids.Contains(r.ConceptId))
					.GroupBy(r => r.ConceptId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return new(); }
		}

		/// An age in days, said in the unit a person would use.
		private string AgeSaid(int days)
		{
			string N(decimal v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
			return days >= 730 ? string.Format(_t["{0} years"].Value, N(Math.Round(days / 365.25m, 1))) : days >= 60 ? string.Format(_t["{0} months"].Value, N(Math.Round(days / 30.4375m, 1))) : string.Format(_t["{0} days"].Value, days);
		}

		/// Who a range is for, in words.
		private string WhoSaid(HospitalConceptRange r)
		{
			var parts = new List<string>();
			if (r.Sex != null) parts.Add(_t[r.Sex == "M" ? "Male" : "Female"].Value);
			if (r.Pregnancy != null) parts.Add(_t["preg:" + r.Pregnancy].Value);
			if (r.AgeFromDays != null || r.AgeToDays != null)
				parts.Add(r.AgeFromDays is int f && r.AgeToDays is int to ? string.Format(_t["age {0} to under {1}"].Value, AgeSaid(f), AgeSaid(to))
					: r.AgeFromDays is int f2 ? string.Format(_t["age {0} and over"].Value, AgeSaid(f2)) : string.Format(_t["under {0}"].Value, AgeSaid(r.AgeToDays!.Value)));
			return string.Join(" · ", parts);
		}

		private static string Limits(decimal? lo, decimal? hi)
		{
			string N(decimal? v) => v?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "";
			return lo != null && hi != null ? N(lo) + " – " + N(hi) : lo != null ? "≥ " + N(lo) : hi != null ? "≤ " + N(hi) : "—";
		}

		[HttpGet("Concepts/{id:int}/Ranges")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ConceptRanges(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var (rows, ready) = await RangeSvc.ListAsync(ctx, id, ct);
			return Json(new
			{
				ok = ready, error = ready ? null : _t[HospitalConceptRangeService.NoTables].Value,
				rows = rows.Select(r => new
				{
					id = r.ID, who = WhoSaid(r), normal = Limits(r.NormalLow, r.NormalHigh) + (r.NormalHigh2 is decimal n2 ? " / ≤ " + n2.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : ""),
					critical = r.CriticalLow == null && r.CriticalHigh == null ? "—" : (r.CriticalLow != null ? "≤ " + r.CriticalLow.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "") + (r.CriticalLow != null && r.CriticalHigh != null ? " · " : "") + (r.CriticalHigh != null ? "≥ " + r.CriticalHigh.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "") + (r.CriticalHigh2 is decimal c2 ? " / ≥ " + c2.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : ""),
					source = r.Source,
				}),
			});
		}

		[HttpPost("Concepts/{id:int}/Ranges"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ConceptRangeSave(int id, [FromBody] HospitalConceptRangeInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RangeSvc.SaveAsync(ctx, id, null, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Concepts/Ranges/{rid:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ConceptRangeRemove(int rid, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RangeSvc.RemoveAsync(ctx, rid, ct));
		}

		[HttpGet("Concepts/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ConceptGet(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var c = await ContentSvc.ConceptAsync(ctx, id, ct);
			return c == null ? NotFound() : Json(c);
		}

		[HttpPost("Concepts"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ConceptSave([FromBody] HospitalConceptInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContentSvc.SaveConceptAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("Templates")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Templates(int? t, int? v, int? f, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanManage = await ContentSvc.CanManageAsync(ctx, ct);
			return View(await ContentSvc.DesignerAsync(ctx, t, v, f, ct));
		}

		[HttpPost("Templates"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TemplateCreate([FromBody] HospitalTemplateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContentSvc.CreateTemplateAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Templates/{id:int}/NewVersion"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TemplateNewVersion(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContentSvc.NewVersionAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		public sealed class HospitalPublishInput { public string? Note { get; set; } public string? NoteEn { get; set; } }

		[HttpPost("Templates/Versions/{id:int}/Publish"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TemplatePublish(int id, [FromBody] HospitalPublishInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContentSvc.PublishAsync(ctx, id, input?.Note, input?.NoteEn, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Templates/Versions/{id:int}/Fields"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TemplateFieldAdd(int id, [FromBody] HospitalTemplateFieldInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContentSvc.AddFieldAsync(ctx, id, input, ct));
		}

		[HttpPost("Templates/Versions/{id:int}/Order"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TemplateFieldOrder(int id, [FromBody] List<HospitalFieldPlace> order, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || order == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContentSvc.ReorderAsync(ctx, id, order, ct));
		}

		[HttpPost("Templates/Fields/{id:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TemplateFieldSave(int id, [FromBody] HospitalTemplateFieldInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContentSvc.SaveFieldAsync(ctx, id, input, ct));
		}

		[HttpPost("Templates/Fields/{id:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> TemplateFieldRemove(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ContentSvc.RemoveFieldAsync(ctx, id, ct));
		}
	}
}
