using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	/// H115: what a range is matched against — the patient's sex, age in days and trimester (null = not pregnant) at the
	/// moment of the reading.
	public sealed record HospitalPatientFacts(string? Sex, int? AgeDays, int? Trimester);

	public sealed class HospitalConceptRangeInput
	{
		public string? Sex { get; set; }
		public int? AgeFrom { get; set; }
		public int? AgeTo { get; set; }
		/// days | months | years — what AgeFrom / AgeTo count.
		public string? AgeUnit { get; set; }
		public string? Pregnancy { get; set; }
		public decimal? NormalLow { get; set; }
		public decimal? NormalHigh { get; set; }
		public decimal? CriticalLow { get; set; }
		public decimal? CriticalHigh { get; set; }
		public decimal? NormalHigh2 { get; set; }
		public decimal? CriticalHigh2 { get; set; }
		public string? Source { get; set; }
	}

	public static class HospitalRangeRules
	{
		public const int MaxAgeDays = 150 * 366;

		/// An age in the unit the screen speaks, as days (a month = 30.4375 days, a year = 365.25: the same in both directions).
		public static int? ToDays(int? n, string? unit) => n is not int v ? null : unit switch
		{
			"days" => v, "months" => (int)Math.Round(v * 30.4375m, MidpointRounding.AwayFromZero), _ => (int)Math.Round(v * 365.25m, MidpointRounding.AwayFromZero),
		};

		public static bool Matches(HospitalConceptRange r, HospitalPatientFacts f)
		{
			if (r.Sex != null && r.Sex != f.Sex) return false;
			if ((r.AgeFromDays != null || r.AgeToDays != null) && f.AgeDays is not int age) return false;   // an age band never fits an unknown age
			if (r.AgeFromDays is int from && f.AgeDays < from) return false;
			if (r.AgeToDays is int to && f.AgeDays >= to) return false;
			return r.Pregnancy switch
			{
				null => true,
				HospitalRangePregnancy.NotPregnant => f.Trimester == null,
				HospitalRangePregnancy.Pregnant => f.Trimester != null,
				HospitalRangePregnancy.T1 => f.Trimester == 1,
				HospitalRangePregnancy.T2 => f.Trimester == 2,
				HospitalRangePregnancy.T3 => f.Trimester == 3,
				_ => false,
			};
		}

		/// How much a range says about who it is for. A trimester says more than "pregnant" with a sex (a pregnancy already
		/// means a woman); an age band says more than a sex alone (a girl's range beats the adult women's).
		public static int Specificity(HospitalConceptRange r) =>
			(r.Sex != null ? 1 : 0) + (r.AgeFromDays != null || r.AgeToDays != null ? 2 : 0)
			+ (r.Pregnancy is HospitalRangePregnancy.T1 or HospitalRangePregnancy.T2 or HospitalRangePregnancy.T3 ? 4 : r.Pregnancy != null ? 2 : 0);

		/// The range that speaks for this patient: the most specific that fits; among equals the narrowest age band, then
		/// the one written first. Null = none fits (the concept's own range stands).
		public static HospitalConceptRange? Pick(IEnumerable<HospitalConceptRange> ranges, HospitalPatientFacts f) =>
			ranges.Where(r => Matches(r, f))
				.OrderByDescending(Specificity)
				.ThenBy(r => (long)(r.AgeToDays ?? MaxAgeDays) - (r.AgeFromDays ?? 0))
				.ThenBy(r => r.ID)
				.FirstOrDefault();

		/// The concept as it reads for this patient: a COPY with the range's limits (never the tracked row). A pair's
		/// second value (a diastolic) takes the range's second limits when it gives them, else keeps the concept's own.
		public static HospitalConcept Effective(HospitalConcept c, HospitalConceptRange? r)
		{
			if (r == null) return c;
			return new HospitalConcept
			{
				ID = c.ID, CompanyId = c.CompanyId, Code = c.Code, CodeSystem = c.CodeSystem, Name = c.Name, NameEn = c.NameEn, ValueType = c.ValueType, Unit = c.Unit,
				Decimals = c.Decimals, NormalLow = r.NormalLow, NormalHigh = r.NormalHigh, NormalHigh2 = r.NormalHigh2 ?? c.NormalHigh2, CriticalLow = r.CriticalLow, CriticalHigh = r.CriticalHigh,
				CriticalHigh2 = r.CriticalHigh2 ?? c.CriticalHigh2, NormalText = c.NormalText, NormalTextEn = c.NormalTextEn, CriticalText = c.CriticalText, CriticalTextEn = c.CriticalTextEn,
				Choices = c.Choices, Calculator = c.Calculator, CalcInputs = c.CalcInputs, HasLaterality = c.HasLaterality, LabAnalyteCode = c.LabAnalyteCode,
				IsActive = c.IsActive, UpdatedAt = c.UpdatedAt, UpdatedByEmployeeId = c.UpdatedByEmployeeId,
			};
		}

		/// Why a range cannot be saved (null = it can).
		public static string? Refusal(HospitalConceptRangeInput i, HospitalConcept? c)
		{
			if (c == null) return "not_found";
			if (c.ValueType is not (HospitalConceptType.Number or HospitalConceptType.Score or HospitalConceptType.Panel)) return "A range applies to a measured number";
			if (i.Sex != null && i.Sex is not ("M" or "F")) return "Choose the sex";
			if (i.Pregnancy != null && !HospitalRangePregnancy.All.Contains(i.Pregnancy)) return "Choose the pregnancy state";
			if (i.Pregnancy is not null and not HospitalRangePregnancy.NotPregnant && i.Sex == "M") return "A pregnancy range is for a female patient";
			if (i.AgeUnit is not ("days" or "months" or "years") && (i.AgeFrom != null || i.AgeTo != null)) return "Choose the age unit";
			int? from = ToDays(i.AgeFrom, i.AgeUnit), to = ToDays(i.AgeTo, i.AgeUnit);
			if (from is < 0 || to is < 0 || from > MaxAgeDays || to > MaxAgeDays) return "The age is out of range";
			if (from != null && to != null && to <= from) return "The age band must end after it starts";
			if (i.Sex == null && i.Pregnancy == null && from == null && to == null) return "Say who the range is for: a sex, an age band or a pregnancy";
			if (i.NormalLow == null && i.NormalHigh == null && i.CriticalLow == null && i.CriticalHigh == null) return "Give at least one limit";
			if (i.NormalLow is decimal nl && i.NormalHigh is decimal nh && nl >= nh) return "The normal range's low must be below its high";
			if (i.CriticalLow is decimal cl && (i.NormalLow ?? i.NormalHigh) is decimal n1 && cl >= n1) return "A critical low lies below the normal range";
			if (i.CriticalHigh is decimal ch && (i.NormalHigh ?? i.NormalLow) is decimal n2 && ch <= n2) return "A critical high lies above the normal range";
			if ((i.NormalHigh2 != null || i.CriticalHigh2 != null) && c.ValueType != HospitalConceptType.Panel) return "A second number's limits are for a pair of values";
			if (i.NormalHigh2 is decimal n2h && i.CriticalHigh2 is decimal c2h && c2h <= n2h) return "A critical high lies above the normal range";
			if ((i.Source?.Length ?? 0) > 200) return "A text is too long";
			return null;
		}
	}

	public sealed class HospitalConceptRangeService
	{
		public const string NoTables = "The patient-specific ranges are not installed yet (hospital_h115_concept_ranges.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalConceptRangeService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		/// H116: for READING ranges only (the alert escalation); saving or removing through it is refused.
		public HospitalConceptRangeService(CrossDbContext db) { _db = db; _access = null!; }
		private bool ReadOnly => _access == null;
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<HospitalConceptRange> Q(BusinessContext ctx) => _db.Set<HospitalConceptRange>().Where(r => r.CompanyId == ctx.CompanyId);

		/// The patient's sex, age in days and trimester on a date (the open pregnancy's gestational age).
		public async Task<HospitalPatientFacts> FactsAsync(BusinessContext ctx, HospitalPatient p, DateTime onUtc, CancellationToken ct = default)
		{
			int? age = p.BirthDate is DateTime b && b.Date <= onUtc.Date ? (onUtc.Date - b.Date).Days : null;
			int? tri = null;
			if (p.Sex == HospitalPatientSex.Female)
			{
				try
				{
					var preg = await _db.Set<HospitalPregnancy>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.PatientId == p.ID && x.Status == HospitalPregnancyStatus.Active)
						.OrderByDescending(x => x.ID).Select(x => new { x.Lmp, x.UltrasoundEdd }).FirstOrDefaultAsync(ct);
					if (preg != null && (preg.Lmp != null || preg.UltrasoundEdd != null))
					{
						int ga = HospitalAntenatalRules.GaDays(HospitalAntenatalRules.Edd(preg.Lmp, preg.UltrasoundEdd), onUtc);
						if (ga >= 0 && ga < 45 * 7) tri = HospitalAntenatalRules.Trimester(ga);   // past 45 weeks it is a record left open, not a pregnancy
					}
				}
				catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
			}
			return new HospitalPatientFacts(p.Sex, age, tri);
		}

		/// The concepts as they read for this patient on this date (copies where a range fits; the rest untouched).
		public async Task<List<HospitalConcept>> ForPatientAsync(BusinessContext ctx, IReadOnlyList<HospitalConcept> concepts, HospitalPatient p, DateTime onUtc, CancellationToken ct = default)
		{
			var ids = concepts.Select(c => c.ID).ToList();
			List<HospitalConceptRange> ranges;
			try { ranges = await Q(ctx).AsNoTracking().Where(r => ids.Contains(r.ConceptId)).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return concepts.ToList(); }
			if (ranges.Count == 0) return concepts.ToList();
			var facts = await FactsAsync(ctx, p, onUtc, ct);
			var by = ranges.GroupBy(r => r.ConceptId).ToDictionary(g => g.Key, g => g.ToList());
			return concepts.Select(c => by.TryGetValue(c.ID, out var rs) ? HospitalRangeRules.Effective(c, HospitalRangeRules.Pick(rs, facts)) : c).ToList();
		}

		public async Task<(IReadOnlyList<HospitalConceptRange> Rows, bool Ready)> ListAsync(BusinessContext ctx, int conceptId, CancellationToken ct = default)
		{
			try { return (await Q(ctx).AsNoTracking().Where(r => r.ConceptId == conceptId).OrderBy(r => r.Sex).ThenBy(r => r.Pregnancy).ThenBy(r => r.AgeFromDays).ThenBy(r => r.ID).ToListAsync(ct), true); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalConceptRange>(), false); }
		}

		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, int conceptId, int? id, HospitalConceptRangeInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (ReadOnly) return HospitalResult.Fail("forbidden");
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var c = await _db.Set<HospitalConcept>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == conceptId && x.CompanyId == ctx.CompanyId, ct);
				if (HospitalRangeRules.Refusal(i, c) is string why) return HospitalResult.Fail(why);
				int? from = HospitalRangeRules.ToDays(i.AgeFrom, i.AgeUnit), to = HospitalRangeRules.ToDays(i.AgeTo, i.AgeUnit);
				var all = await Q(ctx).Where(r => r.ConceptId == conceptId).ToListAsync(ct);
				// two ranges for exactly the same people would make the choice between them arbitrary
				if (all.Any(r => r.ID != id && r.Sex == i.Sex && r.Pregnancy == i.Pregnancy && r.AgeFromDays == from && r.AgeToDays == to))
					return HospitalResult.Fail("This concept already has a range for the same patients");
				HospitalConceptRange row;
				if (id is int rid)
				{
					row = all.FirstOrDefault(r => r.ID == rid) ?? throw new InvalidOperationException();
				}
				else { row = new HospitalConceptRange { CompanyId = ctx.CompanyId, ConceptId = conceptId }; _db.Add(row); }
				row.Sex = i.Sex; row.AgeFromDays = from; row.AgeToDays = to; row.Pregnancy = i.Pregnancy;
				row.NormalLow = i.NormalLow; row.NormalHigh = i.NormalHigh; row.CriticalLow = i.CriticalLow; row.CriticalHigh = i.CriticalHigh;
				row.NormalHigh2 = i.NormalHigh2; row.CriticalHigh2 = i.CriticalHigh2;
				row.Source = string.IsNullOrWhiteSpace(i.Source) ? null : i.Source.Trim();
				row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (InvalidOperationException) { return HospitalResult.Fail("not_found"); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RemoveAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (ReadOnly) return HospitalResult.Fail("forbidden");
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				int n = await Q(ctx).Where(r => r.ID == id).ExecuteDeleteAsync(ct);
				return n == 0 ? HospitalResult.Fail("not_found") : HospitalResult.Success(id);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}
	}
}
