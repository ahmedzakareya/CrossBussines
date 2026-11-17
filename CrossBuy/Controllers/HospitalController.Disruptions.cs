using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H161: clinic disruptions — the list, the declaration window, one disruption (its patients and their plans,
	// apply and notify, the calls), and the desk's "to call" tab. Every rule is HospitalClinicDisruptionService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalDisruptionPlanInput { public string? Plan { get; set; } }
		public sealed class HospitalDisruptionCallInput { public bool Reached { get; set; } public string? Note { get; set; } }

		private HospitalClinicDisruptionService? _disruptions;
		private HospitalClinicDisruptionService DisruptionSvc => _disruptions ??= ActivatorUtilities.CreateInstance<HospitalClinicDisruptionService>(HttpContext.RequestServices, DeskWorkSvc, _outpatient);

		[HospitalReceptionAllowed]   // H161
		[HttpGet("ClinicDisruptions")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ClinicDisruptions(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.ToCall = (await DisruptionSvc.ToCallAsync(ctx, ct)).Count;
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			return View();
		}

		[HospitalReceptionAllowed]   // H161
		[HttpGet("ClinicDisruptions/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ClinicDisruptionsData(string? q, string? open, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total) = await DisruptionSvc.ListAsync(ctx, q, open != "all", page, pageSize, ct);
			PageHeaders(total, page, pageSize);
			return PartialView("_DisruptionRows", rows);
		}

		[HospitalReceptionAllowed]   // H161: the declaration window
		[HttpGet("ClinicDisruptions/New")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> ClinicDisruptionNew(int? doctor, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var (today, minute, _) = DeskClock(DateTime.UtcNow);
			ViewBag.Today = today; ViewBag.Minute = minute; ViewBag.Doctor = doctor;
			ViewBag.Doctors = await _db.HospitalPractitioners.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.IsActive).OrderBy(x => x.Name).ToListAsync(ct);
			return PartialView("_DisruptionNew");
		}

		[HospitalReceptionAllowed]   // H161
		[HttpPost("ClinicDisruptions"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> ClinicDisruptionDeclare([FromBody] HospitalDisruptionInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow; var (_, _, local) = DeskClock(now);
			var r = await DisruptionSvc.DeclareAsync(ctx, input, ctx.EmployeeId, local, now, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, id = r.Id, url = Url.Action("ClinicDisruption", new { id = r.Id }) });
		}

		[HospitalReceptionAllowed]   // H161
		[HttpGet("ClinicDisruptions/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ClinicDisruption(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var v = await DisruptionSvc.ViewAsync(ctx, id, ct);
			if (v == null) return NotFound();
			Chrome();
			ViewBag.CanBook = await _access.CanAsync(ctx, HospitalActions.Book, null, ct);
			return View(v);
		}

		[HospitalReceptionAllowed]   // H161
		[HttpPost("ClinicDisruptions/Rows/{id:int}/Plan"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> ClinicDisruptionPlan(int id, [FromBody] HospitalDisruptionPlanInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DisruptionSvc.SetPlanAsync(ctx, id, input?.Plan, ct));
		}

		[HospitalReceptionAllowed]   // H161
		[HttpPost("ClinicDisruptions/{id:int}/Apply"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> ClinicDisruptionApply(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var now = DateTime.UtcNow; var (_, _, local) = DeskClock(now);
			var (r, applied, failed) = await DisruptionSvc.ApplyAsync(ctx, id, ctx.EmployeeId, local, now, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, applied, failed });
		}

		[HospitalReceptionAllowed]   // H161
		[HttpPost("ClinicDisruptions/Rows/{id:int}/Call"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> ClinicDisruptionCall(int id, [FromBody] HospitalDisruptionCallInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DisruptionSvc.CallAsync(ctx, id, input.Reached, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H161
		[HttpPost("ClinicDisruptions/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> ClinicDisruptionClose(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DisruptionSvc.CloseAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H161: the desk's tab — patients a disruption could not reach yet
		[HttpGet("FrontDesk/ToCall")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FrontDeskToCall(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			return PartialView("_DeskToCall", await DisruptionSvc.ToCallAsync(ctx, ct));
		}
	}
}
