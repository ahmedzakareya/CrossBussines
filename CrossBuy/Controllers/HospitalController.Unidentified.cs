using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H92: the unidentified patient. The list of temporary identities (the unsettled first; searched,
	// filtered, paged), registering one (with its emergency visit), and identifying it (rename or merge).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalUnidentifiedService? _unknownSvc;
		private HospitalUnidentifiedService UnknownSvc => _unknownSvc ??= ActivatorUtilities.CreateInstance<HospitalUnidentifiedService>(HttpContext.RequestServices);

		[HttpGet("Unidentified")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Unidentified(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (_, _, ready, open) = await UnknownSvc.ListAsync(ctx, null, null, 1, 1, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.Open = open;
			ViewBag.CanRegister = await UnknownSvc.CanRegisterAsync(ctx, ct);
			ViewBag.CanMerge = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View();
		}

		[HttpGet("Unidentified/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> UnidentifiedData(string? q, string? filter, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total, _, _) = await UnknownSvc.ListAsync(ctx, q, filter, page, pageSize, DateTime.UtcNow, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.CanRegister = await UnknownSvc.CanRegisterAsync(ctx, ct);
			return PartialView("_UnidentifiedRows", rows);
		}

		[HttpPost("Unidentified"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> RegisterUnidentified([FromBody] HospitalUnidentifiedInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await UnknownSvc.RegisterAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			var erError = r.Args?[1] as string;
			return Json(new { ok = true, id = r.Id, erVisit = r.Args?[0], erError = erError == null ? null : _t[erError].Value });
		}

		[HttpPost("Unidentified/{id:int}/Identify"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> IdentifyUnidentified(int id, [FromBody] HospitalIdentifyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await UnknownSvc.IdentifyAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
