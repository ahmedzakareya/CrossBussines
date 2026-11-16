using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H137 — the controlled-drug register of any store, and its blind counts (see HospitalControlledCount).
	//   · THE STORES: the pharmacy's own, its quarantine store, and every ward store a supply unit keeps.
	//   · BALANCES: every controlled drug, with what the store holds and how its last count ended.
	//   · THE LEDGER: every movement of one drug in one store, oldest to newest, with the running balance after each,
	//     who made it, and why (the shared movement labels).
	//   · COUNT: blind and witnessed. The expected balance is taken at that moment and never shown to the counter. A
	//     difference opens a discrepancy.
	//   · RESOLVE: a discrepancy closes once, with its reason:
	//       - a recount equal to the expected quantity (miscount);
	//       - a note naming the unrecorded movement (documentation);
	//       - a loss, written off through the inventory with a witness.
	//     A surplus cannot be a loss.
	// No stock rule lives here: a loss leaves through the inventory's own write-off.
	// ==========================================================================================

	public sealed record HospitalCdStore(int Id, string Code, string Name, string? NameEn, string Kind);

	public sealed record HospitalCdBalance(int ItemId, string Code, string Name, string? NameEn, HospitalDrug? Drug, decimal OnHand,
		HospitalControlledCount? LastCount, bool OpenDiscrepancy, bool Overdue);

	public sealed record HospitalCdLedgerRow(DateTime At, short Direction, decimal Qty, decimal Balance, string? SourceType, string? Reference, string? By, string? ByEn);

	public sealed record HospitalCdCountRow(HospitalControlledCount C, string Code, string Name, string? NameEn, string StoreCode, string StoreName, string? StoreNameEn,
		string? CountedBy, string? CountedByEn, string? CountedByPhoto, string? Witness, string? WitnessEn, string? WitnessPhoto, bool Overdue);

	public sealed record HospitalCdStats(int Drugs, decimal Units, int OpenDiscrepancies, int Overdue, int CountsThisWeek);

	public sealed class HospitalCdResolveInput
	{
		public string? Resolution { get; set; }
		public string? Note { get; set; }
		public decimal? Recounted { get; set; }
		public int? WitnessId { get; set; }
		public string? Password { get; set; }
	}

	public static class HospitalCdStoreKind
	{
		public const string Pharmacy = "Pharmacy", Quarantine = "Quarantine", Ward = "Ward";
	}

	public static class HospitalControlledCountRules
	{
		/// The shift a discrepancy must be closed within.
		public static readonly TimeSpan Shift = TimeSpan.FromHours(12);

		public static string StatusOf(decimal difference) => difference == 0 ? HospitalCountStatus.Matched : HospitalCountStatus.Open;

		public static bool IsOverdue(HospitalControlledCount c, DateTime nowUtc) => c.Status == HospitalCountStatus.Open && nowUtc - c.CountedAt > Shift;

		/// The running balance after each movement, oldest first.
		public static IReadOnlyList<decimal> Running(IEnumerable<(short Direction, decimal Qty)> moves)
		{
			var list = new List<decimal>(); decimal b = 0;
			foreach (var (d, q) in moves) { b += d * q; list.Add(b); }
			return list;
		}

		public static HospitalResult? ValidateResolution(HospitalControlledCount c, HospitalCdResolveInput i, int? self)
		{
			if (c.Status != HospitalCountStatus.Open) return HospitalResult.Fail("This count has no open discrepancy");
			i.Note = i.Note?.Trim(); if (string.IsNullOrEmpty(i.Note)) i.Note = null;
			switch (i.Resolution)
			{
				case HospitalCountResolution.Miscount:
					if (i.Recounted == null) return HospitalResult.Fail("Enter the recount");
					if (i.Recounted != c.Expected) return HospitalResult.Fail("The recount differs from the expected quantity — choose another reason");
					return null;
				case HospitalCountResolution.Documentation:
					return i.Note == null ? HospitalResult.Fail("Write which movement was not recorded") : null;
				case HospitalCountResolution.Loss:
					if (c.Difference > 0) return HospitalResult.Fail("A surplus is not a loss");
					if (i.Note == null) return HospitalResult.Fail("Write what happened");
					if (i.WitnessId is not int w) return HospitalResult.Fail("Choose the witness");
					if (w == self) return HospitalResult.Fail("The witness must be someone else");
					if (string.IsNullOrEmpty(i.Password)) return HospitalResult.Fail("The witness enters their password");
					return null;
				default: return HospitalResult.Fail("Choose the reason");
			}
		}
	}

	public sealed class HospitalControlledCountService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IStockService _stock;
		private readonly IHospitalWitness _witness;
		private readonly HospitalPharmacyService _pharmacy;

		public HospitalControlledCountService(CrossDbContext db, IHospitalAccessService access, IStockService stock, IHospitalWitness witness, HospitalPharmacyService pharmacy)
		{ _db = db; _access = access; _stock = stock; _witness = witness; _pharmacy = pharmacy; }

		private IQueryable<HospitalControlledCount> Counts(BusinessContext ctx) => _db.Set<HospitalControlledCount>().Where(c => c.CompanyId == ctx.CompanyId);

		// ====================================================================================
		// READS
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalCdStore>> StoresAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var s = await _pharmacy.SettingsAsync(ctx, ct);
			var wards = await _db.Set<HospitalSupplyUnit>().AsNoTracking().Where(u => u.CompanyId == ctx.CompanyId && u.IsActive).Select(u => u.StoreWarehouseId).Distinct().ToListAsync(ct);
			var ids = wards.Concat(new[] { s.WarehouseId, s.QuarantineWarehouseId }.Where(x => x != null).Select(x => x!.Value)).Distinct().ToList();
			var whs = await _db.Warehouses.AsNoTracking().Where(w => w.CompanyID == ctx.CompanyId && ids.Contains(w.ID)).ToListAsync(ct);
			string Kind(int id) => id == s.WarehouseId ? HospitalCdStoreKind.Pharmacy : id == s.QuarantineWarehouseId ? HospitalCdStoreKind.Quarantine : HospitalCdStoreKind.Ward;
			return whs.Select(w => new HospitalCdStore(w.ID, w.Code, w.Name, w.NameEn, Kind(w.ID)))
				.OrderBy(w => w.Kind == HospitalCdStoreKind.Pharmacy ? 0 : w.Kind == HospitalCdStoreKind.Ward ? 1 : 2).ThenBy(w => w.Code).ToList();
		}

		private async Task<List<int>> ControlledIdsAsync(BusinessContext ctx, CancellationToken ct)
			=> await _db.Set<HospitalControlledItem>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId).Select(c => c.ItemId).ToListAsync(ct);

		private async Task<decimal> OnHandAsync(BusinessContext ctx, int storeId, int itemId, CancellationToken ct)
			=> await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == storeId && b.ItemId == itemId).SumAsync(b => (decimal?)b.QtyOnHand, ct) ?? 0;

		public async Task<IReadOnlyList<HospitalCdBalance>> BalancesAsync(BusinessContext ctx, int storeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var ids = await ControlledIdsAsync(ctx, ct);
			var items = await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && ids.Contains(i.ID)).OrderBy(i => i.ItemCode).Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn }).ToListAsync(ct);
			var bal = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == storeId && ids.Contains(b.ItemId))
				.GroupBy(b => b.ItemId).Select(g => new { g.Key, Q = g.Sum(x => x.QtyOnHand) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
			var counts = await Counts(ctx).AsNoTracking().Where(c => c.WarehouseId == storeId && ids.Contains(c.ItemId)).OrderByDescending(c => c.CountedAt).ToListAsync(ct);
			var drugs = await HospitalDrugMasterService.LookupAsync(_db, ctx.CompanyId, ids, ct);
			return items.Select(i =>
			{
				var mine = counts.Where(c => c.ItemId == i.ID).ToList();
				return new HospitalCdBalance(i.ID, i.ItemCode, i.Name, i.NameEn, drugs.GetValueOrDefault(i.ID), bal.GetValueOrDefault(i.ID), mine.FirstOrDefault(),
					mine.Any(c => c.Status == HospitalCountStatus.Open), mine.Any(c => HospitalControlledCountRules.IsOverdue(c, nowUtc)));
			}).ToList();
		}

		public async Task<HospitalCdStats> StatsAsync(BusinessContext ctx, int storeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var b = await BalancesAsync(ctx, storeId, nowUtc, ct);
			var open = await Counts(ctx).AsNoTracking().Where(c => c.WarehouseId == storeId && c.Status == HospitalCountStatus.Open).ToListAsync(ct);
			var week = await Counts(ctx).CountAsync(c => c.WarehouseId == storeId && c.CountedAt >= nowUtc.AddDays(-7), ct);
			return new HospitalCdStats(b.Count(x => x.OnHand != 0), b.Sum(x => x.OnHand), open.Count, open.Count(c => HospitalControlledCountRules.IsOverdue(c, nowUtc)), week);
		}

		/// One drug's ledger in one store, newest first, each row with the balance after it.
		public async Task<(IReadOnlyList<HospitalCdLedgerRow> Rows, int Total)> LedgerAsync(BusinessContext ctx, int storeId, int itemId, int page, int size, CancellationToken ct = default, short? direction = null)
		{
			var mv = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.WarehouseId == storeId && m.ItemId == itemId)
				.OrderBy(m => m.MovementDate).ThenBy(m => m.ID).Select(m => new { m.MovementDate, m.Direction, m.QtyBase, m.SourceType, m.SourceId, m.CreatedBy }).ToListAsync(ct);
			var running = HospitalControlledCountRules.Running(mv.Select(m => (m.Direction, m.QtyBase)));
			var rx = mv.Where(m => m.SourceType == HospitalPharmacyService.MovementSource && m.SourceId != null).Select(m => m.SourceId!.Value).Distinct().ToList();
			var md = mv.Where(m => (m.SourceType == HospitalMedRoutingService.MovementSource || m.SourceType == HospitalMedRoutingService.ReturnSource) && m.SourceId != null).Select(m => m.SourceId!.Value).Distinct().ToList();
			var rxNo = await _db.HospitalDispenses.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && rx.Contains(d.ID)).ToDictionaryAsync(d => d.ID, d => d.RxNo, ct);
			var mdNo = await _db.Set<HospitalMedDelivery>().AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && md.Contains(d.ID)).ToDictionaryAsync(d => d.ID, d => d.DeliveryNo, ct);
			var users = mv.Where(m => m.CreatedBy != null).Select(m => m.CreatedBy!).Distinct().ToList();
			var people = (await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && e.UserId != null && users.Contains(e.UserId)).Select(e => new { e.UserId, e.FullName, e.FullNameEn }).ToListAsync(ct))
				.GroupBy(e => e.UserId!).ToDictionary(g => g.Key, g => g.First());
			var rows = mv.Select((m, k) =>
			{
				string? reference = m.SourceId is int sid ? (m.SourceType == HospitalPharmacyService.MovementSource ? rxNo.GetValueOrDefault(sid)
					: m.SourceType == HospitalMedRoutingService.MovementSource || m.SourceType == HospitalMedRoutingService.ReturnSource ? mdNo.GetValueOrDefault(sid) : null) ?? "#" + sid : null;
				var p = m.CreatedBy != null ? people.GetValueOrDefault(m.CreatedBy) : null;
				return new HospitalCdLedgerRow(m.MovementDate, m.Direction, m.QtyBase, running[k], m.SourceType, reference, p?.FullName, p?.FullNameEn);
			}).Reverse().Where(r => direction == null || r.Direction == direction).ToList();   // the balance runs over every movement, the filter only hides
			return (rows.Skip((page - 1) * size).Take(size).ToList(), rows.Count);
		}

		public async Task<(IReadOnlyList<HospitalCdCountRow> Rows, int Total)> CountsAsync(BusinessContext ctx, int storeId, string? status, int page, int size, DateTime nowUtc, CancellationToken ct = default)
		{
			var q = from c in Counts(ctx).AsNoTracking()
					join i in _db.Items.AsNoTracking() on c.ItemId equals i.ID
					join w in _db.Warehouses.AsNoTracking() on c.WarehouseId equals w.ID
					where c.WarehouseId == storeId
					select new { c, i.ItemCode, i.Name, i.NameEn, wCode = w.Code, wName = w.Name, wNameEn = w.NameEn };
			if (HospitalCountStatus.All.Contains(status ?? "")) q = q.Where(x => x.c.Status == status);
			int total = await q.CountAsync(ct);
			var rows = await q.OrderBy(x => x.c.Status == HospitalCountStatus.Open ? 0 : 1).ThenByDescending(x => x.c.CountedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
			var emp = rows.SelectMany(r => new[] { r.c.CountedByEmployeeId, r.c.WitnessEmployeeId }).Where(e => e != null).Select(e => e!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => emp.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId).ToDictionaryAsync(e => e.ID, e => (e.FullName, e.FullNameEn), ct);
			var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, emp, ct);
			return (rows.Select(r =>
			{
				var by = r.c.CountedByEmployeeId is int b && people.TryGetValue(b, out var x) ? x : default;
				var wi = r.c.WitnessEmployeeId is int w && people.TryGetValue(w, out var y) ? y : default;
				return new HospitalCdCountRow(r.c, r.ItemCode, r.Name, r.NameEn, r.wCode, r.wName, r.wNameEn, by.FullName, by.FullNameEn,
					r.c.CountedByEmployeeId is int b2 ? photos.GetValueOrDefault(b2) : null, wi.FullName, wi.FullNameEn, r.c.WitnessEmployeeId is int w2 ? photos.GetValueOrDefault(w2) : null,
					HospitalControlledCountRules.IsOverdue(r.c, nowUtc));
			}).ToList(), total);
		}

		// ====================================================================================
		// WRITES
		// ====================================================================================

		private async Task<HospitalResult?> WitnessAsync(BusinessContext ctx, int? witnessId, string? password, int? self, CancellationToken ct)
		{
			if (witnessId is not int w) return HospitalResult.Fail("Choose the witness");
			if (w == self) return HospitalResult.Fail("The witness must be someone else");
			if (string.IsNullOrEmpty(password)) return HospitalResult.Fail("The witness enters their password");
			if (!await _db.Employee.AnyAsync(e => e.ID == w && e.EmpCompanyID == ctx.CompanyId && e.IsActive, ct)) return HospitalResult.Fail("not_found");
			return await _witness.VerifyAsync(ctx.CompanyId, w, password, ct) ? null : HospitalResult.Fail("The witness's password is not correct");
		}

		/// A blind count: the counter never sees the expected quantity; the reply says only whether it matched.
		public async Task<HospitalResult> CountAsync(BusinessContext ctx, int storeId, int itemId, decimal counted, int? witnessId, string? password, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			if (counted < 0) return HospitalResult.Fail("Enter what is on the shelf");
			if (!(await StoresAsync(ctx, ct)).Any(s => s.Id == storeId)) return HospitalResult.Fail("not_found");
			if (!(await ControlledIdsAsync(ctx, ct)).Contains(itemId)) return HospitalResult.Fail("not_found");
			var refusal = await WitnessAsync(ctx, witnessId, password, employeeId, ct);
			if (refusal != null) return refusal;
			var expected = await OnHandAsync(ctx, storeId, itemId, ct);
			var c = new HospitalControlledCount
			{
				CompanyId = ctx.CompanyId, CountNo = HospitalDentalPracticeRules.NextNo(await Counts(ctx).Select(x => x.CountNo).ToListAsync(ct), "CDC-", 6),
				WarehouseId = storeId, ItemId = itemId, Expected = expected, Counted = counted, Difference = counted - expected,
				Status = HospitalControlledCountRules.StatusOf(counted - expected), CountedAt = nowUtc, CountedByEmployeeId = employeeId, WitnessEmployeeId = witnessId,
			};
			_db.Add(c);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(c.ID);
		}

		public async Task<HospitalResult> ResolveAsync(BusinessContext ctx, int countId, HospitalCdResolveInput input, int? employeeId, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Pharmacy, null, ct)) return HospitalResult.Fail("forbidden");
			if (input == null) return HospitalResult.Fail("not_found");
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var c = await Counts(ctx).FirstOrDefaultAsync(x => x.ID == countId, ct);
			if (c == null) return HospitalResult.Fail("not_found");
			var refusal = HospitalControlledCountRules.ValidateResolution(c, input, employeeId);
			if (refusal != null) return refusal;
			if (input.Resolution == HospitalCountResolution.Loss)
			{
				var w = await WitnessAsync(ctx, input.WitnessId, input.Password, employeeId, ct);
				if (w != null) return w;
				var item = await _db.Items.AsNoTracking().FirstAsync(i => i.ID == c.ItemId && i.CompanyID == ctx.CompanyId, ct);
				var (ok, error, docNo, _, _) = await _stock.WriteOffAsync(ctx.CompanyId, c.WarehouseId, nowUtc, "Lost", "Controlled-drug count " + c.CountNo + " — loss",
					new List<WriteOffLineInput> { new() { ItemId = c.ItemId, Qty = -c.Difference, UoMId = item.BaseUoMId, Reason = "Lost" } }, userId);
				if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The inventory refused the write-off: {0}", error ?? ""); }
				c.DocumentNo = docNo; c.ResolutionWitnessEmployeeId = input.WitnessId;
			}
			c.Status = HospitalCountStatus.Resolved; c.Resolution = input.Resolution; c.ResolutionNote = input.Note is { Length: > 400 } n ? n[..400] : input.Note;
			c.Recounted = input.Recounted; c.ResolvedAt = nowUtc; c.ResolvedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return HospitalResult.Success(c.ID);
		}
	}
}
