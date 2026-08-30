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
		// Batch: Projects foundation. The writer for dbo.ProjectMembers - see ProjectMembershipService.
		private readonly IProjectMembershipService _members;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _businessContexts;
		// D1 Wave 1: the validated company source (CORRECTION-005) and the accounting right that GL-posting needs.
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		// Concrete, not the interface: the interface exposes only the legacy session-based CanAsync(string).
		// Batch C set this precedent (ProjectsAccessService -> AccountingAccessService) when the interface-collection
		// form turned out to be a DI cycle.
		private readonly AccountingAccessService _accounting;

		public ProjectController(IProjectService projects, IBoqService boq, IContractService contract, IProgressService progress, IProgressBillingService billing, IProjectMaterialIssueService material, IProjectLaborService labor, IProjectBudgetService budget, ISubcontractBillingService subcontract, IVariationOrderService variation, IEquipmentDepreciationService equipDep, ICostCenterService costCenters, CrossDbContext db,
			// Stage 1 Batch C — projects access service + session-free context, for the BOQ proof endpoint.
			IProjectsAccessService projectsAccess, IProjectMembershipService members, CrossBuy.BL.Platform.IBusinessContextAccessor businessContexts,
			CrossBuy.BL.Platform.IRequestCompanyResolver company, AccountingAccessService accounting,
			IStringLocalizer<CrossBuy.SharedResources> localizer)
		{ _projectsAccess = projectsAccess; _members = members; _businessContexts = businessContexts; _projects = projects; _boq = boq; _contract = contract; _progress = progress; _billing = billing; _material = material; _labor = labor; _budget = budget; _subcontract = subcontract; _variation = variation; _equipDep = equipDep; _costCenters = costCenters; _db = db; L = localizer; _company = company; _accounting = accounting; }

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
			// The ACTOR, resolved from the authenticated BusinessContext by the gate itself. Actions take
			// it from here and never from the request, so no posted field can carry an actor id in.
			public int ActorEmployeeId;
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

			// No resolved employee means no attributable actor. Refuse rather than record a null one.
			if (ctx.EmployeeId is not > 0) return new ProjectGate { Denied = Deny() };

			if (projectId <= 0) return new ProjectGate { Denied = Deny() };

			if (!await _projectsAccess.CanAsync(ctx, action, PermissionTarget.ForProject(projectId)))
				return new ProjectGate { Denied = Deny() };

			// Posting to the ledger is an accounting act performed from a project screen. The project right says
			// WHICH project; the accounting right says whether this person may post at all.
			if (requireAccountingPost && !await _accounting.CanAsync(ctx, "post"))
				return new ProjectGate { Denied = Deny() };

			return new ProjectGate { Ok = true, CompanyId = scope.CompanyId, ActorEmployeeId = ctx.EmployeeId!.Value };
		}


		// COMPANY-LEVEL GATE - for the screens and lookups that are not about one project.
		//
		// Same two steps as GateAsync and the same single refusal, minus the record: it resolves the
		// company server-side and asks ProjectsAccessService for a MODULE-level right. Passing no target
		// is what makes it module-level - membership is evaluated against a project, so a member cannot
		// satisfy a check that names no project. That is the property the list and lookup screens need.
		private async Task<ProjectGate> CompanyGateAsync(string action)
		{
			IActionResult Deny()
			{
				TempData["PrjErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Projects));
			}

			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return new ProjectGate { Denied = Deny() };

			var ctx = await _businessContexts.TryGetCurrentAsync();
			if (ctx == null) return new ProjectGate { Denied = Deny() };

			// No resolved employee means no attributable actor. Refuse rather than record a null one.
			if (ctx.EmployeeId is not > 0) return new ProjectGate { Denied = Deny() };

			if (!await _projectsAccess.CanAsync(ctx, action, null))
				return new ProjectGate { Denied = Deny() };

			return new ProjectGate { Ok = true, CompanyId = scope.CompanyId, ActorEmployeeId = ctx.EmployeeId!.Value };
		}

		// dropdown data for the project form
		private async Task PopulateFormListsAsync(int companyId)
		{
			ViewBag.Customers = await _db.Customers.AsNoTracking().Where(c => c.CompanyID == companyId)
				.OrderBy(c => c.Name).ToListAsync();
			ViewBag.CostCenters = await _costCenters.GetFlatAsync(companyId, activeOnly: true);
			ViewBag.ActivityTypes = await _projects.GetActivityTypesAsync(companyId, activeOnly: true);
		}

		// ===== Main dashboard (system landing) — live stats, no fabricated data =====
		[HttpGet]
		public async Task<IActionResult> Dashboard()
		{
			var gate = await CompanyGateAsync(ProjectsActions.Read);
			if (!gate.Ok) return gate.Denied!;
			var companyId = gate.CompanyId;
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
			var gate = await CompanyGateAsync(ProjectsActions.Read);
			if (!gate.Ok) return gate.Denied!;
			bool? active = status == "active" ? true : status == "inactive" ? false : (bool?)null;
			ViewBag.Q = q; ViewBag.Status = status;
			await PopulateFormListsAsync(gate.CompanyId);
			return View(await _projects.GetProjectsAsync(gate.CompanyId, q, active));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveProject(int id, string code, string name, string? nameEn, bool isActive,
			DateTime? startDate, DateTime? endDate, decimal? budget,
			int? customerId, string? location, decimal? contractValue, string? status, int? activityTypeId, int? costCenterId,
			decimal? advancePercent, decimal? retentionPercent)
		{
			// Creating a project is a MODULE right and editing one is a RECORD right, so this action asks
			// two different questions. `create` is deliberately absent from the membership path in
			// ProjectsAccessService - you cannot be a member of a project that does not exist yet - so a
			// non-role holder cannot create, while a project Member may still edit the project they are on.
			var gate = id > 0
				? await GateAsync(ProjectsActions.Edit, id)
				: await CompanyGateAsync(ProjectsActions.Create);
			if (!gate.Ok) return gate.Denied!;
			var (ok, err, _) = await _projects.SaveAsync(new Project
			{
				ID = id, CompanyID = gate.CompanyId, Code = code ?? "", Name = name ?? "", NameEn = nameEn ?? "",
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
			// `close` and not `manage`: ProjectsAccessService returns false for close on the membership
			// path, so deleting a project stays with the module role even for that project's own manager.
			// Destroying the record is at least as administrative as freezing it.
			var gate = await GateAsync(ProjectsActions.Close, id);
			if (!gate.Ok) return gate.Denied!;
			var (ok, err) = await _projects.DeleteAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Project deleted"].Value : err;
			return RedirectToAction(nameof(Projects));
		}

		[HttpGet]
		public async Task<IActionResult> Profitability(DateTime? from, DateTime? to)
		{
			var gate = await CompanyGateAsync(ProjectsActions.BudgetView);
			if (!gate.Ok) return gate.Denied!;
			var f = from ?? new DateTime(DateTime.Today.Year, 1, 1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t;
			return View(await _projects.ProfitabilityAsync(gate.CompanyId, f, t));
		}

		// ---- BOQ (P1): inline editor per project ----
		[HttpGet]
		public async Task<IActionResult> Boq(int id)
		{
			var gate = await GateAsync(ProjectsActions.BudgetView, id);
			if (!gate.Ok) return gate.Denied!;
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

			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Summary = await _boq.GetSummaryAsync(gate.CompanyId, id);
			return View(await _boq.GetForProjectAsync(gate.CompanyId, id));
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
			var gate = await CompanyGateAsync(ProjectsActions.BudgetView);
			if (!gate.Ok) return gate.Denied!;
			ViewBag.Projects = await _projects.GetProjectsAsync(gate.CompanyId, null, true);
			ViewBag.CashAccounts = await _db.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == gate.CompanyId && a.IsPostable && a.IsActive && a.Code.StartsWith("1101"))
				.OrderBy(a => a.Code).ToListAsync();
			var advAcc = await _db.Accounts.Where(a => a.CompanyID == gate.CompanyId && a.Code == ContractService.AdvanceAccountCode).Select(a => a.ID).FirstOrDefaultAsync();
			var bals = await (from l in _db.JournalEntryLines.AsNoTracking()
							  join en in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals en.ID
							  where en.CompanyID == gate.CompanyId && en.Status == "Posted" && l.AccountId == advAcc && l.ProjectId != null
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
			var gate = await CompanyGateAsync(ProjectsActions.BudgetView);
			if (!gate.Ok) return gate.Denied!;
			ViewBag.Projects = await _projects.GetProjectsAsync(gate.CompanyId, null, true);
			ViewBag.CashAccounts = await _db.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == gate.CompanyId && a.IsPostable && a.IsActive && a.Code.StartsWith("1101"))
				.OrderBy(a => a.Code).ToListAsync();
			var retAcc = await _db.Accounts.Where(a => a.CompanyID == gate.CompanyId && a.Code == ContractService.RetentionAccountCode).Select(a => a.ID).FirstOrDefaultAsync();
			var bals = await (from l in _db.JournalEntryLines.AsNoTracking()
							  join en in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals en.ID
							  where en.CompanyID == gate.CompanyId && en.Status == "Posted" && l.AccountId == retAcc && l.ProjectId != null
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
			var gate = await CompanyGateAsync(ProjectsActions.BudgetView);
			if (!gate.Ok) return gate.Denied!;
			ViewBag.Projects = await _projects.GetProjectsAsync(gate.CompanyId, null, true);
			ViewBag.CashAccounts = await _db.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == gate.CompanyId && a.IsPostable && a.IsActive && a.Code.StartsWith("1101"))
				.OrderBy(a => a.Code).ToListAsync();
			var retAcc = await _db.Accounts.Where(a => a.CompanyID == gate.CompanyId && a.Code == ContractService.SubRetentionAccountCode).Select(a => a.ID).FirstOrDefaultAsync();
			var bals = await (from l in _db.JournalEntryLines.AsNoTracking()
							  join en in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals en.ID
							  where en.CompanyID == gate.CompanyId && en.Status == "Posted" && l.AccountId == retAcc && l.ProjectId != null
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
			var gate = await GateAsync(ProjectsActions.Read, id);
			if (!gate.Ok) return gate.Denied!;
			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Measurements = await _progress.GetMeasurementsAsync(gate.CompanyId, id);
			return View(await _progress.BuildEditModelAsync(gate.CompanyId, id, m));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveProgress(int projectId, int measurementId, DateTime measurementDate, string? note, string rowsJson)
		{
			var gate = await GateAsync(ProjectsActions.Edit, projectId);
			if (!gate.Ok) return gate.Denied!;
			List<ProgressRowInput> rows;
			try { rows = System.Text.Json.JsonSerializer.Deserialize<List<ProgressRowInput>>(rowsJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { rows = new(); }
			var (ok, err, sid) = await _progress.SaveMeasurementAsync(gate.CompanyId, projectId, measurementId, measurementDate, note, rows, null);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress measurement saved"].Value : err;
			return RedirectToAction(nameof(Progress), new { id = projectId, m = ok ? sid : measurementId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ConfirmProgress(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.Edit, projectId);
			if (!gate.Ok) return gate.Denied!;
			var (ok, err) = await _progress.ConfirmAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Measurement confirmed"].Value : err;
			return RedirectToAction(nameof(Progress), new { id = projectId, m = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteProgress(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.Edit, projectId);
			if (!gate.Ok) return gate.Denied!;
			var (ok, err) = await _progress.DeleteAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Measurement deleted"].Value : err;
			return RedirectToAction(nameof(Progress), new { id = projectId });
		}

		// ---- P4: progress billing (المستخلص) — invoice + retention/advance settlements via existing services ----
		[HttpGet]
		public async Task<IActionResult> Billing(int id, int? m, int? b, decimal? taxRate, bool neu = false)
		{
			var gate = await GateAsync(ProjectsActions.BudgetView, id);
			if (!gate.Ok) return gate.Denied!;
			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Billings = await _billing.GetBillingsAsync(gate.CompanyId, id);
			// show the editor only when the user explicitly starts a new billing (neu) or opens an existing one (b/m);
			// otherwise the landing shows the list + a CTA, so «New billing» is a visible action, not a silent refresh.
			ViewBag.ShowEditor = neu || (b.HasValue && b.Value > 0) || (m.HasValue && m.Value > 0);
			return View(await _billing.BuildPreviewAsync(gate.CompanyId, id, m, b, taxRate, gate.ActorEmployeeId));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveBilling(int projectId, int billingId, int progressId, DateTime billingDate, decimal taxRate, string? note)
		{
			var gate = await GateAsync(ProjectsActions.BillingPrepare, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err, sid) = await _billing.SaveDraftAsync(gate.CompanyId, projectId, billingId, progressId, billingDate, taxRate, note, gate.ActorEmployeeId);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing saved"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId, b = ok ? sid : billingId, m = progressId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ApproveBilling(int id, int projectId)
		{
			// billing-approve, NOT billing-post: approving creates no ledger effect, so it must not demand the
			// accounting posting right. Whether THIS approver may approve THIS billing (the preparer may not)
			// is decided by the service against the record - a module right cannot answer that.
			var gate = await GateAsync(ProjectsActions.BillingApprove, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _billing.ApproveAsync(gate.CompanyId, id, gate.ActorEmployeeId);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing approved"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId, b = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> PostBilling(int id, int projectId)
		{
			// billing-post already requires the accounting posting right inside ProjectsAccessService, so the
			// requireAccountingPost flag would only re-ask the same question.
			var gate = await GateAsync(ProjectsActions.BillingPost, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _billing.PostAsync(gate.CompanyId, id, gate.ActorEmployeeId);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing posted"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId, b = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteBilling(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BillingPrepare, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _billing.DeleteAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing deleted"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SubmitBilling(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BillingPrepare, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _billing.SubmitAsync(gate.CompanyId, id, gate.ActorEmployeeId);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing submitted for approval"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId, b = id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ReturnBilling(int id, int projectId)
		{
			var gate = await GateAsync(ProjectsActions.BillingApprove, projectId);
			if (!gate.Ok) return gate.Denied!;

			var (ok, err) = await _billing.ReturnAsync(gate.CompanyId, id, gate.ActorEmployeeId);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Progress billing returned to the preparer"].Value : err;
			return RedirectToAction(nameof(Billing), new { id = projectId, b = id });
		}

		// ---- P5-أ: project material issue (actual material cost via StockService) ----
		[HttpGet]
		public async Task<IActionResult> MaterialIssues(int id, int? b, int? w)
		{
			var gate = await GateAsync(ProjectsActions.BudgetView, id);
			if (!gate.Ok) return gate.Denied!;
			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Issues = await _material.GetIssuesAsync(gate.CompanyId, id);
			ViewBag.Warehouses = await _db.Warehouses.AsNoTracking().Where(x => x.CompanyID == gate.CompanyId).OrderBy(x => x.Code).ToListAsync();
			ViewBag.BoqItems = await _db.BoqItems.AsNoTracking().Where(x => x.CompanyID == gate.CompanyId && x.ProjectId == id).OrderBy(x => x.SortOrder).ToListAsync();
			ViewBag.SelectedWarehouseId = w;
			ViewBag.EditIssue = b.HasValue && b.Value > 0 ? await _material.GetAsync(gate.CompanyId, b.Value) : null;
			if (w.HasValue && w.Value > 0)
				ViewBag.Balances = await (from sb in _db.StockBalances.AsNoTracking()
										  join it in _db.Items.AsNoTracking() on sb.ItemId equals it.ID
										  where sb.CompanyID == gate.CompanyId && sb.WarehouseId == w.Value && sb.QtyOnHand > 0
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
			var gate = await GateAsync(ProjectsActions.BudgetView, id);
			if (!gate.Ok) return gate.Denied!;
			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			return View(await _labor.GetProjectLaborAsync(gate.CompanyId, id));
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
			var gate = await GateAsync(ProjectsActions.BudgetView, id);
			if (!gate.Ok) return gate.Denied!;
			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			return View(await _budget.GetBudgetVsActualAsync(gate.CompanyId, id));
		}

		// ---- P6-ج: subcontractor / equipment billing (via PayableService) ----
		[HttpGet]
		public async Task<IActionResult> Subcontracts(int id, int? sc, int? b, decimal? cumulativeWork, decimal? taxRate)
		{
			var gate = await GateAsync(ProjectsActions.BudgetView, id);
			if (!gate.Ok) return gate.Denied!;
			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Subcontracts = await _subcontract.GetSubcontractsAsync(gate.CompanyId, id);
			ViewBag.Vendors = await _db.Vendors.AsNoTracking().Where(v => v.CompanyID == gate.CompanyId && v.IsActive).OrderBy(v => v.Name).ToListAsync();
			ViewBag.SelectedSubcontractId = sc;
			if (sc.HasValue && sc.Value > 0)
			{
				ViewBag.Billings = await _subcontract.GetBillingsAsync(gate.CompanyId, sc.Value);
				ViewBag.Preview = await _subcontract.BuildPreviewAsync(gate.CompanyId, sc.Value, b, cumulativeWork, taxRate);
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
			var gate = await GateAsync(ProjectsActions.BudgetView, id);
			if (!gate.Ok) return gate.Denied!;
			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Orders = await _variation.GetForProjectAsync(gate.CompanyId, id);
			ViewBag.Revised = await _variation.RevisedContractValueAsync(gate.CompanyId, id);
			ViewBag.BoqItems = await _db.BoqItems.AsNoTracking().Where(x => x.CompanyID == gate.CompanyId && x.ProjectId == id).OrderBy(x => x.SortOrder).ToListAsync();
			ViewBag.EditOrder = vo.HasValue && vo.Value > 0 ? await _variation.GetAsync(gate.CompanyId, vo.Value) : null;
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
			var gate = await GateAsync(ProjectsActions.BudgetView, id);
			if (!gate.Ok) return gate.Denied!;
			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) { TempData["PrjErr"] = L["Project not found"].Value; return RedirectToAction(nameof(Projects)); }
			ViewBag.Project = prj;
			ViewBag.Allocations = await _equipDep.GetForProjectAsync(gate.CompanyId, id);
			ViewBag.Assets = await _equipDep.GetAssetsAsync(gate.CompanyId);
			ViewBag.EditAlloc = a.HasValue && a.Value > 0 ? await _equipDep.GetAsync(gate.CompanyId, a.Value) : null;
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

		// ===== PROJECT TEAM =====
		//
		// The only new screen in this pass, and it is here for a security reason rather than a product one:
		// dbo.ProjectMembers had no writer, so a company that configured its first Projects role would have
		// dropped every non-role-holder to AccessScope.None() with no way to grant anybody membership.
		//
		// Each action gates for the COMPANY and the redirect, then calls the service - which asks
		// ProjectsAccessService again for itself. That repetition is deliberate: the service is the security
		// boundary and must hold for any caller, not only for one that came through this controller.
		[HttpGet]
		public async Task<IActionResult> Team(int id)
		{
			var gate = await GateAsync(ProjectsActions.Read, id);
			if (!gate.Ok) return gate.Denied!;

			var prj = await _projects.GetAsync(gate.CompanyId, id);
			if (prj == null) return RedirectToAction(nameof(Projects));
			ViewBag.Project = prj;

			var ctx = await _businessContexts.TryGetCurrentAsync();
			ViewBag.Members = ctx == null
				? new List<ProjectMemberRow>()
				: await _members.ListAsync(ctx, id);

			// Only this company's active employees are offered. The service re-checks the employee's company
			// from the employee row, so a tampered form post cannot add somebody who is not in this list.
			// SelectListItem and not an anonymous type: views compile into their own assembly, so a `dynamic`
			// over an anonymous type declared here throws RuntimeBinderException at render time.
			ViewBag.Employees = await _db.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == gate.CompanyId && e.IsActive)
				.OrderBy(e => e.FullName)
				.Select(e => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(e.FullName, e.ID.ToString()))
				.ToListAsync();

			ViewBag.Roles = ProjectMemberRoles.All;
			ViewBag.CanManage = ctx != null
				&& await _projectsAccess.CanAsync(ctx, ProjectsActions.Manage, PermissionTarget.ForProject(id));
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddMember(int projectId, int employeeId, string role, decimal? allocationPct)
		{
			var gate = await GateAsync(ProjectsActions.Manage, projectId);
			if (!gate.Ok) return gate.Denied!;
			var ctx = await _businessContexts.TryGetCurrentAsync();
			if (ctx == null) return RedirectToAction(nameof(Projects));

			var (ok, err, _) = await _members.AddAsync(ctx, projectId, employeeId, role ?? "", allocationPct);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Member added"].Value : L[err ?? ""].Value;
			return RedirectToAction(nameof(Team), new { id = projectId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> UpdateMember(int projectId, int membershipId, string role, decimal? allocationPct)
		{
			var gate = await GateAsync(ProjectsActions.Manage, projectId);
			if (!gate.Ok) return gate.Denied!;
			var ctx = await _businessContexts.TryGetCurrentAsync();
			if (ctx == null) return RedirectToAction(nameof(Projects));

			var (ok, err) = await _members.UpdateAsync(ctx, projectId, membershipId, role ?? "", allocationPct);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Member updated"].Value : L[err ?? ""].Value;
			return RedirectToAction(nameof(Team), new { id = projectId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> EndMember(int projectId, int membershipId)
		{
			var gate = await GateAsync(ProjectsActions.Manage, projectId);
			if (!gate.Ok) return gate.Denied!;
			var ctx = await _businessContexts.TryGetCurrentAsync();
			if (ctx == null) return RedirectToAction(nameof(Projects));

			var (ok, err) = await _members.EndAsync(ctx, projectId, membershipId);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Membership ended"].Value : L[err ?? ""].Value;
			return RedirectToAction(nameof(Team), new { id = projectId });
		}

		// ---- Project activity types (user-defined lookup) ----
		[HttpGet]
		public async Task<IActionResult> ActivityTypes()
		{
			var gate = await CompanyGateAsync(ProjectsActions.Read);
			if (!gate.Ok) return gate.Denied!;
			return View(await _projects.GetActivityTypesAsync(gate.CompanyId));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveActivityType(int id, string code, string name, string? nameEn, bool isActive)
		{
			var gate = await CompanyGateAsync(ProjectsActions.Read);
			if (!gate.Ok) return gate.Denied!;
			var (ok, err, _) = await _projects.SaveActivityTypeAsync(new ProjectActivityType
			{ ID = id, CompanyID = gate.CompanyId, Code = code ?? "", Name = name ?? "", NameEn = nameEn ?? "", IsActive = isActive });
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Saved"].Value : err;
			return RedirectToAction(nameof(ActivityTypes));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteActivityType(int id)
		{
			var gate = await CompanyGateAsync(ProjectsActions.Manage);
			if (!gate.Ok) return gate.Denied!;
			var (ok, err) = await _projects.DeleteActivityTypeAsync(gate.CompanyId, id);
			TempData[ok ? "PrjMsg" : "PrjErr"] = ok ? L["Deleted"].Value : err;
			return RedirectToAction(nameof(ActivityTypes));
		}
	}
}
