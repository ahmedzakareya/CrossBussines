using System.Text;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H200 — electronic claims: a submitted batch's file for the payer (FHIR R4 Claim bundle), the payer's answer
	// file loaded (FHIR R4 ClaimResponse), and every file kept on the batch. All `bill` (the claims desk's work).
	public partial class HospitalController
	{
		public sealed class HospitalEClaimInput { public string? Raw { get; set; } }

		private HospitalEClaimService? _eclaimSvc;
		private HospitalEClaimService EClaimSvc => _eclaimSvc ??= ActivatorUtilities.CreateInstance<HospitalEClaimService>(HttpContext.RequestServices, InsuranceSvc);

		[HttpGet("Insurance/Batch/{id:int}/Fhir"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> ClaimBatchFhir(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var (r, name, json) = await EClaimSvc.ExportAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return File(new UTF8Encoding(false).GetBytes(json!), "application/fhir+json", name);
		}

		[HttpPost("Insurance/Batch/{id:int}/Response"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		[RequestSizeLimit(6_000_000)]
		public async Task<IActionResult> ClaimBatchResponse(int id, [FromBody] HospitalEClaimInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			var (r, imp) = await EClaimSvc.ImportAsync(ctx, id, input.Raw, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (!r.Ok) return Result(r);
			return Json(new { ok = true, applied = imp!.Applied, skipped = imp.Skipped, detail = imp.Detail });
		}

		[HttpGet("Insurance/Exchange/{id:int}"), HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> ClaimExchangeFile(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound();
			var x = await EClaimSvc.ExchangeAsync(ctx, id, ct);
			if (x == null) return NotFound();
			string name = (x.Direction == HospitalClaimExchangeDirection.Sent ? "claims-" : "answer-") + x.BatchId + "-" + x.ID + ".fhir.json";
			return File(new UTF8Encoding(false).GetBytes(x.Raw), "application/fhir+json", name);
		}
	}
}
