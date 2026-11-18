using CrossBuy.BL;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H99: the clinical canvas (the approved prototype screens cv-home, cv-body, cv-fundus): the overview,
	// one drawing of a visit, its save, and a mark's photo (into the patient's documents).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalCanvasService? _canvasSvc;
		private HospitalCanvasService CanvasSvc => _canvasSvc ??= ActivatorUtilities.CreateInstance<HospitalCanvasService>(HttpContext.RequestServices);

		[HttpGet("Canvas")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CanvasHome(string? kind, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			ViewBag.Visits = await CanvasSvc.OpenVisitsAsync(ctx, 40, ct);
			ViewBag.Kind = HospitalCanvasKind.All.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));   // from the menu: open this drawing once a visit is chosen
			Chrome();
			return View();
		}

		[HttpGet("Consult/{id:int}/Canvas/{kind}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CanvasSheet(int id, string kind, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			kind = HospitalCanvasKind.All.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)) ?? "";
			if (kind == HospitalCanvasKind.Pain) return RedirectToAction("CanvasPatient", new { id });   // the patient's own screen
			if (await _patients.OpenAsync(ctx, (await _consult.GetAsync(ctx, id, ct))?.Encounter.PatientId ?? 0, ctx.EmployeeId, ct) == null) return NotFound();   // reading a visit opens the file: logged
			var (m, ready) = await CanvasSvc.SheetAsync(ctx, _consult, id, kind, ct);
			if (m == null) return NotFound();
			ViewBag.Ready = ready;
			if (m.Sheet != null) ViewBag.Addenda = await AddendaSvc.BlockAsync(ctx, HospitalAddendumSubject.Canvas, m.Sheet.ID, ct);   // H177
			if (kind == HospitalCanvasKind.Burn) ViewBag.BurnLimits = await HospitalBurnRules.ThresholdsAsync(_db, ctx.CompanyId, ct);   // H172: the clinic's approved thresholds
			Chrome();
			return View(m);
		}

		/// H112: the patient's pain screen — handed to the patient on the clinic's tablet (a signed-in device): full screen,
		/// no menu, large, Arabic first; what they draw is kept apart as patient-reported.
		[HttpGet("Consult/{id:int}/Canvas/Pain/Patient")]
		[HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> CanvasPatient(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var (m, ready) = await CanvasSvc.SheetAsync(ctx, _consult, id, HospitalCanvasKind.Pain, ct);
			if (m == null) return NotFound();
			ViewBag.Ready = ready;
			return View(m);
		}

		[HttpPost("Consult/{id:int}/Canvas/{kind}/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> CanvasSave(int id, string kind, [FromBody] HospitalCanvasSaveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			kind = HospitalCanvasKind.All.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)) ?? "";
			return Result(await CanvasSvc.SaveAsync(ctx, id, kind, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Canvas/Marks/{id:int}/Photo"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		[RequestSizeLimit(6_000_000)]
		public async Task<IActionResult> CanvasMarkPhoto(int id, IFormFile? photo, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return NotFound(new { ok = false, code = "not_found" });
			if (photo == null) return Json(new { ok = false, error = _t["The photo must be an image of up to 5 MB"].Value });
			await using var bytes = photo.OpenReadStream();
			var docs = HttpContext.RequestServices.GetRequiredService<IPlatformDocumentService>();
			return Result(await CanvasSvc.PhotoAsync(ctx, docs, id, bytes, photo.Length, ct));
		}

		/// H113: the mark's size measured on its photo against the ruler in the picture.
		[HttpPost("Canvas/Marks/{id:int}/Measure"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> CanvasMarkMeasure(int id, [FromBody] HospitalPhotoMeasureInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return Unauthorized();
			return Result(await CanvasSvc.MeasureAsync(ctx, id, input ?? new(), ct));
		}

		[HttpGet("Canvas/Marks/{id:int}/Photo")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CanvasMarkPhotoImage(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return NotFound();
			var file = await CanvasSvc.OpenPhotoAsync(ctx, HttpContext.RequestServices.GetRequiredService<IPlatformDocumentService>(), id, ct);
			if (file == null) return NotFound();
			Response.Headers["Cache-Control"] = "private, max-age=60";
			return File(file.Content, file.ContentType);
		}
	}
}
