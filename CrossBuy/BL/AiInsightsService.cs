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

		public async Task<AiProxyResult> ScanJournalAnomaliesAsync(int companyId, CancellationToken ct = default)
		{
			RequireCompany(companyId);
			var entries = await _context.JournalEntries.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.Status == "Posted")
				.Select(e => new
				{
					id = e.ID,
					entryNo = e.EntryNo,
					date = e.EntryDate,
					journalType = e.JournalType,
					sourceType = e.SourceType,
					description = e.Description,
					amount = _context.JournalEntryLines.Where(l => l.JournalEntryId == e.ID).Sum(l => (decimal?)l.Debit) ?? 0m,
					lineCount = _context.JournalEntryLines.Count(l => l.JournalEntryId == e.ID)
				})
				.ToListAsync(ct);
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
			return await SendAsync(AiEgressPurpose.JournalAnomalyDetection,
				AiDataClassification.FreeTextBusinessContent,
				companyId, "/anomaly/journal", new { entries }, ct);
		}

		public async Task<AiProxyResult> ForecastCashflowAsync(int companyId, int horizonDays, CancellationToken ct = default)
		{
			RequireCompany(companyId);
			var today = DateTime.Today;

			var cashIds = await _context.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.Code.StartsWith("1101")).Select(a => a.ID).ToListAsync(ct);
			var openingCash = await (from l in _context.JournalEntryLines.AsNoTracking()
									 join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
									 where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed") && cashIds.Contains(l.AccountId)
									 select (decimal?)(l.Debit - l.Credit)).SumAsync(ct) ?? 0m;

			var customers = await _context.Customers.AsNoTracking().Where(c => c.CompanyID == companyId).ToListAsync(ct);
			var sInv = await _context.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").ToListAsync(ct);
			var receipts = await _context.Receipts.AsNoTracking().Where(r => r.CompanyID == companyId && r.Status == "Posted").ToListAsync(ct);
			var inflows = new List<object>();
			foreach (var c in customers)
			{
				var paid = receipts.Where(r => r.CustomerId == c.ID).Sum(r => r.Amount);
				foreach (var inv in sInv.Where(i => i.CustomerId == c.ID).OrderBy(i => i.InvoiceDate))
				{
					var open = inv.GrandTotal;
					if (paid > 0) { var used = Math.Min(paid, open); open -= used; paid -= used; }
					if (open <= 0) continue;
					inflows.Add(new { date = inv.InvoiceDate.AddDays(c.PaymentTermsDays ?? 0), amount = open });
				}
			}

			var vendors = await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == companyId).ToListAsync(ct);
			var pInv = await _context.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").ToListAsync(ct);
			var payments = await _context.Payments.AsNoTracking().Where(p => p.CompanyID == companyId && p.Status == "Posted").ToListAsync(ct);
			var outflows = new List<object>();
			foreach (var v in vendors)
			{
				var paid = payments.Where(p => p.VendorId == v.ID).Sum(p => p.Amount);
				foreach (var inv in pInv.Where(i => i.VendorId == v.ID).OrderBy(i => i.InvoiceDate))
				{
					var open = inv.GrandTotal;
					if (paid > 0) { var used = Math.Min(paid, open); open -= used; paid -= used; }
					if (open <= 0) continue;
					outflows.Add(new { date = inv.InvoiceDate.AddDays(v.PaymentTermsDays ?? 0), amount = open });
				}
			}

			// CLASSIFICATION: FinancialAggregate. The outbound shape is opening cash plus dated amounts —
			// `inflows`/`outflows` carry only { date, amount }. No customer or vendor name, no invoice
			// number, no free text: the per-party detail is consumed to COMPUTE the schedule and is not
			// part of what leaves.
			return await SendAsync(AiEgressPurpose.CashflowForecast,
				AiDataClassification.FinancialAggregate,
				companyId, "/forecast/cashflow",
				new { openingCash, asOf = today, horizonDays, inflows, outflows }, ct);
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
			var ro = itemIds.Count == 0
				? new Dictionary<int, (decimal? rp, decimal? maxq)>()
				: (await _context.ItemWarehouseSettings.AsNoTracking()
					.Where(s => itemIds.Contains(s.ItemId))
					.GroupBy(s => s.ItemId)
					.Select(g => new { itemId = g.Key, rp = g.Sum(x => x.ReorderPoint), maxq = g.Sum(x => x.MaxQty) })
					.ToListAsync(ct))
					.ToDictionary(x => x.itemId, x => (x.rp, x.maxq));

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
				maxQty = ro.TryGetValue(i.ID, out var r2) ? r2.maxq : null
			}).ToList();

			// CLASSIFICATION: FinancialAggregate — the payload carries stock VALUE alongside quantities.
			// Item code/name are catalogue master data rather than user-authored narrative, which is why
			// this is not FreeTextBusinessContent; recorded as a residual consideration for the data owner
			// in a business whose item names are bespoke per customer.
			return await SendAsync(AiEgressPurpose.InventoryAnalysis,
				AiDataClassification.FinancialAggregate,
				companyId, "/inventory/analyze",
				new { asOf = today, slowDays, items = payload }, ct);
		}
	}
}
