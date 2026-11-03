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
	// HOSPITAL — H12: estimates and packages. The estimates screen (the builder, the estimates list), one
	// estimate, and the packages setup. Every rule is HospitalEstimateService's; the cover and the approval
	// request are H11's. Built from the request's services like H11, so Program.cs and the shared
	// constructor are untouched.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalEstimateService? _estimatesSvc;
		private HospitalEstimateService EstimatesSvc => _estimatesSvc ??= ActivatorUtilities.CreateInstance<HospitalEstimateService>(HttpContext.RequestServices, InsuranceSvc);

		[HttpGet("Estimates")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Estimates(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Packages = await EstimatesSvc.PackagesAsync(ctx, true, ct);
			ViewBag.RoomTypes = await _db.HospitalRoomTypes.AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId && t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Code).ToListAsync(ct);
			ViewBag.Currency = await _billing.CurrencyLabelAsync(ctx, null, ct);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			return View(await EstimatesSvc.EstimatesAsync(ctx, ct));
		}

		[HttpGet("Estimates/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Estimate(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var v = await EstimatesSvc.EstimateAsync(ctx, id, DateTime.UtcNow, ct);
			if (v == null) return NotFound();
			Chrome();
			ViewBag.Currency = await _billing.CurrencyLabelAsync(ctx, null, ct);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			return View(v);
		}

		[HttpGet("Estimates/Packages")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Packages(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.RoomTypes = await _db.HospitalRoomTypes.AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId && t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Code).ToListAsync(ct);
			ViewBag.Currency = await _billing.CurrencyLabelAsync(ctx, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(await EstimatesSvc.PackagesAsync(ctx, false, ct));
		}

		/// Prices the builder as it is filled in: nothing is stored.
		[HttpPost("Estimates/Quote"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> QuoteEstimate([FromBody] HospitalEstimateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (q, refusal) = await EstimatesSvc.QuoteAsync(ctx, input, DateTime.UtcNow, ct);
			if (refusal != null) return Result(refusal);
			return Json(new
			{
				ok = true, currency = q!.Currency,
				lines = q.Lines.Select((l, i) => new { kind = l.Kind, code = l.Code, text = DisplayName.Of(l.Description, l.DescriptionEn), qty = l.Quantity, amount = l.Amount, covered = q.Payer != null && l.Covered, payer = q.Totals.PayerPerLine[i] }),
				gross = q.Totals.Gross, payerShare = q.Totals.PayerShare, patientShare = q.Totals.PatientShare, deposit = q.Totals.Deposit,
				payer = q.Payer == null ? null : DisplayName.Of(q.Payer.Name, q.Payer.NameEn), policyNo = q.Policy?.PolicyNo,
				uncovered = q.Ineligible == null ? null : _t["inelig:" + q.Ineligible].Value, needsPreauth = q.NeedsPreauth,
			});
		}

		[HttpPost("Estimates/Draft"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> SaveEstimateDraft([FromBody] HospitalEstimateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await EstimatesSvc.SaveDraftAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Estimates/{id:int}/Issue"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> IssueEstimate(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await EstimatesSvc.IssueAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct);
			return Result(r.Ok || r.Args is not { Length: 1 } ? r : r with { Args = new object[] { _t["inelig:" + r.Args[0]].Value } });
		}

		[HttpPost("Estimates/{id:int}/Cancel"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> CancelEstimate(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await EstimatesSvc.CancelAsync(ctx, id, ct));
		}

		[HttpPost("Estimates/Package"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePackage([FromBody] HospitalPackageInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await EstimatesSvc.SavePackageAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
