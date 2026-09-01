using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// a single stock in/out request (qty is in the entered UoM)
	public class MovementRequest
	{
		public DateTime Date { get; set; } = DateTime.UtcNow;
		public int ItemId { get; set; }
		public int WarehouseId { get; set; }
		public short Direction { get; set; }              // +1 in / -1 out
		public decimal Qty { get; set; }                  // in UoMId (or base when null)
		public int? UoMId { get; set; }
		public decimal? UnitCostInBase { get; set; }      // required for IN (receipt/opening); ignored for OUT
		public decimal? OutCostOverride { get; set; }      // reversal only: force an OUT to leave at this exact per-base cost (not moving-avg) so a receipt reverses value-exact
		public string? BatchNo { get; set; }
		public DateTime? Expiry { get; set; }
		public string? SerialNo { get; set; }
		public int? BinLocationId { get; set; }
		public string SourceType { get; set; } = "Adjustment";   // Opening/Receipt/Issue/Adjustment/TransferIn/TransferOut/...
		public int? SourceId { get; set; }
		public int? SourceLineId { get; set; }
		public bool PostToGl { get; set; } = true;
		public bool AllowExpired { get; set; } = false;   // true = bypass expired-batch block (e.g. inter-branch transfer move)
		public int? ProjectId { get; set; }               // analytic dimension → flows to the COGS/issue JE lines
		public int? CounterAccountOverride { get; set; }   // optional: force the counter (debit for OUT) to this account instead of the category default (e.g. project execution cost 510104); inventory side is UNCHANGED so stock_gl stays intact. null = default behaviour.
		public string? Notes { get; set; }
	}

	// flattened stock-balance row (balance + joined item/warehouse display fields) for the paged balances grid
	public class StockBalanceRow
	{
		public int ItemId { get; set; }
		public int WarehouseId { get; set; }
		public string ItemCode { get; set; } = "";
		public string ItemName { get; set; } = "";
		public string? ItemNameEn { get; set; }
		public string WarehouseCode { get; set; } = "";
		public decimal QtyOnHand { get; set; }
		public decimal AvgCost { get; set; }
		public decimal TotalValue { get; set; }
	}

	public class TransferLineInput
	{
		public int ItemId { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public string? BatchNo { get; set; }
		public string? SerialNo { get; set; }
		public int? BinLocationId { get; set; }         // destination section/rack the goods land on
		public int? SourceBinLocationId { get; set; }   // source section/rack the goods are picked from
	}

	public class CountLineInput
	{
		public int ItemId { get; set; }
		public decimal CountedQty { get; set; }
		// HM-7 batch-aware count: for an expiry-tracked item, one line PER batch. BatchNo null = non-tracked item (unchanged).
		public string? BatchNo { get; set; }
		public DateTime? Expiry { get; set; }   // required only when this line's batch does not yet exist (creates it — HM-6 rule)
	}

	public class LandedChargeInput
	{
		public string? Description { get; set; }
		public decimal Amount { get; set; }
		public int AccountId { get; set; }
	}

	public class OpeningStockLineInput
	{
		public int ItemId { get; set; }
		public int WarehouseId { get; set; }
		public decimal Qty { get; set; }
		public decimal UnitCost { get; set; }
		public int? UoMId { get; set; }
		public string? BatchNo { get; set; }
		public DateTime? Expiry { get; set; }
		public int? BinLocationId { get; set; }   // section/rack the opening stock sits on
	}

	public class WriteOffLineInput   // properties (not fields) so it serializes correctly in approval payloads
	{
		public int ItemId { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public string? BatchNo { get; set; }
		public string? SerialNo { get; set; }
		public string? Reason { get; set; }   // Damaged | Expired | Lost | Other
		public int? BinLocationId { get; set; }   // source section/rack the goods are removed from
	}

	public interface IStockService
	{
		Task<(bool ok, string? error, LandedCost? landed)> PostLandedCostAsync(int companyId, int goodsReceiptId, DateTime date, string allocationMethod, List<LandedChargeInput> charges, string? notes, string? userId);
		Task<(bool ok, string? error, StockTransfer? transfer)> TransferAsync(int companyId, int fromWarehouseId, int toWarehouseId, DateTime date, string? notes, List<TransferLineInput> lines, string? userId);
		Task<(bool ok, string? error, StockCount? count)> PostCountAsync(int companyId, int warehouseId, DateTime date, string? notes, List<CountLineInput> lines, string? userId);
		Task<(bool ok, string? error, string? docNo, int? docId, string mode)> WriteOffAsync(int companyId, int warehouseId, DateTime date, string? reason, string? notes, List<WriteOffLineInput> lines, string? userId);
		Task<(bool ok, string? error, int? jeId, decimal total)> PostOpeningStockAsync(int companyId, DateTime cutoff, List<OpeningStockLineInput> lines, string? userId);
		Task<(bool ok, string? error, int? assetId, decimal cost)> CapitalizeFromStockAsync(int companyId, int itemId, int warehouseId, decimal qty, DateTime date, int? costCenterId, string? userId);
		Task<(bool ok, string? error, StockMovement? movement)> PostMovementAsync(int companyId, MovementRequest req, string? userId);
		Task<(decimal qty, decimal value, decimal avg)> GetBalanceAsync(int companyId, int itemId, int warehouseId);
		Task<List<StockBalance>> GetBalancesAsync(int companyId, int? warehouseId = null);
		Task<(List<StockBalanceRow> rows, int total, decimal grandValue)> SearchBalancesAsync(int companyId, string? q, int? warehouseId, bool onlyInStock, int page, int pageSize);
		Task<List<StockMovement>> GetMovementsAsync(int companyId, int? itemId = null, int? warehouseId = null, int take = 300);
		// produce (or break down) an Assembly composite item from/into its components
		Task<(bool ok, string? error, StockMovement? produced)> AssembleAsync(int companyId, int assemblyItemId, int warehouseId, decimal qty, DateTime date, bool disassemble, string? userId);
		// Module 4 (staged): release a work order — issue BOM materials from stock → WIP (Dr 1105 / Cr raw inventory).
		Task<(bool ok, string? error)> ReleaseWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId);
		// Module 4: complete a work order — (issue if not staged) + labor/overhead → finished goods, clearing WIP, one balanced JE.
		Task<(bool ok, string? error, decimal unitCost)> CompleteWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId);
		// بند5: produce a partial (or final) quantity at standard unit cost; on finalize, clear remaining WIP to variance 520109.
		Task<(bool ok, string? error, decimal produced)> ProducePartialAsync(int companyId, int workOrderId, decimal qty, decimal stdUnitCost, bool finalize, DateTime date, string? userId);
		// Module 4 (staged): cancel a work order — if materials/labor were posted (WIP>0), reverse them, clearing WIP.
		Task<(bool ok, string? error)> CancelWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId);
		// Module 4 (بند3): add a labor line (Employee→Cr 520101 reclass | External→Cr cash/payable+WHT | Applied→Cr 520108) → Dr WIP.
		Task<(bool ok, string? error, int laborId)> AddWorkOrderLaborAsync(int companyId, int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal ratePerHour, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, DateTime date, string? userId);
		// Module 4 (بند3): remove a labor line — reverse its JE (Dr source / Cr WIP), reducing WipBalance.
		Task<(bool ok, string? error)> RemoveWorkOrderLaborAsync(int companyId, int laborId, DateTime date, string? userId);
		// ---- rack-level stock (BinStock) — QUANTITY only, no value/GL ----
		Task<List<BinStock>> GetBinStocksAsync(int companyId, int warehouseId);
		Task<(bool ok, string? error)> RelocateBinAsync(int companyId, int warehouseId, int itemId, int fromBinId, int toBinId, decimal qty, string? userId);
		Task<(bool ok, string? error)> SetBinCountAsync(int companyId, int warehouseId, int binLocationId, int itemId, decimal countedQty, string? userId);
		Task<(bool ok, string? error, int rows)> InitializeBinStockFromDefaultsAsync(int companyId, int warehouseId, string? userId);
	}

	/// The single writer to the stock ledger. Maintains balances + FIFO layers, computes costs
	/// (Moving Average / FIFO), and posts the matching GL entry via the journal engine.
	public class StockService : IStockService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		private readonly IFiscalPeriodService _periods;
		private readonly ICurrencyService _currency;
		private readonly Microsoft.Extensions.Logging.ILogger<StockService> _logger;
		private readonly ICurrencyRounding _rounding;

		// THE CANONICAL BOM EXPLOSION, COMPOSED RATHER THAN INJECTED — and the distinction is deliberate.
		//
		// This is one of the two writers, and IntegrityCheckService's `writer_coupling` check (HM-D53) reflects
		// each writer's CONSTRUCTOR PARAMETERS and records any dependency outside a fixed allow-list. Adding
		// IBomExplosionService as a parameter would register a new coupling on the stock writer — exactly the
		// thing CLAUDE.md names when it explains why StockService carries no localizer. Composing it here changes
		// no signature, so no coupling is recorded and no governance check is weakened to accommodate this batch.
		//
		// It is still the ONE implementation: the same class every other caller resolves from DI, over the same
		// DbContext this writer already owns. It is a pure calculator — it reads BOM rows and returns numbers, it
		// never writes — so it holds no state that a per-request instance would get wrong.
		private readonly IBomExplosionService _bom;

		public StockService(CrossDbContext context, IJournalEntryService journals, IFiscalPeriodService periods, ICurrencyService currency, Microsoft.Extensions.Logging.ILogger<StockService> logger, ICurrencyRounding rounding)
		{
			_context = context; _journals = journals; _periods = periods; _currency = currency; _logger = logger; _rounding = rounding;
			_bom = new BomExplosionService(context);
		}
		// HM-2: inventory is valued in the company FUNCTIONAL currency, so cost-path money values (TotalValue/TotalCost/COGS/landed/
		// count adjustment) round to the functional dp — for an EGP-functional company that is 2dp (unchanged). AvgCost/UnitCost/Qty
		// and FIFO layers stay at R4 (per-unit rates / quantities, 4≥3 — untouched). Reference value = TotalValue (GL-matched);
		// AvgCost is DERIVED = R4(TotalValue / Qty), so it is rounded to functional dp FIRST (via TotalValue) then divided — the
		// average necessarily reflects the GL-matched value, which is correct (it must equal stock_gl).
		private async Task<int> FunctionalDpAsync(int companyId) => await _rounding.DecimalsAsync(companyId, null);

		// HM-D5/D6: process-lifetime tripwire — number of times the locked-balance-read guard aborted an operation because
		// the tracked entity carried an UNSAVED change at the locked read (a batched-writer regression). Must stay 0 in prod.
		// Surfaced (counted) by inv-test-integrity so it is visible, not a log read after a disaster.
		public static int LockReadGuardTrips;

#if DEBUG
		// HM-D6 TEST-ONLY — COMPILED OUT of Release builds, so it is UNREACHABLE in production BY BUILD (not by config).
		// When true, skips the guard+Reload (reproduces the pre-fix stale identity-map read) so a dev self-test can prove
		// the bug FAILS before the fix and PASSES after — toggling precisely this fix. Prod ships -c Release ⇒ this field
		// does not exist there and the fix ALWAYS runs unconditionally. Removal after Phase د (see hm-deferred-backlog).
		internal static bool _testBypassLockReadRefresh;
		// HM-D7 self-test seam (Debug only): bypass the landed-cost LOCKED read (use the old unlocked read) so a dev
		// self-test can reproduce the lost update the fix prevents. In Release this field does not exist ⇒ the fix always runs.
		internal static bool _testBypassLandedLockRead;
#endif

		private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

		// PERIOD GUARD: every stock document is rejected if its date falls in a period that blocks
		// posting. Applied at method entry so even movements that post NO GL — a same-branch transfer,
		// for instance — are blocked. That is why this guard exists ALONGSIDE the one in
		// JournalEntryService rather than being replaced by it: those movements never reach the GL
		// writer, so the chokepoint there cannot see them.
		//
		// IT ASKS THE CANONICAL PREDICATE, and that is the whole of this change. It used to test the
		// literal "Closed", which meant SoftClosed — the state a controller puts a month into while it
		// finishes closing — still admitted stock movements. Inventory valuation could therefore move
		// inside a period finance believed was sealing, and the GL guard would not have noticed because
		// a same-branch transfer posts no journal. Two enforcement POINTS are necessary; two
		// DEFINITIONS of "blocks posting" were the defect.
		private async Task<string?> PeriodGuardAsync(int companyId, DateTime date)
		{
			var p = await _periods.ResolveAsync(companyId, date);
			if (p == null) return "لا توجد فترة مالية تشمل تاريخ الحركة";
			if (CrossBuy.Models.Context.Accounting.AccountingPeriodStatuses.BlocksPosting(p.Status))
				return "الفترة المالية مقفولة — لا يمكن الترحيل فيها";
			return null;
		}

		public async Task<(decimal qty, decimal value, decimal avg)> GetBalanceAsync(int companyId, int itemId, int warehouseId)
		{
			var b = await _context.StockBalances.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ItemId == itemId && x.WarehouseId == warehouseId);
			return b == null ? (0m, 0m, 0m) : (b.QtyOnHand, b.TotalValue, b.AvgCost);
		}

		public async Task<List<StockBalance>> GetBalancesAsync(int companyId, int? warehouseId = null) =>
			await _context.StockBalances.AsNoTracking()
				.Where(b => b.CompanyID == companyId && (warehouseId == null || b.WarehouseId == warehouseId))
				.ToListAsync();

		// server-side search + pagination for stock balances (joins items/warehouses; scales to large catalogs)
		public async Task<(List<StockBalanceRow> rows, int total, decimal grandValue)> SearchBalancesAsync(int companyId, string? q, int? warehouseId, bool onlyInStock, int page, int pageSize)
		{
			// Tagify multi-tag search (OR across tags): pre-filter items, then join balances onto the filtered set
			var itemsQ = _context.Items.AsNoTracking().Where(i => i.CompanyID == companyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Item>(terms, s =>
					i => i.ItemCode.Contains(s) || i.Name.Contains(s)
						|| (i.NameEn != null && i.NameEn.Contains(s)) || (i.Barcode != null && i.Barcode.Contains(s)));
				if (pred != null) itemsQ = itemsQ.Where(pred);
			}
			var q0 = from b in _context.StockBalances.AsNoTracking()
					 join i in itemsQ on b.ItemId equals i.ID
					 join w in _context.Warehouses.AsNoTracking() on b.WarehouseId equals w.ID
					 where b.CompanyID == companyId
					 select new { b, i, w };
			if (warehouseId.HasValue && warehouseId.Value > 0) q0 = q0.Where(x => x.b.WarehouseId == warehouseId.Value);
			if (onlyInStock) q0 = q0.Where(x => x.b.QtyOnHand != 0);
			var total = await q0.CountAsync();
			var grandValue = await q0.SumAsync(x => (decimal?)x.b.TotalValue) ?? 0m;
			if (page < 1) page = 1;
			if (pageSize < 1) pageSize = 25; else if (pageSize > 100000) pageSize = 100000;   // high ceiling allows full-set export
			var rows = await q0.OrderBy(x => x.i.ItemCode).Skip((page - 1) * pageSize).Take(pageSize)
				.Select(x => new StockBalanceRow
				{
					ItemId = x.b.ItemId, WarehouseId = x.b.WarehouseId, ItemCode = x.i.ItemCode, ItemName = x.i.Name, ItemNameEn = x.i.NameEn,
					WarehouseCode = x.w.Code, QtyOnHand = x.b.QtyOnHand, AvgCost = x.b.AvgCost, TotalValue = x.b.TotalValue
				}).ToListAsync();
			return (rows, total, grandValue);
		}

		public async Task<List<StockMovement>> GetMovementsAsync(int companyId, int? itemId = null, int? warehouseId = null, int take = 300) =>
			await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && (itemId == null || m.ItemId == itemId) && (warehouseId == null || m.WarehouseId == warehouseId))
				.OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.ID)
				.Take(take).ToListAsync();

		// converts a qty in UoMId to base units using the item's UoM conversions (1 alt = factor base)
		// HM-2: convert qty in <uomId> to the item's base unit. A non-base unit with NO defined conversion is REJECTED
		// (returns an error) — never the old silent factor-1, which would deduct a wrong base quantity. Barrier-checked:
		// zero existing movements used a non-base unit without a conversion, so this rejects nothing that ever worked.
		private async Task<(decimal baseQty, string? error)> ToBaseAsync(Item item, int? uomId, decimal qty)
		{
			if (uomId == null || uomId == item.BaseUoMId) return (qty, null);
			var conv = await _context.UoMConversions.AsNoTracking()
				.FirstOrDefaultAsync(c => c.ItemId == item.ID && c.FromUoMId == uomId && c.ToUoMId == item.BaseUoMId);
			if (conv == null) return (0m, "لا يوجد تحويل وحدة معرَّف لهذا الصنف من الوحدة المطلوبة إلى الوحدة الأساس — تعذّر الخصم.");
			return (R4(qty * conv.Factor), null);
		}

		private async Task<int?> ResolveBatchAsync(int companyId, int itemId, string? batchNo, DateTime? expiry)
		{
			if (string.IsNullOrWhiteSpace(batchNo)) return null;
			var b = await _context.StockBatches.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ItemId == itemId && x.BatchNo == batchNo);
			if (b == null)
			{
				b = new StockBatch { CompanyID = companyId, ItemId = itemId, BatchNo = batchNo.Trim(), ExpiryDate = expiry, CreatedAt = DateTime.UtcNow };
				_context.StockBatches.Add(b);
				await _context.SaveChangesAsync();
			}
			else if (expiry != null && b.ExpiryDate == null) { b.ExpiryDate = expiry; }
			return b.ID;
		}

		// FEFO allocation (shared by every issue path): for an expiry-tracked item issued WITHOUT a named batch,
		// returns which batches to draw — nearest-expiry first, expired excluded. applicable=false → the caller
		// should post a normal single (unbatched) movement; applicable=true with error → insufficient valid stock.
		private async Task<(bool applicable, string? error, List<(string batchNo, decimal qtyBase)> alloc)> FefoAllocateAsync(
			int companyId, Item item, int warehouseId, decimal neededBase, DateTime date)
		{
			var none = new List<(string, decimal)>();
			if (!item.TrackExpiry) return (false, null, none);
			var perBatch = await _context.StockMovements
				.Where(m => m.CompanyID == companyId && m.ItemId == item.ID && m.WarehouseId == warehouseId && m.BatchId != null)
				.GroupBy(m => m.BatchId!.Value)
				.Select(g => new { BatchId = g.Key, Qty = g.Sum(x => x.Direction * x.QtyBase) })
				.Where(x => x.Qty > 0).ToListAsync();
			if (perBatch.Count == 0)
			{
				// HM-D8: no batched stock for this item/warehouse. If it nonetheless has physical on-hand, that stock is
				// UNBATCHED — invisible to FEFO. BLOCK the issue with a clear data-correction message instead of silently
				// selling it unbatched (which would bypass expiry). Genuinely-zero stock falls through to the normal path.
				decimal onHand0 = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && b.ItemId == item.ID && b.WarehouseId == warehouseId).Select(b => (decimal?)b.QtyOnHand).FirstOrDefaultAsync() ?? 0m;
				if (onHand0 > 0)
					return (true, $"الصنف ({item.ItemCode}) به رصيد غير مرتبط بدفعات ({onHand0:0.##}) — لا يمكن صرفه بنظام الصلاحية؛ يلزم تصحيح البيانات بإدخال الدفعات", none);
				return (false, null, none);
			}
			var batchIds = perBatch.Select(p => p.BatchId).ToList();
			var batches = await _context.StockBatches.AsNoTracking().Where(b => batchIds.Contains(b.ID)).ToListAsync();
			var dateOnly = date.Date;
			var avail = perBatch
				.Select(p => new { p.Qty, Batch = batches.FirstOrDefault(b => b.ID == p.BatchId) })
				.Where(p => p.Batch != null)
				.Select(p => new { p.Qty, p.Batch!.BatchNo, p.Batch.ExpiryDate, Expired = p.Batch.ExpiryDate != null && p.Batch.ExpiryDate.Value.Date < dateOnly })
				.ToList();
			var valid = avail.Where(p => !p.Expired).OrderBy(p => p.ExpiryDate == null ? 1 : 0).ThenBy(p => p.ExpiryDate).ThenBy(p => p.BatchNo).ToList();
			var validQty = valid.Sum(p => p.Qty);
			if (validQty < neededBase)
			{
				var expiredQty = avail.Where(p => p.Expired).Sum(p => p.Qty);
				var hint = expiredQty > 0 ? $" (مستبعَد {expiredQty:0.##} من دفعات منتهية)" : "";
				// HM-D8: if the item ALSO holds unbatched physical stock beyond the batched total, the "available" figure
				// undercounts — say so, so the shortfall reads as a data problem, not a genuine stock-out.
				decimal batchedTotal = avail.Sum(p => p.Qty);
				decimal onHand = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && b.ItemId == item.ID && b.WarehouseId == warehouseId).Select(b => (decimal?)b.QtyOnHand).FirstOrDefaultAsync() ?? 0m;
				if (onHand > batchedTotal) hint += $" (+ {onHand - batchedTotal:0.##} غير مرتبط بدفعات — يلزم تصحيح البيانات)";
				return (true, $"الرصيد الصالح غير كافٍ: المتاح {validQty:0.##}، المطلوب {neededBase:0.##}{hint}", none);
			}
			var alloc = new List<(string batchNo, decimal qtyBase)>();
			decimal rem = neededBase;
			foreach (var p in valid) { if (rem <= 0) break; var take = Math.Min(p.Qty, rem); alloc.Add((p.BatchNo, take)); rem = R4(rem - take); }
			return (true, null, alloc);
		}

		public async Task<(bool ok, string? error, StockMovement? movement)> PostMovementAsync(int companyId, MovementRequest req, string? userId)
		{
			if (req.Qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر", null);
			if (req.Direction != 1 && req.Direction != -1) return (false, "اتجاه الحركة غير صحيح", null);
			{ var pErr = await PeriodGuardAsync(companyId, req.Date); if (pErr != null) return (false, pErr, null); }

			var hdr = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == req.ItemId && i.CompanyID == companyId);
			if (hdr == null) return (false, "الصنف غير موجود", null);

			// ===== Bundle composite: explode into components (the bundle itself holds no stock) =====
			if (hdr.IsComposite && hdr.CompositeType == "Bundle")
			{
				if (req.Direction == 1) return (false, "صنف الحزمة لا يُستلَم في المخزون — أنشئ/استلِم مكوّناته", null);
				// Quantities come from the canonical service now. Two things improve by that alone: the read is
				// COMPANY-SCOPED (this one was not — `Where(c => c.ParentItemId == hdr.ID)` with no company predicate,
				// the same shape of hole that let a work order be built from another tenant's recipe), and the
				// rounding is the canonical AwayFromZero instead of the C# default ToEven.
				//
				// ConvertToBaseUoM = false ON PURPOSE: each component request below carries `UoMId` onward and
				// PostSingleAsync converts it. Converting here as well would apply the factor twice.
				var bundleBom = await _bom.ExplodeAsync(companyId, hdr.ID, req.Qty, new BomExplosionOptions { ConvertToBaseUoM = false });
				if (!bundleBom.Ok) return (false, bundleBom.Error, null);
				if (bundleBom.Lines.Count == 0) return (false, "الحزمة لا تحتوي على مكوّنات", null);
				await using var btx = await ScopedTx.BeginOrJoinAsync(_context);
				try
				{
					StockMovement? last = null;
					foreach (var c in bundleBom.Lines)
					{
						var creq = new MovementRequest
						{
							Date = req.Date, ItemId = c.ComponentItemId, WarehouseId = req.WarehouseId, Direction = -1,
							Qty = c.Quantity,   // canonical: qty * per-parent * (1 + scrap), rounded 4dp AwayFromZero
							UoMId = c.UoMId, SourceType = req.SourceType, SourceId = req.SourceId,
							SourceLineId = req.SourceLineId, PostToGl = req.PostToGl, Notes = "تفكيك حزمة: " + hdr.ItemCode
						};
						var (ok, err, mv) = await PostSingleAsync(companyId, creq, userId);
						if (!ok) { await btx.RollbackAsync(); return (false, $"تعذّر صرف مكوّن الحزمة: {err}", null); }
						last = mv;
					}
					await btx.CommitAsync();
					return (true, null, last);
				}
				catch (Exception ex) { await btx.RollbackAsync(); return (false, "خطأ أثناء تفكيك الحزمة: " + ex.Message, null); }
			}

			// ===== HM-D8: FORCE a batch on the batch-capable USER input paths for expiry-tracked items — else the stock is
			// invisible to FEFO (the "available 0 while stock exists" bug). Placed BEFORE the FEFO block so a write-off
			// must name its batch (an expired write-off can't be FEFO-auto-picked). EXCLUDED: sale/issue (SalesInvoice/
			// Issue → FEFO auto-allocates), manufacturing/assembly output, purchase/GRN (HM-D16/HM-16 — no batch entry yet),
			// and transfer (carries the batch inherited from the transfer line). StockService has no localizer by design —
			// every message here is hardcoded Arabic (file convention); adding a localizer would itself be a new writer
			// coupling (HM-D53). This guard touches ONLY input validation — no FEFO/cost/rounding logic changes.
			// Write-off of a tracked item MUST name its batch — placed BEFORE FEFO so an expired write-off is not
			// FEFO-auto-picked (FEFO excludes expired). Inbound force-batch lives in PostSingleAsync (below) so it also
			// covers PostOpeningStockAsync, which reaches PostSingleAsync directly.
			if (hdr.TrackExpiry && req.Direction == -1 && req.SourceType == "StockWriteOff"
				&& string.IsNullOrWhiteSpace(req.BatchNo) && string.IsNullOrWhiteSpace(req.SerialNo))
				return (false, $"الصنف ({hdr.ItemCode}) يُتتبَّع بالصلاحية — يجب تحديد رقم الدفعة المراد إعدامها", null);

			// ===== FEFO: auto-pick nearest-expiry batches on issue for expiry-tracked items =====
			// Triggers only when the caller didn't name a batch/serial. Allocates the issue across batches
			// First-Expired-First-Out, skipping expired ones, and fails if valid (non-expired) stock is short.
			if (hdr.TrackExpiry && req.Direction == -1 && string.IsNullOrWhiteSpace(req.BatchNo) && string.IsNullOrWhiteSpace(req.SerialNo))
			{
				var (needBase, cerr) = await ToBaseAsync(hdr, req.UoMId, req.Qty);
				if (cerr != null) return (false, cerr, null);
				if (needBase <= 0) return (false, "تعذّر تحويل الكمية إلى الوحدة الأساسية", null);

				var (applicable, ferr, alloc) = await FefoAllocateAsync(companyId, hdr, req.WarehouseId, needBase, req.Date);
				if (applicable)
				{
					if (ferr != null) return (false, ferr, null);
					await using var ftx = await ScopedTx.BeginOrJoinAsync(_context);
					try
					{
						StockMovement? last = null;
						foreach (var a in alloc)
						{
							var creq = new MovementRequest
							{
								Date = req.Date, ItemId = hdr.ID, WarehouseId = req.WarehouseId, Direction = -1,
								Qty = a.qtyBase, UoMId = hdr.BaseUoMId, BatchNo = a.batchNo,
								SourceType = req.SourceType, SourceId = req.SourceId, SourceLineId = req.SourceLineId,
								BinLocationId = req.BinLocationId, PostToGl = req.PostToGl,
								Notes = (req.Notes ?? "") + $" [FEFO {a.batchNo}]"
							};
							var (ok, err, mv) = await PostSingleAsync(companyId, creq, userId);
							if (!ok) { await ftx.RollbackAsync(); return (false, err, null); }
							last = mv;
						}
						await ftx.CommitAsync();
						return (true, null, last);
					}
					catch (Exception ex) { await ftx.RollbackAsync(); return (false, "خطأ أثناء صرف FEFO: " + ex.Message, null); }
				}
				// not applicable (no batch-tracked stock / not expiry) → fall through to the normal movement
			}

			// ===== normal single movement =====
			await using var stx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var (ok, err, mv) = await PostSingleAsync(companyId, req, userId);
				if (!ok) { await stx.RollbackAsync(); return (false, err, null); }
				await stx.CommitAsync();
				return (true, null, mv);
			}
			catch (Exception ex) { await stx.RollbackAsync(); return (false, "خطأ أثناء ترحيل الحركة: " + ex.Message, null); }
		}

		// single-item movement WITHOUT opening its own transaction (the caller owns it)
		private async Task<(bool ok, string? error, StockMovement? movement)> PostSingleAsync(int companyId, MovementRequest req, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId);   // HM-2: cost values round to the functional currency dp
			decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			var item = await _context.Items.FirstOrDefaultAsync(i => i.ID == req.ItemId && i.CompanyID == companyId);
			if (item == null) return (false, "الصنف غير موجود", null);
			var wh = await _context.Warehouses.FirstOrDefaultAsync(w => w.ID == req.WarehouseId && w.CompanyID == companyId);
			if (wh == null) return (false, "المخزن غير موجود", null);

			// ===== HM-D8 / HM-16: FORCE a batch (+expiry) on INBOUND user-entry for expiry-tracked items — the single
			// choke point (PostOpeningStockAsync, the GRN receipt [ProcurementService posts SourceType="Receipt"], manual
			// adjustment and the normal path all reach here). ENFORCED sources are the set below. HM-16 CONFIRMS the GRN
			// receipt path is covered here via the "Receipt" source — closing the HM-6-deferred item (no new code: the
			// HM-6 guard already forced it; only this comment mislabeled GRN as excluded). Excluded: sale/issue (FEFO
			// auto-allocates on the way in), manufacturing/assembly output, transfer-in (carries the inherited batch).
			// Input validation only — no FEFO/cost/rounding change. Hardcoded Arabic (file convention; StockService has no
			// localizer by design — adding one would itself be a new writer coupling, HM-D53).
			var inboundEntrySources = new HashSet<string> { "OpeningStock", "Opening", "Receipt", "Adjustment" };
			if (item.TrackExpiry && req.Direction == 1 && inboundEntrySources.Contains(req.SourceType ?? "")
				&& string.IsNullOrWhiteSpace(req.BatchNo) && string.IsNullOrWhiteSpace(req.SerialNo))
				return (false, $"الصنف ({item.ItemCode}) يُتتبَّع بالصلاحية — يجب إدخال رقم الدفعة", null);
			if (item.TrackExpiry && req.Direction == 1 && inboundEntrySources.Contains(req.SourceType ?? "")
				&& !string.IsNullOrWhiteSpace(req.BatchNo) && req.Expiry == null)
				return (false, $"الصنف ({item.ItemCode}) يُتتبَّع بالصلاحية — يجب تحديد تاريخ الصلاحية للدفعة عند الإدخال", null);

			var cat = await _context.ItemCategories.FirstOrDefaultAsync(c => c.ID == item.ItemCategoryId && c.CompanyID == companyId);

			var method = !string.IsNullOrWhiteSpace(item.CostingMethod) ? item.CostingMethod
						: (!string.IsNullOrWhiteSpace(cat?.DefaultCostingMethod) ? cat!.DefaultCostingMethod : "Moving");

			var (qtyBase, cerr2) = await ToBaseAsync(item, req.UoMId, req.Qty);
			if (cerr2 != null) return (false, cerr2, null);
			if (qtyBase <= 0) return (false, "تعذّر تحويل الكمية إلى الوحدة الأساسية", null);

			var batchId = await ResolveBatchAsync(companyId, item.ID, req.BatchNo, req.Expiry);

			// ===== block issuing an expired batch (unless caller opts out, e.g. a transfer move) =====
			if (req.Direction == -1 && batchId != null && !req.AllowExpired && item.TrackExpiry)
			{
				var exp = await _context.StockBatches.Where(b => b.ID == batchId).Select(b => b.ExpiryDate).FirstOrDefaultAsync();
				if (exp != null && exp.Value.Date < req.Date.Date)
					return (false, $"الدفعة {req.BatchNo} منتهية الصلاحية ({exp:yyyy-MM-dd}) — لا يمكن صرفها", null);
			}

			// ===== concurrency: pessimistically lock the balance row for the life of the caller's transaction.
			// UPDLOCK serializes concurrent read-modify-write on the same (item,warehouse) row so two issues
			// can't both pass the sufficiency check and oversell; HOLDLOCK range-locks the key when the row
			// does not yet exist so two first-time movements can't both insert (also guarded by UX index). =====
			var bal = (await _context.StockBalances
				.FromSqlInterpolated($"SELECT * FROM StockBalances WITH (UPDLOCK, HOLDLOCK) WHERE CompanyID = {companyId} AND ItemId = {item.ID} AND WarehouseId = {wh.ID}")
				.AsTracking().ToListAsync()).FirstOrDefault();
			if (bal == null)
			{
				bal = new StockBalance { CompanyID = companyId, ItemId = item.ID, WarehouseId = wh.ID, QtyOnHand = 0, TotalValue = 0, AvgCost = 0 };
				_context.StockBalances.Add(bal);
			}
			else
#if DEBUG
			if (!_testBypassLockReadRefresh)   // TEST-ONLY bypass (Debug only); in Release this whole condition is gone ⇒ the fix ALWAYS runs
#endif
			{
				// HM-D5/D6 (lost-update fix): the locked SELECT acquires the row lock on the CURRENT DB row, but EF's identity
				// map returns an ALREADY-TRACKED instance WITHOUT overwriting its (possibly stale) values — nullifying the lock
				// for the read. Order: lock (above) → GUARD → Reload → modify.
				var entry = _context.Entry(bal);
				if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Modified || entry.State == Microsoft.EntityFrameworkCore.EntityState.Added)
				{
					// A tracked balance carrying an UNSAVED change at the locked read = a batched-writer re-reading under lock.
					// Reloading would silently wipe it; leaving it keeps the original bug. HARD FAIL (the caller's tx rolls back).
					System.Threading.Interlocked.Increment(ref LockReadGuardTrips);
					_logger?.LogError("HM-D6 guard: StockBalance lock-read found a PENDING {State} entity (company {Company} item {Item} wh {Wh}) — aborting to avoid a lost update", entry.State, companyId, item.ID, wh.ID);
					return (false, $"تعذّر ترحيل الحركة: رصيد الصنف ({item.ItemCode}) يحمل تعديلًا غير محفوظ لحظة القراءة المقفولة — أُلغيت العملية لمنع تحديث ضائع", null);
				}
				// refresh the tracked instance to the LOCKED DB truth (identity map may hold a stale value from an earlier read
				// in this context that another transaction has since changed). We hold UPDLOCK, so this reads our locked row.
				await entry.ReloadAsync();
			}

			decimal unitCost, totalCost;
			{
				if (req.Direction == 1)   // ===== IN =====
				{
					unitCost = R4(req.UnitCostInBase ?? (bal.QtyOnHand > 0 ? bal.AvgCost : 0m));
					totalCost = R2(unitCost * qtyBase);
					bal.QtyOnHand = R4(bal.QtyOnHand + qtyBase);
					bal.TotalValue = R2(bal.TotalValue + totalCost);
					bal.AvgCost = bal.QtyOnHand > 0 ? R4(bal.TotalValue / bal.QtyOnHand) : 0m;

					if (method == "FIFO")
						_context.StockCostLayers.Add(new StockCostLayer { CompanyID = companyId, ItemId = item.ID, WarehouseId = wh.ID, BatchId = batchId, ReceiptDate = req.Date, QtyRemaining = qtyBase, UnitCost = unitCost, CreatedAt = DateTime.UtcNow });
				}
				else                      // ===== OUT =====
				{
					if (bal.QtyOnHand < qtyBase && !wh.AllowNegativeStock)
						return (false, $"الرصيد غير كافٍ: المتاح {bal.QtyOnHand:0.##}، المطلوب {qtyBase:0.##}", null);

					if (method == "FIFO")
					{
						var layers = await _context.StockCostLayers
							.Where(l => l.CompanyID == companyId && l.ItemId == item.ID && l.WarehouseId == wh.ID && l.QtyRemaining > 0)
							.OrderBy(l => l.ReceiptDate).ThenBy(l => l.ID).ToListAsync();
						decimal need = qtyBase, cost = 0m;
						foreach (var l in layers)
						{
							if (need <= 0) break;
							var take = Math.Min(l.QtyRemaining, need);
							cost += take * l.UnitCost;
							l.QtyRemaining = R4(l.QtyRemaining - take);
							need = R4(need - take);
						}
						// if layers ran out (negative stock allowed) fall back to avg/last cost
						if (need > 0) cost += need * (bal.AvgCost);
						totalCost = R2(cost);
						unitCost = qtyBase > 0 ? R4(totalCost / qtyBase) : 0m;
					}
					else if (req.OutCostOverride.HasValue)   // reversal: leave at the exact original receipt cost (value-exact undo)
					{
						unitCost = R4(req.OutCostOverride.Value);
						totalCost = R2(unitCost * qtyBase);
					}
					else  // Moving Average
					{
						unitCost = bal.AvgCost;
						totalCost = R2(unitCost * qtyBase);
					}

					bal.QtyOnHand = R4(bal.QtyOnHand - qtyBase);
					bal.TotalValue = R2(bal.TotalValue - totalCost);
					if (bal.QtyOnHand <= 0) { bal.QtyOnHand = bal.QtyOnHand < 0 ? bal.QtyOnHand : 0m; if (bal.QtyOnHand == 0) bal.TotalValue = 0m; }
					bal.AvgCost = bal.QtyOnHand > 0 ? R4(bal.TotalValue / bal.QtyOnHand) : bal.AvgCost;

					// serial bookkeeping
					if (item.TrackSerial && !string.IsNullOrWhiteSpace(req.SerialNo))
					{
						var sn = await _context.StockSerials.FirstOrDefaultAsync(s => s.CompanyID == companyId && s.ItemId == item.ID && s.SerialNo == req.SerialNo);
						if (sn != null) { sn.Status = "Issued"; sn.WarehouseId = wh.ID; }
					}
				}

				bal.LastMovementAt = req.Date;

				// serial in
				if (req.Direction == 1 && item.TrackSerial && !string.IsNullOrWhiteSpace(req.SerialNo))
				{
					var sn = await _context.StockSerials.FirstOrDefaultAsync(s => s.CompanyID == companyId && s.ItemId == item.ID && s.SerialNo == req.SerialNo);
					if (sn == null) _context.StockSerials.Add(new StockSerial { CompanyID = companyId, ItemId = item.ID, SerialNo = req.SerialNo.Trim(), WarehouseId = wh.ID, Status = "InStock", CreatedAt = DateTime.UtcNow });
					else { sn.Status = "InStock"; sn.WarehouseId = wh.ID; }
				}

				var mv = new StockMovement
				{
					CompanyID = companyId, MovementDate = req.Date, ItemId = item.ID, WarehouseId = wh.ID,
					BinLocationId = req.BinLocationId, BatchId = batchId, SerialNo = req.SerialNo,
					Direction = req.Direction, QtyBase = qtyBase, UoMId = req.UoMId ?? item.BaseUoMId, QtyInUoM = req.Qty,
					UnitCost = unitCost, TotalCost = totalCost, SourceType = req.SourceType, SourceId = req.SourceId, SourceLineId = req.SourceLineId,
					Notes = req.Notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow
				};
				_context.StockMovements.Add(mv);
				await _context.SaveChangesAsync();

				// maintain rack-level quantity (locational only — never value/GL) when the movement specifies a bin
				if (req.BinLocationId.HasValue)
					await AdjustBinStockAsync(companyId, wh.ID, req.BinLocationId.Value, item.ID, req.Direction * qtyBase, req.Date);

				// ===== GL posting =====
				if (req.PostToGl && totalCost != 0m)
				{
					var (gok, gerr, jeId) = await PostGlAsync(companyId, item, cat, req, mv, totalCost, userId);
					if (!gok) return (false, gerr, null);   // caller's transaction rolls back
					mv.JournalEntryId = jeId;
					await _context.SaveChangesAsync();
				}

				return (true, null, mv);
			}
		}

		// ================= Rack-level stock (BinStock) — QUANTITY only, never value/GL =================
		// Adjust the per-(item, warehouse, bin) quantity. Called automatically for any movement that carries a bin.
		private async Task AdjustBinStockAsync(int companyId, int warehouseId, int binLocationId, int itemId, decimal deltaQty, DateTime date)
		{
			var bs = await _context.BinStocks.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.WarehouseId == warehouseId && x.BinLocationId == binLocationId && x.ItemId == itemId);
			if (bs == null) { bs = new BinStock { CompanyID = companyId, WarehouseId = warehouseId, BinLocationId = binLocationId, ItemId = itemId, QtyOnHand = 0m }; _context.BinStocks.Add(bs); }
			bs.QtyOnHand = R4(bs.QtyOnHand + deltaQty);
			if (bs.QtyOnHand < 0) bs.QtyOnHand = 0m;   // defensive: a bin can't hold negative
			bs.LastMovementAt = date;
			await _context.SaveChangesAsync();
		}

		public async Task<List<BinStock>> GetBinStocksAsync(int companyId, int warehouseId) =>
			await _context.BinStocks.AsNoTracking().Where(b => b.CompanyID == companyId && b.WarehouseId == warehouseId && b.QtyOnHand != 0m).ToListAsync();

		// Move quantity of an item from one rack/section to another within the SAME warehouse. Pure relocation:
		// no stock movement, no GL, warehouse total unchanged. Guards source availability + valid bins.
		public async Task<(bool ok, string? error)> RelocateBinAsync(int companyId, int warehouseId, int itemId, int fromBinId, int toBinId, decimal qty, string? userId)
		{
			if (qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر");
			if (fromBinId == toBinId) return (false, "الموقع المصدر والوجهة متطابقان");
			var bins = await _context.BinLocations.AsNoTracking().Where(b => b.WarehouseId == warehouseId && (b.ID == fromBinId || b.ID == toBinId)).Select(b => b.ID).ToListAsync();
			if (!bins.Contains(fromBinId) || !bins.Contains(toBinId)) return (false, "موقع غير صالح لهذا المخزن");
			var from = await _context.BinStocks.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.WarehouseId == warehouseId && x.BinLocationId == fromBinId && x.ItemId == itemId);
			if (from == null || from.QtyOnHand < qty) return (false, "الكمية المتاحة في الموقع المصدر غير كافية");
			await AdjustBinStockAsync(companyId, warehouseId, fromBinId, itemId, -qty, DateTime.UtcNow);
			await AdjustBinStockAsync(companyId, warehouseId, toBinId, itemId, qty, DateTime.UtcNow);
			return (true, null);
		}

		// Rack count: set a bin's counted quantity. Only redistributes LOCATED stock — cannot exceed the item's
		// warehouse balance (Σ located <= StockBalance.QtyOnHand). Real financial variances go through write-off/adjustment.
		public async Task<(bool ok, string? error)> SetBinCountAsync(int companyId, int warehouseId, int binLocationId, int itemId, decimal countedQty, string? userId)
		{
			if (countedQty < 0) return (false, "الكمية لا يمكن أن تكون سالبة");
			var bin = await _context.BinLocations.AsNoTracking().AnyAsync(b => b.ID == binLocationId && b.WarehouseId == warehouseId);
			if (!bin) return (false, "موقع غير صالح لهذا المخزن");
			var (whQty, _, _) = await GetBalanceAsync(companyId, itemId, warehouseId);
			var thisBin = await _context.BinStocks.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.WarehouseId == warehouseId && x.BinLocationId == binLocationId && x.ItemId == itemId);
			decimal locatedOthers = await _context.BinStocks.Where(x => x.CompanyID == companyId && x.WarehouseId == warehouseId && x.ItemId == itemId && x.BinLocationId != binLocationId).SumAsync(x => (decimal?)x.QtyOnHand) ?? 0m;
			if (R4(locatedOthers + countedQty) > R4(whQty))
				return (false, $"الكمية المرصودة ({countedQty}) + المواقع الأخرى ({locatedOthers}) تتجاوز رصيد الصنف بالمخزن ({whQty}). فرق حقيقي؟ استخدم الإعدام/تسوية الجرد.");
			if (thisBin == null) { thisBin = new BinStock { CompanyID = companyId, WarehouseId = warehouseId, BinLocationId = binLocationId, ItemId = itemId }; _context.BinStocks.Add(thisBin); }
			thisBin.QtyOnHand = R4(countedQty);
			thisBin.LastMovementAt = DateTime.UtcNow;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		// One-time backfill: place each item's whole warehouse balance onto its default rack (or section) — for items
		// that have a default location and no BinStock rows yet. Lets rack balances start populated. No value/GL.
		public async Task<(bool ok, string? error, int rows)> InitializeBinStockFromDefaultsAsync(int companyId, int warehouseId, string? userId)
		{
			var settings = await _context.ItemWarehouseSettings.AsNoTracking()
				.Where(s => s.WarehouseId == warehouseId && s.DefaultSectionId != null).ToListAsync();
			if (settings.Count == 0) return (false, "لا توجد مواقع افتراضية للأصناف في هذا المخزن", 0);
			var balances = (await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && b.WarehouseId == warehouseId && b.QtyOnHand > 0).ToListAsync())
				.ToDictionary(b => b.ItemId, b => b.QtyOnHand);
			int n = 0;
			foreach (var s in settings)
			{
				if (!balances.TryGetValue(s.ItemId, out var whQty) || whQty <= 0) continue;
				bool hasAny = await _context.BinStocks.AnyAsync(x => x.CompanyID == companyId && x.WarehouseId == warehouseId && x.ItemId == s.ItemId);
				if (hasAny) continue;   // don't overwrite existing rack data
				int bin = s.DefaultBinLocationId ?? s.DefaultSectionId!.Value;   // most specific default (rack else section)
				_context.BinStocks.Add(new BinStock { CompanyID = companyId, WarehouseId = warehouseId, BinLocationId = bin, ItemId = s.ItemId, QtyOnHand = whQty, LastMovementAt = DateTime.UtcNow });
				n++;
			}
			await _context.SaveChangesAsync();
			return (true, null, n);
		}

		// resolves a default cost center if any of the given accounts require one (else null)
		private async Task<(bool needed, int? cc, string? error)> ResolveCcAsync(int companyId, IEnumerable<int> accountIds)
		{
			var ids = accountIds.Distinct().ToList();
			var accs = await _context.Accounts.AsNoTracking().Where(a => ids.Contains(a.ID)).ToListAsync();
			if (!accs.Any(a => a.RequireCostCenter)) return (false, null, null);
			var cc = await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.ID).Select(c => (int?)c.ID).FirstOrDefaultAsync();
			if (cc == null) return (true, null, "حساب يتطلب مركز تكلفة ولا يوجد مركز تكلفة معرّف");
			return (true, cc, null);
		}

		// move stock from one warehouse to another, at cost (no GL — same inventory account, net zero)
		public async Task<(bool ok, string? error, StockTransfer? transfer)> TransferAsync(int companyId, int fromWarehouseId, int toWarehouseId, DateTime date, string? notes, List<TransferLineInput> lines, string? userId)
		{
			if (fromWarehouseId == toWarehouseId) return (false, "اختر مخزنين مختلفين", null);
			if (lines == null || lines.Count == 0) return (false, "التحويل يجب أن يحتوي على بند واحد على الأقل", null);
			int __fdp = await FunctionalDpAsync(companyId);   // HM-2: functional-currency cost rounding
			decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			{ var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr, null); }

			var srcWh = await _context.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.ID == fromWarehouseId && w.CompanyID == companyId);
			var dstWh = await _context.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.ID == toWarehouseId && w.CompanyID == companyId);
			if (srcWh == null || dstWh == null) return (false, "المخزن غير موجود", null);

			// same branch → pure relocation (no GL). different branch → value moves between cost centers (configurable).
			bool sameBranch = srcWh.BranchHierarchicalId == dstWh.BranchHierarchicalId;
			var settings = await _context.InventorySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyID == companyId);
			var mode = settings?.InterBranchTransferMode ?? "CostCenterPosting";
			// resolve each branch's cost center + the goods-in-transit account
			int? srcCc = srcWh.BranchHierarchicalId == null ? null : await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId && c.SourceHierarchicalId == srcWh.BranchHierarchicalId).Select(c => (int?)c.ID).FirstOrDefaultAsync();
			int? dstCc = dstWh.BranchHierarchicalId == null ? null : await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId && c.SourceHierarchicalId == dstWh.BranchHierarchicalId).Select(c => (int?)c.ID).FirstOrDefaultAsync();
			int? transitAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "110302").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			bool postGl = !sameBranch && mode == "CostCenterPosting" && srcCc != null && dstCc != null && transitAcc != null;

			var tr = new StockTransfer { CompanyID = companyId, FromWarehouseId = fromWarehouseId, ToWarehouseId = toWarehouseId, TransferDate = date.Date, Status = "Posted", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow };
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				_context.StockTransfers.Add(tr);
				await _context.SaveChangesAsync();
				tr.TransferNo = $"TR-{date:yyyy}-{tr.ID:D5}";

				// value moved per inventory (item-category) account, for the inter-branch GL
				var valueByInvAcc = new Dictionary<int, decimal>();
				int ln = 1; decimal total = 0;
				foreach (var l in lines)
				{
					if (l.ItemId <= 0 || l.Qty <= 0) continue;
					var litem = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == l.ItemId && i.CompanyID == companyId);
					if (litem == null) { await tx.RollbackAsync(); return (false, $"صنف غير موجود ({l.ItemId})", null); }

					// FEFO: an expiry-tracked item moved without a named batch → split the move across nearest-expiry batches
					var subs = new List<(string? batchNo, decimal qty, int? uom)>();
					if (litem.TrackExpiry && string.IsNullOrWhiteSpace(l.BatchNo))
					{
						var (needBase, lcerr) = await ToBaseAsync(litem, l.UoMId, l.Qty);
						if (lcerr != null) { await tx.RollbackAsync(); return (false, lcerr, null); }
						var (app, ferr, alloc) = await FefoAllocateAsync(companyId, litem, fromWarehouseId, needBase, date);
						if (app && ferr != null) { await tx.RollbackAsync(); return (false, ferr, null); }
						if (app) foreach (var a in alloc) subs.Add((a.batchNo, a.qtyBase, litem.BaseUoMId));
						else subs.Add((l.BatchNo, l.Qty, l.UoMId));
					}
					else subs.Add((l.BatchNo, l.Qty, l.UoMId));

					foreach (var s in subs)
					{
						// stock OUT of source (computes cost) — stock-only, GL handled below as one inter-branch entry
						var (ook, oerr, mvOut) = await PostSingleAsync(companyId, new MovementRequest
						{ Date = date, ItemId = l.ItemId, WarehouseId = fromWarehouseId, Direction = -1, Qty = s.qty, UoMId = s.uom, BatchNo = s.batchNo, SerialNo = l.SerialNo, BinLocationId = l.SourceBinLocationId, SourceType = "TransferOut", SourceId = tr.ID, PostToGl = false, AllowExpired = true, Notes = $"تحويل {tr.TransferNo}" }, userId);
						if (!ook) { await tx.RollbackAsync(); return (false, $"تعذّر الصرف من المخزن المصدر: {oerr}", null); }
						// stock IN to destination at the same unit cost + same batch
						var (iok, ierr, mvIn) = await PostSingleAsync(companyId, new MovementRequest
						{ Date = date, ItemId = l.ItemId, WarehouseId = toWarehouseId, Direction = 1, Qty = s.qty, UoMId = s.uom, UnitCostInBase = mvOut!.UnitCost, BatchNo = s.batchNo, SerialNo = l.SerialNo, BinLocationId = l.BinLocationId, SourceType = "TransferIn", SourceId = tr.ID, PostToGl = false, Notes = $"تحويل {tr.TransferNo}" }, userId);
						if (!iok) { await tx.RollbackAsync(); return (false, $"تعذّر الإدخال للمخزن الوجهة: {ierr}", null); }
						tr.Lines.Add(new StockTransferLine { StockTransferId = tr.ID, LineNo = ln++, ItemId = l.ItemId, Qty = mvOut.QtyBase, UoMId = s.uom, UnitCost = mvOut.UnitCost, LineTotal = mvOut.TotalCost, BatchNo = s.batchNo, SerialNo = l.SerialNo, OutMovementId = mvOut.ID, InMovementId = mvIn!.ID });
						total += mvOut.TotalCost;

						if (postGl && mvOut.TotalCost != 0m)
						{
							var invAcc = await _context.Items.AsNoTracking().Where(i => i.ID == l.ItemId)
								.Join(_context.ItemCategories, i => i.ItemCategoryId, c => c.ID, (i, c) => c.InventoryAccountId).FirstOrDefaultAsync();
							if (invAcc == null) { await tx.RollbackAsync(); return (false, "حساب المخزون غير مربوط لأحد الأصناف", null); }
							valueByInvAcc[invAcc.Value] = valueByInvAcc.TryGetValue(invAcc.Value, out var v) ? v + mvOut.TotalCost : mvOut.TotalCost;
						}
					}
				}
				tr.TotalCost = R2(total);
				await _context.SaveChangesAsync();

				// ===== inter-branch GL: value moves between cost centers through goods-in-transit =====
				// send:    Dr Transit(srcCC) / Cr Inventory(srcCC)
				// receive: Dr Inventory(dstCC) / Cr Transit(dstCC)   → transit nets to zero (instant transfer)
				if (postGl && valueByInvAcc.Count > 0)
				{
					var glLines = new List<JournalLineInput>();
					var desc = $"تحويل بين الفروع {tr.TransferNo}";
					foreach (var kv in valueByInvAcc)
					{
						var v = R2(kv.Value);
						glLines.Add(new JournalLineInput { AccountId = transitAcc!.Value, Debit = v, Credit = 0, CostCenterId = srcCc, Description = desc });   // leaves source branch
						glLines.Add(new JournalLineInput { AccountId = kv.Key, Debit = 0, Credit = v, CostCenterId = srcCc, Description = desc });
						glLines.Add(new JournalLineInput { AccountId = kv.Key, Debit = v, Credit = 0, CostCenterId = dstCc, Description = desc });          // arrives at destination branch
						glLines.Add(new JournalLineInput { AccountId = transitAcc!.Value, Debit = 0, Credit = v, CostCenterId = dstCc, Description = desc });
					}
					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "StockTransfer", SourceId = tr.ID, CurrencyId = 0, Description = desc, Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد التحويل بين الفروع: " + jerr, null); }
					tr.JournalEntryId = je!.ID;
					await _context.SaveChangesAsync();
				}

				await tx.CommitAsync();
				return (true, null, tr);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء التحويل: " + ex.Message, null); }
		}

		// landed cost: allocates extra charges (freight/customs) over a goods receipt's items, raising their value (qty unchanged)
		public async Task<(bool ok, string? error, LandedCost? landed)> PostLandedCostAsync(int companyId, int goodsReceiptId, DateTime date, string allocationMethod, List<LandedChargeInput> charges, string? notes, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId);   // HM-2: functional-currency cost rounding
			decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			{ var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr, null); }
			var gr = await _context.GoodsReceipts.Include(g => g.Lines).FirstOrDefaultAsync(g => g.ID == goodsReceiptId && g.CompanyID == companyId);
			if (gr == null) return (false, "إذن الاستلام غير موجود", null);
			var grLines = gr.Lines.Where(l => l.Qty > 0).ToList();
			if (grLines.Count == 0) return (false, "إذن الاستلام لا يحتوي على بنود", null);
			charges = (charges ?? new()).Where(c => c.Amount > 0 && c.AccountId > 0).ToList();
			if (charges.Count == 0) return (false, "أضف مصروفًا واحدًا على الأقل", null);

			decimal totalAdd = R2(charges.Sum(c => c.Amount));
			bool byQty = allocationMethod == "Qty";
			decimal sumQty = grLines.Sum(l => l.Qty), sumVal = grLines.Sum(l => l.LineTotal);
			if (byQty ? sumQty <= 0 : sumVal <= 0) return (false, "تعذّر التوزيع (قيمة/كمية الاستلام صفر)", null);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var lc = new LandedCost { CompanyID = companyId, GoodsReceiptId = gr.ID, LandedDate = date.Date, AllocationMethod = allocationMethod, TotalAmount = totalAdd, Status = "Posted", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow };
				_context.LandedCosts.Add(lc);
				await _context.SaveChangesAsync();
				lc.LandedNo = $"LC-{date:yyyy}-{lc.ID:D5}";
				int cln = 1;
				foreach (var ch in charges) _context.LandedCostCharges.Add(new LandedCostCharge { LandedCostId = lc.ID, LineNo = cln++, Description = ch.Description, Amount = R2(ch.Amount), AccountId = ch.AccountId });

				var invDrByAccount = new Dictionary<int, decimal>();
				var shareByItem = new Dictionary<int, decimal>();          // HM-D7: aggregate share per ITEM (one locked read each)
				decimal allocated = 0; int idx = 0;
				// ---- pass 1: allocate share per line (FORMULA UNCHANGED), record the movement + GL, aggregate by item ----
				foreach (var line in grLines)
				{
					idx++;
					decimal share = idx == grLines.Count
						? R2(totalAdd - allocated)                                  // last line absorbs rounding
						: R2(totalAdd * (byQty ? line.Qty / sumQty : line.LineTotal / sumVal));
					allocated += share;
					if (share == 0) continue;
					shareByItem[line.ItemId] = shareByItem.TryGetValue(line.ItemId, out var sv) ? sv + share : share;

					_context.StockMovements.Add(new StockMovement { CompanyID = companyId, MovementDate = date, ItemId = line.ItemId, WarehouseId = gr.WarehouseId, Direction = 1, QtyBase = 0, UnitCost = 0, TotalCost = share, SourceType = "LandedCost", SourceId = lc.ID, Notes = $"تكلفة إضافية {lc.LandedNo}", CreatedBy = userId, CreatedAt = DateTime.UtcNow });

					var cat = await _context.Items.AsNoTracking().Where(i => i.ID == line.ItemId)
						.Join(_context.ItemCategories, i => i.ItemCategoryId, c => c.ID, (i, c) => c.InventoryAccountId).FirstOrDefaultAsync();
					if (cat == null) { await tx.RollbackAsync(); return (false, "حساب المخزون غير مربوط لأحد الأصناف", null); }
					invDrByAccount[cat.Value] = invDrByAccount.TryGetValue(cat.Value, out var v) ? v + share : share;
				}

				// ---- pass 2 (HM-D7 lost-update fix): ONE LOCKED read PER ITEM (lock → guard → Reload → modify), so the
				// moving-average base (TotalValue → COGS + margin) can't be lost to a concurrent receipt/sale on the same
				// item — the exact un-locked read + batched save that HM-D7 flagged. The allocation above is UNCHANGED; only
				// the balance READ is now the same locked-read discipline as PostSingleAsync. Aggregating share per item
				// FIRST means each balance is locked-read EXACTLY ONCE ⇒ the Modified-guard is satisfied by construction. =====
				foreach (var kv in shareByItem)
				{
					int itemId = kv.Key; decimal itemShare = kv.Value;
					StockBalance? bal;
#if DEBUG
					if (_testBypassLandedLockRead)   // TEST-ONLY (Debug): the OLD unlocked read — reproduces the lost update
						bal = await _context.StockBalances.FirstOrDefaultAsync(b => b.CompanyID == companyId && b.ItemId == itemId && b.WarehouseId == gr.WarehouseId);
					else
#endif
						bal = (await _context.StockBalances
							.FromSqlInterpolated($"SELECT * FROM StockBalances WITH (UPDLOCK, HOLDLOCK) WHERE CompanyID = {companyId} AND ItemId = {itemId} AND WarehouseId = {gr.WarehouseId}")
							.AsTracking().ToListAsync()).FirstOrDefault();
					if (bal == null) { bal = new StockBalance { CompanyID = companyId, ItemId = itemId, WarehouseId = gr.WarehouseId }; _context.StockBalances.Add(bal); }
					else
#if DEBUG
					if (!_testBypassLandedLockRead)
#endif
					{
						var entry = _context.Entry(bal);
						if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Modified || entry.State == Microsoft.EntityFrameworkCore.EntityState.Added)
						{
							System.Threading.Interlocked.Increment(ref LockReadGuardTrips);
							await tx.RollbackAsync();
							return (false, "تعذّر ترحيل التكلفة الإضافية: رصيد الصنف يحمل تعديلًا غير محفوظ لحظة القراءة المقفولة — أُلغيت العملية لمنع تحديث ضائع", null);
						}
						await entry.ReloadAsync();   // refresh the tracked instance to the LOCKED DB truth
					}
					bal.TotalValue = R2(bal.TotalValue + itemShare);
					bal.AvgCost = bal.QtyOnHand > 0 ? R4(bal.TotalValue / bal.QtyOnHand) : bal.AvgCost;
					// FIFO: spread the total bump for this item across its remaining layers (per unit) — equivalent to the
					// per-line bumps summed (qty is unchanged, so remQty is constant); no cost-formula change.
					var layers = await _context.StockCostLayers.Where(l => l.CompanyID == companyId && l.ItemId == itemId && l.WarehouseId == gr.WarehouseId && l.QtyRemaining > 0).ToListAsync();
					var remQty = layers.Sum(l => l.QtyRemaining);
					if (remQty > 0) { var bump = itemShare / remQty; foreach (var l in layers) l.UnitCost = R4(l.UnitCost + bump); }
				}
				await _context.SaveChangesAsync();

				// combined JE: Dr Inventory (allocated shares) / Cr charge accounts (amounts)
				var glLines = new List<JournalLineInput>();
				foreach (var kv in invDrByAccount) glLines.Add(new JournalLineInput { AccountId = kv.Key, Debit = R2(kv.Value), Credit = 0, Description = $"تكلفة إضافية {lc.LandedNo}" });
				foreach (var ch in charges) glLines.Add(new JournalLineInput { AccountId = ch.AccountId, Debit = 0, Credit = R2(ch.Amount), Description = ch.Description ?? "تكلفة إضافية" });
				var (need, cc, ccErr) = await ResolveCcAsync(companyId, glLines.Select(g => g.AccountId));
				if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr, null); }
				if (need) foreach (var g in glLines) g.CostCenterId = cc;

				var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput { CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "LandedCost", SourceId = lc.ID, CurrencyId = 0, Description = $"تكلفة إضافية {lc.LandedNo}", Lines = glLines }, null);
				if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد التكلفة الإضافية: " + jerr, null); }
				lc.JournalEntryId = je!.ID;
				await _context.SaveChangesAsync();
				await tx.CommitAsync();
				return (true, null, lc);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء ترحيل التكلفة الإضافية: " + ex.Message, null); }
		}

		// physical count: compares counted vs book qty per item and posts the difference as an Adjustment
		public async Task<(bool ok, string? error, StockCount? count)> PostCountAsync(int companyId, int warehouseId, DateTime date, string? notes, List<CountLineInput> lines, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId);   // HM-2: functional-currency cost rounding
			decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			if (warehouseId <= 0) return (false, "المخزن مطلوب", null);
			if (lines == null || lines.Count == 0) return (false, "الجرد يجب أن يحتوي على بند واحد على الأقل", null);
			{ var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr, null); }

			// HM-7: batch-aware count. An expiry-tracked item is counted ONE LINE PER BATCH (BatchNo set); the diff is
			// attributed to its batch (positive → Adjustment carrying the batch, passing the HM-6 guard; negative → issued
			// from THAT batch, not FEFO — the FEFO gate at line 357 only fires for an UNnamed batch). A non-tracked item
			// (BatchNo null) is the exact pre-HM-7 behaviour. A counted batch that does not exist is CREATED (expiry
			// required, HM-6 rule) and flagged. The whole count is ATOMIC (one ScopedTx) so any reject leaves zero effect.
			var cnt = new StockCount { CompanyID = companyId, WarehouseId = warehouseId, CountDate = date.Date, Status = "Posted", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow };
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				_context.StockCounts.Add(cnt);
				await _context.SaveChangesAsync();
				cnt.CountNo = $"SC-{date:yyyy}-{cnt.ID:D5}";

				int ln = 1; decimal totalAdj = 0;
				foreach (var l in lines)
				{
					if (l.ItemId <= 0) continue;
					var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == l.ItemId && i.CompanyID == companyId);
					if (item == null) { await tx.RollbackAsync(); return (false, $"صنف غير موجود ({l.ItemId})", null); }
					bool batchAware = item.TrackExpiry && !string.IsNullOrWhiteSpace(l.BatchNo);

					decimal bookQty, avg; bool createdBatch = false; DateTime? lineExpiry = null;
					if (batchAware)
					{
						var batch = await _context.StockBatches.AsNoTracking().FirstOrDefaultAsync(b => b.CompanyID == companyId && b.ItemId == l.ItemId && b.BatchNo == l.BatchNo);
						if (batch == null)
						{
							// counted a batch the system does not know → the count creates it (real shelf stock). Expiry required.
							createdBatch = true; bookQty = 0m; lineExpiry = l.Expiry;
							if (l.CountedQty > 0 && l.Expiry == null)
							{ await tx.RollbackAsync(); return (false, $"الدفعة ({l.BatchNo}) للصنف ({item.ItemCode}) غير مسجَّلة ويلزم تاريخ صلاحية لإنشائها أثناء الجرد", null); }
						}
						else { bookQty = await BatchOnHandAsync(companyId, l.ItemId, warehouseId, batch.ID); lineExpiry = batch.ExpiryDate ?? l.Expiry; }
						var (_, _, itemAvg) = await GetBalanceAsync(companyId, l.ItemId, warehouseId);   // cost basis unchanged — item-level avg
						avg = itemAvg;
					}
					else
					{
						var (bq, _, a) = await GetBalanceAsync(companyId, l.ItemId, warehouseId);
						bookQty = bq; avg = a;
					}

					var diff = R4(l.CountedQty - bookQty);
					var cl = new StockCountLine { StockCountId = cnt.ID, LineNo = ln++, ItemId = l.ItemId, BookQty = bookQty, CountedQty = l.CountedQty, DiffQty = diff, UnitCost = avg,
						BatchNo = batchAware ? l.BatchNo : null, ExpiryDate = batchAware ? lineExpiry : null, BatchCreatedInCount = createdBatch && diff > 0 };
					if (diff != 0)
					{
						short dir = diff > 0 ? (short)1 : (short)-1;
						var (sok, serr, mv) = await PostMovementAsync(companyId, new MovementRequest
						{
							Date = date, ItemId = l.ItemId, WarehouseId = warehouseId, Direction = dir, Qty = Math.Abs(diff),
							UnitCostInBase = dir == 1 ? avg : (decimal?)null, SourceType = "Adjustment", SourceId = cnt.ID, PostToGl = true,
							BatchNo = batchAware ? l.BatchNo : null, Expiry = batchAware ? lineExpiry : null,
							Notes = $"تسوية جرد {cnt.CountNo}"
						}, userId);
						if (!sok) { await tx.RollbackAsync(); return (false, $"تعذّر ترحيل تسوية صنف: {serr}", null); }
						cl.UnitCost = mv!.UnitCost;
						cl.DiffValue = R2(dir * mv.TotalCost);
						cl.AdjustmentMovementId = mv.ID;
						totalAdj += cl.DiffValue;
					}
					cnt.Lines.Add(cl);
				}
				cnt.TotalAdjValue = R2(totalAdj);
				await _context.SaveChangesAsync();
				await tx.CommitAsync();
				return (true, null, cnt);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء الجرد: " + ex.Message, null); }
		}

		// HM-7: on-hand of ONE batch in a warehouse, derived from movements (there is no per-batch balance row; batch qty
		// is Σ Direction×QtyBase over its movements — same basis FefoAllocate uses). Read-only; no cost/FEFO change.
		private async Task<decimal> BatchOnHandAsync(int companyId, int itemId, int warehouseId, int batchId) =>
			await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.ItemId == itemId && m.WarehouseId == warehouseId && m.BatchId == batchId)
				.SumAsync(m => (decimal?)(m.Direction * m.QtyBase)) ?? 0m;

		// Write-off / damage: removes stock and books the loss to the write-off expense account (510103).
		// Same accounting entry in BOTH modes — Dr write-off expense / Cr inventory (1103). The mode only
		// decides the document wrapper: SeparateDocument => its own StockWriteOff doc (WOF-); AdjustmentReason
		// => recorded as a StockCount adjustment carrying Reason='WriteOff' (no separate document).
		public async Task<(bool ok, string? error, string? docNo, int? docId, string mode)> WriteOffAsync(
			int companyId, int warehouseId, DateTime date, string? reason, string? notes, List<WriteOffLineInput> lines, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: functional cost dp (no static R2)
			if (warehouseId <= 0) return (false, "المخزن مطلوب", null, null, "");
			{ var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr, null, null, ""); }
			lines = (lines ?? new()).Where(l => l.ItemId > 0 && l.Qty > 0).ToList();
			if (lines.Count == 0) return (false, "أضف بندًا واحدًا على الأقل", null, null, "");

			var mode = await _context.InventorySettings.AsNoTracking().Where(s => s.CompanyID == companyId).Select(s => s.WriteOffMode).FirstOrDefaultAsync() ?? "SeparateDocument";
			var woAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "510103").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (woAcc == null) return (false, "حساب مصروف الإعدام (510103) غير موجود في شجرة الحسابات", null, null, mode);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				// 1) container document (varies by mode)
				StockWriteOff? wof = null; StockCount? adj = null;
				int docId; string docNo; string srcType;
				if (mode == "AdjustmentReason")
				{
					adj = new StockCount { CompanyID = companyId, WarehouseId = warehouseId, CountDate = date.Date, Status = "Posted", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow };
					_context.StockCounts.Add(adj); await _context.SaveChangesAsync();
					adj.CountNo = $"SC-{date:yyyy}-{adj.ID:D5}"; docId = adj.ID; docNo = adj.CountNo; srcType = "Adjustment";
				}
				else
				{
					wof = new StockWriteOff { CompanyID = companyId, WarehouseId = warehouseId, WriteOffDate = date.Date, Status = "Posted", Reason = reason, Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow };
					_context.StockWriteOffs.Add(wof); await _context.SaveChangesAsync();
					wof.WriteOffNo = $"WOF-{date:yyyy}-{wof.ID:D5}"; docId = wof.ID; docNo = wof.WriteOffNo; srcType = "StockWriteOff";
				}

				// 2) issue each line at actual cost (no generic GL — we post our own write-off entry). Expired batches allowed.
				var byInvAcc = new Dictionary<int, decimal>();
				var accForCc = new List<int> { woAcc.Value };
				decimal total = 0m; int ln = 1;
				foreach (var l in lines)
				{
					var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == l.ItemId && i.CompanyID == companyId);
					if (item == null) { await tx.RollbackAsync(); return (false, $"صنف غير موجود ({l.ItemId})", null, null, mode); }
					var cat = await _context.ItemCategories.AsNoTracking().FirstOrDefaultAsync(c => c.ID == item.ItemCategoryId);
					if (cat?.InventoryAccountId == null) { await tx.RollbackAsync(); return (false, $"حساب المخزون غير مربوط لفئة الصنف {item.ItemCode}", null, null, mode); }
					int invAcc = cat.InventoryAccountId.Value;

					var (bookQty, _, _) = await GetBalanceAsync(companyId, l.ItemId, warehouseId);
					var (sok, serr, mv) = await PostSingleAsync(companyId, new MovementRequest
					{
						Date = date, ItemId = l.ItemId, WarehouseId = warehouseId, Direction = -1, Qty = l.Qty, UoMId = l.UoMId,
						BatchNo = l.BatchNo, SerialNo = l.SerialNo, BinLocationId = l.BinLocationId, SourceType = srcType, SourceId = docId, PostToGl = false, AllowExpired = true,
						Notes = $"إعدام {docNo}" + (string.IsNullOrWhiteSpace(l.Reason ?? reason) ? "" : $" ({l.Reason ?? reason})")
					}, userId);
					if (!sok) { await tx.RollbackAsync(); return (false, $"تعذّر صرف صنف الإعدام: {serr}", null, null, mode); }

					byInvAcc[invAcc] = R2(byInvAcc.GetValueOrDefault(invAcc) + mv!.TotalCost);
					accForCc.Add(invAcc);
					total = R2(total + mv.TotalCost);

					if (mode == "AdjustmentReason")
						adj!.Lines.Add(new StockCountLine { StockCountId = docId, LineNo = ln++, ItemId = l.ItemId, BookQty = bookQty, CountedQty = R4(bookQty - l.Qty), DiffQty = R4(-l.Qty), UnitCost = mv.UnitCost, DiffValue = R2(-mv.TotalCost), AdjustmentMovementId = mv.ID, Reason = l.Reason ?? reason ?? "WriteOff" });
					else
						wof!.Lines.Add(new StockWriteOffLine { StockWriteOffId = docId, LineNo = ln++, ItemId = l.ItemId, Qty = l.Qty, UoMId = l.UoMId, BatchNo = l.BatchNo, SerialNo = l.SerialNo, Reason = l.Reason ?? reason, UnitCost = mv.UnitCost, LineValue = mv.TotalCost, MovementId = mv.ID });
				}

				// 3) one journal entry: Dr write-off expense (total) / Cr inventory account(s)
				if (total != 0m)
				{
					var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
					if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr, null, null, mode); }
					var desc = $"إعدام مخزون {docNo}";
					var glLines = new List<JournalLineInput> { new JournalLineInput { AccountId = woAcc.Value, Debit = total, Credit = 0, Description = desc, CostCenterId = cc } };
					foreach (var kv in byInvAcc)
						glLines.Add(new JournalLineInput { AccountId = kv.Key, Debit = 0, Credit = kv.Value, Description = desc, CostCenterId = need ? cc : null });

					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "StockWriteOff", SourceId = docId, CurrencyId = 0, Description = desc, Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد الإعدام: " + jerr, null, null, mode); }
					if (wof != null) wof.JournalEntryId = je!.ID;
				}

				if (wof != null) wof.TotalValue = total; else adj!.TotalAdjValue = R2(-total);
				await _context.SaveChangesAsync();
				await tx.CommitAsync();
				return (true, null, docNo, docId, mode);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء الإعدام: " + ex.Message, null, null, mode); }
		}

		// Opening stock (Go-Live): receives qty at cost building FIFO layers + balance, then books
		// the value to inventory against the Opening Balance Equity clearing account (3301).
		// Dr Inventory (1103) / Cr Opening Balance Equity — keeps stock value == 1103 from day one.
		public async Task<(bool ok, string? error, int? jeId, decimal total)> PostOpeningStockAsync(
			int companyId, DateTime cutoff, List<OpeningStockLineInput> lines, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: functional cost dp (no static R2)
			lines = (lines ?? new()).Where(l => l.ItemId > 0 && l.WarehouseId > 0 && l.Qty > 0).ToList();
			if (lines.Count == 0) return (false, "أضف بندًا واحدًا على الأقل", null, 0);
			{ var pErr = await PeriodGuardAsync(companyId, cutoff); if (pErr != null) return (false, pErr, null, 0); }
			var obe = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "3301").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (obe == null) return (false, "حساب الرصيد الافتتاحي (3301) غير موجود", null, 0);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var byInvAcc = new Dictionary<int, decimal>();
				var accForCc = new List<int>();
				decimal total = 0m;
				foreach (var l in lines)
				{
					var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == l.ItemId && i.CompanyID == companyId);
					if (item == null) { await tx.RollbackAsync(); return (false, $"صنف غير موجود ({l.ItemId})", null, 0); }
					var cat = await _context.ItemCategories.AsNoTracking().FirstOrDefaultAsync(c => c.ID == item.ItemCategoryId);
					if (cat?.InventoryAccountId == null) { await tx.RollbackAsync(); return (false, $"حساب المخزون غير مربوط لفئة الصنف {item.ItemCode}", null, 0); }
					int invAcc = cat.InventoryAccountId.Value;

					var (sok, serr, mv) = await PostSingleAsync(companyId, new MovementRequest
					{
						Date = cutoff, ItemId = l.ItemId, WarehouseId = l.WarehouseId, Direction = 1, Qty = l.Qty, UoMId = l.UoMId,
						UnitCostInBase = l.UnitCost, BatchNo = l.BatchNo, Expiry = l.Expiry, BinLocationId = l.BinLocationId,
						SourceType = "OpeningStock", PostToGl = false, Notes = "رصيد افتتاحي"
					}, userId);
					if (!sok) { await tx.RollbackAsync(); return (false, $"تعذّر إدخال رصيد افتتاحي: {serr}", null, 0); }

					byInvAcc[invAcc] = R2(byInvAcc.GetValueOrDefault(invAcc) + mv!.TotalCost);
					accForCc.Add(invAcc);
					total = R2(total + mv.TotalCost);
				}

				if (total != 0m)
				{
					var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
					if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr, null, 0); }
					var glLines = new List<JournalLineInput>();
					foreach (var kv in byInvAcc)
						glLines.Add(new JournalLineInput { AccountId = kv.Key, Debit = kv.Value, Credit = 0, Description = "مخزون افتتاحي", CostCenterId = need ? cc : null });
					glLines.Add(new JournalLineInput { AccountId = obe.Value, Debit = 0, Credit = total, Description = "رصيد افتتاحي - مخزون" });

					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = cutoff, JournalType = "Opening", SourceType = "OpeningStock", SourceId = 0, CurrencyId = 0, Description = "رصيد مخزون افتتاحي", Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد المخزون الافتتاحي: " + jerr, null, 0); }
					await tx.CommitAsync();
					return (true, null, je!.ID, total);
				}
				await tx.CommitAsync();
				return (true, null, null, 0);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء المخزون الافتتاحي: " + ex.Message, null, 0); }
		}

		// Capitalize an Asset-type item that currently sits in inventory: issue it out at cost and record a fixed asset.
		// Dr Fixed Asset (1201) / Cr Inventory (1103) — stock value drops by the relieved cost so stock == 1103 holds.
		public async Task<(bool ok, string? error, int? assetId, decimal cost)> CapitalizeFromStockAsync(int companyId, int itemId, int warehouseId, decimal qty, DateTime date, int? costCenterId, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: functional cost dp (no static R2)
			if (qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر", null, 0);
			{ var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr, null, 0); }
			var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == companyId);
			if (item == null) return (false, "الصنف غير موجود", null, 0);
			if (item.ItemType != "Asset") return (false, "هذا الصنف ليس من نوع أصل ثابت", null, 0);
			var cat = await _context.ItemCategories.AsNoTracking().FirstOrDefaultAsync(c => c.ID == item.ItemCategoryId);
			if (cat?.InventoryAccountId == null) return (false, "حساب المخزون غير مربوط لفئة الصنف", null, 0);
			int invAcc = cat.InventoryAccountId.Value;
			var costAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "1201").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			var accumAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "1202").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			var expAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "520103").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (costAcc == null || accumAcc == null || expAcc == null) return (false, "حسابات الأصول الثابتة غير مُهيّأة في شجرة الحسابات", null, 0);
			var cc = costCenterId ?? await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.ID).Select(c => (int?)c.ID).FirstOrDefaultAsync();

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var (sok, serr, mv) = await PostSingleAsync(companyId, new MovementRequest
				{ Date = date, ItemId = itemId, WarehouseId = warehouseId, Direction = -1, Qty = qty, SourceType = "AssetCapitalization", PostToGl = false, AllowExpired = true, Notes = "رسملة أصل من المخزون" }, userId);
				if (!sok) { await tx.RollbackAsync(); return (false, $"تعذّر صرف الصنف: {serr}", null, 0); }
				decimal cost = mv!.TotalCost;

				var asset = new CrossBuy.Models.Context.Accounting.FixedAsset
				{
					CompanyID = companyId, Name = item.Name, NameEn = item.NameEn, AcquisitionDate = date.Date, Cost = cost, SalvageValue = 0,
					UsefulLifeMonths = 60, DepreciationMethod = "StraightLine", CostAccountId = costAcc.Value, AccumDepAccountId = accumAcc.Value,
					DepExpenseAccountId = expAcc.Value, CostCenterId = cc, AccumulatedDepreciation = 0, Status = "Active", CreatedAt = DateTime.UtcNow
				};
				_context.FixedAssets.Add(asset); await _context.SaveChangesAsync();
				asset.AssetNo = $"FA-{date:yyyy}-{asset.ID:D4}"; await _context.SaveChangesAsync();

				if (cost != 0m)
				{
					var glLines = new List<JournalLineInput>
					{
						new() { AccountId = costAcc.Value, Debit = cost, Credit = 0, CostCenterId = cc, Description = $"رسملة أصل {asset.AssetNo}" },
						new() { AccountId = invAcc, Debit = 0, Credit = cost, CostCenterId = cc, Description = $"رسملة أصل {asset.AssetNo}" },
					};
					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "AssetCapitalization", SourceId = asset.ID, CurrencyId = 0, Description = $"رسملة أصل من المخزون {asset.AssetNo}", Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد الرسملة: " + jerr, null, 0); }
					asset.AcquisitionJournalEntryId = je!.ID; await _context.SaveChangesAsync();
				}
				await tx.CommitAsync();
				return (true, null, asset.ID, cost);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء الرسملة: " + ex.Message, null, 0); }
		}

		// produce (assemble) or break down (disassemble) an Assembly composite item
		public async Task<(bool ok, string? error, StockMovement? produced)> AssembleAsync(int companyId, int assemblyItemId, int warehouseId, decimal qty, DateTime date, bool disassemble, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: functional cost dp (no static R2)
			if (qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر", null);
			{ var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr, null); }
			var kit = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == assemblyItemId && i.CompanyID == companyId);
			if (kit == null) return (false, "الصنف غير موجود", null);
			if (!(kit.IsComposite && kit.CompositeType == "Assembly")) return (false, "هذا الصنف ليس من نوع التجميع", null);
			var kitCat = await _context.ItemCategories.AsNoTracking().FirstOrDefaultAsync(c => c.ID == kit.ItemCategoryId);
			if (kitCat?.InventoryAccountId == null) return (false, "حساب المخزون غير مربوط لفئة الصنف المركّب", null);
			var comps = await _context.ItemComponents.AsNoTracking().Where(c => c.CompanyID == companyId && c.ParentItemId == kit.ID).OrderBy(c => c.SortOrder).ToListAsync();
			if (comps.Count == 0) return (false, "صنف التجميع لا يحتوي على مكوّنات", null);
			// ASSEMBLY quantities come from the canonical service (extended by planned scrap, canonical rounding).
			// The raw rows above are still loaded because DISASSEMBLY needs the PER-UNIT quantity un-extended — it
			// returns what a kit nominally contains and does not recover scrap — and because the component account
			// lookup below is built from them. Two different questions, two different numbers, one formula each.
			// ConvertToBaseUoM = false: the movement requests below carry UoMId onward and PostSingleAsync converts.
			var kitBom = await _bom.ExplodeAsync(companyId, kit.ID, qty, new BomExplosionOptions { ConvertToBaseUoM = false });
			if (!kitBom.Ok) return (false, kitBom.Error, null);

			// component inventory accounts
			var compItemIds = comps.Select(c => c.ComponentItemId).Distinct().ToList();
			var compItems = await _context.Items.AsNoTracking().Where(i => compItemIds.Contains(i.ID)).ToListAsync();
			var compCatIds = compItems.Select(i => i.ItemCategoryId).Distinct().ToList();
			var compCats = await _context.ItemCategories.AsNoTracking().Where(c => compCatIds.Contains(c.ID)).ToListAsync();
			int? InvAccOf(int itemId) { var it = compItems.FirstOrDefault(x => x.ID == itemId); var ct = it == null ? null : compCats.FirstOrDefault(c => c.ID == it.ItemCategoryId); return ct?.InventoryAccountId; }

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var glLines = new List<JournalLineInput>();
				var accForCc = new List<int> { kitCat.InventoryAccountId!.Value };
				string descKit = (disassemble ? "تفكيك تجميع: " : "تجميع: ") + kit.ItemCode;

				if (!disassemble)
				{
					// consume components at actual cost (FEFO for expiry-tracked components), accumulate total
					decimal total = 0m;
					foreach (var c in kitBom.Lines)
					{
						var invAcc = InvAccOf(c.ComponentItemId);
						if (invAcc == null) { await tx.RollbackAsync(); return (false, "حساب المخزون غير مربوط لأحد المكوّنات", null); }
						var citem = compItems.FirstOrDefault(x => x.ID == c.ComponentItemId);
						var reqQty = c.Quantity;   // canonical: planned scrap included, 4dp AwayFromZero

						// FEFO: split an expiry-tracked component across nearest-expiry batches
						var subs = new List<(string? batchNo, decimal qty, int? uom)>();
						if (citem != null && citem.TrackExpiry)
						{
							var (needBase, ccerr) = await ToBaseAsync(citem, c.UoMId, reqQty);
							if (ccerr != null) { await tx.RollbackAsync(); return (false, ccerr, null); }
							var (app, ferr, alloc) = await FefoAllocateAsync(companyId, citem, warehouseId, needBase, date);
							if (app && ferr != null) { await tx.RollbackAsync(); return (false, $"تعذّر صرف مكوّن: {ferr}", null); }
							if (app) foreach (var a in alloc) subs.Add((a.batchNo, a.qtyBase, citem.BaseUoMId));
							else subs.Add((null, reqQty, c.UoMId));
						}
						else subs.Add((null, reqQty, c.UoMId));

						foreach (var s in subs)
						{
							var (ok, err, mv) = await PostSingleAsync(companyId, new MovementRequest
							{ Date = date, ItemId = c.ComponentItemId, WarehouseId = warehouseId, Direction = -1, Qty = s.qty, UoMId = s.uom, BatchNo = s.batchNo, SourceType = "Assembly", PostToGl = false, Notes = descKit }, userId);
							if (!ok) { await tx.RollbackAsync(); return (false, $"تعذّر صرف مكوّن: {err}", null); }
							accForCc.Add(invAcc.Value);
							glLines.Add(new JournalLineInput { AccountId = invAcc.Value, Debit = 0, Credit = mv!.TotalCost, Description = descKit });
							total = R2(total + mv.TotalCost);
						}
					}
					// produce the kit at the accumulated cost
					var (pok, perr, prod) = await PostSingleAsync(companyId, new MovementRequest
					{ Date = date, ItemId = kit.ID, WarehouseId = warehouseId, Direction = 1, Qty = qty, UnitCostInBase = qty > 0 ? R4(total / qty) : 0m, SourceType = "Assembly", PostToGl = false, Notes = descKit }, userId);
					if (!pok) { await tx.RollbackAsync(); return (false, perr, null); }
					glLines.Insert(0, new JournalLineInput { AccountId = kitCat.InventoryAccountId.Value, Debit = total, Credit = 0, Description = descKit });

					var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
					if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr, null); }
					if (need) foreach (var l in glLines) l.CostCenterId = cc;

					if (total != 0m)
					{
						var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
						{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "Assembly", SourceId = prod!.ID, CurrencyId = 0, Description = descKit, Lines = glLines }, null);
						if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد التجميع: " + jerr, null); }
						prod.JournalEntryId = je!.ID;
						await _context.SaveChangesAsync();
					}
					await tx.CommitAsync();
					return (true, null, prod);
				}
				else
				{
					// consume the kit, distribute its cost back to components by BOM weight
					var (kok, kerr, kmv) = await PostSingleAsync(companyId, new MovementRequest
					{ Date = date, ItemId = kit.ID, WarehouseId = warehouseId, Direction = -1, Qty = qty, SourceType = "Disassembly", PostToGl = false, Notes = descKit }, userId);
					if (!kok) { await tx.RollbackAsync(); return (false, kerr, null); }
					decimal kitCost = kmv!.TotalCost;
					glLines.Add(new JournalLineInput { AccountId = kitCat.InventoryAccountId.Value, Debit = 0, Credit = kitCost, Description = descKit });

					// weights from current component avg cost (fallback equal)
					var weights = new List<decimal>();
					foreach (var c in comps)
					{
						var (cq, cv, ca) = await GetBalanceAsync(companyId, c.ComponentItemId, warehouseId);
						weights.Add(c.Quantity * (ca > 0 ? ca : 1m));
					}
					decimal wsum = weights.Sum(); if (wsum <= 0) wsum = comps.Count;
					for (int i = 0; i < comps.Count; i++)
					{
						var c = comps[i];
						decimal share = wsum > 0 ? R2(kitCost * (weights[i] / wsum)) : R2(kitCost / comps.Count);
						decimal cqty = qty * c.Quantity;
						var (iok, ierr, imv) = await PostSingleAsync(companyId, new MovementRequest
						{ Date = date, ItemId = c.ComponentItemId, WarehouseId = warehouseId, Direction = 1, Qty = cqty, UnitCostInBase = cqty > 0 ? R4(share / cqty) : 0m, SourceType = "Disassembly", PostToGl = false, Notes = descKit }, userId);
						if (!iok) { await tx.RollbackAsync(); return (false, $"تعذّر إدخال مكوّن: {ierr}", null); }
						var invAcc = InvAccOf(c.ComponentItemId);
						if (invAcc == null) { await tx.RollbackAsync(); return (false, "حساب المخزون غير مربوط لأحد المكوّنات", null); }
						accForCc.Add(invAcc.Value);
						glLines.Add(new JournalLineInput { AccountId = invAcc.Value, Debit = share, Credit = 0, Description = descKit });
					}

					var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
					if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr, null); }
					if (need) foreach (var l in glLines) l.CostCenterId = cc;

					if (kitCost != 0m)
					{
						var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
						{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "Disassembly", SourceId = kmv.ID, CurrencyId = 0, Description = descKit, Lines = glLines }, null);
						if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد التفكيك: " + jerr, null); }
						kmv.JournalEntryId = je!.ID;
						await _context.SaveChangesAsync();
					}
					await tx.CommitAsync();
					return (true, null, kmv);
				}
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء التجميع: " + ex.Message, null); }
		}

		// Module 4: complete a work order. Backflush components → WIP(1105) → finished goods, plus optional
		// Appends GL lines that consume all components into WIP (Cr raw-inventory per component + one Dr WIP for total
		// materials) and posts the stock issue movements (StockService -1, PostToGl=false). Returns total material cost.
		// Sets each component's IssuedQty + UnitCost. Caller owns the transaction + JE posting.
		private async Task<(bool ok, string? error, decimal material)> AppendIssueLinesAsync(
			int companyId, ManufWorkOrder wo, List<ManufWorkOrderComponent> comps,
			List<JournalLineInput> glLines, List<int> accForCc, int wipAccId, DateTime date, string desc, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: functional cost dp (no static R2)
			var compItemIds = comps.Select(c => c.ItemId).Distinct().ToList();
			var compItems = await _context.Items.AsNoTracking().Where(i => compItemIds.Contains(i.ID)).ToListAsync();
			var compCatIds = compItems.Select(i => i.ItemCategoryId).Distinct().ToList();
			var compCats = await _context.ItemCategories.AsNoTracking().Where(c => compCatIds.Contains(c.ID)).ToListAsync();
			int? InvAccOf(int itemId) { var it = compItems.FirstOrDefault(x => x.ID == itemId); var ct = it == null ? null : compCats.FirstOrDefault(c => c.ID == it.ItemCategoryId); return ct?.InventoryAccountId; }

			decimal material = 0m;
			foreach (var c in comps)
			{
				var invAcc = InvAccOf(c.ItemId);
				if (invAcc == null) return (false, "حساب المخزون غير مربوط لأحد المكوّنات", 0);
				var citem = compItems.FirstOrDefault(x => x.ID == c.ItemId);
				var subs = new List<(string? batchNo, decimal qty, int? uom)>();
				if (citem != null && citem.TrackExpiry)
				{
					var (needBase, wcerr) = await ToBaseAsync(citem, c.UoMId, c.PlannedQty);
					if (wcerr != null) return (false, wcerr, 0);
					var (app, ferr, alloc) = await FefoAllocateAsync(companyId, citem, wo.WarehouseId, needBase, date);
					if (app && ferr != null) return (false, $"تعذّر صرف مكوّن: {ferr}", 0);
					if (app) foreach (var a in alloc) subs.Add((a.batchNo, a.qtyBase, citem.BaseUoMId));
					else subs.Add((null, c.PlannedQty, c.UoMId));
				}
				else subs.Add((null, c.PlannedQty, c.UoMId));

				decimal compCost = 0m;
				// SourceId = wo.ID on every movement below. These movements were stamped SourceType = "WorkOrder"
				// with SourceId left null, while the JOURNAL ENTRIES for the same transitions all carried
				// SourceId = wo.ID. So the financial half of a work order was traceable and the physical half was
				// not: from a work-order id there was no query that returned its component issues and its finished
				// receipt. StockMovement.SourceId is an existing nullable column - this needs no new table, only
				// the value it was built to hold.
				foreach (var s in subs)
				{
					var (ok, err, mv) = await PostSingleAsync(companyId, new MovementRequest
					{ Date = date, ItemId = c.ItemId, WarehouseId = wo.WarehouseId, Direction = -1, Qty = s.qty, UoMId = s.uom, BatchNo = s.batchNo, SourceType = "WorkOrder", SourceId = wo.ID, PostToGl = false, Notes = desc }, userId);
					if (!ok) return (false, $"تعذّر صرف مكوّن: {err}", 0);
					accForCc.Add(invAcc.Value);
					glLines.Add(new JournalLineInput { AccountId = invAcc.Value, Debit = 0, Credit = mv!.TotalCost, Description = desc });   // Cr component inventory
					compCost = R2(compCost + mv.TotalCost);
				}
				c.IssuedQty = c.PlannedQty; c.UnitCost = c.PlannedQty > 0 ? R4(compCost / c.PlannedQty) : 0m;
				material = R2(material + compCost);
			}
			glLines.Add(new JournalLineInput { AccountId = wipAccId, Debit = material, Credit = 0, Description = desc + " (مواد)" });   // Dr WIP (materials)
			return (true, null, material);
		}

		// STAGED: release an order — issue BOM materials from stock into WIP. After this, WIP holds the material cost.
		public async Task<(bool ok, string? error)> ReleaseWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId)
		{
			var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr);
			var wo = await _context.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == workOrderId);
			if (wo == null) return (false, "أمر التشغيل غير موجود");
			if (wo.Status != "Draft") return (false, "لا يمكن الإصدار إلا من حالة «مخطّط»");
			if (wo.Mode != "OrderBased") return (false, "الإصدار المرحلي متاح لأوامر «بمراحل» فقط");
			var comps = await _context.ManufWorkOrderComponents.Where(c => c.CompanyID == companyId && c.WorkOrderId == wo.ID).ToListAsync();
			if (comps.Count == 0) return (false, "أمر التشغيل لا يحتوي على مكوّنات");
			var wipAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "1105").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (wipAcc == null) return (false, "حساب WIP (1105) غير موجود — شغّل cb_manuf_4_1.sql");

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var glLines = new List<JournalLineInput>();
				var accForCc = new List<int> { wipAcc.Value };
				string desc = "إصدار أمر تشغيل (صرف مواد): " + (wo.WoNo ?? ("#" + wo.ID));
				var (iok, ierr, material) = await AppendIssueLinesAsync(companyId, wo, comps, glLines, accForCc, wipAcc.Value, date, desc, userId);
				if (!iok) { await tx.RollbackAsync(); return (false, ierr); }

				var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
				if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr); }
				if (need) foreach (var l in glLines) l.CostCenterId = cc;

				if (material != 0m)
				{
					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "WorkOrder", SourceId = wo.ID, CurrencyId = 0, Description = desc, Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد الإصدار: " + jerr); }
					wo.JournalEntryId = je!.ID;
				}
				wo.MaterialCost = material; wo.WipBalance = material; wo.Status = "Released"; wo.ReleasedAt = DateTime.UtcNow;
				await _context.SaveChangesAsync();
				await tx.CommitAsync();
				return (true, null);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء إصدار أمر التشغيل: " + ex.Message); }
		}

		// Complete: receive finished goods from WIP, clearing it. If materials were NOT yet issued (direct/quick path
		// from Draft), issue them in the SAME transaction. Labor/overhead (header amounts) → Cr 520108 applied.
		// One (or, when staged, the second) balanced JE; WIP for this order nets to 0; inventory == GL preserved.
		public async Task<(bool ok, string? error, decimal unitCost)> CompleteWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: functional cost dp (no static R2)
			var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr, 0);
			var wo = await _context.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == workOrderId);
			if (wo == null) return (false, "أمر التشغيل غير موجود", 0);
			if (wo.Status == "Completed") return (false, "أمر التشغيل مكتمل بالفعل", 0);
			if (wo.Status == "Cancelled") return (false, "أمر التشغيل ملغى", 0);
			if (wo.Qty <= 0) return (false, "كمية الإنتاج يجب أن تكون أكبر من صفر", 0);
			var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == wo.ItemId && i.CompanyID == companyId);
			if (item == null) return (false, "الصنف المُصنَّع غير موجود", 0);
			var finCat = await _context.ItemCategories.AsNoTracking().FirstOrDefaultAsync(c => c.ID == item.ItemCategoryId);
			if (finCat?.InventoryAccountId == null) return (false, "حساب المخزون غير مربوط لفئة الصنف المُصنَّع", 0);
			var comps = await _context.ManufWorkOrderComponents.Where(c => c.CompanyID == companyId && c.WorkOrderId == wo.ID).ToListAsync();
			if (comps.Count == 0) return (false, "أمر التشغيل لا يحتوي على مكوّنات (راجع قائمة المواد BOM)", 0);

			var wipAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "1105").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			var appliedAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "520108").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (wipAcc == null || appliedAcc == null) return (false, "حسابات التصنيع (WIP/المطبّقة) غير موجودة — شغّل cb_manuf_4_1.sql", 0);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var glLines = new List<JournalLineInput>();
				var accForCc = new List<int> { finCat.InventoryAccountId!.Value, wipAcc.Value };
				string desc = "أمر تشغيل: " + (wo.WoNo ?? ("#" + wo.ID)) + " — " + item.ItemCode;

				// HAS THIS ORDER ALREADY ISSUED ITS MATERIALS? That is a LIFECYCLE question, and it used to be
				// answered with a COST: `wo.WipBalance > 0m`. Release stamps the materials it issued as a value
				// (`wo.WipBalance = material`, ReleaseWorkOrderAsync below), so when those materials were worth
				// NOTHING the balance stayed 0, completion concluded nothing had been issued, and it issued the
				// whole bill of materials a SECOND time. Measured on a BOM of 4: 4 issued at Release, 8 after
				// Complete. Zero-valued material is not a freak input either - the release journal entry is
				// posted only `if (material != 0m)` a few lines down, so the code already anticipates it. A
				// zero-cost component, a promotional or sample input, a raw with no cost loaded yet, or a fully
				// written-down batch all reach this line with material == 0.
				//
				// WHY ReleasedAt AND NOT `Status == "Released"`. Status is not durable: an order that receives
				// sourced labour moves Released -> InProgress (:1535 and :1739), so a status equality check would
				// re-issue for exactly the orders that had done the most work. ReleasedAt is written once, in the
				// same statement that sets Status = "Released", and is never cleared - it exists to record this
				// fact and nothing else reads it. The status terms are kept as a belt for any row released before
				// that column was stamped; both point at the same transition, and neither can be zero.
				//
				// Draft still issues here, which is what keeps the direct/quick path (Mode = Immediate, completed
				// without a separate Release) working exactly as before. Repeated Complete is unaffected: the
				// guards above already refuse a Completed or Cancelled order.
				bool alreadyIssued = wo.ReleasedAt != null || wo.Status == "Released" || wo.Status == "InProgress";
				decimal material;
				if (alreadyIssued) material = wo.MaterialCost;
				else
				{
					var (iok, ierr, mat) = await AppendIssueLinesAsync(companyId, wo, comps, glLines, accForCc, wipAcc.Value, date, desc, userId);
					if (!iok) { await tx.RollbackAsync(); return (false, ierr, 0); }
					material = mat;
				}

				// header applied labor + overhead → Dr WIP, Cr 520108 applied (sourced labor lines are posted separately
				// during InProgress and are ALREADY in WIP via WipBalance — they must NOT be re-applied here)
				var labOh = R2(wo.LaborCost + wo.OverheadCost);
				if (labOh != 0m)
				{
					glLines.Add(new JournalLineInput { AccountId = wipAcc.Value, Debit = labOh, Credit = 0, Description = desc + " (عمالة مطبّقة/أوفرهيد)" });
					glLines.Add(new JournalLineInput { AccountId = appliedAcc.Value, Debit = 0, Credit = labOh, Description = desc + " (تكاليف مطبّقة)" });
				}

				// WIP already holds materials (+ any sourced labor lines) = wipPrior; for the direct path it's the material issued now
				decimal wipPrior = alreadyIssued ? wo.WipBalance : material;
				decimal total = R2(wipPrior + labOh);
				var unit = wo.Qty > 0 ? R4(total / wo.Qty) : 0m;

				// produce finished goods at the rolled-up cost → Dr finished inventory, Cr WIP (clears this order's WIP)
				var (pok, perr, prod) = await PostSingleAsync(companyId, new MovementRequest
				{ Date = date, ItemId = item.ID, WarehouseId = wo.WarehouseId, Direction = 1, Qty = wo.Qty, UnitCostInBase = unit, SourceType = "WorkOrder", SourceId = wo.ID, PostToGl = false, Notes = desc }, userId);
				if (!pok) { await tx.RollbackAsync(); return (false, perr, 0); }
				glLines.Add(new JournalLineInput { AccountId = finCat.InventoryAccountId.Value, Debit = total, Credit = 0, Description = desc });   // Dr finished inventory
				glLines.Add(new JournalLineInput { AccountId = wipAcc.Value, Debit = 0, Credit = total, Description = desc });                       // Cr WIP (clears)

				var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
				if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr, 0); }
				if (need) foreach (var l in glLines) l.CostCenterId = cc;

				if (total != 0m || labOh != 0m)
				{
					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "WorkOrder", SourceId = wo.ID, CurrencyId = 0, Description = desc, Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد أمر التشغيل: " + jerr, 0); }
					wo.JournalEntryId = je!.ID;
				}
				wo.Status = "Completed"; wo.ProducedQty = wo.Qty; wo.MaterialCost = material; wo.UnitCost = unit;
				wo.CompletedAt = DateTime.UtcNow; wo.ClosedAt = DateTime.UtcNow; wo.WipBalance = 0m;
				await _context.SaveChangesAsync();
				await tx.CommitAsync();
				return (true, null, unit);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء تنفيذ أمر التشغيل: " + ex.Message, 0); }
		}

		// بند5 — partial/final production at STANDARD unit cost. Receives finished goods (Dr finished / Cr WIP);
		// on finalize (or when the planned qty is reached) applies header labor+overhead then clears any remaining
		// WIP to the production-variance account 520109 (Dr 520109 unfavorable / Cr 520109 favorable). WIP → 0 at close,
		// so the wip_gl invariant (GL 1105 == Σ open-order WipBalance) stays intact throughout.
		public async Task<(bool ok, string? error, decimal produced)> ProducePartialAsync(int companyId, int workOrderId, decimal qty, decimal stdUnitCost, bool finalize, DateTime date, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: functional cost dp (no static R2)
			var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr, 0);
			var wo = await _context.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == workOrderId);
			if (wo == null) return (false, "أمر التشغيل غير موجود", 0);
			if (wo.Status == "Completed") return (false, "أمر التشغيل مكتمل بالفعل", 0);
			if (wo.Status == "Cancelled") return (false, "أمر التشغيل ملغى", 0);
			if (wo.Status != "Released" && wo.Status != "InProgress") return (false, "أصدر الأمر أولًا (Release) لتحميل المواد على WIP", 0);
			if (qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر", 0);
			decimal remaining = R4(wo.Qty - wo.ProducedQty);
			if (remaining <= 0) return (false, "تم إنتاج الكمية المخطّطة بالكامل", 0);
			decimal receiveQty = qty > remaining ? remaining : qty;
			if (stdUnitCost < 0) stdUnitCost = 0m;

			var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == wo.ItemId && i.CompanyID == companyId);
			if (item == null) return (false, "الصنف المُصنَّع غير موجود", 0);
			var finCat = await _context.ItemCategories.AsNoTracking().FirstOrDefaultAsync(c => c.ID == item.ItemCategoryId);
			if (finCat?.InventoryAccountId == null) return (false, "حساب المخزون غير مربوط لفئة الصنف المُصنَّع", 0);
			var wipAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "1105").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			var appliedAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "520108").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			var varAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "520109").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (wipAcc == null || appliedAcc == null) return (false, "حسابات التصنيع (WIP/المطبّقة) غير موجودة", 0);

			bool isFinal = finalize || R4(wo.ProducedQty + receiveQty) >= wo.Qty;
			if (isFinal && varAcc == null) return (false, "حساب انحراف الإنتاج 520109 غير موجود — شغّل manuf_variance_520109.sql", 0);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var glLines = new List<JournalLineInput>();
				var accForCc = new List<int> { finCat.InventoryAccountId!.Value, wipAcc.Value };
				string desc = "إنتاج جزئي: " + (wo.WoNo ?? ("#" + wo.ID)) + " — " + item.ItemCode;
				decimal recvVal = R2(stdUnitCost * receiveQty);

				// receive finished goods at standard cost → Dr finished inventory / Cr WIP
				var (pok, perr, prod) = await PostSingleAsync(companyId, new MovementRequest
				{ Date = date, ItemId = item.ID, WarehouseId = wo.WarehouseId, Direction = 1, Qty = receiveQty, UnitCostInBase = stdUnitCost, SourceType = "WorkOrder", SourceId = wo.ID, PostToGl = false, Notes = desc }, userId);
				if (!pok) { await tx.RollbackAsync(); return (false, perr, 0); }
				if (recvVal != 0m)
				{
					glLines.Add(new JournalLineInput { AccountId = finCat.InventoryAccountId.Value, Debit = recvVal, Credit = 0, Description = desc });
					glLines.Add(new JournalLineInput { AccountId = wipAcc.Value, Debit = 0, Credit = recvVal, Description = desc });
				}
				wo.ProducedQty = R4(wo.ProducedQty + receiveQty);
				wo.WipBalance = R2(wo.WipBalance - recvVal);
				wo.Status = "InProgress";

				if (isFinal)
				{
					var labOh = R2(wo.LaborCost + wo.OverheadCost);
					if (labOh != 0m)
					{
						glLines.Add(new JournalLineInput { AccountId = wipAcc.Value, Debit = labOh, Credit = 0, Description = desc + " (عمالة مطبّقة/أوفرهيد)" });
						glLines.Add(new JournalLineInput { AccountId = appliedAcc.Value, Debit = 0, Credit = labOh, Description = desc + " (تكاليف مطبّقة)" });
						wo.WipBalance = R2(wo.WipBalance + labOh);
					}
					decimal varAmt = R2(wo.WipBalance);
					if (varAmt > 0m)   // WIP still positive → cost exceeded standard → unfavorable variance
					{
						glLines.Add(new JournalLineInput { AccountId = varAcc!.Value, Debit = varAmt, Credit = 0, Description = desc + " (انحراف غير مُوات)" });
						glLines.Add(new JournalLineInput { AccountId = wipAcc.Value, Debit = 0, Credit = varAmt, Description = desc });
					}
					else if (varAmt < 0m)   // WIP negative → produced at less than accumulated → favorable variance
					{
						glLines.Add(new JournalLineInput { AccountId = wipAcc.Value, Debit = -varAmt, Credit = 0, Description = desc });
						glLines.Add(new JournalLineInput { AccountId = varAcc!.Value, Debit = 0, Credit = -varAmt, Description = desc + " (انحراف مُوات)" });
					}
					wo.WipBalance = 0m; wo.Status = "Completed"; wo.UnitCost = stdUnitCost;
					wo.CompletedAt = DateTime.UtcNow; wo.ClosedAt = DateTime.UtcNow;
				}

				var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
				if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr, 0); }
				if (need) foreach (var l in glLines) l.CostCenterId = cc;

				if (glLines.Count > 0)
				{
					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "WorkOrder", SourceId = wo.ID, CurrencyId = 0, Description = desc, Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد الإنتاج الجزئي: " + jerr, 0); }
					wo.JournalEntryId = je!.ID;
				}
				await _context.SaveChangesAsync();
				await tx.CommitAsync();
				return (true, null, receiveQty);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء الإنتاج الجزئي: " + ex.Message, 0); }
		}

		// STAGED: cancel an order. If nothing was issued (Draft / WIP==0) just mark Cancelled. If materials were issued
		// (Released, WIP>0) reverse them: re-receive components into raw inventory (Dr raw / Cr WIP), clearing WIP.
		public async Task<(bool ok, string? error)> CancelWorkOrderAsync(int companyId, int workOrderId, DateTime date, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: functional cost dp (no static R2)
			var wo = await _context.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == workOrderId);
			if (wo == null) return (false, "أمر التشغيل غير موجود");
			if (wo.Status == "Completed") return (false, "لا يمكن إلغاء أمر مكتمل");
			if (wo.Status == "Cancelled") return (false, "أمر التشغيل ملغى بالفعل");

			if (wo.WipBalance <= 0m)   // nothing issued → cancel with no GL
			{
				wo.Status = "Cancelled"; await _context.SaveChangesAsync();
				return (true, null);
			}

			var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr);
			var comps = await _context.ManufWorkOrderComponents.Where(c => c.CompanyID == companyId && c.WorkOrderId == wo.ID && c.IssuedQty > 0).ToListAsync();
			var wipAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "1105").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (wipAcc == null) return (false, "حساب WIP (1105) غير موجود");
			var compItemIds = comps.Select(c => c.ItemId).Distinct().ToList();
			var compItems = await _context.Items.AsNoTracking().Where(i => compItemIds.Contains(i.ID)).ToListAsync();
			var compCatIds = compItems.Select(i => i.ItemCategoryId).Distinct().ToList();
			var compCats = await _context.ItemCategories.AsNoTracking().Where(c => compCatIds.Contains(c.ID)).ToListAsync();
			int? InvAccOf(int itemId) { var it = compItems.FirstOrDefault(x => x.ID == itemId); var ct = it == null ? null : compCats.FirstOrDefault(c => c.ID == it.ItemCategoryId); return ct?.InventoryAccountId; }

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var glLines = new List<JournalLineInput>();
				var accForCc = new List<int> { wipAcc.Value };
				string desc = "إلغاء أمر تشغيل (عكس المواد/العمالة): " + (wo.WoNo ?? ("#" + wo.ID));
				decimal returned = 0m;
				foreach (var c in comps)
				{
					var invAcc = InvAccOf(c.ItemId);
					if (invAcc == null) { await tx.RollbackAsync(); return (false, "حساب المخزون غير مربوط لأحد المكوّنات"); }
					var (ok, err, mv) = await PostSingleAsync(companyId, new MovementRequest
					{ Date = date, ItemId = c.ItemId, WarehouseId = wo.WarehouseId, Direction = 1, Qty = c.IssuedQty, UoMId = c.UoMId, UnitCostInBase = c.UnitCost, SourceType = "WorkOrder", SourceId = wo.ID, PostToGl = false, Notes = desc }, userId);
					if (!ok) { await tx.RollbackAsync(); return (false, $"تعذّر إرجاع مكوّن: {err}"); }
					accForCc.Add(invAcc.Value);
					glLines.Add(new JournalLineInput { AccountId = invAcc.Value, Debit = mv!.TotalCost, Credit = 0, Description = desc });   // Dr raw inventory (return)
					returned = R2(returned + mv.TotalCost);
					c.IssuedQty = 0;
				}

				// reverse posted labor lines (Dr their source accounts / Cr WIP)
				var laborLines = await _context.ManufWorkOrderLabor.Where(l => l.CompanyID == companyId && l.WorkOrderId == wo.ID).ToListAsync();
				decimal laborReturned = 0m;
				int? whtAccId = laborLines.Any(l => l.WhtAmount > 0)
					? await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "210202").Select(a => (int?)a.ID).FirstOrDefaultAsync()
					: null;
				foreach (var lab in laborLines)
				{
					if (lab.CreditAccountId == null) continue;
					var net = R2(lab.Amount - lab.WhtAmount);
					if (net != 0m) { glLines.Add(new JournalLineInput { AccountId = lab.CreditAccountId.Value, Debit = net, Credit = 0, Description = desc + " (عمالة)" }); accForCc.Add(lab.CreditAccountId.Value); }
					if (lab.WhtAmount > 0m && whtAccId != null) glLines.Add(new JournalLineInput { AccountId = whtAccId.Value, Debit = lab.WhtAmount, Credit = 0, Description = desc + " (عكس ض.خصم)" });
					laborReturned = R2(laborReturned + lab.Amount);
				}

				decimal clearWip = R2(returned + laborReturned);
				glLines.Add(new JournalLineInput { AccountId = wipAcc.Value, Debit = 0, Credit = clearWip, Description = desc });   // Cr WIP (clears materials + labor)

				var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
				if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr); }
				if (need) foreach (var l in glLines) l.CostCenterId = cc;

				if (clearWip != 0m)
				{
					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "WorkOrder", SourceId = wo.ID, CurrencyId = 0, Description = desc, Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد الإلغاء: " + jerr); }
					wo.JournalEntryId = je!.ID;
				}
				if (laborLines.Count > 0) _context.ManufWorkOrderLabor.RemoveRange(laborLines);
				wo.Status = "Cancelled"; wo.WipBalance = 0m; wo.MaterialCost = 0m;
				await _context.SaveChangesAsync();
				await tx.CommitAsync();
				return (true, null);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء إلغاء أمر التشغيل: " + ex.Message); }
		}

		// بند3: add a labor line. Posts Dr WIP / Cr (520101 employee | cash/payable+210202 external | 520108 applied).
		// Sourced labor REPLACES 520108 for that amount (no double-count). Raises WIP + the order's WipBalance.
		public async Task<(bool ok, string? error, int laborId)> AddWorkOrderLaborAsync(int companyId, int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal ratePerHour, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, DateTime date, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: labor cost rounds to functional dp (no static R2)
			var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr, 0);
			var wo = await _context.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == workOrderId);
			if (wo == null) return (false, "أمر التشغيل غير موجود", 0);
			if (wo.Status != "Released" && wo.Status != "InProgress") return (false, "تحميل العمالة متاح بعد الإصدار وقبل الإكمال فقط", 0);
			if (hours <= 0 || ratePerHour <= 0) return (false, "الساعات وسعر الساعة يجب أن يكونا أكبر من صفر", 0);
			var wipAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "1105").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (wipAcc == null) return (false, "حساب WIP (1105) غير موجود", 0);

			// MC (بند ب): the entered rate/amount are in the line currency; convert to FUNCTIONAL (Buy — we pay the worker).
			// Amount = functional equivalent (hits WIP); AmountForeign + CurrencyId + ExchangeRate preserved (same *Base principle).
			int functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			int lineCcy = (currencyId.HasValue && currencyId.Value > 0) ? currencyId.Value : functional;
			decimal amountForeign = R2(hours * ratePerHour);
			decimal fxRate; decimal amount;
			if (lineCcy == functional) { fxRate = 1m; amount = amountForeign; }
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) { fxRate = exchangeRate.Value; amount = R2(amountForeign * fxRate); }
			else { var (b, eff) = await _currency.ToBaseAsync(amountForeign, lineCcy, functional, date, "Buy"); amount = R2(b); fxRate = eff; }

			string srcCode = sourceType == "Employee" ? "520101" : sourceType == "Applied" ? "520108" : "";
			int? creditAcc = srcCode != ""
				? await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == srcCode).Select(a => (int?)a.ID).FirstOrDefaultAsync()
				: (externalCreditAccountId ?? await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "110101").Select(a => (int?)a.ID).FirstOrDefaultAsync());
			if (sourceType != "Employee" && sourceType != "Applied") sourceType = "External";
			if (creditAcc == null) return (false, "حساب الطرف الدائن للعمالة غير موجود (520101 / 110101)", 0);

			decimal whtAmount = 0m; int? whtAccId = null;
			if (whtCodeId != null && whtCodeId > 0)
			{
				var rate = await _context.TaxCodes.AsNoTracking().Where(t => t.CompanyID == companyId && t.ID == whtCodeId && t.Kind == "WHT").Select(t => (decimal?)t.Rate).FirstOrDefaultAsync();
				if (rate == null) return (false, "كود ضريبة الخصم غير صالح", 0);
				whtAmount = R2(amount * rate.Value / 100m);
				if (whtAmount > 0)
				{
					whtAccId = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "210202").Select(a => (int?)a.ID).FirstOrDefaultAsync();
					if (whtAccId == null) return (false, "حساب ضريبة الخصم والتحصيل (210202) غير موجود", 0);
				}
			}

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var glLines = new List<JournalLineInput>();
				var accForCc = new List<int> { wipAcc.Value, creditAcc.Value };
				string lbl = sourceType == "Employee" ? "موظف" : sourceType == "External" ? (workerName ?? "عامل خارجي") : "مطبّقة";
				string desc = "عمالة أمر تشغيل: " + (wo.WoNo ?? ("#" + wo.ID)) + " — " + lbl;
				glLines.Add(new JournalLineInput { AccountId = wipAcc.Value, Debit = amount, Credit = 0, Description = desc });   // Dr WIP
				var net = R2(amount - whtAmount);
				if (net != 0m) glLines.Add(new JournalLineInput { AccountId = creditAcc.Value, Debit = 0, Credit = net, Description = desc });   // Cr source (520101 / cash / 520108)
				if (whtAmount > 0m && whtAccId != null) glLines.Add(new JournalLineInput { AccountId = whtAccId.Value, Debit = 0, Credit = whtAmount, Description = desc + " (ض.خصم)" });

				var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
				if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr, 0); }
				if (need) foreach (var l in glLines) l.CostCenterId = cc;

				int? jeId = null;
				if (amount != 0m)
				{
					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "WorkOrder", SourceId = wo.ID, CurrencyId = 0, Description = desc, Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد العمالة: " + jerr, 0); }
					jeId = je!.ID;
				}
				var labor = new ManufWorkOrderLabor
				{
					CompanyID = companyId, WorkOrderId = wo.ID, SourceType = sourceType, EmployeeId = employeeId, WorkerName = workerName,
					Hours = hours, RatePerHour = ratePerHour, Amount = amount, WhtCodeId = whtCodeId, WhtAmount = whtAmount,
					CurrencyId = lineCcy, ExchangeRate = fxRate, AmountForeign = amountForeign,
					CreditAccountId = creditAcc, JournalEntryId = jeId, CreatedBy = userId, CreatedAt = DateTime.UtcNow
				};
				_context.ManufWorkOrderLabor.Add(labor);
				wo.WipBalance = R2(wo.WipBalance + amount);
				if (wo.Status == "Released") wo.Status = "InProgress";
				await _context.SaveChangesAsync();
				await tx.CommitAsync();
				return (true, null, labor.ID);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء تحميل العمالة: " + ex.Message, 0); }
		}

		// بند3: remove a labor line — post the reverse (Dr its source / Cr WIP), reduce WipBalance, delete the row.
		public async Task<(bool ok, string? error)> RemoveWorkOrderLaborAsync(int companyId, int laborId, DateTime date, string? userId)
		{
			int __fdp = await FunctionalDpAsync(companyId); decimal R2(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);   // HM-2 Batch 5: functional cost dp (no static R2)
			var lab = await _context.ManufWorkOrderLabor.FirstOrDefaultAsync(l => l.CompanyID == companyId && l.ID == laborId);
			if (lab == null) return (false, "سطر العمالة غير موجود");
			var wo = await _context.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == lab.WorkOrderId);
			if (wo == null) return (false, "أمر التشغيل غير موجود");
			if (wo.Status == "Completed" || wo.Status == "Cancelled" || wo.Status == "Closed") return (false, "لا يمكن حذف العمالة بعد إغلاق الأمر");
			var pErr = await PeriodGuardAsync(companyId, date); if (pErr != null) return (false, pErr);
			var wipAcc = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "1105").Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (wipAcc == null) return (false, "حساب WIP (1105) غير موجود");

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			try
			{
				var glLines = new List<JournalLineInput>();
				var accForCc = new List<int> { wipAcc.Value };
				string desc = "حذف عمالة أمر تشغيل: " + (wo.WoNo ?? ("#" + wo.ID));
				var net = R2(lab.Amount - lab.WhtAmount);
				if (lab.CreditAccountId != null && net != 0m) { glLines.Add(new JournalLineInput { AccountId = lab.CreditAccountId.Value, Debit = net, Credit = 0, Description = desc }); accForCc.Add(lab.CreditAccountId.Value); }
				if (lab.WhtAmount > 0m)
				{
					var whtAccId = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "210202").Select(a => (int?)a.ID).FirstOrDefaultAsync();
					if (whtAccId != null) glLines.Add(new JournalLineInput { AccountId = whtAccId.Value, Debit = lab.WhtAmount, Credit = 0, Description = desc + " (عكس ض.خصم)" });
				}
				glLines.Add(new JournalLineInput { AccountId = wipAcc.Value, Debit = 0, Credit = lab.Amount, Description = desc });   // Cr WIP

				var (need, cc, ccErr) = await ResolveCcAsync(companyId, accForCc);
				if (ccErr != null) { await tx.RollbackAsync(); return (false, ccErr); }
				if (need) foreach (var l in glLines) l.CostCenterId = cc;

				if (lab.Amount != 0m)
				{
					var (jok, jerr, je) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
					{ CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "WorkOrder", SourceId = wo.ID, CurrencyId = 0, Description = desc, Lines = glLines }, null);
					if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد حذف العمالة: " + jerr); }
				}
				_context.ManufWorkOrderLabor.Remove(lab);
				wo.WipBalance = R2(wo.WipBalance - lab.Amount);
				await _context.SaveChangesAsync();
				await tx.CommitAsync();
				return (true, null);
			}
			catch (Exception ex) { await tx.RollbackAsync(); return (false, "خطأ أثناء حذف العمالة: " + ex.Message); }
		}

		// builds + posts the 2-line journal entry for a stock movement
		private async Task<(bool ok, string? error, int? jeId)> PostGlAsync(int companyId, Item item, ItemCategory? cat, MovementRequest req, StockMovement mv, decimal value, string? userId)
		{
			int? inventoryAcc = cat?.InventoryAccountId;
			int? cogsAcc = cat?.CogsAccountId;
			int? adjAcc = cat?.AdjustmentAccountId;
			int? grniAcc = cat?.GrniAccountId;
			if (inventoryAcc == null) return (false, "حساب المخزون غير مربوط لفئة الصنف — اربطه من شاشة الفئات", null);

			// the counter account depends on the movement type
			int? counter = req.SourceType switch
			{
				"Receipt" => grniAcc ?? adjAcc,
				"PurchaseInvoice" => grniAcc ?? adjAcc,
				"PurchaseReturn" => grniAcc ?? adjAcc,   // OUT: Dr GRNI / Cr Inventory (bridges the debit-note)
				"Issue" => cogsAcc,
				"SalesInvoice" => cogsAcc,
				"SalesReturn" => cogsAcc,                 // IN: Dr Inventory / Cr COGS (reverses the sale's COGS)
				_ => adjAcc                       // Opening / Adjustment / Transfer / count
			};
			// optional override (e.g. project material issue → project execution cost 510104). Inventory side unchanged → stock_gl intact.
			if (req.CounterAccountOverride.HasValue) counter = req.CounterAccountOverride.Value;
			if (counter == null) return (false, "الحساب المقابل غير مربوط لفئة الصنف (التكلفة/التسوية/GRNI)", null);

			// some accounts (expense/COGS) require a cost center — attach a default one if so
			var accs = await _context.Accounts.AsNoTracking().Where(a => a.ID == inventoryAcc.Value || a.ID == counter.Value).ToListAsync();
			bool needsCc = accs.Any(a => a.RequireCostCenter);
			int? cc = null;
			if (needsCc)
			{
				cc = await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.ID).Select(c => (int?)c.ID).FirstOrDefaultAsync();
				if (cc == null) return (false, "الحساب المقابل يتطلب مركز تكلفة ولا يوجد مركز تكلفة معرّف", null);
			}

			var lines = new List<JournalLineInput>();
			var desc = $"حركة مخزون: {item.ItemCode} - {item.Name}";
			var proj = req.ProjectId;   // analytic dimension carried from the source document (e.g. a project-tagged sales invoice → COGS)
			if (req.Direction == 1)   // IN: Dr Inventory / Cr counter
			{
				lines.Add(new JournalLineInput { AccountId = inventoryAcc.Value, Debit = value, Credit = 0, Description = desc, CostCenterId = cc, ProjectId = proj });
				lines.Add(new JournalLineInput { AccountId = counter.Value, Debit = 0, Credit = value, Description = desc, CostCenterId = cc, ProjectId = proj });
			}
			else                      // OUT: Dr counter (COGS/Adjustment) / Cr Inventory
			{
				lines.Add(new JournalLineInput { AccountId = counter.Value, Debit = value, Credit = 0, Description = desc, CostCenterId = cc, ProjectId = proj });
				lines.Add(new JournalLineInput { AccountId = inventoryAcc.Value, Debit = 0, Credit = value, Description = desc, CostCenterId = cc, ProjectId = proj });
			}

			var (ok, err, entry) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = req.Date, JournalType = "Auto",
				SourceType = "Inventory", SourceId = mv.ID, CurrencyId = 0,
				Description = desc, Lines = lines
			}, null);
			if (!ok) return (false, "تعذّر ترحيل القيد: " + err, null);
			return (true, null, entry?.ID);
		}
	}
}
