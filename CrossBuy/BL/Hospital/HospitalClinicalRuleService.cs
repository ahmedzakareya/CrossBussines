using System.Globalization;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H171 — the clinical rule register (from the review of the clinics: "each sensitive rule with its
	// reference, version, approval and limits"). The CATALOGUE below is the truth of what the code does: one entry per
	// rule the software applies to a patient, with the guideline and version it implements, what it does NOT cover, and
	// the values a clinic may set (bounded). Changing a rule's behaviour means changing its version here — its approval
	// then no longer covers it, and the register shows it awaiting approval.
	//   · Approving (`manage`, the clinical lead): the version shown, the clinic's values, a note. Each approval is a new
	//     row; the latest for the rule is the one in force.
	//   · A rule reads its values through ParamAsync: the approved value for the CURRENT version, else the catalogue's
	//     default (an unapproved rule still runs on its published default, and the register says it is unapproved).
	// ==========================================================================================
	public sealed record HospitalRuleParam(string Key, decimal Default, decimal Min, decimal Max, string Unit);
	public sealed record HospitalRuleDef(string Code, string Name, string Area, string Reference, string Version, string Covers, string Limits, IReadOnlyList<HospitalRuleParam> Params);
	public sealed record HospitalRuleRow(HospitalRuleDef Rule, HospitalClinicalRuleApproval? Latest, bool Current, IReadOnlyDictionary<string, decimal> Values, string? ApprovedBy);

	public sealed class HospitalRuleApproveInput
	{
		public string Code { get; set; } = "";
		public string Version { get; set; } = "";
		public Dictionary<string, decimal?>? Values { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalClinicalRuleCatalog
	{
		public const string Kdigo = "kdigo", EgfrDosing = "egfr-dosing", Burns = "burns", Labour = "labour", AfStroke = "af-stroke",
			Carboplatin = "carboplatin", Immunotherapy = "immunotherapy";

		public static readonly IReadOnlyList<HospitalRuleDef> All = new HospitalRuleDef[]
		{
			new(Kdigo, "rule:kdigo:name", "Nephrology", "KDIGO 2024 Clinical Practice Guideline for the Evaluation and Management of CKD", "2024.1",
				"rule:kdigo:covers", "rule:kdigo:limits", Array.Empty<HospitalRuleParam>()),
			new(EgfrDosing, "rule:egfr-dosing:name", "Pharmacy", "KDIGO 2024 (drug dosing in CKD) and each medicine's own thresholds in the dose limits", "2",
				"rule:egfr-dosing:covers", "rule:egfr-dosing:limits", new[] { new HospitalRuleParam("recencyDays", 90, 1, 365, "days") }),
			new(Burns, "rule:burns:name", "Emergency", "American Burn Association burn-centre referral criteria (2022); Parkland formula", "2",
				"rule:burns:covers", "rule:burns:limits", new[] { new HospitalRuleParam("referPartialPct", 10, 5, 20, "%"), new HospitalRuleParam("fluidsAdultPct", 20, 10, 30, "%"), new HospitalRuleParam("fluidsChildPct", 10, 5, 20, "%") }),
			new(Labour, "rule:labour:name", "Obstetrics", "WHO partograph (2000)", "1",
				"rule:labour:covers", "rule:labour:limits", Array.Empty<HospitalRuleParam>()),
			new(AfStroke, "rule:af-stroke:name", "Cardiology", "ESC 2024 Guidelines for the management of atrial fibrillation — CHA₂DS₂-VA", "2024",
				"rule:af-stroke:covers", "rule:af-stroke:limits", Array.Empty<HospitalRuleParam>()),
			new(Carboplatin, "rule:carboplatin:name", "Oncology", "Calvert formula (1989); Cockcroft-Gault clearance (1976); FDA cap of GFR at 125 mL/min (2010)", "1",
				"rule:carboplatin:covers", "rule:carboplatin:limits", new[] { new HospitalRuleParam("gfrCap", 125, 60, 150, "mL/min") }),
			new(Immunotherapy, "rule:immunotherapy:name", "Allergy", "The clinic's allergen immunotherapy protocol (after the EAACI 2017 guideline)", "2",
				"rule:immunotherapy:covers", "rule:immunotherapy:limits", new[] { new HospitalRuleParam("restartStepsDown", 2, 1, 6, "steps"), new HospitalRuleParam("largeLocalMm", 25, 10, 50, "mm") }),
		};

		public static HospitalRuleDef? Of(string code) => All.FirstOrDefault(r => r.Code == code);

		/// The values an approval sets: every parameter of the rule, each within its bounds.
		public static string? Refusal(HospitalRuleDef def, string version, IReadOnlyDictionary<string, decimal?>? values)
		{
			if (version != def.Version) return "This rule changed since the page was opened: approve the version shown now";
			foreach (var p in def.Params)
			{
				if (values == null || !values.TryGetValue(p.Key, out var v) || v is not decimal) return "Enter every value of the rule";
				if (v < p.Min || v > p.Max) return "A value is outside what the rule allows";
			}
			return null;
		}

		public static IReadOnlyDictionary<string, decimal> Values(HospitalRuleDef def, HospitalClinicalRuleApproval? approval)
		{
			var stored = new Dictionary<string, decimal>();
			if (approval?.Version == def.Version && approval.ParamsJson is string js)
				try { stored = JsonSerializer.Deserialize<Dictionary<string, decimal>>(js) ?? new(); } catch (JsonException) { }
			return def.Params.ToDictionary(p => p.Key, p => stored.TryGetValue(p.Key, out var v) && v >= p.Min && v <= p.Max ? v : p.Default);
		}

		/// A rule's value for this company: the approved one for the current version, else the published default.
		public static async Task<decimal> ParamAsync(CrossDbContext db, int companyId, string code, string key, CancellationToken ct)
		{
			var def = Of(code) ?? throw new ArgumentException(code);
			HospitalClinicalRuleApproval? latest = null;
			try
			{
				latest = await db.Set<HospitalClinicalRuleApproval>().AsNoTracking().Where(a => a.CompanyId == companyId && a.Code == code)
					.OrderByDescending(a => a.ApprovedAt).ThenByDescending(a => a.ID).FirstOrDefaultAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }   // the register's table not created yet: the defaults
			return Values(def, latest)[key];
		}
	}

	public sealed class HospitalClinicalRuleService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalClinicalRuleService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		public async Task<IReadOnlyList<HospitalRuleRow>> RegisterAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var all = await _db.Set<HospitalClinicalRuleApproval>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId).ToListAsync(ct);
			var latest = all.GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.ApprovedAt).ThenByDescending(a => a.ID).First());
			var emps = latest.Values.Select(a => a.ApprovedByEmployeeId).Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => emps.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			return HospitalClinicalRuleCatalog.All.Select(r =>
			{
				var a = latest.GetValueOrDefault(r.Code);
				var by = a == null ? null : names.FirstOrDefault(n => n.ID == a.ApprovedByEmployeeId) is { } n ? DisplayName.Of(n.FullName, n.FullNameEn ?? n.FullName) : null;
				return new HospitalRuleRow(r, a, a?.Version == r.Version, HospitalClinicalRuleCatalog.Values(r, a), by);
			}).ToList();
		}

		/// The clinical lead approves the version shown, with the clinic's values.
		public async Task<HospitalResult> ApproveAsync(BusinessContext ctx, HospitalRuleApproveInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (employeeId is not int emp) return HospitalResult.Fail("forbidden");
			var def = HospitalClinicalRuleCatalog.Of(input.Code);
			if (def == null) return HospitalResult.Fail("not_found");
			if (HospitalClinicalRuleCatalog.Refusal(def, input.Version, input.Values) is string why) return HospitalResult.Fail(why);
			var values = def.Params.ToDictionary(p => p.Key, p => input.Values![p.Key]!.Value);
			var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim()[..Math.Min(300, input.Note.Trim().Length)];
			var row = new HospitalClinicalRuleApproval { CompanyId = ctx.CompanyId, Code = def.Code, Version = def.Version, ParamsJson = values.Count == 0 ? null : JsonSerializer.Serialize(values),
				Note = note, ApprovedByEmployeeId = emp, ApprovedAt = nowUtc };
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}
	}
}
