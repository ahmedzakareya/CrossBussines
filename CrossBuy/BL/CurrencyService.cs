using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	/// Multi-Currency 1-1 foundation service.
	/// Rates are quoted as "EGP per 1 unit of currency" (EGP is the system pivot). Conversion from a
	/// transaction currency C to a branch functional currency F uses the EGP pivot:
	///     amountInF = amount * rate(C->EGP) / rate(F->EGP)
	/// so it stays correct even if a branch's functional currency is not EGP. All GL/stock postings
	/// must receive amounts ALREADY in the functional (base) currency — this service does that conversion.
	public interface ICurrencyService
	{
		/// Functional currency of a branch: Branch.FunctionalCurrencyId → Company.DefaultCurrencyId → EGP.
		Task<int> GetFunctionalCurrencyIdAsync(int companyId, int? branchId);
		Task<Currency?> GetCurrencyAsync(int currencyId);
		/// Latest rate (currency → EGP) effective on/before <paramref name="date"/> for the given type.
		/// EGP returns 1. Returns (0,false) if no rate is found for a foreign currency.
		Task<(decimal rate, bool found)> GetRateToEgpAsync(int currencyId, DateTime date, string rateType);
		/// Convert <paramref name="amount"/> from <paramref name="fromCurrencyId"/> to the functional
		/// currency. Returns the base amount (rounded to 4 dp) + the effective from→functional rate used.
		/// Throws InvalidOperationException with an Arabic message if a required rate is missing.
		Task<(decimal baseAmount, decimal effectiveRate)> ToBaseAsync(decimal amount, int fromCurrencyId, int functionalCurrencyId, DateTime date, string rateType);
		Task<(bool stale, int ageDays, int maxAgeDays, string behavior)> RateStalenessAsync(int companyId, int currencyId, DateTime asOf);   // HM-D23
	}

	public class CurrencyService : ICurrencyService
	{
		private readonly CrossDbContext _db;
		public CurrencyService(CrossDbContext db) { _db = db; }

		private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

		private async Task<int> EgpIdAsync() =>
			await _db.Currencies.AsNoTracking().Where(c => c.Code == "EGP").Select(c => c.ID).FirstOrDefaultAsync();

		public async Task<int> GetFunctionalCurrencyIdAsync(int companyId, int? branchId)
		{
			if (branchId.HasValue)
			{
				var bc = await _db.Branches.AsNoTracking().Where(b => b.ID == branchId.Value)
					.Select(b => b.FunctionalCurrencyId).FirstOrDefaultAsync();
				if (bc.HasValue && bc.Value > 0) return bc.Value;
			}
			var cc = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == companyId)
				.Select(c => c.DefaultCurrencyId).FirstOrDefaultAsync();
			if (cc.HasValue && cc.Value > 0) return cc.Value;
			return await EgpIdAsync();
		}

		public Task<Currency?> GetCurrencyAsync(int currencyId) =>
			_db.Currencies.AsNoTracking().FirstOrDefaultAsync(c => c.ID == currencyId);

		public async Task<(decimal rate, bool found)> GetRateToEgpAsync(int currencyId, DateTime date, string rateType)
		{
			var egp = await EgpIdAsync();
			if (currencyId == egp) return (1m, true);
			var d = date.Date;
			var rate = await _db.ExchangeRates.AsNoTracking()
				.Where(r => r.CurrencyId == currencyId && r.RateType == rateType && r.RateDate <= d)
				.OrderByDescending(r => r.RateDate).ThenByDescending(r => r.ID)
				.Select(r => (decimal?)r.Rate).FirstOrDefaultAsync();
			// fall back to any rate type on/before the date if the requested type is absent
			if (rate == null)
				rate = await _db.ExchangeRates.AsNoTracking()
					.Where(r => r.CurrencyId == currencyId && r.RateDate <= d)
					.OrderByDescending(r => r.RateDate).ThenByDescending(r => r.ID)
					.Select(r => (decimal?)r.Rate).FirstOrDefaultAsync();
			return rate.HasValue ? (rate.Value, true) : (0m, false);
		}

		public async Task<(decimal baseAmount, decimal effectiveRate)> ToBaseAsync(
			decimal amount, int fromCurrencyId, int functionalCurrencyId, DateTime date, string rateType)
		{
			if (fromCurrencyId == functionalCurrencyId || amount == 0m)
				return (R4(amount), 1m);

			var (fromRate, f1) = await GetRateToEgpAsync(fromCurrencyId, date, rateType);
			if (!f1) throw new InvalidOperationException($"لا يوجد سعر صرف للعملة المصدر بتاريخ {date:yyyy-MM-dd} ({rateType}).");
			var (funcRate, f2) = await GetRateToEgpAsync(functionalCurrencyId, date, rateType);
			if (!f2 || funcRate == 0m) throw new InvalidOperationException($"لا يوجد سعر صرف للعملة الوظيفية بتاريخ {date:yyyy-MM-dd} ({rateType}).");

			var effectiveRate = R4(fromRate / funcRate);   // from → functional
			return (R4(amount * fromRate / funcRate), effectiveRate);
		}

		// HM-D23: sales that PROCEEDED with a stale rate (Warn), counted since boot — surfaced by inv-test-integrity (never fails).
		public static long StaleRateSales;

		// HM-D23: is the looked-up rate for `currencyId` older than the company's RateMaxAgeDays (measured vs the DOCUMENT date asOf)?
		// Returns (stale, ageDays, maxAgeDays, behavior). stale=false when currency==functional, MaxAgeDays==0, or no rate found
		// (the existing "no rate → throw" path handles a missing rate). Age is vs the document date so a legitimately BACK-DATED
		// document is judged against the rate valid for its own date, not today.
		public async Task<(bool stale, int ageDays, int maxAgeDays, string behavior)> RateStalenessAsync(int companyId, int currencyId, DateTime asOf)
		{
			var functional = await GetFunctionalCurrencyIdAsync(companyId, null);
			var s = await _db.AccountingSettings.AsNoTracking().Where(x => x.CompanyID == companyId).Select(x => new { x.RateMaxAgeDays, x.RateStaleBehavior }).FirstOrDefaultAsync();
			string behavior = string.IsNullOrWhiteSpace(s?.RateStaleBehavior) ? "Warn" : s!.RateStaleBehavior;
			int maxAge = s?.RateMaxAgeDays ?? 0;
			if (currencyId == functional || maxAge <= 0) return (false, 0, maxAge, behavior);   // no conversion, or no limit set (default)
			var d = asOf.Date;
			var rateDate = await _db.ExchangeRates.AsNoTracking().Where(r => r.CurrencyId == currencyId && r.RateDate <= d)
				.OrderByDescending(r => r.RateDate).ThenByDescending(r => r.ID).Select(r => (DateTime?)r.RateDate).FirstOrDefaultAsync();
			if (rateDate == null) return (false, 0, maxAge, behavior);   // no rate at all — the throw on ToBaseAsync handles it
			int age = (int)(d - rateDate.Value.Date).TotalDays;
			return (age > maxAge, age, maxAge, behavior);
		}
	}
}
