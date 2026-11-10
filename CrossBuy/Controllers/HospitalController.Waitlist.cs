using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H48: the desk's earlier-slot waitlist (the prototype's #/portal-waitlist). The line, the offers open
	// now and their minutes, the times refilled this month, and what happened to the last time that freed up. The desk
	// may offer a waiting patient the earliest free time now, or end an open offer and pass it to the next. Every rule
	// is HospitalPortalWaitlistService's; the desk that books appointments (`book`) runs it.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalPortalWaitlistService? _waitlist;
		private HospitalPortalWaitlistService WaitlistSvc => _waitlist ??= new HospitalPortalWaitlistService(_db,
			ActivatorUtilities.CreateInstance<SmtpHospitalPortalMailer>(HttpContext.RequestServices));

		/// The offer email, its link to the portal's sign-in page (where it is taken or declined).
		private (string Subject, string Html) WaitlistMail(string token, HospitalWaitlistOfferMail m) =>
			HospitalPortalMails.Offer(_t, Url.Action("Offer", "PatientPortal", new { t = token }, Request.Scheme)!, m);

		[HttpGet("PortalWaitlist")]
		[HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> PortalWaitlist(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			await WaitlistSvc.RunAsync(ctx.CompanyId, WaitlistMail, DateTime.Now, DateTime.UtcNow, null, ct);   // no worker: the screen moves the line on
			return View(await WaitlistSvc.BoardAsync(ctx, DateTime.Now, DateTime.UtcNow, ct));
		}

		[HttpPost("PortalWaitlist/{id:int}/Offer"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> PortalWaitlistOffer(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await WaitlistSvc.OfferNowAsync(ctx, id, WaitlistMail, DateTime.Now, DateTime.UtcNow, ct));
		}

		[HttpPost("PortalWaitlist/Offers/{id:int}/Pass"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Book)]
		public async Task<IActionResult> PortalWaitlistPass(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await WaitlistSvc.PassAsync(ctx, id, ctx.EmployeeId, WaitlistMail, DateTime.Now, DateTime.UtcNow, ct));
		}
	}
}
