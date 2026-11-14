using System.Globalization;
using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H120 — A CHEMOTHERAPY CYCLE'S DRUGS GO TO THE PHARMACY. A cycle computed every patient's dose (mg) and that
	// was the end of it: no request reached the pharmacy, so no stock moved and the drugs never reached the bill. Now a
	// cycle given opens a pharmacy request (source "Oncology") with one line per drug — its stock item and the units to
	// prepare (the dose ÷ the item's strength, rounded UP: a vial opened for one patient is not shared) — and the
	// pharmacist verifies and dispenses it through the pharmacy's own flow, which moves the stock and bills. A drug the
	// pharmacy has not linked to a stock item still goes on the request, named, so it is seen and never silently dropped.
	// ==========================================================================================
	public sealed record HospitalChemoDrugLinkRow(string DrugKey, string DrugName, int Regimens, HospitalChemoDrugItem? Link, string? ItemCode, string? ItemName);

	public sealed class HospitalChemoDrugLinkInput
	{
		public string? Drug { get; set; }
		public int? ItemId { get; set; }
		public decimal? MgPerUnit { get; set; }
	}

	public static class HospitalChemoPharmacyRules
	{
		/// The drug as a key: trimmed, single spaces, upper case.
		public static string Key(string? drug) => Regex.Replace((drug ?? "").Trim(), @"\s+", " ").ToUpperInvariant();

		/// The units to prepare for a dose: the dose ÷ the unit's strength, rounded UP (a vial is never split across patients).
		public static decimal Units(decimal doseMg, decimal mgPerUnit) => mgPerUnit <= 0 ? 1 : Math.Max(1, Math.Ceiling(Math.Round(doseMg / mgPerUnit, 6)));

		/// The line the pharmacist reads: the drug, the dose to give, the route and the days.
		public static string Line(string drug, decimal doseMg, string route, string days) =>
			$"{drug.Trim()} {doseMg.ToString("0.#", CultureInfo.InvariantCulture)} mg {route} · {days}";

		public static string? LinkRefusal(HospitalChemoDrugLinkInput i, bool itemIsMedicine)
		{
			if (string.IsNullOrWhiteSpace(i.Drug) || Key(i.Drug).Length > 100) return "Choose the drug";
			if (i.ItemId == null) return null;   // unlinking
			if (!itemIsMedicine) return "Choose a medicine the pharmacy stocks";
			if (i.MgPerUnit is not decimal m || m <= 0 || m > 100000) return "Write how many mg one unit holds";
			return null;
		}
	}

	public sealed class HospitalChemoPharmacyService
	{
		public const string NoTables = "The chemotherapy pharmacy link is not installed yet (hospital_h120_chemo_pharmacy.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalChemoPharmacyService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;

		/// The pharmacy's medicines: the active items of the categories mapped to the hospital (the formulary).
		private async Task<List<int>> MedicinesAsync(BusinessContext ctx, CancellationToken ct)
		{
			var cats = await _db.HospitalItemCategories.AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId).Select(c => c.ItemCategoryId).ToListAsync(ct);
			return await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && i.IsActive && cats.Contains(i.ItemCategoryId)).Select(i => i.ID).ToListAsync(ct);
		}

		/// Every drug the regimens name, with its stock link (or none yet).
		public async Task<(IReadOnlyList<HospitalChemoDrugLinkRow> Rows, bool Ready)> LinksAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				var drugs = await (from d in _db.Set<HospitalChemoRegimenDrug>().AsNoTracking() where d.CompanyId == ctx.CompanyId select new { d.DrugName, d.RegimenId }).ToListAsync(ct);
				var links = await _db.Set<HospitalChemoDrugItem>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).ToDictionaryAsync(x => x.DrugKey, ct);
				var itemIds = links.Values.Select(l => l.ItemId).ToList();
				var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn }).ToDictionaryAsync(i => i.ID, ct);
				var rows = drugs.GroupBy(d => HospitalChemoPharmacyRules.Key(d.DrugName))
					.Select(g =>
					{
						var link = links.GetValueOrDefault(g.Key);
						var it = link == null ? null : items.GetValueOrDefault(link.ItemId);
						return new HospitalChemoDrugLinkRow(g.Key, g.First().DrugName.Trim(), g.Select(x => x.RegimenId).Distinct().Count(), link, it?.ItemCode, it == null ? null : DisplayName.Of(it.Name, it.NameEn));
					})
					.OrderBy(r => r.Link != null).ThenBy(r => r.DrugName).ToList();   // the unlinked first: they are the work
				return (rows, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalChemoDrugLinkRow>(), false); }
		}

		public async Task<HospitalResult> SaveLinkAsync(BusinessContext ctx, HospitalChemoDrugLinkInput i, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				bool medicine = i.ItemId is int iid && (await MedicinesAsync(ctx, ct)).Contains(iid);
				if (HospitalChemoPharmacyRules.LinkRefusal(i, medicine) is string why) return HospitalResult.Fail(why);
				var key = HospitalChemoPharmacyRules.Key(i.Drug);
				var row = await _db.Set<HospitalChemoDrugItem>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.DrugKey == key, ct);
				if (i.ItemId == null)
				{
					if (row != null) { _db.Remove(row); await _db.SaveChangesAsync(ct); }
					return HospitalResult.Success(0);
				}
				if (row == null) { row = new HospitalChemoDrugItem { CompanyId = ctx.CompanyId, DrugKey = key }; _db.Add(row); }
				row.ItemId = i.ItemId.Value; row.MgPerUnit = i.MgPerUnit!.Value; row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The cycle's pharmacy request, inside the caller's transaction: one line per dose, linked to its stock item when the
		/// pharmacy has one. Returns the request (null when the cycle gives no drug).
		public static async Task<HospitalDispense?> OpenRequestAsync(CrossDbContext db, BusinessContext ctx, int patientId, int? employeeId, DateTime nowUtc,
			IReadOnlyList<(string DrugName, decimal DoseMg, string Route, string Days)> doses, CancellationToken ct)
		{
			if (doses.Count == 0) return null;
			var keys = doses.Select(d => HospitalChemoPharmacyRules.Key(d.DrugName)).Distinct().ToList();
			var links = await db.Set<HospitalChemoDrugItem>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && keys.Contains(x.DrugKey)).ToDictionaryAsync(x => x.DrugKey, ct);
			var nos = await db.HospitalDispenses.Where(d => d.CompanyId == ctx.CompanyId && d.RxNo.StartsWith("RX-")).Select(d => d.RxNo).ToListAsync(ct);
			int n = 0;
			foreach (var no in nos) if (int.TryParse(no.AsSpan(3), out var v) && v > n) n = v;
			// the doctor who gave the cycle is the prescriber the pharmacist sees and asks
			int? doctor = employeeId is int me ? await db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.EmployeeId == me && p.IsActive).Select(p => (int?)p.ID).FirstOrDefaultAsync(ct) : null;
			var rx = new HospitalDispense
			{
				CompanyId = ctx.CompanyId, RxNo = "RX-" + (n + 1).ToString("000000"), PatientId = patientId, Source = HospitalDispenseSource.Oncology,
				PrescribedByEmployeeId = employeeId, PractitionerId = doctor, PrescribedAt = nowUtc,
			};
			db.HospitalDispenses.Add(rx);
			await db.SaveChangesAsync(ct);
			int order = 0;
			foreach (var d in doses)
			{
				var link = links.GetValueOrDefault(HospitalChemoPharmacyRules.Key(d.DrugName));
				db.HospitalDispenseLines.Add(new HospitalDispenseLine
				{
					CompanyId = ctx.CompanyId, DispenseId = rx.ID, ItemId = link?.ItemId, Drug = HospitalChemoPharmacyRules.Line(d.DrugName, d.DoseMg, d.Route, d.Days),
					Quantity = link == null ? 1 : HospitalChemoPharmacyRules.Units(d.DoseMg, link.MgPerUnit), SortOrder = ++order,
				});
			}
			await db.SaveChangesAsync(ct);
			return rx;
		}
	}
}
