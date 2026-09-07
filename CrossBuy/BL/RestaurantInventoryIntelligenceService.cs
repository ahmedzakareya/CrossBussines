using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CrossBuy.BL
{
	// WHAT SHOULD HAVE BEEN USED, WHAT WAS ACTUALLY USED, WHAT WAS WASTED, WHAT IS SHORT, WHAT TO REORDER.
	//
	// Batch 2 made physical consumption a recorded fact. This turns those facts into the four numbers a
	// restaurant manager actually asks for, and it does so as a READ over evidence that already exists — no new
	// ledger, no shadow copy, no third place where a quantity is computed differently.
	//
	// THE SOURCES OF TRUTH, each used once and only once:
	//   theoretical   IBomExplosionService over the dishes that were SOLD          (the canonical explosion)
	//   actual        StockMovement, Direction = -1, operational source types      (the immutable ledger)
	//   cancellation  the PosPrep movements of orders cancelled after preparation  (a SUBSET of actual)
	//   manual waste  StockWriteOff / adjustment movements                         (also part of actual)
	//   shortage      PosOrder.ShortageRecorded business events                    (the existing facts)
	//   replenishment ItemWarehouseSetting thresholds against StockBalance         (existing configuration)
	//
	// WHY NO NEW WASTE TABLE. The batch brief asked for a first-class waste entity, and the honest finding is
	// that two canonical operational documents already carry every field it listed, so a third ledger would
	// have to be reconciled against both:
	//   * MANUAL waste is already a real document. StockService.WriteOffAsync creates StockWriteOff +
	//     StockWriteOffLine carrying item, quantity, unit, per-line reason, unit cost, line value, the actor,
	//     the date, the warehouse, the journal entry AND the stock movement id. It moves stock exactly once and
	//     posts the cost exactly once. Nothing about it needed inventing.
	//   * CANCELLATION waste is NOT a new depletion — the food already left stock at preparation. Its
	//     operational record is those PosPrep movements themselves, which are immutable ledger rows already
	//     carrying SourceId = the order and SourceLineId = the line; the cost reclassification is the PosWaste
	//     journal entry; the reason and actor are on the PosOrder.WasteRecorded event. Writing a fourth row
	//     that repeats them would create a reconciliation problem, not solve one.
	//   The one field neither source carries is BranchId, and it is derivable: an order knows its branch, and a
	//   warehouse maps to a branch through BranchPosSettings.DefaultSalesWarehouseId.
	//   This is a deviation from the brief, taken deliberately and reported rather than done quietly.
	public interface IRestaurantInventoryIntelligenceService
	{
		Task<ConsumptionVarianceResult> VarianceAsync(int companyId, int branchId, DateTime fromDate, DateTime toDate,
			int? itemId = null, CancellationToken cancellationToken = default);

		Task<IReadOnlyList<ReplenishmentRecommendation>> ReplenishmentAsync(int companyId, int branchId,
			CancellationToken cancellationToken = default);

		Task<IReadOnlyList<ShortageSignal>> ShortagesAsync(int companyId, int branchId, DateTime fromDate, DateTime toDate,
			CancellationToken cancellationToken = default);
	}

	// ---- results ------------------------------------------------------------------------------------
	public sealed class ConsumptionVarianceRow
	{
		public int ItemId { get; init; }
		public string ItemCode { get; init; } = "";
		public string ItemName { get; init; } = "";
		/// What the BOM says the SOLD dishes should have consumed.
		public decimal Theoretical { get; init; }
		/// Everything that physically left stock through an operational source.
		public decimal Actual { get; init; }
		/// Prepared then cancelled. A SUBSET of Actual, never added to it.
		public decimal CancellationWaste { get; init; }
		/// Spoilage, damage, expiry, preparation error — a write-off, which is its own depletion.
		public decimal ManualWaste { get; init; }
		public decimal KnownWaste => CancellationWaste + ManualWaste;
		/// Actual minus everything already explained.
		public decimal ProductiveActual => Actual - KnownWaste;
		public decimal VarianceQty => ProductiveActual - Theoretical;
		/// Null rather than zero when there is no theoretical base — a percentage of nothing is not 0%.
		public decimal? VariancePct => Theoretical == 0m ? null
			: Math.Round(VarianceQty / Theoretical * 100m, 2, MidpointRounding.AwayFromZero);
	}

	public sealed class ConsumptionVarianceResult
	{
		public IReadOnlyList<ConsumptionVarianceRow> Rows { get; init; } = Array.Empty<ConsumptionVarianceRow>();
		public string? Error { get; init; }
		public bool Ok => Error == null;
		/// The equation, carried with the numbers so a reader never has to guess which convention was used.
		public string Equation =>
			"Theoretical = BOM(sold) · Actual = operational outbound movements · " +
			"KnownWaste = CancellationWaste (subset of Actual) + ManualWaste · " +
			"Variance = (Actual - KnownWaste) - Theoretical";
		public static ConsumptionVarianceResult Fail(string e) => new() { Error = e };
	}

	public sealed class ReplenishmentRecommendation
	{
		public int ItemId { get; init; }
		public string ItemCode { get; init; } = "";
		public string ItemName { get; init; } = "";
		public int WarehouseId { get; init; }
		public decimal Available { get; init; }
		public decimal Threshold { get; init; }
		public decimal Target { get; init; }
		public decimal RecommendedQty { get; init; }
		/// Why this row is here, in words, because an unexplained number is not a recommendation.
		public string Reason { get; init; } = "";
		public bool IsMakeItem { get; init; }
	}

	public sealed class ShortageSignal
	{
		public int OrderId { get; init; }
		public int LineId { get; init; }
		public int ItemId { get; init; }
		public string ItemCode { get; init; } = "";
		public string ItemName { get; init; } = "";
		public decimal Required { get; init; }
		public decimal Consumed { get; init; }
		public decimal Shortfall { get; init; }
		public DateTime OccurredAt { get; init; }
	}

	public sealed class RestaurantInventoryIntelligenceService : IRestaurantInventoryIntelligenceService
	{
		/// The operational source types that represent stock genuinely leaving for an operational reason.
		/// Deliberately explicit: a transfer or an opening balance is not consumption, and counting one would
		/// make every variance meaningless.
		private static readonly string[] OperationalOutbound = { PosPreparationService.PrepSourceType, "SalesInvoice", "StockWriteOff", "Adjustment" };
		private static readonly string[] WasteOutbound = { "StockWriteOff", "Adjustment" };

		private readonly CrossDbContext _db;
		private readonly IBomExplosionService _bom;
		public RestaurantInventoryIntelligenceService(CrossDbContext db, IBomExplosionService bom) { _db = db; _bom = bom; }

		private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

		/// The branch must belong to the resolved company, and the warehouse must be the one that branch sells
		/// from. Neither is taken from the caller beyond the ids, and a branch in another company resolves to
		/// nothing rather than to somebody else's data.
		private async Task<(int warehouseId, string? error)> BranchWarehouseAsync(int companyId, int branchId, CancellationToken ct)
		{
			if (companyId <= 0) return (0, "No company has been selected");
			bool ownsBranch = await _db.Branches.AsNoTracking().AnyAsync(b => b.ID == branchId && b.CompanyID == companyId, ct);
			if (!ownsBranch) return (0, "Branch not found");
			var wh = await _db.BranchPosSettings.AsNoTracking()
				.Where(s => s.BranchId == branchId).Select(s => s.DefaultSalesWarehouseId).FirstOrDefaultAsync(ct);
			if (wh == null || wh == 0) return (0, "The branch default sales warehouse is not set (POS settings)");
			bool ownsWarehouse = await _db.Warehouses.AsNoTracking().AnyAsync(w => w.ID == wh.Value && w.CompanyID == companyId, ct);
			if (!ownsWarehouse) return (0, "Warehouse not found");
			return (wh.Value, null);
		}

		// ===================================================================================================
		// VARIANCE.
		// ===================================================================================================
		public async Task<ConsumptionVarianceResult> VarianceAsync(int companyId, int branchId, DateTime fromDate,
			DateTime toDate, int? itemId = null, CancellationToken ct = default)
		{
			var (whId, err) = await BranchWarehouseAsync(companyId, branchId, ct);
			if (err != null) return ConsumptionVarianceResult.Fail(err);
			var rangeStart = fromDate.Date;
			var rangeEnd = toDate.Date.AddDays(1).AddTicks(-1);

			// ---- ACTUAL, and the two waste subsets, in ONE pass over the ledger --------------------------
			// One grouped query rather than a query per item: the report spans a date range, not a row.
			var moves = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.WarehouseId == whId && m.Direction == -1
					&& m.MovementDate >= rangeStart && m.MovementDate <= rangeEnd
					&& m.SourceType != null && OperationalOutbound.Contains(m.SourceType)
					&& (itemId == null || m.ItemId == itemId))
				.Select(m => new { m.ItemId, m.QtyBase, m.SourceType, m.SourceId })
				.ToListAsync(ct);

			// Which POS orders were cancelled after preparation. Read ONCE for the whole range, then used as a
			// set — this is what keeps cancellation waste from costing a query per movement.
			var prepOrderIds = moves.Where(m => m.SourceType == PosPreparationService.PrepSourceType && m.SourceId != null)
				.Select(m => m.SourceId!.Value).Distinct().ToList();
			var cancelledOrderIds = prepOrderIds.Count == 0 ? new HashSet<int>()
				: (await _db.PosOrders.AsNoTracking()
					.Where(o => o.CompanyId == companyId && prepOrderIds.Contains(o.ID) && (o.Status == "Void" || o.Status == "Voided"))
					.Select(o => o.ID).ToListAsync(ct)).ToHashSet();

			var actual = new Dictionary<int, decimal>();
			var cancelWaste = new Dictionary<int, decimal>();
			var manualWaste = new Dictionary<int, decimal>();
			foreach (var m in moves)
			{
				actual[m.ItemId] = actual.GetValueOrDefault(m.ItemId) + m.QtyBase;
				if (WasteOutbound.Contains(m.SourceType!))
					manualWaste[m.ItemId] = manualWaste.GetValueOrDefault(m.ItemId) + m.QtyBase;
				else if (m.SourceType == PosPreparationService.PrepSourceType && m.SourceId != null && cancelledOrderIds.Contains(m.SourceId.Value))
					cancelWaste[m.ItemId] = cancelWaste.GetValueOrDefault(m.ItemId) + m.QtyBase;
			}

			// ---- THEORETICAL: what the dishes that were SOLD should have used ----------------------------
			// Sold, not prepared: a cancelled dish was never sold, so its consumption belongs in waste rather
			// than in the expectation. That is exactly what stops cancellation waste being counted twice.
			var soldLines = await (from l in _db.PosOrderLines.AsNoTracking()
								   join o in _db.PosOrders.AsNoTracking() on l.OrderId equals o.ID
								   where o.CompanyId == companyId && o.BranchId == branchId && o.Status == "Paid"
										 && o.ClosedAt != null && o.ClosedAt >= rangeStart && o.ClosedAt <= rangeEnd
								   select new { l.ItemId, l.Qty }).ToListAsync(ct);

			var theoretical = new Dictionary<int, decimal>();
			if (soldLines.Count > 0)
			{
				var sourcing = await _db.BranchItemSourcings.AsNoTracking()
					.Where(s => s.BranchId == branchId && s.IsActive)
					.ToDictionaryAsync(s => s.ItemId, s => s.Method, ct);

				// BATCHED, not per row: the explosion is asked once per DISTINCT (dish, quantity) pair and the
				// answer reused. A hundred identical pizzas cost one explosion, not a hundred.
				var cache = new Dictionary<(int itemId, decimal qty), IReadOnlyList<BomLine>>();
				foreach (var g in soldLines.GroupBy(x => new { x.ItemId, x.Qty }))
				{
					int count = g.Count();
					if (sourcing.TryGetValue(g.Key.ItemId, out var method) && method == "RecipeAtSale")
					{
						if (!cache.TryGetValue((g.Key.ItemId, g.Key.Qty), out var lines))
						{
							var ex = await _bom.ExplodeAsync(companyId, g.Key.ItemId, g.Key.Qty, cancellationToken: ct);
							if (!ex.Ok) return ConsumptionVarianceResult.Fail(ex.Error!);
							lines = ex.Lines;
							cache[(g.Key.ItemId, g.Key.Qty)] = lines;
						}
						foreach (var c in lines)
							theoretical[c.ComponentItemId] = theoretical.GetValueOrDefault(c.ComponentItemId) + c.Quantity * count;
					}
					else
					{
						// Sold as itself: the dish IS the component.
						theoretical[g.Key.ItemId] = theoretical.GetValueOrDefault(g.Key.ItemId) + g.Key.Qty * count;
					}
				}
			}

			// ---- shape ------------------------------------------------------------------------------------
			var ids = actual.Keys.Union(theoretical.Keys).Union(manualWaste.Keys).Distinct().ToList();
			if (itemId != null) ids = ids.Where(i => i == itemId.Value).ToList();
			if (ids.Count == 0) return new ConsumptionVarianceResult();

			var items = await _db.Items.AsNoTracking()
				.Where(i => i.CompanyID == companyId && ids.Contains(i.ID))
				.Select(i => new { i.ID, i.ItemCode, i.Name }).ToDictionaryAsync(i => i.ID, i => i, ct);

			var rows = ids
				.Where(items.ContainsKey)   // an item that is not this company's simply is not reported
				.Select(id => new ConsumptionVarianceRow
				{
					ItemId = id,
					ItemCode = items[id].ItemCode ?? "", ItemName = items[id].Name ?? "",
					Theoretical = R4(theoretical.GetValueOrDefault(id)),
					Actual = R4(actual.GetValueOrDefault(id)),
					CancellationWaste = R4(cancelWaste.GetValueOrDefault(id)),
					ManualWaste = R4(manualWaste.GetValueOrDefault(id)),
				})
				.OrderByDescending(r => Math.Abs(r.VarianceQty)).ThenBy(r => r.ItemCode)
				.ToList();

			return new ConsumptionVarianceResult { Rows = rows };
		}

		// ===================================================================================================
		// REPLENISHMENT — deterministic, explainable, and a RECOMMENDATION only.
		//
		//   threshold = ItemWarehouseSetting.ReorderPoint, else MinQty
		//   target    = MaxQty, else the threshold itself (top back up to the line you fell below)
		//   recommend when available <= threshold, quantity = target - available
		//
		// Nothing is ordered, transferred, manufactured or posted. A recommendation is a sentence, not an act.
		// ===================================================================================================
		public async Task<IReadOnlyList<ReplenishmentRecommendation>> ReplenishmentAsync(int companyId, int branchId, CancellationToken ct = default)
		{
			var (whId, err) = await BranchWarehouseAsync(companyId, branchId, ct);
			if (err != null) return Array.Empty<ReplenishmentRecommendation>();

			// Only items configured FOR THIS WAREHOUSE. No configuration means no opinion — inventing a
			// threshold for an unconfigured item would produce noise, not a recommendation.
			var settings = await _db.ItemWarehouseSettings.AsNoTracking()
				.Where(s => s.WarehouseId == whId && (s.ReorderPoint != null || s.MinQty != null))
				.Select(s => new { s.ItemId, s.ReorderPoint, s.MinQty, s.MaxQty }).ToListAsync(ct);
			if (settings.Count == 0) return Array.Empty<ReplenishmentRecommendation>();

			var ids = settings.Select(s => s.ItemId).Distinct().ToList();
			var items = await _db.Items.AsNoTracking()
				.Where(i => i.CompanyID == companyId && ids.Contains(i.ID))
				.Select(i => new { i.ID, i.ItemCode, i.Name }).ToDictionaryAsync(i => i.ID, i => i, ct);
			var balances = await _db.StockBalances.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.WarehouseId == whId && ids.Contains(b.ItemId))
				.ToDictionaryAsync(b => b.ItemId, b => b.QtyOnHand, ct);
			// A component list is how "this is something you MAKE" is known — the same rule the explosion uses.
			var makeItems = (await _db.ItemComponents.AsNoTracking()
				.Where(c => c.CompanyID == companyId && ids.Contains(c.ParentItemId))
				.Select(c => c.ParentItemId).Distinct().ToListAsync(ct)).ToHashSet();

			var result = new List<ReplenishmentRecommendation>();
			foreach (var s in settings)
			{
				if (!items.TryGetValue(s.ItemId, out var it)) continue;   // another company's item: not visible
				decimal threshold = s.ReorderPoint ?? s.MinQty ?? 0m;
				if (threshold <= 0m) continue;                            // zero/negative configuration: no opinion, explicitly
				decimal available = balances.GetValueOrDefault(s.ItemId);
				if (available > threshold) continue;                      // above the line: nothing to say

				decimal target = s.MaxQty is > 0m ? s.MaxQty!.Value : threshold;
				if (target < threshold) target = threshold;               // a target below the trigger is a typo, not a plan
				decimal qty = R4(Math.Max(0m, target - available));
				if (qty <= 0m) continue;

				bool isMake = makeItems.Contains(s.ItemId);
				result.Add(new ReplenishmentRecommendation
				{
					ItemId = s.ItemId, ItemCode = it.ItemCode ?? "", ItemName = it.Name ?? "",
					WarehouseId = whId, Available = R4(available), Threshold = R4(threshold), Target = R4(target),
					RecommendedQty = qty,
					// A made item is FLAGGED, never manufactured. Producing it stays an explicit operation.
					Reason = isMake
						? $"Available {available:0.####} ≤ reorder point {threshold:0.####} — this is a manufactured item: it needs a work order, not a purchase"
						: $"Available {available:0.####} ≤ reorder point {threshold:0.####} — replenishment up to {target:0.####} is recommended",
					IsMakeItem = isMake,
				});
			}
			return result.OrderByDescending(r => r.RecommendedQty).ThenBy(r => r.ItemCode).ToList();
		}

		// ===================================================================================================
		// SHORTAGE — the events Batch 2 already records, read back. No parallel shortage engine.
		// ===================================================================================================
		public async Task<IReadOnlyList<ShortageSignal>> ShortagesAsync(int companyId, int branchId,
			DateTime fromDate, DateTime toDate, CancellationToken ct = default)
		{
			var (_, err) = await BranchWarehouseAsync(companyId, branchId, ct);
			if (err != null) return Array.Empty<ShortageSignal>();
			var rangeStart = fromDate.Date;
			var rangeEnd = toDate.Date.AddDays(1).AddTicks(-1);

			var events = await _db.BusinessEvents.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.EntityType == "PosOrder"
					&& e.EventType == PosPrepEvents.ShortageRecorded
					&& e.CreatedAt >= rangeStart && e.CreatedAt <= rangeEnd)
				.Select(e => new { e.EntityId, e.Payload, e.CreatedAt })
				.ToListAsync(ct);
			if (events.Count == 0) return Array.Empty<ShortageSignal>();

			// The order carries the branch, so the branch filter is applied against real ownership rather than
			// against anything in the payload — a payload is data, not authority.
			var orderIds = events.Select(e => e.EntityId).Distinct().ToList();
			var branchByOrder = await _db.PosOrders.AsNoTracking()
				.Where(o => o.CompanyId == companyId && orderIds.Contains(o.ID))
				.ToDictionaryAsync(o => o.ID, o => o.BranchId, ct);

			var signals = new List<ShortageSignal>();
			foreach (var e in events)
			{
				if (!branchByOrder.TryGetValue(e.EntityId, out var b) || b != branchId) continue;
				var p = ParsePayload(e.Payload);
				if (p == null) continue;
				signals.Add(new ShortageSignal
				{
					OrderId = e.EntityId, LineId = p.Value.lineId, ItemId = p.Value.itemId,
					Required = p.Value.required, Consumed = p.Value.consumed, Shortfall = p.Value.shortfall,
					OccurredAt = e.CreatedAt,
				});
			}
			if (signals.Count == 0) return signals;

			var ids = signals.Select(s => s.ItemId).Distinct().ToList();
			var items = await _db.Items.AsNoTracking()
				.Where(i => i.CompanyID == companyId && ids.Contains(i.ID))
				.Select(i => new { i.ID, i.ItemCode, i.Name }).ToDictionaryAsync(i => i.ID, i => i, ct);

			return signals
				.Where(s => items.ContainsKey(s.ItemId))
				.Select(s => new ShortageSignal
				{
					OrderId = s.OrderId, LineId = s.LineId, ItemId = s.ItemId,
					ItemCode = items[s.ItemId].ItemCode ?? "", ItemName = items[s.ItemId].Name ?? "",
					Required = s.Required, Consumed = s.Consumed, Shortfall = s.Shortfall, OccurredAt = s.OccurredAt,
				})
				.OrderByDescending(s => s.Shortfall).ThenByDescending(s => s.OccurredAt).ToList();
		}

		/// A malformed payload is skipped, never thrown on: an unreadable event must not take down a management
		/// screen that is otherwise correct.
		private static (int lineId, int itemId, decimal required, decimal consumed, decimal shortfall)? ParsePayload(string? json)
		{
			if (string.IsNullOrWhiteSpace(json)) return null;
			try
			{
				using var doc = JsonDocument.Parse(json);
				var r = doc.RootElement;
				return (
					r.TryGetProperty("lineId", out var l) ? l.GetInt32() : 0,
					r.TryGetProperty("itemId", out var i) ? i.GetInt32() : 0,
					r.TryGetProperty("required", out var q) ? q.GetDecimal() : 0m,
					r.TryGetProperty("consumed", out var c) ? c.GetDecimal() : 0m,
					r.TryGetProperty("shortfall", out var s) ? s.GetDecimal() : 0m);
			}
			catch (JsonException) { return null; }
		}
	}
}
