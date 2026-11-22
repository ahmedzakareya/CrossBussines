using System.Text;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H242 — FHIR eligibility and pre-authorisation messages with the payer, and each payer's gateway.
	public partial class HospitalController
	{
		private HospitalFhirExchangeService? _fhirXSvc;
		private HospitalFhirExchangeService FhirXSvc => _fhirXSvc ??= ActivatorUtilities.CreateInstance<HospitalFhirExchangeService>(HttpContext.RequestServices, InsuranceSvc);
		public sealed class HospitalFhirAnswerInput { public string? Raw { get; set; } }

		[HttpGet("Insurance/Fhir"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FhirExchange(string? q, string? kind, int page = 1, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			const int size = 20;
			var d = await FhirXSvc.PageAsync(ctx, q, kind, page, size, ct);
			if (d == null) return Forbid();
			ViewBag.Q = q; ViewBag.Kind = kind ?? ""; ViewBag.Page = Math.Max(1, page); ViewBag.Pages = Math.Max(1, (d.Total + size - 1) / size);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Pending = await InsuranceSvc.PreauthsAsync(ctx, HospitalPreauthStatus.Pending, ct);
			return View(d);
		}

		[HttpPost("Insurance/Fhir/Eligibility/{patientId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FhirEligibility(int patientId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FhirXSvc.CheckEligibilityAsync(ctx, patientId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Insurance/Fhir/Preauth/{preauthId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FhirPreauth(int preauthId, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FhirXSvc.RequestPreauthAsync(ctx, preauthId, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Insurance/Fhir/{id:int}/Answer"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FhirAnswer(int id, [FromBody] HospitalFhirAnswerInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FhirXSvc.LoadAnswerAsync(ctx, id, input?.Raw, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("Insurance/Fhir/{id:int}/File"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> FhirFile(int id, bool answer = false, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var m = await FhirXSvc.MessageAsync(ctx, id, ct);
			var body = m == null ? null : answer ? m.Response : m.Request;
			if (body == null) return NotFound();
			return File(Encoding.UTF8.GetBytes(body), "application/fhir+json", $"{m!.Kind.ToLowerInvariant()}-{m.ID}{(answer ? "-answer" : "")}.json");
		}

		[HttpPost("Insurance/Fhir/Gateway/{payerId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> FhirGateway(int payerId, [FromBody] HospitalGatewayInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await FhirXSvc.SaveGatewayAsync(ctx, payerId, input, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
