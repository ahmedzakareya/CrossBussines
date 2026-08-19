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
			IServiceProvider services,
			IFileManagerService files, IWebHostEnvironment env)
		{ _services = services; _files = files; _env = env; _tasksAccess = tasksAccess; _businessContexts = businessContexts; _company = company; _accounting = accounting; _tasks = tasks; _links = links; _ts = ts; _cost = cost; _billing = billing; _gen = gen; _reports = reports; _matcher = matcher; _checklist = checklist; _deps = deps; _templates = templates; }
		// D1 Wave 1 — resolves the company (CORRECTION-005) and ASKS the approved access services. No predicate
		// lives here: TasksAccessService owns the task record rule and the linked-entity check; AccountingAccessService
		// owns the posting right. TaskItem.CompanyId is verified inside TasksAccessService against the ROW.
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

			var (ok, err) = await _gen.SetRuleAsync(co, ruleType, isActive, defaultAssigneeEmployeeId);
			if (ok) this.ToastSuccess(T("تم حفظ القاعدة", "Rule saved")); else this.ToastError(err ?? T("تعذّر الحفظ", "Could not save"));
			return RedirectToAction(nameof(AutoRules));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> RunGenerator()
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			var summary = await _gen.RunAsync(co);
			int total = summary.Values.Sum();
			this.ToastSuccess(total > 0 ? T($"تم توليد {total} مهمة", $"{total} task(s) generated") : T("لا مهام جديدة للتوليد", "No new tasks to generate"));
			return RedirectToAction(nameof(AutoRules));
		}

		[HttpGet]
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
			// RECORDED, NOT CHANGED IN THIS PASS: this action carries no TasksActions.Edit gate, while BoardMove —
			// the identical operation from the board — does. That is an authorization gap, not a company gap, and
			// closing it changes who may act rather than which company they act in. Logged for the next Tasks
			// increment rather than folded into a defect-closure pass.
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			var (ok, err) = await _tasks.ChangeStatusAsync(co, id, status, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("تم تحديث الحالة", "Status updated")); else this.ToastError(err ?? T("تعذّر التحديث", "Could not update"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> Delete(int id, string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

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

			var (ok, err) = await _ts.StartAsync(co, taskId, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("بدأ المؤقّت", "Timer started")); else this.ToastError(err ?? T("تعذّر بدء المؤقّت", "Could not start timer"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> StopTimer(string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			var (ok, err) = await _ts.StopAsync(co, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("أُوقف المؤقّت وسُجّل الوقت", "Timer stopped and time logged")); else this.ToastError(err ?? T("لا يوجد مؤقّت شغّال", "No running timer"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddTime(int taskId, DateTime workDate, decimal hours, string? description, string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			var (ok, err) = await _ts.AddManualAsync(co, taskId, CurrentEmployeeId(), workDate, hours, description);
			if (ok) this.ToastSuccess(T("تم تسجيل الوقت", "Time logged")); else this.ToastError(err ?? T("تعذّر التسجيل", "Could not log time"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteTime(int id, string scope = "mine")
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedView();

			var (ok, err) = await _ts.DeleteEntryAsync(co, id);
			if (ok) this.ToastSuccess(T("تم حذف السطر", "Entry deleted")); else this.ToastError(err ?? T("تعذّر الحذف", "Could not delete"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpGet]
		public async Task<IActionResult> TimesheetLines(int taskId)
		{
			var co = await CompanyIdAsync();
			if (co == 0) return CompanyRefusedJson();

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
			return View("Detail");
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
		public async Task<IActionResult> ChecklistAdd(int taskId, string title)
		{
			var gate = await TaskGateAsync(TasksActions.Edit, taskId);
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") }); }

			var (ok, err, id) = await _checklist.AddAsync(gate.CompanyId, taskId, title, CurrentEmployeeId());
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
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") }); }

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
			if (!gate.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") }); }

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
			if (!g1.Ok || !g2.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") }); }

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
			if (!g1.Ok || !g2.Ok) { return Json(new { ok = false, error = T("ليست لديك صلاحية لتنفيذ هذا الإجراء", "You do not have permission to perform this action") }); }

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
				createdAt = c.CreatedAt,
				editedAt = c.EditedAt,
				attachments = c.Attachments.Select(a => new
				{
					id = a.AttachmentId, fileName = a.FileName, contentType = a.ContentType,
					sizeBytes = a.SizeBytes,
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
	}
}
