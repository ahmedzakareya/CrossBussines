using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H19 — doctors' fees.
	//
	//   · Fee rules (`manage`), on the practitioner's page.
	//   · A month's statement (`bill`) is generated on demand — no job runs — from the closed bills of the doctor's
	//     visits (bill → encounter → practitioner): the patient's part of each line when the patient finished paying
	//     the bill in the month, the payer's part when its claim line was accepted in the month. The money is read
	//     from the bill itself (HospitalBillingService.BillAsync: its split, H18), never recomputed. A part counted
	//     in one statement is never counted in another (a unique index). Regenerating rebuilds a statement still
	//     waiting for the medical director.
	//   · The medical director approves (`manage`), then finance (`bill`): a doctor paid as a vendor gets the journal
	//     through IHospitalLedger (Dr accrued doctor fees, Cr his payable); one paid through payroll is marked for
	//     payroll, with no journal.
	// ==========================================================================================

	public sealed class HospitalFeeRuleInput
	{
		public int PractitionerId { get; set; }
		public string Category { get; set; } = HospitalServiceCategories.Consultation;
		public int? ServiceId { get; set; }
		public string Basis { get; set; } = HospitalFeeBasis.Percent;
		public decimal Value { get; set; }
	}

	public sealed class HospitalFeeSettingsInput
	{
		public int? ExpenseAccountId { get; set; }
		public int? PayableAccountId { get; set; }
	}

	public sealed record HospitalFeeRuleRow(HospitalFeeRule Rule, string? Service, string? ServiceEn, string? ServiceCode);

	public sealed record HospitalFeeLineRow(HospitalFeeStatementLine Line, string PatientName, string PatientNameEn, string Mrn, string? Photo, string? Payer, string? PayerEn);

	public sealed record HospitalFeeStatementView(HospitalPractitioner Doctor, string? DoctorPhoto, string Period, HospitalFeeStatement? Statement,
		IReadOnlyList<HospitalFeeLineRow> Lines, HospitalFeeSettings? Settings, int Decimals, string Currency);

	public sealed class HospitalFeeService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalBillingService _billing;
		private readonly IHospitalLedger _ledger;
		private readonly HospitalPricingService _pricing;

		public HospitalFeeService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService billing, IHospitalLedger ledger, HospitalPricingService pricing)
		{ _db = db; _access = access; _billing = billing; _ledger = ledger; _pricing = pricing; }

		public const string ConcurrentChange = HospitalInsuranceService.ConcurrentChange;
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalFeeRule> Rules(BusinessContext ctx) => _db.Set<HospitalFeeRule>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalFeeStatement> Statements(BusinessContext ctx) => _db.Set<HospitalFeeStatement>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalFeeStatementLine> SLines(BusinessContext ctx) => _db.Set<HospitalFeeStatementLine>().Where(x => x.CompanyId == ctx.CompanyId);

		private async Task<HospitalResult> SaveAsync(CancellationToken ct)
		{
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success();
		}

		// ---------------- rules and settings ----------------

		public async Task<IReadOnlyList<HospitalFeeRuleRow>> RulesAsync(BusinessContext ctx, int practitionerId, CancellationToken ct = default)
		{
			var q = from r in Rules(ctx).AsNoTracking().Where(r => r.PractitionerId == practitionerId)
					join s0 in _db.HospitalServices.AsNoTracking() on r.ServiceId equals s0.ID into ss
					from s in ss.DefaultIfEmpty()
					orderby r.Category, r.ServiceId
					select new { r, Name = s == null ? null : s.Name, NameEn = s == null ? null : s.NameEn, Code = s == null ? null : s.Code };
			return (await q.ToListAsync(ct)).Select(x => new HospitalFeeRuleRow(x.r, x.Name, x.NameEn, x.Code)).ToList();
		}

		public async Task<HospitalResult> SaveRuleAsync(BusinessContext ctx, HospitalFeeRuleInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalPractitioners.AnyAsync(d => d.ID == input.PractitionerId && d.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			string category = input.Category;
			if (input.ServiceId is int sid)
			{
				var s = await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(x => x.ID == sid && x.CompanyId == ctx.CompanyId, ct);
				if (s == null) return HospitalResult.Fail("not_found");
				category = s.Category;
			}
			var why = HospitalFeeRules.RuleRefusal(category, input.Basis, input.Value);
			if (why != null) return HospitalResult.Fail(why);
			// one rule per category (and per service): saving again replaces it
			var r = await Rules(ctx).FirstOrDefaultAsync(x => x.PractitionerId == input.PractitionerId && x.ServiceId == input.ServiceId && (input.ServiceId != null || x.Category == category), ct);
			if (r == null) { r = new HospitalFeeRule { CompanyId = ctx.CompanyId, PractitionerId = input.PractitionerId }; _db.Add(r); }
			r.Category = category; r.ServiceId = input.ServiceId; r.Basis = input.Basis; r.Value = input.Value;
			var res = await SaveAsync(ct);
			return res.Ok ? HospitalResult.Success(r.ID) : res;
		}

		public async Task<HospitalResult> RemoveRuleAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			int n = await Rules(ctx).Where(r => r.ID == id).ExecuteDeleteAsync(ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail("not_found");
		}

		public async Task<HospitalFeeSettings?> SettingsAsync(BusinessContext ctx, CancellationToken ct = default) =>
			await _db.Set<HospitalFeeSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId, ct);

		public async Task<HospitalResult> SaveSettingsAsync(BusinessContext ctx, HospitalFeeSettingsInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			foreach (var a in new[] { input.ExpenseAccountId, input.PayableAccountId })
				if (a is int id && !await _db.Accounts.AnyAsync(x => x.ID == id && x.CompanyID == ctx.CompanyId && x.IsActive && x.IsPostable, ct)) return HospitalResult.Fail("Choose an active posting account");
			var s = await _db.Set<HospitalFeeSettings>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId, ct);
			if (s == null) { s = new HospitalFeeSettings { CompanyId = ctx.CompanyId }; _db.Add(s); }
			s.ExpenseAccountId = input.ExpenseAccountId; s.PayableAccountId = input.PayableAccountId;
			return await SaveAsync(ct);
		}

		// ---------------- the statement ----------------

		public async Task<HospitalFeeStatementView?> StatementAsync(BusinessContext ctx, int practitionerId, string period, CancellationToken ct = default)
		{
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(d => d.ID == practitionerId && d.CompanyId == ctx.CompanyId, ct);
			if (doc == null) return null;
			var photo = doc.EmployeeId is int e ? (await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, new[] { e }, ct)).GetValueOrDefault(e) : null;
			var st = await Statements(ctx).AsNoTracking().FirstOrDefaultAsync(s => s.PractitionerId == practitionerId && s.Period == period, ct);
			var rows = new List<HospitalFeeLineRow>();
			if (st != null)
			{
				var q = from l in SLines(ctx).AsNoTracking().Where(l => l.StatementId == st.ID)
						join p in _db.HospitalPatients.AsNoTracking() on l.PatientId equals p.ID
						join y0 in _db.Set<HospitalPayer>().AsNoTracking() on l.PayerId equals y0.ID into ys
						from y in ys.DefaultIfEmpty()
						orderby l.CollectedOn, l.ID
						select new { l, p.Name, p.NameEn, p.Mrn, p.PhotoPath, Payer = y == null ? null : y.Name, PayerEn = y == null ? null : y.NameEn };
				rows = (await q.ToListAsync(ct)).Select(x => new HospitalFeeLineRow(x.l, x.Name, x.NameEn, x.Mrn, x.PhotoPath, x.Payer, x.PayerEn)).ToList();
			}
			var fac = await _pricing.FacilityCurrencyAsync(ctx, ct);
			return new HospitalFeeStatementView(doc, photo, period, st, rows, await SettingsAsync(ctx, ct), fac.Decimals, fac.Code);
		}

		/// Builds (or rebuilds, while it waits for the medical director) a doctor's statement for a month.
		public async Task<HospitalResult> GenerateAsync(BusinessContext ctx, int practitionerId, string period, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalFeeRules.IsPeriod(period)) return HospitalResult.Fail("Choose the month");
			if (!await _db.HospitalPractitioners.AnyAsync(d => d.ID == practitionerId && d.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var existing = await Statements(ctx).FirstOrDefaultAsync(s => s.PractitionerId == practitionerId && s.Period == period, ct);
			if (existing != null && existing.Status != HospitalFeeStatus.Generated) return HospitalResult.Fail("This statement is approved: it is no longer rebuilt");
			var rules = await Rules(ctx).AsNoTracking().Where(r => r.PractitionerId == practitionerId).ToListAsync(ct);
			if (rules.Count == 0) return HospitalResult.Fail("This doctor has no fee rules yet");
			int dec = (await _pricing.FacilityCurrencyAsync(ctx, ct)).Decimals;

			// the doctor's closed bills (his visits), and every part already counted in another statement
			var bills = await (from b in _db.HospitalBills.AsNoTracking()
							   join e in _db.HospitalEncounters.AsNoTracking() on b.EncounterId equals e.ID
							   where b.CompanyId == ctx.CompanyId && e.CompanyId == ctx.CompanyId && e.PractitionerId == practitionerId && b.Status == HospitalBillStatus.Closed && b.CurrencyId == null
							   select b.ID).ToListAsync(ct);
			var counted = (await (from l in SLines(ctx) join s in Statements(ctx) on l.StatementId equals s.ID
								  where existing == null || s.ID != existing.ID select new { l.BillLineId, l.Part }).ToListAsync(ct)).Select(x => (x.BillLineId, x.Part)).ToHashSet();
			var accepted = await (from cl in _db.Set<HospitalClaimLine>().AsNoTracking()
								  join c in _db.Set<HospitalClaim>().AsNoTracking() on cl.ClaimId equals c.ID
								  join bt in _db.Set<HospitalClaimBatch>().AsNoTracking() on c.BatchId equals bt.ID
								  where cl.CompanyId == ctx.CompanyId && bills.Contains(c.BillId) && cl.Status == HospitalClaimLineStatus.Accepted && cl.BillLineId != null && cl.DecidedAt != null
								  select new { BillLineId = cl.BillLineId!.Value, cl.AcceptedAmount, cl.InsurerAmount, DecidedAt = cl.DecidedAt!.Value, bt.PayerId, cl.RecoupedAmount, cl.RecoupedAt }).ToListAsync(ct);

			var lines = new List<HospitalFeeStatementLine>();
			foreach (var billId in bills)
			{
				var v = await _billing.BillAsync(ctx, billId, nowUtc, ct);
				if (v == null) continue;
				// H63: a charity waiver was never collected — the patient finishes paying what is left after it, and each
				// line's patient part counts only in the share actually paid
				decimal owedGross = v.Totals.Patient + v.Totals.Vat, owedNet = owedGross - v.Totals.Waived;
				decimal paidShare = owedGross <= 0 ? 1 : Math.Max(0, owedNet) / owedGross;
				var paidOn = HospitalFeeRules.PatientCollectedOn(owedNet, v.Payments.Select(p => (p.ReceivedAt, p.Amount)));
				foreach (var l in v.Lines.Where(l => l.Id != null))
				{
					var stored = await _db.HospitalBillLines.AsNoTracking().Where(x => x.ID == l.Id).Select(x => new { x.ServiceId }).FirstAsync(ct);
					var rule = HospitalFeeRules.Pick(rules, stored.ServiceId, l.Category);
					if (rule == null) continue;
					bool owes = l.Patient > 0;
					// the patient's part: collected when he finished paying the bill, in this month
					if (owes && paidOn is DateTime pd && HospitalFeeRules.Period(pd) == period && !counted.Contains((l.Id!.Value, HospitalFeePart.Patient)))
						lines.Add(Line(ctx, l, v, HospitalFeePart.Patient, null, pd, l.Patient, Math.Round(l.Patient * paidShare, 4), rule, owes, dec));
					// the payer's part: collected when its claim line was accepted, in this month
					foreach (var a in accepted.Where(a => a.BillLineId == l.Id && HospitalFeeRules.Period(a.DecidedAt) == period && HospitalFeeRules.PayerPartCounts(l.Insurer, v.Cover?.PayerId, a.PayerId)))
						if (!counted.Contains((l.Id!.Value, HospitalFeePart.Payer)))
							// H82: a part not counted yet is counted net of what the payer already took back
							lines.Add(Line(ctx, l, v, HospitalFeePart.Payer, a.PayerId, a.DecidedAt, a.InsurerAmount, HospitalRecoupRules.NetAccepted(a.AcceptedAmount ?? a.InsurerAmount, a.RecoupedAmount), rule, owes, dec));
					// H82: the payer took back, this month, part of a line whose payer part an EARLIER statement already counted
					foreach (var a in accepted.Where(a => a.BillLineId == l.Id && a.RecoupedAmount is decimal && a.RecoupedAt is DateTime ra && HospitalFeeRules.Period(ra) == period
						&& HospitalFeeRules.PayerPartCounts(l.Insurer, v.Cover?.PayerId, a.PayerId)))
						if (counted.Contains((l.Id!.Value, HospitalFeePart.Payer)) && !counted.Contains((l.Id!.Value, HospitalFeePart.Recoup)))
							lines.Add(new HospitalFeeStatementLine
							{
								CompanyId = ctx.CompanyId, BillLineId = l.Id!.Value, Part = HospitalFeePart.Recoup, PatientId = v.Bill.PatientId, PayerId = a.PayerId, CollectedOn = a.RecoupedAt!.Value.Date,
								Description = l.Description, DescriptionEn = l.DescriptionEn, Billed = 0, Collected = -a.RecoupedAmount!.Value, Basis = rule.Basis, Rate = rule.Value,
								Share = HospitalRecoupRules.RecoupShare(rule, a.RecoupedAmount!.Value, a.AcceptedAmount ?? a.InsurerAmount, l.Quantity, owes, dec),
							});
				}
			}

			await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
			var st = existing;
			if (st == null)
			{
				var nos = await Statements(ctx).Where(s => s.StatementNo.StartsWith("FS-" + period + "-")).Select(s => s.StatementNo).ToListAsync(ct);
				st = new HospitalFeeStatement { CompanyId = ctx.CompanyId, StatementNo = HospitalDentalPracticeRules.NextNo(nos, "FS-" + period + "-", 3), PractitionerId = practitionerId, Period = period };
				_db.Add(st);
			}
			else await SLines(ctx).Where(l => l.StatementId == st.ID).ExecuteDeleteAsync(ct);
			st.Billed = lines.Sum(l => l.Billed); st.Collected = lines.Sum(l => l.Collected); st.Share = lines.Sum(l => l.Share);
			st.GeneratedAt = nowUtc; st.GeneratedByEmployeeId = employeeId;
			var r = await SaveAsync(ct);
			if (!r.Ok) return r;
			foreach (var l in lines) { l.StatementId = st.ID; _db.Add(l); }
			r = await SaveAsync(ct);
			if (!r.Ok) return r;
			if (tx != null) await tx.CommitAsync(ct);
			return HospitalResult.Success(st.ID);
		}

		private static HospitalFeeStatementLine Line(BusinessContext ctx, HospitalBillLineView l, HospitalBillView v, string part, int? payerId, DateTime on, decimal billed, decimal collected,
			HospitalFeeRule rule, bool owes, int dec) => new()
		{
			CompanyId = ctx.CompanyId, BillLineId = l.Id!.Value, Part = part, PatientId = v.Bill.PatientId, PayerId = payerId, CollectedOn = on.Date,
			Description = l.Description, DescriptionEn = l.DescriptionEn, Billed = billed, Collected = collected, Basis = rule.Basis, Rate = rule.Value,
			Share = HospitalFeeRules.Share(rule, part, collected, l.Quantity, owes, dec),
		};

		/// The next approval: the medical director (`manage`), then finance (`bill`) — which posts for a vendor doctor.
		public async Task<HospitalResult> ApproveAsync(BusinessContext ctx, int statementId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var st = await Statements(ctx).FirstOrDefaultAsync(s => s.ID == statementId, ct);
			if (st == null) return HospitalResult.Fail("not_found");
			var next = HospitalFeeRules.Next(st.Status);
			if (next == null) return HospitalResult.Fail("This statement is fully approved");
			if (!await Can(ctx, next == HospitalFeeStatus.DirectorApproved ? HospitalActions.Manage : HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			if (next == HospitalFeeStatus.DirectorApproved) { st.Status = next; st.DirectorApprovedAt = nowUtc; st.DirectorEmployeeId = employeeId; return await SaveAsync(ct); }

			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstAsync(d => d.ID == st.PractitionerId && d.CompanyId == ctx.CompanyId, ct);
			await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
			if (doc.PayChannel == HospitalPayChannels.Vendor && st.Share > 0)
			{
				var s = await SettingsAsync(ctx, ct);
				if (s?.ExpenseAccountId is not int exp || s.PayableAccountId is not int pay) return HospitalResult.Fail("Map the doctor fee accounts first");
				var memo = st.StatementNo + " · " + doc.NameEn;
				var (ok, error, entryId) = await _ledger.PostAsync(ctx, nowUtc, "أتعاب " + doc.Name + " — " + st.Period, "Doctor fees " + doc.NameEn + " — " + st.Period, "HospitalFeeStatement", st.ID,
					new[] { new HospitalLedgerLine(exp, st.Share, 0, memo), new HospitalLedgerLine(pay, 0, st.Share, memo) }, ct);
				if (!ok) return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? "");
				st.JournalEntryId = entryId;
			}
			st.Status = next; st.FinanceApprovedAt = nowUtc; st.FinanceEmployeeId = employeeId;
			var r = await SaveAsync(ct);
			if (r.Ok && tx != null) await tx.CommitAsync(ct);
			return r.Ok ? HospitalResult.Success(st.ID) : r;
		}
	}
}
