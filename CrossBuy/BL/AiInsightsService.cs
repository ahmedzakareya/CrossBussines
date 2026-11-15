using System.Text.Json;
using CrossBuy.BL.Platform.Ai;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CrossBuy.BL
{
	public class AiInsightsService : IAiInsightsService
	{
		private readonly CrossDbContext _context;
		private readonly IAiService _ai;
		private readonly IAiEgressPolicy _egress;
		private readonly IConfiguration _config;
		private readonly IAiProviderAuthority _providerAuthority;

		public AiInsightsService(CrossDbContext context, IAiService ai, IAiEgressPolicy egress,
			IConfiguration config, IAiProviderAuthority providerAuthority)
		{
			_context = context;
			_ai = ai;
			_egress = egress;
			_config = config;
			_providerAuthority = providerAuthority ?? throw new ArgumentNullException(nameof(providerAuthority));
		}

		// ---------------------------------------------------------------------------------------------
		// THE EGRESS CHOKEPOINT for this service.
		//
		// Serializes the outbound DTO, asks the central policy, and only then calls the AI client. The
		// order matters and is asserted by test: the payload is measured before the decision, and the
		// decision is taken before the network.
		//
		// A DENY returns a synthetic 403 result rather than throwing. The three insight screens already
		// render a "service unavailable" state, so a refusal degrades exactly like an unreachable service
		// instead of producing a 500 — and, critically, ZERO outbound calls are made.
		// ---------------------------------------------------------------------------------------------
		private async Task<AiProxyResult> SendAsync(
			AiEgressPurpose purpose, AiDataClassification classification,
			int companyId, string path, object payload, CancellationToken ct)
		{
			int bytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload));

			var decision = await _egress.EvaluateAsync(new AiEgressRequest
			{
				Purpose = purpose,
				// Increment 4.3: configuration supplies the technical destination; the governance record
				// supplies the authority to treat it as an APPROVED external processor. The policy
				// re-checks this independently, so passing it here is for the correct deny REASON and so
				// this path can work at all once an approval genuinely exists.
				//
				// Increment 4.9: the authority now needs to know WHICH account context is being asked
				// about. This service currently drives the LOCAL Python path, whose destination is
				// Internal and needs no provider scope at all — so the scope comes from the OpenAI
				// options, which are unconfigured here and therefore resolve to an incomplete scope that
				// denies. That is the correct outcome: an unconfigured external context is not approved.
				Destination = AiDestinationResolver.Resolve(
					_config, purpose,
					_providerAuthority.Assess(DateTime.UtcNow, OpenAiOptions.FromConfiguration(_config).Scope).State),
				ProviderScope = OpenAiOptions.FromConfiguration(_config).Scope,
				Classification = classification,
				DataCompanyId = companyId,
				PayloadBytes = bytes,
			}, ct);

			if (!decision.Allowed || decision.Approval == null)
				// The reason is a machine code, never business content, so it is safe to return.
				return new AiProxyResult(403,
					JsonSerializer.Serialize(new { error = "ai_egress_denied", reason = decision.Reason?.ToString() }));

			return await _ai.PostAsync(decision.Approval, path, payload, ct);
		}

		// ---------------------------------------------------------------------------------------------
		// EGRESS GUARD. Every public method calls this FIRST, before it reads a row or contacts the
		// external Python service.
		//
		// WHY IT THROWS RATHER THAN RETURNING EMPTY. This service is an egress boundary: whatever it
		// gathers leaves the .NET process. "No company resolved" must therefore stop the operation, not
		// quietly produce an empty analysis that a caller could mistake for a real answer — and certainly
		// not fall back to a default company, which is exactly the defect this repairs.
		//
		// The caller (AccountingController.AiInsights) resolves the company through IRequestCompanyResolver
		// and refuses before it ever gets here, so in practice this is the second line of defence. It
		// exists because the service is public and its next caller may not be as careful.
		private static void RequireCompany(int companyId)
		{
			if (companyId <= 0)
				throw new ArgumentOutOfRangeException(nameof(companyId),
					"AI insights require an explicit, resolved company. There is no default company, and no data " +
					"may be gathered or sent to the AI service without one.");
		}

		/// Share of the ledger the structural ranking may surface for review. Small on purpose: it is
		/// a queue a person is expected to actually work through, not a detection rate.
		private const double AnomalyReviewBudget = 0.01;

		public async Task<AiProxyResult> ScanJournalAnomaliesAsync(int companyId, CancellationToken ct = default)
		{
			RequireCompany(companyId);

			// THE POSTING ITSELF, as a short stable hash of its lines — (account, debit, credit)
			// ordered, so the same posting hashes the same wherever it is computed.
			//
			// This exists because amount + line count CANNOT tell a double-posting from a REVERSAL.
			// Work order 3052 posts JV-2026-003205 "إصدار أمر تشغيل (صرف مواد)" and JV-2026-003206
			// "إلغاء أمر تشغيل (عكس المواد)": both total 95.00 over three lines, and they are mirror
			// images — DR WIP / CR Inventory against DR Inventory / CR WIP. Keyed on amount and line
			// count every cancelled work order in the ledger reads as a double-posting. Keyed on the
			// lines, a reversal differs from its original because debit and credit have swapped.
			var lines = await _context.JournalEntryLines.AsNoTracking()
				.Where(l => _context.JournalEntries.Any(e =>
					e.ID == l.JournalEntryId && e.CompanyID == companyId && e.Status == "Posted"))
				.Select(l => new { l.JournalEntryId, l.AccountId, l.Debit, l.Credit })
				.ToListAsync(ct);

			var signatures = lines
				.GroupBy(l => l.JournalEntryId)
				.ToDictionary(g => g.Key, g =>
				{
					var ordered = g
						.Select(x => string.Format(System.Globalization.CultureInfo.InvariantCulture,
							"{0}:{1:0.####}:{2:0.####}", x.AccountId, x.Debit, x.Credit))
						.OrderBy(x => x, StringComparer.Ordinal);
					var joined = string.Join("|", ordered);
					var hash = System.Security.Cryptography.SHA256.HashData(
						System.Text.Encoding.UTF8.GetBytes(joined));
					return Convert.ToHexString(hash)[..16];
				});

			var rows = await _context.JournalEntries.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.Status == "Posted")
				.Select(e => new
				{
					id = e.ID,
					entryNo = e.EntryNo,
					date = e.EntryDate,
					journalType = e.JournalType,
					sourceType = e.SourceType,
					// THE DOCUMENT THIS ENTRY POSTS, and the reason duplicate detection works at all.
					// It was not sent before, so the detector had to judge duplication on
					// (amount, sourceType, description, day) — and descriptions in this product are
					// TEMPLATED from the thing they describe ("حركة مخزون: {code} - {name}"), so they
					// cannot tell two documents apart. Thirty separate stock movements of one item on
					// one day, with thirty different SourceIds, were reported as a thirty-way
					// double-posting. That is most of why 1,113 of 2,489 entries came back flagged.
					sourceId = e.SourceId,
					description = e.Description,
					amount = _context.JournalEntryLines.Where(l => l.JournalEntryId == e.ID).Sum(l => (decimal?)l.Debit) ?? 0m,
					lineCount = _context.JournalEntryLines.Count(l => l.JournalEntryId == e.ID)
				})
				.ToListAsync(ct);

			var entries = rows.Select(e => new
			{
				e.id, e.entryNo, e.date, e.journalType, e.sourceType, e.sourceId,
				e.description, e.amount, e.lineCount,
				lineSignature = signatures.TryGetValue(e.id, out var sig) ? sig : null,
			}).ToList();
			// CLASSIFICATION: FreeTextBusinessContent, because `description` is user-authored journal
			// narration. It can — and in this product routinely does — carry a customer name, a
			// counterparty, or a case reference.
			//
			// The consequence is deliberate and was NOT engineered around: under the policy matrix, free
			// text is refused at ANY external destination, including an approved one. So this feature
			// FAILS CLOSED at an external processor and is reported as blocked, rather than having its
			// classification softened to keep it working. Removing `description` would make it
			// FinancialAggregate and unblock it — that is a product decision for the data owner, not a
			// change this increment may make unilaterally.
			// reviewBudget is a budget for HUMAN ATTENTION, not a detection threshold. The structural
			// ranking (IsolationForest) can only ever order entries by strangeness, never decide that
			// one is wrong, so it is capped at 1% of the ledger and returned in its own field rather
			// than counted among the findings.
			return await SendAsync(AiEgressPurpose.JournalAnomalyDetection,
				AiDataClassification.FreeTextBusinessContent,
				companyId, "/anomaly/journal", new { entries, reviewBudget = AnomalyReviewBudget }, ct);
		}

		public async Task<AiProxyResult> ForecastCashflowAsync(int companyId, int horizonDays, CancellationToken ct = default)
		{
			RequireCompany(companyId);
			var today = DateTime.Today;

			// OPENING CASH: POSTED ONLY.
			//
			// `Status == "Reversed"` was included alongside "Posted". But a reversal in this ledger is
			// a SEPARATE Posted entry that negates the original (SourceType "Reversal", 324 of them),
			// and the original is then marked Reversed. Counting both counts the money twice: the
			// screen showed an opening balance of 1,874,703.09 where the posted ledger says
			// 1,859,200.35 — overstated by 15,502.74 on this installation alone.
			var cashIds = await _context.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.Code.StartsWith("1101")).Select(a => a.ID).ToListAsync(ct);
			var openingCash = await (from l in _context.JournalEntryLines.AsNoTracking()
									 join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
									 where e.CompanyID == companyId && e.Status == "Posted" && cashIds.Contains(l.AccountId)
									 select (decimal?)(l.Debit - l.Credit)).SumAsync(ct) ?? 0m;

			var customers = await _context.Customers.AsNoTracking().Where(c => c.CompanyID == companyId).ToListAsync(ct);
			var sInv = await _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").ToListAsync(ct);

			// WHAT IS ACTUALLY SETTLED, from ReceiptAllocations — the table that records which receipt
			// paid which invoice.
			//
			// This previously took the customer's TOTAL receipts and spread them across their invoices
			// oldest-first. The open BALANCE comes out similar, but the DATES do not, and a cashflow
			// projection is made of dates: if a customer settled their newest invoice and left the
			// oldest open, FIFO reports the opposite. Measured here, the guess and the record disagree
			// on 72 invoices.
			//
			// Base currency throughout. The old arithmetic subtracted Receipt.Amount (receipt currency)
			// from SalesInvoice.GrandTotal (invoice currency) across three currencies, and 142 invoices
			// have GrandTotal != GrandTotalBase. ArBase and GrandTotalBase are the comparable pair.
			var arSettled = (await _context.ReceiptAllocations.AsNoTracking()
					.Where(a => a.CompanyID == companyId)
					.GroupBy(a => a.SalesInvoiceId)
					.Select(g => new { invoiceId = g.Key, settled = g.Sum(x => x.ArBase) })
					.ToListAsync(ct))
				.ToDictionary(x => x.invoiceId, x => x.settled);

			var termsByCustomer = customers
				.GroupBy(c => c.ID)
				.ToDictionary(g => g.Key, g => g.First().PaymentTermsDays);

			var inflows = new List<object>();
			decimal arOnRecordedTerms = 0m, arOnAssumedTerms = 0m;
			int arSkippedNoBaseAmount = 0;
			foreach (var inv in sInv.OrderBy(i => i.InvoiceDate))
			{
				// NO BASE AMOUNT, NO ENTRY IN THE SCHEDULE. Six invoices here have GrandTotalBase null.
				// Substituting GrandTotal would quietly put a foreign-currency figure into a
				// base-currency total; counting it as zero would quietly drop a receivable. Neither is
				// a projection, so the invoice is excluded and the count is reported.
				if (inv.GrandTotalBase == null) { arSkippedNoBaseAmount++; continue; }
				var open = inv.GrandTotalBase.Value - (arSettled.TryGetValue(inv.ID, out var paid) ? paid : 0m);
				if (open <= 0.005m) continue;
				var terms = termsByCustomer.TryGetValue(inv.CustomerId, out var t) ? t : null;
				// A customer with no recorded terms is NOT a customer who pays on the invoice date.
				// The amount resting on that assumption is carried out so the projection can say so.
				if (terms.HasValue && terms.Value > 0) arOnRecordedTerms += open; else arOnAssumedTerms += open;
				inflows.Add(new { date = inv.InvoiceDate.AddDays(terms ?? 0), amount = open });
			}

			// RECEIPTS THAT SETTLE NOTHING NAMED. Money is in the bank but no allocation row says which
			// invoice it cleared, so it cannot reduce any dated item without guessing which. It is
			// reported as a figure of its own rather than spread oldest-first, which is what the
			// previous code did and what disagreed with the allocation record on 72 invoices.
			var receiptsTotalBase = await _context.Receipts.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.Status == "Posted")
				.SumAsync(r => (decimal?)r.AmountBase, ct) ?? 0m;
			var arUnapplied = Math.Max(0m, receiptsTotalBase - arSettled.Values.Sum());

			var vendors = await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == companyId).ToListAsync(ct);
			var pInv = await _context.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").ToListAsync(ct);

			var apSettled = (await _context.PaymentAllocations.AsNoTracking()
					.Where(a => a.CompanyID == companyId)
					.GroupBy(a => a.PurchaseInvoiceId)
					.Select(g => new { invoiceId = g.Key, settled = g.Sum(x => x.ApBase) })
					.ToListAsync(ct))
				.ToDictionary(x => x.invoiceId, x => x.settled);

			var termsByVendor = vendors
				.GroupBy(v => v.ID)
				.ToDictionary(g => g.Key, g => g.First().PaymentTermsDays);

			var outflows = new List<object>();
			decimal apOnRecordedTerms = 0m, apOnAssumedTerms = 0m;
			int apSkippedNoBaseAmount = 0;
			foreach (var inv in pInv.OrderBy(i => i.InvoiceDate))
			{
				if (inv.GrandTotalBase == null) { apSkippedNoBaseAmount++; continue; }
				var open = inv.GrandTotalBase.Value - (apSettled.TryGetValue(inv.ID, out var paid) ? paid : 0m);
				if (open <= 0.005m) continue;
				var terms = termsByVendor.TryGetValue(inv.VendorId, out var t) ? t : null;
				if (terms.HasValue && terms.Value > 0) apOnRecordedTerms += open; else apOnAssumedTerms += open;
				outflows.Add(new { date = inv.InvoiceDate.AddDays(terms ?? 0), amount = open });
			}

			// PaymentAllocations is EMPTY on this installation, so no purchase invoice is reduced by
			// anything and every posted payment counts as unapplied. That is the honest reading of the
			// data as recorded, and the figure below is what makes it visible instead of making the
			// payables look larger than they are without saying why.
			var paymentsTotalBase = await _context.Payments.AsNoTracking()
				.Where(p => p.CompanyID == companyId && p.Status == "Posted")
				.SumAsync(p => (decimal?)p.AmountBase, ct) ?? 0m;
			var apUnapplied = Math.Max(0m, paymentsTotalBase - apSettled.Values.Sum());

			// CLASSIFICATION: FinancialAggregate. The outbound shape is opening cash plus dated amounts —
			// `inflows`/`outflows` carry only { date, amount }. No customer or vendor name, no invoice
			// number, no free text: the per-party detail is consumed to COMPUTE the schedule and is not
			// part of what leaves.
			return await SendAsync(AiEgressPurpose.CashflowForecast,
				AiDataClassification.FinancialAggregate,
				companyId, "/forecast/cashflow",
				new
				{
					openingCash, asOf = today, horizonDays, inflows, outflows,
					// How much of the schedule rests on a term nobody recorded. On this installation
					// PaymentTermsDays is null for all 42 customers and all 10 vendors, so every open
					// item is dated to its invoice date, lands in the past, and the projection puts it
					// in the first week. That is an assumption, and the screen has to be able to say so.
					arOnRecordedTerms, arOnAssumedTerms, apOnRecordedTerms, apOnAssumedTerms,
					// Money recorded but not tied to a dated item, and items excluded for want of a
					// base amount. Both are carried so the projection can state its own gaps.
					arUnapplied, apUnapplied, arSkippedNoBaseAmount, apSkippedNoBaseAmount
				}, ct);
		}

		public async Task<AiProxyResult> AnalyzeInventoryAsync(int companyId, int slowDays, CancellationToken ct = default)
		{
			RequireCompany(companyId);
			var today = DateTime.Today;
			var since = today.AddDays(-slowDays);
			var since30 = today.AddDays(-30);

			var items = await _context.Items.AsNoTracking()
				.Where(i => i.CompanyID == companyId && i.IsActive)
				.Select(i => new { i.ID, Code = i.ItemCode, i.Name, i.NameEn })
				.ToListAsync(ct);

			// The company-scoped item set, used below to scope ItemWarehouseSettings — which has NO company
			// column of its own and therefore cannot carry a direct predicate.
			var itemIds = items.Select(i => i.ID).ToList();

			var bal = await _context.StockBalances.AsNoTracking()
				.Where(b => b.CompanyID == companyId)
				.GroupBy(b => b.ItemId)
				.Select(g => new { itemId = g.Key, onHand = g.Sum(x => x.QtyOnHand), value = g.Sum(x => x.TotalValue) })
				.ToListAsync(ct);
			var onHandById = bal.ToDictionary(x => x.itemId, x => x.onHand);
			var valueById = bal.ToDictionary(x => x.itemId, x => x.value);

			// DAILY buckets, not a total. The Python side cannot judge whether 2,470 units moved
			// steadily or in one afternoon unless it is told how many DAYS the demand touched, and a
			// rate derived from a total alone is what made this screen propose quantities nobody
			// should act on. The bucketing happens in SQL; the per-item shape is folded in memory.
			var dailyOut = await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.Direction == -1 && m.MovementDate >= since)
				.GroupBy(m => new { m.ItemId, Day = m.MovementDate.Date })
				.Select(g => new { g.Key.ItemId, g.Key.Day, Qty = g.Sum(x => x.QtyBase) })
				.ToListAsync(ct);

			var shape = dailyOut
				.GroupBy(d => d.ItemId)
				.ToDictionary(g => g.Key, g => new
				{
					activeDays = g.Count(),
					spanDays = (int)(g.Max(x => x.Day) - g.Min(x => x.Day)).TotalDays,
					maxDayQty = g.Max(x => x.Qty),
				});

			// ACTIVITY ACROSS THE WHOLE WINDOW, which decides whether "no issues" can mean dormant at
			// all. Measured on 2026-11-08 this window held 4 active days and 35 movements with 18 of
			// 80 stocked items moving — so calling the other 60 items dead capital described the
			// PERIOD, not the items. The analyser needs these two numbers to tell those apart.
			var windowActiveDays = dailyOut.Select(d => d.Day).Distinct().Count();
			var windowMovements = await _context.StockMovements.AsNoTracking()
				.CountAsync(m => m.CompanyID == companyId && m.Direction == -1 && m.MovementDate >= since, ct);

			var out90 = (await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.Direction == -1 && m.MovementDate >= since)
				.GroupBy(m => m.ItemId).Select(g => new { itemId = g.Key, qty = g.Sum(x => x.QtyBase) }).ToListAsync(ct))
				.ToDictionary(x => x.itemId, x => x.qty);
			var out30 = (await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.Direction == -1 && m.MovementDate >= since30)
				.GroupBy(m => m.ItemId).Select(g => new { itemId = g.Key, qty = g.Sum(x => x.QtyBase) }).ToListAsync(ct))
				.ToDictionary(x => x.itemId, x => x.qty);

			// ItemWarehouseSetting carries NO CompanyID column — verified against the entity, not assumed —
			// so it cannot be scoped by a direct predicate the way every other source here is. It was also
			// the one query in this service with no company scoping WHATSOEVER: it grouped reorder points
			// and max quantities across every company in the database.
			//
			// It is scoped through its ITEM instead, which is the only company-bearing relationship it has.
			// `itemIds` came from the company-predicated, globally-filtered Items query above, so the
			// settings are constrained by exactly the same company boundary as the items they describe.
			//
			// The empty-set case is handled explicitly: EF would translate `Contains` over an empty list
			// into a query returning nothing, which is correct, but skipping the round trip makes the
			// intent obvious and costs nothing.
			// SUMMED IN MEMORY, AND ONLY WHEN A VALUE ACTUALLY EXISTS.
			//
			// `Sum` over an all-NULL nullable column answers ZERO, not null — LINQ-to-Objects does it
			// by specification and EF Core emits COALESCE(SUM(col), 0.0) for the same call. Either
			// way "no reorder point has ever been set" arrived downstream as "a reorder point of
			// zero", and `onHand <= 0` is true for every out-of-stock item. On this installation
			// ReorderPoint is NULL in 47 of 49 settings rows, so the screen reported 42 items as
			// below their reorder point when not one of them has a reorder point at all — directly
			// contradicting the panel underneath it, which said nothing could be concluded.
			//
			// A missing measurement must stay missing. `Any(HasValue)` is what keeps it missing.
			var roRows = itemIds.Count == 0
				? new List<(int ItemId, decimal? ReorderPoint, decimal? MaxQty)>()
				: (await _context.ItemWarehouseSettings.AsNoTracking()
					.Where(s => itemIds.Contains(s.ItemId))
					.Select(s => new { s.ItemId, s.ReorderPoint, s.MaxQty })
					.ToListAsync(ct))
					.Select(s => (s.ItemId, s.ReorderPoint, s.MaxQty))
					.ToList();

			var ro = roRows
				.GroupBy(s => s.ItemId)
				.ToDictionary(g => g.Key, g => (
					rp: g.Any(x => x.ReorderPoint.HasValue) ? g.Sum(x => x.ReorderPoint) : (decimal?)null,
					maxq: g.Any(x => x.MaxQty.HasValue) ? g.Sum(x => x.MaxQty) : (decimal?)null));

			decimal Get(Dictionary<int, decimal> d, int k) => d.TryGetValue(k, out var v) ? v : 0m;

			var payload = items.Select(i => new
			{
				itemId = i.ID,
				code = i.Code,
				name = i.Name,
				nameEn = i.NameEn,
				onHand = Get(onHandById, i.ID),
				value = Get(valueById, i.ID),
				out90 = Get(out90, i.ID),
				out30 = Get(out30, i.ID),
				reorderPoint = ro.TryGetValue(i.ID, out var r) ? r.rp : null,
				maxQty = ro.TryGetValue(i.ID, out var r2) ? r2.maxq : null,
				// Null where the item did not move at all, which the analyser reads as "no rate can
				// be estimated" rather than as a rate of zero.
				activeDays = shape.TryGetValue(i.ID, out var sh) ? (int?)sh.activeDays : null,
				spanDays = shape.TryGetValue(i.ID, out var sh2) ? (int?)sh2.spanDays : null,
				maxDayQty = shape.TryGetValue(i.ID, out var sh3) ? (decimal?)sh3.maxDayQty : null
			}).ToList();

			// CLASSIFICATION: FinancialAggregate — the payload carries stock VALUE alongside quantities.
			// Item code/name are catalogue master data rather than user-authored narrative, which is why
			// this is not FreeTextBusinessContent; recorded as a residual consideration for the data owner
			// in a business whose item names are bespoke per customer.
			return await SendAsync(AiEgressPurpose.InventoryAnalysis,
				AiDataClassification.FinancialAggregate,
				companyId, "/inventory/analyze",
				new { asOf = today, slowDays, windowActiveDays, windowMovements, items = payload }, ct);
		}

		// ---------------------------------------------------------------------------------------------
		// REORDER POINTS.
		//
		// WHY THIS DOES NOT REUSE AnalyzeInventoryAsync's PAYLOAD. That method sends out90/out30 — two
		// totals — and the Python side necessarily derives a daily rate as out90/90. That is right only
		// when demand is spread across the window. Measured against this catalogue it is not: 816 of 882
		// outbound movements fall in two months, and one of them put 2,470 units through a single day.
		// Under out90/90 that single day reads as 27 units/day, for ever, and a reorder point built on it
		// would be a standing instruction to buy stock nobody is consuming.
		//
		// So this gathers the DISTRIBUTION, not the total. Per item x warehouse it sends how many
		// distinct DAYS carried demand, over what SPAN, the largest single day, and the sum of squared
		// daily quantities — from which the engine recovers the variance exactly and can tell a steady
		// item from a burst. Rows whose history cannot support a number come back refused, by design.
		//
		// NOTHING IS WRITTEN. The response is advice. Persisting an accepted figure into
		// ItemWarehouseSettings is a separate, user-initiated action with its own authorization.
		// ---------------------------------------------------------------------------------------------
		public async Task<AiProxyResult> ComputeReorderPointsAsync(int companyId, int windowDays,
			double serviceLevel, double defaultLeadTimeDays, CancellationToken ct = default)
		{
			RequireCompany(companyId);
			if (windowDays < 14) windowDays = 14;          // below a fortnight nothing can be graded anyway
			var today = DateTime.Today;
			var since = today.AddDays(-windowDays);

			// One row per (item, warehouse, DAY). The daily bucketing happens in SQL — doing it in memory
			// would mean pulling every movement row across the window.
			var daily = await _context.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.Direction == -1 && m.MovementDate >= since)
				.GroupBy(m => new { m.ItemId, m.WarehouseId, Day = m.MovementDate.Date })
				.Select(g => new { g.Key.ItemId, g.Key.WarehouseId, g.Key.Day, Qty = g.Sum(x => x.QtyBase) })
				.ToListAsync(ct);

			if (daily.Count == 0)
				return await SendAsync(AiEgressPurpose.InventoryAnalysis,
					AiDataClassification.FinancialAggregate,
					companyId, "/inventory/reorder-points",
					new { asOf = today, windowDays, serviceLevel, defaultLeadTimeDays, rows = Array.Empty<object>() }, ct);

			// Collapse the days into a distribution per pair. sumSq is what makes the variance exact:
			// E[X^2] over the window minus the squared mean, with the quiet days contributing zero.
			var pairs = daily
				.GroupBy(d => new { d.ItemId, d.WarehouseId })
				.Select(g => new
				{
					g.Key.ItemId,
					g.Key.WarehouseId,
					activeDays = g.Count(),
					spanDays = (int)(g.Max(x => x.Day) - g.Min(x => x.Day)).TotalDays,
					totalQty = g.Sum(x => x.Qty),
					sumSqQty = g.Sum(x => x.Qty * x.Qty),
					maxDayQty = g.Max(x => x.Qty),
					firstOut = g.Min(x => x.Day),
					lastOut = g.Max(x => x.Day),
				})
				.ToList();

			var itemIds = pairs.Select(p => p.ItemId).Distinct().ToList();
			var whIds = pairs.Select(p => p.WarehouseId).Distinct().ToList();

			// Company-scoped, exactly as the sibling method scopes it. An item outside the company is
			// dropped by the join below rather than silently carried through with a null name.
			var items = (await _context.Items.AsNoTracking()
				.Where(i => i.CompanyID == companyId && itemIds.Contains(i.ID))
				.Select(i => new { i.ID, Code = i.ItemCode, i.Name, i.NameEn })
				.ToListAsync(ct)).ToDictionary(i => i.ID);

			var warehouses = (await _context.Warehouses.AsNoTracking()
				.Where(w => whIds.Contains(w.ID))
				.Select(w => new { w.ID, w.Name })
				.ToListAsync(ct)).ToDictionary(w => w.ID, w => w.Name);

			var balances = (await _context.StockBalances.AsNoTracking()
				.Where(b => b.CompanyID == companyId)
				.Select(b => new { b.ItemId, b.WarehouseId, b.QtyOnHand })
				.ToListAsync(ct)).ToDictionary(b => (b.ItemId, b.WarehouseId), b => b.QtyOnHand);

			// ItemWarehouseSettings carries NO CompanyID of its own — verified against the entity, not
			// assumed — so it is scoped through its ITEM, which is the only company-bearing relationship
			// it has. `itemIds` came from the company-predicated movement query above, so these settings
			// sit inside exactly the same company boundary.
			var settings = (await _context.ItemWarehouseSettings.AsNoTracking()
				.Where(s => itemIds.Contains(s.ItemId))
				.Select(s => new { s.ItemId, s.WarehouseId, s.LeadTimeDays, s.ReorderPoint, s.SafetyStock })
				.ToListAsync(ct)).ToDictionary(s => (s.ItemId, s.WarehouseId));

			var rows = pairs
				.Where(p => items.ContainsKey(p.ItemId))
				.Select(p =>
				{
					var it = items[p.ItemId];
					settings.TryGetValue((p.ItemId, p.WarehouseId), out var st);
					balances.TryGetValue((p.ItemId, p.WarehouseId), out var onHand);
					return new
					{
						itemId = p.ItemId,
						warehouseId = p.WarehouseId,
						code = it.Code,
						name = it.Name,
						nameEn = it.NameEn,
						warehouseName = warehouses.TryGetValue(p.WarehouseId, out var wn) ? wn : null,
						onHand,
						p.activeDays,
						p.spanDays,
						p.totalQty,
						p.sumSqQty,
						p.maxDayQty,
						firstOut = p.firstOut.ToString("yyyy-MM-dd"),
						lastOut = p.lastOut.ToString("yyyy-MM-dd"),
						leadTimeDays = (double?)st?.LeadTimeDays,
						currentReorderPoint = st?.ReorderPoint,
						currentSafetyStock = st?.SafetyStock,
					};
				})
				.ToList();

			// CLASSIFICATION: FinancialAggregate, matching the sibling. This payload carries QUANTITIES
			// only — no stock value, no cost — so it is strictly less disclosing than /inventory/analyze;
			// it is classified alongside it rather than below it because item names are still catalogue
			// master data and the lower class would need the data owner's ruling, not this method's.
			return await SendAsync(AiEgressPurpose.InventoryAnalysis,
				AiDataClassification.FinancialAggregate,
				companyId, "/inventory/reorder-points",
				new { asOf = today, windowDays, serviceLevel, defaultLeadTimeDays, rows }, ct);
		}
	}
}
