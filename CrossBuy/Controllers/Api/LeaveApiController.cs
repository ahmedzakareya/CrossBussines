using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	[ApiController]
	[Route("api/leave")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class LeaveApiController : ControllerBase
	{
		private readonly IEmployeeService _employeeService;
		private readonly CrossDbContext _context;
		private readonly ILeaveDashboardService _dashboard;
		private readonly ILeaveWorkflowService _workflow;
		private readonly CrossBuy.BL.IHrAccessService _hrAccess;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _businessContexts;

		public LeaveApiController(CrossBuy.BL.IHrAccessService hrAccess,
			CrossBuy.BL.Platform.IBusinessContextAccessor businessContexts,
			IEmployeeService employeeService, CrossDbContext context,
			ILeaveDashboardService dashboard, ILeaveWorkflowService workflow)
		{
			_hrAccess = hrAccess;
			_businessContexts = businessContexts;
			_employeeService = employeeService;
			_context = context;
			_dashboard = dashboard;
			_workflow = workflow;
		}

		private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

		private static readonly string[] StatusAr = { "معلّق", "موافَق عليه", "مرفوض" };
		private static readonly string[] StatusEn = { "Pending", "Approved", "Rejected" };

		// names of all approvers referenced by the given requests' steps
		private async Task<Dictionary<int, (string? ar, string? en)>> ApproverNamesAsync(IEnumerable<LeaveRequest> reqs)
		{
			var ids = reqs.SelectMany(r => r.ApprovalSteps.Select(s => s.ApproverEmployeeID)).Distinct().ToList();
			if (ids.Count == 0) return new();
			var rows = await _context.Employee.AsNoTracking()
				.Where(e => ids.Contains(e.ID))
				.Select(e => new { e.ID, e.FullName, e.FullNameEn })
				.ToListAsync();
			return rows.ToDictionary(x => x.ID, x => (ar: (string?)x.FullName, en: (string?)x.FullNameEn));
		}

		private object MapRequest(LeaveRequest r, Dictionary<int, (string? ar, string? en)>? names = null,
			string? empAr = null, string? empEn = null) => new
		{
			id = r.ID,
			employeeId = r.EmployeeID,
			employeeNameAr = empAr,
			employeeNameEn = empEn,
			leaveTypeId = r.LeaveTypeID,
			leaveTypeAr = r.LeaveType?.NameAr,
			leaveTypeEn = r.LeaveType?.NameEn,
			startDate = r.StartDate,
			endDate = r.EndDate,
			days = r.Days,
			reason = r.Reason,
			status = r.Status,
			statusAr = r.Status >= 0 && r.Status < StatusAr.Length ? StatusAr[r.Status] : null,
			statusEn = r.Status >= 0 && r.Status < StatusEn.Length ? StatusEn[r.Status] : null,
			decisionAt = r.DecisionAt,
			decisionNote = r.DecisionNote,
			createdAt = r.CreatedAt,
			// multi-level approval chain
			currentLevel = r.CurrentLevel,
			totalLevels = r.ApprovalSteps?.Count ?? 0,
			approvals = (r.ApprovalSteps ?? new List<LeaveApprovalStep>())
				.OrderBy(s => s.Level)
				.Select(s => new
				{
					level = s.Level,
					status = s.Status,
					statusAr = s.Status >= 0 && s.Status < StatusAr.Length ? StatusAr[s.Status] : null,
					statusEn = s.Status >= 0 && s.Status < StatusEn.Length ? StatusEn[s.Status] : null,
					approverAr = names != null && names.TryGetValue(s.ApproverEmployeeID, out var n) ? n.ar : null,
					approverEn = names != null && names.TryGetValue(s.ApproverEmployeeID, out var n2) ? n2.en : null,
					decisionAt = s.DecisionAt,
					note = s.DecisionNote,
				}).ToList()
		};

		// GET /api/leave/types — leave types for the request form dropdown
		[HttpGet("types")]
		public async Task<IActionResult> Types()
		{
			var types = await _context.LeaveTypes.AsNoTracking()
				.Select(t => new { id = t.ID, nameAr = t.NameAr, nameEn = t.NameEn })
				.ToListAsync();
			return Ok(new { success = true, data = types });
		}

		// GET /api/leave/workdays — which weekdays are work days for the current employee
		// days[] indexed by DayOfWeek (0=Sunday … 6=Saturday). restricted=false means any day allowed.
		[HttpGet("workdays")]
		public async Task<IActionResult> WorkDays()
		{
			var emp = await _employeeService.GetEmployeeByUserIdAsync(CurrentUserId ?? "");
			if (emp == null) return NotFound(new { success = false, message = "الموظف غير موجود" });

			var flags = await _dashboard.WorkDayFlagsAsync(emp.ID);
			if (flags == null)
				return Ok(new { success = true, restricted = false, days = new[] { true, true, true, true, true, true, true } });

			return Ok(new { success = true, restricted = !flags.All(f => f), days = flags });
		}

		// GET /api/leave/my — my own leave requests (newest first)
		[HttpGet("my")]
		public async Task<IActionResult> My()
		{
			var emp = await _employeeService.GetEmployeeByUserIdAsync(CurrentUserId ?? "");
			if (emp == null) return NotFound(new { success = false, message = "الموظف غير موجود" });

			var list = await _context.LeaveRequests.AsNoTracking()
				.Include(r => r.LeaveType)
				.Include(r => r.ApprovalSteps)
				.Where(r => r.EmployeeID == emp.ID)
				.OrderByDescending(r => r.ID)
				.ToListAsync();

			var names = await ApproverNamesAsync(list);
			return Ok(new { success = true, data = list.Select(r => MapRequest(r, names)) });
		}

		// POST /api/leave — create a new leave request (builds the multi-level approval chain)
		[HttpPost]
		public async Task<IActionResult> Create([FromBody] CreateLeaveDto dto)
		{
			var emp = await _employeeService.GetEmployeeByUserIdAsync(CurrentUserId ?? "");
			if (emp == null) return NotFound(new { success = false, message = "الموظف غير موجود" });
			if (dto == null) return BadRequest(new { success = false, message = "بيانات غير صحيحة" });

			// SELF-SERVICE, on the API path. `employee-request` is self-only by construction in
			// HrAccessService — no role widens it and a null subject refuses — so the subject is taken
			// from the resolved BusinessContext and the DTO carries no employee identifier at all. There
			// is nothing on CreateLeaveDto a caller could use to raise leave for somebody else, and that
			// absence is the design rather than an omission.
			var ctx = await _businessContexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (ctx is not { CompanyId: > 0 } || ctx.EmployeeId is not > 0)
				return NotFound(new { success = false, message = "غير مصرح" });

			bool maySubmit = await _hrAccess.CanAsync(
				ctx, CrossBuy.BL.HrActions.EmployeeRequest,
				CrossBuy.Models.Platform.PermissionTarget.ForSubjectEmployee(ctx.EmployeeId.Value, ctx.CompanyId),
				HttpContext.RequestAborted);
			if (!maySubmit) return NotFound(new { success = false, message = "غير مصرح" });

			var (ok, error, req) = await _workflow.CreateAsync(ctx.EmployeeId.Value, dto.LeaveTypeID, dto.StartDate, dto.EndDate, dto.Reason);
			if (!ok) return BadRequest(new { success = false, message = error });

			return Ok(new { success = true, data = new { id = req!.ID, days = req.Days, status = req.Status, currentLevel = req.CurrentLevel } });
		}

		// GET /api/leave/pending — requests awaiting MY approval at the current level
		[HttpGet("pending")]
		public async Task<IActionResult> Pending()
		{
			var emp = await _employeeService.GetEmployeeByUserIdAsync(CurrentUserId ?? "");
			if (emp == null) return NotFound(new { success = false, message = "الموظف غير موجود" });

			var list = await _context.LeaveRequests.AsNoTracking()
				.Include(r => r.LeaveType)
				.Include(r => r.Employee)
				.Include(r => r.ApprovalSteps)
				.Where(r => r.Status == 0 && r.CurrentApproverEmployeeID == emp.ID)
				.OrderByDescending(r => r.ID)
				.ToListAsync();

			var names = await ApproverNamesAsync(list);
			return Ok(new
			{
				success = true,
				data = list.Select(r => MapRequest(r, names, r.Employee?.FullName, r.Employee?.FullNameEn))
			});
		}

		// POST /api/leave/{id}/decision — approve or reject the current level (manager only)
		[HttpPost("{id:int}/decision")]
		public async Task<IActionResult> Decision(int id, [FromBody] DecisionDto dto)
		{
			var emp = await _employeeService.GetEmployeeByUserIdAsync(CurrentUserId ?? "");
			if (emp == null) return NotFound(new { success = false, message = "الموظف غير موجود" });
			if (dto == null || (dto.Approve != true && dto.Approve != false))
				return BadRequest(new { success = false, message = "قرار غير صحيح" });

			// MANAGERIAL APPROVAL, so it gets the approval authority — deliberately NOT the same treatment
			// as Create directly above, which is self-service. `leave-approve` is record-level: it is never
			// granted by a role, and HrAccessService requires the caller to be a manager of the SUBJECT
			// through the company-intersected hierarchy and refuses self-approval. This is the same gate
			// PeopleController.DecideLeave applies to the browser path; the mobile/API path had none.
			var hrContext = await _businessContexts.TryGetCurrentAsync(HttpContext.RequestAborted);
			if (hrContext == null) return NotFound(new { success = false, message = "غير مصرح" });

			// The subject comes from the request ROW. A posted id is only ever a lookup key.
			int? subjectEmployeeId = await _context.LeaveRequests.AsNoTracking()
				.Where(r => r.ID == id).Select(r => (int?)r.EmployeeID)
				.FirstOrDefaultAsync(HttpContext.RequestAborted);

			// A request that does not exist and one this caller may not decide answer identically, so the
			// endpoint cannot be used to probe which request ids exist.
			if (subjectEmployeeId == null) return NotFound(new { success = false, message = "غير مصرح" });

			bool mayDecide = await _hrAccess.CanAsync(
				hrContext, CrossBuy.BL.HrActions.LeaveApprove,
				CrossBuy.Models.Platform.PermissionTarget.ForSubjectEmployee(subjectEmployeeId.Value, hrContext.CompanyId),
				HttpContext.RequestAborted);
			if (!mayDecide) return NotFound(new { success = false, message = "غير مصرح" });

			var (ok, error) = await _workflow.DecideAsync(id, emp.ID, dto.Approve == true, dto.Note);
			if (!ok) return BadRequest(new { success = false, message = error });
			return Ok(new { success = true });
		}

		public class CreateLeaveDto
		{
			public int LeaveTypeID { get; set; }
			public DateTime StartDate { get; set; }
			public DateTime EndDate { get; set; }
			public string? Reason { get; set; }
		}

		public class DecisionDto
		{
			public bool? Approve { get; set; }
			public string? Note { get; set; }
		}
	}
}
