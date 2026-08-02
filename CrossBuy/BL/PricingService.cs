using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// P3-5 pricing engine result for one item line.
	public class PriceResult
	{
		public decimal UnitPrice { get; set; }
		public decimal DiscountPercent { get; set; }
		public int? PriceListId { get; set; }
		public string? PriceListName { get; set; }
		public string? PriceListNameEn { get; set; }
		public int? CurrencyId { get; set; }            // currency the UnitPrice is expressed in
		public string Source { get; set; } = "base";   // base (SalesPrice, functional) | list (fixed) | converted (temp, foreign) | none
		// Pricing 2B — the promotion folded into DiscountPercent, if any (for display).
		public int? PromotionId { get; set; }
		public string? PromotionName { get; set; }
		public string? PromotionNameEn { get; set; }
	}

	// Pricing 2A — result of a gross-margin floor check for one item line (all amounts functional).
	public class MarginCheckResult
	{
		public string Mode { get; set; } = "Off";        // Off | Warn | Block
		public bool Ok { get; set; } = true;             // price ≥ floor (or check not applicable)
		public bool Skipped { get; set; }                // cost unknown (0) → no judgement, never a false block
		public decimal CostFunctional { get; set; }
		public decimal MarginPct { get; set; }
		public decimal FloorFunctional { get; set; }
		public decimal PriceFunctional { get; set; }
		public string? ItemName { get; set; }
	}

	// flattened price-list row for the management grid (with line count)
	public class PriceListRow
	{
		public int Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int? CustomerId { get; set; }
		public string? CustomerName { get; set; }
		public string? Segment { get; set; }
		public int? CurrencyId { get; set; }
		public string? CurrencyCode { get; set; }
		public int Priority { get; set; }
		public bool IsDefault { get; set; }
		public bool IsActive { get; set; }
		public DateTime? ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
		public int LineCount { get; set; }
	}

	// flattened promotion row for the management grid
	public class PromotionRow
	{
		public int Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string DiscountType { get; set; } = "Percent";
		public decimal Value { get; set; }
		public int? CurrencyId { get; set; }
		public string? CurrencyCode { get; set; }
		public int? ItemId { get; set; }
		public string? ItemName { get; set; }
		public int? CustomerId { get; set; }
		public string? CustomerName { get; set; }
		public string? Segment { get; set; }
		public decimal MinQty { get; set; }
		public int Priority { get; set; }
		public DateTime? ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
		public bool IsActive { get; set; }
	}

	public interface IPricingService
	{
		// Resolve the effective unit price + discount for an item, given the customer (specific + segment), the
		// document currency, ordered qty and date. Price lists are matched by currency (foreign price is fixed);
		// priority: customer-specific > segment > general, then list Priority, then the most specific qty break.
		Task<PriceResult> GetPriceAsync(int companyId, int itemId, int? customerId, string? segment, int? currencyId, decimal qty, DateTime asOf, int? priceListId = null);
		// Pricing 2A — gross-margin floor. netUnitPrice is in the document currency; converted to functional before comparing
		// to cost×(1+margin%). currencyId/exchangeRate describe the document; if rate missing it is resolved (Sell). asOf = doc date.
		Task<MarginCheckResult> CheckMarginAsync(int companyId, int itemId, decimal netUnitPrice, int? currencyId, decimal? exchangeRate, DateTime asOf);
		// Pricing 2D — discount approval ceiling. Returns (block, warn): block != null → reject (needs manage authority);
		// warn != null → allow + notify. canApprove = the user holds manage authority.
		Task<(string? block, string? warn)> EvaluateLineDiscountsAsync(int companyId, IEnumerable<(decimal Qty, decimal UnitPrice, decimal DiscountAmount)> lines, bool canApprove);
		// management
		Task<(List<PriceListRow> rows, int total)> SearchAsync(int companyId, string? q, bool? active, int page, int pageSize);
		Task<Models.Context.Inventory.PriceList?> GetAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveAsync(int companyId, Models.Context.Inventory.PriceList dto, string? userId);
		Task<bool> DeleteAsync(int companyId, int id);
		// Pricing 2B — promotion management
		Task<(List<PromotionRow> rows, int total)> SearchPromotionsAsync(int companyId, string? q, bool? active, int page, int pageSize);
		Task<Models.Context.Inventory.Promotion?> GetPromotionAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SavePromotionAsync(int companyId, Models.Context.Inventory.Promotion dto, string? userId);
		Task<bool> DeletePromotionAsync(int companyId, int id);
	}

	public class PricingService : IPricingService
	{
		private readonly CrossDbContext _context;
		private readonly ICurrencyService _currency;
		private readonly IManufService _manuf;
		private readonly ICurrencyRounding _rounding;
		public PricingService(CrossDbContext context, ICurrencyService currency, IManufService manuf, ICurrencyRounding rounding) { _context = context; _currency = currency; _manuf = manuf; _rounding = rounding; }


		public async Task<PriceResult> GetPriceAsync(int companyId, int itemId, int? customerId, string? segment, int? currencyId, decimal qty, DateTime asOf, int? priceListId = null)
		{
			var q = qty <= 0 ? 1m : qty;
			var seg = string.IsNullOrWhiteSpace(segment) ? null : segment.Trim();
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var docCur = currencyId ?? functional;
			int __ddp = await _rounding.DecimalsAsync(companyId, docCur);   // HM-2 Batch 5: price in the DOCUMENT currency dp (no static R)
			decimal R(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);

			// candidates: active list, item matches, qty break + validity satisfied,
			// currency matches the document (null list currency == functional), and targeting allows this customer
			var candidates = await (
				from l in _context.PriceListLines.AsNoTracking()
				join pl in _context.PriceLists.AsNoTracking() on l.PriceListId equals pl.ID
				where pl.CompanyID == companyId && pl.IsActive && l.ItemId == itemId && l.MinQty <= q
					&& (priceListId == null || pl.ID == priceListId.Value)   // HM-D18: when a branch list is passed, restrict candidates to it
					&& ((pl.CurrencyId ?? functional) == docCur || l.PricingMode == "CostPlus")   // cost-plus is currency-agnostic (computed then converted)
					&& ((customerId != null && pl.CustomerId == customerId) || (pl.CustomerId == null && (pl.Segment == null || pl.Segment == seg)))
					&& (pl.ValidFrom == null || pl.ValidFrom <= asOf) && (pl.ValidTo == null || pl.ValidTo >= asOf)
					&& (l.ValidFrom == null || l.ValidFrom <= asOf) && (l.ValidTo == null || l.ValidTo >= asOf)
				select new { l.UnitPrice, l.DiscountPercent, l.MinQty, l.PricingMode, l.MarkupPercent, pl.Priority, pl.CustomerId, pl.Segment, pl.Name, pl.NameEn, ListId = pl.ID }
			).ToListAsync();

			var itemInfo = await _context.Items.AsNoTracking()
				.Where(i => i.ID == itemId && i.CompanyID == companyId)
				.Select(i => new { i.SalesPrice, i.ItemCategoryId }).FirstOrDefaultAsync();
			var basePrice = itemInfo?.SalesPrice ?? 0m;
			int? itemCategoryId = itemInfo?.ItemCategoryId;

			// rank: customer-specific > segment-specific > general, then higher Priority, then most-specific qty break
			var best = candidates
				.OrderByDescending(x => customerId != null && x.CustomerId == customerId)
				.ThenByDescending(x => x.Segment != null)
				.ThenByDescending(x => x.Priority)
				.ThenByDescending(x => x.MinQty)
				.FirstOrDefault();

			PriceResult result;
			if (best != null)
			{
				var disc = best.DiscountPercent < 0 ? 0m : (best.DiscountPercent > 100 ? 100m : best.DiscountPercent);
				if (best.PricingMode == "CostPlus")
				{
					// Pricing 2C — compute from the current cost (functional) × (1+markup), then convert + round to doc decimals
					decimal costFunc = await ResolveItemCostAsync(companyId, itemId);
					decimal markup = best.MarkupPercent < 0 ? 0m : best.MarkupPercent;
					decimal priceFunc = costFunc * (1 + markup / 100m);
					decimal priceDoc = await FromFunctionalAsync(priceFunc, docCur, functional, asOf);
					result = new PriceResult { UnitPrice = await RoundToCurrencyAsync(companyId, priceDoc, docCur), DiscountPercent = disc, PriceListId = best.ListId, PriceListName = best.Name, PriceListNameEn = best.NameEn, CurrencyId = docCur, Source = "costplus" };
				}
				else
				{
					var price = best.UnitPrice ?? basePrice;   // null line price = use base (functional) — only meaningful for functional-currency lists
					result = new PriceResult { UnitPrice = R(price), DiscountPercent = disc, PriceListId = best.ListId, PriceListName = best.Name, PriceListNameEn = best.NameEn, CurrencyId = docCur, Source = "list" };
				}
			}
			else if (docCur == functional)
			{
				// no list in the functional currency — fall back to base
				result = new PriceResult { UnitPrice = R(basePrice), DiscountPercent = 0m, CurrencyId = functional, Source = "base" };
			}
			else
			{
				// foreign document, no foreign list → optional TEMPORARY converted suggestion (admin setting), else manual
				var allowConvert = (await _context.InventorySettings.AsNoTracking().Where(s => s.CompanyID == companyId)
					.Select(s => (bool?)s.ConvertBasePriceForForeignDocs).FirstOrDefaultAsync()) ?? true;
				result = new PriceResult { UnitPrice = 0m, DiscountPercent = 0m, CurrencyId = docCur, Source = "none" };
				if (allowConvert && basePrice > 0)
				{
					var (rate, found) = await _currency.GetRateToEgpAsync(docCur, asOf, "Sell");   // docCur→functional (EGP pivot)
					if (found && rate > 0)
						result = new PriceResult { UnitPrice = R(basePrice / rate), DiscountPercent = 0m, CurrencyId = docCur, Source = "converted" };
				}
			}

			// Pricing 2B — layer the best matching promotion on top (sequential discount, in doc currency).
			await ApplyBestPromotionAsync(companyId, itemId, itemCategoryId, customerId, seg, docCur, functional, q, asOf, result);
			return result;
		}

		// Pricing 2B — find the single best active promotion and fold it into result.DiscountPercent
		// (sequential: net = unit×(1−listDisc)×… then −promo). Amount discounts convert to the document currency.
		private async Task ApplyBestPromotionAsync(int companyId, int itemId, int? itemCategoryId, int? customerId, string? seg,
			int docCur, int functional, decimal q, DateTime asOf, PriceResult result)
		{
			if (result.UnitPrice <= 0) return;   // nothing to discount (source=none / manual entry)
			int __ddp = await _rounding.DecimalsAsync(companyId, docCur);   // HM-2 Batch 5: promotion amounts in the DOCUMENT currency dp (no static R)
			decimal R(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			var promos = await _context.Promotions.AsNoTracking()
				.Where(p => p.CompanyID == companyId && p.IsActive && p.MinQty <= q
					&& (p.ValidFrom == null || p.ValidFrom <= asOf) && (p.ValidTo == null || p.ValidTo >= asOf)
					&& (p.ItemId == null || p.ItemId == itemId)
					&& (p.ItemCategoryId == null || p.ItemCategoryId == itemCategoryId)
					&& (p.CustomerId == null || p.CustomerId == customerId)
					&& (p.Segment == null || p.Segment == seg))
				.ToListAsync();
			if (promos.Count == 0) return;

			decimal netAfterList = R(result.UnitPrice * (1 - result.DiscountPercent / 100m));
			if (netAfterList <= 0) return;

			// evaluate each promotion's per-unit discount amount in the document currency; the largest wins (tie → Priority)
			Models.Context.Inventory.Promotion? bestPromo = null;
			decimal bestDiscAmt = 0m;
			foreach (var p in promos)
			{
				decimal discAmt;
				if (p.DiscountType == "Amount")
				{
					decimal amtDoc = await ConvertToDocAsync(companyId, p.Value, p.CurrencyId ?? functional, docCur, functional, asOf);
					discAmt = Math.Min(amtDoc, netAfterList);   // a fixed discount can't exceed the net
				}
				else
				{
					decimal pct = p.Value < 0 ? 0m : (p.Value > 100 ? 100m : p.Value);
					discAmt = R(netAfterList * pct / 100m);
				}
				if (discAmt <= 0) continue;
				if (bestPromo == null || discAmt > bestDiscAmt || (discAmt == bestDiscAmt && p.Priority > bestPromo.Priority))
				{ bestPromo = p; bestDiscAmt = discAmt; }
			}
			if (bestPromo == null || bestDiscAmt <= 0) return;

			decimal netFinal = R(netAfterList - bestDiscAmt);
			if (netFinal < 0) netFinal = 0m;
			decimal effDisc = R((1 - netFinal / result.UnitPrice) * 100m);   // fold list + promo into one effective %
			if (effDisc < 0) effDisc = 0m; else if (effDisc > 100) effDisc = 100m;
			result.DiscountPercent = effDisc;
			result.PromotionId = bestPromo.ID;
			result.PromotionName = bestPromo.Name;
			result.PromotionNameEn = bestPromo.NameEn;
		}

		// convert an amount from one currency to another (functional/EGP pivot, Sell rate).
		private async Task<decimal> ConvertToDocAsync(int companyId, decimal amount, int fromCur, int docCur, int functional, DateTime asOf)
		{
			int __ddp = await _rounding.DecimalsAsync(companyId, docCur);   // HM-2 Batch 5: result in the DOCUMENT currency dp (no static R)
			decimal R(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			if (fromCur == docCur) return R(amount);
			decimal inFunc = fromCur == functional ? amount : (await _currency.ToBaseAsync(amount, fromCur, functional, asOf, "Sell")).baseAmount;
			if (docCur == functional) return R(inFunc);
			var (docRate, found) = await _currency.GetRateToEgpAsync(docCur, asOf, "Sell");   // docCur→functional (EGP pivot)
			return (found && docRate > 0) ? R(inFunc / docRate) : R(inFunc);
		}

		// Pricing 2C — the item's current cost in the functional currency:
		// manufactured (has a BOM) → standard manufactured unit cost; else moving-average stock cost; else opening cost.
		private async Task<decimal> ResolveItemCostAsync(int companyId, int itemId)
		{
			int __fdp = await _rounding.DecimalsAsync(companyId, null);   // HM-2 Batch 5: cost is FUNCTIONAL → functional dp (no static R)
			decimal R(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			bool manufactured = await _context.ItemComponents.AsNoTracking().AnyAsync(c => c.CompanyID == companyId && c.ParentItemId == itemId);
			if (manufactured)
			{
				var (mat, lab, oh) = await _manuf.ComputeStandardUnitCostAsync(companyId, itemId);
				var c = mat + lab + oh;
				if (c > 0) return R(c);
			}
			var bal = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && b.ItemId == itemId)
				.GroupBy(b => b.ItemId).Select(g => new { Qty = g.Sum(x => x.QtyOnHand), Val = g.Sum(x => x.TotalValue) }).FirstOrDefaultAsync();
			if (bal != null && bal.Qty > 0) return R(bal.Val / bal.Qty);
			var oc = await _context.Items.AsNoTracking().Where(i => i.ID == itemId && i.CompanyID == companyId).Select(i => i.OpeningCost).FirstOrDefaultAsync();
			return R(oc ?? 0m);
		}

		// functional → document currency (Sell rate, EGP pivot).
		private async Task<decimal> FromFunctionalAsync(decimal amountFunc, int docCur, int functional, DateTime asOf)
		{
			if (docCur == functional) return amountFunc;
			var (docRate, found) = await _currency.GetRateToEgpAsync(docCur, asOf, "Sell");   // docCur→functional
			return (found && docRate > 0) ? amountFunc / docRate : amountFunc;
		}

		// HM-2 Batch 5: round to the currency's configured decimal places via the SINGLE central helper (no direct DecimalPlaces
		// read, no silent ?? 2 — ICurrencyRounding throws for an undefined currency, keeping one source of precision truth).
		private Task<decimal> RoundToCurrencyAsync(int companyId, decimal v, int currencyId)
			=> _rounding.RoundAsync(companyId, v, currencyId);

		// Pricing 2A — gross-margin floor check (all comparison in functional currency).
		public async Task<MarginCheckResult> CheckMarginAsync(int companyId, int itemId, decimal netUnitPrice, int? currencyId, decimal? exchangeRate, DateTime asOf)
		{
			int __fdp = await _rounding.DecimalsAsync(companyId, null);   // HM-2 Batch 5: margin/cost analysis is FUNCTIONAL → functional dp (no static R)
			decimal R(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			var settings = await _context.InventorySettings.AsNoTracking().Where(s => s.CompanyID == companyId)
				.Select(s => new { s.MinMarginPct, s.MinMarginMode }).FirstOrDefaultAsync();
			var mode = string.IsNullOrWhiteSpace(settings?.MinMarginMode) ? "Off" : settings!.MinMarginMode;
			var res = new MarginCheckResult { Mode = mode, Ok = true };
			if (mode != "Warn" && mode != "Block") return res;   // Off → no check

			var item = await _context.Items.AsNoTracking().Where(i => i.ID == itemId && i.CompanyID == companyId)
				.Select(i => new { i.Name, i.MinMarginPct }).FirstOrDefaultAsync();
			res.ItemName = item?.Name;

			// current moving-average cost across all warehouses (functional) = ΣTotalValue / ΣQtyOnHand
			var bal = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && b.ItemId == itemId)
				.GroupBy(b => b.ItemId).Select(g => new { Qty = g.Sum(x => x.QtyOnHand), Val = g.Sum(x => x.TotalValue) }).FirstOrDefaultAsync();
			decimal cost = (bal != null && bal.Qty > 0) ? R(bal.Val / bal.Qty) : 0m;
			res.CostFunctional = cost;
			if (cost <= 0) { res.Skipped = true; return res; }   // unknown cost → skip (never a false block)

			decimal marginPct = item?.MinMarginPct ?? settings?.MinMarginPct ?? 0m;
			if (marginPct < 0) marginPct = 0m;
			res.MarginPct = marginPct;
			decimal floor = R(cost * (1 + marginPct / 100m));
			res.FloorFunctional = floor;

			// net unit price (doc currency) → functional
			int functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			int docCur = (currencyId.HasValue && currencyId.Value > 0) ? currencyId.Value : functional;
			decimal priceFunctional;
			if (docCur == functional) priceFunctional = R(netUnitPrice);
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) priceFunctional = R(netUnitPrice * exchangeRate.Value);
			else { var (b, _) = await _currency.ToBaseAsync(netUnitPrice, docCur, functional, asOf, "Sell"); priceFunctional = R(b); }
			res.PriceFunctional = priceFunctional;

			res.Ok = priceFunctional >= floor;
			return res;
		}

		// Pricing 2D — discount approval ceiling (per-line discount% vs InventorySettings.MaxLineDiscountPct).
		public async Task<(string? block, string? warn)> EvaluateLineDiscountsAsync(int companyId, IEnumerable<(decimal Qty, decimal UnitPrice, decimal DiscountAmount)> lines, bool canApprove)
		{
			var s = await _context.InventorySettings.AsNoTracking().Where(x => x.CompanyID == companyId)
				.Select(x => new { x.MaxLineDiscountPct, x.DiscountApprovalMode }).FirstOrDefaultAsync();
			var mode = string.IsNullOrWhiteSpace(s?.DiscountApprovalMode) ? "Off" : s!.DiscountApprovalMode;
			if (mode != "Warn" && mode != "Block") return (null, null);
			decimal threshold = s?.MaxLineDiscountPct ?? 0m, maxPct = 0m;
			foreach (var l in lines)
			{
				var gross = l.UnitPrice * l.Qty;
				if (gross <= 0) continue;
				var pct = l.DiscountAmount / gross * 100m;
				if (pct > maxPct) maxPct = pct;
			}
			if (maxPct <= threshold) return (null, null);
			string msg = $"خصم {maxPct:N1}% يتجاوز الحدّ المسموح {threshold:N1}%";
			if (mode == "Block")
				return canApprove ? (null, "تجاوز الخصم — معتمَد بصلاحية الإدارة: " + msg) : ("يتطلّب اعتماد الإدارة — " + msg, null);
			return (null, "تنبيه الخصم — " + msg);
		}

		// ---------------- management ----------------
		public async Task<(List<PriceListRow> rows, int total)> SearchAsync(int companyId, string? q, bool? active, int page, int pageSize)
		{
			var query = _context.PriceLists.AsNoTracking().Where(p => p.CompanyID == companyId)
				.Select(p => new PriceListRow
				{
					Id = p.ID, Code = p.Code, Name = p.Name, NameEn = p.NameEn, Segment = p.Segment,
					CustomerId = p.CustomerId, CustomerName = _context.Customers.Where(c => c.ID == p.CustomerId).Select(c => c.Name).FirstOrDefault(),
					CurrencyId = p.CurrencyId, CurrencyCode = _context.Currencies.Where(c => c.ID == p.CurrencyId).Select(c => c.Code).FirstOrDefault(),
					Priority = p.Priority, IsDefault = p.IsDefault, IsActive = p.IsActive, ValidFrom = p.ValidFrom, ValidTo = p.ValidTo,
					LineCount = _context.PriceListLines.Count(l => l.PriceListId == p.ID)
				});
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<PriceListRow>(terms, s => r => r.Code.Contains(s) || r.Name.Contains(s)
					|| (r.NameEn != null && r.NameEn.Contains(s)) || (r.Segment != null && r.Segment.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (active.HasValue) query = query.Where(r => r.IsActive == active.Value);
			var total = await query.CountAsync();
			if (page < 1) page = 1;
			if (pageSize < 1) pageSize = 25; else if (pageSize > 100000) pageSize = 100000;
			var rows = await query.OrderByDescending(r => r.Priority).ThenBy(r => r.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public async Task<Models.Context.Inventory.PriceList?> GetAsync(int companyId, int id)
		{
			var list = await _context.PriceLists.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == id);
			if (list == null) return null;
			list.Lines = await _context.PriceListLines.AsNoTracking().Where(l => l.PriceListId == id).OrderBy(l => l.ItemId).ThenBy(l => l.MinQty).ToListAsync();
			return list;
		}

		public async Task<(bool ok, string? error, int id)> SaveAsync(int companyId, Models.Context.Inventory.PriceList dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name)) return (false, "الكود والاسم مطلوبان", 0);
			var dupCode = await _context.PriceLists.AnyAsync(p => p.CompanyID == companyId && p.Code == dto.Code && p.ID != dto.ID);
			if (dupCode) return (false, "كود قائمة الأسعار مستخدم من قبل", 0);

			Models.Context.Inventory.PriceList entity;
			if (dto.ID > 0)
			{
				entity = await _context.PriceLists.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == dto.ID)
					?? throw new InvalidOperationException("قائمة الأسعار غير موجودة");
				var oldLines = _context.PriceListLines.Where(l => l.PriceListId == entity.ID);
				_context.PriceListLines.RemoveRange(oldLines);
			}
			else
			{
				entity = new Models.Context.Inventory.PriceList { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId };
				_context.PriceLists.Add(entity);
			}
			entity.Code = dto.Code.Trim();
			entity.Name = dto.Name.Trim();
			entity.NameEn = dto.NameEn?.Trim();
			entity.CustomerId = dto.CustomerId;
			entity.CurrencyId = dto.CurrencyId;
			entity.Segment = string.IsNullOrWhiteSpace(dto.Segment) ? null : dto.Segment.Trim();
			entity.Priority = dto.Priority;
			entity.IsDefault = dto.IsDefault;
			entity.IsActive = dto.IsActive;
			entity.ValidFrom = dto.ValidFrom;
			entity.ValidTo = dto.ValidTo;
			await _context.SaveChangesAsync();   // ensure entity.ID for new

			foreach (var l in dto.Lines)
			{
				if (l.ItemId <= 0) continue;
				bool costPlus = l.PricingMode == "CostPlus";
				_context.PriceListLines.Add(new Models.Context.Inventory.PriceListLine
				{
					PriceListId = entity.ID, ItemId = l.ItemId, MinQty = l.MinQty <= 0 ? 1 : l.MinQty,
					UnitPrice = costPlus ? null : l.UnitPrice, DiscountPercent = l.DiscountPercent < 0 ? 0 : (l.DiscountPercent > 100 ? 100 : l.DiscountPercent),
					PricingMode = costPlus ? "CostPlus" : "Fixed", MarkupPercent = costPlus ? (l.MarkupPercent < 0 ? 0 : l.MarkupPercent) : 0,
					ValidFrom = l.ValidFrom, ValidTo = l.ValidTo
				});
			}
			await _context.SaveChangesAsync();
			return (true, null, entity.ID);
		}

		public async Task<bool> DeleteAsync(int companyId, int id)
		{
			var entity = await _context.PriceLists.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == id);
			if (entity == null) return false;
			_context.PriceListLines.RemoveRange(_context.PriceListLines.Where(l => l.PriceListId == id));
			_context.PriceLists.Remove(entity);
			await _context.SaveChangesAsync();
			return true;
		}

		// ---------------- Pricing 2B: promotion management ----------------
		public async Task<(List<PromotionRow> rows, int total)> SearchPromotionsAsync(int companyId, string? q, bool? active, int page, int pageSize)
		{
			var query = _context.Promotions.AsNoTracking().Where(p => p.CompanyID == companyId)
				.Select(p => new PromotionRow
				{
					Id = p.ID, Code = p.Code, Name = p.Name, NameEn = p.NameEn, DiscountType = p.DiscountType, Value = p.Value,
					CurrencyId = p.CurrencyId, CurrencyCode = _context.Currencies.Where(c => c.ID == p.CurrencyId).Select(c => c.Code).FirstOrDefault(),
					ItemId = p.ItemId, ItemName = _context.Items.Where(i => i.ID == p.ItemId).Select(i => i.Name).FirstOrDefault(),
					CustomerId = p.CustomerId, CustomerName = _context.Customers.Where(c => c.ID == p.CustomerId).Select(c => c.Name).FirstOrDefault(),
					Segment = p.Segment, MinQty = p.MinQty, Priority = p.Priority, ValidFrom = p.ValidFrom, ValidTo = p.ValidTo, IsActive = p.IsActive
				});
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<PromotionRow>(terms, s => r => r.Code.Contains(s) || r.Name.Contains(s)
					|| (r.NameEn != null && r.NameEn.Contains(s)) || (r.Segment != null && r.Segment.Contains(s)) || (r.ItemName != null && r.ItemName.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (active.HasValue) query = query.Where(r => r.IsActive == active.Value);
			var total = await query.CountAsync();
			if (page < 1) page = 1;
			if (pageSize < 1) pageSize = 25; else if (pageSize > 100000) pageSize = 100000;
			var rows = await query.OrderByDescending(r => r.IsActive).ThenByDescending(r => r.Priority).ThenBy(r => r.Code)
				.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public Task<Models.Context.Inventory.Promotion?> GetPromotionAsync(int companyId, int id) =>
			_context.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == id);

		public async Task<(bool ok, string? error, int id)> SavePromotionAsync(int companyId, Models.Context.Inventory.Promotion dto, string? userId)
		{
			if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name)) return (false, "الكود والاسم مطلوبان", 0);
			var dupCode = await _context.Promotions.AnyAsync(p => p.CompanyID == companyId && p.Code == dto.Code && p.ID != dto.ID);
			if (dupCode) return (false, "كود العرض مستخدم من قبل", 0);
			var type = dto.DiscountType == "Amount" ? "Amount" : "Percent";
			if (dto.Value < 0) return (false, "قيمة الخصم غير صحيحة", 0);
			if (type == "Percent" && dto.Value > 100) return (false, "نسبة الخصم لا تتجاوز 100%", 0);
			if (dto.ValidFrom.HasValue && dto.ValidTo.HasValue && dto.ValidTo < dto.ValidFrom) return (false, "تاريخ النهاية قبل البداية", 0);

			Models.Context.Inventory.Promotion entity;
			if (dto.ID > 0)
			{
				entity = await _context.Promotions.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == dto.ID)
					?? throw new InvalidOperationException("العرض غير موجود");
			}
			else
			{
				entity = new Models.Context.Inventory.Promotion { CompanyID = companyId, CreatedAt = DateTime.UtcNow, CreatedBy = userId };
				_context.Promotions.Add(entity);
			}
			entity.Code = dto.Code.Trim();
			entity.Name = dto.Name.Trim();
			entity.NameEn = dto.NameEn?.Trim();
			entity.DiscountType = type;
			entity.Value = dto.Value;
			entity.CurrencyId = type == "Amount" ? dto.CurrencyId : null;   // percent is currency-agnostic
			entity.ItemId = dto.ItemId;
			entity.ItemCategoryId = dto.ItemCategoryId;
			entity.CustomerId = dto.CustomerId;
			entity.Segment = string.IsNullOrWhiteSpace(dto.Segment) ? null : dto.Segment.Trim();
			entity.MinQty = dto.MinQty <= 0 ? 1 : dto.MinQty;
			entity.Priority = dto.Priority;
			entity.ValidFrom = dto.ValidFrom;
			entity.ValidTo = dto.ValidTo;
			entity.IsActive = dto.IsActive;
			await _context.SaveChangesAsync();
			return (true, null, entity.ID);
		}

		public async Task<bool> DeletePromotionAsync(int companyId, int id)
		{
			var entity = await _context.Promotions.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == id);
			if (entity == null) return false;
			_context.Promotions.Remove(entity);
			await _context.SaveChangesAsync();
			return true;
		}
	}
}
