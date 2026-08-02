using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class IntegrityCheck
	{
		public string Key { get; set; } = "";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public decimal Expected { get; set; }
		public decimal Actual { get; set; }
		public bool Ok { get; set; }
		public string? Note { get; set; }
		public string? Detail { get; set; }       // drill-down URL
		public decimal Diff => Math.Round(Actual - Expected, 2);
	}

	public interface IIntegrityCheckService
	{
		Task<List<IntegrityCheck>> RunAsync(int companyId);
		Task<(IntegrityCheckRun run, List<IntegrityCheck> checks)> RunAndLogAsync(int companyId, string source);
		Task<List<IntegrityCheckRun>> RecentRunsAsync(int companyId, int take = 30);
	}

	public class IntegrityCheckService : IIntegrityCheckService
	{
		private readonly CrossDbContext _db;
		private readonly INotificationService _notify;
		public IntegrityCheckService(CrossDbContext db, INotificationService notify) { _db = db; _notify = notify; }

		private static decimal R(decimal d) => Math.Round(d, 2);

		private async Task<decimal> GlNetAsync(int companyId, List<int> accIds)
		{
			if (accIds.Count == 0) return 0;
			return await _db.JournalEntryLines.AsNoTracking().Where(l => accIds.Contains(l.AccountId)).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0m;
		}
		private async Task<List<int>> AccByCodeAsync(int companyId, params string[] codes) =>
			await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && codes.Contains(a.Code)).Select(a => a.ID).ToListAsync();

		public async Task<List<IntegrityCheck>> RunAsync(int companyId)
		{
			var res = new List<IntegrityCheck>();

			// ---- 1) stock valuation == inventory GL ----
			var invAccIds = await _db.ItemCategories.AsNoTracking().Where(c => c.CompanyID == companyId && c.InventoryAccountId != null).Select(c => c.InventoryAccountId!.Value).Distinct().ToListAsync();
			decimal stockVal = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId).SumAsync(b => (decimal?)b.TotalValue) ?? 0m;
			decimal invGl = await GlNetAsync(companyId, invAccIds);
			res.Add(new IntegrityCheck { Key = "stock_gl", NameAr = "قيمة المخزون == حساب المخزون", NameEn = "Stock value == inventory GL", Expected = R(invGl), Actual = R(stockVal), Ok = R(stockVal) == R(invGl), Detail = "/Inventory/StockBalances" });

			// ---- 2) FIFO cost layers == on-hand qty (FIFO items) ----
			var fifoItems = await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && i.CostingMethod == "FIFO").Select(i => i.ID).ToListAsync();
			decimal layerQty = await _db.StockCostLayers.AsNoTracking().Where(l => l.CompanyID == companyId && fifoItems.Contains(l.ItemId)).SumAsync(l => (decimal?)l.QtyRemaining) ?? 0m;
			decimal fifoOnHand = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && fifoItems.Contains(b.ItemId)).SumAsync(b => (decimal?)b.QtyOnHand) ?? 0m;
			res.Add(new IntegrityCheck { Key = "fifo_layers", NameAr = "طبقات FIFO == رصيد أصناف FIFO", NameEn = "FIFO layers == FIFO on-hand", Expected = R(fifoOnHand), Actual = R(layerQty), Ok = R(layerQty) == R(fifoOnHand), Detail = "/Inventory/Batches" });

			// ---- 3) AR control == customer subledger ----
			var arIds = await _db.Customers.AsNoTracking().Where(c => c.CompanyID == companyId).Select(c => c.ControlAccountId).Distinct().ToListAsync();
			decimal arGl = await GlNetAsync(companyId, arIds);
			// Multi-Currency: subledger compares in functional currency — use the *Base columns (fall back to source for legacy rows)
			decimal arSub = (await _db.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").SumAsync(i => (decimal?)(i.GrandTotalBase ?? i.GrandTotal)) ?? 0m)
						  - (await _db.Receipts.AsNoTracking().Where(r => r.CompanyID == companyId && r.Status == "Posted").SumAsync(r => (decimal?)(r.AmountBase ?? r.Amount)) ?? 0m)
						  - (await _db.SalesReturns.AsNoTracking().Where(s => s.CompanyID == companyId && s.Status == "Posted").SumAsync(s => (decimal?)(s.GrandTotalBase ?? s.GrandTotal)) ?? 0m);   // credit notes reduce AR
			res.Add(new IntegrityCheck { Key = "ar_sub", NameAr = "حساب العملاء == دفتر العملاء المساعد", NameEn = "AR control == customer subledger", Expected = R(arSub), Actual = R(arGl), Ok = R(arGl) == R(arSub), Detail = "/Accounting/Journals" });

			// ---- 4) AP control == vendor subledger (credit-normal) ----
			var apIds = await _db.Vendors.AsNoTracking().Where(v => v.CompanyID == companyId).Select(v => v.ControlAccountId).Distinct().ToListAsync();
			decimal apGl = -await GlNetAsync(companyId, apIds);
			decimal apSub = (await _db.PurchaseInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && i.Status == "Posted").SumAsync(i => (decimal?)(i.GrandTotalBase ?? i.GrandTotal)) ?? 0m)
						  - (await _db.Payments.AsNoTracking().Where(p => p.CompanyID == companyId && p.Status == "Posted").SumAsync(p => (decimal?)(p.AmountBase ?? p.Amount)) ?? 0m)
						  - (await _db.PurchaseReturns.AsNoTracking().Where(r => r.CompanyID == companyId && r.Status == "Posted").SumAsync(r => (decimal?)(r.GrandTotalBase ?? r.GrandTotal)) ?? 0m);   // debit notes reduce AP
			res.Add(new IntegrityCheck { Key = "ap_sub", NameAr = "حساب الموردين == دفتر الموردين المساعد", NameEn = "AP control == vendor subledger", Expected = R(apSub), Actual = R(apGl), Ok = R(apGl) == R(apSub), Detail = "/Accounting/Journals" });

			// ---- 5) goods-in-transit (110302) == 0 (completed transfers net to zero) ----
			var transitIds = await AccByCodeAsync(companyId, "110302");
			decimal transit = await GlNetAsync(companyId, transitIds);
			res.Add(new IntegrityCheck { Key = "transit_zero", NameAr = "مخزون العبور == صفر", NameEn = "Goods-in-transit == 0", Expected = 0m, Actual = R(transit), Ok = R(transit) == 0m, Detail = "/Inventory/StockTransfers" });

			// ---- 6) GRNI == value of received-but-unbilled goods ----
			var grniIds = await _db.ItemCategories.AsNoTracking().Where(c => c.CompanyID == companyId && c.GrniAccountId != null).Select(c => c.GrniAccountId!.Value).Distinct().ToListAsync();
			decimal grniGl = -await GlNetAsync(companyId, grniIds);     // GRNI is a credit-normal liability
			decimal unbilled = await _db.GoodsReceipts.AsNoTracking().Where(g => g.CompanyID == companyId && g.Status == "Posted" && g.InvoiceId == null).SumAsync(g => (decimal?)g.TotalCost) ?? 0m;
			res.Add(new IntegrityCheck { Key = "grni", NameAr = "GRNI == قيمة الاستلامات غير المفوترة", NameEn = "GRNI == unbilled receipts", Expected = R(unbilled), Actual = R(grniGl), Ok = R(grniGl) == R(unbilled), Detail = "/Inventory/GoodsReceipts" });

			// ---- 7) WIP control (1105) == Σ WipBalance of open work orders ----
			// With staged work orders, materials are issued to WIP at Release and cleared at completion, so WIP
			// is legitimately non-zero while orders are in progress. The invariant: the 1105 GL balance must equal
			// the WIP held by all open orders (Released / InProgress / Done-not-Closed). Immediate + direct-complete
			// orders issue & clear in one transaction → their WipBalance is 0, contributing nothing. This guards
			// manual JEs on 1105 and any partial-posting bug that strands WIP.
			var wipIds = await AccByCodeAsync(companyId, "1105");
			decimal wipGl = await GlNetAsync(companyId, wipIds);
			decimal openWip = await _db.ManufWorkOrders.AsNoTracking()
				.Where(w => w.CompanyID == companyId && w.Status != "Completed" && w.Status != "Cancelled" && w.Status != "Closed")
				.SumAsync(w => (decimal?)w.WipBalance) ?? 0m;
			res.Add(new IntegrityCheck { Key = "wip_gl", NameAr = "حساب الإنتاج تحت التشغيل == WIP الأوامر الجارية", NameEn = "WIP control (1105) == open work-order WIP", Expected = R(openWip), Actual = R(wipGl), Ok = R(wipGl) == R(openWip), Detail = "/Inventory/WorkOrders" });

			// ---- 8) trial balance balanced (ΣDr == ΣCr) ----
			decimal td = await _db.JournalEntryLines.AsNoTracking().SumAsync(l => (decimal?)l.Debit) ?? 0m;
			decimal tc = await _db.JournalEntryLines.AsNoTracking().SumAsync(l => (decimal?)l.Credit) ?? 0m;
			res.Add(new IntegrityCheck { Key = "tb_balanced", NameAr = "ميزان المراجعة متوازن (Σمدين == Σدائن)", NameEn = "Trial balance balanced", Expected = R(td), Actual = R(tc), Ok = R(td) == R(tc), Detail = "/Accounting/Journals" });

			// ============================================================================================
			// HM-1-أ ب-3 — three permanent checks. Baselines FROZEN at fix-apply (see deploy/AUDIT-DEVIATIONS.md).
			// Legacy (pre-fix) findings are COUNTED and shown but never raise failedCount; only NEW violations fail.
			var cutoff = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);   // fix-apply date (legacy = before this)
			const int JvBaselineMaxNo = 1162;         // JV numbers ≤ this predate the fix (the 254 documented gaps live here)
			const int PosOrderBaselineMaxId = 3352;   // ReceiptNo dups among orders ≤ this are the documented legacy dups
			var testInvoiceIds = new HashSet<int> { 26, 27, 6174, 6175, 6176, 6177, 6178, 6179 };   // frozen test-data (exclude by ID)

			// ---- 9) COGS impact: every STOCKABLE sale line has a stock movement WITH a COGS effect (JE posted) ----
			var saleLines = await (from l in _db.SalesInvoiceLines.AsNoTracking()
								   join inv in _db.SalesInvoices.AsNoTracking() on l.SalesInvoiceId equals inv.ID
								   join it in _db.Items.AsNoTracking() on l.ItemId equals it.ID
								   where inv.CompanyID == companyId && inv.Status == "Posted" && l.ItemId != null && l.WarehouseId != null && l.Qty > 0
								   select new { LineId = l.ID, InvId = l.SalesInvoiceId, it.ItemType, inv.CreatedAt }).ToListAsync();
			var saleMoves = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == companyId && m.SourceType == "SalesInvoice")
				.Select(m => new { m.SourceId, m.SourceLineId, m.JournalEntryId }).ToListAsync();
			var moveByLine = saleMoves.Where(m => m.SourceLineId != null)
				.GroupBy(m => $"{m.SourceId}:{m.SourceLineId}").ToDictionary(g => g.Key, g => g.First());
			int newNoCogs = 0, legacyNoCogs = 0, zeroCostCounted = 0, anomalyNonStock = 0;
			foreach (var s in saleLines)
			{
				if (!ItemTypes.RequiresStock(s.ItemType)) { anomalyNonStock++; continue; }   // class: non-stockable carrying a warehouse
				moveByLine.TryGetValue($"{s.InvId}:{s.LineId}", out var mv);
				if (mv == null)
				{
					// NEW only when the invoice is dated on/after the fix AND is not frozen test data; null date ⇒ legacy.
					bool isNew = s.CreatedAt.HasValue && s.CreatedAt.Value >= cutoff && !testInvoiceIds.Contains(s.InvId);
					if (isNew) newNoCogs++;          // class: NEW failure
					else legacyNoCogs++;             // class: legacy debt (pre-fix or frozen test data)
				}
				else if (mv.JournalEntryId == null) zeroCostCounted++;   // class: zero-cost movement (no COGS) — default Allow, counted
			}
			res.Add(new IntegrityCheck { Key = "cogs_impact", NameAr = "كل سطر بيع لصنف مخزني له حركة بأثر COGS", NameEn = "Stockable sale lines have a COGS-effective movement",
				Expected = 0, Actual = newNoCogs, Ok = newNoCogs == 0,
				Note = $"new(fail)={newNoCogs} · legacy(no-COGS)={legacyNoCogs} · zero-cost(counted)={zeroCostCounted} · non-stock-with-wh(anomaly)={anomalyNonStock}", Detail = "/Accounting/Journals" });

			// ---- 10) Journal numbering: NO duplicates (fail); gaps are counted (legacy vs new) never fail; gap-rate warns ----
			var jvNums = (await _db.JournalEntries.AsNoTracking().Where(e => e.EntryNo != null && e.EntryNo.StartsWith("JV-")).Select(e => e.EntryNo!).ToListAsync())
				.Select(s => int.TryParse(s.Substring(Math.Max(0, s.Length - 6)), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n > 0).ToList();
			int jvTotal = jvNums.Count, jvDistinct = jvNums.Distinct().Count(), jvDup = jvTotal - jvDistinct;
			int jvMax = jvNums.Count > 0 ? jvNums.Max() : 0;
			int jvGaps = jvMax - jvDistinct;
			int newIssued = jvMax > JvBaselineMaxNo ? jvMax - JvBaselineMaxNo : 0;
			int newGaps = newIssued > 0 ? newIssued - jvNums.Where(n => n > JvBaselineMaxNo).Distinct().Count() : 0;
			double gapRate = newIssued > 0 ? (double)newGaps / newIssued : 0;
			// A gap = a JV number allocated (in the isolated context) for a transaction that then ROLLED BACK, so the
			// new-period gap rate IS the transaction rollback rate. After fixing the swallowed stock-issue, a healthy system
			// rolls back only on genuine operational rejections (out-of-stock with negative disabled, closed period, unmapped
			// account) — well under a couple of %. Warn above 10% (≈1 in 10 sale attempts failing is operationally abnormal —
			// investigate stock availability / account config / period status, NOT the ledger, which stays perfect), and only
			// once the sample is meaningful (≥50 new numbers) so a single early rollback doesn't trip a false alarm.
			bool rateWarn = newIssued >= 50 && gapRate > 0.10;
			res.Add(new IntegrityCheck { Key = "entryno_dup", NameAr = "لا تكرار في أرقام القيود (الفجوات مقبولة)", NameEn = "No duplicate entry numbers (gaps allowed)",
				Expected = 0, Actual = jvDup, Ok = jvDup == 0,
				Note = $"dup(fail)={jvDup} · gaps total={jvGaps} (legacy≤{JvBaselineMaxNo}, new>{JvBaselineMaxNo}={newGaps})" + (rateWarn ? $" · WARN new-period rollback rate {gapRate:P0} (>10%)" : ""), Detail = "/Accounting/Journals" });

			// ---- 11) No DUPLICATE ReceiptNo within a terminal (detective — replaces the cancelled unique index) ----
			var recOrders = await _db.PosOrders.AsNoTracking().Where(o => o.ReceiptNo != null).Select(o => new { o.ID, o.TerminalId, o.ReceiptNo }).ToListAsync();
			var dupGroups = recOrders.GroupBy(o => new { o.TerminalId, o.ReceiptNo }).Where(g => g.Count() > 1).ToList();
			// DEV-2026-008 (baseline amendment): 3 dup groups on terminal 1004 (T1-OFF001/002, T1-9E01) were created by the
			// offline-sync dev tests that hardcoded ReceiptNos onto an EXISTING terminal. The tests are now fixed (dedicated ZZ
			// terminal + AllocateReceiptNoAsync — no re-run can add more), but these 3 groups are paid orders with invoices/JEs/
			// movements (a ReceiptNo is a tax document) so they are NOT deleted — they are documented legacy, like the JV baseline.
			var dev2026_008 = new HashSet<string> { "1004|T1-OFF001", "1004|T1-OFF002", "1004|T1-9E01" };
			int legacyRecDup = dupGroups.Count(g => g.Max(o => o.ID) <= PosOrderBaselineMaxId || dev2026_008.Contains($"{g.Key.TerminalId}|{g.Key.ReceiptNo}"));
			int newRecDup = dupGroups.Count(g => g.Max(o => o.ID) > PosOrderBaselineMaxId && !dev2026_008.Contains($"{g.Key.TerminalId}|{g.Key.ReceiptNo}"));
			res.Add(new IntegrityCheck { Key = "receiptno_dup", NameAr = "لا تكرار لرقم الإيصال داخل الترمينال", NameEn = "No duplicate receipt no within a terminal",
				Expected = 0, Actual = newRecDup, Ok = newRecDup == 0,
				Note = $"new(fail)={newRecDup} · legacy(baseline)={legacyRecDup}", Detail = "/Pos/Terminals" });

			// ---- 12) receipts OUT OF their terminal's series (HM-D5-أ): after the scrape fix a mismatched offline order is
			//         still posted under its ORIGINAL number, leaving a row whose ReceiptNo isn't in its terminal's prefix
			//         series (e.g. T1-9E01 on terminal 1004 = RC6-). receiptno_dup can't see this (it looks for duplicates).
			//         COUNTED, legacy (≤ baseline order id) vs new shown separately; never raises failedCount. ----
			var termPfx = await _db.PosTerminals.AsNoTracking().ToDictionaryAsync(t => t.ID, t => t.ReceiptPrefix ?? "");
			var recSeries = await _db.PosOrders.AsNoTracking().Where(o => o.ReceiptNo != null && o.TerminalId != null)
				.Select(o => new { o.ID, o.TerminalId, o.ReceiptNo }).ToListAsync();
			int legacyOoS = 0, newOoS = 0; var oosSample = new List<string>();
			foreach (var o in recSeries)
			{
				var pfx = termPfx.TryGetValue(o.TerminalId!.Value, out var p) ? p : "";
				if (pfx.Length > 0 && o.ReceiptNo!.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) continue;   // in series
				if (o.ID <= PosOrderBaselineMaxId) legacyOoS++; else newOoS++;
				if (oosSample.Count < 6) oosSample.Add($"#{o.ID} {o.ReceiptNo}→term{o.TerminalId}({pfx})");
			}
			res.Add(new IntegrityCheck { Key = "receiptno_out_of_series", NameAr = "إيصالات خارج سلسلة ترمينالها (معدودة)", NameEn = "Receipts out of their terminal's series (counted)",
				Expected = 0, Actual = newOoS, Ok = true,   // COUNTED — legacy AND new both counted, never fails
				Note = $"legacy(≤{PosOrderBaselineMaxId})={legacyOoS} · new={newOoS}" + (oosSample.Count > 0 ? " · " + string.Join(", ", oosSample) : ""), Detail = "/Pos/Terminals" });

			// ---- 13) POS sync conflicts (POS-9e + HM-D5-أ ReceiptNoMismatch): COUNTED, never fails. Shown by status
			//         (Open / Acknowledged) AND type, so a resolvable-by-ACK conflict doesn't sit unseen on a screen nobody opens. ----
			var conflictRows = await _db.PosSyncConflicts.AsNoTracking().Where(c => c.CompanyId == companyId)
				.GroupBy(c => new { c.Status, c.ConflictType }).Select(g => new { g.Key.Status, g.Key.ConflictType, N = g.Count() }).ToListAsync();
			int openTotal = conflictRows.Where(x => x.Status == "Open").Sum(x => x.N);
			int ackTotal = conflictRows.Where(x => x.Status == "Acknowledged").Sum(x => x.N);
			string byType = conflictRows.Count == 0 ? "لا تعارضات" : string.Join(" · ", conflictRows.Select(x => $"{x.ConflictType}[{x.Status}]={x.N}"));
			res.Add(new IntegrityCheck { Key = "sync_conflicts", NameAr = "تعارضات مزامنة نقاط البيع (معدودة)", NameEn = "POS sync conflicts (counted)",
				Expected = 0, Actual = openTotal, Ok = true,   // COUNTED — Actual = still-OPEN count; Acknowledged shown in Note; never raises failedCount
				Note = $"مفتوح={openTotal} · مُقَرّ به={ackTotal} · {byType}", Detail = "/Pos/SyncConflicts" });

			// ==================== HM-D6: per-item balance integrity ====================
			var balRows = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId).Select(b => new { b.ItemId, b.WarehouseId, b.QtyOnHand, b.TotalValue }).ToListAsync();
			var moveAgg = (await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == companyId).Select(m => new { m.ItemId, m.WarehouseId, m.Direction, m.QtyBase, m.TotalCost }).ToListAsync())
				.GroupBy(m => (m.ItemId, m.WarehouseId)).ToDictionary(g => g.Key, g => (Qty: g.Sum(x => x.Direction * x.QtyBase), Val: g.Sum(x => x.Direction * x.TotalCost)));
			var codeById = await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId).ToDictionaryAsync(i => i.ID, i => i.ItemCode);
			var valBaselineCodes = new HashSet<string> { "ITM-0001", "MFGT-FIN" };   // frozen VALUE baseline (documented; see deploy/AUDIT-DEVIATIONS.md)
			int qtyFail = 0, valBaseline = 0, valNew = 0; var qtyBad = new List<string>(); var valNewList = new List<string>();
			foreach (var b in balRows)
			{
				moveAgg.TryGetValue((b.ItemId, b.WarehouseId), out var mv);
				if (Math.Abs(b.QtyOnHand - mv.Qty) > 0.001m) { qtyFail++; if (qtyBad.Count < 8) qtyBad.Add($"{codeById.GetValueOrDefault(b.ItemId, b.ItemId.ToString())} wh{b.WarehouseId}: bal {b.QtyOnHand:0.##}≠mv {mv.Qty:0.##}"); }
				if (Math.Abs(b.TotalValue - mv.Val) > 0.01m)
				{
					var code = codeById.GetValueOrDefault(b.ItemId, "");
					if (valBaselineCodes.Contains(code)) valBaseline++;
					else { valNew++; if (valNewList.Count < 8) valNewList.Add($"{code} wh{b.WarehouseId}: Δ{Math.Round(b.TotalValue - mv.Val, 2)}"); }
				}
			}
			// (a) QTY: balance == net movements — a QUANTITY diff is a REAL failure (raises failedCount). "الرقم الحرج".
			res.Add(new IntegrityCheck { Key = "bal_qty_vs_moves", NameAr = "لكل صنف: الرصيد = صافي حركاته (الكمية)", NameEn = "Per-item: balance qty == net movements",
				Expected = 0, Actual = qtyFail, Ok = qtyFail == 0,
				Note = qtyFail == 0 ? "متطابق" : string.Join(" · ", qtyBad), Detail = "/Inventory/StockBalances" });
			// (b) FIFO layers per item == on-hand (FIFO items only; Average items have no layers) — REAL failure.
			var fifoSet = (await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && i.CostingMethod == "FIFO").Select(i => i.ID).ToListAsync()).ToHashSet();
			var layerByItem = (await _db.StockCostLayers.AsNoTracking().Where(l => l.CompanyID == companyId).Select(l => new { l.ItemId, l.WarehouseId, l.QtyRemaining }).ToListAsync())
				.GroupBy(l => (l.ItemId, l.WarehouseId)).ToDictionary(g => g.Key, g => g.Sum(x => x.QtyRemaining));
			int layerFail = 0; var layerBad = new List<string>();
			foreach (var b in balRows.Where(b => fifoSet.Contains(b.ItemId)))
			{ layerByItem.TryGetValue((b.ItemId, b.WarehouseId), out var lq); if (Math.Abs(b.QtyOnHand - lq) > 0.001m) { layerFail++; if (layerBad.Count < 8) layerBad.Add($"{codeById.GetValueOrDefault(b.ItemId, b.ItemId.ToString())}: bal {b.QtyOnHand:0.##}≠layers {lq:0.##}"); } }
			res.Add(new IntegrityCheck { Key = "fifo_layers_per_item", NameAr = "لكل صنف FIFO: الرصيد = مجموع طبقاته", NameEn = "Per-item FIFO: balance == Σ layers",
				Expected = 0, Actual = layerFail, Ok = layerFail == 0, Note = layerFail == 0 ? "متطابق" : string.Join(" · ", layerBad), Detail = "/Inventory/Batches" });
			// (b2) BATCH-tracked items: StockBatches has NO quantity column (it is a BatchNo/Expiry DEFINITION table), so the
			// per-batch quantity lives in the movements' BatchId. The batch-integrity invariant is therefore "no batch's net
			// movements go negative" (a real FEFO/oversell failure); the "Σ per-batch == balance" identity is already the
			// bal_qty_vs_moves universal check. Covers the TrackExpiry/TrackBatch slice.
			var batchItems = (await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && (i.TrackExpiry || i.TrackBatch)).Select(i => i.ID).ToListAsync()).ToHashSet();
			var batchMoves = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == companyId && m.BatchId != null && batchItems.Contains(m.ItemId))
				.Select(m => new { m.ItemId, m.WarehouseId, m.BatchId, m.Direction, m.QtyBase }).ToListAsync();
			int negBatch = 0; var negBatchBad = new List<string>();
			foreach (var g in batchMoves.GroupBy(m => (m.ItemId, m.WarehouseId, m.BatchId)))
			{ var net = g.Sum(x => x.Direction * x.QtyBase); if (net < -0.001m) { negBatch++; if (negBatchBad.Count < 8) negBatchBad.Add($"{codeById.GetValueOrDefault(g.Key.ItemId, g.Key.ItemId.ToString())} batch#{g.Key.BatchId}: {net:0.##}"); } }
			res.Add(new IntegrityCheck { Key = "batch_no_negative", NameAr = "لا دفعة سالبة (أصناف الدفعات/الصلاحية)", NameEn = "No negative batch (batch/expiry items)",
				Expected = 0, Actual = negBatch, Ok = negBatch == 0, Note = negBatch == 0 ? "لا دفعة سالبة" : string.Join(" · ", negBatchBad), Detail = "/Inventory/Batches" });
			// (c) VALUE diff — COUNTED (never raises failedCount). Frozen baseline = {ITM-0001, MFGT-FIN}; "new" accumulates the HM-D7 footprint.
			res.Add(new IntegrityCheck { Key = "bal_value_diff", NameAr = "فروق قيمة الرصيد (معدودة)", NameEn = "Balance value diffs (counted)",
				Expected = 0, Actual = valNew, Ok = true,
				Note = $"baseline(ITM-0001/MFGT-FIN)={valBaseline} · new={valNew} [new = HM-D7 footprint / unreconciled]" + (valNewList.Count > 0 ? " · " + string.Join(" · ", valNewList) : ""), Detail = "/Inventory/StockBalances" });
			// (d) locked-read guard trips — COUNTED tripwire (process-lifetime); must stay 0 in prod.
			res.Add(new IntegrityCheck { Key = "stock_guard_trips", NameAr = "إطلاق حارس القراءة المقفولة (معدود)", NameEn = "Locked-read guard trips (counted)",
				Expected = 0, Actual = StockService.LockReadGuardTrips, Ok = true,
				Note = StockService.LockReadGuardTrips == 0 ? "لم يُطلَق (سليم)" : "أُطلِق — راجع السجلّ التطبيقي", Detail = "/Inventory/StockBalances" });
			// (g) inv-reconcile / inv-resync-item usage — COUNTED (a derived-cache repair leaves a permanent row; repeated use
			// must be visible, not silent). Never raises failedCount.
			int reconTotal = await _db.InventoryReconcileLogs.CountAsync(r => r.CompanyID == companyId);
			var reconSince = DateTime.UtcNow.AddDays(-30);
			int recon30 = await _db.InventoryReconcileLogs.CountAsync(r => r.CompanyID == companyId && r.RanAt >= reconSince);
			res.Add(new IntegrityCheck { Key = "reconcile_log", NameAr = "سجلّ تسويات المخزون (معدود)", NameEn = "Inventory reconcile-log usage (counted)",
				Expected = 0, Actual = reconTotal, Ok = true,
				Note = $"إجمالي={reconTotal} · آخر 30 يومًا={recon30}", Detail = "/Inventory/StockBalances" });

			return res;
		}

		// DEV-2026-010: documented baseline of the PRE-EXISTING integrity failures, so FailedCount counts only failures ABOVE the
		// baseline — the metric is a live alarm again (0 = clean; any NEW deviation raises it immediately). Explicit key/count only,
		// NO range/tolerance. stock_gl & grni = HM-D16 structural gap whose value moves with every stock/GRNI op → keyed (null).
		// cogs_impact = exactly 2 pre-existing sale lines with no COGS movement; receiptno_dup = exactly 5 pre-existing duplicates →
		// pinned counts, so a RISE adds the excess. Full IDs/values in deploy/AUDIT-DEVIATIONS.md (DEV-2026-010).
		public static readonly Dictionary<string, decimal?> Baseline = new()
		{
			["stock_gl"] = null,     // HM-D16 structural (value fluctuates)
			["grni"] = null,         // HM-D16 structural
			["cogs_impact"] = 2m,    // 2 pre-existing sale lines without a COGS movement
			["receiptno_dup"] = 5m,  // 5 pre-existing duplicate receipt numbers
		};
		// count of failing checks ABOVE the documented baseline (a new failing check, or a pinned-count check that rose)
		public static int BaselineExcess(IEnumerable<IntegrityCheck> checks)
		{
			int excess = 0;
			foreach (var c in checks.Where(x => !x.Ok))
			{
				if (!Baseline.TryGetValue(c.Key, out var bl)) { excess++; continue; }        // a NEW failing check
				if (bl == null) continue;                                                     // keyed structural debt (HM-D16)
				if (c.Actual > bl.Value) excess += (int)Math.Ceiling(c.Actual - bl.Value);    // pinned count rose ⇒ the excess is new
			}
			return excess;
		}

		public async Task<(IntegrityCheckRun run, List<IntegrityCheck> checks)> RunAndLogAsync(int companyId, string source)
		{
			var checks = await RunAsync(companyId);
			int failed = BaselineExcess(checks);   // DEV-2026-010: failures ABOVE the documented baseline only
			var run = new IntegrityCheckRun
			{
				CompanyID = companyId, RunAt = DateTime.UtcNow, Source = source, AllOk = failed == 0, FailedCount = failed,
				Summary = System.Text.Json.JsonSerializer.Serialize(checks.Select(c => new { c.Key, c.NameAr, c.Expected, c.Actual, diff = c.Diff, c.Ok }))
			};
			_db.IntegrityCheckRuns.Add(run);
			await _db.SaveChangesAsync();

			if (failed > 0)
			{
				// notify inventory managers (the financial governance role)
				var managers = await _db.InventoryUserRoles.AsNoTracking().Where(r => r.CompanyID == companyId && r.Role == "InventoryManager").Select(r => r.EmployeeId).Distinct().ToListAsync();
				var names = string.Join("، ", checks.Where(c => !c.Ok).Select(c => c.NameAr));
				foreach (var m in managers)
					await _notify.NotifyAsync(m, "تنبيه سلامة بيانات", "Integrity alert",
						$"فحص السلامة وجد {failed} انحرافًا: {names}", $"{failed} integrity check(s) failed", "Integrity", run.ID);
			}
			return (run, checks);
		}

		public Task<List<IntegrityCheckRun>> RecentRunsAsync(int companyId, int take = 30) =>
			_db.IntegrityCheckRuns.AsNoTracking().Where(r => r.CompanyID == companyId).OrderByDescending(r => r.ID).Take(take).ToListAsync();
	}
}
