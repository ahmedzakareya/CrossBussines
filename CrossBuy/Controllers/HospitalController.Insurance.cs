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
	// HOSPITAL — H11: insurance and claims. The approvals and claims screen, a claim batch, and the payers'
	// contracts with the patients' policies. Every rule is HospitalInsuranceService's.
	//
	// The service is built from the request's own services (ActivatorUtilities), not injected through the
	// constructor: the unit adds no registration to Program.cs and leaves the shared constructor alone.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalPreauthRequestInput { public int PatientId { get; set; } public int ServiceId { get; set; } public decimal? Amount { get; set; } }
		public sealed class HospitalPreauthDecisionInput { public bool Approve { get; set; } public string? ApprovalRef { get; set; } public string? Reason { get; set; } }
		public sealed class HospitalClaimBuildInput { public int PayerId { get; set; } public string Period { get; set; } = ""; }
		public sealed class HospitalClaimResponseInput { public bool Accept { get; set; } public decimal? AcceptedAmount { get; set; } public string? Code { get; set; } public string? Reason { get; set; } }
		public sealed class HospitalClaimResubmitInput { public string? Justification { get; set; } }

		private HospitalInsuranceService? _insuranceSvc;
		private HospitalInsuranceService InsuranceSvc => _insuranceSvc ??= ActivatorUtilities.CreateInstance<HospitalInsuranceService>(HttpContext.RequestServices);

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Insurance")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Insurance(string? tab, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Tab = tab is "cl" or "rj" ? tab : "pa";
			ViewBag.Stats = await InsuranceSvc.StatsAsync(ctx, DateTime.UtcNow, ct);
			ViewBag.Batches = await InsuranceSvc.BatchesAsync(ctx, ct);
			ViewBag.Rejections = await InsuranceSvc.RejectionsAsync(ctx, ct);
			ViewBag.Payers = (await InsuranceSvc.PayersAsync(ctx, DateTime.UtcNow, ct)).Where(p => p.Payer.IsActive).ToList();
			ViewBag.Currency = await _billing.CurrencyLabelAsync(ctx, null, ct);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			return View(await InsuranceSvc.PreauthsAsync(ctx, null, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Insurance/Batch/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> ClaimBatch(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var batch = await InsuranceSvc.BatchAsync(ctx, id, ct);
			if (batch == null) return NotFound();
			Chrome();
			ViewBag.Currency = await _billing.CurrencyLabelAsync(ctx, null, ct);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			ViewBag.Ledger = await ClaimAccountingSvc.BatchLedgerAsync(ctx, id, ct);
			ViewBag.Scrub = await ActivatorUtilities.CreateInstance<HospitalClaimScrubService>(HttpContext.RequestServices).ScrubAsync(ctx, id, DateTime.UtcNow, ct);   // H62
			return View(batch);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Insurance/Payers")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Payers(int? patient, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Policies = await InsuranceSvc.PoliciesAsync(ctx, patient, DateTime.UtcNow, ct);
			var lists = await _db.HospitalPriceLists.Where(l => l.CompanyId == ctx.CompanyId).OrderBy(l => l.Code).Select(l => new { l.ID, l.Code, l.Name, l.NameEn }).ToListAsync(ct);
			ViewBag.PriceLists = lists.Select(l => (l.ID, l.Code + " · " + DisplayName.Of(l.Name, l.NameEn))).ToList();
			ViewBag.Currency = await _billing.CurrencyLabelAsync(ctx, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.CanRegister = await _access.CanAsync(ctx, HospitalActions.Register, null, ct);
			ViewBag.DentalBenefits = (await DentalSvc.BenefitsAsync(ctx, ct)).ToDictionary(b => b.PayerId);   // H15
			return View(await InsuranceSvc.PayersAsync(ctx, DateTime.UtcNow, ct));
		}

		/// The insurance desk's patient picker (select2). The admission picker needs `admit`; a policy is
		/// `register` work and an approval request `bill` work, so this one asks for either — never admit.
		/// It lists no more than the patients screen already does on `read`.
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Insurance/Patients/Search")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SearchInsurancePatients(string? q, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct) && !await _access.CanAsync(ctx, HospitalActions.Bill, null, ct))
				return Result(HospitalResult.Fail("forbidden"));
			var page = await _patients.ListAsync(ctx, q, 1, DateTime.UtcNow, ct);
			return Json(new { ok = true, results = page.Rows.Select(p => new { id = p.Id, text = DisplayName.Of(p.Name, p.NameEn) + " — " + p.Mrn }) });
		}

		/// The patient's cover today — the bill and the approval request ask it before they go on.
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Insurance/Eligibility/{patientId:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Eligibility(int patientId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var (policy, payer, why) = await InsuranceSvc.EligibilityAsync(ctx, patientId, DateTime.UtcNow, null, ct);
			return Json(new
			{
				ok = true, eligible = why == null, reason = why == null ? null : _t["inelig:" + why].Value,
				payer = payer == null ? null : DisplayName.Of(payer.Name, payer.NameEn), policyNo = policy?.PolicyNo, memberClass = policy?.MemberClass,
				copayPct = payer?.CopayPct, copayCap = payer?.CopayCap,
			});
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Insurance/Payer"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SavePayer([FromBody] HospitalPayerInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InsuranceSvc.SavePayerAsync(ctx, input ?? new(), ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Insurance/Policy"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Register)]
		public async Task<IActionResult> SavePolicy([FromBody] HospitalPolicyInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InsuranceSvc.SavePolicyAsync(ctx, input ?? new(), ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Insurance/Preauth"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> RequestPreauth([FromBody] HospitalPreauthRequestInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await InsuranceSvc.RequestPreauthAsync(ctx, input.PatientId, input.ServiceId, input.Amount, ctx.EmployeeId, DateTime.UtcNow, ct);
			// the reason a patient is not covered arrives as a code; it is shown in the reader's language
			return Result(r.Ok || r.Args is not { Length: 1 } ? r : r with { Args = new object[] { _t["inelig:" + r.Args[0]].Value } });
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Insurance/Preauth/{id:int}/Decide"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> DecidePreauth(int id, [FromBody] HospitalPreauthDecisionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InsuranceSvc.DecidePreauthAsync(ctx, id, input.Approve, input.ApprovalRef, input.Reason, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Insurance/Batch"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> BuildClaimBatch([FromBody] HospitalClaimBuildInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InsuranceSvc.BuildBatchAsync(ctx, input.PayerId, input.Period, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// H62: holds a claim back from its draft batch (its bill returns to the next build).
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Insurance/Claims/{id:int}/Hold"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> HoldClaim(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await ActivatorUtilities.CreateInstance<HospitalClaimScrubService>(HttpContext.RequestServices).HoldAsync(ctx, id, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Insurance/Batch/{id:int}/Submit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> SubmitClaimBatch(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InsuranceSvc.SubmitAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Insurance/Line/{id:int}/Response"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> ClaimLineResponse(int id, [FromBody] HospitalClaimResponseInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InsuranceSvc.RecordResponseAsync(ctx, id, input.Accept, input.AcceptedAmount, input.Code, input.Reason, DateTime.UtcNow, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Insurance/Line/{id:int}/Resubmit"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> ResubmitClaimLine(int id, [FromBody] HospitalClaimResubmitInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await InsuranceSvc.ResubmitAsync(ctx, id, input?.Justification, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
