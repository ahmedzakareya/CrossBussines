using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	/// <summary>
	/// Sales-rep routes and the day's visits — the web side of the mobile "زيارات اليوم".
	///
	/// A manager plans each rep's route (customers in order, planned times, weekdays, a daily sales target) and watches
	/// the day: who has been visited, when the rep arrived, the order taken, the money collected, and why a visit was
	/// skipped. The phone reads the same routes and writes the same visits (RepApiController → SalesRepRouteService).
	///
	/// Guard: inventory "manage" — the role that keeps master data and settings, as for promotions and price lists.
	/// Company: resolved once per request like InventoryController; 0 (nothing) when none resolves.
	/// </summary>
	[SessionValidation]
	public class SalesRepController : Controller
	{
		private readonly CrossDbContext _db;
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		private int? _companyId;
		private int co => _companyId ?? 0;
		private static bool IsAr => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

		public SalesRepController(CrossDbContext db, CrossBuy.BL.Platform.IRequestCompanyResolver company) { _db = db; _company = company; }

		public override async Task OnActionExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context, Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
		{
			if (!_companyId.HasValue) { var scope = await _company.ResolveAsync(); _companyId = scope.Ok ? scope.CompanyId : 0; }
			await next();
		}

		private SalesRepRouteService Svc => new(_db);
		private static string L(string ar, string en) => IsAr ? ar : en;

		private void SetPaging(int total, int page, int pageSize)
		{
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
		}

		private async Task LoadPickListsAsync()
		{
			ViewBag.Reps = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == co && e.IsActive)
				.OrderBy(e => e.FullName).Select(e => new { e.ID, e.FullName, e.FullNameEn })
				.ToListAsync() is var reps ? reps.Select(e => (e.ID, IsAr || string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullName : e.FullNameEn!)).ToList() : new();
			ViewBag.Customers = await _db.Customers.AsNoTracking().Where(c => c.CompanyID == co && c.IsActive)
				.OrderBy(c => c.Name).Select(c => new { c.ID, c.Name, c.NameEn, c.Address })
				.ToListAsync() is var cs ? cs.Select(c => (c.ID, IsAr || string.IsNullOrWhiteSpace(c.NameEn) ? c.Name : c.NameEn!, c.Address)).ToList() : new();
		}

		// ---------------- routes ----------------
		[HttpGet][InvPerm("manage")]
		public IActionResult Routes() => View();   // shell; rows via RoutesData

		[HttpGet][InvPerm("manage")]
		public async Task<IActionResult> RoutesData(string? q, bool? active, int page = 1, int pageSize = 25)
		{
			var (rows, total) = await Svc.SearchRoutesAsync(co, q, active, page, pageSize, IsAr, HttpContext.RequestAborted);
			SetPaging(total, page, pageSize);
			return PartialView("_RouteRows", rows);
		}

		[HttpGet][InvPerm("manage")]
		public async Task<IActionResult> RouteEditor(int? id)
		{
			var model = id is > 0 ? await Svc.GetRouteAsync(co, id.Value, HttpContext.RequestAborted) : null;
			if (id is > 0 && model == null) { TempData["InvErr"] = L("المسار غير موجود", "Route not found"); return RedirectToAction(nameof(Routes)); }
			await LoadPickListsAsync();
			return View(model ?? new Models.Context.Inventory.SalesRepRoute { IsActive = true });
		}

		public sealed class StopForm { public int CustomerId { get; set; } public string? Time { get; set; } public List<int>? Days { get; set; } }

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> SaveRoute(int id, string name, string? nameEn, int repEmployeeId, decimal dailyTarget, bool isActive, List<StopForm>? stops)
		{
			var list = (stops ?? new()).Where(s => s.CustomerId > 0).Select((s, i) => new SalesRepRouteService.StopInput(
				s.CustomerId, i + 1,
				TimeSpan.TryParse(s.Time, out var t) ? t : null,
				(s.Days ?? new()).Where(d => d is >= 0 and <= 6).Distinct().Sum(d => 1 << d))).ToList();
			var (ok, err, newId) = await Svc.SaveRouteAsync(co, id, name, nameEn, repEmployeeId, dailyTarget, isActive, list, User?.Identity?.Name, HttpContext.RequestAborted);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L("حُفظ المسار", "Route saved") : Translate(err);
			return ok ? RedirectToAction(nameof(RouteEditor), new { id = newId }) : RedirectToAction(nameof(RouteEditor), new { id = id > 0 ? id : (int?)null });
		}

		// ---------------- the day's board ----------------
		[HttpGet][InvPerm("manage")]
		public async Task<IActionResult> Visits(DateTime? date, int? repId)
		{
			var day = (date ?? DateTime.Today).Date;
			await LoadPickListsAsync();
			ViewBag.Date = day; ViewBag.RepId = repId;
			ViewBag.Targets = await _db.SalesRepRoutes.AsNoTracking().Where(r => r.CompanyID == co && r.IsActive)
				.GroupBy(r => r.RepEmployeeID).Select(g => new { g.Key, T = g.Sum(r => r.DailyTarget) }).ToDictionaryAsync(x => x.Key, x => x.T);
			ViewBag.Currency = await _db.Currencies.AsNoTracking().Where(c => c.ID == _db.Companies.Where(x => x.CompanyID == co).Select(x => x.DefaultCurrencyId).FirstOrDefault())
				.Select(c => c.Code).FirstOrDefaultAsync() ?? "";
			return View(await Svc.BoardAsync(co, day, repId, IsAr, HttpContext.RequestAborted));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> AddVisit(int repEmployeeId, int customerId, DateTime date, string? time)
		{
			var (ok, err) = await Svc.AddVisitAsync(co, repEmployeeId, customerId, date, TimeSpan.TryParse(time, out var t) ? t : null, User?.Identity?.Name, HttpContext.RequestAborted);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L("أُضيفت الزيارة", "Visit added") : Translate(err);
			return RedirectToAction(nameof(Visits), new { date = date.ToString("yyyy-MM-dd"), repId = (int?)null });
		}

		// the service speaks English (the file convention of BL services); the screen says it in the reader's language
		private static string Translate(string? err) => err switch
		{
			"The route needs a name" => L("أدخل اسم المسار", err),
			"The daily target cannot be negative" => L("الهدف اليومي لا يكون سالباً", err),
			"Choose an active employee of this company as the rep" => L("اختر موظفاً نشطاً في هذه الشركة مندوباً", err),
			"A customer appears twice on the route" => L("عميل مكرر في المسار", err),
			"A customer on the route is not one of this company's" => L("عميل في المسار ليس من عملاء هذه الشركة", err),
			"Every stop needs at least one weekday" => L("كل محطة تحتاج يوماً واحداً على الأقل", err),
			"Route not found" => L("المسار غير موجود", err),
			"Customer not found" => L("العميل غير موجود", err),
			"A visit cannot be planned in the past" => L("لا تُخطَّط زيارة في يوم مضى", err),
			_ => err ?? L("تعذّر الحفظ", "Could not save"),
		};
	}
}
