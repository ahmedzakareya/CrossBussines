using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// P3-6 three-way match: PO (ordered) ↔ GR (received) ↔ Invoice (billed).
	// Flags lines where billed qty exceeds received, or PO price vs received cost, beyond tolerance.
	public class MatchLine
	{
		public int ItemId { get; set; }
		public string ItemDesc { get; set; } = "";
		public decimal OrderedQty { get; set; }
		public decimal ReceivedQty { get; set; }
		public decimal PoPrice { get; set; }
		public decimal ReceivedCost { get; set; }
		public decimal QtyVarPct { get; set; }
		public decimal PriceVarPct { get; set; }
		public bool Ok { get; set; }
	}

	public class MatchResult
	{
		public bool Ok { get; set; } = true;
		public bool Received { get; set; }
		public decimal QtyTolerancePct { get; set; }
		public decimal PriceTolerancePct { get; set; }
		public List<MatchLine> Lines { get; set; } = new();
		public List<string> Failures { get; set; } = new();   // human-readable offending lines
	}

	public interface IThreeWayMatchService
	{
		Task<MatchResult> CheckPoAsync(int companyId, int poId);
	}

	public class ThreeWayMatchService : IThreeWayMatchService
	{
		private readonly CrossDbContext _context;
		public ThreeWayMatchService(CrossDbContext context) { _context = context; }

		public const decimal QtyTolPct = 5m;     // allowed qty variance (ordered vs received)
		public const decimal PriceTolPct = 2m;    // allowed price variance (PO price vs received cost)

		public async Task<MatchResult> CheckPoAsync(int companyId, int poId)
		{
			var res = new MatchResult { QtyTolerancePct = QtyTolPct, PriceTolerancePct = PriceTolPct };
			var po = await _context.PurchaseOrders.Include(p => p.Lines).AsNoTracking()
				.FirstOrDefaultAsync(p => p.ID == poId && p.CompanyID == companyId);
			if (po == null) { res.Ok = false; res.Failures.Add("Purchase order not found"); return res; }

			var receipts = await _context.GoodsReceipts.AsNoTracking()
				.Where(g => g.CompanyID == companyId && g.PurchaseOrderId == poId && g.Status == "Posted").Select(g => g.ID).ToListAsync();
			res.Received = receipts.Count > 0;
			// received GR lines for this PO, grouped by the PO line they fulfil
			var grLines = await _context.GoodsReceiptLines.AsNoTracking()
				.Where(l => receipts.Contains(l.GoodsReceiptId) && l.PurchaseOrderLineId != null)
				.Select(l => new { l.PurchaseOrderLineId, l.Qty, l.UnitCost }).ToListAsync();

			foreach (var pl in po.Lines.Where(x => x.ItemId != null).OrderBy(x => x.LineNo))
			{
				var fulfil = grLines.Where(g => g.PurchaseOrderLineId == pl.ID).ToList();
				var receivedQty = fulfil.Sum(g => g.Qty);
				var recvCost = receivedQty > 0 ? fulfil.Sum(g => g.Qty * g.UnitCost) / receivedQty : pl.UnitPrice;
				var qtyVar = pl.Qty > 0 ? Math.Abs(pl.Qty - receivedQty) / pl.Qty * 100m : 0m;
				var priceVar = pl.UnitPrice > 0 ? Math.Abs(pl.UnitPrice - recvCost) / pl.UnitPrice * 100m : 0m;
				var ok = qtyVar <= QtyTolPct && priceVar <= PriceTolPct;
				var line = new MatchLine
				{
					ItemId = pl.ItemId!.Value, ItemDesc = pl.ItemDescription ?? ("#" + pl.ItemId),
					OrderedQty = pl.Qty, ReceivedQty = receivedQty, PoPrice = pl.UnitPrice, ReceivedCost = Math.Round(recvCost, 2),
					QtyVarPct = Math.Round(qtyVar, 2), PriceVarPct = Math.Round(priceVar, 2), Ok = ok
				};
				res.Lines.Add(line);
				if (!ok)
				{
					res.Ok = false;
					res.Failures.Add($"{line.ItemDesc}: ordered {pl.Qty:0.##} / received {receivedQty:0.##} (quantity variance {line.QtyVarPct:0.#}%, price variance {line.PriceVarPct:0.#}%)");
				}
			}
			return res;
		}
	}
}
