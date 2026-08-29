using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
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
		Task<(bool ok, string? error, int laborId)> AddLaborAsync(int companyId, int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal? ratePerHourOverride, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, DateTime date, string? userId);
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
		Task<(bool ok, string? error, int id)> CreatePlanAsync(int companyId, string name, DateTime? planDate, string? userId);
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
		public ManufService(CrossDbContext db, IStockService stock, IEmployeeCostService empCost) { _db = db; _stock = stock; _empCost = empCost; }

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
			if (itemId <= 0) return (false, "اختر الصنف المُصنَّع", 0);
			if (qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر", 0);
			if (warehouseId <= 0) return (false, "اختر المخزن", 0);
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
			if (prod == null) return (false, "الصنف المُصنَّع غير موجود", 0);

			var warehouseOwned = await _db.Warehouses.AsNoTracking()
				.AnyAsync(w => w.ID == warehouseId && w.CompanyID == companyId);
			if (!warehouseOwned) return (false, "المخزن غير موجود", 0);

			// The recipe is now company-scoped, so a foreign BOM is not merely rejected later - it is never read.
			var bom = await _db.ItemComponents.AsNoTracking()
				.Where(c => c.ParentItemId == itemId && c.CompanyID == companyId)
				.OrderBy(c => c.SortOrder).ToListAsync();
			if (bom.Count == 0) return (false, "هذا الصنف ليس له قائمة مواد (BOM) — أضف مكوّناته أولًا", 0);
			// Every component the plan will reference must be ours too. A BOM row inside our own recipe can
			// still name a foreign component id, and those ids are what get stamped onto ManufWorkOrderComponents
			// and later consumed as stock - so this is checked before a single row is written, not at consumption.
			var componentIds = bom.Select(b => b.ComponentItemId).Distinct().ToList();
			var ownedComponentCount = await _db.Items.AsNoTracking()
				.CountAsync(i => componentIds.Contains(i.ID) && i.CompanyID == companyId);
			if (ownedComponentCount != componentIds.Count)
				return (false, "قائمة المواد تحتوي على مكوّن لا ينتمي لهذه الشركة", 0);

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
			_db.ManufWorkOrders.Add(wo); await _db.SaveChangesAsync();
			wo.WoNo = $"WO-{wo.ID:D5}";
			foreach (var b in bom)
				_db.ManufWorkOrderComponents.Add(new ManufWorkOrderComponent { CompanyID = companyId, WorkOrderId = wo.ID, ItemId = b.ComponentItemId, PlannedQty = Math.Round(b.Quantity * qty * (1 + b.ScrapPct / 100m), 4), UoMId = b.UoMId });
			await _db.SaveChangesAsync();
			return (true, null, wo.ID);
		}

		public async Task<(bool ok, string? error)> SaveHeaderAsync(int companyId, int id, decimal qty, DateTime? start, DateTime? end, decimal labor, decimal overhead, string? notes)
		{
			var wo = await _db.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == id);
			if (wo == null) return (false, "غير موجود");
			if (wo.Status != "Draft") return (false, "لا يمكن التعديل بعد الإصدار");
			if (qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر");
			var ratio = wo.Qty > 0 ? qty / wo.Qty : 1m;
			wo.Qty = qty; wo.PlannedStart = start; wo.PlannedEnd = end; wo.LaborCost = labor < 0 ? 0 : labor; wo.OverheadCost = overhead < 0 ? 0 : overhead; wo.Notes = notes;
			// rescale component planned quantities to the new qty
			if (ratio != 1m)
			{
				var comps = await _db.ManufWorkOrderComponents.Where(c => c.CompanyID == companyId && c.WorkOrderId == id).ToListAsync();
				foreach (var c in comps) c.PlannedQty = Math.Round(c.PlannedQty * ratio, 4);
			}
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SetStatusAsync(int companyId, int id, string status)
		{
			var wo = await _db.ManufWorkOrders.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == id);
			if (wo == null) return (false, "غير موجود");
			if (wo.Status == "Completed") return (false, "أمر التشغيل مكتمل");
			if (status == "Released" && wo.Status != "Draft") return (false, "الحالة غير صالحة");
			if (status != "Released" && status != "Cancelled") return (false, "حالة غير مدعومة");
			wo.Status = status; await _db.SaveChangesAsync();
			return (true, null);
		}

		// staged lifecycle (route to StockService — the sole stock + GL writer)
		public Task<(bool ok, string? error)> ReleaseAsync(int companyId, int id, DateTime date, string? userId)
			=> _stock.ReleaseWorkOrderAsync(companyId, id, date, userId);
		public Task<(bool ok, string? error)> CancelAsync(int companyId, int id, DateTime date, string? userId)
			=> _stock.CancelWorkOrderAsync(companyId, id, date, userId);

		// بند3: labor lines
		public Task<List<ManufWorkOrderLabor>> GetLaborAsync(int companyId, int workOrderId) =>
			_db.ManufWorkOrderLabor.AsNoTracking().Where(l => l.CompanyID == companyId && l.WorkOrderId == workOrderId).OrderBy(l => l.ID).ToListAsync();

		// resolves the employee hour-rate (explicit field → salary fallback → BLOCK) then posts via StockService
		public async Task<(bool ok, string? error, int laborId)> AddLaborAsync(int companyId, int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal? ratePerHourOverride, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, DateTime date, string? userId)
		{
			decimal rate = ratePerHourOverride ?? 0m;
			if (sourceType == "Employee")
			{
				if (employeeId == null || employeeId <= 0) return (false, "اختر الموظف", 0);
				if (rate <= 0)
				{
					// TM-4: single source of truth for the hourly cost (extracted to IEmployeeCostService; no duplication)
					rate = await _empCost.HourlyCostAsync(companyId, employeeId.Value);
					if (rate <= 0) return (false, "لا يوجد «سعر ساعة تصنيع» للموظف ولا راتب قابل للاشتقاق — حدّد سعر الساعة على الموظف، أو اربط لائحة راتب واضبط الساعات المعيارية في إعدادات الرواتب.", 0);
				}
			}
			else if (rate <= 0) return (false, "أدخل سعر الساعة", 0);   // External / Applied
			// currency/exchangeRate matter for External foreign-currency lines only; StockService converts to functional
			return await _stock.AddWorkOrderLaborAsync(companyId, workOrderId, sourceType, employeeId, workerName, hours, rate, whtCodeId, externalCreditAccountId, currencyId, exchangeRate, date, userId);
		}

		public Task<(bool ok, string? error)> RemoveLaborAsync(int companyId, int laborId, DateTime date, string? userId)
			=> _stock.RemoveWorkOrderLaborAsync(companyId, laborId, date, userId);

		public Task<(bool ok, string? error, decimal unitCost)> CompleteAsync(int companyId, int id, DateTime date, string? userId)
			=> _stock.CompleteWorkOrderAsync(companyId, id, date, userId);

		public async Task<(bool ok, string? error, decimal produced)> ProducePartialAsync(int companyId, int id, decimal qty, bool finalize, DateTime date, string? userId)
		{
			var wo = await _db.ManufWorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == id);
			if (wo == null) return (false, "أمر التشغيل غير موجود", 0);
			var (m, l, o) = await ComputeStandardUnitCostAsync(companyId, wo.ItemId);
			return await _stock.ProducePartialAsync(companyId, id, qty, m + l + o, finalize, date, userId);
		}

		// ---- 4-2: work centers + routing ----
		public Task<List<ManufWorkCenter>> GetWorkCentersAsync(int companyId) =>
			_db.ManufWorkCenters.AsNoTracking().Where(w => w.CompanyID == companyId).OrderBy(w => w.ID).ToListAsync();

		public async Task<List<(int id, string name)>> WorkCentersForPickAsync(int companyId)
		{
			var rows = await _db.ManufWorkCenters.AsNoTracking().Where(w => w.CompanyID == companyId && w.IsActive).OrderBy(w => w.Name).Select(w => new { w.ID, w.Code, w.Name }).ToListAsync();
			return rows.Select(r => (r.ID, string.IsNullOrWhiteSpace(r.Code) ? r.Name : $"{r.Code} — {r.Name}")).ToList();
		}

		public async Task<(bool ok, string? error)> SaveWorkCenterAsync(int companyId, ManufWorkCenter dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم مركز العمل مطلوب");
			ManufWorkCenter e;
			if (dto.ID > 0) { e = await _db.ManufWorkCenters.FirstOrDefaultAsync(w => w.CompanyID == companyId && w.ID == dto.ID) ?? throw new InvalidOperationException("غير موجود"); }
			else { e = new ManufWorkCenter { CompanyID = companyId, CreatedAt = DateTime.UtcNow }; _db.ManufWorkCenters.Add(e); }
			e.Code = dto.Code; e.Name = dto.Name.Trim(); e.CostPerHour = dto.CostPerHour < 0 ? 0 : dto.CostPerHour; e.OverheadPerHour = dto.OverheadPerHour < 0 ? 0 : dto.OverheadPerHour; e.IsActive = dto.IsActive;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public Task<List<ManufRoutingOp>> GetRoutingAsync(int companyId, int itemId) =>
			_db.ManufRoutingOps.AsNoTracking().Where(o => o.CompanyID == companyId && o.ItemId == itemId).OrderBy(o => o.Seq).ToListAsync();

		public async Task<(bool ok, string? error)> SaveRoutingOpAsync(int companyId, ManufRoutingOp dto)
		{
			if (dto.ItemId <= 0) return (false, "اختر الصنف");
			if (dto.WorkCenterId <= 0) return (false, "اختر مركز العمل");
			ManufRoutingOp e;
			if (dto.ID > 0) { e = await _db.ManufRoutingOps.FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == dto.ID) ?? throw new InvalidOperationException("غير موجود"); }
			else { e = new ManufRoutingOp { CompanyID = companyId, ItemId = dto.ItemId, CreatedAt = DateTime.UtcNow }; _db.ManufRoutingOps.Add(e); }
			e.Seq = dto.Seq <= 0 ? 1 : dto.Seq; e.WorkCenterId = dto.WorkCenterId; e.OperationName = dto.OperationName;
			e.SetupMins = dto.SetupMins < 0 ? 0 : dto.SetupMins; e.RunMinsPerUnit = dto.RunMinsPerUnit < 0 ? 0 : dto.RunMinsPerUnit;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteRoutingOpAsync(int companyId, int id)
		{
			var e = await _db.ManufRoutingOps.FirstOrDefaultAsync(o => o.CompanyID == companyId && o.ID == id);
			if (e == null) return (false, "غير موجود");
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

		public async Task<(bool ok, string? error, int id)> CreatePlanAsync(int companyId, string name, DateTime? planDate, string? userId)
		{
			if (string.IsNullOrWhiteSpace(name)) return (false, "اسم الخطة مطلوب", 0);
			var p = new ManufPlan { CompanyID = companyId, Name = name.Trim(), PlanDate = planDate ?? DateTime.UtcNow, Status = "Draft", CreatedBy = userId, CreatedAt = DateTime.UtcNow };
			_db.ManufPlans.Add(p); await _db.SaveChangesAsync();
			return (true, null, p.ID);
		}

		public async Task<(bool ok, string? error)> AddDemandAsync(int companyId, int planId, int itemId, decimal qty, DateTime? dueDate)
		{
			var plan = await _db.ManufPlans.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == planId);
			if (plan == null) return (false, "الخطة غير موجودة");
			if (qty <= 0) return (false, "الكمية يجب أن تكون أكبر من صفر");
			var hasBom = await _db.ItemComponents.AnyAsync(c => c.ParentItemId == itemId);
			if (!hasBom) return (false, "اختر صنفًا مُصنَّعًا له قائمة مواد (BOM)");
			_db.ManufPlanDemands.Add(new ManufPlanDemand { CompanyID = companyId, PlanId = planId, ItemId = itemId, Qty = qty, DueDate = dueDate, CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RemoveDemandAsync(int companyId, int demandId)
		{
			var d = await _db.ManufPlanDemands.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == demandId);
			if (d == null) return (false, "غير موجود");
			_db.ManufPlanDemands.Remove(d); await _db.SaveChangesAsync(); return (true, null);
		}

		public async Task<(bool ok, string? error)> DeletePlanAsync(int companyId, int id)
		{
			var p = await _db.ManufPlans.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.ID == id);
			if (p == null) return (false, "غير موجود");
			_db.ManufPlanDemands.RemoveRange(_db.ManufPlanDemands.Where(d => d.CompanyID == companyId && d.PlanId == id));
			_db.ManufPlans.Remove(p); await _db.SaveChangesAsync(); return (true, null);
		}

		// Multi-level BOM explosion with on-hand netting. Available stock is consumed once (top-down),
		// and only the NET shortage of a make-item is exploded into its components.
		public async Task<List<MrpRow>> RunMrpAsync(int companyId, int planId)
		{
			var demands = await _db.ManufPlanDemands.AsNoTracking().Where(d => d.CompanyID == companyId && d.PlanId == planId).ToListAsync();
			if (demands.Count == 0) return new();

			// preload BOMs for the whole company (component graph) and on-hand per item
			var allBoms = await _db.ItemComponents.AsNoTracking().Where(c => c.CompanyID == companyId)
				.Select(c => new { c.ParentItemId, c.ComponentItemId, c.Quantity, c.ScrapPct }).ToListAsync();
			var bomByParent = allBoms.GroupBy(c => c.ParentItemId).ToDictionary(g => g.Key, g => g.ToList());
			var onHand = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId)
				.GroupBy(b => b.ItemId).Select(g => new { ItemId = g.Key, Qty = g.Sum(x => x.QtyOnHand), Val = g.Sum(x => x.TotalValue) })
				.ToDictionaryAsync(x => x.ItemId, x => x);
			var available = onHand.ToDictionary(k => k.Key, v => v.Value.Qty);

			var rows = new Dictionary<int, MrpRow>();
			var level = new Dictionary<int, int>();
			void Require(int itemId, decimal qty, int depth)
			{
				if (depth > 30 || qty <= 0) return;
				if (!rows.TryGetValue(itemId, out var r)) { r = new MrpRow { ItemId = itemId, IsMake = bomByParent.ContainsKey(itemId) }; rows[itemId] = r; level[itemId] = depth; }
				if (depth < level[itemId]) level[itemId] = depth;
				r.Gross += qty;
				decimal avail = available.TryGetValue(itemId, out var a) ? a : 0m;
				decimal use = Math.Min(Math.Max(avail, 0m), qty);
				available[itemId] = avail - use;
				decimal net = qty - use; r.Net += net;
				if (net > 0 && bomByParent.TryGetValue(itemId, out var comps))
					foreach (var c in comps) Require(c.ComponentItemId, c.Quantity * net * (1 + c.ScrapPct / 100m), depth + 1);
			}
			foreach (var d in demands) Require(d.ItemId, d.Qty, 0);

			// enrich with item code/name/unit-cost
			var ids = rows.Keys.ToList();
			var items = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i);
			foreach (var r in rows.Values)
			{
				items.TryGetValue(r.ItemId, out var it);
				r.ItemCode = it?.ItemCode ?? ("#" + r.ItemId); r.ItemName = it?.Name ?? "";
				r.OnHand = onHand.TryGetValue(r.ItemId, out var oh) ? oh.Qty : 0m;
				r.UnitCost = (onHand.TryGetValue(r.ItemId, out var oh2) && oh2.Qty != 0) ? Math.Round(oh2.Val / oh2.Qty, 4) : 0m;
				r.Level = level[r.ItemId];
			}
			// make-items first, then by level, then code
			return rows.Values.OrderByDescending(r => r.IsMake).ThenBy(r => r.Level).ThenBy(r => r.ItemCode).ToList();
		}

		public async Task<(bool ok, string? error, int created)> GeneratePlanWorkOrdersAsync(int companyId, int planId, int warehouseId, string? userId)
		{
			var plan = await _db.ManufPlans.FirstOrDefaultAsync(p => p.CompanyID == companyId && p.ID == planId);
			if (plan == null) return (false, "الخطة غير موجودة", 0);
			var rows = await RunMrpAsync(companyId, planId);
			var makeShortages = rows.Where(r => r.IsMake && r.Net > 0).ToList();
			if (makeShortages.Count == 0) return (false, "لا توجد نواقص تصنيع تتطلب أوامر تشغيل", 0);
			int created = 0;
			foreach (var r in makeShortages)
			{
				var (ok, _, _) = await CreateAsync(companyId, r.ItemId, r.Net, warehouseId, null, null, 0, 0, $"من خطة: {plan.Name}", userId);
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
			var comps = await _db.ItemComponents.AsNoTracking().Where(c => c.CompanyID == companyId && c.ParentItemId == itemId)
				.Select(c => new { c.ComponentItemId, c.Quantity, c.ScrapPct }).ToListAsync();
			if (comps.Count > 0)
			{
				var ids = comps.Select(c => c.ComponentItemId).Distinct().ToList();
				var bals = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && ids.Contains(b.ItemId))
					.GroupBy(b => b.ItemId).Select(g => new { ItemId = g.Key, Qty = g.Sum(x => x.QtyOnHand), Val = g.Sum(x => x.TotalValue) })
					.ToDictionaryAsync(x => x.ItemId, x => x);
				foreach (var c in comps)
				{
					decimal uc = (bals.TryGetValue(c.ComponentItemId, out var b) && b.Qty != 0) ? b.Val / b.Qty : 0m;
					material += c.Quantity * (1 + c.ScrapPct / 100m) * uc;
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
