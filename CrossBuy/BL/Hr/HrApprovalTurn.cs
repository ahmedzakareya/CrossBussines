using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// "IT IS MY TURN" — the record-level fact beside leave-approve (2026-11-10).
	//
	// leave-approve (HrAccessService) is "the caller manages the SUBJECT through the company-intersected hierarchy".
	// Three things the HR programme added put a request's current step on someone OUTSIDE that hierarchy:
	//   * an approval path's HR final approver (HrApprovalPathService) — an HR officer manages nobody;
	//   * an approval delegate (ApplyDelegationsAsync) — often a peer of the absent manager;
	//   * a leaver's handover / an SLA escalation can land on such a person too.
	// The chain placed them there deliberately, and the workflow service only lets the current step's approver
	// decide, so a decision gate that refuses them strands the request. This is the second, record-level way in:
	// the request is PENDING, its current approver IS the caller, the caller is not the requester, and the
	// requester belongs to the caller's company. Nothing else is widened.
	// ==========================================================================================
	public static class HrApprovalTurn
	{
		public static async Task<bool> HoldsLeaveTurnAsync(CrossDbContext db, int requestId, int callerEmployeeId, int companyId, CancellationToken ct = default) =>
			callerEmployeeId > 0 && companyId > 0 && await db.LeaveRequests.AsNoTracking()
				.Where(r => r.ID == requestId && r.Status == 0 && r.CurrentApproverEmployeeID == callerEmployeeId && r.EmployeeID != callerEmployeeId)
				.Join(db.Employee.AsNoTracking(), r => r.EmployeeID, e => e.ID, (r, e) => e.EmpCompanyID)
				.AnyAsync(c => c == companyId, ct);

		public static async Task<bool> HoldsRequestTurnAsync(CrossDbContext db, int requestId, int callerEmployeeId, int companyId, CancellationToken ct = default) =>
			callerEmployeeId > 0 && companyId > 0 && await db.EmployeeRequests.AsNoTracking()
				.AnyAsync(r => r.ID == requestId && r.Status == 0 && r.CurrentApproverEmployeeID == callerEmployeeId
					&& r.EmployeeID != callerEmployeeId && r.CompanyID == companyId, ct);
	}
}
