using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// MOVING A PENDING APPROVAL STEP ON (2026-11-09). Two callers, one rule:
	//   * HANDOVER  — the final settlement terminates an approver: every pending step naming them moves on (StageAsync);
	//   * ESCALATION — a step waited past its path's SLA: that one step moves to its approver's manager (HrApprovalEscalation).
	//
	// The person it moves to is found by the chain's own climb (LeaveWorkflowService.ApproverChainAsync: same company,
	// leavers skipped, delegation applied). Then, for the request in hand:
	//   * they hold no step of it                 -> the step is REASSIGNED to them;
	//   * they hold a later PENDING step           -> the step is REMOVED and the levels above move down, so the request
	//                                                 waits at whoever now holds that level (an undecided step carries no
	//                                                 decision, so nothing is lost but the routing);
	//   * nobody, the requester, or someone who already DECIDED a step -> UNROUTED, left as it is (no one approves their
	//                                                 own request, no one approves twice).
	// Callers save; these only stage changes on the context.
	// ==========================================================================================
	public enum ApprovalRoute { Reassigned, Collapsed, Unrouted }

	public sealed record ApprovalHandoverResult(int Reassigned, int Collapsed, int Unrouted, int? NextApproverId);

	public static class HrApprovalHandover
	{
		/// The next active manager above an approver (null = nobody).
		public static async Task<int?> NextAboveAsync(ILeaveWorkflowService workflow, int approverId)
		{
			var above = (await workflow.ApproverChainAsync(approverId)).Approvers;
			return above.FirstOrDefault(id => id > 0 && id != approverId) is int n && n > 0 ? n : null;
		}

		public static async Task<ApprovalHandoverResult> StageAsync(CrossDbContext db, ILeaveWorkflowService workflow, int leaverId, CancellationToken ct = default)
		{
			var next = await NextAboveAsync(workflow, leaverId);
			int reassigned = 0, collapsed = 0, unrouted = 0;
			void Count(ApprovalRoute r) { if (r == ApprovalRoute.Reassigned) reassigned++; else if (r == ApprovalRoute.Collapsed) collapsed++; else unrouted++; }

			var leaveIds = await db.LeaveApprovalSteps.AsNoTracking()
				.Where(s => s.ApproverEmployeeID == leaverId && s.Status == 0 && s.LeaveRequest != null && s.LeaveRequest.Status == 0)
				.Select(s => s.LeaveRequestID).Distinct().ToListAsync(ct);
			foreach (var id in leaveIds)
			{
				var req = await db.LeaveRequests.Include(r => r.ApprovalSteps).FirstAsync(r => r.ID == id, ct);
				Count(RouteLeave(db, req, req.ApprovalSteps.First(s => s.ApproverEmployeeID == leaverId && s.Status == 0), next));
			}

			var reqIds = await db.EmployeeRequestSteps.AsNoTracking().Where(s => s.ApproverEmployeeID == leaverId && s.Status == 0)
				.Select(s => s.EmployeeRequestID).Distinct().ToListAsync(ct);
			foreach (var id in reqIds)
			{
				var req = await db.EmployeeRequests.Include(r => r.ApprovalSteps).FirstOrDefaultAsync(r => r.ID == id && r.Status == 0, ct);
				if (req == null) continue;
				Count(RouteRequest(db, req, req.ApprovalSteps.First(s => s.ApproverEmployeeID == leaverId && s.Status == 0), next));
			}
			return new ApprovalHandoverResult(reassigned, collapsed, unrouted, next);
		}

		public static ApprovalRoute RouteLeave(CrossDbContext db, LeaveRequest req, LeaveApprovalStep mine, int? next)
		{
			var steps = req.ApprovalSteps.OrderBy(s => s.Level).ToList();
			var from = mine.ApproverEmployeeID;
			var route = Decide(next, req.EmployeeID, steps.Where(s => s != mine).Select(s => (s.ApproverEmployeeID, s.Status == 0)));
			if (route == ApprovalRoute.Reassigned)
			{
				mine.ApproverEmployeeID = next!.Value;
				if (req.CurrentApproverEmployeeID == from && req.CurrentLevel == mine.Level) req.CurrentApproverEmployeeID = next;
			}
			else if (route == ApprovalRoute.Collapsed)
			{
				foreach (var s in steps.Where(s => s.Level > mine.Level)) s.Level--;
				db.LeaveApprovalSteps.Remove(mine);
				if (req.CurrentLevel == mine.Level) req.CurrentApproverEmployeeID = steps.FirstOrDefault(s => s != mine && s.Level == mine.Level)?.ApproverEmployeeID;
				else if (req.CurrentLevel > mine.Level) req.CurrentLevel--;
			}
			return route;
		}

		public static ApprovalRoute RouteRequest(CrossDbContext db, EmployeeRequest req, EmployeeRequestStep mine, int? next)
		{
			var steps = req.ApprovalSteps.OrderBy(s => s.Level).ToList();
			var from = mine.ApproverEmployeeID;
			var route = Decide(next, req.EmployeeID, steps.Where(s => s != mine).Select(s => (s.ApproverEmployeeID, s.Status == 0)));
			if (route == ApprovalRoute.Reassigned)
			{
				mine.ApproverEmployeeID = next!.Value;
				if (req.CurrentApproverEmployeeID == from && req.CurrentLevel == mine.Level) req.CurrentApproverEmployeeID = next;
			}
			else if (route == ApprovalRoute.Collapsed)
			{
				foreach (var s in steps.Where(s => s.Level > mine.Level)) s.Level--;
				db.EmployeeRequestSteps.Remove(mine);
				if (req.CurrentLevel == mine.Level) req.CurrentApproverEmployeeID = steps.FirstOrDefault(s => s != mine && s.Level == mine.Level)?.ApproverEmployeeID;
				else if (req.CurrentLevel > mine.Level) req.CurrentLevel--;
			}
			return route;
		}

		// others = the request's OTHER steps with whether each is still pending. Collapsing onto a PENDING step always leaves
		// somebody to decide; a person who already DECIDED a step is never handed the request again (no one approves twice).
		private static ApprovalRoute Decide(int? next, int requesterId, IEnumerable<(int Approver, bool Pending)> others)
		{
			if (next is not int to || to == requesterId) return ApprovalRoute.Unrouted;
			var held = others.Where(o => o.Approver == to).ToList();
			if (held.Count == 0) return ApprovalRoute.Reassigned;
			return held.Any(o => o.Pending) ? ApprovalRoute.Collapsed : ApprovalRoute.Unrouted;
		}
	}
}
