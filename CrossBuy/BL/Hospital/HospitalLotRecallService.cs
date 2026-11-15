using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H134 — batch recalls (see HospitalLotRecall).
	//
	//   · OPEN: the pharmacist names the batch, the class, who issued the notice and why. In ONE transaction the recall is
	//     written and every unit of the batch, in every store that holds it, is moved into the quarantine store through the
	//     inventory's own transfer (IStockService.TransferAsync, one transfer per store): FEFO never looks there, so nothing
	//     of it can be dispensed again. A store's move that fails undoes the whole recall.
	//   · SWEEP: units that reach a store later (a dose returned from a ward, a late receipt) are moved in again.
	//   · RELEASE: the notice was withdrawn: the quarantined units go back to the pharmacy store.
	//   · CLOSE: the batch was returned to the supplier or destroyed (H135 posts that); the note says which.
	//   · TRACE, read from the batch's own stock movements, so no path is forgotten:
	//       - a prescription dispensed (HospitalDispense → its patient);
	//       - a dose delivered to the ward, net of a dose returned (HospitalMedDelivery / HospitalMedReturn → its patient);
	//       - a session's kit (dialysis, chemotherapy, physiotherapy → the session's patient).
	//     A patient whose doses all came back is no longer on the list.
	// No stock rule lives here: the stock writer moves the units, the recall remembers why.
	// ==========================================================================================

	public sealed record HospitalLotRecallRow(int Id, string RecallNo, int ItemId, string Code, string Name, string? NameEn, string BatchNo, DateTime? Expiry,
		string RecallClass, string Source, string? Reference, string Status, decimal Quantity, DateTime OpenedAt);

	public sealed record HospitalLotRecallStats(bool Ready, int Open, int ClassIOpen, decimal UnitsHeld, int Closed);

	public sealed record HospitalLotRecallPlace(int WarehouseId, string Code, string Name, string? NameEn, decimal Qty, bool Quarantine);

	public sealed record HospitalLotRecallPatient(int PatientId, string Name, string? NameEn, string Mrn, string? Photo, string? Phone, decimal Qty, DateTime LastAt, IReadOnlyList<string> Ways);

	public sealed record HospitalLotRecallView(HospitalLotRecall Recall, string ItemCode, string ItemName, string? ItemNameEn, DateTime? Expiry,
		IReadOnlyList<HospitalLotRecallPlace> Places, IReadOnlyList<HospitalLotRecallPatient> Patients, IReadOnlyDictionary<int, string> People, string? QuarantineStore);

	public sealed class HospitalLotRecallInput
	{
		public int ItemId { get; set; }
		public string? BatchNo { get; set; }
		public string? RecallClass { get; set; }
		public string? Source { get; set; }
		public string? Reference { get; set; }
		public string? Reason { get; set; }
	}

	/// One issue of the batch toward a patient (Direction -1) or back from one (+1), as the movements record it.
	public sealed record HospitalLotRecallIssue(int PatientId, int Direction, decimal Qty, DateTime At, string Way);

	public static class HospitalLotRecallWays
	{
		public const string Dispensed = "Dispensed", Delivered = "Delivered", Kit = "Kit";
	}

	public static class HospitalLotRecallRules
	{
		public static HospitalResult? Validate(HospitalLotRecallInput i)
		{
			i.BatchNo = i.BatchNo?.Trim(); i.Reference = Clean(i.Reference, 100); i.Reason = Clean(i.Reason, 400);
			if (string.IsNullOrEmpty(i.BatchNo)) return HospitalResult.Fail("Choose the batch");
			if (!HospitalLotRecallClass.All.Contains(i.RecallClass ?? "")) return HospitalResult.Fail("Choose the recall class");
			if (!HospitalLotRecallSource.All.Contains(i.Source ?? "")) return HospitalResult.Fail("Choose who issued the recall");
			if (i.Reason == null) return HospitalResult.Fail("Write why the batch is recalled");
			return null;
		}

		private static string? Clean(string? s, int max) { s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s; }

		/// The patients who still hold units of the batch: what reached them, less what came back; most recent first.
		public static IReadOnlyList<(int PatientId, decimal Qty, DateTime LastAt, IReadOnlyList<string> Ways)> Reached(IEnumerable<HospitalLotRecallIssue> issues)
			=> issues.GroupBy(x => x.PatientId)
				.Select(g => (PatientId: g.Key, Qty: g.Sum(x => -x.Direction * x.Qty), LastAt: g.Where(x => x.Direction < 0).Select(x => x.At).DefaultIfEmpty().Max(),
					Ways: (IReadOnlyList<string>)g.Where(x => x.Direction < 0).Select(x => x.Way).Distinct().ToList()))
				.Where(x => x.Qty > 0).OrderByDescending(x => x.LastAt).ToList();
	}

	public sealed class HospitalLotRecallService
	{
		private const string KitDialysis = "HospitalDialysis", KitChemo = "HospitalChemo", KitRehab = "HospitalRehab";   // HospitalSessionKitRules.MovementSource
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IStockService _stock;
		private readonly HospitalPharmacyService _pharmacy;

		public HospitalLotRecallService(CrossDbContext db, IHospitalAccessService access, IStockService stock, HospitalPharmacyService pharmacy)
		{ _db = db; _access = access; _stock = stock; _pharmacy = pharmacy; }

		private IQueryable<HospitalLotRecall> Recalls(BusinessContext ctx) => _db.Set<HospitalLotRecall>().Where(r => r.CompanyId == ctx.CompanyId);

		// ====================================================================================
		// READS
		// ====================================================================================

		public async Task<HospitalLotRecallStats> StatsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var s = await _pharmacy.SettingsAsync(ctx, ct);
			var rows = await Recalls(ctx).AsNoTracking().Select(r => new { r.Status, r.RecallClass, r.ItemId, r.BatchNo, r.QuarantineWarehouseId }).ToListAsync(ct);
			decimal held = 0;
			foreach (var r in rows.Where(r => r.Status == HospitalLotRecallStatus.Quarantined))
				held += (await PlacesAsync(ctx, r.ItemId, r.BatchNo, ct)).Where(p => p.WarehouseId == r.QuarantineWarehouseId).Sum(p => p.Qty);
			return new HospitalLotRecallStats(s.QuarantineWarehouseId != null, rows.Count(r => r.Status == HospitalLotRecallStatus.Quarantined),
				rows.Count(r => r.Status == HospitalLotRecallStatus.Quarantined && r.RecallClass == HospitalLotRecallClass.I), held, rows.Count(r => r.Status != HospitalLotRecallStatus.Quarantined));
		}

		public async Task<(IReadOnlyList<HospitalLotRecallRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? q, string? status, int page, int size, CancellationToken ct = default)
		{
			var query = from r in Recalls(ctx).AsNoTracking()
						join i in _db.Items.AsNoTracking() on r.ItemId equals i.ID
						select new { r, i.ItemCode, i.Name, i.NameEn };
			if (HospitalLotRecallStatus.All.Contains(status ?? "")) query = query.Where(x => x.r.Status == status);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var s = q.Trim();
				query = query.Where(x => x.r.RecallNo.Contains(s) || x.r.BatchNo.Contains(s) || x.ItemCode.Contains(s) || x.Name.Contains(s) || (x.NameEn != null && x.NameEn.Contains(s)) || (x.r.Reference != null && x.r.Reference.Contains(s)));
			}
			int total = await query.CountAsync(ct);
			var page1 = await query.OrderBy(x => x.r.Status == HospitalLotRecallStatus.Quarantined ? 0 : 1).ThenByDescending(x => x.r.OpenedAt)
				.Skip((page - 1) * size).Take(size).ToListAsync(ct);
			var expiries = await ExpiriesAsync(ctx, page1.Select(x => (x.r.ItemId, x.r.BatchNo)), ct);
			return (page1.Select(x => new HospitalLotRecallRow(x.r.ID, x.r.RecallNo, x.r.ItemId, x.ItemCode, x.Name, x.NameEn, x.r.BatchNo, expiries.GetValueOrDefault((x.r.ItemId, x.r.BatchNo)),
				x.r.RecallClass, x.r.Source, x.r.Reference, x.r.Status, x.r.QuarantinedQty, x.r.OpenedAt)).ToList(), total);
		}

		private async Task<Dictionary<(int, string), DateTime?>> ExpiriesAsync(BusinessContext ctx, IEnumerable<(int ItemId, string BatchNo)> keys, CancellationToken ct)
		{
			var items = keys.Select(k => k.ItemId).Distinct().ToList();
			var batches = await _db.StockBatches.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && items.Contains(b.ItemId)).Select(b => new { b.ItemId, b.BatchNo, b.ExpiryDate }).ToListAsync(ct);
			return batches.GroupBy(b => (b.ItemId, b.BatchNo)).ToDictionary(g => g.Key, g => g.First().ExpiryDate);
		}

		/// Where the batch is now: every store with units of it, from the movements (the inventory keeps no per-batch balance).
		public async Task<List<HospitalLotRecallPlace>> PlacesAsync(BusinessContext ctx, int itemId, string batchNo, CancellationToken ct = default)
		{
			var batchId = await BatchIdAsync(ctx, itemId, batchNo, ct);
			if (batchId == null) return new();
			var sums = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.BatchId == batchId)
				.GroupBy(m => m.WarehouseId).Select(g => new { WarehouseId = g.Key, Qty = g.Sum(m => m.Direction * m.QtyBase) }).Where(x => x.Qty != 0).ToListAsync(ct);
			var ids = sums.Select(s => s.WarehouseId).ToList();
			var whs = await _db.Warehouses.AsNoTracking().Where(w => ids.Contains(w.ID) && w.CompanyID == ctx.CompanyId).ToDictionaryAsync(w => w.ID, ct);
			var q = (await _pharmacy.SettingsAsync(ctx, ct)).QuarantineWarehouseId;
			return sums.Where(s => whs.ContainsKey(s.WarehouseId))
				.Select(s => new HospitalLotRecallPlace(s.WarehouseId, whs[s.WarehouseId].Code, whs[s.WarehouseId].Name, whs[s.WarehouseId].NameEn, s.Qty, s.WarehouseId == q))
				.OrderBy(p => p.Quarantine).ThenByDescending(p => p.Qty).ToList();
		}

		private Task<int?> BatchIdAsync(BusinessContext ctx, int itemId, string batchNo, CancellationToken ct)
			=> _db.StockBatches.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.ItemId == itemId && b.BatchNo == batchNo).Select(b => (int?)b.ID).FirstOrDefaultAsync(ct);

		/// Every issue of the batch toward a patient, and every return, as the batch's movements record them.
		public async Task<List<HospitalLotRecallIssue>> IssuesAsync(BusinessContext ctx, int itemId, string batchNo, CancellationToken ct = default)
		{
			var batchId = await BatchIdAsync(ctx, itemId, batchNo, ct);
			if (batchId == null) return new();
			string[] sources = { HospitalPharmacyService.MovementSource, HospitalMedRoutingService.MovementSource, HospitalMedRoutingService.ReturnSource, KitDialysis, KitChemo, KitRehab };
			var mv = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.BatchId == batchId && m.SourceId != null && sources.Contains(m.SourceType!))
				.Select(m => new { m.SourceType, SourceId = m.SourceId!.Value, m.Direction, m.QtyBase, m.MovementDate }).ToListAsync(ct);
			async Task<Dictionary<int, int>> Patients(IEnumerable<int> ids, Func<List<int>, Task<Dictionary<int, int>>> read) { var l = ids.Distinct().ToList(); return l.Count == 0 ? new() : await read(l); }
			int cid = ctx.CompanyId;
			var rx = await Patients(mv.Where(m => m.SourceType == HospitalPharmacyService.MovementSource).Select(m => m.SourceId),
				l => _db.HospitalDispenses.AsNoTracking().Where(x => x.CompanyId == cid && l.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.PatientId, ct));
			var md = await Patients(mv.Where(m => m.SourceType == HospitalMedRoutingService.MovementSource || m.SourceType == HospitalMedRoutingService.ReturnSource).Select(m => m.SourceId),
				l => _db.Set<HospitalMedDelivery>().AsNoTracking().Where(x => x.CompanyId == cid && l.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.PatientId, ct));
			var dia = await Patients(mv.Where(m => m.SourceType == KitDialysis).Select(m => m.SourceId),
				l => _db.Set<HospitalDialysisSession>().AsNoTracking().Where(x => x.CompanyId == cid && l.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.PatientId, ct));
			var chemo = await Patients(mv.Where(m => m.SourceType == KitChemo).Select(m => m.SourceId),
				l => _db.Set<HospitalChemoCycle>().AsNoTracking().Where(x => x.CompanyId == cid && l.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.PatientId, ct));
			var rehab = await Patients(mv.Where(m => m.SourceType == KitRehab).Select(m => m.SourceId),
				l => _db.Set<HospitalRehabSession>().AsNoTracking().Where(x => x.CompanyId == cid && l.Contains(x.ID)).ToDictionaryAsync(x => x.ID, x => x.PatientId, ct));
			var list = new List<HospitalLotRecallIssue>();
			foreach (var m in mv)
			{
				var (map, way) = m.SourceType switch
				{
					HospitalPharmacyService.MovementSource => (rx, HospitalLotRecallWays.Dispensed),
					KitDialysis => (dia, HospitalLotRecallWays.Kit),
					KitChemo => (chemo, HospitalLotRecallWays.Kit),
					KitRehab => (rehab, HospitalLotRecallWays.Kit),
					_ => (md, HospitalLotRecallWays.Delivered),
				};
				if (map.TryGetValue(m.SourceId, out var pid)) list.Add(new HospitalLotRecallIssue(pid, m.Direction, m.QtyBase, m.MovementDate, way));
			}
			return list;
		}

		public async Task<HospitalLotRecallView?> ViewAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			var r = await Recalls(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return null;
			var item = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == r.ItemId && i.CompanyID == ctx.CompanyId, ct);
			var reached = HospitalLotRecallRules.Reached(await IssuesAsync(ctx, r.ItemId, r.BatchNo, ct));
			var pids = reached.Select(x => x.PatientId).ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var patients = reached.Where(x => pats.ContainsKey(x.PatientId)).Select(x =>
			{
				var p = pats[x.PatientId];
				return new HospitalLotRecallPatient(p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.Phone, x.Qty, x.LastAt, x.Ways);
			}).ToList();
			var emp = new[] { r.OpenedByEmployeeId, r.EndedByEmployeeId }.Where(e => e != null).Select(e => e!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => emp.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
			var qName = await _db.Warehouses.AsNoTracking().Where(w => w.ID == r.QuarantineWarehouseId && w.CompanyID == ctx.CompanyId).Select(w => w.Code + " · " + (DisplayName.IsArabic ? w.Name : w.NameEn ?? w.Name)).FirstOrDefaultAsync(ct);
			var expiry = await _db.StockBatches.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.ItemId == r.ItemId && b.BatchNo == r.BatchNo).Select(b => b.ExpiryDate).FirstOrDefaultAsync(ct);
			return new HospitalLotRecallView(r, item.ItemCode, item.Name, item.NameEn, expiry, await PlacesAsync(ctx, r.ItemId, r.BatchNo, ct), patients, people, qName);
		}

		/// The new-recall form's batches of a medicine: every batch the inventory knows, with what is on hand anywhere.
		public async Task<IReadOnlyList<(string BatchNo, DateTime? Expiry, decimal OnHand)>> BatchesOfAsync(BusinessContext ctx, int itemId, CancellationToken ct = default)
		{
			var batches = await _db.StockBatches.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.ItemId == itemId).Select(b => new { b.ID, b.BatchNo, b.ExpiryDate }).ToListAsync(ct);
			var ids = batches.Select(b => b.ID).ToList();
			var qty = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.BatchId != null && ids.Contains(m.BatchId!.Value))
				.GroupBy(m => m.BatchId).Select(g => new { g.Key, Qty = g.Sum(m => m.Direction * m.QtyBase) }).ToDictionaryAsync(x => x.Key!.Value, x => x.Qty, ct);
			return batches.Select(b => (b.BatchNo, b.ExpiryDate, qty.GetValueOrDefault(b.ID))).OrderByDescending(b => b.Item3 > 0).ThenBy(b => b.ExpiryDate).ToList();
		}

		// ====================================================================================
		// WRITES
		// ====================================================================================

		/// Moves every unit of the batch outside the quarantine store into it: one inventory transfer per store. Returns the
		/// quantity moved and the transfers' numbers, or the stock writer's refusal.
		private async Task<(decimal Moved, List<string> Transfers, string? Error)> SweepCoreAsync(BusinessContext ctx, HospitalLotRecall r, string? userId, DateTime nowUtc, CancellationToken ct)
		{
			var item = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == r.ItemId && i.CompanyID == ctx.CompanyId, ct);
			decimal moved = 0; var nos = new List<string>();
			foreach (var p in (await PlacesAsync(ctx, r.ItemId, r.BatchNo, ct)).Where(p => p.WarehouseId != r.QuarantineWarehouseId && p.Qty > 0))
			{
				var (ok, error, tr) = await _stock.TransferAsync(ctx.CompanyId, p.WarehouseId, r.QuarantineWarehouseId, nowUtc, "Recall " + r.RecallNo + " — batch " + r.BatchNo,
					new List<TransferLineInput> { new() { ItemId = r.ItemId, Qty = p.Qty, UoMId = item.BaseUoMId, BatchNo = r.BatchNo } }, userId);
				if (!ok) return (0, nos, error ?? "transfer refused");
				moved += p.Qty; if (tr?.TransferNo != null) nos.Add(tr.TransferNo);
			}
			return (moved, nos, null);
		}

		private static string? Join(string? had, List<string> add)
		{
			var all = string.Join(",", new[] { had }.Where(s => !string.IsNullOrEmpty(s)).Concat(add));
			return all.Length == 0 ? null : all.Length > 400 ? all[..400] : all;
		}

		public async Task<HospitalResult> OpenAsync(BusinessContext ctx, HospitalLotRecallInput input, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			if (input == null) return HospitalResult.Fail("not_found");
			var refusal = HospitalLotRecallRules.Validate(input);
			if (refusal != null) return refusal;
			if (!await _db.StockBatches.AnyAsync(b => b.CompanyID == ctx.CompanyId && b.ItemId == input.ItemId && b.BatchNo == input.BatchNo, ct)) return HospitalResult.Fail("not_found");
			var settings = await _pharmacy.SettingsAsync(ctx, ct);
			if (settings.QuarantineWarehouseId is not int q) return HospitalResult.Fail("Choose the quarantine store in the pharmacy settings first");
			var open = await Recalls(ctx).AsNoTracking().Where(r => r.ItemId == input.ItemId && r.BatchNo == input.BatchNo && r.Status == HospitalLotRecallStatus.Quarantined).Select(r => r.RecallNo).FirstOrDefaultAsync(ct);
			if (open != null) return HospitalResult.Fail("This batch is already under recall {0}", open);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var nos = await Recalls(ctx).Select(r => r.RecallNo).ToListAsync(ct);
			var r = new HospitalLotRecall
			{
				CompanyId = ctx.CompanyId, RecallNo = HospitalDentalPracticeRules.NextNo(nos, "RCL-", 6), ItemId = input.ItemId, BatchNo = input.BatchNo!,
				RecallClass = input.RecallClass!, Source = input.Source!, Reference = input.Reference, Reason = input.Reason!, Status = HospitalLotRecallStatus.Quarantined,
				QuarantineWarehouseId = q, OpenedAt = nowUtc, OpenedByEmployeeId = employeeId,
			};
			_db.Add(r);
			await _db.SaveChangesAsync(ct);
			var (moved, transfers, error) = await SweepCoreAsync(ctx, r, userId, nowUtc, ct);
			if (error != null) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The batch could not be moved into quarantine: {0}", error); }
			r.QuarantinedQty = moved; r.Transfers = Join(null, transfers);
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(r.ID);
		}

		public async Task<HospitalResult> SweepAsync(BusinessContext ctx, int id, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var r = await Recalls(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (r.Status != HospitalLotRecallStatus.Quarantined) return HospitalResult.Fail("This recall has ended");
			var (moved, transfers, error) = await SweepCoreAsync(ctx, r, userId, nowUtc, ct);
			if (error != null) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The batch could not be moved into quarantine: {0}", error); }
			if (moved == 0) return HospitalResult.Fail("Nothing of this batch is left outside quarantine");
			r.QuarantinedQty += moved; r.Transfers = Join(r.Transfers, transfers);
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(r.ID);
		}

		/// The notice was withdrawn: what quarantine holds of the batch goes back to the pharmacy store.
		public async Task<HospitalResult> ReleaseAsync(BusinessContext ctx, int id, string? note, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			note = note?.Trim();
			if (string.IsNullOrEmpty(note)) return HospitalResult.Fail("Write why the recall ends");
			var settings = await _pharmacy.SettingsAsync(ctx, ct);
			if (settings.WarehouseId is not int pharmacyStore) return HospitalResult.Fail("Choose the pharmacy store in the pharmacy settings");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var r = await Recalls(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (r.Status != HospitalLotRecallStatus.Quarantined) return HospitalResult.Fail("This recall has ended");
			var held = (await PlacesAsync(ctx, r.ItemId, r.BatchNo, ct)).Where(p => p.WarehouseId == r.QuarantineWarehouseId).Sum(p => p.Qty);
			if (held > 0)
			{
				var item = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == r.ItemId && i.CompanyID == ctx.CompanyId, ct);
				var (ok, error, tr) = await _stock.TransferAsync(ctx.CompanyId, r.QuarantineWarehouseId, pharmacyStore, nowUtc, "Recall " + r.RecallNo + " released — batch " + r.BatchNo,
					new List<TransferLineInput> { new() { ItemId = r.ItemId, Qty = held, UoMId = item.BaseUoMId, BatchNo = r.BatchNo } }, userId);
				if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The batch could not be moved back: {0}", error ?? ""); }
				if (tr?.TransferNo != null) r.Transfers = Join(r.Transfers, new List<string> { tr.TransferNo });
			}
			r.Status = HospitalLotRecallStatus.Released; r.EndedAt = nowUtc; r.EndedByEmployeeId = employeeId; r.EndNote = note.Length > 400 ? note[..400] : note;
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(r.ID);
		}

		/// The batch was returned to the supplier or destroyed: the recall ends with what was done.
		public async Task<HospitalResult> CloseAsync(BusinessContext ctx, int id, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			note = note?.Trim();
			if (string.IsNullOrEmpty(note)) return HospitalResult.Fail("Write why the recall ends");
			var r = await Recalls(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (r.Status != HospitalLotRecallStatus.Quarantined) return HospitalResult.Fail("This recall has ended");
			r.Status = HospitalLotRecallStatus.Closed; r.EndedAt = nowUtc; r.EndedByEmployeeId = employeeId; r.EndNote = note.Length > 400 ? note[..400] : note;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(r.ID);
		}
	}
}
