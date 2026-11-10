using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H44: the central kitchen (the prototype's #/kitchen) and its setting. Production today, patient meals,
	// the cafeteria and the month's costing and allocation; counts, issues and the allocation journal. Every rule is
	// HospitalKitchenService's; the door is `read`, the service decides who may do what. Built from the request's services.
	// ==========================================================================================
	public partial class HospitalController
	{
		private HospitalKitchenService? _kitchenSvc;
		private HospitalKitchenService KitchenSvc => _kitchenSvc ??= ActivatorUtilities.CreateInstance<HospitalKitchenService>(HttpContext.RequestServices);

		public sealed class HospitalKitchenCountInput { public int DestinationId { get; set; } public string Round { get; set; } = ""; public int Meals { get; set; } }
		public sealed class HospitalKitchenIssueInput { public int ItemId { get; set; } public decimal Qty { get; set; } public bool Waste { get; set; } public string? Note { get; set; } }
		public sealed class HospitalKitchenAllocateInput { public DateTime Month { get; set; } }
		public sealed class HospitalKitchenSettingInput
		{
			public int? KitchenWarehouseId { get; set; } public int? PoolAccountId { get; set; } public int? PatientMealsAccountId { get; set; }
			public int? StaffMealsAccountId { get; set; } public int? CafeteriaCogsAccountId { get; set; } public Dictionary<string, int?> Recipes { get; set; } = new();
		}

		[HttpGet("Kitchen")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Kitchen(string? month, string? recipe, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var today = HospitalDietaryService.Today(DateTime.UtcNow);
			var m = DateTime.TryParseExact(month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var mm) && mm <= today ? mm : new DateTime(today.Year, today.Month, 1);
			try
			{
				ViewBag.Setting = await KitchenSvc.SettingAsync(ctx, ct);
				ViewBag.Rounds = await KitchenSvc.RoundsAsync(ctx, DateTime.UtcNow, ct);
				ViewBag.ByDiet = await DietSvc.DayByDietAsync(ctx, today, ct);
				var recipes = await KitchenSvc.DietRecipesAsync(ctx, ct);
				ViewBag.Recipes = recipes;
				string? diet = recipe != null && recipes.ContainsKey(recipe) ? recipe : recipes.ContainsKey(HospitalDiet.Diabetic) ? HospitalDiet.Diabetic : recipes.Keys.FirstOrDefault();
				ViewBag.RecipeDiet = diet;
				ViewBag.Recipe = diet == null ? null : await KitchenSvc.RecipeAsync(ctx, recipes[diet], ct);
				ViewBag.Destinations = await KitchenSvc.DestinationsAsync(ctx, ct);
				var month_ = await KitchenSvc.MonthAsync(ctx, m, ct);
				ViewBag.Month = month_;
				ViewBag.Cafeteria = await KitchenSvc.CafeteriaAsync(ctx, month_, ct);
				ViewBag.Ready = true;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { ViewBag.Ready = false; }
			ViewBag.Today = today;
			ViewBag.CanWork = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View();
		}

		[HttpPost("Kitchen/Count"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> KitchenCount([FromBody] HospitalKitchenCountInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await KitchenSvc.CountAsync(ctx, input.DestinationId, input.Round, input.Meals, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Kitchen/Issue"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> KitchenIssue([FromBody] HospitalKitchenIssueInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await KitchenSvc.IssueAsync(ctx, input.ItemId, input.Qty, input.Waste, input.Note, ctx.UserId, DateTime.UtcNow, ct));
		}

		[HttpPost("Kitchen/Allocate"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> KitchenAllocate([FromBody] HospitalKitchenAllocateInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await KitchenSvc.AllocateAsync(ctx, input.Month, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		// ---------------- the setting (`manage`) ----------------

		[HttpGet("Kitchen/Setting")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> KitchenSetting(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			try
			{
				ViewBag.Setting = await KitchenSvc.SettingAsync(ctx, ct);
				var recipes = await KitchenSvc.DietRecipesAsync(ctx, ct);
				ViewBag.Recipes = recipes;
				ViewBag.Destinations = await KitchenSvc.DestinationsAsync(ctx, ct);
				var itemIds = recipes.Values.Concat(((IReadOnlyList<HospitalKitchenDestination>)ViewBag.Destinations).Where(d => d.RecipeItemId != null).Select(d => d.RecipeItemId!.Value)).Distinct().ToList();
				ViewBag.ItemNames = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemCode + " — " + DisplayName.Of(i.Name, i.NameEn), ct);
				ViewBag.Ready = true;
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { ViewBag.Ready = false; }
			ViewBag.Warehouses = await _db.Warehouses.AsNoTracking().Where(w => w.CompanyID == ctx.CompanyId && w.IsActive).OrderBy(w => w.Code)
				.Select(w => new HospitalChoice(w.ID, w.Name ?? w.Code, w.NameEn ?? w.Name ?? w.Code, w.Code)).ToListAsync(ct);
			ViewBag.Accounts = await (from a in _db.Accounts.AsNoTracking() join t in _db.AccountTypes.AsNoTracking() on a.AccountTypeId equals t.ID
									  where a.CompanyID == ctx.CompanyId && a.IsActive && a.IsPostable && (t.Code == HospitalClaimAccounts.Expense || t.Code == HospitalClaimAccounts.Asset)
									  orderby a.Code select new HospitalChoice(a.ID, a.Name, a.NameEn ?? a.Name, a.Code + "|" + t.Code)).ToListAsync(ct);
			ViewBag.Wards = await _db.HospitalWards.AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId).OrderBy(w => w.Code)
				.Select(w => new HospitalChoice(w.ID, w.Name, w.NameEn ?? w.Name, w.Code)).ToListAsync(ct);
			ViewBag.CostCenters = await _db.CostCenters.AsNoTracking().Where(c => c.CompanyID == ctx.CompanyId && c.IsActive).OrderBy(c => c.Code)
				.Select(c => new HospitalChoice(c.ID, c.Name, c.NameEn, c.Code)).ToListAsync(ct);
			ViewBag.Branches = await _db.Branches.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId).OrderBy(b => b.Name)
				.Select(b => new HospitalChoice(b.ID, b.NameAr ?? b.Name, b.Name, null)).ToListAsync(ct);
			return View();
		}

		[HttpPost("Kitchen/Setting"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> KitchenSaveSetting([FromBody] HospitalKitchenSettingInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await KitchenSvc.SaveSettingAsync(ctx, new HospitalKitchenSetting
			{
				KitchenWarehouseId = input.KitchenWarehouseId, PoolAccountId = input.PoolAccountId, PatientMealsAccountId = input.PatientMealsAccountId,
				StaffMealsAccountId = input.StaffMealsAccountId, CafeteriaCogsAccountId = input.CafeteriaCogsAccountId,
			}, input.Recipes, DateTime.UtcNow, ct));
		}

		[HttpPost("Kitchen/Destinations"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> KitchenSaveDestination([FromBody] HospitalKitchenDestination input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await KitchenSvc.SaveDestinationAsync(ctx, input, DateTime.UtcNow, ct));
		}
	}
}
