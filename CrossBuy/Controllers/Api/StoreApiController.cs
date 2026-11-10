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

		public StoreApiController(CrossDbContext db, IBusinessContextAccessor context, IInventoryAccessService access,
			IInventoryApprovalService approvals, IStockService stock)
		{ _db = db; _context = context; _access = access; _approvals = approvals; _stock = stock; }

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
