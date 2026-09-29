using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class SoLineInput
	{
		public int? ItemId { get; set; }
		public string? ItemDescription { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
	}

	public class DeliveryLineInput
	{
		public int ItemId { get; set; }
		public decimal Qty { get; set; } = 1;
		public int? UoMId { get; set; }
		public string? BatchNo { get; set; }
		public string? SerialNo { get; set; }
		public int? SalesOrderLineId { get; set; }
		public int? BinLocationId { get; set; }   // source section/rack the goods are picked from
	}

	public interface ISellingService
	{
		Task<List<SalesOrder>> GetSalesOrdersAsync(int companyId);
		Task<SalesOrder?> GetSalesOrderAsync(int companyId, int id);
		Task<(bool ok, string? error, SalesOrder? so)> CreateSalesOrderAsync(int companyId, int customerId, int? warehouseId, DateTime date, DateTime? expected, string? notes, List<SoLineInput> lines, string? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null);
		Task<List<DeliveryNote>> GetDeliveriesAsync(int companyId);
		Task<DeliveryNote?> GetDeliveryAsync(int companyId, int id);
		Task<(bool ok, string? error, DeliveryNote? dn)> CreateDeliveryAsync(int companyId, int? customerId, int warehouseId, int? soId, DateTime date, string? notes, List<DeliveryLineInput> lines, string? userId);
		Task<(bool ok, string? error, int? invoiceId)> ConvertToInvoiceAsync(int companyId, int soId, string? userId);
		// quotations (P3-2)
		Task<List<Quotation>> GetQuotationsAsync(int companyId);
		Task<Quotation?> GetQuotationAsync(int companyId, int id);
		Task<(bool ok, string? error, Quotation? q)> CreateQuotationAsync(int companyId, int customerId, int? warehouseId, DateTime date, DateTime? validUntil, string? notes, List<SoLineInput> lines, string? userId, int? currencyId = null, decimal? exchangeRate = null);
		/// `customerDecision` says the CUSTOMER answered, through the portal, and it is the only
		/// thing that produces a Quotation.*ByCustomer event. Staff moving the same status on the
		/// customer's behalf is a different fact and deliberately records nothing here.
		Task<(bool ok, string? error)> SetQuotationStatusAsync(
			int companyId, int id, string status, bool customerDecision = false);
		Task<(bool ok, string? error, int? salesOrderId)> ConvertQuotationToOrderAsync(int companyId, int id, string? userId);
	}

	/// Sales: orders + delivery notes. Delivery posts stock-out + COGS (Dr COGS / Cr Inventory) via StockService.
	public class SellingService : ISellingService
	{
		private readonly CrossDbContext _context;
		private readonly IStockService _stock;
		private readonly IReceivableService _receivables;
		private readonly INotificationService _notify;
		private readonly ICurrencyService _currency;
		private readonly ICurrencyRounding _rounding;

		/// OPTIONAL so every existing construction of this service keeps working unchanged. When it is
		/// absent the customer-decision path REFUSES rather than silently transitioning without its
		/// event - a state change whose fact was never recorded is precisely what the outbox exists to
		/// prevent, and quietly dropping it would be worse than refusing.
		private readonly CrossBuy.BL.Platform.IBusinessEventService? _events;

		public SellingService(CrossDbContext context, IStockService stock, IReceivableService receivables, INotificationService notify, ICurrencyService currency, ICurrencyRounding rounding, CrossBuy.BL.Platform.IBusinessEventService? events = null) { _context = context; _stock = stock; _receivables = receivables; _notify = notify; _currency = currency; _rounding = rounding; _events = events; }

		private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

		// Multi-Currency: resolve the document currency + foreign→functional rate (Sell, for sales docs)
		private async Task<(int cur, decimal rate)> ResolveCurAsync(int companyId, int? currencyId, decimal? exchangeRate, DateTime date)
		{
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			if (cur == functional) return (cur, 1m);
			if (exchangeRate.HasValue && exchangeRate.Value > 0) return (cur, exchangeRate.Value);
			var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Sell");
			return (cur, r);
		}

		public async Task<List<SalesOrder>> GetSalesOrdersAsync(int companyId) =>
			await _context.SalesOrders.AsNoTracking().Where(s => s.CompanyID == companyId).OrderByDescending(s => s.ID).ToListAsync();

		public async Task<SalesOrder?> GetSalesOrderAsync(int companyId, int id) =>
			await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.ID == id && s.CompanyID == companyId);

		public async Task<(bool ok, string? error, SalesOrder? so)> CreateSalesOrderAsync(int companyId, int customerId, int? warehouseId, DateTime date, DateTime? expected, string? notes, List<SoLineInput> lines, string? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
		{
			if (customerId <= 0) return (false, "Customer is required", null);
			if (lines == null || lines.Count == 0) return (false, "The sales order must contain at least one line", null);

			var (cur, rate) = await ResolveCurAsync(companyId, currencyId, exchangeRate, date);
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);   // HM-2 Batch 5: document-currency dp (EGP no-op; KWD keeps fils; no static R)
			decimal R(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			var so = new SalesOrder { CompanyID = companyId, CustomerId = customerId, WarehouseId = warehouseId, OrderDate = date.Date, ExpectedDate = expected, Status = "Approved", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = rate, ProjectId = projectId };
			int ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lt = R(l.Qty * l.UnitPrice - l.DiscountAmount);
				sub += lt; tax += R(lt * l.TaxRate / 100m);
				so.Lines.Add(new SalesOrderLine { LineNo = ln++, ItemId = l.ItemId, ItemDescription = l.ItemDescription, Qty = l.Qty, UoMId = l.UoMId, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, LineTotal = lt, DeliveredQty = 0 });
			}
			so.SubTotal = R(sub); so.TaxTotal = R(tax); so.GrandTotal = R(sub + tax);
			_context.SalesOrders.Add(so);
			await _context.SaveChangesAsync();
			so.OrderNo = $"SO-{date:yyyy}-{so.ID:D5}";
			await _context.SaveChangesAsync();
			try
			{
				await _notify.NotifyRoleAsync(companyId, "inv", new[] { "WarehouseKeeper", "PurchasingOfficer", "InventoryManager" },
					"أمر بيع جديد", "New sales order",
					$"Sales order {so.OrderNo}, worth {so.GrandTotal:N2}, needs its delivery prepared", $"Sales order {so.OrderNo} ({so.GrandTotal:N2}) is ready to prepare for delivery",
					"sales_order", so.ID);
			}
			catch { /* notifications never block the business flow */ }
			return (true, null, so);
		}

		// ---------------- Quotations (P3-2) ----------------
		public async Task<List<Quotation>> GetQuotationsAsync(int companyId) =>
			await _context.Quotations.AsNoTracking().Where(q => q.CompanyID == companyId).OrderByDescending(q => q.ID).ToListAsync();

		public async Task<Quotation?> GetQuotationAsync(int companyId, int id) =>
			await _context.Quotations.AsNoTracking().Include(q => q.Lines).FirstOrDefaultAsync(q => q.ID == id && q.CompanyID == companyId);

		public async Task<(bool ok, string? error, Quotation? q)> CreateQuotationAsync(int companyId, int customerId, int? warehouseId, DateTime date, DateTime? validUntil, string? notes, List<SoLineInput> lines, string? userId, int? currencyId = null, decimal? exchangeRate = null)
		{
			if (customerId <= 0) return (false, "Customer is required", null);
			if (lines == null || lines.Count == 0) return (false, "The quotation must contain at least one line", null);

			var (cur, rate) = await ResolveCurAsync(companyId, currencyId, exchangeRate, date);
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);   // HM-2 Batch 5: document-currency dp (EGP no-op; no static R)
			decimal R(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			var q = new Quotation { CompanyID = companyId, CustomerId = customerId, WarehouseId = warehouseId, QuoteDate = date.Date, ValidUntil = validUntil, Status = "Draft", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = rate };
			int ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lt = R(l.Qty * l.UnitPrice - l.DiscountAmount);
				sub += lt; tax += R(lt * l.TaxRate / 100m);
				q.Lines.Add(new QuotationLine { LineNo = ln++, ItemId = l.ItemId, ItemDescription = l.ItemDescription, Qty = l.Qty, UoMId = l.UoMId, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, LineTotal = lt });
			}
			q.SubTotal = R(sub); q.TaxTotal = R(tax); q.GrandTotal = R(sub + tax);
			_context.Quotations.Add(q);
			await _context.SaveChangesAsync();
			q.QuoteNo = $"QT-{date:yyyy}-{q.ID:D5}";
			await _context.SaveChangesAsync();
			return (true, null, q);
		}

		// =============================================================================================
		// THE QUOTATION STATUS TRANSITION, and the customer-decision event that must share its fate.
		//
		// WHY THE EVENT IS RECORDED HERE AND NOT IN THE CONTROLLER. This method mutates the row and
		// calls SaveChangesAsync; BusinessEventService.RecordAsync writes the event AND its outbox
		// dispatch rows, and REFUSES to run without an ambient transaction. Emitting after the save
		// would be a best-effort event that can be lost between two writes - the status would move and
		// the fact would not, which is the one failure an outbox exists to prevent.
		//
		// ONLY A CUSTOMER DECISION PRODUCES AN EVENT. Staff can move a quotation to Accepted on the
		// phone, on the customer's behalf, and that is a different fact: an event that could not tell
		// the two apart would be useless to anyone auditing who decided. So the two authorized names
		// carry ByCustomer, and `customerDecision` is what selects them.
		//
		// THE COMPANY IS NEVER TAKEN FROM THE CALLER'S ARGUMENT. RecordAsync resolves it from the
		// ambient BusinessContext, which for a portal request is the ExternalPortal context bound from
		// the ACTIVE PortalUser row. `companyId` here scopes the QUERY; the event's tenancy comes from
		// the resolved identity, so a caller who lied about the company would find no row to move.
		public async Task<(bool ok, string? error)> SetQuotationStatusAsync(
			int companyId, int id, string status, bool customerDecision = false)
		{
			var allowed = new[] { "Draft", "Sent", "Accepted", "Rejected", "Expired" };
			if (!allowed.Contains(status)) return (false, "Invalid status");

			// A customer decision with no recorder REFUSES. Transitioning anyway would move the state
			// and record nothing, which is the exact asymmetry this seam was authorized to remove.
			if (customerDecision && _events == null)
				return (false, "The business event recorder is unavailable");

			var q = await _context.Quotations.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (q == null) return (false, "Quotation not found");
			if (q.Status == "Converted") return (false, "The quotation has been converted to a sales order — its status cannot be changed");

			// NO EVENT FOR A TRANSITION THAT DID NOT OCCUR. Re-asserting the status a row already holds
			// is not a decision; it is a repeat, and it must not add a second fact. Returning before the
			// transaction also means a double-click writes nothing at all.
			var alreadyThere = string.Equals(q.Status, status, StringComparison.Ordinal);

			var eventType = !customerDecision || alreadyThere ? null
				: string.Equals(status, "Accepted", StringComparison.Ordinal)
					? CrossBuy.BL.Platform.QuotationEvents.AcceptedByCustomer
				: string.Equals(status, "Rejected", StringComparison.Ordinal)
					? CrossBuy.BL.Platform.QuotationEvents.RejectedByCustomer
				: null;

			// ONE UNIT OF WORK. BeginOrJoin, so this nests safely under an outer transaction and behaves
			// exactly as before at a site that has none.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);

			q.Status = status;
			await _context.SaveChangesAsync();

			if (eventType != null)
			{
				await _events!.RecordAsync(new CrossBuy.Models.Platform.BusinessEventRecord
				{
					EntityCode = CrossBuy.BL.Platform.EntityRegistry.Quotation,
					EntityId = q.ID,
					EventType = eventType,
					// The DEDUP KEY is what makes a repeated decision exactly one event even if the
					// status check above is ever bypassed: RecordAsync returns the original id and
					// writes nothing for a key it has already stored in this company.
					DedupKey = $"quotation-{q.ID}-{eventType}",
					Payload = new
					{
						quotationId = q.ID,
						customerId = q.CustomerId,
						quoteNo = q.QuoteNo,
						grandTotal = q.GrandTotal,
						currencyId = q.CurrencyId,
						decision = status,
					},
				});
			}

			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error, int? salesOrderId)> ConvertQuotationToOrderAsync(int companyId, int id, string? userId)
		{
			var q = await _context.Quotations.Include(x => x.Lines).FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (q == null) return (false, "Quotation not found", null);
			if (q.Status == "Converted" || q.SalesOrderId != null) return (false, "The quotation has already been converted to a sales order", null);
			if (q.Status == "Rejected" || q.Status == "Expired") return (false, "A rejected or expired quotation cannot be converted", null);

			var soLines = q.Lines.OrderBy(l => l.LineNo).Select(l => new SoLineInput
			{ ItemId = l.ItemId, ItemDescription = l.ItemDescription, Qty = l.Qty, UoMId = l.UoMId, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate }).ToList();
			var (ok, err, so) = await CreateSalesOrderAsync(companyId, q.CustomerId, q.WarehouseId, DateTime.Today, q.ValidUntil, q.Notes, soLines, userId, q.CurrencyId, q.ExchangeRate);
			if (!ok) return (false, err, null);

			q.Status = "Converted"; q.SalesOrderId = so!.ID;
			await _context.SaveChangesAsync();
			return (true, null, so.ID);
		}

		public async Task<List<DeliveryNote>> GetDeliveriesAsync(int companyId) =>
			await _context.DeliveryNotes.AsNoTracking().Where(d => d.CompanyID == companyId).OrderByDescending(d => d.ID).ToListAsync();

		public async Task<DeliveryNote?> GetDeliveryAsync(int companyId, int id) =>
			await _context.DeliveryNotes.Include(d => d.Lines).FirstOrDefaultAsync(d => d.ID == id && d.CompanyID == companyId);

		public async Task<(bool ok, string? error, DeliveryNote? dn)> CreateDeliveryAsync(int companyId, int? customerId, int warehouseId, int? soId, DateTime date, string? notes, List<DeliveryLineInput> lines, string? userId)
		{
			if (warehouseId <= 0) return (false, "Warehouse is required", null);
			if (lines == null || lines.Count == 0) return (false, "The delivery note must contain at least one line", null);

			// HM-2 Batch 5: TotalCost is a FUNCTIONAL cost (Σ movement TotalCost) → round to functional dp via the central helper (EGP no-op; no static R).
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal R(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			var dn = new DeliveryNote { CompanyID = companyId, CustomerId = customerId, WarehouseId = warehouseId, SalesOrderId = soId, DeliveryDate = date.Date, Status = "Posted", Notes = notes, CreatedBy = userId, CreatedAt = DateTime.UtcNow };
			_context.DeliveryNotes.Add(dn);
			await _context.SaveChangesAsync();
			dn.DeliveryNo = $"DN-{date:yyyy}-{dn.ID:D5}";

			int ln = 1; decimal total = 0;
			foreach (var l in lines)
			{
				if (l.ItemId <= 0 || l.Qty <= 0) continue;
				var (sok, serr, mv) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId, WarehouseId = warehouseId, Direction = -1, Qty = l.Qty, UoMId = l.UoMId,
					BatchNo = l.BatchNo, SerialNo = l.SerialNo, BinLocationId = l.BinLocationId, SourceType = "Issue", SourceId = dn.ID, PostToGl = true, Notes = $"Delivery note {dn.DeliveryNo}"
				}, userId);
				if (!sok)
				{
					_context.DeliveryNoteLines.RemoveRange(dn.Lines);
					_context.DeliveryNotes.Remove(dn);
					await _context.SaveChangesAsync();
					return (false, $"Could not issue an item: {serr}", null);
				}
				dn.Lines.Add(new DeliveryNoteLine { DeliveryNoteId = dn.ID, LineNo = ln++, ItemId = l.ItemId, Qty = l.Qty, UoMId = l.UoMId, UnitCost = mv!.UnitCost, LineTotal = mv.TotalCost, BatchNo = l.BatchNo, SerialNo = l.SerialNo, SalesOrderLineId = l.SalesOrderLineId, StockMovementId = mv.ID });
				total += mv.TotalCost;
				if (l.SalesOrderLineId != null)
				{
					var sol = await _context.SalesOrderLines.FirstOrDefaultAsync(x => x.ID == l.SalesOrderLineId);
					if (sol != null) sol.DeliveredQty += l.Qty;
				}
			}
			dn.TotalCost = R(total);
			await _context.SaveChangesAsync();

			if (soId != null)
			{
				var so = await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.ID == soId);
				if (so != null) { so.Status = so.Lines.All(x => x.DeliveredQty >= x.Qty) ? "Delivered" : "Approved"; await _context.SaveChangesAsync(); }
			}
			try
			{
				await _notify.NotifyRoleAsync(companyId, "acc", new[] { "Accountant", "ChiefAccountant" },
					"تم تسليم بضاعة", "Goods delivered",
					$"Delivery note {dn.DeliveryNo}, costing {dn.TotalCost:N2}, is ready to invoice", $"Delivery {dn.DeliveryNo} ({dn.TotalCost:N2}) is ready to invoice",
					"delivery_posted", dn.ID);
			}
			catch { /* notifications never block the business flow */ }
			return (true, null, dn);
		}

		// turns a SO into a sales invoice. If goods were delivered → revenue only (COGS already posted at delivery).
		// If not delivered → the invoice also issues stock + posts COGS (direct sale).
		public async Task<(bool ok, string? error, int? invoiceId)> ConvertToInvoiceAsync(int companyId, int soId, string? userId)
		{
			var so = await _context.SalesOrders.Include(s => s.Lines).FirstOrDefaultAsync(s => s.ID == soId && s.CompanyID == companyId);
			if (so == null) return (false, "Sales order not found", null);
			if (so.Status == "Closed") return (false, "The sales order has already been converted to an invoice", null);
			if (so.Status == "Cancelled") return (false, "The sales order is cancelled", null);

			var deliveries = await _context.DeliveryNotes.Where(d => d.CompanyID == companyId && d.SalesOrderId == soId && d.Status == "Posted").ToListAsync();
			bool delivered = deliveries.Any();
			int revenue = await _context.Accounts.Where(a => a.CompanyID == companyId && a.Code == "4101").Select(a => a.ID).FirstOrDefaultAsync();
			if (revenue == 0) revenue = await _context.Accounts.Where(a => a.CompanyID == companyId && a.IsPostable && a.Code.StartsWith("4")).OrderBy(a => a.Code).Select(a => a.ID).FirstOrDefaultAsync();
			if (revenue == 0) return (false, "No revenue account is defined", null);

			var lines = new List<SalesLineInput>();
			foreach (var l in so.Lines.OrderBy(x => x.LineNo))
			{
				if (l.Qty <= 0) continue;
				lines.Add(new SalesLineInput
				{
					ItemDescription = l.ItemDescription ?? "",
					Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate,
					RevenueAccountId = revenue,
					ItemId = delivered ? (int?)null : l.ItemId,         // delivered → no stock/COGS (done at delivery); else direct issue
					WarehouseId = delivered ? (int?)null : so.WarehouseId,
				});
			}
			if (lines.Count == 0) return (false, "There are no invoiceable lines", null);

			var (ok, err, inv) = await _receivables.CreateSalesInvoiceAsync(companyId, so.CustomerId, DateTime.Today, lines, $"From sales order {so.OrderNo}", null, so.CurrencyId, so.ExchangeRate, so.ProjectId);
			if (!ok) return (false, err, null);

			so.Status = "Closed";
			foreach (var d in deliveries) d.InvoiceId = inv!.ID;
			await _context.SaveChangesAsync();
			return (true, null, inv!.ID);
		}
	}
}
