using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// HR (2026-11-19) — where a phone punch is accepted: each branch's office location and radius (attendance-manage).
	// The phone reports its position; EssApiController.Punch measures the distance on the server and refuses a punch
	// outside the radius, or at a branch with no location set.
	public partial class AdminController
	{
		[HttpGet]
		public async Task<IActionResult> PunchLocations()
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage);
			if (!gate.Ok) return HrDenied(nameof(Attendance));
			var branches = await Db.Branches.AsNoTracking().Where(b => b.CompanyID == gate.CompanyId).OrderBy(b => b.ID).ToListAsync();
			var ids = branches.Select(b => b.ID).ToList();
			ViewBag.Staff = await Db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == gate.CompanyId && e.IsActive && e.BranchID != null && ids.Contains(e.BranchID.Value))
				.GroupBy(e => e.BranchID!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
			return View(branches);
		}

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> SavePunchLocation(int branchId, decimal? latitude, decimal? longitude, int? radius)
		{
			var gate = await HrGateAsync(CrossBuy.BL.HrActions.AttendanceManage);
			if (!gate.Ok) return HrDenied(nameof(Attendance));
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var b = await Db.Branches.FirstOrDefaultAsync(x => x.ID == branchId && x.CompanyID == gate.CompanyId);
			if (b == null) { TempData["HrErr"] = ar ? "الفرع غير موجود" : "Branch not found"; return RedirectToAction(nameof(PunchLocations)); }
			bool clearing = latitude == null && longitude == null;
			if (!clearing && (latitude is null or < -90 or > 90 || longitude is null or < -180 or > 180))
			{ TempData["HrErr"] = ar ? "أدخل خط عرض وخط طول صحيحين" : "Enter a valid latitude and longitude"; return RedirectToAction(nameof(PunchLocations)); }
			// phone GPS is commonly 10–30 m off outdoors and worse indoors: below 50 m people at their desks would be refused
			if (!clearing && radius is null or < 50 or > 5000)
			{ TempData["HrErr"] = ar ? "النطاق بين 50 و5000 متر (دقة GPS في الجوال لا تكفي لأقل من ذلك)" : "The radius is between 50 and 5,000 metres (phone GPS is not precise enough below that)"; return RedirectToAction(nameof(PunchLocations)); }
			b.Latitude = clearing ? null : Math.Round(latitude!.Value, 6);
			b.Longitude = clearing ? null : Math.Round(longitude!.Value, 6);
			b.PunchRadiusMeters = clearing ? null : radius;
			await Db.SaveChangesAsync();
			TempData["HrMsg"] = clearing ? (ar ? "أُلغي موقع البصمة؛ لا بصمة من الجوال في هذا الفرع" : "Location cleared; no phone punches at this branch")
				: (ar ? "حُفظ موقع البصمة" : "Punch location saved");
			return RedirectToAction(nameof(PunchLocations));
		}
	}
}
