using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Loyalty;

namespace CrossBuy.BL
{
	// HM-9 slice 2: loyalty EARN engine. Static (no DI) — it writes ONLY PointsMovements (a memo ledger), never GL or stock,
	// so it does not violate the two-writers rule and adds no constructor coupling. The balance is DERIVED (Σ signed Points);
	// there is no stored-balance column. Points are a WHOLE count: earn = Math.Floor(net × rate) — an EXPLICIT floor on a
	// COUNT, deliberately NOT routed through ICurrencyRounding (that is for money; this mirrors the separate retail fils-step).
	// Guarded no-op unless the branch's "Loyalty" capability is ON and the sale carries a real (non-walk-in) customer.
	public static class LoyaltyPointsHelper
	{
		public static Task<bool> IsEnabledAsync(CrossDbContext db, int branchId) =>
			db.BranchCapabilities.AsNoTracking().AnyAsync(c => c.BranchId == branchId && c.CapabilityKey == "Loyalty" && c.Enabled);

		public static Task<bool> IsWalkInAsync(CrossDbContext db, int companyId, int customerId) =>
			db.Customers.AsNoTracking().AnyAsync(c => c.ID == customerId && c.CompanyID == companyId && c.NameEn == "POS Walk-in");

		// branch override → else company default → else 0 (no earning). Data, not code.
		public static async Task<decimal> ResolveRateAsync(CrossDbContext db, int companyId, int branchId)
		{
			var branchRate = await db.BranchPosSettings.AsNoTracking().Where(s => s.BranchId == branchId)
				.Select(s => s.LoyaltyPointsPerCurrencyUnit).FirstOrDefaultAsync();
			if (branchRate != null) return branchRate.Value;
			var coRate = await db.Companies.AsNoTracking().Where(c => c.CompanyID == companyId)
				.Select(c => c.LoyaltyPointsPerCurrencyUnit).FirstOrDefaultAsync();
			return coRate ?? 0m;
		}

		// Σ of the NET line totals of the loyalty-ELIGIBLE lines. Eligibility = Item.LoyaltyEligible ?? Category.LoyaltyEligible.
		// A line with no item (or an unknown item) earns nothing (conservative — no category to consult). 0-price lines add 0.
		public static async Task<decimal> EligibleNetAsync(CrossDbContext db, List<(int? itemId, decimal net)> lines)
		{
			var itemIds = lines.Where(l => l.itemId != null).Select(l => l.itemId!.Value).Distinct().ToList();
			if (itemIds.Count == 0) return 0m;
			var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID))
				.Select(i => new { i.ID, i.ItemCategoryId, i.LoyaltyEligible }).ToListAsync();
			var catIds = items.Select(i => i.ItemCategoryId).Distinct().ToList();
			var cats = await db.ItemCategories.AsNoTracking().Where(c => catIds.Contains(c.ID))
				.ToDictionaryAsync(c => c.ID, c => c.LoyaltyEligible);
			var elig = items.ToDictionary(i => i.ID,
				i => i.LoyaltyEligible ?? (cats.TryGetValue(i.ItemCategoryId, out var ce) ? ce : true));
			decimal net = 0m;
			foreach (var l in lines)
				if (l.itemId != null && elig.TryGetValue(l.itemId.Value, out var ok) && ok) net += l.net;
			return net;
		}

		// Derived balance = Σ signed Points (never a stored column).
		public static async Task<long> GetBalanceAsync(CrossDbContext db, int companyId, int customerId) =>
			await db.PointsMovements.AsNoTracking().Where(m => m.CompanyID == companyId && m.CustomerId == customerId)
				.SumAsync(m => (long?)m.Points) ?? 0L;

		// EARN at pay: floor(eligibleNet × rate) points for the sale, once per invoice. In-transaction with the sale.
		public static async Task<long> AccrueForInvoiceAsync(CrossDbContext db, int companyId, int branchId, int invoiceId, int customerId, int? userId)
		{
			if (customerId <= 0) return 0;
			if (!await IsEnabledAsync(db, branchId)) return 0;                                   // capability "Loyalty" OFF ⇒ no points
			if (await IsWalkInAsync(db, companyId, customerId)) return 0;                        // no identified customer ⇒ no balance
			if (await db.PointsMovements.AnyAsync(m => m.SourceInvoiceId == invoiceId && m.Kind == "Earn")) return 0;   // earn once
			decimal rate = await ResolveRateAsync(db, companyId, branchId);
			if (rate <= 0m) return 0;
			var lines = await db.SalesInvoiceLines.AsNoTracking().Where(l => l.SalesInvoiceId == invoiceId)
				.Select(l => new { l.ItemId, l.LineTotal }).ToListAsync();
			decimal net = await EligibleNetAsync(db, lines.Select(l => (l.ItemId, l.LineTotal)).ToList());
			long points = (long)Math.Floor(net * rate);   // EXPLICIT floor on a COUNT — not ICurrencyRounding
			if (points <= 0) return 0;
			db.PointsMovements.Add(new PointsMovement
			{
				CompanyID = companyId, CustomerId = customerId, Kind = "Earn", SourceInvoiceId = invoiceId,
				Points = points, CreatedAt = DateTime.UtcNow, CreatedBy = userId
			});
			await db.SaveChangesAsync();
			return points;
		}

		// REVERSE on return/paid-cancel: a NEGATIVE movement (reverse-never-delete) proportional to the returned eligible net,
		// capped at what remains un-reversed. A FULL return (returnedNet == saleNet) reverses exactly the earned points ⇒ the
		// derived balance returns to 0. Fires only if the original sale actually earned — independent of the current capability
		// state (so turning the capability off later never strands an un-reversed balance). `kind` = ReturnReversal | CancelReversal.
		public static async Task<long> ReverseForSaleUndoAsync(CrossDbContext db, int companyId, int returnId, string kind, int? userId)
		{
			var ret = await db.SalesReturns.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.ID == returnId && r.CompanyID == companyId);
			if (ret == null || ret.OriginalInvoiceId == null) return 0;
			int invId = ret.OriginalInvoiceId.Value;
			long earned = await db.PointsMovements.Where(m => m.SourceInvoiceId == invId && m.Kind == "Earn").SumAsync(m => (long?)m.Points) ?? 0;
			if (earned <= 0) return 0;                                                            // the sale never earned
			// HM-22 — A REDEMPTION IS NOT A REVERSAL, even though both are negative.
			// This counted every negative movement on the invoice as points already given back. Once spending
			// shipped, a customer who spent points ON THE SALE THAT EARNED THEM made that true by accident: the
			// Redeem row looked like a reversal, `remaining` came out short by exactly the points spent, and a
			// later return under-reversed the earn and left the customer holding points the returned sale had
			// created. The sign cannot carry this distinction, so the KIND does.
			long alreadyReversed = -(await db.PointsMovements
				.Where(m => m.SourceInvoiceId == invId && m.Points < 0 && m.Kind != "Redeem")
				.SumAsync(m => (long?)m.Points) ?? 0);
			long remaining = earned - alreadyReversed;
			if (remaining <= 0) return 0;
			var saleLines = await db.SalesInvoiceLines.AsNoTracking().Where(l => l.SalesInvoiceId == invId).Select(l => new { l.ItemId, l.LineTotal }).ToListAsync();
			decimal saleNet = await EligibleNetAsync(db, saleLines.Select(l => (l.ItemId, l.LineTotal)).ToList());
			if (saleNet <= 0m) return 0;
			decimal retNet = await EligibleNetAsync(db, ret.Lines.Select(l => ((int?)l.ItemId, l.LineTotal)).ToList());
			long rev = (long)Math.Floor((decimal)earned * retNet / saleNet);
			if (rev > remaining) rev = remaining;                                                // full return ⇒ rev==earned; never over-reverse
			if (rev <= 0) return 0;
			db.PointsMovements.Add(new PointsMovement
			{
				CompanyID = companyId, CustomerId = ret.CustomerId, Kind = kind, SourceInvoiceId = invId,
				SourceDocType = "SalesReturn", SourceDocId = returnId, Points = -rev, CreatedAt = DateTime.UtcNow, CreatedBy = userId
			});
			await db.SaveChangesAsync();
			return -rev;
		}

		// HM-22 — SPEND POINTS. The arm this ledger was built for and never got.
		//
		// Called INSIDE the settlement transaction, after the invoice exists, so the points and the discount
		// that paid for them commit together or not at all. The balance is re-read HERE rather than trusted
		// from the caller: two tills serving the same customer at once would both pass a check made before
		// the transaction, and the second one would spend points the first had already taken.
		//
		// Returns the points actually taken as a NEGATIVE number, or 0 with an error when it refuses.
		public static async Task<(bool ok, string? error, long moved)> RedeemForInvoiceAsync(CrossDbContext db,
			int companyId, int branchId, int invoiceId, int customerId, long points, int? userId)
		{
			if (points <= 0) return (true, null, 0);                       // nothing asked for is not a failure
			if (customerId <= 0) return (false, "Points can only be spent by an identified customer.", 0);
			if (!await IsEnabledAsync(db, branchId)) return (false, "Loyalty is not enabled on this branch.", 0);
			if (await IsWalkInAsync(db, companyId, customerId)) return (false, "Points can only be spent by an identified customer.", 0);

			// ONE REDEMPTION PER INVOICE. A retried settlement must not spend the points twice.
			if (await db.PointsMovements.AnyAsync(m => m.SourceInvoiceId == invoiceId && m.Kind == "Redeem"))
				return (true, null, 0);

			long balance = await GetBalanceAsync(db, companyId, customerId);
			if (points > balance) return (false, $"That is more points than the customer has ({balance}).", 0);

			db.PointsMovements.Add(new PointsMovement
			{
				CompanyID = companyId, CustomerId = customerId, Kind = "Redeem", SourceInvoiceId = invoiceId,
				SourceDocType = "PosRedeem", Points = -points, CreatedAt = DateTime.UtcNow, CreatedBy = userId
			});
			await db.SaveChangesAsync();
			return (true, null, -points);
		}

		// HM-22 — GIVE SPENT POINTS BACK when the whole sale is cancelled.
		//
		// WHY THIS IS NOT OPTIONAL: VoidPaidOrderAsync reverses the invoice journal entry wholesale, and that
		// entry includes the «Points» tender line. Reversing it credits the loyalty account back, so the SHOP
		// is made whole. Without this arm the CUSTOMER is not: their points were taken, the goods went back,
		// the shop recovered its liability, and the points simply ceased to exist. That is the one outcome a
		// loyalty ledger must never produce.
		//
		// FULL CANCEL ONLY, deliberately. A partial SalesReturn refunds money without knowing which tenders
		// paid the original sale, so handing points back there as well would compensate the customer twice
		// — once in cash and once in points. That case needs the refund path to settle by tender first,
		// and is left alone rather than guessed at.
		public static async Task<long> ReverseRedeemForInvoiceAsync(CrossDbContext db, int companyId, int invoiceId, int? userId)
		{
			if (invoiceId <= 0) return 0;
			long spent = await RedeemedOnInvoiceAsync(db, invoiceId);
			if (spent <= 0) return 0;
			// reverse-never-delete, and never twice: whatever has already been handed back is netted off.
			long alreadyBack = await db.PointsMovements.AsNoTracking()
				.Where(m => m.SourceInvoiceId == invoiceId && m.Kind == "RedeemReversal")
				.SumAsync(m => (long?)m.Points) ?? 0L;
			long give = spent - alreadyBack;
			if (give <= 0) return 0;
			int cust = await db.PointsMovements.Where(m => m.SourceInvoiceId == invoiceId && m.Kind == "Redeem")
				.Select(m => m.CustomerId).FirstAsync();
			db.PointsMovements.Add(new PointsMovement
			{
				CompanyID = companyId, CustomerId = cust, Kind = "RedeemReversal", SourceInvoiceId = invoiceId,
				SourceDocType = "PosOrderVoid", Points = give, CreatedAt = DateTime.UtcNow, CreatedBy = userId
			});
			await db.SaveChangesAsync();
			return give;
		}

		// HM-22: points SPENT on an invoice (a positive count), so a caller can show or reverse them.
		public static async Task<long> RedeemedOnInvoiceAsync(CrossDbContext db, int invoiceId) =>
			-(await db.PointsMovements.AsNoTracking().Where(m => m.SourceInvoiceId == invoiceId && m.Kind == "Redeem")
				.SumAsync(m => (long?)m.Points) ?? 0L);

		// FULL reversal for a paid-order CANCEL (VoidPaidOrderAsync reverses the invoice JE directly — no SalesReturn to
		// proportion against). Reverses whatever earned points remain un-reversed for the invoice ⇒ derived balance returns to 0.
		public static async Task<long> ReverseAllForInvoiceAsync(CrossDbContext db, int companyId, int invoiceId, int? userId)
		{
			if (invoiceId <= 0) return 0;
			long earned = await db.PointsMovements.Where(m => m.SourceInvoiceId == invoiceId && m.Kind == "Earn").SumAsync(m => (long?)m.Points) ?? 0;
			if (earned <= 0) return 0;
			// HM-22: same correction as ReverseForSaleUndoAsync — a Redeem row is negative but is not a reversal.
			long alreadyReversed = -(await db.PointsMovements
				.Where(m => m.SourceInvoiceId == invoiceId && m.Points < 0 && m.Kind != "Redeem")
				.SumAsync(m => (long?)m.Points) ?? 0);
			long remaining = earned - alreadyReversed;
			if (remaining <= 0) return 0;
			int cust = await db.PointsMovements.Where(m => m.SourceInvoiceId == invoiceId && m.Kind == "Earn").Select(m => m.CustomerId).FirstAsync();
			db.PointsMovements.Add(new PointsMovement
			{
				CompanyID = companyId, CustomerId = cust, Kind = "CancelReversal", SourceInvoiceId = invoiceId,
				SourceDocType = "PosOrderVoid", Points = -remaining, CreatedAt = DateTime.UtcNow, CreatedBy = userId
			});
			await db.SaveChangesAsync();
			return -remaining;
		}
	}
}
