using System.Globalization;
using System.Text.Json;
using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Menu;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// TM-1: Task management (operational only — NO GL/stock). Admin-app module, uses the shared backend shell + its own
	// sidebar menu (MainMenu.Tasks()). Linking to records (TM-2), timesheet (TM-3) and money (TM-4+) come later.
	public class TasksController : Controller
	{
		private const int DefaultCompanyId = 1;
		private readonly ITaskService _tasks;
		private readonly ITaskLinkResolver _links;
		private readonly ITimesheetService _ts;
		private readonly ITaskCostService _cost;
		private readonly ITaskBillingService _billing;
		private readonly ITaskGeneratorService _gen;
		private readonly ITaskReportService _reports;
		private readonly ITaskScheduleMatcher _matcher;
		public TasksController(ITaskService tasks, ITaskLinkResolver links, ITimesheetService ts, ITaskCostService cost, ITaskBillingService billing, ITaskGeneratorService gen, ITaskReportService reports, ITaskScheduleMatcher matcher) { _tasks = tasks; _links = links; _ts = ts; _cost = cost; _billing = billing; _gen = gen; _reports = reports; _matcher = matcher; }

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
			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Scope = scope;
			ViewBag.Status = status; ViewBag.Priority = priority; ViewBag.Q = q; ViewBag.View = view ?? "all"; ViewBag.Assignee = assignee; ViewBag.Sort = sort;
			if (page < 1) page = 1; if (pageSize < 1 || pageSize > 200) pageSize = 25;
			int me = CurrentEmployeeId();
			ViewBag.Me = me;
			ViewBag.Employees = await _tasks.ActiveEmployeesAsync();
			ViewBag.LinkTypes = _links.Types();
			ViewBag.Kpis = await _tasks.GetKpisAsync(DefaultCompanyId, scope, me);
			int total = await _tasks.CountTasksAsync(DefaultCompanyId, scope, me, status, priority, q, view, assignee);
			int pages = (int)Math.Ceiling(total / (double)pageSize); if (pages < 1) pages = 1; if (page > pages) page = pages;
			ViewBag.Total = total; ViewBag.Page = page; ViewBag.PageSize = pageSize; ViewBag.Pages = pages;
			var rows = await _tasks.GetTasksAsync(DefaultCompanyId, scope, me, status, priority, q, page, pageSize, view, assignee, sort);
			ViewBag.Tasks = rows;
			// TM-2: resolve each linked row to {label, url} for the "open record" link
			var linkMap = new Dictionary<int, CrossBuy.BL.TaskLinkDto>();
			foreach (var r in rows.Where(x => !string.IsNullOrEmpty(x.EntityType) && x.EntityId > 0))
			{
				var resolved = await _links.ResolveAsync(DefaultCompanyId, r.EntityType, r.EntityId);
				if (resolved != null) linkMap[r.Id] = resolved;
			}
			ViewBag.Links = linkMap;
			ViewBag.Running = await _ts.GetRunningAsync(DefaultCompanyId, me);   // TM-3: the employee's live timer (if any)
			return View("Index");
		}

		[HttpGet]
		public Task<IActionResult> Index(string? status, string? priority, string? q, int page = 1, int pageSize = 25, string? view = null, int? assignee = null, string? sort = null) => RenderList("mine", status, priority, q, page, pageSize, view, assignee, sort);

		[HttpGet]
		public Task<IActionResult> All(string? status, string? priority, string? q, int page = 1, int pageSize = 25, string? view = null, int? assignee = null, string? sort = null) => RenderList("all", status, priority, q, page, pageSize, view, assignee, sort);

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> Save(TaskSaveInput input, string scope = "mine")
		{
			var (ok, err, _) = await _tasks.SaveAsync(DefaultCompanyId, input, CurrentEmployeeId());
			if (ok) this.ToastSuccess(input.Id > 0 ? T("تم حفظ التعديلات", "Changes saved") : T("تم إنشاء المهمة", "Task created"));
			else this.ToastError(err ?? T("تعذّر الحفظ", "Could not save"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		// TM-2: record picker search (AJAX, read-only). Returns [{id,text}] for select2.
		// TM-8: unified read-only reports hub. `r` selects the report; all aggregate existing data, write nothing.
		[HttpGet]
		public async Task<IActionResult> Reports(string r = "summary", DateTime? from = null, DateTime? to = null)
		{
			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.R = r;
			var f = from ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t;
			switch (r)
			{
				case "overdue": ViewBag.Data = await _reports.OverdueAsync(DefaultCompanyId); break;
				case "productivity": ViewBag.Data = await _reports.ProductivityAsync(DefaultCompanyId, f, t); break;
				case "cost-project": ViewBag.Data = await _reports.CostByAsync(DefaultCompanyId, "Project"); break;
				case "cost-wo": ViewBag.Data = await _reports.CostByAsync(DefaultCompanyId, "ManufWorkOrder"); break;
				case "billable": ViewBag.Data = await _reports.BillableByCustomerAsync(DefaultCompanyId); break;
				case "auto": ViewBag.Data = await _reports.AutoGeneratedAsync(DefaultCompanyId); break;
				default:
					r = "summary"; ViewBag.R = r;
					var (mine, team) = await _reports.SummaryAsync(DefaultCompanyId, CurrentEmployeeId());
					ViewBag.Mine = mine; ViewBag.Team = team; break;
			}
			return View();
		}

		// TM-6: read-only hours report (per employee, date range) for HR to feed the existing payroll. Writes nothing.
		[HttpGet]
		public async Task<IActionResult> HoursReport(DateTime? from, DateTime? to, int? employeeId)
		{
			ViewBag.SidebarMenu = MainMenu.Tasks();
			var f = from ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t; ViewBag.EmployeeId = employeeId;
			ViewBag.Employees = await _tasks.ActiveEmployeesAsync();
			var report = await _ts.GetHoursReportAsync(DefaultCompanyId, f, t, employeeId);
			ViewBag.Report = report;
			// employee profile photos + culture-aware display names for the report cards
			var imgs = new Dictionary<int, string>();
			var names = new Dictionary<int, string>();
			if (report.Employees.Count > 0
				&& HttpContext.RequestServices.GetService(typeof(CrossBuy.Models.Context.CrossDbContext)) is CrossBuy.Models.Context.CrossDbContext db)
			{
				var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
				var ids = report.Employees.Select(e => e.EmployeeId).ToList();
				var rows = await db.Employee.AsNoTracking()
					.Where(e => ids.Contains(e.ID))
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
			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Employees = await _tasks.ActiveEmployeesAsync();
			ViewBag.Rules = await _gen.GetRulesAsync(DefaultCompanyId);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveAutoRule(string ruleType, bool isActive, int? defaultAssigneeEmployeeId)
		{
			var (ok, err) = await _gen.SetRuleAsync(DefaultCompanyId, ruleType, isActive, defaultAssigneeEmployeeId);
			if (ok) this.ToastSuccess(T("تم حفظ القاعدة", "Rule saved")); else this.ToastError(err ?? T("تعذّر الحفظ", "Could not save"));
			return RedirectToAction(nameof(AutoRules));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> RunGenerator()
		{
			var summary = await _gen.RunAsync(DefaultCompanyId);
			int total = summary.Values.Sum();
			this.ToastSuccess(total > 0 ? T($"تم توليد {total} مهمة", $"{total} task(s) generated") : T("لا مهام جديدة للتوليد", "No new tasks to generate"));
			return RedirectToAction(nameof(AutoRules));
		}

		[HttpGet]
		public async Task<IActionResult> LinkSearch(string entityType, string? term)
		{
			if (string.IsNullOrWhiteSpace(entityType)) return Json(new { results = Array.Empty<object>() });
			var opts = await _links.SearchAsync(DefaultCompanyId, entityType, term);
			return Json(new { results = opts.Select(o => new { id = o.Id, text = o.Label }) });
		}

		// TM-9-ج: manager review of pending multiple-match suggestions for scheduled tasks
		[HttpGet]
		public async Task<IActionResult> MatchSuggestions()
		{
			ViewBag.SidebarMenu = MainMenu.Tasks();
			ViewBag.Groups = await _matcher.GetPendingAsync(DefaultCompanyId);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ConfirmMatch(int taskId, int entityId)
		{
			var (ok, err) = await _matcher.ConfirmAsync(DefaultCompanyId, taskId, entityId);
			if (ok) this.ToastSuccess(T("تم ربط المهمة بالحركة", "Task linked to the movement")); else this.ToastError(err ?? T("تعذّر الربط", "Could not link"));
			return RedirectToAction(nameof(MatchSuggestions));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DismissMatch(int suggestionId)
		{
			var (ok, err) = await _matcher.DismissAsync(DefaultCompanyId, suggestionId);
			if (ok) this.ToastSuccess(T("تم رفض الاقتراح", "Suggestion dismissed")); else this.ToastError(err ?? T("تعذّر الرفض", "Could not dismiss"));
			return RedirectToAction(nameof(MatchSuggestions));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> ChangeStatus(int id, string status, string scope = "mine")
		{
			var (ok, err) = await _tasks.ChangeStatusAsync(DefaultCompanyId, id, status, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("تم تحديث الحالة", "Status updated")); else this.ToastError(err ?? T("تعذّر التحديث", "Could not update"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> Delete(int id, string scope = "mine")
		{
			var (ok, err) = await _tasks.DeleteAsync(DefaultCompanyId, id);
			if (ok) this.ToastSuccess(T("تم حذف المهمة", "Task deleted")); else this.ToastError(err ?? T("تعذّر الحذف", "Could not delete"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		// ===== TM-3: Timesheet =====
		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> StartTimer(int taskId, string scope = "mine")
		{
			var (ok, err) = await _ts.StartAsync(DefaultCompanyId, taskId, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("بدأ المؤقّت", "Timer started")); else this.ToastError(err ?? T("تعذّر بدء المؤقّت", "Could not start timer"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> StopTimer(string scope = "mine")
		{
			var (ok, err) = await _ts.StopAsync(DefaultCompanyId, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T("أُوقف المؤقّت وسُجّل الوقت", "Timer stopped and time logged")); else this.ToastError(err ?? T("لا يوجد مؤقّت شغّال", "No running timer"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AddTime(int taskId, DateTime workDate, decimal hours, string? description, string scope = "mine")
		{
			var (ok, err) = await _ts.AddManualAsync(DefaultCompanyId, taskId, CurrentEmployeeId(), workDate, hours, description);
			if (ok) this.ToastSuccess(T("تم تسجيل الوقت", "Time logged")); else this.ToastError(err ?? T("تعذّر التسجيل", "Could not log time"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteTime(int id, string scope = "mine")
		{
			var (ok, err) = await _ts.DeleteEntryAsync(DefaultCompanyId, id);
			if (ok) this.ToastSuccess(T("تم حذف السطر", "Entry deleted")); else this.ToastError(err ?? T("تعذّر الحذف", "Could not delete"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		[HttpGet]
		public async Task<IActionResult> TimesheetLines(int taskId)
		{
			var lines = await _ts.GetEntriesAsync(DefaultCompanyId, taskId);
			var cost = await _cost.GetTaskCostAsync(DefaultCompanyId, taskId);   // TM-4: informational cost (no GL)
			var bill = await _billing.GetBillingAsync(DefaultCompanyId, taskId); // TM-5: billing status
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
			var (ok, err) = await _cost.PostToWorkOrderAsync(DefaultCompanyId, taskId, null);
			if (ok) this.ToastSuccess(T("تم ترحيل العمالة إلى أمر التشغيل", "Labor posted to the work order")); else this.ToastError(err ?? T("تعذّر الترحيل", "Could not post"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}

		// TM-5: generate an hourly service invoice for this task's uninvoiced billable hours (via ReceivableService).
		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> GenerateInvoice(int taskId, string scope = "mine")
		{
			var (ok, err, invId) = await _billing.GenerateInvoiceAsync(DefaultCompanyId, taskId, CurrentEmployeeId());
			if (ok) this.ToastSuccess(T($"تم إنشاء فاتورة الخدمة #{invId}", $"Service invoice #{invId} created")); else this.ToastError(err ?? T("تعذّر إنشاء الفاتورة", "Could not create invoice"));
			return RedirectToAction(scope == "all" ? nameof(All) : nameof(Index));
		}
	}
}
