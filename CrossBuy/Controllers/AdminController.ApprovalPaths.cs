using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR — APPROVAL PATH DESIGNER (prototype #/workflow, 2026-11-09). Reading needs the HR read right; changing a path
	// needs organization-manage, the same right the country rule pack uses. The paths shape the org-tree chain; the
	// safeguards (delegation, leavers, no self- or double approval) stay in code and are listed on the screen.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record ApprovalPersonOption(int Id, string Name, string? Photo, string Initials);
		public sealed record ApprovalPathRow(CrossBuy.Models.Context.Admin.HrApprovalPath Path, string KindAr, string KindEn, string? LeaveTypeAr,
			string? LeaveTypeEn, ApprovalPersonOption? Final);
		public sealed record ApprovalPathsPageModel(IReadOnlyList<ApprovalPathRow> Rows, IReadOnlyList<(int Id, string Ar, string En)> LeaveTypes,
			IReadOnlyList<ApprovalPersonOption> People, bool CanEdit);

		[HttpGet]
		public async Task<IActionResult> ApprovalPaths(CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.Read);
			if (!gate.Ok) return HrDenied(nameof(Index));
			var paths = await new CrossBuy.BL.Hr.HrApprovalPathService(Db).ListAsync(gate.CompanyId, ct);
			var types = (await Db.LeaveTypes.AsNoTracking().OrderBy(t => t.ID).Select(t => new { t.ID, t.NameAr, t.NameEn }).ToListAsync(ct))
				.Select(t => (t.ID, t.NameAr ?? t.NameEn ?? "", t.NameEn ?? t.NameAr ?? "")).ToList();
			var people = (await Db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == gate.CompanyId && e.IsActive).OrderBy(e => e.FullName)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync(ct))
				.Select(e => new ApprovalPersonOption(e.ID, CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn) ?? ("#" + e.ID),
					CrossBuy.BL.Hr.PersonTile.Photo(e.ProfileImage), CrossBuy.BL.Hr.PersonTile.Initials(e.FullName, e.FullNameEn))).ToList();
			var byId = people.ToDictionary(p => p.Id);
			var rows = paths.Select(p =>
			{
				var k = CrossBuy.BL.Hr.HrApprovalKinds.Name(p.Kind);
				var t = p.LeaveTypeID is int id ? types.FirstOrDefault(x => x.Item1 == id) : default;
				return new ApprovalPathRow(p, k.Ar, k.En, p.LeaveTypeID == null ? null : t.Item2, p.LeaveTypeID == null ? null : t.Item3,
					p.FinalApproverEmployeeID is int f && byId.TryGetValue(f, out var person) ? person : null);
			}).ToList();
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			bool canEdit = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.OrganizationManage, null);
			return View(new ApprovalPathsPageModel(rows, types, people, canEdit));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SaveApprovalPath(int id, string kind, int? leaveTypeId, int managerLevels, int directOnlyUpToDays,
			int? finalApproverEmployeeId, int slaDays = 0, bool isActive = true, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.OrganizationManage);
			if (!gate.Ok) return HrDenied(nameof(ApprovalPaths));
			var (ok, ar, en) = await new CrossBuy.BL.Hr.HrApprovalPathService(Db).SaveAsync(gate.CompanyId, new CrossBuy.Models.Context.Admin.HrApprovalPath
			{
				ID = id, Kind = kind ?? "", LeaveTypeID = leaveTypeId is > 0 ? leaveTypeId : null, ManagerLevels = managerLevels,
				DirectOnlyUpToDays = directOnlyUpToDays, FinalApproverEmployeeID = finalApproverEmployeeId, SlaDays = slaDays, IsActive = isActive,
			}, gate.EmployeeId, ct);
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? (isAr ? "حُفظ مسار الموافقة" : "Approval path saved") : (isAr ? ar : en);
			return RedirectToAction(nameof(ApprovalPaths));
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> DeleteApprovalPath(int id, CancellationToken ct = default)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.OrganizationManage);
			if (!gate.Ok) return HrDenied(nameof(ApprovalPaths));
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var ok = await new CrossBuy.BL.Hr.HrApprovalPathService(Db).DeleteAsync(gate.CompanyId, id, ct);
			TempData[ok ? "HrMsg" : "HrErr"] = ok ? (isAr ? "حُذف المسار؛ يعود النوع إلى سلسلة المديرين كاملة" : "Path removed; the kind goes back to the full manager chain")
				: (isAr ? "المسار غير موجود" : "Path not found");
			return RedirectToAction(nameof(ApprovalPaths));
		}
	}
}
