using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H41: the blood bank (the prototype's #/bloodbank). The shelf per group, the transfusion requests and
	// their cross-match, receiving and discarding bags. Every rule is HospitalBloodBankService's; the door is `read`,
	// the service decides who may do what. Built from the request's services, so Program.cs is untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalBloodBankService? _bloodSvc;
		private HospitalBloodBankService BloodSvc => _bloodSvc ??= ActivatorUtilities.CreateInstance<HospitalBloodBankService>(HttpContext.RequestServices);

		public sealed class HospitalBloodReceiveInput
		{
			public List<string> UnitNos { get; set; } = new(); public string BloodGroup { get; set; } = ""; public string Product { get; set; } = "";
			public DateTime ExpiresOn { get; set; } public string? Note { get; set; }
		}
		public sealed class HospitalBloodRequestInput { public int PatientId { get; set; } public string Product { get; set; } = ""; public int Units { get; set; } public string? Note { get; set; } }
		public sealed class HospitalBloodMoveInput { public string To { get; set; } = ""; public string? Reason { get; set; } }
		public sealed class HospitalBloodDiscardInput { public string? Reason { get; set; } }

		[HttpGet("BloodBank")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> BloodBank(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (board, ready) = await BloodSvc.BoardAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Ready = ready;
			ViewBag.CanLab = await _access.CanAsync(ctx, HospitalActions.Lab, null, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(board);
		}

		[HttpPost("BloodBank/Units"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> BloodReceive([FromBody] HospitalBloodReceiveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BloodSvc.ReceiveAsync(ctx, input.UnitNos, input.BloodGroup, input.Product, input.ExpiresOn, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("BloodBank/Units/{id:int}/Discard"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> BloodDiscard(int id, [FromBody] HospitalBloodDiscardInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BloodSvc.DiscardAsync(ctx, id, input?.Reason, DateTime.UtcNow, ct));
		}

		[HttpPost("BloodBank/Requests"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> BloodRequest([FromBody] HospitalBloodRequestInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BloodSvc.RequestAsync(ctx, input.PatientId, input.Product, input.Units, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("BloodBank/Requests/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> BloodMove(int id, [FromBody] HospitalBloodMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await BloodSvc.MoveAsync(ctx, id, input.To, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
