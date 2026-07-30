using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public interface ILeaveWorkflowService
	{
		/// Ordered chain of approver employee IDs from the direct manager up to (and including)
		/// the head of the NEAREST organizational unit (company/branch/administrative body)
		/// the employee belongs to. Level 1 = direct manager, last = the unit head (final approver).
		Task<List<int>> ManagerChainAsync(int employeeId);

		/// Create a leave request, build its approval chain, notify the first approver
		/// (or auto-approve when the requester is at/above the top of the chain).
		Task<(bool ok, string? error, LeaveRequest? req)> CreateAsync(
			int employeeId, int leaveTypeId, DateTime start, DateTime end, string? reason);

		/// Approve/reject the CURRENT level. Approval advances up the chain; rejection ends it.
		Task<(bool ok, string? error)> DecideAsync(int requestId, int approverEmployeeId, bool approve, string? note);
	}

	public class LeaveWorkflowService : ILeaveWorkflowService
	{
		private readonly CrossDbContext _context;
		private readonly INotificationService _notifications;
		private readonly ILeaveDashboardService _dashboard;

		public LeaveWorkflowService(CrossDbContext context, INotificationService notifications, ILeaveDashboardService dashboard)
		{
			_context = context;
			_notifications = notifications;
			_dashboard = dashboard;
		}

		// the position/seat title of an employee in the org tree (e.g., "رئيس قسم", "مدير الفرع")
		private async Task<(string ar, string en)> PositionTitleAsync(int employeeId)
		{
			var node = await _context.Hierarchicals.AsNoTracking()
				.FirstOrDefaultAsync(h => h.H_Type == 5 && h.H_ObjectID == employeeId);
			if (node?.H_Parent != null)
			{
				var pos = await _context.Hierarchicals.AsNoTracking()
					.FirstOrDefaultAsync(h => h.H_ID == node.H_Parent.Value);
				if (pos != null && !string.IsNullOrWhiteSpace(pos.H_Name))
					return (pos.H_Name!, string.IsNullOrWhiteSpace(pos.H_NameEn) ? pos.H_Name! : pos.H_NameEn!);
			}
			return ("المدير", "the manager");
		}

		public async Task<List<int>> ManagerChainAsync(int employeeId)
		{
			var all = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var byId = all.ToDictionary(h => h.H_ID);

			// the manager directly above: employee node → its position → the parent employee node
			int? ManagerOf(int empId)
			{
				var node = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == empId);
				if (node?.H_Parent == null || !byId.ContainsKey(node.H_Parent.Value)) return null;
				var pos = byId[node.H_Parent.Value];                 // position node (type 4)
				if (pos.H_Parent == null || !byId.ContainsKey(pos.H_Parent.Value)) return null;
				var mgr = byId[pos.H_Parent.Value];                  // manager employee node (type 5)
				return mgr.H_Type == 5 ? mgr.H_ObjectID : null;
			}

			// an employee is a unit head when their position attaches DIRECTLY to a unit node
			// (1 = company, 2 = branch, 3 = administrative body)
			bool IsUnitHead(int empId)
			{
				var node = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == empId);
				if (node?.H_Parent == null || !byId.ContainsKey(node.H_Parent.Value)) return false;
				var pos = byId[node.H_Parent.Value];
				if (pos.H_Parent == null || !byId.ContainsKey(pos.H_Parent.Value)) return false;
				var parent = byId[pos.H_Parent.Value];
				return parent.H_Type == 1 || parent.H_Type == 2 || parent.H_Type == 3;
			}

			// climb from the direct manager upward, stopping at the FIRST unit head reached (inclusive)
			var chain = new List<int>();
			var current = employeeId;
			var guard = 0;
			while (guard++ < 50)
			{
				var mgr = ManagerOf(current);
				if (mgr == null || mgr == employeeId || chain.Contains(mgr.Value)) break;
				chain.Add(mgr.Value);
				if (IsUnitHead(mgr.Value)) break;   // reached the nearest unit head → final approver
				current = mgr.Value;
			}
			return chain;
		}

		public async Task<(bool ok, string? error, LeaveRequest? req)> CreateAsync(
			int employeeId, int leaveTypeId, DateTime start, DateTime end, string? reason)
		{
			if (end.Date < start.Date) return (false, "تاريخ النهاية قبل تاريخ البداية", null);
			if (!await _context.LeaveTypes.AnyAsync(t => t.ID == leaveTypeId)) return (false, "نوع الإجازة غير صحيح", null);

			// no overlapping leave: the employee can't be on two leaves (ANY type) on the same day(s).
			// blocks against any existing request that is pending (0) or approved (1).
			var s = start.Date;
			var e = end.Date;
			var conflict = await _context.LeaveRequests.AsNoTracking()
				.Include(r => r.LeaveType)
				.Where(r => r.EmployeeID == employeeId && (r.Status == 0 || r.Status == 1)
					&& r.StartDate <= e && r.EndDate >= s)
				.OrderBy(r => r.StartDate)
				.FirstOrDefaultAsync();
			if (conflict != null)
			{
				var tname = conflict.LeaveType?.NameAr ?? "إجازة";
				var st = conflict.Status == 1 ? "موافَق عليه" : "معلّق";
				return (false,
					$"يوجد طلب {tname} ({st}) يتداخل مع هذه الفترة ({conflict.StartDate:yyyy/MM/dd} → {conflict.EndDate:yyyy/MM/dd}). لا يمكن أخذ إجازتين في نفس الأيام.",
					null);
			}

			// working days only (exclude weekly rest days defined by the policy)
			var days = await _dashboard.WorkingDaysAsync(employeeId, start, end);
			if (days <= 0) return (false, "كل الأيام المختارة أيام راحة. اختر تواريخ ضمن أيام العمل.", null);

			// strict balance check
			var remaining = await _dashboard.RemainingForTypeAsync(employeeId, leaveTypeId);
			if (days > remaining)
				return (false, $"الرصيد غير كافٍ. المتبقي {(remaining < 0 ? 0 : remaining)} يوم والمطلوب {days} يوم.", null);

			var chain = await ManagerChainAsync(employeeId);

			var req = new LeaveRequest
			{
				EmployeeID = employeeId,
				LeaveTypeID = leaveTypeId,
				StartDate = start.Date,
				EndDate = end.Date,
				Days = days,
				Reason = reason,
				CreatedAt = DateTime.UtcNow,
				CreatedBy = employeeId,
			};

			if (chain.Count == 0)
			{
				// requester is at/above the top of the chain → auto-approve
				req.Status = 1;
				req.CurrentLevel = 0;
				req.CurrentApproverEmployeeID = null;
				req.DecisionAt = DateTime.UtcNow;
			}
			else
			{
				req.Status = 0;
				req.CurrentLevel = 1;
				req.CurrentApproverEmployeeID = chain[0];
			}

			_context.LeaveRequests.Add(req);
			await _context.SaveChangesAsync();

			if (chain.Count > 0)
			{
				for (var i = 0; i < chain.Count; i++)
				{
					_context.LeaveApprovalSteps.Add(new LeaveApprovalStep
					{
						LeaveRequestID = req.ID,
						Level = i + 1,
						ApproverEmployeeID = chain[i],
						Status = 0,
						CreatedAt = DateTime.UtcNow,
						CreatedBy = employeeId,
					});
				}
				await _context.SaveChangesAsync();

				var requester = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == employeeId);
				var rAr = requester?.FullName ?? "موظف";
				var rEn = requester?.FullNameEn ?? requester?.FullName ?? "An employee";
				await _notifications.NotifyAsync(chain[0],
					"طلب إجازة جديد", "New leave request",
					$"قدّم {rAr} طلب إجازة لمدة {days} يوم بانتظار اعتمادك.",
					$"{rEn} submitted a leave request for {days} day(s) awaiting your approval.",
					"leave_submitted", req.ID);
			}

			return (true, null, req);
		}

		public async Task<(bool ok, string? error)> DecideAsync(int requestId, int approverEmployeeId, bool approve, string? note)
		{
			var req = await _context.LeaveRequests
				.Include(r => r.LeaveType)
				.Include(r => r.ApprovalSteps)
				.FirstOrDefaultAsync(r => r.ID == requestId);
			if (req == null) return (false, "الطلب غير موجود");
			if (req.Status != 0) return (false, "تم البتّ في الطلب مسبقًا");
			if (req.CurrentApproverEmployeeID != approverEmployeeId) return (false, "لا تملك صلاحية اعتماد هذا الطلب");

			var step = req.ApprovalSteps.FirstOrDefault(s => s.Level == req.CurrentLevel && s.Status == 0);
			if (step == null) return (false, "خطوة الاعتماد غير موجودة");

			var approver = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == approverEmployeeId);
			var aAr = approver?.FullName ?? "المدير";
			var aEn = approver?.FullNameEn ?? approver?.FullName ?? "Manager";
			var typeAr = req.LeaveType?.NameAr ?? "إجازة";
			var typeEn = req.LeaveType?.NameEn ?? "leave";

			var now = DateTime.UtcNow;

			// ---- rejection: ends the whole request immediately ----
			if (!approve)
			{
				step.Status = 2; step.DecisionAt = now; step.DecisionNote = note;
				step.UpdatedAt = now; step.updatedBy = approverEmployeeId;
				req.Status = 2; req.ApproverEmployeeID = approverEmployeeId;
				req.DecisionAt = now; req.DecisionNote = note; req.CurrentApproverEmployeeID = null;
				req.UpdatedAt = now; req.updatedBy = approverEmployeeId;
				await _context.SaveChangesAsync();

				await _notifications.NotifyAsync(req.EmployeeID,
					"تم رفض طلب إجازتك", "Your leave was rejected",
					$"طلب {typeAr} ({req.Days} يوم) تم رفضه بواسطة {aAr}.",
					$"Your {typeEn} request ({req.Days} day(s)) was rejected by {aEn}.",
					"leave_rejected", req.ID);
				return (true, null);
			}

			var maxLevel = req.ApprovalSteps.Count == 0 ? 0 : req.ApprovalSteps.Max(s => s.Level);
			var isFinal = req.CurrentLevel >= maxLevel;

			// ---- final approval ----
			if (isFinal)
			{
				// strict balance re-check (other approved requests may have consumed it meanwhile)
				var remaining = await _dashboard.RemainingForTypeAsync(req.EmployeeID, req.LeaveTypeID);
				if (req.Days > remaining)
					return (false, $"لا يمكن الاعتماد: الرصيد غير كافٍ (المتبقي {(remaining < 0 ? 0 : remaining)} يوم، المطلوب {req.Days}).");

				step.Status = 1; step.DecisionAt = now; step.DecisionNote = note;
				step.UpdatedAt = now; step.updatedBy = approverEmployeeId;
				req.Status = 1; req.ApproverEmployeeID = approverEmployeeId;
				req.DecisionAt = now; req.DecisionNote = note;
				req.CurrentApproverEmployeeID = null; req.CurrentLevel = 0;
				req.UpdatedAt = now; req.updatedBy = approverEmployeeId;
				await _context.SaveChangesAsync();

				await _notifications.NotifyAsync(req.EmployeeID,
					"تمت الموافقة على إجازتك", "Your leave was approved",
					$"طلب {typeAr} ({req.Days} يوم) تمت الموافقة عليه نهائيًا بواسطة {aAr}.",
					$"Your {typeEn} request ({req.Days} day(s)) was fully approved by {aEn}.",
					"leave_approved", req.ID);
				return (true, null);
			}

			// ---- intermediate approval: advance to the next level ----
			step.Status = 1; step.DecisionAt = now; step.DecisionNote = note;
			step.UpdatedAt = now; step.updatedBy = approverEmployeeId;

			var nextLevel = req.CurrentLevel + 1;
			var nextStep = req.ApprovalSteps.First(s => s.Level == nextLevel);
			req.CurrentLevel = nextLevel;
			req.CurrentApproverEmployeeID = nextStep.ApproverEmployeeID;
			req.UpdatedAt = now; req.updatedBy = approverEmployeeId;
			await _context.SaveChangesAsync();

			var requesterEmp = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == req.EmployeeID);
			var reqAr = requesterEmp?.FullName ?? "موظف";
			var reqEn = requesterEmp?.FullNameEn ?? requesterEmp?.FullName ?? "An employee";
			var nextTitle = await PositionTitleAsync(nextStep.ApproverEmployeeID);

			await _notifications.NotifyAsync(nextStep.ApproverEmployeeID,
				"طلب إجازة بانتظار اعتمادك", "Leave request awaiting your approval",
				$"طلب إجازة من {reqAr} ({req.Days} يوم) بانتظار اعتمادك.",
				$"A leave request from {reqEn} ({req.Days} day(s)) is awaiting your approval.",
				"leave_submitted", req.ID);

			await _notifications.NotifyAsync(req.EmployeeID,
				"تقدّم طلب إجازتك", "Your leave request advanced",
				$"وافق {aAr}، والطلب الآن بانتظار موافقة {nextTitle.ar}.",
				$"{aEn} approved; your request now awaits {nextTitle.en}'s approval.",
				"leave_progress", req.ID);

			return (true, null);
		}
	}
}
