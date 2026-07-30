using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// HR-9 — performance appraisal (cycles, templates, scoring, submit). Employee acknowledgment lives in PeopleController (ESS).
	public partial class AdminController
	{
		private CrossBuy.BL.IAppraisalService AprSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IAppraisalService)) as CrossBuy.BL.IAppraisalService)!;

		private async Task PopulateAppraisalListsAsync()
		{
			ViewBag.Cycles = await AprSvc.GetCyclesAsync(HrCompanyId);
			ViewBag.Templates = await AprSvc.GetTemplatesAsync(HrCompanyId);
			// public DTO (NOT an anonymous type) so the view can bind @e.ID/@e.FullName dynamically across the Views assembly (avoids RuntimeBinderException)
			ViewBag.Employees = await Db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == HrCompanyId && e.IsActive)
				.OrderBy(e => e.FullName)
				.Select(e => new CrossBuy.ViewModel.EmployeeListItemDto { ID = e.ID, FullName = e.FullName, FullNameEn = e.FullNameEn }).ToListAsync();
		}

		[HttpGet]
		public async Task<IActionResult> Appraisals(int? cycleId)
		{
			await PopulateAppraisalListsAsync();
			ViewBag.CycleId = cycleId;
			var list = await AprSvc.GetAppraisalsAsync(HrCompanyId, cycleId);
			var empIds = list.SelectMany(a => new[] { a.EmployeeID, a.ManagerEmployeeID }).Distinct().ToList();
			ViewBag.Names = await Db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => e.FullName);
			var cycles = await AprSvc.GetCyclesAsync(HrCompanyId);
			ViewBag.CycleNames = cycles.ToDictionary(c => c.ID, c => c.Name);
			return View(list);
		}

		[HttpPost]
		public async Task<IActionResult> SaveAppraisalCycle(int id, string name, string? nameEn, int year, DateTime? startDate, DateTime? endDate)
		{
			var (ok, err) = await AprSvc.SaveCycleAsync(new AppraisalCycle { ID = id, CompanyID = HrCompanyId, Name = name ?? "", NameEn = nameEn, Year = year <= 0 ? DateTime.Today.Year : year, StartDate = startDate, EndDate = endDate });
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Appraisal cycle saved"].Value : err;
			return RedirectToAction(nameof(Appraisals));
		}

		[HttpPost]
		public async Task<IActionResult> SetAppraisalCycleStatus(int id, string status)
		{
			await AprSvc.SetCycleStatusAsync(HrCompanyId, id, status);
			TempData["HrMsg"] = L["Cycle status updated"].Value;
			return RedirectToAction(nameof(Appraisals));
		}

		[HttpGet]
		public async Task<IActionResult> AppraisalTemplates()
		{
			return View(await AprSvc.GetTemplatesAsync(HrCompanyId));
		}

		[HttpGet]
		public async Task<IActionResult> AppraisalTemplateEditor(int? id)
		{
			var model = (id.HasValue && id.Value > 0 ? await AprSvc.GetTemplateAsync(HrCompanyId, id.Value) : null)
				?? new AppraisalTemplate { IsActive = true };
			return View(model);
		}

		[HttpPost]
		public async Task<IActionResult> SaveAppraisalTemplate(int id, string name, string? nameEn, bool isActive, string? criteriaJson)
		{
			List<AppraisalCriterion> criteria;
			try { criteria = System.Text.Json.JsonSerializer.Deserialize<List<AppraisalCriterion>>(criteriaJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { criteria = new(); }
			var (ok, err, newId) = await AprSvc.SaveTemplateAsync(new AppraisalTemplate { ID = id, CompanyID = HrCompanyId, Name = name ?? "", NameEn = nameEn, IsActive = isActive, Criteria = criteria });
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Appraisal template saved"].Value : err;
			return ok ? RedirectToAction(nameof(AppraisalTemplates)) : RedirectToAction(nameof(AppraisalTemplateEditor), new { id });
		}

		[HttpPost]
		public async Task<IActionResult> DeleteAppraisalTemplate(int id)
		{
			await AprSvc.DeleteTemplateAsync(HrCompanyId, id);
			TempData["HrMsg"] = L["Template deleted"].Value;
			return RedirectToAction(nameof(AppraisalTemplates));
		}

		[HttpPost]
		public async Task<IActionResult> CreateAppraisal(int cycleId, int templateId, int employeeId, int managerId)
		{
			var (ok, err, newId) = await AprSvc.CreateAppraisalAsync(HrCompanyId, cycleId, templateId, employeeId, managerId, null);
			if (!ok) { TempData["HrErr"] = err; return RedirectToAction(nameof(Appraisals), new { cycleId }); }
			TempData["HrMsg"] = L["Appraisal created — enter scores"].Value;
			return RedirectToAction(nameof(AppraisalScore), new { id = newId });
		}

		[HttpGet]
		public async Task<IActionResult> AppraisalScore(int id)
		{
			var appr = await AprSvc.GetAppraisalAsync(HrCompanyId, id);
			if (appr == null) { TempData["HrErr"] = L["Appraisal not found"].Value; return RedirectToAction(nameof(Appraisals)); }
			var tpl = await AprSvc.GetTemplateAsync(HrCompanyId, appr.TemplateId);
			ViewBag.Criteria = tpl?.Criteria ?? new List<AppraisalCriterion>();
			ViewBag.Names = await Db.Employee.AsNoTracking().Where(e => e.ID == appr.EmployeeID || e.ID == appr.ManagerEmployeeID).ToDictionaryAsync(e => e.ID, e => e.FullName);
			return View(appr);
		}

		[HttpPost]
		public async Task<IActionResult> SaveAppraisalScores(int id, string? scoresJson, string? managerComment, bool submit)
		{
			var scores = new Dictionary<int, decimal>(); var notes = new Dictionary<int, string?>();
			try
			{
				using var doc = System.Text.Json.JsonDocument.Parse(scoresJson ?? "[]");
				foreach (var el in doc.RootElement.EnumerateArray())
				{
					int cid = el.GetProperty("criterionId").GetInt32();
					decimal sc = el.TryGetProperty("score", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number ? s.GetDecimal() : 0m;
					string? nt = el.TryGetProperty("note", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null;
					scores[cid] = sc; notes[cid] = nt;
				}
			}
			catch { }
			var (ok, err) = await AprSvc.SaveScoresAsync(HrCompanyId, id, scores, notes, managerComment);
			if (ok && submit) { var (sok, serr) = await AprSvc.SubmitAsync(HrCompanyId, id); if (!sok) { TempData["HrErr"] = serr; return RedirectToAction(nameof(AppraisalScore), new { id }); } }
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? (submit ? L["Appraisal sent to employee"].Value : L["Scores saved"].Value) : err;
			return submit && ok ? RedirectToAction(nameof(Appraisals)) : RedirectToAction(nameof(AppraisalScore), new { id });
		}
	}
}
