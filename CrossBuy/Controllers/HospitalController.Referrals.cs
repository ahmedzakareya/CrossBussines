using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H203 — referrals: the list (read with `clinical-read`), a new referral, and its moves (`consult`).
	public partial class HospitalController
	{
		public sealed class HospitalReferralMoveInput { public string? To { get; set; } public string? Text { get; set; } }

		private HospitalReferralService? _refSvc;
		private HospitalReferralService RefSvc => _refSvc ??= ActivatorUtilities.CreateInstance<HospitalReferralService>(HttpContext.RequestServices);

		[HttpGet("Referrals")]
		[HospitalPerm(HospitalActions.ClinicalRead)]
		public async Task<IActionResult> Referrals(string? q, string? status, string? kind, int? dept, bool overdue = false, int? patient = null, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			if (!string.IsNullOrEmpty(status) && !HospitalReferralStatus.All.Contains(status)) status = null;
			if (!string.IsNullOrEmpty(kind) && !HospitalReferralKind.All.Contains(kind)) kind = null;
			var (rows, total, open, late, ready) = await RefSvc.ListAsync(ctx, q, status, kind, dept, overdue, patient, page, size, DateTime.UtcNow, ct);
			ViewBag.Q = q; ViewBag.Status = status; ViewBag.Kind = kind; ViewBag.Dept = dept; ViewBag.Overdue = overdue; ViewBag.Patient = patient;
			ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (total + size - 1) / size); ViewBag.Total = total; ViewBag.Open = open; ViewBag.Late = late; ViewBag.Ready = ready;
			ViewBag.Departments = await RefSvc.DepartmentsAsync(ctx, ct);
			ViewBag.Doctors = await RefSvc.DoctorsAsync(ctx, ct);
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			return View(rows);
		}

		[HttpPost("Referrals"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> ReferralCreate([FromBody] HospitalReferralInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RefSvc.CreateAsync(ctx, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Referrals/{id:int}/Move"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> ReferralMove(int id, [FromBody] HospitalReferralMoveInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await RefSvc.MoveAsync(ctx, id, input.To ?? "", input.Text, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
