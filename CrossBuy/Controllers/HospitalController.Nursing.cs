using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H14: the nursing station of a ward. The medication administration record, the inpatient
	// medication orders, nursing tasks and the shift handover. Every rule is HospitalNursingService's.
	// Built from the request's services like H11–H13, so Program.cs and the shared constructor are untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalNursingTaskInput { public int AdmissionId { get; set; } public string? Title { get; set; } public DateTime DueAt { get; set; } }
		public sealed class HospitalHandoverInput { public int WardId { get; set; } public string? Note { get; set; } }

		private HospitalNursingService? _nursingSvc;
		private HospitalNursingService NursingSvc => _nursingSvc ??= ActivatorUtilities.CreateInstance<HospitalNursingService>(HttpContext.RequestServices);

		[HttpGet("Nursing")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Nursing(int? ward, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var wards = await NursingSvc.WardsAsync(ctx, ct);
			ViewBag.Wards = wards;
			int? pick = ward ?? wards.FirstOrDefault()?.Id;
			ViewBag.Staff = await NursingSvc.StaffAsync(ctx, ct);
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			// H270: the order picks the pharmacy's medicine (its item: the pack scan at the bedside matches through it)
			if (ViewBag.CanConsult == true) ViewBag.Formulary = await _pharmacy.FormularyAsync(ctx, ct);
			var board = pick == null ? null : await NursingSvc.BoardAsync(ctx, pick.Value, DateTime.UtcNow, ct);
			// H76: each patient's isolation and the devices in place, beside the name
			if (board != null) ViewBag.Ic = await HospitalInfectionService.BadgesAsync(_db, ctx.CompanyId, board.Patients.Select(p => (p.AdmissionId, p.PatientId)).ToList(), DateTime.UtcNow, ct);
			// H77: the open consultation requests and the fresh answers
			if (board != null) ViewBag.Cr = await HospitalConsultRequestService.BadgesAsync(_db, ctx.CompanyId, board.Patients.Select(p => p.AdmissionId).ToList(), DateTime.UtcNow, ct);
			// H78: today's fluid balance
			if (board != null) ViewBag.Fb = await HospitalFluidService.BadgesAsync(_db, ctx.CompanyId, board.Patients.Select(p => p.AdmissionId).ToList(), DateTime.UtcNow, ct);
			// H79: active care plans, reviews due and suggestions
			if (board != null) ViewBag.Cp = await HospitalCarePlanService.BadgesAsync(_db, ctx.CompanyId, board.Patients.Select(p => p.AdmissionId).ToList(), DateTime.UtcNow, ct);
			return View(board);
		}

		/// H270: what the scanned wristband and pack name, for the line under each box (read only; the dose itself is
		/// checked again when it is recorded). Spaces around a scan are ignored.
		[HttpGet("Nursing/ScanLook")]
		[HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> NursingScanLook(int orderId, string? wristband, string? pack, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var order = await _db.Set<CrossBuy.Models.Context.Hospital.HospitalMedOrder>().AsNoTracking().FirstOrDefaultAsync(o => o.ID == orderId && o.CompanyId == ctx.CompanyId, ct);
			if (order == null) return NotFound(new { ok = false, code = "not_found" });
			object? wrist = null, packOut = null;
			var w = (wristband ?? "").Trim();
			if (w.Length > 0)
			{
				var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.Mrn == w.ToUpper()).Select(x => new { x.ID, x.Mrn, x.Name, x.NameEn }).FirstOrDefaultAsync(ct);
				wrist = p == null ? new { ok = false, text = _t["No patient file has this wristband code"].Value }
					: new { ok = p.ID == order.PatientId, text = string.Format(_t[p.ID == order.PatientId ? "{0} · {1} — this order's patient" : "{0} · {1} — NOT this order's patient"].Value, DisplayName.Of(p.Name, p.NameEn), p.Mrn) };
			}
			var k = (pack ?? "").Trim();
			if (k.Length > 0)
			{
				var itemId = await HospitalBedsideScanLookup.ItemOfAsync(_db, ctx, k, ct);
				var item = itemId == null ? null : await _db.Items.AsNoTracking().Where(i => i.ID == itemId).Select(i => new { i.Name, i.NameEn }).FirstOrDefaultAsync(ct);
				packOut = item == null ? new { ok = false, text = _t["No medicine of the drug list has this code"].Value }
					: new { ok = itemId == order.ItemId, text = string.Format(_t[itemId == order.ItemId ? "{0} — the ordered medicine" : "{0} — NOT the ordered medicine"].Value, DisplayName.Of(item.Name, item.NameEn)) };
			}
			return Json(new { ok = true, wristband = wrist, pack = packOut });
		}

		[HttpPost("Nursing/Order"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> NursingOrder([FromBody] HospitalMedOrderInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			// H270: the ward orders from the pharmacy's list; a drug outside it (the patient's own medicine) is an explicit
			// choice with its reason, never typed words the search did not find
			if (input.ItemId is int pick && !(await _pharmacy.MedicineItemIdsAsync(ctx, ct)).Contains(pick)) return Result(HospitalResult.Fail("Choose a medicine of the pharmacy's list"));
			if (input.ItemId == null && string.IsNullOrWhiteSpace(input.Note)) return Result(HospitalResult.Fail("A drug outside the pharmacy's list needs its reason"));
			var alerts = await DrugSafetySvc.CheckOrderAsync(ctx, input.AdmissionId, input.Drug ?? "", input.Dose, input.Route, input.Hours, input.AsNeeded, ct);
			bool stops = alerts.Any(a => a.Blocks);
			if (stops && string.IsNullOrWhiteSpace(input.OverrideReason))
				return Json(new { ok = false, code = "clinical_alert", error = _t["This order meets a safety alert"].Value, alerts = alerts.Select(AlertJson) });
			var placed = await NursingSvc.AddOrderAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!placed.Ok || alerts.Count == 0) return Result(placed);
			if (stops)
			{
				var patientId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(_db.HospitalAdmissions.Where(a => a.ID == input.AdmissionId && a.CompanyId == ctx.CompanyId).Select(a => a.PatientId), ct);
				await DrugSafetySvc.RecordOverridesAsync(ctx, patientId, input.AdmissionId, "MedOrder", placed.Id!.Value, alerts, input.OverrideReason!, ctx.EmployeeId, DateTime.UtcNow, ct);
			}
			return Json(new { ok = true, id = placed.Id, warnings = alerts.Where(a => !a.Blocks).Select(AlertJson) });
		}

		[HttpPost("Nursing/Order/{id:int}/Stop"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> NursingStopOrder(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await NursingSvc.StopOrderAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Nursing/Record"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> NursingRecord([FromBody] HospitalMarInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await NursingSvc.RecordAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Nursing/Task"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> NursingTask([FromBody] HospitalNursingTaskInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			// the form's time is the ward's clock
			return Result(await NursingSvc.AddTaskAsync(ctx, input.AdmissionId, input.Title, HospitalNursingService.WardToUtc(input.DueAt), ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Nursing/Task/{id:int}/Done"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> NursingTaskDone(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await NursingSvc.DoneTaskAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Nursing/Handover"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> NursingHandover([FromBody] HospitalHandoverInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await NursingSvc.SaveHandoverAsync(ctx, input.WardId, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
