using CrossBuy.BL;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// Recruitment (HR-11). R0 = required-document catalog CRUD. R1 = applications board + applicant stepper. R2/R3 next.
	// ────────────────────────────────────────────────────────────────────────────────────────────
	// RECRUITMENT — seven baseline AuthorizationGap entries closed here, and the same company-1 literal
	// removed with them.
	//
	// This surface holds candidate personal data and uploaded identity documents: passports, national
	// IDs, certificates. Every write below was reachable by any signed-in user, and every read and write
	// addressed `HrCompanyId` — the literal 1 — so a recruiter in company 41 was administering company
	// 1's candidate pipeline.
	//
	// HrActions.PerformanceManage is the existing vocabulary entry whose own comment reads "appraisals,
	// training, recruitment". No new permission is introduced.
	//
	// DENIALS MATCH THE CALLER. The JSON actions return a JSON refusal and the page actions redirect,
	// because a board drag that silently received an HTML redirect would look to the user like a saved
	// move that later vanished.
	// ────────────────────────────────────────────────────────────────────────────────────────────
	public partial class AdminController
	{
		private IRecruitmentService RecruitSvc => (HttpContext.RequestServices.GetService(typeof(IRecruitmentService)) as IRecruitmentService)!;

		// ===== Job applications — board + applicant form (R1) =====
		[HttpGet]
		public async Task<IActionResult> Applications()
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Index));

			ViewBag.Stages = CrossBuy.BL.RecruitmentStages.Order;
			ViewBag.JobTitles = await Db.JobTitles.AsNoTracking().ToListAsync();
			return View(await RecruitSvc.GetApplicationsAsync(gate.CompanyId));
		}

		private async Task PopulateApplicationListsAsync(int companyId)
		{
			ViewBag.JobTitles = await Db.JobTitles.AsNoTracking().OrderBy(t => t.TitleAr).ToListAsync();
			ViewBag.Branches = await Db.Branches.AsNoTracking().Where(b => b.CompanyID == companyId).OrderBy(b => b.NameAr).ToListAsync();
			ViewBag.Companies = await Db.Companies.AsNoTracking().OrderBy(c => c.ComoanyNameAr).ToListAsync();
			ViewBag.Countries = await Db.CountriesLookup.AsNoTracking().OrderBy(c => c.CountryNameAr).ToListAsync();
			ViewBag.Departments = await Db.Hierarchicals.AsNoTracking().Where(h => h.IsActive == true || h.IsActive == null).OrderBy(h => h.Sort).ToListAsync();
		}

		[HttpGet]
		public async Task<IActionResult> ApplicationForm(int? id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Index));

			await PopulateApplicationListsAsync(gate.CompanyId);
			var app = id.HasValue && id.Value > 0 ? await RecruitSvc.GetApplicationAsync(gate.CompanyId, id.Value) : null;
			if (id.HasValue && id.Value > 0 && app == null) { TempData["HrErr"] = L["Record not found."].Value; return RedirectToAction(nameof(Applications)); }
			return View(app ?? new JobApplication());
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveApplication([FromForm] JobApplication input, IFormFile? photo)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Applications));

			// optional applicant photo
			if (photo != null && photo.Length > 0 && !string.IsNullOrEmpty(WebRoot))
			{
				var dir = System.IO.Path.Combine(WebRoot, "uploads", "applicants");
				if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
				var fn = Guid.NewGuid() + System.IO.Path.GetExtension(photo.FileName);
				using (var st = System.IO.File.Create(System.IO.Path.Combine(dir, fn))) await photo.CopyToAsync(st);
				input.PhotoPath = "/uploads/applicants/" + fn;
			}
			var (ok, err, sid) = await RecruitSvc.SaveApplicationAsync(gate.CompanyId, input, null);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Application saved"].Value : err;
			return ok ? RedirectToAction(nameof(Applications)) : RedirectToAction(nameof(ApplicationForm), new { id = input.ID });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> MoveApplication(int id, string stage)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return Json(new { ok = false, error = HrDeniedMessage });

			var (ok, err) = await RecruitSvc.MoveStageAsync(gate.CompanyId, id, stage, null);
			return Json(new { ok, error = err });
		}

		// ===== Application detail + document checklist (R2) =====
		[HttpGet]
		public async Task<IActionResult> ApplicationDetail(int id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Applications));

			var app = await RecruitSvc.GetApplicationAsync(gate.CompanyId, id);
			if (app == null) { TempData["HrErr"] = L["Record not found."].Value; return RedirectToAction(nameof(Applications)); }
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			ViewBag.Checklist = await RecruitSvc.GetChecklistAsync(gate.CompanyId, id);
			ViewBag.OtherDocs = (await RecruitSvc.GetApplicationDocsAsync(gate.CompanyId, id)).Where(d => d.RequiredDocumentTypeID == null).ToList();
			// resolve display names
			ViewBag.JobTitleName = app.JobTitleID == null ? null : await Db.JobTitles.Where(t => t.ID == app.JobTitleID).Select(t => isAr ? t.TitleAr : t.Title).FirstOrDefaultAsync();
			ViewBag.BranchName = app.BranchID == null ? null : await Db.Branches.Where(b => b.ID == app.BranchID).Select(b => isAr ? b.NameAr : b.Name).FirstOrDefaultAsync();
			ViewBag.DeptName = app.DepartmentID == null ? null : await Db.Hierarchicals.Where(h => h.H_ID == app.DepartmentID).Select(h => isAr ? h.H_Name : DisplayName.Or(h.H_NameEn, h.H_Name)).FirstOrDefaultAsync();
			ViewBag.Stages = CrossBuy.BL.RecruitmentStages.Order;
			return View(app);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> ChangeApplicationStage(int id, string stage)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(ApplicationDetail), new { id });

			var (ok, err) = await RecruitSvc.MoveStageAsync(gate.CompanyId, id, stage, null);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Stage updated"].Value : err;
			return RedirectToAction(nameof(ApplicationDetail), new { id });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> UploadApplicationDoc(int applicationId, int? requiredDocumentTypeId, IFormFile? file, string? docNumber, DateTime? issueDate, DateTime? expiryDate)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(ApplicationDetail), new { id = applicationId });

			var (ok, err) = await RecruitSvc.AddApplicationDocAsync(gate.CompanyId, applicationId, requiredDocumentTypeId, file, docNumber, issueDate, expiryDate, WebRoot);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Document uploaded"].Value : err;
			return RedirectToAction(nameof(ApplicationDetail), new { id = applicationId });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteApplicationDoc(int id, int applicationId)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(ApplicationDetail), new { id = applicationId });

			var (ok, err, _) = await RecruitSvc.DeleteApplicationDocAsync(gate.CompanyId, id, WebRoot);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Document deleted"].Value : err;
			return RedirectToAction(nameof(ApplicationDetail), new { id = applicationId });
		}

		// ===== Required-document catalog (R0) =====
		[HttpGet]
		public async Task<IActionResult> RequiredDocTypesList()
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Index));

			return View(await RecruitSvc.GetDocTypesAsync(gate.CompanyId));
		}

		[HttpPost]
		public async Task<IActionResult> SaveRequiredDocType([FromBody] RequiredDocTypeDto dto)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return Json(new { success = false, message = HrDeniedMessage });

			try
			{
				var (ok, err, _) = await RecruitSvc.SaveDocTypeAsync(gate.CompanyId, dto);
				return Json(ok ? new { success = true } : new { success = false, message = err });
			}
			catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
		}

		[HttpGet]
		public async Task<IActionResult> GetRequiredDocTypeById(int id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return Json(new { success = false, message = HrDeniedMessage });

			try
			{
				var dto = await RecruitSvc.GetDocTypeAsync(gate.CompanyId, id);
				return dto == null
					? Json(new { success = false, message = L["Record not found."].Value })
					: Json(new { success = true, data = dto });
			}
			catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
		}

		[HttpPost]
		public async Task<IActionResult> DeleteRequiredDocType(int id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return Json(new { success = false, message = HrDeniedMessage });

			try
			{
				var (ok, err) = await RecruitSvc.DeleteDocTypeAsync(gate.CompanyId, id);
				return Json(ok ? new { success = true } : new { success = false, message = err });
			}
			catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
		}
	}
}
