using AutoMapper;
using CrossBuy.BL;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	public partial class AdminController : Controller
	{
		private readonly IJobTitles jobTitles;
		private readonly IAdministrativeBodiesCompanyService administrativeBodiesCompanyService;
		private readonly IAdministrativeStructureService administrativeStructureService;
		private readonly IPolicesService policesService;
		private readonly ICompanyService companyService;
		private readonly IJobTitles jobTitleService;
		private readonly IHolidayService holidayService;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;

        public AdminController(IJobTitles jobTitles, IAdministrativeBodiesCompanyService administrativeBodiesCompanyService, IAdministrativeStructureService administrativeStructureService,
			IPolicesService policesService, ICompanyService companyService, IJobTitles jobTitleService, IHolidayService holidayService,
			IStringLocalizer<CrossBuy.SharedResources> localizer)
		{
			this.jobTitles = jobTitles;
			this.administrativeBodiesCompanyService = administrativeBodiesCompanyService;
			this.administrativeStructureService = administrativeStructureService;
			this.policesService = policesService;
			this.companyService = companyService;
			this.jobTitleService = jobTitleService;
			this.holidayService = holidayService;
			this.L = localizer;
		}

		// ===== Official holidays (HR-1) =====
		private const int HrCompanyId = 1;
		[HttpGet]
		public async Task<IActionResult> HolidaysList()
		{
			return View(await holidayService.GetAllAsync(HrCompanyId));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveHoliday(int id, string nameAr, string? nameEn, DateTime holidayDate, bool isRecurring, string? notes)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage);
			if (!gate.Ok) return HrDenied(nameof(HolidaysList));

			var (ok, err) = await holidayService.SaveAsync(new OfficialHoliday { ID = id, CompanyID = gate.CompanyId, NameAr = nameAr, NameEn = nameEn, HolidayDate = holidayDate, IsRecurring = isRecurring, Notes = notes });
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Holiday saved"].Value : err;
			return RedirectToAction(nameof(HolidaysList));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteHoliday(int id)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage);
			if (!gate.Ok) return HrDenied(nameof(HolidaysList));

			await holidayService.DeleteAsync(gate.CompanyId, id);
			TempData["HrMsg"] = L["Holiday deleted"].Value;
			return RedirectToAction(nameof(HolidaysList));
		}

		// ===== Attendance (HR-2) =====
		private IAttendanceService AttSvc => (HttpContext.RequestServices.GetService(typeof(IAttendanceService)) as IAttendanceService)!;
		private async Task<List<ViewModel.EmployeeListItemDto>> EmployeesAsync()
		{
			var svc = HttpContext.RequestServices.GetService(typeof(IEmployeeService)) as IEmployeeService;
			return svc != null ? await svc.GetAllAsync() : new List<ViewModel.EmployeeListItemDto>();
		}

		[HttpGet]
		public async Task<IActionResult> Attendance(int? year, int? month)
		{
			int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
			ViewBag.Year = y; ViewBag.Month = m;
			return View(await AttSvc.MonthlySummaryAsync(HrCompanyId, y, m));
		}

		[HttpGet]
		public async Task<IActionResult> AttendanceEntry(int? year, int? month)
		{
			int y = year ?? DateTime.Today.Year, m = month ?? DateTime.Today.Month;
			ViewBag.Year = y; ViewBag.Month = m;
			ViewBag.Employees = await EmployeesAsync();
			ViewBag.Records = await AttSvc.ForMonthAsync(HrCompanyId, null, y, m);
			return View();
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveAttendance(int employeeId, DateTime workDate, string? checkIn, string? checkOut, string? source, string? notes)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(Attendance));

			DateTime? ci = null, co = null;
			if (TimeSpan.TryParse(checkIn, out var t1)) ci = workDate.Date + t1;
			if (TimeSpan.TryParse(checkOut, out var t2)) co = workDate.Date + t2;
			var (ok, err, _) = await AttSvc.RecordAsync(gate.CompanyId, employeeId, workDate, ci, co, source ?? "Manual", notes, null);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Attendance recorded"].Value : err;
			return RedirectToAction(nameof(AttendanceEntry), new { year = workDate.Year, month = workDate.Month });
		}

		// ===== End-of-service / final settlement (HR-7) =====
		private CrossBuy.BL.IFinalSettlementService SettleSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IFinalSettlementService)) as CrossBuy.BL.IFinalSettlementService)!;
		private CrossBuy.BL.IBankService BankSvc2 => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IBankService)) as CrossBuy.BL.IBankService)!;

		[HttpGet]
		public async Task<IActionResult> FinalSettlement(int? employeeId, DateTime? terminationDate)
		{
			ViewBag.Employees = await EmployeesAsync();
			ViewBag.SelectedEmployee = employeeId;
			var termDate = terminationDate ?? DateTime.Today;
			ViewBag.TermDate = termDate;
			if (employeeId.HasValue) ViewBag.Preview = await SettleSvc.PreviewAsync(HrCompanyId, employeeId.Value, termDate);
			var banks = await BankSvc2.GetBankAccountsAsync(HrCompanyId);
			var boxes = await BankSvc2.GetCashBoxesAsync(HrCompanyId);
			ViewBag.PaySources = banks.Select(b => new PaySourceOption { GlAccountId = b.GlAccountId, Name = b.BankName })
				.Concat(boxes.Select(c => new PaySourceOption { GlAccountId = c.GlAccountId, Name = c.Name })).ToList();
			ViewBag.Settlements = await Db.FinalSettlements.AsNoTracking().Where(s => s.CompanyID == HrCompanyId).OrderByDescending(s => s.ID).Take(20).ToListAsync();
			return View();
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> PostFinalSettlement(int employeeId, DateTime terminationDate, string? reason, decimal gratuity, decimal otherEarnings, decimal deductions, int payFromGlAccountId)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.PayrollManage, subjectEmployeeId: employeeId, requireAccountingPost: true);
			if (!gate.Ok) return HrDenied(nameof(FinalSettlement), new { employeeId });

			var (ok, err) = await SettleSvc.PostAsync(gate.CompanyId, employeeId, terminationDate, reason, gratuity, otherEarnings, deductions, payFromGlAccountId, null);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Final settlement posted and employee terminated"].Value : err;
			return RedirectToAction(nameof(FinalSettlement), new { employeeId });
		}

		// ===== Employee contracts & document vault (HR-5) =====

		// =====================================================================================
		// STAGE 1 BATCH D1 WAVE 1 — THE HR PAYROLL GATE
		//
		// Before this wave these five actions carried [HttpPost][ValidateAntiForgeryToken] and NOTHING else, with
		// the company taken from the compile-time constant `HrCompanyId`. So any signed-in employee could post a
		// FINAL SETTLEMENT and a LEAVE PROVISION (both payroll journals), disburse cash for accrued leave, rewrite
		// every employee's leave balance via carry-over, and change the SALARY POLICY payroll is calculated from.
		//
		// No new authorization model was built: `HrAccessService` and its vocabulary already existed from Batch C,
		// and every action below maps onto a DOCUMENTED action rather than a new one —
		//   `leave-manage`   is defined as "leave types/policies/ENCASHMENT/PROVISION"
		//   `payroll-manage` is defined as "SALARY POLICIES, payroll runs"
		// Services are resolved through RequestServices because that is this controller's existing convention
		// (SettleSvc, AccrualSvc and DocSvc are all resolved the same way); adding five parameters to an already
		// very long constructor would be the larger change, not the smaller one.
		private CrossBuy.BL.IHrAccessService HrAccess =>
			(HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IHrAccessService)) as CrossBuy.BL.IHrAccessService)!;
		private CrossBuy.BL.Platform.IRequestCompanyResolver CompanyResolver =>
			(HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.Platform.IRequestCompanyResolver)) as CrossBuy.BL.Platform.IRequestCompanyResolver)!;
		private CrossBuy.BL.Platform.IBusinessContextAccessor BusinessContexts =>
			(HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.Platform.IBusinessContextAccessor)) as CrossBuy.BL.Platform.IBusinessContextAccessor)!;
		private CrossBuy.BL.AccountingAccessService AccountingAccess =>
			(HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.AccountingAccessService)) as CrossBuy.BL.AccountingAccessService)!;

		private sealed class HrGate { public bool Ok; public int CompanyId; public int? EmployeeId; }

		// `subjectEmployeeId` is the employee the operation is ABOUT, when there is one. It is passed as a
		// PermissionTarget so HrAccessService applies its own record rule (self / company-intersected manager /
		// role) against the EMPLOYEE ROW's company — the posted id never establishes authorization by itself.
		private async Task<HrGate> HrGateAsync(
			string action, int? subjectEmployeeId = null, bool requireAccountingPost = false)
		{
			var scope = await CompanyResolver.ResolveAsync();
			if (!scope.Ok) return new HrGate();

			var ctx = await BusinessContexts.TryGetCurrentAsync();
			if (ctx == null) return new HrGate();

			var target = subjectEmployeeId is > 0
				? CrossBuy.Models.Platform.PermissionTarget.ForSubjectEmployee(subjectEmployeeId.Value)
				: null;

			if (!await HrAccess.CanAsync(ctx, action, target)) return new HrGate();

			// Posting a payroll journal is an accounting act performed from an HR screen. The HR right says who may
			// run payroll; the accounting right says who may post to the ledger at all.
			if (requireAccountingPost && !await AccountingAccess.CanAsync(ctx, "post")) return new HrGate();

			return new HrGate { Ok = true, CompanyId = scope.CompanyId, EmployeeId = scope.EmployeeId };
		}

		// The same refusal wording the redirect path puts in TempData, for the actions that answer JSON.
		// One string, so a denied board drag and a denied page load say the same thing.
		private string HrDeniedMessage => L["You do not have permission for this HR action."].Value;

		private IActionResult HrDenied(string redirectAction, object? routeValues = null)
		{
			// One message for every refusal reason, so a missing right, a foreign employee and a non-existent one
			// are indistinguishable.
			TempData["HrErr"] = L["You do not have permission to perform this action"].Value;
			return RedirectToAction(redirectAction, routeValues);
		}

		private CrossBuy.BL.IHrDocumentService DocSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IHrDocumentService)) as CrossBuy.BL.IHrDocumentService)!;
		private string? WebRoot => (HttpContext.RequestServices.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment)) as Microsoft.AspNetCore.Hosting.IWebHostEnvironment)?.WebRootPath;

		[HttpGet]
		public async Task<IActionResult> HrDocuments(int? employeeId)
		{
			ViewBag.Employees = await EmployeesAsync();
			ViewBag.SelectedEmployee = employeeId;
			if (employeeId.HasValue)
			{
				var contracts = await DocSvc.GetContractsAsync(HrCompanyId, employeeId.Value);
				var documents = await DocSvc.GetDocumentsAsync(HrCompanyId, employeeId.Value);
				ViewBag.Contracts = contracts;
				ViewBag.Documents = documents;
				ViewBag.ContractFileCounts = await DocSvc.GetAttachmentCountsAsync(HrCompanyId, "Contract", contracts.Select(c => c.ID));
				ViewBag.DocumentFileCounts = await DocSvc.GetAttachmentCountsAsync(HrCompanyId, "Document", documents.Select(d => d.ID));
			}
			return View();
		}

		// Attachments of one record (contract/document) as JSON — used by the preview gallery on both HR screens.
		[HttpGet]
		public async Task<IActionResult> HrDocFiles(string kind, int id)
		{
			var k = kind == "Contract" ? "Contract" : "Document";
			var atts = await DocSvc.GetAttachmentsAsync(HrCompanyId, k, id);
			return Json(atts.Select(a => new { id = a.ID, url = a.FilePath, name = a.FileName ?? System.IO.Path.GetFileName(a.FilePath) }));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> AddAttachments(string kind, int ownerId, int employeeId, List<Microsoft.AspNetCore.Http.IFormFile>? files)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(HrDocuments), new { employeeId });

			await DocSvc.AddFilesAsync(gate.CompanyId, kind, ownerId, files, WebRoot);
			TempData["HrMsg"] = L["Attachments added"].Value;
			return RedirectToAction(nameof(HrDocuments), new { employeeId });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteAttachment(int id, int employeeId)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(HrDocuments), new { employeeId });

			var (ok, _) = await DocSvc.DeleteAttachmentAsync(gate.CompanyId, id, WebRoot);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Attachment deleted"].Value : L["Attachment not found"].Value;
			return RedirectToAction(nameof(HrDocuments), new { employeeId });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveContract(int id, int employeeId, string contractType, DateTime startDate, DateTime? endDate, string status, string? notes, List<Microsoft.AspNetCore.Http.IFormFile>? files)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(HrDocuments), new { employeeId });

			var (ok, err) = await DocSvc.SaveContractAsync(new EmploymentContract
			{ ID = id, CompanyID = gate.CompanyId, EmployeeID = employeeId, ContractType = contractType, StartDate = startDate, EndDate = endDate, Status = status ?? "Active", Notes = notes }, files, WebRoot);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Contract saved"].Value : err;
			return RedirectToAction(nameof(HrDocuments), new { employeeId });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteContract(int id, int employeeId)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(HrDocuments), new { employeeId });

			await DocSvc.DeleteContractAsync(gate.CompanyId, id);
			TempData["HrMsg"] = L["Contract deleted"].Value;
			return RedirectToAction(nameof(HrDocuments), new { employeeId });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveDocument(int id, int employeeId, string docType, string? docNumber, DateTime? issueDate, DateTime? expiryDate, string? notes, List<Microsoft.AspNetCore.Http.IFormFile>? files)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(HrDocuments), new { employeeId });

			var (ok, err) = await DocSvc.SaveDocumentAsync(new EmployeeDocument
			{ ID = id, CompanyID = gate.CompanyId, EmployeeID = employeeId, DocType = docType, DocNumber = docNumber, IssueDate = issueDate, ExpiryDate = expiryDate, Notes = notes }, files, WebRoot);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Document saved"].Value : err;
			return RedirectToAction(nameof(HrDocuments), new { employeeId });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteDocument(int id, int employeeId)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: employeeId);
			if (!gate.Ok) return HrDenied(nameof(HrDocuments), new { employeeId });

			await DocSvc.DeleteDocumentAsync(gate.CompanyId, id);
			TempData["HrMsg"] = L["Document deleted"].Value;
			return RedirectToAction(nameof(HrDocuments), new { employeeId });
		}

		[HttpGet]
		public async Task<IActionResult> DocExpiryAlerts(int? days)
		{
			int d = days ?? 60;
			ViewBag.Days = d;
			return View(await DocSvc.ExpiringAsync(HrCompanyId, d));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> NotifyExpiring(int days)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage);
			if (!gate.Ok) return HrDenied(nameof(DocExpiryAlerts));

			var count = await DocSvc.NotifyExpiringAsync(gate.CompanyId, days);
			TempData["HrMsg"] = string.Format(L["Sent {0} alert(s)"].Value, count);
			return RedirectToAction(nameof(DocExpiryAlerts), new { days });
		}

		// ===== Leave encashment & provision (HR-2f) =====
		private CrossBuy.BL.ILeaveAccrualService AccrualSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.ILeaveAccrualService)) as CrossBuy.BL.ILeaveAccrualService)!;
		private CrossBuy.BL.IBankService BankSvc => (HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.IBankService)) as CrossBuy.BL.IBankService)!;
		private CrossBuy.Models.Context.CrossDbContext Db => (HttpContext.RequestServices.GetService(typeof(CrossBuy.Models.Context.CrossDbContext)) as CrossBuy.Models.Context.CrossDbContext)!;

		[HttpGet]
		public async Task<IActionResult> LeaveAccrual(int? employeeId, DateTime? asOf)
		{
			ViewBag.Employees = await EmployeesAsync();
			ViewBag.SelectedEmployee = employeeId;
			ViewBag.Balances = employeeId.HasValue ? await AccrualSvc.GetEncashableBalancesAsync(employeeId.Value) : new List<CrossBuy.BL.EncashableBalance>();
			var banks = await BankSvc.GetBankAccountsAsync(HrCompanyId);
			var boxes = await BankSvc.GetCashBoxesAsync(HrCompanyId);
			ViewBag.PaySources = banks.Select(b => new PaySourceOption { GlAccountId = b.GlAccountId, Name = b.BankName })
				.Concat(boxes.Select(c => new PaySourceOption { GlAccountId = c.GlAccountId, Name = c.Name })).ToList();
			var asOfDate = asOf ?? new DateTime(DateTime.Today.Year, 12, 31);
			ViewBag.AsOf = asOfDate;
			ViewBag.Provision = await AccrualSvc.ProvisionPreviewAsync(HrCompanyId, asOfDate);
			ViewBag.LeaveTypes = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(Db.LeaveTypes.AsNoTracking().OrderBy(t => t.NameAr));
			return View();
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> Encash(int employeeId, int leaveTypeId, int days, int payFromGlAccountId, DateTime encashDate)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.LeaveManage, subjectEmployeeId: employeeId, requireAccountingPost: true);
			if (!gate.Ok) return HrDenied(nameof(LeaveAccrual), new { employeeId });

			var (ok, err) = await AccrualSvc.EncashAsync(gate.CompanyId, employeeId, leaveTypeId, days, payFromGlAccountId, encashDate, null);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Leave allowance disbursed"].Value : err;
			return RedirectToAction(nameof(LeaveAccrual), new { employeeId });
		}

		[HttpGet]
		public async Task<IActionResult> LeaveCarryOver(int? fromYear)
		{
			int y = fromYear ?? (DateTime.Today.Year - 1);
			ViewBag.FromYear = y;
			ViewBag.Rows = await AccrualSvc.CarryOverPreviewAsync(HrCompanyId, y);
			ViewBag.Existing = await Db.LeaveCarryOvers.AsNoTracking().Where(c => c.CompanyID == HrCompanyId && c.Year == y + 1).SumAsync(c => (int?)c.Days) ?? 0;
			return View();
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> RunLeaveCarryOver(int fromYear)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.LeaveManage);
			if (!gate.Ok) return HrDenied(nameof(LeaveCarryOver), new { fromYear });

			var (ok, err, rows) = await AccrualSvc.RunCarryOverAsync(gate.CompanyId, fromYear);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? string.Format(L["Carried over {0} record(s) to year {1}"].Value, rows, fromYear + 1) : err;
			return RedirectToAction(nameof(LeaveCarryOver), new { fromYear });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> PostLeaveProvision(DateTime asOf)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.LeaveManage, requireAccountingPost: true);
			if (!gate.Ok) return HrDenied(nameof(LeaveAccrual), new { asOf });

			var (ok, err) = await AccrualSvc.PostProvisionAsync(gate.CompanyId, asOf, null);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? L["Leave provision settlement posted"].Value : err;
			return RedirectToAction(nameof(LeaveAccrual), new { asOf });
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveLeaveTypeEncashable(int id, bool isEncashable)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.LeaveManage);
			if (!gate.Ok) return HrDenied(nameof(LeaveTypesList));

			var t = await Db.LeaveTypes.FirstOrDefaultAsync(x => x.ID == id);
			if (t != null) { t.IsEncashable = isEncashable; await Db.SaveChangesAsync(); TempData["HrMsg"] = L["Encashability updated"].Value; }
			return RedirectToAction(nameof(LeaveAccrual));
		}
		public async Task<IActionResult> Index()
		{
			var now = DateTime.UtcNow;
			var year = now.Year;
			var today = now.Date;
			var dto = new CrossBuy.BL.AdminDashboardDto { Year = year };

			// ----- Employees (company scoped) -----
			var emps = await Db.Employee.AsNoTracking().Include(e => e.JobTitle)
				.Where(e => e.EmpCompanyID == HrCompanyId).ToListAsync();
			dto.TotalEmployees = emps.Count;
			dto.ActiveEmployees = emps.Count(e => e.IsActive);
			dto.InactiveEmployees = emps.Count(e => !e.IsActive);
			dto.NewHiresThisYear = emps.Count(e => e.DateOfJoining.Year == year);
			dto.ByJobTitle = emps
				.GroupBy(e => new { e.JobTitleID, Ar = e.JobTitle != null ? e.JobTitle.TitleAr : null, En = e.JobTitle != null ? e.JobTitle.Title : null })
				.Select(g => new CrossBuy.BL.HrDistRow { NameAr = g.Key.Ar, NameEn = g.Key.En, Count = g.Count() })
				.OrderByDescending(x => x.Count).Take(6).ToList();
			foreach (var r in dto.ByJobTitle) r.Pct = dto.TotalEmployees > 0 ? (int)System.Math.Round(100.0 * r.Count / dto.TotalEmployees) : 0;

			// ----- Leave requests (company scoped via Employee) -----
			var reqs = await Db.LeaveRequests.AsNoTracking().Include(r => r.LeaveType).Include(r => r.Employee)
				.Where(r => r.Employee != null && r.Employee.EmpCompanyID == HrCompanyId).ToListAsync();
			dto.PendingLeaves = reqs.Count(r => r.Status == 0);
			dto.ApprovedLeavesYtd = reqs.Count(r => r.Status == 1 && r.StartDate.Year == year);
			dto.OnLeaveToday = reqs.Count(r => r.Status == 1 && r.StartDate.Date <= today && r.EndDate.Date >= today);
			for (var i = 5; i >= 0; i--)
			{
				var m = new DateTime(now.Year, now.Month, 1).AddMonths(-i);
				dto.LeaveMonthly.Add(new CrossBuy.BL.HrMonthPoint
				{
					Year = m.Year,
					Month = m.Month,
					Count = reqs.Count(r => { var d = r.CreatedAt ?? r.StartDate; return d.Year == m.Year && d.Month == m.Month; })
				});
			}
			dto.RecentLeaves = reqs.OrderByDescending(r => r.ID).Take(6).Select(r => new CrossBuy.BL.HrLeaveRow
			{
				// The name the reader sees, not the Arabic one: this list sits beside the expiring-documents
				// card, which already resolved it, so the two cards disagreed about the same person.
				EmployeeName = r.Employee != null
					? CrossBuy.BL.EmployeeNames.Of(r.Employee.FullName, r.Employee.FullNameEn)
					: null,
				TypeAr = r.LeaveType != null ? r.LeaveType.NameAr : null,
				TypeEn = r.LeaveType != null ? r.LeaveType.NameEn : null,
				StartDate = r.StartDate,
				EndDate = r.EndDate,
				Days = r.Days,
				Status = r.Status
			}).ToList();

			// ----- Attendance (current month, company-wide totals) -----
			try
			{
				var att = await AttSvc.MonthlySummaryAsync(HrCompanyId, now.Year, now.Month);
				dto.AttPresent = att.Sum(a => a.PresentDays);
				dto.AttAbsent = att.Sum(a => a.AbsentDays);
				dto.AttLate = att.Sum(a => a.LateCount);
				dto.AttLeave = att.Sum(a => a.LeaveDays);
				dto.AttWorkDays = att.Sum(a => a.WorkDays);
			}
			catch { /* attendance service optional */ }

			// ----- Documents / contracts expiring (next 60 days) -----
			try
			{
				var exp = await DocSvc.ExpiringAsync(HrCompanyId, 60);
				dto.ExpiredDocs = exp.Count(x => x.DaysLeft < 0);
				dto.ExpiringSoonCount = exp.Count(x => x.DaysLeft >= 0);
				dto.ExpiringDocs = exp.OrderBy(x => x.DaysLeft).Take(6).Select(x => new CrossBuy.BL.HrExpiryRow
				{
					EmployeeName = x.EmployeeName,
					Label = x.Label,
					Date = x.Date,
					DaysLeft = x.DaysLeft,
					Kind = x.Kind
				}).ToList();
			}
			catch { }

			// ----- Upcoming official holidays -----
			try
			{
				var hols = await holidayService.GetAllAsync(HrCompanyId);
				dto.UpcomingHolidays = hols.Select(h =>
				{
					var d = h.HolidayDate.Date;
					if (h.IsRecurring)
					{
						try { d = new DateTime(now.Year, h.HolidayDate.Month, h.HolidayDate.Day); if (d < today) d = d.AddYears(1); }
						catch { }
					}
					return new CrossBuy.BL.HrHolidayRow { NameAr = h.NameAr, NameEn = h.NameEn, Date = d };
				}).Where(h => h.Date >= today).OrderBy(h => h.Date).Take(5).ToList();
			}
			catch { }

			return View(dto);
		}

        // image is saved together with SaveEmployee (no separate endpoint)
		public async Task<IActionResult> JobTitlesList()
		{
			var model = await this.jobTitles.GetAll();
			return View(model);
		}

		[HttpGet]
		public async Task<IActionResult> GetJobTitles()
		{
			try
			{
				var list = await jobTitles.GetAll();
				var items = list.Select(j => new { id = j.ID, name = j.TitleAr ?? j.Title, nameEn = j.Title });
				return Json(new { ok = true, data = items });
			}
			catch (Exception ex)
			{
				return Json(new { ok = false, message = ex.Message });
			}
		}

		[HttpGet]
		public async Task<IActionResult> GetBranches(int? companyId = null)
		{
			try
			{
				var svc = HttpContext.RequestServices.GetService(typeof(BL.ICompanyService)) as BL.ICompanyService;
				if (svc == null) return Json(new { ok = false, message = "service missing" });
				var list = companyId.HasValue && companyId.Value > 0
					? await svc.GetBranchesByCompanyAsync(companyId.Value)
					: await svc.GetAllBranchesAsync();
				var items = list.Select(b => new { id = b.ID, name = b.NameAr ?? b.Name, nameEn = b.Name });
				return Json(new { ok = true, data = items });
			}
			catch (Exception ex)
			{
				return Json(new { ok = false, message = ex.Message });
			}
		}

		[HttpGet]
		public async Task<IActionResult> GetCompanies()
		{
			try
			{
				var svc = HttpContext.RequestServices.GetService(typeof(BL.ICompanyService)) as BL.ICompanyService;
				if (svc == null) return Json(new { ok = false, message = "service missing" });
				var list = await svc.GetAllCompaniesAsync();
				var items = list.Select(c => new { id = c.CompanyID, name = c.CompanyNameAr ?? c.CompanyName, nameEn = c.CompanyName });
				return Json(new { ok = true, data = items });
			}
			catch (Exception ex)
			{
				return Json(new { ok = false, message = ex.Message });
			}
		}

		public async Task<IActionResult> JobTitle(int? ID)
		{
			var model = await this.jobTitles.GetByID(ID);
			return View(model);
		}
		[HttpPost]
		public async Task<IActionResult> JobTitle(JobTitleDto model)
		{
			if (ModelState.IsValid)
			{
				try
				{
					var result = await jobTitles.Save(model);
					return Json(new { success = true, data = result });
				}
				catch (Exception ex)
				{
					return Json(new { success = false, message = ex.Message });
				}
			}

			var errors = ModelState.Values.SelectMany(v => v.Errors)
										  .Select(e => e.ErrorMessage).ToList();
			return Json(new { success = false, message = L["Invalid data"].Value, errors });
		}

		public async Task<IActionResult> AdministrativeBodiesCompanyList()
		{
			var model = await administrativeBodiesCompanyService.GetAll();
			return View(model);
		}


		public async Task<IActionResult> AdministrativeBodiesCompany(int id)
		{
			var model = await this.administrativeBodiesCompanyService.GetById(id);
			return View(model);
		}

		[HttpPost]
		public async Task<IActionResult> AdministrativeBodiesCompany(AdministrativeBodiesCompanyDto model)
		{
			if (ModelState.IsValid)
			{
				try
				{
					var result = await administrativeBodiesCompanyService.Save(model);
					return Json(new { success = true, data = result });
				}
				catch (Exception ex)
				{
					return Json(new { success = false, message = ex.Message });
				}
			}

			var errors = ModelState.Values.SelectMany(v => v.Errors)
										  .Select(e => e.ErrorMessage).ToList();
			return Json(new { success = false, message = L["Invalid data"].Value, errors });
		}
		public async Task<IActionResult> AdministrativeStructure()
		{

			return View();
		}
		[HttpGet]
		public async Task<IActionResult> GetHierarchicals(int? companyId = null)
		{
			var data = companyId.HasValue && companyId.Value > 0
				? await administrativeStructureService.GetByCompanyAsync(companyId.Value)
				: await administrativeStructureService.GataAll();
			return Json(data);
		}

		public async Task<JsonResult> GetParentInfo(int parentId)
		{
			var parent = await administrativeStructureService.GetParentData(parentId);
			if (parent == null)
			{
				return Json(new { success = false, message = L["Item not found"].Value });
			}

			return Json(new
			{
				success = true,
				nameAr = parent.H_Name,
				nameEn = parent.H_NameEn
			});
		}


		[HttpPost]
		public async Task<JsonResult> AddHierarchicalItem(int parentId, string nameAr, string nameEn, string notes)
		{
			try
			{
				var newItem = new HierarchicalDto()
				{
					H_Name = nameAr,
					H_NameEn = nameEn,
					H_Parent = parentId,
					H_Notes = notes
				};

				var dto = await administrativeStructureService.Save(newItem);
				return Json(new { success = true, itemId = dto.H_ID, nameAr = dto.H_Name, nameEn = dto.H_NameEn });
			}
			catch (Exception ex)
			{
				return Json(new { success = false, message = L["An error occurred: "].Value + ex.Message });
			}
		}



		public async Task<IActionResult> Polices()
		{
			List<PoliciesDto> model = await policesService.GetAllPoliciesAsync();
			return View(model);
		}

        // GET — أنواع الإجازات للـ dropdown
        [HttpGet]
        public async Task<IActionResult> GetLeaveTypes(CancellationToken ct)
        {
            try
            {
                var types = await policesService.GetLeaveTypesAsync(ct);
                return Json(new { ok = true, data = types });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveLeavePolicy(
    [FromBody] LeavePoliciesDto model,
    CancellationToken ct)
        {
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.LeaveManage);
			if (!gate.Ok) return Json(new { ok = false, message = HrDeniedMessage });

            if (model == null)
                return Json(new { ok = false, message = L["Invalid data"].Value });

            try
            {
                var result = await policesService.SaveLeavePolicyAsync(model, ct);
                var verb = model.ID == 0 ? L["Added"].Value : L["Updated"].Value;
                return Json(new { ok = true, message = verb + L[" successfully."].Value, data = result });
            }
            catch (KeyNotFoundException knf)
            {
                return Json(new { ok = false, message = knf.Message });
            }
            catch (InvalidOperationException ioe)        // ✅ التحقق من التكرار
            {
                return Json(new { ok = false, message = ioe.Message });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    ok = false,
                    message = L["An error occurred while saving."].Value,
                    errors = new[] { ex.Message }
                });
            }
        }

        public async Task<IActionResult> PolicesData()
		{
			return View();
		}

		public async Task<IActionResult> LeaveTypesList()
		{
			List<LeaveTypesDto> model = await policesService.GetAllLeaveTypesAsync();

			return View(model);
		}


		[HttpPost]
		public async Task<IActionResult> SaveLeaveType([FromBody] LeaveTypesDto dto)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.LeaveManage);
			if (!gate.Ok) return Json(new { ok = false, message = HrDeniedMessage });

			try
			{
				await policesService.SaveLeaveType(dto);
				return Json(new { success = true });
			}
			catch (Exception ex)
			{
				return Json(new { success = false, message = ex.Message });
			}
		}
		[HttpGet]
		public async Task<IActionResult> GetLeaveTypeById(int id)
		{
			try
			{
				var leaveType = await policesService.GetLeaveTypeByIDAsync(id);

				if (leaveType == null)
				{
					return Json(new { success = false, message = L["Leave type not found."].Value });
				}

				return Json(new { success = true, data = leaveType });
			}
			catch (Exception ex)
			{
				return Json(new { success = false, message = ex.Message });
			}
		}




        [HttpGet]
        public async Task<IActionResult> GetSalaryPolicy(int id)
        {
            try
            {
                if (id <= 0)
                    return Json(new { ok = false, message = L["Invalid identifier."].Value });

                var result = await policesService.GetSalaryPolicyAsync(id);
                if (result == null)
                    return Json(new { ok = false, message = L["Record not found."].Value });

                return Json(new { ok = true, data = new { item = result } });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        // D1 WAVE 1 — CRITICAL. The salary policy is the BASIS every payroll calculation reads, so changing it
        // silently changes future payroll for everyone it applies to. `payroll-manage` is HrActions' documented
        // action for "salary policies, payroll runs" — derived, not invented. ApiPerm because this returns JSON.
        [CrossBuy.Models.ApiPerm(CrossBuy.Models.ApiPermAttribute.Hr, CrossBuy.BL.HrActions.PayrollManage)]
        public async Task<IActionResult> SaveSalaryPolicy([FromBody] SalaryPoliciesDto model)
        {
            try
            {
                if (model == null)
                    return Json(new { ok = false, message = L["Invalid data"].Value });

                var result = await policesService.SaveSalaryPolicyAsync(model);

                return Json(new
                {
                    ok = true,
                    message = model.ID == 0 ? L["Added successfully."].Value : L["Updated successfully."].Value,
                    data = new { item = result, id = result.ID }
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Json(new { ok = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, message = L["An unexpected error occurred."].Value, errors = new[] { ex.Message } });
            }
        }





        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveMainPolices(
    [FromBody] PoliciesDto model,
    CancellationToken ct = default)
        {
            if (model == null)
                return Json(new { ok = false, message = L["No data submitted."].Value });

            // POLICY ADMINISTRATION, plus a second gate that closes a real back door.
            //
            // PoliciesDto carries LeavePolicies, AttendancePolicies AND SalaryPolicies. SalaryPolicies is
            // base salary, allowances, overtime rate, late and absence penalties, social-insurance shares
            // and tax rate — compensation. The dedicated endpoint for it, SaveSalaryPolicy, is already
            // [ApiPerm(Hr, PayrollManage)], so without the second check below an HR officer holding only
            // leave-manage could change every employee's salary basis through THIS endpoint while being
            // refused at the one built for it. A permission that another route walks around is not a
            // permission.
            //
            // payroll-manage is also NeverBootstrapOpen, so this branch stays closed for a company that
            // has configured no HR role — which is the point of that classification.
            var gate = await HrGateAsync(CrossBuy.BL.HrActions.LeaveManage);
            if (!gate.Ok) return Json(new { ok = false, message = HrDeniedMessage });

            if (model.SalaryPolicies is { Count: > 0 })
            {
                var payroll = await HrGateAsync(CrossBuy.BL.HrActions.PayrollManage);
                if (!payroll.Ok) return Json(new { ok = false, message = HrDeniedMessage });
            }

            try
            {
                // Persist
                var saved = await policesService.SavePolicyAsync(model, ct);

                // Reload from DB to ensure we return the canonical, up-to-date DTO
                var reloaded = await policesService.GetPolicyAsync(saved.ID, ct);

                // Build response (include both id and item as your AJAX expects)
                return Json(new
                {
                    ok = true,
                    message = L["Saved successfully."].Value,
                    data = new
                    {
                        id = (reloaded?.ID ?? saved.ID),
                        item = (reloaded ?? saved)
                    }
                });
            }
            catch (KeyNotFoundException ex)
            {
                Response.StatusCode = 404;
                return Json(new { ok = false, message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                Response.StatusCode = 400;
                return Json(new { ok = false, message = ex.Message });
            }
            catch
            {
                Response.StatusCode = 500;
                return Json(new { ok = false, message = L["An unexpected error occurred while saving."].Value });
            }
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAttendancePolicy(
    [FromBody] AttendancePoliciesDto model,
    CancellationToken ct)
        {
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage);
			if (!gate.Ok) return Json(new { ok = false, message = HrDeniedMessage });

            if (model == null)
                return Json(new { ok = false, message = L["Invalid data"].Value });

            try
            {
                var result = await policesService.SaveAttendancePolicyAsync(model, ct);
                var verb = model.ID == 0 ? L["Added"].Value : L["Updated"].Value;
                return Json(new { ok = true, message = verb + L[" successfully."].Value, data = result });
            }
            catch (KeyNotFoundException knf)
            {
                return Json(new { ok = false, message = knf.Message });
            }
            catch (InvalidOperationException ioe)
            {
                return Json(new { ok = false, message = ioe.Message });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    ok = false,
                    message = L["An error occurred while saving."].Value,
                    errors = new[] { ex.Message }
                });
            }
        }

        // AdminController (DI: IPoliciesService _policiesService)
        [HttpGet]
		public async Task<IActionResult> GetPolicy(int id, CancellationToken ct = default)
		{
			if (id <= 0)
			{
				Response.StatusCode = 400;
				return Json(new { ok = false, message = L["Invalid identifier."].Value });
			}

			var dto = await policesService.GetPolicyAsync(id, ct);
			if (dto == null)
			{
				Response.StatusCode = 404;
				return Json(new { ok = false, message = L["Policy not found."].Value });
			}

			// Shape matches: res.ok && res.data.item
			return Json(new
			{
				ok = true,
				data = new { item = dto }
			});
		}



        // Employees list screen (mirrors Service/BranchesList)
        public async Task<IActionResult> EmployeesList()
        {
            var svc = HttpContext.RequestServices.GetService(typeof(BL.IEmployeeService)) as BL.IEmployeeService;
            var model = svc != null ? await svc.GetAllAsync() : new List<ViewModel.EmployeeListItemDto>();
            return View(model);
        }

        public async Task<IActionResult> EmployeeData(int? id, int? fromApplication)
        {
            EmployeeViewModel model;
            if (id.HasValue && id.Value > 0)
            {
                var svc = HttpContext.RequestServices.GetService(typeof(BL.IEmployeeService)) as BL.IEmployeeService;
                model = svc != null ? await svc.GetByIdAsync(id.Value) : null;
                if (model == null) return NotFound();
            }
            else if (fromApplication.HasValue && fromApplication.Value > 0)
            {
                // R3: hire — prefill the wizard from an accepted application (HR reviews, then saves)
                var app = await RecruitSvc.GetApplicationAsync(HrCompanyId, fromApplication.Value);
                if (app == null) { TempData["HrErr"] = L["Record not found."].Value; return RedirectToAction(nameof(Applications)); }
                if (app.Status != "Accepted") { TempData["HrErr"] = L["Only accepted applications can be hired"].Value; return RedirectToAction(nameof(ApplicationDetail), new { id = app.ID }); }
                model = new EmployeeViewModel
                {
                    FirstName = app.FirstName, LastName = app.LastName,
                    FullName = string.IsNullOrWhiteSpace(app.FullName) ? (app.FirstName + " " + app.LastName).Trim() : app.FullName,
                    FullNameEn = app.FullNameEn, Email = app.Email, PhoneNumber = app.PhoneNumber, Address = app.Address,
                    DateOfBirth = app.DateOfBirth, Gender = app.Gender, MaritalStatus = app.MaritalStatus,
                    CountryID = app.CountryID, JobTitleID = app.JobTitleID, BranchID = app.BranchID,
                    EmpCompanyID = app.EmpCompanyID, DepartmentID = app.DepartmentID, EmploymentType = app.EmploymentType,
                    ProfileImage = app.PhotoPath,   // carry the applicant photo → shown in the wizard, no re-upload needed
                    DateOfJoining = DateTime.Today
                };
                ViewBag.ApplicationId = app.ID;
            }
            else
            {
                model = new EmployeeViewModel();
            }
            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> GetPolicies()
        {
            try
            {
                var list = await policesService.GetAllPoliciesAsync();
                var items = list.Select(p => new { id = p.ID, name = p.NameAr ?? p.NameEn, nameEn = p.NameEn, notes = p.Notes });
                return Json(new { ok = true, data = items });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, message = ex.Message });
            }
        }

        [HttpGet]
	public async Task<IActionResult> GetEmployeeByUserId(string userId)
	{
		if (string.IsNullOrEmpty(userId)) return Json(new { ok = false, message = "missing userId" });
		try
		{
         // resolve IEmployeeService from DI
			var svc = HttpContext.RequestServices.GetService(typeof(BL.IEmployeeService)) as BL.IEmployeeService;
			if (svc == null) return Json(new { ok = false, message = "service missing" });
			var e = await svc.GetEmployeeByUserIdAsync(userId);
			if (e == null) return Json(new { ok = false, message = "not found" });
			return Json(new { ok = true, data = e });
		}
		catch (Exception ex)
		{
			return Json(new { ok = false, message = ex.Message });
		}
	}

    [HttpPost]
	[ValidateAntiForgeryToken]
 public async Task<IActionResult> SaveEmployee([FromForm] EmployeeViewModel model, [FromForm] Microsoft.AspNetCore.Http.IFormFile ProfileImage, [FromForm] int applicationId = 0)
	{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.EmployeeManage, subjectEmployeeId: model.ID);
			// THIS ENDPOINT IS CALLED BY AJAX, so a refusal must be JSON. HrDenied answers with a 302 to
			// EmployeesList; jQuery follows it, receives that page's HTML with status 200, hands the string to
			// `success`, finds no `success === true` and no `message`, and shows the generic "save failed,
			// please try again" — a refusal displayed as a malfunction, with the reason thrown away. The other
			// JSON actions in this controller already answer HrDeniedMessage; this one did not.
			if (!gate.Ok) return Json(new { success = false, message = HrDeniedMessage });

      if (model == null)
			return Json(new { success = false, message = "Invalid data" });

		// explicit server-side validation (return field-level errors similar to old flow)
		var errorsList = new List<string>();
		if (string.IsNullOrWhiteSpace(model.FirstName)) errorsList.Add(L["First name is required"].Value);
		if (string.IsNullOrWhiteSpace(model.LastName)) errorsList.Add(L["Last name is required"].Value);
		if (string.IsNullOrWhiteSpace(model.Email)) errorsList.Add(L["Email is required"].Value);
		else
		{
			try
			{
				var _ = new System.Net.Mail.MailAddress(model.Email);
			}
			catch
			{
				errorsList.Add(L["Invalid email format"].Value);
			}
		}

		if (errorsList.Any())
		{
			return Json(new { success = false, message = L["Invalid data"].Value, errors = errorsList });
		}

		try
		{
			var svc = HttpContext.RequestServices.GetService(typeof(BL.IEmployeeService)) as BL.IEmployeeService;
			if (svc == null) return Json(new { success = false, message = "service missing" });

			// R3 hire: the applicant has no login → auto-create an Identity user (email = applicant email) and link it.
			string? tempPassword = null;
			if (applicationId > 0 && string.IsNullOrWhiteSpace(model.UserId) && !string.IsNullOrWhiteSpace(model.Email))
			{
				var userManager = HttpContext.RequestServices.GetService(typeof(Microsoft.AspNetCore.Identity.UserManager<CrossBuy.Models.Context.Admin.Users>)) as Microsoft.AspNetCore.Identity.UserManager<CrossBuy.Models.Context.Admin.Users>;
				if (userManager != null)
				{
					var existingUser = await userManager.FindByEmailAsync(model.Email);
					if (existingUser != null) { model.UserId = existingUser.Id; }
					else
					{
						var newUser = new CrossBuy.Models.Context.Admin.Users { UserName = model.Email, Email = model.Email, EmailConfirmed = true };
						tempPassword = "Cb@" + Guid.NewGuid().ToString("N").Substring(0, 8) + "9";   // meets Identity complexity
						var cr = await userManager.CreateAsync(newUser, tempPassword);
						if (!cr.Succeeded) return Json(new { success = false, message = string.Join("; ", cr.Errors.Select(e => e.Description)) });
						try { await userManager.AddToRoleAsync(newUser, "Employee"); } catch { /* role optional */ }
						model.UserId = newUser.Id;
					}
				}
			}

			var webRoot = HttpContext.RequestServices.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment)) as Microsoft.AspNetCore.Hosting.IWebHostEnvironment;
			var saved = await svc.SaveEmployeeAsync(model, ProfileImage, webRoot?.WebRootPath);
			// R3: if this came from a job application, carry its documents onto the new employee and mark it Hired
			string? hireError = null;
			if (applicationId > 0 && saved != null && saved.ID > 0)
			{
				var (hok, herr) = await RecruitSvc.HireFromApplicationAsync(gate.CompanyId, applicationId, saved.ID);
				if (!hok) hireError = herr;
			}
			if (applicationId > 0 && hireError == null)
				TempData["HrMsg"] = tempPassword != null
					? L["Applicant hired. A login was created — temporary password:"].Value + " " + tempPassword
					: L["Applicant hired and linked to the employee."].Value;
			return Json(new { success = true, data = saved, hired = applicationId > 0 && hireError == null, hireError, tempPassword });
		}
		catch (Exception ex)
		{
			// The service raises its deployment failures as ONE English sentence that is also a resource key,
			// so the reader gets it in their own language. A key with no entry resolves to itself, which is the
			// existing behaviour for every other exception — nothing is swallowed.
			return Json(new { success = false, message = L[ex.Message].Value });
		}
	}

	}



}
