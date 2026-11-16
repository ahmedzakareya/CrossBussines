using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H132 — the pharmacy's own stock, inside the hospital. The medicines live in the inventory (the pharmacy store
	// chosen in the pharmacy settings, the items of the categories mapped as medicines); the dispensing screen only counted
	// them, and its "pharmacy stock" button left the hospital for the inventory's expiry page. This reads that store for the
	// pharmacist: every medicine with what is on hand, its nearest expiry and its reorder level; every batch with its expiry
	// and the days left; every movement in or out of the store, named by what caused it (a prescription dispensed, a
	// medication delivery, a dose returned, a session's kit, a receipt). Read only — the inventory stays the one writer.
	// ==========================================================================================
	public static class HospitalPharmacyStockState
	{
		public const string Ok = "Ok", Low = "Low", Out = "Out", Near = "Near", Expired = "Expired";
	}

	public sealed record HospitalPharmacyStockStats(bool Ready, string? StoreName, int NearDays, int Medicines, int OutOfStock, int Low, int NearBatches, int ExpiredBatches);
	public sealed record HospitalPharmacyMedRow(int ItemId, string Code, string Name, string NameEn, decimal OnHand, decimal? Reorder, DateTime? NearestExpiry, int Batches, string State);
	public sealed record HospitalPharmacyBatchRow(int ItemId, string Code, string Name, string NameEn, string BatchNo, DateTime? Expiry, decimal Qty, int? DaysLeft, string State);
	public sealed record HospitalPharmacyMoveRow(DateTime At, int ItemId, string Code, string Name, string NameEn, short Direction, decimal Qty, decimal UnitCost, string? SourceType, string? Reference, string? BatchNo);

	/// Why a stock movement happened, as a label key (pmv:…): the pharmacy stock screen and the controlled-drug ledger read
	/// the same map (H137).
	public static class HospitalStockSourceLabels
	{
		public static string Key(string? sourceType) => sourceType switch
		{
			HospitalPharmacyService.MovementSource => "pmv:Dispense",
			HospitalMedRoutingService.MovementSource => "pmv:Delivery",
			HospitalMedRoutingService.ReturnSource => "pmv:Return",
			"HospitalDialysis" or "HospitalChemo" or "HospitalRehab" or "HospitalSurgery" => "pmv:Kit",
			HospitalControlledRules.MovementSource => "pmv:Controlled",
			"Receipt" or "GoodsReceipt" or "PurchaseInvoice" => "pmv:Receipt",
			"PurchaseReturn" => "pmv:SupplierReturn",
			"StockWriteOff" => "pmv:WriteOff",
			"Adjustment" => "pmv:Adjustment",
			"Opening" => "pmv:Opening",
			"TransferIn" or "TransferOut" or "Transfer" => "pmv:Transfer",
			_ => "pmv:Other",
		};
	}

	public static class HospitalPharmacyStockRules
	{
		/// A batch's state on a day: expired, near (within the pharmacy's near-expiry days), fine — or nothing to say (no expiry).
		public static string BatchState(DateTime? expiry, DateTime today, int nearDays) =>
			expiry is not DateTime e ? HospitalPharmacyStockState.Ok : e.Date < today.Date ? HospitalPharmacyStockState.Expired : e.Date <= today.Date.AddDays(nearDays) ? HospitalPharmacyStockState.Near : HospitalPharmacyStockState.Ok;

		/// A medicine's state, the most urgent first: nothing on hand, an expired batch on the shelf, a batch near expiry,
		/// at or under its reorder level, fine.
		public static string MedicineState(decimal onHand, decimal? reorder, IEnumerable<string> batchStates)
		{
			var s = batchStates.ToList();
			if (onHand <= 0) return HospitalPharmacyStockState.Out;
			if (s.Contains(HospitalPharmacyStockState.Expired)) return HospitalPharmacyStockState.Expired;
			if (s.Contains(HospitalPharmacyStockState.Near)) return HospitalPharmacyStockState.Near;
			if (reorder is decimal r && onHand <= r) return HospitalPharmacyStockState.Low;
			return HospitalPharmacyStockState.Ok;
		}

		public static int? DaysLeft(DateTime? expiry, DateTime today) => expiry is DateTime e ? (int)(e.Date - today.Date).TotalDays : null;
	}

	public sealed class HospitalPharmacyStockService
	{
		private readonly CrossDbContext _db;
		private readonly HospitalPharmacyService _pharmacy;
		public HospitalPharmacyStockService(CrossDbContext db, HospitalPharmacyService pharmacy) { _db = db; _pharmacy = pharmacy; }

		private sealed record Snapshot(int? Store, string? StoreName, int NearDays, Dictionary<int, CrossBuy.Models.Context.Inventory.Item> Items, Dictionary<int, decimal> OnHand,
			Dictionary<int, decimal?> Reorder, List<(int ItemId, string BatchNo, DateTime? Expiry, decimal Qty)> Batches);

		private async Task<Snapshot> SnapshotAsync(BusinessContext ctx, CancellationToken ct)
		{
			var settings = await _pharmacy.SettingsAsync(ctx, ct);
			var meds = await _pharmacy.MedicineItemIdsAsync(ctx, ct);
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && meds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, ct);
			if (settings.WarehouseId is not int wh) return new Snapshot(null, null, settings.NearExpiryDays, items, new(), new(), new());
			var store = await HospitalKitStock.StoreNameAsync(_db, ctx.CompanyId, wh, ct);
			var onHand = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == wh && meds.Contains(b.ItemId))
				.GroupBy(b => b.ItemId).Select(g => new { g.Key, Q = g.Sum(x => x.QtyOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
			var reorder = (await _db.ItemWarehouseSettings.AsNoTracking().Where(s => s.WarehouseId == wh && meds.Contains(s.ItemId)).ToListAsync(ct))
				.GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.First().ReorderPoint ?? g.First().MinQty);
			var batches = await _pharmacy.BatchesAsync(ctx, wh, meds, ct);
			return new Snapshot(wh, store, settings.NearExpiryDays, items, onHand, reorder, batches);
		}

		private static List<HospitalPharmacyMedRow> Medicines(Snapshot s, DateTime today) => s.Items.Values.Select(i =>
		{
			var bs = s.Batches.Where(b => b.ItemId == i.ID).ToList();
			decimal on = s.OnHand.GetValueOrDefault(i.ID);
			var rp = s.Reorder.GetValueOrDefault(i.ID);
			return new HospitalPharmacyMedRow(i.ID, i.ItemCode, i.Name, i.NameEn ?? i.Name, on, rp, bs.Where(b => b.Expiry != null).Select(b => b.Expiry).Min(), bs.Count,
				HospitalPharmacyStockRules.MedicineState(on, rp, bs.Select(b => HospitalPharmacyStockRules.BatchState(b.Expiry, today, s.NearDays))));
		}).ToList();

		private static bool Matches(string? q, string code, string name, string nameEn) =>
			string.IsNullOrWhiteSpace(q) || code.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || nameEn.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase);

		public async Task<HospitalPharmacyStockStats> StatsAsync(BusinessContext ctx, DateTime today, CancellationToken ct = default)
		{
			var s = await SnapshotAsync(ctx, ct);
			if (s.Store == null) return new HospitalPharmacyStockStats(false, null, s.NearDays, s.Items.Count, 0, 0, 0, 0);
			var meds = Medicines(s, today);
			var states = s.Batches.Select(b => HospitalPharmacyStockRules.BatchState(b.Expiry, today, s.NearDays)).ToList();
			return new HospitalPharmacyStockStats(true, s.StoreName, s.NearDays, meds.Count, meds.Count(m => m.State == HospitalPharmacyStockState.Out),
				meds.Count(m => m.State == HospitalPharmacyStockState.Low), states.Count(x => x == HospitalPharmacyStockState.Near), states.Count(x => x == HospitalPharmacyStockState.Expired));
		}

		/// Every medicine — searched by code or name, filtered by state (out / low / near / expired), most urgent first.
		public async Task<(IReadOnlyList<HospitalPharmacyMedRow> Rows, int Total)> MedicinesAsync(BusinessContext ctx, string? q, string? filter, int page, int size, DateTime today, CancellationToken ct = default)
		{
			var s = await SnapshotAsync(ctx, ct);
			var order = new[] { HospitalPharmacyStockState.Out, HospitalPharmacyStockState.Expired, HospitalPharmacyStockState.Near, HospitalPharmacyStockState.Low, HospitalPharmacyStockState.Ok };
			var rows = Medicines(s, today).Where(m => Matches(q, m.Code, m.Name, m.NameEn) && (string.IsNullOrEmpty(filter) || m.State == filter))
				.OrderBy(m => Array.IndexOf(order, m.State)).ThenBy(m => m.Code).ToList();
			return (rows.Skip((page - 1) * size).Take(size).ToList(), rows.Count);
		}

		/// Every batch on the shelf — searched by medicine or batch number, filtered (near / expired), the nearest expiry first.
		public async Task<(IReadOnlyList<HospitalPharmacyBatchRow> Rows, int Total)> BatchesAsync(BusinessContext ctx, string? q, string? filter, int page, int size, DateTime today, CancellationToken ct = default)
		{
			var s = await SnapshotAsync(ctx, ct);
			var rows = s.Batches.Where(b => s.Items.ContainsKey(b.ItemId)).Select(b =>
			{
				var i = s.Items[b.ItemId];
				return new HospitalPharmacyBatchRow(i.ID, i.ItemCode, i.Name, i.NameEn ?? i.Name, b.BatchNo, b.Expiry, b.Qty, HospitalPharmacyStockRules.DaysLeft(b.Expiry, today),
					HospitalPharmacyStockRules.BatchState(b.Expiry, today, s.NearDays));
			}).Where(r => (Matches(q, r.Code, r.Name, r.NameEn) || (!string.IsNullOrWhiteSpace(q) && r.BatchNo.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))) && (string.IsNullOrEmpty(filter) || r.State == filter))
			.OrderBy(r => r.Expiry ?? DateTime.MaxValue).ThenBy(r => r.Code).ToList();
			return (rows.Skip((page - 1) * size).Take(size).ToList(), rows.Count);
		}

		/// Every movement in or out of the pharmacy store for a medicine, the newest first, each named by what caused it.
		public async Task<(IReadOnlyList<HospitalPharmacyMoveRow> Rows, int Total)> MovementsAsync(BusinessContext ctx, string? q, string? filter, int page, int size, CancellationToken ct = default)
		{
			var settings = await _pharmacy.SettingsAsync(ctx, ct);
			if (settings.WarehouseId is not int wh) return (Array.Empty<HospitalPharmacyMoveRow>(), 0);
			var meds = await _pharmacy.MedicineItemIdsAsync(ctx, ct);
			var query = from m in _db.StockMovements.AsNoTracking()
						join i in _db.Items.AsNoTracking() on m.ItemId equals i.ID
						where m.CompanyID == ctx.CompanyId && m.WarehouseId == wh && meds.Contains(m.ItemId)
						select new { m, i };
			if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(x => x.i.ItemCode.Contains(t) || x.i.Name.Contains(t) || (x.i.NameEn != null && x.i.NameEn.Contains(t))); }
			if (filter == "in") query = query.Where(x => x.m.Direction > 0); else if (filter == "out") query = query.Where(x => x.m.Direction < 0);
			int total = await query.CountAsync(ct);
			var page1 = await query.OrderByDescending(x => x.m.MovementDate).ThenByDescending(x => x.m.ID).Skip((page - 1) * size).Take(size).ToListAsync(ct);
			// what caused each: a prescription dispensed (its RX), a medication delivery or its return (its MR)
			var rxIds = page1.Where(x => x.m.SourceType == HospitalPharmacyService.MovementSource && x.m.SourceId != null).Select(x => x.m.SourceId!.Value).Distinct().ToList();
			var rx = rxIds.Count == 0 ? new Dictionary<int, string>() : await _db.HospitalDispenses.AsNoTracking().Where(d => rxIds.Contains(d.ID)).ToDictionaryAsync(d => d.ID, d => d.RxNo, ct);
			var mrIds = page1.Where(x => (x.m.SourceType == HospitalMedRoutingService.MovementSource || x.m.SourceType == HospitalMedRoutingService.ReturnSource) && x.m.SourceId != null).Select(x => x.m.SourceId!.Value).Distinct().ToList();
			var mr = mrIds.Count == 0 ? new Dictionary<int, string>() : await _db.Set<HospitalMedDelivery>().AsNoTracking().Where(d => mrIds.Contains(d.ID)).ToDictionaryAsync(d => d.ID, d => d.DeliveryNo, ct);
			var bids = page1.Where(x => x.m.BatchId != null).Select(x => x.m.BatchId!.Value).Distinct().ToList();
			var bno = bids.Count == 0 ? new Dictionary<int, string>() : await _db.StockBatches.AsNoTracking().Where(b => bids.Contains(b.ID)).ToDictionaryAsync(b => b.ID, b => b.BatchNo, ct);
			var rows = page1.Select(x =>
			{
				string? reference = x.m.SourceId is int sid
					? (rx.TryGetValue(sid, out var r1) && x.m.SourceType == HospitalPharmacyService.MovementSource ? r1
						: mr.TryGetValue(sid, out var r2) && (x.m.SourceType == HospitalMedRoutingService.MovementSource || x.m.SourceType == HospitalMedRoutingService.ReturnSource) ? r2
						: "#" + sid)
					: x.m.MovementNo;
				return new HospitalPharmacyMoveRow(x.m.MovementDate, x.i.ID, x.i.ItemCode, x.i.Name, x.i.NameEn ?? x.i.Name, x.m.Direction, x.m.QtyBase, x.m.UnitCost, x.m.SourceType, reference,
					x.m.BatchId is int b && bno.TryGetValue(b, out var n) ? n : null);
			}).ToList();
			return (rows, total);
		}
	}
}
