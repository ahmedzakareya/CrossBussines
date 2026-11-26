using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Pos;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class CouponPreview
	{
		public int CouponId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public decimal Discount { get; set; }        // what it is worth on THIS basket, document currency
		public bool Capped { get; set; }             // the percentage hit MaxDiscount
	}

	public interface IPosCouponService
	{
		Task<(bool ok, string? error, CouponPreview? preview)> ApplyAsync(int companyId, int orderId, string code, int? employeeId);
		Task<(bool ok, string? error)> RemoveAsync(int companyId, int orderId);
		Task<CouponPreview?> CurrentAsync(int companyId, int orderId);
		Task StampInvoiceAsync(int companyId, int orderId, int invoiceId);
	}

	// ============================================================================================
	// HM-26 — COUPONS.
	//
	// A coupon is NOT a promotion. The promotion engine (IPricingService) applies automatic per-line
	// price rules; this applies a BASKET discount that somebody typed in, and limits how many times
	// it may ever be typed. Building it on the promotion tables would have put coupon concerns into
	// every pricing query, which is why it has its own.
	//
	// THE DISCOUNT LANDS ON THE LINES, in PosOrderLine.CouponDiscount. That is not an implementation
	// detail, it is what makes the tax right: RecomputeAsync takes tax from LineTotal AFTER discounts,
	// so spreading the coupon across the lines reduces the taxable base by exactly the discount given.
	// Holding it as a single order-level figure would have required the tax path to learn about
	// coupons, and a basket discount that does not reduce tax is a basket discount the shop pays the
	// tax on.
	// ============================================================================================
	public class PosCouponService : IPosCouponService
	{
		private readonly CrossDbContext _db;
		private readonly ICurrencyRounding _rounding;
		public PosCouponService(CrossDbContext db, ICurrencyRounding rounding) { _db = db; _rounding = rounding; }

		public async Task<CouponPreview?> CurrentAsync(int companyId, int orderId)
		{
			var o = await _db.PosOrders.AsNoTracking().FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o?.CouponId == null) return null;
			var c = await _db.PosCoupons.AsNoTracking().FirstOrDefaultAsync(x => x.ID == o.CouponId.Value);
			if (c == null) return null;
			decimal spent = await _db.PosOrderLines.Where(l => l.OrderId == orderId).SumAsync(l => (decimal?)l.CouponDiscount) ?? 0m;
			return new CouponPreview { CouponId = c.ID, Code = c.Code, Name = c.Name, NameEn = c.NameEn, Discount = spent };
		}

		public async Task<(bool ok, string? error, CouponPreview? preview)> ApplyAsync(int companyId, int orderId, string code, int? employeeId)
		{
			code = (code ?? "").Trim();
			if (code.Length == 0) return (false, "Enter a coupon code.", null);

			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "Order not found", null);
			if (o.Status != "Open") return (false, "A settled sale cannot take a coupon.", null);
			if (o.CouponId != null) return (false, "This sale already has a coupon. Remove it first.", null);

			var c = await _db.PosCoupons.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.Code == code);
			// THE SAME REFUSAL for "no such code" and "not active". A message that distinguishes them
			// turns the till into an oracle that confirms which codes exist to anyone holding a queue.
			if (c == null || !c.IsActive) return (false, "That coupon code is not valid.", null);

			var now = DateTime.UtcNow;
			if (c.ValidFrom != null && now < c.ValidFrom.Value) return (false, "That coupon is not valid yet.", null);
			if (c.ValidTo != null && now > c.ValidTo.Value) return (false, "That coupon has expired.", null);

			// Identity rules, before any arithmetic.
			bool identified = o.CustomerId != null && o.CustomerId > 0
				&& !await _db.Customers.AsNoTracking().AnyAsync(x => x.ID == o.CustomerId && x.NameEn == "POS Walk-in");
			if (c.RequiresCustomer && !identified) return (false, "That coupon needs the customer attached to the sale.", null);

			if (c.PerCustomerLimit != null)
			{
				// A per-customer limit on an unidentified basket cannot be enforced, so it is REFUSED
				// rather than waved through: letting a walk-in use a once-per-customer coupon makes the
				// limit meaningless for everyone who does not sign in.
				if (!identified) return (false, "That coupon is limited per customer — attach the customer to the sale.", null);
				int used = await _db.PosCouponRedemptions.CountAsync(r => r.CouponId == c.ID && r.CustomerId == o.CustomerId);
				if (used >= c.PerCustomerLimit.Value) return (false, "This customer has already used that coupon.", null);
			}

			var lines = await _db.PosOrderLines.Where(l => l.OrderId == orderId).OrderBy(l => l.Sort).ToListAsync();
			if (lines.Count == 0) return (false, "An empty basket cannot take a coupon.", null);

			int dp = await _rounding.DecimalsAsync(companyId, o.CurrencyId, o.BranchId);
			decimal R(decimal v) => Math.Round(v, dp, MidpointRounding.AwayFromZero);

			// The base is what the lines are worth AFTER their existing discounts and BEFORE this coupon.
			decimal baseTotal = lines.Sum(l => R(l.Qty * l.UnitPrice - l.DiscountAmount));
			if (baseTotal <= 0m) return (false, "There is nothing left to discount on this basket.", null);
			if (c.MinBasket != null && baseTotal < c.MinBasket.Value)
				return (false, $"That coupon needs a basket of at least {c.MinBasket.Value.ToString("0." + new string('#', dp))}.", null);

			decimal discount = c.DiscountType == "Percent" ? R(baseTotal * c.Value / 100m) : R(c.Value);
			bool capped = false;
			if (c.DiscountType == "Percent" && c.MaxDiscount != null && discount > c.MaxDiscount.Value)
			{ discount = R(c.MaxDiscount.Value); capped = true; }
			// A coupon worth more than the basket discounts the basket, never below zero and never into
			// change owed. "20 off" on a 12 basket is 12 off.
			if (discount > baseTotal) discount = baseTotal;
			if (discount <= 0m) return (false, "That coupon is worth nothing on this basket.", null);

			// ================= THE ATOMIC CLAIM =================
			// Check-and-increment in ONE statement. Two tills reading UsageCount, both seeing room, and
			// both writing back is how the last use of a coupon gets spent twice; the database decides
			// instead. UsageLimit NULL means unlimited and the predicate lets it through.
			int claimed = await _db.Database.ExecuteSqlRawAsync(
				"UPDATE PosCoupons SET UsageCount = UsageCount + 1 " +
				"WHERE ID = {0} AND IsActive = 1 AND (UsageLimit IS NULL OR UsageCount < UsageLimit)", c.ID);
			if (claimed == 0) return (false, "That coupon has been fully used.", null);

			// From here the use IS claimed, so any failure must give it back.
			try
			{
				// Spread the discount across the lines in proportion to what each contributes, then give
				// the REMAINDER to the largest line so the parts sum to the whole exactly. Rounding each
				// share independently would leave the basket a fil short or long of the discount promised.
				decimal allocated = 0m;
				var shares = new List<(PosOrderLine line, decimal share)>();
				foreach (var l in lines)
				{
					decimal lineBase = R(l.Qty * l.UnitPrice - l.DiscountAmount);
					decimal share = R(discount * (lineBase / baseTotal));
					shares.Add((l, share));
					allocated += share;
				}
				decimal drift = R(discount - allocated);
				if (drift != 0m && shares.Count > 0)
				{
					var biggest = shares.OrderByDescending(x => x.share).First();
					shares[shares.IndexOf(biggest)] = (biggest.line, R(biggest.share + drift));
				}
				foreach (var (line, share) in shares) line.CouponDiscount = share;

				o.CouponId = c.ID;
				_db.PosCouponRedemptions.Add(new PosCouponRedemption
				{
					CompanyID = companyId, CouponId = c.ID, OrderId = orderId,
					CustomerId = identified ? o.CustomerId : null,
					DiscountAmount = discount, RedeemedAt = DateTime.UtcNow, RedeemedByEmployeeId = employeeId
				});
				await _db.SaveChangesAsync();
			}
			catch
			{
				// Give the claim back. Without this a failed apply burns a use of a coupon nobody got.
				await _db.Database.ExecuteSqlRawAsync(
					"UPDATE PosCoupons SET UsageCount = CASE WHEN UsageCount > 0 THEN UsageCount - 1 ELSE 0 END WHERE ID = {0}", c.ID);
				throw;
			}

			return (true, null, new CouponPreview
			{
				CouponId = c.ID, Code = c.Code, Name = c.Name, NameEn = c.NameEn, Discount = discount, Capped = capped
			});
		}

		public async Task<(bool ok, string? error)> RemoveAsync(int companyId, int orderId)
		{
			var o = await _db.PosOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == companyId);
			if (o == null) return (false, "Order not found");
			if (o.Status != "Open") return (false, "A settled sale's coupon cannot be removed.");
			if (o.CouponId == null) return (true, null);   // nothing to do is not a failure

			int couponId = o.CouponId.Value;
			var lines = await _db.PosOrderLines.Where(l => l.OrderId == orderId).ToListAsync();
			foreach (var l in lines) l.CouponDiscount = 0m;   // the separate column is what makes this exact
			o.CouponId = null;

			var red = await _db.PosCouponRedemptions.FirstOrDefaultAsync(r => r.OrderId == orderId);
			if (red != null) _db.PosCouponRedemptions.Remove(red);
			await _db.SaveChangesAsync();

			// The use goes back to the pool. A coupon removed before settlement was never spent, and a
			// cashier correcting a mis-keyed code must not cost the customer their one use of it.
			await _db.Database.ExecuteSqlRawAsync(
				"UPDATE PosCoupons SET UsageCount = CASE WHEN UsageCount > 0 THEN UsageCount - 1 ELSE 0 END WHERE ID = {0}", couponId);
			return (true, null);
		}

		// Called at settlement so the redemption row points at the invoice it actually discounted.
		// Until then it carries only the order, because an open basket has no invoice to name.
		public async Task StampInvoiceAsync(int companyId, int orderId, int invoiceId)
		{
			var red = await _db.PosCouponRedemptions.FirstOrDefaultAsync(r => r.OrderId == orderId && r.CompanyID == companyId);
			if (red == null || red.InvoiceId != null) return;
			red.InvoiceId = invoiceId;
			await _db.SaveChangesAsync();
		}
	}
}
