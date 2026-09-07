using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class WorkOrderRow
	{
		public int Id { get; set; }
		public string? WoNo { get; set; }
		public int ItemId { get; set; }
		public string? ItemName { get; set; }
		public decimal Qty { get; set; }
		public decimal ProducedQty { get; set; }
		public string Status { get; set; } = "";
		public string? WarehouseName { get; set; }
		public decimal UnitCost { get; set; }
		public DateTime? PlannedEnd { get; set; }
	}

	// 4-3: one netted requirement line from an MRP run
	public class MrpRow
	{
		public int ItemId { get; set; }
		public string ItemCode { get; set; } = "";
		public string ItemName { get; set; } = "";
		public bool IsMake { get; set; }            // has a BOM → suggest a work order; else → purchase
		public int Level { get; set; }              // BOM depth (0 = demanded finished item)
		public decimal Gross { get; set; }          // total required
		public decimal OnHand { get; set; }         // available before this plan
		public decimal Net { get; set; }            // shortage to make/buy
		public decimal UnitCost { get; set; }
	}

	// 4-4: manufacturing reporting
	public class ManufWoCostRow
	{
		public int Id { get; set; }
		public string? WoNo { get; set; }
		public string ItemCode { get; set; } = "";
		public string ItemName { get; set; } = "";
		public DateTime? CompletedAt { get; set; }
		public decimal Qty { get; set; }
		public decimal Material { get; set; }
		public decimal Labor { get; set; }
		public decimal Overhead { get; set; }
		public decimal Total { get; set; }
		public decimal UnitCost { get; set; }      // actual produced unit cost
		public decimal StdUnitCost { get; set; }    // standard (current BOM avg + routing)
		public decimal VarUnitCost => Math.Round(UnitCost - StdUnitCost, 4);
	}

	public class WorkCenterLoadRow
	{
		public string WorkCenterName { get; set; } = "";
		public decimal Minutes { get; set; }
		public decimal LaborCost { get; set; }
		public decimal OverheadCost { get; set; }
		public decimal TotalCost => LaborCost + OverheadCost;
	}

	public class ManufReport
	{
		public DateTime From { get; set; }
		public DateTime To { get; set; }
		public int WosCompleted { get; set; }
		public decimal TotMaterial { get; set; }
		public decimal TotLabor { get; set; }
		public decimal TotOverhead { get; set; }
		public decimal TotProducedValue { get; set; }
		public List<ManufWoCostRow> Lines { get; set; } = new();
		public List<WorkCenterLoadRow> Load { get; set; } = new();
	}

	public interface IManufService
	{
		Task<(List<WorkOrderRow> rows, int total)> SearchAsync(int companyId, string? q, string? status, int page, int pageSize);
		Task<ManufWorkOrder?> GetAsync(int companyId, int id);
		Task<List<ManufWorkOrderComponent>> GetComponentsAsync(int companyId, int woId);
		Task<List<(int id, string text)>> ManufacturableItemsAsync(int companyId, string? term);   // items that have a BOM
		Task<(bool ok, string? error, int id)> CreateAsync(int companyId, int itemId, decimal qty, int warehouseId, DateTime? start, DateTime? end, decimal labor, decimal overhead, string? notes, string? userId, string? modeOverride = null);
		Task<(bool ok, string? error)> SaveHeaderAsync(int companyId, int id, decimal qty, DateTime? start, DateTime? end, decimal labor, decimal overhead, string? notes);
		Task<(bool ok, string? error)> SetStatusAsync(int companyId, int id, string status);
		Task<(bool ok, string? error)> ReleaseAsync(int companyId, int id, DateTime date, string? userId);
		Task<(bool ok, string? error)> CancelAsync(int companyId, int id, DateTime date, string? userId);
		// بند3: labor lines by source
		Task<List<ManufWorkOrderLabor>> GetLaborAsync(int companyId, int workOrderId);
		Task<(bool ok, string? error, int laborId)> AddLaborAsync(int companyId, int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal? ratePerHourOverride, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, DateTime date, string? userId, string? workerNameEn = null);
		Task<(bool ok, string? error)> RemoveLaborAsync(int companyId, int laborId, DateTime date, string? userId);
		Task<(bool ok, string? error, decimal unitCost)> CompleteAsync(int companyId, int id, DateTime date, string? userId);
		// بند5: partial/final production at standard cost (computes the std unit cost, then StockService posts + variance).
		Task<(bool ok, string? error, decimal produced)> ProducePartialAsync(int companyId, int id, decimal qty, bool finalize, DateTime date, string? userId);
		// 4-2: work centers + routing
		Task<List<ManufWorkCenter>> GetWorkCentersAsync(int companyId);
		Task<List<(int id, string name)>> WorkCentersForPickAsync(int companyId);
		Task<(bool ok, string? error)> SaveWorkCenterAsync(int companyId, ManufWorkCenter dto);
		Task<List<ManufRoutingOp>> GetRoutingAsync(int companyId, int itemId);
		Task<(bool ok, string? error)> SaveRoutingOpAsync(int companyId, ManufRoutingOp dto);
		Task<(bool ok, string? error)> DeleteRoutingOpAsync(int companyId, int id);
		Task<(decimal labor, decimal overhead)> ComputeRoutingCostAsync(int companyId, int itemId, decimal qty);
		// 4-3: production planning (MRP-lite)
		Task<List<ManufPlan>> GetPlansAsync(int companyId);
		Task<ManufPlan?> GetPlanAsync(int companyId, int id);
		Task<List<ManufPlanDemand>> GetPlanDemandsAsync(int companyId, int planId);
		Task<(bool ok, string? error, int id)> CreatePlanAsync(int companyId, string name, string? nameEn, DateTime? planDate, string? userId);
		Task<(bool ok, string? error)> AddDemandAsync(int companyId, int planId, int itemId, decimal qty, DateTime? dueDate);
		Task<(bool ok, string? error)> RemoveDemandAsync(int companyId, int demandId);
		Task<(bool ok, string? error)> DeletePlanAsync(int companyId, int id);
		Task<List<MrpRow>> RunMrpAsync(int companyId, int planId);
		Task<(bool ok, string? error, int created)> GeneratePlanWorkOrdersAsync(int companyId, int planId, int warehouseId, string? userId);
		// 4-4: reporting
		Task<(decimal material, decimal labor, decimal overhead)> ComputeStandardUnitCostAsync(int companyId, int itemId);
		Task<ManufReport> GetManufReportAsync(int companyId, DateTime from, DateTime to);
	}

	public class ManufService : IManufService
	{
		private readonly CrossDbContext _db;
		private readonly IStockService _stock;
		private readonly IEmployeeCostService _empCost;
		private readonly CrossBuy.BL.Platform.IBusinessEventService _events;   // Platform Kernel: durable business facts (in-transaction)
		private readonly IBomExplosionService _bom;   // the ONE place a bill of materials becomes quantities
		public ManufService(CrossDbContext db, IStockService stock, IEmployeeCostService empCost, CrossBuy.BL.Platform.IBusinessEventService events, IBomExplosionService bom) { _db = db; _stock = stock; _empCost = empCost; _events = events; _bom = bom; }

		// ---------------------------------------------------------------------------------------------
		// Platform Kernel slice 2 — work-order event helpers.
		//
		// The staged lifecycle methods below (Release/Cancel/Complete/ProducePartial) delegate to StockService,
		// which is the SOLE stock + GL writer and owns its own ScopedTx. Rather than edit that writer, each
		// wrapper here opens an OUTER ScopedTx: BeginOrJoinAsync makes StockService's inner transaction JOIN it
		// (becoming a no-op on commit), so the business fact and its event commit together while StockService
		// stays untouched. This is exactly what ScopedTx's own-or-join design exists for.
		//
		// It also closes a real gap: CancelWorkOrderAsync has an early-return path (nothing issued → no GL)
		// that commits with NO transaction of its own. The outer transaction covers that path too.
		// ---------------------------------------------------------------------------------------------
		private async Task<string?> ItemLabelAsync(int companyId, int itemId, CancellationToken ct = default) =>
			await _db.Items.AsNoTracking()
				.Where(i => i.ID == itemId && i.CompanyID == companyId)
				.Select(i => (i.ItemCode ?? "") + " — " + i.Name)
				.FirstOrDefaultAsync(ct);

		// Records a work-order lifecycle fact. dedupKey is set for transitions that can only happen once.
		private async Task RecordWorkOrderEventAsync(
			int companyId, ManufWorkOrder wo, string eventType, string? oldStatus,
			decimal? producedBefore = null, string[]? changedFields = null, string? dedupKey = null)
		{
			await _events.RecordAsync(new BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.ManufWorkOrder,
				EntityId = wo.ID,
				EventType = eventType,
				PayloadVersion = ManufWorkOrderEventPayload.Version,
				Visibility = BusinessEventVisibility.Internal,
				DedupKey = dedupKey,
				Payload = new ManufWorkOrderEventPayload
				{
					WorkOrderNumber = wo.WoNo,
					ItemId = wo.ItemId,
					ItemName = await ItemLabelAsync(companyId, wo.ItemId),
					PlannedQuantity = wo.Qty,
					CompletedQuantityBefore = producedBefore,
					CompletedQuantityAfter = wo.ProducedQty,
					OldStatus = oldStatus,
					NewStatus = wo.Status,
					PlannedStartDate = wo.PlannedStart,
					PlannedEndDate = wo.PlannedEnd,
					ChangedFields = changedFields,
				},
			});
		}

		// Re-reads the order AFTER a StockService call so the event carries the committed state, not the
		// pre-call snapshot. AsNoTracking would fight the tracked instance StockService just mutated.
		private Task<ManufWorkOrder?> ReloadAsync(int companyId, int id) =>
			_db.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == id);

		public async Task<(List<WorkOrderRow> rows, int total)> SearchAsync(int companyId, string? q, string? status, int page, int pageSize)
		{
			var query = from w in _db.ManufWorkOrders.AsNoTracking().Where(w => w.CompanyID == companyId)
						join it in _db.Items.AsNoTracking() on w.ItemId equals it.ID into gi
						from it in gi.DefaultIfEmpty()
						join wh in _db.Warehouses.AsNoTracking() on w.WarehouseId equals wh.ID into gw
						from wh in gw.DefaultIfEmpty()
						select new WorkOrderRow {
							Id = w.ID, WoNo = w.WoNo, ItemId = w.ItemId, ItemName = it != null ? it.Name : null,
							Qty = w.Qty, ProducedQty = w.ProducedQty, Status = w.Status, WarehouseName = wh != null ? wh.Name : null,
							UnitCost = w.UnitCost, PlannedEnd = w.PlannedEnd };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<WorkOrderRow>(terms, s => r => (r.WoNo != null && r.WoNo.Contains(s)) || (r.ItemName != null && r.ItemName.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
			var total = await query.CountAsync();
			if (pageSize <= 0) pageSize = 25; if (page < 1) page = 1;
			var rows = await query.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public Task<ManufWorkOrder?> GetAsync(int companyId, int id) =>
			_db.ManufWorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == id);

		public Task<List<ManufWorkOrderComponent>> GetComponentsAsync(int companyId, int woId) =>
			_db.ManufWorkOrderComponents.AsNoTracking().Where(c => c.CompanyID == companyId && c.WorkOrderId == woId).ToListAsync();

		public async Task<List<(int id, string text)>> ManufacturableItemsAsync(int companyId, string? term)
		{
			var t = (term ?? "").Trim();
			// any item that has at least one BOM component (ItemComponent) is manufacturable
			var withBom = _db.ItemComponents.AsNoTracking().Select(c => c.ParentItemId).Distinct();
			var q = _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && i.IsActive && withBom.Contains(i.ID));
			if (t.Length > 0) q = q.Where(i => i.Name.Contains(t) || i.ItemCode.Contains(t));
			var rows = await q.OrderBy(i => i.ItemCode).Take(20).Select(i => new { i.ID, i.ItemCode, i.Name }).ToListAsync();
			return rows.Select(r => (r.ID, $"{r.ItemCode} — {r.Name}")).ToList();
		}

		public async Task<(bool ok, string? error, int id)> CreateAsync(int companyId, int itemId, decimal qty, int warehouseId, DateTime? start, DateTime? end, decimal labor, decimal overhead, string? userId)
			=> await CreateAsync(companyId, itemId, qty, warehouseId, start, end, labor, overhead, null, userId);

		public async Task<(bool ok, string? error, int id)> CreateAsync(int companyId, int itemId, decimal qty, int warehouseId, DateTime? start, DateTime? end, decimal labor, decimal overhead, string? notes, string? userId, string? modeOverride = null)
		{
			if (itemId <= 0) return (false, "Choose the manufactured item", 0);
			if (qty <= 0) return (false, "Quantity must be greater than zero", 0);
			if (warehouseId <= 0) return (false, "Choose the warehouse", 0);
			// ===== COMPANY BOUNDARY. Every authoritative input is validated as belonging to `companyId`
			// BEFORE anything is written. =====
			//
			// What used to happen: none of the three inputs was checked for ownership. The bill of materials was
			// read as `Where(c => c.ParentItemId == itemId)` with no company predicate at all, so any company
			// recipe was readable by any other; the manufactured item WAS read with a company predicate but only
			// to pick a production mode, and a null result silently defaulted to "OrderBased" instead of refusing;
			// and warehouseId was only checked for `> 0`. Company 1 could therefore open a live work order on
			// company 2 item, in company 2 warehouse, planning company 2 components. Not a stray row either: the
			// component rows are stamped `CompanyID = companyId` (the CALLER) while `ItemId` pointed at the other
			// tenant parts, so the order was a cross-tenant hybrid that would consume caller stock against a
			// recipe it was never entitled to read.
			//
			// A FOREIGN ID AND A MISSING ID GET THE SAME REFUSAL, deliberately. If "belongs to someone else" and
			// "does not exist" said different things, this method would answer the question "does id N exist in
			// another company?" - an id here would become a tenant probe. Both are "not found".
			//
			// `companyId` is not client-supplied on any path that reaches here: InventoryController derives it
			// from IRequestCompanyResolver (never from a request parameter) and PosOrderService passes its own
			// resolved company. So the value is already the resolved one, and no BusinessContext accessor is
			// introduced into this constructor to re-derive it - that would add a DI coupling to a service that
			// currently has none, for a value the two callers have already resolved.
			var prod = await _db.Items.AsNoTracking()
				.Where(i => i.ID == itemId && i.CompanyID == companyId)
				.Select(i => new { i.ProductionMethod })
				.FirstOrDefaultAsync();
			if (prod == null) return (false, "The manufactured item was not found", 0);

			var warehouseOwned = await _db.Warehouses.AsNoTracking()
				.AnyAsync(w => w.ID == warehouseId && w.CompanyID == companyId);
			if (!warehouseOwned) return (false, "Warehouse not found", 0);

			// The recipe is now company-scoped, so a foreign BOM is not merely rejected later - it is never read.
			var bom = await _db.ItemComponents.AsNoTracking()
				.Where(c => c.ParentItemId == itemId && c.CompanyID == companyId)
				.OrderBy(c => c.SortOrder).ToListAsync();
			if (bom.Count == 0) return (false, "This item has no bill of materials — add its components first", 0);
			// Every component the plan will reference must be ours too. A BOM row inside our own recipe can
			// still name a foreign component id, and those ids are what get stamped onto ManufWorkOrderComponents
			// and later consumed as stock - so this is checked before a single row is written, not at consumption.
			var componentIds = bom.Select(b => b.ComponentItemId).Distinct().ToList();
			var ownedComponentCount = await _db.Items.AsNoTracking()
				.CountAsync(i => componentIds.Contains(i.ID) && i.CompanyID == companyId);
			if (ownedComponentCount != componentIds.Count)
				return (false, "The bill of materials contains a component that does not belong to this company", 0);

			// stamp the production mode from the item (overridable later); only OrderBased uses staged WO lifecycle
			var prodMethod = prod.ProductionMethod;
			var mode = !string.IsNullOrWhiteSpace(modeOverride) ? modeOverride
				: (string.IsNullOrWhiteSpace(prodMethod) ? "OrderBased" : prodMethod);
			if (mode != "Immediate" && mode != "OrderBased") mode = "OrderBased";

			var wo = new ManufWorkOrder { CompanyID = companyId, ItemId = itemId, Qty = qty, WarehouseId = warehouseId,
				PlannedStart = start, PlannedEnd = end, LaborCost = labor < 0 ? 0 : labor, OverheadCost = overhead < 0 ? 0 : overhead,
				Notes = notes, Status = "Draft", Mode = mode, CreatedBy = userId, CreatedAt = DateTime.UtcNow };
			// 4-2: if the item has a routing, compute time-based labor/overhead and use it (overrides manual entry)
			var (rlabor, roh) = await ComputeRoutingCostAsync(companyId, itemId, qty);
			if (rlabor > 0 || roh > 0) { wo.LaborCost = rlabor; wo.OverheadCost = roh; }
			// Platform Kernel slice 2: creation writes the order, its number and its component rows in three
			// SaveChanges calls that previously had no transaction between them. One ScopedTx now makes the
			// whole creation — and its event — atomic.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			_db.ManufWorkOrders.Add(wo); await _db.SaveChangesAsync();
			wo.WoNo = $"WO-{wo.ID:D5}";
			// Planned quantities come from the canonical explosion. ConvertToBaseUoM = false because PlannedQty is
			// STORED alongside its UoMId and the issue path converts it at PostMovementAsync — converting here too
			// would apply the factor twice, and the stored row would no longer agree with the unit it names.
			var planned = await _bom.ExplodeAsync(companyId, itemId, qty, new BomExplosionOptions { ConvertToBaseUoM = false });
			if (!planned.Ok) return (false, planned.Error, 0);
			foreach (var b in planned.Lines)
				_db.ManufWorkOrderComponents.Add(new ManufWorkOrderComponent { CompanyID = companyId, WorkOrderId = wo.ID, ItemId = b.ComponentItemId, PlannedQty = b.Quantity, UoMId = b.UoMId });
			await _db.SaveChangesAsync();

			await RecordWorkOrderEventAsync(companyId, wo, CrossBuy.BL.Platform.ManufWorkOrderEvents.Created,
				oldStatus: null, dedupKey: $"ManufWorkOrder.Created:{wo.ID}");

			await tx.CommitAsync();
			return (true, null, wo.ID);
		}

		public async Task<(bool ok, string? error)> SaveHeaderAsync(int companyId, int id, decimal qty, DateTime? start, DateTime? end, decimal labor, decimal overhead, string? notes)
		{
			var wo = await _db.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == id);
			if (wo == null) return (false, "Not found");
			if (wo.Status != "Draft") return (false, "It cannot be edited after release");
			if (qty <= 0) return (false, "Quantity must be greater than zero");

			// Snapshot before mutation for the change summary.
			var beforeQty = wo.Qty; var beforeStart = wo.PlannedStart; var beforeEnd = wo.PlannedEnd;
			var beforeLabor = wo.LaborCost; var beforeOverhead = wo.OverheadCost; var beforeNotes = wo.Notes;

			var ratio = wo.Qty > 0 ? qty / wo.Qty : 1m;
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			wo.Qty = qty; wo.PlannedStart = start; wo.PlannedEnd = end; wo.LaborCost = labor < 0 ? 0 : labor; wo.OverheadCost = overhead < 0 ? 0 : overhead; wo.Notes = notes;
			// rescale component planned quantities to the new qty
			if (ratio != 1m)
			{
				var comps = await _db.ManufWorkOrderComponents.Where(c => c.CompanyID == companyId && c.WorkOrderId == id).ToListAsync();
				foreach (var c in comps) c.PlannedQty = Math.Round(c.PlannedQty * ratio, 4);
			}
			await _db.SaveChangesAsync();

			var changed = new List<string>();
			if (beforeQty != wo.Qty) changed.Add(nameof(wo.Qty));
			if (beforeStart != wo.PlannedStart) changed.Add(nameof(wo.PlannedStart));
			if (beforeEnd != wo.PlannedEnd) changed.Add(nameof(wo.PlannedEnd));
			if (beforeLabor != wo.LaborCost) changed.Add(nameof(wo.LaborCost));
			if (beforeOverhead != wo.OverheadCost) changed.Add(nameof(wo.OverheadCost));
			if (beforeNotes != wo.Notes) changed.Add(nameof(wo.Notes));

			// No DedupKey: a draft order may be edited repeatedly, and each edit is its own fact.
			await RecordWorkOrderEventAsync(companyId, wo, CrossBuy.BL.Platform.ManufWorkOrderEvents.Updated,
				oldStatus: wo.Status, changedFields: changed.Count > 0 ? changed.ToArray() : null);

			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SetStatusAsync(int companyId, int id, string status)
		{
			var wo = await _db.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == id);
			if (wo == null) return (false, "Not found");
			if (wo.Status == "Completed") return (false, "The work order is complete");
			if (status == "Released" && wo.Status != "Draft") return (false, "Invalid status");
			if (status != "Released" && status != "Cancelled") return (false, "Unsupported status");

			// The guards above are the REAL transition rules. An event is recorded only past them, so a
			// rejected transition (Released from a non-Draft order, an unsupported status, a completed order)
			// produces no event at all.
			var oldStatus = wo.Status;
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			wo.Status = status; await _db.SaveChangesAsync();

			await RecordWorkOrderEventAsync(companyId, wo,
				status == "Released"
					? CrossBuy.BL.Platform.ManufWorkOrderEvents.Released
					: CrossBuy.BL.Platform.ManufWorkOrderEvents.Cancelled,
				oldStatus: oldStatus,
				// Each of these transitions can only happen once per order (the guards block a repeat), so the
				// key is pinned — a retried command cannot double-record it.
				dedupKey: $"ManufWorkOrder.{status}:{wo.ID}");

			await tx.CommitAsync();
			return (true, null);
		}

		// staged lifecycle (route to StockService — the sole stock + GL writer).
		// Platform Kernel slice 2 wraps each one in an OUTER ScopedTx that StockService's own transaction joins,
		// so the stock/GL work and the event commit together without editing the writer.
		public async Task<(bool ok, string? error)> ReleaseAsync(int companyId, int id, DateTime date, string? userId)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var before = await ReloadAsync(companyId, id);
			var oldStatus = before?.Status;

			var (ok, error) = await _stock.ReleaseWorkOrderAsync(companyId, id, date, userId);
			if (!ok) return (false, error);   // not committed → the outer transaction rolls everything back

			var wo = await ReloadAsync(companyId, id);
			if (wo != null)
				await RecordWorkOrderEventAsync(companyId, wo, CrossBuy.BL.Platform.ManufWorkOrderEvents.Released,
					oldStatus: oldStatus, dedupKey: $"ManufWorkOrder.Released:{id}");

			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> CancelAsync(int companyId, int id, DateTime date, string? userId)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var before = await ReloadAsync(companyId, id);
			var oldStatus = before?.Status;

			var (ok, error) = await _stock.CancelWorkOrderAsync(companyId, id, date, userId);
			if (!ok) return (false, error);

			var wo = await ReloadAsync(companyId, id);
			if (wo != null)
				await RecordWorkOrderEventAsync(companyId, wo, CrossBuy.BL.Platform.ManufWorkOrderEvents.Cancelled,
					oldStatus: oldStatus, dedupKey: $"ManufWorkOrder.Cancelled:{id}");

			await tx.CommitAsync();
			return (true, null);
		}

		// بند3: labor lines
		public Task<List<ManufWorkOrderLabor>> GetLaborAsync(int companyId, int workOrderId) =>
			_db.ManufWorkOrderLabor.AsNoTracking().Where(l => l.CompanyID == companyId && l.WorkOrderId == workOrderId).OrderBy(l => l.ID).ToListAsync();

		// resolves the employee hour-rate (explicit field → salary fallback → BLOCK) then posts via StockService
		public async Task<(bool ok, string? error, int laborId)> AddLaborAsync(int companyId, int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal? ratePerHourOverride, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, DateTime date, string? userId, string? workerNameEn = null)
		{
			decimal rate = ratePerHourOverride ?? 0m;
			if (sourceType == "Employee")
			{
				if (employeeId == null || employeeId <= 0) return (false, "Choose the employee", 0);
				if (rate <= 0)
				{
					// TM-4: single source of truth for the hourly cost (extracted to IEmployeeCostService; no duplication)
					rate = await _empCost.HourlyCostAsync(companyId, employeeId.Value);
					if (rate <= 0) return (false, "The employee has no manufacturing hourly rate and no salary to derive one from — set an hourly rate on the employee, or link a salary policy and set the standard hours in the payroll settings.", 0);
				}
			}
			else if (rate <= 0) return (false, "Enter the hourly rate", 0);   // External / Applied
			// currency/exchangeRate matter for External foreign-currency lines only; StockService converts to functional
			return await _stock.AddWorkOrderLaborAsync(companyId, workOrderId, sourceType, employeeId, workerName, hours, rate, whtCodeId, externalCreditAccountId, currencyId, exchangeRate, date, userId, workerNameEn);
		}

		public Task<(bool ok, string? error)> RemoveLaborAsync(int companyId, int laborId, DateTime date, string? userId)
			=> _stock.RemoveWorkOrderLaborAsync(companyId, laborId, date, userId);

		public async Task<(bool ok, string? error, decimal unitCost)> CompleteAsync(int companyId, int id, DateTime date, string? userId)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var before = await ReloadAsync(companyId, id);
			var oldStatus = before?.Status;
			var producedBefore = before?.ProducedQty;

			var (ok, error, unitCost) = await _stock.CompleteWorkOrderAsync(companyId, id, date, userId);
			if (!ok) return (false, error, 0);

			var wo = await ReloadAsync(companyId, id);
			if (wo != null)
				await RecordWorkOrderEventAsync(companyId, wo, CrossBuy.BL.Platform.ManufWorkOrderEvents.Completed,
					oldStatus: oldStatus, producedBefore: producedBefore,
					dedupKey: $"ManufWorkOrder.Completed:{id}");

			await tx.CommitAsync();
			return (true, null, unitCost);
		}

		public async Task<(bool ok, string? error, decimal produced)> ProducePartialAsync(int companyId, int id, decimal qty, bool finalize, DateTime date, string? userId)
		{
			var wo = await _db.ManufWorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == id);
			if (wo == null) return (false, "Work order not found", 0);
			var (m, l, o) = await ComputeStandardUnitCostAsync(companyId, wo.ItemId);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var oldStatus = wo.Status;
			var producedBefore = wo.ProducedQty;

			var (ok, error, produced) = await _stock.ProducePartialAsync(companyId, id, qty, m + l + o, finalize, date, userId);
			if (!ok) return (false, error, 0);

			var after = await ReloadAsync(companyId, id);
			if (after != null)
			{
				// Which fact this is depends on the OUTCOME, not the caller's flag: finalize only completes the
				// order when the whole quantity is done, so the status after the call is the authority.
				bool completed = after.Status == "Completed";
				await RecordWorkOrderEventAsync(companyId, after,
					completed
						? CrossBuy.BL.Platform.ManufWorkOrderEvents.Completed
						: CrossBuy.BL.Platform.ManufWorkOrderEvents.Produced,
					oldStatus: oldStatus, producedBefore: producedBefore,
					// Completion happens once, so it is pinned. A partial production legitimately repeats and
					// each one is its own fact, so it is not.
					dedupKey: completed ? $"ManufWorkOrder.Completed:{id}" : null);
			}

			await tx.CommitAsync();
			return (true, null, produced);
		}

		// ---- 4-2: work centers + routing ----
		public Task<List<ManufWorkCenter>> GetWorkCentersAsync(int companyId) =>
			_db.ManufWorkCenters.AsNoTracking().Where(w => w.CompanyID == companyId).OrderBy(w => w.ID).ToListAsync();

		public async Task<List<(int id, string name)>> WorkCentersForPickAsync(int companyId)
		{
			var rows = await _db.ManufWorkCenters.AsNoTracking().Where(w => w.CompanyID == companyId && w.IsActive).OrderBy(w => w.Name).Select(w => new { w.ID, w.Code, w.Name, w.NameEn }).ToListAsync();
			// The picker label follows the reader's language, falling back to the Arabic name when
			// the English one was never filled in -- a blank option is worse than one in Arabic.
			return rows.Select(r =>
			{
				var name = DisplayName.Of(r.Name, r.NameEn);
				return (r.ID, string.IsNullOrWhiteSpace(r.Code) ? name : $"{r.Code} — {name}");
			}).ToList();
		}

		public async Task<(bool ok, string? error)> SaveWorkCenterAsync(int companyId, ManufWorkCenter dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Work centre name is required");
			ManufWorkCenter e;
			if (dto.ID > 0) { e = await _db.ManufWorkCenters.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == dto.ID) ?? throw new InvalidOperationException("Not found"); }
			else { e = new ManufWorkCenter { CompanyID = companyId, CreatedAt = DateTime.UtcNow }; _db.ManufWorkCenters.Add(e); }
			e.Code = dto.Code; e.Name = dto.Name.Trim(); e.NameEn = string.IsNullOrWhiteSpace(dto.NameEn) ? null : dto.NameEn.Trim(); e.CostPerHour = dto.CostPerHour < 0 ? 0 : dto.CostPerHour; e.OverheadPerHour = dto.OverheadPerHour < 0 ? 0 : dto.OverheadPerHour; e.IsActive = dto.IsActive;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public Task<List<ManufRoutingOp>> GetRoutingAsync(int companyId, int itemId) =>
			_db.ManufRoutingOps.AsNoTracking().Where(o => o.CompanyID == companyId && o.ItemId == itemId).OrderBy(o => o.Seq).ToListAsync();

		public async Task<(bool ok, string? error)> SaveRoutingOpAsync(int companyId, ManufRoutingOp dto)
		{
			if (dto.ItemId <= 0) return (false, "Choose the item");
			if (dto.WorkCenterId <= 0) return (false, "Choose the work centre");
			ManufRoutingOp e;
			if (dto.ID > 0) { e = await _db.ManufRoutingOps.FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == dto.ID) ?? throw new InvalidOperationException("Not found"); }
			else { e = new ManufRoutingOp { CompanyID = companyId, ItemId = dto.ItemId, CreatedAt = DateTime.UtcNow }; _db.ManufRoutingOps.Add(e); }
			e.Seq = dto.Seq <= 0 ? 1 : dto.Seq; e.WorkCenterId = dto.WorkCenterId; e.OperationName = dto.OperationName; e.OperationNameEn = string.IsNullOrWhiteSpace(dto.OperationNameEn) ? null : dto.OperationNameEn.Trim();
			e.SetupMins = dto.SetupMins < 0 ? 0 : dto.SetupMins; e.RunMinsPerUnit = dto.RunMinsPerUnit < 0 ? 0 : dto.RunMinsPerUnit;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteRoutingOpAsync(int companyId, int id)
		{
			var e = await _db.ManufRoutingOps.FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == id);
			if (e == null) return (false, "Not found");
			_db.ManufRoutingOps.Remove(e); await _db.SaveChangesAsync(); return (true, null);
		}

		// labor/overhead from routing: Σ ops [(SetupMins + RunMinsPerUnit×qty)/60 × work-center rate]
		public async Task<(decimal labor, decimal overhead)> ComputeRoutingCostAsync(int companyId, int itemId, decimal qty)
		{
			var ops = await _db.ManufRoutingOps.AsNoTracking().Where(o => o.CompanyID == companyId && o.ItemId == itemId).ToListAsync();
			if (ops.Count == 0) return (0, 0);
			var wcIds = ops.Select(o => o.WorkCenterId).Distinct().ToList();
			// Company-scoped: the work-center rates feed the labour/overhead stamped on a work order, and this
			// runs inside CreateAsync, so an unscoped read here would let another company cost rates reach ours.
			var wcs = await _db.ManufWorkCenters.AsNoTracking().Where(w => w.CompanyID == companyId && wcIds.Contains(w.ID)).ToDictionaryAsync(w => w.ID, w => w);
			decimal labor = 0, oh = 0;
			foreach (var op in ops)
			{
				if (!wcs.TryGetValue(op.WorkCenterId, out var wc)) continue;
				var hours = (op.SetupMins + op.RunMinsPerUnit * qty) / 60m;
				labor += hours * wc.CostPerHour; oh += hours * wc.OverheadPerHour;
			}
			return (Math.Round(labor, 4), Math.Round(oh, 4));
		}

		// ---- 4-3: production planning (MRP-lite) ----
		public Task<List<ManufPlan>> GetPlansAsync(int companyId) =>
			_db.ManufPlans.AsNoTracking().Where(p => p.CompanyID == companyId).OrderByDescending(p => p.ID).ToListAsync();

		public Task<ManufPlan?> GetPlanAsync(int companyId, int id) =>
			_db.ManufPlans.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == id);

		public Task<List<ManufPlanDemand>> GetPlanDemandsAsync(int companyId, int planId) =>
			_db.ManufPlanDemands.AsNoTracking().Where(d => d.CompanyID == companyId && d.PlanId == planId).OrderBy(d => d.ID).ToListAsync();

		public async Task<(bool ok, string? error, int id)> CreatePlanAsync(int companyId, string name, string? nameEn, DateTime? planDate, string? userId)
		{
			if (string.IsNullOrWhiteSpace(name)) return (false, "Plan name is required", 0);
			var p = new ManufPlan { CompanyID = companyId, Name = name.Trim(), NameEn = string.IsNullOrWhiteSpace(nameEn) ? null : nameEn.Trim(), PlanDate = planDate ?? DateTime.UtcNow, Status = "Draft", CreatedBy = userId, CreatedAt = DateTime.UtcNow };
			_db.ManufPlans.Add(p); await _db.SaveChangesAsync();
			return (true, null, p.ID);
		}

		public async Task<(bool ok, string? error)> AddDemandAsync(int companyId, int planId, int itemId, decimal qty, DateTime? dueDate)
		{
			var plan = await _db.ManufPlans.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == planId);
			if (plan == null) return (false, "Plan not found");
			if (qty <= 0) return (false, "Quantity must be greater than zero");
			var hasBom = await _db.ItemComponents.AnyAsync(c => c.ParentItemId == itemId);
			if (!hasBom) return (false, "Choose a manufactured item that has a bill of materials");
			_db.ManufPlanDemands.Add(new ManufPlanDemand { CompanyID = companyId, PlanId = planId, ItemId = itemId, Qty = qty, DueDate = dueDate, CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RemoveDemandAsync(int companyId, int demandId)
		{
			var d = await _db.ManufPlanDemands.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == demandId);
			if (d == null) return (false, "Not found");
			_db.ManufPlanDemands.Remove(d); await _db.SaveChangesAsync(); return (true, null);
		}

		public async Task<(bool ok, string? error)> DeletePlanAsync(int companyId, int id)
		{
			var p = await _db.ManufPlans.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (p == null) return (false, "Not found");
			_db.ManufPlanDemands.RemoveRange(_db.ManufPlanDemands.Where(d => d.CompanyID == companyId && d.PlanId == id));
			_db.ManufPlans.Remove(p); await _db.SaveChangesAsync(); return (true, null);
		}

		// Multi-level BOM explosion with on-hand netting. Available stock is consumed once (top-down),
		// and only the NET shortage of a make-item is exploded into its components.
		public async Task<List<MrpRow>> RunMrpAsync(int companyId, int planId)
		{
			var demands = await _db.ManufPlanDemands.AsNoTracking().Where(d => d.CompanyID == companyId && d.PlanId == planId).ToListAsync();
			if (demands.Count == 0) return new();

			// ON-HAND per item, which is what the walk nets against as it descends.
			var onHand = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId)
				.GroupBy(b => b.ItemId).Select(g => new { ItemId = g.Key, Qty = g.Sum(x => x.QtyOnHand), Val = g.Sum(x => x.TotalValue) })
				.ToDictionaryAsync(x => x.ItemId, x => x);
			var available = onHand.ToDictionary(k => k.Key, v => v.Value.Qty);

			// THE RECURSION THAT USED TO LIVE HERE NOW LIVES IN IBomExplosionService, unchanged in behaviour.
			// It was the strongest implementation in the repository, so it BECAME the canonical one rather than
			// being replaced by a weaker abstraction: gross accumulated per item across all demands, netted against
			// the RUNNING `available` map as the walk descends, only the NET shortage exploded, scrap applied at
			// every level, the shallowest depth kept as the item's level, and the same depth cap. What changed is
			// that quantities are rounded by the canonical policy instead of propagating unrounded.
			//
			// ConvertToBaseUoM = false preserves today's MRP exactly. MRP compares against on-hand held in BASE
			// units while recipe rows may name another unit, so conversion is a real correction — but this method
			// returns a bare List<MrpRow> with no error channel, and a missing conversion must not silently become
			// an empty plan. Enabling it belongs with an error channel, and is reported as a follow-up.
			var req = await _bom.BomRequirementsAsync(companyId,
				demands.Select(d => (d.ItemId, d.Qty)).ToList(), available,
				new BomExplosionOptions { Recursive = true, NetAgainstOnHand = true, ConvertToBaseUoM = false });
			// A malformed bill of materials (a cycle) now yields an EMPTY plan rather than the silently truncated
			// one the old depth cap produced. An empty plan is visibly wrong to a planner; truncated garbage is not.
			if (!req.Ok) return new();

			var rows = req.Requirements.ToDictionary(r => r.ItemId, r => new MrpRow
			{ ItemId = r.ItemId, IsMake = r.IsMakeItem, Gross = r.Gross, Net = r.Net, Level = r.Level });

			// enrich with item code/name/unit-cost
			var ids = rows.Keys.ToList();
			var items = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i);
			foreach (var r in rows.Values)
			{
				items.TryGetValue(r.ItemId, out var it);
				r.ItemCode = it?.ItemCode ?? ("#" + r.ItemId); r.ItemName = it?.Name ?? "";
				r.OnHand = onHand.TryGetValue(r.ItemId, out var oh) ? oh.Qty : 0m;
				r.UnitCost = (onHand.TryGetValue(r.ItemId, out var oh2) && oh2.Qty != 0) ? Math.Round(oh2.Val / oh2.Qty, 4) : 0m;
				// Level already came from the canonical walk (the shallowest depth the item was reached at).
			}
			// make-items first, then by level, then code
			return rows.Values.OrderByDescending(r => r.IsMake).ThenBy(r => r.Level).ThenBy(r => r.ItemCode).ToList();
		}

		public async Task<(bool ok, string? error, int created)> GeneratePlanWorkOrdersAsync(int companyId, int planId, int warehouseId, string? userId)
		{
			var plan = await _db.ManufPlans.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == planId);
			if (plan == null) return (false, "Plan not found", 0);
			var rows = await RunMrpAsync(companyId, planId);
			var makeShortages = rows.Where(r => r.IsMake && r.Net > 0).ToList();
			if (makeShortages.Count == 0) return (false, "There are no manufacturing shortages that require work orders", 0);
			int created = 0;
			foreach (var r in makeShortages)
			{
				var (ok, _, _) = await CreateAsync(companyId, r.ItemId, r.Net, warehouseId, null, null, 0, 0, $"From plan: {plan.Name}", userId);
				if (ok) created++;
			}
			plan.Status = "Generated"; await _db.SaveChangesAsync();
			return (true, null, created);
		}

		// ---- 4-4: reporting ----
		// standard unit cost = current BOM material (direct components × avg cost) + routing labor/overhead per unit
		public async Task<(decimal material, decimal labor, decimal overhead)> ComputeStandardUnitCostAsync(int companyId, int itemId)
		{
			decimal material = 0m;
			// Quantities from the canonical explosion for ONE unit. ConvertToBaseUoM = true here, unlike the paths
			// that hand a UoMId onward: the unit cost below is derived from StockBalances, which are held in BASE
			// units, so the quantity must be in base units for the multiplication to mean anything.
			// Single level, as before — a semi-finished component is valued at its own stock cost, not re-exploded.
			var std = await _bom.ExplodeAsync(companyId, itemId, 1m);
			var comps = std.Ok ? std.Lines : Array.Empty<BomLine>() as IReadOnlyList<BomLine>;
			if (comps.Count > 0)
			{
				var ids = comps.Select(c => c.ComponentItemId).Distinct().ToList();
				var bals = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && ids.Contains(b.ItemId))
					.GroupBy(b => b.ItemId).Select(g => new { ItemId = g.Key, Qty = g.Sum(x => x.QtyOnHand), Val = g.Sum(x => x.TotalValue) })
					.ToDictionaryAsync(x => x.ItemId, x => x);
				foreach (var c in comps)
				{
					decimal uc = (bals.TryGetValue(c.ComponentItemId, out var b) && b.Qty != 0) ? b.Val / b.Qty : 0m;
					material += c.Quantity * uc;   // canonical: scrap already applied, 4dp AwayFromZero
				}
			}
			var (labor, overhead) = await ComputeRoutingCostAsync(companyId, itemId, 1);
			return (Math.Round(material, 4), labor, overhead);
		}

		public async Task<ManufReport> GetManufReportAsync(int companyId, DateTime from, DateTime to)
		{
			var rep = new ManufReport { From = from, To = to };
			var toEnd = to.Date.AddDays(1).AddTicks(-1);
			var wos = await _db.ManufWorkOrders.AsNoTracking()
				.Where(w => w.CompanyID == companyId && w.Status == "Completed" && w.CompletedAt != null && w.CompletedAt >= from.Date && w.CompletedAt <= toEnd)
				.OrderBy(w => w.CompletedAt).ToListAsync();
			if (wos.Count == 0) return rep;

			var itemIds = wos.Select(w => w.ItemId).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i);
			var stdCache = new Dictionary<int, (decimal m, decimal l, decimal o)>();
			foreach (var id in itemIds) stdCache[id] = await ComputeStandardUnitCostAsync(companyId, id);

			foreach (var w in wos)
			{
				items.TryGetValue(w.ItemId, out var it);
				var std = stdCache[w.ItemId];
				rep.Lines.Add(new ManufWoCostRow
				{
					Id = w.ID, WoNo = w.WoNo, ItemCode = it?.ItemCode ?? ("#" + w.ItemId), ItemName = it?.Name ?? "",
					CompletedAt = w.CompletedAt, Qty = w.ProducedQty, Material = w.MaterialCost, Labor = w.LaborCost, Overhead = w.OverheadCost,
					Total = w.MaterialCost + w.LaborCost + w.OverheadCost, UnitCost = w.UnitCost, StdUnitCost = Math.Round(std.m + std.l + std.o, 4)
				});
				rep.TotMaterial += w.MaterialCost; rep.TotLabor += w.LaborCost; rep.TotOverhead += w.OverheadCost;
				rep.TotProducedValue += w.MaterialCost + w.LaborCost + w.OverheadCost;
			}
			rep.WosCompleted = wos.Count;

			// work-center load from routings of the produced items × produced qty
			var routing = await _db.ManufRoutingOps.AsNoTracking().Where(o => o.CompanyID == companyId && itemIds.Contains(o.ItemId)).ToListAsync();
			if (routing.Count > 0)
			{
				var wcIds = routing.Select(o => o.WorkCenterId).Distinct().ToList();
				var wcs = await _db.ManufWorkCenters.AsNoTracking().Where(w => wcIds.Contains(w.ID)).ToDictionaryAsync(w => w.ID, w => w);
				var byItem = routing.GroupBy(o => o.ItemId).ToDictionary(g => g.Key, g => g.ToList());
				var load = new Dictionary<int, WorkCenterLoadRow>();
				foreach (var w in wos)
				{
					if (!byItem.TryGetValue(w.ItemId, out var ops)) continue;
					foreach (var op in ops)
					{
						if (!wcs.TryGetValue(op.WorkCenterId, out var wc)) continue;
						decimal mins = op.SetupMins + op.RunMinsPerUnit * w.ProducedQty;
						decimal hrs = mins / 60m;
						if (!load.TryGetValue(wc.ID, out var row)) { row = new WorkCenterLoadRow { WorkCenterName = string.IsNullOrWhiteSpace(wc.Code) ? wc.Name : $"{wc.Code} — {wc.Name}" }; load[wc.ID] = row; }
						row.Minutes += mins; row.LaborCost += hrs * wc.CostPerHour; row.OverheadCost += hrs * wc.OverheadPerHour;
					}
				}
				foreach (var r in load.Values) { r.Minutes = Math.Round(r.Minutes, 2); r.LaborCost = Math.Round(r.LaborCost, 4); r.OverheadCost = Math.Round(r.OverheadCost, 4); }
				rep.Load = load.Values.OrderByDescending(r => r.Minutes).ToList();
			}
			return rep;
		}
	}
}
