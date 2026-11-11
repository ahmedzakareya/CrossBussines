using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
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
			return PartialView("_ConceptRows", rows);
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
