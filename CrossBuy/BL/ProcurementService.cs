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
		public ProcurementService(CrossDbContext context, IStockService stock, IPayableService payables, IFixedAssetService fixedAssets, IThreeWayMatchService match, INotificationService notify, ICurrencyService currency, ICurrencyRounding rounding) { _context = context; _stock = stock; _payables = payables; _fixedAssets = fixedAssets; _match = match; _notify = notify; _currency = currency; _rounding = rounding; }

		private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

		public async Task<List<PurchaseOrder>> GetPurchaseOrdersAsync(int companyId) =>
			await _context.PurchaseOrders.AsNoTracking().Where(p => p.CompanyID == companyId).OrderByDescending(p => p.ID).ToListAsync();

		public async Task<PurchaseOrder?> GetPurchaseOrderAsync(int companyId, int id) =>
			await _context.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.ID == id && p.CompanyID == companyId);

		public async Task<(bool ok, string? error, PurchaseOrder? po)> CreatePurchaseOrderAsync(int companyId, int vendorId, int? warehouseId, DateTime date, DateTime? expected, string? notes, List<PoLineInput> lines, string? userId, int? projectId = null)
		{
			if (vendorId <= 0) return (false, "المورد مطلوب", null);
			if (lines == null || lines.Count == 0) return (false, "أمر الشراء يجب أن يحتوي على بند واحد على الأقل", null);

			// HM-2 Batch 5: PO carries no document currency here → round to the FUNCTIONAL dp via the central helper (EGP no-op; no static R).
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal R(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			var po = new PurchaseOrder { CompanyID = companyId, VendorId = vendorId, WarehouseId = warehouseId, OrderDate = date.Date, ExpectedDate = expected, Status = "Approved", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow, ProjectId = projectId };
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
			if (warehouseId <= 0) return (false, "المخزن مطلوب", null);
			if (lines == null || lines.Count == 0) return (false, "إذن الاستلام يجب أن يحتوي على بند واحد على الأقل", null);

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

			var gr = new GoodsReceipt { CompanyID = companyId, VendorId = vendorId, WarehouseId = warehouseId, PurchaseOrderId = poId, ReceiptDate = date.Date, Status = "Posted", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = R4(rate) };
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
					if (grni == null) { _context.GoodsReceiptLines.RemoveRange(gr.Lines); _context.GoodsReceipts.Remove(gr); await _context.SaveChangesAsync(); return (false, "حساب فواتير لم ترد (GRNI) غير مُهيّأ لرسملة الأصل", null); }
					var cc = await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.ID).Select(c => (int?)c.ID).FirstOrDefaultAsync();
					var aUnit = BaseUnit(l.UnitCost);
					decimal aCost = Rf(l.Qty * aUnit);
					var (aok, aerr, asset) = await _fixedAssets.CreateAssetAsync(companyId, new FixedAssetInput
					{ Name = hdr.Name, NameEn = hdr.NameEn, Cost = aCost, SalvageValue = 0, UsefulLifeMonths = 60, AcquisitionDate = date, FundingAccountId = grni.Value, CostCenterId = cc }, null);
					if (!aok) { _context.GoodsReceiptLines.RemoveRange(gr.Lines); _context.GoodsReceipts.Remove(gr); await _context.SaveChangesAsync(); return (false, $"تعذّرت رسملة الأصل: {aerr}", null); }
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
					SourceType = "Receipt", SourceId = gr.ID, PostToGl = true, Notes = $"إذن استلام {gr.ReceiptNo}"
				}, userId);
				if (!sok)
				{
					// roll back: remove what we created so a failed receipt leaves nothing
					_context.GoodsReceiptLines.RemoveRange(gr.Lines);
					_context.GoodsReceipts.Remove(gr);
					await _context.SaveChangesAsync();
					return (false, $"تعذّر استلام صنف: {serr}", null);
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
			try
			{
				await _notify.NotifyRoleAsync(companyId, "acc", new[] { "Accountant", "ChiefAccountant" },
					"تم استلام بضاعة من مورد", "Goods received",
					$"إذن الاستلام {gr.ReceiptNo} بقيمة {gr.TotalCost:N2} جاهز لمطابقة فاتورة المورد", $"Goods receipt {gr.ReceiptNo} ({gr.TotalCost:N2}) is ready for vendor-invoice matching",
					"goods_receipt", gr.ID);
			}
			catch { /* notifications never block the business flow */ }
			return (true, null, gr);
		}

		// turns a PO into a purchase invoice. If goods were already received → clears GRNI (Dr GRNI / Cr AP, no stock).
		// If not received yet → the invoice also receives the stock (Dr Inventory / Cr AP), i.e. a direct 2-way bill.
		public async Task<(bool ok, string? error, int? invoiceId)> ConvertToInvoiceAsync(int companyId, int poId, string? userId)
		{
			var po = await _context.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.ID == poId && p.CompanyID == companyId);
			if (po == null) return (false, "أمر الشراء غير موجود", null);
			if (po.Status == "Closed") return (false, "أمر الشراء محوّل لفاتورة بالفعل", null);
			if (po.Status == "Cancelled") return (false, "أمر الشراء ملغي", null);

			var receipts = await _context.GoodsReceipts.Where(g => g.CompanyID == companyId && g.PurchaseOrderId == poId && g.Status == "Posted").ToListAsync();
			bool received = receipts.Any();

			// P3-6 three-way match: when goods were received, billed qty/price must match the receipt within tolerance
			if (received)
			{
				var match = await _match.CheckPoAsync(companyId, poId);
				if (!match.Ok)
					return (false, "فشل المطابقة الثلاثية (الكمية/السعر خارج السماحية): " + string.Join("؛ ", match.Failures), null);
			}

			var lines = new List<PurchaseLineInput>();
			foreach (var l in po.Lines.OrderBy(x => x.LineNo))
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
			}
			if (lines.Count == 0) return (false, "لا توجد بنود قابلة للفوترة", null);

			var (ok, err, inv) = await _payables.CreatePurchaseInvoiceAsync(companyId, po.VendorId, DateTime.Today, lines, $"من أمر شراء {po.OrderNo}", null, po.CurrencyId, po.ExchangeRate, po.ProjectId);
			if (!ok) return (false, err, null);

			po.Status = "Closed";
			foreach (var g in receipts) g.InvoiceId = inv!.ID;
			await _context.SaveChangesAsync();
			return (true, null, inv!.ID);
		}
	}
}
