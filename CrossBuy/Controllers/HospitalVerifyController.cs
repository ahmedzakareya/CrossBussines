using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H208 — the public check behind a sick leave certificate's QR: an employer or the Ministry opens it from the
	// paper, with no login, and sees whether the certificate is genuine and still valid — never the illness.
	[Route("verify")]
	public class HospitalVerifyController : Controller
	{
		private readonly CrossDbContext _db;
		public HospitalVerifyController(CrossDbContext db) { _db = db; }

		[AllowAnonymous]
		[AnonymousByDesign(
			"A sick leave certificate's QR opens this page for whoever holds the paper (an employer, the Ministry); they have no login. " +
			"The 10-character random code opens exactly one certificate and the page shows only what proves the paper: its number, " +
			"the facility, the patient's initials, the period, whether it is void, and the Ministry's authentication — no diagnosis, " +
			"no file number, no name. It reads; it writes nothing.")]
		[HttpGet("sl/{code}")]
		public async Task<IActionResult> SickLeave(string code, CancellationToken ct = default)
		{
			var c = await HospitalSickLeaveService.CheckAsync(_db, code, ct);
			Response.Headers["X-Robots-Tag"] = "noindex";
			return View("~/Views/Hospital/VerifySickLeave.cshtml", c);
		}
	}
}
