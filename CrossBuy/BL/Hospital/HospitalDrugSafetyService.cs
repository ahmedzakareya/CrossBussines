using System.Globalization;
using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H73 — drug interactions and dose limits. The pure rules match a medicine's name against the knowledge
	// base's words, read a written dose ("1 g", "500mg", "٥٠٠ مجم" is not read — the order screen writes Latin units),
	// and compare it to the limit in the limit's own unit. The service checks an order against the patient's medicines
	// still running, records the alerts a doctor went past, and lets the pharmacy keep the knowledge base.
	// ==========================================================================================

	/// An alert: its kind, how severe, whether it stops the order, and what to say — as a resource key with values
	/// (the screen says it in the reader's language); an interaction carries its effect in both languages.
	public sealed record HospitalDrugAlert(string Kind, string Severity, bool Blocks, string Key, IReadOnlyList<string> Args, string? EffectAr, string? EffectEn, string Detail);

	public sealed class HospitalInteractionInput
	{
		public int? Id { get; set; }
		public string? DrugA { get; set; }
		public string? DrugB { get; set; }
		public string? Severity { get; set; }
		public string? EffectAr { get; set; }
		public string? EffectEn { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalDoseLimitInput
	{
		public int? Id { get; set; }
		public string? Drug { get; set; }
		public string? Route { get; set; }
		public string? Unit { get; set; }
		public decimal? MaxSingle { get; set; }
		public decimal? MaxDaily { get; set; }
		public string? Note { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed record HospitalOverrideRow(HospitalAlertOverride Row, string Patient, string PatientEn, string Mrn, string? By);

	public static class HospitalDrugSafetyRules
	{
		/// A knowledge-base word: trimmed, lower-case, single spaces.
		public static string Key(string? s) => Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

		/// The pair in the one order it is stored in.
		public static (string A, string B) Pair(string a, string b)
		{
			var (x, y) = (Key(a), Key(b));
			return string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
		}

		/// A medicine's name mentions a knowledge-base word (three letters at least, so "a" never matches everything).
		public static bool Mentions(string drugName, string word) => word.Length >= 3 && (drugName ?? "").Contains(word, StringComparison.OrdinalIgnoreCase);

		/// The interactions of a medicine with each of the others: (the other medicine, the interaction).
		public static IReadOnlyList<(string Other, HospitalDrugInteraction Hit)> Interactions(string drug, IEnumerable<string> others, IEnumerable<HospitalDrugInteraction> kb)
		{
			var list = new List<(string, HospitalDrugInteraction)>();
			var rules = kb.Where(k => k.IsActive).ToList();
			foreach (var o in others.Where(o => !string.IsNullOrWhiteSpace(o)).Distinct(StringComparer.OrdinalIgnoreCase))
				foreach (var r in rules)
					if ((Mentions(drug, r.DrugA) && Mentions(o, r.DrugB)) || (Mentions(drug, r.DrugB) && Mentions(o, r.DrugA)))
						list.Add((o, r));
			return list.OrderBy(x => Array.IndexOf(HospitalInteractionSeverity.All.ToArray(), x.Item2.Severity)).ToList();
		}

		private static readonly Regex DoseText = new(@"^\s*(\d+(?:[.,]\d+)?)\s*(mg|mcg|µg|ug|g|gm|gram|grams|units?|iu|ml)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		/// A written dose as an amount and a unit ("1 g" → 1 g; "500mg" → 500 mg; "10 units"); null when it cannot be read.
		public static (decimal Amount, string Unit)? ParseDose(string? dose)
		{
			var m = DoseText.Match(dose ?? "");
			if (!m.Success) return null;
			var amount = decimal.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
			var u = m.Groups[2].Value.ToLowerInvariant() switch
			{
				"mcg" or "µg" or "ug" => HospitalDoseUnit.Mcg,
				"g" or "gm" or "gram" or "grams" => HospitalDoseUnit.G,
				"unit" or "units" or "iu" => HospitalDoseUnit.Units,
				"ml" => HospitalDoseUnit.Ml,
				_ => HospitalDoseUnit.Mg,
			};
			return (amount, u);
		}

		/// An amount in another unit: grams, milligrams and micrograms convert among themselves; units and ml only to themselves.
		public static decimal? Convert(decimal amount, string from, string to)
		{
			if (from == to) return amount;
			static decimal? ToMg(decimal a, string u) => u switch { HospitalDoseUnit.G => a * 1000m, HospitalDoseUnit.Mg => a, HospitalDoseUnit.Mcg => a / 1000m, _ => null };
			var mg = ToMg(amount, from);
			if (mg == null) return null;
			return to switch { HospitalDoseUnit.G => mg / 1000m, HospitalDoseUnit.Mg => mg, HospitalDoseUnit.Mcg => mg * 1000m, _ => null };
		}

		/// The limits a dose goes over: its single dose, and its day (single × the doses a day; an as-needed order has no day).
		public static IReadOnlyList<(HospitalDoseLimit Limit, bool Daily, decimal Value)> DoseHits(string drug, string? dose, string route, int? dosesPerDay, IEnumerable<HospitalDoseLimit> limits)
		{
			var hits = new List<(HospitalDoseLimit, bool, decimal)>();
			if (ParseDose(dose) is not var (amount, unit)) return hits;
			foreach (var l in limits.Where(l => l.IsActive && Mentions(drug, l.Drug) && (l.Route == null || l.Route == route)))
			{
				if (Convert(amount, unit, l.Unit) is not decimal single) continue;
				if (l.MaxSingle is decimal ms && single > ms) hits.Add((l, false, single));
				if (l.MaxDaily is decimal md && dosesPerDay is int n && single * n > md) hits.Add((l, true, single * n));
			}
			return hits;
		}

		public static string? InteractionRefusal(HospitalInteractionInput i)
		{
			var (a, b) = (Key(i.DrugA), Key(i.DrugB));
			if (a.Length < 3 || b.Length < 3) return "Write both medicines (three letters at least)";
			if (a == b) return "Write two different medicines";
			if (a.Length > 80 || b.Length > 80) return "The medicine's name is too long";
			if (!HospitalInteractionSeverity.All.Contains(i.Severity ?? "")) return "Choose the severity";
			if (string.IsNullOrWhiteSpace(i.EffectAr) || string.IsNullOrWhiteSpace(i.EffectEn)) return "Write the effect in Arabic and in English";
			return null;
		}

		public static string? LimitRefusal(HospitalDoseLimitInput i)
		{
			if (Key(i.Drug).Length < 3) return "Write the medicine (three letters at least)";
			if (!HospitalDoseUnit.All.Contains(i.Unit ?? "")) return "Choose the unit";
			if (!string.IsNullOrEmpty(i.Route) && !HospitalMedRoute.All.Contains(i.Route)) return "Choose the route";
			if (i.MaxSingle is null && i.MaxDaily is null) return "Write a single or a daily limit";
			if (i.MaxSingle is <= 0 || i.MaxDaily is <= 0) return "A limit is above zero";
			if (i.MaxSingle is decimal s && i.MaxDaily is decimal d && d < s) return "The daily limit cannot be below the single one";
			return null;
		}

		public static string Num(decimal d) => d.ToString("0.###", CultureInfo.InvariantCulture);
	}

	public sealed class HospitalDrugSafetyService
	{
		public const string NoTables = "Drug safety is not installed yet (hospital_h73_drug_safety.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalDrugSafetyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		/// The active knowledge base — empty when not installed.
		public static async Task<(List<HospitalDrugInteraction> Pairs, List<HospitalDoseLimit> Limits)> KbAsync(CrossDbContext db, int companyId, CancellationToken ct)
		{
			try
			{
				return (await db.Set<HospitalDrugInteraction>().AsNoTracking().Where(k => k.CompanyId == companyId && k.IsActive).ToListAsync(ct),
						await db.Set<HospitalDoseLimit>().AsNoTracking().Where(k => k.CompanyId == companyId && k.IsActive).ToListAsync(ct));
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), new()); }
		}

		/// What a new hospital order meets: interactions with the admission's medicines still running, and its dose limits.
		/// `alsoRunning`: medicines about to start with this one (the earlier lines of an order set, H74).
		public async Task<IReadOnlyList<HospitalDrugAlert>> CheckOrderAsync(BusinessContext ctx, int admissionId, string drug, string? dose, string route, string? hours, bool asNeeded, CancellationToken ct = default, IEnumerable<string>? alsoRunning = null)
		{
			var (pairs, limits) = await KbAsync(_db, ctx.CompanyId, ct);
			if (pairs.Count == 0 && limits.Count == 0) return Array.Empty<HospitalDrugAlert>();
			var running = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.AdmissionId == admissionId && o.Status == HospitalMedOrderStatus.Active)
				.Select(o => o.Drug).ToListAsync(ct);
			if (alsoRunning != null) running.AddRange(alsoRunning);
			var alerts = new List<HospitalDrugAlert>();
			foreach (var (other, hit) in HospitalDrugSafetyRules.Interactions(drug, running, pairs))
				alerts.Add(new(HospitalAlertKind.Interaction, hit.Severity, HospitalInteractionSeverity.Blocks(hit.Severity), "{0} with {1}", new[] { drug, other }, hit.EffectAr, hit.EffectEn,
					$"{hit.DrugA} + {hit.DrugB} — {hit.Severity}"));
			int? perDay = asNeeded ? null : HospitalNursingRules.ParseHours(hours)?.Count;
			foreach (var (limit, daily, value) in HospitalDrugSafetyRules.DoseHits(drug, dose, route, perDay, limits))
			{
				var max = daily ? limit.MaxDaily!.Value : limit.MaxSingle!.Value;
				alerts.Add(new(HospitalAlertKind.Dose, HospitalInteractionSeverity.Major, true, daily ? "{0}: {1} {2} a day is above the daily limit of {3} {2}" : "{0}: {1} {2} is above the single-dose limit of {3} {2}",
					new[] { drug, HospitalDrugSafetyRules.Num(value), limit.Unit, HospitalDrugSafetyRules.Num(max) }, null, null,
					$"{limit.Drug} {(daily ? "daily" : "single")} {HospitalDrugSafetyRules.Num(value)} > {HospitalDrugSafetyRules.Num(max)} {limit.Unit}"));
			}
			return alerts;
		}

		/// Keeps the stopping alerts a doctor went past with their reason.
		public async Task RecordOverridesAsync(BusinessContext ctx, int patientId, int? admissionId, string sourceType, int sourceId, IEnumerable<HospitalDrugAlert> alerts, string reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				foreach (var a in alerts.Where(a => a.Blocks))
					_db.Add(new HospitalAlertOverride
					{
						CompanyId = ctx.CompanyId, PatientId = patientId, AdmissionId = admissionId, SourceType = sourceType, SourceId = sourceId, Kind = a.Kind,
						Detail = Clip(a.Detail, 300)!, Reason = Clip(reason, 500)!, At = nowUtc, ByEmployeeId = employeeId,
					});
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { }
		}

		// ---------------- the knowledge base (the pharmacy keeps it) ----------------

		private Task<bool> CanKeepAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Manage, null, ct);

		public async Task<HospitalResult> SaveInteractionAsync(BusinessContext ctx, HospitalInteractionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanKeepAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalDrugSafetyRules.InteractionRefusal(input) is string why) return HospitalResult.Fail(why);
			var (a, b) = HospitalDrugSafetyRules.Pair(input.DrugA!, input.DrugB!);
			try
			{
				var set = _db.Set<HospitalDrugInteraction>();
				if (await set.AnyAsync(x => x.CompanyId == ctx.CompanyId && x.DrugA == a && x.DrugB == b && x.ID != (input.Id ?? 0), ct)) return HospitalResult.Fail("This pair is already in the list");
				HospitalDrugInteraction? row;
				if (input.Id is int id) { row = await set.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct); if (row == null) return HospitalResult.Fail("not_found"); }
				else { row = new HospitalDrugInteraction { CompanyId = ctx.CompanyId }; set.Add(row); }
				row.DrugA = a; row.DrugB = b; row.Severity = input.Severity!; row.EffectAr = Clip(input.EffectAr, 400)!; row.EffectEn = Clip(input.EffectEn, 400)!;
				row.IsActive = input.IsActive; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> SaveLimitAsync(BusinessContext ctx, HospitalDoseLimitInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanKeepAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalDrugSafetyRules.LimitRefusal(input) is string why) return HospitalResult.Fail(why);
			try
			{
				var set = _db.Set<HospitalDoseLimit>();
				HospitalDoseLimit? row;
				if (input.Id is int id) { row = await set.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct); if (row == null) return HospitalResult.Fail("not_found"); }
				else { row = new HospitalDoseLimit { CompanyId = ctx.CompanyId }; set.Add(row); }
				row.Drug = HospitalDrugSafetyRules.Key(input.Drug); row.Route = string.IsNullOrEmpty(input.Route) ? null : input.Route; row.Unit = input.Unit!;
				row.MaxSingle = input.MaxSingle; row.MaxDaily = input.MaxDaily; row.Note = Clip(input.Note, 300); row.IsActive = input.IsActive; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<(List<HospitalDrugInteraction> Rows, int Total, bool Ready)> InteractionsAsync(BusinessContext ctx, string? q, string? severity, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var rows = _db.Set<HospitalDrugInteraction>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId);
				if (!string.IsNullOrEmpty(severity)) rows = rows.Where(x => x.Severity == severity);
				foreach (var t in (q ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
				{
					var term = t.Trim();
					rows = rows.Where(x => x.DrugA.Contains(term) || x.DrugB.Contains(term) || x.EffectAr.Contains(term) || x.EffectEn.Contains(term));
				}
				int total = await rows.CountAsync(ct);
				var list = await rows.OrderBy(x => x.DrugA).ThenBy(x => x.DrugB).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				return (list, total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), 0, false); }
		}

		public async Task<(List<HospitalDoseLimit> Rows, int Total)> LimitsAsync(BusinessContext ctx, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var rows = _db.Set<HospitalDoseLimit>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId);
				foreach (var t in (q ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)) { var term = t.Trim(); rows = rows.Where(x => x.Drug.Contains(term) || (x.Note != null && x.Note.Contains(term))); }
				int total = await rows.CountAsync(ct);
				return (await rows.OrderBy(x => x.Drug).ThenBy(x => x.Route).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct), total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), 0); }
		}

		public async Task<(List<HospitalOverrideRow> Rows, int Total)> OverridesAsync(BusinessContext ctx, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var rows = from o in _db.Set<HospitalAlertOverride>().AsNoTracking()
						   join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
						   where o.CompanyId == ctx.CompanyId
						   select new { o, p.Name, p.NameEn, p.Mrn };
				foreach (var t in (q ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
				{ var term = t.Trim(); rows = rows.Where(x => x.Name.Contains(term) || x.NameEn.Contains(term) || x.Mrn.Contains(term) || x.o.Detail.Contains(term) || x.o.Reason.Contains(term)); }
				int total = await rows.CountAsync(ct);
				var list = await rows.OrderByDescending(x => x.o.At).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var ids = list.Where(x => x.o.ByEmployeeId != null).Select(x => x.o.ByEmployeeId!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return (list.Select(x => new HospitalOverrideRow(x.o, x.Name, x.NameEn, x.Mrn, x.o.ByEmployeeId is int b ? people.GetValueOrDefault(b) : null)).ToList(), total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), 0); }
		}

		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
	}
}
