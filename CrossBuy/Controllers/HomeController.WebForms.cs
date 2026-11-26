using System.Security.Cryptography;
using System.Text;
using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// CRM C2 — the PUBLIC lead form a company places on its own website.
	//
	// It lives under /store/... because that is the product's public surface: SessionValidationMiddleware already
	// lets /store through without a staff session, so no gate had to be widened for it. Nothing here reads or writes a
	// CRM record. A visit renders one form found by its unguessable key; a submission stores ONE row in
	// CrmWebSubmissions under the company that owns that form. The lead itself is made later, inside the company's
	// own scope (CrmCaptureService.ProcessPendingAsync).
	public partial class HomeController
	{
		private ICrmCaptureService WebCapture => ActivatorUtilities.CreateInstance<CrmCaptureService>(HttpContext.RequestServices);

		/// A hash of the visitor's address and browser, salted per day: enough to slow one sender down, never stored raw.
		private string VisitorKey()
		{
			var raw = (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-") + "|" + Request.Headers.UserAgent + "|" + DateTime.UtcNow.ToString("yyyyMMdd");
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
		}

		/// Which sites may frame the form. Empty = any site (a form is meant to be placed anywhere the company wants).
		private void FrameAncestors(string? allowedSites)
		{
			var sites = (allowedSites ?? "").Split(new[] { ',', ' ', '\n', '،' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Trim().TrimEnd('/')).Where(s => s.StartsWith("https://") || s.StartsWith("http://")).ToList();
			Response.Headers["Content-Security-Policy"] = "frame-ancestors " + (sites.Count == 0 ? "*" : "'self' " + string.Join(' ', sites));
		}

		[AllowAnonymous]
		[HttpGet("store/forms/{key}")]
		public async Task<IActionResult> WebForm(string key, bool embed = false)
		{
			var form = await WebCapture.PublicFormAsync(key);
			if (form == null) return NotFound();
			FrameAncestors(form.AllowedSites);
			ViewBag.Embed = embed;
			return View("~/Views/Home/WebForm.cshtml", form);
		}

		/// The one line a company pastes into its website: it draws the form in a frame that grows to fit.
		[AllowAnonymous]
		[HttpGet("store/forms/{key}/embed.js")]
		public async Task<IActionResult> WebFormScript(string key)
		{
			var form = await WebCapture.PublicFormAsync(key);
			if (form == null) return NotFound();
			var src = $"{Request.Scheme}://{Request.Host}/store/forms/{Uri.EscapeDataString(key)}?embed=true";
			var js = "(function(){var s=document.currentScript;var f=document.createElement('iframe');" +
					 // the form speaks the language of the page it sits on: Arabic for an Arabic site, English otherwise
					 "var l=((document.documentElement.lang||navigator.language||'')+'').slice(0,2).toLowerCase()==='ar'?'ar':'en';" +
					 "f.src=" + System.Text.Json.JsonSerializer.Serialize(src) + "+'&culture='+l+'&ui-culture='+l+'&page='+encodeURIComponent(location.href);" +
					 "f.title=" + System.Text.Json.JsonSerializer.Serialize(form.Title) + ";" +
					 "f.style.cssText='width:100%;max-width:640px;border:0;min-height:520px;display:block';f.loading='lazy';" +
					 "window.addEventListener('message',function(e){if(e.source===f.contentWindow&&e.data&&e.data.cbFormHeight){f.style.height=e.data.cbFormHeight+'px';}});" +
					 "s.parentNode.insertBefore(f,s);})();";
			Response.Headers.CacheControl = "public, max-age=300";
			return Content(js, "application/javascript; charset=utf-8");
		}

		[AllowAnonymous]
		[IgnoreAntiforgeryToken]
		[RequestSizeLimit(16 * 1024)]
		[AnonymousByDesign(
			"A website visitor asking a company to contact them has no account and never will - requiring a login would " +
			"make the form useless to the only people it is for. It is not an unguarded mutator: it can write exactly ONE " +
			"row, to CrmWebSubmissions, and nothing else (no lead, no consent, no notification - those are made later in " +
			"the company's own scope). The company is taken from the form row found by an unguessable 24-character key; " +
			"the request names no company, customer, lead or user id. Every field is length-clipped and validated, a " +
			"honeypot field marks robots as spam, and each visitor (a daily-salted hash, never the raw address) and each " +
			"form are rate-limited. Anti-forgery is not used because there is no session for a forgery to ride on, and a " +
			"form framed on the company's own site cannot carry a SameSite cookie.")]
		[HttpPost("store/forms/{key}")]
		public async Task<IActionResult> WebFormSubmit(string key, string? name, string? company, string? phone, string? email, string? area,
			string? need, string? message, bool consent, string? website, string? page)
		{
			var (ok, code) = await WebCapture.SubmitAsync(key, new PublicFormFields(name, company, phone, email, area, need, message, consent, website, page), VisitorKey());
			if (code == "not_found") return NotFound();
			var t = HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CrossBuy.CrmResources>>();
			return Json(new { ok, code, message = ok ? null : t["form:" + code].Value });
		}
	}
}
