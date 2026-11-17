using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H162 — insurance for this visit (see HospitalVisitCoverages.cs). For one appointment the desk sees:
	//   · the payer and policy that cover the visit's day (or why none does), and the visit's latest verification —
	//     when, by whom, how (card, portal, phone, letter, the patient's word) and the insurer's reference;
	//   · the visit's services (the consultation, then what the doctor ordered) with their price, whether the contract
	//     needs prior approval for them and that approval's state;
	//   · the patient's share: ESTIMATED from the contract (copay %, cap, outside-list lines) before the bill exists,
	//     and FINAL once the visit's bill closed (its frozen split) — side by side;
	//   · the NEXT STEP, one sentence: verify, decide the payer, request or await an approval, tell the patient a
	//     service is refused, collect the share — or nothing to do.
	// Verifying records the decision (insured under a named policy that covers the day, or self-pay) with its source.
	// ==========================================================================================

	public sealed class HospitalVisitCoverageInput
	{
		public string? Decision { get; set; }
		public int? PolicyId { get; set; }
		public string? Source { get; set; }
		public string? Reference { get; set; }
		public string? Note { get; set; }
	}

	public sealed record HospitalVisitService(int ServiceId, string Code, string Name, string NameEn, string Category, decimal Price, bool OutsideList, bool NeedsApproval, string? Approval, string? ApprovalRef);

	public sealed record HospitalVisitCoverageSheet(HospitalAppointment Appointment, string PatientName, string? PatientNameEn, string Mrn,
		HospitalPatientPolicy? Policy, HospitalPayer? Payer, string? Ineligible, IReadOnlyList<(HospitalPatientPolicy Policy, HospitalPayer Payer)> Policies,
		HospitalVisitCoverage? Latest, string? VerifiedBy, IReadOnlyList<HospitalVisitService> Services,
		decimal EstimatedTotal, decimal EstimatedShare, decimal? FinalShare, decimal? FinalInsurer, decimal ShareOwed, string NextStep, string? NextService);

	public static class HospitalVisitCoverageRules
	{
		public static string? Refuse(HospitalVisitCoverageInput i)
		{
			if (!HospitalCoverageDecision.All.Contains(i.Decision ?? "")) return "Choose insured or self-pay";
			if (!HospitalCoverageSource.All.Contains(i.Source ?? "")) return "Choose how the cover was verified";
			if (i.Decision == HospitalCoverageDecision.Insured && i.PolicyId == null) return "Choose the policy that covers the visit";
			if (i.Decision == HospitalCoverageDecision.Insured && i.Source is HospitalCoverageSource.Portal or HospitalCoverageSource.Phone && string.IsNullOrWhiteSpace(i.Reference))
				return "Write the insurer's reference for the check";
			return null;
		}

		/// The next step, in the order the desk must take them (keys of the strings the desk reads).
		public static (string Step, string? Service) Next(bool coveredToday, HospitalVisitCoverage? latest, DateTime todayLocal, IReadOnlyList<HospitalVisitService> services, decimal shareOwed)
		{
			if (latest == null || latest.VerifiedAt.Date < todayLocal.Date.AddDays(-1)) return coveredToday ? ("next:verify", null) : ("next:decide", null);
			if (latest.Decision == HospitalCoverageDecision.Insured)
			{
				if (services.FirstOrDefault(s => s.NeedsApproval && s.Approval == HospitalPreauthStatus.Rejected) is { } rej) return ("next:refused", rej.Name);
				if (services.FirstOrDefault(s => s.NeedsApproval && s.Approval == null) is { } req) return ("next:request", req.Name);
				if (services.FirstOrDefault(s => s.NeedsApproval && s.Approval == HospitalPreauthStatus.Pending) is { } wait) return ("next:await", wait.Name);
			}
			if (shareOwed > 0) return ("next:collect", null);
			return ("next:none", null);
		}
	}

	public sealed class HospitalVisitCoverageService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalPricingService _pricing;
		private readonly HospitalBillingService _billing;
		public HospitalVisitCoverageService(CrossDbContext db, IHospitalAccessService access, HospitalPricingService pricing, HospitalBillingService billing) { _db = db; _access = access; _pricing = pricing; _billing = billing; }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number == 208;

		public async Task<HospitalVisitCoverageSheet?> SheetAsync(BusinessContext ctx, int appointmentId, DateTime todayLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			int cid = ctx.CompanyId;
			var a = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == appointmentId && x.CompanyId == cid, ct);
			if (a == null) return null;
			var p = await _db.HospitalPatients.AsNoTracking().FirstAsync(x => x.ID == a.PatientId && x.CompanyId == cid, ct);
			var day = a.AppointmentDate.Date;
			var all = await (from po in _db.Set<HospitalPatientPolicy>().AsNoTracking()
							 join py in _db.Set<HospitalPayer>().AsNoTracking() on po.PayerId equals py.ID
							 where po.CompanyId == cid && py.CompanyId == cid && po.PatientId == p.ID
							 select new { po, py }).ToListAsync(ct);
			var eligible = all.Where(x => HospitalInsuranceRules.Ineligibility(x.po, x.py, day) == null).OrderByDescending(x => x.po.ValidFrom).ToList();
			string? why = eligible.Count == 0 && all.Count > 0 ? all.Select(x => HospitalInsuranceRules.Ineligibility(x.po, x.py, day)).FirstOrDefault(w => w != null) : null;

			HospitalVisitCoverage? latest = null; string? by = null;
			try { latest = await _db.Set<HospitalVisitCoverage>().AsNoTracking().Where(v => v.CompanyId == cid && v.AppointmentId == a.ID).OrderByDescending(v => v.ID).FirstOrDefaultAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			if (latest?.VerifiedByEmployeeId is int eid)
				by = await _db.Employee.AsNoTracking().Where(e => e.ID == eid && e.EmpCompanyID == cid).Select(e => DisplayName.Of(e.FullName, e.FullNameEn)).FirstOrDefaultAsync(ct);
			// the cover the visit is read under: the decision recorded, else the policy that covers the day
			var chosen = latest?.Decision == HospitalCoverageDecision.SelfPay ? null
				: latest?.PolicyId is int lp ? all.FirstOrDefault(x => x.po.ID == lp) : null;
			chosen ??= latest?.Decision == HospitalCoverageDecision.SelfPay ? null : eligible.FirstOrDefault();

			// the visit's services: the consultation, then what the doctor ordered
			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(x => x.ID == a.PractitionerId && x.CompanyId == cid, ct);
			var fee = doc?.ConsultationServiceId is int sid ? await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.ID == sid && s.CompanyId == cid, ct) : null;
			fee ??= await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == cid && s.IsActive && s.Category == HospitalServiceCategories.Consultation && s.DepartmentId == doc!.DepartmentId, ct);
			fee ??= await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == cid && s.IsActive && s.Code == "CONS-GEN", ct);
			var services = new List<HospitalService>(); if (fee != null) services.Add(fee);
			var enc = await _db.Set<HospitalEncounter>().AsNoTracking().Where(e => e.CompanyId == cid && e.AppointmentId == a.ID).OrderByDescending(e => e.ID).FirstOrDefaultAsync(ct);
			if (enc != null)
			{
				var ids = await _db.Set<HospitalEncounterOrder>().AsNoTracking().Where(o => o.CompanyId == cid && o.EncounterId == enc.ID).Select(o => o.ServiceId).ToListAsync(ct);
				services.AddRange(await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == cid && ids.Contains(s.ID)).ToListAsync(ct));
			}
			var needs = (chosen?.py.PreauthCategories ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var sids = services.Select(s => s.ID).ToList();
			var approvals = chosen == null ? new List<HospitalPreauth>() : await _db.Set<HospitalPreauth>().AsNoTracking()
				.Where(x => x.CompanyId == cid && x.PatientId == p.ID && x.PolicyId == chosen.po.ID && sids.Contains(x.ServiceId)).OrderByDescending(x => x.ID).ToListAsync(ct);
			var lines = new List<HospitalVisitService>(); int decimals = 3;
			foreach (var s in services)
			{
				var q = await _pricing.QuoteServiceAsync(ctx, chosen?.py.PriceListId, s, ct);
				decimals = (await _pricing.CurrencyOfAsync(ctx, q.CurrencyId, ct)).Decimals;
				var ap = approvals.FirstOrDefault(x => x.ServiceId == s.ID);
				lines.Add(new HospitalVisitService(s.ID, s.Code, s.Name, s.NameEn, s.Category, q.Price, q.OutsideList, needs.Contains(s.Category), ap?.Status, ap?.ApprovalRef));
			}
			decimal total = lines.Sum(l => l.Price), share = total;
			if (chosen != null && lines.Count > 0)
			{
				// a service the insurer refused is not covered: the split puts it on the patient in full
				var split = HospitalCoverage.Of(chosen.py, chosen.po, chosen.py.PriceListId,
					lines.Select(l => new HospitalSplitLine(null, l.Code, l.Category, 1, l.Price, l.OutsideList || (l.NeedsApproval && l.Approval == HospitalPreauthStatus.Rejected))).ToList(), decimals);
				total = split.Split.Gross; share = split.Split.PatientShare;
			}

			// the final share: the visit's bill, once closed (its frozen split), and what of the patient's part is still owed
			decimal? finalShare = null, finalInsurer = null; decimal owed = 0;
			if (enc != null)
			{
				var bills = await _db.HospitalBills.AsNoTracking().Where(b => b.CompanyId == cid && b.EncounterId == enc.ID).Select(b => b.ID).ToListAsync(ct);
				foreach (var bid in bills)
				{
					var frozen = await HospitalCoverage.FrozenAsync(_db, ctx, bid, ct);
					if (frozen is var (head, _)) { finalShare = (finalShare ?? 0) + head.PatientShare; finalInsurer = (finalInsurer ?? 0) + head.InsurerShare; }
				}
				if (bills.Count > 0) owed = (await _billing.BillsAsync(ctx, null, p.ID, nowUtc, ct)).Where(b => bills.Contains(b.Id)).Sum(b => Math.Max(0, b.Total - b.Paid));
			}
			var (step, svc) = HospitalVisitCoverageRules.Next(eligible.Count > 0, latest, todayLocal, lines, owed);
			return new HospitalVisitCoverageSheet(a, p.Name, p.NameEn, p.Mrn, chosen?.po, chosen?.py, why, eligible.Select(x => (x.po, x.py)).ToList(), latest, by, lines,
				total, share, finalShare, finalInsurer, owed, step, svc);
		}

		public async Task<HospitalResult> VerifyAsync(BusinessContext ctx, int appointmentId, HospitalVisitCoverageInput input, int? employeeId, DateTime todayLocal, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Register, null, ct)) return HospitalResult.Fail("forbidden");
			var why = HospitalVisitCoverageRules.Refuse(input);
			if (why != null) return HospitalResult.Fail(why);
			var a = await _db.HospitalAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.ID == appointmentId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			if (a.Status is HospitalAppointmentStatus.Cancelled or HospitalAppointmentStatus.NoShow) return HospitalResult.Fail("The visit is not taking place");
			HospitalPatientPolicy? po = null; HospitalPayer? py = null;
			if (input.Decision == HospitalCoverageDecision.Insured)
			{
				po = await _db.Set<HospitalPatientPolicy>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.PolicyId && x.CompanyId == ctx.CompanyId && x.PatientId == a.PatientId, ct);
				py = po == null ? null : await _db.Set<HospitalPayer>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == po.PayerId && x.CompanyId == ctx.CompanyId, ct);
				if (po == null || py == null) return HospitalResult.Fail("not_found");
				var inel = HospitalInsuranceRules.Ineligibility(po, py, a.AppointmentDate);
				if (inel != null) return HospitalResult.Fail("This policy does not cover the visit's day");
			}
			var row = new HospitalVisitCoverage
			{
				CompanyId = ctx.CompanyId, AppointmentId = a.ID, PatientId = a.PatientId, Decision = input.Decision!, PolicyId = po?.ID, PayerId = py?.ID, Source = input.Source!,
				Reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim()[..Math.Min(60, input.Reference.Trim().Length)],
				VerifiedAt = nowUtc, VerifiedByEmployeeId = employeeId, Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim()[..Math.Min(300, input.Note.Trim().Length)],
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			// the estimate the patient is told, as read under this decision
			var sheet = await SheetAsync(ctx, a.ID, todayLocal, nowUtc, ct);
			if (sheet != null)
			{
				var saved = await _db.Set<HospitalVisitCoverage>().FirstAsync(x => x.ID == row.ID, ct);
				saved.EstimatedTotal = sheet.EstimatedTotal; saved.EstimatedShare = sheet.EstimatedShare;
				await _db.SaveChangesAsync(ct);
			}
			return HospitalResult.Success(row.ID);
		}

		/// For the board and the card: today's appointments with a current verification (id → decision).
		public async Task<IReadOnlyDictionary<int, string>> VerifiedTodayAsync(BusinessContext ctx, IReadOnlyCollection<int> appointmentIds, CancellationToken ct = default)
		{
			try
			{
				return (await _db.Set<HospitalVisitCoverage>().AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId && appointmentIds.Contains(v.AppointmentId))
					.Select(v => new { v.ID, v.AppointmentId, v.Decision }).ToListAsync(ct)).GroupBy(v => v.AppointmentId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.ID).First().Decision);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new Dictionary<int, string>(); }
		}
	}
}
