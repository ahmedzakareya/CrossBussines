using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H18 — the ONE place a bill is split between the patient and the payer.
	//
	// The bill (what the cashier asks for, what closing posts) and the claim (what the payer is asked for) both
	// read their split from here, so they can never disagree. The rules are H11's (HospitalInsuranceRules):
	// the contract's discount on a bill priced on the main list, the co-pay on the covered amount and its cap,
	// a line outside the payer's list paid by the patient in full, VAT on the patient's part only. A bill in a
	// currency other than the contract's is not covered (a claim carries no currency of its own).
	// ==========================================================================================

	/// One line as the split sees it (a stored bill line, or a running accommodation line with no id yet).
	public sealed record HospitalSplitLine(int? BillLineId, string Code, string Category, decimal Quantity, decimal UnitPrice, bool OutsideList);

	/// A bill's split: who pays, and per line (same order as the lines given) its amount and the payer's part.
	public sealed record HospitalBillSplit(HospitalPayer Payer, HospitalPatientPolicy Policy, HospitalCoverageSplit Split, IReadOnlyList<HospitalClaimableLine> Lines);

	public static class HospitalCoverage
	{
		/// The split of a bill's lines by the patient's cover on `day` (null = not covered: self-pay, no eligible
		/// policy, or a currency the contract is not in). `onlyPayerId` restricts the cover to one payer (a claim
		/// batch is per payer).
		public static async Task<HospitalBillSplit?> SplitAsync(CrossDbContext db, HospitalPricingService? pricing, BusinessContext ctx, int patientId, int? billPriceListId,
			int? billCurrencyId, IReadOnlyList<HospitalSplitLine> lines, DateTime day, int? onlyPayerId, CancellationToken ct)
		{
			var cover = await (from po in db.Set<HospitalPatientPolicy>().AsNoTracking()
							   join py in db.Set<HospitalPayer>().AsNoTracking() on po.PayerId equals py.ID
							   where po.CompanyId == ctx.CompanyId && py.CompanyId == ctx.CompanyId && po.PatientId == patientId && (onlyPayerId == null || po.PayerId == onlyPayerId)
							   select new { po, py }).ToListAsync(ct);
			// the policy that covers the day (the most recently started), as the claim has always chosen it
			var pick = cover.Where(x => HospitalInsuranceRules.Ineligibility(x.po, x.py, day) == null).OrderByDescending(x => x.po.ValidFrom).FirstOrDefault();
			if (pick == null) return null;
			int? contractCurrency = pick.py.PriceListId is int lid
				? await db.HospitalPriceLists.AsNoTracking().Where(l => l.ID == lid && l.CompanyId == ctx.CompanyId).Select(l => l.CurrencyId).FirstOrDefaultAsync(ct) : null;
			if (billCurrencyId != contractCurrency) return null;
			int decimals = pricing == null ? 3 : (await pricing.CurrencyOfAsync(ctx, contractCurrency, ct)).Decimals;
			return Of(pick.py, pick.po, billPriceListId, lines, decimals);
		}

		/// The pure part: the split of the lines under a payer's terms.
		public static HospitalBillSplit Of(HospitalPayer payer, HospitalPatientPolicy policy, int? billPriceListId, IReadOnlyList<HospitalSplitLine> lines, int decimals)
		{
			var claimable = lines.Select(l => new HospitalClaimableLine(l.BillLineId, l.Code, l.Category,
				HospitalInsuranceRules.LineAmount(l.Quantity, l.UnitPrice, billPriceListId, payer.DiscountPct, decimals), !l.OutsideList)).ToList();
			return new HospitalBillSplit(payer, policy, HospitalInsuranceRules.Split(claimable, payer.CopayPct, payer.CopayCap, decimals), claimable);
		}

		/// The split frozen when a bill closed, as the claim and the bill read it back.
		public static async Task<(HospitalBillCoverage Head, IReadOnlyList<HospitalBillCoverageLine> Lines)?> FrozenAsync(CrossDbContext db, BusinessContext ctx, int billId, CancellationToken ct)
		{
			var head = await db.Set<HospitalBillCoverage>().AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == ctx.CompanyId && c.BillId == billId, ct);
			if (head == null) return null;
			var lines = await db.Set<HospitalBillCoverageLine>().AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.CoverageId == head.ID).ToListAsync(ct);
			return (head, lines);
		}
	}
}
