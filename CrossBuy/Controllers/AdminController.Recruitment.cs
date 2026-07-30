using CrossBuy.BL;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// Recruitment (HR-11). R0 = required-document catalog CRUD. R1 = applications board + applicant stepper. R2/R3 next.
	public partial class AdminController
	{
		private IRecruitmentService RecruitSvc => (HttpContext.RequestServices.GetService(typeof(IRecruitmentService)) as IRecruitmentService)!;

		// ===== Job applications — board + applicant form (R1) =====
		[HttpGet]
		public async Task<IActionResult> Applications()
		{
			ViewBag.Stages = CrossBuy.BL.RecruitmentStages.Order;
			ViewBag.JobTitles = await Db.JobTitles.AsNoTracking().ToListAsync();
			return View(await RecruitSvc.GetApplicationsAsync(HrCompanyId));
		}

		private async Task PopulateApplicationListsAsync()
		{
			ViewBag.JobTitles = await Db.JobTitles.AsNoTracking().OrderBy(t => t.TitleAr).ToListAsync();
			ViewBag.Branches = await Db.Branches.AsNoTracking().Where(b => b.CompanyID == HrCompanyId).OrderBy(b => b.NameAr).ToListAsync();
			ViewBag.Companies = await Db.Companies.AsNoTracking().OrderBy(c => c.ComoanyNameAr).ToListAsync();
			ViewBag.Countries = await Db.CountriesLookup.AsNoTracking().OrderBy(c => c.CountryNameAr).ToListAsync();
			ViewBag.Departments = await Db.Hierarchicals.AsNoTracking().Where(h => h.IsActive == true || h.IsActive == null).OrderBy(h => h.Sort).ToListAsync();
		}

		[HttpGet]
		public async Task<IActionResult> ApplicationForm(int? id)
		{
			await PopulateApplicationListsAsync();
			var app = id.HasValue && id.Value > 0 ? await RecruitSvc.GetApplicationAsync(HrCompanyId, id.Value) : null;
			if (id.HasValue && id.Value > 0 && app == null) { TempData["HrErr"] = L["Record not found."].Value; return RedirectToAction(nameof(Applications)); }
			return View(app ?? new JobApplication());
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveApplication([FromForm] JobApplication input, IFormFile? photo)
		{
			// optional applicant photo
			if (photo != null && photo.Length > 0 && !string.IsNullOrEmpty(WebRoot))
			{
				var dir = System.IO.Path.Combine(WebRoot, "uploads", "applicants");
				if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
				var fn = Guid.NewGuid() + System.IO.Path.GetExtension(photo.FileName);
				using (var st = System.IO.File.Create(System.IO.Path.Combine(dir, fn))) await photo.CopyToAsync(st);
				input.PhotoPath = "/uploads/applicants/" + fn;
			}
			var (ok, err, sid) = await RecruitSvc.SaveApplicationAsync(HrCompanyId, input, null);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Application saved"].Value : err;
			return ok ? RedirectToAction(nameof(Applications)) : RedirectToAction(nameof(ApplicationForm), new { id = input.ID });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> MoveApplication(int id, string stage)
		{
			var (ok, err) = await RecruitSvc.MoveStageAsync(HrCompanyId, id, stage, null);
			return Json(new { ok, error = err });
		}

		// ===== Application detail + document checklist (R2) =====
		[HttpGet]
		public async Task<IActionResult> ApplicationDetail(int id)
		{
			var app = await RecruitSvc.GetApplicationAsync(HrCompanyId, id);
			if (app == null) { TempData["HrErr"] = L["Record not found."].Value; return RedirectToAction(nameof(Applications)); }
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			ViewBag.Checklist = await RecruitSvc.GetChecklistAsync(HrCompanyId, id);
			ViewBag.OtherDocs = (await RecruitSvc.GetApplicationDocsAsync(HrCompanyId, id)).Where(d => d.RequiredDocumentTypeID == null).ToList();
			// resolve display names
			ViewBag.JobTitleName = app.JobTitleID == null ? null : await Db.JobTitles.Where(t => t.ID == app.JobTitleID).Select(t => isAr ? t.TitleAr : t.Title).FirstOrDefaultAsync();
			ViewBag.BranchName = app.BranchID == null ? null : await Db.Branches.Where(b => b.ID == app.BranchID).Select(b => isAr ? b.NameAr : b.Name).FirstOrDefaultAsync();
			ViewBag.DeptName = app.DepartmentID == null ? null : await Db.Hierarchicals.Where(h => h.H_ID == app.DepartmentID).Select(h => isAr ? h.H_Name : h.H_NameEn).FirstOrDefaultAsync();
			ViewBag.Stages = CrossBuy.BL.RecruitmentStages.Order;
			return View(app);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> ChangeApplicationStage(int id, string stage)
		{
			var (ok, err) = await RecruitSvc.MoveStageAsync(HrCompanyId, id, stage, null);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Stage updated"].Value : err;
			return RedirectToAction(nameof(ApplicationDetail), new { id });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> UploadApplicationDoc(int applicationId, int? requiredDocumentTypeId, IFormFile? file, string? docNumber, DateTime? issueDate, DateTime? expiryDate)
		{
			var (ok, err) = await RecruitSvc.AddApplicationDocAsync(HrCompanyId, applicationId, requiredDocumentTypeId, file, docNumber, issueDate, expiryDate, WebRoot);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Document uploaded"].Value : err;
			return RedirectToAction(nameof(ApplicationDetail), new { id = applicationId });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteApplicationDoc(int id, int applicationId)
		{
			var (ok, err, _) = await RecruitSvc.DeleteApplicationDocAsync(HrCompanyId, id, WebRoot);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Document deleted"].Value : err;
			return RedirectToAction(nameof(ApplicationDetail), new { id = applicationId });
		}

		// ===== Required-document catalog (R0) =====
		[HttpGet]
		public async Task<IActionResult> RequiredDocTypesList()
		{
			return View(await RecruitSvc.GetDocTypesAsync(HrCompanyId));
		}

		[HttpPost]
		public async Task<IActionResult> SaveRequiredDocType([FromBody] RequiredDocTypeDto dto)
		{
			try
			{
				var (ok, err, _) = await RecruitSvc.SaveDocTypeAsync(HrCompanyId, dto);
				return Json(ok ? new { success = true } : new { success = false, message = err });
			}
			catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
		}

		[HttpGet]
		public async Task<IActionResult> GetRequiredDocTypeById(int id)
		{
			try
			{
				var dto = await RecruitSvc.GetDocTypeAsync(HrCompanyId, id);
				return dto == null
					? Json(new { success = false, message = L["Record not found."].Value })
					: Json(new { success = true, data = dto });
			}
			catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
		}

		[HttpPost]
		public async Task<IActionResult> DeleteRequiredDocType(int id)
		{
			try
			{
				var (ok, err) = await RecruitSvc.DeleteDocTypeAsync(HrCompanyId, id);
				return Json(ok ? new { success = true } : new { success = false, message = err });
			}
			catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
		}
	}
}
