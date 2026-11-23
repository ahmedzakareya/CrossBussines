using CrossBuy.BL.Hospital;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H257 — DEVELOPMENT ONLY: the test exchange that stands in for the payer, the outbound feeds' receiver and
	// the payment provider while the hospital is in test (HospitalTestExchange). [DevOnly]: 404 outside a local
	// Development process. The payer endpoint needs the test key; the checkout page takes no money and marks nothing paid.
	// ==========================================================================================
	[Route("dev/hospital-exchange")]
	[AllowAnonymous]
	[CrossBuy.Models.DevOnly]
	[IgnoreAntiforgeryToken]
	public sealed class HospitalTestExchangeController : Controller
	{
		private ContentResult Fhir(string json, int status = 200) => new() { Content = json, ContentType = "application/fhir+json; charset=utf-8", StatusCode = status };

		private const string PayerWhy =
			"It stands in for an outside payer's FHIR gateway, which the hospital's server calls with no session: it checks the test " +
			"exchange's key in the X-Api-Key header instead, writes nothing, and [DevOnly] makes it 404 outside a local Development process.";

		// POST dev/hospital-exchange/payer/CoverageEligibilityRequest | Claim
		[HttpPost("payer/{resource}"), CrossBuy.Models.Platform.AnonymousByDesign(PayerWhy)]
		public async Task<IActionResult> Payer(string resource, CancellationToken ct)
		{
			if (HospitalTestExchange.KeyRefusal(Request.Headers[HospitalTestExchange.KeyHeader].FirstOrDefault()) is string why) return Fhir(HospitalTestExchange.Error(why), 401);
			using var reader = new StreamReader(Request.Body);
			var body = await reader.ReadToEndAsync(ct);
			var answer = resource switch
			{
				"CoverageEligibilityRequest" => HospitalTestExchange.Eligibility(body),
				"Claim" => HospitalTestExchange.Preauth(body),
				_ => null,
			};
			return answer == null ? Fhir(HospitalTestExchange.Error("The test exchange does not read this message"), 400) : Fhir(answer);
		}

		// GET dev/hospital-exchange/received — what the MLLP test receiver took (in memory)
		[HttpGet("received")]
		public IActionResult Received() => Json(HospitalTestReceiverLog.Recent());

		// GET dev/hospital-exchange/pay?ref=&amount=&currency= — the test checkout
		[HttpGet("pay")]
		public IActionResult Pay([FromQuery(Name = "ref")] string? billNo, string? amount, string? currency)
		{
			ViewBag.Ref = billNo ?? ""; ViewBag.Amount = amount ?? ""; ViewBag.Currency = currency ?? "";
			return View("~/Views/HospitalTestExchange/Pay.cshtml");
		}
	}
}
