using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H62 — CLAIM SCRUBBING (the prototype's "claim scrubbing", owner 2026-11-08: the claims group first).
	// Before a draft batch goes to the payer, every claim is checked for what payers reject on: an approval the line
	// needs and has none, a policy or contract that does not cover the visit dates, a missing member number, a visit
	// or stay with no diagnosis, a line with no service code. Those are ERRORS: the batch is not submitted while any
	// remains — fix it, or HOLD the claim (it leaves the draft; its bill returns to the next build). Warnings (no
	// primary diagnosis, no visit or stay linked, no national ID, a possible double billing, a late claim) are shown
	// and do not block. The check is computed live, so an approval granted after the build clears its error at once.
	//
	// A rejected line never earns the doctor anything: fees are computed on what was COLLECTED (H19), and the payer's
	// part counts only when its claim line is accepted, at the accepted amount — so there is no share to reverse.
	// ==========================================================================================

	public static class HospitalScrubSeverity
	{
		public const string Error = "Error";
		public const string Warning = "Warning";
	}

	public sealed record HospitalScrubIssue(int ClaimId, string Severity, string Message, object[] Args);

	public static class HospitalScrubDiagnosis
	{
		public const string Present = "Present";
		public const string NoPrimary = "NoPrimary";
		public const string VisitMissing = "VisitMissing";
		public const string StayMissing = "StayMissing";
		public const string NotLinked = "NotLinked";
	}

	/// What the check knows about one claim. `Unapproved` = the codes of lines that need an approval and have none
	/// today; `Duplicates` = (code, day, other claim's bill no.) billed for the same patient to the same payer.
	public sealed record HospitalScrubFacts(int ClaimId, HospitalBill Bill, HospitalPatientPolicy? Policy, HospitalPayer Payer, string? NationalId,
		IReadOnlyList<string> Unapproved, IReadOnlyList<string> UncodedLines, string Diagnosis, IReadOnlyList<(string Code, DateTime Day, string OtherBill)> Duplicates);

	public static class HospitalClaimScrubRules
	{
		/// Past this many days since the bill closed, most payers no longer take the claim.
		public const int LateDays = 90;

		public static IReadOnlyList<HospitalScrubIssue> Check(HospitalScrubFacts f, DateTime today)
		{
			var list = new List<HospitalScrubIssue>();
			void E(string m, params object[] a) => list.Add(new(f.ClaimId, HospitalScrubSeverity.Error, m, a));
			void W(string m, params object[] a) => list.Add(new(f.ClaimId, HospitalScrubSeverity.Warning, m, a));

			foreach (var code in f.Unapproved) E("This line needs the payer's prior approval and has none: {0}", code);
			// the cover must hold on the first and the last day of the visit or stay
			var first = f.Bill.OpenedAt.Date; var last = (f.Bill.ClosedAt ?? f.Bill.OpenedAt).Date;
			if ((HospitalInsuranceRules.Ineligibility(f.Policy, f.Payer, first) ?? HospitalInsuranceRules.Ineligibility(f.Policy, f.Payer, last)) is string why)
				E("inelig:" + why);   // the eligibility reason, translated as the payers screen shows it (inelig:policy-expired...)
			if (f.Policy != null && string.IsNullOrWhiteSpace(f.Policy.PolicyNo)) E("The member number is missing");
			foreach (var code in f.UncodedLines) E("A line has no service code: {0}", code);
			switch (f.Diagnosis)
			{
				case HospitalScrubDiagnosis.VisitMissing: E("The visit has no diagnosis"); break;
				case HospitalScrubDiagnosis.StayMissing: E("The stay has no final diagnosis (write the discharge summary)"); break;
				case HospitalScrubDiagnosis.NoPrimary: W("No diagnosis is marked primary"); break;
				case HospitalScrubDiagnosis.NotLinked: W("The bill is not linked to a visit or a stay: the payer may ask for the diagnosis"); break;
			}
			if (string.IsNullOrWhiteSpace(f.NationalId)) W("The patient has no national ID");
			foreach (var d in f.Duplicates) W("Possibly billed twice: {0} on {1} is also in bill {2}", d.Code, d.Day.ToString("yyyy-MM-dd"), d.OtherBill);
			if (f.Bill.ClosedAt is DateTime closed && (today.Date - closed.Date).TotalDays > LateDays) W("Closed more than {0} days ago: it may be past the payer's submission window", LateDays);
			return list;
		}

		/// The submit refusal: how many claims still carry an error (null = the batch may go).
		public static string? SubmitRefusal(IEnumerable<HospitalScrubIssue> issues, out int claims)
		{
			claims = issues.Where(i => i.Severity == HospitalScrubSeverity.Error).Select(i => i.ClaimId).Distinct().Count();
			return claims == 0 ? null : "Fix or hold the {0} claims with errors before submitting";
		}
	}

	public sealed class HospitalClaimScrubService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalClaimScrubService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		/// Every issue of a batch's claims (a submitted batch is not checked: it is the payer's now).
		public async Task<IReadOnlyList<HospitalScrubIssue>> ScrubAsync(BusinessContext ctx, int batchId, DateTime nowUtc, CancellationToken ct = default)
		{
			int c = ctx.CompanyId;
			var batch = await _db.Set<HospitalClaimBatch>().AsNoTracking().FirstOrDefaultAsync(b => b.ID == batchId && b.CompanyId == c, ct);
			if (batch == null || batch.Status != HospitalClaimBatchStatus.Draft) return Array.Empty<HospitalScrubIssue>();
			var payer = await _db.Set<HospitalPayer>().AsNoTracking().FirstAsync(p => p.ID == batch.PayerId && p.CompanyId == c, ct);
			var claims = await _db.Set<HospitalClaim>().AsNoTracking().Where(x => x.CompanyId == c && x.BatchId == batchId).ToListAsync(ct);
			if (claims.Count == 0) return Array.Empty<HospitalScrubIssue>();
			var cids = claims.Select(x => x.ID).ToList();
			var billIds = claims.Select(x => x.BillId).ToList();
			var pids = claims.Select(x => x.PatientId).Distinct().ToList();
			var bills = await _db.HospitalBills.AsNoTracking().Where(b => b.CompanyId == c && billIds.Contains(b.ID)).ToDictionaryAsync(b => b.ID, ct);
			var policyIds = claims.Select(x => x.PolicyId).Distinct().ToList();
			var policies = await _db.Set<HospitalPatientPolicy>().AsNoTracking().Where(p => p.CompanyId == c && policyIds.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var nationalIds = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == c && pids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, p => p.NationalId, ct);
			var lines = await _db.Set<HospitalClaimLine>().AsNoTracking().Where(l => l.CompanyId == c && cids.Contains(l.ClaimId)).ToListAsync(ct);
			var blIds = lines.Where(l => l.BillLineId != null).Select(l => l.BillLineId!.Value).ToList();
			var billLines = await _db.HospitalBillLines.AsNoTracking().Where(l => l.CompanyId == c && blIds.Contains(l.ID)).ToDictionaryAsync(l => l.ID, ct);
			// approvals as they stand NOW: one granted since the build clears the line
			var approved = await _db.Set<HospitalPreauth>().AsNoTracking().Where(a => a.CompanyId == c && a.PayerId == payer.ID && pids.Contains(a.PatientId) && a.Status == HospitalPreauthStatus.Approved)
				.Select(a => new { a.PatientId, a.ServiceId }).ToListAsync(ct);
			// diagnoses: a visit's coded diagnoses, a stay's discharge summary
			var encIds = bills.Values.Where(b => b.EncounterId != null).Select(b => b.EncounterId!.Value).ToList();
			var dx = await _db.Set<HospitalEncounterDiagnosis>().AsNoTracking().Where(d => d.CompanyId == c && encIds.Contains(d.EncounterId))
				.GroupBy(d => d.EncounterId).Select(g => new { g.Key, Any = g.Count(), Primary = g.Count(x => x.IsPrimary) }).ToDictionaryAsync(g => g.Key, ct);
			var admIds = bills.Values.Where(b => b.AdmissionId != null).Select(b => b.AdmissionId!.Value).ToList();
			Dictionary<int, string?> finals;
			try { finals = await _db.Set<HospitalDischargeSummary>().AsNoTracking().Where(s => s.CompanyId == c && admIds.Contains(s.AdmissionId)).ToDictionaryAsync(s => s.AdmissionId, s => s.FinalDiagnosis, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { finals = new(); }
			// the same code, the same patient, the same day, claimed to this payer in ANOTHER claim not rejected
			var others = await (from l in _db.Set<HospitalClaimLine>().AsNoTracking()
								join cl in _db.Set<HospitalClaim>().AsNoTracking() on l.ClaimId equals cl.ID
								join bt in _db.Set<HospitalClaimBatch>().AsNoTracking() on cl.BatchId equals bt.ID
								join bl in _db.HospitalBillLines.AsNoTracking() on l.BillLineId equals bl.ID
								join b in _db.HospitalBills.AsNoTracking() on cl.BillId equals b.ID
								where l.CompanyId == c && bt.PayerId == payer.ID && pids.Contains(cl.PatientId) && l.Status != HospitalClaimLineStatus.Rejected && l.Code != ""
								select new { cl.ID, cl.PatientId, l.Code, bl.AddedAt, b.BillNo }).ToListAsync(ct);
			var today = HospitalNursingService.UtcToWard(nowUtc);

			var all = new List<HospitalScrubIssue>();
			foreach (var claim in claims)
			{
				var bill = bills[claim.BillId];
				var mine = lines.Where(l => l.ClaimId == claim.ID).ToList();
				var unapproved = mine.Where(l => l.MissingPreauth).Where(l =>
				{
					var sid = l.BillLineId is int bid && billLines.TryGetValue(bid, out var bl) ? bl.ServiceId : null;
					return !approved.Any(a => a.PatientId == claim.PatientId && a.ServiceId == sid);
				}).Select(l => l.Code is { Length: > 0 } ? l.Code : l.Description).ToList();
				var uncoded = mine.Where(l => string.IsNullOrWhiteSpace(l.Code)).Select(l => l.Description).ToList();
				string diagnosis = bill.EncounterId is int e
					? (dx.TryGetValue(e, out var g) ? (g.Primary > 0 ? HospitalScrubDiagnosis.Present : HospitalScrubDiagnosis.NoPrimary) : HospitalScrubDiagnosis.VisitMissing)
					: bill.AdmissionId is int a ? (finals.TryGetValue(a, out var fd) && !string.IsNullOrWhiteSpace(fd) ? HospitalScrubDiagnosis.Present : HospitalScrubDiagnosis.StayMissing)
					: HospitalScrubDiagnosis.NotLinked;
				var dups = new List<(string, DateTime, string)>();
				foreach (var l in mine.Where(l => l.BillLineId != null && !string.IsNullOrWhiteSpace(l.Code)))
				{
					if (!billLines.TryGetValue(l.BillLineId!.Value, out var bl)) continue;
					var o = others.FirstOrDefault(x => x.ID != claim.ID && x.PatientId == claim.PatientId && x.Code == l.Code && x.AddedAt.Date == bl.AddedAt.Date);
					if (o != null && !dups.Any(d => d.Item1 == l.Code)) dups.Add((l.Code, bl.AddedAt.Date, o.BillNo));
				}
				var facts = new HospitalScrubFacts(claim.ID, bill, policies.GetValueOrDefault(claim.PolicyId), payer, nationalIds.GetValueOrDefault(claim.PatientId), unapproved, uncoded, diagnosis, dups);
				all.AddRange(HospitalClaimScrubRules.Check(facts, today));
			}
			return all;
		}

		/// Holds a claim back: it leaves the draft batch (with its lines), and its bill is claimable again by the next build.
		public async Task<HospitalResult> HoldAsync(BusinessContext ctx, int claimId, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Bill, null, ct)) return HospitalResult.Fail("forbidden");
			var claim = await _db.Set<HospitalClaim>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == claimId && x.CompanyId == ctx.CompanyId, ct);
			if (claim == null) return HospitalResult.Fail("not_found");
			// a resubmission cannot be held: the line it resubmits is already marked resubmitted
			if (await _db.Set<HospitalClaimLine>().AnyAsync(l => l.CompanyId == ctx.CompanyId && l.ClaimId == claimId && l.ResubmitsLineId != null, ct))
				return HospitalResult.Fail("A claim that resubmits a rejected line cannot be held");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			// only from a batch still in draft — the check and the removal are one statement each, inside one transaction
			bool draft = await _db.Set<HospitalClaimBatch>().AnyAsync(b => b.ID == claim.BatchId && b.CompanyId == ctx.CompanyId && b.Status == HospitalClaimBatchStatus.Draft, ct);
			if (!draft) { await tx.RollbackAsync(); return HospitalResult.Fail("This batch was already submitted"); }
			await _db.Set<HospitalClaimLine>().Where(l => l.CompanyId == ctx.CompanyId && l.ClaimId == claimId).ExecuteDeleteAsync(ct);
			int n = await _db.Set<HospitalClaim>().Where(x => x.ID == claimId && x.CompanyId == ctx.CompanyId
				&& _db.Set<HospitalClaimBatch>().Any(b => b.ID == x.BatchId && b.Status == HospitalClaimBatchStatus.Draft)).ExecuteDeleteAsync(ct);
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("This batch was already submitted"); }
			await tx.CommitAsync();
			return HospitalResult.Success(claim.BatchId);
		}
	}
}
