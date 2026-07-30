using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using CrossBuy.Models.Context;

namespace CrossBuy.BL
{
	// HM-2: the SINGLE source of currency rounding precision. No value/cost path reads Currencies.DecimalPlaces
	// directly or hardcodes Math.Round(v, 2) — every money rounding resolves its decimals through here.
	// - currencyId != null  → that currency's DecimalPlaces (the DOCUMENT currency: KWD=3, EGP=2 …).
	// - currencyId == null   → the company's FUNCTIONAL currency decimals (used for GL Debit/Credit and inventory cost).
	// Never falls back to a silent 2: an undefined currency / missing decimals throws (conservative + explicit).
	public interface ICurrencyRounding
	{
		Task<int> DecimalsAsync(int companyId, int? currencyId, int? branchId = null);
		Task<decimal> RoundAsync(int companyId, decimal value, int? currencyId, int? branchId = null);
	}

	public class CurrencyRounding : ICurrencyRounding
	{
		private readonly CrossDbContext _db;
		private readonly ICurrencyService _currency;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		// DbContext is Scoped, so this per-instance cache is per-request — safe, no cross-request bleed.
		private readonly Dictionary<int, int> _cache = new();

		public CurrencyRounding(CrossDbContext db, ICurrencyService currency, IStringLocalizer<CrossBuy.SharedResources> localizer) { _db = db; _currency = currency; L = localizer; }

		public async Task<int> DecimalsAsync(int companyId, int? currencyId, int? branchId = null)
		{
			int cid = currencyId ?? await _currency.GetFunctionalCurrencyIdAsync(companyId, branchId);
			if (cid <= 0)
				throw new InvalidOperationException(L["Cannot determine the currency for value rounding (no document currency and no company functional currency)."]);
			if (_cache.TryGetValue(cid, out var dp)) return dp;
			var val = await _db.Currencies.AsNoTracking().Where(c => c.ID == cid).Select(c => (int?)c.DecimalPlaces).FirstOrDefaultAsync();
			if (val == null)
				throw new InvalidOperationException(L["Currency #{0} is undefined or has no decimal precision — silent 2-decimal rounding is not allowed.", cid]);
			_cache[cid] = val.Value;
			return val.Value;
		}

		public async Task<decimal> RoundAsync(int companyId, decimal value, int? currencyId, int? branchId = null)
			=> Math.Round(value, await DecimalsAsync(companyId, currencyId, branchId), MidpointRounding.AwayFromZero);
	}
}
