using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H94: the specialty visit beside the consultation (the approved prototype screen sc-im): its templates
	// filled and saved, ONE signature with the consultation, the order sets its diagnoses propose, a gap's test ordered,
	// the diagnoses added to the patient's problem list.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalSpecialtyVisitService? _visitSvc;
		private HospitalSpecialtyVisitService VisitSvc => _visitSvc ??= ActivatorUtilities.CreateInstance<HospitalSpecialtyVisitService>(HttpContext.RequestServices);
		private bool UiEnglish => !System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("ar");

		public sealed class HospitalVisitFormsInput { public Dictionary<int, Dictionary<string, string>> Forms { get; set; } = new(); }
		public sealed class HospitalVisitTemplateInput { public int TemplateId { get; set; } }
		public sealed class HospitalVisitOrderSetInput { public int SetId { get; set; } public List<int> ItemIds { get; set; } = new(); }
		public sealed class HospitalVisitServiceInput { public int ServiceId { get; set; } }

		/// H98: the specialty catalogue — the built specialties and every other one, with their state read from the system.
		[HttpGet("Specialty")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SpecialtyCatalog(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (built, others) = await HospitalSpecialtyCatalog.CardsAsync(_db, ctx, ct);
			ViewBag.Built = built;
			return View(others);
		}

		/// A specialty clinic's list of visits (the menu's "Internal medicine", "Paediatrics" …), the clinic by its code.
		[HttpGet("Specialty/{code}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SpecialtyClinic(string code, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var (_, total, clinic) = await VisitSvc.ClinicVisitsAsync(ctx, code, null, "today", 1, 1, DateTime.UtcNow, ct);
			if (clinic == null) return NotFound();
			Chrome();
			ViewBag.Clinic = clinic;
			ViewBag.Today = total;
			return View();
		}

		[HttpGet("Specialty/{code}/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SpecialtyClinicData(string code, string? q, string? scope, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total, _) = await VisitSvc.ClinicVisitsAsync(ctx, code, q, scope, page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(total, page, pageSize);
			return PartialView("_SpecialtyClinicRows", rows);
		}

		[HttpGet("Consult/{id:int}/Specialty")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SpecialtyVisit(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			if (await _patients.OpenAsync(ctx, (await _consult.GetAsync(ctx, id, ct))?.Encounter.PatientId ?? 0, ctx.EmployeeId, ct) == null) return NotFound();   // reading a visit opens the file: logged
			var m = await VisitSvc.VisitAsync(ctx, _consult, id, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (m == null) return NotFound();
			Chrome();
			// H95: growth, dosing, development, scores and vaccines — shown by what the visit holds
			ViewBag.Widgets = await ActivatorUtilities.CreateInstance<HospitalVisitWidgetsService>(HttpContext.RequestServices).BuildAsync(ctx, m, DateTime.UtcNow, ct);
			ViewBag.Canvas = await CanvasSvc.SummaryAsync(ctx, id, ct);   // H99: the visit's drawings
			return View(m);
		}

		[HttpPost("Consult/{id:int}/Specialty/Template"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SpecialtyVisitTemplate(int id, [FromBody] HospitalVisitTemplateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await VisitSvc.AddTemplateAsync(ctx, id, input.TemplateId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Consult/{id:int}/Specialty/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SpecialtyVisitSave(int id, [FromBody] HospitalVisitFormsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow;
			var saved = await VisitSvc.SaveAsync(ctx, id, input.Forms, ctx.EmployeeId, now, ct);
			if (!saved.Ok) return Result(saved);
			// H106: an alert that cannot wait — the on-call doctor is told now, its template opens on the visit
			var esc = await ActivatorUtilities.CreateInstance<HospitalFieldAlertEscalation>(HttpContext.RequestServices).RunAsync(ctx, VisitSvc, id, ctx.EmployeeId, now, ct);
			return Json(new { ok = true, id, notified = esc.NotifiedAr == null ? null : (UiEnglish ? DisplayName.Of(esc.NotifiedEn, esc.NotifiedAr) : esc.NotifiedAr),
				nobodyOnCall = esc.NobodyOnCall, added = esc.TemplatesAdded.Count });
		}

		[HttpPost("Consult/{id:int}/Specialty/Sign"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SpecialtyVisitSign(int id, [FromBody] HospitalVisitFormsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow;
			var saved = await VisitSvc.SaveAsync(ctx, id, input.Forms, ctx.EmployeeId, now, ct);
			if (!saved.Ok) return Result(saved);
			// H106: the alert is sent, and its template opened, BEFORE the signature — which then needs that template filled
			await ActivatorUtilities.CreateInstance<HospitalFieldAlertEscalation>(HttpContext.RequestServices).RunAsync(ctx, VisitSvc, id, ctx.EmployeeId, now, ct);
			return Result(await VisitSvc.SignAsync(ctx, _consult, id, null, UiEnglish, ctx.EmployeeId, now, ct));
		}

		[HttpPost("Consult/{id:int}/Specialty/OrderSet"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SpecialtyVisitOrderSet(int id, [FromBody] HospitalVisitOrderSetInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await VisitSvc.ApplyOrderSetAsync(ctx, _consult, id, input.SetId, input.ItemIds, ctx.EmployeeId, ct));
		}

		[HttpPost("Consult/{id:int}/Specialty/Order"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SpecialtyVisitOrder(int id, [FromBody] HospitalVisitServiceInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await VisitSvc.OrderServiceAsync(ctx, _consult, id, input.ServiceId, ctx.EmployeeId, ct));
		}

		[HttpPost("Consult/{id:int}/Specialty/Problems"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> SpecialtyVisitProblems(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await VisitSvc.AddProblemsAsync(ctx, _consult, id, UiEnglish, ct));
		}
	}
}
