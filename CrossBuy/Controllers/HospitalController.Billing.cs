using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H6: billing and the cashier. The service catalogue, billing settings (tax and the
	// GL account map), bills, payments and cashier shifts. Every rule is HospitalBillingService's,
	// and the general ledger is reached only through the accounting journal engine.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalAddLineInput { public int ServiceId { get; set; } public decimal Quantity { get; set; } = 1; }
		public sealed class HospitalCollectInput { public decimal Amount { get; set; } public string Method { get; set; } = "Cash"; }
		public sealed class HospitalOpenShiftInput { public string Desk { get; set; } = ""; public decimal OpeningFloat { get; set; } }
		public sealed class HospitalCloseShiftInput { public Dictionary<string, int> Counts { get; set; } = new(); public string? HandoverTo { get; set; } }

		private async Task<List<HospitalChoice>> PostingAccountsAsync(BusinessContext ctx, CancellationToken ct)
			=> await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == ctx.CompanyId && a.IsActive && a.IsPostable).OrderBy(a => a.Code)
				.Select(a => new HospitalChoice(a.ID, a.Name, a.NameEn, a.Code)).ToListAsync(ct);

		// ---------------- the service catalogue ----------------

		[HttpGet("Services")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Services(string? category, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Category = HospitalServiceCategories.IsKnown(category) ? category : null;
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Departments = await _outpatient.DepartmentsAsync(ctx, ct);
			ViewBag.Accounts = await PostingAccountsAsync(ctx, ct);
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			ViewBag.All = await _billing.ServicesAsync(ctx, null, false, ct);
			return View(await _billing.ServicesAsync(ctx, ViewBag.Category as string, false, ct));
		}

		[HttpPost("Services/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveService([FromBody] HospitalServiceInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _billing.SaveServiceAsync(ctx, input ?? new(), ctx.EmployeeId, ct));
		}

		[HttpPost("Services/SeedDefaults"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SeedDefaultServices(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _billing.SeedDefaultServicesAsync(ctx, ctx.EmployeeId, ct));
		}

		/// The service picker (select2): `orderable` limits it to what a doctor orders.
		[HttpGet("Services/Search")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> SearchServices(string? q, bool orderable = false, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var term = (q ?? "").Trim();
			var currency = await _facility.CurrencyAsync(ctx, ct);
			var all = await _billing.ServicesAsync(ctx, null, true, ct);
			var hits = all.Where(s => s.Category != HospitalServiceCategories.Accommodation
					&& (!orderable || HospitalServiceCategories.Orderable.Contains(s.Category))
					&& (term.Length == 0 || s.Code.Contains(term, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(term) || s.NameEn.Contains(term, StringComparison.OrdinalIgnoreCase)))
				.Take(40);
			return Json(new
			{
				ok = true,
				results = hits.Select(s => new
				{
					id = s.ID, code = s.Code, name = DisplayName.Of(s.Name, s.NameEn), category = s.Category, price = s.Price,
					text = s.Code + " — " + DisplayName.Of(s.Name, s.NameEn) + " · " + (currency == null ? s.Price.ToString("0.00") : currency.Format(s.Price) + " " + currency.Label(DisplayName.IsArabic)),
				}),
			});
		}

		// ---------------- billing settings ----------------

		[HttpGet("Settings/Billing")]
		[HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> BillingSettings(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.Accounts = await PostingAccountsAsync(ctx, ct);
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			return View(await _billing.SettingsAsync(ctx, ct));
		}

		[HttpPost("Settings/Billing"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveBillingSettings([FromBody] HospitalBillingSettingsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _billing.SaveSettingsAsync(ctx, input ?? new(), ctx.EmployeeId, ct));
		}

		// ---------------- bills ----------------

		[HttpGet("Bills")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Bills(string? status, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			// Stays admitted before billing existed get their bill the first time the list is opened.
			var open = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.Status == HospitalAdmissionStatus.Active).Select(a => a.ID).ToListAsync(ct);
			foreach (var id in open) await _billing.EnsureAdmissionBillAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct);
			Chrome();
			ViewBag.Status = status is HospitalBillStatus.Open or HospitalBillStatus.Closed ? status : null;
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			return View(await _billing.BillsAsync(ctx, ViewBag.Status as string, null, DateTime.UtcNow, ct));
		}

		[HttpGet("Bill/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Bill(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var v = await _billing.BillAsync(ctx, id, DateTime.UtcNow, ct);
			if (v == null) return NotFound();
			Chrome();
			var (journal, missing) = await _billing.PreviewAsync(ctx, v, ct);
			var accIds = journal.Select(j => j.AccountId).Distinct().ToList();
			ViewBag.Journal = journal;
			ViewBag.JournalMissing = missing;
			ViewBag.AccountNames = await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == ctx.CompanyId && accIds.Contains(a.ID))
				.ToDictionaryAsync(a => a.ID, a => a.Code + " · " + DisplayName.Of(a.Name, a.NameEn), ct);
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			ViewBag.MyShift = ctx.EmployeeId is int me ? await _billing.OpenShiftOfAsync(ctx, me, ct) : null;
			if (v.Bill.AdmissionId is int adm) ViewBag.Admission = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == adm && a.CompanyId == ctx.CompanyId, ct);
			if (v.Bill.EncounterId is int enc) ViewBag.Encounter = await _db.HospitalEncounters.AsNoTracking().FirstOrDefaultAsync(e => e.ID == enc && e.CompanyId == ctx.CompanyId, ct);
			return View(v);
		}

		[HttpPost("Bill/{id:int}/Lines"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> AddBillLine(int id, [FromBody] HospitalAddLineInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _billing.AddLineAsync(ctx, id, input?.ServiceId ?? 0, input?.Quantity ?? 1, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("BillLines/{id:int}/Remove"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> RemoveBillLine(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _billing.RemoveLineAsync(ctx, id, ct));
		}

		[HttpPost("Bill/{id:int}/Collect"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> CollectPayment(int id, [FromBody] HospitalCollectInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx?.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _billing.CollectAsync(ctx, id, input?.Amount ?? 0, input?.Method ?? "", me, DateTime.UtcNow, ct));
		}

		[HttpPost("Bill/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> CloseBill(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _billing.CloseAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		// ---------------- the cashier ----------------

		[HttpGet("Cashier")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Cashier(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var currency = await _facility.CurrencyAsync(ctx, ct);
			ViewBag.Currency = currency;
			ViewBag.Denominations = HospitalBillingService.Denominations(currency?.Code);
			ViewBag.CanBill = await _access.CanAsync(ctx, HospitalActions.Bill, null, ct);
			var mine = ctx.EmployeeId is int me ? await _billing.OpenShiftOfAsync(ctx, me, ct) : null;
			ViewBag.Mine = mine == null ? null : await _billing.ShiftSummaryAsync(ctx, mine.ID, ct);
			var today = await _billing.ShiftsOfDayAsync(ctx, DateTime.UtcNow.Date, ct);
			var ids = today.Select(s => s.Shift.CashierEmployeeId).Append(ctx.EmployeeId ?? 0).Distinct().ToList();
			ViewBag.People = (await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct)).ToDictionary(e => e.ID, e => (Name: DisplayName.Of(e.FullName, e.FullNameEn), En: e.FullNameEn ?? e.FullName));
			ViewBag.Photos = await EmployeePhotos.ResolveAsync(_db, ctx, ids, ct);
			ViewBag.Me = ctx.EmployeeId;
			return View(today);
		}

		[HttpPost("Cashier/Open"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> OpenShift([FromBody] HospitalOpenShiftInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx?.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _billing.OpenShiftAsync(ctx, input?.Desk ?? "", input?.OpeningFloat ?? 0, me, DateTime.UtcNow, ct));
		}

		[HttpPost("Cashier/{id:int}/Close"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Bill)]
		public async Task<IActionResult> CloseShift(int id, [FromBody] HospitalCloseShiftInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx?.EmployeeId is not int me) return NotFound(new { ok = false, code = "not_found" });
			var counts = new Dictionary<decimal, int>();
			foreach (var kv in input?.Counts ?? new())
				if (decimal.TryParse(kv.Key, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d)) counts[d] = kv.Value;
			return Result(await _billing.CloseShiftAsync(ctx, id, counts, input?.HandoverTo, me, DateTime.UtcNow, ct));
		}
	}
}
