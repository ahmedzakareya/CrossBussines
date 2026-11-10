using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H86: the labour ward (labours in progress, worrying first, and the women admitted who can be started on
	// a partograph), one labour's partograph with its chart, an observation, and closing the labour with its outcome.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalLabourService? _labourSvc;
		private HospitalLabourService LabourSvc => _labourSvc ??= ActivatorUtilities.CreateInstance<HospitalLabourService>(HttpContext.RequestServices);

		[HttpGet("Labour")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> LabourWard(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (rows, ready) = await LabourSvc.ActiveAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanWard = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			var busy = rows.Select(r => r.Labour.AdmissionId).ToList();
			ViewBag.Candidates = (await (from a in _db.HospitalAdmissions.AsNoTracking()
										 join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
										 where a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active && p.Sex == HospitalPatientSex.Female && !busy.Contains(a.ID)
										 orderby p.Name
										 select new { a.ID, p.Name, p.NameEn, p.Mrn }).ToListAsync(ct))
				.Select(x => (x.ID, DisplayName.Of(x.Name, x.NameEn ?? x.Name) + " — " + x.Mrn)).ToList();
			return View(rows);
		}

		[HttpGet("Labour/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Partograph(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var page = await LabourSvc.PageAsync(ctx, id, ct);
			if (page == null) return NotFound();
			Chrome();
			ViewBag.CanWard = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(page);
		}

		public sealed class HospitalLabourStartInput { public DateTime? StartedAt { get; set; } }

		[HttpPost("Labour/Start/{admissionId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> StartLabour(int admissionId, [FromBody] HospitalLabourStartInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await LabourSvc.StartAsync(ctx, admissionId, input?.StartedAt, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Labour/Observe"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ObserveLabour([FromBody] HospitalLabourObservationInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await LabourSvc.ObserveAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Labour/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> CloseLabour(int id, [FromBody] HospitalLabourCloseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await LabourSvc.CloseAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
