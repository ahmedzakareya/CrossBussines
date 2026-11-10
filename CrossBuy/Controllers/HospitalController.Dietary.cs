using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H43: clinical dietary (the prototype's #/dietary). A ward's patients for one round: the diet, the
	// allergy, the kitchen's state; sending the round and marking it served. Every rule is HospitalDietaryService's;
	// the door is `read`, the service decides who may do what. Built from the request's services.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalDietaryService? _dietSvc;
		private HospitalDietaryService DietSvc => _dietSvc ??= ActivatorUtilities.CreateInstance<HospitalDietaryService>(HttpContext.RequestServices);

		public sealed class HospitalDietInput { public int AdmissionId { get; set; } public string Diet { get; set; } = ""; public string? Note { get; set; } }
		public sealed class HospitalRoundInput { public int WardId { get; set; } public string Round { get; set; } = ""; }

		[HttpGet("Dietary")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Dietary(int? ward, string? round, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var wards = await NursingSvc.WardsAsync(ctx, ct);
			ViewBag.Wards = wards;
			int? pick = wards.Any(w => w.Id == ward) ? ward : wards.FirstOrDefault()?.Id;
			var (board, ready) = pick is int w ? await DietSvc.BoardAsync(ctx, w, round, DateTime.UtcNow, ct) : (null, true);
			ViewBag.Ready = ready;
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.CanDiet = ViewBag.CanAdmit == true || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(board);
		}

		[HttpPost("Dietary/Diet"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DietarySetDiet([FromBody] HospitalDietInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DietSvc.SetDietAsync(ctx, input.AdmissionId, input.Diet, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dietary/Send"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DietarySend([FromBody] HospitalRoundInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DietSvc.SendRoundAsync(ctx, input.WardId, input.Round, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Dietary/Served"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> DietaryServed([FromBody] HospitalRoundInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await DietSvc.ServeRoundAsync(ctx, input.WardId, input.Round, DateTime.UtcNow, ct));
		}
	}
}
