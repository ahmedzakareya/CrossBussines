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
	}

	public sealed class HospitalOrthoVisitInput
	{
		public DateTime? VisitDate { get; set; }
		public int? AlignerStage { get; set; }
		public DateTime? NextVisit { get; set; }
		public string? Note { get; set; }
		public string? Status { get; set; }
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
	}

	public sealed record HospitalOrthoRow(HospitalOrthoCase Case, string PatientName, string PatientNameEn, string Mrn, string? Photo, string Sex, DateTime? BirthDate,
		int Visits, decimal Paid, decimal Due, int Progress, bool Missed);

	public sealed record HospitalOrthoStats(int Active, int AdjustmentsThisWeek, decimal InstalmentsDue, int Missed);

	public sealed record HospitalImplantRow(HospitalImplant Implant, string PatientName, string PatientNameEn, string Mrn, string? Photo, HospitalDentist? Surgeon);

	public sealed record HospitalImplantStats(int ThisYear, int Healing, decimal? SurvivalRate, int Lots);

	public sealed class HospitalDentalCasesService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalDentalService _dental;

		public HospitalDentalCasesService(CrossDbContext db, IHospitalAccessService access, HospitalDentalService dental) { _db = db; _access = access; _dental = dental; }

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
			var visits = await _db.Set<HospitalOrthoVisit>().AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId && ids.Contains(v.CaseId)).Select(v => new { v.CaseId, v.VisitDate }).ToListAsync(ct);
			var paid = await _db.Set<HospitalOrthoPayment>().AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId && ids.Contains(v.CaseId)).GroupBy(v => v.CaseId).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) }).ToListAsync(ct);
			var list = rows.Select(r =>
			{
				var c = r.c; decimal p = paid.FirstOrDefault(x => x.Key == c.ID)?.Sum ?? 0;
				bool open = HospitalOrthoStatus.Open(c.Status);
				return new HospitalOrthoRow(c, r.Name, r.NameEn, r.Mrn, r.PhotoPath, r.Sex, r.BirthDate, visits.Count(v => v.CaseId == c.ID), p,
					open ? HospitalDentalCasesRules.Due(c.ContractTotal, c.MonthlyInstalment, c.StartDate, today, p) : Math.Max(0, c.ContractTotal - p),
					c.Status == HospitalOrthoStatus.Completed ? 100 : HospitalDentalCasesRules.Progress(c.StartDate, c.PlannedMonths, today),
					HospitalDentalCasesRules.Missed(c.Status, c.NextVisit, today));
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
			if (await Cases(ctx).AnyAsync(c => c.PatientId == input.PatientId && (c.Status == HospitalOrthoStatus.Active || c.Status == HospitalOrthoStatus.Retention), ct))
				return HospitalResult.Fail("This patient already has an open orthodontic case");
			var nos = await Cases(ctx).Where(c => c.CaseNo.StartsWith("OR-")).Select(c => c.CaseNo).ToListAsync(ct);
			var c = new HospitalOrthoCase
			{
				CompanyId = ctx.CompanyId, CaseNo = HospitalDentalPracticeRules.NextNo(nos, "OR-", 4), PatientId = input.PatientId, PractitionerId = input.PractitionerId,
				Appliance = input.Appliance, StartDate = input.StartDate.Date, PlannedMonths = input.PlannedMonths, Stages = input.Stages, CurrentStage = input.Stages == null ? null : 1,
				ContractTotal = input.ContractTotal, MonthlyInstalment = input.MonthlyInstalment, NextVisit = input.NextVisit?.Date,
				Status = input.Appliance == "Retention" ? HospitalOrthoStatus.Retention : HospitalOrthoStatus.Active, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
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
			_db.Add(new HospitalOrthoVisit { CompanyId = ctx.CompanyId, CaseId = caseId, VisitDate = day, AlignerStage = input.AlignerStage, Note = Clip(input.Note, 300), PractitionerId = practitionerId, CreatedAt = nowUtc });
			if (input.AlignerStage != null) c.CurrentStage = input.AlignerStage;
			c.NextVisit = input.NextVisit?.Date;
			if (input.Status != null) c.Status = input.Status;
			var r = await SaveAsync(ct);
			return r.Ok ? HospitalResult.Success(caseId) : r;
		}

		public async Task<HospitalResult> RecordPaymentAsync(BusinessContext ctx, int caseId, decimal amount, string? reference, int? employeeId, DateTime nowUtc, DateTime today, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct) && !await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == caseId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			if (amount <= 0) return HospitalResult.Fail("A payment is more than zero");
			decimal paid = await _db.Set<HospitalOrthoPayment>().Where(p => p.CompanyId == ctx.CompanyId && p.CaseId == caseId).SumAsync(p => (decimal?)p.Amount, ct) ?? 0;
			if (paid + amount > c.ContractTotal) return HospitalResult.Fail("This is more than what is left on the contract");
			_db.Add(new HospitalOrthoPayment { CompanyId = ctx.CompanyId, CaseId = caseId, PaidOn = today.Date, Amount = amount, Reference = Clip(reference, 60), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId });
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

		public async Task<HospitalResult> MoveImplantAsync(BusinessContext ctx, int id, HospitalImplantMoveInput input, DateTime today, CancellationToken ct = default)
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
					m.Lot = Clip(input.Lot, 40); m.PlacedOn = day; m.Isq = input.Isq; break;
				case HospitalImplantStage.Restored:
					if (input.Isq is < 1 or > 100) return HospitalResult.Fail("ISQ is 1 to 100");
					var isq = input.Isq ?? m.Isq;
					var no = HospitalDentalCasesRules.RestoreRefusal(isq);
					if (no != null) return HospitalResult.Fail(no, HospitalDentalCasesRules.IsqLoadable);
					m.Isq = isq; m.RestoredOn = day; break;
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
