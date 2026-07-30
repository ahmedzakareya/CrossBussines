using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class AiInsightsService : IAiInsightsService
	{
		private readonly CrossDbContext _context;
		private readonly IAiService _ai;

		public AiInsightsService(CrossDbContext context, IAiService ai)
		{
			_context = context;
			_ai = ai;
		}

		public async Task<AiProxyResult> ScanJournalAnomaliesAsync(int companyId, CancellationToken ct = default)
		{
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
			return await _ai.PostAsync("/anomaly/journal", new { entries }, ct);
		}

		public async Task<AiProxyResult> ForecastCashflowAsync(int companyId, int horizonDays, CancellationToken ct = default)
		{
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

			return await _ai.PostAsync("/forecast/cashflow",
				new { openingCash, asOf = today, horizonDays, inflows, outflows }, ct);
		}

		public async Task<AiProxyResult> AnalyzeInventoryAsync(int companyId, int slowDays, CancellationToken ct = default)
		{
			var today = DateTime.Today;
			var since = today.AddDays(-slowDays);
			var since30 = today.AddDays(-30);

			var items = await _context.Items.AsNoTracking()
				.Where(i => i.CompanyID == companyId && i.IsActive)
				.Select(i => new { i.ID, Code = i.ItemCode, i.Name, i.NameEn })
				.ToListAsync(ct);

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

			var ro = (await _context.ItemWarehouseSettings.AsNoTracking()
				.GroupBy(s => s.ItemId)
				.Select(g => new { itemId = g.Key, rp = g.Sum(x => x.ReorderPoint), maxq = g.Sum(x => x.MaxQty) }).ToListAsync(ct))
				.ToDictionary(x => x.itemId);

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

			return await _ai.PostAsync("/inventory/analyze",
				new { asOf = today, slowDays, items = payload }, ct);
		}
	}
}
