using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H82 — PAYER RECOUPMENT. After a payer accepted a claim line (and often paid it), an audit takes part or all
	// of it back. The billing desk records it once per line — never more than was accepted, always with the payer's reason.
	// It posts Dr claim rejections / Cr payer receivable (H20), and the doctor's share follows: when the line's payer part
	// was already counted in an earlier fee statement, the next statement carries a negative "Recoup" line; when it was
	// not counted yet, the payer part is counted net of what was taken back.
	// ==========================================================================================
	public static class HospitalRecoupRules
	{
		public static string? RecoupRefusal(string lineStatus, decimal accepted, decimal? alreadyRecouped, decimal amount, string? reason)
		{
			if (lineStatus != HospitalClaimLineStatus.Accepted) return "Only an accepted line can be recouped";
			if (alreadyRecouped != null) return "This line was already recouped";
			if (amount <= 0) return "Write the amount the payer takes back";
			if (amount > accepted) return "The payer cannot take back more than it accepted";
			if (string.IsNullOrWhiteSpace(reason)) return "Write the payer's reason";
			return null;
		}

		/// What the payer finally paid on a line: what it accepted, less what it took back.
		public static decimal NetAccepted(decimal accepted, decimal? recouped) => Math.Max(0, accepted - (recouped ?? 0));

		/// The doctor's share given back for a recoupment of a part already counted: a percent rule gives back the same
		/// percent of what was taken; a per-unit fee is given back only when the payer took the whole line back, and only
		/// when the payer part was the one that earned it (the patient owed nothing on the line).
		public static decimal RecoupShare(HospitalFeeRule rule, decimal recouped, decimal accepted, decimal quantity, bool patientOwesAnything, int decimals)
		{
			if (rule.Basis == HospitalFeeBasis.Percent) return -Math.Round(recouped * rule.Value / 100m, decimals, MidpointRounding.AwayFromZero);
			return !patientOwesAnything && recouped >= accepted ? -Math.Round(rule.Value * quantity, decimals, MidpointRounding.AwayFromZero) : 0;
		}
	}

	public sealed class HospitalRecoupService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IHospitalLedger? _ledger;
		public HospitalRecoupService(CrossDbContext db, IHospitalAccessService access, IHospitalLedger? ledger = null) { _db = db; _access = access; _ledger = ledger; }

		public async Task<HospitalResult> RecoupAsync(BusinessContext ctx, int lineId, decimal amount, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return HospitalResult.Fail("forbidden");
			var l = await _db.Set<HospitalClaimLine>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == lineId && x.CompanyId == ctx.CompanyId, ct);
			if (l == null) return HospitalResult.Fail("not_found");
			if (HospitalRecoupRules.RecoupRefusal(l.Status, l.AcceptedAmount ?? l.InsurerAmount, l.RecoupedAmount, amount, reason) is string why) return HospitalResult.Fail(why);
			var c = await _db.Set<HospitalClaim>().AsNoTracking().FirstAsync(x => x.ID == l.ClaimId && x.CompanyId == ctx.CompanyId, ct);
			var b = await _db.Set<HospitalClaimBatch>().AsNoTracking().FirstAsync(x => x.ID == c.BatchId && x.CompanyId == ctx.CompanyId, ct);
			// the accounts first, outside the transaction: unmapped = not recorded
			if (_ledger == null) return HospitalResult.Fail(HospitalClaimAccounts.NotSetUp);
			var (accounts, w1) = await HospitalClaimAccounts.CompanyAsync(_db, ctx, ct);
			if (w1 != null) return HospitalResult.Fail(w1);
			var (payerAr, w2) = await HospitalClaimAccounts.PayerReceivableAsync(_db, ctx, b.PayerId, ct);
			if (w2 != null) return HospitalResult.Fail(w2);
			string r = reason!.Trim(); r = r[..Math.Min(300, r.Length)];
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			// once: only the first recoupment of a still-accepted line lands
			int n = await _db.Set<HospitalClaimLine>().Where(x => x.ID == lineId && x.CompanyId == ctx.CompanyId && x.Status == HospitalClaimLineStatus.Accepted && x.RecoupedAmount == null)
				.ExecuteUpdateAsync(u => u.SetProperty(x => x.RecoupedAmount, amount).SetProperty(x => x.RecoupedAt, nowUtc).SetProperty(x => x.RecoupReason, r)
					.SetProperty(x => x.RecoupedByEmployeeId, employeeId), ct);
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("This line was already recouped"); }
			var posted = await new HospitalClaimAccountingService(_db, _access, _ledger).PostRecoupAsync(ctx, l, c, b, amount, employeeId, nowUtc, accounts!, payerAr!.Value, ct);
			if (!posted.Ok) { await tx.RollbackAsync(); return posted; }
			await tx.CommitAsync();
			return HospitalResult.Success(l.ID);
		}
	}
}
