using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Pos;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// =================================================================================================
	// HM-15 — WRITING PROMOTION RULES.
	//
	// SEPARATE FROM THE ENGINE ON PURPOSE. PosPromotionEngine evaluates a basket and writes a discount;
	// it never prices, never posts and never decides what a campaign IS. Putting the editor inside it
	// would make the thing that applies rules also the thing that invents them, and the first time a
	// rule read oddly nobody could say whether the fault was in the campaign or in the evaluation.
	//
	// WHAT IT VALIDATES, AND WHY EACH ONE IS HERE RATHER THAN IN THE SCREEN. A rule saved wrong does not
	// fail — it quietly gives away margin on every basket until someone reconciles. So each check below
	// exists because its absence is silent:
	//   - a BuyXGetY with no trigger or no reward never fires, and looks active on the list for ever;
	//   - a percentage over 100 hands back more than the line is worth;
	//   - a threshold with no minimum fires on an empty basket;
	//   - an end date before the start date makes a campaign that is finished the day it is written.
	// =================================================================================================
	public interface IPosPromotionAdmin
	{
		Task<List<PosPromotion>> ListAsync(int companyId, CancellationToken ct = default);
		Task<PosPromotion?> GetAsync(int companyId, int id, CancellationToken ct = default);
		Task<(bool ok, string? error, int id)> SaveAsync(int companyId, PosPromotion input, CancellationToken ct = default);
		Task<(bool ok, string? error)> SetActiveAsync(int companyId, int id, bool active, CancellationToken ct = default);
	}

	public class PosPromotionAdmin : IPosPromotionAdmin
	{
		private readonly CrossDbContext _db;
		public PosPromotionAdmin(CrossDbContext db) { _db = db; }

		public Task<List<PosPromotion>> ListAsync(int companyId, CancellationToken ct = default) =>
			_db.PosPromotions.AsNoTracking()
				.Where(p => p.CompanyId == companyId)
				.OrderBy(p => p.Priority).ThenBy(p => p.ID)
				.ToListAsync(ct);

		public Task<PosPromotion?> GetAsync(int companyId, int id, CancellationToken ct = default) =>
			_db.PosPromotions.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == companyId && p.ID == id, ct);

		public async Task<(bool ok, string? error, int id)> SaveAsync(int companyId, PosPromotion input, CancellationToken ct = default)
		{
			var code = (input.Code ?? "").Trim().ToUpperInvariant();
			if (code.Length == 0) return (false, "A promotion needs a code — it is what sales reports group by.", 0);
			if (string.IsNullOrWhiteSpace(input.NameAr) && string.IsNullOrWhiteSpace(input.NameEn))
				return (false, "A promotion needs a name the cashier can read on the till.", 0);

			var types = new[] { "PercentOff", "AmountOff", "BuyXGetY", "ThresholdAmount" };
			if (!types.Contains(input.PromoType)) return (false, "Unknown promotion type.", 0);

			if (input.DiscountPct < 0 || input.DiscountPct > 100) return (false, "A percentage must be between 0 and 100.", 0);
			if (input.DiscountAmount < 0) return (false, "A discount amount cannot be negative.", 0);
			if (input.MaxDiscountPct < 0 || input.MaxDiscountPct > 100) return (false, "The ceiling must be between 0 and 100 percent.", 0);

			switch (input.PromoType)
			{
				case "BuyXGetY":
					if (input.TriggerQty <= 0 || input.RewardQty <= 0)
						return (false, "Buy X get Y needs both a trigger quantity and a reward quantity — with either missing the rule never fires.", 0);
					break;
				case "PercentOff":
					if (input.DiscountPct <= 0) return (false, "A percentage promotion needs a percentage above zero.", 0);
					break;
				case "AmountOff":
					if (input.DiscountAmount <= 0) return (false, "An amount promotion needs an amount above zero.", 0);
					break;
				case "ThresholdAmount":
					if (input.MinBasket <= 0) return (false, "A basket threshold needs a minimum — without one it fires on every sale, including an empty basket.", 0);
					if (input.DiscountPct <= 0 && input.DiscountAmount <= 0)
						return (false, "A basket threshold needs either a percentage or an amount to give.", 0);
					break;
			}

			if (input.StartsAt.HasValue && input.EndsAt.HasValue && input.EndsAt < input.StartsAt)
				return (false, "The end date is before the start date — the campaign would be over before it began.", 0);

			// The code is the report key, so a duplicate would merge two campaigns into one line.
			bool taken = await _db.PosPromotions.AnyAsync(p => p.CompanyId == companyId && p.ID != input.ID && p.Code == code, ct);
			if (taken) return (false, $"Code {code} is already used by another promotion in this company.", 0);

			PosPromotion row;
			if (input.ID > 0)
			{
				row = await _db.PosPromotions.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.ID == input.ID, ct)
					?? throw new InvalidOperationException("Promotion not found");
			}
			else
			{
				row = new PosPromotion { CompanyId = companyId, CreatedAt = DateTime.UtcNow };
				_db.PosPromotions.Add(row);
			}

			row.BranchId = input.BranchId;
			row.Code = code;
			row.NameAr = (input.NameAr ?? "").Trim();
			row.NameEn = (input.NameEn ?? "").Trim();
			row.PromoType = input.PromoType;
			row.TriggerQty = input.TriggerQty;
			row.RewardQty = input.RewardQty;
			row.DiscountPct = input.DiscountPct;
			row.DiscountAmount = input.DiscountAmount;
			row.MinBasket = input.MinBasket;
			row.Priority = input.Priority;
			row.IsExclusive = input.IsExclusive;
			row.MaxDiscountPct = input.MaxDiscountPct;
			row.StartsAt = input.StartsAt;
			row.EndsAt = input.EndsAt;
			row.IsActive = input.IsActive;

			await _db.SaveChangesAsync(ct);
			return (true, null, row.ID);
		}

		/// Switching a campaign off is NOT a delete. What it already gave away stays recorded against the
		/// sales it gave it away on, and the rule itself stays readable — a receipt from last week has to
		/// remain explainable after the campaign ends.
		public async Task<(bool ok, string? error)> SetActiveAsync(int companyId, int id, bool active, CancellationToken ct = default)
		{
			var row = await _db.PosPromotions.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.ID == id, ct);
			if (row == null) return (false, "Promotion not found.");
			row.IsActive = active;
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}
	}
}
