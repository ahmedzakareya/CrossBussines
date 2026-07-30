using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// Multi-Currency 1-2: master screens — currencies, exchange rates, and branch/company
	// functional-currency setup (stable: locked after the first transaction).
	[SessionValidation]
	public class CurrencyController : Controller
	{
		private readonly CrossDbContext _context;
		private readonly ICurrencyService _currency;
		private readonly IFxRevaluationService _reval;
		private const int DefaultCompanyId = 1;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public CurrencyController(CrossDbContext context, ICurrencyService currency, IFxRevaluationService reval, IStringLocalizer<CrossBuy.SharedResources> localizer) { _context = context; _currency = currency; _reval = reval; L = localizer; }

		// whether the company books already have postings → functional currency becomes immutable
		private Task<bool> HasPostingsAsync() => _context.JournalEntries.AnyAsync(e => e.CompanyID == DefaultCompanyId);

		// ---------------- Currencies ----------------
		[HttpGet]
		public async Task<IActionResult> Currencies() =>
			View(await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync());

		[HttpGet]
		public async Task<IActionResult> CurrenciesData()   // for any ajax consumers (kept simple)
			=> Json(await _context.Currencies.AsNoTracking().OrderBy(c => c.Code)
				.Select(c => new { c.ID, c.Code, c.Symbol, c.Name, c.NameEn, c.DecimalPlaces }).ToListAsync());

		[HttpPost][ValidateAntiForgeryToken][AccPerm("manage")]
		public async Task<IActionResult> SaveCurrency(int id, string code, string? symbol, string name, string? nameEn, byte decimalPlaces)
		{
			code = (code ?? "").Trim().ToUpperInvariant();
			if (code.Length < 2 || string.IsNullOrWhiteSpace(name))
			{ TempData["AccErr"] = L["The code and name are required"].Value; return RedirectToAction(nameof(Currencies)); }
			if (decimalPlaces > 4) decimalPlaces = 4;

			if (id > 0)
			{
				var c = await _context.Currencies.FirstOrDefaultAsync(x => x.ID == id);
				if (c == null) { TempData["AccErr"] = L["Currency not found"].Value; return RedirectToAction(nameof(Currencies)); }
				// code stays unique; block rename onto another currency's code
				if (await _context.Currencies.AnyAsync(x => x.ID != id && x.Code == code))
				{ TempData["AccErr"] = L["The currency code is already in use"].Value; return RedirectToAction(nameof(Currencies)); }
				c.Code = code; c.Symbol = symbol; c.Name = name; c.NameEn = nameEn; c.DecimalPlaces = decimalPlaces;
			}
			else
			{
				if (await _context.Currencies.AnyAsync(x => x.Code == code))
				{ TempData["AccErr"] = L["The currency code already exists"].Value; return RedirectToAction(nameof(Currencies)); }
				_context.Currencies.Add(new Currency { Code = code, Symbol = symbol, Name = name, NameEn = nameEn, DecimalPlaces = decimalPlaces });
			}
			await _context.SaveChangesAsync();
			TempData["AccMsg"] = id > 0 ? L["Currency updated"].Value : L["Currency added"].Value;
			return RedirectToAction(nameof(Currencies));
		}

		// ---------------- Exchange rates ----------------
		[HttpGet]
		public async Task<IActionResult> ExchangeRates()
		{
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			var rows = await (from r in _context.ExchangeRates.AsNoTracking()
							  join c in _context.Currencies.AsNoTracking() on r.CurrencyId equals c.ID
							  orderby r.RateDate descending, c.Code
							  select new ExchangeRateRow { ID = r.ID, CurrencyId = c.ID, Code = c.Code, Name = c.Name, NameEn = c.NameEn, RateDate = r.RateDate, RateType = r.RateType, Rate = r.Rate })
							 .Take(500).ToListAsync();
			return View(rows);
		}

		[HttpPost][ValidateAntiForgeryToken][AccPerm("post")]
		public async Task<IActionResult> SaveExchangeRate(int currencyId, DateTime rateDate, string rateType, decimal rate)
		{
			var allowed = new[] { "Buy", "Sell", "Central" };
			if (currencyId <= 0 || rate <= 0 || !allowed.Contains(rateType))
			{ TempData["AccErr"] = L["Invalid exchange rate data"].Value; return RedirectToAction(nameof(ExchangeRates)); }
			var d = rateDate.Date;
			var existing = await _context.ExchangeRates.FirstOrDefaultAsync(x => x.CurrencyId == currencyId && x.RateDate == d && x.RateType == rateType);
			if (existing != null) existing.Rate = rate;
			else _context.ExchangeRates.Add(new ExchangeRate { CurrencyId = currencyId, RateDate = d, RateType = rateType, Rate = rate });
			await _context.SaveChangesAsync();
			TempData["AccMsg"] = existing != null ? L["Exchange rate updated"].Value : L["Exchange rate added"].Value;
			return RedirectToAction(nameof(ExchangeRates));
		}

		// ---------------- Functional-currency setup (stable) ----------------
		[HttpGet]
		public async Task<IActionResult> Setup()
		{
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			ViewBag.HasPostings = await HasPostingsAsync();
			ViewBag.Company = await _context.Companies.AsNoTracking()
				.Where(c => c.CompanyID == DefaultCompanyId)
				.FirstOrDefaultAsync();
			return View(await _context.Branches.AsNoTracking().Where(b => b.CompanyID == DefaultCompanyId).ToListAsync());
		}

		[HttpPost][ValidateAntiForgeryToken][AccPerm("manage")]
		public async Task<IActionResult> SetCompanyCurrency(int currencyId)
		{
			if (await HasPostingsAsync())
			{ TempData["AccErr"] = L["The company currency cannot be changed once postings exist — the currency is locked"].Value; return RedirectToAction(nameof(Setup)); }
			if (!await _context.Currencies.AnyAsync(c => c.ID == currencyId))
			{ TempData["AccErr"] = L["Currency not found"].Value; return RedirectToAction(nameof(Setup)); }
			var co = await _context.Companies.FirstOrDefaultAsync(c => c.CompanyID == DefaultCompanyId);
			if (co != null) { co.DefaultCurrencyId = currencyId; await _context.SaveChangesAsync(); TempData["AccMsg"] = L["The company default currency has been set"].Value; }
			return RedirectToAction(nameof(Setup));
		}

		[HttpPost][ValidateAntiForgeryToken][AccPerm("manage")]
		public async Task<IActionResult> SetBranchCurrency(int branchId, int currencyId)
		{
			var b = await _context.Branches.FirstOrDefaultAsync(x => x.ID == branchId && x.CompanyID == DefaultCompanyId);
			if (b == null) { TempData["AccErr"] = L["Branch not found"].Value; return RedirectToAction(nameof(Setup)); }
			if (b.CurrencyLockedAt != null)
			{ TempData["AccErr"] = L["This branch currency is locked after the first transaction — it cannot be changed"].Value; return RedirectToAction(nameof(Setup)); }
			if (!await _context.Currencies.AnyAsync(c => c.ID == currencyId))
			{ TempData["AccErr"] = L["Currency not found"].Value; return RedirectToAction(nameof(Setup)); }
			b.FunctionalCurrencyId = currencyId;
			await _context.SaveChangesAsync();
			TempData["AccMsg"] = L["The branch functional currency has been set"].Value;
			return RedirectToAction(nameof(Setup));
		}

		// ---------------- Period-end FX revaluation (unrealized) ----------------
		[HttpGet]
		public async Task<IActionResult> Revaluation(DateTime? asOf, string rateType = "Central")
		{
			var d = asOf ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddDays(-1); // default: end of last month
			ViewBag.AsOf = d; ViewBag.RateType = rateType;
			ViewBag.History = await _reval.HistoryAsync(DefaultCompanyId);
			return View(await _reval.PreviewAsync(DefaultCompanyId, d, rateType));
		}

		[HttpPost][ValidateAntiForgeryToken][AccPerm("manage")]
		public async Task<IActionResult> PostRevaluation(DateTime asOf, string rateType)
		{
			var (ok, err, _) = await _reval.PostAsync(DefaultCompanyId, asOf, rateType, null);
			TempData[ok ? "AccMsg" : "AccErr"] = ok ? L["The revaluation and its automatic reversing entry have been posted"].Value : err;
			return RedirectToAction(nameof(Revaluation), new { asOf = asOf.ToString("yyyy-MM-dd"), rateType });
		}

		public class ExchangeRateRow
		{
			public int ID { get; set; }
			public int CurrencyId { get; set; }
			public string Code { get; set; } = "";
			public string Name { get; set; } = "";
			public string? NameEn { get; set; }
			public DateTime RateDate { get; set; }
			public string RateType { get; set; } = "";
			public decimal Rate { get; set; }
		}
	}
}
