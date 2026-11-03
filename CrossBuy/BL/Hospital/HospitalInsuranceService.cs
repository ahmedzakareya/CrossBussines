using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H11 — insurance and claims.
	//
	//   · Contracts (`manage`) and patient policies (`register`); eligibility is the policy and the contract
	//     both valid on the day (HospitalInsuranceRules.Ineligibility).
	//   · Pre-authorisations (`bill`): requested for one service of an eligible patient, answered approved
	//     (with the payer's reference) or rejected (with the reason). Nothing is booked.
	//   · Claims (`bill`): a month's batch per payer gathers the CLOSED bills of that payer's eligible
	//     patients not claimed yet, each split by the rules into the payer's share and the patient's; a line
	//     in a category the payer wants approved, without an approved request, goes out flagged. The batch is
	//     submitted, then the payer's remittance accepts or rejects each line (code and reason); a rejected
	//     line can be resubmitted, with a justification, into the payer's next draft batch.
	//   · This unit READS bills and never changes them, posts nothing to the ledger, and starts no job.
	//     The entities reach the model through HospitalInsuranceModel.Configure (wired in CrossDbContext).
	// ==========================================================================================

	public sealed class HospitalPayerInput
	{
		public int? Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Kind { get; set; } = HospitalPayerKind.Insurer;
		public string? Classes { get; set; }
		public decimal DiscountPct { get; set; }
		public decimal CopayPct { get; set; }
		public decimal CopayCap { get; set; }
		public int? PriceListId { get; set; }
		public List<string> PreauthCategories { get; set; } = new();
		public DateTime ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalPolicyInput
	{
		public int? Id { get; set; }
		public int PatientId { get; set; }
		public int PayerId { get; set; }
		public string PolicyNo { get; set; } = "";
		public string? MemberClass { get; set; }
		public DateTime ValidFrom { get; set; }
		public DateTime? ValidTo { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed record HospitalPayerRow(HospitalPayer Payer, string? PriceList, int Policies, decimal? RejectionRate, bool Expired);

	public sealed record HospitalPolicyRow(HospitalPatientPolicy Policy, string PatientName, string PatientNameEn, string Mrn, string? Photo, string Payer, string PayerEn, string? Ineligible);

	public sealed record HospitalPreauthRow(HospitalPreauth Preauth, string PatientName, string PatientNameEn, string Mrn, string? Photo, string Payer, string PayerEn, string Service, string ServiceEn, string ServiceCode);

	/// Currency: the contract's currency code when it is not the facility's (null = the facility's); a batch's money is in it.
	public sealed record HospitalBatchRow(HospitalClaimBatch Batch, string Payer, string PayerEn, int Claims, decimal Amount, decimal Accepted, decimal Rejected, string? Currency = null);

	public sealed record HospitalClaimView(HospitalClaim Claim, string BillNo, string PatientName, string PatientNameEn, string Mrn, string PolicyNo, IReadOnlyList<HospitalClaimLine> Lines);

	public sealed record HospitalBatchView(HospitalBatchRow Row, IReadOnlyList<HospitalClaimView> Claims);

	public sealed record HospitalRejectionRow(HospitalClaimLine Line, string BatchNo, int BatchId, string PatientName, string PatientNameEn, string? Photo);

	public sealed record HospitalInsuranceStats(int PendingPreauths, int? OldestPendingMinutes, decimal ClaimedThisMonth, decimal? RejectionRate, int? DaysToCollect);

	public sealed class HospitalInsuranceService
	{
		/// When no pricing service answers (a bare test host): the facility's KWD, three decimals.
		private const int DefaultDecimals = 3;
		/// Two people did the same step at the same moment; the unique indexes or the guarded update let one win.
		public const string ConcurrentChange = "Someone else changed this at the same moment; refresh and try again";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalPricingService? _pricing;

		public HospitalInsuranceService(CrossDbContext db, IHospitalAccessService access, HospitalPricingService? pricing = null) { _db = db; _access = access; _pricing = pricing; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalPayer> Payers(BusinessContext ctx) => _db.Set<HospitalPayer>().Where(p => p.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalPatientPolicy> Policies(BusinessContext ctx) => _db.Set<HospitalPatientPolicy>().Where(p => p.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalPreauth> Preauths(BusinessContext ctx) => _db.Set<HospitalPreauth>().Where(p => p.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalClaimBatch> Batches(BusinessContext ctx) => _db.Set<HospitalClaimBatch>().Where(b => b.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalClaim> Claims(BusinessContext ctx) => _db.Set<HospitalClaim>().Where(c => c.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalClaimLine> Lines(BusinessContext ctx) => _db.Set<HospitalClaimLine>().Where(l => l.CompanyId == ctx.CompanyId);

		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		private static async Task<string> NextAsync(IQueryable<string> numbers, string prefix, CancellationToken ct, int width = 6)
		{
			var nos = await numbers.Where(n => n.StartsWith(prefix)).ToListAsync(ct);
			int n = 0;
			foreach (var no in nos) if (int.TryParse(no.AsSpan(prefix.Length), out var v) && v > n) n = v;
			return prefix + (n + 1).ToString(new string('0', width));
		}

		// ====================================================================================
		// CONTRACTS AND POLICIES
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalPayerRow>> PayersAsync(BusinessContext ctx, DateTime day, CancellationToken ct = default)
		{
			var payers = await Payers(ctx).AsNoTracking().OrderBy(p => p.Code).ToListAsync(ct);
			var lists = await _db.HospitalPriceLists.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId).Select(l => new { l.ID, l.Name, l.NameEn, l.Code }).ToListAsync(ct);
			var counts = await Policies(ctx).AsNoTracking().GroupBy(p => p.PayerId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
			var decided = await (from l in Lines(ctx).AsNoTracking()
								 join c in Claims(ctx).AsNoTracking() on l.ClaimId equals c.ID
								 join b in Batches(ctx).AsNoTracking() on c.BatchId equals b.ID
								 where l.Status != HospitalClaimLineStatus.Pending
								 group new { l.Status, l.InsurerAmount, l.AcceptedAmount } by b.PayerId into g
								 select new { g.Key, Rows = g.ToList() }).ToListAsync(ct);
			return payers.Select(p =>
			{
				var d = decided.FirstOrDefault(x => x.Key == p.ID)?.Rows;
				decimal acc = d?.Where(x => x.Status == HospitalClaimLineStatus.Accepted).Sum(x => x.AcceptedAmount ?? x.InsurerAmount) ?? 0;
				decimal rej = d?.Where(x => x.Status != HospitalClaimLineStatus.Accepted).Sum(x => x.InsurerAmount) ?? 0;
				var l = lists.FirstOrDefault(x => x.ID == p.PriceListId);
				return new HospitalPayerRow(p, l == null ? null : l.Code + " · " + DisplayName.Of(l.Name, l.NameEn), counts.GetValueOrDefault(p.ID),
					HospitalInsuranceRules.RejectionRate(acc, rej), p.ValidTo != null && p.ValidTo.Value.Date < day.Date);
			}).ToList();
		}

		public async Task<HospitalResult> SavePayerAsync(BusinessContext ctx, HospitalPayerInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim().ToUpperInvariant();
			if (code.Length is 0 or > 20) return HospitalResult.Fail("Enter a code of up to 20 characters");
			if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.NameEn)) return HospitalResult.Fail("Enter the name in both languages");
			if (!HospitalPayerKind.All.Contains(input.Kind)) return HospitalResult.Fail("Unknown payer kind");
			if (input.DiscountPct is < 0 or > 100) return HospitalResult.Fail("The discount is 0 to 100%");
			if (input.CopayPct is < 0 or > 100) return HospitalResult.Fail("The co-pay is 0 to 100%");
			if (input.CopayCap < 0) return HospitalResult.Fail("The co-pay cap cannot be negative");
			if (input.ValidTo != null && input.ValidTo.Value.Date < input.ValidFrom.Date) return HospitalResult.Fail("The contract ends before it starts");
			if (input.PriceListId is int pl && !await _db.HospitalPriceLists.AnyAsync(l => l.ID == pl && l.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var cats = input.PreauthCategories.Where(c => HospitalServiceCategories.All.Contains(c)).Distinct().ToList();
			int self = input.Id ?? 0;
			if (await Payers(ctx).AnyAsync(p => p.Code == code && p.ID != self, ct)) return HospitalResult.Fail("Another contract uses this code");
			HospitalPayer? p;
			if (input.Id is int id)
			{
				p = await Payers(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (p == null) return HospitalResult.Fail("not_found");
			}
			else { p = new HospitalPayer { CompanyId = ctx.CompanyId }; _db.Set<HospitalPayer>().Add(p); }
			p.Code = code; p.Name = input.Name.Trim(); p.NameEn = input.NameEn.Trim(); p.Kind = input.Kind; p.Classes = Clip(input.Classes, 100);
			p.DiscountPct = input.PriceListId == null ? input.DiscountPct : 0; p.CopayPct = input.CopayPct; p.CopayCap = input.CopayCap; p.PriceListId = input.PriceListId; p.PreauthCategories = cats.Count == 0 ? null : string.Join(",", cats);
			p.ValidFrom = input.ValidFrom.Date; p.ValidTo = input.ValidTo?.Date; p.IsActive = input.IsActive;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(p.ID);
		}

		public async Task<IReadOnlyList<HospitalPolicyRow>> PoliciesAsync(BusinessContext ctx, int? patientId, DateTime day, CancellationToken ct = default)
		{
			var q = from po in Policies(ctx).AsNoTracking()
					join pa in _db.HospitalPatients.AsNoTracking() on po.PatientId equals pa.ID
					join py in Payers(ctx).AsNoTracking() on po.PayerId equals py.ID
					where pa.CompanyId == ctx.CompanyId
					select new { po, pa, py };
			if (patientId != null) q = q.Where(x => x.po.PatientId == patientId);
			var rows = await q.OrderBy(x => x.pa.Name).Take(500).ToListAsync(ct);
			return rows.Select(x => new HospitalPolicyRow(x.po, x.pa.Name, x.pa.NameEn, x.pa.Mrn, x.pa.PhotoPath, x.py.Name, x.py.NameEn,
				HospitalInsuranceRules.Ineligibility(x.po, x.py, day))).ToList();
		}

		public async Task<HospitalResult> SavePolicyAsync(BusinessContext ctx, HospitalPolicyInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Register, ct)) return HospitalResult.Fail("forbidden");
			var no = (input.PolicyNo ?? "").Trim();
			if (no.Length is 0 or > 40) return HospitalResult.Fail("Enter the member number (up to 40 characters)");
			if (input.ValidTo != null && input.ValidTo.Value.Date < input.ValidFrom.Date) return HospitalResult.Fail("The policy ends before it starts");
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (!await Payers(ctx).AnyAsync(p => p.ID == input.PayerId, ct)) return HospitalResult.Fail("not_found");
			int self = input.Id ?? 0;
			if (await Policies(ctx).AnyAsync(p => p.PayerId == input.PayerId && p.PolicyNo == no && p.ID != self, ct)) return HospitalResult.Fail("This member number is already registered with this payer");
			HospitalPatientPolicy? po;
			if (input.Id is int id)
			{
				po = await Policies(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (po == null) return HospitalResult.Fail("not_found");
			}
			else { po = new HospitalPatientPolicy { CompanyId = ctx.CompanyId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId }; _db.Set<HospitalPatientPolicy>().Add(po); }
			po.PatientId = input.PatientId; po.PayerId = input.PayerId; po.PolicyNo = no; po.MemberClass = Clip(input.MemberClass, 30);
			po.ValidFrom = input.ValidFrom.Date; po.ValidTo = input.ValidTo?.Date; po.IsActive = input.IsActive;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(po.ID);
		}

		/// The policy that covers a patient on `day` (the most recently started valid one), with why not when none does.
		public async Task<(HospitalPatientPolicy? Policy, HospitalPayer? Payer, string? Ineligible)> EligibilityAsync(BusinessContext ctx, int patientId, DateTime day, int? payerId = null, CancellationToken ct = default)
		{
			var q = from po in Policies(ctx).AsNoTracking() join py in Payers(ctx).AsNoTracking() on po.PayerId equals py.ID where po.PatientId == patientId select new { po, py };
			if (payerId != null) q = q.Where(x => x.po.PayerId == payerId);
			var all = await q.ToListAsync(ct);
			var ok = all.Where(x => HospitalInsuranceRules.Ineligibility(x.po, x.py, day) == null).OrderByDescending(x => x.po.ValidFrom).FirstOrDefault();
			if (ok != null) return (ok.po, ok.py, null);
			var first = all.OrderByDescending(x => x.po.ValidFrom).FirstOrDefault();
			return (first?.po, first?.py, HospitalInsuranceRules.Ineligibility(first?.po, first?.py, day));
		}

		// ====================================================================================
		// PRE-AUTHORISATIONS
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalPreauthRow>> PreauthsAsync(BusinessContext ctx, string? status, CancellationToken ct = default)
		{
			var q = from a in Preauths(ctx).AsNoTracking()
					join pa in _db.HospitalPatients.AsNoTracking() on a.PatientId equals pa.ID
					join py in Payers(ctx).AsNoTracking() on a.PayerId equals py.ID
					join s in _db.HospitalServices.AsNoTracking() on a.ServiceId equals s.ID
					where pa.CompanyId == ctx.CompanyId && s.CompanyId == ctx.CompanyId
					select new { a, pa, py, s };
			if (status != null) q = q.Where(x => x.a.Status == status);
			var rows = await q.OrderBy(x => x.a.Status == HospitalPreauthStatus.Pending ? 0 : 1).ThenByDescending(x => x.a.RequestedAt).Take(300).ToListAsync(ct);
			return rows.Select(x => new HospitalPreauthRow(x.a, x.pa.Name, x.pa.NameEn, x.pa.Mrn, x.pa.PhotoPath, x.py.Name, x.py.NameEn, x.s.Name, x.s.NameEn, x.s.Code)).ToList();
		}

		public async Task<HospitalResult> RequestPreauthAsync(BusinessContext ctx, int patientId, int serviceId, decimal? amount, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var s = await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(x => x.ID == serviceId && x.CompanyId == ctx.CompanyId && x.IsActive, ct);
			if (s == null || !await _db.HospitalPatients.AnyAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var (policy, payer, why) = await EligibilityAsync(ctx, patientId, nowUtc, null, ct);
			if (why != null) return HospitalResult.Fail("The patient is not covered: {0}", why);
			if (await Preauths(ctx).AnyAsync(a => a.PatientId == patientId && a.ServiceId == serviceId && a.Status == HospitalPreauthStatus.Pending, ct))
				return HospitalResult.Fail("A request for this service is already waiting for the payer");
			if (amount is < 0) return HospitalResult.Fail("The amount cannot be negative");
			var a = new HospitalPreauth
			{
				CompanyId = ctx.CompanyId, RequestNo = await NextAsync(Preauths(ctx).Select(x => x.RequestNo), "PA-", ct), PatientId = patientId, PayerId = payer!.ID, PolicyId = policy!.ID,
				ServiceId = serviceId, Amount = amount ?? s.Price, RequestedAt = nowUtc, RequestedByEmployeeId = employeeId,
			};
			_db.Set<HospitalPreauth>().Add(a);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.Entry(a).State = EntityState.Detached; return HospitalResult.Fail(ConcurrentChange); }   // a twin request, or the same number
			return HospitalResult.Success(a.ID);
		}

		/// Records the payer's answer: approved with its reference, or rejected with the reason.
		public async Task<HospitalResult> DecidePreauthAsync(BusinessContext ctx, int id, bool approve, string? approvalRef, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var a = await Preauths(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			var refusal = HospitalInsuranceRules.PreauthDecisionRefusal(a.Status, approve, approvalRef, reason);
			if (refusal != null) return HospitalResult.Fail(refusal);
			string status = approve ? HospitalPreauthStatus.Approved : HospitalPreauthStatus.Rejected;
			string? reference = approve ? Clip(approvalRef, 60) : null, why = approve ? null : Clip(reason, 300);
			// only the first answer lands: the update is conditional on the request still waiting
			int n = await Preauths(ctx).Where(x => x.ID == id && x.Status == HospitalPreauthStatus.Pending).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Status, status).SetProperty(x => x.ApprovalRef, reference).SetProperty(x => x.Reason, why)
				.SetProperty(x => x.DecidedAt, nowUtc).SetProperty(x => x.DecidedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(a.ID) : HospitalResult.Fail("The payer already answered this request");
		}

		// ====================================================================================
		// CLAIMS
		// ====================================================================================

		/// The payers whose contract is in another currency than the facility's, with that currency's code.
		private async Task<Dictionary<int, string?>> ForeignCurrencyByPayerAsync(BusinessContext ctx, CancellationToken ct)
		{
			var pairs = await (from py in Payers(ctx).AsNoTracking()
							   join l in _db.HospitalPriceLists.AsNoTracking() on py.PriceListId equals l.ID
							   where l.CompanyId == ctx.CompanyId && l.CurrencyId != null
							   select new { py.ID, l.CurrencyId }).ToListAsync(ct);
			var map = new Dictionary<int, string?>();
			foreach (var p in pairs)
				map[p.ID] = _pricing == null ? "#" + p.CurrencyId : (await _pricing.CurrencyOfAsync(ctx, p.CurrencyId, ct)).Code;
			return map;
		}

		private async Task<List<HospitalBatchRow>> BatchRowsAsync(BusinessContext ctx, IQueryable<HospitalClaimBatch> batches, CancellationToken ct)
		{
			var rows = await (from b in batches.AsNoTracking() join py in Payers(ctx).AsNoTracking() on b.PayerId equals py.ID select new { b, py })
				.OrderByDescending(x => x.b.Period).ThenBy(x => x.py.Code).ToListAsync(ct);
			var ids = rows.Select(r => r.b.ID).ToList();
			var foreign = await ForeignCurrencyByPayerAsync(ctx, ct);
			var claims = await Claims(ctx).AsNoTracking().Where(c => ids.Contains(c.BatchId)).ToListAsync(ct);
			var cids = claims.Select(c => c.ID).ToList();
			var lines = await Lines(ctx).AsNoTracking().Where(l => cids.Contains(l.ClaimId)).Select(l => new { l.ClaimId, l.Status, l.InsurerAmount, l.AcceptedAmount }).ToListAsync(ct);
			return rows.Select(r =>
			{
				var cs = claims.Where(c => c.BatchId == r.b.ID).Select(c => c.ID).ToHashSet();
				var ls = lines.Where(l => cs.Contains(l.ClaimId)).ToList();
				return new HospitalBatchRow(r.b, r.py.Name, r.py.NameEn, cs.Count, ls.Sum(l => l.InsurerAmount),
					ls.Where(l => l.Status == HospitalClaimLineStatus.Accepted).Sum(l => l.AcceptedAmount ?? l.InsurerAmount),
					ls.Where(l => l.Status is HospitalClaimLineStatus.Rejected or HospitalClaimLineStatus.Resubmitted).Sum(l => l.InsurerAmount), foreign.GetValueOrDefault(r.py.ID));
			}).ToList();
		}

		public Task<List<HospitalBatchRow>> BatchesAsync(BusinessContext ctx, CancellationToken ct = default) => BatchRowsAsync(ctx, Batches(ctx), ct);

		public async Task<HospitalBatchView?> BatchAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			var row = (await BatchRowsAsync(ctx, Batches(ctx).Where(b => b.ID == id), ct)).FirstOrDefault();
			if (row == null) return null;
			var claims = await (from c in Claims(ctx).AsNoTracking()
								join b in _db.HospitalBills.AsNoTracking() on c.BillId equals b.ID
								join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID
								join po in Policies(ctx).AsNoTracking() on c.PolicyId equals po.ID
								where c.BatchId == id && b.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId
								orderby b.BillNo
								select new { c, b.BillNo, p.Name, p.NameEn, p.Mrn, po.PolicyNo }).ToListAsync(ct);
			var cids = claims.Select(c => c.c.ID).ToList();
			var lines = await Lines(ctx).AsNoTracking().Where(l => cids.Contains(l.ClaimId)).OrderBy(l => l.ID).ToListAsync(ct);
			return new HospitalBatchView(row, claims.Select(c => new HospitalClaimView(c.c, c.BillNo, c.Name, c.NameEn, c.Mrn, c.PolicyNo, lines.Where(l => l.ClaimId == c.c.ID).ToList())).ToList());
		}

		private async Task<HospitalClaimBatch> DraftBatchAsync(BusinessContext ctx, HospitalPayer payer, string period, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			var b = await Batches(ctx).FirstOrDefaultAsync(x => x.PayerId == payer.ID && x.Period == period && x.Status == HospitalClaimBatchStatus.Draft, ct);
			if (b != null) return b;
			var prefix = "CLB-" + period.Replace("-", "") + "-" + payer.Code + "-";
			b = new HospitalClaimBatch
			{
				CompanyId = ctx.CompanyId, BatchNo = await NextAsync(Batches(ctx).Select(x => x.BatchNo), prefix, ct, 2), PayerId = payer.ID, Period = period,
				CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			_db.Set<HospitalClaimBatch>().Add(b);
			await _db.SaveChangesAsync(ct);
			return b;
		}

		/// Gathers a month's closed, unclaimed bills of the payer's eligible patients into its draft batch.
		public async Task<HospitalResult> BuildBatchAsync(BusinessContext ctx, int payerId, string period, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalInsuranceRules.IsPeriod(period)) return HospitalResult.Fail("Choose the month to claim");
			var payer = await Payers(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ID == payerId, ct);
			if (payer == null) return HospitalResult.Fail("not_found");
			var from = DateTime.ParseExact(period + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
			var to = from.AddMonths(1);
			// the contract's currency (its list's, or the facility's) decides the claim's money and its decimals
			int? contractCurrency = payer.PriceListId is int lid
				? await _db.HospitalPriceLists.AsNoTracking().Where(l => l.ID == lid && l.CompanyId == ctx.CompanyId).Select(l => l.CurrencyId).FirstOrDefaultAsync(ct) : null;
			int decimals = _pricing == null ? DefaultDecimals : (await _pricing.CurrencyOfAsync(ctx, contractCurrency, ct)).Decimals;
			var policies = await Policies(ctx).AsNoTracking().Where(p => p.PayerId == payerId).ToListAsync(ct);
			var insured = policies.Select(p => p.PatientId).Distinct().ToList();
			var bills = await _db.HospitalBills.AsNoTracking()
				.Where(b => b.CompanyId == ctx.CompanyId && b.Status == HospitalBillStatus.Closed && b.ClosedAt >= from && b.ClosedAt < to && insured.Contains(b.PatientId))
				.OrderBy(b => b.ClosedAt).ToListAsync(ct);
			var ids = bills.Select(b => b.ID).ToList();
			var claimed = (await Claims(ctx).AsNoTracking().Where(c => ids.Contains(c.BillId)).Select(c => c.BillId).ToListAsync(ct)).ToHashSet();
			var preauthCats = HospitalInsuranceRules.PreauthCategories(payer.PreauthCategories);
			int added = 0;
			HospitalClaimBatch? batch = null;
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
			foreach (var bill in bills)
			{
				// the patient's policy with THIS payer that covers the day the bill closed (the most recently started)
				var policy = policies.Where(p => p.PatientId == bill.PatientId && HospitalInsuranceRules.Ineligibility(p, payer, bill.ClosedAt!.Value) == null)
					.OrderByDescending(p => p.ValidFrom).FirstOrDefault() ?? policies.FirstOrDefault(p => p.PatientId == bill.PatientId);
				var candidate = new HospitalClaimCandidate(bill.ID, bill.CompanyId, bill.PatientId, bill.Status, bill.ClosedAt, bill.PriceListId, bill.CurrencyId);
				if (HospitalInsuranceRules.ClaimRefusal(candidate, ctx.CompanyId, period, contractCurrency, claimed, policy, payer) != null) continue;
				var billLines = await _db.HospitalBillLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.BillId == bill.ID).OrderBy(l => l.ID).ToListAsync(ct);
				if (billLines.Count == 0) continue;
				var claimable = billLines.Select(l => new HospitalClaimableLine(l.ID, l.Code, l.Category,
					HospitalInsuranceRules.LineAmount(l.Quantity, l.UnitPrice, bill.PriceListId, payer.DiscountPct, decimals), !l.OutsideList)).ToList();
				var split = HospitalInsuranceRules.Split(claimable, payer.CopayPct, payer.CopayCap, decimals);
				batch ??= await DraftBatchAsync(ctx, payer, period, employeeId, nowUtc, ct);
				var claim = new HospitalClaim { CompanyId = ctx.CompanyId, BatchId = batch.ID, BillId = bill.ID, PatientId = bill.PatientId, PolicyId = policy!.ID,
					Gross = split.Gross, PatientShare = split.PatientShare, InsurerShare = split.InsurerShare };
				_db.Set<HospitalClaim>().Add(claim);
				await _db.SaveChangesAsync(ct);
				var approved = await Preauths(ctx).AsNoTracking().Where(a => a.PatientId == bill.PatientId && a.PayerId == payerId && a.Status == HospitalPreauthStatus.Approved).ToListAsync(ct);
				for (int i = 0; i < billLines.Count; i++)
				{
					var bl = billLines[i];
					if (!claimable[i].Covered) continue;   // the patient's, in full — not claimed
					var pre = bl.ServiceId == null ? null : approved.FirstOrDefault(a => a.ServiceId == bl.ServiceId);
					_db.Set<HospitalClaimLine>().Add(new HospitalClaimLine
					{
						CompanyId = ctx.CompanyId, ClaimId = claim.ID, BillLineId = bl.ID, Code = bl.Code, Description = bl.Description, DescriptionEn = bl.DescriptionEn, Category = bl.Category,
						Amount = claimable[i].Amount, InsurerAmount = split.InsurerPerLine[i], PreauthId = pre?.ID, MissingPreauth = preauthCats.Contains(bl.Category) && pre == null,
					});
				}
				await _db.SaveChangesAsync(ct);
				added++;
			}
			}
			catch (DbUpdateException)
			{
				// another build of the same payer and month got there first: its draft (one per payer and month)
				// or its claim of the same bill holds the unique index, and this build gives way whole
				await tx.RollbackAsync(); _db.ChangeTracker.Clear();
				return HospitalResult.Fail(ConcurrentChange);
			}
			if (added == 0) { await tx.RollbackAsync(); return HospitalResult.Fail("No closed, unclaimed bills of this payer's covered patients in that month"); }
			await tx.CommitAsync();
			return HospitalResult.Success(batch!.ID);
		}

		public async Task<HospitalResult> SubmitAsync(BusinessContext ctx, int batchId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var b = await Batches(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == batchId, ct);
			if (b == null) return HospitalResult.Fail("not_found");
			if (b.Status != HospitalClaimBatchStatus.Draft) return HospitalResult.Fail("This batch was already submitted");
			if (!await Claims(ctx).AnyAsync(c => c.BatchId == b.ID, ct)) return HospitalResult.Fail("The batch has no claims");
			// conditional on the batch still being a draft: only one submission lands
			int n = await Batches(ctx).Where(x => x.ID == batchId && x.Status == HospitalClaimBatchStatus.Draft).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Status, HospitalClaimBatchStatus.Submitted).SetProperty(x => x.SubmittedAt, nowUtc).SetProperty(x => x.SubmittedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(b.ID) : HospitalResult.Fail("This batch was already submitted");
		}

		/// The payer's remittance for one line: accepted (in full, or the amount it pays), or rejected with its code and reason.
		public async Task<HospitalResult> RecordResponseAsync(BusinessContext ctx, int lineId, bool accept, decimal? acceptedAmount, string? code, string? reason, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var l = await Lines(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == lineId, ct);
			if (l == null) return HospitalResult.Fail("not_found");
			var c = await Claims(ctx).AsNoTracking().FirstAsync(x => x.ID == l.ClaimId, ct);
			var b = await Batches(ctx).AsNoTracking().FirstAsync(x => x.ID == c.BatchId, ct);
			var refusal = HospitalInsuranceRules.ResponseRefusal(b.Status, l.Status, accept, acceptedAmount, l.InsurerAmount, code, reason);
			if (refusal != null) return HospitalResult.Fail(refusal);
			string status = accept ? HospitalClaimLineStatus.Accepted : HospitalClaimLineStatus.Rejected;
			decimal paid = accept ? acceptedAmount ?? l.InsurerAmount : 0;
			string? rc = accept ? null : Clip(code, 20), rr = accept ? null : Clip(reason, 300);
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			// the line's answer and the batch's state move together, and only the first answer for a line lands
			int n = await Lines(ctx).Where(x => x.ID == lineId && x.Status == HospitalClaimLineStatus.Pending).ExecuteUpdateAsync(u => u
				.SetProperty(x => x.Status, status).SetProperty(x => x.AcceptedAmount, paid).SetProperty(x => x.RejectCode, rc)
				.SetProperty(x => x.RejectReason, rr).SetProperty(x => x.DecidedAt, nowUtc), ct);
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail("The payer's answer for this line is already recorded"); }
			var statuses = await (from x in Lines(ctx) join cc in Claims(ctx) on x.ClaimId equals cc.ID where cc.BatchId == b.ID select x.Status).ToListAsync(ct);
			string batchStatus = HospitalInsuranceRules.BatchStatusAfterResponse(statuses);
			DateTime? settled = batchStatus == HospitalClaimBatchStatus.Submitted ? null : nowUtc;
			await Batches(ctx).Where(x => x.ID == b.ID).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, batchStatus).SetProperty(x => x.SettledAt, settled), ct);
			await tx.CommitAsync();
			return HospitalResult.Success(l.ID);
		}

		/// Sends a rejected line again, with the justification, in the payer's draft batch of this month.
		public async Task<HospitalResult> ResubmitAsync(BusinessContext ctx, int lineId, string? justification, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var l = await Lines(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == lineId, ct);
			if (l == null) return HospitalResult.Fail("not_found");
			var refusal = HospitalInsuranceRules.ResubmitRefusal(l.Status, justification);
			if (refusal != null) return HospitalResult.Fail(refusal);
			var c = await Claims(ctx).AsNoTracking().FirstAsync(x => x.ID == l.ClaimId, ct);
			var old = await Batches(ctx).AsNoTracking().FirstAsync(x => x.ID == c.BatchId, ct);
			var payer = await Payers(ctx).AsNoTracking().FirstAsync(p => p.ID == old.PayerId, ct);
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			// the line is taken first, conditionally: two resubmissions of one line cannot both go out
			if (await Lines(ctx).Where(x => x.ID == lineId && x.Status == HospitalClaimLineStatus.Rejected)
				.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, HospitalClaimLineStatus.Resubmitted), ct) != 1)
			{ await tx.RollbackAsync(); return HospitalResult.Fail("Only a rejected line can be resubmitted"); }
			try
			{
			var batch = await DraftBatchAsync(ctx, payer, HospitalInsuranceRules.Period(nowUtc), employeeId, nowUtc, ct);
			var claim = await Claims(ctx).FirstOrDefaultAsync(x => x.BatchId == batch.ID && x.BillId == c.BillId, ct);
			if (claim == null)
			{
				claim = new HospitalClaim { CompanyId = ctx.CompanyId, BatchId = batch.ID, BillId = c.BillId, PatientId = c.PatientId, PolicyId = c.PolicyId, Gross = l.Amount, PatientShare = 0, InsurerShare = l.InsurerAmount };
				_db.Set<HospitalClaim>().Add(claim);
			}
			else { claim.Gross += l.Amount; claim.InsurerShare += l.InsurerAmount; }
			await _db.SaveChangesAsync(ct);
			_db.Set<HospitalClaimLine>().Add(new HospitalClaimLine
			{
				CompanyId = ctx.CompanyId, ClaimId = claim.ID, BillLineId = l.BillLineId, Code = l.Code, Description = l.Description, DescriptionEn = l.DescriptionEn, Category = l.Category,
				Amount = l.Amount, InsurerAmount = l.InsurerAmount, PreauthId = l.PreauthId, MissingPreauth = l.MissingPreauth, Justification = Clip(justification, 500), ResubmitsLineId = l.ID,
			});
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(batch.ID);
			}
			catch (DbUpdateException) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
		}

		public async Task<IReadOnlyList<HospitalRejectionRow>> RejectionsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var rows = await (from l in Lines(ctx).AsNoTracking()
							  join c in Claims(ctx).AsNoTracking() on l.ClaimId equals c.ID
							  join b in Batches(ctx).AsNoTracking() on c.BatchId equals b.ID
							  join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID
							  where l.Status == HospitalClaimLineStatus.Rejected && p.CompanyId == ctx.CompanyId
							  orderby l.DecidedAt descending
							  select new { l, b.BatchNo, b.ID, p.Name, p.NameEn, p.PhotoPath }).Take(300).ToListAsync(ct);
			return rows.Select(r => new HospitalRejectionRow(r.l, r.BatchNo, r.ID, r.Name, r.NameEn, r.PhotoPath)).ToList();
		}

		public async Task<HospitalInsuranceStats> StatsAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var pending = await Preauths(ctx).AsNoTracking().Where(a => a.Status == HospitalPreauthStatus.Pending).Select(a => a.RequestedAt).ToListAsync(ct);
			int? oldest = pending.Count == 0 ? null : (int)Math.Round((nowUtc - pending.Min()).TotalMinutes);
			var period = HospitalInsuranceRules.Period(nowUtc);
			// the month's claims in the facility's currency only: a sum across currencies means nothing
			var foreignPayers = (await ForeignCurrencyByPayerAsync(ctx, ct)).Keys.ToList();
			decimal month = await (from c in Claims(ctx) join b in Batches(ctx) on c.BatchId equals b.ID where b.Period == period && !foreignPayers.Contains(b.PayerId) select (decimal?)c.InsurerShare).SumAsync(ct) ?? 0;
			var decided = await Lines(ctx).AsNoTracking().Where(l => l.Status != HospitalClaimLineStatus.Pending).Select(l => new { l.Status, l.InsurerAmount, l.AcceptedAmount }).ToListAsync(ct);
			var rate = HospitalInsuranceRules.RejectionRate(decided.Where(d => d.Status == HospitalClaimLineStatus.Accepted).Sum(d => d.AcceptedAmount ?? d.InsurerAmount),
				decided.Where(d => d.Status != HospitalClaimLineStatus.Accepted).Sum(d => d.InsurerAmount));
			var settled = await Batches(ctx).AsNoTracking().Where(b => b.SubmittedAt != null && b.SettledAt != null).Select(b => new { b.SubmittedAt, b.SettledAt }).ToListAsync(ct);
			int? days = settled.Count == 0 ? null : (int)Math.Round(settled.Average(s => (s.SettledAt!.Value - s.SubmittedAt!.Value).TotalDays));
			return new HospitalInsuranceStats(pending.Count, oldest, month, rate, days);
		}
	}
}
