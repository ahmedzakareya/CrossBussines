using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H15 — dentistry: the chart, the periodontal chart and the treatment plan.
	//
	//   · The chart (`read`; marks are `consult`): conditions, existing work and the tooth's states are recorded
	//     on the chart; a mark made in error is voided, never deleted. Planned work is never marked by hand: it is a
	//     line of a treatment plan, and its mark completes with the line.
	//   · The periodontal chart (`consult`): an exam is saved whole, one row per tooth; the chart shows the latest.
	//   · The plan (`consult`): phases of lines, each line a procedure on a tooth and its surfaces at the fee the
	//     patient's price list quotes when it is added. A phase is decided on the patient's side (`consult` or
	//     `bill`) with who signed; accepting it files the pre-authorisations its lines need through H11.
	//     Only accepted work is done; only work not yet accepted is cancelled.
	//   · The quotation is HospitalDentalRules.Quote over the payer's dental benefit (setup, `manage`).
	//   · Nothing is billed or posted here, and no job runs. The entities reach the model through
	//     HospitalDentalModel.Configure (wired in CrossDbContext).
	// ==========================================================================================

	public sealed class HospitalDentalFindingInput
	{
		public int PatientId { get; set; }
		public int Tooth { get; set; }
		public string? Surfaces { get; set; }
		public string? Work { get; set; }
		public string Status { get; set; } = HospitalDentalMark.Condition;
		public string? Note { get; set; }
		/// H33: the note's English twin.
		public string? NoteEn { get; set; }
	}

	public sealed class HospitalPerioToothInput
	{
		public int Tooth { get; set; }
		public string Depths { get; set; } = "";
		public string Recession { get; set; } = "";
		public string Bleeding { get; set; } = "000000";
		public int? Mobility { get; set; }
		public int? Furcation { get; set; }
	}

	public sealed class HospitalPerioExamInput
	{
		public int PatientId { get; set; }
		public DateTime? ExamDate { get; set; }
		public int? PractitionerId { get; set; }
		public List<HospitalPerioToothInput> Teeth { get; set; } = new();
	}

	public sealed class HospitalDentalPlanInput
	{
		public int PatientId { get; set; }
		public int? PractitionerId { get; set; }
		public string Title { get; set; } = "";
		public string TitleEn { get; set; } = "";
	}

	public sealed class HospitalDentalLineInput
	{
		public int PhaseId { get; set; }
		public int ServiceId { get; set; }
		public int? Tooth { get; set; }
		public string? Surfaces { get; set; }
		public int? ProviderId { get; set; }
	}

	public sealed class HospitalDentalProcedureInput
	{
		public int ServiceId { get; set; }
		public string Category { get; set; } = HospitalDentalCategory.Restorative;
		public int? Surfaces { get; set; }
		public string? ToothWork { get; set; }
		public bool NeedsTooth { get; set; }
		public bool NeedsPreauth { get; set; }
		public bool NeedsLab { get; set; }
		public bool NotCovered { get; set; }
		public int Minutes { get; set; } = 30;
	}

	public sealed class HospitalDentalBenefitInput
	{
		public int PayerId { get; set; }
		public decimal YearlyLimit { get; set; }
		public Dictionary<string, decimal> Cover { get; set; } = new();
	}

	public sealed record HospitalDentist(int Id, string Name, string NameEn, string? Photo);

	public sealed record HospitalDentalProcedureRow(HospitalDentalProcedure Procedure, string Code, string Name, string NameEn, decimal Price);

	public sealed record HospitalDentalMarkRow(int Id, int Tooth, string? Surfaces, string? Work, string Status, bool FromPlan, string? Note, DateTime At);

	public sealed record HospitalDentalHistoryRow(DateTime At, int? Tooth, string? Surfaces, string What, string WhatEn, string? Note, HospitalDentist? By, bool Elsewhere);

	public sealed record HospitalDentalImageRow(int Id, string AccessionNo, string Name, string NameEn, string Modality, string Status, DateTime At);

	public sealed record HospitalPerioView(HospitalPerioExam Exam, IReadOnlyList<HospitalPerioTooth> Teeth, HospitalPerioStats Stats, HospitalDentist? Examiner);

	public sealed record HospitalDentalOpenPlan(int Id, string PlanNo, IReadOnlyList<(int Id, int Seq, string Title, string TitleEn)> PendingPhases);

	public sealed record HospitalDentalChartView(HospitalPatient Patient, string? Payer, string? PayerEn, string Dentition, bool DentitionChosen,
		Dictionary<int, HospitalToothChart> Teeth, IReadOnlyList<HospitalDentalMarkRow> Marks, HospitalPerioView? Perio,
		IReadOnlyList<HospitalDentalHistoryRow> History, IReadOnlyList<HospitalDentalImageRow> Images, IReadOnlyList<HospitalDentalOpenPlan> OpenPlans, int? LatestPlanId);

	public sealed record HospitalDentalPlanRow(HospitalDentalPlan Plan, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		HospitalDentist? Dentist, int Phases, string State, HospitalDentalQuote Quote, string? Currency, int Decimals);

	public sealed record HospitalDentalLineRow(HospitalDentalPlanLine Line, HospitalDentalProcedureRow Procedure, HospitalDentist? By, HospitalDentalQuoteLine? Quote);

	public sealed record HospitalDentalPhaseRow(HospitalDentalPlanPhase Phase, IReadOnlyList<HospitalDentalLineRow> Lines);

	public sealed record HospitalDentalPlanView(HospitalDentalPlanRow Row, IReadOnlyList<HospitalDentalPhaseRow> Phases, decimal? YearlyLimit, decimal UsedThisYear, string? Payer, string? PayerEn);

	public sealed record HospitalDentalPlanStats(int PresentedThisMonth, decimal? AcceptanceRate, decimal AcceptedNotDone, decimal? YearlyLimitMin, decimal? YearlyLimitMax, string? Currency, int Decimals);

	public sealed class HospitalDentalService
	{
		private const int DefaultDecimals = 3;
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalInsuranceService _insurance;
		private readonly HospitalPricingService _pricing;

		private readonly HospitalBillingService? _billing;   // H53: completed work goes on the patient's bill
		public HospitalDentalService(CrossDbContext db, IHospitalAccessService access, HospitalInsuranceService insurance, HospitalPricingService pricing, HospitalBillingService? billing = null)
		{ _db = db; _access = access; _insurance = insurance; _pricing = pricing; _billing = billing; }

		public const string ConcurrentChange = HospitalInsuranceService.ConcurrentChange;
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		private IQueryable<HospitalDentalProcedure> Procedures(BusinessContext ctx) => _db.Set<HospitalDentalProcedure>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDentalFinding> Findings(BusinessContext ctx) => _db.Set<HospitalDentalFinding>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDentalPlan> Plans(BusinessContext ctx) => _db.Set<HospitalDentalPlan>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDentalPlanPhase> Phases(BusinessContext ctx) => _db.Set<HospitalDentalPlanPhase>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDentalPlanLine> Lines(BusinessContext ctx) => _db.Set<HospitalDentalPlanLine>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDentalBenefit> Benefits(BusinessContext ctx) => _db.Set<HospitalDentalBenefit>().Where(x => x.CompanyId == ctx.CompanyId);
		private Task<bool> PatientExists(BusinessContext ctx, int id, CancellationToken ct) => _db.HospitalPatients.AnyAsync(p => p.ID == id && p.CompanyId == ctx.CompanyId, ct);

		private async Task<HospitalResult> SaveAsync(CancellationToken ct, int? id = null)
		{
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(ConcurrentChange); }
			return HospitalResult.Success(id);
		}

		// ====================================================================================
		// PEOPLE AND PROCEDURES
		// ====================================================================================

		/// The facility's practitioners, each with a photo (a person is chosen by face).
		public async Task<IReadOnlyList<HospitalDentist>> DentistsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var rows = await _db.HospitalPractitioners.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.Name)
				.Select(d => new { d.ID, d.Name, d.NameEn, d.EmployeeId }).ToListAsync(ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, rows.Where(d => d.EmployeeId != null).Select(d => d.EmployeeId!.Value), ct);
			return rows.Select(d => new HospitalDentist(d.ID, d.Name, d.NameEn ?? d.Name, d.EmployeeId is int e ? photos.GetValueOrDefault(e) : null)).ToList();
		}

		public async Task<IReadOnlyList<HospitalDentalProcedureRow>> ProceduresAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var q = from p in Procedures(ctx).AsNoTracking()
					join s in _db.HospitalServices.AsNoTracking() on p.ServiceId equals s.ID
					where s.CompanyId == ctx.CompanyId && s.IsActive
					orderby s.SortOrder, s.Code
					select new { p, s.Code, s.Name, s.NameEn, s.Price };
			return (await q.ToListAsync(ct)).Select(x => new HospitalDentalProcedureRow(x.p, x.Code, x.Name, x.NameEn, x.Price)).ToList();
		}

		public async Task<HospitalDentalProcedure?> ProcedureOfAsync(BusinessContext ctx, int serviceId, CancellationToken ct = default) =>
			await Procedures(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ServiceId == serviceId, ct);

		/// The dental profile of a catalogue service (setup). `remove` takes the profile off; the service stays.
		public async Task<HospitalResult> SaveProcedureAsync(BusinessContext ctx, HospitalDentalProcedureInput input, bool remove, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.HospitalServices.AnyAsync(s => s.ID == input.ServiceId && s.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var p = await Procedures(ctx).FirstOrDefaultAsync(x => x.ServiceId == input.ServiceId, ct);
			if (remove)
			{
				if (p == null) return HospitalResult.Success();
				if (await Lines(ctx).AnyAsync(l => l.ServiceId == input.ServiceId, ct) || await Findings(ctx).AnyAsync(f => f.ServiceId == input.ServiceId, ct))
					return HospitalResult.Fail("Treatment plans use this procedure; it cannot stop being a dental procedure");
				_db.Remove(p);
				return await SaveAsync(ct);
			}
			if (!HospitalDentalCategory.All.Contains(input.Category)) return HospitalResult.Fail("Choose the dental category");
			if (input.Surfaces is < 1 or > 5) return HospitalResult.Fail("A surface code bills 1 to 5 surfaces");
			if (input.ToothWork != null && !HospitalToothWork.Procedures.Contains(input.ToothWork)) return HospitalResult.Fail("Choose the work it does on the tooth");
			if (input.Surfaces != null && input.ToothWork != null) return HospitalResult.Fail("A procedure works on surfaces or on the whole tooth, not both");
			if (input.Minutes is < 5 or > 480) return HospitalResult.Fail("The chair time is 5 to 480 minutes");
			if (p == null) { p = new HospitalDentalProcedure { CompanyId = ctx.CompanyId, ServiceId = input.ServiceId }; _db.Add(p); }
			p.Category = input.Category; p.Surfaces = input.Surfaces; p.ToothWork = input.ToothWork; p.NeedsTooth = input.NeedsTooth || input.Surfaces != null || input.ToothWork != null;
			p.NeedsPreauth = input.NeedsPreauth; p.NeedsLab = input.NeedsLab; p.NotCovered = input.NotCovered; p.Minutes = input.Minutes;
			{ var saved = await SaveAsync(ct); return saved.Ok ? HospitalResult.Success(p.ID) : saved; }   // the id exists only after the save
		}

		public async Task<HospitalDentalBenefit?> BenefitOfAsync(BusinessContext ctx, int payerId, CancellationToken ct = default) =>
			await Benefits(ctx).AsNoTracking().FirstOrDefaultAsync(b => b.PayerId == payerId, ct);

		public async Task<IReadOnlyList<HospitalDentalBenefit>> BenefitsAsync(BusinessContext ctx, CancellationToken ct = default) =>
			await Benefits(ctx).AsNoTracking().ToListAsync(ct);

		public async Task<HospitalResult> SaveBenefitAsync(BusinessContext ctx, HospitalDentalBenefitInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (!await _db.Set<HospitalPayer>().AnyAsync(p => p.ID == input.PayerId && p.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (input.YearlyLimit < 0) return HospitalResult.Fail("The yearly dental limit cannot be negative");
			if (input.Cover.Values.Any(v => v is < 0 or > 100)) return HospitalResult.Fail("A cover is 0 to 100%");
			decimal C(string k) => input.Cover.TryGetValue(k, out var v) ? v : 0;
			var b = await Benefits(ctx).FirstOrDefaultAsync(x => x.PayerId == input.PayerId, ct);
			if (b == null) { b = new HospitalDentalBenefit { CompanyId = ctx.CompanyId, PayerId = input.PayerId }; _db.Add(b); }
			b.YearlyLimit = input.YearlyLimit;
			b.DiagnosticPct = C(HospitalDentalCategory.Diagnostic); b.PreventivePct = C(HospitalDentalCategory.Preventive); b.RestorativePct = C(HospitalDentalCategory.Restorative);
			b.EndodonticPct = C(HospitalDentalCategory.Endodontic); b.ProstheticPct = C(HospitalDentalCategory.Prosthetic); b.SurgeryPct = C(HospitalDentalCategory.Surgery);
			b.ImplantPct = C(HospitalDentalCategory.Implant); b.CosmeticPct = C(HospitalDentalCategory.Cosmetic);
			b.UpdatedAt = nowUtc; b.UpdatedByEmployeeId = employeeId;
			{ var saved = await SaveAsync(ct); return saved.Ok ? HospitalResult.Success(b.ID) : saved; }   // the id exists only after the save
		}

		// ====================================================================================
		// THE CHART
		// ====================================================================================

		public async Task<HospitalDentalChartView?> ChartAsync(BusinessContext ctx, int patientId, DateTime nowUtc, CancellationToken ct = default)
		{
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == patientId && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return null;
			var header = await _db.Set<HospitalDentalChart>().AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == ctx.CompanyId && c.PatientId == patientId, ct);
			var marks = await Findings(ctx).AsNoTracking().Where(f => f.PatientId == patientId && f.VoidedAt == null).ToListAsync(ct);
			var procs = await ProceduresAsync(ctx, ct);
			var plans = await Plans(ctx).AsNoTracking().Where(p => p.PatientId == patientId).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
			var planIds = plans.Select(p => p.ID).ToList();
			var phases = await Phases(ctx).AsNoTracking().Where(p => planIds.Contains(p.PlanId)).ToListAsync(ct);
			var lines = await Lines(ctx).AsNoTracking().Where(l => planIds.Contains(l.PlanId)).ToListAsync(ct);
			var declined = phases.Where(p => p.Decision == HospitalDentalDecision.Declined).Select(p => p.ID).ToHashSet();
			string CodeOf(int serviceId) => procs.FirstOrDefault(p => p.Procedure.ServiceId == serviceId)?.Code ?? "";
			// a tooth's planned work, as "code|line id" (the page links each to its line)
			var planned = lines.Where(l => l.Tooth != null && l.Status == HospitalDentalLineStatus.Planned && !declined.Contains(l.PhaseId)).Select(l => (l.Tooth!.Value, CodeOf(l.ServiceId) + "|" + l.ID)).ToList();
			var teeth = HospitalDentalRules.Chart(marks, planned);
			var dentists = await DentistsAsync(ctx, ct);
			HospitalDentist? Who(int? id) => id is int i ? dentists.FirstOrDefault(d => d.Id == i) : null;

			// history: what was done here (plan lines) and what was found already done elsewhere
			var history = new List<HospitalDentalHistoryRow>();
			foreach (var l in lines.Where(l => l.Status == HospitalDentalLineStatus.Completed))
			{
				var pr = procs.FirstOrDefault(p => p.Procedure.ServiceId == l.ServiceId);
				var plan = plans.First(p => p.ID == l.PlanId);
				history.Add(new(l.CompletedAt ?? l.CreatedAt, l.Tooth, l.Surfaces, pr?.Name ?? "", pr?.NameEn ?? "", null, Who(l.ProviderId ?? plan.PractitionerId), false));
			}
			foreach (var f in marks.Where(f => f.PlanLineId == null && f.Status is HospitalDentalMark.Existing or HospitalDentalMark.Completed && !HospitalToothWork.IsState(f.Work)))
				history.Add(new(f.RecordedAt, f.Tooth, f.Surfaces, f.Work ?? "", f.Work ?? "", f.Note, Who(f.PractitionerId), f.Status == HospitalDentalMark.Existing));

			var exam = await _db.Set<HospitalPerioExam>().AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId && e.PatientId == patientId)
				.OrderByDescending(e => e.ExamDate).ThenByDescending(e => e.ID).FirstOrDefaultAsync(ct);
			HospitalPerioView? perio = null;
			if (exam != null)
			{
				var pt = await _db.Set<HospitalPerioTooth>().AsNoTracking().Where(t => t.ExamId == exam.ID && t.CompanyId == ctx.CompanyId).ToListAsync(ct);
				perio = new HospitalPerioView(exam, pt, HospitalDentalRules.PerioStats(pt), Who(exam.PractitionerId));
			}

			var images = await _db.HospitalRadOrders.AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.PatientId == patientId)
				.OrderByDescending(o => o.OrderedAt).Take(12)
				.Select(o => new HospitalDentalImageRow(o.ID, o.AccessionNo, o.Name, o.NameEn, o.Modality, o.Status, o.OrderedAt)).ToListAsync(ct);

			var (_, payer, why) = await _insurance.EligibilityAsync(ctx, patientId, nowUtc, null, ct);
			var open = plans.Select(p => new HospitalDentalOpenPlan(p.ID, p.PlanNo, phases.Where(h => h.PlanId == p.ID && h.Decision == HospitalDentalDecision.Pending)
				.OrderBy(h => h.Seq).Select(h => (h.ID, h.Seq, h.Title, h.TitleEn)).ToList())).Where(p => p.PendingPhases.Count > 0).ToList();
			var markRows = marks.Select(f => new HospitalDentalMarkRow(f.ID, f.Tooth, f.Surfaces, f.Work, f.Status, f.PlanLineId != null, f.Note == null && f.NoteEn == null ? null : DisplayName.Of(f.Note ?? "", f.NoteEn ?? ""), f.CompletedAt ?? f.RecordedAt)).ToList();
			return new HospitalDentalChartView(patient, why == null ? payer?.Name : null, why == null ? payer?.NameEn : null,
				header?.Dentition ?? HospitalDentalRules.DentitionFor(patient.BirthDate, nowUtc.Date), header?.Dentition != null,
				teeth, markRows, perio, history.OrderByDescending(h => h.At).ToList(), images, open, plans.FirstOrDefault()?.ID);
		}

		public async Task<HospitalResult> SetDentitionAsync(BusinessContext ctx, int patientId, string? dentition, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!await PatientExists(ctx, patientId, ct)) return HospitalResult.Fail("not_found");
			if (dentition != null && !HospitalDentition.All.Contains(dentition)) return HospitalResult.Fail("Choose the dentition");
			var c = await _db.Set<HospitalDentalChart>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.PatientId == patientId, ct);
			if (c == null) { c = new HospitalDentalChart { CompanyId = ctx.CompanyId, PatientId = patientId }; _db.Add(c); }
			c.Dentition = dentition; c.UpdatedAt = nowUtc;
			return await SaveAsync(ct);
		}

		/// A condition, existing work or a state of the tooth. Planned work is a plan line, never a hand mark.
		public async Task<HospitalResult> RecordFindingAsync(BusinessContext ctx, HospitalDentalFindingInput input, int? practitionerId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!await PatientExists(ctx, input.PatientId, ct)) return HospitalResult.Fail("not_found");
			if (!HospitalDentalRules.IsTooth(input.Tooth)) return HospitalResult.Fail("Choose the tooth");
			string? surfaces = null, work = null, status = input.Status;
			if (HospitalToothWork.IsState(input.Work)) { work = input.Work; status = HospitalDentalMark.Existing; }
			else
			{
				if (status is not (HospitalDentalMark.Condition or HospitalDentalMark.Existing)) return HospitalResult.Fail("Planned work is added to a treatment plan");
				if (!string.IsNullOrWhiteSpace(input.Surfaces))
				{
					surfaces = HospitalDentalRules.NormalizeSurfaces(input.Tooth, input.Surfaces);
					if (surfaces == null) return HospitalResult.Fail("Choose the surfaces on the tooth");
					if (input.Work != null) return HospitalResult.Fail("A mark is on surfaces or on the whole tooth, not both");
				}
				else if (input.Work != null && HospitalToothWork.Procedures.Contains(input.Work)) work = input.Work;
				else return HospitalResult.Fail("Choose the surfaces or the work on the tooth");
				if (work == HospitalToothWork.Implant && HospitalDentalRules.IsPrimary(input.Tooth)) return HospitalResult.Fail("An implant replaces a permanent tooth");
			}
			_db.Add(new HospitalDentalFinding
			{
				CompanyId = ctx.CompanyId, PatientId = input.PatientId, Tooth = input.Tooth, Surfaces = surfaces, Work = work, Status = status,
				PractitionerId = status == HospitalDentalMark.Existing ? null : practitionerId, Note = Clip(input.Note, 300), NoteEn = Clip(input.NoteEn, 300), RecordedAt = nowUtc, RecordedByEmployeeId = employeeId,
			});
			return await SaveAsync(ct);
		}

		/// A mark made in error. A plan's mark goes with its line (cancelled on the plan), never from here.
		public async Task<HospitalResult> VoidFindingAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var f = await Findings(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (f == null) return HospitalResult.Fail("not_found");
			if (f.PlanLineId != null) return HospitalResult.Fail("This mark belongs to a treatment plan; change it on the plan");
			int n = await Findings(ctx).Where(x => x.ID == id && x.VoidedAt == null && x.PlanLineId == null)
				.ExecuteUpdateAsync(u => u.SetProperty(x => x.VoidedAt, nowUtc).SetProperty(x => x.VoidedByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(id) : HospitalResult.Fail(ConcurrentChange);
		}

		// ====================================================================================
		// THE PERIODONTAL CHART
		// ====================================================================================

		public async Task<HospitalResult> SavePerioExamAsync(BusinessContext ctx, HospitalPerioExamInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!await PatientExists(ctx, input.PatientId, ct)) return HospitalResult.Fail("not_found");
			if (input.PractitionerId is int pid && !await _db.HospitalPractitioners.AnyAsync(d => d.ID == pid && d.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var day = (input.ExamDate ?? nowUtc).Date;
			if (day > nowUtc.Date) return HospitalResult.Fail("An exam cannot be dated in the future");
			if (input.Teeth.Count == 0) return HospitalResult.Fail("Chart at least one tooth");
			if (input.Teeth.Select(t => t.Tooth).Distinct().Count() != input.Teeth.Count) return HospitalResult.Fail("A tooth is charted twice");
			foreach (var t in input.Teeth)
			{
				var why = HospitalDentalRules.PerioRefusal(t.Tooth, t.Depths, t.Recession, t.Bleeding, t.Mobility, t.Furcation);
				if (why != null) return HospitalResult.Fail(why, t.Tooth);
			}
			var exam = new HospitalPerioExam { CompanyId = ctx.CompanyId, PatientId = input.PatientId, ExamDate = day, PractitionerId = input.PractitionerId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
			_db.Add(exam);
			await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
			var r = await SaveAsync(ct);
			if (!r.Ok) return r;
			foreach (var t in input.Teeth)
				_db.Add(new HospitalPerioTooth
				{
					CompanyId = ctx.CompanyId, ExamId = exam.ID, Tooth = t.Tooth,
					Depths = string.Join(",", HospitalDentalRules.Sites(t.Depths, 0, 15)!), Recession = string.Join(",", HospitalDentalRules.Sites(t.Recession, -5, 15)!),
					Bleeding = t.Bleeding, Mobility = t.Mobility, Furcation = t.Furcation,
				});
			r = await SaveAsync(ct, exam.ID);
			if (r.Ok && tx != null) await tx.CommitAsync(ct);
			return r;
		}

		// ====================================================================================
		// TREATMENT PLANS
		// ====================================================================================

		public async Task<HospitalResult> CreatePlanAsync(BusinessContext ctx, HospitalDentalPlanInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!await PatientExists(ctx, input.PatientId, ct)) return HospitalResult.Fail("not_found");
			if (input.PractitionerId is int pid && !await _db.HospitalPractitioners.AnyAsync(d => d.ID == pid && d.CompanyId == ctx.CompanyId && d.IsActive, ct)) return HospitalResult.Fail("not_found");
			if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.TitleEn)) return HospitalResult.Fail("Name the first phase in both languages");
			var list = await _pricing.ListForPatientAsync(ctx, input.PatientId, nowUtc, ct);
			var nos = await Plans(ctx).Where(p => p.PlanNo.StartsWith("TP-" + nowUtc.Year + "-")).Select(p => p.PlanNo).ToListAsync(ct);
			var plan = new HospitalDentalPlan
			{
				CompanyId = ctx.CompanyId, PlanNo = HospitalDentalRules.NextPlanNo(nos, nowUtc.Year), PatientId = input.PatientId, PractitionerId = input.PractitionerId,
				CurrencyId = list?.CurrencyId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
			};
			_db.Add(plan);
			await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
			var r = await SaveAsync(ct);
			if (!r.Ok) return r;
			_db.Add(new HospitalDentalPlanPhase { CompanyId = ctx.CompanyId, PlanId = plan.ID, Seq = 1, Title = input.Title.Trim(), TitleEn = input.TitleEn.Trim() });
			r = await SaveAsync(ct, plan.ID);
			if (r.Ok && tx != null) await tx.CommitAsync(ct);
			return r;
		}

		public async Task<HospitalResult> AddPhaseAsync(BusinessContext ctx, int planId, string title, string titleEn, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			if (!await Plans(ctx).AnyAsync(p => p.ID == planId, ct)) return HospitalResult.Fail("not_found");
			if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(titleEn)) return HospitalResult.Fail("Name the phase in both languages");
			int seq = (await Phases(ctx).Where(p => p.PlanId == planId).Select(p => (int?)p.Seq).MaxAsync(ct) ?? 0) + 1;
			if (seq > 20) return HospitalResult.Fail("A plan has at most 20 phases");
			_db.Add(new HospitalDentalPlanPhase { CompanyId = ctx.CompanyId, PlanId = planId, Seq = seq, Title = title.Trim(), TitleEn = titleEn.Trim() });
			return await SaveAsync(ct, planId);
		}

		/// A procedure on a tooth into a phase the patient has not decided yet, at today's fee from the patient's list.
		public async Task<HospitalResult> AddLineAsync(BusinessContext ctx, HospitalDentalLineInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var phase = await Phases(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ID == input.PhaseId, ct);
			if (phase == null) return HospitalResult.Fail("not_found");
			if (phase.Decision != HospitalDentalDecision.Pending) return HospitalResult.Fail("The patient has decided this phase; add the work to a new phase");
			var plan = await Plans(ctx).AsNoTracking().FirstAsync(p => p.ID == phase.PlanId, ct);
			var proc = await Procedures(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ServiceId == input.ServiceId, ct);
			var service = await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.ID == input.ServiceId && s.CompanyId == ctx.CompanyId && s.IsActive, ct);
			if (proc == null || service == null) return HospitalResult.Fail("not_found");
			if (input.ProviderId is int prov && !await _db.HospitalPractitioners.AnyAsync(d => d.ID == prov && d.CompanyId == ctx.CompanyId && d.IsActive, ct)) return HospitalResult.Fail("not_found");
			var why = HospitalDentalRules.PlacementRefusal(proc, input.Tooth, input.Surfaces);
			if (why != null) return HospitalResult.Fail(why, proc.Surfaces ?? 0);
			int? tooth = proc.NeedsTooth || proc.Surfaces != null || proc.ToothWork != null ? input.Tooth : null;
			string? surfaces = proc.Surfaces != null ? HospitalDentalRules.NormalizeSurfaces(tooth!.Value, input.Surfaces) : null;
			// several restorations on one tooth bill as one multi-surface code, never one line per surface
			if (surfaces != null)
			{
				var others = await (from l in Lines(ctx) join p in Procedures(ctx) on l.ServiceId equals p.ServiceId
									where l.PlanId == plan.ID && l.Tooth == tooth && l.Status == HospitalDentalLineStatus.Planned && p.Surfaces != null && p.Category == proc.Category
									select l.ID).AnyAsync(ct);
				if (others) return HospitalResult.Fail("This tooth already has a filling in the plan; use one code for all its surfaces");
			}
			var list = await _pricing.ListForPatientAsync(ctx, plan.PatientId, nowUtc, ct);
			if (list?.CurrencyId != plan.CurrencyId) return HospitalResult.Fail("The patient's price list is now in another currency; open a new plan");
			var fee = (await _pricing.QuoteServiceAsync(ctx, list?.ID, service, ct)).Price;
			var line = new HospitalDentalPlanLine
			{
				CompanyId = ctx.CompanyId, PlanId = plan.ID, PhaseId = phase.ID, Tooth = tooth, Surfaces = surfaces, ServiceId = service.ID,
				ProviderId = input.ProviderId, Fee = fee, CreatedAt = nowUtc,
			};
			_db.Add(line);
			await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
			var r = await SaveAsync(ct);
			if (!r.Ok) return r;
			// the plan's mark on the chart (a line with no tooth, or on a tooth with nothing to draw, has none)
			string? work = proc.ToothWork;
			if (tooth != null && (surfaces != null || work != null))
			{
				_db.Add(new HospitalDentalFinding
				{
					CompanyId = ctx.CompanyId, PatientId = plan.PatientId, Tooth = tooth.Value, Surfaces = surfaces, Work = surfaces == null ? work : null,
					Status = HospitalDentalMark.Planned, ServiceId = service.ID, PlanLineId = line.ID, PractitionerId = input.ProviderId ?? plan.PractitionerId,
					RecordedAt = nowUtc, RecordedByEmployeeId = employeeId,
				});
				r = await SaveAsync(ct, line.ID);
				if (!r.Ok) return r;
			}
			if (tx != null) await tx.CommitAsync(ct);
			return HospitalResult.Success(line.ID);
		}

		/// The patient's decision on a phase, with who signed. Accepting files the approvals its lines need with the
		/// patient's payer (H11); declining takes its marks off the chart.
		public async Task<HospitalResult> DecidePhaseAsync(BusinessContext ctx, int phaseId, bool accept, string? signatory, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct) && !await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var phase = await Phases(ctx).AsNoTracking().FirstOrDefaultAsync(p => p.ID == phaseId, ct);
			if (phase == null) return HospitalResult.Fail("not_found");
			if (phase.Decision != HospitalDentalDecision.Pending) return HospitalResult.Fail("The patient has already decided this phase");
			var who = Clip(signatory, 150);
			if (who == null) return HospitalResult.Fail("Enter who signed for the patient");
			var lines = await Lines(ctx).AsNoTracking().Where(l => l.PhaseId == phaseId && l.Status == HospitalDentalLineStatus.Planned).ToListAsync(ct);
			if (accept && lines.Count == 0) return HospitalResult.Fail("This phase has no work to accept");
			string decision = accept ? HospitalDentalDecision.Accepted : HospitalDentalDecision.Declined;
			int n = await Phases(ctx).Where(p => p.ID == phaseId && p.Decision == HospitalDentalDecision.Pending).ExecuteUpdateAsync(u => u
				.SetProperty(p => p.Decision, decision).SetProperty(p => p.Signatory, who).SetProperty(p => p.DecidedAt, nowUtc).SetProperty(p => p.DecidedByEmployeeId, employeeId), ct);
			if (n != 1) return HospitalResult.Fail(ConcurrentChange);
			var ids = lines.Select(l => l.ID).ToList();
			if (!accept)
			{
				await Findings(ctx).Where(f => f.PlanLineId != null && ids.Contains(f.PlanLineId.Value) && f.VoidedAt == null)
					.ExecuteUpdateAsync(u => u.SetProperty(f => f.VoidedAt, nowUtc).SetProperty(f => f.VoidedByEmployeeId, employeeId), ct);
				return HospitalResult.Success(phase.PlanId);
			}
			// the approvals: one request per procedure that needs one, when the patient is insured today
			var plan = await Plans(ctx).AsNoTracking().FirstAsync(p => p.ID == phase.PlanId, ct);
			var (_, payer, why) = await _insurance.EligibilityAsync(ctx, plan.PatientId, nowUtc, null, ct);
			if (why == null && payer != null)
			{
				var needs = await Procedures(ctx).AsNoTracking().Where(p => p.NeedsPreauth && !p.NotCovered).Select(p => p.ServiceId).ToListAsync(ct);
				foreach (var l in lines.Where(l => needs.Contains(l.ServiceId)))
				{
					var r = await _insurance.RequestPreauthAsync(ctx, plan.PatientId, l.ServiceId, l.Fee, employeeId, nowUtc, ct);
					if (r.Ok && r.Id is int pa) await Lines(ctx).Where(x => x.ID == l.ID).ExecuteUpdateAsync(u => u.SetProperty(x => x.PreauthId, pa), ct);
				}
			}
			return HospitalResult.Success(phase.PlanId);
		}

		/// Work the patient accepted, done: the line and its mark on the chart complete together.
		public async Task<HospitalResult> CompleteLineAsync(BusinessContext ctx, int lineId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var line = await Lines(ctx).AsNoTracking().FirstOrDefaultAsync(l => l.ID == lineId, ct);
			if (line == null) return HospitalResult.Fail("not_found");
			var decision = await Phases(ctx).Where(p => p.ID == line.PhaseId).Select(p => p.Decision).FirstAsync(ct);
			if (decision != HospitalDentalDecision.Accepted) return HospitalResult.Fail("Only work the patient accepted is done");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			int n = await Lines(ctx).Where(l => l.ID == lineId && l.Status == HospitalDentalLineStatus.Planned).ExecuteUpdateAsync(u => u
				.SetProperty(l => l.Status, HospitalDentalLineStatus.Completed).SetProperty(l => l.CompletedAt, nowUtc).SetProperty(l => l.CompletedByEmployeeId, employeeId), ct);
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail(ConcurrentChange); }
			await Findings(ctx).Where(f => f.PlanLineId == lineId && f.VoidedAt == null)
				.ExecuteUpdateAsync(u => u.SetProperty(f => f.Status, HospitalDentalMark.Completed).SetProperty(f => f.CompletedAt, nowUtc), ct);
			// H53: the work done, on the patient's bill at the fee they accepted, in the same transaction
			if (_billing != null && line.BillLineId == null)
			{
				var patientId = await Plans(ctx).Where(p => p.ID == line.PlanId).Select(p => p.PatientId).FirstAsync(ct);
				var now = HospitalNursingService.UtcToWard(nowUtc);
				var bill = await _billing.BillForWardChargeAsync(ctx, patientId, employeeId, now, ct);
				var charge = await _billing.AddServiceChargeAsync(ctx, bill, line.ServiceId, employeeId, now, ct, 1, line.Fee);
				await _db.SaveChangesAsync(ct);
				if (charge != null) await Lines(ctx).Where(l => l.ID == lineId).ExecuteUpdateAsync(u => u.SetProperty(l => l.BillLineId, (int?)charge.ID), ct);
			}
			await tx.CommitAsync();
			return HospitalResult.Success(line.PlanId);
		}

		/// Work taken out of the plan before the patient decided its phase.
		public async Task<HospitalResult> CancelLineAsync(BusinessContext ctx, int lineId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			var line = await Lines(ctx).AsNoTracking().FirstOrDefaultAsync(l => l.ID == lineId, ct);
			if (line == null) return HospitalResult.Fail("not_found");
			var decision = await Phases(ctx).Where(p => p.ID == line.PhaseId).Select(p => p.Decision).FirstAsync(ct);
			if (decision != HospitalDentalDecision.Pending) return HospitalResult.Fail("The patient has decided this phase; its work is no longer taken out");
			int n = await Lines(ctx).Where(l => l.ID == lineId && l.Status == HospitalDentalLineStatus.Planned)
				.ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, HospitalDentalLineStatus.Cancelled), ct);
			if (n != 1) return HospitalResult.Fail(ConcurrentChange);
			await Findings(ctx).Where(f => f.PlanLineId == lineId && f.VoidedAt == null)
				.ExecuteUpdateAsync(u => u.SetProperty(f => f.VoidedAt, nowUtc).SetProperty(f => f.VoidedByEmployeeId, employeeId), ct);
			return HospitalResult.Success(line.PlanId);
		}

		// ---------------- reading plans ----------------

		private sealed record PlanData(List<HospitalDentalPlan> Plans, List<HospitalDentalPlanPhase> Phases, List<HospitalDentalPlanLine> Lines);

		/// The quotation of each plan: its patient's benefit today, what of the yearly limit is spent, repeats.
		private async Task<Dictionary<int, (HospitalDentalQuote Quote, HospitalDentalBenefit? Benefit, decimal Used, HospitalPayer? Payer, string? Currency, int Decimals)>> QuotesAsync(
			BusinessContext ctx, PlanData d, IReadOnlyList<HospitalDentalProcedureRow> procs, DateTime nowUtc, CancellationToken ct)
		{
			var result = new Dictionary<int, (HospitalDentalQuote, HospitalDentalBenefit?, decimal, HospitalPayer?, string?, int)>();
			var patients = d.Plans.Select(p => p.PatientId).Distinct().ToList();
			// every completed line of these patients this year and every completed filling in two years (for the limit and the repeat rule)
			var allLines = await (from l in Lines(ctx).AsNoTracking() join p in Plans(ctx).AsNoTracking() on l.PlanId equals p.ID
								  where patients.Contains(p.PatientId) && l.Status == HospitalDentalLineStatus.Completed
								  select new { l.ID, l.PlanId, p.PatientId, l.ServiceId, l.Fee, l.Tooth, l.Surfaces, l.CompletedAt }).ToListAsync(ct);
			var fillings = await Findings(ctx).AsNoTracking().Where(f => patients.Contains(f.PatientId) && f.VoidedAt == null && f.Surfaces != null
					&& (f.Status == HospitalDentalMark.Completed || f.Status == HospitalDentalMark.Existing))
				.Select(f => new { f.PatientId, f.Tooth, f.Surfaces, f.PlanLineId, At = f.CompletedAt ?? f.RecordedAt }).ToListAsync(ct);
			var eligibility = new Dictionary<int, (HospitalPayer? Payer, HospitalDentalBenefit? Benefit)>();
			HospitalDentalProcedure? P(int serviceId) => procs.FirstOrDefault(x => x.Procedure.ServiceId == serviceId)?.Procedure;
			foreach (var plan in d.Plans)
			{
				if (!eligibility.TryGetValue(plan.PatientId, out var el))
				{
					var (_, payer, why) = await _insurance.EligibilityAsync(ctx, plan.PatientId, nowUtc, null, ct);
					el = why == null && payer != null ? (payer, await BenefitOfAsync(ctx, payer.ID, ct)) : (null, null);
					eligibility[plan.PatientId] = el;
				}
				var cur = await _pricing.CurrencyOfAsync(ctx, plan.CurrencyId, ct);
				int dec = cur?.Decimals ?? DefaultDecimals;
				// the limit is spent by this year's completed work in the patient's OTHER plans, at the same cover
				var spentLines = allLines.Where(l => l.PatientId == plan.PatientId && l.PlanId != plan.ID && l.CompletedAt?.Year == nowUtc.Year)
					.Select(l => { var pr = P(l.ServiceId); return new HospitalDentalRules.QuoteInput(l.ID, l.Fee, pr?.Category ?? "", pr?.NotCovered ?? true, false, false); });
				decimal used = HospitalDentalRules.Quote(spentLines, el.Benefit, 0, dec).PayerShare;
				var declined = d.Phases.Where(h => h.PlanId == plan.ID && h.Decision == HospitalDentalDecision.Declined).Select(h => h.ID).ToHashSet();
				var mine = d.Lines.Where(l => l.PlanId == plan.ID && l.Status != HospitalDentalLineStatus.Cancelled).Select(l =>
				{
					var pr = P(l.ServiceId);
					var before = fillings.Where(f => f.PatientId == plan.PatientId && f.PlanLineId != l.ID).Select(f => (f.Tooth, f.Surfaces!, f.At));
					bool repeat = el.Benefit != null && HospitalDentalRules.RepeatWithinTwoYears(l.Tooth, l.Surfaces, before, l.CompletedAt ?? nowUtc);
					return new HospitalDentalRules.QuoteInput(l.ID, l.Fee, pr?.Category ?? "", pr?.NotCovered ?? true, declined.Contains(l.PhaseId), repeat);
				});
				result[plan.ID] = (HospitalDentalRules.Quote(mine, el.Benefit, used, dec), el.Benefit, used, el.Payer, cur?.Code, dec);
			}
			return result;
		}

		private async Task<List<HospitalDentalPlanRow>> RowsAsync(BusinessContext ctx, PlanData d, IReadOnlyList<HospitalDentalProcedureRow> procs, DateTime nowUtc,
			Dictionary<int, (HospitalDentalQuote Quote, HospitalDentalBenefit? Benefit, decimal Used, HospitalPayer? Payer, string? Currency, int Decimals)> quotes, CancellationToken ct)
		{
			var pids = d.Plans.Select(p => p.PatientId).Distinct().ToList();
			var patients = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pids.Contains(p.ID))
				.Select(p => new { p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath }).ToListAsync(ct);
			var dentists = await DentistsAsync(ctx, ct);
			return d.Plans.Select(p =>
			{
				var pa = patients.First(x => x.ID == p.PatientId);
				var q = quotes[p.ID];
				return new HospitalDentalPlanRow(p, pa.Name, pa.NameEn, pa.Mrn, pa.PhotoPath, dentists.FirstOrDefault(x => x.Id == p.PractitionerId),
					d.Phases.Count(h => h.PlanId == p.ID), HospitalDentalRules.PlanState(d.Phases.Where(h => h.PlanId == p.ID).Select(h => h.Decision)), q.Quote, q.Currency, q.Decimals);
			}).ToList();
		}

		private async Task<PlanData> LoadAsync(BusinessContext ctx, IQueryable<HospitalDentalPlan> q, CancellationToken ct)
		{
			var plans = await q.AsNoTracking().OrderByDescending(p => p.CreatedAt).Take(300).ToListAsync(ct);
			var ids = plans.Select(p => p.ID).ToList();
			return new PlanData(plans, await Phases(ctx).AsNoTracking().Where(h => ids.Contains(h.PlanId)).OrderBy(h => h.Seq).ToListAsync(ct),
				await Lines(ctx).AsNoTracking().Where(l => ids.Contains(l.PlanId)).OrderBy(l => l.ID).ToListAsync(ct));
		}

		public async Task<(IReadOnlyList<HospitalDentalPlanRow> Rows, HospitalDentalPlanStats Stats)> PlansAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var d = await LoadAsync(ctx, Plans(ctx), ct);
			var procs = await ProceduresAsync(ctx, ct);
			var quotes = await QuotesAsync(ctx, d, procs, nowUtc, ct);
			var rows = await RowsAsync(ctx, d, procs, nowUtc, quotes, ct);
			var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1);
			var month = rows.Where(r => r.Plan.CreatedAt >= monthStart).ToList();
			// acceptance by value over this month's plans: accepted phases' work against all the work presented
			decimal presented = d.Lines.Where(l => month.Any(m => m.Plan.ID == l.PlanId) && l.Status != HospitalDentalLineStatus.Cancelled).Sum(l => l.Fee);
			var accepted = d.Phases.Where(h => h.Decision == HospitalDentalDecision.Accepted).Select(h => h.ID).ToHashSet();
			decimal acceptedValue = d.Lines.Where(l => month.Any(m => m.Plan.ID == l.PlanId) && l.Status != HospitalDentalLineStatus.Cancelled && accepted.Contains(l.PhaseId)).Sum(l => l.Fee);
			decimal notDone = d.Lines.Where(l => accepted.Contains(l.PhaseId) && l.Status == HospitalDentalLineStatus.Planned).Sum(l => l.Fee);
			var limits = quotes.Values.Where(q => q.Benefit != null && q.Benefit.YearlyLimit > 0).Select(q => q.Benefit!.YearlyLimit).ToList();
			var facility = await _pricing.FacilityCurrencyAsync(ctx, ct);
			return (rows, new HospitalDentalPlanStats(month.Count, presented == 0 ? null : Math.Round(acceptedValue * 100 / presented, 0), notDone,
				limits.Count == 0 ? null : limits.Min(), limits.Count == 0 ? null : limits.Max(), facility.Code, facility.Decimals));
		}

		public async Task<HospitalDentalPlanView?> PlanAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			var d = await LoadAsync(ctx, Plans(ctx).Where(p => p.ID == id), ct);
			if (d.Plans.Count == 0) return null;
			var procs = await ProceduresAsync(ctx, ct);
			// a line keeps its procedure even when the service was later retired from the catalogue
			var lineServices = d.Lines.Select(l => l.ServiceId).Where(s => procs.All(p => p.Procedure.ServiceId != s)).Distinct().ToList();
			if (lineServices.Count > 0)
			{
				var extra = await (from p in Procedures(ctx).AsNoTracking() join s in _db.HospitalServices.AsNoTracking() on p.ServiceId equals s.ID
								   where lineServices.Contains(p.ServiceId) select new { p, s.Code, s.Name, s.NameEn, s.Price }).ToListAsync(ct);
				procs = procs.Concat(extra.Select(x => new HospitalDentalProcedureRow(x.p, x.Code, x.Name, x.NameEn, x.Price))).ToList();
			}
			var quotes = await QuotesAsync(ctx, d, procs, nowUtc, ct);
			var row = (await RowsAsync(ctx, d, procs, nowUtc, quotes, ct))[0];
			var dentists = await DentistsAsync(ctx, ct);
			var q = quotes[id];
			var phases = d.Phases.Select(h => new HospitalDentalPhaseRow(h, d.Lines.Where(l => l.PhaseId == h.ID && l.Status != HospitalDentalLineStatus.Cancelled).Select(l =>
				new HospitalDentalLineRow(l, procs.First(p => p.Procedure.ServiceId == l.ServiceId), dentists.FirstOrDefault(x => x.Id == (l.ProviderId ?? row.Plan.PractitionerId)),
					q.Quote.Lines.FirstOrDefault(x => x.LineId == l.ID))).ToList())).ToList();
			return new HospitalDentalPlanView(row, phases, q.Benefit?.YearlyLimit, q.Used, q.Payer?.Name, q.Payer?.NameEn);
		}
	}
}
