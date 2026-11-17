using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H152: formulary governance — the pharmacist asks the committee from the dispense card; the committee (manage)
	// decides on the requests screen, searched and paged by cbServerList. Every rule is HospitalFormularyGovernanceService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalFormularyAskInput { public string? Justification { get; set; } }
		public sealed class HospitalFormularyDecideInput { public bool Approve { get; set; } public string? Note { get; set; } public int? AlternativeItemId { get; set; } }

		private HospitalFormularyGovernanceService? _gov;
		private HospitalFormularyGovernanceService GovSvc => _gov ??= ActivatorUtilities.CreateInstance<HospitalFormularyGovernanceService>(HttpContext.RequestServices);

		[HttpPost("Pharmacy/{id:int}/Lines/{lineId:int}/FormularyRequest"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Pharmacy)]
		public async Task<IActionResult> PharmacyFormularyAsk(int id, int lineId, [FromBody] HospitalFormularyAskInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await GovSvc.AskAsync(ctx, id, lineId, input.Justification, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("Pharmacy/FormularyRequests")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FormularyRequests(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Alternatives = await GovSvc.AlternativesAsync(ctx, ct);
			return View(await GovSvc.StatsAsync(ctx, DateTime.UtcNow, ct));
		}

		[HttpGet("Pharmacy/FormularyRequests/Data")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FormularyRequestsData(string? q, string? status, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			Paging(ref page, ref pageSize);
			var (rows, total) = await GovSvc.ListAsync(ctx, q, status, page, pageSize, ct);
			PageHeaders(total, page, pageSize);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Me = ctx.EmployeeId;
			return PartialView("_FormularyRequestRows", rows);
		}

		[HttpPost("Pharmacy/FormularyRequests/{id:int}/Decide"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> FormularyRequestDecide(int id, [FromBody] HospitalFormularyDecideInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await GovSvc.DecideAsync(ctx, id, input.Approve, input.Note, input.AlternativeItemId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
