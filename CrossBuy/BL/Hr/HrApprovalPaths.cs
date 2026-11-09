using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// APPROVAL PATHS YOU DESIGN, NOT CODE (prototype #/workflow, 2026-11-09).
	//
	// The org tree still decides WHO the managers are (LeaveWorkflowService.ApproverChainAsync: company intersection,
	// leavers skipped, delegation applied). A path only SHAPES that chain for one request kind:
	//   ManagerLevels       keep the first N managers (0 = all of them, up to the unit head);
	//   DirectOnlyUpToDays  a leave of this many days or fewer needs the direct manager only (0 = off);
	//   FinalApprover       an HR officer appended as the last approver — only when active, in the requester's company,
	//                       not the requester and not already in the chain (no self-approval, no double approval).
	// The shape is applied AFTER the chain's refusal test, so a broken org tree is still refused, never "fixed" by a path.
	// A leave-type path wins over the all-leave path; no path (or an inactive one) leaves the chain exactly as it was.
	// ==========================================================================================
	public static class HrApprovalKinds
	{
		public const string Leave = "Leave";
		public static readonly string[] All = { Leave, "Letter", "Permission", "Advance", "ProfileChange" };

		public static (string Ar, string En) Name(string kind) => kind switch
		{
			Leave => ("الإجازات", "Leave"),
			"Letter" => ("طلب خطاب", "Letter request"),
			"Permission" => ("إذن", "Permission"),
			"Advance" => ("سلفة راتب", "Salary advance"),
			"ProfileChange" => ("تعديل بيانات", "Profile change"),
			_ => (kind, kind),
		};
	}

	public sealed class HrApprovalPathService
	{
		private readonly CrossDbContext _db;
		public HrApprovalPathService(CrossDbContext db) { _db = db; }

		/// The pure shaping step, so every rule is testable without a database.
		public static List<int> Shape(IReadOnlyList<int> chain, HrApprovalPath? path, int? days, int requesterId, int? finalApprover)
		{
			var c = chain.ToList();
			if (path is not { IsActive: true }) return c;
			if (path.ManagerLevels > 0 && c.Count > path.ManagerLevels) c = c.Take(path.ManagerLevels).ToList();
			if (days is int d && path.DirectOnlyUpToDays > 0 && d <= path.DirectOnlyUpToDays && c.Count > 1) c = c.Take(1).ToList();
			if (finalApprover is int f && f > 0 && f != requesterId && !c.Contains(f)) c.Add(f);
			return c;
		}

		public async Task<HrApprovalPath?> ForAsync(int companyId, string kind, int? leaveTypeId, CancellationToken ct = default)
		{
			var rows = await _db.HrApprovalPaths.AsNoTracking()
				.Where(p => p.CompanyID == companyId && p.Kind == kind && p.IsActive && (p.LeaveTypeID == null || p.LeaveTypeID == leaveTypeId)).ToListAsync(ct);
			return rows.FirstOrDefault(p => leaveTypeId != null && p.LeaveTypeID == leaveTypeId) ?? rows.FirstOrDefault(p => p.LeaveTypeID == null);
		}

		/// The chain the request will actually use. Called by the leave and employee-request flows after their refusal test.
		public async Task<List<int>> ApplyAsync(string kind, int? leaveTypeId, int? days, int requesterId, List<int> chain, CancellationToken ct = default)
		{
			var companyId = await _db.Employee.AsNoTracking().Where(e => e.ID == requesterId).Select(e => (int?)e.EmpCompanyID).FirstOrDefaultAsync(ct);
			if (companyId is not > 0) return chain;
			var path = await ForAsync(companyId.Value, kind, leaveTypeId, ct);
			if (path == null) return chain;
			int? final = null;
			if (path.FinalApproverEmployeeID is int f
				&& await _db.Employee.AsNoTracking().AnyAsync(e => e.ID == f && e.EmpCompanyID == companyId.Value && e.IsActive, ct))
				final = f;
			return Shape(chain, path, days, requesterId, final);
		}

		public Task<List<HrApprovalPath>> ListAsync(int companyId, CancellationToken ct = default) =>
			_db.HrApprovalPaths.AsNoTracking().Where(p => p.CompanyID == companyId).OrderBy(p => p.Kind).ThenBy(p => p.LeaveTypeID).ToListAsync(ct);

		public async Task<(bool Ok, string? Ar, string? En)> SaveAsync(int companyId, HrApprovalPath input, int? byEmployeeId, CancellationToken ct = default)
		{
			if (!HrApprovalKinds.All.Contains(input.Kind)) return (false, "نوع طلب غير معروف", "Unknown request kind");
			if (input.Kind != HrApprovalKinds.Leave) { input.LeaveTypeID = null; input.DirectOnlyUpToDays = 0; }
			if (input.ManagerLevels is < 0 or > 20 || input.DirectOnlyUpToDays is < 0 or > 365 || input.SlaDays is < 0 or > 90) return (false, "القيم خارج النطاق", "Values are out of range");
			if (input.LeaveTypeID is int lt && !await _db.LeaveTypes.AsNoTracking().AnyAsync(t => t.ID == lt, ct)) return (false, "نوع الإجازة غير موجود", "Leave type not found");
			if (input.FinalApproverEmployeeID is int f && f > 0)
			{
				if (!await _db.Employee.AsNoTracking().AnyAsync(e => e.ID == f && e.EmpCompanyID == companyId && e.IsActive, ct))
					return (false, "المعتمِد النهائي يجب أن يكون موظفاً نشطاً في الشركة", "The final approver must be an active employee of the company");
			}
			else input.FinalApproverEmployeeID = null;

			var row = input.ID > 0
				? await _db.HrApprovalPaths.FirstOrDefaultAsync(p => p.ID == input.ID && p.CompanyID == companyId, ct)
				: await _db.HrApprovalPaths.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.Kind == input.Kind && p.LeaveTypeID == input.LeaveTypeID, ct);
			if (input.ID > 0 && row == null) return (false, "المسار غير موجود", "Path not found");
			if (row == null) { row = new HrApprovalPath { CompanyID = companyId }; _db.HrApprovalPaths.Add(row); }
			else if (await _db.HrApprovalPaths.AnyAsync(p => p.CompanyID == companyId && p.ID != row.ID && p.Kind == input.Kind && p.LeaveTypeID == input.LeaveTypeID, ct))
				return (false, "يوجد مسار آخر لنفس النوع", "Another path already covers this kind");
			row.Kind = input.Kind; row.LeaveTypeID = input.LeaveTypeID; row.ManagerLevels = input.ManagerLevels;
			row.DirectOnlyUpToDays = input.DirectOnlyUpToDays; row.FinalApproverEmployeeID = input.FinalApproverEmployeeID; row.SlaDays = input.SlaDays;
			row.IsActive = input.IsActive; row.UpdatedAt = DateTime.UtcNow; row.UpdatedByEmployeeID = byEmployeeId;
			await _db.SaveChangesAsync(ct);
			return (true, null, null);
		}

		public async Task<bool> DeleteAsync(int companyId, int id, CancellationToken ct = default)
		{
			var row = await _db.HrApprovalPaths.FirstOrDefaultAsync(p => p.ID == id && p.CompanyID == companyId, ct);
			if (row == null) return false;
			_db.HrApprovalPaths.Remove(row);
			await _db.SaveChangesAsync(ct);
			return true;
		}
	}
}
