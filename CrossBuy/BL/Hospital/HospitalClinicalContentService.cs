using System.Globalization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H93 — the concept dictionary and the template designer (the approved prototype screens sc-concepts and
	// sc-designer). The dictionary is edited by the hospital administrator; a template is built from it, VERSIONED:
	//   * a template has at most one DRAFT and one PUBLISHED version; only the draft takes changes;
	//   * "new version" copies the published version into a new draft (its conditional links remapped);
	//   * publishing freezes the draft, and the version it replaces becomes RETIRED — a visit taken on it still renders
	//     with it, because a form keeps the version id it was filled on.
	// ==========================================================================================
	public sealed class HospitalConceptInput
	{
		public int? Id { get; set; }
		public string? Code { get; set; }
		public string? CodeSystem { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public string? ValueType { get; set; }
		public string? Unit { get; set; }
		public int Decimals { get; set; } = 1;
		public decimal? NormalLow { get; set; }
		public decimal? NormalHigh { get; set; }
		public decimal? NormalHigh2 { get; set; }
		public decimal? CriticalLow { get; set; }
		public decimal? CriticalHigh { get; set; }
		public decimal? CriticalHigh2 { get; set; }
		public string? NormalText { get; set; }
		public string? NormalTextEn { get; set; }
		public string? CriticalText { get; set; }
		public string? CriticalTextEn { get; set; }
		public string? Choices { get; set; }
		public string? Calculator { get; set; }
		public string? CalcInputs { get; set; }
		public bool HasLaterality { get; set; }
		public string? LabAnalyteCode { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalTemplateInput
	{
		public string? Code { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public int? DepartmentId { get; set; }
	}

	public sealed class HospitalTemplateFieldInput
	{
		public int? ConceptId { get; set; }
		public string? Section { get; set; }
		public string? SectionEn { get; set; }
		public string? Label { get; set; }
		public string? LabelEn { get; set; }
		public bool Required { get; set; }
		public string? ValueSource { get; set; }
		public int? LookbackDays { get; set; }
		public int? ShowWhenFieldId { get; set; }
		public decimal? ShowWhenAbove { get; set; }
		public string? Note { get; set; }
		public string? NoteEn { get; set; }
		public decimal? AlertAbove { get; set; }
		public decimal? AlertBelow { get; set; }
		public string? AlertText { get; set; }
		public string? AlertTextEn { get; set; }
		public int? CareGapDays { get; set; }
		public bool ShowTrend { get; set; }
		public bool AlertNotify { get; set; }
		public int? AlertTemplateId { get; set; }
		public string? AlertRange { get; set; }
	}

	public sealed record HospitalConceptRow(HospitalConcept Concept, IReadOnlyList<string> UsedBy, IReadOnlyList<string> UsedByEn);
	public sealed record HospitalTemplateCard(HospitalClinicalTemplate Template, string? Clinic, string? ClinicEn, HospitalClinicalTemplateVersion? Current, bool HasDraft, int Visits);
	public sealed record HospitalTemplateFieldView(HospitalClinicalTemplateField Field, HospitalConcept Concept, HospitalClinicalTemplateField? ShowWhen);
	public sealed record HospitalDesignerModel(IReadOnlyList<HospitalTemplateCard> Templates, HospitalTemplateCard? Selected, HospitalClinicalTemplateVersion? Version,
		IReadOnlyList<HospitalTemplateFieldView> Fields, HospitalTemplateFieldView? Field, IReadOnlyList<HospitalClinicalTemplateVersion> Versions,
		IReadOnlyList<HospitalConcept> Concepts, IReadOnlyList<HospitalDepartment> Clinics, bool Ready);

	public static class HospitalConceptRules
	{
		/// A value at the concept's own precision, always ("8.0%", never "8%").
		public static string Num(decimal v, int dec) => Math.Round(v, Math.Clamp(dec, 0, 4)).ToString("F" + Math.Clamp(dec, 0, 4), CultureInfo.InvariantCulture);
		private static string N(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);

		/// The normal range as the dictionary shows it: "4–5.6", "≤ 21", "≥ 90", "≤ 130/80" — or the written text.
		public static string NormalDisplay(HospitalConcept c, bool ar)
		{
			var text = ar ? c.NormalText ?? c.NormalTextEn : c.NormalTextEn ?? c.NormalText;
			if (!string.IsNullOrWhiteSpace(text)) return text!;
			string hi2 = c.NormalHigh2 is decimal h2 ? "/" + N(h2) : "";
			if (c.NormalLow is decimal lo && c.NormalHigh is decimal hi) return N(lo) + "–" + N(hi) + hi2;
			if (c.NormalHigh is decimal h) return "≤ " + N(h) + hi2;
			if (c.NormalLow is decimal l) return "≥ " + N(l);
			return "—";
		}

		/// The critical limits: "≥ 10", "≤ 15", "≥ 180/120", "≤ 2.5 · ≥ 6.5".
		public static string CriticalDisplay(HospitalConcept c, bool ar)
		{
			var text = ar ? c.CriticalText ?? c.CriticalTextEn : c.CriticalTextEn ?? c.CriticalText;
			if (!string.IsNullOrWhiteSpace(text)) return text!;
			var parts = new List<string>();
			if (c.CriticalLow is decimal lo) parts.Add("≤ " + N(lo));
			if (c.CriticalHigh is decimal hi) parts.Add("≥ " + N(hi) + (c.CriticalHigh2 is decimal h2 ? "/" + N(h2) : ""));
			return parts.Count == 0 ? "—" : string.Join(" · ", parts);
		}

		/// The flag of a value against the concept's limits (a panel's second number against the …2 limits).
		public static string Flag(HospitalConcept c, decimal? v, decimal? v2 = null)
		{
			// no number, or no limit to hold it against: "not known", never "normal" (TARGET's review, 2026-11-11)
			if (v == null) return HospitalObsFlag.Unknown;
			if (c.NormalLow == null && c.NormalHigh == null && c.NormalHigh2 == null && c.CriticalLow == null && c.CriticalHigh == null && c.CriticalHigh2 == null) return HospitalObsFlag.Unknown;
			if (c.CriticalHigh is decimal ch && v >= ch || c.CriticalHigh2 is decimal ch2 && v2 >= ch2) return HospitalObsFlag.CriticalHigh;
			if (c.CriticalLow is decimal cl && v <= cl) return HospitalObsFlag.CriticalLow;
			if (c.NormalHigh is decimal nh && v > nh || c.NormalHigh2 is decimal nh2 && v2 > nh2) return HospitalObsFlag.High;
			if (c.NormalLow is decimal nl && v < nl) return HospitalObsFlag.Low;
			return HospitalObsFlag.Normal;
		}

		/// A choice's answers: one per line, code|Arabic|English.
		public static IReadOnlyList<(string Code, string Ar, string En)> Choices(string? raw) =>
			(raw ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(l => l.Split('|')).Where(p => p.Length >= 1 && p[0].Trim().Length > 0)
				.Select(p => (p[0].Trim(), (p.Length > 1 ? p[1] : p[0]).Trim(), (p.Length > 2 ? p[2] : p.Length > 1 ? p[1] : p[0]).Trim())).ToList();

		/// H98: the band a score falls in (the band with the highest lowest-value not above it); null with no bands.
		public static (string Ar, string En)? Band(HospitalConcept c, decimal? v)
		{
			if (c.ValueType != HospitalConceptType.Score || v == null) return null;
			var b = Choices(c.Choices).Select(x => (Min: decimal.TryParse(x.Code, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : decimal.MinValue, x.Ar, x.En))
				.Where(x => x.Min <= v).OrderByDescending(x => x.Min).FirstOrDefault();
			return b.Ar == null ? null : (b.Ar, b.En);
		}

		public static IReadOnlyList<string> Inputs(string? raw) => (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		public static string? Refusal(HospitalConceptInput i)
		{
			if (string.IsNullOrWhiteSpace(i.Code) || i.Code.Trim().Length > 30) return "Write the concept's code";
			if (!HospitalCodeSystem.All.Contains(i.CodeSystem ?? "")) return "Choose the code system";
			if (string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.NameEn)) return "Write the name, in Arabic and in English";
			if (!HospitalConceptType.All.Contains(i.ValueType ?? "")) return "Choose the value type";
			if (i.Decimals is < 0 or > 4) return "Decimals are between 0 and 4";
			if (i.NormalLow > i.NormalHigh) return "The normal low is above the normal high";
			if (i.CriticalLow != null && i.NormalLow != null && i.CriticalLow > i.NormalLow) return "The critical low must be at or below the normal range";
			if (i.CriticalHigh != null && i.NormalHigh != null && i.CriticalHigh < i.NormalHigh) return "The critical high must be at or above the normal range";
			if (i.ValueType == HospitalConceptType.Choice && Choices(i.Choices).Count < 2) return "A choice needs at least two answers";
			// H98: a score's bands — one per line, "lowest value|Arabic|English" (PHQ-9: 0 none, 5 mild, 10 moderate …)
			if (i.ValueType == HospitalConceptType.Score && !string.IsNullOrWhiteSpace(i.Choices) && Choices(i.Choices).Any(b => !decimal.TryParse(b.Code, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
				return "A score's band starts with the lowest value of the band";
			if (i.ValueType == HospitalConceptType.Calculated && !HospitalConceptCalculator.All.Contains(i.Calculator ?? "")) return "Choose the calculator";
			// a score is typed as its total (ACT), or summed from its items — never another calculator
			if (i.ValueType == HospitalConceptType.Score && !string.IsNullOrWhiteSpace(i.Calculator) && i.Calculator != HospitalConceptCalculator.Sum) return "A score is the sum of its items: choose Sum";
			if (!string.IsNullOrWhiteSpace(i.Calculator) && !HospitalConceptCalculator.All.Contains(i.Calculator)) return "Choose the calculator";
			if (!string.IsNullOrWhiteSpace(i.Calculator) && Inputs(i.CalcInputs).Count == 0) return "List the codes the calculator reads";
			// H188: a calculator reads its inputs by position — exactly as many as it needs (a sum: any number)
			if (!string.IsNullOrWhiteSpace(i.Calculator) && i.Calculator != HospitalConceptCalculator.Sum && Inputs(i.CalcInputs).Count != HospitalConceptCalculator.InputsNeeded(i.Calculator))
				return HospitalConceptCalculator.InputsNeeded(i.Calculator) switch { 1 => "This calculator reads one code", 2 => "This calculator reads two codes, in order", _ => "This calculator reads sixteen codes, in order: the four regions, each as area, erythema, induration, scaling" };
			return null;
		}
	}

	public static class HospitalTemplateRules
	{
		public static string? CreateRefusal(HospitalTemplateInput i)
		{
			if (string.IsNullOrWhiteSpace(i.Code) || i.Code.Trim().Length > 20) return "Write the template's code";
			if (string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.NameEn)) return "Write the name, in Arabic and in English";
			return null;
		}

		public static string? FieldRefusal(HospitalTemplateFieldInput i, HospitalConcept? c, IReadOnlyList<HospitalClinicalTemplateField> siblings, int? selfId)
		{
			if (c == null || !c.IsActive) return "Choose a concept from the dictionary";
			if (string.IsNullOrWhiteSpace(i.Section) || string.IsNullOrWhiteSpace(i.SectionEn)) return "Write the section, in Arabic and in English";
			if (!HospitalValueSource.All.Contains(i.ValueSource ?? HospitalValueSource.Manual)) return "Choose where the value comes from";
			if (i.ValueSource == HospitalValueSource.LastLab && string.IsNullOrWhiteSpace(c.LabAnalyteCode)) return "This concept is not linked to a lab test";
			if (i.LookbackDays is < 1 or > 3650 || i.CareGapDays is < 1 or > 3650) return "Days are between 1 and 3650";
			if (i.ShowWhenFieldId is int w)
			{
				if (w == selfId || !siblings.Any(f => f.ID == w)) return "A field can only depend on another field of the same template";
				if (i.ShowWhenAbove == null) return "Write the value the other field must be above";
			}
			if (i.AlertAbove != null && i.AlertBelow != null && i.AlertBelow >= i.AlertAbove) return "The low alert must be below the high alert";
			if (i.AlertRange != null && !HospitalAlertRange.All.Contains(i.AlertRange)) return "Choose when the alert fires";
			if (i.AlertRange != null && c.ValueType is not (HospitalConceptType.Number or HospitalConceptType.Panel or HospitalConceptType.Score or HospitalConceptType.Calculated))
				return "An alert on the patient's range needs a measured number";
			bool anyRule = i.AlertAbove != null || i.AlertBelow != null || i.AlertRange != null;
			if (anyRule && (string.IsNullOrWhiteSpace(i.AlertText) || string.IsNullOrWhiteSpace(i.AlertTextEn))) return "Write the alert, in Arabic and in English";
			if ((i.AlertNotify || i.AlertTemplateId != null) && !anyRule) return "Notifying or opening a template needs the alert rule first";
			return null;
		}
	}

	public sealed class HospitalClinicalContentService
	{
		public const string NoTables = "The specialty clinic content is not installed yet (hospital_h93_clinical_content.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalClinicalContentService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private IQueryable<T> Q<T>(BusinessContext ctx) where T : class => _db.Set<T>().Where(x => EF.Property<int>(x, "CompanyId") == ctx.CompanyId);

		public Task<bool> CanManageAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Manage, null, ct);

		// ---------------- the dictionary ----------------
		public async Task<(IReadOnlyList<HospitalConceptRow> Rows, int Total, bool Ready)> ConceptsAsync(BusinessContext ctx, string? q, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var src = Q<HospitalConcept>(ctx);
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); src = src.Where(c => c.Code.Contains(s) || c.Name.Contains(s) || c.NameEn.Contains(s) || (c.Unit != null && c.Unit.Contains(s))); }
				int total = await src.CountAsync(ct);
				var rows = await src.OrderBy(c => c.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var ids = rows.Select(r => r.ID).ToList();
				// "used by" = the clinics of the templates whose current (published, else draft) version asks the concept
				var uses = await (from f in Q<HospitalClinicalTemplateField>(ctx)
								  join v in Q<HospitalClinicalTemplateVersion>(ctx) on f.VersionId equals v.ID
								  join t in Q<HospitalClinicalTemplate>(ctx) on v.TemplateId equals t.ID
								  where ids.Contains(f.ConceptId) && v.Status != HospitalTemplateStatus.Retired && t.IsActive
								  select new { f.ConceptId, t.DepartmentId }).Distinct().ToListAsync(ct);
				var deps = await _db.Set<HospitalDepartment>().Where(d => d.CompanyId == ctx.CompanyId).ToDictionaryAsync(d => d.ID, ct);
				return (rows.Select(c =>
				{
					var mine = uses.Where(u => u.ConceptId == c.ID).ToList();
					var ar = mine.Select(u => u.DepartmentId is int d && deps.TryGetValue(d, out var dep) ? dep.Name : "الكل").Distinct().OrderBy(x => x).ToList();
					var en = mine.Select(u => u.DepartmentId is int d && deps.TryGetValue(d, out var dep) ? dep.NameEn ?? dep.Name : "All").Distinct().OrderBy(x => x).ToList();
					return new HospitalConceptRow(c, ar, en);
				}).ToList(), total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalConceptRow>(), 0, false); }
		}

		public async Task<HospitalConcept?> ConceptAsync(BusinessContext ctx, int id, CancellationToken ct = default) => await Q<HospitalConcept>(ctx).FirstOrDefaultAsync(c => c.ID == id, ct);

		public async Task<HospitalResult> SaveConceptAsync(BusinessContext ctx, HospitalConceptInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalConceptRules.Refusal(i) is string why) return HospitalResult.Fail(why);
			try
			{
				var code = i.Code!.Trim();
				int self = i.Id ?? 0;   // computed outside the query: EF cannot translate a coalesce of the input against the key
				if (await Q<HospitalConcept>(ctx).AnyAsync(c => c.Code == code && c.ID != self, ct)) return HospitalResult.Fail("Another concept already has this code");
				// a calculator's inputs must already be in the dictionary
				foreach (var inp in HospitalConceptRules.Inputs(i.CalcInputs))
					if (inp != code && !await Q<HospitalConcept>(ctx).AnyAsync(c => c.Code == inp, ct)) return HospitalResult.Fail("A code the calculator reads is not in the dictionary: {0}", inp);
				var c = i.Id is int id ? await Q<HospitalConcept>(ctx).FirstOrDefaultAsync(x => x.ID == id, ct) : null;
				if (i.Id != null && c == null) return HospitalResult.Fail("not_found");
				if (c == null) { c = new HospitalConcept { CompanyId = ctx.CompanyId }; _db.Add(c); }
				else if (c.ValueType != i.ValueType && await Q<HospitalClinicalObservation>(ctx).AnyAsync(o => o.ConceptId == c.ID, ct))
					return HospitalResult.Fail("This concept already has recorded values: its type cannot change");
				c.Code = code; c.CodeSystem = i.CodeSystem!; c.Name = Clip(i.Name, 150)!; c.NameEn = Clip(i.NameEn, 150)!; c.ValueType = i.ValueType!;
				c.Unit = Clip(i.Unit, 30); c.Decimals = i.Decimals; c.NormalLow = i.NormalLow; c.NormalHigh = i.NormalHigh; c.NormalHigh2 = i.NormalHigh2;
				c.CriticalLow = i.CriticalLow; c.CriticalHigh = i.CriticalHigh; c.CriticalHigh2 = i.CriticalHigh2;
				c.NormalText = Clip(i.NormalText, 60); c.NormalTextEn = Clip(i.NormalTextEn, 60); c.CriticalText = Clip(i.CriticalText, 60); c.CriticalTextEn = Clip(i.CriticalTextEn, 60);
				c.Choices = i.ValueType is HospitalConceptType.Choice or HospitalConceptType.Score or HospitalConceptType.Calculated ? Clip(i.Choices, 2000) : null;   // H98: a score keeps its bands here; H188: a calculator names its result codes here ("TR4-FNA|…|…")
				c.Calculator = string.IsNullOrWhiteSpace(i.Calculator) ? null : i.Calculator; c.CalcInputs = c.Calculator == null ? null : Clip(i.CalcInputs, 200);
				c.HasLaterality = i.HasLaterality; c.LabAnalyteCode = Clip(i.LabAnalyteCode, 20); c.IsActive = i.IsActive;
				c.UpdatedAt = nowUtc; c.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------- templates ----------------
		public async Task<HospitalDesignerModel> DesignerAsync(BusinessContext ctx, int? templateId, int? versionId, int? fieldId, CancellationToken ct = default)
		{
			try
			{
				var templates = await Q<HospitalClinicalTemplate>(ctx).Where(t => t.IsActive).OrderBy(t => t.ID).ToListAsync(ct);
				var versions = await Q<HospitalClinicalTemplateVersion>(ctx).ToListAsync(ct);
				var visits = await Q<HospitalClinicalForm>(ctx).Join(Q<HospitalClinicalTemplateVersion>(ctx), f => f.TemplateVersionId, v => v.ID, (f, v) => v.TemplateId)
					.GroupBy(x => x).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				var clinics = await _db.Set<HospitalDepartment>().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync(ct);
				var cards = templates.Select(t =>
				{
					var mine = versions.Where(v => v.TemplateId == t.ID).ToList();
					var current = mine.FirstOrDefault(v => v.Status == HospitalTemplateStatus.Published) ?? mine.FirstOrDefault(v => v.Status == HospitalTemplateStatus.Draft);
					var dep = clinics.FirstOrDefault(d => d.ID == t.DepartmentId);
					return new HospitalTemplateCard(t, dep?.Name, dep?.NameEn ?? dep?.Name, current, mine.Any(v => v.Status == HospitalTemplateStatus.Draft), visits.GetValueOrDefault(t.ID));
				}).ToList();
				var sel = cards.FirstOrDefault(c => c.Template.ID == templateId) ?? cards.FirstOrDefault();
				var concepts = await Q<HospitalConcept>(ctx).OrderBy(c => c.Name).ToListAsync(ct);
				if (sel == null) return new HospitalDesignerModel(cards, null, null, Array.Empty<HospitalTemplateFieldView>(), null, Array.Empty<HospitalClinicalTemplateVersion>(), concepts, clinics, true);
				var history = versions.Where(v => v.TemplateId == sel.Template.ID).OrderByDescending(v => v.Version).ToList();
				// the version on the canvas: the one asked for, else the draft (where the work is), else the published one
				var ver = history.FirstOrDefault(v => v.ID == versionId) ?? history.FirstOrDefault(v => v.Status == HospitalTemplateStatus.Draft) ?? sel.Current;
				var fields = ver == null ? new List<HospitalTemplateFieldView>() : await FieldViewsAsync(ctx, ver.ID, concepts, ct);
				var field = fields.FirstOrDefault(f => f.Field.ID == fieldId) ?? fields.FirstOrDefault();
				return new HospitalDesignerModel(cards, sel, ver, fields, field, history, concepts, clinics, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex))
			{
				return new HospitalDesignerModel(Array.Empty<HospitalTemplateCard>(), null, null, Array.Empty<HospitalTemplateFieldView>(), null, Array.Empty<HospitalClinicalTemplateVersion>(), Array.Empty<HospitalConcept>(), Array.Empty<HospitalDepartment>(), false);
			}
		}

		public async Task<List<HospitalTemplateFieldView>> FieldViewsAsync(BusinessContext ctx, int versionId, IReadOnlyList<HospitalConcept>? concepts, CancellationToken ct)
		{
			var fs = await Q<HospitalClinicalTemplateField>(ctx).Where(f => f.VersionId == versionId).OrderBy(f => f.SortOrder).ThenBy(f => f.ID).ToListAsync(ct);
			var cids = fs.Select(f => f.ConceptId).Distinct().ToList();
			var cs = concepts?.Where(c => cids.Contains(c.ID)).ToDictionary(c => c.ID) ?? await Q<HospitalConcept>(ctx).Where(c => cids.Contains(c.ID)).ToDictionaryAsync(c => c.ID, ct);
			return fs.Select(f => new HospitalTemplateFieldView(f, cs[f.ConceptId], fs.FirstOrDefault(x => x.ID == f.ShowWhenFieldId))).ToList();
		}

		public async Task<HospitalResult> CreateTemplateAsync(BusinessContext ctx, HospitalTemplateInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalTemplateRules.CreateRefusal(i) is string why) return HospitalResult.Fail(why);
			try
			{
				var code = i.Code!.Trim().ToUpperInvariant();
				if (await Q<HospitalClinicalTemplate>(ctx).AnyAsync(t => t.Code == code, ct)) return HospitalResult.Fail("Another template already has this code");
				if (i.DepartmentId is int d && !await _db.Set<HospitalDepartment>().AnyAsync(x => x.ID == d && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
				var t = new HospitalClinicalTemplate { CompanyId = ctx.CompanyId, Code = code, Name = Clip(i.Name, 150)!, NameEn = Clip(i.NameEn, 150)!, DepartmentId = i.DepartmentId, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
				_db.Add(t);
				await _db.SaveChangesAsync(ct);
				_db.Add(new HospitalClinicalTemplateVersion { CompanyId = ctx.CompanyId, TemplateId = t.ID, Version = 1, Status = HospitalTemplateStatus.Draft, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId });
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(t.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// A new draft copied from the published version. Returns the draft's id.
		public async Task<HospitalResult> NewVersionAsync(BusinessContext ctx, int templateId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var vs = await Q<HospitalClinicalTemplateVersion>(ctx).Where(v => v.TemplateId == templateId).ToListAsync(ct);
				if (vs.Count == 0) return HospitalResult.Fail("not_found");
				if (vs.Any(v => v.Status == HospitalTemplateStatus.Draft)) return HospitalResult.Fail("This template already has a draft: finish or publish it first");
				var pub = vs.FirstOrDefault(v => v.Status == HospitalTemplateStatus.Published) ?? vs.OrderByDescending(v => v.Version).First();
				var draft = new HospitalClinicalTemplateVersion { CompanyId = ctx.CompanyId, TemplateId = templateId, Version = vs.Max(v => v.Version) + 1, Status = HospitalTemplateStatus.Draft, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId };
				_db.Add(draft);
				await _db.SaveChangesAsync(ct);
				var src = await Q<HospitalClinicalTemplateField>(ctx).Where(f => f.VersionId == pub.ID).OrderBy(f => f.SortOrder).ThenBy(f => f.ID).ToListAsync(ct);
				var map = new Dictionary<int, HospitalClinicalTemplateField>();
				foreach (var f in src)
				{
					var n = new HospitalClinicalTemplateField
					{
						CompanyId = ctx.CompanyId, VersionId = draft.ID, SortOrder = f.SortOrder, Section = f.Section, SectionEn = f.SectionEn, ConceptId = f.ConceptId, Label = f.Label, LabelEn = f.LabelEn,
						Required = f.Required, ValueSource = f.ValueSource, LookbackDays = f.LookbackDays, ShowWhenAbove = f.ShowWhenAbove, Note = f.Note, NoteEn = f.NoteEn,
						AlertAbove = f.AlertAbove, AlertBelow = f.AlertBelow, AlertText = f.AlertText, AlertTextEn = f.AlertTextEn, CareGapDays = f.CareGapDays,
						AlertRange = f.AlertRange,
					};
					n.ShowTrend = f.ShowTrend;
					n.AlertNotify = f.AlertNotify; n.AlertTemplateId = f.AlertTemplateId;
					map[f.ID] = n; _db.Add(n);
				}
				await _db.SaveChangesAsync(ct);
				// the conditions point at the COPIES, never back into the frozen version
				foreach (var f in src.Where(f => f.ShowWhenFieldId is int w && map.ContainsKey(w))) map[f.ID].ShowWhenFieldId = map[f.ShowWhenFieldId!.Value].ID;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(draft.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> PublishAsync(BusinessContext ctx, int versionId, string? note, string? noteEn, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var v = await Q<HospitalClinicalTemplateVersion>(ctx).FirstOrDefaultAsync(x => x.ID == versionId, ct);
				if (v == null) return HospitalResult.Fail("not_found");
				if (v.Status != HospitalTemplateStatus.Draft) return HospitalResult.Fail("Only a draft can be published");
				if (!await Q<HospitalClinicalTemplateField>(ctx).AnyAsync(f => f.VersionId == v.ID, ct)) return HospitalResult.Fail("Add at least one field before publishing");
				if (string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write what changed in this version");
				if (string.IsNullOrWhiteSpace(noteEn)) return HospitalResult.Fail("Write what changed in this version, in Arabic and in English");
				foreach (var old in await Q<HospitalClinicalTemplateVersion>(ctx).Where(x => x.TemplateId == v.TemplateId && x.Status == HospitalTemplateStatus.Published).ToListAsync(ct))
					old.Status = HospitalTemplateStatus.Retired;
				await _db.SaveChangesAsync(ct);   // the old one retires first: one published version at a time (a unique index)
				v.Status = HospitalTemplateStatus.Published; v.ChangeNote = Clip(note, 300); v.PublishedAt = nowUtc; v.PublishedByEmployeeId = employeeId;
				v.ChangeNoteEn = Clip(noteEn, 300);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(v.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		private async Task<(HospitalClinicalTemplateVersion? V, string? Why)> DraftAsync(BusinessContext ctx, int versionId, CancellationToken ct)
		{
			var v = await Q<HospitalClinicalTemplateVersion>(ctx).FirstOrDefaultAsync(x => x.ID == versionId, ct);
			if (v == null) return (null, "not_found");
			return v.Status == HospitalTemplateStatus.Draft ? (v, null) : (null, "A published version cannot change: make a new version");
		}

		private static void Apply(HospitalClinicalTemplateField f, HospitalTemplateFieldInput i)
		{
			f.ConceptId = i.ConceptId!.Value; f.Section = Clip(i.Section, 100)!; f.SectionEn = Clip(i.SectionEn, 100)!; f.Label = Clip(i.Label, 200); f.LabelEn = Clip(i.LabelEn, 200);
			f.Required = i.Required; f.ValueSource = i.ValueSource ?? HospitalValueSource.Manual; f.LookbackDays = f.ValueSource == HospitalValueSource.LastLab ? i.LookbackDays ?? 90 : null;
			f.ShowWhenFieldId = i.ShowWhenFieldId; f.ShowWhenAbove = i.ShowWhenFieldId == null ? null : i.ShowWhenAbove; f.Note = Clip(i.Note, 300); f.NoteEn = Clip(i.NoteEn, 300);
			f.AlertAbove = i.AlertAbove; f.AlertBelow = i.AlertBelow; f.AlertRange = i.AlertRange; bool alert = i.AlertAbove != null || i.AlertBelow != null || i.AlertRange != null;
			f.AlertText = alert ? Clip(i.AlertText, 150) : null; f.AlertTextEn = alert ? Clip(i.AlertTextEn, 150) : null; f.CareGapDays = i.CareGapDays;
			f.ShowTrend = i.ShowTrend;
			f.AlertNotify = alert && i.AlertNotify; f.AlertTemplateId = alert ? i.AlertTemplateId : null;
		}

		public async Task<HospitalResult> AddFieldAsync(BusinessContext ctx, int versionId, HospitalTemplateFieldInput i, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var (v, why) = await DraftAsync(ctx, versionId, ct);
				if (v == null) return HospitalResult.Fail(why!);
				var siblings = await Q<HospitalClinicalTemplateField>(ctx).Where(f => f.VersionId == v.ID).ToListAsync(ct);
				var c = i.ConceptId is int cid ? await Q<HospitalConcept>(ctx).FirstOrDefaultAsync(x => x.ID == cid, ct) : null;
				if (HospitalTemplateRules.FieldRefusal(i, c, siblings, null) is string r) return HospitalResult.Fail(r);
				if (siblings.Any(f => f.ConceptId == c!.ID)) return HospitalResult.Fail("This concept is already on the template");
				// a new field goes to the end of its section (or the end of the form for a new section)
				var sec = siblings.Where(f => f.Section == i.Section!.Trim()).ToList();
				int order = sec.Count > 0 ? sec.Max(f => f.SortOrder) + 1 : (siblings.Count == 0 ? 10 : siblings.Max(f => f.SortOrder) + 10);
				foreach (var f in siblings.Where(f => sec.Count > 0 && f.SortOrder >= order)) f.SortOrder++;
				var n = new HospitalClinicalTemplateField { CompanyId = ctx.CompanyId, VersionId = v.ID, SortOrder = order };
				Apply(n, i);
				_db.Add(n);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(n.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> SaveFieldAsync(BusinessContext ctx, int fieldId, HospitalTemplateFieldInput i, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var f = await Q<HospitalClinicalTemplateField>(ctx).FirstOrDefaultAsync(x => x.ID == fieldId, ct);
				if (f == null) return HospitalResult.Fail("not_found");
				var (v, why) = await DraftAsync(ctx, f.VersionId, ct);
				if (v == null) return HospitalResult.Fail(why!);
				i.ConceptId = f.ConceptId;   // the concept is the field's identity: another concept is another field
				var siblings = await Q<HospitalClinicalTemplateField>(ctx).Where(x => x.VersionId == v.ID).ToListAsync(ct);
				var c = await Q<HospitalConcept>(ctx).FirstOrDefaultAsync(x => x.ID == f.ConceptId, ct);
				if (HospitalTemplateRules.FieldRefusal(i, c, siblings, f.ID) is string r) return HospitalResult.Fail(r);
				// no cycle: the field it depends on must not (through others) depend on it
				for (int? w = i.ShowWhenFieldId, hops = 0; w != null && hops < 50; hops++) { if (w == f.ID) return HospitalResult.Fail("A field can only depend on another field of the same template"); w = siblings.FirstOrDefault(x => x.ID == w)?.ShowWhenFieldId; }
				Apply(f, i);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(f.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RemoveFieldAsync(BusinessContext ctx, int fieldId, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var f = await Q<HospitalClinicalTemplateField>(ctx).FirstOrDefaultAsync(x => x.ID == fieldId, ct);
				if (f == null) return HospitalResult.Fail("not_found");
				var (v, why) = await DraftAsync(ctx, f.VersionId, ct);
				if (v == null) return HospitalResult.Fail(why!);
				if (await Q<HospitalClinicalTemplateField>(ctx).AnyAsync(x => x.VersionId == v.ID && x.ShowWhenFieldId == f.ID, ct)) return HospitalResult.Fail("Another field depends on this one: change it first");
				_db.Remove(f);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(v.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The draft's fields in the order the designer dragged them, each with the section it was dropped into.
		public async Task<HospitalResult> ReorderAsync(BusinessContext ctx, int versionId, IReadOnlyList<HospitalFieldPlace> order, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var (v, why) = await DraftAsync(ctx, versionId, ct);
				if (v == null) return HospitalResult.Fail(why!);
				var fs = await Q<HospitalClinicalTemplateField>(ctx).Where(f => f.VersionId == v.ID).ToDictionaryAsync(f => f.ID, ct);
				if (order.Count != fs.Count || order.Any(o => !fs.ContainsKey(o.FieldId)) || order.Select(o => o.FieldId).Distinct().Count() != fs.Count) return HospitalResult.Fail("The list of fields changed: reload the page");
				int n = 10;
				foreach (var o in order)
				{
					var f = fs[o.FieldId]; f.SortOrder = n; n += 10;
					// moved into another section: it takes that section's names from a field already there
					var host = fs.Values.FirstOrDefault(x => x.ID != f.ID && x.Section == o.Section);
					if (host != null && f.Section != host.Section) { f.Section = host.Section; f.SectionEn = host.SectionEn; }
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(v.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}
	}

	public sealed class HospitalFieldPlace
	{
		public int FieldId { get; set; }
		public string Section { get; set; } = "";
	}
}
