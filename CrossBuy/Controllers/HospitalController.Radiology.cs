using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H9: radiology. The worklist and the machines, the scan steps, the report and its
	// signature, and the exam/room setup. Every rule is HospitalRadiologyService's; a critical report is
	// told through the same critical-value page and alerts as the lab (source Radiology).
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalRadArriveInput { public int? RoomId { get; set; } }
		public sealed class HospitalRadMaintenanceInput { public bool On { get; set; } public string? Note { get; set; } }

		[HttpGet("Radiology")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Radiology(string? status, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			await _rad.SyncOrdersAsync(ctx, ct);
			Chrome();
			ViewBag.Status = HospitalRadStatus.All.Contains(status ?? "") ? status : null;
			ViewBag.Stats = await _rad.StatsAsync(ctx, DateTime.Today.ToUniversalTime(), ct);
			ViewBag.CanRad = await _access.CanAsync(ctx, HospitalActions.Rad, null, ct);
			ViewBag.CanReport = await _access.CanAsync(ctx, HospitalActions.RadReport, null, ct);
			var open = await _rad.WorklistAsync(ctx, null, null, ct);
			ViewBag.Rooms = await _rad.RoomBoardAsync(ctx, open, ct);
			ViewBag.Critical = open.Where(r => r.CriticalOpen).ToList();
			return View(ViewBag.Status == null ? open : await _rad.WorklistAsync(ctx, ViewBag.Status as string, null, ct));
		}

		[HttpGet("Radiology/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RadOrder(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var v = await _rad.OrderAsync(ctx, id, ct);
			if (v == null) return NotFound();
			Chrome();
			ViewBag.CanRad = await _access.CanAsync(ctx, HospitalActions.Rad, null, ct);
			ViewBag.CanReport = await _access.CanAsync(ctx, HospitalActions.RadReport, null, ct);
			ViewBag.Previous = (await _rad.SignedForPatientAsync(ctx, v.Patient.ID, 6, ct)).Where(r => r.ID != id).Take(5).ToList();
			// H27: whether the patient may see it on the portal, and who may let them
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.PortalRelease = await PortalRecordsSvc.ReleaseOfAsync(ctx, HospitalPortalResultSource.Radiology, id, ct);
			// H31: the exam's pictures, and who may attach them
			ViewBag.Images = (await RadImageSvc.OfOrdersAsync(ctx, new[] { id }, ct)).GetValueOrDefault(id) ?? Array.Empty<HospitalRadImageRow>();
			ViewBag.PacsLink = HospitalPacsRules.ViewerLink((await PacsSvc.GetAsync(ctx, ct)).ViewerUrl, v.Order.AccessionNo, v.Patient.Mrn);   // H199
			ViewBag.RadQuality = await RadQSvc.PanelAsync(ctx, id, v.Patient.ID, DateTime.UtcNow, ct);   // H243
			ViewBag.RadTemplates = await RadRepSvc.ForModalityAsync(ctx, v.Order.Modality, ct);   // H259
			ViewBag.RadTemplateUsed = await RadRepSvc.UsedTemplateAsync(ctx, id, ct);
			return View(v);
		}

		private async Task<IActionResult> RadStep(int id, Func<BusinessContext, Task<HospitalResult>> step, CancellationToken ct)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await step(ctx));
		}

		[HttpPost("Radiology/{id:int}/Arrive"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Rad)]
		public Task<IActionResult> RadArrive(int id, [FromBody] HospitalRadArriveInput? input, CancellationToken ct = default)
			=> RadStep(id, ctx => _rad.ArriveAsync(ctx, id, input?.RoomId, ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpPost("Radiology/{id:int}/Start"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Rad)]
		public Task<IActionResult> RadStart(int id, CancellationToken ct = default)
			=> RadStep(id, ctx => _rad.StartScanAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpPost("Radiology/{id:int}/End"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Rad)]
		public Task<IActionResult> RadEnd(int id, CancellationToken ct = default)
			=> RadStep(id, ctx => _rad.EndScanAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpPost("Radiology/{id:int}/Priority"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Rad)]
		public Task<IActionResult> RadPriority(int id, [FromBody] HospitalLabPriorityInput input, CancellationToken ct = default)
			=> RadStep(id, ctx => _rad.SetPriorityAsync(ctx, id, input?.Priority ?? "", ct), ct);

		[HttpPost("Radiology/{id:int}/Report"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.RadReport)]
		public Task<IActionResult> RadReport(int id, [FromBody] HospitalRadReportInput input, CancellationToken ct = default)
			=> RadStep(id, ctx => _rad.SaveReportAsync(ctx, id, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpPost("Radiology/{id:int}/Sign"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.RadReport)]
		public Task<IActionResult> RadSign(int id, [FromBody] HospitalRadReportInput input, CancellationToken ct = default)
			=> RadStep(id, async ctx =>
			{
				var r = await _rad.SignAsync(ctx, id, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct);
				if (r.Ok) await _labAlerts.NotifyAbnormalAsync(ctx, HospitalAlertSource.Radiology, id, ctx.EmployeeId, DateTime.UtcNow, ct);   // an urgent abnormal report tells its doctor
				return r;
			}, ct);

		/// H34: English added to a signed report (the Arabic stays as signed).
		[HttpPost("Radiology/{id:int}/Translate"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.RadReport)]
		public Task<IActionResult> RadTranslate(int id, [FromBody] HospitalRadTranslationInput input, CancellationToken ct = default)
			=> RadStep(id, ctx => _rad.TranslateAsync(ctx, id, input?.FindingsEn, input?.ImpressionEn, ctx.EmployeeId, DateTime.UtcNow, ct), ct);

		[HttpGet("Radiology/{id:int}/Critical")]
		[HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> RadCritical(int id, CancellationToken ct = default) => CriticalPage(HospitalAlertSource.Radiology, id, ct);

		[HttpPost("Radiology/{id:int}/Critical/Send"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Rad)]
		public async Task<IActionResult> RadCriticalSend(int id, [FromBody] HospitalLabSendInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _labAlerts.SendAsync(ctx, HospitalAlertSource.Radiology, id, input?.PractitionerId ?? 0, input?.ViaSms == true, input?.PhoneNote, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		// ---------------- setup ----------------

		[HttpGet("RadiologySetup")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RadiologySetup(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.CanRad = await _access.CanAsync(ctx, HospitalActions.Rad, null, ct);
			ViewBag.Rooms = await _rad.RoomsAsync(ctx, ct);
			ViewBag.Pacs = await PacsSvc.GetAsync(ctx, ct);   // H199
			return View(await _rad.ExamsAsync(ctx, ct));
		}

		[HttpPost("RadiologySetup/Exam/{serviceId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public Task<IActionResult> SaveRadExam(int serviceId, [FromBody] HospitalRadExamInput input, CancellationToken ct = default)
			=> RadStep(serviceId, ctx => _rad.SaveExamAsync(ctx, serviceId, input ?? new(), ct), ct);

		[HttpPost("RadiologySetup/Room"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public Task<IActionResult> SaveRadRoom([FromBody] HospitalRadRoomInput input, CancellationToken ct = default)
			=> RadStep(0, ctx => _rad.SaveRoomAsync(ctx, input ?? new(), ct), ct);

		[HttpPost("RadiologySetup/Room/{id:int}/Maintenance"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Rad)]
		public Task<IActionResult> RadRoomMaintenance(int id, [FromBody] HospitalRadMaintenanceInput input, CancellationToken ct = default)
			=> RadStep(id, ctx => _rad.SetMaintenanceAsync(ctx, id, input?.On == true, input?.Note, ct), ct);

		[HttpPost("RadiologySetup/SeedStandard"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public Task<IActionResult> SeedRadStandard(CancellationToken ct = default)
			=> RadStep(0, ctx => _rad.SeedStandardAsync(ctx, ct), ct);
	}
}
