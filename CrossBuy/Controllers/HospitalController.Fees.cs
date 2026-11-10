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
	// HOSPITAL — H19: doctors' fee statements. The rules live on the practitioner's page; the statement is
	// generated on demand, approved by the medical director then finance. Every rule is HospitalFeeService's.
	// Built from the request's services like H11–H18, so Program.cs and the shared constructor are untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalFeeGenerateInput { public int PractitionerId { get; set; } public string Period { get; set; } = ""; }

		private HospitalFeeService? _feeSvc;
		private HospitalFeeService FeeSvc => _feeSvc ??= ActivatorUtilities.CreateInstance<HospitalFeeService>(HttpContext.RequestServices);

		[HttpGet("Fees")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Fees(int? practitioner, string? period, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			// the doctors with fee rules (the ones a statement can exist for); the previous month by default
			var withRules = await _db.Set<HospitalFeeRule>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId).Select(r => r.PractitionerId).Distinct().ToListAsync(ct);
			var doctors = (await DentalSvc.DentistsAsync(ctx, ct)).Where(d => withRules.Contains(d.Id)).ToList();
			ViewBag.Doctors = doctors;
			var p = HospitalFeeRules.IsPeriod(period) ? period! : HospitalFeeRules.Period(DateTime.Now.AddMonths(-1));
			ViewBag.Periods = Enumerable.Range(0, 12).Select(i => HospitalFeeRules.Period(DateTime.Now.AddMonths(-i))).ToList();
			ViewBag.Accounts = await PostingAccountsAsync(ctx, ct);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			int? id = practitioner ?? doctors.FirstOrDefault()?.Id;
			if (id is not int pid) return View(null);
			return View(await FeeSvc.StatementAsync(ctx, pid, p, ct));
		}

		[HttpPost("Fees/Generate"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FeesGenerate([FromBody] HospitalFeeGenerateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FeeSvc.GenerateAsync(ctx, input.PractitionerId, input.Period, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// `read` at the door; the service asks `manage` (the medical director) or `bill` (finance) for the next step.
		[HttpPost("Fees/{id:int}/Approve"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> FeesApprove(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FeeSvc.ApproveAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Fees/Rule"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> FeesSaveRule([FromBody] HospitalFeeRuleInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FeeSvc.SaveRuleAsync(ctx, input, ct));
		}

		[HttpPost("Fees/Rule/{id:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> FeesRemoveRule(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FeeSvc.RemoveRuleAsync(ctx, id, ct));
		}

		[HttpPost("Fees/Settings"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> FeesSaveSettings([FromBody] HospitalFeeSettingsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FeeSvc.SaveSettingsAsync(ctx, input, ct));
		}
	}
}
