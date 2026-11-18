using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H17 — orthodontic cases and the implant register.
	//
	//   · A case (`consult` opens it and logs its visits; `bill` or `consult` records what was paid): the rules
	//     are HospitalDentalCasesRules. A payment recorded here is the contract's own account of what came in;
	//     posting it to the books stays billing's (H6).
	//   · An implant (`consult`): planned, then placed with its lot (healing), restored or failed.
	//   · Nothing is posted here, and no job runs. Wired through HospitalDentalCasesModel.Configure.
	// ==========================================================================================

	public sealed class HospitalOrthoCaseInput
	{
		public int PatientId { get; set; }
		public int? PractitionerId { get; set; }
		public string Appliance { get; set; } = "FixedMetal";
		public DateTime StartDate { get; set; }
		public int PlannedMonths { get; set; }
		public int? Stages { get; set; }
		public decimal ContractTotal { get; set; }
		public decimal MonthlyInstalment { get; set; }
		public DateTime? NextVisit { get; set; }
		/// H185: the diagnosis (required, except for a retention-only case).
		public string? AngleClass { get; set; }
		public decimal? OverjetMm { get; set; }
		public decimal? OverbiteMm { get; set; }
		public decimal? CrowdingUpperMm { get; set; }
		public decimal? CrowdingLowerMm { get; set; }
		public int? Iotn { get; set; }
	}

	public sealed class HospitalOrthoVisitInput
	{
		public DateTime? VisitDate { get; set; }
		public int? AlignerStage { get; set; }
		public DateTime? NextVisit { get; set; }
		public string? Note { get; set; }
		public string? Status { get; set; }
		/// H185: the adjustment's detail, and the retainers when the appliance comes off.
		public string? ArchwireUpper { get; set; }
		public string? ArchwireLower { get; set; }
		public string? Elastics { get; set; }
		public string? Hygiene { get; set; }
		public int? Breakages { get; set; }
		public string? RetainerUpper { get; set; }
		public string? RetainerLower { get; set; }
	}

	public sealed class HospitalImplantInput
	{
		public int PatientId { get; set; }
		public int Tooth { get; set; }
		public string System { get; set; } = "";
		public decimal Diameter { get; set; }
		public decimal Length { get; set; }
		public int? SurgeonId { get; set; }
	}

	public sealed class HospitalImplantMoveInput
	{
		public string To { get; set; } = "";
		public string? Lot { get; set; }
		public DateTime? On { get; set; }
		public int? Isq { get; set; }
		public string? Reason { get; set; }
		/// H168: the insertion torque at placement; at restoration, how it is loaded, what is fitted, and any exception.
		public int? TorqueNcm { get; set; }
		public string? LoadingKind { get; set; }
		public string? Restoration { get; set; }
		public string? ExceptionReason { get; set; }
		/// H186: at placement the catalogue reference and the graft with its lot; at restoration the abutment's lot.
		public string? Ref { get; set; }
		public string? GraftMaterial { get; set; }
		public string? GraftLot { get; set; }
		public string? AbutmentLot { get; set; }
	}

	/// H186: a peri-implant maintenance review.
	public sealed class HospitalImplantReviewInput
	{
		public DateTime? On { get; set; }
		public decimal MaxProbingMm { get; set; }
		public bool Bleeding { get; set; }
		public bool Suppuration { get; set; }
		public decimal? BoneLossMm { get; set; }
		public bool Mobility { get; set; }
		public string? Note { get; set; }
	}

	/// H186: an implant, graft or abutment carrying a lot — who received it.
	public sealed record HospitalLotTraceRow(int ImplantId, string ImplantNo, int PatientId, string PatientName, string PatientNameEn, string Mrn, string? Phone, int Tooth, string What, string Lot, DateTime? PlacedOn, string Stage);

	public sealed class HospitalImplantMeasureInput
	{
		public string? Moment { get; set; }
		public int? Isq { get; set; }
		public int? TorqueNcm { get; set; }
		public DateTime? On { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalImplantProtocolInput
	{
		public int? MinIsqImmediate { get; set; }
		public int? MinIsqEarly { get; set; }
		public int? MinIsqConventional { get; set; }
		public int? MinTorqueImmediate { get; set; }
		public string? Reference { get; set; }
	}

	public sealed record HospitalOrthoRow(HospitalOrthoCase Case, string PatientName, string PatientNameEn, string Mrn, string? Photo, string Sex, DateTime? BirthDate,
		int Visits, decimal Paid, decimal Due, int Progress, bool Missed, IReadOnlyList<string>? Warnings = null, DateTime? NextReview = null);

	public sealed record HospitalOrthoStats(int Active, int AdjustmentsThisWeek, decimal InstalmentsDue, int Missed);

	public sealed record HospitalImplantRow(HospitalImplant Implant, string PatientName, string PatientNameEn, string Mrn, string? Photo, HospitalDentist? Surgeon);

	public sealed record HospitalImplantStats(int ThisYear, int Healing, decimal? SurvivalRate, int Lots);

	public sealed class HospitalDentalCasesService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalDentalService _dental;

		private readonly HospitalBillingService? _billing;   // H167: an instalment is collected at the cashier
		public HospitalDentalCasesService(CrossDbContext db, IHospitalAccessService access, HospitalDentalService dental, HospitalBillingService? billing = null) { _db = db; _access = access; _dental = dental; _billing = billing; }

		public const string ConcurrentChange = HospitalInsuranceService.ConcurrentChange;
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private IQueryable<HospitalOrthoCase> Cases(BusinessContext ctx) => _db.Set<HospitalOrthoCase>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalImplant> Implants(BusinessContext ctx) => _db.Set<HospitalImplant>().Where(x => x.CompanyId == ctx.CompanyId);

		private async Task<HospitalResult> SaveAsync(CancellationToken ct)
		{
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success();
		}

		// ====================================================================================
		// ORTHODONTICS
		// ====================================================================================

		public async Task<(IReadOnlyList<HospitalOrthoRow> Rows, HospitalOrthoStats Stats)> OrthoAsync(BusinessContext ctx, DateTime today, CancellationToken ct = default)
		{
			var q = from c in Cases(ctx).AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID
					select new { c, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.Sex, p.BirthDate };
			var rows = await q.OrderBy(x => x.c.Status == HospitalOrthoStatus.Active ? 0 : x.c.Status == HospitalOrthoStatus.Retention ? 1 : 2).ThenBy(x => x.c.NextVisit).Take(300).ToListAsync(ct);
			var ids = rows.Select(r => r.c.ID).ToList();
			var visits = await _db.Set<HospitalOrthoVisit>().AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId && ids.Contains(v.CaseId)).Select(v => new { v.CaseId, v.VisitDate, v.ID, v.Hygiene, v.Breakages }).ToListAsync(ct);
			var paid = await _db.Set<HospitalOrthoPayment>().AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId && ids.Contains(v.CaseId)).GroupBy(v => v.CaseId).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) }).ToListAsync(ct);
			var list = rows.Select(r =>
			{
				var c = r.c; decimal p = paid.FirstOrDefault(x => x.Key == c.ID)?.Sum ?? 0;
				bool open = HospitalOrthoStatus.Open(c.Status);
				// H185: the case's warnings and, in retention, its next review
				var mine = visits.Where(v => v.CaseId == c.ID).OrderByDescending(v => v.VisitDate).ThenByDescending(v => v.ID).Select(v => (v.VisitDate, v.Hygiene, v.Breakages)).ToList();
				DateTime? review = c.Status == HospitalOrthoStatus.Retention && c.DebondedOn is DateTime off ? HospitalOrthoClinicalRules.NextReview(off, HospitalOrthoClinicalRules.LastSeen(off, mine)) : null;
				return new HospitalOrthoRow(c, r.Name, r.NameEn, r.Mrn, r.PhotoPath, r.Sex, r.BirthDate, visits.Count(v => v.CaseId == c.ID), p,
					open ? HospitalDentalCasesRules.Due(c.ContractTotal, c.MonthlyInstalment, c.StartDate, today, p) : Math.Max(0, c.ContractTotal - p),
					c.Status == HospitalOrthoStatus.Completed ? 100 : HospitalDentalCasesRules.Progress(c.StartDate, c.PlannedMonths, today),
					HospitalDentalCasesRules.Missed(c.Status, c.NextVisit, today), HospitalOrthoClinicalRules.Warnings(c, mine, today), review);
			}).ToList();
			return (list, new HospitalOrthoStats(list.Count(r => HospitalOrthoStatus.Open(r.Case.Status)), visits.Count(v => v.VisitDate >= today.AddDays(-6)),
				list.Where(r => HospitalOrthoStatus.Open(r.Case.Status)).Sum(r => r.Due), list.Count(r => r.Missed)));
		}

		public async Task<HospitalResult> OpenCaseAsync(BusinessContext ctx, HospitalOrthoCaseInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (input.PractitionerId is int d && !await _db.HospitalPractitioners.AnyAsync(x => x.ID == d && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var why = HospitalDentalCasesRules.CaseRefusal(input.Appliance, input.PlannedMonths, input.Stages, input.ContractTotal, input.MonthlyInstalment);
			if (why != null) return HospitalResult.Fail(why);
			bool retentionOnly = input.Appliance == "Retention";
			if (!retentionOnly && HospitalOrthoClinicalRules.DiagnosisRefusal(input.AngleClass, input.OverjetMm, input.OverbiteMm, input.CrowdingUpperMm, input.CrowdingLowerMm, input.Iotn) is string dx) return HospitalResult.Fail(dx);
			if (await Cases(ctx).AnyAsync(c => c.PatientId == input.PatientId && (c.Status == HospitalOrthoStatus.Active || c.Status == HospitalOrthoStatus.Retention), ct))
				return HospitalResult.Fail("This patient already has an open orthodontic case");
			var nos = await Cases(ctx).Where(c => c.CaseNo.StartsWith("OR-")).Select(c => c.CaseNo).ToListAsync(ct);
			var c = new HospitalOrthoCase
			{
				CompanyId = ctx.CompanyId, CaseNo = HospitalDentalPracticeRules.NextNo(nos, "OR-", 4), PatientId = input.PatientId, PractitionerId = input.PractitionerId,
				Appliance = input.Appliance, StartDate = input.StartDate.Date, PlannedMonths = input.PlannedMonths, Stages = input.Stages, CurrentStage = input.Stages == null ? null : 1,
				ContractTotal = input.ContractTotal, MonthlyInstalment = input.MonthlyInstalment, NextVisit = input.NextVisit?.Date,
				Status = input.Appliance == "Retention" ? HospitalOrthoStatus.Retention : HospitalOrthoStatus.Active, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				AngleClass = retentionOnly ? null : input.AngleClass, OverjetMm = retentionOnly ? null : input.OverjetMm, OverbiteMm = retentionOnly ? null : input.OverbiteMm,
				CrowdingUpperMm = retentionOnly ? null : input.CrowdingUpperMm, CrowdingLowerMm = retentionOnly ? null : input.CrowdingLowerMm, Iotn = retentionOnly ? null : input.Iotn,
			};
			_db.Add(c);
			var r = await SaveAsync(ct);
			return r.Ok ? HospitalResult.Success(c.ID) : r;
		}

		/// An adjustment logged on the case: the aligner stage reached, the next visit, and the case's state (retention, completed).
		public async Task<HospitalResult> LogVisitAsync(BusinessContext ctx, int caseId, HospitalOrthoVisitInput input, int? practitionerId, DateTime nowUtc, DateTime today, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).FirstOrDefaultAsync(x => x.ID == caseId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (!HospitalOrthoStatus.Open(c.Status)) return HospitalResult.Fail("This case is closed");
			var day = (input.VisitDate ?? today).Date;
			if (day > today.Date || day < c.StartDate) return HospitalResult.Fail("The visit is between the case's start and today");
			if (input.AlignerStage != null && (c.Stages == null || input.AlignerStage < 1 || input.AlignerStage > c.Stages)) return HospitalResult.Fail("The aligner stage is one of the case's stages");
			if (input.NextVisit is DateTime nv && nv.Date <= day) return HospitalResult.Fail("The next visit comes after this one");
			if (input.Status != null && !HospitalOrthoStatus.All.Contains(input.Status)) return HospitalResult.Fail("not_found");
			if (HospitalOrthoClinicalRules.VisitRefusal(input.Hygiene, input.Breakages) is string vw) return HospitalResult.Fail(vw);
			// H185: the appliance comes off — the retainers are chosen, and the retention reviews start from today
			bool debond = input.Status == HospitalOrthoStatus.Retention && c.Status == HospitalOrthoStatus.Active;
			if (debond && HospitalOrthoClinicalRules.RetentionRefusal(input.RetainerUpper, input.RetainerLower) is string rw) return HospitalResult.Fail(rw);
			_db.Add(new HospitalOrthoVisit
			{
				CompanyId = ctx.CompanyId, CaseId = caseId, VisitDate = day, AlignerStage = input.AlignerStage, Note = Clip(input.Note, 300), PractitionerId = practitionerId, CreatedAt = nowUtc,
				ArchwireUpper = Clip(input.ArchwireUpper, 40), ArchwireLower = Clip(input.ArchwireLower, 40), Elastics = Clip(input.Elastics, 60), Hygiene = input.Hygiene, Breakages = input.Breakages,
			});
			if (debond) { c.DebondedOn = day; c.RetainerUpper = input.RetainerUpper; c.RetainerLower = input.RetainerLower; }
			if (input.AlignerStage != null) c.CurrentStage = input.AlignerStage;
			c.NextVisit = input.NextVisit?.Date;
			if (input.Status != null) c.Status = input.Status;
			var r = await SaveAsync(ct);
			return r.Ok ? HospitalResult.Success(caseId) : r;
		}

		/// H167: an instalment is money, so it is the cashier's act (`bill`, never `consult` alone) and goes the way all the
		/// hospital's money goes: a charge on the patient's bill at the instalment's amount, as the service the orthodontics
		/// settings name, then a collection on that bill in the cashier's open shift — a receipt, posted to the ledger. The
		/// case row then links the charge and the receipt. A refused collection takes the charge back off the bill.
		public async Task<HospitalResult> RecordPaymentAsync(BusinessContext ctx, int caseId, decimal amount, string? method, string? reference, int? employeeId, DateTime nowUtc, DateTime today, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == caseId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (amount <= 0) return HospitalResult.Fail("A payment is more than zero");
			if (!HospitalPaymentMethods.IsKnown(method)) return HospitalResult.Fail("Unknown payment method");
			decimal paid = await _db.Set<HospitalOrthoPayment>().Where(p => p.CompanyId == ctx.CompanyId && p.CaseId == caseId).SumAsync(p => (decimal?)p.Amount, ct) ?? 0;
			if (paid + amount > c.ContractTotal) return HospitalResult.Fail("This is more than what is left on the contract");
			if (_billing == null || employeeId is not int me) return HospitalResult.Fail("forbidden");
			var unit = await HospitalUnitSettingsService.RowAsync(_db, ctx.CompanyId, HospitalKitUnits.DentalOrtho, ct);
			if (unit?.SessionServiceId is not int serviceId) return HospitalResult.Fail("Choose the service an orthodontic instalment is billed as, in the orthodontics settings");
			if (await _billing.OpenShiftOfAsync(ctx, me, ct) == null) return HospitalResult.Fail("Open your cashier shift before taking a payment");

			var now = HospitalNursingService.UtcToWard(nowUtc);
			var bill = await _billing.BillForWardChargeAsync(ctx, c.PatientId, employeeId, now, ct);
			var charge = await _billing.AddServiceChargeAsync(ctx, bill, serviceId, employeeId, now, ct, 1, amount);
			await _db.SaveChangesAsync(ct);
			if (charge == null) return HospitalResult.Fail("Choose the service an orthodontic instalment is billed as, in the orthodontics settings");
			var receipt = await _billing.CollectAsync(ctx, bill.ID, amount, method!, me, nowUtc, ct);
			if (!receipt.Ok)
			{
				// nothing owed for money that was not taken: exactly this charge leaves the (open) bill
				await _db.HospitalBillLines.Where(l => l.ID == charge.ID && l.CompanyId == ctx.CompanyId).ExecuteDeleteAsync(ct);
				return receipt;
			}
			_db.Add(new HospitalOrthoPayment
			{
				CompanyId = ctx.CompanyId, CaseId = caseId, PaidOn = today.Date, Amount = amount, Reference = Clip(reference, 60), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				Method = method, BillLineId = charge.ID, PaymentId = receipt.Id,
			});
			var r = await SaveAsync(ct);
			return r.Ok ? HospitalResult.Success(caseId) : r;
		}

		// ====================================================================================
		// IMPLANTS
		// ====================================================================================

		public async Task<(IReadOnlyList<HospitalImplantRow> Rows, HospitalImplantStats Stats)> ImplantsAsync(BusinessContext ctx, DateTime today, CancellationToken ct = default)
		{
			var q = from m in Implants(ctx).AsNoTracking() join p in _db.HospitalPatients.AsNoTracking() on m.PatientId equals p.ID
					select new { m, p.Name, p.NameEn, p.Mrn, p.PhotoPath };
			var rows = await q.OrderByDescending(x => x.m.CreatedAt).Take(500).ToListAsync(ct);
			var people = await _dental.DentistsAsync(ctx, ct);
			var list = rows.Select(r => new HospitalImplantRow(r.m, r.Name, r.NameEn, r.Mrn, r.PhotoPath, people.FirstOrDefault(d => d.Id == r.m.SurgeonId))).ToList();
			return (list, new HospitalImplantStats(list.Count(r => r.Implant.PlacedOn?.Year == today.Year), list.Count(r => r.Implant.Stage == HospitalImplantStage.Healing),
				HospitalDentalCasesRules.SurvivalRate(list.Select(r => (r.Implant.PlacedOn, r.Implant.Stage)), today), list.Select(r => r.Implant.Lot).Where(l => l != null).Distinct().Count()));
		}

		public async Task<HospitalResult> PlanImplantAsync(BusinessContext ctx, HospitalImplantInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalPatients.AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (!HospitalDentalRules.IsTooth(input.Tooth) || HospitalDentalRules.IsPrimary(input.Tooth)) return HospitalResult.Fail("An implant replaces a permanent tooth");
			var sys = Clip(input.System, 60);
			if (sys == null) return HospitalResult.Fail("Enter the implant system");
			if (input.Diameter is < 2 or > 8 || input.Length is < 4 or > 20) return HospitalResult.Fail("The size is 2–8 mm wide and 4–20 mm long");
			if (input.SurgeonId is int s && !await _db.HospitalPractitioners.AnyAsync(x => x.ID == s && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (await Implants(ctx).AnyAsync(m => m.PatientId == input.PatientId && m.Tooth == input.Tooth && m.Stage != HospitalImplantStage.Failed, ct))
				return HospitalResult.Fail("This tooth already has an implant in the register");
			var nos = await Implants(ctx).Where(m => m.ImplantNo.StartsWith("IM-")).Select(m => m.ImplantNo).ToListAsync(ct);
			var m = new HospitalImplant
			{
				CompanyId = ctx.CompanyId, ImplantNo = HospitalDentalPracticeRules.NextNo(nos, "IM-", 4), PatientId = input.PatientId, Tooth = input.Tooth, System = sys,
				Diameter = input.Diameter, Length = input.Length, SurgeonId = input.SurgeonId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			_db.Add(m);
			var r = await SaveAsync(ct);
			return r.Ok ? HospitalResult.Success(m.ID) : r;
		}

		private IQueryable<HospitalImplantMeasure> Measures(BusinessContext ctx) => _db.Set<HospitalImplantMeasure>().Where(x => x.CompanyId == ctx.CompanyId);

		/// H168: the clinic's approved loading protocol, or null (none approved yet, or not installed).
		public async Task<HospitalImplantProtocol?> ProtocolAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalImplantProtocol>().AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == ctx.CompanyId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return null; }
		}

		/// H168: the clinical lead approves the protocol (`manage`); each save is a new approval, by the one who saves it.
		public async Task<HospitalResult> SaveProtocolAsync(BusinessContext ctx, HospitalImplantProtocolInput input, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var why = HospitalImplantStabilityRules.RefuseProtocol(input.MinIsqImmediate, input.MinIsqEarly, input.MinIsqConventional, input.MinTorqueImmediate, input.Reference);
			if (why != null) return HospitalResult.Fail(why);
			var p = await _db.Set<HospitalImplantProtocol>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId, ct);
			if (p == null) { p = new HospitalImplantProtocol { CompanyId = ctx.CompanyId }; _db.Add(p); }
			p.MinIsqImmediate = input.MinIsqImmediate; p.MinIsqEarly = input.MinIsqEarly; p.MinIsqConventional = input.MinIsqConventional; p.MinTorqueImmediate = input.MinTorqueImmediate;
			p.Reference = Clip(input.Reference, 300)!; p.ApprovedByEmployeeId = employeeId; p.ApprovedAt = nowUtc;
			var r = await SaveAsync(ct);
			return r.Ok ? HospitalResult.Success(p.ID) : r;
		}

		/// H168: a stability measurement on an implant (not one that failed), with its date and moment.
		public async Task<HospitalResult> AddMeasureAsync(BusinessContext ctx, int implantId, HospitalImplantMeasureInput input, int? employeeId, DateTime nowUtc, DateTime today, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var m = await Implants(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == implantId, ct);
			if (m == null) return HospitalResult.Fail("not_found");
			if (m.Stage is HospitalImplantStage.Planned or HospitalImplantStage.Failed) return HospitalResult.Fail("Stability is measured on a placed implant");
			var why = HospitalImplantStabilityRules.RefuseMeasure(input.Moment, input.Isq, input.TorqueNcm, input.On ?? today, today);
			if (why != null) return HospitalResult.Fail(why);
			if (m.PlacedOn is DateTime placed && (input.On ?? today).Date < placed.Date) return HospitalResult.Fail("A measurement cannot be before the implant was placed");
			var row = new HospitalImplantMeasure { CompanyId = ctx.CompanyId, ImplantId = m.ID, PatientId = m.PatientId, MeasuredOn = (input.On ?? today).Date, Moment = input.Moment!,
				Isq = input.Isq, TorqueNcm = input.TorqueNcm, Note = Clip(input.Note, 300), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
			_db.Add(row);
			var r = await SaveAsync(ct);
			return r.Ok ? HospitalResult.Success(row.ID) : r;
		}

		/// H168: the measurements of these implants, oldest first.
		public async Task<ILookup<int, HospitalImplantMeasure>> MeasuresOfAsync(BusinessContext ctx, IReadOnlyCollection<int> implantIds, CancellationToken ct = default)
		{
			try { return (await Measures(ctx).AsNoTracking().Where(x => implantIds.Contains(x.ImplantId)).OrderBy(x => x.MeasuredOn).ThenBy(x => x.ID).ToListAsync(ct)).ToLookup(x => x.ImplantId); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return Array.Empty<HospitalImplantMeasure>().ToLookup(x => x.ImplantId); }
		}

		// ---------------- H186: peri-implant maintenance and lot traceability ----------------

		private IQueryable<HospitalImplantReview> Reviews(BusinessContext ctx) => _db.Set<HospitalImplantReview>().Where(x => x.CompanyId == ctx.CompanyId);

		public async Task<HospitalResult> AddReviewAsync(BusinessContext ctx, int implantId, HospitalImplantReviewInput input, int? employeeId, DateTime nowUtc, DateTime today, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var m = await Implants(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == implantId, ct);
			if (m == null) return HospitalResult.Fail("not_found");
			var on = (input.On ?? today).Date;
			if (HospitalImplantReviewRules.Refusal(m.Stage, input.MaxProbingMm, input.BoneLossMm, on, today, m.RestoredOn) is string why) return HospitalResult.Fail(why);
			var row = new HospitalImplantReview
			{
				CompanyId = ctx.CompanyId, ImplantId = m.ID, PatientId = m.PatientId, ReviewedOn = on, MaxProbingMm = input.MaxProbingMm, Bleeding = input.Bleeding, Suppuration = input.Suppuration,
				BoneLossMm = input.BoneLossMm, Mobility = input.Mobility, Note = Clip(input.Note, 300), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				Status = HospitalImplantReviewRules.Classify(input.Bleeding, input.Suppuration, input.MaxProbingMm, input.BoneLossMm, input.Mobility),
			};
			_db.Add(row);
			var r = await SaveAsync(ct);
			return r.Ok ? HospitalResult.Success(row.ID) : r;
		}

		/// The reviews of these implants, newest first.
		public async Task<ILookup<int, HospitalImplantReview>> ReviewsOfAsync(BusinessContext ctx, IReadOnlyCollection<int> implantIds, CancellationToken ct = default)
		{
			try { return (await Reviews(ctx).AsNoTracking().Where(x => implantIds.Contains(x.ImplantId)).OrderByDescending(x => x.ReviewedOn).ThenByDescending(x => x.ID).ToListAsync(ct)).ToLookup(x => x.ImplantId); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return Array.Empty<HospitalImplantReview>().ToLookup(x => x.ImplantId); }
		}

		/// Every implant, graft or abutment of this lot, and the patient who received it — the whole register, not a page of it.
		public async Task<IReadOnlyList<HospitalLotTraceRow>> TraceLotAsync(BusinessContext ctx, string? lot, CancellationToken ct = default)
		{
			var key = HospitalImplantReviewRules.LotKey(lot);
			if (key == null) return Array.Empty<HospitalLotTraceRow>();
			var rows = await (from m in Implants(ctx).AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on m.PatientId equals p.ID
							  where (m.Lot != null && m.Lot.ToUpper() == key) || (m.GraftLot != null && m.GraftLot.ToUpper() == key) || (m.AbutmentLot != null && m.AbutmentLot.ToUpper() == key)
							  orderby m.PlacedOn
							  select new { m, p.Name, p.NameEn, p.Mrn, p.Phone }).Take(500).ToListAsync(ct);
			var list = new List<HospitalLotTraceRow>();
			foreach (var r in rows)
			{
				void Add(string what, string? l) { if (l != null && l.Trim().ToUpperInvariant() == key) list.Add(new HospitalLotTraceRow(r.m.ID, r.m.ImplantNo, r.m.PatientId, r.Name, r.NameEn ?? "", r.Mrn, r.Phone, r.m.Tooth, what, l, r.m.PlacedOn, r.m.Stage)); }
				Add("Implant", r.m.Lot); Add("Graft", r.m.GraftLot); Add("Abutment", r.m.AbutmentLot);
			}
			return list;
		}

		public async Task<HospitalResult> MoveImplantAsync(BusinessContext ctx, int id, HospitalImplantMoveInput input, DateTime today, CancellationToken ct = default, int? employeeId = null)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var m = await Implants(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (m == null) return HospitalResult.Fail("not_found");
			if (!HospitalDentalCasesRules.CanMove(m.Stage, input.To)) return HospitalResult.Fail("This implant cannot make that move");
			var day = (input.On ?? today).Date;
			if (day > today.Date) return HospitalResult.Fail("A date cannot be in the future");
			switch (input.To)
			{
				case HospitalImplantStage.Healing:
					var why = HospitalDentalCasesRules.PlaceRefusal(input.Lot, day, input.Isq, today);
					if (why != null) return HospitalResult.Fail(why);
					if (input.TorqueNcm is < 1 or > 150) return HospitalResult.Fail("The insertion torque is 1 to 150 N·cm");
					if (HospitalImplantReviewRules.GraftRefusal(input.GraftMaterial, input.GraftLot) is string gw) return HospitalResult.Fail(gw);
					m.Lot = Clip(input.Lot, 40); m.PlacedOn = day; m.Isq = input.Isq;
					m.Ref = Clip(input.Ref, 60); m.GraftMaterial = Clip(input.GraftMaterial, 60); m.GraftLot = m.GraftMaterial == null ? null : Clip(input.GraftLot, 40);   // H186
					// H168: the stability at placement is a measurement of its own, kept with its date
					if (input.Isq != null || input.TorqueNcm != null)
						_db.Add(new HospitalImplantMeasure { CompanyId = ctx.CompanyId, ImplantId = m.ID, PatientId = m.PatientId, MeasuredOn = day, Moment = HospitalImplantMoment.Placement,
							Isq = input.Isq, TorqueNcm = input.TorqueNcm, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId });
					break;
				case HospitalImplantStage.Restored:
					// H168: loading readiness is the clinic's protocol over the measurement the loading rests on — no fixed number
					var measures = await Measures(ctx).AsNoTracking().Where(x => x.ImplantId == m.ID).ToListAsync(ct);
					var basis = HospitalImplantStabilityRules.Basis(measures, input.LoadingKind, day);
					var protocol = await ProtocolAsync(ctx, ct);
					var no = HospitalImplantStabilityRules.RefuseRestoration(input.LoadingKind, input.Restoration, basis, protocol, input.ExceptionReason);
					if (no != null) return HospitalResult.Fail(no);
					bool below = HospitalImplantStabilityRules.BelowProtocol(protocol, input.LoadingKind, basis!);
					m.Isq = basis!.Isq ?? m.Isq; m.RestoredOn = day; m.LoadingKind = input.LoadingKind; m.Restoration = input.Restoration;
					m.ExceptionReason = below ? Clip(input.ExceptionReason, 300) : null; m.ExceptionByEmployeeId = below ? employeeId : null;
					m.AbutmentLot = Clip(input.AbutmentLot, 40);   // H186
					break;
				case HospitalImplantStage.Failed:
					var reason = Clip(input.Reason, 300);
					if (reason == null) return HospitalResult.Fail("Say why the implant failed");
					m.FailedOn = day; m.FailureReason = reason; break;
			}
			m.Stage = input.To;
			var r = await SaveAsync(ct);
			return r.Ok ? HospitalResult.Success(id) : r;
		}
	}
}
