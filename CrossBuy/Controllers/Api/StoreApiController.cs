using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// STOREKEEPER MODE (mobile, 2026-11-10) — a blind stock count and a warehouse transfer from the phone.
	//
	// NOTHING HERE IS A SECOND RULE. The web screens (InventoryController.PostCount / CreateTransfer) are guarded by
	// [InvPerm("doc")] — InventoryManager or WarehouseKeeper, plus the keeper's warehouse scope — and route anything at
	// or above the company's approval threshold to InventoryApprovals instead of posting it. This controller asks the
	// SAME two questions InvPerm asks (CanAsync("doc"), then CanUseWarehouseAsync for every warehouse it touches), applies the SAME threshold estimate, and posts through the SAME StockService methods. The only new thing
	// is the transport.
	//
	// BLIND: the count sheet carries no quantities. The counter sees the book figure only in the result, after the
	// count is posted (taken from the posted StockCountLines, i.e. what the engine actually booked).
	//
	// BATCH-TRACKED items are counted one line per batch (HM-7); the phone sheet does not do batches yet, so such
	// items are marked and refused here rather than posted as an un-batched line.
	[ApiController]
	[Route("api/store")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class StoreApiController : ControllerBase
	{
		private readonly CrossDbContext _db;
		private readonly IBusinessContextAccessor _context;
		private readonly IInventoryAccessService _access;
		private readonly IInventoryApprovalService _approvals;
		private readonly IStockService _stock;
		private readonly IProcurementService _proc;
		private readonly ICurrencyService _currency;
		private readonly IAccountingAccessService _accAccess;

		public StoreApiController(CrossDbContext db, IBusinessContextAccessor context, IInventoryAccessService access,
			IInventoryApprovalService approvals, IStockService stock, IProcurementService proc, ICurrencyService currency,
			IAccountingAccessService accAccess)
		{ _db = db; _context = context; _access = access; _approvals = approvals; _stock = stock; _proc = proc; _currency = currency; _accAccess = accAccess; }

		private bool IsAr => Request.Headers["Accept-Language"].ToString().StartsWith("ar");
		private static readonly object Refused = new { success = false, message = "You do not have permission to perform this action" };

		// The warehouse half of what [InvPerm("doc")] checks: an active warehouse of MY company, inside the keeper's scope.
		// The action half (_access.CanAsync("doc")) is called in each action body, where the CBA001 analyzer sees it.
		private async Task<bool> WarehouseOkAsync(BusinessContext ctx, int warehouseId) =>
			warehouseId > 0
			&& await _db.Warehouses.AsNoTracking().AnyAsync(w => w.ID == warehouseId && w.CompanyID == ctx.CompanyId && w.IsActive)
			&& await _access.CanUseWarehouseAsync(warehouseId);

		// GET /api/store/warehouses — the warehouses this caller may post stock documents in (empty = not a storekeeper)
		[HttpGet("warehouses")]
		public async Task<IActionResult> Warehouses()
		{
			var ctx = await _context.TryGetCurrentAsync();
			if (ctx == null) return Ok(new { success = true, data = Array.Empty<object>() });
			var all = await _db.Warehouses.AsNoTracking().Where(w => w.CompanyID == ctx.CompanyId && w.IsActive).OrderBy(w => w.Code).ToListAsync();
			var mine = new List<object>();
			if (await _access.CanAsync("doc"))
				foreach (var w in all)
					if (await _access.CanUseWarehouseAsync(w.ID))
						mine.Add(new { id = w.ID, code = w.Code, name = IsAr || string.IsNullOrWhiteSpace(w.NameEn) ? w.Name : w.NameEn });
			// transfers may go TO any active warehouse of the company; the source must be one of mine
			var targets = all.Where(_ => mine.Count > 0).Select(w => new { id = w.ID, code = w.Code, name = IsAr || string.IsNullOrWhiteSpace(w.NameEn) ? w.Name : w.NameEn });
			return Ok(new { success = true, data = mine, targets });
		}

		// GET /api/store/count-sheet?warehouseId= — the items held in that warehouse, WITHOUT quantities (blind count)
		[HttpGet("count-sheet")]
		public async Task<IActionResult> CountSheet(int warehouseId)
		{
			var ctx = await _context.TryGetCurrentAsync();
			if (ctx == null || !await _access.CanAsync("doc") || !await WarehouseOkAsync(ctx, warehouseId)) return StatusCode(403, Refused);
			var ids = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && b.WarehouseId == warehouseId).Select(b => b.ItemId).Distinct().ToListAsync();
			var units = await _db.UnitsOfMeasure.AsNoTracking().Where(u => u.CompanyID == ctx.CompanyId).ToDictionaryAsync(u => u.ID);
			var items = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).OrderBy(i => i.ItemCode).ToListAsync();
			var extra = await _db.ItemBarcodes.AsNoTracking().Where(b => ids.Contains(b.ItemId)).Select(b => new { b.ItemId, b.Barcode }).ToListAsync();
			return Ok(new
			{
				success = true,
				data = items.Select(i => new
				{
					id = i.ID, code = i.ItemCode,
					name = IsAr || string.IsNullOrWhiteSpace(i.NameEn) ? i.Name : i.NameEn,
					unit = units.TryGetValue(i.BaseUoMId, out var u) ? (IsAr || string.IsNullOrWhiteSpace(u.NameEn) ? u.Name : u.NameEn) : "",
					barcodes = new[] { i.Barcode }.Concat(extra.Where(x => x.ItemId == i.ID).Select(x => x.Barcode)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(),
					batchTracked = i.TrackExpiry,
				}),
			});
		}

		public sealed class CountDto
		{
			public int WarehouseId { get; set; }
			public string? Notes { get; set; }
			public List<CountLineInput> Lines { get; set; } = new();
		}

		// POST /api/store/count — post the count (or send it for approval above the threshold), then reveal the variance
		[HttpPost("count")]
		public async Task<IActionResult> Count([FromBody] CountDto dto)
		{
			var ctx = await _context.TryGetCurrentAsync();
			if (ctx == null || dto == null || !await _access.CanAsync("doc") || !await WarehouseOkAsync(ctx, dto.WarehouseId)) return StatusCode(403, Refused);
			var lines = (dto.Lines ?? new()).Where(l => l.ItemId > 0 && l.CountedQty >= 0).GroupBy(l => l.ItemId).Select(g => new CountLineInput { ItemId = g.Key, CountedQty = g.Sum(x => x.CountedQty) }).ToList();
			if (lines.Count == 0) return BadRequest(new { success = false, message = "The stock count must contain at least one line" });
			var ids = lines.Select(l => l.ItemId).ToList();
			var known = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID) && i.CompanyID == ctx.CompanyId).Select(i => new { i.ID, i.TrackExpiry }).ToListAsync();
			if (known.Count != ids.Count) return BadRequest(new { success = false, message = "Unknown item" });
			if (known.Any(k => k.TrackExpiry)) return BadRequest(new { success = false, message = "Batch-tracked items are counted per batch on the web" });

			var date = DateTime.Today;
			decimal est = 0;   // same estimate as the web: |counted - book| x avg cost
			foreach (var l in lines) { var (bookQ, _, avg) = await _stock.GetBalanceAsync(ctx.CompanyId, l.ItemId, dto.WarehouseId); est += Math.Abs(l.CountedQty - bookQ) * avg; }
			if (await _approvals.RequiresApprovalAsync(est))
			{
				var apId = await _approvals.SubmitAsync("StockCount", est, new CountApprovalPayload { WarehouseId = dto.WarehouseId, CountDate = date, Notes = dto.Notes, Lines = lines }, ctx.EmployeeId);
				return Ok(new { success = true, pendingApproval = true, approvalId = apId });
			}
			var (ok, err, cnt) = await _stock.PostCountAsync(ctx.CompanyId, dto.WarehouseId, date, dto.Notes, lines, null);
			if (!ok || cnt == null) return BadRequest(new { success = false, message = err });
			var posted = await _db.StockCountLines.AsNoTracking().Where(l => l.StockCountId == cnt.ID).OrderBy(l => l.LineNo).ToListAsync();
			var names = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => IsAr || string.IsNullOrWhiteSpace(i.NameEn) ? i.Name : i.NameEn);
			return Ok(new
			{
				success = true, pendingApproval = false, countNo = cnt.CountNo, totalAdjValue = cnt.TotalAdjValue,
				lines = posted.Select(l => new { itemId = l.ItemId, name = names.TryGetValue(l.ItemId, out var n) ? n : "", book = l.BookQty, counted = l.CountedQty, diff = l.DiffQty, diffValue = l.DiffValue }),
			});
		}

		public sealed class TransferDto
		{
			public int FromWarehouseId { get; set; }
			public int ToWarehouseId { get; set; }
			public string? Notes { get; set; }
			public List<TransferLineInput> Lines { get; set; } = new();
		}

		// ---------------- RECEIVE (the prototype's third tab) ----------------
		// The web's CreateGoodsReceipt is guarded by [InvPerm("purchase")] plus the keeper's warehouse scope, fills each line
		// from the purchase order (remaining quantity, the order's unit price and unit, the order line), passes the order's
		// currency, and asks "currency-override" for a foreign one. These three endpoints ask the same and post through the
		// same ProcurementService.CreateReceiptAsync. Items tracked by batch / expiry / serial need those details, which the
		// phone does not take yet: they are marked and refused, never received without them.

		private static bool OpenPo(CrossBuy.Models.Context.Inventory.PurchaseOrder po) => po.Status is "Approved" or "Received";

		// GET /api/store/receipts?warehouseId= — approved purchase orders still awaiting goods, for this warehouse
		[HttpGet("receipts")]
		public async Task<IActionResult> Receipts(int warehouseId)
		{
			var ctx = await _context.TryGetCurrentAsync();
			if (ctx == null || !await _access.CanAsync("purchase") || !await WarehouseOkAsync(ctx, warehouseId)) return StatusCode(403, Refused);
			var pos = await _db.PurchaseOrders.AsNoTracking().Include(p => p.Lines)
				.Where(p => p.CompanyID == ctx.CompanyId && (p.Status == "Approved" || p.Status == "Received") && (p.WarehouseId == null || p.WarehouseId == warehouseId))
				.OrderBy(p => p.ExpectedDate ?? p.OrderDate).ThenBy(p => p.ID).Take(50).ToListAsync();
			pos = pos.Where(p => p.Lines.Any(l => l.ItemId != null && l.Qty - l.ReceivedQty > 0)).ToList();
			var vendorIds = pos.Select(p => p.VendorId).Distinct().ToList();
			var vendors = await _db.Vendors.AsNoTracking().Where(v => vendorIds.Contains(v.ID)).ToDictionaryAsync(v => v.ID, v => IsAr || string.IsNullOrWhiteSpace(v.NameEn) ? v.Name : v.NameEn!);
			return Ok(new
			{
				success = true,
				data = pos.Select(p => new
				{
					id = p.ID, orderNo = p.OrderNo, vendor = vendors.TryGetValue(p.VendorId, out var vn) ? vn : "",
					orderDate = p.OrderDate, expectedDate = p.ExpectedDate,
					lines = p.Lines.Count(l => l.ItemId != null && l.Qty - l.ReceivedQty > 0),
					partly = p.Lines.Any(l => l.ReceivedQty > 0),
				}),
			});
		}

		// GET /api/store/receipts/{poId}?warehouseId= — the order's lines still awaiting goods, with the codes a scan matches
		[HttpGet("receipts/{poId:int}")]
		public async Task<IActionResult> ReceiptSheet(int poId, int warehouseId)
		{
			var ctx = await _context.TryGetCurrentAsync();
			if (ctx == null || !await _access.CanAsync("purchase") || !await WarehouseOkAsync(ctx, warehouseId)) return StatusCode(403, Refused);
			var po = await _db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.ID == poId && p.CompanyID == ctx.CompanyId);
			if (po == null || !OpenPo(po) || (po.WarehouseId != null && po.WarehouseId != warehouseId)) return NotFound(new { success = false, message = "The purchase order is not open for this warehouse" });
			var lines = po.Lines.Where(l => l.ItemId != null && l.Qty - l.ReceivedQty > 0).OrderBy(l => l.LineNo).ToList();
			var ids = lines.Select(l => l.ItemId!.Value).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).ToDictionaryAsync(i => i.ID);
			var extra = await _db.ItemBarcodes.AsNoTracking().Where(b => ids.Contains(b.ItemId)).Select(b => new { b.ItemId, b.Barcode }).ToListAsync();
			var vendor = await _db.Vendors.AsNoTracking().Where(v => v.ID == po.VendorId).Select(v => IsAr || v.NameEn == null || v.NameEn == "" ? v.Name : v.NameEn).FirstOrDefaultAsync();
			return Ok(new
			{
				success = true,
				data = new
				{
					id = po.ID, orderNo = po.OrderNo, vendor,
					lines = lines.Select(l =>
					{
						var it = items.GetValueOrDefault(l.ItemId!.Value);
						return new
						{
							lineId = l.ID, itemId = l.ItemId,
							code = it?.ItemCode ?? "",
							name = it == null ? (l.ItemDescription ?? "") : (IsAr || string.IsNullOrWhiteSpace(it.NameEn) ? it.Name : it.NameEn),
							ordered = l.Qty, received = l.ReceivedQty, remaining = l.Qty - l.ReceivedQty,
							barcodes = new[] { it?.Barcode }.Concat(extra.Where(x => x.ItemId == l.ItemId).Select(x => x.Barcode)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(),
							needsDetails = it != null && (it.TrackBatch || it.TrackExpiry || it.TrackSerial),
						};
					}),
				},
			});
		}

		public sealed class ReceiveLine { public int LineId { get; set; } public decimal Qty { get; set; } }
		public sealed class ReceiveDto { public int PoId { get; set; } public int WarehouseId { get; set; } public string? Notes { get; set; } public List<ReceiveLine> Lines { get; set; } = new(); }

		// POST /api/store/receipts — receive what was scanned against the order's lines; prices, units and currency come from
		// the order itself, never from the phone
		[HttpPost("receipts")]
		public async Task<IActionResult> Receive([FromBody] ReceiveDto dto)
		{
			var ctx = await _context.TryGetCurrentAsync();
			if (ctx == null || dto == null || !await _access.CanAsync("purchase") || !await WarehouseOkAsync(ctx, dto.WarehouseId)) return StatusCode(403, Refused);
			var po = await _db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.ID == dto.PoId && p.CompanyID == ctx.CompanyId);
			if (po == null || !OpenPo(po) || (po.WarehouseId != null && po.WarehouseId != dto.WarehouseId)) return NotFound(new { success = false, message = "The purchase order is not open for this warehouse" });
			var functional = await _currency.GetFunctionalCurrencyIdAsync(ctx.CompanyId, null);
			if (po.CurrencyId.HasValue && po.CurrencyId.Value != functional && !await _accAccess.CanAsync("currency-override"))
				return StatusCode(403, new { success = false, message = "You do not have permission to issue a document in a currency other than the branch currency" });
			var itemIds = po.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).ToList();
			var tracked = await _db.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID) && (i.TrackBatch || i.TrackExpiry || i.TrackSerial)).Select(i => i.ID).ToListAsync();
			var lines = new List<ReceiptLineInput>();
			foreach (var r in (dto.Lines ?? new()).Where(x => x.Qty > 0))
			{
				var pol = po.Lines.FirstOrDefault(l => l.ID == r.LineId && l.ItemId != null);
				if (pol == null) return BadRequest(new { success = false, message = "A line does not belong to this purchase order" });
				if (tracked.Contains(pol.ItemId!.Value)) return BadRequest(new { success = false, message = "An item tracked by batch, expiry or serial is received on the web, with those details" });
				lines.Add(new ReceiptLineInput { ItemId = pol.ItemId.Value, Qty = r.Qty, UoMId = pol.UoMId, UnitCost = pol.UnitPrice, PurchaseOrderLineId = pol.ID });
			}
			if (lines.Count == 0) return BadRequest(new { success = false, message = "The receipt must contain at least one line" });
			var (ok, err, gr) = await _proc.CreateReceiptAsync(ctx.CompanyId, po.VendorId, dto.WarehouseId, po.ID, DateTime.Today, dto.Notes, lines, null, po.CurrencyId, po.ExchangeRate);
			if (!ok || gr == null) return BadRequest(new { success = false, message = err });
			return Ok(new { success = true, receiptNo = gr.ReceiptNo });
		}

		// POST /api/store/transfer — the source must be a warehouse I may post in; the engine checks stock and periods
		[HttpPost("transfer")]
		public async Task<IActionResult> Transfer([FromBody] TransferDto dto)
		{
			var ctx = await _context.TryGetCurrentAsync();
			// the web filter checks EVERY warehouse argument against the keeper's scope; so does this
			if (ctx == null || dto == null || !await _access.CanAsync("doc")
				|| !await WarehouseOkAsync(ctx, dto.FromWarehouseId) || !await WarehouseOkAsync(ctx, dto.ToWarehouseId)) return StatusCode(403, Refused);
			if (dto.FromWarehouseId == dto.ToWarehouseId) return BadRequest(new { success = false, message = "Source and destination must differ" });
			var lines = (dto.Lines ?? new()).Where(l => l.ItemId > 0 && l.Qty > 0).Select(l => new TransferLineInput { ItemId = l.ItemId, Qty = l.Qty, UoMId = l.UoMId }).ToList();
			if (lines.Count == 0) return BadRequest(new { success = false, message = "The transfer must contain at least one line" });
			var date = DateTime.Today;
			decimal est = 0;   // same estimate as the web: qty x avg cost at the source
			foreach (var l in lines) { var (_, _, avg) = await _stock.GetBalanceAsync(ctx.CompanyId, l.ItemId, dto.FromWarehouseId); est += l.Qty * avg; }
			if (await _approvals.RequiresApprovalAsync(est))
			{
				var apId = await _approvals.SubmitAsync("StockTransfer", est, new TransferApprovalPayload { FromWarehouseId = dto.FromWarehouseId, ToWarehouseId = dto.ToWarehouseId, Date = date, Notes = dto.Notes, Lines = lines }, ctx.EmployeeId);
				return Ok(new { success = true, pendingApproval = true, approvalId = apId });
			}
			var (ok, err, tr) = await _stock.TransferAsync(ctx.CompanyId, dto.FromWarehouseId, dto.ToWarehouseId, date, dto.Notes, lines, null);
			if (!ok || tr == null) return BadRequest(new { success = false, message = err });
			return Ok(new { success = true, pendingApproval = false, transferNo = tr.TransferNo });
		}
	}
}
