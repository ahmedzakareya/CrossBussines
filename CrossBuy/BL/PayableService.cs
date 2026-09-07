using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class PurchaseLineInput
	{
		public string ItemDescription { get; set; } = "";
		public decimal Qty { get; set; } = 1;
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
		public int ExpenseAccountId { get; set; }
		public int? CostCenterId { get; set; }
		public int? ItemId { get; set; }          // when set → posts a stock receipt + uses the category inventory account
		public int? WarehouseId { get; set; }
		// HM-16: when set → this line SETTLES a posted goods receipt (Dr GRNI / Cr AP, NO stock movement). The caller
		// passes ExpenseAccountId = the category GRNI account and ItemId = null (the stock was already received on the GRN).
		// CreatePurchaseInvoiceAsync validates the GRN is Posted+un-invoiced (set-once) and stamps it Invoiced in-tx.
		public int? GoodsReceiptId { get; set; }
	}

	public interface IPayableService
	{
		Task<List<Vendor>> GetVendorsAsync(int companyId);
		Task<(List<Vendor> rows, int total)> SearchVendorsAsync(int companyId, string? q, bool? active, int page, int pageSize);
		Task<List<(string value, string name)>> SuggestVendorsAsync(int companyId, string? term, int take = 10);
		Task<Vendor> CreateVendorAsync(int companyId, string name, string? nameEn, string? taxNo);
		Task<(bool ok, string? error)> SaveVendorAsync(int companyId, Vendor dto);
		Task<(bool ok, string? error, PurchaseInvoice? inv)> CreatePurchaseInvoiceAsync(int companyId, int vendorId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null);
		// P3: edit a POSTED purchase invoice = reverse the original GL + stock (at the exact received cost), then re-post — same invoice number.
		Task<(bool ok, string? error, PurchaseInvoice? inv)> EditPurchaseInvoiceAsync(int companyId, int invoiceId, int vendorId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null);
		// HM-16: match a POSTED goods receipt to a NEW purchase invoice that clears its GRNI (Dr GRNI / Cr AP, no re-receipt). 1:1 minimal.
		Task<(bool ok, string? error, PurchaseInvoice? inv)> MatchGoodsReceiptToInvoiceAsync(int companyId, int goodsReceiptId, DateTime invoiceDate, decimal invoiceAmount, int? userId);
		Task<(bool ok, string? error)> CreatePaymentAsync(int companyId, int vendorId, DateTime date, decimal amount, string method, int cashAccountId, string? notes, int? userId, decimal whtRate = 0, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null);
		Task<List<PurchaseInvoice>> GetInvoicesAsync(int companyId);
		Task<List<AgingRow>> AgingAsync(int companyId, DateTime asOf);
		// P3-3b: purchase returns / debit notes
		Task<List<PurchaseReturn>> GetPurchaseReturnsAsync(int companyId);
		Task<PurchaseReturn?> GetPurchaseReturnAsync(int companyId, int id);
		Task<(bool ok, string? error, PurchaseReturn? ret)> CreatePurchaseReturnAsync(int companyId, int vendorId, int? originalInvoiceId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId);
		Task<(bool ok, string? error, PurchaseReturn? ret)> EditPurchaseReturnAsync(int companyId, int returnId, int vendorId, int? originalInvoiceId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId);
	}

	public class PayableService : IPayableService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		private readonly IStockService _stock;
		private readonly INotificationService _notify;
		private readonly ICurrencyService _currency;
		private readonly ICurrencyRounding _rounding;
		private readonly CrossBuy.BL.Platform.IBusinessEventService _events;   // Platform Kernel: durable business facts (in-transaction)
		public PayableService(CrossDbContext context, IJournalEntryService journals, IStockService stock, INotificationService notify, ICurrencyService currency, ICurrencyRounding rounding, CrossBuy.BL.Platform.IBusinessEventService events) { _context = context; _journals = journals; _stock = stock; _notify = notify; _currency = currency; _rounding = rounding; _events = events; }

		private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
		private async Task<int?> AccIdAsync(int companyId, string code) =>
			await _context.Accounts.Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		public async Task<List<Vendor>> GetVendorsAsync(int companyId) =>
			await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == companyId).OrderBy(v => v.Name).ToListAsync();

		// server-side paged vendor search (Tagify multi-tag OR over name/tax/phone/segment/contact/email)
		public async Task<(List<Vendor> rows, int total)> SearchVendorsAsync(int companyId, string? q, bool? active, int page, int pageSize)
		{
			var query = _context.Vendors.AsNoTracking().Where(v => v.CompanyID == companyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Vendor>(terms, s =>
					v => v.Name.Contains(s) || (v.NameEn != null && v.NameEn.Contains(s)) || (v.TaxRegNo != null && v.TaxRegNo.Contains(s))
						|| (v.Phone != null && v.Phone.Contains(s)) || (v.Segment != null && v.Segment.Contains(s))
						|| (v.ContactPerson != null && v.ContactPerson.Contains(s)) || (v.Email != null && v.Email.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (active.HasValue) query = query.Where(v => v.IsActive == active.Value);
			var total = await query.CountAsync();
			if (page < 1) page = 1;
			if (pageSize < 1) pageSize = 25; else if (pageSize > 100000) pageSize = 100000;
			var rows = await query.OrderBy(v => v.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public async Task<List<(string value, string name)>> SuggestVendorsAsync(int companyId, string? term, int take = 10)
		{
			var t = (term ?? "").Trim();
			var query = _context.Vendors.AsNoTracking().Where(v => v.CompanyID == companyId && v.IsActive);
			if (t.Length > 0)
				query = query.Where(v => v.Name.Contains(t) || (v.NameEn != null && v.NameEn.Contains(t))
					|| (v.TaxRegNo != null && v.TaxRegNo.Contains(t)) || (v.Phone != null && v.Phone.Contains(t)) || (v.Segment != null && v.Segment.Contains(t)));
			var rows = await query.OrderBy(v => v.Name).Take(take <= 0 ? 10 : take)
				.Select(v => new { v.Name, v.Segment, v.Phone }).ToListAsync();
			return rows.Select(v => (v.Name, v.Segment ?? v.Phone ?? "")).ToList();
		}

		public async Task<Vendor> CreateVendorAsync(int companyId, string name, string? nameEn, string? taxNo)
		{
			var control = await AccIdAsync(companyId, "2101") ?? 0;   // AP control
			var v = new Vendor { CompanyID = companyId, Name = name, NameEn = nameEn, TaxRegNo = taxNo, ControlAccountId = control, IsActive = true, CreatedAt = DateTime.UtcNow };
			_context.Vendors.Add(v);
			await _context.SaveChangesAsync();
			return v;
		}

		public async Task<(bool ok, string? error)> SaveVendorAsync(int companyId, Vendor dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Supplier name is required");
			var v = dto.ID > 0 ? await _context.Vendors.FirstOrDefaultAsync(x => x.ID == dto.ID && x.CompanyID == companyId) : null;
			if (v == null)
			{
				v = new Vendor { CompanyID = companyId, ControlAccountId = await AccIdAsync(companyId, "2101") ?? 0, IsActive = true, CreatedAt = DateTime.UtcNow };
				_context.Vendors.Add(v);
			}
			v.Name = dto.Name; v.NameEn = dto.NameEn; v.TaxRegNo = dto.TaxRegNo; v.Address = dto.Address;
			v.PaymentTermsDays = dto.PaymentTermsDays; v.Phone = dto.Phone; v.Email = dto.Email;
			v.ContactPerson = dto.ContactPerson; v.Segment = dto.Segment; v.IsActive = dto.IsActive;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<PurchaseInvoice>> GetInvoicesAsync(int companyId) =>
			await _context.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == companyId).OrderByDescending(i => i.ID).ToListAsync();

		public async Task<(bool ok, string? error, PurchaseInvoice? inv)> CreatePurchaseInvoiceAsync(
			int companyId, int vendorId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
		{
			var ven = await _context.Vendors.FirstOrDefaultAsync(v => v.ID == vendorId && v.CompanyID == companyId);
			if (ven == null) return (false, "Supplier not found", null);
			if (lines == null || lines.Count == 0) return (false, "The invoice must contain at least one line", null);

			// HM-16: some lines may SETTLE posted goods receipts (vendor-invoice matching). Load + validate those GRNs and
			// enforce SET-ONCE *before* any posting, so a failed match leaves zero effect. Loaded TRACKED so we can stamp
			// them Invoiced inside the same transaction below (they share the invoice's fate).
			var grnIds = lines.Where(l => l.GoodsReceiptId is int).Select(l => l.GoodsReceiptId!.Value).Distinct().ToList();
			var grns = new List<CrossBuy.Models.Context.Inventory.GoodsReceipt>();
			if (grnIds.Count > 0)
			{
				grns = await _context.GoodsReceipts.Where(g => grnIds.Contains(g.ID) && g.CompanyID == companyId).ToListAsync();
				foreach (var id in grnIds)
				{
					var g = grns.FirstOrDefault(x => x.ID == id);
					if (g == null) return (false, $"Goods receipt ({id}) not found", null);
					if (g.Status != "Posted") return (false, $"Goods receipt ({g.ReceiptNo}) is not posted — it cannot be invoiced", null);
					if (g.InvoiceId != null) return (false, $"Goods receipt ({g.ReceiptNo}) is already invoiced — it cannot be invoiced twice", null);   // SET-ONCE guard
					if (g.VendorId != null && g.VendorId != vendorId) return (false, $"Goods receipt ({g.ReceiptNo}) belongs to a different supplier", null);
				}
			}

			var vatIn = await AccIdAsync(companyId, "110401");

			// Multi-Currency (1-3): document is in `cur`; books/GL/stock are in the branch functional currency.
			// rate = foreign→functional (EGP per unit when functional is EGP). Convert AT SOURCE → GL & stock stay base.
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Buy"); rate = r; }
			// HM-2 (3-ج): document totals round to document dp (Rd); the base is FUNCTIONAL (Rf). The purchase invoice JE has NO eligible
			// P&L line for a pure-stockable invoice (Dr 1103 / Cr 2101 / Dr VAT are all reconciled), so it must balance BY CONSTRUCTION:
			// GrandTotalBase (= Σ Rf line bases + Rf VAT) is BOTH the stored column AND the single 2101 line, and the Dr lines are those
			// same Rf bases ⇒ Σ Dr = 2101 exactly, no remainder reaches JES. No distribution onto a reconciled account (no stock_gl drift).
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rd(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			decimal ToBase(decimal foreignAmt) => Rf(foreignAmt * rate);   // functional base per component

			var inv = new PurchaseInvoice { CompanyID = companyId, VendorId = vendorId, InvoiceDate = date.Date, Status = "Posted", Notes = notes, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = rate, ProjectId = projectId };
			var ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lineTotal = Rd(l.Qty * l.UnitPrice - l.DiscountAmount);   // document
				var lineTax = Rd(lineTotal * l.TaxRate / 100m);
				sub += lineTotal; tax += lineTax;
				// for an inventory item, the line debits the category inventory account (perpetual)
				var acct = l.ExpenseAccountId;
				if (l.ItemId != null)
				{
					var invAcc = await _context.Items.AsNoTracking().Where(i => i.ID == l.ItemId && i.CompanyID == companyId)
						.Join(_context.ItemCategories, i => i.ItemCategoryId, c => c.ID, (i, c) => c.InventoryAccountId)
						.FirstOrDefaultAsync();
					if (invAcc != null) acct = invAcc.Value;
				}
				inv.Lines.Add(new PurchaseInvoiceLine { LineNo = ln++, ItemDescription = l.ItemDescription, Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, ExpenseAccountId = acct, CostCenterId = l.CostCenterId, ItemId = l.ItemId, WarehouseId = l.WarehouseId, LineTotal = lineTotal });
			}
			inv.SubTotal = Rd(sub); inv.TaxTotal = Rd(tax); inv.GrandTotal = Rd(sub + tax);   // document totals
			// base totals: sum of per-line functional bases so the GL balances exactly (Σ Dr = GrandTotalBase = 2101)
			decimal subBase = inv.Lines.Sum(l => ToBase(l.LineTotal));
			decimal taxBase = inv.Lines.Sum(l => ToBase(Rd(l.LineTotal * l.TaxRate / 100m)));
			inv.SubTotalBase = subBase; inv.TaxTotalBase = taxBase; inv.GrandTotalBase = subBase + taxBase;
			// HM-1-أ ب-3: ONE ambient transaction — the invoice + its GL + the stock receipt are all-or-nothing.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			_context.PurchaseInvoices.Add(inv);
			await _context.SaveChangesAsync();
			inv.InvoiceNo = $"PI-{date:yyyy}-{inv.ID:D5}";
			await _context.SaveChangesAsync();

			// auto journal (in functional currency): Dr expense/inventory per line / Dr VAT input / Cr AP (grand base)
			var jlines = new List<JournalLineInput>();
			foreach (var l in inv.Lines)
				jlines.Add(new JournalLineInput { AccountId = l.ExpenseAccountId, Debit = ToBase(l.LineTotal), Credit = 0, CostCenterId = l.CostCenterId, ProjectId = projectId, Description = l.ItemDescription });
			if (taxBase > 0 && vatIn != null)
				jlines.Add(new JournalLineInput { AccountId = vatIn.Value, Debit = taxBase, Credit = 0, ProjectId = projectId, Description = "Input VAT" });
			jlines.Add(new JournalLineInput { AccountId = ven.ControlAccountId, Debit = 0, Credit = inv.GrandTotalBase.Value, ProjectId = projectId, Description = $"Purchase invoice {inv.InvoiceNo}" });

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "PurchaseInvoice", SourceId = inv.ID, CurrencyId = cur,
				Description = $"فاتورة شراء {inv.InvoiceNo} - {ven.Name}", DescriptionEn = $"Purchase invoice {inv.InvoiceNo}", Lines = jlines,
			}, userId);
			if (!ok) return (false, err, null);
			inv.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();

			// HM-1-أ ب-3/ب-4: receive stock — Stockable only, deterministic order, EVERY result CHECKED.
			var recItemIds = inv.Lines.Where(x => x.ItemId != null).Select(x => x.ItemId!.Value).Distinct().ToList();
			var recItemTypes = await _context.Items.AsNoTracking().Where(i => recItemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemType);
			foreach (var l in inv.Lines.Where(x => x.ItemId != null && x.WarehouseId != null && x.Qty > 0).OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseId))
			{
				if (!CrossBuy.Models.Context.Inventory.ItemTypes.RequiresStock(recItemTypes.GetValueOrDefault(l.ItemId!.Value))) continue;
				var unitCost = Rf(ToBase(l.LineTotal) / l.Qty);   // FUNCTIONAL base cost (converted) net of discount, excl. tax
				var (sok, serr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = 1,
					Qty = l.Qty, UnitCostInBase = unitCost, SourceType = "PurchaseInvoice", SourceId = inv.ID, SourceLineId = l.ID,
					PostToGl = false, Notes = $"Receipt for purchase invoice {inv.InvoiceNo}"
				}, userId?.ToString());
				if (!sok) return (false, serr ?? "Could not receive the stock", null);
			}

			// HM-16: stamp each settled goods receipt as Invoiced — SET-ONCE, inside this transaction so the link shares
			// the invoice's fate (a rolled-back invoice leaves the GRN open). Re-verify un-invoiced under the tx to keep
			// the guard honest against a racing match (the earlier read was pre-tx). grns were loaded tracked above.
			foreach (var g in grns)
			{
				if (g.InvoiceId != null) return (false, $"Goods receipt ({g.ReceiptNo}) is already invoiced — it cannot be invoiced twice", null);
				g.InvoiceId = inv.ID;
			}
			if (grns.Count > 0) await _context.SaveChangesAsync();

			// Platform Kernel (ADR-001): the durable fact, INSIDE this transaction and BEFORE the commit, so
			// the event shares the fate of the invoice, its journal entry AND its stock receipt. No try/catch:
			// if the event cannot be written the purchase must not stand.
			await _events.RecordAsync(new BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.PurchaseInvoice,
				EntityId = inv.ID,
				EventType = CrossBuy.BL.Platform.PurchaseInvoiceEvents.Created,
				PayloadVersion = PurchaseInvoiceEventPayload.Version,
				Visibility = BusinessEventVisibility.Internal,
				DedupKey = $"PurchaseInvoice.Created:{inv.ID}",   // one Created per invoice, forever
				Payload = new PurchaseInvoiceEventPayload
				{
					InvoiceNumber = inv.InvoiceNo,
					SupplierId = inv.VendorId,
					SupplierName = ven.Name,
					InvoiceDate = inv.InvoiceDate,
					NewStatus = inv.Status,
					TotalAfter = inv.GrandTotal,
				},
			});

			await tx.CommitAsync();

			// Platform Kernel slice 2: the legacy after-commit NotifyRoleAsync that used to sit here was
			// REMOVED — NotificationProjection now produces it from the event above, with the same audience
			// ("acc"/ChiefAccountant), the same catalog type ("purchase_invoice") and the same wording.
			// Keeping both would double-notify. See ADR-006.
			return (true, null, inv);
		}

		// HM-16: match a POSTED goods receipt to a NEW purchase invoice that CLEARS its GRNI (Dr GRNI / Cr AP, NO second
		// stock movement — the goods were received on the GRN). 1:1 minimal: the billed amount MUST equal the received
		// value; price/tax variance is deferred, so a mismatch is REFUSED (no partial/held difference). The invoice is
		// booked in the branch FUNCTIONAL currency (GRNI is a functional balance) so the receipt's functional line costs
		// pass straight through with no re-conversion. Set-once is enforced inside CreatePurchaseInvoiceAsync (it stamps
		// GoodsReceipt.InvoiceId in-tx). Reuses CreatePurchaseInvoiceAsync — no re-implementation of the GRNI posting.
		// Hardcoded Arabic (this file's convention — every PayableService message is hardcoded Arabic).
		public async Task<(bool ok, string? error, PurchaseInvoice? inv)> MatchGoodsReceiptToInvoiceAsync(
			int companyId, int goodsReceiptId, DateTime invoiceDate, decimal invoiceAmount, int? userId)
		{
			var gr = await _context.GoodsReceipts.AsNoTracking().Include(g => g.Lines)
				.FirstOrDefaultAsync(g => g.ID == goodsReceiptId && g.CompanyID == companyId);
			if (gr == null) return (false, "Goods receipt not found", null);
			if (gr.Status != "Posted" || gr.InvoiceId != null) return (false, "The goods receipt is not posted, or is already invoiced", null);
			if (gr.VendorId == null) return (false, "The goods receipt has no supplier to invoice", null);
			// value guard: price variance is deferred — the billed amount must equal the received value or the match is refused.
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			if (Rf(invoiceAmount) != Rf(gr.TotalCost))
				return (false, $"The invoice value ({Rf(invoiceAmount)}) differs from the received value ({Rf(gr.TotalCost)}); price-variance matching is not supported — the match was rejected", null);
			// build the GRNI-clearing lines from the receipt: ExpenseAccountId = category GRNI, ItemId = null (no re-receipt),
			// GoodsReceiptId set so CreatePurchaseInvoiceAsync enforces set-once and stamps the GRN Invoiced in-transaction.
			var lines = new List<PurchaseLineInput>();
			foreach (var l in gr.Lines)
			{
				var grni = await _context.Items.AsNoTracking().Where(i => i.ID == l.ItemId && i.CompanyID == companyId)
					.Join(_context.ItemCategories, i => i.ItemCategoryId, c => c.ID, (i, c) => (int?)(c.GrniAccountId ?? c.InventoryAccountId)).FirstOrDefaultAsync();
				if (grni == null || grni == 0) return (false, "One of the receipt lines has no GRNI/inventory account configured on its category", null);
				var name = await _context.Items.AsNoTracking().Where(i => i.ID == l.ItemId).Select(i => i.Name).FirstOrDefaultAsync();
				lines.Add(new PurchaseLineInput { ItemDescription = name ?? $"GRN {gr.ReceiptNo} #{l.LineNo}", Qty = l.Qty, UnitPrice = l.UnitCost, DiscountAmount = 0, TaxRate = 0, ExpenseAccountId = grni.Value, ItemId = null, WarehouseId = null, GoodsReceiptId = gr.ID });
			}
			if (lines.Count == 0) return (false, "The goods receipt has no lines to invoice", null);
			return await CreatePurchaseInvoiceAsync(companyId, gr.VendorId.Value, invoiceDate, lines, $"Match of goods receipt {gr.ReceiptNo}", userId);
		}

		// P3: EDIT a posted purchase invoice — reverse the original postings (stock at the EXACT received cost), then re-post on the same row/number.
		public async Task<(bool ok, string? error, PurchaseInvoice? inv)> EditPurchaseInvoiceAsync(
			int companyId, int invoiceId, int vendorId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
		{
			var inv = await _context.PurchaseInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.ID == invoiceId && i.CompanyID == companyId);
			if (inv == null) return (false, "Invoice not found", null);
			if (inv.Status != "Posted") return (false, "An invoice that is not posted, or is cancelled, cannot be edited", null);
			if (await _context.PaymentAllocations.AsNoTracking().AnyAsync(a => a.CompanyID == companyId && a.PurchaseInvoiceId == invoiceId))
				return (false, "The invoice cannot be edited because a payment is allocated to it — unallocate it first", null);
			var ven = await _context.Vendors.FirstOrDefaultAsync(v => v.ID == vendorId && v.CompanyID == companyId);
			if (ven == null) return (false, "Supplier not found", null);
			if (lines == null || lines.Count == 0) return (false, "The invoice must contain at least one line", null);

			// Platform Kernel slice 2: snapshot the header BEFORE any mutation so the event can carry a change
			// SUMMARY (field names + total delta) instead of the entity graph.
			var beforeVendorId = inv.VendorId;
			var beforeInvoiceDate = inv.InvoiceDate;
			var beforeCurrencyId = inv.CurrencyId;
			var beforeExchangeRate = inv.ExchangeRate;
			var beforeProjectId = inv.ProjectId;
			var beforeNotes = inv.Notes;
			var beforeStatus = inv.Status;
			var beforeGrandTotal = inv.GrandTotal;
			var beforeLineSignature = LineSignature(inv.Lines);

			var vatIn = await AccIdAsync(companyId, "110401");
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Buy"); rate = r; }
			// HM-2 (3-ج): same as create — document Rd, base Rf; balance by construction (Σ Dr = GrandTotalBase = 2101). Reverse+repost
			// use the SAME rate resolution, so a KWD edit leaves no artifact in 2101 or stock_gl.
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rd(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			decimal ToBase(decimal foreignAmt) => Rf(foreignAmt * rate);

			// HM-1-أ ب-3: ONE ambient transaction wraps the whole reverse+repost so an edit is all-or-nothing.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			// (1) reverse the original receipts — OUT at the EXACT received cost (OutCostOverride) so the subledger backs out the same value the GL will
			var origMoves = await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.SourceType == "PurchaseInvoice" && m.SourceId == invoiceId && m.Direction == 1)
				.OrderBy(m => m.ItemId).ThenBy(m => m.WarehouseId).ToListAsync();
			foreach (var m in origMoves)
			{
				var (rok, rerr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest {
					Date = date, ItemId = m.ItemId, WarehouseId = m.WarehouseId, Direction = -1,
					Qty = m.QtyBase, UoMId = null, OutCostOverride = m.UnitCost, PostToGl = false,
					SourceType = "PurchaseInvoiceEdit", SourceId = invoiceId, Notes = $"Reversal of the receipt for the edit of invoice {inv.InvoiceNo}"
				}, userId?.ToString());
				if (!rok) return (false, rerr ?? "Could not reverse the stock receipt", null);
			}
			// (2) reverse the original GL entry (mirror; inventory/expense + VAT + AP all back out at the original amounts)
			if (inv.JournalEntryId.HasValue)
			{
				var (rjok, rjerr, _) = await _journals.ReverseAsync(inv.JournalEntryId.Value, userId, $"Edit of invoice {inv.InvoiceNo}");
				if (!rjok) return (false, rjerr ?? "Could not reverse the invoice entry", null);
			}

			// (3) drop old lines
			_context.PurchaseInvoiceLines.RemoveRange(inv.Lines);
			inv.Lines.Clear();

			// (4) recompute + update the invoice row (keep ID + InvoiceNo)
			var ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lineTotal = Rd(l.Qty * l.UnitPrice - l.DiscountAmount);   // document
				var lineTax = Rd(lineTotal * l.TaxRate / 100m);
				sub += lineTotal; tax += lineTax;
				var acct = l.ExpenseAccountId;
				if (l.ItemId != null)
				{
					var invAcc = await _context.Items.AsNoTracking().Where(i => i.ID == l.ItemId && i.CompanyID == companyId)
						.Join(_context.ItemCategories, i => i.ItemCategoryId, c => c.ID, (i, c) => c.InventoryAccountId).FirstOrDefaultAsync();
					if (invAcc != null) acct = invAcc.Value;
				}
				inv.Lines.Add(new PurchaseInvoiceLine { LineNo = ln++, ItemDescription = l.ItemDescription, Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, ExpenseAccountId = acct, CostCenterId = l.CostCenterId, ItemId = l.ItemId, WarehouseId = l.WarehouseId, LineTotal = lineTotal });
			}
			decimal subBase = inv.Lines.Sum(l => ToBase(l.LineTotal));
			decimal taxBase = inv.Lines.Sum(l => ToBase(Rd(l.LineTotal * l.TaxRate / 100m)));
			inv.VendorId = vendorId; inv.InvoiceDate = date.Date; inv.Notes = notes; inv.CurrencyId = cur; inv.ExchangeRate = rate; inv.ProjectId = projectId;
			inv.SubTotal = Rd(sub); inv.TaxTotal = Rd(tax); inv.GrandTotal = Rd(sub + tax);   // document totals
			inv.SubTotalBase = subBase; inv.TaxTotalBase = taxBase; inv.GrandTotalBase = subBase + taxBase;   // Σ Dr = 2101 by construction
			await _context.SaveChangesAsync();

			// (5) new GL entry
			var jlines = new List<JournalLineInput>();
			foreach (var l in inv.Lines)
				jlines.Add(new JournalLineInput { AccountId = l.ExpenseAccountId, Debit = ToBase(l.LineTotal), Credit = 0, CostCenterId = l.CostCenterId, ProjectId = projectId, Description = l.ItemDescription });
			if (taxBase > 0 && vatIn != null)
				jlines.Add(new JournalLineInput { AccountId = vatIn.Value, Debit = taxBase, Credit = 0, ProjectId = projectId, Description = "Input VAT" });
			jlines.Add(new JournalLineInput { AccountId = ven.ControlAccountId, Debit = 0, Credit = inv.GrandTotalBase.Value, ProjectId = projectId, Description = $"Purchase invoice {inv.InvoiceNo} (edited)" });
			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "PurchaseInvoice", SourceId = inv.ID, CurrencyId = cur,
				Description = $"فاتورة شراء {inv.InvoiceNo} (معدّلة) - {ven.Name}", DescriptionEn = $"Purchase invoice {inv.InvoiceNo} (edited)", Lines = jlines,
			}, userId);
			if (!ok) return (false, err, null);
			inv.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();

			// (6) re-receive stock — Stockable only, deterministic order, results CHECKED
			var recItemIds = inv.Lines.Where(x => x.ItemId != null).Select(x => x.ItemId!.Value).Distinct().ToList();
			var recItemTypes = await _context.Items.AsNoTracking().Where(i => recItemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemType);
			foreach (var l in inv.Lines.Where(x => x.ItemId != null && x.WarehouseId != null && x.Qty > 0).OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseId))
			{
				if (!CrossBuy.Models.Context.Inventory.ItemTypes.RequiresStock(recItemTypes.GetValueOrDefault(l.ItemId!.Value))) continue;
				var unitCost = Rf(ToBase(l.LineTotal) / l.Qty);   // FUNCTIONAL base cost
				var (sok, serr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = 1,
					Qty = l.Qty, UnitCostInBase = unitCost, SourceType = "PurchaseInvoice", SourceId = inv.ID, SourceLineId = l.ID,
					PostToGl = false, Notes = $"Receipt for purchase invoice {inv.InvoiceNo} (edited)"
				}, userId?.ToString());
				if (!sok) return (false, serr ?? "Could not receive the stock", null);
			}

			// Platform Kernel (ADR-001): the durable fact, inside this transaction and before the commit.
			// Field NAMES only. No DedupKey — an invoice may legitimately be edited more than once.
			var changedFields = new List<string>();
			if (beforeVendorId != inv.VendorId) changedFields.Add(nameof(inv.VendorId));
			if (beforeInvoiceDate != inv.InvoiceDate) changedFields.Add(nameof(inv.InvoiceDate));
			if (beforeCurrencyId != inv.CurrencyId) changedFields.Add(nameof(inv.CurrencyId));
			if (beforeExchangeRate != inv.ExchangeRate) changedFields.Add(nameof(inv.ExchangeRate));
			if (beforeProjectId != inv.ProjectId) changedFields.Add(nameof(inv.ProjectId));
			if (beforeNotes != inv.Notes) changedFields.Add(nameof(inv.Notes));
			if (beforeLineSignature != LineSignature(inv.Lines)) changedFields.Add(nameof(inv.Lines));
			if (beforeGrandTotal != inv.GrandTotal) changedFields.Add(nameof(inv.GrandTotal));

			await _events.RecordAsync(new BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.PurchaseInvoice,
				EntityId = inv.ID,
				EventType = CrossBuy.BL.Platform.PurchaseInvoiceEvents.Updated,
				PayloadVersion = PurchaseInvoiceEventPayload.Version,
				Visibility = BusinessEventVisibility.Internal,
				Payload = new PurchaseInvoiceEventPayload
				{
					InvoiceNumber = inv.InvoiceNo,
					SupplierId = inv.VendorId,
					SupplierName = ven.Name,
					InvoiceDate = inv.InvoiceDate,
					ChangedFields = changedFields.Count > 0 ? changedFields.ToArray() : null,
					OldStatus = beforeStatus,
					NewStatus = inv.Status,
					TotalBefore = beforeGrandTotal,
					TotalAfter = inv.GrandTotal,
				},
			});

			await tx.CommitAsync();
			return (true, null, inv);
		}

		// Platform Kernel: a stable fingerprint of the invoice lines, used only to decide whether "Lines"
		// belongs in an edit's changed-field list. Never stored in the event payload.
		private static string LineSignature(IEnumerable<PurchaseInvoiceLine> lines) => string.Join("|",
			lines.OrderBy(l => l.LineNo)
				 .Select(l => $"{l.ItemId}:{l.ItemDescription}:{l.Qty}:{l.UnitPrice}:{l.DiscountAmount}:{l.TaxRate}:{l.WarehouseId}"));

		// ---------------- P3-3b: Purchase returns / debit notes ----------------
		public async Task<List<PurchaseReturn>> GetPurchaseReturnsAsync(int companyId) =>
			await _context.PurchaseReturns.AsNoTracking().Where(r => r.CompanyID == companyId).OrderByDescending(r => r.ID).ToListAsync();

		public async Task<PurchaseReturn?> GetPurchaseReturnAsync(int companyId, int id) =>
			await _context.PurchaseReturns.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.ID == id && r.CompanyID == companyId);

		// Lines must reference items+warehouse. Valued at AVG cost (preserves 1103=stock). Stock-out posts Dr GRNI/Cr Inventory;
		// the debit note posts Dr AP / Cr GRNI / Cr VAT-input → GRNI nets to zero.
		public async Task<(bool ok, string? error, PurchaseReturn? ret)> CreatePurchaseReturnAsync(
			int companyId, int vendorId, int? originalInvoiceId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId)
		{
			var ven = await _context.Vendors.FirstOrDefaultAsync(v => v.ID == vendorId && v.CompanyID == companyId);
			if (ven == null) return (false, "Supplier not found", null);
			var itemLines = (lines ?? new()).Where(l => l.ItemId != null && l.WarehouseId != null && l.Qty > 0).ToList();
			if (itemLines.Count == 0) return (false, "The return must contain at least one item line (with a warehouse)", null);
			var grniAcc = await AccIdAsync(companyId, "210203");
			var vatIn = await AccIdAsync(companyId, "110401");
			if (grniAcc == null) return (false, "The invoices-not-received account (210203) does not exist", null);
			// HM-2: a purchase return is valued at the item's STORED COST (functional — from mv.TotalCost via StockService), NOT the
			// document/purchase amount (that would be HM-D16 territory). So all values are functional ⇒ round to the functional dp (Rf).
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);

			var ret = new PurchaseReturn { CompanyID = companyId, VendorId = vendorId, OriginalInvoiceId = originalInvoiceId, ReturnDate = date.Date, WarehouseId = itemLines[0].WarehouseId, Status = "Posted", Notes = notes, CreatedAt = DateTime.UtcNow };
			_context.PurchaseReturns.Add(ret);
			await _context.SaveChangesAsync();
			ret.ReturnNo = $"DN-{date:yyyy}-{ret.ID:D5}";

			int ln = 1; decimal costTotal = 0, vatTotal = 0;
			foreach (var l in itemLines)
			{
				// stock-OUT at cost → Dr GRNI / Cr Inventory (via the "PurchaseReturn" GL mapping)
				var (sok, serr, mv) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = -1,
					Qty = l.Qty, SourceType = "PurchaseReturn", SourceId = ret.ID, PostToGl = true, Notes = $"Purchase return {ret.ReturnNo}"
				}, userId?.ToString());
				if (!sok || mv == null)
				{
					_context.PurchaseReturns.Remove(ret); await _context.SaveChangesAsync();
					return (false, serr ?? "Could not issue the goods out of stock", null);
				}
				var lineCost = Rf(mv.TotalCost);
				var lineVat = Rf(lineCost * l.TaxRate / 100m);
				costTotal += lineCost; vatTotal += lineVat;
				var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == l.ItemId.Value);
				_context.PurchaseReturnLines.Add(new PurchaseReturnLine { PurchaseReturnId = ret.ID, LineNo = ln++, ItemId = l.ItemId.Value, ItemDescription = string.IsNullOrWhiteSpace(l.ItemDescription) ? (item?.Name ?? "") : l.ItemDescription, Qty = l.Qty, WarehouseId = l.WarehouseId.Value, TaxRate = l.TaxRate, UnitCost = l.Qty > 0 ? Rf(lineCost / l.Qty) : 0, LineTotal = lineCost });
			}
			ret.SubTotal = Rf(costTotal); ret.TaxTotal = Rf(vatTotal); ret.GrandTotal = Rf(costTotal + vatTotal);
			// functional-cost values ⇒ the base columns equal the document columns; ap_sub uses GrandTotalBase (= the single 2101 line value)
			ret.SubTotalBase = ret.SubTotal; ret.TaxTotalBase = ret.TaxTotal; ret.GrandTotalBase = ret.GrandTotal;
			await _context.SaveChangesAsync();

			// debit-note JE (all functional): Dr AP (grandBase) / Cr GRNI (cost) / Cr VAT-input (vat) — 2101 from the single GrandTotal
			var jlines = new List<JournalLineInput> { new() { AccountId = ven.ControlAccountId, Debit = ret.GrandTotal, Credit = 0, Description = $"إشعار مدين {ret.ReturnNo}", DescriptionEn = $"Debit note {ret.ReturnNo}" } };
			jlines.Add(new JournalLineInput { AccountId = grniAcc.Value, Debit = 0, Credit = Rf(costTotal), Description = "عكس استلام (GRNI)", DescriptionEn = "GRNI reversal" });
			if (vatTotal > 0 && vatIn != null)
				jlines.Add(new JournalLineInput { AccountId = vatIn.Value, Debit = 0, Credit = Rf(vatTotal), Description = "عكس ض.ق.م مدخلات", DescriptionEn = "Input VAT reversal" });
			else if (vatTotal > 0) { jlines[0].Debit = Rf(costTotal); ret.TaxTotal = 0; ret.GrandTotal = Rf(costTotal); ret.GrandTotalBase = ret.GrandTotal; await _context.SaveChangesAsync(); }

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "PurchaseReturn", SourceId = ret.ID,
				Description = $"إشعار مدين {ret.ReturnNo} - {ven.Name}", DescriptionEn = $"Debit note {ret.ReturnNo}", Lines = jlines,
			}, userId);
			if (!ok) return (false, err, null);   // stock already out; JE failed → report (caller can void)
			ret.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();
			return (true, null, ret);
		}

		// P3: EDIT a posted purchase return — reverse the original postings (stock subledger at exact cost + all GL via mirror entries), then re-post on the same row/number.
		public async Task<(bool ok, string? error, PurchaseReturn? ret)> EditPurchaseReturnAsync(
			int companyId, int returnId, int vendorId, int? originalInvoiceId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId)
		{
			var ret = await _context.PurchaseReturns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.ID == returnId && r.CompanyID == companyId);
			if (ret == null) return (false, "Return not found", null);
			if (ret.Status != "Posted") return (false, "A return that is not posted cannot be edited", null);
			var ven = await _context.Vendors.FirstOrDefaultAsync(v => v.ID == vendorId && v.CompanyID == companyId);
			if (ven == null) return (false, "Supplier not found", null);
			var itemLines = (lines ?? new()).Where(l => l.ItemId != null && l.WarehouseId != null && l.Qty > 0).ToList();
			if (itemLines.Count == 0) return (false, "The return must contain at least one item line (with a warehouse)", null);
			var grniAcc = await AccIdAsync(companyId, "210203");
			var vatIn = await AccIdAsync(companyId, "110401");
			if (grniAcc == null) return (false, "The invoices-not-received account (210203) does not exist", null);
			int __fdp = await _rounding.DecimalsAsync(companyId, null);   // HM-2: functional-cost values
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);

			// HM-1-أ ب-3: ONE ambient transaction wraps the whole reverse+repost so an edit is all-or-nothing.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			// (1) reverse original stock (goods had gone OUT → bring them back IN at the exact cost; GL reversed via the movement's own entry)
			var moves = await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.SourceType == "PurchaseReturn" && m.SourceId == returnId && m.Direction == -1)
				.OrderBy(m => m.ItemId).ThenBy(m => m.WarehouseId).ToListAsync();
			foreach (var m in moves)
			{
				var (rok, rerr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest {
					Date = date, ItemId = m.ItemId, WarehouseId = m.WarehouseId, Direction = 1,
					Qty = m.QtyBase, UnitCostInBase = m.UnitCost, PostToGl = false,
					SourceType = "PurchaseReturnEdit", SourceId = returnId, Notes = $"Reversal of return {ret.ReturnNo}"
				}, userId?.ToString());
				if (!rok) return (false, rerr ?? "Could not reverse the return's stock", null);
				if (m.JournalEntryId.HasValue) { var (jr, je, _) = await _journals.ReverseAsync(m.JournalEntryId.Value, userId, $"Edit of return {ret.ReturnNo}"); if (!jr) return (false, je ?? "Could not reverse the inventory entry", null); }
			}
			// (2) reverse the debit-note GL
			if (ret.JournalEntryId.HasValue) { var (jr2, je2, _) = await _journals.ReverseAsync(ret.JournalEntryId.Value, userId, $"Edit of return {ret.ReturnNo}"); if (!jr2) return (false, je2 ?? "Could not reverse the credit-note entry", null); }

			// (3) drop old lines
			_context.PurchaseReturnLines.RemoveRange(ret.Lines); ret.Lines.Clear();
			ret.VendorId = vendorId; ret.OriginalInvoiceId = originalInvoiceId; ret.ReturnDate = date.Date; ret.Notes = notes; ret.WarehouseId = itemLines[0].WarehouseId;
			await _context.SaveChangesAsync();

			// (4) re-post: stock OUT at cost (Dr GRNI / Cr Inventory) → build the value from the actual moving-avg cost
			int ln = 1; decimal costTotal = 0, vatTotal = 0;
			foreach (var l in itemLines)
			{
				var (sok, serr, mv) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = -1,
					Qty = l.Qty, SourceType = "PurchaseReturn", SourceId = ret.ID, PostToGl = true, Notes = $"Purchase return {ret.ReturnNo} (edited)"
				}, userId?.ToString());
				if (!sok || mv == null) return (false, serr ?? "Could not issue the goods out of stock", null);
				var lineCost = Rf(mv.TotalCost); var lineVat = Rf(lineCost * l.TaxRate / 100m);
				costTotal += lineCost; vatTotal += lineVat;
				var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == l.ItemId.Value);
				_context.PurchaseReturnLines.Add(new PurchaseReturnLine { PurchaseReturnId = ret.ID, LineNo = ln++, ItemId = l.ItemId.Value, ItemDescription = string.IsNullOrWhiteSpace(l.ItemDescription) ? (item?.Name ?? "") : l.ItemDescription, Qty = l.Qty, WarehouseId = l.WarehouseId.Value, TaxRate = l.TaxRate, UnitCost = l.Qty > 0 ? Rf(lineCost / l.Qty) : 0, LineTotal = lineCost });
			}
			ret.SubTotal = Rf(costTotal); ret.TaxTotal = Rf(vatTotal); ret.GrandTotal = Rf(costTotal + vatTotal);
			ret.SubTotalBase = ret.SubTotal; ret.TaxTotalBase = ret.TaxTotal; ret.GrandTotalBase = ret.GrandTotal;
			await _context.SaveChangesAsync();

			// (5) new debit-note JE (all functional): Dr AP (single GrandTotal) / Cr GRNI / Cr VAT-input
			var jlines = new List<JournalLineInput> { new() { AccountId = ven.ControlAccountId, Debit = ret.GrandTotal, Credit = 0, Description = $"إشعار مدين {ret.ReturnNo} (معدّل)", DescriptionEn = $"Debit note {ret.ReturnNo} (edited)" } };
			jlines.Add(new JournalLineInput { AccountId = grniAcc.Value, Debit = 0, Credit = Rf(costTotal), Description = "عكس استلام (GRNI)", DescriptionEn = "GRNI reversal" });
			if (vatTotal > 0 && vatIn != null)
				jlines.Add(new JournalLineInput { AccountId = vatIn.Value, Debit = 0, Credit = Rf(vatTotal), Description = "عكس ض.ق.م مدخلات", DescriptionEn = "Input VAT reversal" });
			else if (vatTotal > 0) { jlines[0].Debit = Rf(costTotal); ret.TaxTotal = 0; ret.GrandTotal = Rf(costTotal); ret.GrandTotalBase = ret.GrandTotal; await _context.SaveChangesAsync(); }
			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "PurchaseReturn", SourceId = ret.ID,
				Description = $"إشعار مدين {ret.ReturnNo} (معدّل) - {ven.Name}", DescriptionEn = $"Debit note {ret.ReturnNo} (edited)", Lines = jlines,
			}, userId);
			if (!ok) return (false, err, null);
			ret.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null, ret);
		}

		// projectId (optional, P6-ج): tags the payment's JE lines — used by subcontractor retention (cashAccountId=2105) to route
		// the withheld amount as a ProjectId-tagged settlement (Dr AP / Cr 2105). null = default behaviour.
		public async Task<(bool ok, string? error)> CreatePaymentAsync(int companyId, int vendorId, DateTime date, decimal amount, string method, int cashAccountId, string? notes, int? userId, decimal whtRate = 0, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
		{
			var ven = await _context.Vendors.FirstOrDefaultAsync(v => v.ID == vendorId && v.CompanyID == companyId);
			if (ven == null) return (false, "Supplier not found");
			if (amount <= 0) return (false, "The amount must be greater than zero");
			if (whtRate < 0 || whtRate > 100) return (false, "Invalid withholding and collection rate");
			// HM-1-أ (هـ): payment row + its JE + the JournalEntryId back-ref must be ATOMIC (own-or-join) — closes the standalone 2-commit gap.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);

			// Multi-Currency (1-5): `amount` is the gross liability settled in `cur`. FIFO-allocate to open same-currency
			// purchase invoices, clearing AP at each invoice's rate → rate diff vs the payment rate is realized FX.
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Buy"); rate = r; }
			// HM-2: document amounts round to document dp (Rd); base to functional (Rf). Like the receipt, the payment JE has no eligible
			// P&L line (cash/AP/WHT/FX all forbidden), so it balances by construction — fxNet absorbs the conversion sub-unit as realized FX.
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rd(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);

			// "amount" (gross, foreign) — WHT withheld, net paid in cash (both translated at the payment rate).
			var whtForeign = Rd(amount * whtRate / 100m);
			var netForeign = Rd(amount - whtForeign);
			var whtAcc = whtForeign > 0 ? await AccIdAsync(companyId, "210202") : null;   // Withholding Tax Payable
			if (whtForeign > 0 && whtAcc == null) return (false, "The withholding and collection tax account (210202) does not exist");

			var pay = new Payment { CompanyID = companyId, VendorId = vendorId, PaymentDate = date.Date, Amount = Rd(amount), Method = method, CashAccountId = cashAccountId, Status = "Posted", Notes = notes, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = rate };
			_context.Payments.Add(pay);
			await _context.SaveChangesAsync();
			pay.PaymentNo = $"PY-{date:yyyy}-{pay.ID:D5}";

			// FIFO allocation to open purchase invoices (same currency) → AP cleared at each invoice's rate
			var openInvoices = await (from i in _context.PurchaseInvoices.AsNoTracking()
									  where i.CompanyID == companyId && i.VendorId == vendorId && i.Status == "Posted" && (i.CurrencyId ?? functional) == cur
									  orderby i.InvoiceDate, i.ID
									  select new { i.ID, i.GrandTotal, i.ExchangeRate }).ToListAsync();
			var settledByInv = (await _context.PaymentAllocations.AsNoTracking().Where(a => a.CompanyID == companyId)
								.GroupBy(a => a.PurchaseInvoiceId).Select(g => new { Inv = g.Key, F = g.Sum(x => x.ForeignAmount) }).ToListAsync())
								.ToDictionary(x => x.Inv, x => x.F);

			decimal left = Rd(amount), apBaseTotal = 0m;
			var allocs = new List<PaymentAllocation>();
			foreach (var inv in openInvoices)
			{
				if (left <= 0) break;
				var remaining = Rd(inv.GrandTotal - (settledByInv.TryGetValue(inv.ID, out var s) ? s : 0m));   // document
				if (remaining <= 0) continue;
				var take = Math.Min(remaining, left);
				var invRate = (inv.ExchangeRate.HasValue && inv.ExchangeRate.Value > 0) ? inv.ExchangeRate.Value : 1m;
				var apBase = Rf(take * invRate);           // AP cleared at the INVOICE rate (functional) — matches GrandTotalBase
				apBaseTotal += apBase; left = Rd(left - take);
				allocs.Add(new PaymentAllocation { CompanyID = companyId, PaymentId = pay.ID, PurchaseInvoiceId = inv.ID, ForeignAmount = take, InvoiceRate = R4(invRate), PaymentRate = R4(rate), ApBase = apBase, FxDiff = apBase - Rf(take * rate), CreatedAt = DateTime.UtcNow });
			}
			if (left > 0) apBaseTotal += Rf(left * rate);   // unallocated (advance) → AP at payment rate, no FX

			decimal cashBase = Rf(netForeign * rate);
			decimal whtBase = Rf(whtForeign * rate);
			pay.AmountBase = apBaseTotal;                  // AP cleared = subledger basis (single source → the 2101 JE line)
			decimal fxNet = Rf(apBaseTotal - cashBase - whtBase);   // gain(+) when we settle for less base than the liability

			var lines = new List<JournalLineInput>
			{
				new() { AccountId = ven.ControlAccountId, Debit = apBaseTotal, Credit = 0, Description = "سداد مورد", DescriptionEn = "Vendor settlement", ProjectId = projectId },
				new() { AccountId = cashAccountId, Debit = 0, Credit = cashBase, Description = "دفع نقدي", DescriptionEn = "Cash payment", ProjectId = projectId },
			};
			if (whtBase > 0) lines.Add(new() { AccountId = whtAcc!.Value, Debit = 0, Credit = whtBase, Description = "ضريبة خصم وتحصيل مستحقة", DescriptionEn = "Withholding tax payable", ProjectId = projectId });
			if (fxNet != 0)
			{
				var fxAcc = await AccIdAsync(companyId, fxNet > 0 ? "4902" : "5902");
				if (fxAcc == null) { if (_context.Database.CurrentTransaction == null) { _context.Payments.Remove(pay); await _context.SaveChangesAsync(); } return (false,"The realised exchange-difference account (4902/5902) is not configured"); }
				if (fxNet > 0) lines.Add(new() { AccountId = fxAcc.Value, Debit = 0, Credit = fxNet, Description = "Realised exchange gain", ProjectId = projectId });
				else lines.Add(new() { AccountId = fxAcc.Value, Debit = -fxNet, Credit = 0, Description = "Realised exchange loss", ProjectId = projectId });
			}

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "Payment", SourceId = pay.ID, CurrencyId = cur,
				Description = $"سند دفع {pay.PaymentNo} - {ven.Name}", DescriptionEn = $"Payment {pay.PaymentNo}", Lines = lines,
			}, userId);
			if (!ok) { if (_context.Database.CurrentTransaction == null) { _context.Payments.Remove(pay); await _context.SaveChangesAsync(); } return (false,err); }
			pay.JournalEntryId = entry!.ID;
			if (allocs.Count > 0) _context.PaymentAllocations.AddRange(allocs);
			await _context.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<List<AgingRow>> AgingAsync(int companyId, DateTime asOf)
		{
			// culture-aware party name: English (NameEn) when UI is not Arabic, fall back to Arabic
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var vendors = await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == companyId).ToListAsync();
			var invoices = await _context.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").ToListAsync();
			var payments = await _context.Payments.AsNoTracking().Where(p => p.CompanyID == companyId && p.Status == "Posted").ToListAsync();

			var rows = new List<AgingRow>();
			foreach (var v in vendors)
			{
				var venInv = invoices.Where(i => i.VendorId == v.ID).OrderBy(i => i.InvoiceDate).ToList();
				if (venInv.Count == 0) continue;
				// Multi-Currency: age in the functional currency (base columns)
				var paid = payments.Where(p => p.VendorId == v.ID).Sum(p => p.AmountBase ?? p.Amount);
				var row = new AgingRow { PartyId = v.ID, Name = (isEn && !string.IsNullOrWhiteSpace(v.NameEn)) ? v.NameEn! : v.Name };
				foreach (var inv in venInv)
				{
					var open = inv.GrandTotalBase ?? inv.GrandTotal;
					if (paid > 0) { var used = Math.Min(paid, open); open -= used; paid -= used; }
					if (open <= 0) continue;
					var days = (asOf.Date - inv.InvoiceDate.Date).Days;
					if (days <= 30) row.Current += open;
					else if (days <= 60) row.D30 += open;
					else if (days <= 90) row.D60 += open;
					else row.D90 += open;
				}
				if (row.Total > 0) rows.Add(row);
			}
			return rows.OrderByDescending(r => r.Total).ToList();
		}
	}
}
