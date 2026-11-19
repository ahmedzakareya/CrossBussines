using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// ==========================================================================================
	// EMPLOYEE SELF-SERVICE API for the redesigned mobile app (2026-11-10, owner-approved prototype
	// design-output/mobile-prototype): my payslips, my attendance, my requests, and ONE approvals inbox.
	//
	// Same rules as the browser, never a second set:
	//   * the employee is the resolved BusinessContext's, never an id from the body or the query;
	//   * creating a request asks employee-request about the caller themself (HrAccessService);
	//   * deciding asks leave-approve OR "it is my turn" (HrApprovalTurn), exactly as PeopleController does;
	//   * the work is done by the existing services (EmployeeRequestService, LeaveWorkflowService, AttendanceService).
	// A request that does not exist and one the caller may not touch answer identically (404), so ids cannot be probed.
	// ==========================================================================================
	[ApiController]
	[Route("api/ess")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class EssApiController : ControllerBase
	{
		private readonly CrossDbContext _db;
		private readonly IHrAccessService _hrAccess;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
		private readonly IEmployeeRequestService _requests;
		private readonly ILeaveWorkflowService _leave;
		private readonly IAttendanceService _attendance;
		private readonly ILeaveDashboardService _dashboard;

		public EssApiController(CrossDbContext db, IHrAccessService hrAccess, CrossBuy.BL.Platform.IBusinessContextAccessor contexts,
			IEmployeeRequestService requests, ILeaveWorkflowService leave, IAttendanceService attendance, ILeaveDashboardService dashboard)
		{ _db = db; _hrAccess = hrAccess; _contexts = contexts; _requests = requests; _leave = leave; _attendance = attendance; _dashboard = dashboard; }

		private static object NotAuthorised => new { success = false, message = "Not authorised" };

		private async Task<BusinessContext?> MeAsync()
		{
			var ctx = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			return ctx is { CompanyId: > 0, EmployeeId: > 0 } ? ctx : null;
		}

		private async Task<string> CurrencyAsync(int companyId) =>
			(await new CrossBuy.BL.Hr.HrRulePackService(_db).EffectiveAsync(companyId, HttpContext.RequestAborted))?.Rules.Currency ?? "";

		// ---------------- payslips ----------------
		// GET /api/ess/payslips — my posted payslips, newest first
		[HttpGet("payslips")]
		public async Task<IActionResult> Payslips()
		{
			var me = await MeAsync(); if (me == null) return NotFound(NotAuthorised);
			var rows = await _db.Payslips.AsNoTracking().Where(p => p.EmployeeID == me.EmployeeId && p.CompanyID == me.CompanyId)
				.OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
				.Select(p => new { id = p.ID, year = p.Year, month = p.Month, net = p.Net }).ToListAsync();
			return Ok(new { success = true, currency = await CurrencyAsync(me.CompanyId), data = rows });
		}

		// GET /api/ess/payslips/{id} — one of MY payslips, every earning and deduction (they reconcile to the net)
		[HttpGet("payslips/{id:int}")]
		public async Task<IActionResult> Payslip(int id)
		{
			var me = await MeAsync(); if (me == null) return NotFound(NotAuthorised);
			var p = await _db.Payslips.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.EmployeeID == me.EmployeeId && x.CompanyID == me.CompanyId);
			if (p == null) return NotFound(NotAuthorised);
			return Ok(new
			{
				success = true, currency = await CurrencyAsync(me.CompanyId),
				data = new
				{
					id = p.ID, slipNo = $"PS-{p.Year}-{p.ID:D5}", year = p.Year, month = p.Month,
					earnings = new { basic = p.BaseSalary, allowances = p.Allowances, overtime = p.OvertimePay },
					deductions = new { socialInsurance = p.SiEmployee, tax = p.Tax, late = p.LatePenalty, lateMinutes = p.LateMinutes,
						absence = p.AbsencePenalty, absentDays = p.AbsentDays, loan = p.LoanDeduction, partPaidLeave = p.SickLeaveDeduction },
					net = p.Net, employerInsurance = p.SiCompany,
					// printing is Report Studio's: the app opens this document, which re-checks that the slip is the caller's
					report = new { code = "Hr.Payslip", parameter = "PayslipId" },
				},
			});
		}

		// ---------------- attendance ----------------
		// GET /api/ess/attendance?year=2026&month=11 — my month: the summary and each day
		[HttpGet("attendance")]
		public async Task<IActionResult> Attendance(int? year, int? month)
		{
			var me = await MeAsync(); if (me == null) return NotFound(NotAuthorised);
			int y = year ?? DateTime.Today.Year, m = month is >= 1 and <= 12 ? month.Value : DateTime.Today.Month;
			var summary = (await _attendance.MonthlySummaryAsync(me.CompanyId, y, m)).FirstOrDefault(r => r.EmployeeID == me.EmployeeId);
			var days = await _attendance.ForMonthAsync(me.CompanyId, me.EmployeeId, y, m);
			return Ok(new
			{
				success = true, year = y, month = m,
				summary = summary == null ? null : new { workDays = summary.WorkDays, presentDays = summary.PresentDays, lateCount = summary.LateCount, lateMinutes = summary.LateMinutes,
					overtimeMinutes = summary.OvertimeMinutes, leaveDays = summary.LeaveDays, absentDays = summary.AbsentDays },
				// Every day of the month: the punches where there are any, and the verdict the summary itself reached for that
				// day (so a counted absence is a marked day, not a number with nothing behind it).
				days = Enumerable.Range(1, DateTime.DaysInMonth(y, m)).Select(n => new DateTime(y, m, n)).Select(dt =>
				{
					var rec = days.FirstOrDefault(d => d.WorkDate.Date == dt);
					var verdict = summary != null && summary.Days.TryGetValue(dt, out var v) ? v : rec?.Status;
					return new { date = dt.ToString("yyyy-MM-dd"), checkIn = rec?.CheckIn, checkOut = rec?.CheckOut, status = verdict ?? "",
						lateMinutes = rec?.LateMinutes ?? 0, overtimeMinutes = rec?.OvertimeMinutes ?? 0, workedHours = rec?.WorkedHours ?? 0m, source = rec?.Source };
				}).Where(d => d.status != "" || d.checkIn != null),
			});
		}

		public sealed class PunchDto { public double Latitude { get; set; } public double Longitude { get; set; } public double? Accuracy { get; set; } }

		// POST /api/ess/punch — a punch from the phone, inside the office ("بصمة الآن · داخل نطاق المكتب").
		// The phone reports where it is; the SERVER measures the distance to the employee's branch office and refuses a
		// punch outside its radius, or at a branch with no location set (HR → Punch locations). The first punch of the
		// day is the check-in, any later one the check-out (the latest wins); both go through AttendanceService.RecordAsync,
		// the same arithmetic as a device import or an HR entry, with source "Mobile". The phone's own accuracy widens the
		// radius by at most 50 m, so a coarse fix cannot stretch the office across town.
		[HttpPost("punch")]
		public async Task<IActionResult> Punch([FromBody] PunchDto dto)
		{
			var me = await MeAsync(); if (me == null || dto == null) return NotFound(NotAuthorised);
			bool may = await _hrAccess.CanAsync(me, HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(me.EmployeeId!.Value, me.CompanyId), HttpContext.RequestAborted);
			if (!may) return NotFound(NotAuthorised);
			bool ar = Request.Headers["Accept-Language"].ToString().StartsWith("ar");
			if (dto.Latitude is < -90 or > 90 || dto.Longitude is < -180 or > 180) return BadRequest(new { success = false, message = ar ? "موقع غير صالح" : "Invalid location" });
			var branchId = await _db.Employee.AsNoTracking().Where(e => e.ID == me.EmployeeId.Value).Select(e => e.BranchID).FirstOrDefaultAsync();
			var br = branchId == null ? null : await _db.Branches.AsNoTracking().Where(b => b.ID == branchId && b.CompanyID == me.CompanyId)
				.Select(b => new { b.Latitude, b.Longitude, b.PunchRadiusMeters }).FirstOrDefaultAsync();
			if (br?.Latitude == null || br.Longitude == null || br.PunchRadiusMeters == null)
				return BadRequest(new { success = false, message = ar ? "لم تحدد الموارد البشرية موقع مكتب فرعك بعد؛ البصمة من الجوال غير متاحة." : "HR has not set your branch's office location yet; phone punches are not available." });
			var meters = Haversine((double)br.Latitude.Value, (double)br.Longitude.Value, dto.Latitude, dto.Longitude);
			var allowed = br.PunchRadiusMeters.Value + Math.Min(Math.Max(dto.Accuracy ?? 0, 0), 50);
			if (meters > allowed)
				return BadRequest(new { success = false, distance = Math.Round(meters), message = ar ? $"أنت على بعد {Math.Round(meters):N0} متر من المكتب؛ البصمة داخل {br.PunchRadiusMeters} متر فقط." : $"You are {Math.Round(meters):N0} m from the office; punches are accepted within {br.PunchRadiusMeters} m." });

			var now = DateTime.Now;   // the server's local clock, as the device import and HR entry use
			var today = now.Date;
			var rec = await _db.AttendanceRecords.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyID == me.CompanyId && r.EmployeeID == me.EmployeeId.Value && r.WorkDate == today);
			bool isIn = rec?.CheckIn == null;
			var (ok, err, saved) = await _attendance.RecordAsync(me.CompanyId, me.EmployeeId.Value, today, isIn ? now : rec!.CheckIn, isIn ? null : now, "Mobile", rec?.Notes, User?.Identity?.Name);
			if (!ok || saved == null) return BadRequest(new { success = false, message = err });
			return Ok(new { success = true, kind = isIn ? "in" : "out", at = now, checkIn = saved.CheckIn, checkOut = saved.CheckOut, distance = Math.Round(meters), status = saved.Status });
		}

		private static double Haversine(double lat1, double lon1, double lat2, double lon2)
		{
			const double R = 6371000;   // metres
			double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
			double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
			return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
		}

		// ---------------- my requests (letters, permissions, advances) ----------------
		// GET /api/ess/requests
		[HttpGet("requests")]
		public async Task<IActionResult> MyRequests()
		{
			var me = await MeAsync(); if (me == null) return NotFound(NotAuthorised);
			var rows = await _db.EmployeeRequests.AsNoTracking().Where(r => r.EmployeeID == me.EmployeeId && r.CompanyID == me.CompanyId)
				.OrderByDescending(r => r.ID).Take(100)
				.Select(r => new { id = r.ID, type = r.RequestType, letterType = r.LetterType, addressee = r.Addressee, amount = r.AdvanceAmount, instalment = r.AdvanceInstallment,
					status = r.Status, currentLevel = r.CurrentLevel, createdAt = r.CreatedAt }).ToListAsync();
			return Ok(new { success = true, currency = await CurrencyAsync(me.CompanyId), data = rows });   // an advance is an amount: say in what
		}

		public sealed class CreateRequestDto
		{
			public string RequestType { get; set; } = "Letter";   // Letter | Permission | Advance | ProfileChange
			public string? LetterType { get; set; }
			public string? Addressee { get; set; }
			public DateTime? PermissionDate { get; set; }
			public string? FromTime { get; set; }
			public string? ToTime { get; set; }
			public decimal? AdvanceAmount { get; set; }
			public decimal? AdvanceInstallment { get; set; }
			public string? Reason { get; set; }
			/// ProfileChange only: field → new value (PhoneNumber, Address, EmergencyContact*, BankName, Iban). The service
			/// keeps only its own self-service fields and applies them when the request is approved (HR9).
			public Dictionary<string, string?>? Changes { get; set; }
		}

		// POST /api/ess/requests — raised FOR the caller (employee-request is self-only), routed by the approval path
		[HttpPost("requests")]
		public async Task<IActionResult> CreateRequest([FromBody] CreateRequestDto dto)
		{
			var me = await MeAsync(); if (me == null || dto == null) return NotFound(NotAuthorised);
			bool may = await _hrAccess.CanAsync(me, HrActions.EmployeeRequest, PermissionTarget.ForSubjectEmployee(me.EmployeeId!.Value, me.CompanyId), HttpContext.RequestAborted);
			if (!may) return NotFound(NotAuthorised);
			var type = dto.RequestType is "Permission" or "Advance" or "ProfileChange" ? dto.RequestType : "Letter";
			var draft = new EmployeeRequest
			{
				CompanyID = me.CompanyId, EmployeeID = me.EmployeeId.Value, RequestType = type,
				LetterType = type == "Letter" ? dto.LetterType : null, Addressee = type == "Letter" ? dto.Addressee : null,
				PermissionDate = type == "Permission" ? dto.PermissionDate : null,
				FromTime = type == "Permission" && TimeSpan.TryParse(dto.FromTime, out var ft) ? ft : null,
				ToTime = type == "Permission" && TimeSpan.TryParse(dto.ToTime, out var tt) ? tt : null,
				AdvanceAmount = type == "Advance" ? dto.AdvanceAmount : null, AdvanceInstallment = type == "Advance" ? dto.AdvanceInstallment : null,
				Reason = dto.Reason,
				ChangesJson = type == "ProfileChange" && dto.Changes != null ? System.Text.Json.JsonSerializer.Serialize(dto.Changes) : null,
			};
			var (ok, error, req) = await _requests.CreateAsync(draft);
			if (!ok) return BadRequest(new { success = false, message = error });
			return Ok(new { success = true, data = new { id = req!.ID, status = req.Status, currentLevel = req.CurrentLevel } });
		}

		// ---------------- ONE approvals inbox ----------------
		// GET /api/ess/approvals — every leave and employee request whose CURRENT step is mine
		[HttpGet("approvals")]
		public async Task<IActionResult> Approvals()
		{
			var me = await MeAsync(); if (me == null) return NotFound(NotAuthorised);
			int mine = me.EmployeeId!.Value;
			var company = _db.Employee.Where(e => e.EmpCompanyID == me.CompanyId).Select(e => e.ID);
			var leaves = await _db.LeaveRequests.AsNoTracking().Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == mine && company.Contains(r.EmployeeID))
				.Select(r => new { kind = "leave", id = r.ID, type = r.LeaveType != null ? r.LeaveType.NameEn : null, typeAr = r.LeaveType != null ? r.LeaveType.NameAr : null,
					requesterId = r.EmployeeID, requesterAr = r.Employee!.FullName, requesterEn = r.Employee.FullNameEn,
					from = (DateTime?)r.StartDate, to = (DateTime?)r.EndDate, days = (int?)r.Days, amount = (decimal?)null, level = r.CurrentLevel, createdAt = r.CreatedAt,
					deptId = r.Employee.DepartmentID, typeId = (int?)r.LeaveTypeID, substituteId = r.SubstituteEmployeeID }).ToListAsync();
			var reqs = await _db.EmployeeRequests.AsNoTracking().Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == mine && r.CompanyID == me.CompanyId)
				.Join(_db.Employee.AsNoTracking(), r => r.EmployeeID, e => e.ID, (r, e) => new { kind = "request", id = r.ID, type = r.RequestType, typeAr = (string?)null,
					requesterId = r.EmployeeID, requesterAr = e.FullName, requesterEn = e.FullNameEn,
					from = r.PermissionDate, to = r.PermissionDate, days = (int?)null, amount = r.AdvanceAmount, level = r.CurrentLevel, createdAt = r.CreatedAt,
					deptId = e.DepartmentID, typeId = (int?)null, substituteId = (int?)null }).ToListAsync();
			var items = leaves.Concat(reqs).OrderBy(x => x.createdAt).ToList();

			// what the reference's card and detail show beside the request: the requester's department, the next step of the
			// chain after mine, and for a leave the balance now and after it, the substitute and who else in the department
			// is away on those days
			var deptIds = items.Where(x => x.deptId != null).Select(x => x.deptId!.Value).Distinct().ToList();
			var depts = await _db.Hierarchicals.AsNoTracking().IgnoreQueryFilters().Where(h => deptIds.Contains(h.H_ID))
				// the id is read from an employee I am already authorised to see, and only the node's NAME is shown (not the tree),
				// so the HR company filter is bypassed for this label lookup; in correct data the node is in the employee's own company anyway
				.Select(h => new { h.H_ID, h.H_Name, h.H_NameEn }).ToDictionaryAsync(h => h.H_ID);
			var leaveIds = leaves.Select(x => x.id).ToList(); var reqIds = reqs.Select(x => x.id).ToList();
			var leaveNext = await _db.LeaveApprovalSteps.AsNoTracking().Where(s => leaveIds.Contains(s.LeaveRequestID)).Select(s => new { id = s.LeaveRequestID, s.Level, s.ApproverEmployeeID }).ToListAsync();
			var reqNext = await _db.EmployeeRequestSteps.AsNoTracking().Where(s => reqIds.Contains(s.EmployeeRequestID)).Select(s => new { id = s.EmployeeRequestID, s.Level, s.ApproverEmployeeID }).ToListAsync();
			var peopleIds = leaveNext.Select(s => s.ApproverEmployeeID).Concat(reqNext.Select(s => s.ApproverEmployeeID))
				.Concat(items.Where(x => x.substituteId != null).Select(x => x.substituteId!.Value)).Append(mine).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => peopleIds.Contains(e.ID) && e.EmpCompanyID == me.CompanyId)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, titleAr = e.JobTitle != null ? e.JobTitle.TitleAr : null, titleEn = e.JobTitle != null ? e.JobTitle.Title : null })
				.ToDictionaryAsync(e => e.ID);
			var myTitle = people.TryGetValue(mine, out var meRow) ? meRow : null;

			var rows = new List<object>();
			foreach (var x in items)
			{
				var steps = x.kind == "leave" ? leaveNext.Where(s => s.id == x.id).Select(s => (s.Level, s.ApproverEmployeeID)) : reqNext.Where(s => s.id == x.id).Select(s => (s.Level, s.ApproverEmployeeID));
				var nextStep = steps.Where(s => s.Level > x.level).OrderBy(s => s.Level).Select(s => (int?)s.ApproverEmployeeID).FirstOrDefault();
				var next = nextStep is int nid && people.TryGetValue(nid, out var np) ? np : null;
				var dept = x.deptId is int d && depts.TryGetValue(d, out var dr) ? dr : null;
				int? remaining = null; List<string>? awayAr = null, awayEn = null; int away = 0;
				if (x.kind == "leave" && x.typeId is int tid)
				{
					var bal = (await _dashboard.BuildAsync(x.requesterId)).Balances.FirstOrDefault(b => b.LeaveTypeId == tid);
					remaining = bal?.Remaining;
					if (x.deptId is int dep && x.from is DateTime f && x.to is DateTime t)
					{
						var others = await _db.LeaveRequests.AsNoTracking()
							.Where(r => (r.Status == 0 || r.Status == 1) && r.EmployeeID != x.requesterId && r.StartDate <= t && r.EndDate >= f
								&& r.Employee!.DepartmentID == dep && r.Employee.EmpCompanyID == me.CompanyId)
							.Select(r => new { r.EmployeeID, r.Employee!.FullName, r.Employee.FullNameEn }).Distinct().ToListAsync();
						away = others.Count;
						awayAr = others.Take(3).Select(o => o.FullName).ToList();
						awayEn = others.Take(3).Select(o => o.FullNameEn ?? o.FullName).ToList();
					}
				}
				var sub = x.substituteId is int sid && people.TryGetValue(sid, out var sp) ? sp : null;
				rows.Add(new
				{
					x.kind, x.id, x.type, x.typeAr, x.requesterId, x.requesterAr, x.requesterEn, x.from, x.to, x.days, x.amount, x.level, x.createdAt,
					photo = $"/api/ess/photo/{x.requesterId}",
					deptAr = dept?.H_Name, deptEn = dept?.H_NameEn ?? dept?.H_Name,
					remaining, after = remaining is int rem && x.days is int dd ? rem - dd : (int?)null,
					substituteAr = sub?.FullName, substituteEn = sub?.FullNameEn ?? sub?.FullName,
					away, awayAr, awayEn,
					nextAr = next?.FullName, nextEn = next?.FullNameEn ?? next?.FullName, nextTitleAr = next?.titleAr?.Trim(), nextTitleEn = (next?.titleEn ?? next?.titleAr)?.Trim(),
					myTitleAr = myTitle?.titleAr?.Trim(), myTitleEn = (myTitle?.titleEn ?? myTitle?.titleAr)?.Trim(),
				});
			}
			return Ok(new { success = true, currency = await CurrencyAsync(me.CompanyId), data = rows });
		}

		public sealed class ReturnDto { public string? Note { get; set; } }

		// POST /api/ess/approvals/{kind}/{id}/return — "إعادة للمراجعة": the request goes back to its requester to amend and
		// send again (Status 3, the note says what to review). The SAME gate as deciding: leave-approve on the subject, or
		// it is my turn in the stored chain.
		[HttpPost("approvals/{kind}/{id:int}/return")]
		public async Task<IActionResult> ReturnForReview(string kind, int id, [FromBody] ReturnDto dto)
		{
			var me = await MeAsync(); if (me == null) return NotFound(NotAuthorised);
			bool leave = kind == "leave";
			if (!leave && kind != "request") return NotFound(NotAuthorised);
			int? subject = leave
				? await _db.LeaveRequests.AsNoTracking().Where(r => r.ID == id).Select(r => (int?)r.EmployeeID).FirstOrDefaultAsync(HttpContext.RequestAborted)
				: await _db.EmployeeRequests.AsNoTracking().Where(r => r.ID == id).Select(r => (int?)r.EmployeeID).FirstOrDefaultAsync(HttpContext.RequestAborted);
			if (subject == null) return NotFound(NotAuthorised);
			bool mayDecide = await _hrAccess.CanAsync(me, HrActions.LeaveApprove, PermissionTarget.ForSubjectEmployee(subject.Value, me.CompanyId), HttpContext.RequestAborted)
				|| (leave ? await CrossBuy.BL.Hr.HrApprovalTurn.HoldsLeaveTurnAsync(_db, id, me.EmployeeId!.Value, me.CompanyId, HttpContext.RequestAborted)
						  : await CrossBuy.BL.Hr.HrApprovalTurn.HoldsRequestTurnAsync(_db, id, me.EmployeeId!.Value, me.CompanyId, HttpContext.RequestAborted));
			if (!mayDecide) return NotFound(NotAuthorised);
			var (ok, error) = leave ? await _leave.ReturnAsync(id, me.EmployeeId!.Value, dto?.Note) : await _requests.ReturnAsync(id, me.EmployeeId!.Value, dto?.Note);
			return ok ? Ok(new { success = true }) : BadRequest(new { success = false, message = error });
		}

		// GET /api/ess/photo/{employeeId} — a colleague's photo for the phone, which signs in with a token, not the browser's
		// cookie that People/Photo needs. The same rule: any member of the employee's own company; a foreign or missing
		// employee and one without a photo answer the same bare 404.
		[HttpGet("photo/{employeeId:int}")]
		public async Task<IActionResult> Photo(int employeeId)
		{
			var me = await MeAsync(); if (me == null || employeeId <= 0) return NotFound();
			var store = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<CrossBuy.BL.Hr.HrFileStore>(HttpContext.RequestServices);
			var photo = await store.OpenPhotoAsync(me.CompanyId, employeeId, HttpContext.RequestAborted);
			if (photo is not { } p) return NotFound();
			Response.Headers.CacheControl = "private, max-age=86400";
			return File(p.Content, p.ContentType);
		}

		public sealed class DecisionDto { public bool? Approve { get; set; } public string? Note { get; set; } }

		// POST /api/ess/requests/{id}/decision — the browser's DecideRequest gate, on the API
		[HttpPost("requests/{id:int}/decision")]
		public async Task<IActionResult> DecideRequest(int id, [FromBody] DecisionDto dto)
		{
			var me = await MeAsync(); if (me == null) return NotFound(NotAuthorised);
			if (dto?.Approve is not bool approve) return BadRequest(new { success = false, message = "Invalid decision" });
			int? subject = await _db.EmployeeRequests.AsNoTracking().Where(r => r.ID == id).Select(r => (int?)r.EmployeeID).FirstOrDefaultAsync(HttpContext.RequestAborted);
			if (subject == null) return NotFound(NotAuthorised);
			bool mayDecide = await _hrAccess.CanAsync(me, HrActions.LeaveApprove, PermissionTarget.ForSubjectEmployee(subject.Value, me.CompanyId), HttpContext.RequestAborted)
				|| await CrossBuy.BL.Hr.HrApprovalTurn.HoldsRequestTurnAsync(_db, id, me.EmployeeId!.Value, me.CompanyId, HttpContext.RequestAborted);
			if (!mayDecide) return NotFound(NotAuthorised);
			var (ok, error) = await _requests.DecideAsync(id, me.EmployeeId!.Value, approve, dto.Note);
			return ok ? Ok(new { success = true }) : BadRequest(new { success = false, message = error });
		}
	}
}
