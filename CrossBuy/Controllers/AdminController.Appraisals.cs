using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// HR-9 — performance appraisal (cycles, templates, scoring, submit). Employee acknowledgment lives in PeopleController (ESS).
	//
	// ────────────────────────────────────────────────────────────────────────────────────────────
	// EVERY ACTION ON THIS PARTIAL NOW PASSES THROUGH HrGateAsync, and that closes TWO defects at once
	// rather than one — which is why the diff touches reads as well as writes.
	//
	//   AUTHORIZATION. Six of these actions were in engineering/authorization-baseline.json as
	//   AuthorizationGap: SaveAppraisalCycle, SetAppraisalCycleStatus, SaveAppraisalTemplate,
	//   DeleteAppraisalTemplate, CreateAppraisal, SaveAppraisalScores. Appraisal content is somebody's
	//   performance record — the most sensitive non-payroll data HR holds — and any signed-in user could
	//   write it.
	//
	//   TENANT. Every one of them read `HrCompanyId`, which is `private const int HrCompanyId = 1`. Not a
	//   default that a resolver later overrode: a literal. So an appraisal cycle created by a user of
	//   company 41 was written to company 1, and the appraisal LIST that same user saw was company 1's.
	//   The already-gated actions elsewhere in this controller use `gate.CompanyId` instead, so the fix
	//   is to finish a pattern the file already contains rather than to introduce one.
	//
	// The action is HrActions.PerformanceManage throughout — the vocabulary's own comment reads
	// "appraisals, training, recruitment", so this is the existing semantics, not a new permission. It is
	// deliberately NOT ConfidentialView: that tier is never bootstrap-open, and putting ordinary appraisal
	// administration behind it would lock out every company that has configured no HR role. Where an
	// action names one employee, the gate carries a PermissionTarget for them so the record-level rule
	// applies instead of a bare role check.
	// ────────────────────────────────────────────────────────────────────────────────────────────
	public partial class AdminController
	{
		private CrossBuy.BL.IAppraisalService AprSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IAppraisalService)) as CrossBuy.BL.IAppraisalService)!;

		private async Task PopulateAppraisalListsAsync(int companyId)
		{
			ViewBag.Cycles = await AprSvc.GetCyclesAsync(companyId);
			ViewBag.Templates = await AprSvc.GetTemplatesAsync(companyId);
			// public DTO (NOT an anonymous type) so the view can bind @e.ID/@e.FullName dynamically across the Views assembly (avoids RuntimeBinderException)
			ViewBag.Employees = await Db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && e.IsActive)
				.OrderBy(e => e.FullName)
				.Select(e => new CrossBuy.ViewModel.EmployeeListItemDto { ID = e.ID, FullName = e.FullName, FullNameEn = e.FullNameEn }).ToListAsync();
		}

		[HttpGet]
		public async Task<IActionResult> Appraisals(int? cycleId)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Index));

			await PopulateAppraisalListsAsync(gate.CompanyId);
			ViewBag.CycleId = cycleId;
			var list = await AprSvc.GetAppraisalsAsync(gate.CompanyId, cycleId);
			var empIds = list.SelectMany(a => new[] { a.EmployeeID, a.ManagerEmployeeID }).Distinct().ToList();

			// The name lookup is company-bounded too. Without the second predicate an appraisal that
			// somehow referenced a foreign employee would resolve that person's name here — a small leak
			// through a convenience dictionary, which is exactly where they hide.
			ViewBag.Names = await Db.Employee.AsNoTracking()
				.Where(e => empIds.Contains(e.ID) && e.EmpCompanyID == gate.CompanyId)
				.ToDictionaryAsync(e => e.ID, e => CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn));
			var cycles = await AprSvc.GetCyclesAsync(gate.CompanyId);
			ViewBag.CycleNames = cycles.ToDictionary(c => c.ID, c => c.Name);
			return View(list);
		}

		[HttpPost]
		public async Task<IActionResult> SaveAppraisalCycle(int id, string name, string? nameEn, int year, DateTime? startDate, DateTime? endDate)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Appraisals));

			var (ok, err) = await AprSvc.SaveCycleAsync(new AppraisalCycle { ID = id, CompanyID = gate.CompanyId, Name = name ?? "", NameEn = nameEn, Year = year <= 0 ? DateTime.Today.Year : year, StartDate = startDate, EndDate = endDate });
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Appraisal cycle saved"].Value : err;
			return RedirectToAction(nameof(Appraisals));
		}

		[HttpPost]
		public async Task<IActionResult> SetAppraisalCycleStatus(int id, string status)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Appraisals));

			// The company travels INTO the service call, so a cycle id belonging to another company simply
			// does not match there. The id alone stops being sufficient.
			await AprSvc.SetCycleStatusAsync(gate.CompanyId, id, status);
			TempData["HrMsg"] = L["Cycle status updated"].Value;
			return RedirectToAction(nameof(Appraisals));
		}

		[HttpGet]
		public async Task<IActionResult> AppraisalTemplates()
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Index));

			return View(await AprSvc.GetTemplatesAsync(gate.CompanyId));
		}

		[HttpGet]
		public async Task<IActionResult> AppraisalTemplateEditor(int? id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Index));

			var model = (id.HasValue && id.Value > 0 ? await AprSvc.GetTemplateAsync(gate.CompanyId, id.Value) : null)
				?? new AppraisalTemplate { IsActive = true };
			return View(model);
		}

		[HttpPost]
		public async Task<IActionResult> SaveAppraisalTemplate(int id, string name, string? nameEn, bool isActive, string? criteriaJson)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(AppraisalTemplates));

			List<AppraisalCriterion> criteria;
			try { criteria = System.Text.Json.JsonSerializer.Deserialize<List<AppraisalCriterion>>(criteriaJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { criteria = new(); }
			var (ok, err, newId) = await AprSvc.SaveTemplateAsync(new AppraisalTemplate { ID = id, CompanyID = gate.CompanyId, Name = name ?? "", NameEn = nameEn, IsActive = isActive, Criteria = criteria });
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Appraisal template saved"].Value : err;
			return ok ? RedirectToAction(nameof(AppraisalTemplates)) : RedirectToAction(nameof(AppraisalTemplateEditor), new { id });
		}

		[HttpPost]
		public async Task<IActionResult> DeleteAppraisalTemplate(int id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(AppraisalTemplates));

			await AprSvc.DeleteTemplateAsync(gate.CompanyId, id);
			TempData["HrMsg"] = L["Template deleted"].Value;
			return RedirectToAction(nameof(AppraisalTemplates));
		}

		[HttpPost]
		public async Task<IActionResult> CreateAppraisal(int cycleId, int templateId, int employeeId, int managerId)
		{
			// SUBJECT-SCOPED. This action names the person being appraised, so the gate carries a
			// PermissionTarget for them: HrAccessService then checks that the subject belongs to the
			// caller's company by reading the Employee ROW, never the posted id. Appraising somebody in
			// another tenant is refused before the service is reached.
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(Appraisals), new { cycleId });

			var (ok, err, newId) = await AprSvc.CreateAppraisalAsync(gate.CompanyId, cycleId, templateId, employeeId, managerId, null);
			if (!ok) { TempData["HrErr"] = err; return RedirectToAction(nameof(Appraisals), new { cycleId }); }
			TempData["HrMsg"] = L["Appraisal created — enter scores"].Value;
			return RedirectToAction(nameof(AppraisalScore), new { id = newId });
		}

		[HttpGet]
		public async Task<IActionResult> AppraisalScore(int id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Appraisals));

			var appr = await AprSvc.GetAppraisalAsync(gate.CompanyId, id);
			if (appr == null) { TempData["HrErr"] = L["Appraisal not found"].Value; return RedirectToAction(nameof(Appraisals)); }
			var tpl = await AprSvc.GetTemplateAsync(gate.CompanyId, appr.TemplateId);
			ViewBag.Criteria = tpl?.Criteria ?? new List<AppraisalCriterion>();
			ViewBag.Names = await Db.Employee.AsNoTracking()
				.Where(e => (e.ID == appr.EmployeeID || e.ID == appr.ManagerEmployeeID) && e.EmpCompanyID == gate.CompanyId)
				.ToDictionaryAsync(e => e.ID, e => CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn));
			return View(appr);
		}

		[HttpPost]
		public async Task<IActionResult> SaveAppraisalScores(int id, string? scoresJson, string? managerComment, bool submit)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PerformanceManage);
			if (!gate.Ok) return HrDenied(nameof(Appraisals));

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
			var (ok, err) = await AprSvc.SaveScoresAsync(gate.CompanyId, id, scores, notes, managerComment);
			if (ok && submit) { var (sok, serr) = await AprSvc.SubmitAsync(gate.CompanyId, id); if (!sok) { TempData["HrErr"] = serr; return RedirectToAction(nameof(AppraisalScore), new { id }); } }
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? (submit ? L["Appraisal sent to employee"].Value : L["Scores saved"].Value) : err;
			return submit && ok ? RedirectToAction(nameof(Appraisals)) : RedirectToAction(nameof(AppraisalScore), new { id });
		}
	}
}
