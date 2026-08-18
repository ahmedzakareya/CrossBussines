using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Menu;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using CrossBuy.Models.Platform;

namespace CrossBuy.Controllers
{
	// Project analytic dimension + Projects & Contracting (P0). CRUD + profitability (revenue 4xxx − cost 5xxx from the GL
	// Project dimension). Contracting fields (customer/location/contract value/status/activity type/cost center) are
	// additive & nullable — attribution stays via ProjectId, no new accounting writer.
	[SessionValidation]
	public class ProjectController : Controller
	{
		private const int DefaultCompanyId = 1;
		private readonly IProjectService _projects;
		private readonly IBoqService _boq;
		private readonly IContractService _contract;
		private readonly IProgressService _progress;
		private readonly IProgressBillingService _billing;
		private readonly IProjectMaterialIssueService _material;
		private readonly IProjectLaborService _labor;
		private readonly IProjectBudgetService _budget;
		private readonly ISubcontractBillingService _subcontract;
		private readonly IVariationOrderService _variation;
		private readonly IEquipmentDepreciationService _equipDep;
		private readonly ICostCenterService _costCenters;
		private readonly CrossDbContext _db;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		private readonly IProjectsAccessService _projectsAccess;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _businessContexts;
		// D1 Wave 1: the validated company source (CORRECTION-005) and the accounting right that GL-posting needs.
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		// Concrete, not the interface: the interface exposes only the legacy session-based CanAsync(string).
		// Batch C set this precedent (ProjectsAccessService -> AccountingAccessService) when the interface-collection
		// form turned out to be a DI cycle.
		private readonly AccountingAccessService _accounting;

		public ProjectController(IProjectService projects, IBoqService boq, IContractService contract, IProgressService progress, IProgressBillingService billing, IProjectMaterialIssueService material, IProjectLaborService labor, IProjectBudgetService budget, ISubcontractBillingService subcontract, IVariationOrderService variation, IEquipmentDepreciationService equipDep, ICostCenterService costCenters, CrossDbContext db,
			// Stage 1 Batch C — projects access service + session-free context, for the BOQ proof endpoint.
			IProjectsAccessService projectsAccess, CrossBuy.BL.Platform.IBusinessContextAccessor businessContexts,
			CrossBuy.BL.Platform.IRequestCompanyResolver company, AccountingAccessService accounting,
			IStringLocalizer<CrossBuy.SharedResources> localizer)
		{ _projectsAccess = projectsAccess; _businessContexts = businessContexts; _projects = projects; _boq = boq; _contract = contract; _progress = progress; _billing = billing; _material = material; _labor = labor; _budget = budget; _subcontract = subcontract; _variation = variation; _equipDep = equipDep; _costCenters = costCenters; _db = db; L = localizer; _company = company; _accounting = accounting; }

		// Projects & Contracting is its OWN system → show its sidebar (not Accounting/Admin).
		public override void OnActionExecuting(ActionExecutingContext context)
		{
			ViewData["SidebarMenu"] = MainMenu.Projects();
			base.OnActionExecuting(context);
		}

		// =====================================================================================
		// STAGE 1 BATCH D1 WAVE 1 — THE PROJECT FINANCIAL GATE
		//
		// Before this wave, all 23 mutating project financial actions carried `[SessionValidation]` and nothing
		// else, and every one of them passed the compile-time constant `DefaultCompanyId` into its service. So any
		// signed-in employee could post progress billing and subcontractor billing to the general ledger, issue
		// project material (a STOCK movement), post equipment depreciation, receive advances and release retention.
		//
		// This helper is NOT an authorization rule. It RESOLVES the company (CORRECTION-005) and ASKS the two
		// approved access services; every predicate stays inside them, which is what keeps this from becoming the
		// "authorization copied into a controller" the brief forbids. `ProjectsAccessService` is what loads the
		// PROJECT ROW and compares ITS CompanyID — so the project, not the request and not a membership row, is
		// the ownership authority.
		//
		// `requireAccountingPost` is the second half of the rule for actions that reach the GL: a project right
		// alone must not let someone post a journal. Those actions need BOTH the project action AND accounting
		// "post" — derived from the existing vocabulary, not invented.
		private sealed class ProjectGate
		{
			public bool Ok;
			public int CompanyId;
			public IActionResult? Denied;
		}

		private async Task<ProjectGate> GateAsync(string action, int projectId, bool requireAccountingPost = false)
		{
			IActionResult Deny()
			{
				// One message for every refusal reason. An unresolved identity, a missing right, a foreign
				// project and a non-existent project must be indistinguishable, or the response becomes a probe.
				TempData["PrjErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Projects));
			}

			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return new ProjectGate { Denied = Deny() };

			var ctx = await _businessContexts.TryGetCurrentAsync();
			if (ctx == null) return new ProjectGate { Denied = Deny() };

			if (projectId <= 0) return new ProjectGate { Denied = Deny() };

			if (!await _projectsAccess.CanAsync(ctx, action, PermissionTarget.ForProject(projectId)))
				return new ProjectGate { Denied = Deny() };

			// Posting to the ledger is an accounting act performed from a project screen. The project right says
			// WHICH project; the accounting right says whether this person may post at all.
			if (requireAccountingPost && !await _accounting.CanAsync(ctx, "post"))
				return new ProjectGate { Denied = Deny() };

			return new ProjectGate { Ok = true, CompanyId = scope.CompanyId };
		}


		// dropdown data for the project form
		private async Task PopulateFormListsAsync()
		{
			ViewBag.Customers = await _db.Customers.AsNoTracking().Where(c => c.CompanyID == DefaultCompanyId)
				.OrderBy(c => c.Name).ToListAsync();
			ViewBag.CostCenters = await _costCenters.GetFlatAsync(DefaultCompanyId, activeOnly: true);
			ViewBag.ActivityTypes = await _projects.GetActivityTypesAsync(DefaultCompanyId, activeOnly: true);
		}

		// ===== Main dashboard (system landing) — live stats, no fabricated data =====
		[HttpGet]
		public async Task<IActionResult> Dashboard()
		{
			var companyId = DefaultCompanyId;
			var projects = await _db.Projects.AsNoTracking().Where(p => p.CompanyID == companyId).ToListAsync();
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			string Bucket(Project p) => string.IsNullOrEmpty(p.Status) ? (p.IsActive ? "Active" : "Draft") : p.Status!;

			var dto = new ProjectDashboardDto
			{
				Year = DateTime.Today.Year,
				Total = projects.Count,
				Active = projects.Count(p => Bucket(p) == "Active"),
				Completed = projects.Count(p => Bucket(p) == "Completed"),
				OnHold = projects.Count(p => Bucket(p) == "OnHold"),
				Draft = projects.Count(p => Bucket(p) == "Draft"),
				Cancelled = projects.Count(p => Bucket(p) == "Cancelled"),
				ContractValue = projects.Sum(p => p.ContractValue ?? 0m),
				Budget = projects.Sum(p => p.Budget ?? 0m),
			};

			// profitability YTD (revenue 4xxx − cost 5xxx tagged to the project dimension)
			var pnl = await _projects.ProfitabilityAsync(companyId, new DateTime(dto.Year, 1, 1), DateTime.Today);
			dto.Revenue = pnl.Sum(r => r.Revenue);
			dto.Cost = pnl.Sum(r => r.Cost);
			dto.TopByProfit = pnl.OrderByDescending(r => r.Profit).Take(6)
				.Select(r => new PrjPnlRow { Code = r.Code, Name = r.Name, Revenue = r.Revenue, Cost = r.Cost, Profit = r.Profit, Budget = r.Budget }).ToList();

			dto.StatusDist = new[] { "Active", "Completed", "OnHold", "Draft", "Cancelled" }
				.Select(s => new PrjStatusSlice { Key = s, Count = projects.Count(p => Bucket(p) == s) })
				.Where(x => x.Count > 0).ToList();

			var maxV = projects.Where(p => p.ContractValue.HasValue).Select(p => p.ContractValue!.Value).DefaultIfEmpty(0m).Max();
			dto.TopByValue = projects.Where(p => (p.ContractValue ?? 0m) > 0m).OrderByDescending(p => p.ContractValue).Take(6)
				.Select(p => new PrjBarRow { Name = p.Name, NameEn = p.NameEn, Code = p.Code, Value = p.ContractValue ?? 0m, Pct = maxV > 0m ? (int)Math.Round(100m * (p.ContractValue ?? 0m) / maxV) : 0 }).ToList();

			var custNames = await _db.Customers.AsNoTracking().Where(c => c.CompanyID == companyId).ToDictionaryAsync(c => c.ID, c => c.Name);
			dto.Recent = projects.OrderByDescending(p => p.CreatedAt ?? DateTime.MinValue).Take(7)
				.Select(p => new PrjRecentRow
				{
					Code = p.Code, Name = p.Name, NameEn = p.NameEn, Status = Bucket(p), ContractValue = p.ContractValue,
					Customer = p.CustomerId.HasValue && custNames.ContainsKey(p.CustomerId.Value) ? custNames[p.CustomerId.Value] : null,
					StartDate = p.StartDate, EndDate = p.EndDate
				}).ToList();

			return View(dto);
		}

		[HttpGet]
		public async Task<IActionResult> Projects(string? q, string? status)
		{
			bool? active = status == "active" ? true : status == "inactive" ? false : (bool?)null;
			ViewBag.Q = q; ViewBag.Status = status;
			await PopulateFormListsAsync();
			return View(await _projects.GetProjectsAsync(DefaultCompanyId, q, active));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveProject(int id, string code, string name, string? nameEn, bool isActive,
			DateTime? startDate, DateTime? endDate, decimal? budget,
			int? customerId, string? location, decimal? contractValue, string? status, int? activityTypeId, int? costCenterId,
			decimal? advancePercent, decimal? retentionPercent)
		{
			var (ok, err, _) = await _projects.SaveAsync(new Project
			{
				ID = id, CompanyID = DefaultCompanyId, Code = code ?? "", Name = name ?? "", NameEn = nameEn ?? "",
				IsActive = isActive, StartDate = startDate, EndDate = endDate, Budget = budget,
				CustomerId = customerId, Location = location, ContractValue = contractValue,
				Status = status, ActivityTypeId = activityTypeId, CostCenterId = costCenterId,
				AdvancePercent = advancePercent, RetentionPercent = retentionPercent
			});
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Project saved"].Value : err;
			return RedirectToAction(nameof(Projects));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteProject(int id)
		{
			var (ok, err) = await _projects.DeleteAsync(DefaultCompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Project deleted"].Value : err;
			return RedirectToAction(nameof(Projects));
		}

		[HttpGet]
		public async Task<IActionResult> Profitability(DateTime? from, DateTime? to)
		{
			var f = from ?? new DateTime(DateTime.Today.Year, 1, 1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t;
			return View(await _projects.ProfitabilityAsync(DefaultCompanyId, f, t));
		}

		// ---- BOQ (P1): inline editor per project ----
		[HttpGet]
		public async Task<IActionResult> Boq(int id)
		{
			// ===== Stage 1 Batch C proof endpoint (Projects) =====
			// Before this batch there was no project record-level access AT ALL — `Project` had no member,
			// manager or owner column, so any signed-in employee could open any project's BOQ. ProjectMembers
			// is the relationship that makes this decidable; the project's company comes from the PROJECT ROW.
			var prjContext = await _businessContexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (prjContext == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }

			bool mayRead = await _projectsAccess.CanAsync(
				prjContext, ProjectsActions.Read,
				PermissionTarget.ForProject(id, prjContext.CompanyId),
				HttpContext.RequestAborted);
			// Refused and absent answer IDENTICALLY, so project ids cannot be enumerated by probing.
			if (!mayRead) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }

			var prj = await _projects.GetAsync(DefaultCompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Summary = await _boq.GetSummaryAsync(DefaultCompanyId, id);
			return View(await _boq.GetForProjectAsync(DefaultCompanyId, id));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveBoq(int projectId, string rowsJson)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId);
			if (!gate.Ok) return gate.Denied!;

			List<BoqRowInput> rows;
			try { rows = System.Text.Json.JsonSerializer.Deserialize<List<BoqRowInput>>(rowsJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { rows = new(); }
			var (ok, err, count) = await _boq.ReplaceAllAsync(gate.CompanyId, projectId, rows);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? string.Format(L["BOQ saved ({0} items)"].Value, count) : err;
			return RedirectToAction(nameof(Boq), new { id = projectId });
		}

		// ---- P2: advance receipt (Dr cash · Cr 2104 via ContractService) ----
		[HttpGet]
		public async Task<IActionResult> Advance(int? projectId)
		{
			ViewBag.Projects = await _projects.GetProjectsAsync(DefaultCompanyId, null, true);
			ViewBag.CashAccounts = await _db.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == DefaultCompanyId && a.IsPostable && a.IsActive && a.Code.StartsWith("1101"))
				.OrderBy(a => a.Code).ToListAsync();
			var advAcc = await _db.Accounts.Where(a => a.CompanyID == DefaultCompanyId && a.Code == ContractService.AdvanceAccountCode).Select(a => a.ID).FirstOrDefaultAsync();
			var bals = await (from l in _db.JournalEntryLines.AsNoTracking()
							  join en in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals en.ID
							  where en.CompanyID == DefaultCompanyId && en.Status == "Posted" && l.AccountId == advAcc && l.ProjectId != null
							  group l by l.ProjectId!.Value into g
							  select new { pid = g.Key, bal = g.Sum(x => x.Credit - x.Debit) }).ToListAsync();
			ViewBag.AdvBalances = bals.ToDictionary(x => x.pid, x => Math.Round(x.bal, 2));
			ViewBag.SelectedProjectId = projectId;
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ReceiveAdvance(int projectId, string amount, int cashAccountId, DateTime date)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			// <input type=number> always posts a dot-decimal value; parse invariantly so the ar request culture
			// (whose decimal separator is "٫") doesn't silently bind it to 0.
			decimal.TryParse(amount, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amt);
			var (ok, err, _) = await _contract.ReceiveAdvanceAsync(gate.CompanyId, projectId, amt, cashAccountId, date, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Advance received & posted"].Value : err;
			return RedirectToAction(nameof(Advance), new { projectId });
		}

		// ---- P6-ب: retention release (Dr cash · Cr 1104 via ContractService) ----
		[HttpGet]
		public async Task<IActionResult> RetentionRelease(int? projectId)
		{
			ViewBag.Projects = await _projects.GetProjectsAsync(DefaultCompanyId, null, true);
			ViewBag.CashAccounts = await _db.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == DefaultCompanyId && a.IsPostable && a.IsActive && a.Code.StartsWith("1101"))
				.OrderBy(a => a.Code).ToListAsync();
			var retAcc = await _db.Accounts.Where(a => a.CompanyID == DefaultCompanyId && a.Code == ContractService.RetentionAccountCode).Select(a => a.ID).FirstOrDefaultAsync();
			var bals = await (from l in _db.JournalEntryLines.AsNoTracking()
							  join en in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals en.ID
							  where en.CompanyID == DefaultCompanyId && en.Status == "Posted" && l.AccountId == retAcc && l.ProjectId != null
							  group l by l.ProjectId!.Value into g
							  select new { pid = g.Key, bal = g.Sum(x => x.Debit - x.Credit) }).ToListAsync();   // asset = debit-normal
			ViewBag.RetBalances = bals.ToDictionary(x => x.pid, x => Math.Round(x.bal, 2));
			ViewBag.SelectedProjectId = projectId;
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ReleaseRetention(int projectId, string amount, int cashAccountId, DateTime date)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			decimal.TryParse(amount, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amt);
			var (ok, err, _) = await _contract.ReleaseRetentionAsync(gate.CompanyId, projectId, amt, cashAccountId, date, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Retention released & posted"].Value : err;
			return RedirectToAction(nameof(RetentionRelease), new { projectId });
		}

		// ---- P6-ج-2: subcontractor retention release (Dr 2105 · Cr cash via ContractService) ----
		[HttpGet]
		public async Task<IActionResult> SubRetentionRelease(int? projectId)
		{
			ViewBag.Projects = await _projects.GetProjectsAsync(DefaultCompanyId, null, true);
			ViewBag.CashAccounts = await _db.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == DefaultCompanyId && a.IsPostable && a.IsActive && a.Code.StartsWith("1101"))
				.OrderBy(a => a.Code).ToListAsync();
			var retAcc = await _db.Accounts.Where(a => a.CompanyID == DefaultCompanyId && a.Code == ContractService.SubRetentionAccountCode).Select(a => a.ID).FirstOrDefaultAsync();
			var bals = await (from l in _db.JournalEntryLines.AsNoTracking()
							  join en in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals en.ID
							  where en.CompanyID == DefaultCompanyId && en.Status == "Posted" && l.AccountId == retAcc && l.ProjectId != null
							  group l by l.ProjectId!.Value into g
							  select new { pid = g.Key, bal = g.Sum(x => x.Credit - x.Debit) }).ToListAsync();   // liability = credit-normal
			ViewBag.RetBalances = bals.ToDictionary(x => x.pid, x => Math.Round(x.bal, 2));
			ViewBag.SelectedProjectId = projectId;
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ReleaseSubRetention(int projectId, string amount, int cashAccountId, DateTime date)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			decimal.TryParse(amount, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amt);
			var (ok, err, _) = await _contract.ReleaseSubRetentionAsync(gate.CompanyId, projectId, amt, cashAccountId, date, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Subcontractor retention released & posted"].Value : err;
			return RedirectToAction(nameof(SubRetentionRelease), new { projectId });
		}

		// ---- P3: execution / progress (operational — no GL) ----
		[HttpGet]
		public async Task<IActionResult> Progress(int id, int? m)
		{
			var prj = await _projects.GetAsync(DefaultCompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Measurements = await _progress.GetMeasurementsAsync(DefaultCompanyId, id);
			return View(await _progress.BuildEditModelAsync(DefaultCompanyId, id, m));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveProgress(int projectId, int measurementId, DateTime measurementDate, string? note, string rowsJson)
		{
			List<ProgressRowInput> rows;
			try { rows = System.Text.Json.JsonSerializer.Deserialize<List<ProgressRowInput>>(rowsJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { rows = new(); }
			var (ok, err, sid) = await _progress.SaveMeasurementAsync(DefaultCompanyId, projectId, measurementId, measurementDate, note, rows, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress measurement saved"].Value : err;
			return RedirectToAction(nameof(Progress), new { id = projectId, m = ok ? sid : measurementId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ConfirmProgress(int id, int projectId)
		{
			var (ok, err) = await _progress.ConfirmAsync(DefaultCompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Measurement confirmed"].Value : err;
			return RedirectToAction(nameof(Progress), new { id = projectId, m = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteProgress(int id, int projectId)
		{
			var (ok, err) = await _progress.DeleteAsync(DefaultCompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Measurement deleted"].Value : err;
			return RedirectToAction(nameof(Progress), new { id = projectId });
		}

		// ---- P4: progress billing (المستخلص) — invoice + retention/advance settlements via existing services ----
		[HttpGet]
		public async Task<IActionResult> Billing(int id, int? m, int? b, decimal? taxRate, bool neu = false)
		{
			var prj = await _projects.GetAsync(DefaultCompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Billings = await _billing.GetBillingsAsync(DefaultCompanyId, id);
			// show the editor only when the user explicitly starts a new billing (neu) or opens an existing one (b/m);
			// otherwise the landing shows the list + a CTA, so «New billing» is a visible action, not a silent refresh.
			ViewBag.ShowEditor = neu || (b.HasValue && b.Value > 0) || (m.HasValue && m.Value > 0);
			return View(await _billing.BuildPreviewAsync(DefaultCompanyId, id, m, b, taxRate));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveBilling(int projectId, int billingId, int progressId, DateTime billingDate, decimal taxRate, string? note)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err, sid) = await _billing.SaveDraftAsync(gate.CompanyId, projectId, billingId, progressId, billingDate, taxRate, note, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing saved"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId, b = ok ? sid : billingId, m = progressId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ApproveBilling(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _billing.ApproveAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing approved"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId, b = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> PostBilling(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _billing.PostAsync(gate.CompanyId, id, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing posted"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId, b = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteBilling(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _billing.DeleteAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing deleted"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId });
		}

		// ---- P5-أ: project material issue (actual material cost via StockService) ----
		[HttpGet]
		public async Task<IActionResult> MaterialIssues(int id, int? b, int? w)
		{
			var prj = await _projects.GetAsync(DefaultCompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Issues = await _material.GetIssuesAsync(DefaultCompanyId, id);
			ViewBag.Warehouses = await _db.Warehouses.AsNoTracking().Where(x => x.CompanyID == DefaultCompanyId).OrderBy(x => x.Code).ToListAsync();
			ViewBag.BoqItems = await _db.BoqItems.AsNoTracking().Where(x => x.CompanyID == DefaultCompanyId && x.ProjectId == id).OrderBy(x => x.SortOrder).ToListAsync();
			ViewBag.SelectedWarehouseId = w;
			ViewBag.EditIssue = b.HasValue && b.Value > 0 ? await _material.GetAsync(DefaultCompanyId, b.Value) : null;
			if (w.HasValue && w.Value > 0)
				ViewBag.Balances = await (from sb in _db.StockBalances.AsNoTracking()
										  join it in _db.Items.AsNoTracking() on sb.ItemId equals it.ID
										  where sb.CompanyID == DefaultCompanyId && sb.WarehouseId == w.Value && sb.QtyOnHand > 0
										  orderby it.ItemCode
										  select new ProjectStockRow { ItemId = sb.ItemId, ItemCode = it.ItemCode, Name = it.Name, NameEn = it.NameEn, QtyOnHand = sb.QtyOnHand }).ToListAsync();
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveMaterialIssue(int projectId, int issueId, int warehouseId, DateTime issueDate, string? note, string rowsJson)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId);
			if (!gate.Ok) return gate.Denied!;

			List<MaterialLineInput> rows;
			try { rows = System.Text.Json.JsonSerializer.Deserialize<List<MaterialLineInput>>(rowsJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { rows = new(); }
			var (ok, err, sid) = await _material.SaveDraftAsync(gate.CompanyId, projectId, issueId, issueDate, warehouseId, note, rows, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Material issue saved"].Value : err;
			return RedirectToAction(nameof(MaterialIssues), new { id = projectId, b = ok ? sid : issueId, w = warehouseId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> PostMaterialIssue(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _material.PostAsync(gate.CompanyId, id, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Material issue posted"].Value : err;
			return RedirectToAction(nameof(MaterialIssues), new { id = projectId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteMaterialIssue(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _material.DeleteAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Material issue deleted"].Value : err;
			return RedirectToAction(nameof(MaterialIssues), new { id = projectId });
		}

		// ---- P5-ب: project labor (timesheet hours × hourly cost → Dr 510104 / Cr 520101, ProjectId on debit) ----
		[HttpGet]
		public async Task<IActionResult> Labor(int id)
		{
			var prj = await _projects.GetAsync(DefaultCompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			return View(await _labor.GetProjectLaborAsync(DefaultCompanyId, id));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> PostLabor(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _labor.PostTaskLaborAsync(gate.CompanyId, id, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Labor posted"].Value : err;
			return RedirectToAction(nameof(Labor), new { id = projectId });
		}

		// ---- P6-أ: budget-vs-actual report (read-only) ----
		[HttpGet]
		public async Task<IActionResult> Budget(int id)
		{
			var prj = await _projects.GetAsync(DefaultCompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			return View(await _budget.GetBudgetVsActualAsync(DefaultCompanyId, id));
		}

		// ---- P6-ج: subcontractor / equipment billing (via PayableService) ----
		[HttpGet]
		public async Task<IActionResult> Subcontracts(int id, int? sc, int? b, decimal? cumulativeWork, decimal? taxRate)
		{
			var prj = await _projects.GetAsync(DefaultCompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Subcontracts = await _subcontract.GetSubcontractsAsync(DefaultCompanyId, id);
			ViewBag.Vendors = await _db.Vendors.AsNoTracking().Where(v => v.CompanyID == DefaultCompanyId && v.IsActive).OrderBy(v => v.Name).ToListAsync();
			ViewBag.SelectedSubcontractId = sc;
			if (sc.HasValue && sc.Value > 0)
			{
				ViewBag.Billings = await _subcontract.GetBillingsAsync(DefaultCompanyId, sc.Value);
				ViewBag.Preview = await _subcontract.BuildPreviewAsync(DefaultCompanyId, sc.Value, b, cumulativeWork, taxRate);
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveSubcontract(int projectId, int subcontractId, int vendorId, string? description, decimal? contractValue, decimal? retentionPercent)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err, sid) = await _subcontract.SaveSubcontractAsync(gate.CompanyId, projectId, subcontractId, vendorId, description, contractValue, retentionPercent, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Subcontract saved"].Value : err;
			return RedirectToAction(nameof(Subcontracts), new { id = projectId, sc = ok ? sid : subcontractId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveSubBilling(int projectId, int subcontractId, int billingId, DateTime billingDate, decimal cumulativeWork, decimal taxRate, string? note)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err, bid) = await _subcontract.SaveBillingDraftAsync(gate.CompanyId, subcontractId, billingId, billingDate, cumulativeWork, taxRate, note, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing saved"].Value : err;
			return RedirectToAction(nameof(Subcontracts), new { id = projectId, sc = subcontractId, b = ok ? bid : billingId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ApproveSubBilling(int id, int projectId, int subcontractId)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _subcontract.ApproveBillingAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing approved"].Value : err;
			return RedirectToAction(nameof(Subcontracts), new { id = projectId, sc = subcontractId, b = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> PostSubBilling(int id, int projectId, int subcontractId)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _subcontract.PostBillingAsync(gate.CompanyId, id, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing posted"].Value : err;
			return RedirectToAction(nameof(Subcontracts), new { id = projectId, sc = subcontractId, b = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteSubBilling(int id, int projectId, int subcontractId)
		{
			var gate = await GateAsync(ProjectsActions.Billing, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _subcontract.DeleteBillingAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing deleted"].Value : err;
			return RedirectToAction(nameof(Subcontracts), new { id = projectId, sc = subcontractId });
		}

		// ---- P6-د: variation orders (operational scope change — zero GL) ----
		[HttpGet]
		public async Task<IActionResult> VariationOrders(int id, int? vo)
		{
			var prj = await _projects.GetAsync(DefaultCompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Orders = await _variation.GetForProjectAsync(DefaultCompanyId, id);
			ViewBag.Revised = await _variation.RevisedContractValueAsync(DefaultCompanyId, id);
			ViewBag.BoqItems = await _db.BoqItems.AsNoTracking().Where(x => x.CompanyID == DefaultCompanyId && x.ProjectId == id).OrderBy(x => x.SortOrder).ToListAsync();
			ViewBag.EditOrder = vo.HasValue && vo.Value > 0 ? await _variation.GetAsync(DefaultCompanyId, vo.Value) : null;
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveVariationOrder(int projectId, int voId, string? description, string? descriptionEn, string? reason, string rowsJson)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId);
			if (!gate.Ok) return gate.Denied!;

			List<VoLineInput> rows;
			try { rows = System.Text.Json.JsonSerializer.Deserialize<List<VoLineInput>>(rowsJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { rows = new(); }
			var (ok, err, sid) = await _variation.SaveDraftAsync(gate.CompanyId, projectId, voId, description, descriptionEn, reason, rows, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Variation order saved"].Value : err;
			return RedirectToAction(nameof(VariationOrders), new { id = projectId, vo = ok ? sid : voId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ApproveVariationOrder(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _variation.ApproveAsync(gate.CompanyId, id, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Variation order approved"].Value : err;
			return RedirectToAction(nameof(VariationOrders), new { id = projectId, vo = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteVariationOrder(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _variation.DeleteAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Variation order deleted"].Value : err;
			return RedirectToAction(nameof(VariationOrders), new { id = projectId });
		}

		// ---- P6-هـ: owned-equipment depreciation allocation (Dr 510104[proj]/Cr 520103 reclass — no touch to dep schedule) ----
		[HttpGet]
		public async Task<IActionResult> EquipmentDepreciation(int id, int? a)
		{
			var prj = await _projects.GetAsync(DefaultCompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Allocations = await _equipDep.GetForProjectAsync(DefaultCompanyId, id);
			ViewBag.Assets = await _equipDep.GetAssetsAsync(DefaultCompanyId);
			ViewBag.EditAlloc = a.HasValue && a.Value > 0 ? await _equipDep.GetAsync(DefaultCompanyId, a.Value) : null;
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveEquipmentDepreciation(int projectId, int allocId, int fixedAssetId, DateTime periodDate, decimal? hours, decimal? rate, decimal amount, string? note)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId);
			if (!gate.Ok) return gate.Denied!;

			var input = new EquipmentAllocInput { FixedAssetId = fixedAssetId, PeriodDate = periodDate, Hours = hours, Rate = rate, Amount = amount, Note = note };
			var (ok, err, sid) = await _equipDep.SaveDraftAsync(gate.CompanyId, projectId, allocId, input, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Depreciation allocation saved"].Value : err;
			return RedirectToAction(nameof(EquipmentDepreciation), new { id = projectId, a = ok ? sid : allocId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> PostEquipmentDepreciation(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _equipDep.PostAsync(gate.CompanyId, id, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Depreciation allocation posted"].Value : err;
			return RedirectToAction(nameof(EquipmentDepreciation), new { id = projectId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteEquipmentDepreciation(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BudgetManage, projectId, requireAccountingPost: true);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _equipDep.DeleteAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Depreciation allocation deleted"].Value : err;
			return RedirectToAction(nameof(EquipmentDepreciation), new { id = projectId });
		}

		// ---- Project activity types (user-defined lookup) ----
		[HttpGet]
		public async Task<IActionResult> ActivityTypes()
			=> View(await _projects.GetActivityTypesAsync(DefaultCompanyId));

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveActivityType(int id, string code, string name, string? nameEn, bool isActive)
		{
			var (ok, err, _) = await _projects.SaveActivityTypeAsync(new ProjectActivityType
			{ ID = id, CompanyID = DefaultCompanyId, Code = code ?? "", Name = name ?? "", NameEn = nameEn ?? "", IsActive = isActive });
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Saved"].Value : err;
			return RedirectToAction(nameof(ActivityTypes));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteActivityType(int id)
		{
			var (ok, err) = await _projects.DeleteActivityTypeAsync(DefaultCompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Deleted"].Value : err;
			return RedirectToAction(nameof(ActivityTypes));
		}
	}
}
