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
	// HOSPITAL — H13: the emergency department board. Arrival, triage, bay, doctor, the lanes and the
	// outcome. Every rule is HospitalEmergencyService's. Built from the request's services like H11/H12,
	// so Program.cs and the shared constructor are untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalErMoveInput { public string Stage { get; set; } = ""; public int? PractitionerId { get; set; } }
		public sealed class HospitalErPlaceInput { public int? BayId { get; set; } }
		public sealed class HospitalErCloseInput { public string Outcome { get; set; } = ""; public string? Note { get; set; } }
		public sealed class HospitalErIdentifyInput { public int PatientId { get; set; } }

		private HospitalEmergencyService? _erSvc;
		private HospitalEmergencyService ErSvc => _erSvc ??= ActivatorUtilities.CreateInstance<HospitalEmergencyService>(HttpContext.RequestServices);

		[HttpGet("Emergency")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Emergency(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Stats = await ErSvc.StatsAsync(ctx, DateTime.Today.ToUniversalTime(), DateTime.UtcNow, ct);
			ViewBag.Bays = await ErSvc.BaysAsync(ctx, false, ct);   // all of them: the setup lists the closed ones too
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			var doctors = await _db.HospitalPractitioners.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.Name).Select(d => new { d.ID, d.Name, d.NameEn, d.EmployeeId }).ToListAsync(ct);
			// a doctor is chosen by face: the picker shows each one's photo (the owner's rule)
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, doctors.Where(d => d.EmployeeId != null).Select(d => d.EmployeeId!.Value), ct);
			ViewBag.Doctors = doctors.Select(d => (d.ID, DisplayName.Of(d.Name, d.NameEn), d.EmployeeId is int e ? photos.GetValueOrDefault(e) : null, d.NameEn)).ToList();
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.CanRegister = await _access.CanAsync(ctx, HospitalActions.Register, null, ct);
			return View(await ErSvc.BoardAsync(ctx, DateTime.UtcNow, null, ct));
		}

		/// A patient at the door. The arrival window picks a known patient with the accommodation window's
		/// picker (SearchPatients, on `admit` like the desk itself), or describes an unknown one.
		[HttpPost("Emergency/Arrive"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErArrive([FromBody] HospitalErArrivalInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErSvc.ArriveAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Emergency/{id:int}/Triage"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErTriage(int id, [FromBody] HospitalErTriageInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErSvc.TriageAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Emergency/{id:int}/Bay"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErBay(int id, [FromBody] HospitalErPlaceInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErSvc.BayAsync(ctx, id, input?.BayId, ct));
		}

		[HttpPost("Emergency/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> ErMove(int id, [FromBody] HospitalErMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErSvc.MoveAsync(ctx, id, input.Stage, input.PractitionerId, DateTime.UtcNow, ct));
		}

		/// `read` at the door; the service asks `consult` for a doctor's outcome and `admit` for "left without being seen".
		[HttpPost("Emergency/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ErClose(int id, [FromBody] HospitalErCloseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErSvc.CloseAsync(ctx, id, input.Outcome, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// The department's bays (setup).
		[HttpPost("Emergency/Bay"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveErBay([FromBody] HospitalErBayInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErSvc.SaveBayAsync(ctx, input, ct));
		}

		[HttpPost("Emergency/{id:int}/Identify"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> ErIdentify(int id, [FromBody] HospitalErIdentifyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ErSvc.IdentifyAsync(ctx, id, input.PatientId, ct));
		}
	}
}
