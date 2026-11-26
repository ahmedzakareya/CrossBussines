using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class PoLineInput
	{
		public int? ItemId { get; set; }
		public string? ItemDescription { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
	}

	public class ReceiptLineInput
	{
		public int ItemId { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public decimal UnitCost { get; set; }
		public string? BatchNo { get; set; }
		public DateTime? Expiry { get; set; }
		public string? SerialNo { get; set; }
		public int? PurchaseOrderLineId { get; set; }
		public int? BinLocationId { get; set; }   // section/rack the goods are placed on (rack if chosen, else section)
	}

	public interface IProcurementService
	{
		Task<List<PurchaseOrder>> GetPurchaseOrdersAsync(int companyId);
		Task<PurchaseOrder?> GetPurchaseOrderAsync(int companyId, int id);
		Task<(bool ok, string? error, PurchaseOrder? po)> CreatePurchaseOrderAsync(int companyId, int vendorId, int? warehouseId, DateTime date, DateTime? expected, string? notes, List<PoLineInput> lines, string? userId, int? projectId = null);
		Task<List<GoodsReceipt>> GetReceiptsAsync(int companyId);
		Task<GoodsReceipt?> GetReceiptAsync(int companyId, int id);
		Task<(bool ok, string? error, GoodsReceipt? gr)> CreateReceiptAsync(int companyId, int? vendorId, int warehouseId, int? poId, DateTime date, string? notes, List<ReceiptLineInput> lines, string? userId, int? currencyId = null, decimal? exchangeRate = null);
		Task<(bool ok, string? error, int? invoiceId)> ConvertToInvoiceAsync(int companyId, int poId, string? userId);
	}

	/// Procurement: purchase orders + goods receipts. Receipts post stock (Dr Inventory / Cr GRNI) via StockService.
	public class ProcurementService : IProcurementService
	{
		private readonly CrossDbContext _context;
		private readonly IStockService _stock;
		private readonly IPayableService _payables;
		private readonly IFixedAssetService _fixedAssets;
		private readonly IThreeWayMatchService _match;
		private readonly INotificationService _notify;
		private readonly ICurrencyService _currency;
		private readonly ICurrencyRounding _rounding;
		private readonly Platform.IBusinessEventService _events;
		private readonly Construction.IConstructionRebasedReceiptService? _rebasedReceipts;
		public ProcurementService(CrossDbContext context, IStockService stock, IPayableService payables, IFixedAssetService fixedAssets, IThreeWayMatchService match, INotificationService notify, ICurrencyService currency, ICurrencyRounding rounding, Platform.IBusinessEventService events, Construction.IConstructionRebasedReceiptService? rebasedReceipts = null) { _rebasedReceipts = rebasedReceipts; _context = context; _stock = stock; _payables = payables; _fixedAssets = fixedAssets; _match = match; _notify = notify; _currency = currency; _rounding = rounding; _events = events; }

		private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

		public async Task<List<PurchaseOrder>> GetPurchaseOrdersAsync(int companyId) =>
			await _context.PurchaseOrders.AsNoTracking().Where(p => p.CompanyID == companyId).OrderByDescending(p => p.ID).ToListAsync();

		public async Task<PurchaseOrder?> GetPurchaseOrderAsync(int companyId, int id) =>
			await _context.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.ID == id && p.CompanyID == companyId);

		public async Task<(bool ok, string? error, PurchaseOrder? po)> CreatePurchaseOrderAsync(int companyId, int vendorId, int? warehouseId, DateTime date, DateTime? expected, string? notes, List<PoLineInput> lines, string? userId, int? projectId = null)
		{
			if (companyId <= 0 || _context.CompanyScope.FilterCompanyId != companyId) return (false, "Unresolved or mismatched company", null);
			if (vendorId <= 0) return (false, "Supplier is required", null);
			if (lines == null || lines.Count == 0) return (false, "The purchase order must contain at least one line", null);
			if (!await _context.Vendors.AsNoTracking().AnyAsync(x => x.ID == vendorId && x.CompanyID == companyId)) return (false, "Supplier not found", null);
			if (warehouseId.HasValue && !await _context.Warehouses.AsNoTracking().AnyAsync(x => x.ID == warehouseId && x.CompanyID == companyId)) return (false, "Warehouse not found", null);
			if (projectId.HasValue && !await _context.Projects.AsNoTracking().AnyAsync(x => x.ID == projectId && x.CompanyID == companyId)) return (false, "Project not found", null);

			// HM-2 Batch 5: PO carries no document currency here → round to the FUNCTIONAL dp via the central helper (EGP no-op; no static R).
			var documentCurrency = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			int __fdp = await _rounding.DecimalsAsync(companyId, documentCurrency);
			decimal R(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			var po = new PurchaseOrder { CompanyID = companyId, VendorId = vendorId, WarehouseId = warehouseId, CurrencyId = documentCurrency, OrderDate = date.Date, ExpectedDate = expected, Status = "Approved", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow, ProjectId = projectId };
			int ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lt = R(l.Qty * l.UnitPrice - l.DiscountAmount);
				sub += lt; tax += R(lt * l.TaxRate / 100m);
				po.Lines.Add(new PurchaseOrderLine { LineNo = ln++, ItemId = l.ItemId, ItemDescription = l.ItemDescription, Qty = l.Qty, UoMId = l.UoMId, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, LineTotal = lt, ReceivedQty = 0 });
			}
			po.SubTotal = R(sub); po.TaxTotal = R(tax); po.GrandTotal = R(sub + tax);
			_context.PurchaseOrders.Add(po);
			await _context.SaveChangesAsync();
			po.OrderNo = $"PO-{date:yyyy}-{po.ID:D5}";
			await _context.SaveChangesAsync();
			return (true, null, po);
		}

		public async Task<List<GoodsReceipt>> GetReceiptsAsync(int companyId) =>
			await _context.GoodsReceipts.AsNoTracking().Where(g => g.CompanyID == companyId).OrderByDescending(g => g.ID).ToListAsync();

		public async Task<GoodsReceipt?> GetReceiptAsync(int companyId, int id) =>
			await _context.GoodsReceipts.Include(g => g.Lines).FirstOrDefaultAsync(g => g.ID == id && g.CompanyID == companyId);

		public async Task<(bool ok, string? error, GoodsReceipt? gr)> CreateReceiptAsync(int companyId, int? vendorId, int warehouseId, int? poId, DateTime date, string? notes, List<ReceiptLineInput> lines, string? userId, int? currencyId = null, decimal? exchangeRate = null)
		{
			if (companyId <= 0 || _context.CompanyScope.FilterCompanyId != companyId) return (false, "Unresolved or mismatched company", null);
			if (_context.ChangeTracker.HasChanges()) return (false, "Save pending changes before receiving goods", null);
			var outer = _context.Database.CurrentTransaction;
			if (outer != null && !outer.SupportsSavepoints) return (false, "The current transaction cannot safely receive goods", null);
			var original = _context.ChangeTracker.Entries().Select(x => (x.Entity, Values: x.CurrentValues.Clone())).ToList();
			// Read the immutable hint before the owned transaction, then lock project before PO.
			var constructionProject = poId.HasValue ? await Construction.ConstructionRebasedReceiptService.ProjectAsync(_context, companyId, poId.Value) : null;
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context, System.Data.IsolationLevel.Serializable);
			var savepoint = "GRC_" + Guid.NewGuid().ToString("N")[..24];
			if (outer != null) await outer.CreateSavepointAsync(savepoint);
			async Task Undo()
			{
				if (outer == null) await tx.RollbackAsync();
				else { await outer.RollbackToSavepointAsync(savepoint); await outer.ReleaseSavepointAsync(savepoint); }
				RestoreConversionTracking(original);
			}
			try
			{
				int? rebaseProject = null;
				if (constructionProject.HasValue)
				{
					if (!await Construction.ConstructionCommitmentReservationService.AcquireProjectScopeLock(_context, companyId, constructionProject.Value, CancellationToken.None))
					{ await Undo(); return (false, "The project is busy; refresh and retry receiving", null); }
					if (await Construction.ConstructionRebasedReceiptService.RequiresAsync(_context, companyId, poId!.Value)) rebaseProject = constructionProject;
				}
				int? receiptEmployee = null;
				if (rebaseProject.HasValue && _rebasedReceipts == null)
				{ await Undo(); return (false, "Rebased purchase receiving is not configured", null); }
				if (rebaseProject.HasValue)
				{
					var check = await _rebasedReceipts!.EnterAsync(companyId, rebaseProject.Value, poId!.Value, warehouseId);
					if (!check.Ok) { await Undo(); return (false, check.Code == "WarehouseExceptionContinuationUnavailable" ? check.Code : "The rebased purchase could not be verified for receiving. Refresh the order or ask an authorized reviewer.", null); }
					receiptEmployee = check.EmployeeId;
				}
				var result = await CreateReceiptCoreAsync(companyId, vendorId, warehouseId, poId, date, notes, lines, userId, currencyId, exchangeRate, constructionProject);
				if (result.ok && rebaseProject.HasValue)
				{
					var check = await _rebasedReceipts!.CompleteAsync(companyId, rebaseProject.Value, poId!.Value, warehouseId, receiptEmployee!.Value);
					if (!check.Ok) result = (false, check.Code == "WarehouseExceptionContinuationUnavailable" ? check.Code : "The receipt no longer matches the approved purchase or your current access.", null);
				}
				if (!result.ok) await Undo();
				else if (outer == null) await tx.CommitAsync();
				else await outer.ReleaseSavepointAsync(savepoint);
				return result;
			}
			catch (Exception originalFailure)
			{
				try { await Undo(); }
				catch (Exception rollbackFailure) { throw new AggregateException("Goods receipt and its rollback both failed", originalFailure, rollbackFailure); }
				throw;
			}
		}

		private async Task<(bool ok, string? error, GoodsReceipt? gr)> CreateReceiptCoreAsync(int companyId, int? vendorId, int warehouseId, int? poId, DateTime date, string? notes, List<ReceiptLineInput> lines, string? userId, int? currencyId, decimal? exchangeRate, int? expectedConstructionProject)
		{
			if (warehouseId <= 0) return (false, "Warehouse is required", null);
			if (lines == null || lines.Count == 0) return (false, "The goods receipt must contain at least one line", null);
			if (lines.Any(l => l == null || l.ItemId <= 0 || l.Qty <= 0 || l.UnitCost < 0)) return (false, "Every receipt line requires an item, a positive quantity and a non-negative cost", null);
			if (!await _context.Warehouses.AsNoTracking().AnyAsync(w => w.ID == warehouseId && w.CompanyID == companyId)) return (false, "Warehouse not found", null);
			if (!poId.HasValue && lines.Any(l => l.PurchaseOrderLineId.HasValue)) return (false, "A purchase-order line requires its purchase order", null);
			if (poId.HasValue)
			{
				// Same first lock as conversion: whichever operation wins decides from committed PO truth.
				var orders = _context.Database.IsSqlServer()
					? _context.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM dbo.PurchaseOrders WITH (UPDLOCK,HOLDLOCK) WHERE ID={poId.Value} AND CompanyID={companyId}")
					: _context.PurchaseOrders.Where(p => p.ID == poId.Value && p.CompanyID == companyId);
				var source = await orders.FirstOrDefaultAsync();
				if (source == null) return (false, "Purchase order not found", null);
				await _context.Entry(source).ReloadAsync();
				var currentRebaseProject = await Construction.ConstructionRebasedReceiptService.ProjectAsync(_context, companyId, source.ID);
				if (currentRebaseProject != expectedConstructionProject) return (false, "The purchase baseline changed; refresh the order before receiving", null);
				if (source.Status == "Closed" || source.Status == "Cancelled" || await _context.Set<PurchaseOrderInvoiceLink>().AnyAsync(x => x.CompanyID == companyId && x.PurchaseOrderId == source.ID))
					return (false, "The purchase order is closed, cancelled or already invoiced", null);
				if (source.Status is not ("Approved" or "Received")) return (false, "The purchase order must be approved before receiving goods", null);
				if (vendorId.HasValue && vendorId.Value != source.VendorId) return (false, "The supplier does not match the purchase order", null);
				vendorId ??= source.VendorId;
				var orderedLines = await _context.PurchaseOrderLines.Where(l => l.PurchaseOrderId == source.ID).ToListAsync();
				foreach (var ordered in orderedLines) await _context.Entry(ordered).ReloadAsync();
				if (lines.Any(l => !l.PurchaseOrderLineId.HasValue || !orderedLines.Any(p => p.ID == l.PurchaseOrderLineId.Value && p.ItemId == l.ItemId)))
					return (false, "A receipt line does not match the purchase order and item", null);
			}
			if (vendorId.HasValue && !await _context.Vendors.AsNoTracking().AnyAsync(v => v.ID == vendorId.Value && v.CompanyID == companyId)) return (false, "Supplier not found", null);
			var itemIds = lines.Select(l => l.ItemId).Distinct().ToList();
			if (await _context.Items.AsNoTracking().CountAsync(i => itemIds.Contains(i.ID) && i.CompanyID == companyId) != itemIds.Count) return (false, "An item does not belong to this company", null);

			// Multi-Currency (1-3): line UnitCost is in `cur`; stock value + GL are FUNCTIONAL currency.
			// Convert the unit cost to base AT SOURCE so StockService never sees a foreign amount.
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Buy"); rate = r; }
			decimal BaseUnit(decimal foreignUnit) => R4(foreignUnit * rate);   // FUNCTIONAL unit cost (converted at source) — inventory is valued in the functional currency
			int __fdp = await _rounding.DecimalsAsync(companyId, null);        // HM-2 (3-ج): GRN value is functional-cost ⇒ round to functional dp
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);

			var gr = new GoodsReceipt { CompanyID = companyId, VendorId = vendorId, WarehouseId = warehouseId, PurchaseOrderId = poId, ReceiptDate = date.Date, Status = "Posted", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = rate };
			_context.GoodsReceipts.Add(gr);
			await _context.SaveChangesAsync();
			gr.ReceiptNo = $"GRN-{date:yyyy}-{gr.ID:D5}";

			int ln = 1; decimal total = 0;
			foreach (var l in lines)
			{
				if (l.ItemId <= 0 || l.Qty <= 0) continue;

				// ===== Fixed-asset bridge: an Asset-type item is capitalized on receipt, not stocked for sale =====
				// Dr Fixed Asset (1201) / Cr GRNI — the purchase invoice later clears GRNI exactly like a stock receipt.
				var hdr = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == l.ItemId && i.CompanyID == companyId);
				if (hdr != null && hdr.ItemType == "Asset")
				{
					var grni = await _context.ItemCategories.AsNoTracking().Where(c => c.ID == hdr.ItemCategoryId).Select(c => c.GrniAccountId).FirstOrDefaultAsync()
							?? await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "210203").Select(a => (int?)a.ID).FirstOrDefaultAsync();
					if (grni == null) return (false, "The GRNI account is not configured for capitalising the asset", null);
					var cc = await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.ID).Select(c => (int?)c.ID).FirstOrDefaultAsync();
					var aUnit = BaseUnit(l.UnitCost);
					decimal aCost = Rf(l.Qty * aUnit);
					var (aok, aerr, asset) = await _fixedAssets.CreateAssetAsync(companyId, new FixedAssetInput
					{ Name = hdr.Name, NameEn = hdr.NameEn, Cost = aCost, SalvageValue = 0, UsefulLifeMonths = 60, AcquisitionDate = date, FundingAccountId = grni.Value, CostCenterId = cc }, null);
					if (!aok) return (false, $"Could not capitalize the asset: {aerr}", null);
					gr.Lines.Add(new GoodsReceiptLine { GoodsReceiptId = gr.ID, LineNo = ln++, ItemId = l.ItemId, Qty = l.Qty, UoMId = l.UoMId, UnitCost = aUnit, LineTotal = aCost, BatchNo = l.BatchNo, ExpiryDate = l.Expiry, SerialNo = l.SerialNo, PurchaseOrderLineId = l.PurchaseOrderLineId, StockMovementId = null });
					total += aCost;
					if (l.PurchaseOrderLineId != null) { var pol2 = await _context.PurchaseOrderLines.FirstOrDefaultAsync(x => x.ID == l.PurchaseOrderLineId); if (pol2 != null) pol2.ReceivedQty += l.Qty; }
					continue;
				}

				var baseUnit = BaseUnit(l.UnitCost);
				var (sok, serr, mv) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId, WarehouseId = warehouseId, Direction = 1, Qty = l.Qty, UoMId = l.UoMId,
					UnitCostInBase = baseUnit, BatchNo = l.BatchNo, Expiry = l.Expiry, SerialNo = l.SerialNo, BinLocationId = l.BinLocationId,
					SourceType = "Receipt", SourceId = gr.ID, PostToGl = true, Notes = $"Goods receipt {gr.ReceiptNo}"
				}, userId);
				if (!sok)
				{
					return (false, $"Could not receive an item: {serr}", null);
				}
				var lineCost = mv!.TotalCost;
				gr.Lines.Add(new GoodsReceiptLine { GoodsReceiptId = gr.ID, LineNo = ln++, ItemId = l.ItemId, Qty = l.Qty, UoMId = l.UoMId, UnitCost = baseUnit, LineTotal = lineCost, BatchNo = l.BatchNo, ExpiryDate = l.Expiry, SerialNo = l.SerialNo, PurchaseOrderLineId = l.PurchaseOrderLineId, StockMovementId = mv.ID });
				total += lineCost;

				// update PO line received qty
				if (l.PurchaseOrderLineId != null)
				{
					var pol = await _context.PurchaseOrderLines.FirstOrDefaultAsync(x => x.ID == l.PurchaseOrderLineId);
					if (pol != null) { pol.Qty = pol.Qty; pol.ReceivedQty += l.Qty; }
				}
			}
			gr.TotalCost = Rf(total);
			await _context.SaveChangesAsync();

			// update PO status if fully received
			if (poId != null)
			{
				var po = await _context.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.ID == poId);
				if (po != null)
				{
					var allRecv = po.Lines.All(x => x.ReceivedQty >= x.Qty);
					po.Status = allRecv ? "Received" : "Approved";
					await _context.SaveChangesAsync();
				}
			}
			// Durable fact/outbox only. NotificationProjection delivers after the owning transaction commits.
			await _events.RecordAsync(new CrossBuy.Models.Platform.BusinessEventRecord
			{
				EntityCode = Platform.EntityRegistry.GoodsReceipt, EntityId = gr.ID,
				EventType = Platform.GoodsReceiptEvents.Posted, PayloadVersion = 1,
				Visibility = CrossBuy.Models.Platform.BusinessEventVisibility.Internal,
				DedupKey = $"GoodsReceipt.Posted:{gr.ID}",
				Payload = new CrossBuy.Models.Platform.GoodsReceiptEventPayload { ReceiptNumber = gr.ReceiptNo, ReceiptDate = gr.ReceiptDate, TotalCostBase = gr.TotalCost, FunctionalCurrencyId = functional }
			});
			return (true, null, gr);
		}

		// turns a PO into a purchase invoice. If goods were already received → clears GRNI (Dr GRNI / Cr AP, no stock).
		// If not received yet → the invoice also receives the stock (Dr Inventory / Cr AP), i.e. a direct 2-way bill.
		public async Task<(bool ok, string? error, int? invoiceId)> ConvertToInvoiceAsync(int companyId, int poId, string? userId)
		{
			if (companyId <= 0 || _context.CompanyScope.FilterCompanyId != companyId) return (false, "Unresolved or mismatched company", null);
			if (_context.ChangeTracker.HasChanges()) return (false, "Save pending changes before converting the purchase order", null);
			var outer = _context.Database.CurrentTransaction;
			if (outer != null && !outer.SupportsSavepoints) return (false, "The current transaction cannot safely convert the purchase order", null);
			var original = _context.ChangeTracker.Entries().Select(x => (x.Entity, Values: x.CurrentValues.Clone())).ToList();
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			var savepoint = "POC_" + Guid.NewGuid().ToString("N")[..24];
			if (outer != null) await outer.CreateSavepointAsync(savepoint);
			async Task Undo()
			{
				if (outer == null) await tx.RollbackAsync();
				else { await outer.RollbackToSavepointAsync(savepoint); await outer.ReleaseSavepointAsync(savepoint); }
				RestoreConversionTracking(original);
			}
			try
			{
				var result = await ConvertToInvoiceCoreAsync(companyId, poId, userId);
				if (!result.ok) await Undo();
				else if (outer == null) await tx.CommitAsync();
				else await outer.ReleaseSavepointAsync(savepoint);
				return result;
			}
			catch (Exception originalFailure)
			{
				try { await Undo(); }
				catch (Exception rollbackFailure) { throw new AggregateException("Purchase-order conversion and its rollback both failed", originalFailure, rollbackFailure); }
				// A concurrent partial issue may win after our preflight. Translate only the
				// specific no-mix trigger after successful rollback; other database faults remain faults.
				if (IsPartialInvoiceMix(originalFailure)) return (false, PartialInvoiceConversionRefusal, null);
				throw;
			}
		}

		public const string PartialInvoiceConversionRefusal = "This purchase order has partial invoice history. Use partial invoicing for the remaining quantities.";
		private static bool IsPartialInvoiceMix(Exception failure)
		{
			for (Exception? current = failure; current != null; current = current.InnerException)
				if (current is Microsoft.Data.SqlClient.SqlException sql &&
					sql.Errors.Cast<Microsoft.Data.SqlClient.SqlError>().Any(x => x.Number == 51911)) return true;
			return false;
		}

		private void RestoreConversionTracking(List<(object Entity, Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues Values)> original)
		{
			var retained = original.Select(x => x.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
			foreach (var entry in _context.ChangeTracker.Entries().ToList())
				if (!retained.Contains(entry.Entity)) entry.State = EntityState.Detached;
			foreach (var saved in original)
			{
				var entry = _context.Entry(saved.Entity);
				if (entry.State == EntityState.Detached) _context.Attach(saved.Entity);
				entry.CurrentValues.SetValues(saved.Values);
				entry.OriginalValues.SetValues(saved.Values);
				entry.State = EntityState.Unchanged;
			}
		}

		private async Task<(bool ok, string? error, int? invoiceId)> ConvertToInvoiceCoreAsync(int companyId, int poId, string? userId)
		{
			var orders = _context.Database.IsSqlServer()
				? _context.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM dbo.PurchaseOrders WITH (UPDLOCK,HOLDLOCK) WHERE ID={poId} AND CompanyID={companyId}")
				: _context.PurchaseOrders.Where(x => x.ID == poId && x.CompanyID == companyId);
			var po = await orders.FirstOrDefaultAsync();
			if (po == null) return (false, "Purchase order not found", null);
			// An earlier read may have cached the header in this request. The locked database row wins.
			await _context.Entry(po).ReloadAsync();
			if (po.ProjectId.HasValue &&
				(await _context.Set<CrossBuy.Models.Context.Construction.ConstructionPartialInvoice>().AsNoTracking()
					.AnyAsync(x => x.CompanyID == companyId && x.PurchaseOrderId == poId) ||
				 await _context.Set<CrossBuy.Models.Context.Construction.ConstructionPartialInvoiceSlice>().AsNoTracking()
					.AnyAsync(x => x.CompanyID == companyId && x.PurchaseOrderId == poId)))
				return (false, PartialInvoiceConversionRefusal, null);
			var linked = await _context.Set<PurchaseOrderInvoiceLink>().AsNoTracking()
				.SingleOrDefaultAsync(x => x.CompanyID == companyId && x.PurchaseOrderId == poId);
			if (linked != null)
			{
				var existing = await _context.PurchaseInvoices.AsNoTracking().SingleOrDefaultAsync(x => x.ID == linked.PurchaseInvoiceId && x.CompanyID == companyId);
				if (existing == null || existing.VendorId != po.VendorId || existing.ProjectId != po.ProjectId)
					return (false, "The conversion source relation requires review", null);
				return (true, null, existing.ID); // an idempotent retry, including reversed historical invoices
			}
			if (await Construction.ConstructionRebasedReceiptService.RequiresAsync(_context, companyId, poId))
				return (false, "Invoicing this rebased purchase requires its qualified continuation workflow", null);
			if (po.Status == "Closed") return (false, "The purchase order has already been converted to an invoice", null);
			if (po.Status == "Cancelled") return (false, "The purchase order is cancelled", null);
			if (po.Status is not ("Approved" or "Received")) return (false, "The purchase order must be approved before invoicing", null);
			if (po.ProjectId.HasValue && !await _context.Projects.AsNoTracking().AnyAsync(x => x.ID == po.ProjectId && x.CompanyID == companyId))
				return (false, "Project not found", null);
			if (po.WarehouseId.HasValue && !await _context.Warehouses.AsNoTracking().AnyAsync(x => x.ID == po.WarehouseId && x.CompanyID == companyId))
				return (false, "Warehouse not found", null);

			var receiptHistory = await _context.GoodsReceipts.Where(g => g.CompanyID == companyId && g.PurchaseOrderId == poId).ToListAsync();
			foreach (var receipt in receiptHistory) await _context.Entry(receipt).ReloadAsync();
			if (receiptHistory.Any(g => g.InvoiceId.HasValue || g.Status == "Invoiced"))
				return (false, "Purchase-order receipts already have invoice history; review their source relations", null);
			var receipts = receiptHistory.Where(g => g.Status == "Posted").ToList();
			bool received = receipts.Any();
			var sourceLines = _context.Database.IsSqlServer()
				? _context.PurchaseOrderLines.FromSqlInterpolated($"SELECT * FROM dbo.PurchaseOrderLines WITH (UPDLOCK,HOLDLOCK) WHERE PurchaseOrderId={poId}")
				: _context.PurchaseOrderLines.Where(x => x.PurchaseOrderId == poId);
			var currentLines = await sourceLines.AsNoTracking().OrderBy(x => x.LineNo).ToListAsync();

			// P3-6 three-way match: when goods were received, billed qty/price must match the receipt within tolerance
			if (received)
			{
				// This endpoint bills the entire order. Matching tolerance is not permission to
				// clear unreceived project stock; partial invoicing needs its own quantity lineage.
				if (po.ProjectId.HasValue)
				{
					var receiptIds = receipts.Select(x => x.ID).ToList();
					var receivedLines = await _context.GoodsReceiptLines.AsNoTracking()
						.Where(x => receiptIds.Contains(x.GoodsReceiptId))
						.Select(x => new { x.PurchaseOrderLineId, x.ItemId, x.Qty }).ToListAsync();
					var stockLines = currentLines.Where(x => x.ItemId.HasValue && x.Qty > 0).ToList();
					if (receivedLines.Any(x => x.Qty <= 0 || !stockLines.Any(l => l.ID == x.PurchaseOrderLineId && l.ItemId == x.ItemId)) ||
						stockLines.Any(l => l.ReceivedQty != l.Qty || receivedLines.Where(x => x.PurchaseOrderLineId == l.ID).Sum(x => x.Qty) != l.Qty))
						return (false, "Full project invoicing requires exact receipt coverage for every stock line; partial invoicing is not supported by this action", null);
				}
				var match = await _match.CheckPoAsync(companyId, poId);
				if (!match.Ok)
					return (false, "Three-way match failed (quantity/price outside tolerance): " + string.Join("; ", match.Failures), null);
			}

			var lines = new List<PurchaseLineInput>();
			var sourceLineIds = new List<int>();
			foreach (var l in currentLines)
			{
				if (l.Qty <= 0) continue;
				int grni = 0;
				if (received && l.ItemId != null)
				{
					var cat = await _context.Items.AsNoTracking().Where(i => i.ID == l.ItemId)
						.Join(_context.ItemCategories, i => i.ItemCategoryId, c => c.ID, (i, c) => new { c.GrniAccountId, c.InventoryAccountId }).FirstOrDefaultAsync();
					grni = cat?.GrniAccountId ?? cat?.InventoryAccountId ?? 0;
				}
				lines.Add(new PurchaseLineInput
				{
					ItemDescription = l.ItemDescription ?? "",
					Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate,
					ExpenseAccountId = received ? grni : 0,           // received → GRNI clearing; else resolved to inventory by ItemId
					ItemId = received ? (int?)null : l.ItemId,        // received → no stock (already received); else stock-in
					WarehouseId = received ? (int?)null : po.WarehouseId,
				});
				sourceLineIds.Add(l.ID);
			}
			if (lines.Count == 0) return (false, "There are no invoiceable lines", null);

			var (ok, err, inv) = await _payables.CreatePurchaseInvoiceAsync(companyId, po.VendorId, DateTime.Today, lines, $"From purchase order {po.OrderNo}", null, po.CurrencyId, po.ExchangeRate, po.ProjectId);
			if (!ok) return (false, err, null);
			if (inv == null) return (false, "The converted invoice could not be verified", null);
			var storedInvoice = await _context.PurchaseInvoices.AsNoTracking().SingleOrDefaultAsync(x => x.ID == inv.ID && x.CompanyID == companyId);
			if (storedInvoice == null || storedInvoice.VendorId != po.VendorId || storedInvoice.ProjectId != po.ProjectId)
				return (false, "The converted invoice could not be verified", null);
			var writerReceipt = inv.CreatedNetLineage;
			if (storedInvoice.JournalEntryId is not > 0 || writerReceipt.Count != sourceLineIds.Count ||
				writerReceipt.Select(x => x.InputOrdinal).Distinct().Count() != sourceLineIds.Count ||
				writerReceipt.Any(x => x.InputOrdinal < 0 || x.InputOrdinal >= sourceLineIds.Count || x.InvoiceLineId <= 0 || x.JournalLineId <= 0) ||
				writerReceipt.Select(x => x.InvoiceLineId).Distinct().Count() != sourceLineIds.Count ||
				writerReceipt.Select(x => x.JournalLineId).Distinct().Count() != sourceLineIds.Count)
				return (false, "The converted invoice has no complete writer line provenance", null);

			po.Status = "Closed";
			foreach (var g in receipts) g.InvoiceId = storedInvoice.ID;
			_context.Add(new PurchaseOrderInvoiceLink { CompanyID = companyId, PurchaseOrderId = po.ID,
				PurchaseInvoiceId = storedInvoice.ID, CreatedAt = DateTime.UtcNow, CreatedByUserId = userId });
			await _context.SaveChangesAsync();
			foreach (var link in writerReceipt)
				_context.Add(new PurchaseOrderInvoiceLineLink
				{
					CompanyID = companyId, PurchaseOrderId = po.ID, PurchaseOrderLineId = sourceLineIds[link.InputOrdinal],
					PurchaseInvoiceId = storedInvoice.ID, PurchaseInvoiceLineId = link.InvoiceLineId,
					JournalEntryId = storedInvoice.JournalEntryId.Value, JournalEntryLineId = link.JournalLineId, CreatedAt = DateTime.UtcNow
				});
			await _context.SaveChangesAsync();
			return (true, null, storedInvoice.ID);
		}
	}
}
