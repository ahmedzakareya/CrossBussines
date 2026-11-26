using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models;
using CrossBuy.Models.Context.Crm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// CRM C2 — lead capture (web forms and their submissions) and assignment by territory. Staff side only: the
	// anonymous form lives on the public surface (HomeController.WebForms.cs) and writes nothing but a submission.
	public partial class CrmController
	{
		private ICrmCaptureService Capture => _capture ??= ActivatorUtilities.CreateInstance<CrmCaptureService>(HttpContext.RequestServices);
		private ICrmCaptureService? _capture;

		// ---------------- web forms ----------------
		[HttpGet] public async Task<IActionResult> WebForms()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			// opening the screen turns anything still waiting into leads now, rather than at the worker's next tick
			ViewBag.Processed = await Capture.ProcessPendingAsync(cid);
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(await Capture.FormsAsync(cid));
		}

		[HttpGet] public async Task<IActionResult> WebFormEditor(int? id)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var form = id is int i && i > 0 ? await Capture.FormAsync(cid, i) : null;
			if (id > 0 && form == null) { this.ToastError(CL["Record not found"].Value); return RedirectToAction(nameof(WebForms)); }
			form ??= new CrmWebForm
			{
				IsActive = true, AskCompany = true, AskEmail = true, Source = "Website",
				Title = IsAr ? "اطلب عرض سعر" : "Request a quote",
				NeedOptions = IsAr ? "عرض سعر، زيارة مندوب، استفسار عام" : "A quote, A sales visit, A general question",
				ConsentText = IsAr ? "أوافق على التواصل معي عبر واتساب والبريد بخصوص طلبي والعروض." : "I agree to be contacted on WhatsApp and email about my request and offers.",
				ThankYou = IsAr ? "شكراً لك، سيتواصل معك أحد مختصينا خلال ساعة عمل." : "Thank you — one of our specialists will contact you within a working hour.",
			};
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(form);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> SaveWebForm(int id, string name, string title, string? source, string? segment, bool askCompany, bool askEmail,
			bool askArea, string? areaOptions, string? needOptions, string consentText, string thankYou, string? allowedSites, bool isActive)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (done, err, newId) = await Capture.SaveFormAsync(cid, new CrmWebForm
			{
				ID = id, Name = name ?? "", Title = title ?? "", Source = source ?? "", Segment = segment, AskCompany = askCompany, AskEmail = askEmail,
				AskArea = askArea, AreaOptions = areaOptions, NeedOptions = needOptions, ConsentText = consentText ?? "", ThankYou = thankYou ?? "",
				AllowedSites = allowedSites, IsActive = isActive,
			}, _access.CurrentEmployeeId());
			this.ToastResult(done, done ? CL["Form saved"].Value : CL[err ?? "Operation failed"].Value);
			return done ? RedirectToAction(nameof(WebFormEditor), new { id = newId }) : RedirectToAction(nameof(WebFormEditor), new { id = id > 0 ? id : (int?)null });
		}

		[HttpGet] public async Task<IActionResult> WebSubmissionsData(int? formId, string? status, string? q, int page = 1, int pageSize = 25)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (rows, total) = await Capture.SubmissionsAsync(cid, formId, status, q, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_SubmissionRows", rows);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> ProcessWebSubmissions()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var r = await Capture.ProcessPendingAsync(cid);
			return Json(new { ok = true, r.Converted, r.Linked, r.Failed, r.Unassigned });
		}

		// ---------------- assignment & territories ----------------
		[HttpGet] public async Task<IActionResult> Assignment()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.CanManage = await _access.CanAsync("manage");
			var isAr = IsAr;
			ViewBag.People = (await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == cid && e.IsActive)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync())
				.Select(e => new PersonRef(e.ID, (!isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName) ?? "", !string.IsNullOrWhiteSpace(e.ProfileImage)))
				.OrderBy(p => p.Name).ToList();
			return View(await Capture.TerritoriesAsync(cid));
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> SaveTerritory(int id, string name, string? nameEn, string? areas, string? segments, string? sources, int priority, bool isActive, int[]? memberIds)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await Capture.SaveTerritoryAsync(cid, new CrmTerritory { ID = id, Name = name ?? "", NameEn = nameEn, Areas = areas, Segments = segments, Sources = sources, Priority = priority, IsActive = isActive }, memberIds ?? Array.Empty<int>());
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> DeleteTerritory(int id)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false, code = "forbidden", error = CL["You do not have permission to perform this action"].Value });
			var (done, err) = await Capture.DeleteTerritoryAsync(cid, id);
			return Json(new { ok = done, error = done ? null : CL[err ?? "Operation failed"].Value });
		}

		/// "Who would get this lead, and why" — the same rules the intake uses, read-only.
		[HttpGet] public async Task<IActionResult> SimulateAssignment(string? area, string? segment, string? source, string? phone, string? email, string? company)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return Json(new { ok = false });
			var r = await Capture.SimulateAsync(cid, area, segment, source, phone, email, company);
			return Json(new
			{
				ok = true, owner = r.Decision.OwnerEmployeeId, ownerName = r.OwnerName, hasPhoto = r.OwnerHasPhoto,
				territory = r.Decision.TerritoryName, reason = CL["why:" + r.Decision.Reason].Value, account = r.AccountName,
			});
		}
	}
}
