using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H63: charity cases. The billing desk requests a waiver on a closed bill (from the bill), the hospital
	// administrator approves or refuses it on the charity screen (requests first; searched and paged on the server) and
	// keeps the funds there. The patient's account statement prints through Report Studio (Hospital.PatientStatement).
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalCharityService? _charitySvc;
		private HospitalCharityService CharitySvc => _charitySvc ??= ActivatorUtilities.CreateInstance<HospitalCharityService>(HttpContext.RequestServices);

		public sealed class HospitalCharityDecisionInput { public bool Approve { get; set; } public string? Note { get; set; } }

		[HttpGet("Charity")]
		[HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> Charity(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var (funds, ready) = await CharitySvc.FundsAsync(ctx, false, ct);
			ViewBag.Ready = ready;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Accounts = await PostingAccountsAsync(ctx, ct);
			ViewBag.Currency = await _billing.CurrencyLabelAsync(ctx, null, ct);
			if (ready) { var (_, _, pending, approved) = await CharitySvc.ListAsync(ctx, null, null, 1, 1, ct); ViewBag.Pending = pending; ViewBag.Approved = approved; }
			return View(funds);
		}

		/// The requests and decisions (cbServerList): search by patient, MRN, bill or number; a status narrows it.
		[HttpGet("Charity/Data")]
		[HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> CharityData(string? q, string? status, int page = 1, int pageSize = 25, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			if (pageSize is < 1 or > 200) pageSize = 25;
			if (page < 1) page = 1;
			var (rows, total, _, _) = await CharitySvc.ListAsync(ctx, string.IsNullOrEmpty(status) ? null : status, q, page, pageSize, ct);
			int pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = pages.ToString();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Me = ctx.EmployeeId;
			ViewBag.Currency = await _billing.CurrencyLabelAsync(ctx, null, ct);
			return PartialView("_CharityRows", rows);
		}

		[HttpPost("Charity/Funds"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveCharityFund(int? id, [FromBody] HospitalCharityFundInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CharitySvc.SaveFundAsync(ctx, id is > 0 ? id : null, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Bill/{id:int}/Charity"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> RequestCharity(int id, [FromBody] HospitalCharityRequestInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CharitySvc.RequestAsync(ctx, id, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Charity/{id:int}/Decide"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> DecideCharity(int id, [FromBody] HospitalCharityDecisionInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await CharitySvc.DecideAsync(ctx, id, input.Approve, input.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
