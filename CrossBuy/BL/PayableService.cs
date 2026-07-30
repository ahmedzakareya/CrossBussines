using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
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
		public PayableService(CrossDbContext context, IJournalEntryService journals, IStockService stock, INotificationService notify, ICurrencyService currency) { _context = context; _journals = journals; _stock = stock; _notify = notify; _currency = currency; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
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
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "اسم المورد مطلوب");
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
			if (ven == null) return (false, "المورد غير موجود", null);
			if (lines == null || lines.Count == 0) return (false, "الفاتورة يجب أن تحتوي على بند واحد على الأقل", null);

			var vatIn = await AccIdAsync(companyId, "110401");

			// Multi-Currency (1-3): document is in `cur`; books/GL/stock are in the branch functional currency.
			// rate = foreign→functional (EGP per unit when functional is EGP). Convert AT SOURCE → GL & stock stay base.
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Buy"); rate = r; }
			decimal ToBase(decimal foreignAmt) => R(foreignAmt * rate);

			var inv = new PurchaseInvoice { CompanyID = companyId, VendorId = vendorId, InvoiceDate = date.Date, Status = "Posted", Notes = notes, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = R4(rate), ProjectId = projectId };
			var ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lineTotal = R(l.Qty * l.UnitPrice - l.DiscountAmount);
				var lineTax = R(lineTotal * l.TaxRate / 100m);
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
			inv.SubTotal = R(sub); inv.TaxTotal = R(tax); inv.GrandTotal = R(sub + tax);
			// base totals: sum of per-line bases so the GL balances exactly in functional currency
			decimal subBase = inv.Lines.Sum(l => ToBase(l.LineTotal));
			decimal taxBase = inv.Lines.Sum(l => ToBase(R(l.LineTotal * l.TaxRate / 100m)));
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
				jlines.Add(new JournalLineInput { AccountId = vatIn.Value, Debit = taxBase, Credit = 0, ProjectId = projectId, Description = "ض.ق.م مدخلات" });
			jlines.Add(new JournalLineInput { AccountId = ven.ControlAccountId, Debit = 0, Credit = inv.GrandTotalBase.Value, ProjectId = projectId, Description = $"فاتورة شراء {inv.InvoiceNo}" });

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
				var unitCost = R(ToBase(l.LineTotal) / l.Qty);   // base cost net of discount, excl. tax
				var (sok, serr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = 1,
					Qty = l.Qty, UnitCostInBase = unitCost, SourceType = "PurchaseInvoice", SourceId = inv.ID, SourceLineId = l.ID,
					PostToGl = false, Notes = $"استلام فاتورة شراء {inv.InvoiceNo}"
				}, userId?.ToString());
				if (!sok) return (false, serr ?? "تعذّر استلام المخزون", null);
			}
			await tx.CommitAsync();
			try
			{
				await _notify.NotifyRoleAsync(companyId, "acc", new[] { "ChiefAccountant" },
					"فاتورة شراء جديدة", "New purchase invoice",
					$"سُجّلت فاتورة شراء {inv.InvoiceNo} من المورد {ven.Name} بقيمة {inv.GrandTotal:N2}", $"Purchase invoice {inv.InvoiceNo} from {ven.Name} recorded ({inv.GrandTotal:N2})",
					"purchase_invoice", inv.ID);
			}
			catch { /* notifications never block the business flow */ }
			return (true, null, inv);
		}

		// P3: EDIT a posted purchase invoice — reverse the original postings (stock at the EXACT received cost), then re-post on the same row/number.
		public async Task<(bool ok, string? error, PurchaseInvoice? inv)> EditPurchaseInvoiceAsync(
			int companyId, int invoiceId, int vendorId, DateTime date, List<PurchaseLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
		{
			var inv = await _context.PurchaseInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.ID == invoiceId && i.CompanyID == companyId);
			if (inv == null) return (false, "الفاتورة غير موجودة", null);
			if (inv.Status != "Posted") return (false, "لا يمكن تعديل فاتورة غير مُرحّلة أو ملغاة", null);
			if (await _context.PaymentAllocations.AsNoTracking().AnyAsync(a => a.CompanyID == companyId && a.PurchaseInvoiceId == invoiceId))
				return (false, "لا يمكن تعديل الفاتورة لوجود سداد مخصّص عليها — ألغِ التخصيص أولًا", null);
			var ven = await _context.Vendors.FirstOrDefaultAsync(v => v.ID == vendorId && v.CompanyID == companyId);
			if (ven == null) return (false, "المورد غير موجود", null);
			if (lines == null || lines.Count == 0) return (false, "الفاتورة يجب أن تحتوي على بند واحد على الأقل", null);

			var vatIn = await AccIdAsync(companyId, "110401");
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Buy"); rate = r; }
			decimal ToBase(decimal foreignAmt) => R(foreignAmt * rate);

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
					SourceType = "PurchaseInvoiceEdit", SourceId = invoiceId, Notes = $"عكس استلام تعديل فاتورة {inv.InvoiceNo}"
				}, userId?.ToString());
				if (!rok) return (false, rerr ?? "تعذّر عكس استلام المخزون", null);
			}
			// (2) reverse the original GL entry (mirror; inventory/expense + VAT + AP all back out at the original amounts)
			if (inv.JournalEntryId.HasValue)
			{
				var (rjok, rjerr, _) = await _journals.ReverseAsync(inv.JournalEntryId.Value, userId, $"تعديل فاتورة {inv.InvoiceNo}");
				if (!rjok) return (false, rjerr ?? "تعذّر عكس قيد الفاتورة", null);
			}

			// (3) drop old lines
			_context.PurchaseInvoiceLines.RemoveRange(inv.Lines);
			inv.Lines.Clear();

			// (4) recompute + update the invoice row (keep ID + InvoiceNo)
			var ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lineTotal = R(l.Qty * l.UnitPrice - l.DiscountAmount);
				var lineTax = R(lineTotal * l.TaxRate / 100m);
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
			decimal taxBase = inv.Lines.Sum(l => ToBase(R(l.LineTotal * l.TaxRate / 100m)));
			inv.VendorId = vendorId; inv.InvoiceDate = date.Date; inv.Notes = notes; inv.CurrencyId = cur; inv.ExchangeRate = R4(rate); inv.ProjectId = projectId;
			inv.SubTotal = R(sub); inv.TaxTotal = R(tax); inv.GrandTotal = R(sub + tax);
			inv.SubTotalBase = subBase; inv.TaxTotalBase = taxBase; inv.GrandTotalBase = subBase + taxBase;
			await _context.SaveChangesAsync();

			// (5) new GL entry
			var jlines = new List<JournalLineInput>();
			foreach (var l in inv.Lines)
				jlines.Add(new JournalLineInput { AccountId = l.ExpenseAccountId, Debit = ToBase(l.LineTotal), Credit = 0, CostCenterId = l.CostCenterId, ProjectId = projectId, Description = l.ItemDescription });
			if (taxBase > 0 && vatIn != null)
				jlines.Add(new JournalLineInput { AccountId = vatIn.Value, Debit = taxBase, Credit = 0, ProjectId = projectId, Description = "ض.ق.م مدخلات" });
			jlines.Add(new JournalLineInput { AccountId = ven.ControlAccountId, Debit = 0, Credit = inv.GrandTotalBase.Value, ProjectId = projectId, Description = $"فاتورة شراء {inv.InvoiceNo} (معدّلة)" });
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
				var unitCost = R(ToBase(l.LineTotal) / l.Qty);
				var (sok, serr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = 1,
					Qty = l.Qty, UnitCostInBase = unitCost, SourceType = "PurchaseInvoice", SourceId = inv.ID, SourceLineId = l.ID,
					PostToGl = false, Notes = $"استلام فاتورة شراء {inv.InvoiceNo} (معدّلة)"
				}, userId?.ToString());
				if (!sok) return (false, serr ?? "تعذّر استلام المخزون", null);
			}
			await tx.CommitAsync();
			return (true, null, inv);
		}

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
			if (ven == null) return (false, "المورد غير موجود", null);
			var itemLines = (lines ?? new()).Where(l => l.ItemId != null && l.WarehouseId != null && l.Qty > 0).ToList();
			if (itemLines.Count == 0) return (false, "المرتجع يجب أن يحتوي على بند صنف واحد على الأقل (مع المخزن)", null);
			var grniAcc = await AccIdAsync(companyId, "210203");
			var vatIn = await AccIdAsync(companyId, "110401");
			if (grniAcc == null) return (false, "حساب فواتير لم ترد (210203) غير موجود", null);

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
					Qty = l.Qty, SourceType = "PurchaseReturn", SourceId = ret.ID, PostToGl = true, Notes = $"مرتجع شراء {ret.ReturnNo}"
				}, userId?.ToString());
				if (!sok || mv == null)
				{
					_context.PurchaseReturns.Remove(ret); await _context.SaveChangesAsync();
					return (false, serr ?? "تعذّر إخراج البضاعة من المخزون", null);
				}
				var lineCost = R(mv.TotalCost);
				var lineVat = R(lineCost * l.TaxRate / 100m);
				costTotal += lineCost; vatTotal += lineVat;
				var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == l.ItemId.Value);
				_context.PurchaseReturnLines.Add(new PurchaseReturnLine { PurchaseReturnId = ret.ID, LineNo = ln++, ItemId = l.ItemId.Value, ItemDescription = string.IsNullOrWhiteSpace(l.ItemDescription) ? (item?.Name ?? "") : l.ItemDescription, Qty = l.Qty, WarehouseId = l.WarehouseId.Value, TaxRate = l.TaxRate, UnitCost = l.Qty > 0 ? R(lineCost / l.Qty) : 0, LineTotal = lineCost });
			}
			ret.SubTotal = R(costTotal); ret.TaxTotal = R(vatTotal); ret.GrandTotal = R(costTotal + vatTotal);
			await _context.SaveChangesAsync();

			// debit-note JE: Dr AP (cost+vat) / Cr GRNI (cost) / Cr VAT-input (vat)
			var jlines = new List<JournalLineInput> { new() { AccountId = ven.ControlAccountId, Debit = ret.GrandTotal, Credit = 0, Description = $"إشعار مدين {ret.ReturnNo}", DescriptionEn = $"Debit note {ret.ReturnNo}" } };
			jlines.Add(new JournalLineInput { AccountId = grniAcc.Value, Debit = 0, Credit = R(costTotal), Description = "عكس استلام (GRNI)", DescriptionEn = "GRNI reversal" });
			if (vatTotal > 0 && vatIn != null)
				jlines.Add(new JournalLineInput { AccountId = vatIn.Value, Debit = 0, Credit = R(vatTotal), Description = "عكس ض.ق.م مدخلات", DescriptionEn = "Input VAT reversal" });
			else if (vatTotal > 0) { jlines[0].Debit = R(costTotal); ret.TaxTotal = 0; ret.GrandTotal = R(costTotal); await _context.SaveChangesAsync(); }

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
			if (ret == null) return (false, "المرتجع غير موجود", null);
			if (ret.Status != "Posted") return (false, "لا يمكن تعديل مرتجع غير مُرحّل", null);
			var ven = await _context.Vendors.FirstOrDefaultAsync(v => v.ID == vendorId && v.CompanyID == companyId);
			if (ven == null) return (false, "المورد غير موجود", null);
			var itemLines = (lines ?? new()).Where(l => l.ItemId != null && l.WarehouseId != null && l.Qty > 0).ToList();
			if (itemLines.Count == 0) return (false, "المرتجع يجب أن يحتوي على بند صنف واحد على الأقل (مع المخزن)", null);
			var grniAcc = await AccIdAsync(companyId, "210203");
			var vatIn = await AccIdAsync(companyId, "110401");
			if (grniAcc == null) return (false, "حساب فواتير لم ترد (210203) غير موجود", null);

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
					SourceType = "PurchaseReturnEdit", SourceId = returnId, Notes = $"عكس مرتجع {ret.ReturnNo}"
				}, userId?.ToString());
				if (!rok) return (false, rerr ?? "تعذّر عكس مخزون المرتجع", null);
				if (m.JournalEntryId.HasValue) { var (jr, je, _) = await _journals.ReverseAsync(m.JournalEntryId.Value, userId, $"تعديل مرتجع {ret.ReturnNo}"); if (!jr) return (false, je ?? "تعذّر عكس قيد المخزون", null); }
			}
			// (2) reverse the debit-note GL
			if (ret.JournalEntryId.HasValue) { var (jr2, je2, _) = await _journals.ReverseAsync(ret.JournalEntryId.Value, userId, $"تعديل مرتجع {ret.ReturnNo}"); if (!jr2) return (false, je2 ?? "تعذّر عكس قيد الإشعار", null); }

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
					Qty = l.Qty, SourceType = "PurchaseReturn", SourceId = ret.ID, PostToGl = true, Notes = $"مرتجع شراء {ret.ReturnNo} (معدّل)"
				}, userId?.ToString());
				if (!sok || mv == null) return (false, serr ?? "تعذّر إخراج البضاعة من المخزون", null);
				var lineCost = R(mv.TotalCost); var lineVat = R(lineCost * l.TaxRate / 100m);
				costTotal += lineCost; vatTotal += lineVat;
				var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == l.ItemId.Value);
				_context.PurchaseReturnLines.Add(new PurchaseReturnLine { PurchaseReturnId = ret.ID, LineNo = ln++, ItemId = l.ItemId.Value, ItemDescription = string.IsNullOrWhiteSpace(l.ItemDescription) ? (item?.Name ?? "") : l.ItemDescription, Qty = l.Qty, WarehouseId = l.WarehouseId.Value, TaxRate = l.TaxRate, UnitCost = l.Qty > 0 ? R(lineCost / l.Qty) : 0, LineTotal = lineCost });
			}
			ret.SubTotal = R(costTotal); ret.TaxTotal = R(vatTotal); ret.GrandTotal = R(costTotal + vatTotal);
			await _context.SaveChangesAsync();

			// (5) new debit-note JE: Dr AP / Cr GRNI / Cr VAT-input
			var jlines = new List<JournalLineInput> { new() { AccountId = ven.ControlAccountId, Debit = ret.GrandTotal, Credit = 0, Description = $"إشعار مدين {ret.ReturnNo} (معدّل)", DescriptionEn = $"Debit note {ret.ReturnNo} (edited)" } };
			jlines.Add(new JournalLineInput { AccountId = grniAcc.Value, Debit = 0, Credit = R(costTotal), Description = "عكس استلام (GRNI)", DescriptionEn = "GRNI reversal" });
			if (vatTotal > 0 && vatIn != null)
				jlines.Add(new JournalLineInput { AccountId = vatIn.Value, Debit = 0, Credit = R(vatTotal), Description = "عكس ض.ق.م مدخلات", DescriptionEn = "Input VAT reversal" });
			else if (vatTotal > 0) { jlines[0].Debit = R(costTotal); ret.TaxTotal = 0; ret.GrandTotal = R(costTotal); await _context.SaveChangesAsync(); }
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
			if (ven == null) return (false, "المورد غير موجود");
			if (amount <= 0) return (false, "المبلغ يجب أن يكون أكبر من صفر");
			if (whtRate < 0 || whtRate > 100) return (false, "نسبة الخصم والتحصيل غير صحيحة");
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

			// "amount" (gross, foreign) — WHT withheld, net paid in cash (both translated at the payment rate).
			var whtForeign = R(amount * whtRate / 100m);
			var netForeign = R(amount - whtForeign);
			var whtAcc = whtForeign > 0 ? await AccIdAsync(companyId, "210202") : null;   // Withholding Tax Payable
			if (whtForeign > 0 && whtAcc == null) return (false, "حساب ضريبة الخصم والتحصيل (210202) غير موجود");

			var pay = new Payment { CompanyID = companyId, VendorId = vendorId, PaymentDate = date.Date, Amount = R(amount), Method = method, CashAccountId = cashAccountId, Status = "Posted", Notes = notes, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = R4(rate) };
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

			decimal left = R(amount), apBaseTotal = 0m;
			var allocs = new List<PaymentAllocation>();
			foreach (var inv in openInvoices)
			{
				if (left <= 0) break;
				var remaining = R(inv.GrandTotal - (settledByInv.TryGetValue(inv.ID, out var s) ? s : 0m));
				if (remaining <= 0) continue;
				var take = Math.Min(remaining, left);
				var invRate = (inv.ExchangeRate.HasValue && inv.ExchangeRate.Value > 0) ? inv.ExchangeRate.Value : 1m;
				var apBase = R(take * invRate);
				apBaseTotal += apBase; left = R(left - take);
				allocs.Add(new PaymentAllocation { CompanyID = companyId, PaymentId = pay.ID, PurchaseInvoiceId = inv.ID, ForeignAmount = take, InvoiceRate = R4(invRate), PaymentRate = R4(rate), ApBase = apBase, FxDiff = apBase - R(take * rate), CreatedAt = DateTime.UtcNow });
			}
			if (left > 0) apBaseTotal += R(left * rate);   // unallocated (advance) → AP at payment rate, no FX

			decimal cashBase = R(netForeign * rate);
			decimal whtBase = R(whtForeign * rate);
			pay.AmountBase = apBaseTotal;                  // AP cleared = subledger basis
			decimal fxNet = R(apBaseTotal - cashBase - whtBase);   // gain(+) when we settle for less base than the liability

			var lines = new List<JournalLineInput>
			{
				new() { AccountId = ven.ControlAccountId, Debit = apBaseTotal, Credit = 0, Description = "سداد مورد", DescriptionEn = "Vendor settlement", ProjectId = projectId },
				new() { AccountId = cashAccountId, Debit = 0, Credit = cashBase, Description = "دفع نقدي", DescriptionEn = "Cash payment", ProjectId = projectId },
			};
			if (whtBase > 0) lines.Add(new() { AccountId = whtAcc!.Value, Debit = 0, Credit = whtBase, Description = "ضريبة خصم وتحصيل مستحقة", DescriptionEn = "Withholding tax payable", ProjectId = projectId });
			if (fxNet != 0)
			{
				var fxAcc = await AccIdAsync(companyId, fxNet > 0 ? "4902" : "5902");
				if (fxAcc == null) { if (_context.Database.CurrentTransaction == null) { _context.Payments.Remove(pay); await _context.SaveChangesAsync(); } return (false,"حساب فروق العملة المحققة (4902/5902) غير مُهيّأ"); }
				if (fxNet > 0) lines.Add(new() { AccountId = fxAcc.Value, Debit = 0, Credit = fxNet, Description = "ربح فرق عملة محقق", ProjectId = projectId });
				else lines.Add(new() { AccountId = fxAcc.Value, Debit = -fxNet, Credit = 0, Description = "خسارة فرق عملة محققة", ProjectId = projectId });
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
