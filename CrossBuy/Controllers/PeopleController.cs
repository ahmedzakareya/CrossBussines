using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using CrossBuy.Models.Platform;
using CrossBuy.BL;

namespace CrossBuy.Controllers
{
	/// Employee self-service portal (People). Separate from the HR/Admin back-office.
	/// Reuses the existing Metronic design via _LayoutPeople and the same data layer.
	public class PeopleController : Controller
	{
		private readonly CrossDbContext _context;
		private readonly CrossBuy.BL.INotificationService _notifications;
		private readonly CrossBuy.BL.ILeaveDashboardService _dashboard;
		private readonly CrossBuy.BL.ILeaveWorkflowService _workflow;
		private readonly CrossBuy.BL.IEmployeeRequestService _requests;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;

		private readonly CrossBuy.BL.IHrAccessService _hrAccess;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _businessContexts;

		public PeopleController(CrossDbContext context, CrossBuy.BL.INotificationService notifications,
			CrossBuy.BL.ILeaveDashboardService dashboard, CrossBuy.BL.ILeaveWorkflowService workflow,
			CrossBuy.BL.IEmployeeRequestService requests,
			// Stage 1 Batch C — the HR access service and the session-free context accessor, for the
			// leave-approval proof endpoint.
			CrossBuy.BL.IHrAccessService hrAccess,
			CrossBuy.BL.Platform.IBusinessContextAccessor businessContexts,
			IStringLocalizer<CrossBuy.SharedResources> localizer)
		{
			_hrAccess = hrAccess;
			_businessContexts = businessContexts;
			_context = context;
			_notifications = notifications;
			_dashboard = dashboard;
			_workflow = workflow;
			_requests = requests;
			L = localizer;
		}

		// People landing = the dashboard
		public async Task<IActionResult> Dashboard()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");
			var vm = await _dashboard.BuildAsync(emp.ID);
			ViewBag.NameAr = emp.FullName;
			ViewBag.NameEn = emp.FullNameEn;

			// recent requests (for the table)
			ViewBag.Recent = await _context.LeaveRequests.AsNoTracking()
				.Include(r => r.LeaveType)
				.Where(r => r.EmployeeID == emp.ID)
				.OrderByDescending(r => r.ID).Take(5).ToListAsync();

			// days of the current month that fall within any leave (for the calendar)
			var now = DateTime.UtcNow;
			var monthStart = new DateTime(now.Year, now.Month, 1);
			var monthEnd = monthStart.AddMonths(1).AddDays(-1);
			var myReqs = await _context.LeaveRequests.AsNoTracking()
				.Where(r => r.EmployeeID == emp.ID && r.StartDate <= monthEnd && r.EndDate >= monthStart)
				.ToListAsync();
			var leaveDays = new HashSet<int>();
			foreach (var r in myReqs)
			{
				var s = r.StartDate < monthStart ? monthStart : r.StartDate;
				var e = r.EndDate > monthEnd ? monthEnd : r.EndDate;
				for (var dt = s.Date; dt <= e.Date; dt = dt.AddDays(1)) leaveDays.Add(dt.Day);
			}
			ViewBag.LeaveDays = leaveDays;
			ViewBag.Now = now;
			return View(vm);
		}

		// the manager (employee) directly above the given employee in the org tree
		private async Task<int?> ManagerEmployeeIdAsync(int empId)
		{
			var all = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var byId = all.ToDictionary(h => h.H_ID);
			var node = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == empId);
			if (node?.H_Parent == null || !byId.ContainsKey(node.H_Parent.Value)) return null;
			var pos = byId[node.H_Parent.Value];
			if (pos.H_Parent == null || !byId.ContainsKey(pos.H_Parent.Value)) return null;
			var mgr = byId[pos.H_Parent.Value];
			return mgr.H_Type == 5 ? mgr.H_ObjectID : null;
		}

		// ────────────────────────────────────────────────────────────────────────────────────────
		// THE SELF-SERVICE GATE.
		//
		// `employee-request` is the capability TAB-1 added for exactly this: "an employee may submit and
		// manage their own employee-service requests". It is deliberately unlike every other HR action —
		// HrAccessService answers it `aboutMe` and nothing else, ABOVE the bootstrap branch and above the
		// role rules, so no HR role widens it and a null target is a refusal. That is why this helper
		// returns the subject id rather than a bare bool: the caller then has the ONE employee id it is
		// allowed to act for, and cannot accidentally use another.
		//
		// THE SUBJECT COMES FROM THE RESOLVED BusinessContext, never from the form. A posted, routed or
		// hidden employee id is not identity — it is a parameter, and treating it as identity is how a
		// self-service endpoint becomes an act-as-anyone endpoint.
		//
		// A context with no EmployeeId fails closed here rather than falling back to the company, the
		// first employee, or 1. There is no self to serve, so there is nothing to authorise.
		// ────────────────────────────────────────────────────────────────────────────────────────
		private async Task<int?> SelfServiceSubjectAsync()
		{
			var ctx = await _businessContexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (ctx is not { CompanyId: > 0 } || ctx.EmployeeId is not > 0) return null;

			bool may = await _hrAccess.CanAsync(
				ctx, HrActions.EmployeeRequest,
				PermissionTarget.ForSubjectEmployee(ctx.EmployeeId.Value, ctx.CompanyId),
				HttpContext.RequestAborted);

			return may ? ctx.EmployeeId.Value : null;
		}

		private EmployeeViewModel? CurrentEmployee()
		{
			var json = HttpContext.Session.GetString("Employee");
			return json != null ? JsonSerializer.Deserialize<EmployeeViewModel>(json) : null;
		}

		private static string? NormImg(string? p)
		{
			if (string.IsNullOrEmpty(p)) return null;
			p = p.Replace("\\", "/");
			if (!p.StartsWith("/") && !p.StartsWith("http")) p = "/" + p;
			return p;
		}

		public IActionResult Index() => RedirectToAction(nameof(Dashboard));

		// ---------------- My Profile ----------------
		public async Task<IActionResult> Profile()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");

			var jobTitle = emp.JobTitleID.HasValue
				? await _context.JobTitles.AsNoTracking().FirstOrDefaultAsync(j => j.ID == emp.JobTitleID.Value) : null;
			var company = emp.EmpCompanyID.HasValue
				? await _context.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyID == emp.EmpCompanyID.Value) : null;
			var branch = emp.BranchID.HasValue
				? await _context.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.ID == emp.BranchID.Value) : null;

			var ent = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == emp.ID);
			var dept = (ent != null && ent.DepartmentID.HasValue)
				? await _context.Hierarchicals.AsNoTracking().FirstOrDefaultAsync(h => h.H_ID == ent.DepartmentID.Value) : null;

			var vm = new PeopleProfileVm
			{
				Employee = emp,
				JobTitleAr = jobTitle?.TitleAr,
				JobTitleEn = jobTitle?.Title,
				CompanyAr = company?.ComoanyNameAr,
				CompanyEn = company?.CompanyName,
				BranchAr = branch?.NameAr,
				BranchEn = branch?.Name,
				IsActive = ent?.IsActive ?? true,
				EmploymentType = ent?.EmploymentType,
				DepartmentAr = dept?.H_Name,
				DepartmentEn = dept?.H_NameEn,
			};
			return View(vm);
		}

		// ---------------- Org Structure ----------------
		public async Task<IActionResult> Structure()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");

			var vm = new PeopleStructureVm();
			var all = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var typeNames = await _context.HierarchicalTypes.AsNoTracking()
				.ToDictionaryAsync(t => t.ID, t => new { t.TypeNameAr, t.TypeNameEn });

			var myNode = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == emp.ID);
			if (myNode == null) { vm.Placed = false; return View(vm); }

			var (compImgs, branchImgs, empImgs) = await ImageMapsAsync();
			string? ImageFor(Hierarchical h)
			{
				if (h.H_ObjectID == null) return null;
				var oid = h.H_ObjectID.Value;
				if (h.H_Type == 1 && compImgs.TryGetValue(oid, out var ci)) return NormImg(ci);
				if (h.H_Type == 2 && branchImgs.TryGetValue(oid, out var bi)) return NormImg(bi);
				if (h.H_Type == 5 && empImgs.TryGetValue(oid, out var pi)) return NormImg(pi);
				return null;
			}

			var byId = all.ToDictionary(h => h.H_ID);
			var chain = new List<Hierarchical>();
			var cursor = myNode.H_Parent; var guard = 0;
			while (cursor.HasValue && byId.ContainsKey(cursor.Value) && guard++ < 50)
			{
				chain.Add(byId[cursor.Value]);
				cursor = byId[cursor.Value].H_Parent;
			}
			chain.Reverse();

			var positionNode = myNode.H_Parent.HasValue && byId.ContainsKey(myNode.H_Parent.Value) ? byId[myNode.H_Parent.Value] : null;

			vm.Placed = true;
			vm.MeNameAr = myNode.H_Name; vm.MeNameEn = myNode.H_NameEn;
			vm.PositionAr = positionNode?.H_Name; vm.PositionEn = positionNode?.H_NameEn;
			vm.MeImage = ImageFor(myNode);
			vm.Ancestors = chain.Select(h => new OrgNodeRow
			{
				NameAr = h.H_Name, NameEn = h.H_NameEn, Type = h.H_Type ?? 0,
				TypeNameAr = h.H_Type.HasValue && typeNames.ContainsKey(h.H_Type.Value) ? typeNames[h.H_Type.Value].TypeNameAr : null,
				TypeNameEn = h.H_Type.HasValue && typeNames.ContainsKey(h.H_Type.Value) ? typeNames[h.H_Type.Value].TypeNameEn : null,
				Image = ImageFor(h),
			}).ToList();

			var myChildPositions = all.Where(h => h.H_Parent == myNode.H_ID).Select(h => h.H_ID).ToHashSet();
			vm.Subordinates = all
				.Where(h => h.H_Type == 5 && h.H_Parent.HasValue && myChildPositions.Contains(h.H_Parent.Value))
				.Select(h => new OrgPersonRow
				{
					NameAr = h.H_Name, NameEn = h.H_NameEn,
					PositionAr = byId.ContainsKey(h.H_Parent!.Value) ? byId[h.H_Parent.Value].H_Name : null,
					PositionEn = byId.ContainsKey(h.H_Parent!.Value) ? byId[h.H_Parent.Value].H_NameEn : null,
					Image = ImageFor(h),
				}).ToList();

			return View(vm);
		}

		// ---------------- Leaves ----------------
		public async Task<IActionResult> Leaves()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");

			var vm = new PeopleLeavesVm
			{
				Types = await _context.LeaveTypes.AsNoTracking().ToListAsync(),
				Mine = await _context.LeaveRequests.AsNoTracking().Include(r => r.LeaveType).Include(r => r.ApprovalSteps)
					.Where(r => r.EmployeeID == emp.ID).OrderByDescending(r => r.ID).ToListAsync(),
				WorkDays = await _dashboard.WorkDayFlagsAsync(emp.ID),
			};

			// requests awaiting MY approval at the current level (multi-level chain)
			vm.Pending = await _context.LeaveRequests.AsNoTracking()
				.Include(r => r.LeaveType).Include(r => r.Employee).Include(r => r.ApprovalSteps)
				.Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == emp.ID)
				.OrderByDescending(r => r.ID).ToListAsync();

			// approver display names for the approval-chain timeline
			var approverIds = vm.Mine.Concat(vm.Pending)
				.SelectMany(r => r.ApprovalSteps.Select(s => s.ApproverEmployeeID)).Distinct().ToList();
			if (approverIds.Count > 0)
			{
				vm.ApproverNames = await _context.Employee.AsNoTracking()
					.Where(e => approverIds.Contains(e.ID))
					.ToDictionaryAsync(e => e.ID, e => new ApproverName { Ar = e.FullName, En = e.FullNameEn });
			}

			return View(vm);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateLeave(int leaveTypeId, DateTime startDate, DateTime endDate, string? reason)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");

			// The request is raised FOR the authenticated employee, and the id used below is the one the
			// gate returned — not `emp.ID` from the session view-model, so BusinessContext is the single
			// authority for whose leave this is.
			var self = await SelfServiceSubjectAsync();
			if (self == null) { TempData["LeaveErr"] = L["You are not allowed to perform this action"].Value; return RedirectToAction(nameof(Leaves)); }

			var (ok, error, _) = await _workflow.CreateAsync(self.Value, leaveTypeId, startDate, endDate, reason);
			if (!ok) TempData["LeaveErr"] = error;
			else TempData["LeaveMsg"] = L["Leave request submitted"].Value;
			return RedirectToAction(nameof(Leaves));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DecideLeave(int id, bool approve, string? note)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");

			// ===== Stage 1 Batch C proof endpoint (HR) =====
			// Before this batch, ANY signed-in employee could post a decision on ANY leave request: the only
			// guard was the session. The workflow service checked whether the id was the CURRENT step, not
			// whether this caller was entitled to decide it.
			//
			// `leave-approve` is record-level by nature: it is not a role grant at all. HrAccessService requires
			// the caller to be a manager of the SUBJECT through the company-intersected hierarchy — the same
			// relationship LeaveWorkflowService builds its approver chain from.
			var hrContext = await _businessContexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (hrContext == null) { TempData["LeaveErr"] = L["You are not allowed to perform this action"].Value; return RedirectToAction(nameof(Leaves)); }

			// The SUBJECT comes from the request row, never from the request body — a posted employee id is
			// only ever a lookup key.
			int? subjectEmployeeId = await _context.LeaveRequests.AsNoTracking()
				.Where(r => r.ID == id).Select(r => (int?)r.EmployeeID).FirstOrDefaultAsync(HttpContext.RequestAborted);
			if (subjectEmployeeId == null) { TempData["LeaveErr"] = L["Request not found"].Value; return RedirectToAction(nameof(Leaves)); }

			bool mayDecide = await _hrAccess.CanAsync(
				hrContext, HrActions.LeaveApprove,
				PermissionTarget.ForSubjectEmployee(subjectEmployeeId.Value, hrContext.CompanyId),
				HttpContext.RequestAborted);
			if (!mayDecide)
			{
				TempData["LeaveErr"] = L["You are not allowed to perform this action"].Value;
				return RedirectToAction(nameof(Leaves));
			}

			var (ok, error) = await _workflow.DecideAsync(id, emp.ID, approve, note);
			if (!ok) TempData["LeaveErr"] = error;
			else TempData["LeaveMsg"] = approve ? L["Request approved"].Value : L["Request rejected"].Value;
			return RedirectToAction(nameof(Leaves));
		}

		// ---------------- My Requests: letters + hourly permissions (HR-8) ----------------
		public async Task<IActionResult> Requests()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");

			var vm = new PeopleRequestsVm
			{
				Mine = await _context.EmployeeRequests.AsNoTracking().Include(r => r.ApprovalSteps)
					.Where(r => r.EmployeeID == emp.ID).OrderByDescending(r => r.ID).ToListAsync(),
				Pending = await _context.EmployeeRequests.AsNoTracking().Include(r => r.ApprovalSteps)
					.Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == emp.ID).OrderByDescending(r => r.ID).ToListAsync(),
				HasPayslip = await _context.Payslips.AsNoTracking().AnyAsync(s => s.EmployeeID == emp.ID),
			};
			var ids = vm.Pending.Select(r => r.EmployeeID)
				.Concat(vm.Mine.Concat(vm.Pending).SelectMany(r => r.ApprovalSteps.Select(s => s.ApproverEmployeeID))).Distinct().ToList();
			if (ids.Count > 0)
				vm.Names = await _context.Employee.AsNoTracking().Where(e => ids.Contains(e.ID))
					.ToDictionaryAsync(e => e.ID, e => new ApproverName { Ar = e.FullName, En = e.FullNameEn });
			return View(vm);
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> CreateRequest(string requestType, string? letterType, string? addressee,
			DateTime? permissionDate, string? fromTime, string? toTime, string? reason)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");

			var self = await SelfServiceSubjectAsync();
			if (self == null) { TempData["ReqErr"] = L["You are not allowed to perform this action"].Value; return RedirectToAction(nameof(Requests)); }

			// The company still comes from the employee ROW rather than from the request, and the employee
			// is the gate's subject. Both halves of the record's ownership are therefore server-derived.
			var companyId = await _context.Employee.Where(e => e.ID == self.Value).Select(e => e.EmpCompanyID).FirstOrDefaultAsync();
			var draft = new EmployeeRequest
			{
				CompanyID = companyId, EmployeeID = self.Value, RequestType = requestType == "Permission" ? "Permission" : "Letter",
				LetterType = letterType, Addressee = addressee, PermissionDate = permissionDate, Reason = reason,
				FromTime = TimeSpan.TryParse(fromTime, out var ft) ? ft : (TimeSpan?)null,
				ToTime = TimeSpan.TryParse(toTime, out var tt) ? tt : (TimeSpan?)null,
			};
			var (ok, error, _) = await _requests.CreateAsync(draft);
			TempData[ok ? "ReqMsg" : "ReqErr"] = ok ? L["Request submitted"].Value : error;
			return RedirectToAction(nameof(Requests));
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> DecideRequest(int id, bool approve, string? note)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");

			// THE SAME SHAPE AS DecideLeave DIRECTLY ABOVE, and for the same reason: before this, any
			// signed-in employee could post a decision on any employee request, because the only guard was
			// the session and the service checked whether the step was current, not whether this caller was
			// entitled to decide it.
			//
			// ON THE ACTION NAME — flagged rather than hidden. `leave-approve` is not a role grant: it is a
			// RECORD-LEVEL rule that the caller must be a manager of the SUBJECT through the
			// company-intersected hierarchy, and must not be the subject. That rule is exactly right for
			// approving a subordinate's letter or permission request. Its NAME is leave-flavoured, and the
			// HR vocabulary has no general "approve an employee request" action. Reusing it is the correct
			// authority with an imprecise name; inventing a new one needs HrActions, which is TAB-1's file.
			// Recorded for TAB-1 in the handoff as a vocabulary-naming decision, not a security gap.
			var hrContext = await _businessContexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (hrContext == null) { TempData["ReqErr"] = L["You are not allowed to perform this action"].Value; return RedirectToAction(nameof(Requests)); }

			// SUBJECT FROM THE ROW, never from the body — a posted id is only ever a lookup key.
			int? subjectEmployeeId = await _context.EmployeeRequests.AsNoTracking()
				.Where(r => r.ID == id).Select(r => (int?)r.EmployeeID).FirstOrDefaultAsync(HttpContext.RequestAborted);
			if (subjectEmployeeId == null) { TempData["ReqErr"] = L["Request not found"].Value; return RedirectToAction(nameof(Requests)); }

			bool mayDecide = await _hrAccess.CanAsync(
				hrContext, HrActions.LeaveApprove,
				PermissionTarget.ForSubjectEmployee(subjectEmployeeId.Value, hrContext.CompanyId),
				HttpContext.RequestAborted);
			if (!mayDecide)
			{
				TempData["ReqErr"] = L["You are not allowed to perform this action"].Value;
				return RedirectToAction(nameof(Requests));
			}

			var (ok, error) = await _requests.DecideAsync(id, emp.ID, approve, note);
			TempData[ok ? "ReqMsg" : "ReqErr"] = ok ? (approve ? L["Request approved"].Value : L["Request rejected"].Value) : error;
			return RedirectToAction(nameof(Requests));
		}

		// printable HR letter (approved only) — salary figures pulled from the latest payslip
		public async Task<IActionResult> LetterPrint(int id)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");
			var req = await _context.EmployeeRequests.AsNoTracking().FirstOrDefaultAsync(r => r.ID == id && r.EmployeeID == emp.ID);
			if (req == null || req.RequestType != "Letter" || req.Status != 1) return RedirectToAction(nameof(Requests));
			var ent = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == emp.ID);
			var company = emp.EmpCompanyID.HasValue ? await _context.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyID == emp.EmpCompanyID.Value) : null;
			var jobTitle = emp.JobTitleID.HasValue ? await _context.JobTitles.AsNoTracking().FirstOrDefaultAsync(j => j.ID == emp.JobTitleID.Value) : null;
			ViewBag.Employee = emp; ViewBag.Entity = ent; ViewBag.Company = company; ViewBag.JobTitle = jobTitle;
			ViewBag.Payslip = await _requests.LatestPayslipAsync(emp.ID);
			return View(req);
		}

		// ---------------- My Appraisals (ESS / HR-9) ----------------
		private CrossBuy.BL.IAppraisalService AprSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IAppraisalService)) as CrossBuy.BL.IAppraisalService)!;

		public async Task<IActionResult> MyAppraisals()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");
			var list = await AprSvc.MyAppraisalsAsync(emp.ID);
			var cycleIds = list.Select(a => a.CycleId).Distinct().ToList();
			var mgrIds = list.Select(a => a.ManagerEmployeeID).Distinct().ToList();
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			ViewBag.CycleNames = (await _context.AppraisalCycles.AsNoTracking().Where(c => cycleIds.Contains(c.ID)).Select(c => new { c.ID, c.Name, c.NameEn }).ToListAsync())
				.ToDictionary(c => c.ID, c => !isAr && !string.IsNullOrWhiteSpace(c.NameEn) ? c.NameEn : c.Name);
			ViewBag.Managers = (await _context.Employee.AsNoTracking().Where(e => mgrIds.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.ToDictionary(e => e.ID, e => !isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName);
			return View(list);
		}

		public async Task<IActionResult> MyAppraisal(int id)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");
			var appr = await AprSvc.GetAppraisalAsync(await _context.Employee.Where(e => e.ID == emp.ID).Select(e => e.EmpCompanyID).FirstOrDefaultAsync(), id);
			if (appr == null || appr.EmployeeID != emp.ID || appr.Status < 1) return RedirectToAction(nameof(MyAppraisals));
			var tpl = await AprSvc.GetTemplateAsync(appr.CompanyID, appr.TemplateId);
			ViewBag.Criteria = tpl?.Criteria ?? new List<CrossBuy.Models.Context.Admin.AppraisalCriterion>();
			ViewBag.Manager = await _context.Employee.AsNoTracking().Where(e => e.ID == appr.ManagerEmployeeID).Select(e => e.FullName).FirstOrDefaultAsync();
			return View(appr);
		}

		[HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> AcknowledgeAppraisal(int id, string? comment)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");

			// `employee-request` authorises the EMPLOYEE SIDE of an appraisal — acknowledging one's own.
			// It grants nothing on the administrative side: creating cycles, editing somebody else's
			// appraisal and entering scores all remain under PerformanceManage, which this does not touch.
			var self = await SelfServiceSubjectAsync();
			if (self == null) { TempData["ReqErr"] = L["You are not allowed to perform this action"].Value; return RedirectToAction(nameof(MyAppraisals)); }

			// AND THE RECORD MUST BE THEIRS. The capability says "for myself"; this says "and this is
			// mine". Without the second half, an authorised employee could acknowledge a colleague's
			// appraisal by id — self-service authority on somebody else's record.
			bool mine = await _context.Appraisals.AsNoTracking()
				.AnyAsync(a => a.ID == id && a.EmployeeID == self.Value, HttpContext.RequestAborted);
			if (!mine) { TempData["ReqErr"] = L["You are not allowed to perform this action"].Value; return RedirectToAction(nameof(MyAppraisals)); }

			var (ok, error) = await AprSvc.AcknowledgeAsync(id, self.Value, comment);
			TempData[ok ? "ReqMsg" : "ReqErr"] = ok ? L["Acknowledged"].Value : error;
			return RedirectToAction(nameof(MyAppraisals));
		}

		// ---------------- My Trainings (ESS / HR-10) ----------------
		public async Task<IActionResult> MyTrainings()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");
			var svc = HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.ITrainingService)) as CrossBuy.BL.ITrainingService;
			var list = await svc!.MyTrainingsAsync(emp.ID);
			var courseIds = list.Select(x => x.CourseId).Distinct().ToList();
			ViewBag.Courses = await _context.TrainingCourses.AsNoTracking().Where(c => courseIds.Contains(c.ID))
				.ToDictionaryAsync(c => c.ID, c => new MyTrainingCourseInfo { Title = c.Title, TitleEn = c.TitleEn, Provider = c.Provider, ProviderEn = c.ProviderEn, Hours = c.Hours });
			return View(list);
		}

		// ---------------- My Payslips (ESS / HR-2g) ----------------
		public async Task<IActionResult> Payslips()
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");
			var slips = await _context.Payslips.AsNoTracking()
				.Where(s => s.EmployeeID == emp.ID)
				.OrderByDescending(s => s.Year).ThenByDescending(s => s.Month).ToListAsync();
			return View(slips);
		}

		public async Task<IActionResult> Payslip(int id)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");
			// security: an employee can only open their OWN payslip
			var slip = await _context.Payslips.AsNoTracking().FirstOrDefaultAsync(s => s.ID == id && s.EmployeeID == emp.ID);
			if (slip == null) return RedirectToAction(nameof(Payslips));
			return View(slip);
		}

		// ---------------- My Attendance (ESS / HR-2g) ----------------
		public async Task<IActionResult> Attendance(int? year, int? month)
		{
			var emp = CurrentEmployee();
			if (emp == null) return RedirectToAction("Login", "Account");
			int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
			ViewBag.Year = y; ViewBag.Month = m;
			var companyId = await _context.Employee.Where(e => e.ID == emp.ID).Select(e => e.EmpCompanyID).FirstOrDefaultAsync();
			var att = (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IAttendanceService)) as CrossBuy.BL.IAttendanceService)!;
			var rows = await att.MonthlySummaryAsync(companyId, y, m);
			ViewBag.Summary = rows.FirstOrDefault(r => r.EmployeeID == emp.ID);
			ViewBag.Records = await att.ForMonthAsync(companyId, emp.ID, y, m);
			return View();
		}

		// ---------------- helpers ----------------
		private async Task<List<int>> SubordinateEmployeeIdsAsync(int myEmployeeId)
		{
			var all = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var myNode = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == myEmployeeId);
			if (myNode == null) return new List<int>();
			var myChildPositions = all.Where(h => h.H_Parent == myNode.H_ID).Select(h => h.H_ID).ToHashSet();
			return all
				.Where(h => h.H_Type == 5 && h.H_Parent.HasValue && myChildPositions.Contains(h.H_Parent.Value) && h.H_ObjectID.HasValue)
				.Select(h => h.H_ObjectID!.Value).Distinct().ToList();
		}

		private async Task<(Dictionary<int, string> comp, Dictionary<int, string> branch, Dictionary<int, string> emp)> ImageMapsAsync()
		{
			var comp = await _context.Companies.AsNoTracking().Where(c => c.CompanyImage != null && c.CompanyImage != "")
				.ToDictionaryAsync(c => c.CompanyID, c => c.CompanyImage);
			var branch = await _context.Branches.AsNoTracking().Where(b => b.ImageUrl != null && b.ImageUrl != "")
				.ToDictionaryAsync(b => b.ID, b => b.ImageUrl);
			var emp = await _context.Employee.AsNoTracking().Where(e => e.ProfileImage != null && e.ProfileImage != "")
				.ToDictionaryAsync(e => e.ID, e => e.ProfileImage);
			return (comp, branch, emp);
		}
	}

	// ---- view models for the People portal ----
	public class PeopleProfileVm
	{
		public EmployeeViewModel Employee { get; set; } = null!;
		public string? JobTitleAr { get; set; }
		public string? JobTitleEn { get; set; }
		public string? CompanyAr { get; set; }
		public string? CompanyEn { get; set; }
		public string? BranchAr { get; set; }
		public string? BranchEn { get; set; }
		public bool IsActive { get; set; }
		public string? EmploymentType { get; set; }
		public string? DepartmentAr { get; set; }
		public string? DepartmentEn { get; set; }
	}

	public class OrgNodeRow
	{
		public string? NameAr { get; set; }
		public string? NameEn { get; set; }
		public int Type { get; set; }
		public string? TypeNameAr { get; set; }
		public string? TypeNameEn { get; set; }
		public string? Image { get; set; }
	}

	public class OrgPersonRow
	{
		public string? NameAr { get; set; }
		public string? NameEn { get; set; }
		public string? PositionAr { get; set; }
		public string? PositionEn { get; set; }
		public string? Image { get; set; }
	}

	public class PeopleStructureVm
	{
		public bool Placed { get; set; }
		public string? MeNameAr { get; set; }
		public string? MeNameEn { get; set; }
		public string? PositionAr { get; set; }
		public string? PositionEn { get; set; }
		public string? MeImage { get; set; }
		public List<OrgNodeRow> Ancestors { get; set; } = new();
		public List<OrgPersonRow> Subordinates { get; set; } = new();
	}

	public class ApproverName
	{
		public string? Ar { get; set; }
		public string? En { get; set; }
	}

	public class PeopleRequestsVm
	{
		public List<CrossBuy.Models.Context.Admin.EmployeeRequest> Mine { get; set; } = new();
		public List<CrossBuy.Models.Context.Admin.EmployeeRequest> Pending { get; set; } = new();
		public bool HasPayslip { get; set; }
		public Dictionary<int, ApproverName> Names { get; set; } = new();
	}

	public class PeopleLeavesVm
	{
		public List<LeaveTypes> Types { get; set; } = new();
		public List<LeaveRequest> Mine { get; set; } = new();
		public List<LeaveRequest> Pending { get; set; } = new();

		/// Work-day flags indexed by DayOfWeek (0=Sunday … 6=Saturday); null = no restriction.
		public bool[]? WorkDays { get; set; }

		/// Approver employee ID → display name, for the approval-chain timeline.
		public Dictionary<int, ApproverName> ApproverNames { get; set; } = new();
	}

	// public DTO for the MyTrainings course lookup (avoid anonymous-type dynamic binding across the Views assembly)
	public class MyTrainingCourseInfo
	{
		public string Title { get; set; } = "";
		public string? TitleEn { get; set; }
		public string? Provider { get; set; }
		public string? ProviderEn { get; set; }
		public decimal Hours { get; set; }
	}
}
