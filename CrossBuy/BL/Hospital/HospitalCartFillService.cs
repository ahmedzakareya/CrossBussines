using System.Globalization;
using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H138 — the unit-dose cart fill (see HospitalCartFill).
	//   · PREVIEW: for one ward and a 24-hour window, one line per active medication order of every patient in a bed of
	//     that ward. Each line carries:
	//       - the doses due in the window (the nursing schedule's own DosesOn); an as-needed order gets a supply of 2;
	//       - the units per dose (the written dose against the formulary's strength; "check" when they cannot be matched);
	//       - what the line does: filled, from the cabinet (the ward's supply unit holds a par for it), not stocked,
	//         already on its way, no dose due, or short (the pharmacy store cannot cover it).
	//   · FILL: every line to fill becomes a medication delivery (H21), reviewed and prepared at once. The stock leaves
	//     FEFO there, and the charge comes with the MAR. A line whose preparation fails is cancelled; the others go on.
	//     One HospitalCartFill row records the fill.
	// No stock rule lives here: the delivery path issues, charges and returns.
	// ==========================================================================================

	public static class HospitalCartLineState
	{
		public const string Fill = "Fill", Cabinet = "Cabinet", NotStocked = "NotStocked", OnItsWay = "OnItsWay", NoDose = "NoDose", Short = "Short";
	}

	public sealed record HospitalCartLine(int AdmissionId, int PatientId, string PatientName, string? PatientNameEn, string Mrn, string? Photo, string Bed,
		int OrderId, string Drug, string Dose, string Route, string? Hours, bool AsNeeded, bool Controlled, int? ItemId, string? DrugShown,
		int Doses, decimal UnitsPerDose, bool UnitsExact, decimal Units, string State);

	public sealed record HospitalCartItemTotal(int ItemId, string Code, string Name, string? NameEn, decimal Units, decimal InStore);

	public sealed record HospitalCartPreview(int WardId, string WardName, string? WardNameEn, DateTime WindowStart, DateTime WindowEnd,
		IReadOnlyList<HospitalCartLine> Lines, IReadOnlyList<HospitalCartItemTotal> Totals, HospitalCartFill? LastFill, bool HasCabinet);

	public sealed record HospitalCartFillResult(int FillId, int Deliveries, int Skipped, IReadOnlyList<string> Problems);

	public static class HospitalCartFillRules
	{
		/// The supply of an as-needed order for one fill.
		public const int AsNeededDoses = 2;

		private static readonly Regex DoseText = new(@"^\s*(\d+(?:[.,]\d+)?)\s*([^\d\s].*)?$", RegexOptions.Compiled);

		/// The dose's quantity in a base unit (mg for mass, IU, mL), or null when it cannot be read.
		public static (decimal Value, string Base)? Measure(decimal value, string? unit)
		{
			var u = (unit ?? "").Trim().ToLowerInvariant();
			return u switch
			{
				"mg" or "ملغ" or "مجم" or "ملجم" => (value, "mg"),
				"g" or "gm" or "جم" or "غ" or "جرام" => (value * 1000, "mg"),
				"mcg" or "µg" or "ug" or "مكجم" or "ميكروجرام" => (value / 1000, "mg"),
				"iu" or "unit" or "units" or "وحدة" or "وحدات" => (value, "IU"),
				"ml" or "مل" => (value, "mL"),
				_ => null,
			};
		}

		/// Units of the item one dose takes: the written dose over the item's strength, rounded up to a half unit. Exact
		/// only when both are read in the same base unit; otherwise one unit, to be checked.
		public static (decimal Units, bool Exact) UnitsPerDose(string? dose, decimal? strength, string? strengthUnit)
		{
			var m = DoseText.Match(dose ?? "");
			if (!m.Success || strength is not decimal s || s <= 0) return (1, false);
			var d = Measure(decimal.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture), m.Groups[2].Value);
			var st = Measure(s, strengthUnit);
			if (d == null || st == null || d.Value.Base != st.Value.Base) return (1, false);
			var ratio = d.Value.Value / st.Value.Value;
			return (Math.Max(0.5m, Math.Ceiling(ratio * 2) / 2), true);
		}

		/// The scheduled doses of an order inside [start, end), over the ward days the window touches.
		public static int DosesIn(IReadOnlyList<int> hours, DateTime orderStartUtc, DateTime? stoppedUtc, DateTime startUtc, DateTime endUtc, Func<DateTime, DateTime> wardToUtc, Func<DateTime, DateTime> utcToWard)
		{
			int n = 0;
			for (var day = utcToWard(startUtc).Date; wardToUtc(day) < endUtc; day = day.AddDays(1))
				n += HospitalNursingRules.DosesOn(day, hours, orderStartUtc, stoppedUtc, wardToUtc).Count(t => t >= startUtc && t < endUtc);
			return n;
		}
	}

	public sealed class HospitalCartFillService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalMedRoutingService _routing;
		private readonly HospitalPharmacyService _pharmacy;

		public HospitalCartFillService(CrossDbContext db, IHospitalAccessService access, HospitalMedRoutingService routing, HospitalPharmacyService pharmacy)
		{ _db = db; _access = access; _routing = routing; _pharmacy = pharmacy; }

		public async Task<IReadOnlyList<(int Id, string Code, string Name, string? NameEn, int Patients)>> WardsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var wards = await _db.HospitalWards.AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId && w.IsActive).OrderBy(w => w.Code).Select(w => new { w.ID, w.Code, w.Name, w.NameEn }).ToListAsync(ct);
			var counts = await (from s in _db.HospitalBedStays.AsNoTracking()
								join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
								where s.CompanyId == ctx.CompanyId && s.EndedAt == null
								group s by r.WardId into g select new { WardId = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.WardId, x => x.N, ct);
			return wards.Select(w => (w.ID, w.Code, w.Name, w.NameEn, counts.GetValueOrDefault(w.ID))).ToList();
		}

		public async Task<HospitalCartPreview?> PreviewAsync(BusinessContext ctx, int wardId, DateTime startUtc, CancellationToken ct = default)
		{
			var ward = await _db.HospitalWards.AsNoTracking().FirstOrDefaultAsync(w => w.ID == wardId && w.CompanyId == ctx.CompanyId, ct);
			if (ward == null) return null;
			var endUtc = startUtc.AddHours(24);
			var here = await (from s in _db.HospitalBedStays.AsNoTracking()
							  join b in _db.HospitalBeds.AsNoTracking() on s.BedId equals b.ID
							  join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							  join a in _db.HospitalAdmissions.AsNoTracking() on s.AdmissionId equals a.ID
							  join p in _db.HospitalPatients.AsNoTracking() on s.PatientId equals p.ID
							  where s.CompanyId == ctx.CompanyId && s.EndedAt == null && r.WardId == wardId && a.Status == HospitalAdmissionStatus.Active && p.CompanyId == ctx.CompanyId
							  orderby b.Code
							  select new { s.AdmissionId, PatientId = p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, Bed = b.Code }).ToListAsync(ct);
			var admissions = here.Select(h => h.AdmissionId).ToList();
			var orders = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && admissions.Contains(o.AdmissionId) && o.Status == HospitalMedOrderStatus.Active)
				.OrderBy(o => o.ID).ToListAsync(ct);
			var orderIds = orders.Select(o => o.ID).ToList();
			var onItsWay = (await _db.Set<HospitalMedDelivery>().AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && orderIds.Contains(d.MedOrderId) && d.CancelledAt == null && d.Stage < HospitalMedStage.Administered)
				.Select(d => d.MedOrderId).ToListAsync(ct)).ToHashSet();
			var itemIds = orders.Where(o => o.ItemId != null).Select(o => o.ItemId!.Value).Distinct().ToList();
			var drugs = await HospitalDrugMasterService.LookupAsync(_db, ctx.CompanyId, itemIds, ct);
			// the ward's cabinet: the supply unit kept for this ward, and the medicines it holds a par for
			var unitIds = await _db.Set<HospitalSupplyUnit>().AsNoTracking().Where(u => u.CompanyId == ctx.CompanyId && u.IsActive && u.WardId == wardId).Select(u => u.ID).ToListAsync(ct);
			var cabinet = (await _db.Set<HospitalSupplyPar>().AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && unitIds.Contains(p.UnitId)).Select(p => p.ItemId).ToListAsync(ct)).ToHashSet();
			var store = (await _pharmacy.SettingsAsync(ctx, ct)).WarehouseId;
			var inStore = store is not int wh ? new Dictionary<int, decimal>() : await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == wh && itemIds.Contains(b.ItemId))
				.GroupBy(b => b.ItemId).Select(g => new { g.Key, Q = g.Sum(x => x.QtyOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);

			var lines = new List<HospitalCartLine>();
			var left = new Dictionary<int, decimal>(inStore);
			foreach (var h in here)
				foreach (var o in orders.Where(o => o.AdmissionId == h.AdmissionId))
				{
					var dm = o.ItemId is int iid ? drugs.GetValueOrDefault(iid) : null;
					int doses = o.AsNeeded ? HospitalCartFillRules.AsNeededDoses
						: HospitalCartFillRules.DosesIn(HospitalNursingRules.ParseHours(o.Hours) ?? Array.Empty<int>(), o.StartAt, o.StoppedAt, startUtc, endUtc, HospitalNursingService.WardToUtc, HospitalNursingService.UtcToWard);
					var (per, exact) = HospitalCartFillRules.UnitsPerDose(o.Dose, dm?.Strength, dm?.StrengthUnit);
					decimal units = doses * per;
					string state;
					if (o.ItemId is not int item) state = HospitalCartLineState.NotStocked;
					else if (cabinet.Contains(item)) state = HospitalCartLineState.Cabinet;
					else if (onItsWay.Contains(o.ID)) state = HospitalCartLineState.OnItsWay;
					else if (doses == 0) state = HospitalCartLineState.NoDose;
					else if (left.GetValueOrDefault(item) < units) state = HospitalCartLineState.Short;
					else { state = HospitalCartLineState.Fill; left[item] = left.GetValueOrDefault(item) - units; }
					lines.Add(new HospitalCartLine(h.AdmissionId, h.PatientId, h.Name, h.NameEn, h.Mrn, h.PhotoPath, h.Bed, o.ID, o.Drug, o.Dose, o.Route, o.Hours, o.AsNeeded, o.Controlled,
						o.ItemId, HospitalDrugRules.Shown(dm), doses, per, exact, units, state));
				}
			var fillItems = lines.Where(l => l.State == HospitalCartLineState.Fill && l.ItemId != null).GroupBy(l => l.ItemId!.Value).ToList();
			var ids = fillItems.Select(g => g.Key).ToList();
			var items = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID) && i.CompanyID == ctx.CompanyId).ToDictionaryAsync(i => i.ID, ct);
			var totals = fillItems.Where(g => items.ContainsKey(g.Key)).Select(g => new HospitalCartItemTotal(g.Key, items[g.Key].ItemCode, items[g.Key].Name, items[g.Key].NameEn, g.Sum(l => l.Units), inStore.GetValueOrDefault(g.Key)))
				.OrderBy(t => t.Code).ToList();
			HospitalCartFill? last = null;
			try { last = await _db.Set<HospitalCartFill>().AsNoTracking().Where(f => f.CompanyId == ctx.CompanyId && f.WardId == wardId).OrderByDescending(f => f.DoneAt).FirstOrDefaultAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }   // the slice not yet applied
			return new HospitalCartPreview(ward.ID, ward.Name, ward.NameEn, startUtc, endUtc, lines, totals, last, unitIds.Count > 0);
		}

		/// Fills the cart: a reviewed, prepared delivery per line to fill. Each line is its own act: one that fails is
		/// cancelled and reported, the others are sent.
		public async Task<(HospitalResult Result, HospitalCartFillResult? Fill)> FillAsync(BusinessContext ctx, int wardId, DateTime startUtc, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return (HospitalResult.Fail("forbidden"), null);
			var p = await PreviewAsync(ctx, wardId, startUtc, ct);
			if (p == null) return (HospitalResult.Fail("not_found"), null);
			var todo = p.Lines.Where(l => l.State == HospitalCartLineState.Fill).ToList();
			if (todo.Count == 0) return (HospitalResult.Fail("Nothing on this ward is due to be filled"), null);
			int sent = 0; decimal units = 0; var problems = new List<string>();
			foreach (var l in todo)
			{
				var made = await _routing.CreateAsync(ctx, new HospitalMedDeliveryInput { MedOrderId = l.OrderId, Priority = HospitalMedPriority.Routine, Via = HospitalMedVia.Porter, Quantity = l.Units }, employeeId, nowUtc, ct);
				if (!made.Ok) { problems.Add(l.Drug + ": " + made.Error); continue; }
				var id = made.Id!.Value;
				var verified = await _routing.StepAsync(ctx, id, new HospitalMedStepInput { Stage = HospitalMedStage.Verified }, employeeId, userId, nowUtc, ct);
				var prepared = verified.Ok ? await _routing.StepAsync(ctx, id, new HospitalMedStepInput { Stage = HospitalMedStage.Prepared }, employeeId, userId, nowUtc, ct) : verified;
				if (!prepared.Ok)
				{
					await _routing.CancelAsync(ctx, id, "Cart fill: " + (prepared.Error ?? ""), employeeId, userId, nowUtc, ct);   // never left half-way
					problems.Add(l.Drug + ": " + prepared.Error);
					continue;
				}
				sent++; units += l.Units;
			}
			var nos = await _db.Set<HospitalCartFill>().Where(f => f.CompanyId == ctx.CompanyId).Select(f => f.FillNo).ToListAsync(ct);
			var fill = new HospitalCartFill
			{
				CompanyId = ctx.CompanyId, FillNo = HospitalDentalPracticeRules.NextNo(nos, "CF-", 6), WardId = wardId, WindowStart = p.WindowStart, WindowEnd = p.WindowEnd,
				Patients = todo.Select(l => l.PatientId).Distinct().Count(), Deliveries = sent, Units = units, Skipped = p.Lines.Count - sent, DoneAt = nowUtc, DoneByEmployeeId = employeeId,
			};
			_db.Add(fill);
			await _db.SaveChangesAsync(ct);
			return (HospitalResult.Success(fill.ID), new HospitalCartFillResult(fill.ID, sent, p.Lines.Count - sent, problems));
		}
	}
}
