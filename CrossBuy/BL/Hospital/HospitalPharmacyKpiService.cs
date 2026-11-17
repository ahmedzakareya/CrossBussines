using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H151 — the pharmacy's indicators and its ABC/VEN analysis.
	//
	// THE INDICATORS, over a chosen period (30, 90 or 365 days): how many prescriptions were dispensed and refused; the
	// dispensing turnaround (median and 90th percentile, prescribed → dispensed) and the share within its priority's
	// target; the consumption (cost of what left for patients, less what came back); the stock value now, the stock
	// turns per year and the days of cover it gives; the expiry loss (expired stock destroyed); returns; prescriptions
	// still owing; and the month's interventions.
	//
	// ABC/VEN (WHO / MSH method). ABC ranks the medicines by their consumption value over the last 365 days: A = the
	// medicines making the first 80% of the value, B the next 15%, C the rest (and anything not consumed). VEN is the
	// drug master's clinical class (vital, essential, non-essential; unclassified counts as essential). The matrix sets
	// the attention: category I — AV, AE, AN, BV, CV — is watched closely and kept with a safety stock; II — BE, BN, CE —
	// is managed normally; III — CN — is kept lean.
	// ==========================================================================================

	public static class HospitalAbcVenRules
	{
		public const string A = "A", B = "B", C = "C";
		public static readonly IReadOnlyList<string> Classes = new[] { A, B, C };
		public static readonly IReadOnlyList<string> Vens = new[] { HospitalVen.Vital, HospitalVen.Essential, HospitalVen.NonEssential };
		public const string CategoryI = "I", CategoryII = "II", CategoryIII = "III";

		/// ABC by consumption value: a medicine is A while the value ranked before it is under 80% of the total, B under
		/// 95%, C after that. Nothing consumed is C. Ties keep their order by item id, so the result is stable.
		public static Dictionary<int, (string Abc, decimal SharePct)> Classify(IEnumerable<(int ItemId, decimal Value)> consumption)
		{
			var list = consumption.Select(x => (x.ItemId, Value: Math.Max(0, x.Value))).OrderByDescending(x => x.Value).ThenBy(x => x.ItemId).ToList();
			decimal total = list.Sum(x => x.Value), before = 0;
			var result = new Dictionary<int, (string, decimal)>();
			foreach (var (id, v) in list)
			{
				decimal share = total == 0 ? 0 : v / total * 100m, prior = total == 0 ? 100 : before / total * 100m;
				result[id] = (v <= 0 ? C : prior < 80 ? A : prior < 95 ? B : C, Math.Round(share, 2));
				before += v;
			}
			return result;
		}

		public static string Ven(string? ven) => ven is HospitalVen.Vital or HospitalVen.NonEssential ? ven : HospitalVen.Essential;

		public static string Category(string abc, string? ven) => (abc, Ven(ven)) switch
		{
			(A, _) or (B, HospitalVen.Vital) or (C, HospitalVen.Vital) => CategoryI,
			(C, HospitalVen.NonEssential) => CategoryIII,
			_ => CategoryII,
		};

		/// The p-th percentile (0–100) of minutes, nearest rank.
		public static int? Percentile(IReadOnlyList<double> values, double p)
		{
			if (values.Count == 0) return null;
			var s = values.OrderBy(x => x).ToList();
			int rank = (int)Math.Ceiling(p / 100.0 * s.Count);
			return (int)Math.Round(s[Math.Clamp(rank - 1, 0, s.Count - 1)]);
		}

		/// Days the stock on hand covers at the average daily consumption (null when nothing is consumed).
		public static int? DaysOfCover(decimal stock, decimal consumed, int days) => consumed <= 0 || days <= 0 ? null : (int)Math.Round(stock / (consumed / days));
	}

	public sealed record HospitalAbcVenCell(string Abc, string Ven, int Items, decimal Value);
	public sealed record HospitalAbcVenRow(int ItemId, string Code, string Name, string? NameEn, string Abc, string? Ven, string Category, decimal Value, decimal SharePct, decimal OnHand, decimal StockValue, int? DaysOfCover);
	public sealed record HospitalPharmacyKpis(bool Ready, int Days, string? StoreName, int Dispensed, int Rejected, int? MedianMinutes, int? P90Minutes, int? WithinTargetPct,
		decimal Consumption, decimal StockValue, decimal? TurnsPerYear, int? DaysOnHand, decimal ExpiryLoss, int Returns, decimal ReturnsValue, int Owing,
		int Interventions, int? AcceptedPct, IReadOnlyList<HospitalAbcVenCell> Matrix, decimal AnnualConsumption);

	public sealed class HospitalPharmacyKpiService
	{
		public static readonly IReadOnlyList<int> Periods = new[] { 30, 90, 365 };
		private readonly CrossDbContext _db;
		private readonly HospitalPharmacyService _pharmacy;
		public HospitalPharmacyKpiService(CrossDbContext db, HospitalPharmacyService pharmacy) { _db = db; _pharmacy = pharmacy; }

		/// Consumption cost per medicine since a moment: what left for patients less what came back.
		private async Task<Dictionary<int, decimal>> ConsumedAsync(BusinessContext ctx, DateTime sinceUtc, CancellationToken ct)
		{
			var moves = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == ctx.CompanyId && m.MovementDate >= sinceUtc
					&& ((m.SourceType == HospitalPharmacyService.MovementSource && m.Direction == -1) || (m.SourceType == HospitalReturnRules.ReturnSource && m.Direction == 1)))
				.GroupBy(m => m.ItemId).Select(g => new { g.Key, V = g.Sum(m => m.Direction == -1 ? m.TotalCost : -m.TotalCost) }).ToListAsync(ct);
			return moves.ToDictionary(x => x.Key, x => x.V);
		}

		private sealed record Universe(int? Store, string? StoreName, Dictionary<int, CrossBuy.Models.Context.Inventory.Item> Items, Dictionary<int, (decimal Qty, decimal Value)> Stock,
			Dictionary<int, decimal> Annual, Dictionary<int, (string Abc, decimal SharePct)> Abc, Dictionary<int, HospitalDrug> Masters);

		private async Task<Universe> UniverseAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct)
		{
			var settings = await _pharmacy.SettingsAsync(ctx, ct);
			int? store = settings.WarehouseId;
			var wh = store == null ? null : await _db.Warehouses.AsNoTracking().Where(w => w.ID == store && w.CompanyID == ctx.CompanyId).Select(w => new { w.Name, w.NameEn }).FirstOrDefaultAsync(ct);
			string? storeName = wh == null ? null : DisplayName.Of(wh.Name, wh.NameEn);   // in the reader's language
			var stock = store == null ? new() : await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == store && b.QtyOnHand > 0)
				.ToDictionaryAsync(b => b.ItemId, b => (b.QtyOnHand, b.TotalValue), ct);
			var annual = await ConsumedAsync(ctx, nowUtc.AddDays(-365), ct);
			var masterIds = await _db.Set<HospitalDrug>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId).Select(m => m.ItemId).ToListAsync(ct);
			var ids = stock.Keys.Concat(annual.Keys).Concat(masterIds).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, ct);
			var masters = await HospitalDrugMasterService.LookupAsync(_db, ctx.CompanyId, items.Keys.ToList(), ct);
			var abc = HospitalAbcVenRules.Classify(items.Keys.Select(id => (id, annual.GetValueOrDefault(id))));
			return new Universe(store, storeName, items, stock, annual, abc, masters);
		}

		public async Task<HospitalPharmacyKpis> KpisAsync(BusinessContext ctx, int days, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!Periods.Contains(days)) days = 90;
			var since = nowUtc.AddDays(-days);
			var u = await UniverseAsync(ctx, nowUtc, ct);
			var all = _db.HospitalDispenses.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId);
			var done = await all.Where(d => d.DispensedAt >= since).Select(d => new { d.PrescribedAt, DispensedAt = d.DispensedAt!.Value, d.Priority, d.Source }).ToListAsync(ct);
			var minutes = done.Select(d => Math.Max(0, (d.DispensedAt - d.PrescribedAt).TotalMinutes)).ToList();
			int? within = done.Count == 0 ? null : (int)Math.Round(100.0 * done.Count(d => (d.DispensedAt - d.PrescribedAt).TotalMinutes <= HospitalRxPriority.TargetMinutes(HospitalRxPriority.Effective(d.Priority, d.Source))) / done.Count);
			int rejected = await all.CountAsync(d => d.Status == HospitalDispenseStatus.Rejected && d.RejectedAt >= since, ct);
			int owing = await all.CountAsync(d => d.Status == HospitalDispenseStatus.Partial, ct);
			decimal consumption = (await ConsumedAsync(ctx, since, ct)).Values.Sum();
			decimal stockValue = u.Stock.Values.Sum(s => s.Value);
			decimal? turns = stockValue <= 0 ? null : Math.Round(consumption / days * 365m / stockValue, 2);
			decimal loss = 0; int returns = 0; decimal returnsValue = 0;
			try { loss = await _db.Set<HospitalStockDisposal>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.DoneAt >= since && x.Kind == HospitalDisposalKind.Destroyed && x.Reason == HospitalDisposalReason.Expired).SumAsync(x => (decimal?)x.Value, ct) ?? 0; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }
			try
			{
				var r = await _db.Set<HospitalDispenseReturn>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.At >= since).Select(x => x.Credited).ToListAsync(ct);
				returns = r.Count; returnsValue = r.Sum();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { }
			var (interventions, accepted) = await HospitalPharmacyReviewService.MonthAsync(_db, ctx.CompanyId, nowUtc, ct);

			var matrix = new List<HospitalAbcVenCell>();
			foreach (var a in HospitalAbcVenRules.Classes)
				foreach (var v in HospitalAbcVenRules.Vens)
				{
					var inCell = u.Items.Keys.Where(id => u.Abc[id].Abc == a && HospitalAbcVenRules.Ven(u.Masters.GetValueOrDefault(id)?.Ven) == v).ToList();
					matrix.Add(new HospitalAbcVenCell(a, v, inCell.Count, inCell.Sum(id => Math.Max(0, u.Annual.GetValueOrDefault(id)))));
				}
			return new HospitalPharmacyKpis(u.Store != null, days, u.StoreName, done.Count, rejected, HospitalAbcVenRules.Percentile(minutes, 50), HospitalAbcVenRules.Percentile(minutes, 90), within,
				consumption, stockValue, turns, HospitalAbcVenRules.DaysOfCover(stockValue, consumption, days), loss, returns, returnsValue, owing, interventions, accepted, matrix,
				u.Annual.Values.Where(x => x > 0).Sum());
		}

		/// The medicines with their class, searched and paged; `cell` = "AV"… narrows to one cell of the matrix, `category` = I/II/III.
		public async Task<(IReadOnlyList<HospitalAbcVenRow> Rows, int Total)> RowsAsync(BusinessContext ctx, string? q, string? cell, string? category, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			var u = await UniverseAsync(ctx, nowUtc, ct);
			var rows = u.Items.Values.Select(i =>
			{
				var (abc, share) = u.Abc[i.ID];
				var ven = u.Masters.GetValueOrDefault(i.ID)?.Ven;
				var (qty, value) = u.Stock.GetValueOrDefault(i.ID);
				var annual = Math.Max(0, u.Annual.GetValueOrDefault(i.ID));
				return new HospitalAbcVenRow(i.ID, i.ItemCode, i.Name, i.NameEn, abc, ven, HospitalAbcVenRules.Category(abc, ven), annual, share, qty, value, HospitalAbcVenRules.DaysOfCover(value, annual, 365));
			});
			if (!string.IsNullOrWhiteSpace(q))
			{
				var s = q.Trim();
				rows = rows.Where(r => r.Code.Contains(s, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || (r.NameEn ?? "").Contains(s, StringComparison.OrdinalIgnoreCase));
			}
			if (cell is { Length: 2 }) rows = rows.Where(r => r.Abc == cell[..1] && HospitalAbcVenRules.Ven(r.Ven) == cell[1..]);
			if (category is HospitalAbcVenRules.CategoryI or HospitalAbcVenRules.CategoryII or HospitalAbcVenRules.CategoryIII) rows = rows.Where(r => r.Category == category);
			var list = rows.OrderByDescending(r => r.Value).ThenBy(r => r.Code).ToList();
			return (list.Skip((page - 1) * size).Take(size).ToList(), list.Count);
		}
	}
}
