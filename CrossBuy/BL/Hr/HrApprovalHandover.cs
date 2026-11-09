using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// APPROVAL HANDOVER WHEN AN APPROVER LEAVES (2026-11-09).
	//
	// The approver chain skips a manager who has left (LeaveWorkflowService.ApproverChainAsync), but only for a chain
	// built AFTER they left. A leave or employee request already waiting at their level would wait forever: nobody else
	// is allowed to decide it. So when the final settlement terminates an employee, every PENDING step that names them
	// is handed on, using the same climb the chain itself uses (delegation included) to find the next active manager:
	//
	//   * the next manager holds no step of that request  -> the step is REASSIGNED to them;
	//   * they already hold a later step (the usual case: the chain was leaver -> their manager) -> the leaver's step is
	//     REMOVED, the steps above it move down one level, and the request now waits at whoever holds that level. An
	//     undecided step carries no decision, so removing it loses nothing but the routing.
	//   * there is nobody above, or it is the requester -> left as it is and counted UNROUTED, for HR to route by hand.
	//
	// The caller saves; this only stages the changes on the context.
	// ==========================================================================================
	public sealed record ApprovalHandoverResult(int Reassigned, int Collapsed, int Unrouted, int? NextApproverId);

	public static class HrApprovalHandover
	{
		public static async Task<ApprovalHandoverResult> StageAsync(CrossDbContext db, ILeaveWorkflowService workflow, int leaverId, CancellationToken ct = default)
		{
			var above = (await workflow.ApproverChainAsync(leaverId)).Approvers;
			int? next = above.FirstOrDefault(id => id > 0 && id != leaverId) is int n && n > 0 ? n : null;
			int reassigned = 0, collapsed = 0, unrouted = 0;

			var leaveIds = await db.LeaveApprovalSteps.AsNoTracking()
				.Where(s => s.ApproverEmployeeID == leaverId && s.Status == 0 && s.LeaveRequest != null && s.LeaveRequest.Status == 0)
				.Select(s => s.LeaveRequestID).Distinct().ToListAsync(ct);
			foreach (var id in leaveIds)
			{
				var req = await db.LeaveRequests.Include(r => r.ApprovalSteps).FirstAsync(r => r.ID == id, ct);
				var steps = req.ApprovalSteps.OrderBy(s => s.Level).ToList();
				var mine = steps.First(s => s.ApproverEmployeeID == leaverId && s.Status == 0);
				switch (Decide(next, req.EmployeeID, steps.Where(s => s != mine).Select(s => (s.ApproverEmployeeID, s.Status == 0))))
				{
					case Outcome.Reassign:
						mine.ApproverEmployeeID = next!.Value;
						if (req.CurrentApproverEmployeeID == leaverId) req.CurrentApproverEmployeeID = next;
						reassigned++; break;
					case Outcome.Collapse:
						foreach (var s in steps.Where(s => s.Level > mine.Level)) s.Level--;
						db.LeaveApprovalSteps.Remove(mine);
						if (req.CurrentLevel == mine.Level)
							req.CurrentApproverEmployeeID = steps.FirstOrDefault(s => s != mine && s.Level == mine.Level)?.ApproverEmployeeID;
						else if (req.CurrentLevel > mine.Level) req.CurrentLevel--;
						collapsed++; break;
					default: unrouted++; break;
				}
			}

			var reqIds = await db.EmployeeRequestSteps.AsNoTracking().Where(s => s.ApproverEmployeeID == leaverId && s.Status == 0)
				.Select(s => s.EmployeeRequestID).Distinct().ToListAsync(ct);
			foreach (var id in reqIds)
			{
				var req = await db.EmployeeRequests.Include(r => r.ApprovalSteps).FirstOrDefaultAsync(r => r.ID == id && r.Status == 0, ct);
				if (req == null) continue;
				var steps = req.ApprovalSteps.OrderBy(s => s.Level).ToList();
				var mine = steps.First(s => s.ApproverEmployeeID == leaverId && s.Status == 0);
				switch (Decide(next, req.EmployeeID, steps.Where(s => s != mine).Select(s => (s.ApproverEmployeeID, s.Status == 0))))
				{
					case Outcome.Reassign:
						mine.ApproverEmployeeID = next!.Value;
						if (req.CurrentApproverEmployeeID == leaverId) req.CurrentApproverEmployeeID = next;
						reassigned++; break;
					case Outcome.Collapse:
						foreach (var s in steps.Where(s => s.Level > mine.Level)) s.Level--;
						db.EmployeeRequestSteps.Remove(mine);
						if (req.CurrentLevel == mine.Level)
							req.CurrentApproverEmployeeID = steps.FirstOrDefault(s => s != mine && s.Level == mine.Level)?.ApproverEmployeeID;
						else if (req.CurrentLevel > mine.Level) req.CurrentLevel--;
						collapsed++; break;
					default: unrouted++; break;
				}
			}
			return new ApprovalHandoverResult(reassigned, collapsed, unrouted, next);
		}

		private enum Outcome { Reassign, Collapse, Unrouted }

		// others = the request's OTHER steps with whether each is still pending. Collapsing onto a PENDING step always leaves
		// somebody to decide; a next manager who already DECIDED a step is never handed the request again (no one approves twice).
		private static Outcome Decide(int? next, int requesterId, IEnumerable<(int Approver, bool Pending)> others)
		{
			if (next is not int to || to == requesterId) return Outcome.Unrouted;
			var held = others.Where(o => o.Approver == to).ToList();
			if (held.Count == 0) return Outcome.Reassign;
			return held.Any(o => o.Pending) ? Outcome.Collapse : Outcome.Unrouted;
		}
	}
}
