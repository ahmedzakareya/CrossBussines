using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H209 — notifiable diseases: the register of cases to report to the Ministry (read with `clinical-read`),
	// closing a case (reported with its reference, or dismissed with its reason), and the list (an administrator's).
	public partial class HospitalController
	{
		public sealed class HospitalDiseaseCloseInput { public string? To { get; set; } public string? Text { get; set; } }

		private HospitalNotifiableService? _notifSvc;
		private HospitalNotifiableService NotifSvc => _notifSvc ??= ActivatorUtilities.CreateInstance<HospitalNotifiableService>(HttpContext.RequestServices);

		[HttpGet("Notifiable")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Notifiable(string? q, string? status, bool overdue = false, int? patient = null, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			if (!string.IsNullOrEmpty(status) && !HospitalDiseaseReportStatus.All.Contains(status)) status = null;
			var (rows, total, due, late, ready) = await NotifSvc.ListAsync(ctx, q, status, overdue, patient, page, size, DateTime.UtcNow, ct);
			ViewBag.Q = q; ViewBag.Status = status; ViewBag.Overdue = overdue; ViewBag.Patient = patient;
			ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size); ViewBag.Total = total; ViewBag.Due = due; ViewBag.Late = late; ViewBag.Ready = ready;
			ViewBag.Diseases = ready ? await NotifSvc.DiseasesAsync(ctx, ct) : Array.Empty<HospitalNotifiableDisease>();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(rows);
		}

		[HttpPost("Notifiable/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> NotifiableClose(int id, [FromBody] HospitalDiseaseCloseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await NotifSvc.CloseAsync(ctx, id, input.To ?? "", input.Text, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Notifiable/Diseases"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> NotifiableDiseaseSave(int? id, [FromBody] HospitalNotifiableInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await NotifSvc.SaveDiseaseAsync(ctx, id, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Notifiable/Diseases/{id:int}/Toggle"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> NotifiableDiseaseToggle(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await NotifSvc.ToggleDiseaseAsync(ctx, id, ct));
		}
	}
}
