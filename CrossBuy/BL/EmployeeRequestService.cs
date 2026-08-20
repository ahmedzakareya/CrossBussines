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

		public async Task<(bool ok, string? error, EmployeeRequest? req)> CreateAsync(EmployeeRequest draft)
		{
			if (draft.EmployeeID <= 0) return (false, "الموظف مطلوب", null);
			if (draft.RequestType == "Permission")
			{
				if (draft.PermissionDate == null || draft.FromTime == null || draft.ToTime == null)
					return (false, "التاريخ ووقت البداية/النهاية مطلوبة للإذن", null);
				if (draft.ToTime <= draft.FromTime) return (false, "وقت النهاية يجب أن يكون بعد البداية", null);
			}
			else
			{
				draft.RequestType = "Letter";
				if (string.IsNullOrWhiteSpace(draft.LetterType)) return (false, "نوع الخطاب مطلوب", null);
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
				return (false, "لا يمكن تحديد سلسلة الموافقة لهذا الموظف — الهيكل التنظيمي غير صحيح. راجع إدارة الموارد البشرية.", null);
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
					$"طلب {Label(draft, true)} جديد", $"New {Label(draft, false)} request",
					$"قدّم {rAr} {Label(draft, true)} بانتظار اعتمادك.",
					$"{rEn} submitted a {Label(draft, false)} request awaiting your approval.",
					"emp_request_submitted", draft.ID);
			}
			return (true, null, draft);
		}

		public async Task<(bool ok, string? error)> DecideAsync(int requestId, int approverEmployeeId, bool approve, string? note)
		{
			var req = await _context.EmployeeRequests.Include(r => r.ApprovalSteps).FirstOrDefaultAsync(r => r.ID == requestId);
			if (req == null) return (false, "الطلب غير موجود");
			if (req.Status != 0) return (false, "تم البتّ في الطلب مسبقًا");
			if (req.CurrentApproverEmployeeID != approverEmployeeId) return (false, "لا تملك صلاحية اعتماد هذا الطلب");
			var step = req.ApprovalSteps.FirstOrDefault(s => s.Level == req.CurrentLevel && s.Status == 0);
			if (step == null) return (false, "خطوة الاعتماد غير موجودة");

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
