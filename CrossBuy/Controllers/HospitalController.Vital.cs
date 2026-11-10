using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H58/H59: births and deaths (the prototype's "medical records: births, deaths"). One register screen.
	// A birth is registered by the ward (`admit`); voided with a reason by a doctor or the administrator. Each prints
	// only through Report Studio.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalBirthService? _birthSvc;
		private HospitalBirthService BirthSvc => _birthSvc ??= ActivatorUtilities.CreateInstance<HospitalBirthService>(HttpContext.RequestServices, _patients);

		[HttpGet("Vital")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Vital(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (births, ready) = await BirthSvc.ListAsync(ctx, null, ct);
			ViewBag.BirthsReady = ready;
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.CanVoid = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) || await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			var (deaths, deathsReady) = await DeathSvc.ListAsync(ctx, ct);   // H59
			ViewBag.Deaths = deaths; ViewBag.DeathsReady = deathsReady;
			ViewBag.CanCertify = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(births);
		}

		[HttpPost("Vital/Births"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Admit)]
		public async Task<IActionResult> RegisterBirth([FromBody] HospitalBirthInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BirthSvc.RegisterAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		// ---------------- H59: deaths ----------------
		private HospitalDeathService? _deathSvc;
		private HospitalDeathService DeathSvc => _deathSvc ??= ActivatorUtilities.CreateInstance<HospitalDeathService>(HttpContext.RequestServices, _admissions);

		[HttpPost("Vital/Deaths"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> CertifyDeath([FromBody] HospitalDeathInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await DeathSvc.CertifyAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, id = r.Id, stay = r.Args?.FirstOrDefault() as string });   // "discharged" / "stay-open" / null
		}

		[HttpPost("Vital/Deaths/{id:int}/Void"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> VoidDeath(int id, [FromBody] HospitalVoidInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DeathSvc.VoidAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Vital/Births/{id:int}/Void"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> VoidBirth(int id, [FromBody] HospitalVoidInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BirthSvc.VoidAsync(ctx, id, input?.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
