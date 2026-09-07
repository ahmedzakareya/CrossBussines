using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class SalesLineInput
	{
		public string ItemDescription { get; set; } = "";
		public decimal Qty { get; set; } = 1;
		public decimal UnitPrice { get; set; }
		public decimal DiscountAmount { get; set; }
		public decimal TaxRate { get; set; }
		public int RevenueAccountId { get; set; }
		public int? ItemId { get; set; }          // when set → stock-out + COGS on post
		public int? WarehouseId { get; set; }
		public int? UoMId { get; set; }           // HM-2: sold unit (null = base) — carried to the stock movement so qty converts to base
	}

	public class AgingRow
	{
		public int PartyId { get; set; }
		public string Name { get; set; } = "";
		public decimal Current { get; set; }   // 0-30
		public decimal D30 { get; set; }        // 31-60
		public decimal D60 { get; set; }        // 61-90
		public decimal D90 { get; set; }        // 90+
		public decimal Total => Current + D30 + D60 + D90;
	}

	// ---- 2.8 customer analytics ----
	public class CustomerAnalyticsRow
	{
		public int CustomerId { get; set; }
		public string Name { get; set; } = "";
		public string? Segment { get; set; }
		public decimal Invoiced { get; set; }      // posted sales (incl tax)
		public decimal Received { get; set; }
		public decimal Returns { get; set; }
		public decimal Outstanding { get; set; }   // Invoiced − Received − Returns
		public decimal Revenue { get; set; }       // net of tax & returns (margin base)
		public decimal Cogs { get; set; }
		public decimal Margin { get; set; }         // Revenue − Cogs
		public decimal MarginPct { get; set; }
		public int InvoiceCount { get; set; }
		public DateTime? FirstInvoice { get; set; }
		public DateTime? LastInvoice { get; set; }
		public decimal AvgInvoice { get; set; }
	}
	public class SegmentRollup
	{
		public string Segment { get; set; } = "";
		public int Customers { get; set; }
		public decimal Revenue { get; set; }
		public decimal Margin { get; set; }
		public decimal Outstanding { get; set; }
	}
	public class CustomerAnalytics
	{
		public List<CustomerAnalyticsRow> Rows { get; set; } = new();
		public List<SegmentRollup> Segments { get; set; } = new();
		public decimal TotalRevenue { get; set; }
		public decimal TotalMargin { get; set; }
		public decimal TotalOutstanding { get; set; }
		public int ActiveCustomers { get; set; }     // customers with ≥1 posted invoice
	}

	public interface IReceivableService
	{
		Task<List<Customer>> GetCustomersAsync(int companyId);
		Task<(List<Customer> rows, int total)> SearchCustomersAsync(int companyId, string? q, bool? active, int page, int pageSize);
		Task<List<(string value, string name)>> SuggestCustomersAsync(int companyId, string? term, int take = 10);
		Task<Customer> CreateCustomerAsync(int companyId, string name, string? nameEn, string? taxNo, decimal? creditLimit);
		Task<(bool ok, string? error)> SaveCustomerAsync(int companyId, Customer dto);
		Task<(bool ok, string? error, SalesInvoice? inv)> CreateSalesInvoiceAsync(int companyId, int customerId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null);
		// P3: edit a POSTED sales invoice = reverse the original GL + stock effects, then re-post the new values, keeping the same invoice number.
		Task<(bool ok, string? error, SalesInvoice? inv)> EditSalesInvoiceAsync(int companyId, int invoiceId, int customerId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null);
		Task<(bool ok, string? error)> CreateReceiptAsync(int companyId, int customerId, DateTime date, decimal amount, string method, int cashAccountId, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null);
		Task<List<SalesInvoice>> GetInvoicesAsync(int companyId);
		Task<decimal> CustomerOutstandingAsync(int companyId, int customerId);
		Task<List<AgingRow>> AgingAsync(int companyId, DateTime asOf);
		Task<CustomerAnalytics> GetCustomerAnalyticsAsync(int companyId);   // 2.8 customer profitability / LTV / segments
		// P3-3a: sales returns / credit notes
		Task<List<SalesReturn>> GetSalesReturnsAsync(int companyId);
		Task<SalesReturn?> GetSalesReturnAsync(int companyId, int id);
		Task<(bool ok, string? error, SalesReturn? ret)> CreateSalesReturnAsync(int companyId, int customerId, int? originalInvoiceId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null);
		Task<(bool ok, string? error, SalesReturn? ret)> EditSalesReturnAsync(int companyId, int returnId, int customerId, int? originalInvoiceId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null);
	}

	public class ReceivableService : IReceivableService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		private readonly IStockService _stock;
		private readonly INotificationService _notify;
		private readonly ICurrencyService _currency;
		private readonly ICurrencyRounding _rounding;
		private readonly CrossBuy.BL.Platform.IBusinessEventService _events;   // Platform Kernel: durable business facts (in-transaction)
		private readonly Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> L;
		public ReceivableService(CrossDbContext context, IJournalEntryService journals, IStockService stock, INotificationService notify, ICurrencyService currency, ICurrencyRounding rounding, CrossBuy.BL.Platform.IBusinessEventService events, Microsoft.Extensions.Localization.IStringLocalizer<CrossBuy.SharedResources> localizer) { _context = context; _journals = journals; _stock = stock; _notify = notify; _currency = currency; _rounding = rounding; _events = events; L = localizer; }

		private static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

		private async Task<int?> AccIdAsync(int companyId, string code) =>
			await _context.Accounts.Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		public async Task<List<Customer>> GetCustomersAsync(int companyId) =>
			await _context.Customers.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.Name).ToListAsync();

		// server-side paged customer search (Tagify multi-tag OR over name/tax/phone/segment/contact/email)
		public async Task<(List<Customer> rows, int total)> SearchCustomersAsync(int companyId, string? q, bool? active, int page, int pageSize)
		{
			var query = _context.Customers.AsNoTracking().Where(c => c.CompanyID == companyId);
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Customer>(terms, s =>
					c => c.Name.Contains(s) || (c.NameEn != null && c.NameEn.Contains(s)) || (c.TaxRegNo != null && c.TaxRegNo.Contains(s))
						|| (c.Phone != null && c.Phone.Contains(s)) || (c.Segment != null && c.Segment.Contains(s))
						|| (c.ContactPerson != null && c.ContactPerson.Contains(s)) || (c.Email != null && c.Email.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (active.HasValue) query = query.Where(c => c.IsActive == active.Value);
			var total = await query.CountAsync();
			if (page < 1) page = 1;
			if (pageSize < 1) pageSize = 25; else if (pageSize > 100000) pageSize = 100000;   // high ceiling allows full-set export
			var rows = await query.OrderBy(c => c.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		public async Task<List<(string value, string name)>> SuggestCustomersAsync(int companyId, string? term, int take = 10)
		{
			var t = (term ?? "").Trim();
			var query = _context.Customers.AsNoTracking().Where(c => c.CompanyID == companyId && c.IsActive);
			if (t.Length > 0)
				query = query.Where(c => c.Name.Contains(t) || (c.NameEn != null && c.NameEn.Contains(t))
					|| (c.TaxRegNo != null && c.TaxRegNo.Contains(t)) || (c.Phone != null && c.Phone.Contains(t)) || (c.Segment != null && c.Segment.Contains(t)));
			var rows = await query.OrderBy(c => c.Name).Take(take <= 0 ? 10 : take)
				.Select(c => new { c.Name, c.Segment, c.Phone }).ToListAsync();
			return rows.Select(c => (c.Name, c.Segment ?? c.Phone ?? "")).ToList();
		}

		// Platform Kernel slice 2: the customer write paths had NO transaction — a single SaveChanges is atomic
		// on its own, so none was needed. Recording an event needs one (ADR-001: the fact and its event must
		// share a fate), so both paths now open a ScopedTx. BeginOrJoinAsync JOINS an ambient transaction when
		// there is one and owns a new one otherwise, so a caller with no transaction behaves exactly as before.
		public async Task<Customer> CreateCustomerAsync(int companyId, string name, string? nameEn, string? taxNo, decimal? creditLimit)
		{
			var control = await AccIdAsync(companyId, "1102") ?? 0;   // AR control
			var c = new Customer { CompanyID = companyId, Name = name, NameEn = nameEn, TaxRegNo = taxNo, ControlAccountId = control, CreditLimit = creditLimit, IsActive = true, CreatedAt = DateTime.UtcNow };

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			_context.Customers.Add(c);
			await _context.SaveChangesAsync();

			await _events.RecordAsync(new BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.Customer,
				EntityId = c.ID,
				EventType = CrossBuy.BL.Platform.CustomerEvents.Created,
				PayloadVersion = CustomerEventPayload.Version,
				Visibility = BusinessEventVisibility.Internal,
				// The quick-add is fired from a document screen and is a realistic double-submit target
				// (double-clicked button / retried POST), so one Created per customer row is pinned.
				DedupKey = $"Customer.Created:{c.ID}",
				Payload = new CustomerEventPayload
				{
					CustomerCode = c.TaxRegNo,
					CustomerName = c.Name,
					CustomerType = c.Segment,
					InitialStatus = ActiveStatus(c.IsActive),
					NewStatus = ActiveStatus(c.IsActive),
				},
			});

			await tx.CommitAsync();
			return c;
		}

		public async Task<(bool ok, string? error)> SaveCustomerAsync(int companyId, Customer dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name)) return (false, "Customer name is required");
			var c = dto.ID > 0 ? await _context.Customers.FirstOrDefaultAsync(x => x.ID == dto.ID && x.CompanyID == companyId) : null;
			bool isNew = c == null;

			// Snapshot BEFORE mutation so the update event can carry a change summary. Field NAMES only — the
			// values are deliberately not carried, because several of them are private contact details.
			string? beforeName = c?.Name, beforeNameEn = c?.NameEn, beforeTaxNo = c?.TaxRegNo, beforeSegment = c?.Segment;
			string? beforeAddress = c?.Address, beforeShipping = c?.ShippingAddress, beforePhone = c?.Phone;
			string? beforeEmail = c?.Email, beforeContact = c?.ContactPerson;
			decimal? beforeCreditLimit = c?.CreditLimit;
			int? beforeTerms = c?.PaymentTermsDays;
			bool? beforeActive = c?.IsActive;

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			if (c == null)
			{
				c = new Customer { CompanyID = companyId, ControlAccountId = await AccIdAsync(companyId, "1102") ?? 0, IsActive = true, CreatedAt = DateTime.UtcNow };
				_context.Customers.Add(c);
			}
			c.Name = dto.Name; c.NameEn = dto.NameEn; c.TaxRegNo = dto.TaxRegNo; c.CreditLimit = dto.CreditLimit;
			c.PaymentTermsDays = dto.PaymentTermsDays; c.Address = dto.Address; c.ShippingAddress = dto.ShippingAddress;
			c.Phone = dto.Phone; c.Email = dto.Email; c.ContactPerson = dto.ContactPerson; c.Segment = dto.Segment;
			c.IsActive = dto.IsActive;
			await _context.SaveChangesAsync();

			// This method is an UPSERT: the same screen creates and edits, so which event fires depends on
			// whether a row was found — not on which method was called.
			if (isNew)
			{
				await _events.RecordAsync(new BusinessEventRecord
				{
					EntityCode = CrossBuy.BL.Platform.EntityRegistry.Customer,
					EntityId = c.ID,
					EventType = CrossBuy.BL.Platform.CustomerEvents.Created,
					PayloadVersion = CustomerEventPayload.Version,
					Visibility = BusinessEventVisibility.Internal,
					DedupKey = $"Customer.Created:{c.ID}",
					Payload = new CustomerEventPayload
					{
						CustomerCode = c.TaxRegNo, CustomerName = c.Name, CustomerType = c.Segment,
						InitialStatus = ActiveStatus(c.IsActive), NewStatus = ActiveStatus(c.IsActive),
					},
				});
			}
			else
			{
				var changed = new List<string>();
				if (beforeName != c.Name) changed.Add(nameof(c.Name));
				if (beforeNameEn != c.NameEn) changed.Add(nameof(c.NameEn));
				if (beforeTaxNo != c.TaxRegNo) changed.Add(nameof(c.TaxRegNo));
				if (beforeCreditLimit != c.CreditLimit) changed.Add(nameof(c.CreditLimit));
				if (beforeTerms != c.PaymentTermsDays) changed.Add(nameof(c.PaymentTermsDays));
				if (beforeSegment != c.Segment) changed.Add(nameof(c.Segment));
				if (beforeAddress != c.Address) changed.Add(nameof(c.Address));
				if (beforeShipping != c.ShippingAddress) changed.Add(nameof(c.ShippingAddress));
				if (beforePhone != c.Phone) changed.Add(nameof(c.Phone));
				if (beforeEmail != c.Email) changed.Add(nameof(c.Email));
				if (beforeContact != c.ContactPerson) changed.Add(nameof(c.ContactPerson));
				if (beforeActive != c.IsActive) changed.Add(nameof(c.IsActive));

				// No DedupKey: a customer may legitimately be edited many times, and each edit is its own fact.
				await _events.RecordAsync(new BusinessEventRecord
				{
					EntityCode = CrossBuy.BL.Platform.EntityRegistry.Customer,
					EntityId = c.ID,
					EventType = CrossBuy.BL.Platform.CustomerEvents.Updated,
					PayloadVersion = CustomerEventPayload.Version,
					Visibility = BusinessEventVisibility.Internal,
					Payload = new CustomerEventPayload
					{
						CustomerCode = c.TaxRegNo,
						CustomerName = c.Name,
						CustomerType = c.Segment,
						ChangedFields = changed.Count > 0 ? changed.ToArray() : null,
						// There is no status column on Customer and no dedicated activate/deactivate operation,
						// so an IsActive flip is reported HERE rather than as a Customer.StatusChanged event
						// that would name a transition the code does not have.
						OldStatus = beforeActive.HasValue ? ActiveStatus(beforeActive.Value) : null,
						NewStatus = ActiveStatus(c.IsActive),
					},
				});
			}

			await tx.CommitAsync();
			return (true, null);
		}

		// Customer has no status column; IsActive is the only state it carries.
		private static string ActiveStatus(bool isActive) => isActive ? "Active" : "Inactive";

		public async Task<List<SalesInvoice>> GetInvoicesAsync(int companyId) =>
			await _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == companyId).OrderByDescending(i => i.ID).ToListAsync();

		// P3-4: customer's current outstanding receivable = posted invoices − receipts − credit notes (returns)
		public async Task<decimal> CustomerOutstandingAsync(int companyId, int customerId)
		{
			// Multi-Currency: outstanding is measured in the functional currency (use *Base, fall back to source for legacy rows)
			var inv = await _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.CustomerId == customerId && i.Status == "Posted").SumAsync(i => (decimal?)(i.GrandTotalBase ?? i.GrandTotal)) ?? 0m;
			var rcpt = await _context.Receipts.AsNoTracking().Where(r => r.CompanyID == companyId && r.CustomerId == customerId && r.Status == "Posted").SumAsync(r => (decimal?)(r.AmountBase ?? r.Amount)) ?? 0m;
			var ret = await _context.SalesReturns.AsNoTracking().Where(s => s.CompanyID == companyId && s.CustomerId == customerId && s.Status == "Posted").SumAsync(s => (decimal?)(s.GrandTotalBase ?? s.GrandTotal)) ?? 0m;
			return await _rounding.RoundAsync(companyId, inv - rcpt - ret, null);   // HM-2 Batch 5: functional-currency round via the central helper (no static R)
		}

		public async Task<(bool ok, string? error, SalesInvoice? inv)> CreateSalesInvoiceAsync(
			int companyId, int customerId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
		{
			var cust = await _context.Customers.FirstOrDefaultAsync(c => c.ID == customerId && c.CompanyID == companyId);
			if (cust == null) return (false, "Customer not found", null);
			if (lines == null || lines.Count == 0) return (false, "The invoice must contain at least one line", null);

			var vatOut = await AccIdAsync(companyId, "210201");

			// Multi-Currency (1-4): document in `cur`; revenue/VAT/AR posted in branch functional currency. Sell rate (collection).
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Sell"); rate = r; }
			// HM-D23: rate was LOOKED UP (not caller-supplied) and older than RateMaxAgeDays vs the DOCUMENT date ⇒ Reject (blocks, zero
			// effect) or Warn (proceed + on-screen notification + counted in inv-test-integrity), per the company's RateStaleBehavior.
			if (cur != functional && !exchangeRate.HasValue)
			{
				var (stale, ageDays, _maxAge, behavior) = await _currency.RateStalenessAsync(companyId, cur, date);
				if (stale)
				{
					if (behavior == "Reject")
						return (false, L["The exchange rate is {0} days old — today's rate is not entered. Ask the branch accountant to enter it in the Exchange Rates screen, then retry.", ageDays], null);
					System.Threading.Interlocked.Increment(ref CrossBuy.BL.CurrencyService.StaleRateSales);
					try { await _notify.NotifyRoleAsync(companyId, "acc", new[] { "Accountant", "ChiefAccountant" }, "بيع بسعر صرف بائت", "Sale at a stale rate", $"تمّ بيع بسعر صرف عمره {ageDays} يومًا — يرجى تحديث سعر اليوم من شاشة أسعار الصرف.", $"A sale posted at an exchange rate {ageDays} days old — update today's rate in the Exchange Rates screen.", "ExchangeRate", null); } catch { }
				}
			}
			// HM-2: document totals round to the DOCUMENT currency (Rd); stored base totals round to the FUNCTIONAL currency (Rf).
			// ToBase stays RAW — JournalEntryService rounds the JE lines to functional dp and owns the rounding remainder (Batch 1).
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rd(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			decimal ToBase(decimal foreignAmt) => foreignAmt * rate;   // RAW (unrounded)

			var inv = new SalesInvoice { CompanyID = companyId, CustomerId = customerId, InvoiceDate = date.Date, Status = "Posted", Notes = notes, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = rate, ProjectId = projectId };
			var ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lineTotal = Rd(l.Qty * l.UnitPrice - l.DiscountAmount);   // document currency
				var lineTax = Rd(lineTotal * l.TaxRate / 100m);              // per-line tax (no header distribution)
				sub += lineTotal; tax += lineTax;
				inv.Lines.Add(new SalesInvoiceLine { LineNo = ln++, ItemDescription = l.ItemDescription, Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, RevenueAccountId = l.RevenueAccountId, ItemId = l.ItemId, WarehouseId = l.WarehouseId, UoMId = l.UoMId, LineTotal = lineTotal });
			}
			inv.SubTotal = Rd(sub); inv.TaxTotal = Rd(tax); inv.GrandTotal = Rd(sub + tax);   // document totals = Σ rounded lines
			// functional-currency base totals — RAW conversion of ONE grand value; stored rounded to functional dp (Rf).
			// The customer (1102) JE line below uses the SAME raw grandBase, which JES rounds to Rf — so column == JE line by construction.
			var revGroups = inv.Lines.GroupBy(l => l.RevenueAccountId).Select(g => new { Acc = g.Key, Base = ToBase(g.Sum(x => x.LineTotal)) }).ToList();
			decimal revBase = revGroups.Sum(g => g.Base);
			decimal vatBase = ToBase(inv.TaxTotal);
			decimal grandBase = revBase + vatBase;   // the SINGLE raw value that feeds both the stored column and the 1102 line
			inv.SubTotalBase = Rf(revBase); inv.TaxTotalBase = Rf(vatBase); inv.GrandTotalBase = Rf(grandBase);

			// P3-4: credit-limit enforcement (0/null limit = disabled) — compared in functional currency
			if (cust.CreditLimit.HasValue && cust.CreditLimit.Value > 0)
			{
				var outstanding = await CustomerOutstandingAsync(companyId, customerId);
				if (outstanding + grandBase > cust.CreditLimit.Value)
				{
					try
					{
						await _notify.NotifyRoleAsync(companyId, "acc", new[] { "Accountant", "ChiefAccountant" },
							"تجاوز حدّ ائتمان عميل", "Customer credit limit exceeded",
							$"An invoice of {grandBase:N2} for customer {cust.Name} was rejected: the outstanding balance {outstanding:N2} exceeds the limit {cust.CreditLimit.Value:N2}", $"Invoice of {grandBase:N2} for {cust.Name} was blocked: outstanding {outstanding:N2} exceeds limit {cust.CreditLimit.Value:N2}",
							"credit_block", customerId);
					}
					catch { /* notifications never block the business flow */ }
					return (false, $"Credit limit exceeded: the current outstanding {outstanding:N2} + this invoice {grandBase:N2} = {(outstanding + grandBase):N2}, which exceeds the customer limit {cust.CreditLimit.Value:N2}", null);
				}
			}

			// HM-1-أ ب-3: ONE ambient transaction wraps document + JE + stock so the sale is all-or-nothing.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);

			_context.SalesInvoices.Add(inv);
			await _context.SaveChangesAsync();
			inv.InvoiceNo = $"SV-{date:yyyy}-{inv.ID:D5}";
			await _context.SaveChangesAsync();

			// auto journal (functional currency): Dr AR control (grand base) / Cr revenue per line / Cr VAT output
			var jlines = new List<JournalLineInput> { new() { AccountId = cust.ControlAccountId, Debit = grandBase, Credit = 0, Description = $"Sales invoice {inv.InvoiceNo}", ProjectId = projectId } };
			foreach (var g in revGroups)
				jlines.Add(new JournalLineInput { AccountId = g.Acc, Debit = 0, Credit = g.Base, Description = "Revenue", ProjectId = projectId });
			if (vatBase > 0 && vatOut != null)
				jlines.Add(new JournalLineInput { AccountId = vatOut.Value, Debit = 0, Credit = vatBase, Description = "Output VAT", ProjectId = projectId });

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "SalesInvoice", SourceId = inv.ID, CurrencyId = cur,
				Description = $"فاتورة بيع {inv.InvoiceNo} - {cust.Name}", DescriptionEn = $"Sales invoice {inv.InvoiceNo}", Lines = jlines,
			}, userId);
			if (!ok) return (false, err, null);   // not committed → ambient tx rolls the invoice back
			inv.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();

			// HM-1-أ ب-3/ب-4: perpetual issue — only STOCKABLE items require a movement; deterministic order (ItemId,
			// WarehouseId) to avoid cross-basket deadlocks; EVERY result is CHECKED — a failed issue fails the whole sale.
			var lineItemIds = inv.Lines.Where(x => x.ItemId != null).Select(x => x.ItemId!.Value).Distinct().ToList();
			var lineItemTypes = await _context.Items.AsNoTracking().Where(i => lineItemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemType);
			foreach (var l in inv.Lines.Where(x => x.ItemId != null && x.WarehouseId != null && x.Qty > 0).OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseId))
			{
				if (!CrossBuy.Models.Context.Inventory.ItemTypes.RequiresStock(lineItemTypes.GetValueOrDefault(l.ItemId!.Value)))
					continue;   // Service/NonStockable/Asset → no movement required (anomalies surfaced by inv-test-integrity)
				var (sok, serr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = -1,
					Qty = l.Qty, UoMId = l.UoMId, SourceType = "SalesInvoice", SourceId = inv.ID, SourceLineId = l.ID,
					PostToGl = true, ProjectId = inv.ProjectId, Notes = $"Issue for sales invoice {inv.InvoiceNo}"
				}, userId?.ToString());
				if (!sok) return (false, serr ?? "Could not issue the stock", null);
			}

			// Platform Kernel (ADR-001): the durable fact, written INSIDE this transaction and BEFORE the
			// commit — the event and the invoice share one fate. Deliberately NOT in a try/catch: if the
			// event cannot be written the sale must not stand, which is the opposite of the notification
			// convention a few lines below (best-effort, after commit, swallowed).
			await _events.RecordAsync(new BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.SalesInvoice,
				EntityId = inv.ID,
				EventType = CrossBuy.BL.Platform.SalesInvoiceEvents.Created,
				PayloadVersion = SalesInvoiceEventPayload.Version,
				Visibility = BusinessEventVisibility.Internal,
				DedupKey = $"SalesInvoice.Created:{inv.ID}",   // one Created per invoice, forever
				Payload = new SalesInvoiceEventPayload
				{
					ReferenceNumber = inv.InvoiceNo,
					NewStatus = inv.Status,
					TotalAfter = inv.GrandTotal,
				},
			});

			await tx.CommitAsync();

			// Platform Kernel slice 2: the legacy after-commit NotifyRoleAsync that used to sit here was
			// REMOVED and is now produced by NotificationProjection from the SalesInvoice.Created event above
			// (same audience "acc"/ChiefAccountant, same catalog type "sales_invoice", same wording). Keeping
			// both would double-notify. The other notifications in this service — the stale-rate warning and
			// the credit-limit block — are untouched: neither has an event, and the credit-limit one fires on a
			// REJECTED invoice, where no business fact exists to record. See ADR-006.
			return (true, null, inv);
		}

		// P3: EDIT a posted sales invoice — reverse the original postings, then re-post the new values on the SAME invoice row/number.
		public async Task<(bool ok, string? error, SalesInvoice? inv)> EditSalesInvoiceAsync(
			int companyId, int invoiceId, int customerId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
		{
			var inv = await _context.SalesInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.ID == invoiceId && i.CompanyID == companyId);
			if (inv == null) return (false, "Invoice not found", null);
			if (inv.Status != "Posted") return (false, "An invoice that is not posted, or is cancelled, cannot be edited", null);
			// integrity guard: a collected invoice must not be edited (its AR is already allocated to a receipt)
			bool allocated = await _context.ReceiptAllocations.AsNoTracking().AnyAsync(a => a.CompanyID == companyId && a.SalesInvoiceId == invoiceId);
			if (allocated) return (false, "The invoice cannot be edited because a collection is allocated to it — unallocate it first", null);
			var cust = await _context.Customers.FirstOrDefaultAsync(c => c.ID == customerId && c.CompanyID == companyId);
			if (cust == null) return (false, "Customer not found", null);
			if (lines == null || lines.Count == 0) return (false, "The invoice must contain at least one line", null);

			// Platform Kernel: snapshot the header values BEFORE any mutation so the event can carry a
			// change SUMMARY (field names + total delta) instead of the whole entity graph.
			var beforeCustomerId = inv.CustomerId;
			var beforeInvoiceDate = inv.InvoiceDate;
			var beforeCurrencyId = inv.CurrencyId;
			var beforeExchangeRate = inv.ExchangeRate;
			var beforeProjectId = inv.ProjectId;
			var beforeNotes = inv.Notes;
			var beforeStatus = inv.Status;
			var beforeGrandTotal = inv.GrandTotal;
			var beforeLineSignature = LineSignature(inv.Lines);

			var vatOut = await AccIdAsync(companyId, "210201");
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Sell"); rate = r; }
			// HM-2: same pattern as create — document Rd, base Rf, single grandBase feeds the 1102 line + column. Reverse+repost
			// use the SAME rate resolution, so an edit of a KWD invoice leaves no artifact in 1102 (net = new − old at one rate).
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rd(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			decimal ToBase(decimal foreignAmt) => foreignAmt * rate;   // RAW

			// HM-1-أ ب-3: ONE ambient transaction wraps the whole reverse+repost so an edit is all-or-nothing.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			// (1) reverse the original stock issues — return each at the EXACT issued cost so qty + value are restored precisely
			var origMoves = await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.SourceType == "SalesInvoice" && m.SourceId == invoiceId && m.Direction == -1)
				.OrderBy(m => m.ItemId).ThenBy(m => m.WarehouseId).ToListAsync();
			foreach (var m in origMoves)
			{
				var (rok, rerr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest {
					Date = date, ItemId = m.ItemId, WarehouseId = m.WarehouseId, Direction = 1,
					Qty = m.QtyBase, UoMId = null, UnitCostInBase = m.UnitCost,
					SourceType = "SalesInvoiceEdit", SourceId = invoiceId, PostToGl = true,
					ProjectId = inv.ProjectId, Notes = $"Reversal of the issue for the edit of invoice {inv.InvoiceNo}"
				}, userId?.ToString());
				if (!rok) return (false, rerr ?? "Could not reverse the stock issue", null);
			}
			// (2) reverse the original GL entry (mirror entry dated today)
			if (inv.JournalEntryId.HasValue)
			{
				var (rjok, rjerr, _) = await _journals.ReverseAsync(inv.JournalEntryId.Value, userId, $"Edit of invoice {inv.InvoiceNo}");
				if (!rjok) return (false, rjerr ?? "Could not reverse the invoice entry", null);
			}

			// (3) drop the old lines
			_context.SalesInvoiceLines.RemoveRange(inv.Lines);
			inv.Lines.Clear();

			// (4) recompute + update the invoice row (keep ID + InvoiceNo)
			var ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lineTotal = Rd(l.Qty * l.UnitPrice - l.DiscountAmount);
				var lineTax = Rd(lineTotal * l.TaxRate / 100m);
				sub += lineTotal; tax += lineTax;
				inv.Lines.Add(new SalesInvoiceLine { LineNo = ln++, ItemDescription = l.ItemDescription, Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, RevenueAccountId = l.RevenueAccountId, ItemId = l.ItemId, WarehouseId = l.WarehouseId, UoMId = l.UoMId, LineTotal = lineTotal });
			}
			var revGroups = inv.Lines.GroupBy(l => l.RevenueAccountId).Select(g => new { Acc = g.Key, Base = ToBase(g.Sum(x => x.LineTotal)) }).ToList();
			decimal revBase = revGroups.Sum(g => g.Base), vatBase = ToBase(Rd(tax)), grandBase = revBase + vatBase;   // single raw grandBase
			inv.CustomerId = customerId; inv.InvoiceDate = date.Date; inv.Notes = notes; inv.CurrencyId = cur; inv.ExchangeRate = rate; inv.ProjectId = projectId;
			inv.SubTotal = Rd(sub); inv.TaxTotal = Rd(tax); inv.GrandTotal = Rd(sub + tax);
			inv.SubTotalBase = Rf(revBase); inv.TaxTotalBase = Rf(vatBase); inv.GrandTotalBase = Rf(grandBase);
			await _context.SaveChangesAsync();

			// (5) post the new GL entry
			var jlines = new List<JournalLineInput> { new() { AccountId = cust.ControlAccountId, Debit = grandBase, Credit = 0, Description = $"Sales invoice {inv.InvoiceNo} (edited)", ProjectId = projectId } };
			foreach (var g in revGroups) jlines.Add(new JournalLineInput { AccountId = g.Acc, Debit = 0, Credit = g.Base, Description = "Revenue", ProjectId = projectId });
			if (vatBase > 0 && vatOut != null) jlines.Add(new JournalLineInput { AccountId = vatOut.Value, Debit = 0, Credit = vatBase, Description = "Output VAT", ProjectId = projectId });
			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "SalesInvoice", SourceId = inv.ID, CurrencyId = cur,
				Description = $"فاتورة بيع {inv.InvoiceNo} (معدّلة) - {cust.Name}", DescriptionEn = $"Sales invoice {inv.InvoiceNo} (edited)", Lines = jlines,
			}, userId);
			if (!ok) return (false, err, null);
			inv.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();

			// (6) re-issue stock for the new item lines — Stockable only, deterministic order, results CHECKED
			var editItemIds = inv.Lines.Where(x => x.ItemId != null).Select(x => x.ItemId!.Value).Distinct().ToList();
			var editItemTypes = await _context.Items.AsNoTracking().Where(i => editItemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemType);
			foreach (var l in inv.Lines.Where(x => x.ItemId != null && x.WarehouseId != null && x.Qty > 0).OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseId))
			{
				if (!CrossBuy.Models.Context.Inventory.ItemTypes.RequiresStock(editItemTypes.GetValueOrDefault(l.ItemId!.Value))) continue;
				var (sok, serr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = -1,
					Qty = l.Qty, UoMId = l.UoMId, SourceType = "SalesInvoice", SourceId = inv.ID, SourceLineId = l.ID,
					PostToGl = true, ProjectId = inv.ProjectId, Notes = $"Issue for sales invoice {inv.InvoiceNo} (edited)"
				}, userId?.ToString());
				if (!sok) return (false, serr ?? "Could not issue the stock", null);
			}

			// Platform Kernel (ADR-001): the durable fact, inside this transaction and before the commit.
			// Field NAMES only — no old/new values beyond the totals, which the reader can already see on
			// the invoice. No DedupKey: an invoice may legitimately be edited more than once, and each edit
			// is its own fact.
			var changedFields = new List<string>();
			if (beforeCustomerId != inv.CustomerId) changedFields.Add(nameof(inv.CustomerId));
			if (beforeInvoiceDate != inv.InvoiceDate) changedFields.Add(nameof(inv.InvoiceDate));
			if (beforeCurrencyId != inv.CurrencyId) changedFields.Add(nameof(inv.CurrencyId));
			if (beforeExchangeRate != inv.ExchangeRate) changedFields.Add(nameof(inv.ExchangeRate));
			if (beforeProjectId != inv.ProjectId) changedFields.Add(nameof(inv.ProjectId));
			if (beforeNotes != inv.Notes) changedFields.Add(nameof(inv.Notes));
			if (beforeLineSignature != LineSignature(inv.Lines)) changedFields.Add(nameof(inv.Lines));
			if (beforeGrandTotal != inv.GrandTotal) changedFields.Add(nameof(inv.GrandTotal));

			await _events.RecordAsync(new BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.SalesInvoice,
				EntityId = inv.ID,
				EventType = CrossBuy.BL.Platform.SalesInvoiceEvents.Updated,
				PayloadVersion = SalesInvoiceEventPayload.Version,
				Visibility = BusinessEventVisibility.Internal,
				Payload = new SalesInvoiceEventPayload
				{
					ReferenceNumber = inv.InvoiceNo,
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
		private static string LineSignature(IEnumerable<SalesInvoiceLine> lines) => string.Join("|",
			lines.OrderBy(l => l.LineNo)
				 .Select(l => $"{l.ItemId}:{l.ItemDescription}:{l.Qty}:{l.UnitPrice}:{l.DiscountAmount}:{l.TaxRate}:{l.WarehouseId}:{l.UoMId}"));

		// ---------------- P3-3a: Sales returns / credit notes ----------------
		public async Task<List<SalesReturn>> GetSalesReturnsAsync(int companyId) =>
			await _context.SalesReturns.AsNoTracking().Where(r => r.CompanyID == companyId).OrderByDescending(r => r.ID).ToListAsync();

		public async Task<SalesReturn?> GetSalesReturnAsync(int companyId, int id) =>
			await _context.SalesReturns.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.ID == id && r.CompanyID == companyId);

		public async Task<(bool ok, string? error, SalesReturn? ret)> CreateSalesReturnAsync(
			int companyId, int customerId, int? originalInvoiceId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null)
		{
			var cust = await _context.Customers.FirstOrDefaultAsync(c => c.ID == customerId && c.CompanyID == companyId);
			if (cust == null) return (false, "Customer not found", null);
			if (lines == null || lines.Count == 0) return (false, "The return must contain at least one line", null);
			var vatOut = await AccIdAsync(companyId, "210201");
			// HM-2 (3-ج-0): currency-aware. The AR is reversed at the ORIGINAL INVOICE's rate (so ar_sub nets to 0 against it);
			// with NO original invoice there is no reference rate ⇒ book at the return-day rate (no FX). All JE lines are functional (base).
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (originalInvoiceId != null)
			{
				var origRate = await _context.SalesInvoices.AsNoTracking().Where(i => i.ID == originalInvoiceId.Value && i.CompanyID == companyId).Select(i => i.ExchangeRate).FirstOrDefaultAsync();
				rate = (origRate.HasValue && origRate.Value > 0) ? origRate.Value : 1m;   // settle at the invoice rate
			}
			else if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Sell"); rate = r; }
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rd(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			decimal ToBase(decimal foreignAmt) => foreignAmt * rate;   // RAW at the settlement (invoice) rate

			var ret = new SalesReturn { CompanyID = companyId, CustomerId = customerId, OriginalInvoiceId = originalInvoiceId, ReturnDate = date.Date, WarehouseId = lines.FirstOrDefault()?.WarehouseId, Status = "Posted", Notes = notes, CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = rate };
			var ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lineTotal = Rd(l.Qty * l.UnitPrice - l.DiscountAmount);   // document currency
				var lineTax = Rd(lineTotal * l.TaxRate / 100m);
				sub += lineTotal; tax += lineTax;
				ret.Lines.Add(new SalesReturnLine { LineNo = ln++, ItemDescription = l.ItemDescription, Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, RevenueAccountId = l.RevenueAccountId, ItemId = l.ItemId, WarehouseId = l.WarehouseId, LineTotal = lineTotal });
			}
			ret.SubTotal = Rd(sub); ret.TaxTotal = Rd(tax); ret.GrandTotal = Rd(sub + tax);   // document totals
			// base at the settlement (invoice) rate — the single raw grandBase feeds both the stored column and the 1102 line
			var revGroups = ret.Lines.GroupBy(l => l.RevenueAccountId).Select(g => new { Acc = g.Key, Base = ToBase(g.Sum(x => x.LineTotal)) }).ToList();
			decimal revBase = revGroups.Sum(g => g.Base), vatBase = ToBase(ret.TaxTotal), grandBase = revBase + vatBase;
			ret.SubTotalBase = Rf(revBase); ret.TaxTotalBase = Rf(vatBase); ret.GrandTotalBase = Rf(grandBase);
			// HM-1-أ ب-3: ONE ambient transaction — the credit note + its GL + the stock return are all-or-nothing.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			_context.SalesReturns.Add(ret);
			await _context.SaveChangesAsync();
			ret.ReturnNo = $"CN-{date:yyyy}-{ret.ID:D5}";
			await _context.SaveChangesAsync();

			// credit-note JE = REVERSE of the sale at the invoice rate (all lines functional/base): Dr revenue + Dr VAT / Cr AR (single grandBase)
			var jlines = new List<JournalLineInput>();
			foreach (var g in revGroups)
				jlines.Add(new JournalLineInput { AccountId = g.Acc, Debit = g.Base, Credit = 0, Description = "Sales return — revenue reduction" });
			if (vatBase > 0 && vatOut != null)
				jlines.Add(new JournalLineInput { AccountId = vatOut.Value, Debit = vatBase, Credit = 0, Description = "Output VAT reversal" });
			jlines.Add(new JournalLineInput { AccountId = cust.ControlAccountId, Debit = 0, Credit = grandBase, Description = $"Credit note {ret.ReturnNo}" });

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "SalesReturn", SourceId = ret.ID, CurrencyId = cur,
				Description = $"إشعار دائن {ret.ReturnNo} - {cust.Name}", DescriptionEn = $"Credit note {ret.ReturnNo}", Lines = jlines,
			}, userId);
			if (!ok) return (false, err, null);
			ret.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();

			// return goods to stock (Direction +1) — Stockable only, deterministic order, results CHECKED
			var retItemIds = ret.Lines.Where(x => x.ItemId != null).Select(x => x.ItemId!.Value).Distinct().ToList();
			var retItemTypes = await _context.Items.AsNoTracking().Where(i => retItemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemType);
			foreach (var l in ret.Lines.Where(x => x.ItemId != null && x.WarehouseId != null && x.Qty > 0).OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseId))
			{
				if (!CrossBuy.Models.Context.Inventory.ItemTypes.RequiresStock(retItemTypes.GetValueOrDefault(l.ItemId!.Value))) continue;
				var (sok, serr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = 1,
					Qty = l.Qty, SourceType = "SalesReturn", SourceId = ret.ID, SourceLineId = l.ID,
					PostToGl = true, Notes = $"Sales return {ret.ReturnNo}"
				}, userId?.ToString());
				if (!sok) return (false, serr ?? "Could not return the stock", null);
			}
			await tx.CommitAsync();
			return (true, null, ret);
		}

		// P3: EDIT a posted sales return — reverse the original postings (stock subledger at exact cost + all GL via mirror entries), then re-post on the same row/number.
		public async Task<(bool ok, string? error, SalesReturn? ret)> EditSalesReturnAsync(
			int companyId, int returnId, int customerId, int? originalInvoiceId, DateTime date, List<SalesLineInput> lines, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null)
		{
			var ret = await _context.SalesReturns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.ID == returnId && r.CompanyID == companyId);
			if (ret == null) return (false, "Return not found", null);
			if (ret.Status != "Posted") return (false, "A return that is not posted cannot be edited", null);
			var cust = await _context.Customers.FirstOrDefaultAsync(c => c.ID == customerId && c.CompanyID == companyId);
			if (cust == null) return (false, "Customer not found", null);
			if (lines == null || lines.Count == 0) return (false, "The return must contain at least one line", null);
			var vatOut = await AccIdAsync(companyId, "210201");
			// HM-2 (3-ج-0): same currency rule as CreateSalesReturnAsync — settle at the ORIGINAL INVOICE rate (else return-day). All JE lines base.
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? ret.CurrencyId ?? functional;
			decimal rate;
			if (originalInvoiceId != null)
			{
				var origRate = await _context.SalesInvoices.AsNoTracking().Where(i => i.ID == originalInvoiceId.Value && i.CompanyID == companyId).Select(i => i.ExchangeRate).FirstOrDefaultAsync();
				rate = (origRate.HasValue && origRate.Value > 0) ? origRate.Value : 1m;
			}
			else if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Sell"); rate = r; }
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rd(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);
			decimal ToBase(decimal foreignAmt) => foreignAmt * rate;   // RAW at the settlement rate

			// HM-1-أ ب-3: ONE ambient transaction wraps the whole reverse+repost so an edit is all-or-nothing.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);
			// (1) reverse original stock (goods had come IN → take them OUT at the exact received cost; GL reversed via the movement's own entry)
			var moves = await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.SourceType == "SalesReturn" && m.SourceId == returnId && m.Direction == 1)
				.OrderBy(m => m.ItemId).ThenBy(m => m.WarehouseId).ToListAsync();
			foreach (var m in moves)
			{
				var (rok, rerr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest {
					Date = date, ItemId = m.ItemId, WarehouseId = m.WarehouseId, Direction = -1,
					Qty = m.QtyBase, OutCostOverride = m.UnitCost, PostToGl = false,
					SourceType = "SalesReturnEdit", SourceId = returnId, Notes = $"Reversal of return {ret.ReturnNo}"
				}, userId?.ToString());
				if (!rok) return (false, rerr ?? "Could not reverse the return's stock", null);
				if (m.JournalEntryId.HasValue) { var (jr, je, _) = await _journals.ReverseAsync(m.JournalEntryId.Value, userId, $"Edit of return {ret.ReturnNo}"); if (!jr) return (false, je ?? "Could not reverse the inventory entry", null); }
			}
			// (2) reverse the credit-note GL
			if (ret.JournalEntryId.HasValue) { var (jr2, je2, _) = await _journals.ReverseAsync(ret.JournalEntryId.Value, userId, $"Edit of return {ret.ReturnNo}"); if (!jr2) return (false, je2 ?? "Could not reverse the credit-note entry", null); }

			// (3) drop old lines + recompute
			_context.SalesReturnLines.RemoveRange(ret.Lines); ret.Lines.Clear();
			var ln = 1; decimal sub = 0, tax = 0;
			foreach (var l in lines)
			{
				var lineTotal = Rd(l.Qty * l.UnitPrice - l.DiscountAmount);   // document
				sub += lineTotal; tax += Rd(lineTotal * l.TaxRate / 100m);
				ret.Lines.Add(new SalesReturnLine { LineNo = ln++, ItemDescription = l.ItemDescription, Qty = l.Qty, UnitPrice = l.UnitPrice, DiscountAmount = l.DiscountAmount, TaxRate = l.TaxRate, RevenueAccountId = l.RevenueAccountId, ItemId = l.ItemId, WarehouseId = l.WarehouseId, LineTotal = lineTotal });
			}
			ret.CustomerId = customerId; ret.OriginalInvoiceId = originalInvoiceId; ret.ReturnDate = date.Date; ret.Notes = notes;
			ret.WarehouseId = lines.FirstOrDefault()?.WarehouseId; ret.CurrencyId = cur; ret.ExchangeRate = rate;
			ret.SubTotal = Rd(sub); ret.TaxTotal = Rd(tax); ret.GrandTotal = Rd(sub + tax);   // document totals
			var revGroups = ret.Lines.GroupBy(l => l.RevenueAccountId).Select(g => new { Acc = g.Key, Base = ToBase(g.Sum(x => x.LineTotal)) }).ToList();
			decimal revBase = revGroups.Sum(g => g.Base), vatBase = ToBase(ret.TaxTotal), grandBase = revBase + vatBase;   // single raw grandBase
			ret.SubTotalBase = Rf(revBase); ret.TaxTotalBase = Rf(vatBase); ret.GrandTotalBase = Rf(grandBase);
			await _context.SaveChangesAsync();

			// (4) new credit-note JE (all functional/base): Dr revenue + Dr VAT / Cr AR (single grandBase)
			var jlines = new List<JournalLineInput>();
			foreach (var g in revGroups)
				jlines.Add(new JournalLineInput { AccountId = g.Acc, Debit = g.Base, Credit = 0, Description = "Sales return — revenue reduction" });
			if (vatBase > 0 && vatOut != null)
				jlines.Add(new JournalLineInput { AccountId = vatOut.Value, Debit = vatBase, Credit = 0, Description = "Output VAT reversal" });
			jlines.Add(new JournalLineInput { AccountId = cust.ControlAccountId, Debit = 0, Credit = grandBase, Description = $"Credit note {ret.ReturnNo} (edited)" });
			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "SalesReturn", SourceId = ret.ID,
				Description = $"إشعار دائن {ret.ReturnNo} (معدّل) - {cust.Name}", DescriptionEn = $"Credit note {ret.ReturnNo} (edited)", Lines = jlines,
			}, userId);
			if (!ok) return (false, err, null);
			ret.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();

			// (5) re-return goods to stock — Stockable only, deterministic order, results CHECKED
			var rrItemIds = ret.Lines.Where(x => x.ItemId != null).Select(x => x.ItemId!.Value).Distinct().ToList();
			var rrItemTypes = await _context.Items.AsNoTracking().Where(i => rrItemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemType);
			foreach (var l in ret.Lines.Where(x => x.ItemId != null && x.WarehouseId != null && x.Qty > 0).OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseId))
			{
				if (!CrossBuy.Models.Context.Inventory.ItemTypes.RequiresStock(rrItemTypes.GetValueOrDefault(l.ItemId!.Value))) continue;
				var (sok, serr, _) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = date, ItemId = l.ItemId!.Value, WarehouseId = l.WarehouseId!.Value, Direction = 1,
					Qty = l.Qty, SourceType = "SalesReturn", SourceId = ret.ID, SourceLineId = l.ID,
					PostToGl = true, Notes = $"Sales return {ret.ReturnNo} (edited)"
				}, userId?.ToString());
				if (!sok) return (false, serr ?? "Could not return the stock", null);
			}
			await tx.CommitAsync();
			return (true, null, ret);
		}

		// projectId (optional, P4): tags the receipt's JE lines with the project dimension — used by progress billing to
		// route retention (cashAccountId=1104) / advance recovery (cashAccountId=2104) as ProjectId-tagged settlements.
		public async Task<(bool ok, string? error)> CreateReceiptAsync(int companyId, int customerId, DateTime date, decimal amount, string method, int cashAccountId, string? notes, int? userId, int? currencyId = null, decimal? exchangeRate = null, int? projectId = null)
		{
			var cust = await _context.Customers.FirstOrDefaultAsync(c => c.ID == customerId && c.CompanyID == companyId);
			if (cust == null) return (false, "Customer not found");
			if (amount <= 0) return (false, "The amount must be greater than zero");
			// HM-1-أ (هـ): receipt row + its JE + the JournalEntryId back-ref must be ATOMIC (own-or-join). Called from PayAsync
			// it joins that transaction; standalone it opens its own — closing the old 2-commit gap (JE posted, back-ref unsaved).
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);

			// Multi-Currency (1-5): receipt in `cur`. FIFO-allocate to open same-currency invoices, clearing AR
			// at EACH invoice's rate, so the rate difference vs the receipt rate is realized FX (4902/5902).
			var functional = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			var cur = currencyId ?? functional;
			decimal rate;
			if (cur == functional) rate = 1m;
			else if (exchangeRate.HasValue && exchangeRate.Value > 0) rate = exchangeRate.Value;
			else { var (_, r) = await _currency.ToBaseAsync(1m, cur, functional, date, "Sell"); rate = r; }
			// HM-2: document amounts round to the document currency (Rd); base amounts to functional (Rf). NOTE: a receipt JE has no
			// eligible P&L line (Cash/AR/FX are all forbidden from the rounding remainder), so it must balance BY CONSTRUCTION —
			// fxNet = cashBase − arBaseTotal naturally absorbs any sub-unit as realized FX (which is what a currency-conversion
			// sub-unit IS). So base amounts are Rf-rounded here (not passed raw) and the JE is exactly balanced before JES.
			int __ddp = await _rounding.DecimalsAsync(companyId, cur);
			int __fdp = await _rounding.DecimalsAsync(companyId, null);
			decimal Rd(decimal v) => Math.Round(v, __ddp, MidpointRounding.AwayFromZero);
			decimal Rf(decimal v) => Math.Round(v, __fdp, MidpointRounding.AwayFromZero);

			var rc = new Receipt { CompanyID = companyId, CustomerId = customerId, ReceiptDate = date.Date, Amount = Rd(amount), Method = method, CashAccountId = cashAccountId, Status = "Posted", CreatedAt = DateTime.UtcNow, CurrencyId = cur, ExchangeRate = rate };
			_context.Receipts.Add(rc);
			await _context.SaveChangesAsync();
			rc.ReceiptNo = $"RC-{date:yyyy}-{rc.ID:D5}";

			// open invoices of this customer in the same currency, oldest first
			var openInvoices = await (from i in _context.SalesInvoices.AsNoTracking()
									  where i.CompanyID == companyId && i.CustomerId == customerId && i.Status == "Posted" && (i.CurrencyId ?? functional) == cur
									  orderby i.InvoiceDate, i.ID
									  select new { i.ID, i.GrandTotal, i.ExchangeRate }).ToListAsync();
			var settledByInv = (await _context.ReceiptAllocations.AsNoTracking().Where(a => a.CompanyID == companyId)
								.GroupBy(a => a.SalesInvoiceId).Select(g => new { Inv = g.Key, F = g.Sum(x => x.ForeignAmount) }).ToListAsync())
								.ToDictionary(x => x.Inv, x => x.F);

			decimal left = Rd(amount), arBaseTotal = 0m;
			var allocs = new List<ReceiptAllocation>();
			foreach (var inv in openInvoices)
			{
				if (left <= 0) break;
				var remaining = Rd(inv.GrandTotal - (settledByInv.TryGetValue(inv.ID, out var s) ? s : 0m));   // document currency
				if (remaining <= 0) continue;
				var take = Math.Min(remaining, left);
				var invRate = (inv.ExchangeRate.HasValue && inv.ExchangeRate.Value > 0) ? inv.ExchangeRate.Value : 1m;
				var arBase = Rf(take * invRate);           // AR cleared at the INVOICE rate (functional) — matches GrandTotalBase
				arBaseTotal += arBase; left = Rd(left - take);
				allocs.Add(new ReceiptAllocation { CompanyID = companyId, ReceiptId = rc.ID, SalesInvoiceId = inv.ID, ForeignAmount = take, InvoiceRate = R4(invRate), ReceiptRate = R4(rate), ArBase = arBase, FxDiff = Rf(take * rate) - arBase, CreatedAt = DateTime.UtcNow });
			}
			if (left > 0) arBaseTotal += Rf(left * rate);   // unallocated (advance / no open invoice) → AR at receipt rate, no FX

			decimal cashBase = Rf(amount * rate);
			rc.AmountBase = arBaseTotal;                   // AR cleared = subledger basis (single source → matches the 1102 JE line)
			decimal fxNet = Rf(cashBase - arBaseTotal);    // realized FX (gain when cash exceeds AR cleared); absorbs the conversion sub-unit

			var lines = new List<JournalLineInput>
			{
				// DescriptionEn on every line, not only on the entry header. The header here already had one
				// ("Receipt {no}") while the lines under it stayed Arabic, so /Accounting/JournalEntry read as
				// an English document with Arabic rows. These strings are STORED, so the English text must not
				// depend on the operator's UI language - both are written, and the view picks.
				new() { AccountId = cashAccountId, Debit = cashBase, Credit = 0, Description = "تحصيل نقدي", DescriptionEn = "Cash collected", ProjectId = projectId },
				new() { AccountId = cust.ControlAccountId, Debit = 0, Credit = arBaseTotal, Description = "سداد عميل", DescriptionEn = "Customer settlement", ProjectId = projectId },
			};
			if (fxNet != 0)
			{
				var fxAcc = await AccIdAsync(companyId, fxNet > 0 ? "4902" : "5902");
				if (fxAcc == null) { if (_context.Database.CurrentTransaction == null) { _context.Receipts.Remove(rc); await _context.SaveChangesAsync(); } return (false,"حساب فروق العملة المحققة (4902/5902) غير مُهيّأ"); }
				if (fxNet > 0) lines.Add(new() { AccountId = fxAcc.Value, Debit = 0, Credit = fxNet, Description = "ربح فرق عملة محقق", DescriptionEn = "Realised FX gain", ProjectId = projectId });
				else lines.Add(new() { AccountId = fxAcc.Value, Debit = -fxNet, Credit = 0, Description = "خسارة فرق عملة محققة", DescriptionEn = "Realised FX loss", ProjectId = projectId });
			}

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date, JournalType = "Auto", SourceType = "Receipt", SourceId = rc.ID, CurrencyId = cur,
				Description = $"سند قبض {rc.ReceiptNo} - {cust.Name}", DescriptionEn = $"Receipt {rc.ReceiptNo}", Lines = lines,
			}, userId);
			if (!ok) { if (_context.Database.CurrentTransaction == null) { _context.Receipts.Remove(rc); await _context.SaveChangesAsync(); } return (false,err); }
			rc.JournalEntryId = entry!.ID;
			if (allocs.Count > 0) _context.ReceiptAllocations.AddRange(allocs);
			await _context.SaveChangesAsync();
			await tx.CommitAsync();
			return (true, null);
		}

		public async Task<List<AgingRow>> AgingAsync(int companyId, DateTime asOf)
		{
			// culture-aware party name: English (NameEn) when UI is not Arabic, fall back to Arabic
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var customers = await _context.Customers.AsNoTracking().Where(c => c.CompanyID == companyId).ToListAsync();
			var invoices = await _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").ToListAsync();
			var receipts = await _context.Receipts.AsNoTracking().Where(r => r.CompanyID == companyId && r.Status == "Posted").ToListAsync();

			var rows = new List<AgingRow>();
			foreach (var c in customers)
			{
				var custInv = invoices.Where(i => i.CustomerId == c.ID).OrderBy(i => i.InvoiceDate).ToList();
				if (custInv.Count == 0) continue;
				// Multi-Currency: age in the functional currency (base columns)
				var paid = receipts.Where(r => r.CustomerId == c.ID).Sum(r => r.AmountBase ?? r.Amount);

				// FIFO: apply receipts to the oldest invoices first
				var row = new AgingRow { PartyId = c.ID, Name = (isEn && !string.IsNullOrWhiteSpace(c.NameEn)) ? c.NameEn! : c.Name };
				foreach (var inv in custInv)
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

		public async Task<CustomerAnalytics> GetCustomerAnalyticsAsync(int companyId)
		{
			// culture-aware party name: English (NameEn) when UI is not Arabic, fall back to Arabic
			var isEn = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
			var customers = await _context.Customers.AsNoTracking().Where(c => c.CompanyID == companyId)
				.Select(c => new { c.ID, c.Name, c.NameEn, c.Segment }).ToListAsync();
			// Multi-Currency: analytics are reported in the functional currency (use *Base, fall back to source)
			var inv = await _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted")
				.GroupBy(i => i.CustomerId).Select(g => new { id = g.Key, grand = g.Sum(x => x.GrandTotalBase ?? x.GrandTotal), sub = g.Sum(x => x.SubTotalBase ?? x.SubTotal), cnt = g.Count(), first = g.Min(x => x.InvoiceDate), last = g.Max(x => x.InvoiceDate) }).ToListAsync();
			var rcp = await _context.Receipts.AsNoTracking().Where(r => r.CompanyID == companyId && r.Status == "Posted" && r.CustomerId != null)
				.GroupBy(r => r.CustomerId!.Value).Select(g => new { id = g.Key, v = g.Sum(x => x.AmountBase ?? x.Amount) }).ToListAsync();
			var ret = await _context.SalesReturns.AsNoTracking().Where(s => s.CompanyID == companyId && s.Status == "Posted")
				.GroupBy(s => s.CustomerId).Select(g => new { id = g.Key, grand = g.Sum(x => x.GrandTotalBase ?? x.GrandTotal), sub = g.Sum(x => x.SubTotalBase ?? x.SubTotal) }).ToListAsync();
			var cogsInv = await (from m in _context.StockMovements.AsNoTracking()
								 where m.CompanyID == companyId && m.SourceType == "SalesInvoice" && m.SourceId != null
								 join i in _context.SalesInvoices.AsNoTracking() on m.SourceId equals (int?)i.ID
								 group m.TotalCost by i.CustomerId into g
								 select new { id = g.Key, v = g.Sum() }).ToListAsync();
			var cogsRet = await (from m in _context.StockMovements.AsNoTracking()
								 where m.CompanyID == companyId && m.SourceType == "SalesReturn" && m.SourceId != null
								 join s in _context.SalesReturns.AsNoTracking() on m.SourceId equals (int?)s.ID
								 group m.TotalCost by s.CustomerId into g
								 select new { id = g.Key, v = g.Sum() }).ToListAsync();

			var invById = inv.ToDictionary(x => x.id);
			var rcpById = rcp.ToDictionary(x => x.id, x => x.v);
			var retById = ret.ToDictionary(x => x.id);
			var cogsInvById = cogsInv.ToDictionary(x => x.id, x => x.v);
			var cogsRetById = cogsRet.ToDictionary(x => x.id, x => x.v);

			var result = new CustomerAnalytics();
			int __fdp = await _rounding.DecimalsAsync(companyId, null);   // HM-2 Batch 5: analytics averages in functional dp (no literal ,2)
			foreach (var c in customers)
			{
				if (!invById.TryGetValue(c.ID, out var iv)) continue;   // only customers with posted sales
				var received = rcpById.TryGetValue(c.ID, out var rv) ? rv : 0m;
				var retGrand = retById.TryGetValue(c.ID, out var rt) ? rt.grand : 0m;
				var retSub = retById.TryGetValue(c.ID, out var rt2) ? rt2.sub : 0m;
				var cogs = (cogsInvById.TryGetValue(c.ID, out var ci) ? ci : 0m) - (cogsRetById.TryGetValue(c.ID, out var cr) ? cr : 0m);
				var revenue = iv.sub - retSub;
				var margin = revenue - cogs;
				result.Rows.Add(new CustomerAnalyticsRow
				{
					CustomerId = c.ID, Name = (isEn && !string.IsNullOrWhiteSpace(c.NameEn)) ? c.NameEn! : c.Name, Segment = c.Segment,
					Invoiced = iv.grand, Received = received, Returns = retGrand,
					Outstanding = iv.grand - received - retGrand,
					Revenue = revenue, Cogs = cogs, Margin = margin,
					MarginPct = revenue != 0 ? Math.Round(margin / revenue * 100, 1) : 0,
					InvoiceCount = iv.cnt, FirstInvoice = iv.first, LastInvoice = iv.last,
					AvgInvoice = iv.cnt > 0 ? Math.Round(iv.grand / iv.cnt, __fdp, MidpointRounding.AwayFromZero) : 0
				});
			}
			result.Rows = result.Rows.OrderByDescending(r => r.Revenue).ToList();
			result.ActiveCustomers = result.Rows.Count;
			result.TotalRevenue = result.Rows.Sum(r => r.Revenue);
			result.TotalMargin = result.Rows.Sum(r => r.Margin);
			result.TotalOutstanding = result.Rows.Sum(r => r.Outstanding);
			result.Segments = result.Rows
				.GroupBy(r => string.IsNullOrWhiteSpace(r.Segment) ? "—" : r.Segment!)
				.Select(g => new SegmentRollup { Segment = g.Key, Customers = g.Count(), Revenue = g.Sum(x => x.Revenue), Margin = g.Sum(x => x.Margin), Outstanding = g.Sum(x => x.Outstanding) })
				.OrderByDescending(s => s.Revenue).ToList();
			return result;
		}
	}
}
