using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Pos;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// PHYSICAL CONSUMPTION, WHICH IS NOT THE SAME FACT AS PAYMENT.
	//
	// In a restaurant the flour is gone when the pizza is cooked. The books said otherwise: stock left only
	// when the customer paid, so an order cooked and then voided consumed real food the ledger never saw, and
	// stock was wrong for the whole time an order stayed open — which in a restaurant is the entire service.
	//
	// This service records the PHYSICAL half. It deliberately does NOT post revenue, AR or VAT: those belong
	// to Pay and stay there. What it does post is the consumption itself — the same Dr COGS / Cr Inventory the
	// sale used to post, moved to the moment the ingredients actually leave, and then NOT posted again at Pay.
	//
	// WHAT IT IS NOT:
	//   * it is not the invoice path moved earlier. ReceivableService still owns the sale; this service never
	//     touches an invoice, a receipt or a customer. Mixing the two is exactly what the design forbids.
	//   * it is not a second stock writer. Every movement goes through StockService, as everything must.
	//   * it is not a new event system. It records canonical business events through IBusinessEventService.
	//   * it does not manufacture anything. A stocked semi-finished (a prepared sauce) is CONSUMED, never
	//     silently produced mid-service — the canonical explosion is called non-recursively, exactly as the
	//     sale calls it. Producing a semi-finished stays an explicit manufacturing operation.
	//
	// OPT-IN PER BRANCH, through the capability mechanism this repository already has. A branch with no
	// BranchCapability row behaves exactly as before — the declared default for an absent capability is
	// DISABLED — so nothing changes until somebody switches it on. Changing when a live money path moves
	// stock is not something to switch on for everyone at once.
	//
	// USING CAPABILITIES RATHER THAN NEW COLUMNS IS DELIBERATE. Both policies are booleans, BranchCapability
	// is exactly a per-branch boolean, and CLAUDE.md names IsCapabilityEnabledAsync as the way to gate a POS
	// behaviour per branch. It also means this batch needs NO schema change at all: no column, no slice, no
	// DDL to schedule, and no "SQL before code" ordering risk where the model expects a column production
	// does not have yet.
	public interface IPosPreparationService
	{
		/// True when this branch consumes at kitchen dispatch rather than at payment.
		Task<bool> ConsumesAtDispatchAsync(int branchId, CancellationToken cancellationToken = default);

		/// Consume the ingredients for the given order lines. Idempotent: a line already consumed is skipped,
		/// so a retried kitchen send cannot deduct twice.
		Task<PosPrepResult> ConsumeForDispatchAsync(int companyId, int orderId, IReadOnlyList<int> lineIds,
			string? userId, CancellationToken cancellationToken = default);

		/// Which of this order's lines have already had their ingredients consumed. Pay reads this to avoid
		/// deducting the same ingredients a second time.
		Task<HashSet<int>> ConsumedLineIdsAsync(int companyId, int orderId, CancellationToken cancellationToken = default);

		/// The dish was cooked and then cancelled. The food is gone: stock stays consumed and the cost is
		/// reclassified out of cost-of-sales into the inventory-adjustment account, because nothing was sold.
		Task<(bool ok, string? error, decimal wastedValue)> RecordWasteForCancelledOrderAsync(int companyId,
			int orderId, string? reason, string? userId, CancellationToken cancellationToken = default);
	}

	public sealed class PosPrepShortage
	{
		public int LineId { get; init; }
		public int ItemId { get; init; }
		public decimal Required { get; init; }
		public decimal Consumed { get; init; }
		public decimal Shortfall { get; init; }
	}

	public sealed class PosPrepResult
	{
		public IReadOnlyList<int> ConsumedLineIds { get; init; } = Array.Empty<int>();
		public IReadOnlyList<int> AlreadyConsumedLineIds { get; init; } = Array.Empty<int>();
		public IReadOnlyList<PosPrepShortage> Shortages { get; init; } = Array.Empty<PosPrepShortage>();
		public string? Error { get; init; }
		public bool Ok => Error == null;

		public static PosPrepResult Fail(string error) => new() { Error = error };
	}

	/// The vocabulary, kept here rather than in the platform's own files because those belong to another tab.
	/// The EVENT NAMES are still canonical: BusinessEventTypes.TryValidate requires
	/// "&lt;RegisteredEntityCode&gt;.&lt;PascalCaseAction&gt;", and PosOrder is a registered entity code, so these
	/// validate and record through the ordinary platform pipeline with no parallel machinery.
	public static class PosPrepEvents
	{
		public const string ConsumptionRecorded = "PosOrder.ConsumptionRecorded";
		public const string ShortageRecorded = "PosOrder.ShortageRecorded";
		public const string WasteRecorded = "PosOrder.WasteRecorded";
	}

	/// The two per-branch policies, expressed as capability keys. Absent row ⇒ disabled ⇒ today's behaviour:
	/// stock is deducted at Pay, and a short ingredient blocks the operation.
	public static class PosPrepCapabilities
	{
		/// ON  — the components leave stock when the line is sent to the kitchen, and Pay posts revenue only.
		/// OFF — the sales invoice deducts at Pay, exactly as before.
		public const string KitchenConsumption = "KitchenConsumption";

		/// ON  — a short ingredient is consumed down to zero and the shortfall is recorded as an auditable
		///       event, so service continues. Stock NEVER goes negative, and Warehouse.AllowNegativeStock is
		///       neither read nor bypassed — it stays an inventory capability, not a restaurant policy.
		/// OFF — a short ingredient refuses the whole dispatch.
		public const string ShortageAllowed = "ShortageAllowed";
	}

	public sealed class PosPreparationService : IPosPreparationService
	{
		/// The SourceType that marks an operational consumption. It is what makes the movements queryable as
		/// "what this order actually used", and it is also the idempotency key — no new column is needed to
		/// know whether a line has been consumed, because the movements themselves are that record.
		public const string PrepSourceType = "PosPrep";

		private readonly CrossDbContext _db;
		private readonly IStockService _stock;
		private readonly IJournalEntryService _journals;
		private readonly IBomExplosionService _bom;
		private readonly IBusinessEventService _events;

		public PosPreparationService(CrossDbContext db, IStockService stock, IJournalEntryService journals,
			IBomExplosionService bom, IBusinessEventService events)
		{ _db = db; _stock = stock; _journals = journals; _bom = bom; _events = events; }

		public async Task<bool> ConsumesAtDispatchAsync(int branchId, CancellationToken ct = default)
			=> await CapabilityAsync(branchId, PosPrepCapabilities.KitchenConsumption, ct);

		/// The same rule PosSetupService.IsCapabilityEnabledAsync declares: a capability is on only when an
		/// explicit row says so. Read directly rather than through that service so this one keeps a small,
		/// obvious dependency set — it is the same table and the same rule, not a second mechanism.
		private async Task<bool> CapabilityAsync(int branchId, string key, CancellationToken ct)
			=> await _db.BranchCapabilities.AsNoTracking()
				.AnyAsync(c => c.BranchId == branchId && c.CapabilityKey == key && c.Enabled, ct);

		public async Task<HashSet<int>> ConsumedLineIdsAsync(int companyId, int orderId, CancellationToken ct = default)
		{
			var ids = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.SourceType == PrepSourceType && m.SourceId == orderId && m.SourceLineId != null)
				.Select(m => m.SourceLineId!.Value).Distinct().ToListAsync(ct);
			return ids.ToHashSet();
		}

		// ===================================================================================================
		// CONSUME.
		// ===================================================================================================
		public async Task<PosPrepResult> ConsumeForDispatchAsync(int companyId, int orderId,
			IReadOnlyList<int> lineIds, string? userId, CancellationToken ct = default)
		{
			if (companyId <= 0) return PosPrepResult.Fail("لم يتم تحديد الشركة");
			var order = await _db.PosOrders.AsNoTracking()
				.FirstOrDefaultAsync(o => o.ID == orderId && o.CompanyId == companyId, ct);
			if (order == null) return PosPrepResult.Fail("الطلب غير موجود");

			var setting = await _db.BranchPosSettings.AsNoTracking().FirstOrDefaultAsync(s => s.BranchId == order.BranchId, ct);
			int? whId = setting?.DefaultSalesWarehouseId;
			if (whId == null) return PosPrepResult.Fail("لم يُحدَّد مخزن البيع الافتراضي للفرع (إعدادات نقاط البيع)");
			bool allowShortage = await CapabilityAsync(order.BranchId, PosPrepCapabilities.ShortageAllowed, ct);

			var lines = await _db.PosOrderLines.AsNoTracking()
				.Where(l => l.OrderId == orderId && lineIds.Contains(l.ID)).OrderBy(l => l.Sort).ToListAsync(ct);
			if (lines.Count == 0) return new PosPrepResult();

			// IDEMPOTENCY, read once. A retried kitchen send re-enters here with the same line ids; the ones
			// already backed by PosPrep movements are skipped rather than consumed again.
			var already = await ConsumedLineIdsAsync(companyId, orderId, ct);

			var methodByItem = await _db.BranchItemSourcings.AsNoTracking()
				.Where(s => s.BranchId == order.BranchId && s.IsActive)
				.ToDictionaryAsync(s => s.ItemId, s => s.Method, ct);
			var modsByLine = (await _db.PosOrderLineModifiers.AsNoTracking()
				.Where(m => lines.Select(l => l.ID).Contains(m.OrderLineId)).ToListAsync(ct))
				.ToLookup(m => m.OrderLineId);

			var consumed = new List<int>();
			var skipped = new List<int>();
			var shortages = new List<PosPrepShortage>();

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
				foreach (var l in lines)
				{
					if (already.Contains(l.ID)) { skipped.Add(l.ID); continue; }

					// WHAT THIS LINE PHYSICALLY USES. Identical routing to the sale, so the kitchen consumes the
					// same things the invoice would have: a RecipeAtSale dish draws its recipe, anything else
					// draws the finished item itself, and the chosen modifiers backflush either way.
					var draws = new List<(int itemId, decimal qty, int? uom)>();
					if (methodByItem.TryGetValue(l.ItemId, out var method) && method == "RecipeAtSale")
					{
						// NON-RECURSIVE, deliberately: a stocked semi-finished is consumed, never manufactured here.
						var ex = await _bom.ExplodeAsync(companyId, l.ItemId, l.Qty, cancellationToken: ct);
						if (!ex.Ok) { await tx.RollbackAsync(); return PosPrepResult.Fail(ex.Error!); }
						// THE EXPLOSION ALREADY CONVERTED. With ConvertToBaseUoM on (the default) BomLine.Quantity is
						// in the component's BASE unit, while BomLine.UoMId still names the unit the RECIPE was written
						// in — informational, not a unit to convert by. Passing it on would apply the factor a second
						// time: 500 g of flour became 0.5 kg and then 0.0005 kg. The unit is deliberately dropped here.
						foreach (var c in ex.Lines) draws.Add((c.ComponentItemId, c.Quantity, (int?)null));
					}
					else draws.Add((l.ItemId, l.Qty, l.UoMId));

					foreach (var m in modsByLine[l.ID])
					{
						var mq = Math.Round(m.QtyDeducted * l.Qty, 4, MidpointRounding.AwayFromZero);
						if (mq > 0) draws.Add((m.LinkedItemId, mq, null));
					}

					bool wroteSomething = false;
					foreach (var d in draws)
					{
						if (d.qty <= 0) continue;

						// SHORTAGE. The balance is read in BASE units, so a draw expressed in another unit is
						// compared after conversion — the same conversion StockService will apply when it posts.
						var need = await ToBaseAsync(companyId, d.itemId, d.uom, d.qty, ct);
						if (need.error != null) { await tx.RollbackAsync(); return PosPrepResult.Fail(need.error); }

						var (onHand, _, _) = await _stock.GetBalanceAsync(companyId, d.itemId, whId.Value);
						decimal take = need.qty;
						decimal shortfall = 0m;
						if (onHand < need.qty)
						{
							if (!allowShortage)
							{
								await tx.RollbackAsync();
								return PosPrepResult.Fail($"الرصيد غير كافٍ للتحضير: المتاح {onHand:0.####}، المطلوب {need.qty:0.####}");
							}
							// CONSUME WHAT IS ACTUALLY THERE. Never a negative balance — the shortfall becomes an
							// auditable fact instead of an invented quantity, and Warehouse.AllowNegativeStock is
							// neither read nor bypassed.
							take = onHand > 0 ? onHand : 0m;
							shortfall = need.qty - take;
							shortages.Add(new PosPrepShortage { LineId = l.ID, ItemId = d.itemId, Required = need.qty, Consumed = take, Shortfall = shortfall });
						}

						if (take > 0)
						{
							var (ok, err, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
							{
								Date = DateTime.Today, ItemId = d.itemId, WarehouseId = whId.Value, Direction = -1,
								Qty = take, SourceType = PrepSourceType, SourceId = orderId, SourceLineId = l.ID,
								PostToGl = true,   // Dr COGS / Cr Inventory — the cost follows the physical consumption
								Notes = $"تحضير مطبخ — طلب #{orderId}",
							}, userId);
							if (!ok) { await tx.RollbackAsync(); return PosPrepResult.Fail(err ?? "تعذّر صرف مكوّنات التحضير"); }
							wroteSomething = true;
						}
					}

					if (wroteSomething) consumed.Add(l.ID);
				}

				// THE EVENTS, INSIDE THE TRANSACTION AND BEFORE THE COMMIT — the platform's ordering rule.
				// RecordAsync refuses to run without an ambient transaction, and there is no swallowing catch.
				foreach (var id in consumed)
					await _events.RecordAsync(new BusinessEventRecord
					{
						EntityCode = EntityRegistry.PosOrder, EntityId = orderId,
						EventType = PosPrepEvents.ConsumptionRecorded,
						DedupKey = $"{PosPrepEvents.ConsumptionRecorded}:{orderId}:{id}",
						Payload = new { orderId, lineId = id, warehouseId = whId.Value },
					}, ct);

				foreach (var s in shortages)
					await _events.RecordAsync(new BusinessEventRecord
					{
						EntityCode = EntityRegistry.PosOrder, EntityId = orderId,
						EventType = PosPrepEvents.ShortageRecorded,
						DedupKey = $"{PosPrepEvents.ShortageRecorded}:{orderId}:{s.LineId}:{s.ItemId}",
						Payload = new { orderId, lineId = s.LineId, itemId = s.ItemId, required = s.Required, consumed = s.Consumed, shortfall = s.Shortfall, warehouseId = whId.Value },
					}, ct);

				await tx.CommitAsync();
				return new PosPrepResult { ConsumedLineIds = consumed, AlreadyConsumedLineIds = skipped, Shortages = shortages };
			}
			catch (Exception ex)
			{
				await tx.RollbackAsync();
				return PosPrepResult.Fail("خطأ أثناء صرف مكوّنات التحضير: " + ex.Message);
			}
		}

		// ===================================================================================================
		// WASTE — the dish was cooked and then cancelled.
		//
		// The food is gone, so the stock stays consumed: reversing the movement would claim ingredients were
		// returned to the shelf, which is a lie the count would later contradict. What IS wrong after a cancel
		// is the ACCOUNT: the cost sits in cost-of-sales, and nothing was sold. It is reclassified into the
		// inventory-adjustment account, which is the same account the write-off path already uses for stock
		// that was lost rather than sold. No stock movement, no reversal, no deletion.
		// ===================================================================================================
		public async Task<(bool ok, string? error, decimal wastedValue)> RecordWasteForCancelledOrderAsync(
			int companyId, int orderId, string? reason, string? userId, CancellationToken ct = default)
		{
			var order = await _db.PosOrders.AsNoTracking().FirstOrDefaultAsync(o => o.ID == orderId && o.CompanyId == companyId, ct);
			if (order == null) return (false, "الطلب غير موجود", 0m);

			// IDEMPOTENT: a second cancel of the same order must not waste it twice.
			bool alreadyWasted = await _db.JournalEntries.AsNoTracking()
				.AnyAsync(j => j.CompanyID == companyId && j.SourceType == "PosWaste" && j.SourceId == orderId, ct);
			if (alreadyWasted) return (true, null, 0m);

			var moves = await _db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.SourceType == PrepSourceType && m.SourceId == orderId)
				.ToListAsync(ct);
			if (moves.Count == 0) return (true, null, 0m);   // nothing was ever prepared — nothing to waste

			decimal value = Math.Round(moves.Sum(m => m.TotalCost), 4, MidpointRounding.AwayFromZero);
			if (value <= 0) return (true, null, 0m);

			var cogs = await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "510101").Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
			var waste = await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "520110").Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
			if (cogs == null || waste == null) return (false, "حساب تكلفة المبيعات (510101) أو التسويات المخزنية (520110) غير موجود", 0m);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			try
			{
				var (jok, jerr, _) = await _journals.CreateAndPostNoTxAsync(new JournalEntryInput
				{
					CompanyID = companyId, EntryDate = DateTime.Today, JournalType = "Auto",
					SourceType = "PosWaste", SourceId = orderId, CurrencyId = 0,
					Description = $"هدر تحضير — طلب كاشير #{orderId}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason}"),
					Lines = new List<JournalLineInput>
					{
						new JournalLineInput { AccountId = waste.Value, Debit = value, Credit = 0, Description = "هدر/تسوية مخزنية" },
						new JournalLineInput { AccountId = cogs.Value, Debit = 0, Credit = value, Description = "عكس تكلفة مبيعات لم تتحقق" },
					},
				}, int.TryParse(userId, out var uid) ? uid : (int?)null);
				if (!jok) { await tx.RollbackAsync(); return (false, "تعذّر ترحيل قيد الهدر: " + jerr, 0m); }

				await _events.RecordAsync(new BusinessEventRecord
				{
					EntityCode = EntityRegistry.PosOrder, EntityId = orderId,
					EventType = PosPrepEvents.WasteRecorded,
					DedupKey = $"{PosPrepEvents.WasteRecorded}:{orderId}",
					Payload = new { orderId, value, reason },
				}, ct);

				await tx.CommitAsync();
				return (true, null, value);
			}
			catch (Exception ex)
			{
				await tx.RollbackAsync();
				return (false, "خطأ أثناء تسجيل الهدر: " + ex.Message, 0m);
			}
		}

		/// The same rows and the same rule StockService uses — a non-base unit with no defined conversion is
		/// refused, never treated as factor 1. Reading the balance in the wrong unit would make the shortage
		/// decision meaningless.
		private async Task<(decimal qty, string? error)> ToBaseAsync(int companyId, int itemId, int? uomId, decimal qty, CancellationToken ct)
		{
			if (uomId == null) return (qty, null);
			var baseUoM = await _db.Items.AsNoTracking().Where(i => i.ID == itemId && i.CompanyID == companyId)
				.Select(i => (int?)i.BaseUoMId).FirstOrDefaultAsync(ct);
			if (baseUoM == null || uomId == baseUoM) return (qty, null);
			var factor = await _db.UoMConversions.AsNoTracking()
				.Where(c => c.ItemId == itemId && c.FromUoMId == uomId && c.ToUoMId == baseUoM)
				.Select(c => (decimal?)c.Factor).FirstOrDefaultAsync(ct);
			if (factor == null) return (0m, $"لا يوجد تحويل وحدة معرَّف للصنف #{itemId} من الوحدة المطلوبة إلى الوحدة الأساس");
			return (Math.Round(qty * factor.Value, 4, MidpointRounding.AwayFromZero), null);
		}
	}
}
