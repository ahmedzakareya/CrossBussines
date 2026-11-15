using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H135 — expiry and disposal (see HospitalStockDisposal).
	//
	//   · WHAT TO DEAL WITH:
	//       - in the pharmacy store: every batch that has expired, or will within the pharmacy's near-expiry days;
	//       - in the quarantine store: everything (recalled or suspect).
	//     Each with its value at the store's moving-average cost and, when it is under recall, the recall.
	//   · DESTROY: the inventory's own write-off of that batch, from that store. The stock writer issues the batch even
	//     expired, and posts Dr write-off expense / Cr inventory. A witness (anyone else with a sign-in) confirms with their
	//     own password. The value lost is the write-off's cost.
	//   · RETURN TO THE SUPPLIER: the payables' purchase return, a debit note against the supplier at cost.
	//       - The purchase return picks its batch nearest-expiry-first and never an expired one.
	//       - So a return is refused for an expired batch (that is destroyed, or credited outside), and when another batch
	//         of the medicine would leave the store first.
	//       - After the return, the movements are read back: had any other batch left, the whole act is undone.
	//   · Both run in one transaction with the record. When nothing of a recalled batch is left in any store, its recall
	//     closes itself, naming the document.
	// No stock or ledger rule lives here: the stock writer and the payables do the moving and the posting.
	// ==========================================================================================

	public static class HospitalDisposalState
	{
		public const string Expired = "Expired", Near = "Near", Quarantined = "Quarantined";
	}

	public sealed record HospitalDisposalCandidate(int ItemId, string Code, string Name, string? NameEn, string BatchNo, DateTime? Expiry, int? DaysLeft,
		int WarehouseId, string StoreCode, string StoreName, string? StoreNameEn, decimal Qty, decimal UnitCost, decimal Value, string State, int? RecallId, string? RecallNo);

	public sealed record HospitalDisposalRow(HospitalStockDisposal D, string Code, string Name, string? NameEn, string StoreCode, string StoreName, string? StoreNameEn,
		string? Vendor, string? DoneBy, string? DoneByEn, string? Witness, string? WitnessEn, string? WitnessPhoto);

	public sealed record HospitalDisposalStats(bool Ready, int ExpiredBatches, decimal ExpiredValue, decimal NearValue, decimal QuarantineValue, decimal DestroyedValue, decimal ReturnedValue);

	public sealed class HospitalDisposalInput
	{
		public int ItemId { get; set; }
		public string? BatchNo { get; set; }
		public int WarehouseId { get; set; }
		public decimal Qty { get; set; }
		public string? Reason { get; set; }
		/// Destroying: how, and the witness who confirms with their own password.
		public string? Method { get; set; }
		public int? WitnessId { get; set; }
		public string? Password { get; set; }
		/// Returning: to whom.
		public int? VendorId { get; set; }
		public string? Note { get; set; }
	}

	public static class HospitalDisposalRules
	{
		/// Why the batch is on the list: in quarantine, expired, near expiry; null when it is not.
		public static string? State(DateTime? expiry, DateTime today, int nearDays, bool inQuarantine)
		{
			if (inQuarantine) return HospitalDisposalState.Quarantined;
			var s = HospitalPharmacyStockRules.BatchState(expiry, today, nearDays);
			return s == HospitalPharmacyStockState.Expired ? HospitalDisposalState.Expired : s == HospitalPharmacyStockState.Near ? HospitalDisposalState.Near : null;
		}

		public static HospitalResult? ValidateDestroy(HospitalDisposalInput i, int? selfEmployeeId)
		{
			if (i.Qty <= 0) return HospitalResult.Fail("Enter the quantity");
			if (!HospitalDisposalReason.All.Contains(i.Reason ?? "")) return HospitalResult.Fail("Choose the reason");
			if (!HospitalDestructionMethod.All.Contains(i.Method ?? "")) return HospitalResult.Fail("Choose how it is destroyed");
			if (i.WitnessId is not int w) return HospitalResult.Fail("Choose the witness");
			if (w == selfEmployeeId) return HospitalResult.Fail("The witness must be someone else");
			if (string.IsNullOrEmpty(i.Password)) return HospitalResult.Fail("The witness enters their password");
			return null;
		}

		public static HospitalResult? ValidateReturn(HospitalDisposalInput i)
		{
			if (i.Qty <= 0) return HospitalResult.Fail("Enter the quantity");
			if (!HospitalDisposalReason.All.Contains(i.Reason ?? "")) return HospitalResult.Fail("Choose the reason");
			if (i.VendorId == null) return HospitalResult.Fail("Choose the supplier");
			return null;
		}

		/// The purchase return takes the store's batches nearest-expiry-first, never an expired one. The returned batch must
		/// therefore be unexpired, the first in that order, and hold the quantity.
		public static HospitalResult? ReturnTakesThisBatch(IReadOnlyList<(string BatchNo, DateTime? Expiry, decimal Qty)> storeBatches, string batchNo, decimal qty, DateTime today)
		{
			var mine = storeBatches.FirstOrDefault(b => b.BatchNo == batchNo);
			if (mine.BatchNo == null || mine.Qty < qty) return HospitalResult.Fail("The store does not hold that much of this batch");
			if (mine.Expiry is DateTime e && e.Date < today) return HospitalResult.Fail("An expired batch is destroyed, not returned to the supplier");
			var first = storeBatches.Where(b => b.Qty > 0 && (b.Expiry == null || b.Expiry.Value.Date >= today))
				.OrderBy(b => b.Expiry == null ? 1 : 0).ThenBy(b => b.Expiry).ThenBy(b => b.BatchNo).First();
			return first.BatchNo == batchNo ? null : HospitalResult.Fail("Another batch of this medicine would leave the store first — quarantine this batch and return it from there");
		}

		/// The inventory's write-off reasons are Damaged | Expired | Lost | Other.
		public static string WriteOffReason(string reason) => reason switch
		{
			HospitalDisposalReason.Expired or HospitalDisposalReason.NearExpiry => "Expired",
			HospitalDisposalReason.Damaged => "Damaged",
			_ => "Other",
		};
	}

	public sealed class HospitalStockDisposalService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IStockService _stock;
		private readonly IPayableService _payables;
		private readonly IHospitalWitness _witness;
		private readonly HospitalPharmacyService _pharmacy;

		public HospitalStockDisposalService(CrossDbContext db, IHospitalAccessService access, IStockService stock, IPayableService payables, IHospitalWitness witness, HospitalPharmacyService pharmacy)
		{ _db = db; _access = access; _stock = stock; _payables = payables; _witness = witness; _pharmacy = pharmacy; }

		private IQueryable<HospitalStockDisposal> Disposals(BusinessContext ctx) => _db.Set<HospitalStockDisposal>().Where(d => d.CompanyId == ctx.CompanyId);

		// ====================================================================================
		// READS
		// ====================================================================================

		/// Every batch to deal with, in the pharmacy store and the quarantine store.
		public async Task<List<HospitalDisposalCandidate>> CandidatesAsync(BusinessContext ctx, DateTime today, CancellationToken ct = default)
		{
			var s = await _pharmacy.SettingsAsync(ctx, ct);
			var stores = new[] { s.WarehouseId, s.QuarantineWarehouseId }.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
			if (stores.Count == 0) return new();
			var sums = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && stores.Contains(m.WarehouseId) && m.BatchId != null)
				.GroupBy(m => new { m.WarehouseId, m.ItemId, m.BatchId }).Select(g => new { g.Key.WarehouseId, g.Key.ItemId, g.Key.BatchId, Qty = g.Sum(m => m.Direction * m.QtyBase) })
				.Where(x => x.Qty > 0).ToListAsync(ct);
			var bids = sums.Select(x => x.BatchId!.Value).Distinct().ToList();
			var batches = await _db.StockBatches.AsNoTracking().Where(b => bids.Contains(b.ID)).ToDictionaryAsync(b => b.ID, ct);
			var list = new List<(int Wh, int Item, string Batch, DateTime? Exp, decimal Qty, string State)>();
			foreach (var x in sums)
			{
				var b = batches[x.BatchId!.Value];
				var state = HospitalDisposalRules.State(b.ExpiryDate, today, s.NearExpiryDays, x.WarehouseId == s.QuarantineWarehouseId);
				if (state != null) list.Add((x.WarehouseId, x.ItemId, b.BatchNo, b.ExpiryDate, x.Qty, state));
			}
			if (list.Count == 0) return new();
			var itemIds = list.Select(l => l.Item).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, ct);
			var whs = await _db.Warehouses.AsNoTracking().Where(w => stores.Contains(w.ID)).ToDictionaryAsync(w => w.ID, ct);
			var cost = (await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && stores.Contains(b.WarehouseId) && itemIds.Contains(b.ItemId)).ToListAsync(ct))
				.ToDictionary(b => (b.WarehouseId, b.ItemId), b => b.QtyOnHand > 0 ? b.TotalValue / b.QtyOnHand : 0m);
			var recalls = await _db.Set<HospitalLotRecall>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.Status == HospitalLotRecallStatus.Quarantined && itemIds.Contains(r.ItemId))
				.Select(r => new { r.ID, r.RecallNo, r.ItemId, r.BatchNo }).ToListAsync(ct);
			return list.Where(l => items.ContainsKey(l.Item) && whs.ContainsKey(l.Wh)).Select(l =>
			{
				var it = items[l.Item]; var w = whs[l.Wh]; var uc = cost.GetValueOrDefault((l.Wh, l.Item));
				var rc = recalls.FirstOrDefault(r => r.ItemId == l.Item && r.BatchNo == l.Batch);
				return new HospitalDisposalCandidate(l.Item, it.ItemCode, it.Name, it.NameEn, l.Batch, l.Exp, HospitalPharmacyStockRules.DaysLeft(l.Exp, today),
					l.Wh, w.Code, w.Name, w.NameEn, l.Qty, Math.Round(uc, 4), Math.Round(uc * l.Qty, 3), l.State, rc?.ID, rc?.RecallNo);
			}).OrderBy(c => c.State == HospitalDisposalState.Expired ? 0 : c.State == HospitalDisposalState.Quarantined ? 1 : 2).ThenBy(c => c.Expiry).ToList();
		}

		public async Task<(IReadOnlyList<HospitalDisposalCandidate> Rows, int Total)> CandidatesPageAsync(BusinessContext ctx, string? q, string? state, int page, int size, DateTime today, CancellationToken ct = default)
		{
			IEnumerable<HospitalDisposalCandidate> rows = await CandidatesAsync(ctx, today, ct);
			if (!string.IsNullOrEmpty(state)) rows = rows.Where(r => r.State == state);
			if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); rows = rows.Where(r => new[] { r.Code, r.Name, r.NameEn, r.BatchNo, r.RecallNo }.Any(x => x != null && x.Contains(s, StringComparison.OrdinalIgnoreCase))); }
			var list = rows.ToList();
			return (list.Skip((page - 1) * size).Take(size).ToList(), list.Count);
		}

		public async Task<HospitalDisposalStats> StatsAsync(BusinessContext ctx, DateTime today, CancellationToken ct = default)
		{
			var s = await _pharmacy.SettingsAsync(ctx, ct);
			var c = await CandidatesAsync(ctx, today, ct);
			var yearStart = new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			var done = await Disposals(ctx).AsNoTracking().Where(d => d.DoneAt >= yearStart).Select(d => new { d.Kind, d.Value }).ToListAsync(ct);
			return new HospitalDisposalStats(s.WarehouseId != null, c.Count(x => x.State == HospitalDisposalState.Expired), c.Where(x => x.State == HospitalDisposalState.Expired).Sum(x => x.Value),
				c.Where(x => x.State == HospitalDisposalState.Near).Sum(x => x.Value), c.Where(x => x.State == HospitalDisposalState.Quarantined).Sum(x => x.Value),
				done.Where(d => d.Kind == HospitalDisposalKind.Destroyed).Sum(d => d.Value), done.Where(d => d.Kind == HospitalDisposalKind.Returned).Sum(d => d.Value));
		}

		public async Task<(IReadOnlyList<HospitalDisposalRow> Rows, int Total)> DoneAsync(BusinessContext ctx, string? q, string? kind, int page, int size, CancellationToken ct = default)
		{
			var query = from d in Disposals(ctx).AsNoTracking()
						join i in _db.Items.AsNoTracking() on d.ItemId equals i.ID
						join w in _db.Warehouses.AsNoTracking() on d.WarehouseId equals w.ID
						select new { d, i.ItemCode, i.Name, i.NameEn, wCode = w.Code, wName = w.Name, wNameEn = w.NameEn };
			if (HospitalDisposalKind.All.Contains(kind ?? "")) query = query.Where(x => x.d.Kind == kind);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var s = q.Trim();
				query = query.Where(x => x.d.DisposalNo.Contains(s) || x.d.BatchNo.Contains(s) || x.ItemCode.Contains(s) || x.Name.Contains(s) || (x.NameEn != null && x.NameEn.Contains(s)) || (x.d.DocumentNo != null && x.d.DocumentNo.Contains(s)));
			}
			int total = await query.CountAsync(ct);
			var rows = await query.OrderByDescending(x => x.d.DoneAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
			var emp = rows.SelectMany(r => new[] { r.d.DoneByEmployeeId, r.d.WitnessEmployeeId }).Where(e => e != null).Select(e => e!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => emp.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).ToDictionaryAsync(e => e.ID, e => (e.FullName, e.FullNameEn), ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, emp, ct);
			var vids = rows.Where(r => r.d.VendorId != null).Select(r => r.d.VendorId!.Value).Distinct().ToList();
			var vendors = await _db.Vendors.AsNoTracking().Where(v => vids.Contains(v.ID) && v.CompanyID == ctx.CompanyId).ToDictionaryAsync(v => v.ID, v => v.Name, ct);
			return (rows.Select(r =>
			{
				var by = r.d.DoneByEmployeeId is int b && people.TryGetValue(b, out var pb) ? pb : default;
				var wi = r.d.WitnessEmployeeId is int w && people.TryGetValue(w, out var pw) ? pw : default;
				return new HospitalDisposalRow(r.d, r.ItemCode, r.Name, r.NameEn, r.wCode, r.wName, r.wNameEn, r.d.VendorId is int v ? vendors.GetValueOrDefault(v) : null,
					by.FullName, by.FullNameEn, wi.FullName, wi.FullNameEn, r.d.WitnessEmployeeId is int w2 ? photos.GetValueOrDefault(w2) : null);
			}).ToList(), total);
		}

		/// The supplier a batch came from, when a goods receipt brought it in.
		public async Task<int?> SupplierOfAsync(BusinessContext ctx, int itemId, string batchNo, CancellationToken ct = default)
			=> await (from l in _db.GoodsReceiptLines.AsNoTracking()
					  join g in _db.GoodsReceipts.AsNoTracking() on l.GoodsReceiptId equals g.ID
					  where g.CompanyID == ctx.CompanyId && l.ItemId == itemId && l.BatchNo == batchNo && g.VendorId != null
					  orderby g.ID descending
					  select g.VendorId).FirstOrDefaultAsync(ct);

		// ====================================================================================
		// WRITES
		// ====================================================================================

		private async Task<List<(string BatchNo, DateTime? Expiry, decimal Qty)>> StoreBatchesAsync(BusinessContext ctx, int warehouseId, int itemId, CancellationToken ct)
			=> (await _pharmacy.BatchesAsync(ctx, warehouseId, new[] { itemId }, ct)).Select(b => (b.BatchNo, b.Expiry, b.Qty)).ToList();

		/// The pharmacy store or the quarantine store, and the batch's expiry; or the refusal.
		private async Task<(HospitalPharmacySettings? S, DateTime? Expiry, decimal Held, HospitalResult? Refusal)> CheckAsync(BusinessContext ctx, HospitalDisposalInput i, CancellationToken ct)
		{
			var s = await _pharmacy.SettingsAsync(ctx, ct);
			if (i.WarehouseId != s.WarehouseId && i.WarehouseId != s.QuarantineWarehouseId) return (null, null, 0, HospitalResult.Fail("not_found"));
			i.BatchNo = i.BatchNo?.Trim();
			var b = (await StoreBatchesAsync(ctx, i.WarehouseId, i.ItemId, ct)).FirstOrDefault(x => x.BatchNo == i.BatchNo);
			if (b.BatchNo == null) return (null, null, 0, HospitalResult.Fail("not_found"));
			if (b.Qty < i.Qty) return (null, null, 0, HospitalResult.Fail("The store does not hold that much of this batch"));
			return (s, b.Expiry, b.Qty, null);
		}

		private async Task<string> NextNoAsync(BusinessContext ctx, CancellationToken ct)
			=> HospitalDentalPracticeRules.NextNo(await Disposals(ctx).Select(d => d.DisposalNo).ToListAsync(ct), "DSP-", 6);

		/// When nothing of a recalled batch is left in any store, its recall ends, naming the act.
		private async Task<int?> CloseRecallIfGoneAsync(BusinessContext ctx, HospitalStockDisposal d, CancellationToken ct)
		{
			var r = await _db.Set<HospitalLotRecall>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.ItemId == d.ItemId && x.BatchNo == d.BatchNo && x.Status == HospitalLotRecallStatus.Quarantined, ct);
			if (r == null) return null;
			var batchId = await _db.StockBatches.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.ItemId == d.ItemId && b.BatchNo == d.BatchNo).Select(b => (int?)b.ID).FirstOrDefaultAsync(ct);
			var left = batchId == null ? 0 : await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.BatchId == batchId).SumAsync(m => (decimal?)(m.Direction * m.QtyBase), ct) ?? 0;
			if (left <= 0)
			{
				r.Status = HospitalLotRecallStatus.Closed; r.EndedAt = d.DoneAt; r.EndedByEmployeeId = d.DoneByEmployeeId;
				r.EndNote = (d.Kind == HospitalDisposalKind.Destroyed ? "Destroyed " : "Returned to the supplier ") + d.DisposalNo + (d.DocumentNo != null ? " (" + d.DocumentNo + ")" : "");
			}
			return r.ID;
		}

		public async Task<HospitalResult> DestroyAsync(BusinessContext ctx, HospitalDisposalInput input, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			if (input == null) return HospitalResult.Fail("not_found");
			var refusal = HospitalDisposalRules.ValidateDestroy(input, employeeId);
			if (refusal != null) return refusal;
			var (_, expiry, _, notHere) = await CheckAsync(ctx, input, ct);
			if (notHere != null) return notHere;
			if (!await _db.Employee.AnyAsync(e => e.ID == input.WitnessId && e.EmpCompanyID == ctx.CompanyId && e.IsActive, ct)) return HospitalResult.Fail("not_found");
			if (!await _witness.VerifyAsync(ctx.CompanyId, input.WitnessId!.Value, input.Password!, ct)) return HospitalResult.Fail("The witness's password is not correct");
			var item = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == input.ItemId && i.CompanyID == ctx.CompanyId, ct);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var no = await NextNoAsync(ctx, ct);
			var (ok, error, docNo, docId, _) = await _stock.WriteOffAsync(ctx.CompanyId, input.WarehouseId, nowUtc, HospitalDisposalRules.WriteOffReason(input.Reason!),
				"Pharmacy " + no + " — batch " + input.BatchNo + " destroyed (" + input.Method + ")",
				new List<WriteOffLineInput> { new() { ItemId = input.ItemId, Qty = input.Qty, UoMId = item.BaseUoMId, BatchNo = input.BatchNo, Reason = HospitalDisposalRules.WriteOffReason(input.Reason!) } }, userId);
			if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The inventory refused the write-off: {0}", error ?? ""); }
			var value = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == ctx.CompanyId && m.SourceId == docId && (m.SourceType == "StockWriteOff" || m.SourceType == "Adjustment") && m.ItemId == input.ItemId && m.Direction < 0)
				.SumAsync(m => (decimal?)m.TotalCost, ct) ?? 0;
			var d = new HospitalStockDisposal
			{
				CompanyId = ctx.CompanyId, DisposalNo = no, Kind = HospitalDisposalKind.Destroyed, ItemId = input.ItemId, BatchNo = input.BatchNo!, Expiry = expiry,
				WarehouseId = input.WarehouseId, Qty = input.Qty, Value = value, Reason = input.Reason!, Method = input.Method, DocumentNo = docNo, DocumentId = docId,
				WitnessEmployeeId = input.WitnessId, DoneByEmployeeId = employeeId, DoneAt = nowUtc, Note = Clean(input.Note),
			};
			_db.Add(d);
			d.RecallId = await CloseRecallIfGoneAsync(ctx, d, ct);
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(d.ID);
		}

		public async Task<HospitalResult> ReturnAsync(BusinessContext ctx, HospitalDisposalInput input, int? employeeId, DateTime nowUtc, DateTime today, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			if (input == null) return HospitalResult.Fail("not_found");
			var refusal = HospitalDisposalRules.ValidateReturn(input);
			if (refusal != null) return refusal;
			var (_, expiry, _, notHere) = await CheckAsync(ctx, input, ct);
			if (notHere != null) return notHere;
			if (!await _db.Vendors.AnyAsync(v => v.ID == input.VendorId && v.CompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var takes = HospitalDisposalRules.ReturnTakesThisBatch(await StoreBatchesAsync(ctx, input.WarehouseId, input.ItemId, ct), input.BatchNo!, input.Qty, today);
			if (takes != null) return takes;
			var item = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == input.ItemId && i.CompanyID == ctx.CompanyId, ct);
			// the stock leaving on a return books against the category's goods-received account (GRNI), which the debit note
			// clears. With none linked, the stock writer falls back to the category's adjustment account and the two never meet.
			var cat = await _db.ItemCategories.AsNoTracking().Where(c => c.ID == item.ItemCategoryId).Select(c => new { c.GrniAccountId, c.Name, c.NameEn }).FirstOrDefaultAsync(ct);
			if (cat?.GrniAccountId == null) return HospitalResult.Fail("Link the goods-received (GRNI) account of {0}'s inventory category first", DisplayName.Of(item.Name, item.NameEn ?? item.Name));
			var batchId = await _db.StockBatches.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.ItemId == input.ItemId && b.BatchNo == input.BatchNo).Select(b => b.ID).FirstAsync(ct);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var no = await NextNoAsync(ctx, ct);
			var (ok, error, ret) = await _payables.CreatePurchaseReturnAsync(ctx.CompanyId, input.VendorId!.Value, null, nowUtc,
				new List<PurchaseLineInput> { new() { ItemId = input.ItemId, WarehouseId = input.WarehouseId, Qty = input.Qty, TaxRate = 0, ItemDescription = DisplayName.Of(item.Name, item.NameEn) + " · " + input.BatchNo } },
				"Pharmacy " + no + " — batch " + input.BatchNo + " returned to the supplier", null);
			if (!ok || ret == null) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The supplier return was refused: {0}", error ?? ""); }
			// the purchase return chose its batch itself: read back what left, and undo everything if it was not this batch
			var moved = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.SourceType == "PurchaseReturn" && m.SourceId == ret.ID && m.Direction < 0)
				.Select(m => new { m.BatchId, m.QtyBase }).ToListAsync(ct);
			if (moved.Count == 0 || moved.Any(m => m.BatchId != batchId))
			{ await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("Another batch of this medicine would leave the store first — quarantine this batch and return it from there"); }
			var d = new HospitalStockDisposal
			{
				CompanyId = ctx.CompanyId, DisposalNo = no, Kind = HospitalDisposalKind.Returned, ItemId = input.ItemId, BatchNo = input.BatchNo!, Expiry = expiry,
				WarehouseId = input.WarehouseId, Qty = input.Qty, Value = ret.SubTotal, Reason = input.Reason!, VendorId = input.VendorId, DocumentNo = ret.ReturnNo, DocumentId = ret.ID,
				DoneByEmployeeId = employeeId, DoneAt = nowUtc, Note = Clean(input.Note),
			};
			_db.Add(d);
			d.RecallId = await CloseRecallIfGoneAsync(ctx, d, ct);
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(d.ID);
		}

		private static string? Clean(string? s) { s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s.Length > 400 ? s[..400] : s; }
	}
}
