using System.Globalization;
using System.Text.Json;
using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Menu;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
// GetService<T>() — the optional resolution the Communication Platform requires. WorkspaceService uses the
// same extension for the same reason.
using Microsoft.Extensions.DependencyInjection;
using CrossBuy.Models.Platform;

namespace CrossBuy.Controllers
{
	// TM-1: Task management (operational only — NO GL/stock). Admin-app module, uses the shared backend shell + its own
	// sidebar menu (MainMenu.Tasks()). Linking to records (TM-2), timesheet (TM-3) and money (TM-4+) come later.
	public class TasksController : Controller
	{
		// ==========================================================================================
		// THE COMPANY FOR THIS REQUEST  (UAT DEFECT 1 — cross-company read/write leak)
		//
		// WHAT USED TO BE HERE
		//
		//     private const int DefaultCompanyId = 1;
		//
		// …passed to 56 service calls across every Tasks screen and every Tasks write. On a multi-company
		// install that is not "a default" — it is the wrong company for everyone who is not in company 1.
		// IRequestCompanyResolver's own header names this exact defect ("Twelve controllers hold the company
		// as a compile-time constant"), so this is the sanctioned remediation, not a new invention.
		//
		// MEASURED, NOT ASSUMED. The populated UAT dataset caught it at runtime: signed in as company 65's
		// only employee, /Tasks/All?page=6 returned 25 rows and /Tasks/Templates returned 10 templates
		// carrying company 1's UAT marker — while company 65 owns 20 tasks and 2 templates. Page 6 of a list
		// its own company cannot fill is not a display bug; it is another tenant's data.
		//
		// READS AND WRITES WERE BOTH AFFECTED, and the split is worth recording because it is not obvious:
		//   · Actions that go through TaskGateAsync/TaskManageGateAsync were ALREADY protected on the row —
		//     TasksAccessService.CanAsync reads TaskItem.CompanyId from the ROW and refuses a mismatch
		//     (TasksAccessService.cs:134). BoardMove, the checklist and dependency mutations, PostLaborToWO,
		//     GenerateInvoice and Detail fell in that group.
		//   · Actions with NO gate had nothing to catch them: Save, ChangeStatus, Delete, the four timer
		//     actions, SaveAutoRule, RunGenerator, DismissMatch and all three template actions each carried
		//     the literal straight into a service that filters on it. Those were real cross-company writes.
		// Both groups now take the resolved company; the gated ones additionally keep their row check.
		//
		// WHY A CACHED FIELD AND NOT A CONSTRUCTOR-RESOLVED VALUE. Resolution is async (it reads the
		// BusinessContext, which reads Employee), and a controller constructor cannot await. The controller is
		// per-request, so one resolution per request is cached here and every call site sees the same answer.
		//
		// FAIL CLOSED, TWICE OVER. `0` is returned when nothing resolves, and 0 is a company id no row can
		// hold — so every downstream `CompanyId == companyId` predicate matches nothing, for reads AND for
		// the row lookups the writes perform. That is the floor. On top of it each action refuses explicitly,
		// so an unresolved caller gets a stated denial instead of a silently empty screen.
		private int? _companyId;

		private async Task<int> CompanyIdAsync()
		{
			if (_companyId.HasValue) return _companyId.Value;
			var scope = await _company.ResolveAsync();
			_companyId = scope.Ok ? scope.CompanyId : 0;
			return _companyId.Value;
		}

		/// Page-shaped refusal, matching what AccPerm/InvPerm/PlatformOps already do to a denied page request.
		private IActionResult CompanyRefusedView()
		{
			this.ToastError(T("تعذّر تحديد الشركة لهذه الجلسة", "No company could be resolved for this session"));
			return RedirectToAction("Index", "Home");
		}

		/// JSON-shaped refusal, for the AJAX endpoints the board and the detail panels call.
		private IActionResult CompanyRefusedJson() =>
			Json(new { ok = false, error = T("تعذّر تحديد الشركة لهذه الجلسة", "No company could be resolved for this session") });

		/// Page-shaped PERMISSION refusal. A redirect-and-toast rather than Forbid(), because every caller
		/// below is a posted form answered by a redirect: a bare 403 would replace the screen the user was
		/// standing on with an empty error page, and a redirect-based mutation reports through the toast.
		///
		/// ONE SENTENCE FOR EVERY CAUSE, and that is the point. "No such task", "another company's task" and
		/// "yours but not for this action" are answered identically here, exactly as TasksAccessService
		/// answers them identically underneath — a refusal that distinguishes them is a probe for which task
		/// ids exist.
		private IActionResult PermissionRefusedView(string scope = "mine")
		{
			this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		/// JSON-shaped permission refusal, worded exactly as BoardMove's so the two cannot be told apart.
		private IActionResult PermissionRefusedJson() =>
			Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") });

		private readonly ITaskService _tasks;
		private readonly ITaskLinkResolver _links;
		private readonly ITimesheetService _ts;
		private readonly ITaskCostService _cost;
		private readonly ITaskBillingService _billing;
		private readonly ITaskGeneratorService _gen;
		private readonly ITaskReportService _reports;
		private readonly ITaskScheduleMatcher _matcher;
		private readonly ITasksAccessService _tasksAccess;
		// D1 Wave 1: validated company (CORRECTION-005) + the accounting right an invoice-creating action needs.
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		private readonly AccountingAccessService _accounting;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _businessContexts;
		// Task ecosystem: checklist, dependencies, templates. Read-and-write helpers around TaskItems;
		// none of them writes a task row directly — task creation still goes through ITaskService so
		// events and notifications stay on one path.
		private readonly CrossBuy.BL.TasksCalendar.ITaskChecklistService _checklist;
		private readonly CrossBuy.BL.TasksCalendar.ITaskDependencyService _deps;
		private readonly CrossBuy.BL.TasksCalendar.ITaskTemplateService _templates;

		// Attachments. NOTHING here stores a file itself and NOTHING here defines a new attachment table:
		// bytes go to the SAME wwwroot/uploads store FileManagerController writes to, are registered in the
		// SAME file library, and the resulting web path becomes the official CommCommentAttachment.StorageKey.
		// The Comm platform stores no bytes and resolves no url by contract (ADR-030 §8) — the deployment's
		// file store does, and this is that store.
		//
		// THE COMMUNICATION PLATFORM IS OPTIONAL, SO IT IS RESOLVED AT THE POINT OF USE — NOT IN THE CONSTRUCTOR.
		//
		// AddCommunicationPlatform is deliberately NOT called in Program.cs: activating it is a separate platform
		// decision. Taking ICommThreadService/ICommCommentService as constructor parameters made that decision
		// fatal here — DI could not build the controller at all, so ALL EIGHT Tasks screens returned HTTP 500
		// while only the task discussion needed Communication. A screen that shows a list of tasks must not fail
		// because a comment box has no backing platform.
		//
		// This is the pattern WorkspaceService already uses (`_services.GetService<ICommMentionService>()`,
		// WorkspaceService.cs): the platform is asked for, never required, and its absence is reported as an
		// explicit UNAVAILABLE rather than an exception. Nothing is substituted, faked or re-implemented — when
		// the platform IS registered these resolve to the very same services the constructor used to receive.
		private readonly IServiceProvider _services;
		private readonly IFileManagerService _files;
		private readonly IWebHostEnvironment _env;

		public TasksController(ITaskService tasks, ITaskLinkResolver links, ITimesheetService ts, ITaskCostService cost, ITaskBillingService billing, ITaskGeneratorService gen, ITaskReportService reports, ITaskScheduleMatcher matcher,
			// Stage 1 Batch C — tasks access service + session-free context, for the ConfirmMatch proof endpoint.
			ITasksAccessService tasksAccess, CrossBuy.BL.Platform.IBusinessContextAccessor businessContexts,
			CrossBuy.BL.Platform.IRequestCompanyResolver company, AccountingAccessService accounting,
			CrossBuy.BL.TasksCalendar.ITaskChecklistService checklist,
			CrossBuy.BL.TasksCalendar.ITaskDependencyService deps,
			CrossBuy.BL.TasksCalendar.ITaskTemplateService templates,
			CrossBuy.BL.TasksCalendar.ITaskEscalationService escalation,
			IServiceProvider services,
			IFileManagerService files, IWebHostEnvironment env)
		{ _services = services; _files = files; _env = env; _tasksAccess = tasksAccess; _businessContexts = businessContexts; _company = company; _accounting = accounting; _tasks = tasks; _links = links; _ts = ts; _cost = cost; _billing = billing; _gen = gen; _reports = reports; _matcher = matcher; _checklist = checklist; _deps = deps; _templates = templates; _escalation = escalation; }
		// D1 Wave 1 — resolves the company (CORRECTION-005) and ASKS the approved access services. No predicate
		// lives here: TasksAccessService owns the task record rule and the linked-entity check; AccountingAccessService
		// owns the posting right. TaskItem.CompanyId is verified inside TasksAccessService against the ROW.
		private readonly CrossBuy.BL.TasksCalendar.ITaskEscalationService _escalation;

		private sealed class TaskGate { public bool Ok; public int CompanyId; public int? EmployeeId; }

		// Template administration is company-wide, not per-task: a template belongs to no single task, so
		// the right question is "may this employee administer tasks in this company", asked of the same
		// access service that answers the per-task question.
		private async Task<TaskGate> TaskManageGateAsync()
		{
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return new TaskGate();
			var ctx = await _businessContexts.TryGetCurrentAsync();
			if (ctx == null) return new TaskGate();
			if (!await _tasksAccess.CanAsync(ctx, TasksActions.Manage, new PermissionTarget { CompanyId = scope.CompanyId }))
				return new TaskGate();
			return new TaskGate { Ok = true, CompanyId = scope.CompanyId, EmployeeId = scope.EmployeeId };
		}

		// The MODULE-level question, for mutations that name no task: "may this employee do this KIND of
		// thing in this company at all". TaskGateAsync cannot answer it, because it authorises a row and
		// these have no row yet (a create) or no row at all (generator rules). Same service, same context,
		// same fail-closed shape as TaskManageGateAsync — which is this helper with Manage hardcoded, left
		// untouched so its five existing callers keep the exact code path they were tested against.
		private async Task<TaskGate> TaskModuleGateAsync(string action)
		{
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return new TaskGate();
			var ctx = await _businessContexts.TryGetCurrentAsync();
			if (ctx == null) return new TaskGate();
			if (!await _tasksAccess.CanAsync(ctx, action, new PermissionTarget { CompanyId = scope.CompanyId }))
				return new TaskGate();
			return new TaskGate { Ok = true, CompanyId = scope.CompanyId, EmployeeId = scope.EmployeeId };
		}

		private async Task<TaskGate> TaskGateAsync(string action, int taskId, bool requireAccountingPost = false)
		{
			if (taskId <= 0) return new TaskGate();
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return new TaskGate();
			var ctx = await _businessContexts.TryGetCurrentAsync();
			if (ctx == null) return new TaskGate();
			if (!await _tasksAccess.CanAsync(ctx, action, PermissionTarget.ForTask(taskId))) return new TaskGate();
			if (requireAccountingPost && !await _accounting.CanAsync(ctx, "post")) return new TaskGate();
			return new TaskGate { Ok = true, CompanyId = scope.CompanyId, EmployeeId = scope.EmployeeId };
		}


		private static bool IsAr => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
		private static string T(string ar, string en) => IsAr ? ar : en;

		private int CurrentEmployeeId()
		{
			var json = HttpContext.Session.GetString("Employee");
			var emp = json != null ? JsonSerializer.Deserialize<EmployeeViewModel>(json) : null;
			return emp?.ID ?? 0;
		}

		private async Task<IActionResult> RenderList(string scope, string? status, string? priority, string? q, int page = 1, int pageSize = 25, string? view = null, int? assignee = null, string? sort = null)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Scope = scope;
			ViewBag.Status = status; ViewBag.Priority = priority; ViewBag.Q = q; ViewBag.View = view ?? "all"; ViewBag.Assignee = assignee; ViewBag.Sort = sort;
			if (page < 1) page = 1; if (pageSize < 1 || pageSize > 200) pageSize = 25;
			int me = CurrentEmployeeId();
			ViewBag.Me = me;
			ViewBag.Employees = await _tasks.ActiveEmployeesAsync(co);
			ViewBag.LinkTypes = _links.Types();
			ViewBag.Kpis = await _tasks.GetKpisAsync(co, scope, me);
			int total = await _tasks.CountTasksAsync(co, scope, me, status, priority, q, view, assignee);
			int pages = (int)Math.Ceiling(total / (double)pageSize); if (pages < 1) pages = 1; if (page > pages) page = pages;
			ViewBag.Total = total; ViewBag.Page = page; ViewBag.PageSize = pageSize; ViewBag.Pages = pages;
			var rows = await _tasks.GetTasksAsync(co, scope, me, status, priority, q, page, pageSize, view, assignee, sort);
			ViewBag.Tasks = rows;
			// TM-2: resolve each linked row to {label, url} for the "open record" link
			var linkMap = new Dictionary<int, CrossBuy.BL.TaskLinkDto>();
			foreach (var r in rows.Where(x => !string.IsNullOrEmpty(x.EntityType) && x.EntityId > 0))
			{
				var resolved = await _links.ResolveAsync(co, r.EntityType, r.EntityId);
				if (resolved != null) linkMap[r.Id] = resolved;
			}
			ViewBag.Links = linkMap;
			ViewBag.Running = await _ts.GetRunningAsync(co, me);   // TM-3: the employee's live timer (if any)
			return View("Index");
		}

		[HttpGet]
		public Task<IActionResult> Index(string? status, string? priority, string? q, int page = 1, int pageSize = 25, string? view = null, int? assignee = null, string? sort = null) => RenderList("mine", status, priority, q, page, pageSize, view, assignee, sort);

		[HttpGet]
		public Task<IActionResult> All(string? status, string? priority, string? q, int page = 1, int pageSize = 25, string? view = null, int? assignee = null, string? sort = null) => RenderList("all", status, priority, q, page, pageSize, view, assignee, sort);

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> Save(TaskSaveInput input, string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			// CREATE AND EDIT ARE DIFFERENT QUESTIONS and were both unasked. A new task has no row to be
			// related to, so the only answerable question is the module-level one; an existing task is
			// authorised on its own row, which is also what re-checks its company.
			if (input.Id > 0)
			{
				if (!(await TaskGateAsync(TasksActions.Edit, input.Id)).Ok) return PermissionRefusedView(scope);

				// REASSIGNMENT IS NOT EDITING, and TasksAccessService has always said so:
				//     Edit     => isAssignee || isCreator || managesAssignee
				//     Reassign => isCreator || managesAssignee     ("never the assignee handing their work on")
				// The rule was written and evaluated and NOTHING EVER ASKED FOR IT, so until now an assignee
				// could move their own task to somebody else through this form. Asking the declared question
				// is a DELIBERATE TIGHTENING — it is the one change in this pass that removes an ability
				// somebody has today, and it removes exactly the one the rule table says they never had.
				var current = await _tasks.GetAsync(co, input.Id);
				if (current != null && current.AssigneeEmployeeId != input.AssigneeEmployeeId
					&& !(await TaskGateAsync(TasksActions.Reassign, input.Id)).Ok)
					return PermissionRefusedView(scope);
			}
			else if (!(await TaskModuleGateAsync(TasksActions.Create)).Ok)
			{
				return PermissionRefusedView(scope);
			}

			var (ok, err, _) = await _tasks.SaveAsync(co, input, CurrentEmployeeId());
			if (ok) this.ToastSuccess(input.Id > 0 ? T("تم حفظ التعديلات", "Changes saved") : T("تم إنشاء المهمة", "Task created"));
			else this.ToastError(err ?? T("تعذّر الحفظ", "Could not save"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		// TM-2: record picker search (AJAX, read-only). Returns [{id,text}] for select2.
		// TM-8: unified read-only reports hub. `r` selects the report; all aggregate existing data, write nothing.
		[HttpGet]
		public async Task<IActionResult> Reports(string r = "summary", DateTime? from = null, DateTime? to = null)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.R = r;
			var f = from ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t;
			switch (r)
			{
				case "overdue": ViewBag.Data = await _reports.OverdueAsync(co); break;
				case "productivity": ViewBag.Data = await _reports.ProductivityAsync(co, f, t); break;
				case "cost-project": ViewBag.Data = await _reports.CostByAsync(co, "Project"); break;
				case "cost-wo": ViewBag.Data = await _reports.CostByAsync(co, "ManufWorkOrder"); break;
				case "billable": ViewBag.Data = await _reports.BillableByCustomerAsync(co); break;
				case "auto": ViewBag.Data = await _reports.AutoGeneratedAsync(co); break;
				default:
					r = "summary"; ViewBag.R = r;
					var (mine, team) = await _reports.SummaryAsync(co, CurrentEmployeeId());
					ViewBag.Mine = mine; ViewBag.Team = team; break;
			}
			return View();
		}

		// TM-6: read-only hours report (per employee, date range) for HR to feed the existing payroll. Writes nothing.
		[HttpGet]
		public async Task<IActionResult> HoursReport(DateTime? from, DateTime? to, int? employeeId)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			ViewBag.SidebarMenu = MainMenu.Tasks();
			var f = from ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t; ViewBag.EmployeeId = employeeId;
			ViewBag.Employees = await _tasks.ActiveEmployeesAsync(co);
			var report = await _ts.GetHoursReportAsync(co, f, t, employeeId);
			ViewBag.Report = report;
			// employee profile photos + culture-aware display names for the report cards
			var imgs = new Dictionary<int, string>();
			var names = new Dictionary<int, string>();
			if (report.Employees.Count > 0
				&& HttpContext.RequestServices.GetService(typeof(CrossBuy.Models.Context.CrossDbContext)) is CrossBuy.Models.Context.CrossDbContext db)
			{
				var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
				var ids = report.Employees.Select(e => e.EmployeeId).ToList();
				// The ids already come from a company-scoped report, so the company predicate changes no
				// legitimate result — it is here so this Employee read cannot become a disclosure if the
				// report's own scoping is ever loosened. An unfiltered Employee read is the shape Defect 2 took.
				var rows = await db.Employee.AsNoTracking()
					.Where(e => e.EmpCompanyID == co && ids.Contains(e.ID))
					.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage })
					.ToListAsync();
				foreach (var r in rows)
				{
					if (!string.IsNullOrEmpty(r.ProfileImage)) imgs[r.ID] = r.ProfileImage;
					names[r.ID] = (isEn && !string.IsNullOrWhiteSpace(r.FullNameEn)) ? r.FullNameEn : r.FullName;
				}
			}
			ViewBag.EmpImages = imgs;
			ViewBag.EmpNames = names;
			return View();
		}

		// TM-7: auto-task rules (toggle + default assignee) + run-now
		[HttpGet]
		public async Task<IActionResult> AutoRules()
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Employees = await _tasks.ActiveEmployeesAsync(co);
			ViewBag.Rules = await _gen.GetRulesAsync(co);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveAutoRule(string ruleType, bool isActive, int? defaultAssigneeEmployeeId)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			// A generation RULE belongs to no task: it mints them for the whole company, so the right
			// question is the company-wide one. Manage is admin-only, which is the correct breadth for a
			// control that can create work for everybody.
			if (!(await TaskManageGateAsync()).Ok) return PermissionRefusedView();

			var (ok, err) = await _gen.SetRuleAsync(co, ruleType, isActive, defaultAssigneeEmployeeId);
			if (ok) this.ToastSuccess(T("تم حفظ القاعدة", "Rule saved")); else this.ToastError(err ?? T("تعذّر الحفظ", "Could not save"));
			return RedirectToAction(nameof(AutoRules));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> RunGenerator()
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			// Same breadth as editing the rules: running the generator writes tasks across the company.
			if (!(await TaskManageGateAsync()).Ok) return PermissionRefusedView();

			var summary = await _gen.RunAsync(co);
			int total = summary.Values.Sum();
			this.ToastSuccess(total > 0 ? T($"تم توليد {total} مهمة", $"{total} task(s) generated") : T("لا مهام جديدة للتوليد", "No new tasks to generate"));
			return RedirectToAction(nameof(AutoRules));
		}

		// [SessionValidation] ADDED: it was the one endpoint in this controller without it, and the
		// controller carries no class-level [Authorize]. It was never actually reachable anonymously —
		// global authorization redirects first, and CompanyIdAsync() returns 0 with no session, which
		// this action already answers with an empty list — so this closes an INCONSISTENCY rather than
		// an open hole. It is worth closing anyway: relying on middleware alone leaves the guarantee one
		// routing change away from being wrong, and the contract here is unchanged either way (an
		// unauthenticated caller got nothing before and gets nothing now).
		[SessionValidation][HttpGet]
		public async Task<IActionResult> LinkSearch(string entityType, string? term)
		{
			if (string.IsNullOrWhiteSpace(entityType)) return Json(new { results = Array.Empty<object>() });

			// A picker with no resolved company offers NOTHING to link to — the same empty result an unknown
			// entity type gets, so the caller cannot tell the two apart by probing.
			var co = await CompanyIdAsync();
			if (co == 0) return Json(new { results = Array.Empty<object>() });

			var opts = await _links.SearchAsync(co, entityType, term);
			return Json(new { results = opts.Select(o => new { id = o.Id, text = o.Label }) });
		}

		// TM-9-ج: manager review of pending multiple-match suggestions for scheduled tasks
		[HttpGet]
		public async Task<IActionResult> MatchSuggestions()
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Groups = await _matcher.GetPendingAsync(co);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ConfirmMatch(int taskId, int entityId)
		{
			// ===== Stage 1 Batch C proof endpoint (Tasks) =====
			// Linking a task to a business movement EDITS that task. Before this batch any signed-in employee
			// could do it to any task in the company. TasksAccessService requires a real relationship to the
			// task (assignee, creator, or manager of the assignee through the company-intersected hierarchy),
			// verifies the task's own CompanyId, and — because the task carries EntityType/EntityId — asks
			// IPlatformPermissionProvider whether the caller may even see the linked object.
			var taskContext = await _businessContexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (taskContext == null) { this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You are not allowed to perform this action")); return RedirectToAction(nameof(MatchSuggestions)); }

			bool mayEdit = await _tasksAccess.CanAsync(
				taskContext, TasksActions.Edit,
				PermissionTarget.ForTask(taskId, taskContext.CompanyId),
				HttpContext.RequestAborted);
			if (!mayEdit)
			{
				this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You are not allowed to perform this action"));
				return RedirectToAction(nameof(MatchSuggestions));
			}

			// The company comes from the SAME resolved context the authorisation above used, so the row the
			// matcher confirms and the row TasksAccessService checked are the same row in the same company.
			var co = taskContext.CompanyId;
			var (ok, err) = await _matcher.ConfirmAsync(co, taskId, entityId);
			if (ok) this.ToastSuccess(T("تم ربط المهمة بالحركة", "Task linked to the movement")); else this.ToastError(err ?? T("تعذّر الربط", "Could not link"));
			return RedirectToAction(nameof(MatchSuggestions));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DismissMatch(int suggestionId)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			// RESOLVE, THEN GATE. The caller names a suggestion, not a task, so the row is read first —
			// under the resolved company — and the task it points at is what gets authorised. Gating the
			// id the caller sent would authorise nothing.
			var suggestedTaskId = await _services.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>()
				.TaskMatchSuggestions.AsNoTracking()
				.Where(x => x.ID == suggestionId && x.CompanyId == co)
				.Select(x => (int?)x.TaskId).FirstOrDefaultAsync();
			if (suggestedTaskId is not > 0 || !(await TaskGateAsync(TasksActions.Edit, suggestedTaskId.Value)).Ok)
				return PermissionRefusedView();

			var (ok, err) = await _matcher.DismissAsync(co, suggestionId);
			if (ok) this.ToastSuccess(T("تم رفض الاقتراح", "Suggestion dismissed")); else this.ToastError(err ?? T("تعذّر الرفض", "Could not dismiss"));
			return RedirectToAction(nameof(MatchSuggestions));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ChangeStatus(int id, string status, string scope = "mine")
		{
			// The resolved company IS the isolation control here: ChangeStatusAsync looks the row up by
			// (ID AND CompanyId), so another company's task id resolves to "not found" rather than being moved.
			//
			// CLOSED. This action carried no gate while BoardMove — the identical operation from the board —
			// did, so the same status change was authorised from one screen and unauthorised from the other.
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			// THE RIGHT DEPENDS ON THE TRANSITION, not on the endpoint. Finishing work and resuming finished
			// work are their own actions in TasksActions and the rule table already evaluates both.
			//
			// THIS CHANGES NOBODY'S ACCESS TODAY, and that is deliberate rather than accidental: Edit,
			// Complete and Reopen all resolve to the same predicate right now (`related`, and admin-or-
			// supervisor company-wide). Asking the precise question costs nothing now and means that if
			// completing ever becomes stricter than editing, this endpoint is already asking the stricter
			// one instead of silently keeping the weaker answer.
			var currentStatus = (await _tasks.GetAsync(co, id))?.Status;
			var required =
				string.Equals(status, CrossBuy.Models.Context.Tasks.TaskStatuses.Done, StringComparison.Ordinal) ? TasksActions.Complete
				: string.Equals(currentStatus, CrossBuy.Models.Context.Tasks.TaskStatuses.Done, StringComparison.Ordinal) ? TasksActions.Reopen
				: TasksActions.Edit;
			if (!(await TaskGateAsync(required, id)).Ok) return PermissionRefusedView(scope);

			var (ok, err) = await _tasks.ChangeStatusAsync(co, id, status, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("تم تحديث الحالة", "Status updated")); else this.ToastError(err ?? T("تعذّر التحديث", "Could not update"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> Delete(int id, string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			// GATED ON Manage, AND THAT IS A TIGHTENING — stated plainly because it removes an ability.
			// Before this, any employee with a session could delete any task in their company. There is no
			// TasksActions.Delete to ask for: the vocabulary is Read/Create/Edit/Assign/Reassign/Complete/
			// Reopen/Manage, and adding one is TAB-1's AuthorityTypes, not this file's. Of what exists,
			// Manage is the only action whose stated meaning — company-wide task administration — matches
			// destroying a record, and erring toward admin-only is the recoverable direction: a refusal is
			// one grant away from fixed, a deleted task is not. REQUESTED OF TAB-1: TasksActions.Delete, so
			// this can become a record-level right (creator or manager) instead of an administrator's.
			if (!(await TaskManageGateAsync()).Ok) return PermissionRefusedView(scope);

			var (ok, err) = await _tasks.DeleteAsync(co, id);
			if (ok) this.ToastSuccess(T("تم حذف المهمة", "Task deleted")); else this.ToastError(err ?? T("تعذّر الحذف", "Could not delete"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		// ===== TM-3: Timesheet =====
		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> StartTimer(int taskId, string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			// Logging time against a task is a change to that task's record, so it takes the task's Edit right.
			if (!(await TaskGateAsync(TasksActions.Edit, taskId)).Ok) return PermissionRefusedView(scope);

			var (ok, err) = await _ts.StartAsync(co, taskId, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("بدأ المؤقّت", "Timer started")); else this.ToastError(err ?? T("تعذّر بدء المؤقّت", "Could not start timer"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> StopTimer(string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			// RESOLVE, THEN GATE. This action names no task — it stops whatever is running for this employee —
			// so the running entry is read first and the task it belongs to is what gets authorised. A timer
			// the caller may no longer act on is still theirs to stop, so a refusal here is deliberate and
			// rare: it means the task moved out from under them.
			var running = await _ts.GetRunningAsync(co, CurrentEmployeeId());
			if (running != null && !(await TaskGateAsync(TasksActions.Edit, running.TaskId)).Ok)
				return PermissionRefusedView(scope);

			var (ok, err) = await _ts.StopAsync(co, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("أُوقف المؤقّت وسُجّل الوقت", "Timer stopped and time logged")); else this.ToastError(err ?? T("لا يوجد مؤقّت شغّال", "No running timer"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddTime(int taskId, DateTime workDate, decimal hours, string? description, string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			if (!(await TaskGateAsync(TasksActions.Edit, taskId)).Ok) return PermissionRefusedView(scope);

			var (ok, err) = await _ts.AddManualAsync(co, taskId, CurrentEmployeeId(), workDate, hours, description);
			if (ok) this.ToastSuccess(T("تم تسجيل الوقت", "Time logged")); else this.ToastError(err ?? T("تعذّر التسجيل", "Could not log time"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteTime(int id, string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			// RESOLVE, THEN GATE: the caller names a timesheet line, so the line is read under the resolved
			// company and the task it belongs to is what is authorised.
			var entryTaskId = await _services.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>()
				.TimesheetEntries.AsNoTracking()
				.Where(x => x.ID == id && x.CompanyId == co)
				.Select(x => (int?)x.TaskId).FirstOrDefaultAsync();
			if (entryTaskId is not > 0 || !(await TaskGateAsync(TasksActions.Edit, entryTaskId.Value)).Ok)
				return PermissionRefusedView(scope);

			var (ok, err) = await _ts.DeleteEntryAsync(co, id);
			if (ok) this.ToastSuccess(T("تم حذف السطر", "Entry deleted")); else this.ToastError(err ?? T("تعذّر الحذف", "Could not delete"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpGet]
		public async Task<IActionResult> TimesheetLines(int taskId)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedJson();

			// A READ, GATED LIKE ONE — and it is gated because of WHAT it returns. This endpoint hands back
			// who worked on the task, the labour cost and the billing position, which is more than the list
			// shows; company scope alone would let any employee read any task's cost by typing its id.
			if (!(await TaskGateAsync(TasksActions.Read, taskId)).Ok) return PermissionRefusedJson();

			var lines = await _ts.GetEntriesAsync(co, taskId);
			var cost = await _cost.GetTaskCostAsync(co, taskId);   // TM-4: informational cost (no GL)
			var bill = await _billing.GetBillingAsync(co, taskId); // TM-5: billing status
			return Json(new
			{
				lines = lines.Select(l => new { l.Id, l.EmployeeName, workDate = l.WorkDate.ToString("yyyy-MM-dd"), l.Hours, l.Description, l.Source, l.Running }),
				cost = new { total = cost.Total, linkedToWorkOrder = cost.LinkedToWorkOrder, laborPosted = cost.LaborPosted, lines = cost.Lines },
				billing = new { bill.IsBillable, bill.BillableHours, bill.Amount, bill.CanInvoice, bill.CustomerName, bill.Reason }
			});
		}

		// TM-4: post this WO-linked task's labor to its work order via the EXISTING ManufService writer (post-once).
		// ==========================================================================================
		// ON HOLD / SLA PAUSE — five endpoints, no business rules.
		//
		// EVERY DECISION IS THE SERVICE'S. These actions resolve the caller, gate on the EXISTING task
		// permissions and hand over; who may approve, whether a request is pending, whether the task is
		// terminal and how much time a resume gives back are all answered in TaskService, where they can
		// be tested without a controller.
		//
		// THE GATES ARE THE EXISTING ACTIONS AND NOTHING NEW. Request is an edit to the task, so it
		// gates on TasksActions.Edit. The three decision endpoints gate on TasksActions.Read - enough to
		// reach the task at all - and then the SERVICE applies the real authority (Manage, or the
		// assignee's current direct manager). Gating the endpoint on Manage instead would lock out the
		// direct manager, who is not a Manage holder and is explicitly entitled to decide.
		// ==========================================================================================

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> RequestHold(int id, string? reason)
		{
			var gate = await TaskGateAsync(TasksActions.Edit, id);
			if (!gate.Ok)
			{
				this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"));
				return RedirectToAction(nameof(Detail), new { id });
			}

			var (ok, err) = await _tasks.RequestHoldAsync(gate.CompanyId, id, CurrentEmployeeId(), reason);
			if (ok) this.ToastSuccess(T("تم إرسال طلب التعليق", "Hold request submitted"));
			else this.ToastError(err ?? T("تعذّر إرسال طلب التعليق", "Could not submit the hold request"));
			return RedirectToAction(nameof(Detail), new { id });
		}

		// ONE BUTTON, TWO OPERATIONS, and the row decides which. With a request outstanding this is an
		// APPROVAL; with nothing outstanding it is the manager's own DIRECT hold. Splitting them into two
		// endpoints would make the screen compute which to call, putting a rule in the view.
		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ApproveHold(int id, string? reason)
		{
			var gate = await TaskGateAsync(TasksActions.Read, id);
			if (!gate.Ok)
			{
				this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"));
				return RedirectToAction(nameof(Detail), new { id });
			}

			var task = await _tasks.GetAsync(gate.CompanyId, id);
			bool decidingARequest = task != null && task.HoldRequestedAtUtc != null;

			var (ok, err) = decidingARequest
				? await _tasks.ApproveHoldAsync(gate.CompanyId, id, CurrentEmployeeId())
				: await _tasks.DirectHoldAsync(gate.CompanyId, id, CurrentEmployeeId(), reason);

			if (ok) this.ToastSuccess(T("تم تعليق المهمة", "The task is on hold"));
			else this.ToastError(err ?? T("تعذّر تعليق المهمة", "Could not put the task on hold"));
			return RedirectToAction(nameof(Detail), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> RejectHold(int id, string? decisionReason)
		{
			var gate = await TaskGateAsync(TasksActions.Read, id);
			if (!gate.Ok)
			{
				this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"));
				return RedirectToAction(nameof(Detail), new { id });
			}

			var (ok, err) = await _tasks.RejectHoldAsync(gate.CompanyId, id, CurrentEmployeeId(), decisionReason);
			if (ok) this.ToastSuccess(T("تم رفض طلب التعليق", "Hold request rejected"));
			else this.ToastError(err ?? T("تعذّر رفض طلب التعليق", "Could not reject the hold request"));
			return RedirectToAction(nameof(Detail), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ResumeHold(int id)
		{
			var gate = await TaskGateAsync(TasksActions.Read, id);
			if (!gate.Ok)
			{
				this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"));
				return RedirectToAction(nameof(Detail), new { id });
			}

			var (ok, err) = await _tasks.ResumeHoldAsync(gate.CompanyId, id, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("تم استئناف المهمة", "The task has resumed"));
			else this.ToastError(err ?? T("تعذّر استئناف المهمة", "Could not resume the task"));
			return RedirectToAction(nameof(Detail), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> PostLaborToWO(int taskId, string scope = "mine")
		{
			var gate = await TaskGateAsync(TasksActions.Edit, taskId);
			if (!gate.Ok) { this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action")); return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index)); }

			var (ok, err) = await _cost.PostToWorkOrderAsync(gate.CompanyId, taskId, null);
			if (ok) this.ToastSuccess(T("تم ترحيل العمالة إلى أمر التشغيل", "Labor posted to the work order")); else this.ToastError(err ?? T("تعذّر الترحيل", "Could not post"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		// TM-5: generate an hourly service invoice for this task's uninvoiced billable hours (via ReceivableService).
		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> GenerateInvoice(int taskId, string scope = "mine")
		{
			var gate = await TaskGateAsync(TasksActions.Edit, taskId, requireAccountingPost: true);
			if (!gate.Ok) { this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action")); return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index)); }

			var (ok, err, invId) = await _billing.GenerateInvoiceAsync(gate.CompanyId, taskId, gate.EmployeeId);
			if (ok) this.ToastSuccess(T($"تم إنشاء فاتورة الخدمة #{invId}", $"Service invoice #{invId} created")); else this.ToastError(err ?? T("تعذّر إنشاء الفاتورة", "Could not create invoice"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		// ==========================================================================================
		// TASK ECOSYSTEM — board, checklist, dependencies, templates.
		//
		// The board is a THIRD VIEW of the same list, not a second task system: it calls the same
		// GetTasksAsync and the same ChangeStatusAsync the list already uses. Dragging a card is a
		// status change, so it raises the same events and sends the same notifications as the
		// dropdown on the list screen — there is no board-only write path to keep in step.
		// ==========================================================================================

		[HttpGet]
		public async Task<IActionResult> Board(string scope = "mine", string? q = null, int? assignee = null, string? priority = null)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Scope = scope; ViewBag.Q = q; ViewBag.Assignee = assignee; ViewBag.Priority = priority;
			int me = CurrentEmployeeId();
			ViewBag.Me = me;
			ViewBag.Employees = await _tasks.ActiveEmployeesAsync(co);

			// A board shows every card in its column, so it is not paged the way the list is. The cap is
			// explicit rather than silent: a board with more cards than this is unreadable anyway, and
			// the screen says so instead of quietly truncating.
			const int BoardCap = 500;
			var rows = await _tasks.GetTasksAsync(co, scope, me, null, priority, q, 1, BoardCap, "all", assignee, null);
			ViewBag.Tasks = rows;
			ViewBag.Capped = rows.Count >= BoardCap;
			ViewBag.BoardCap = BoardCap;

			// Both extras resolve for every visible card in ONE query each, not one per card.
			var ids = rows.Select(r => r.Id).ToList();
			ViewBag.Blocking = await _deps.BlockingStateManyAsync(co, ids);
			ViewBag.Checklists = await _checklist.ProgressManyAsync(co, ids);
			// Escalation is DERIVED, so the board asks for it the same way — one query for every card, never
			// one per card. A card that has been escalated shows it; nothing else changes.
			ViewBag.Escalations = await _escalation.EvaluateManyAsync(co, ids);
			return View("Board");
		}

		// One task, with everything attached to it. Checklist and dependencies are ours; the activity
		// timeline and the comments are the PLATFORM's, surfaced here rather than rebuilt — Task is
		// registered with SupportsTimeline and SupportsComments, so /PlatformTimeline/List and
		// /Comments/List already answer for it. A second comment store would be a second truth.
		[HttpGet]
		public async Task<IActionResult> Detail(int id)
		{
			var gate = await TaskGateAsync(TasksActions.Read, id);
			if (!gate.Ok) return Forbid();

			// The gate's company, not a re-resolution: TaskGateAsync has already resolved it AND had
			// TasksAccessService verify this task's own CompanyId against it, so reusing it is the one value
			// guaranteed to agree with the authorisation that just passed.
			int co = gate.CompanyId;

			// Read the row by id, not from a page of the list: a task opened from a stale board must show
			// what the database says now. GetAsync applies the company guard.
			var row = await _tasks.GetAsync(co, id);
			if (row == null) return NotFound();

			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Task = row;
			ViewBag.Checklist = await _checklist.ForTaskAsync(co, id);
			ViewBag.Dependencies = await _deps.ForTaskAsync(co, id);
			ViewBag.Blocking = await _deps.BlockingStateAsync(co, id);
			ViewBag.Employees = await _tasks.ActiveEmployeesAsync(co);
			ViewBag.Escalation = await _escalation.EvaluateAsync(co, id);

			// WHETHER TO OFFER THE DECISION ACTIONS AT ALL. Asked of the service that will enforce it, so
			// the screen can never offer Approve/Reject/Resume to somebody the service would refuse - and
			// never hide them from somebody it would allow. The derived hold state itself is read off the
			// row by the view; it is not a second copy of the rule.
			ViewBag.CanDecideHold = await _tasks.CanDecideHoldAsync(co, id, CurrentEmployeeId());
			return View("Detail");
		}

		// ONE READ-ONLY PAGE with every fact about a task on it: details, checklist, dependencies,
		// the time log, cost, billing, comments and the activity trail. It exists because three
		// recorded timer runs looked like none - the figures were all correct and all unreachable,
		// spread over four tabs and two kebab menus.
		//
		// Every value comes from the SAME read the owning screen uses. A summary that re-derived
		// anything would be a second answer to the same question, and this page is precisely where
		// two answers would sit side by side without anyone noticing.
		public async Task<IActionResult> Summary(int id, CancellationToken ct = default)
		{
			var gate = await TaskGateAsync(TasksActions.Read, id);
			if (!gate.Ok) return Forbid();

			int co = gate.CompanyId;
			var row = await _tasks.GetAsync(co, id);
			if (row == null) return NotFound();

			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Task = row;
			ViewBag.Checklist = await _checklist.ForTaskAsync(co, id);
			ViewBag.Dependencies = await _deps.ForTaskAsync(co, id);
			ViewBag.Blocking = await _deps.BlockingStateAsync(co, id);
			ViewBag.Escalation = await _escalation.EvaluateAsync(co, id);

			// The assignee by NAME. ActiveEmployeesAsync already resolves the display language, and it
			// is the same list every other Tasks screen names people from.
			ViewBag.AssigneeName = (await _tasks.ActiveEmployeesAsync(co))
				.Where(e => e.Id == row.AssigneeEmployeeId)
				.Select(e => e.Name)
				.FirstOrDefault();

			// The time log, and the two figures derived FROM it. Kept together deliberately: cost and
			// billing are readings of these same lines, so showing them apart invites the reader to
			// treat them as independent numbers.
			ViewBag.TimeLines = await _ts.GetEntriesAsync(co, id);
			ViewBag.Cost = await _cost.GetTaskCostAsync(co, id);
			ViewBag.Billing = await _billing.GetBillingAsync(co, id);

			// COMMUNICATION IS OPTIONAL. Without it the page loses its comment section and keeps
			// everything else - every other fact here is still true, and refusing the whole page
			// because one platform is switched off would be the wrong trade.
			ViewBag.Comments = null;
			var comm = TryCommunication();
			if (comm != null)
			{
				var ctx = await CommContextAsync(ct);
				if (ctx != null)
				{
					var thread = await comm.Value.Threads.GetOrCreateAsync(ctx,
						new CrossBuy.Models.Communication.CommThreadRequest
						{
							Entity = new CrossBuy.Models.Communication.CommEntityRef(
								CrossBuy.BL.TasksCalendar.TaskCalendarEntityCodes.Task, id)
						}, ct);
					var page = await comm.Value.Comments.ListAsync(ctx, thread.Id, null, ct);
					ViewBag.Comments = page.Items;
				}
			}

			return View("Summary");
		}

		// Candidate predecessors/successors for the dependency picker: same company, never the task
		// itself. The cycle rule is enforced on ADD, not here — a picker that pre-filtered every
		// reachable task would hide the very edge whose refusal explains the graph.
		[HttpGet]
		public async Task<IActionResult> DependencyCandidates(int taskId, string? term)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return Json(Array.Empty<object>());

			var rows = await _tasks.GetTasksAsync(co, "all", CurrentEmployeeId(), null, null, term, 1, 25, "all", null, null);
			return Json(rows.Where(r => r.Id != taskId).Select(r => new { id = r.Id, text = $"#{r.Id} — {r.Title}" }));
		}

		// Dragging a card. Same service call as the list's status dropdown; answers JSON because the
		// board updates in place.
		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> BoardMove(int id, string status)
		{
			// A drag is a status change, so it needs the same right the list's dropdown needs.
			var gate = await TaskGateAsync(TasksActions.Edit, id);
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") }); }

			var (ok, err) = await _tasks.ChangeStatusAsync(gate.CompanyId, id, status, CurrentEmployeeId());
			return Json(new { ok, error = err });
		}

		// ---- checklist ------------------------------------------------------------------------
		[HttpGet]
		public async Task<IActionResult> Checklist(int taskId)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedJson();

			var lines = await _checklist.ForTaskAsync(co, taskId);
			return Json(lines.Select(l => new { id = l.ID, title = l.Title, done = l.IsDone, sort = l.SortOrder }));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ChecklistAdd(int taskId, string title, string? titleEn = null)
		{
			var gate = await TaskGateAsync(TasksActions.Edit, taskId);
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"),
				// The KIND of refusal, so the view can dress a missing permission differently from a
				// validation refusal. Branching on the message text would break the moment the UI
				// language changed.
				code = "forbidden" }); }

			var (ok, err, id) = await _checklist.AddAsync(gate.CompanyId, taskId, title, CurrentEmployeeId(),
				default, titleEn);
			return Json(new { ok, error = err, id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ChecklistToggle(int id, bool done)
		{
			// Resolve the owning task from the ROW, then authorise against it. Authorising against a task
			// id supplied by the caller would authorise the wrong task.
			//
			// The company is resolved FIRST and the line is looked up within it, so a checklist line belonging
			// to another company resolves to no owning task and the gate refuses — the cross-company case dies
			// here rather than at the mutation.
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedJson();

			var owner = await _checklist.OwningTaskIdAsync(co, id);
			var gate = owner == null ? new TaskGate() : await TaskGateAsync(TasksActions.Edit, owner.Value);
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"),
				// The KIND of refusal, so the view can dress a missing permission differently from a
				// validation refusal. Branching on the message text would break the moment the UI
				// language changed.
				code = "forbidden" }); }

			var (ok, err) = await _checklist.SetDoneAsync(co, id, done, CurrentEmployeeId());
			return Json(new { ok, error = err });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ChecklistRemove(int id)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedJson();

			var owner = await _checklist.OwningTaskIdAsync(co, id);
			var gate = owner == null ? new TaskGate() : await TaskGateAsync(TasksActions.Edit, owner.Value);
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"),
				// The KIND of refusal, so the view can dress a missing permission differently from a
				// validation refusal. Branching on the message text would break the moment the UI
				// language changed.
				code = "forbidden" }); }

			var (ok, err) = await _checklist.RemoveAsync(co, id);
			return Json(new { ok, error = err });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ChecklistReorder(int taskId, [FromForm] int[] orderedIds)
		{
			var gate = await TaskGateAsync(TasksActions.Edit, taskId);
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") }); }

			var (ok, err) = await _checklist.ReorderAsync(gate.CompanyId, taskId, orderedIds ?? Array.Empty<int>());
			return Json(new { ok, error = err });
		}

		// ---- dependencies ---------------------------------------------------------------------
		[HttpGet]
		public async Task<IActionResult> Dependencies(int taskId)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedJson();

			var edges = await _deps.ForTaskAsync(co, taskId);
			return Json(edges.Select(e => new
			{
				id = e.Id,
				isPredecessor = e.SuccessorTaskId == taskId,   // true = the OTHER task must finish first
				otherId = e.SuccessorTaskId == taskId ? e.PredecessorTaskId : e.SuccessorTaskId,
				otherTitle = e.SuccessorTaskId == taskId ? e.PredecessorTitle : e.SuccessorTitle,
				kind = e.Kind, lagDays = e.LagDays, blocking = e.IsBlocking
			}));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DependencyAdd(int predecessorTaskId, int successorTaskId, string kind, int lagDays = 0)
		{
			// BOTH ends are authorised: an edge changes what each of them waits for, so a caller who may
			// edit only one of the pair may not create it.
			var g1 = await TaskGateAsync(TasksActions.Edit, predecessorTaskId);
			var g2 = await TaskGateAsync(TasksActions.Edit, successorTaskId);
			if (!g1.Ok || !g2.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"),
				// The KIND of refusal, so the view can dress a missing permission differently from a
				// validation refusal. Branching on the message text would break the moment the UI
				// language changed.
				code = "forbidden" }); }

			// Either gate's company would do — both passed, and both verified their end's row against the SAME
			// resolved context, so g1.CompanyId == g2.CompanyId by construction.
			var (ok, err, id) = await _deps.AddAsync(g1.CompanyId, predecessorTaskId, successorTaskId,
				kind ?? CrossBuy.Models.Context.Tasks.TaskDependencyKinds.FinishToStart, lagDays, CurrentEmployeeId());
			// The cycle refusal carries the offending chain in its message — pass it through unchanged
			// rather than replacing it with a generic failure the user cannot act on.
			return Json(new { ok, error = err, id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DependencyRemove(int id)
		{
			// The edge is looked up INSIDE the resolved company, so another company's edge id has no endpoints
			// here and both gates refuse — the same shape as the checklist mutations above.
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedJson();

			var ends = await _deps.EndpointsAsync(co, id);
			var g1 = ends == null ? new TaskGate() : await TaskGateAsync(TasksActions.Edit, ends.Value.predecessorTaskId);
			var g2 = ends == null ? new TaskGate() : await TaskGateAsync(TasksActions.Edit, ends.Value.successorTaskId);
			if (!g1.Ok || !g2.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action"),
				// The KIND of refusal, so the view can dress a missing permission differently from a
				// validation refusal. Branching on the message text would break the moment the UI
				// language changed.
				code = "forbidden" }); }

			var (ok, err) = await _deps.RemoveAsync(co, id);
			return Json(new { ok, error = err });
		}

		// ---- attachments & comments (the Comm platform, which is the store that owns attachments) ----
		//
		// WHY THE COMM PLATFORM AND NOT DocComments: attachments hang off CommComments via
		// CommCommentAttachments. DocComments has no attachment table, so attaching a file to a DocComment
		// would mean inventing a link — which is exactly the second attachment system that is forbidden.
		// So the task discussion IS the Comm thread, and attachments are native to it.

		private async Task<CrossBuy.Models.Platform.BusinessContext?> CommContextAsync(CancellationToken ct)
			=> await _businessContexts.TryGetCurrentAsync(ct);

		// The machine code the discussion endpoints return when the platform is absent. It is a CODE, not a
		// sentence, because the browser branches on it: an unavailable capability must be distinguishable from
		// a refusal and from a failure, and a translated sentence cannot carry that distinction.
		// Public because it IS the public contract: the browser and the tests both branch on it, and a
		// contract two callers depend on should not be a literal repeated in three places.
		public const string CommunicationUnavailableCode = "communication_unavailable";

		// Asks the container for the optional platform. Both services come from the SAME registration
		// (AddCommunicationPlatform), so a half-present pair would mean a hand-rolled partial registration —
		// treated as absent rather than used, because a discussion that can list comments but not add one is a
		// worse answer than an honest "unavailable".
		private (CrossBuy.BL.Communication.ICommThreadService Threads,
		         CrossBuy.BL.Communication.ICommCommentService Comments)? TryCommunication()
		{
			var threads = _services.GetService<CrossBuy.BL.Communication.ICommThreadService>();
			var comments = _services.GetService<CrossBuy.BL.Communication.ICommCommentService>();
			return threads is null || comments is null ? null : (threads, comments);
		}

		// 503, deliberately: this is not the caller's fault (4xx) and nothing threw (500). It is a capability the
		// deployment has not activated, and it becomes available the moment it is — which is precisely what
		// "Service Unavailable" states.
		private IActionResult CommunicationUnavailable() => StatusCode(StatusCodes.Status503ServiceUnavailable, new
		{
			ok = false,
			unavailable = true,
			code = CommunicationUnavailableCode,
			error = T("منصّة الاتصالات غير مفعّلة في هذه البيئة، فالمناقشة والمرفقات غير متاحة. باقي شاشات المهام تعمل بالكامل.",
			          "The Communication Platform is not activated in this environment, so discussion and attachments are unavailable. The rest of Tasks works normally.")
		});

		// Employee id -> photo, for the activity timeline. TimelineItemViewModel carries the actor's
		// ID and NAME but no avatar, and that DTO belongs to Models/Platform (another tab), so the
		// ids it returns are resolved here instead of widening the platform contract.
		// Company-scoped and capped: this answers "what does this person look like", nothing more.
		[SessionValidation][HttpGet]
		public async Task<IActionResult> ActorAvatars(string? ids, CancellationToken ct = default)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return Json(new Dictionary<string, string>());

			var wanted = (ids ?? "")
				.Split(',', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
				.Where(n => n > 0)
				.Distinct()
				.Take(100)
				.ToList();
			if (wanted.Count == 0) return Json(new Dictionary<string, string>());

			// Resolved through _services, the same lazy pattern this controller already uses for the
			// Communication platform - rather than adding a DbContext to a constructor that
			// deliberately takes services only.
			var db = _services.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>();
			var rows = await db.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == co && wanted.Contains(e.ID)
					&& e.ProfileImage != null && e.ProfileImage != "")
				.Select(e => new { e.ID, e.ProfileImage })
				.ToListAsync(ct);

			return Json(rows.ToDictionary(r => r.ID.ToString(), r => r.ProfileImage));
		}

		// WHAT THE CHECKLIST PANE RENDERS. It used to be drawn by Razor and repainted by a full page
		// reload after every add, toggle and delete - which discarded the open tab, the scroll position
		// and any unsent comment draft, and re-ran every query on the page to redraw one row.
		[SessionValidation][HttpGet]
		public async Task<IActionResult> ChecklistList(int taskId)
		{
			var gate = await TaskGateAsync(TasksActions.Read, taskId);
			if (!gate.Ok) return Forbid();

			var rows = await _checklist.ForTaskAsync(gate.CompanyId, taskId);
			return Json(rows.Select(c => new
			{
				id = c.ID,
				done = c.IsDone,
				title = CrossBuy.BL.DisplayName.Of(c.Title, c.TitleEn)
			}));
		}

		// The same, for the dependency pane. Which END the other task sits on decides the arrow and the
		// wording, and only this action knows which task the page is about - so it is resolved here
		// rather than sending both ends and leaving the view to work it out.
		[SessionValidation][HttpGet]
		public async Task<IActionResult> DependencyList(int taskId)
		{
			var gate = await TaskGateAsync(TasksActions.Read, taskId);
			if (!gate.Ok) return Forbid();

			var rows = await _deps.ForTaskAsync(gate.CompanyId, taskId);
			return Json(rows.Select(d => new
			{
				id = d.Id,
				otherIsPredecessor = d.SuccessorTaskId == taskId,
				otherId = d.SuccessorTaskId == taskId ? d.PredecessorTaskId : d.SuccessorTaskId,
				otherTitle = d.SuccessorTaskId == taskId ? d.PredecessorTitle : d.SuccessorTitle,
				isBlocking = d.IsBlocking
			}));
		}

		// @-MENTION SUGGESTIONS for the comment composer. The platform already owns everything that
		// happens AFTER the text is written: CommBodyPolicy parses "@[Name](employee:12)" out of the body,
		// CommMentionService writes the mention, the audit entry and the timeline source, and
		// CommNotificationService delivers it - skipping the author, because
		// ExcludeActorFromOwnNotifications is on. What was missing was only the list to pick FROM.
		//
		// The caller is left OUT of the list. Mentioning yourself notifies nobody by design, so offering
		// your own name would advertise an action that does nothing.
		[SessionValidation][HttpGet]
		public async Task<IActionResult> MentionSearch(string? q, CancellationToken ct = default)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return Json(Array.Empty<object>());

			// THE QUERY MOVED, THE BEHAVIOUR DID NOT. It now lives in CommMentionCandidates because the
			// document conversation panel needs the same list: who may be offered in an @-picker is one
			// rule, and a second copy of it drifts silently - a picker offering the wrong people still
			// looks like it works. The exclusions, the two-language match and the cap are unchanged.
			var db = _services.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>();
			var rows = await CrossBuy.BL.Communication.CommMentionCandidates.SearchAsync(
				db, co, CurrentEmployeeId(), q, arabic: IsAr, cancellationToken: ct);

			return Json(rows.Select(r => new { id = r.Id, name = r.Name, avatar = r.Avatar }));
		}

        // Collaboration is rendered by Communication; Tasks retains its record-level read gate.
        [HttpGet]
        public async Task<IActionResult> TaskActivity(int taskId, DateTime? before = null,
            string? beforeKey = null, int take = 100, CancellationToken ct = default)
        {
            var gate = await TaskGateAsync(TasksActions.Read, taskId);
            if (!gate.Ok) return Forbid();
            var timeline = _services.GetService<CrossBuy.BL.Communication.ICommTimelineAggregator>();
            if (timeline == null) return CommunicationUnavailable();
            var context = await CommContextAsync(ct);
            if (context == null || context.CompanyId != gate.CompanyId) return Unauthorized();
            var page = await timeline.GetAsync(context, new CrossBuy.Models.Communication.CommTimelineQuery
            {
                Entity = new CrossBuy.Models.Communication.CommEntityRef(
                    CrossBuy.BL.TasksCalendar.TaskCalendarEntityCodes.Task, taskId),
                Before = before, BeforeKey = beforeKey, Take = Math.Clamp(take, 1, 100)
            }, ct);
            ct.ThrowIfCancellationRequested();
            if (page.SourceReports.Any(r => r.Source == "*" && r.Note?.StartsWith("denied:", StringComparison.Ordinal) == true))
                return Forbid();
            return Json(new
            {
                items = page.Items.Select(i => new
                {
                    key = i.ItemKey, source = i.Source,
                    title = IsAr ? CrossBuy.BL.DisplayName.Or(i.TitleAr, i.TitleEn)
                                 : CrossBuy.BL.DisplayName.Or(i.TitleEn, i.TitleAr),
                    description = i.Body ?? (IsAr ? CrossBuy.BL.DisplayName.Or(i.DescriptionAr, i.DescriptionEn)
                                                : CrossBuy.BL.DisplayName.Or(i.DescriptionEn, i.DescriptionAr)),
                    actorEmployeeId = i.ActorEmployeeId, actorName = i.ActorDisplayName,
                    icon = i.Icon, color = i.Color, createdAt = i.OccurredAt
                }),
                nextBefore = page.NextBefore, nextBeforeKey = page.NextBeforeKey,
                // Do not expose exception messages from a failed contributor to the browser.
                degraded = page.SourceReports.Any(r => r.Note?.StartsWith("error:", StringComparison.Ordinal) == true)
            });
        }

		[HttpGet]
		public async Task<IActionResult> TaskComments(int taskId, CancellationToken ct = default)
		{
			// The permission gate runs FIRST and is untouched by availability: a caller with no right to read
			// this task must be refused, never told which platforms the deployment happens to run.
			var gate = await TaskGateAsync(TasksActions.Read, taskId);
			if (!gate.Ok) return Forbid();

			var comm = TryCommunication();
			if (comm == null) return CommunicationUnavailable();

			var ctx = await CommContextAsync(ct);
			if (ctx == null) return Unauthorized();

			var thread = await comm.Value.Threads.GetOrCreateAsync(ctx, new CrossBuy.Models.Communication.CommThreadRequest
			{
				Entity = new CrossBuy.Models.Communication.CommEntityRef(
					CrossBuy.BL.TasksCalendar.TaskCalendarEntityCodes.Task, taskId)
			}, ct);

			var page = await comm.Value.Comments.ListAsync(ctx, thread.Id, null, ct);
			return Json(page.Items.Select(c => new
			{
				id = c.CommentId,
				body = c.Body,
				author = c.Author.Display(IsAr),
				// The actor already carries a photo (CommActorDto.AvatarUrl); only the display name was
				// being sent, so every comment rendered a bare initial in its avatar circle.
				authorAvatar = c.Author.AvatarUrl,
				createdAt = c.CreatedAt,
				editedAt = c.EditedAt,
				// The RESOLVED labels, so the reader sees a name where the author typed a token. The label was
				// frozen when the mention was made (CommMentionService), which is the point: a renamed
				// department was not the department that was mentioned.
				mentions = c.Mentions.Select(m => new
				{
					kind = m.TargetKind,
					targetId = m.TargetId,
					targetKey = m.TargetKey,
					label = IsAr ? (m.LabelAr ?? m.LabelEn) : (m.LabelEn ?? m.LabelAr)
				}),
				attachments = c.Attachments.Select(a => new
				{
					id = a.AttachmentId, fileName = a.FileName, contentType = a.ContentType,
					sizeBytes = a.SizeBytes,
					// The platform ALREADY classifies whether a file can be shown inline and what kind of
					// preview it is (Module 11, CommFilePreviewDto). None of it was being sent, so the UI
					// could only offer a download link for an image it was allowed to display.
					previewKind = a.Preview.Kind,
					canInline = a.Preview.CanInline,
					previewReason = a.Preview.Reason,
					// The StorageKey IS the deployment's path. The platform resolves no url by contract;
					// this controller does, because it owns the store the key points into.
					url = a.StorageKey
				})
			}));
		}

		/// A comment, optionally carrying files. One call, so the comment and its attachments share a fate —
		/// CommAttachmentService enrols in the comment's transaction rather than saving separately.
		[HttpPost]
		[ValidateAntiForgeryToken]
		[RequestSizeLimit(268435456)]   // 256 MB, matching FileManagerController
		public async Task<IActionResult> TaskCommentAdd(int taskId, string? body, List<IFormFile>? files,
			CancellationToken ct = default)
		{
			var gate = await TaskGateAsync(TasksActions.Edit, taskId);
			if (!gate.Ok) return Json(new { ok = false, error = T("ليست لديك صلاحية", "You do not have permission") });

			// Checked BEFORE a single byte is written: uploading files to disk and registering them in the file
			// library, only to discover there is no platform to attach them to, would leave orphaned files behind
			// on every attempt.
			var comm = TryCommunication();
			if (comm == null) return CommunicationUnavailable();

			var ctx = await CommContextAsync(ct);
			if (ctx == null) return Json(new { ok = false, error = T("جلسة غير صالحة", "Invalid session") });

			bool hasFiles = files != null && files.Any(f => f != null && f.Length > 0);
			if (string.IsNullOrWhiteSpace(body) && !hasFiles)
				return Json(new { ok = false, error = T("اكتب تعليقًا أو أرفق ملفًا", "Write a comment or attach a file") });

			var attachments = new List<CrossBuy.Models.Communication.CommAttachmentRequest>();
			if (hasFiles)
			{
				// The SAME directory convention FileManagerController uses. Not a new location, and not a
				// task-private one: a file attached to a task is a company file like any other.
				var dir = Path.Combine(_env.WebRootPath, "uploads", "library", gate.CompanyId.ToString());
				if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

				foreach (var f in files!.Where(x => x != null && x.Length > 0))
				{
					var origName = Path.GetFileName(f.FileName);
					var ext = Path.GetExtension(origName);
					var stored = Guid.NewGuid().ToString("N") + ext;
					await using (var fs = new FileStream(Path.Combine(dir, stored), FileMode.Create))
						await f.CopyToAsync(fs, ct);

					var webPath = "/uploads/library/" + gate.CompanyId + "/" + stored;

					// Registered in the file library too, so an attached file is findable in File Manager
					// rather than existing only as a row in a comment table.
					await _files.AddFileAsync(gate.CompanyId, gate.EmployeeId ?? 0, null,
						origName, null, webPath, f.ContentType, f.Length);

					attachments.Add(new CrossBuy.Models.Communication.CommAttachmentRequest
					{
						FileName = origName,
						ContentType = f.ContentType ?? "application/octet-stream",
						SizeBytes = f.Length,
						StorageKey = webPath
					});
				}
			}

			try
			{
				var dto = await comm.Value.Comments.AddAsync(ctx, new CrossBuy.Models.Communication.CommCommentRequest
				{
					Entity = new CrossBuy.Models.Communication.CommEntityRef(
						CrossBuy.BL.TasksCalendar.TaskCalendarEntityCodes.Task, taskId),
					ThreadKind = CrossBuy.Models.Communication.CommThreadKind.Discussion,
					ThreadKey = "",
					Body = string.IsNullOrWhiteSpace(body) ? "" : body,
					Attachments = attachments.Count == 0 ? null : attachments
				}, ct);

				return Json(new { ok = true, id = dto.CommentId, attachments = dto.Attachments.Count });
			}
			catch (CrossBuy.Models.Communication.CommValidationException ex)
			{
				// The platform's own validation message (size cap, count cap, missing key) is more useful
				// than a generic failure, and it is already a machine code plus a sentence.
				return Json(new { ok = false, error = ex.Message });
			}
		}

		// ---- templates ------------------------------------------------------------------------
		[HttpGet]
		public async Task<IActionResult> Templates()
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Templates = await _templates.ListAsync(co, activeOnly: false);
			ViewBag.Employees = await _tasks.ActiveEmployeesAsync(co);
			return View("Templates");
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> TemplateSave(string payload)
		{
			var gate = await TaskManageGateAsync();
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") }); }

			CrossBuy.Models.Context.Tasks.TaskTemplate? input = null;
			try { input = JsonSerializer.Deserialize<CrossBuy.Models.Context.Tasks.TaskTemplate>(payload ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
			catch (JsonException) { /* fall through to the null check — a malformed body is a bad request, not a 500 */ }
			if (input == null) return Json(new { ok = false, error = T("بيانات غير صالحة", "Invalid data") });

			// TaskManageGateAsync resolved and authorised this company; a template is company-wide, so its
			// company must be the gate's and never a literal — this action was creating and editing company 1's
			// templates on behalf of a manager in any company.
			var (ok, err, id) = await _templates.SaveAsync(gate.CompanyId, input, CurrentEmployeeId());
			return Json(new { ok, error = err, id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> TemplateSetActive(int id, bool active)
		{
			var gate = await TaskManageGateAsync();
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") }); }

			var (ok, err) = await _templates.SetActiveAsync(gate.CompanyId, id, active);
			return Json(new { ok, error = err });
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> TemplateApply(int id, DateTime anchorDate, int? assigneeOverride)
		{
			// Applying a template CREATES tasks, so it needs the create right — not merely the right to
			// edit the template it came from.
			var scope = await _company.ResolveAsync();
			var ctx = scope.Ok ? await _businessContexts.TryGetCurrentAsync() : null;
			if (ctx == null || !await _tasksAccess.CanAsync(ctx, TasksActions.Create, new PermissionTarget { CompanyId = scope.CompanyId }))
				{ this.ToastError(T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action")); return RedirectToAction(nameof(Index)); }

			// The company that was AUTHORISED two lines above, not a literal. This action authorised Create in
			// `scope.CompanyId` and then created the tasks in company 1 — so a company-65 manager's template
			// application produced company-1 tasks. The two must be the same company or the check means nothing.
			var (ok, err, result) = await _templates.ApplyAsync(scope.CompanyId, id, anchorDate, assigneeOverride, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T($"تم إنشاء {result!.CreatedTaskIds.Count} مهمة من القالب", $"{result!.CreatedTaskIds.Count} tasks created from the template"));
			else this.ToastError(err ?? T("تعذّر تطبيق القالب", "Could not apply the template"));
			return RedirectToAction(nameof(Templates));
		}

		// ============================================================================================
		// COMMUNICATION PHASE 3 — the two optional endpoints.
		//
		// OPTIONAL, THROUGH THE CONTAINER, exactly like TryCommunication above: a deployment that has not
		// activated the Communication Platform answers 503 with `communication_unavailable`, and every
		// other Tasks screen keeps working. These actions add no mandatory constructor dependency, so a
		// graph without Communication still builds.
		//
		// AUTHORIZATION IS NOT DUPLICATED HERE. The adapter runs its own checks; what this layer owns is
		// the TASK-side gate and the session, in that order — permission first, availability second, so a
		// caller with no right to the task is refused without learning which platforms this deployment
		// happens to run.
		// ============================================================================================
		private CrossBuy.BL.Communication.ICommTaskIntegrationService? TryTaskIntegration()
			=> _services.GetService<CrossBuy.BL.Communication.ICommTaskIntegrationService>();

		// POST: a task opened from a message in a conversation.
		//
		// THE CONTRACT IS THE PANEL'S. _EntityConversation posts entity, id, commentId, title, titleEn,
		// description, assigneeEmployeeId, priority, dueDate and dedupKey as form fields with the
		// anti-forgery token. The first version of this action took commentId alone and would have
		// rejected the panel's request as malformed.
		//
		// `entity` AND `id` ARE ACCEPTED AND DELIBERATELY NOT TRUSTED. They are what the browser says the
		// conversation is about, and a browser is not an authorization source. The anchor the task
		// actually gets is derived SERVER-SIDE by the adapter from commentId — it reads the comment, its
		// thread and that thread's entity. So a caller who lies about `entity` changes nothing: the task
		// still anchors to the record the message really belongs to, or to nothing. They are bound here
		// only so the request shape matches and so the parameters are visible in the signature rather
		// than silently discarded from a form collection.
		//
		// dedupKey is passed STRAIGHT THROUGH. Idempotency for a double-clicked dialog belongs to the
		// adapter, which returns the FIRST task for a repeated key; generating a key here would make
		// every retry a new task.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateFromMessage(string? entity, int id, long commentId,
			string title, string? titleEn, string? description, int assigneeEmployeeId,
			string? priority, DateTime? dueDate, string? dedupKey, CancellationToken ct = default)
		{
			// CREATE, not Read: this makes a task, and no task id exists yet, so the company-level create
			// right is what is asked. The gate runs BEFORE the availability check so an unauthorized
			// caller is refused without learning which platforms this deployment runs.
			var scope = await _company.ResolveAsync();
			var ctx = scope.Ok ? await _businessContexts.TryGetCurrentAsync(ct) : null;
			if (ctx == null) return Unauthorized();
			if (!await _tasksAccess.CanAsync(ctx, TasksActions.Create,
				new PermissionTarget { CompanyId = scope.CompanyId })) return Forbid();

			var integration = TryTaskIntegration();
			if (integration == null) return CommunicationUnavailable();

			if (string.IsNullOrWhiteSpace(title))
				return Json(new { ok = false, error = T("العنوان مطلوب", "A title is required") });

			var result = await integration.CreateFromMessageAsync(ctx, new CrossBuy.BL.Communication.CommTaskCreateRequest
			{
				CommentId = commentId,
				Title = title,
				TitleEn = titleEn,
				Description = description,
				AssigneeEmployeeId = assigneeEmployeeId,
				Priority = string.IsNullOrWhiteSpace(priority) ? "Normal" : priority,
				DueDate = dueDate,
				DedupKey = dedupKey,
			}, ct);

			if (!result.Ok) return Json(new { ok = false, error = result.Error, code = result.Error });

			// THE TASK IS MADE; THE PROVENANCE LINK IS A SECOND ACT, AND IT CAN FAIL ON ITS OWN.
			//
			// CreateFromMessageAsync creates the task and writes an audit row, and that is all: it never
			// calls the link store, so a task raised from a message held no CommTaskLink and appeared in
			// neither the conversation's linked list nor the task's own related-messages list. The only
			// record that the message was its source was an audit entry nothing reads.
			//
			// So the link is created here, from the same commentId, through the ordinary authorized
			// path. Two separate writes, deliberately not wrapped in one transaction: they are owned by
			// different services with their own SaveChanges, and the honest handling of a half-done pair
			// is to REPORT it rather than to pretend a rollback this architecture cannot perform.
			//
			// NOTHING IS DUPLICATED BY A RETRY. The create is idempotent on DedupKey (it returns the
			// existing task id rather than making a second), and AddAsync is idempotent on
			// (company, task, thread, comment) — so re-sending a request whose link failed completes the
			// pair instead of making another task.
			//
			// THE ORIGIN IS RECORDED TRUTHFULLY, and it is the caller that supplies it.
			// CommTaskLinkRequest.Origin defaults to LinkedExisting and is validated against
			// CommTaskLinkOrigin before it reaches the column's CHECK constraint, so this path names
			// CreatedFromMessage explicitly below. It was briefly a known loss — the request carried no
			// Origin and every link read as LinkedExisting whatever made it — and it was reported
			// rather than faked. The parameter closed it without a second link write.
			long? linkId = result.LinkId;
			string? linkError = null;

			if (linkId is not > 0 && commentId > 0 && integration.LinkingAvailable)
			{
				// THE THREAD IS READ OFF THE MESSAGE, never taken from the caller and never guessed from
				// the entity. This action is not given a threadId, and inventing one from the record
				// would pick the wrong conversation whenever a record has more than one. GetAsync
				// authorizes the read, so a message this caller may not see yields no link rather than a
				// link to a conversation they cannot open.
				var comm = TryCommunication();
				long sourceThreadId = 0;
				if (comm != null)
				{
					try
					{
						var message = await comm.Value.Comments.GetAsync(ctx, commentId, ct);
						sourceThreadId = message.ThreadId;
					}
					catch (CrossBuy.Models.Communication.CommNotFoundException) { }
					catch (CrossBuy.Models.Communication.CommAccessDeniedException) { }
				}

				if (sourceThreadId <= 0)
				{
					linkError = CrossBuy.BL.Communication.CommTaskErrors.MessageNotFound;
				}
				else
				{
					var link = await integration.LinkExistingAsync(ctx, new CrossBuy.BL.Communication.CommTaskLinkRequest
					{
						ThreadId = sourceThreadId,
						TaskId = result.TaskId,
						CommentId = commentId,

						// ONLY THE CALLER KNOWS THIS. From inside the service, a link written moments
						// after a creation is indistinguishable from somebody attaching an existing
						// task, so it cannot infer the provenance and must be told. This is the create
						// path; LinkConversation leaves the LinkedExisting default, which is exactly
						// what attaching an already-existing task is.
						Origin = CrossBuy.BL.Communication.CommTaskLinkOrigin.CreatedFromMessage,
					}, ct);

					if (link.Ok && link.LinkId is > 0) { linkId = link.LinkId; }
					else { linkError = link.Error ?? CrossBuy.BL.Communication.CommTaskErrors.LinkRejected; }
				}
			}

			// AnchorWarning IS SURFACED, not swallowed: it means the task exists but the conversation's
			// business anchor could not be carried across, and the panel renders a different, warning-
			// coloured confirmation for it. A caller that never hears so believes the link is there.
			//
			// `linked` is a FIELD, not something to infer from a null id. The partial state — task
			// created, message not attached — is a real outcome with a real recovery (retry, which
			// completes the pair), and a caller that reads only `ok` must still not be able to render a
			// plain success over it.
			return Json(new
			{
				ok = true,
				error = (string?)null,
				taskId = result.TaskId,
				anchorWarning = result.AnchorWarning,
				linkId,
				linked = linkId is > 0,
				linkError,
				commentId,
			});
		}

		// GET: the tasks a record's conversation is about — ?entity=<code>&id=<n>.
		//
		// THE THREAD IS RESOLVED ON THE SERVER AND NEVER ACCEPTED FROM THE CALLER. The first version of
		// this action took a threadId from the query string, which is a record identifier handed to the
		// server by the browser: a caller could have walked other conversations by incrementing it. The
		// panel never sent one either — it sends the record, which is what it legitimately knows.
		//
		// ListForEntityAsync is the resolution, and it is the authorization too: it returns "every thread
		// on a record the caller may read", so a record they may not read yields no threads and therefore
		// no tasks. That is also why an empty answer is returned rather than a 404 — distinguishing "no
		// conversation" from "not yours" would make this endpoint a probe for which records exist.
		[SessionValidation][HttpGet]
		public async Task<IActionResult> ForConversation(string? entity, int id, CancellationToken ct = default)
		{
			var scope = await _company.ResolveAsync();
			var ctx = scope.Ok ? await _businessContexts.TryGetCurrentAsync(ct) : null;
			if (ctx == null) return Unauthorized();
			if (!await _tasksAccess.CanAsync(ctx, TasksActions.Read,
				new PermissionTarget { CompanyId = scope.CompanyId })) return Forbid();

			var integration = TryTaskIntegration();
			var comm = TryCommunication();
			if (integration == null || comm == null) return CommunicationUnavailable();

			if (string.IsNullOrWhiteSpace(entity) || id <= 0)
				return Json(new { ok = false, error = T("سجل غير صالح", "Invalid record") });

			var threads = await comm.Value.Threads.ListForEntityAsync(
				ctx, new CrossBuy.Models.Communication.CommEntityRef(entity, id), ct);

			// ONE ROW PER RELATIONSHIP, AND THE TASK ID IS NOT THE KEY.
			//
			// WHAT THIS USED TO DO AND WHY IT IS NOW WRONG. It collapsed everything into a
			// Dictionary<int, CommTaskRef> keyed by task id, so a task could appear once. That was
			// correct while the store allowed one link per (task, thread): the only duplicates possible
			// were an explicit link and the same task showing up again as shared business context.
			//
			// comm_task_links_m2m changed the rule underneath it. A task may now be attached to SEVERAL
			// MESSAGES in one conversation, and each of those is its own relationship with its own
			// LinkId that unlinks on its own. Keyed by task id, the second link overwrote the first:
			// the panel showed one row, the reader could not tell which message it pointed at, and the
			// link that lost the race could not be removed from the UI at all because its id was gone.
			// Ordering decided which survived, so a conversation-level link could also silently replace
			// a message-level one — the downgrade this wave exists to prevent.
			//
			// So explicit links are keyed by LINK ID and never merged. Shared business context is still
			// collapsed by task, because it is INFERRED rather than stored: it has no LinkId, the same
			// task is the same inference however many times it is derived, and two identical rows would
			// read as two tasks. It is also suppressed entirely for a task that already carries an
			// explicit link in this conversation — the deliberate relationship is the truer statement,
			// and showing both would describe one task twice in two different vocabularies.
			var explicitLinks = new Dictionary<long, (long ThreadId, CrossBuy.BL.Communication.CommTaskRef Ref)>();
			var shared = new Dictionary<int, (long ThreadId, CrossBuy.BL.Communication.CommTaskRef Ref)>();
			var linkedTaskIds = new HashSet<int>();

			foreach (var thread in threads)
			{
				foreach (var r in await integration.LinkedTasksAsync(ctx, thread.ThreadId, ct))
				{
					// A link with no id cannot be addressed by the unlink endpoint, so it is not a row a
					// reader can act on. It should not occur; it is dropped rather than drawn.
					if (r.LinkId is not > 0) { continue; }
					explicitLinks[r.LinkId.Value] = (thread.ThreadId, r);
					linkedTaskIds.Add(r.TaskId);
				}

				foreach (var r in await integration.RelatedTasksAsync(ctx, thread.ThreadId, ct))
					if (!shared.ContainsKey(r.TaskId)) { shared[r.TaskId] = (thread.ThreadId, r); }
			}

			var rows = explicitLinks.Values
				.OrderByDescending(x => x.Ref.LinkId)
				.Concat(shared.Where(kv => !linkedTaskIds.Contains(kv.Key)).Select(kv => kv.Value))
				.ToList();

			// Resolve the assignee label once, inside the same company, before serializing the rows.
			// The conversation panel already renders `assigneeName` in the reader's UI language; until
			// this lookup travels with the response it can only fall back to an employee number.
			var assigneeIds = rows.Select(x => x.Ref.AssigneeEmployeeId)
				.Where(id => id > 0).Distinct().ToList();
			var assignees = assigneeIds.Count == 0
				? new Dictionary<int, (string Name, string? Avatar)>()
				: (await _services.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>()
					.Employee.AsNoTracking()
					.Where(e => e.EmpCompanyID == scope.CompanyId && assigneeIds.Contains(e.ID))
					.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage })
					.ToListAsync(ct))
					.ToDictionary(e => e.ID, e =>
						(Name: CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn),
						 Avatar: string.IsNullOrWhiteSpace(e.ProfileImage) ? null : e.ProfileImage));

			// LinkingAvailable is the SERVICE's answer and the only thing that may reveal the link button.
			// Reported even when the list is empty, because empty means two different things: nobody
			// linked anything, or the link table is not provisioned.
			return Json(new
			{
				ok = true,
				linkingAvailable = integration.LinkingAvailable,

				// EVERY FIELD THE PANEL RENDERS. An earlier projection returned five of them, so the rows
				// drew with an undefined route, no due date and no assignee — the shape has to match what
				// the view reads, not what the endpoint found convenient to send.
				// `tasks` keeps its name because the panel reads it, but it is now a list of
				// RELATIONSHIPS: the same taskId may legitimately appear more than once, each time with
				// a different linkId and commentId. A consumer that still keys this by taskId will drop
				// rows — that is the bug being fixed, not a shape to preserve.
				tasks = rows.Select(x => new
				{
					taskId = x.Ref.TaskId,
					title = CrossBuy.BL.DisplayName.Of(x.Ref.Title, x.Ref.TitleEn),
					titleEn = x.Ref.TitleEn,
					status = x.Ref.Status,
					priority = x.Ref.Priority,
					dueDate = x.Ref.DueDate,
					assigneeEmployeeId = x.Ref.AssigneeEmployeeId,
					assigneeName = assignees.TryGetValue(x.Ref.AssigneeEmployeeId, out var assignee) ? assignee.Name : null,
					assigneeAvatar = assignees.TryGetValue(x.Ref.AssigneeEmployeeId, out assignee) ? assignee.Avatar : null,
					progressPct = x.Ref.ProgressPct,
					relationship = x.Ref.Relationship,
					linkId = x.Ref.LinkId,
					commentId = x.Ref.CommentId,

					// WHICH KIND OF LINK, as a field rather than something to infer. A reader deciding
					// "message or conversation" from commentId != null would be re-deriving a fact the
					// server already knows, and would get it wrong for a shared-context row.
					scope = x.Ref.LinkId is > 0
						? (x.Ref.CommentId is > 0 ? "message" : "conversation")
						: "inferred",

					// The conversation this relationship sits in. With several threads on one record the
					// panel cannot otherwise tell which one an unlink belongs to.
					threadId = x.ThreadId,
					route = x.Ref.Route,
				}),
			});
		}

		// GET: the pending approvals on the record a conversation is about —
		// ?entity={entityCode}&id={entityId}. READ ONLY: this endpoint decides nothing and writes
		// nothing; it renders what the approvals module already decided, for the viewer who is asking.
		//
		// ADDRESSED BY THE RECORD, like the tasks panel, and for the same reason: the record is what the
		// panel legitimately knows. No threadId, no companyId and no employeeId is accepted from the
		// browser — the company comes from the resolved scope and the viewer from the BusinessContext.
		//
		// THREE STATES ARE DELIBERATELY INDISTINGUISHABLE, and the adapter's own contract says so: the
		// approvals module is absent, the record has no approval workflow, or the viewer is not an
		// approver for it. All three answer with an empty list. Distinguishing them would let a caller
		// use this endpoint to discover which records carry approvals and who may decide them.
		//
		// THE ROUTE IS RESOLVED HERE AND NOWHERE ELSE. CommConversationApproval carries the owning
		// module's Controller, Action and RouteValues and deliberately NOT a url — Communication does not
		// invent a route to somebody else's screen, and nor does the browser. Url.Action turns the three
		// into a link through the routing table, so a module that moves its screen does not strand this
		// panel, and a row whose route cannot be resolved renders as plain text rather than a dead anchor.
		[SessionValidation][HttpGet]
		public async Task<IActionResult> ConversationApprovals(string? entity, int id,
			CancellationToken ct = default)
		{
			// THE VIEWER IS RESOLVED SERVER-SIDE, and the gate runs BEFORE the availability check so an
			// unauthorized caller is refused without learning which platforms this deployment runs.
			var scope = await _company.ResolveAsync();
			var ctx = scope.Ok ? await _businessContexts.TryGetCurrentAsync(ct) : null;
			if (ctx == null)
				return Json(new { ok = false, code = CrossBuy.BL.Communication.CommTaskErrors.NoCompanyScope });

			if (!await _tasksAccess.CanAsync(ctx, TasksActions.Read,
				new PermissionTarget { CompanyId = scope.CompanyId })) return Forbid();

			var integration = TryTaskIntegration();
			if (integration == null) return CommunicationUnavailable();

			if (string.IsNullOrWhiteSpace(entity) || id <= 0)
				return Json(new { ok = false, code = CrossBuy.BL.Communication.CommTaskErrors.ConversationNotFound });

			var approvals = await integration.ApprovalsForEntityAsync(
				ctx, new CrossBuy.Models.Communication.CommEntityRef(entity, id), ct);

			// SUPPORTED=FALSE IS NOT AN EMPTY INBOX, and conflating them is the whole reason this shape
			// changed. "This record type has no approval workflow" and "you have nothing to approve on
			// this record" look identical as an empty array, and a panel that cannot tell them apart
			// renders a permanently empty section on every screen that will never have one. The reason
			// travels with the answer so the panel can hide itself instead.
			return Json(new
			{
				ok = true,
				supported = approvals.Supported,
				notApplicableReason = approvals.NotApplicableReason,
				approvals = approvals.Items.Select(a => new
				{
					reference = a.Reference,
					silo = a.Silo,
					entityId = a.EntityId,
					status = a.Status,
					titleAr = a.TitleAr,
					titleEn = a.TitleEn,
					requesterNameAr = a.RequesterNameAr,
					requesterNameEn = a.RequesterNameEn,
					submittedAt = a.SubmittedAt,
					amount = a.Amount,
					url = ApprovalUrl(a),
				}),
			});
		}

		// The owning module's navigation, resolved through the routing table rather than composed as a
		// string. A composed path would hard-code somebody else's URL shape here and would also be a
		// place a route value could be smuggled in unescaped; Url.Action escapes and validates.
		//
		// NULL WHEN IT CANNOT BE RESOLVED. The panel renders a row without a link rather than an anchor
		// that 404s, which is the honest rendering of "this module did not give me a screen".
		private string? ApprovalUrl(CrossBuy.BL.Communication.CommConversationApproval approval)
		{
			if (string.IsNullOrWhiteSpace(approval.Controller) || string.IsNullOrWhiteSpace(approval.Action))
				return null;

			var values = new RouteValueDictionary();
			if (approval.RouteValues != null)
				foreach (var pair in approval.RouteValues)
					values[pair.Key] = pair.Value;

			return Url.Action(approval.Action, approval.Controller, values);
		}

		// ============================================================================================
		// LINK AND UNLINK — the two write endpoints for the conversation's task panel.
		//
		// THE CONTROLLER DECIDES NOTHING ABOUT AUTHORIZATION HERE. CommTaskIntegrationService already
		// owns company scope, record visibility, the task permission, duplicate-link behaviour and the
		// comment-belongs-to-thread check, and it answers with a machine code. Re-deciding any of it
		// here would be a second rule that drifts from the first, so this layer does exactly four
		// things: resolve the caller server-side, refuse when link persistence is absent, call the
		// service, and map its answer to an HTTP status.
		//
		// WHY A STATUS MAP AND NOT A FLAT 200. The panel branches on the code already, but a 200 for a
		// refusal makes every proxy, log and dashboard read a denial as a success. The map below is
		// deliberately coarse and never widens what the service disclosed: `link_not_found` and a
		// cross-company link both arrive as the same code, so both answer 404 and the caller cannot
		// tell a foreign link from an absent one.
		// ============================================================================================

		// Codes that mean "the caller may not do this", as opposed to "the request was wrong".
		private static readonly HashSet<string> LinkForbiddenCodes = new(StringComparer.Ordinal)
		{
			CrossBuy.BL.Communication.CommTaskErrors.NotAuthorizedToLinkTask,
			CrossBuy.BL.Communication.CommTaskErrors.NoEmployeeIdentity,
		};

		// Codes that mean "no such thing, or none you may see" — ONE status for both, on purpose.
		private static readonly HashSet<string> LinkNotFoundCodes = new(StringComparer.Ordinal)
		{
			CrossBuy.BL.Communication.CommTaskErrors.LinkNotFound,
			CrossBuy.BL.Communication.CommTaskErrors.TaskNotFound,
			CrossBuy.BL.Communication.CommTaskErrors.ConversationNotFound,
			CrossBuy.BL.Communication.CommTaskErrors.MessageNotFound,
		};

		// ONE SENTENCE PER REFUSAL, and the four NOT-FOUND codes deliberately share theirs.
		//
		// The code stays the machine contract and every status is unchanged; this adds the human half.
		// A reader who picked a task that was closed out of reach between the search and the
		// confirmation was being told "rejected", which names nothing they can act on.
		//
		// WHAT THE WORDING MAY NOT REVEAL. task/conversation/message/link-not-found all resolve to the
		// SAME sentence, because the service already merges "absent" with "you may not see it" for each
		// of them — writing four different sentences would hand back the distinction the service spent
		// effort removing. The codes differ only in WHICH resource failed, which the caller already
		// knows it supplied, so they stay distinct for the client to branch on.
		private string LinkFailureMessage(string c)
		{
			if (c == CrossBuy.BL.Communication.CommTaskErrors.LinkPersistenceNotProvisioned)
				return T("ربط المهام بالمحادثات غير مُفعّل في هذه البيئة.",
				         "Linking tasks to conversations is not enabled in this environment.");

			if (c == CrossBuy.BL.Communication.CommTaskErrors.MessageNotInConversation)
				return T("هذه الرسالة ليست في هذه المحادثة.",
				         "That message is not part of this conversation.");

			if (LinkForbiddenCodes.Contains(c))
				return T("ليست لديك صلاحية لربط هذه المهمة.",
				         "You do not have permission to link this task.");

			if (LinkNotFoundCodes.Contains(c))
				return T("لم يعد هذا العنصر متاحًا. حدّث القائمة وحاول مرة أخرى.",
				         "That item is no longer available. Refresh the list and try again.");

			if (c == CrossBuy.BL.Communication.CommTaskErrors.NoCompanyScope)
				return T("انتهت الجلسة. سجّل الدخول مرة أخرى.", "Your session has ended. Sign in again.");

			return T("تعذّر تنفيذ الطلب.", "The request could not be completed.");
		}

		private IActionResult LinkFailure(string? code)
		{
			var c = code ?? CrossBuy.BL.Communication.CommTaskErrors.LinkRejected;

			// `error` is ADDITIVE. The client branches on `code` and on the status — never on this
			// sentence, which changes with the reader's language.
			var payload = new { ok = false, code = c, error = LinkFailureMessage(c) };

			if (c == CrossBuy.BL.Communication.CommTaskErrors.LinkPersistenceNotProvisioned)
				return StatusCode(StatusCodes.Status503ServiceUnavailable, payload);
			if (LinkForbiddenCodes.Contains(c))
				return StatusCode(StatusCodes.Status403Forbidden, payload);
			if (LinkNotFoundCodes.Contains(c))
				return StatusCode(StatusCodes.Status404NotFound, payload);
			if (c == CrossBuy.BL.Communication.CommTaskErrors.NoCompanyScope)
				return StatusCode(StatusCodes.Status401Unauthorized, payload);

			// MessageNotInConversation and LinkRejected: the request was understood and refused.
			return StatusCode(StatusCodes.Status400BadRequest, payload);
		}

		// The unprovisioned answer, asked BEFORE the service so a deployment with no link table says so
		// once, in one place, with the service's own code rather than a second vocabulary.
		private IActionResult? LinkUnavailable(CrossBuy.BL.Communication.ICommTaskIntegrationService integration)
			=> integration.LinkingAvailable
				? null
				: StatusCode(StatusCodes.Status503ServiceUnavailable, new
				{
					ok = false,
					code = CrossBuy.BL.Communication.CommTaskErrors.LinkPersistenceNotProvisioned,
				});

		// GET: the TASKS this caller may attach to a conversation — ?term=<text or task number>.
		//
		// WHY THIS EXISTS, AND WHAT IT REPLACES. The conversation panel was pointed at LinkSearch, which
		// is the record picker for the OTHER direction: "which business record is this task about". It
		// searches SalesInvoices, Customers, Items — and returns THEIR ids. The panel then posted one of
		// those as a taskId, which is how a live link attempt arrived as
		// { threadId: 10002, taskId: 12545 } and was refused with task_not_found. 12545 is a SalesInvoice.
		//
		// THE REFUSAL WAS CORRECT AND THE WIRING WAS NOT. Measured in CrossBuyDB2: no TaskItem has id
		// 12545, one SalesInvoice does. Today no invoice id in company 1 collides with a task id in
		// company 1 (0 of 441), so every mis-pick has failed closed — but task ids run 149..28953 and
		// invoice ids fall inside that range, so a collision would have LINKED THE WRONG TASK silently.
		// That is the reason this endpoint exists rather than a note telling the panel to filter better.
		//
		// SEARCH AND LINK SHARE ONE AUTHORITY. Candidates come from ITaskReadAuthorizer — the same
		// authoritative read path the Work reporting pack uses — under the same TasksActions.Edit gate
		// LinkConversation applies. A task offered here is therefore a task the link endpoint will
		// accept, and a task it would refuse is never offered.
		[SessionValidation][HttpGet]
		public async Task<IActionResult> LinkCandidates(string? term, CancellationToken ct = default)
		{
			var scope = await _company.ResolveAsync();
			var ctx = scope.Ok ? await _businessContexts.TryGetCurrentAsync(ct) : null;
			if (ctx == null) return Json(new { results = Array.Empty<object>() });

			// THE SAME GATE THE LINK ENDPOINT APPLIES. A picker that offered what the writer refuses is
			// a UI that teaches people the system is broken.
			if (!await _tasksAccess.CanAsync(ctx, TasksActions.Edit,
				new PermissionTarget { CompanyId = scope.CompanyId }))
				return Json(new { results = Array.Empty<object>() });

			// Resolved from the container, the same lazy pattern ActorAvatars uses rather than adding a
			// DbContext to a constructor that deliberately takes services.
			var db = _services.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>();
			var authorizer = _services.GetRequiredService<CrossBuy.BL.ITaskReadAuthorizer>();

			var authorization = await authorizer.ForListAsync(ct);
			if (authorization is null) return Json(new { results = Array.Empty<object>() });

			var rows = await authorizer.ApplyAsync(db.TaskItems.AsNoTracking(), authorization, ct);

			// TWO WAYS TO FIND A TASK, because people arrive with either. A number is matched as an ID
			// AND as text, so "1254" still finds "Reconcile 1254" — an exact-id-only match would hide it.
			term = term?.Trim();
			if (!string.IsNullOrWhiteSpace(term))
			{
				var byId = int.TryParse(term, out var id) ? id : 0;
				rows = rows.Where(t =>
						(byId > 0 && t.ID == byId)
						|| (t.Title != null && t.Title.Contains(term))
						|| (t.TitleEn != null && t.TitleEn.Contains(term)));
			}

			// Open work first: a closed task is rarely what somebody means to attach to a live thread,
			// and the cap keeps a picker a picker.
			var page = await rows
				.OrderBy(t => t.Status == CrossBuy.Models.Context.Tasks.TaskStatuses.Done ? 1 : 0)
				.ThenByDescending(t => t.CreatedAt)
				.Take(25)
				.Select(t => new
				{
					t.ID, t.Title, t.TitleEn, t.Description, t.Status, t.Priority,
					t.AssigneeEmployeeId, t.DueDate, t.EntityType, t.EntityId,
				})
				.ToListAsync(ct);

			// Names AND PHOTOS through COMPANY-SCOPED employees, set-based — one round trip, never per
			// row, and never a lookup that could reach an employee of another company.
			//
			// THE PHOTO TRAVELS WITH THE NAME, out of the SAME query. A row that names a person shows
			// that person, and the chain for this usually breaks here: the name is selected, the
			// avatar is not, and the panel draws initials forever with nothing to report. Employee
			// .ProfileImage is the only source, and it is already a url wherever it is read.
			var empIds = page.Select(p => p.AssigneeEmployeeId).Where(i => i > 0).Distinct().ToList();
			var people = empIds.Count == 0
				? new Dictionary<int, (string? Name, string? Avatar)>()
				: (await db.Employee.AsNoTracking()
						.Where(e => e.EmpCompanyID == scope.CompanyId && empIds.Contains(e.ID))
						.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage })
						.ToListAsync(ct))
					.ToDictionary(
						e => e.ID,
						e => (Name: CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn),
							  Avatar: string.IsNullOrWhiteSpace(e.ProfileImage) ? null : e.ProfileImage));

			return Json(new
			{
				results = page.Select(t =>
				{
					people.TryGetValue(t.AssigneeEmployeeId, out var who);
					var title = CrossBuy.BL.DisplayName.Of(t.Title, t.TitleEn);
					return new
					{
						id = t.ID,

						// `text` is kept so an older picker that reads it still renders a NAME rather than
						// a number — the owner's point exactly: a task number explains nothing.
						text = title,
						title,
						summary = Summarise(t.Description),
						status = t.Status,
						priority = t.Priority,
						assigneeName = who.Name,

						// The picker already draws an <img> when this is present and falls back to the
						// initials tile when it is not, so an employee with no photo costs nothing.
						assigneeAvatar = who.Avatar,
						assigneeEmployeeId = t.AssigneeEmployeeId > 0 ? t.AssigneeEmployeeId : (int?)null,
						dueDate = t.DueDate,

						// The record the task is already about, as codes only. No cross-module read here:
						// resolving a label per row would be an N+1 and could reach another company's row.
						entityType = t.EntityType,
						entityId = t.EntityId,
					};
				}),
			});
		}

		// A one-line summary, never the whole description: a picker row is a line, and a long body would
		// push the title out of view — which is the problem this endpoint was asked to fix.
		private static string? Summarise(string? description)
		{
			if (string.IsNullOrWhiteSpace(description)) return null;
			var flat = System.Text.RegularExpressions.Regex.Replace(description, @"\s+", " ").Trim();
			return flat.Length <= 120 ? flat : flat[..119] + "\u2026";
		}

		// GET: every MESSAGE this task is attached to — ?taskId=<n>.
		//
		// WHY IT IS COMPOSED HERE RATHER THAN ASKED FOR. The conversation side has had its answer since
		// the panel was built (LinkedTasksAsync). The task side has none: ICommTaskIntegrationService
		// offers ConversationForTaskAsync, which returns ONE CommTaskConversation, and one conversation
		// cannot express a task attached to three messages across two threads. Adding the method where
		// it belongs means editing CommTaskIntegration.cs, which is TAB-3's file — so this composes the
		// answer from two calls that are already authorized rather than reaching past an owner.
		//
		// TWO GATES, AND THEY GUARD DIFFERENT THINGS. Tasks/Read decides whether this caller may know
		// the task's relationships at all. The Communication services decide, per row, whether the
		// caller may see the conversation and read the message — and they are asked SEPARATELY for
		// every link, because the right to a task has never implied the right to the conversations
		// other people attached it to.
		//
		// A ROW SURVIVES A REFUSED MESSAGE. The relationship itself is the task's own data: dropping the
		// row would hide a link the owner may legitimately unlink. What is withheld is the CONTENT —
		// preview, author and thread label come back null and `readable` says so, which is the
		// difference between "there is a message here you may not read" and "there is nothing here".
		[SessionValidation][HttpGet]
		public async Task<IActionResult> MessagesForTask(int taskId, CancellationToken ct = default)
		{
			var scope = await _company.ResolveAsync();
			var ctx = scope.Ok ? await _businessContexts.TryGetCurrentAsync(ct) : null;
			if (ctx == null) return Unauthorized();

			// PermissionTarget.ForTask, not the bare company: whether this caller may read THIS task is
			// the question, and the company-level answer is a different, wider one.
			if (!await _tasksAccess.CanAsync(ctx, TasksActions.Read,
				PermissionTarget.ForTask(taskId, scope.CompanyId))) return Forbid();

			var integration = TryTaskIntegration();
			var comm = TryCommunication();
			if (integration == null || comm == null) return CommunicationUnavailable();

			var store = _services.GetService<CrossBuy.BL.Communication.ICommTaskLinkStore>();
			if (store == null || !store.IsProvisioned)
				return Json(new
				{
					ok = true,
					linkingAvailable = false,
					messages = Array.Empty<object>(),
				});

			if (taskId <= 0) return Json(new { ok = true, linkingAvailable = true, messages = Array.Empty<object>() });

			// The task must exist IN THIS COMPANY before its links are followed. The store filters on
			// company too, but a link row is only a pointer and following one without re-checking the
			// task's own tenancy is how a stray row reaches across companies.
			var db = _services.GetRequiredService<CrossBuy.Models.Context.CrossDbContext>();
			var taskExists = await db.TaskItems.AsNoTracking()
				.AnyAsync(t => t.ID == taskId && t.CompanyId == scope.CompanyId, ct);
			if (!taskExists) return Json(new { ok = true, linkingAvailable = true, messages = Array.Empty<object>() });

			var links = await store.ForTaskAsync(scope.CompanyId, taskId, ct);

			// Threads are read once each, not once per link: a task attached to four messages in one
			// conversation would otherwise authorize the same thread four times.
			var threadCache = new Dictionary<long, CrossBuy.Models.Context.Communication.CommThread?>();
			var results = new List<object>();
			var registry = _services.GetService<CrossBuy.BL.Platform.IEntityRegistry>();
			var isArabic = System.Globalization.CultureInfo.CurrentUICulture
				.TwoLetterISOLanguageName == "ar";

			// The TYPE's definition, for the row's label, icon and colour. Asked through the registry
			// so a module that renames or recolours its entity reaches this screen without an edit here.
			CrossBuy.Models.Platform.EntityDefinition? defOf(CrossBuy.Models.Context.Communication.CommThread? t)
				=> t != null && registry != null && !string.IsNullOrWhiteSpace(t.EntityType)
				   && registry.TryGetDefinition(t.EntityType, out var d) ? d : null;

			string? routeFor(CrossBuy.Models.Context.Communication.CommThread? t, long? comment)
			{
				if (t == null || registry == null || string.IsNullOrWhiteSpace(t.EntityType)) return null;
				if (!registry.IsValid(t.EntityType)) return null;
				// A LIST-ONLY TEMPLATE IS NOT A DESTINATION. BuildUrl returns a template WITHOUT
				// "{id}" unchanged, by its own documented contract, so an entity registered against
				// a module index comes back as a perfectly valid-looking url that cannot show the
				// message. Ask the definition whether it has a record route at all - the previous
				// test, "does the built url contain the id", is a heuristic that happens to hold
				// only because no list-only template in the registry contains a digit today.
				var def = defOf(t);
				if (def?.RouteTemplate == null || !def.RouteTemplate.Contains("{id}", StringComparison.Ordinal))
					return null;

				var url = registry.BuildUrl(t.EntityType, t.EntityId);

				// AND THE SUBSTITUTION ACTUALLY HAPPENED. Kept as the second half of the guard: a
				// template that declares {id} but comes back without it is a registry bug, and the
				// honest answer to that is still no link.
				if (string.IsNullOrEmpty(url) || !url.Contains(t.EntityId.ToString(), StringComparison.Ordinal))
					return null;
				return comment is > 0 ? url + "#cbComment" + comment.Value : url;
			}

			foreach (var link in links)
			{
				if (!threadCache.TryGetValue(link.ThreadId, out var thread))
				{
					try { thread = await comm.Value.Threads.RequireAsync(ctx, link.ThreadId, ct); }
					catch (CrossBuy.Models.Communication.CommNotFoundException) { thread = null; }
					catch (CrossBuy.Models.Communication.CommAccessDeniedException) { thread = null; }
					threadCache[link.ThreadId] = thread;
				}

				string? preview = null, authorName = null, authorAvatar = null;
				DateTime? postedAt = null;
				var readable = false;

				if (thread != null && link.CommentId is > 0)
				{
					try
					{
						var message = await comm.Value.Comments.GetAsync(ctx, link.CommentId.Value, ct);

						// A DELETED MESSAGE IS A ROW WITHOUT CONTENT. The service blanks the body for a
						// reader who may not moderate, so echoing it would print an empty quotation as
						// though that were what somebody wrote.
						readable = !message.IsDeleted;
						if (readable)
						{
							preview = Summarise(message.Body);
							authorName = message.Author?.Display(
								System.Globalization.CultureInfo.CurrentUICulture
									.TwoLetterISOLanguageName == "ar");

							// THE PERSON, NOT A GLYPH. CommActorDto already carries the photo; the row
							// draws it and falls back to initials, so a message never names somebody
							// the reader cannot recognise.
							authorAvatar = message.Author?.AvatarUrl;
							postedAt = message.CreatedAt;
						}
					}
					catch (CrossBuy.Models.Communication.CommNotFoundException) { }
					catch (CrossBuy.Models.Communication.CommAccessDeniedException) { }
				}

				results.Add(new
				{
					linkId = link.Id,
					taskId = link.TaskId,
					threadId = link.ThreadId,
					commentId = link.CommentId,

					// Each relationship classified on its own. Two links to two messages in ONE thread
					// are two rows here with two linkIds, and neither is merged into the other.
					scope = link.CommentId is > 0 ? "message" : "conversation",
					origin = link.Origin,
					createdAt = link.CreatedAt,

					// The business record the conversation hangs on, where the caller may see it. Null
					// for a thread they cannot open — which is also why the row still appears.
					entityType = thread?.EntityType,
					entityId = thread?.EntityId,

					// WHETHER THE CONVERSATION OPENED, asked separately from whether the message did.
					// A conversation-level link has no message, so `readable` is false for one by
					// construction; without this the screen cannot tell "there is no single message"
					// from "that message is not yours to read" and was saying the second for both.
					threadReadable = thread != null,
					readable,
					preview,
					authorName,
					authorAvatar,
					postedAt,

					// THE RECORD'S TYPE, as the registry words it — not the record's own label. Reading
					// an invoice's number here would be a cross-module read per row, and the row is
					// already drawn for somebody who may not be allowed that record's contents. Type
					// plus id is what the task screen's existing anchor row shows, and it is honest.
					entityLabel = defOf(thread) is { } ed
						? (isArabic ? ed.DisplayNameAr : ed.DisplayNameEn)
						: null,
					entityIcon = defOf(thread)?.Icon,
					entityColor = defOf(thread)?.Color,

					// A STABLE TARGET, FROM THE REGISTRY THAT OWNS IT. There is no conversation screen to
					// link to: the panel is a partial embedded in the record's own page, so the
					// destination is that record plus a fragment for the message. The path comes from
					// IEntityRegistry.BuildUrl rather than a string built here, because the mapping from
					// an entity code to its screen is that registry's to change and has already moved
					// once. Null when the caller may not open the thread, or when the entity has no
					// detail screen - a list-only template would send the reader somewhere that cannot
					// show the message.
					route = routeFor(thread, link.CommentId),
				});
			}

			return Json(new { ok = true, linkingAvailable = true, messages = results });
		}

		// POST: attach an existing task to a conversation.
		//
		// threadId and taskId ARE record identifiers from the browser and are NOT authority. The service
		// re-reads both under the caller's own company and refuses anything outside it; what makes that
		// safe is that the company comes from the resolved BusinessContext below and never from the form.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> LinkConversation(long threadId, int taskId, long? commentId,
			CancellationToken ct = default)
		{
			var scope = await _company.ResolveAsync();
			var ctx = scope.Ok ? await _businessContexts.TryGetCurrentAsync(ct) : null;
			if (ctx == null)
				return StatusCode(StatusCodes.Status401Unauthorized, new
				{ ok = false, code = CrossBuy.BL.Communication.CommTaskErrors.NoCompanyScope });

			// The TASK-side gate, before availability, so an unauthorized caller is refused without
			// learning whether this deployment has link persistence at all.
			if (!await _tasksAccess.CanAsync(ctx, TasksActions.Edit,
				new PermissionTarget { CompanyId = scope.CompanyId })) return Forbid();

			var integration = TryTaskIntegration();
			if (integration == null) return CommunicationUnavailable();

			var unavailable = LinkUnavailable(integration);
			if (unavailable != null) return unavailable;

			var result = await integration.LinkExistingAsync(ctx, new CrossBuy.BL.Communication.CommTaskLinkRequest
			{
				ThreadId = threadId,
				TaskId = taskId,
				CommentId = commentId,
			}, ct);

			// NEVER A SUCCESS WITHOUT A LINK ID. A result that says Ok but carries no LinkId would mean
			// nothing was persisted, and reporting that as success is the one failure this endpoint must
			// not have: the panel would show an attachment that does not exist.
			if (!result.Ok) return LinkFailure(result.Error);
			if (result.LinkId is not > 0)
				return StatusCode(StatusCodes.Status503ServiceUnavailable, new
				{ ok = false, code = CrossBuy.BL.Communication.CommTaskErrors.LinkPersistenceNotProvisioned });

			// A MESSAGE-LEVEL REQUEST THAT CAME BACK AS SOMETHING ELSE IS NOT A SUCCESS.
			//
			// The service sets LinkWarning when the row it returned carries a different provenance from
			// the one asked for — the caller wanted this task attached to THIS message and got an
			// existing link to the conversation, or to another message, instead. Answering 200/ok for
			// that would put a confirmation on screen for a relationship nobody created.
			//
			// This is a 409: the request was well-formed and authorized, and it lost to something
			// already in the table. The existing link's id travels with it, because the only useful next
			// act — look at it, or remove it — needs the id.
			//
			// It should now be UNREACHABLE. AddAsync matches on CommentId exactly, NULL included, so a
			// message-level request can only ever match a row for that same message. The guard stays
			// because the alternative is trusting that invariant silently: this is the one place the
			// downgrade would surface, and an unreachable branch costs nothing while a missing one costs
			// a wrong confirmation.
			if (!string.IsNullOrEmpty(result.LinkWarning))
				return StatusCode(StatusCodes.Status409Conflict, new
				{
					ok = false,
					code = result.LinkWarning,
					linkId = result.LinkId.Value,
					requestedCommentId = commentId,
				});

			return Json(new
			{
				ok = true,
				linkId = result.LinkId.Value,

				// Echoed so the caller can place the confirmation against the message it asked about
				// without holding its own copy of the request.
				commentId,
				scope = commentId is > 0 ? "message" : "conversation",
			});
		}

		// POST: detach a task from a conversation.
		//
		// linkId alone, because the link row already names its company, its thread and its task — and
		// the service re-reads it under the caller's context. A linkId belonging to another company is
		// answered exactly as an absent one.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> UnlinkConversation(long linkId, CancellationToken ct = default)
		{
			var scope = await _company.ResolveAsync();
			var ctx = scope.Ok ? await _businessContexts.TryGetCurrentAsync(ct) : null;
			if (ctx == null)
				return StatusCode(StatusCodes.Status401Unauthorized, new
				{ ok = false, code = CrossBuy.BL.Communication.CommTaskErrors.NoCompanyScope });

			if (!await _tasksAccess.CanAsync(ctx, TasksActions.Edit,
				new PermissionTarget { CompanyId = scope.CompanyId })) return Forbid();

			var integration = TryTaskIntegration();
			if (integration == null) return CommunicationUnavailable();

			var unavailable = LinkUnavailable(integration);
			if (unavailable != null) return unavailable;

			var result = await integration.UnlinkAsync(ctx, linkId, ct);
			return result.Ok ? Json(new { ok = true }) : LinkFailure(result.Error);
		}
	}
}
