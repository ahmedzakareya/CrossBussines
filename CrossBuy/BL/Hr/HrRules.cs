using System.Text.Json;
using System.Text.Json.Serialization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR COUNTRY RULE PACKS (owner ruling 2026-11-08: every legal figure is a setting per country, never code).
	//
	// The shape of one pack, stored as HrRulePacks.RulesJson. A country is DATA in this shape: Kuwait is seeded by
	// hr_rule_packs.sql; Saudi Arabia and the UAE are added as rows, not as `if (country == …)` branches. Every
	// figure marked Counsel is a default the company's legal counsel confirms before go-live.
	// ==========================================================================================
	public sealed class HrRules
	{
		public string Currency { get; set; } = "KWD";
		public List<string> Weekend { get; set; } = new() { "Friday", "Saturday" };
		public decimal WageDaysPerMonth { get; set; } = 26;
		public decimal HoursPerDay { get; set; } = 8;
		/// Which wage the gratuity and overtime are computed on: "total" (basic + allowances) or "basic". The UAE pays
		/// both on the basic wage; Kuwait on the total wage. A per-pack setting, never a country branch in code.
		public string WageBasis { get; set; } = "total";
		/// The country whose nationals the "nationals" insurance lines cover, by its CountriesLookup.CountryName
		/// ("Kuwait", "Egypt" …). An employee whose nationality matches is a national; anyone else is not.
		public string? NationalCountryName { get; set; }
		public static bool IsBasic(HrRules r) => string.Equals(r.WageBasis, "basic", StringComparison.OrdinalIgnoreCase);   // the hourly wage for overtime = monthly wage ÷ WageDaysPerMonth ÷ HoursPerDay
		public List<HrLeaveRule> Leave { get; set; } = new();
		public List<HrInsuranceRule> Insurance { get; set; } = new();
		public HrOvertimeRule Overtime { get; set; } = new();
		public HrGratuityRule Gratuity { get; set; } = new();
		public List<HrResignationTier> Resignation { get; set; } = new();
		public string? ResignationReference { get; set; }
		public bool ResignationCounsel { get; set; }
		public decimal LoanCapPct { get; set; }
		public string? LoanReference { get; set; }
		/// The end-of-service clearance checklist: each item becomes a follow-up task when an employee's exit starts.
		public List<HrClearanceItem> Clearance { get; set; } = new();
		public string? Notes { get; set; }   // the pack's own caveats (e.g. "gratuity is on the BASIC wage")
	}
	public sealed class HrLeaveRule
	{
		public string Code { get; set; } = "";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public decimal Days { get; set; }
		public string? RuleAr { get; set; }
		public string? RuleEn { get; set; }
		public string? Reference { get; set; }
		public bool Counsel { get; set; }

		// ---- HR3: the eligibility the leave engine enforces on every request (all settings, 0/empty = no condition) ----
		/// Months of continuous service (from DateOfJoining to the leave's first day) before this leave may be taken.
		public decimal MinServiceMonths { get; set; }
		/// How many times in the whole service this leave may be taken (Hajj = 1, Egyptian maternity = 3); 0 = no limit.
		public int MaxTimesPerService { get; set; }
		/// "upfront" = the whole year's days are available on 1 January; "monthly" = Days ÷ 12 accrue each month.
		public string Accrual { get; set; } = "upfront";
		/// Only employees of this religion ("Muslim" for Hajj in Kuwait); empty = everyone.
		public string? Religion { get; set; }
		public bool AccruesMonthly => string.Equals(Accrual, "monthly", StringComparison.OrdinalIgnoreCase);
	}

	public static class HrLeaveMatch
	{
		// A company's leave TYPES are its own rows (LeaveTypes); the pack's rules are the law's. They meet by the pack code's
		// synonyms in the type's English name, then by the pack's Arabic name inside the type's Arabic name.
		private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
		{
			["annual"] = new[] { "annual" }, ["sick"] = new[] { "sick", "medical" }, ["maternity"] = new[] { "maternity" },
			["paternity"] = new[] { "paternity" }, ["hajj"] = new[] { "hajj", "umrah" }, ["bereave"] = new[] { "bereave", "death", "condolence" },
			["iddah"] = new[] { "iddah" }, ["marriage"] = new[] { "marriage", "wedding" }, ["parental"] = new[] { "parental" },
			["study"] = new[] { "study", "exam" }, ["casual"] = new[] { "casual", "emergency" },
		};

		public static HrLeaveRule? For(HrRules? pack, string? typeNameEn, string? typeNameAr)
		{
			if (pack == null) return null;
			var en = (typeNameEn ?? "").ToLowerInvariant();
			return pack.Leave.FirstOrDefault(r => Synonyms.TryGetValue(r.Code ?? "", out var keys) && keys.Any(k => en.Contains(k)))
				?? pack.Leave.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.NameEn) && en.Contains(r.NameEn.ToLowerInvariant()))
				?? pack.Leave.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.NameAr) && (typeNameAr ?? "").Contains(r.NameAr));
		}
	}
	public sealed class HrClearanceItem
	{
		public string Code { get; set; } = "";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
	}
	public sealed class HrInsuranceRule
	{
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public decimal EmployeePct { get; set; }
		public decimal EmployerPct { get; set; }
		public decimal? WageCap { get; set; }
		public bool Counsel { get; set; }
		/// "all" = every employee; "nationals" = only nationals of the pack's country (e.g. PIFSS in Kuwait, GOSI annuities in KSA).
		public string AppliesTo { get; set; } = "all";
		public bool ForNationalsOnly => string.Equals(AppliesTo, "nationals", StringComparison.OrdinalIgnoreCase);
	}
	public sealed class HrOvertimeRule
	{
		public decimal WorkingDayPct { get; set; }
		public decimal RestDayPct { get; set; }
		public decimal HolidayPct { get; set; }
		public decimal DailyCapHours { get; set; }
		public decimal YearlyCapHours { get; set; }
		/// HR5: overtime is paid only once approved (per employee, per month, on /Admin/Overtime); off = paid as recorded.
		public bool RequiresApproval { get; set; }
		public string? Reference { get; set; }
		public bool Counsel { get; set; }
	}
	public sealed class HrGratuityRule
	{
		public decimal FirstYears { get; set; }
		public decimal FirstDaysPerYear { get; set; }
		public decimal LaterDaysPerYear { get; set; }
		public decimal CapMonths { get; set; }
		public string? Reference { get; set; }
		public bool Counsel { get; set; }
	}
	public sealed class HrResignationTier
	{
		public decimal FromYears { get; set; }
		public decimal? ToYears { get; set; }
		public decimal PaidPct { get; set; }
	}

	/// <summary>The gratuity a pack yields: the tiers, the cap, and what a resignation pays.</summary>
	public sealed record HrGratuityResult(decimal Years, decimal DailyWage, decimal FirstPart, decimal LaterPart, decimal CapAmount,
		decimal OnTermination, decimal ResignationPaidPct, decimal OnResignation, bool CapApplied);

	public static class HrGratuity
	{
		/// The statutory gratuity for <paramref name="years"/> of service on <paramref name="monthlyWage"/>, per the pack:
		/// first-tier years × first-tier days, later years × later days, capped at CapMonths of wage; a resignation is
		/// paid the tier percentage of that. Pure — no database — so it is testable at every boundary.
		public static HrGratuityResult Compute(HrRules rules, decimal years, decimal monthlyWage)
		{
			years = Math.Max(0, years);
			var g = rules.Gratuity;
			var perDay = rules.WageDaysPerMonth > 0 ? monthlyWage / rules.WageDaysPerMonth : 0m;
			var first = Math.Min(years, g.FirstYears) * g.FirstDaysPerYear * perDay;
			var later = Math.Max(0, years - g.FirstYears) * g.LaterDaysPerYear * perDay;
			var cap = g.CapMonths > 0 ? g.CapMonths * monthlyWage : decimal.MaxValue;
			var full = Math.Min(first + later, cap);
			var tier = rules.Resignation.FirstOrDefault(t => years >= t.FromYears && (t.ToYears == null || years < t.ToYears));
			var pct = tier?.PaidPct ?? 100m;
			return new HrGratuityResult(years, R(perDay), R(first), R(later), cap == decimal.MaxValue ? 0 : R(cap),
				R(full), pct, R(full * pct / 100m), first + later > cap);
		}

		private static decimal R(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
	}

	public sealed class HrRulePackService
	{
		private static readonly JsonSerializerOptions Json = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = true,
			WriteIndented = true,
			NumberHandling = JsonNumberHandling.AllowReadingFromString,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};

		private readonly CrossDbContext _db;
		public HrRulePackService(CrossDbContext db) { _db = db; }

		public static HrRules Parse(string json) => JsonSerializer.Deserialize<HrRules>(json, Json) ?? new HrRules();
		public static string Serialize(HrRules rules) => JsonSerializer.Serialize(rules, Json);

		/// The pack this company runs on: its own copy when it has one, else the country template (Kuwait first).
		/// IsCompanyCopy tells the screen whether a save creates the copy or updates it.
		public async Task<(HrRulePack Pack, HrRules Rules, bool IsCompanyCopy)?> EffectiveAsync(int companyId, CancellationToken ct = default)
		{
			var own = await _db.HrRulePacks.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyID == companyId, ct);
			if (own != null) return (own, Parse(own.RulesJson), true);
			var template = await _db.HrRulePacks.AsNoTracking().Where(p => p.CompanyID == null).OrderBy(p => p.CountryCode == "KW" ? 0 : 1).ThenBy(p => p.ID).FirstOrDefaultAsync(ct);
			return template == null ? null : (template, Parse(template.RulesJson), false);
		}

		public Task<List<HrRulePack>> TemplatesAsync(CancellationToken ct = default) =>
			_db.HrRulePacks.AsNoTracking().Where(p => p.CompanyID == null).OrderBy(p => p.CountryCode).ToListAsync(ct);

		/// Saves the company's copy (creating it from <paramref name="countryCode"/>'s template on the first save) and
		/// bumps the version, so a change is never silent.
		public async Task<(bool Ok, string? Error)> SaveAsync(int companyId, string countryCode, HrRules rules, int? employeeId, CancellationToken ct = default)
		{
			var problem = Validate(rules);
			if (problem != null) return (false, problem);
			var own = await _db.HrRulePacks.FirstOrDefaultAsync(p => p.CompanyID == companyId, ct);
			if (own == null)
			{
				var template = await _db.HrRulePacks.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyID == null && p.CountryCode == countryCode, ct);
				if (template == null) return (false, "Unknown country template");
				own = new HrRulePack { CompanyID = companyId, CountryCode = template.CountryCode, NameAr = template.NameAr, NameEn = template.NameEn, EffectiveFrom = DateTime.Today, Version = 0 };
				_db.HrRulePacks.Add(own);
			}
			own.RulesJson = Serialize(rules);
			own.Version += 1;
			own.UpdatedAt = DateTime.UtcNow;
			own.UpdatedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		/// Replaces the company's copy with a country template (creating the copy if there is none): country, names and
		/// rules all follow the template, and the version goes up.
		public async Task<(bool Ok, string? Error)> ApplyTemplateAsync(int companyId, HrRulePack template, int? employeeId, CancellationToken ct = default)
		{
			if (template.CompanyID != null) return (false, "Unknown country template");
			var own = await _db.HrRulePacks.FirstOrDefaultAsync(p => p.CompanyID == companyId, ct);
			if (own == null) { own = new HrRulePack { CompanyID = companyId, Version = 0 }; _db.HrRulePacks.Add(own); }
			own.CountryCode = template.CountryCode; own.NameAr = template.NameAr; own.NameEn = template.NameEn;
			own.RulesJson = template.RulesJson; own.EffectiveFrom = DateTime.Today;
			own.Version += 1; own.UpdatedAt = DateTime.UtcNow; own.UpdatedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		/// Refuses figures that cannot be law: negative days, percentages outside 0..1000, an empty tier table.
		public static string? Validate(HrRules r)
		{
			if (r.WageDaysPerMonth is <= 0 or > 31) return "Wage days per month must be between 1 and 31";
			if (r.HoursPerDay is <= 0 or > 24) return "Working hours per day must be between 1 and 24";
			if (r.Leave.Any(l => l.Days < 0 || l.Days > 400)) return "Leave days must be between 0 and 400";
			if (r.Leave.Any(l => string.IsNullOrWhiteSpace(l.NameAr) && string.IsNullOrWhiteSpace(l.NameEn))) return "Every leave type needs a name";
			if (r.Leave.Any(l => l.MinServiceMonths is < 0 or > 600 || l.MaxTimesPerService is < 0 or > 50)) return "Leave eligibility figures are out of range";
			if (r.Insurance.Any(i => string.IsNullOrWhiteSpace(i.NameAr) && string.IsNullOrWhiteSpace(i.NameEn))) return "Every insurance line needs a name";
			if (r.Clearance.Any(c => string.IsNullOrWhiteSpace(c.NameAr) && string.IsNullOrWhiteSpace(c.NameEn))) return "Every clearance item needs a name";
			if (r.Resignation.Any(t => t.ToYears.HasValue && t.ToYears <= t.FromYears)) return "A resignation tier must end after it starts";
			if (r.Insurance.Any(i => i.EmployeePct is < 0 or > 100 || i.EmployerPct is < 0 or > 100 || i.WageCap < 0)) return "Insurance percentages must be between 0 and 100";
			var o = r.Overtime;
			if (new[] { o.WorkingDayPct, o.RestDayPct, o.HolidayPct }.Any(p => p is < 0 or > 1000) || o.DailyCapHours < 0 || o.YearlyCapHours < 0) return "Overtime figures are out of range";
			var g = r.Gratuity;
			if (g.FirstYears < 0 || g.FirstDaysPerYear < 0 || g.LaterDaysPerYear < 0 || g.CapMonths < 0) return "Gratuity figures cannot be negative";
			if (r.Resignation.Count == 0 || r.Resignation.Any(t => t.PaidPct is < 0 or > 100)) return "Every resignation tier needs a paid percentage between 0 and 100";
			if (r.LoanCapPct is < 0 or > 100) return "The loan cap must be between 0 and 100";
			return null;
		}
	}
}
