using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// CRM C1 — one customer record: the contacts screen, the contact file with consent, the account 360 file,
	// data quality with duplicate merge, and the live duplicate check used by the entry forms.
	//
	// A PARTIAL of CrmController (the platform's controller count is frozen) that answers to the same authority:
	// ResolveCompanyAsync (company + CRM "read"), CrmPerm on every write, and the owner visibility whitelist from
	// ICrmAccessService handed to the service so a typed id can never reach a record the grid would hide.
	public partial class CrmController
	{
		private ICrmIdentityService Identity => _identity ??= ActivatorUtilities.CreateInstance<CrmIdentityService>(HttpContext.RequestServices);
		private ICrmIdentityService? _identity;
		private IStringLocalizer<CrossBuy.CrmResources> CL => _cl ??= HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CrossBuy.CrmResources>>();
		private IStringLocalizer<CrossBuy.CrmResources>? _cl;
		private bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

		// ---------------- contacts ----------------
		[HttpGet] public async Task<IActionResult> Contacts()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.Health = await Identity.HealthAsync(cid, await _access.VisibleOwnerIdsAsync());
			return View();
		}

		[HttpGet] public async Task<IActionResult> ContactsData(string? q, string? consent, int page = 1, int pageSize = 25)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (rows, total) = await Identity.SearchContactsAsync(cid, await _access.VisibleOwnerIdsAsync(), q, consent, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_ContactRows", rows);
		}

		[HttpGet] public async Task<IActionResult> Contact(int id)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var file = await Identity.ContactAsync(cid, await _access.VisibleOwnerIdsAsync(), id);
			if (file == null)
			{
				if (await Identity.SurvivorOfAsync(cid, "Contact", id) is int survivor) return RedirectToAction(nameof(Contact), new { id = survivor });
				this.ToastError(CL["Record not found"].Value);
				return RedirectToAction(nameof(Contacts));
			}
			ViewBag.CanEdit = await _access.CanAsync("edit");
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(file);
		}

		/// One consent decision. Answers JSON; the screen repaints the one channel row, never the page.
		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> ContactConsent(int id, string channel, string state, string source, string? note)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await Identity.RecordConsentAsync(cid, await _access.VisibleOwnerIdsAsync(), "Contact", id, channel, state, source, note, _access.CurrentEmployeeId());
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		// ---------------- account 360 ----------------
		[HttpGet] public async Task<IActionResult> Account360(int id)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var file = await Identity.Account360Async(cid, await _access.VisibleOwnerIdsAsync(), id);
			if (file == null)
			{
				if (await Identity.SurvivorOfAsync(cid, "Account", id) is int survivor) return RedirectToAction(nameof(Account360), new { id = survivor });
				this.ToastError(CL["Record not found"].Value);
				return RedirectToAction(nameof(Accounts));
			}
			// amounts in the company's own currency, at its own number of decimals (KWD three, most two)
			var cur = await (from co in _context.Companies.AsNoTracking()
							 where co.CompanyID == cid
							 join c in _context.Currencies.AsNoTracking() on co.DefaultCurrencyId equals (int?)c.ID
							 select new { c.Code, c.DecimalPlaces }).FirstOrDefaultAsync();
			ViewBag.CurCode = cur?.Code ?? "";
			ViewBag.CurDec = (int)(cur?.DecimalPlaces ?? 2);
			return View(file);
		}

		// ---------------- data quality + duplicates ----------------
		[HttpGet] public async Task<IActionResult> Duplicates(string? type)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var visible = await _access.VisibleOwnerIdsAsync();
			ViewBag.Type = type == "Contact" ? "Contact" : "Account";
			ViewBag.Health = await Identity.HealthAsync(cid, visible);
			ViewBag.CanEdit = await _access.CanAsync("edit");
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(await Identity.PairsAsync(cid, visible, (string)ViewBag.Type));
		}

		[HttpGet] public async Task<IActionResult> MergePreview(string type, int survivorId, int mergedId)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var preview = await Identity.PreviewAsync(cid, await _access.VisibleOwnerIdsAsync(), type, survivorId, mergedId, IsAr);
			if (preview == null) return NotFound();
			return PartialView("_MergePreview", preview);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> MergeRecords(string type, int survivorId, int mergedId, string[]? take)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await Identity.MergeAsync(cid, await _access.VisibleOwnerIdsAsync(), type, survivorId, mergedId, take ?? Array.Empty<string>(), _access.CurrentEmployeeId());
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value,
				url = done ? (type == "Contact" ? Url.Action(nameof(Contact), new { id = survivorId }) : Url.Action(nameof(Account360), new { id = survivorId })) : null });
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> DismissDuplicate(string type, int a, int b)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await Identity.DismissAsync(cid, await _access.VisibleOwnerIdsAsync(), type, a, b, _access.CurrentEmployeeId());
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		/// The live "a similar record exists" check behind every entry form. Read-only; answers at most eight hits.
		[HttpGet] public async Task<IActionResult> DuplicateCheck(string kind, int? selfId, string? name, string? phone, string? email)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(Array.Empty<object>());
			var hits = await Identity.CheckAsync(cid, await _access.VisibleOwnerIdsAsync(), kind ?? "", selfId, name, phone, email);
			return Json(hits.Select(h => new
			{
				kind = h.Kind, kindLabel = CL["kind:" + h.Kind].Value, id = h.Id, name = h.Name, sub = h.Sub, url = h.Url, score = h.Score,
				reasons = h.Reasons.Select(r => CL["dup:" + r].Value),
			}));
		}
	}
}
