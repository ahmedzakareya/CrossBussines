using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H171: the clinical rule register. Every rule the software applies to a patient, its guideline and
	// version, what it covers and what it does not, the clinic's values, and who approved which version. Reading is
	// `read`; approving is `manage` (door and service).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalClinicalRuleService? _ruleSvc;
		private HospitalClinicalRuleService RuleSvc => _ruleSvc ??= ActivatorUtilities.CreateInstance<HospitalClinicalRuleService>(HttpContext.RequestServices);

		[HttpGet("ClinicalRules")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ClinicalRules(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			try { return View(await RuleSvc.RegisterAsync(ctx, ct)); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
			{
				ViewBag.NotReady = true;
				return View(HospitalClinicalRuleCatalog.All.Select(r => new HospitalRuleRow(r, null, false, HospitalClinicalRuleCatalog.Values(r, null), null)).ToList());
			}
		}

		[HttpPost("ClinicalRules/Approve"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> ClinicalRuleApprove([FromBody] HospitalRuleApproveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RuleSvc.ApproveAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
