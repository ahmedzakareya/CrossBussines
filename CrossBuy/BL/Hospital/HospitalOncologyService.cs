using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H88 — chemotherapy. The regimen library, a patient's course, and each cycle checked, dosed and given (or
	// held). The dose is computed, never typed: mg/m² × the Mosteller body surface area, mg/kg × the weight, a flat mg,
	// or a carboplatin AUC × (GFR + 25) by Calvert with the Cockcroft-Gault clearance capped at 125 ml/min (the FDA's
	// 2010 cap). Before a cycle the counts must clear it (neutrophils ≥ 1.5 ×10⁹/L, platelets ≥ 100 ×10⁹/L), the cycle
	// must not come early, a grade 3–4 toxicity must bring a reduction, and a drug with a lifetime limit must stay under
	// it across ALL the patient's courses. A blocked check can be overruled only with a written reason, kept on the cycle.
	// ==========================================================================================
	public sealed class HospitalChemoDrugInput
	{
		public string? DrugName { get; set; }
		public decimal Dose { get; set; }
		public string? Basis { get; set; }
		public string? Route { get; set; }
		public string? Days { get; set; }
		public decimal? LifetimeMgM2 { get; set; }
	}

	public sealed class HospitalChemoRegimenInput
	{
		public int Id { get; set; }
		public string? Code { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public int CycleDays { get; set; }
		public int MaxCycles { get; set; }
		public string? Note { get; set; }
		public bool IsActive { get; set; } = true;
		public List<HospitalChemoDrugInput>? Drugs { get; set; }
	}

	public sealed class HospitalChemoCourseInput
	{
		public int PatientId { get; set; }
		public int RegimenId { get; set; }
		public string? Diagnosis { get; set; }
		public string? Stage { get; set; }
		public string? Intent { get; set; }
		public decimal HeightCm { get; set; }
		public decimal WeightKg { get; set; }
		public DateTime? StartDate { get; set; }
	}

	public sealed class HospitalChemoCycleInput
	{
		public decimal? WeightKg { get; set; }
		public decimal? Anc { get; set; }
		public int? Platelets { get; set; }
		public decimal? Creatinine { get; set; }
		public int DoseReductionPct { get; set; }
		public int? ToxicityGrade { get; set; }
		public string? ToxicityNote { get; set; }
		public string? OverrideReason { get; set; }
		/// Ward-local day; empty means today.
		public DateTime? Date { get; set; }
	}

	public sealed class HospitalChemoHoldInput
	{
		public DateTime? Until { get; set; }
		public string? Reason { get; set; }
		public int? ToxicityGrade { get; set; }
		public decimal? Anc { get; set; }
		public int? Platelets { get; set; }
	}

	public sealed class HospitalChemoEndInput
	{
		public bool Completed { get; set; }
		public string? Reason { get; set; }
	}

	public static class HospitalChemoCheck
	{
		public const string NoCounts = "NoCounts";
		public const string LowAnc = "LowAnc";
		public const string LowPlatelets = "LowPlatelets";
		public const string Early = "Early";
		public const string Toxicity = "Toxicity";
		public const string Lifetime = "Lifetime";
		public const string WeightChange = "WeightChange";
		/// Only these may be overruled with a reason; WeightChange is shown, never blocks.
		public static bool Blocks(string code) => code != WeightChange;
	}

	public sealed record HospitalChemoDoseLine(string DrugName, decimal Dose, string Basis, string Route, string Days, decimal DoseMg, decimal? CycleMgM2, decimal? LifetimeMgM2, decimal? PriorMgM2);
	public sealed record HospitalChemoPreview(int CycleNo, decimal Bsa, decimal? Gfr, IReadOnlyList<HospitalChemoDoseLine> Doses, IReadOnlyList<string> Checks, string? Refusal);
	public sealed record HospitalChemoCourseRow(HospitalChemoCourse Course, string Name, string NameEn, string Mrn, string Regimen, int Given, int MaxCycles, DateTime? NextDue, bool Overdue, int? LastToxicity, bool Held);
	public sealed record HospitalChemoCycleView(HospitalChemoCycle Cycle, IReadOnlyList<HospitalChemoDose> Doses);
	public sealed record HospitalChemoCoursePage(HospitalChemoCourse Course, HospitalChemoRegimen Regimen, IReadOnlyList<HospitalChemoRegimenDrug> Drugs, string Name, string NameEn, string Mrn, string? Photo,
		string Sex, int? Age, IReadOnlyList<HospitalChemoCycleView> Cycles, int Given, DateTime? NextDue, IReadOnlyDictionary<string, decimal> Lifetime, IReadOnlyDictionary<int, string> People);
	public sealed record HospitalChemoRegimenRow(HospitalChemoRegimen Regimen, IReadOnlyList<HospitalChemoRegimenDrug> Drugs, int ActiveCourses);

	public static class HospitalOncologyRules
	{
		public const decimal MinAnc = 1.5m;
		public const int MinPlatelets = 100;
		public const int EarlyByDays = 2;
		public const decimal WeightChangePct = 10m;
		public const decimal GfrCap = 125m;

		/// Mosteller: √(height cm × weight kg / 3600), to two places.
		public static decimal Bsa(decimal heightCm, decimal weightKg) =>
			heightCm <= 0 || weightKg <= 0 ? 0 : Math.Round((decimal)Math.Sqrt((double)(heightCm * weightKg) / 3600.0), 2);

		/// Cockcroft-Gault in ml/min (× 0.85 for a woman), capped at 125 for the Calvert formula.
		public static decimal? Gfr(int? age, decimal weightKg, decimal? creatinineMgDl, bool female)
		{
			if (age is not int a || a <= 0 || creatinineMgDl is not decimal cr || cr <= 0 || weightKg <= 0) return null;
			var g = (140 - a) * weightKg / (72m * cr) * (female ? 0.85m : 1m);
			return Math.Round(Math.Min(GfrCap, Math.Max(0, g)), 1);
		}

		public static int DayCount(string? days) => (days ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Count(d => int.TryParse(d, out var n) && n >= 1);

		/// One administration's mg after the reduction, or null when it cannot be computed (an AUC without a GFR).
		public static decimal? DoseMg(string basis, decimal dose, decimal bsa, decimal weightKg, decimal? gfr, int reductionPct)
		{
			decimal? full = basis switch
			{
				HospitalChemoBasis.PerM2 => dose * bsa,
				HospitalChemoBasis.PerKg => dose * weightKg,
				HospitalChemoBasis.Flat => dose,
				HospitalChemoBasis.Auc => gfr is decimal g ? dose * (g + 25m) : null,
				_ => null,
			};
			return full == null ? null : Math.Round(full.Value * (100 - reductionPct) / 100m, 1);
		}

		public static string? DaysRefusal(string? days)
		{
			var parts = (days ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (parts.Length == 0 || parts.Any(p => !int.TryParse(p, out var n) || n < 1 || n > 56)) return "Write the days as numbers, like 1 or 1,8";
			return null;
		}

		public static string? RegimenRefusal(HospitalChemoRegimenInput i)
		{
			if (string.IsNullOrWhiteSpace(i.Code)) return "Write the regimen's code";
			if (string.IsNullOrWhiteSpace(i.Name)) return "Write the regimen's name";
			if (i.CycleDays is < 7 or > 56) return "A cycle lasts between 7 and 56 days";
			if (i.MaxCycles is < 1 or > 52) return "The cycles are between 1 and 52";
			if (i.Drugs == null || i.Drugs.Count == 0) return "Add at least one drug";
			foreach (var d in i.Drugs)
			{
				if (string.IsNullOrWhiteSpace(d.DrugName)) return "Write each drug's name";
				if (d.Dose <= 0) return "Each drug needs a dose above zero";
				if (!HospitalChemoBasis.All.Contains(d.Basis ?? "")) return "Choose each drug's dose basis";
				if (!HospitalChemoRoute.All.Contains(d.Route ?? "")) return "Choose each drug's route";
				if (DaysRefusal(d.Days) is string bad) return bad;
				if (d.Days!.Split(',').Select(x => int.Parse(x.Trim())).Any(n => n > i.CycleDays)) return "A drug's day falls after the cycle ends";
				if (d.Basis == HospitalChemoBasis.Auc && d.Dose > 10) return "An AUC is between 1 and 10";
				if (d.LifetimeMgM2 is <= 0) return "A lifetime limit must be above zero";
				if (d.LifetimeMgM2 != null && d.Basis != HospitalChemoBasis.PerM2) return "A lifetime limit is kept for a mg/m² dose only";
			}
			if (i.Drugs.GroupBy(d => d.DrugName!.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return "A drug is written twice";
			return null;
		}

		public static string? CourseRefusal(HospitalChemoCourseInput i, DateTime start, DateTime today)
		{
			if (string.IsNullOrWhiteSpace(i.Diagnosis)) return "Write the diagnosis";
			if (!HospitalChemoIntent.All.Contains(i.Intent ?? "")) return "Choose the intent of the treatment";
			if (i.HeightCm is < 40 or > 250) return "The height is between 40 and 250 cm";
			if (i.WeightKg is < 2 or > 300) return "The weight is between 2 and 300 kg";
			if (start > today.AddDays(60) || start < today.AddDays(-30)) return "The start is within a month back and two months ahead";
			return null;
		}

		/// The day the next cycle is due: the last given + the cycle's days, or later when it was held to a later day.
		public static DateTime? NextDue(HospitalChemoCourse c, int cycleDays, IEnumerable<HospitalChemoCycle> cycles)
		{
			var list = cycles.ToList();
			var lastGiven = list.Where(x => x.Status == HospitalChemoCycleStatus.Given).OrderByDescending(x => x.CycleNo).FirstOrDefault();
			DateTime due = lastGiven == null ? c.StartDate : lastGiven.CycleDate.AddDays(cycleDays);
			var held = list.Where(x => x.Status == HospitalChemoCycleStatus.Held && (lastGiven == null || x.CycleNo > lastGiven.CycleNo)).Select(x => x.HeldUntil).Max();
			return held is DateTime h && h > due ? h : due;
		}

		/// Everything a cycle is checked and dosed on, without writing. `prior` is each drug's mg/m² given in earlier
		/// cycles of ANY course; `lastGiven` the day the previous cycle of THIS course was given.
		public static HospitalChemoPreview Preview(HospitalChemoCourse c, HospitalChemoRegimen r, IReadOnlyList<HospitalChemoRegimenDrug> drugs, int given, DateTime? lastGiven,
			int? prevToxicity, IReadOnlyDictionary<string, decimal> prior, int? age, bool female, HospitalChemoCycleInput i, DateTime day)
		{
			int no = given + 1;
			string? refusal = null;
			if (c.Status != HospitalChemoCourseStatus.Active) refusal = "This course is closed";
			else if (given >= r.MaxCycles) refusal = "The regimen's cycles are all given";
			else if (i.WeightKg is not decimal w0 || w0 < 2 || w0 > 300) refusal = "Weigh the patient: the dose is computed on today's weight";
			else if (i.DoseReductionPct is < 0 or > 75) refusal = "The dose reduction is between 0 and 75 %";
			else if (i.ToxicityGrade is < 0 or > 4) refusal = "The toxicity grade is between 0 and 4";
			else if (i.Anc is < 0 or > 100 || i.Platelets is < 0 or > 2000 || i.Creatinine is < 0.1m or > 20m) refusal = "A count is not plausible";
			var w = i.WeightKg ?? c.WeightKg;
			var bsa = HospitalOncologyRules.Bsa(c.HeightCm, w);
			var gfr = Gfr(age, w, i.Creatinine, female);
			var lines = new List<HospitalChemoDoseLine>();
			var checks = new List<string>();
			foreach (var d in drugs.OrderBy(d => d.SortOrder))
			{
				var mg = DoseMg(d.Basis, d.Dose, bsa, w, gfr, i.DoseReductionPct);
				if (mg == null) { refusal ??= "The carboplatin dose needs the creatinine and the patient's age"; continue; }
				decimal? cycleM2 = d.Basis == HospitalChemoBasis.PerM2 && d.LifetimeMgM2 != null ? Math.Round(d.Dose * (100 - i.DoseReductionPct) / 100m * DayCount(d.Days), 1) : null;
				var before = prior.TryGetValue(d.DrugName.Trim().ToUpperInvariant(), out var p) ? p : 0m;
				if (cycleM2 != null && before + cycleM2 > d.LifetimeMgM2 && !checks.Contains(HospitalChemoCheck.Lifetime)) checks.Add(HospitalChemoCheck.Lifetime);
				lines.Add(new HospitalChemoDoseLine(d.DrugName, d.Dose, d.Basis, d.Route, d.Days, mg.Value, cycleM2, d.LifetimeMgM2, d.LifetimeMgM2 == null ? null : before));
			}
			if (i.Anc == null || i.Platelets == null) checks.Insert(0, HospitalChemoCheck.NoCounts);
			else
			{
				if (i.Anc < MinAnc) checks.Insert(0, HospitalChemoCheck.LowAnc);
				if (i.Platelets < MinPlatelets) checks.Add(HospitalChemoCheck.LowPlatelets);
			}
			if (lastGiven is DateTime lg && (day - lg).TotalDays < r.CycleDays - EarlyByDays) checks.Add(HospitalChemoCheck.Early);
			if (Math.Max(prevToxicity ?? 0, i.ToxicityGrade ?? 0) >= 3 && i.DoseReductionPct == 0) checks.Add(HospitalChemoCheck.Toxicity);
			if (c.WeightKg > 0 && Math.Abs(w - c.WeightKg) / c.WeightKg * 100m > WeightChangePct) checks.Add(HospitalChemoCheck.WeightChange);
			return new HospitalChemoPreview(no, bsa, gfr, lines, checks, refusal);
		}

		public static string? HoldRefusal(HospitalChemoCourse c, HospitalChemoHoldInput i, DateTime today)
		{
			if (c.Status != HospitalChemoCourseStatus.Active) return "This course is closed";
			if (string.IsNullOrWhiteSpace(i.Reason)) return "Write why the cycle is held";
			if (i.Until is not DateTime u || u.Date <= today || u.Date > today.AddDays(42)) return "Hold the cycle to a day within six weeks";
			if (i.ToxicityGrade is < 0 or > 4) return "The toxicity grade is between 0 and 4";
			return null;
		}

		public static int? Age(DateTime? birth, DateTime today)
		{
			if (birth is not DateTime b) return null;
			int a = today.Year - b.Year; if (b.Date > today.AddYears(-a)) a--;
			return a;
		}
	}

	public sealed class HospitalOncologyService
	{
		public const string NoTables = "Oncology is not installed yet (hospital_h88_oncology.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalOncologyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		// H119: with the billing service a completed session is charged; without it (a test's hand-made service) it is not
		private readonly HospitalBillingService? _billing;
		[ActivatorUtilitiesConstructor]
		public HospitalOncologyService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService billing) : this(db, access) { _billing = billing; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private static DateTime Today(DateTime nowUtc) => HospitalNursingService.UtcToWard(nowUtc).Date;
		private IQueryable<HospitalChemoRegimen> Regimens(BusinessContext ctx) => _db.Set<HospitalChemoRegimen>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalChemoRegimenDrug> RegimenDrugs(BusinessContext ctx) => _db.Set<HospitalChemoRegimenDrug>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalChemoCourse> Courses(BusinessContext ctx) => _db.Set<HospitalChemoCourse>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalChemoCycle> Cycles(BusinessContext ctx) => _db.Set<HospitalChemoCycle>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalChemoDose> Doses(BusinessContext ctx) => _db.Set<HospitalChemoDose>().Where(x => x.CompanyId == ctx.CompanyId);

		public Task<bool> CanLibraryAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
		public Task<bool> CanTreatAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Consult, null, ct);

		// ---------------------------------------------------------------- the regimen library
		public async Task<HospitalResult> SaveRegimenAsync(BusinessContext ctx, HospitalChemoRegimenInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanLibraryAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalOncologyRules.RegimenRefusal(input) is string why) return HospitalResult.Fail(why);
			try
			{
				var code = input.Code!.Trim().ToUpperInvariant();
				if (await Regimens(ctx).AnyAsync(r => r.Code == code && r.ID != input.Id, ct)) return HospitalResult.Fail("Another regimen has this code");
				HospitalChemoRegimen row;
				await using var tx = await _db.Database.BeginTransactionAsync(ct);
				if (input.Id == 0)
				{
					row = new HospitalChemoRegimen { CompanyId = ctx.CompanyId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
					_db.Add(row);
				}
				else
				{
					var found = await Regimens(ctx).FirstOrDefaultAsync(r => r.ID == input.Id, ct);
					if (found == null) return HospitalResult.Fail("not_found");
					// a protocol in use is never changed under its patients: save the change as a new regimen (a new code)
					bool used = await Courses(ctx).AnyAsync(c => c.RegimenId == found.ID, ct);
					if (used && (found.CycleDays != input.CycleDays || found.MaxCycles != input.MaxCycles || await DrugsChangedAsync(ctx, found.ID, input.Drugs!, ct)))
						return HospitalResult.Fail("This regimen is in use: save the change under a new code");
					row = found; row.UpdatedAt = nowUtc;
					if (!used) _db.RemoveRange(await RegimenDrugs(ctx).Where(d => d.RegimenId == found.ID).ToListAsync(ct));
				}
				bool fresh = row.ID == 0 || !await Courses(ctx).AnyAsync(c => c.RegimenId == row.ID, ct);
				row.Code = code; row.Name = input.Name!.Trim(); row.NameEn = Clip(input.NameEn, 150); row.CycleDays = input.CycleDays; row.MaxCycles = input.MaxCycles;
				row.Note = Clip(input.Note, 500); row.IsActive = input.IsActive;
				await _db.SaveChangesAsync(ct);
				if (fresh)
				{
					int n = 0;
					foreach (var d in input.Drugs!)
						_db.Add(new HospitalChemoRegimenDrug { CompanyId = ctx.CompanyId, RegimenId = row.ID, SortOrder = ++n, DrugName = d.DrugName!.Trim(), Dose = d.Dose, Basis = d.Basis!, Route = d.Route!,
							Days = string.Join(",", d.Days!.Split(',').Select(x => int.Parse(x.Trim()))), LifetimeMgM2 = d.LifetimeMgM2 });
					await _db.SaveChangesAsync(ct);
				}
				await tx.CommitAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		private async Task<bool> DrugsChangedAsync(BusinessContext ctx, int regimenId, List<HospitalChemoDrugInput> drugs, CancellationToken ct)
		{
			var have = await RegimenDrugs(ctx).AsNoTracking().Where(d => d.RegimenId == regimenId).OrderBy(d => d.SortOrder).ToListAsync(ct);
			string Key(string n, decimal dose, string b, string r, string days, decimal? l) => $"{n.Trim().ToUpperInvariant()}|{dose:0.##}|{b}|{r}|{string.Join(",", days.Split(',').Select(x => x.Trim()))}|{l:0.#}";
			return !have.Select(d => Key(d.DrugName, d.Dose, d.Basis, d.Route, d.Days, d.LifetimeMgM2))
				.SequenceEqual(drugs.Select(d => Key(d.DrugName ?? "", d.Dose, d.Basis ?? "", d.Route ?? "", d.Days ?? "", d.LifetimeMgM2)));
		}

		public async Task<(IReadOnlyList<HospitalChemoRegimenRow> Rows, int Total, bool Ready)> RegimensAsync(BusinessContext ctx, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var query = Regimens(ctx).AsNoTracking();
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(r => r.Code.Contains(s) || r.Name.Contains(s) || (r.NameEn != null && r.NameEn.Contains(s))
					|| RegimenDrugs(ctx).Any(d => d.RegimenId == r.ID && d.DrugName.Contains(s))); }
				int total = await query.CountAsync(ct);
				var list = await query.OrderByDescending(r => r.IsActive).ThenBy(r => r.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var ids = list.Select(r => r.ID).ToList();
				var drugs = await RegimenDrugs(ctx).AsNoTracking().Where(d => ids.Contains(d.RegimenId)).OrderBy(d => d.SortOrder).ToListAsync(ct);
				var used = await Courses(ctx).AsNoTracking().Where(c => ids.Contains(c.RegimenId) && c.Status == HospitalChemoCourseStatus.Active).GroupBy(c => c.RegimenId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
				return (list.Select(r => new HospitalChemoRegimenRow(r, drugs.Where(d => d.RegimenId == r.ID).ToList(), used.FirstOrDefault(u => u.Key == r.ID)?.N ?? 0)).ToList(), total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalChemoRegimenRow>(), 0, false); }
		}

		public async Task<IReadOnlyList<(int Id, string Text)>> ActiveRegimensAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				return (await Regimens(ctx).AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Code).Select(r => new { r.ID, r.Code, r.Name, r.NameEn }).ToListAsync(ct))
					.Select(r => (r.ID, r.Code + " — " + DisplayName.Of(r.Name, r.NameEn ?? r.Name))).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<(int, string)>(); }
		}

		// ---------------------------------------------------------------- courses
		public async Task<HospitalResult> StartCourseAsync(BusinessContext ctx, HospitalChemoCourseInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			var today = Today(nowUtc); var start = (input.StartDate ?? today).Date;
			if (HospitalOncologyRules.CourseRefusal(input, start, today) is string why) return HospitalResult.Fail(why);
			try
			{
				if (!await _db.HospitalPatients.AsNoTracking().AnyAsync(p => p.ID == input.PatientId && p.CompanyId == ctx.CompanyId && p.IsActive, ct)) return HospitalResult.Fail("not_found");
				var r = await Regimens(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.RegimenId, ct);
				if (r == null || !r.IsActive) return HospitalResult.Fail("Choose an active regimen");
				if (await Courses(ctx).AnyAsync(c => c.PatientId == input.PatientId && c.RegimenId == r.ID && c.Status == HospitalChemoCourseStatus.Active, ct)) return HospitalResult.Fail("The patient is already on this regimen");
				var nos = await Courses(ctx).Select(x => x.CourseNo).ToListAsync(ct);
				var row = new HospitalChemoCourse
				{
					CompanyId = ctx.CompanyId, CourseNo = HospitalDentalPracticeRules.NextNo(nos, "CH-", 6), PatientId = input.PatientId, RegimenId = r.ID, Diagnosis = input.Diagnosis!.Trim()[..Math.Min(200, input.Diagnosis.Trim().Length)],
					Stage = Clip(input.Stage, 30), Intent = input.Intent!, HeightCm = input.HeightCm, WeightKg = input.WeightKg, StartDate = start, Status = HospitalChemoCourseStatus.Active,
					CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> EndCourseAsync(BusinessContext ctx, int id, HospitalChemoEndInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var c = await Courses(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (c == null) return HospitalResult.Fail("not_found");
				if (c.Status != HospitalChemoCourseStatus.Active) return HospitalResult.Fail("This course is closed");
				if (!input.Completed && string.IsNullOrWhiteSpace(input.Reason)) return HospitalResult.Fail("Write why the course is stopped");
				c.Status = input.Completed ? HospitalChemoCourseStatus.Completed : HospitalChemoCourseStatus.Stopped; c.EndReason = Clip(input.Reason, 300); c.EndedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Each drug's mg/m² already given to the patient, in every course (the lifetime limit is the patient's, not the course's).
		private async Task<Dictionary<string, decimal>> LifetimeAsync(BusinessContext ctx, int patientId, CancellationToken ct) =>
			(await (from d in Doses(ctx).AsNoTracking() join y in Cycles(ctx).AsNoTracking() on d.CycleId equals y.ID
					where d.PatientId == patientId && y.Status == HospitalChemoCycleStatus.Given && d.CumulativeMgM2 != null
					select new { d.DrugName, d.CumulativeMgM2 }).ToListAsync(ct))
			.GroupBy(x => x.DrugName.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Sum(x => x.CumulativeMgM2!.Value));

		private async Task<(HospitalChemoCourse C, HospitalChemoRegimen R, List<HospitalChemoRegimenDrug> D, List<HospitalChemoCycle> Y, int? Age, bool Female)?> LoadAsync(BusinessContext ctx, int id, DateTime today, CancellationToken ct)
		{
			var c = await Courses(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (c == null) return null;
			var r = await Regimens(ctx).AsNoTracking().FirstAsync(x => x.ID == c.RegimenId, ct);
			var d = await RegimenDrugs(ctx).AsNoTracking().Where(x => x.RegimenId == r.ID).OrderBy(x => x.SortOrder).ToListAsync(ct);
			var y = await Cycles(ctx).AsNoTracking().Where(x => x.CourseId == id).OrderBy(x => x.CycleNo).ThenBy(x => x.ID).ToListAsync(ct);
			var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == c.PatientId).Select(x => new { x.BirthDate, x.Sex }).FirstAsync(ct);
			return (c, r, d, y, HospitalOncologyRules.Age(p.BirthDate, today), p.Sex == HospitalPatientSex.Female);
		}

		private static (int Given, DateTime? LastGiven, int? PrevTox) State(List<HospitalChemoCycle> y)
		{
			var given = y.Where(x => x.Status == HospitalChemoCycleStatus.Given).OrderBy(x => x.CycleNo).ToList();
			var last = given.LastOrDefault();
			// the toxicity seen SINCE the last cycle was given: written on a hold after it (the next cycle's own form adds its grade)
			var sinceLast = y.Where(x => x.Status == HospitalChemoCycleStatus.Held && (last == null || x.CycleNo > last.CycleNo)).Select(x => x.ToxicityGrade).Where(t => t != null).Select(t => t!.Value).DefaultIfEmpty(-1).Max();
			return (given.Count, last?.CycleDate, sinceLast < 0 ? null : sinceLast);
		}

		public async Task<HospitalChemoPreview?> PreviewAsync(BusinessContext ctx, int courseId, HospitalChemoCycleInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var today = Today(nowUtc);
				var l = await LoadAsync(ctx, courseId, today, ct);
				if (l == null) return null;
				var (c, r, d, y, age, female) = l.Value;
				var (given, lastGiven, prevTox) = State(y);
				return HospitalOncologyRules.Preview(c, r, d, given, lastGiven, prevTox, await LifetimeAsync(ctx, c.PatientId, ct), age, female, input, (input.Date ?? today).Date);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// Gives the next cycle: the checks again (on the server, never trusted from the screen), the override reason when a
		/// check blocks, and every dose frozen as given.
		public async Task<HospitalResult> GiveAsync(BusinessContext ctx, int courseId, HospitalChemoCycleInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var today = Today(nowUtc); var day = (input.Date ?? today).Date;
				if (day > today || day < today.AddDays(-3)) return HospitalResult.Fail("A cycle is recorded on its day, or up to three days back");
				var l = await LoadAsync(ctx, courseId, today, ct);
				if (l == null) return HospitalResult.Fail("not_found");
				var (c, r, d, y, age, female) = l.Value;
				var (given, lastGiven, prevTox) = State(y);
				if (lastGiven is DateTime lg && day < lg) return HospitalResult.Fail("The day is before the previous cycle");
				var pv = HospitalOncologyRules.Preview(c, r, d, given, lastGiven, prevTox, await LifetimeAsync(ctx, c.PatientId, ct), age, female, input, day);
				if (pv.Refusal != null) return HospitalResult.Fail(pv.Refusal);
				if (pv.Checks.Any(HospitalChemoCheck.Blocks) && string.IsNullOrWhiteSpace(input.OverrideReason)) return HospitalResult.Fail("A check stops this cycle: write the reason to give it anyway");
				await using var tx = await _db.Database.BeginTransactionAsync(ct);
				var cy = new HospitalChemoCycle
				{
					CompanyId = ctx.CompanyId, CourseId = c.ID, PatientId = c.PatientId, CycleNo = pv.CycleNo, Status = HospitalChemoCycleStatus.Given, CycleDate = day, WeightKg = input.WeightKg, BsaM2 = pv.Bsa,
					Anc = input.Anc, Platelets = input.Platelets, Creatinine = input.Creatinine, DoseReductionPct = input.DoseReductionPct, ToxicityGrade = input.ToxicityGrade, ToxicityNote = Clip(input.ToxicityNote, 300),
					OverrideReason = pv.Checks.Any(HospitalChemoCheck.Blocks) ? Clip(input.OverrideReason, 300) : null, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(cy);
				await _db.SaveChangesAsync(ct);
				foreach (var x in pv.Doses)
					_db.Add(new HospitalChemoDose { CompanyId = ctx.CompanyId, CycleId = cy.ID, PatientId = c.PatientId, DrugName = x.DrugName, Dose = x.Dose, Basis = x.Basis, Route = x.Route, Days = x.Days, DoseMg = x.DoseMg, CumulativeMgM2 = x.CycleMgM2 });
				await _db.SaveChangesAsync(ct);
				// H119: the cycle's administration is charged with it (the drugs follow the pharmacy's own dispensing)
				if (_billing != null && await HospitalSessionCharges.ChargeAsync(_db, _billing, ctx, c.PatientId, HospitalSessionServiceCodes.Chemo, employeeId, nowUtc, ct) is { } line)
				{ cy.BillLineId = line.ID; await _db.SaveChangesAsync(ct); }
				// H120: the cycle's drugs go to the pharmacy as one request, line by line — stock and bill follow its dispensing
				if (await HospitalChemoPharmacyService.OpenRequestAsync(_db, ctx, c.PatientId, employeeId, nowUtc, pv.Doses.Select(x => (x.DrugName, x.DoseMg, x.Route, x.Days)).ToList(), ct) is { } rx)
				{ cy.DispenseId = rx.ID; await _db.SaveChangesAsync(ct); }
				await tx.CommitAsync(ct);
				return HospitalResult.Success(cy.ID);
			}
			catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 }) { return HospitalResult.Fail("This cycle was just given by someone else"); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> HoldAsync(BusinessContext ctx, int courseId, HospitalChemoHoldInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanTreatAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var today = Today(nowUtc);
				var l = await LoadAsync(ctx, courseId, today, ct);
				if (l == null) return HospitalResult.Fail("not_found");
				var (c, r, _, y, _, _) = l.Value;
				if (HospitalOncologyRules.HoldRefusal(c, input, today) is string why) return HospitalResult.Fail(why);
				var (given, _, _) = State(y);
				if (given >= r.MaxCycles) return HospitalResult.Fail("The regimen's cycles are all given");
				var row = new HospitalChemoCycle
				{
					CompanyId = ctx.CompanyId, CourseId = c.ID, PatientId = c.PatientId, CycleNo = given + 1, Status = HospitalChemoCycleStatus.Held, CycleDate = today, HeldUntil = input.Until!.Value.Date,
					Reason = Clip(input.Reason, 300), ToxicityGrade = input.ToxicityGrade, Anc = input.Anc, Platelets = input.Platelets, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------------------------------------------------------- reading
		/// The active courses, the most pressing first (overdue, then due soonest), searched, filtered and paged.
		public async Task<(IReadOnlyList<HospitalChemoCourseRow> Rows, int Total, bool Ready)> CoursesAsync(BusinessContext ctx, string? q, string? filter, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			var today = Today(nowUtc);
			try
			{
				var query = from c in Courses(ctx).AsNoTracking()
							join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID
							join r in Regimens(ctx).AsNoTracking() on c.RegimenId equals r.ID
							where c.Status == HospitalChemoCourseStatus.Active
							select new { c, p.Name, p.NameEn, p.Mrn, p.Phone, r.Code, RName = r.Name, RNameEn = r.NameEn, r.CycleDays, r.MaxCycles };
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.c.CourseNo.Contains(s) || x.Name.Contains(s) || x.NameEn.Contains(s) || x.Mrn.Contains(s) || (x.Phone != null && x.Phone.Contains(s))
					|| x.Code.Contains(s) || x.c.Diagnosis.Contains(s)); }
				var all = await query.ToListAsync(ct);
				var ids = all.Select(x => x.c.ID).ToList();
				var cycles = await Cycles(ctx).AsNoTracking().Where(y => ids.Contains(y.CourseId)).ToListAsync(ct);
				var rows = all.Select(x =>
				{
					var mine = cycles.Where(y => y.CourseId == x.c.ID).ToList();
					var (given, _, _) = State(mine);
					var tox = mine.Where(y => y.ToxicityGrade != null).OrderByDescending(y => y.ID).Select(y => y.ToxicityGrade).FirstOrDefault();   // the latest graded
					var next = HospitalOncologyRules.NextDue(x.c, x.CycleDays, mine);
					var lastGiven = mine.Where(y => y.Status == HospitalChemoCycleStatus.Given).Select(y => y.CycleNo).DefaultIfEmpty(0).Max();
					bool held = mine.Any(y => y.Status == HospitalChemoCycleStatus.Held && y.CycleNo > lastGiven && y.HeldUntil >= today);
					return new HospitalChemoCourseRow(x.c, x.Name, x.NameEn, x.Mrn, x.Code + " — " + DisplayName.Of(x.RName, x.RNameEn ?? x.RName), given, x.MaxCycles, given >= x.MaxCycles ? null : next,
						given < x.MaxCycles && next < today, tox, held);
				});
				rows = filter switch { "due" => rows.Where(r => r.NextDue != null && r.NextDue <= today.AddDays(7)), "overdue" => rows.Where(r => r.Overdue), "held" => rows.Where(r => r.Held), "toxic" => rows.Where(r => r.LastToxicity >= 3), _ => rows };
				var ordered = rows.OrderByDescending(r => r.Overdue).ThenBy(r => r.NextDue ?? DateTime.MaxValue).ThenBy(r => r.Name).ToList();
				return (ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), ordered.Count, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalChemoCourseRow>(), 0, false); }
		}

		public async Task<HospitalChemoCoursePage?> CoursePageAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var today = Today(nowUtc);
				var l = await LoadAsync(ctx, id, today, ct);
				if (l == null) return null;
				var (c, r, d, y, age, _) = l.Value;
				var p = await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == c.PatientId).Select(x => new { x.Name, x.NameEn, x.Mrn, x.PhotoPath, x.Sex }).FirstAsync(ct);
				var yIds = y.Select(x => x.ID).ToList();
				var doses = await Doses(ctx).AsNoTracking().Where(x => yIds.Contains(x.CycleId)).OrderBy(x => x.ID).ToListAsync(ct);
				var (given, _, _) = State(y);
				var eids = y.Where(x => x.CreatedByEmployeeId != null).Select(x => x.CreatedByEmployeeId!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new HospitalChemoCoursePage(c, r, d, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.Sex, age, y.Select(x => new HospitalChemoCycleView(x, doses.Where(z => z.CycleId == x.ID).ToList())).ToList(),
					given, c.Status == HospitalChemoCourseStatus.Active && given < r.MaxCycles ? HospitalOncologyRules.NextDue(c, r.CycleDays, y) : null, await LifetimeAsync(ctx, c.PatientId, ct), people);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}
	}
}
