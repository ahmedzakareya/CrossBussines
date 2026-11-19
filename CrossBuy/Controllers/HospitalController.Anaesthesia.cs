using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H202 — an operation's anaesthesia record: read with `clinical-read`; the assessment, the record's entries
	// and the recovery room with `consult`. Times are the theatre's (local), as the theatre's own moves are.
	public partial class HospitalController
	{
		private HospitalAnaesthesiaService? _anaesSvc;
		private HospitalAnaesthesiaService AnaesSvc => _anaesSvc ??= ActivatorUtilities.CreateInstance<HospitalAnaesthesiaService>(HttpContext.RequestServices);

		[HttpGet("Theatre/{id:int}/Anaesthesia")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Anaesthesia(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			HospitalAnaesthesiaView? v;
			try { v = await AnaesSvc.ViewAsync(ctx, id, ct); }
			catch (Microsoft.Data.SqlClient.SqlException) { v = null; ViewBag.NotInstalled = true; }
			if (v == null && ViewBag.NotInstalled != true) return NotFound();
			Chrome();
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.Now = DateTime.Now;
			return View(v);
		}

		[HttpPost("Theatre/{id:int}/Anaesthesia/Assess"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> AnaesthesiaAssess(int id, [FromBody] HospitalAnaesthesiaInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await AnaesSvc.AssessAsync(ctx, id, input, ctx.EmployeeId, DateTime.Now, ct));
		}

		[HttpPost("Theatre/{id:int}/Anaesthesia/Entry"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> AnaesthesiaEntry(int id, [FromBody] HospitalAnaesthesiaEntryInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await AnaesSvc.AddEntryAsync(ctx, id, input, ctx.EmployeeId, DateTime.Now, ct));
		}

		[HttpPost("Theatre/{id:int}/Anaesthesia/PacuIn"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> AnaesthesiaPacuIn(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await AnaesSvc.PacuInAsync(ctx, id, DateTime.Now, ct));
		}

		[HttpPost("Theatre/{id:int}/Anaesthesia/PacuOut"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> AnaesthesiaPacuOut(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await AnaesSvc.PacuOutAsync(ctx, id, ctx.EmployeeId, DateTime.Now, ct));
		}
	}
}
