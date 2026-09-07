using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// HR-8 — ESS self-service requests (letters + hourly permissions). Reuses the leave approval
	// chain (ManagerChainAsync) and notification patterns. No GL impact.
	public interface IEmployeeRequestService
	{
		Task<(bool ok, string? error, EmployeeRequest? req)> CreateAsync(EmployeeRequest draft);
		Task<(bool ok, string? error)> DecideAsync(int requestId, int approverEmployeeId, bool approve, string? note);
		/// Latest payslip figures for the salary certificate (net/gross/base), or null if none posted yet.
		Task<Payslip?> LatestPayslipAsync(int employeeId);

		/// Pending employee requests awaiting this approver, in the context's company. READ ONLY - the
		/// approval inbox's module reader (SHF-15), not part of the workflow.
		Task<IReadOnlyList<CrossBuy.BL.Approvals.PendingApprovalRow>> PendingForApproverAsync(
			CrossBuy.Models.Platform.BusinessContext context, int approverEmployeeId,
			CancellationToken cancellationToken = default);
	}

	public class EmployeeRequestService : IEmployeeRequestService
	{
		private readonly CrossDbContext _context;
		private readonly INotificationService _notifications;
		private readonly ILeaveWorkflowService _chain;   // reuse ManagerChainAsync

		public EmployeeRequestService(CrossDbContext context, INotificationService notifications, ILeaveWorkflowService chain)
		{ _context = context; _notifications = notifications; _chain = chain; }

		public Task<Payslip?> LatestPayslipAsync(int employeeId) =>
			_context.Payslips.AsNoTracking().Where(s => s.EmployeeID == employeeId)
				.OrderByDescending(s => s.Year).ThenByDescending(s => s.Month).FirstOrDefaultAsync();

		private static string Label(EmployeeRequest r, bool ar)
		{
			if (r.RequestType == "Permission") return ar ? "إذن" : "permission";
			return ar ? "خطاب" : "letter";
		}

		// =============================================================================================
		// THE APPROVAL INBOX'S EMPLOYEE-REQUEST READER (SHF-15 narrow read handoff).
		//
		// A READ. CreateAsync, DecideAsync, the workflow transitions, the approval writes and the
		// notification logic are all untouched.
		//
		// EmployeeRequest DOES carry CompanyID - written from the requester's own Employee.EmpCompanyID - so
		// the boundary is stated directly on the row rather than inferred through a join. The approver match
		// alone would not be isolation: Employee.ID is a global primary key, so it rules out an id COLLISION
		// across companies but holds only as long as approver assignment is correct.
		//
		// FAIL CLOSED: no company or no approver yields an empty list.
		// =============================================================================================
		public async Task<IReadOnlyList<CrossBuy.BL.Approvals.PendingApprovalRow>> PendingForApproverAsync(
			CrossBuy.Models.Platform.BusinessContext context, int approverEmployeeId,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);

			var empty = Array.Empty<CrossBuy.BL.Approvals.PendingApprovalRow>();
			if (context.CompanyId <= 0 || approverEmployeeId <= 0) return empty;

			var rows = await _context.EmployeeRequests.AsNoTracking()
				.Where(r => r.Status == 0
					&& r.CurrentApproverEmployeeID == approverEmployeeId
					&& r.CompanyID == context.CompanyId)
				.OrderByDescending(r => r.ID)
				.ToListAsync(cancellationToken);

			return rows.Select(r => new CrossBuy.BL.Approvals.PendingApprovalRow
			{
				Silo = CrossBuy.BL.Approvals.ApprovalSilos.Request,
				EntityId = r.ID,
				ApprovalType = r.RequestType,
				// The title is the request KIND, which the inbox localizes ("Letter" / "Permission"). The
				// discriminator travels in ApprovalType; no rendered string is invented here.
				RequesterEmployeeId = r.EmployeeID,
				SubmittedAt = r.CreatedAt,
				Status = "Pending",
				OnDate = r.PermissionDate,
				FromTime = r.FromTime,
				ToTime = r.ToTime,
				TitleEn = r.LetterType,
				Navigation = new CrossBuy.BL.Approvals.ApprovalNavigationTarget("People", "Requests"),
			}).ToList();
		}

		public async Task<(bool ok, string? error, EmployeeRequest? req)> CreateAsync(EmployeeRequest draft)
		{
			if (draft.EmployeeID <= 0) return (false, "Employee is required", null);
			if (draft.RequestType == "Permission")
			{
				if (draft.PermissionDate == null || draft.FromTime == null || draft.ToTime == null)
					return (false, "The date and the start/end time are required for a permission request", null);
				if (draft.ToTime <= draft.FromTime) return (false, "The end time must be after the start time", null);
			}
			else
			{
				draft.RequestType = "Letter";
				if (string.IsNullOrWhiteSpace(draft.LetterType)) return (false, "Letter type is required", null);
			}

			// Approver selection is SHARED with LeaveWorkflowService, so the company boundary that Batch C.1
			// added to the hierarchy climb applies here too — and so does its trap. `chain.Count == 0` below
			// means "the requester is at the top of the tree" and AUTO-APPROVES the request. Once the climb
			// intersects with the requester's company, a cross-company graft ALSO empties the chain, so
			// without this guard closing the isolation leak would silently approve employee requests that
			// have no approver at all. Same refusal as the leave path, for the same reason.
			var chainResult = await _chain.ApproverChainAsync(draft.EmployeeID);
			var chain = chainResult.Approvers;
			if (chain.Count == 0 && chainResult.HierarchyDefect)
				return (false, "The approval chain for this employee cannot be determined — the organisation structure is not correct. Please contact HR.", null);
			draft.CreatedAt = DateTime.UtcNow;
			draft.CreatedBy = draft.EmployeeID;
			if (chain.Count == 0)
			{
				draft.Status = 1; draft.CurrentLevel = 0; draft.CurrentApproverEmployeeID = null; draft.DecisionAt = DateTime.UtcNow;
			}
			else
			{
				draft.Status = 0; draft.CurrentLevel = 1; draft.CurrentApproverEmployeeID = chain[0];
			}
			_context.EmployeeRequests.Add(draft);
			await _context.SaveChangesAsync();

			if (chain.Count > 0)
			{
				for (var i = 0; i < chain.Count; i++)
					_context.EmployeeRequestSteps.Add(new EmployeeRequestStep
					{
						EmployeeRequestID = draft.ID, Level = i + 1, ApproverEmployeeID = chain[i], Status = 0,
						CreatedAt = DateTime.UtcNow, CreatedBy = draft.EmployeeID
					});
				await _context.SaveChangesAsync();

				var requester = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == draft.EmployeeID);
				var rAr = requester?.FullName ?? "موظف";
				var rEn = requester?.FullNameEn ?? requester?.FullName ?? "An employee";
				await _notifications.NotifyAsync(chain[0],
					$"New {Label(draft, true)} request", $"New {Label(draft, false)} request",
					$"قدّم {rAr} {Label(draft, true)} بانتظار اعتمادك.",
					$"{rEn} submitted a {Label(draft, false)} request awaiting your approval.",
					"emp_request_submitted", draft.ID);
			}
			return (true, null, draft);
		}

		public async Task<(bool ok, string? error)> DecideAsync(int requestId, int approverEmployeeId, bool approve, string? note)
		{
			var req = await _context.EmployeeRequests.Include(r => r.ApprovalSteps).FirstOrDefaultAsync(r => r.ID == requestId);
			if (req == null) return (false, "Order not found");
			if (req.Status != 0) return (false, "The request has already been decided");
			if (req.CurrentApproverEmployeeID != approverEmployeeId) return (false, "You do not have permission to approve this request");
			var step = req.ApprovalSteps.FirstOrDefault(s => s.Level == req.CurrentLevel && s.Status == 0);
			if (step == null) return (false, "Approval step not found");

			var approver = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == approverEmployeeId);
			var aAr = approver?.FullName ?? "المدير";
			var aEn = approver?.FullNameEn ?? approver?.FullName ?? "Manager";
			var lblAr = Label(req, true); var lblEn = Label(req, false);
			var now = DateTime.UtcNow;

			if (!approve)
			{
				step.Status = 2; step.DecisionAt = now; step.DecisionNote = note; step.UpdatedAt = now; step.updatedBy = approverEmployeeId;
				req.Status = 2; req.ApproverEmployeeID = approverEmployeeId; req.DecisionAt = now; req.DecisionNote = note;
				req.CurrentApproverEmployeeID = null; req.UpdatedAt = now; req.updatedBy = approverEmployeeId;
				await _context.SaveChangesAsync();
				await _notifications.NotifyAsync(req.EmployeeID, $"تم رفض {lblAr}", $"Your {lblEn} was rejected",
					$"تم رفض طلب {lblAr} بواسطة {aAr}.", $"Your {lblEn} request was rejected by {aEn}.", "emp_request_rejected", req.ID);
				return (true, null);
			}

			var maxLevel = req.ApprovalSteps.Count == 0 ? 0 : req.ApprovalSteps.Max(s => s.Level);
			if (req.CurrentLevel >= maxLevel)
			{
				step.Status = 1; step.DecisionAt = now; step.DecisionNote = note; step.UpdatedAt = now; step.updatedBy = approverEmployeeId;
				req.Status = 1; req.ApproverEmployeeID = approverEmployeeId; req.DecisionAt = now; req.DecisionNote = note;
				req.CurrentApproverEmployeeID = null; req.CurrentLevel = 0; req.UpdatedAt = now; req.updatedBy = approverEmployeeId;
				await _context.SaveChangesAsync();
				await _notifications.NotifyAsync(req.EmployeeID, $"تمت الموافقة على {lblAr}", $"Your {lblEn} was approved",
					$"تمت الموافقة على طلب {lblAr} نهائيًا بواسطة {aAr}.", $"Your {lblEn} request was fully approved by {aEn}.", "emp_request_approved", req.ID);
				return (true, null);
			}

			step.Status = 1; step.DecisionAt = now; step.DecisionNote = note; step.UpdatedAt = now; step.updatedBy = approverEmployeeId;
			var nextLevel = req.CurrentLevel + 1;
			var nextStep = req.ApprovalSteps.First(s => s.Level == nextLevel);
			req.CurrentLevel = nextLevel; req.CurrentApproverEmployeeID = nextStep.ApproverEmployeeID;
			req.UpdatedAt = now; req.updatedBy = approverEmployeeId;
			await _context.SaveChangesAsync();
			await _notifications.NotifyAsync(nextStep.ApproverEmployeeID,
				$"طلب {lblAr} بانتظار اعتمادك", $"A {lblEn} request awaiting your approval",
				$"طلب {lblAr} بانتظار اعتمادك.", $"A {lblEn} request is awaiting your approval.", "emp_request_submitted", req.ID);
			return (true, null);
		}
	}
}
