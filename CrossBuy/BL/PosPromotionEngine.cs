using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Pos;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// =================================================================================================
	// HM-12 — THE RETAIL PROMOTION ENGINE.
	//
	// ONE PLACE A BASKET BECOMES A DISCOUNT. The hypermarket lane advertised a "Promotions" capability
	// for months with no consumer anywhere in the application - a green tick over nothing. This is the
	// consumer. Pricing stays where it is; this only decides what comes OFF a priced basket.
	//
	// WHAT IT WRITES, AND WHY THAT IS THE WHOLE INTEGRATION: PosOrderLine.DiscountAmount. That column
	// already existed and RecomputeAsync already carries it into SubTotal, TaxTotal and GrandTotal, and
	// from there into the sales invoice, AR and COGS. So a promotion reaches the ledger by the exact
	// route a manual discount already took. No posting path is new, and no second definition of "what
	// this sale is worth" is created.
	//
	// IT IS RE-ENTRANT BY CONSTRUCTION. The lane recomputes on every scan, every quantity change and
	// every removal, so this runs many times over one basket. Each run first REVERSES its own previous
	// contribution (recorded per line in PosOrderLinePromotions) and then applies fresh. It therefore
	// never compounds - and, just as important, it never silently erases a discount it did not grant:
	// only the amounts this engine recorded are subtracted back.
	//
	// THE FOUR RULES IT FOLLOWS, taken from how established retail engines behave rather than invented:
	//   1. FIXED ORDER. Promotions run by Priority, lowest first. Without a deterministic order the same
	//      trolley totals differently on two tills and nobody can say which till is right.
	//   2. CHEAPEST REWARD. When several items qualify for the reward, the discount lands on the
	//      CHEAPEST ones. Rewarding the dearest turns "buy 2 get 1 free" into an unbounded giveaway.
	//   3. EXCLUSIVITY. Once an exclusive rule touches a line, no later rule may touch it again. This is
	//      what stops a coupon and a loyalty offer stacking into a sale below cost.
	//   4. A CEILING. MaxDiscountPct caps what any one rule may take off, so a misconfigured campaign
	//      costs a margin point rather than the inventory.
	//
	// WHAT IT DELIBERATELY DOES NOT DO: it never prices, never posts, never touches stock and never
	// decides WHO may receive an offer. Customer eligibility belongs to the loyalty/CRM owner.
	// =================================================================================================
	public interface IPosPromotionEngine
	{
		/// Recomputes every promotion for one open order. Returns what fired, newest run only.
		/// Safe to call repeatedly; see the re-entrancy note above.
		Task<List<AppliedPromotion>> ApplyAsync(int companyId, int branchId, int orderId, CancellationToken ct = default);

		/// What fired on this order, for the lane to display and the receipt to print.
		Task<List<AppliedPromotion>> GetAppliedAsync(int companyId, int orderId, CancellationToken ct = default);
	}

	public class AppliedPromotion
	{
		public int PromotionId { get; set; }
		public string Code { get; set; } = "";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public decimal Amount { get; set; }
		public string Display(bool arabic) => arabic ? (NameAr.Length > 0 ? NameAr : NameEn) : (NameEn.Length > 0 ? NameEn : NameAr);
	}

	public class PosPromotionEngine : IPosPromotionEngine
	{
		private readonly CrossDbContext _db;
		private readonly ICurrencyRounding _rounding;

		public PosPromotionEngine(CrossDbContext db, ICurrencyRounding rounding) { _db = db; _rounding = rounding; }

		public async Task<List<AppliedPromotion>> GetAppliedAsync(int companyId, int orderId, CancellationToken ct = default)
		{
			var rows = await _db.PosOrderLinePromotions.AsNoTracking()
				.Where(p => p.CompanyId == companyId && p.OrderId == orderId)
				.ToListAsync(ct);

			return rows.GroupBy(r => new { r.PromotionId, r.PromoCode, r.PromoName })
				.Select(g => new AppliedPromotion
				{
					PromotionId = g.Key.PromotionId,
					Code = g.Key.PromoCode,
					NameAr = g.Key.PromoName,
					NameEn = g.Key.PromoName,
					Amount = g.Sum(x => x.Amount),
				})
				.OrderByDescending(a => a.Amount)
				.ToList();
		}

		public async Task<List<AppliedPromotion>> ApplyAsync(int companyId, int branchId, int orderId, CancellationToken ct = default)
		{
			var order = await _db.PosOrders.FirstOrDefaultAsync(o => o.ID == orderId && o.CompanyId == companyId, ct);
			if (order == null) return new();
			// A settled order is HISTORY. Re-running a campaign over it would rewrite what the customer
			// was charged after the fact, which is the one thing a receipt must never do.
			if (order.Status != "Open") return await GetAppliedAsync(companyId, orderId, ct);

			int dp = await _rounding.DecimalsAsync(companyId, order.CurrencyId, branchId);
			decimal R(decimal v) => Math.Round(v, dp, MidpointRounding.AwayFromZero);

			var lines = await _db.PosOrderLines.Where(l => l.OrderId == orderId).OrderBy(l => l.Sort).ToListAsync(ct);
			if (lines.Count == 0)
			{
				await ClearPriorAsync(companyId, orderId, lines, ct);
				await _db.SaveChangesAsync(ct);
				return new();
			}

			// ---- 1. reverse ONLY what this engine granted last run -------------------------------
			await ClearPriorAsync(companyId, orderId, lines, ct);

			// ---- 2. the rules in force, in their fixed order ---------------------------------------
			var now = DateTime.UtcNow;
			var promos = await _db.PosPromotions.AsNoTracking()
				.Where(p => p.CompanyId == companyId && p.IsActive
					&& (p.BranchId == null || p.BranchId == branchId)
					&& (p.StartsAt == null || p.StartsAt <= now)
					&& (p.EndsAt == null || p.EndsAt >= now))
				.OrderBy(p => p.Priority).ThenBy(p => p.ID)
				.ToListAsync(ct);
			if (promos.Count == 0) { await _db.SaveChangesAsync(ct); return new(); }

			var promoIds = promos.Select(p => p.ID).ToList();
			var members = await _db.PosPromotionItems.AsNoTracking()
				.Where(m => promoIds.Contains(m.PromotionId)).ToListAsync(ct);

			var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
			var itemCategory = await _db.Items.AsNoTracking()
				.Where(i => itemIds.Contains(i.ID))
				.Select(i => new { i.ID, i.ItemCategoryId })
				.ToDictionaryAsync(x => x.ID, x => x.ItemCategoryId, ct);

			// gross (pre-discount) value of each line, and the per-unit price used for "cheapest first"
			decimal Gross(PosOrderLine l) => R(l.Qty * l.UnitPrice);
			decimal basketGross = R(lines.Sum(Gross));

			var lockedLines = new HashSet<int>();          // touched by an exclusive rule
			var granted = new Dictionary<int, decimal>();  // lineId -> total granted this run
			var applied = new List<PosOrderLinePromotion>();

			foreach (var p in promos)
			{
				var mine = members.Where(m => m.PromotionId == p.ID).ToList();

				bool Matches(PosOrderLine l, string role)
				{
					if (mine.Count == 0) return true;                       // no membership = basket-wide
					itemCategory.TryGetValue(l.ItemId, out int cat);
					return mine.Any(m =>
						(m.Role == "Both" || m.Role == role) &&
						((m.ItemId != null && m.ItemId == l.ItemId) || (m.CategoryId != null && m.CategoryId == cat)));
				}

				var rewardLines = lines.Where(l => !lockedLines.Contains(l.ID) && Gross(l) > 0 && Matches(l, "Reward")).ToList();
				if (rewardLines.Count == 0) continue;

				decimal pool = 0m;   // how much discount this rule hands out, before the ceiling

				switch (p.PromoType)
				{
					case "ThresholdAmount":
						// the WHOLE basket must reach the bar - including lines this rule cannot discount,
						// because the customer's spend is what earned it.
						if (basketGross < p.MinBasket) continue;
						pool = p.DiscountPct > 0
							? R(rewardLines.Sum(Gross) * p.DiscountPct / 100m)
							: p.DiscountAmount;
						break;

					case "AmountOff":
						pool = p.DiscountAmount;
						break;

					case "BuyXGetY":
					{
						if (p.TriggerQty <= 0 || p.RewardQty <= 0) continue;
						decimal triggerQty = lines.Where(l => Matches(l, "Trigger")).Sum(l => l.Qty);
						decimal sets = Math.Floor(triggerQty / p.TriggerQty);
						if (sets <= 0) continue;
						decimal freeUnits = sets * p.RewardQty;

						// RULE 2 - the free units are the CHEAPEST qualifying ones. Taking the dearest
						// first turns "buy 2 get 1 free" into an unbounded giveaway on a mixed basket.
						foreach (var l in rewardLines.OrderBy(l => l.UnitPrice))
						{
							if (freeUnits <= 0) break;
							decimal take = Math.Min(freeUnits, l.Qty);
							decimal value = R(take * l.UnitPrice);
							decimal off = p.DiscountPct > 0 ? R(value * p.DiscountPct / 100m) : value;   // no pct = free
							Grant(l, off);
							freeUnits -= take;
						}
						pool = 0m;   // already distributed line by line
						break;
					}

					default:   // PercentOff
						if (p.DiscountPct <= 0) continue;
						foreach (var l in rewardLines) Grant(l, R(Gross(l) * p.DiscountPct / 100m));
						pool = 0m;
						break;
				}

				// a pooled rule (threshold / flat amount) spreads over the reward lines in proportion to value
				if (pool > 0)
				{
					decimal baseSum = rewardLines.Sum(Gross);
					if (baseSum <= 0) continue;
					decimal handed = 0m;
					for (int i = 0; i < rewardLines.Count; i++)
					{
						var l = rewardLines[i];
						// the LAST line absorbs the rounding remainder so Σ granted == pool exactly
						decimal share = i == rewardLines.Count - 1 ? R(pool - handed) : R(pool * Gross(l) / baseSum);
						Grant(l, share);
						handed += share;
					}
				}

				if (p.IsExclusive)
					foreach (var l in rewardLines) lockedLines.Add(l.ID);

				void Grant(PosOrderLine l, decimal off)
				{
					if (off <= 0) return;

					// RULE 4 - the ceiling, per rule, against the line's gross.
					if (p.MaxDiscountPct > 0)
					{
						decimal cap = R(Gross(l) * p.MaxDiscountPct / 100m);
						if (off > cap) off = cap;
					}

					// never discount a line below zero, however many rules reach it
					granted.TryGetValue(l.ID, out decimal already);
					decimal room = R(Gross(l) - already);
					if (off > room) off = room;
					if (off <= 0) return;

					granted[l.ID] = R(already + off);
					l.DiscountAmount = R(l.DiscountAmount + off);
					applied.Add(new PosOrderLinePromotion
					{
						CompanyId = companyId, OrderId = orderId, OrderLineId = l.ID,
						PromotionId = p.ID, PromoCode = p.Code,
						PromoName = string.IsNullOrWhiteSpace(p.NameAr) ? p.NameEn : p.NameAr,
						Amount = off, AppliedAt = DateTime.UtcNow,
					});
				}
			}

			if (applied.Count > 0) _db.PosOrderLinePromotions.AddRange(applied);
			await _db.SaveChangesAsync(ct);

			return applied.GroupBy(a => a.PromotionId)
				.Select(g =>
				{
					var p = promos.First(x => x.ID == g.Key);
					return new AppliedPromotion
					{
						PromotionId = p.ID, Code = p.Code, NameAr = p.NameAr, NameEn = p.NameEn,
						Amount = g.Sum(x => x.Amount),
					};
				})
				.OrderByDescending(a => a.Amount).ToList();
		}

		/// Subtracts this engine's PREVIOUS grants from the lines and deletes their record. Only amounts
		/// this engine wrote are reversed, so a discount granted by anything else survives untouched.
		private async Task ClearPriorAsync(int companyId, int orderId, List<PosOrderLine> lines, CancellationToken ct)
		{
			var prior = await _db.PosOrderLinePromotions
				.Where(p => p.CompanyId == companyId && p.OrderId == orderId).ToListAsync(ct);
			if (prior.Count == 0) return;

			foreach (var g in prior.GroupBy(p => p.OrderLineId))
			{
				var line = lines.FirstOrDefault(l => l.ID == g.Key);
				if (line == null) continue;                       // the line is gone; its rows go too
				decimal back = g.Sum(x => x.Amount);
				line.DiscountAmount = Math.Max(0m, line.DiscountAmount - back);
			}
			_db.PosOrderLinePromotions.RemoveRange(prior);
		}
	}
}
