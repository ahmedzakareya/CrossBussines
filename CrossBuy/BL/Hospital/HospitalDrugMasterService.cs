using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H133 — the drug master (formulary), kept by the pharmacist.
	//
	//   · The medicines are the inventory items in the categories the hospital maps as medicines (H10's mapping). Each
	//     may carry one drug record (HospitalDrug). An item without one is "not described": the pharmacy dispenses it,
	//     but no generic name, form or safety flag stands behind its brand name.
	//   · A record is "incomplete" until it has the five things every leading system asks first: the generic name, the
	//     strength, the form, at least one route and the ATC code.
	//   · A controlled schedule keeps the H39 controlled-drug flag (HospitalControlledItems) in step, so the ward's
	//     controlled register and the formulary never disagree.
	//   · The dispense review reads the record (Lookup): the Tall Man name, the generic for allergy and interaction
	//     matching, the high-alert / look-alike / restricted / non-formulary warnings. A medicine marked unavailable is
	//     refused at dispensing.
	// ==========================================================================================

	public sealed record HospitalDrugRow(int ItemId, string Code, string Name, string? NameEn, HospitalDrug? Drug, bool Controlled, IReadOnlyList<string> Missing, decimal? Price);

	public sealed record HospitalFormularyStats(int Medicines, int Described, int Incomplete, int Restricted, int OffList, int HighAlert, int Controlled, int Lasa);

	public sealed class HospitalDrugInput
	{
		public int ItemId { get; set; }
		public string? GenericName { get; set; }
		public string? GenericNameEn { get; set; }
		public string? TallMan { get; set; }
		public decimal? Strength { get; set; }
		public string? StrengthUnit { get; set; }
		public string? Form { get; set; }
		public List<string>? Routes { get; set; }
		public string? Atc { get; set; }
		public string? TherapeuticClass { get; set; }
		public string? FormularyStatus { get; set; }
		public string? RestrictionNote { get; set; }
		public bool HighAlert { get; set; }
		public string? LasaWith { get; set; }
		public string? ControlledSchedule { get; set; }
		public bool Hazardous { get; set; }
		public bool ColdChain { get; set; }
		public string? Ven { get; set; }
	}

	public static class HospitalFormularyFilter
	{
		public const string Undescribed = "undescribed", Incomplete = "incomplete", HighAlert = "high-alert", Lasa = "lasa", Controlled = "controlled",
			Hazardous = "hazardous", ColdChain = "cold-chain";
	}

	public static class HospitalDrugRules
	{
		public const string MissingRecord = "record", MissingGeneric = "generic", MissingStrength = "strength", MissingForm = "form", MissingRoute = "route", MissingAtc = "atc";

		/// WHO ATC at any level: A · A02 · A02B · A02BC · A02BC01.
		private static readonly Regex AtcCode = new(@"^[A-Z](\d{2}([A-Z]([A-Z](\d{2})?)?)?)?$", RegexOptions.Compiled);

		public static bool IsAtc(string? code) => code != null && AtcCode.IsMatch(code);

		/// The ATC anatomical main group (its first letter), for its name: atc:A = alimentary tract and metabolism…
		public static string? AtcGroup(string? atc) => IsAtc(atc) ? atc![..1] : null;

		/// What a record still lacks, in the order the pharmacist fills it; empty when complete.
		public static IReadOnlyList<string> Missing(HospitalDrug? d)
		{
			if (d == null) return new[] { MissingRecord };
			var m = new List<string>();
			if (string.IsNullOrWhiteSpace(d.GenericName)) m.Add(MissingGeneric);
			if (d.Strength == null) m.Add(MissingStrength);
			if (d.Form == null) m.Add(MissingForm);
			if (string.IsNullOrEmpty(d.Routes)) m.Add(MissingRoute);
			if (d.Atc == null) m.Add(MissingAtc);
			return m;
		}

		/// Tall Man lettering changes the case only: the letters must spell the generic name (Arabic or English).
		public static bool TallManSpells(string tallMan, string? generic, string? genericEn)
		{
			static string Letters(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
			var t = Letters(tallMan);
			return t.Length > 0 && (t == Letters(generic) || t == Letters(genericEn));
		}

		/// The name shown wherever the medicine is named: its Tall Man spelling, else its generic name.
		public static string? Shown(HospitalDrug? d) => d == null ? null : d.TallMan ?? DisplayName.Of(d.GenericName, d.GenericNameEn);

		/// "500 mg · Tablet": the strength with its unit (the form is translated by the screen).
		public static string? StrengthText(HospitalDrug? d) => d?.Strength is decimal s
			? s.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + (d.StrengthUnit == null ? "" : " " + d.StrengthUnit) : null;

		/// The words allergies and interactions are matched in: the prescribed name, and the generic in both languages, so
		/// an allergy to amoxicillin is caught when the doctor wrote the brand.
		public static string MatchText(string drug, HospitalDrug? d) => d == null ? drug
			: string.Join(" ", new[] { drug, d.GenericName, d.GenericNameEn }.Where(s => !string.IsNullOrWhiteSpace(s)));

		/// Cleans the input in place; a refusal, or null when it is sound.
		public static HospitalResult? Validate(HospitalDrugInput i)
		{
			i.GenericName = Clean(i.GenericName, 150); i.GenericNameEn = Clean(i.GenericNameEn, 150); i.TallMan = Clean(i.TallMan, 150);
			i.StrengthUnit = Clean(i.StrengthUnit, 10); i.Form = Clean(i.Form, 20); i.Atc = Clean(i.Atc, 10)?.ToUpperInvariant();
			i.TherapeuticClass = Clean(i.TherapeuticClass, 120); i.RestrictionNote = Clean(i.RestrictionNote, 300); i.LasaWith = Clean(i.LasaWith, 200);
			i.ControlledSchedule = Clean(i.ControlledSchedule, 20); i.Ven = Clean(i.Ven, 1); i.FormularyStatus = Clean(i.FormularyStatus, 20) ?? HospitalFormularyStatus.Formulary;
			i.Routes = (i.Routes ?? new()).Select(r => r?.Trim()).Where(r => !string.IsNullOrEmpty(r)).Select(r => r!).Distinct().ToList();

			if (i.GenericName == null && i.GenericNameEn == null) return HospitalResult.Fail("Enter the generic name");
			i.GenericName ??= i.GenericNameEn;
			if (i.TallMan != null && !TallManSpells(i.TallMan, i.GenericName, i.GenericNameEn))
				return HospitalResult.Fail("The Tall Man name must spell the generic name — only the letter case changes");
			if (i.Strength is decimal s && (s <= 0 || s > 1_000_000)) return HospitalResult.Fail("The strength must be above zero");
			if (i.Strength != null && i.StrengthUnit == null) return HospitalResult.Fail("Choose the strength's unit");
			if (i.Strength == null) i.StrengthUnit = null;
			if (i.StrengthUnit != null && !HospitalStrengthUnit.All.Contains(i.StrengthUnit)) return HospitalResult.Fail("Choose the strength's unit");
			if (i.Form != null && !HospitalDrugForm.All.Contains(i.Form)) return HospitalResult.Fail("Choose the dosage form from the list");
			if (i.Routes.Any(r => !HospitalDrugRoute.All.Contains(r))) return HospitalResult.Fail("Choose the routes from the list");
			if (i.Atc != null && !IsAtc(i.Atc)) return HospitalResult.Fail("The ATC code is a letter, then two digits, then letters and digits — like A02BC01");
			if (!HospitalFormularyStatus.All.Contains(i.FormularyStatus)) return HospitalResult.Fail("Choose the formulary status");
			if (i.FormularyStatus != HospitalFormularyStatus.Formulary && i.RestrictionNote == null)
				return HospitalResult.Fail("Write who may prescribe it, or why it is off the list");
			if (i.ControlledSchedule != null && !HospitalControlledSchedule.All.Contains(i.ControlledSchedule)) return HospitalResult.Fail("Choose the controlled schedule");
			if (i.Ven != null && !HospitalVen.All.Contains(i.Ven)) return HospitalResult.Fail("Choose V, E or N");
			return null;
		}

		private static string? Clean(string? s, int max) { s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s; }

		public static bool Matches(HospitalDrugRow r, string? filter) => filter switch
		{
			null or "" => true,
			HospitalFormularyFilter.Undescribed => r.Drug == null,
			HospitalFormularyFilter.Incomplete => r.Drug != null && r.Missing.Count > 0,
			HospitalFormularyFilter.HighAlert => r.Drug?.HighAlert == true,
			HospitalFormularyFilter.Lasa => r.Drug?.LasaWith != null,
			HospitalFormularyFilter.Controlled => r.Controlled,
			HospitalFormularyFilter.Hazardous => r.Drug?.Hazardous == true,
			HospitalFormularyFilter.ColdChain => r.Drug?.ColdChain == true,
			_ => r.Drug?.FormularyStatus == filter,
		};
	}

	public sealed class HospitalDrugMasterService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalDrugMasterService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		/// The medicines: items in the categories mapped as medicines, plus any item that already carries a drug record.
		private async Task<List<HospitalDrugRow>> RowsAsync(BusinessContext ctx, CancellationToken ct)
		{
			var cats = await _db.HospitalItemCategories.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && c.Kind == HospitalPriceGroups.Medicine).Select(c => c.ItemCategoryId).ToListAsync(ct);
			var drugs = await _db.Set<HospitalDrug>().AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId).ToDictionaryAsync(d => d.ItemId, ct);
			var described = drugs.Keys.ToList();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && i.IsActive && (cats.Contains(i.ItemCategoryId) || described.Contains(i.ID)))
				.Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn, i.SalesPrice }).ToListAsync(ct);
			var controlled = (await _db.Set<HospitalControlledItem>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId).Select(c => c.ItemId).ToListAsync(ct)).ToHashSet();
			return items.Select(i =>
			{
				var d = drugs.GetValueOrDefault(i.ID);
				return new HospitalDrugRow(i.ID, i.ItemCode, i.Name, i.NameEn, d, controlled.Contains(i.ID) || d?.ControlledSchedule != null, HospitalDrugRules.Missing(d), i.SalesPrice);
			}).ToList();
		}

		public async Task<HospitalFormularyStats> StatsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var rows = await RowsAsync(ctx, ct);
			return new HospitalFormularyStats(rows.Count, rows.Count(r => r.Drug != null), rows.Count(r => r.Drug != null && r.Missing.Count > 0),
				rows.Count(r => r.Drug?.FormularyStatus == HospitalFormularyStatus.Restricted),
				rows.Count(r => r.Drug?.FormularyStatus is HospitalFormularyStatus.NonFormulary or HospitalFormularyStatus.Unavailable),
				rows.Count(r => r.Drug?.HighAlert == true), rows.Count(r => r.Controlled), rows.Count(r => r.Drug?.LasaWith != null));
		}

		/// The list, searched by code, brand, generic, Tall Man or ATC; the medicines still to describe first, then by name.
		public async Task<(IReadOnlyList<HospitalDrugRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? q, string? filter, int page, int size, CancellationToken ct = default)
		{
			var rows = (await RowsAsync(ctx, ct)).Where(r => HospitalDrugRules.Matches(r, filter));
			if (!string.IsNullOrWhiteSpace(q))
			{
				var s = q.Trim();
				rows = rows.Where(r => new[] { r.Code, r.Name, r.NameEn, r.Drug?.GenericName, r.Drug?.GenericNameEn, r.Drug?.TallMan, r.Drug?.Atc, r.Drug?.TherapeuticClass }
					.Any(x => x != null && x.Contains(s, StringComparison.OrdinalIgnoreCase)));
			}
			var list = rows.OrderBy(r => r.Drug == null ? 0 : r.Missing.Count > 0 ? 1 : 2).ThenBy(r => r.Drug?.GenericNameEn ?? r.Drug?.GenericName ?? r.NameEn ?? r.Name).ToList();
			return (list.Skip((page - 1) * size).Take(size).ToList(), list.Count);
		}

		/// The drug records of the given items (the dispense review's lookup).
		public static async Task<Dictionary<int, HospitalDrug>> LookupAsync(CrossDbContext db, int companyId, IReadOnlyCollection<int> itemIds, CancellationToken ct)
		{
			if (itemIds.Count == 0) return new();
			try { return await db.Set<HospitalDrug>().AsNoTracking().Where(d => d.CompanyId == companyId && itemIds.Contains(d.ItemId)).ToDictionaryAsync(d => d.ItemId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return new(); }   // the slice not yet applied: no record, no warning
		}

		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, HospitalDrugInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			if (input == null) return HospitalResult.Fail("not_found");
			var refusal = HospitalDrugRules.Validate(input);
			if (refusal != null) return refusal;
			if (!(await RowsAsync(ctx, ct)).Any(r => r.ItemId == input.ItemId)) return HospitalResult.Fail("not_found");   // only a medicine of this company

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var d = await _db.Set<HospitalDrug>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.ItemId == input.ItemId, ct);
			if (d == null) { d = new HospitalDrug { CompanyId = ctx.CompanyId, ItemId = input.ItemId }; _db.Add(d); }
			d.GenericName = input.GenericName!; d.GenericNameEn = input.GenericNameEn; d.TallMan = input.TallMan;
			d.Strength = input.Strength; d.StrengthUnit = input.StrengthUnit; d.Form = input.Form;
			d.Routes = input.Routes!.Count == 0 ? null : string.Join(",", HospitalDrugRoute.All.Where(input.Routes.Contains));
			d.Atc = input.Atc; d.TherapeuticClass = input.TherapeuticClass; d.FormularyStatus = input.FormularyStatus!;
			d.RestrictionNote = input.FormularyStatus == HospitalFormularyStatus.Formulary ? null : input.RestrictionNote;
			d.HighAlert = input.HighAlert; d.LasaWith = input.LasaWith; d.ControlledSchedule = input.ControlledSchedule;
			d.Hazardous = input.Hazardous; d.ColdChain = input.ColdChain; d.Ven = input.Ven;
			d.UpdatedAt = nowUtc; d.UpdatedByEmployeeId = employeeId;

			// the ward's controlled register reads the H39 flag: keep it the schedule's shadow
			var flag = await _db.Set<HospitalControlledItem>().FirstOrDefaultAsync(c => c.CompanyId == ctx.CompanyId && c.ItemId == input.ItemId, ct);
			if (input.ControlledSchedule != null && flag == null) _db.Add(new HospitalControlledItem { CompanyId = ctx.CompanyId, ItemId = input.ItemId, CreatedAt = nowUtc });
			if (input.ControlledSchedule == null && flag != null) _db.Remove(flag);
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(d.ID);
		}
	}
}
