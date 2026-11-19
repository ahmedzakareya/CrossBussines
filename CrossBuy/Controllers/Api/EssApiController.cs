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

		public EssApiController(CrossDbContext db, IHrAccessService hrAccess, CrossBuy.BL.Platform.IBusinessContextAccessor contexts,
			IEmployeeRequestService requests, ILeaveWorkflowService leave, IAttendanceService attendance)
		{ _db = db; _hrAccess = hrAccess; _contexts = contexts; _requests = requests; _leave = leave; _attendance = attendance; }

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
					from = (DateTime?)r.StartDate, to = (DateTime?)r.EndDate, days = (int?)r.Days, amount = (decimal?)null, level = r.CurrentLevel, createdAt = r.CreatedAt }).ToListAsync();
			var reqs = await _db.EmployeeRequests.AsNoTracking().Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == mine && r.CompanyID == me.CompanyId)
				.Join(_db.Employee.AsNoTracking(), r => r.EmployeeID, e => e.ID, (r, e) => new { kind = "request", id = r.ID, type = r.RequestType, typeAr = (string?)null,
					requesterId = r.EmployeeID, requesterAr = e.FullName, requesterEn = e.FullNameEn,
					from = r.PermissionDate, to = r.PermissionDate, days = (int?)null, amount = r.AdvanceAmount, level = r.CurrentLevel, createdAt = r.CreatedAt }).ToListAsync();
			var all = leaves.Concat(reqs).OrderBy(x => x.createdAt).Select(x => new
			{
				x.kind, x.id, x.type, x.typeAr, x.requesterId, x.requesterAr, x.requesterEn, x.from, x.to, x.days, x.amount, x.level, x.createdAt,
				photo = $"/People/Photo/{x.requesterId}",
			});
			return Ok(new { success = true, currency = await CurrencyAsync(me.CompanyId), data = all });
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
