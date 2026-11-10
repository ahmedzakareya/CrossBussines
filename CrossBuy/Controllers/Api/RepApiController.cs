using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// SALES-REP MODE (mobile, 2026-11-10) — the customer card, a sales order taken on the visit, and the cash/bank
	// accounts a collection can be received into. The collection itself posts through the existing, authorized
	// POST /api/acc/ar/receipts (accounting "pay"), so there is one receipt endpoint, not two.
	//
	// SAME RULES AS THE WEB, NOT NEW ONES:
	//   * customers / cash accounts — the accounting module's own decision (IAccountingApiAuthorization: read / pay).
	//   * the order — InventoryController.CreateSalesOrder is [InvPerm("doc")]; this asks _access.CanAsync("doc").
	//   * PRICES ARE THE SERVER'S. The rep sends item + quantity only. Each line is priced by IPricingService.GetPriceAsync
	//     for THAT customer (price list, promotion), then checked by the same margin floor (CheckMarginAsync, Block
	//     rejects) and discount ceiling (EvaluateLineDiscountsAsync) the web form runs. A phone cannot type a price.
	//   * TAX is the company's default active VAT TaxCode (0 when it has none). The web form's literal 14% default is
	//     not copied.
	//   * the credit limit is enforced at INVOICING (ReceivableService); the order only reports the headroom so the rep
	//     can see it before saving.
	[ApiController]
	[Route("api/rep")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class RepApiController : ControllerBase
	{
		private readonly CrossDbContext _db;
		private readonly IAccountingApiAuthorization _guard;
		private readonly IInventoryAccessService _access;
		private readonly IBusinessContextAccessor _context;
		private readonly IReceivableService _ar;
		private readonly ISellingService _sell;
		private readonly IPricingService _pricing;
		private readonly IChartOfAccountsService _coa;
		private readonly ICurrencyService _currency;

		public RepApiController(CrossDbContext db, IAccountingApiAuthorization guard, IInventoryAccessService access, IBusinessContextAccessor context,
			IReceivableService ar, ISellingService sell, IPricingService pricing, IChartOfAccountsService coa, ICurrencyService currency)
		{ _db = db; _guard = guard; _access = access; _context = context; _ar = ar; _sell = sell; _pricing = pricing; _coa = coa; _currency = currency; }

		// The functional currency's code: orders are priced, and balances kept, in it.
		private async Task<string> CurrencyCodeAsync(int companyId)
		{
			var id = await _currency.GetFunctionalCurrencyIdAsync(companyId, null);
			return await _db.Currencies.AsNoTracking().Where(c => c.ID == id).Select(c => c.Code).FirstOrDefaultAsync() ?? "";
		}

		private bool IsAr => Request.Headers["Accept-Language"].ToString().StartsWith("ar");
		private string Pick(string? ar, string? en) => IsAr || string.IsNullOrWhiteSpace(en) ? (ar ?? "") : en;

		private async Task<object> CardAsync(int companyId, Models.Context.Accounting.Customer c)
		{
			var outstanding = await _ar.CustomerOutstandingAsync(companyId, c.ID);
			return new
			{
				id = c.ID, name = Pick(c.Name, c.NameEn), phone = c.Phone, address = c.Address,
				creditLimit = c.CreditLimit, outstanding,
				headroom = c.CreditLimit is > 0 ? c.CreditLimit.Value - outstanding : (decimal?)null,
			};
		}

		// GET /api/rep/customers?q=&page= — search + pagination (20 a page), each row with its outstanding and headroom
		[HttpGet("customers")]
		public async Task<IActionResult> Customers(string? q, int page = 1, CancellationToken ct = default)
		{
			var auth = await _guard.AuthorizeAsync("read", null, ct);
			if (!auth.Ok) return auth.Error!;
			var rows = _db.Customers.AsNoTracking().Where(c => c.CompanyID == auth.CompanyId && c.IsActive);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				rows = rows.Where(c => c.Name.Contains(t) || (c.NameEn != null && c.NameEn.Contains(t)) || (c.Phone != null && c.Phone.Contains(t)));
			}
			int total = await rows.CountAsync(ct);
			page = Math.Max(1, page);
			var list = await rows.OrderBy(c => c.Name).Skip((page - 1) * 20).Take(20).ToListAsync(ct);
			var data = new List<object>();
			foreach (var c in list) data.Add(await CardAsync(auth.CompanyId, c));
			return Ok(new { success = true, currency = await CurrencyCodeAsync(auth.CompanyId), total, page, pageSize = 20, data });
		}

		// GET /api/rep/customers/{id}
		[HttpGet("customers/{id:int}")]
		public async Task<IActionResult> Customer(int id, CancellationToken ct = default)
		{
			var auth = await _guard.AuthorizeAsync("read", null, ct);
			if (!auth.Ok) return auth.Error!;
			var c = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == auth.CompanyId, ct);
			if (c == null) return NotFound(new { success = false });
			return Ok(new { success = true, currency = await CurrencyCodeAsync(auth.CompanyId), data = await CardAsync(auth.CompanyId, c), canOrder = await _access.CanAsync("doc"),
				canCollect = (await _guard.AuthorizeAsync("pay", null, ct)).Ok });
		}

		private async Task<decimal> DefaultVatAsync(int companyId) =>
			await _db.TaxCodes.AsNoTracking().Where(t => t.CompanyID == companyId && t.IsActive && t.IsDefault && t.Kind == "VAT")
				.Select(t => (decimal?)t.Rate).FirstOrDefaultAsync() ?? 0m;

		public sealed class OrderLineDto { public int ItemId { get; set; } public decimal Qty { get; set; } }
		public sealed class OrderDto { public int CustomerId { get; set; } public int? WarehouseId { get; set; } public string? Notes { get; set; } public List<OrderLineDto> Lines { get; set; } = new(); }

		// Prices every line for the customer (server-side), so the preview and the saved order are the same computation.
		private async Task<(List<SoLineInput> lines, List<object> view, decimal total, string? block, string? warn)> PriceAsync(int companyId, OrderDto dto)
		{
			var date = DateTime.Today;
			var vat = await DefaultVatAsync(companyId);
			var ids = dto.Lines.Select(l => l.ItemId).Distinct().ToList();
			var items = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID) && i.CompanyID == companyId).ToDictionaryAsync(i => i.ID);
			var lines = new List<SoLineInput>(); var view = new List<object>(); decimal total = 0; var warns = new List<string>();
			foreach (var l in dto.Lines.Where(x => x.Qty > 0).GroupBy(x => x.ItemId).Select(g => new OrderLineDto { ItemId = g.Key, Qty = g.Sum(x => x.Qty) }))
			{
				if (!items.TryGetValue(l.ItemId, out var it)) return (lines, view, 0, "Unknown item", null);
				var p = await _pricing.GetPriceAsync(companyId, it.ID, dto.CustomerId, null, null, l.Qty, date);
				decimal disc = Math.Round(l.Qty * p.UnitPrice * p.DiscountPercent / 100m, 3);
				var mc = await _pricing.CheckMarginAsync(companyId, it.ID, l.Qty > 0 ? p.UnitPrice - disc / l.Qty : p.UnitPrice, null, null, date);
				if (!(mc.Mode == "Off" || mc.Ok || mc.Skipped))
				{
					var msg = $"{Pick(it.Name, it.NameEn)}: {mc.PriceFunctional:N3} < {mc.FloorFunctional:N3}";
					if (mc.Mode == "Block") return (lines, view, 0, "Minimum profit floor — " + msg, null);
					warns.Add(msg);
				}
				var net = l.Qty * p.UnitPrice - disc;
				var lineTotal = net + Math.Round(net * vat / 100m, 3);
				total += lineTotal;
				lines.Add(new SoLineInput { ItemId = it.ID, ItemDescription = it.Name, Qty = l.Qty, UoMId = it.BaseUoMId, UnitPrice = p.UnitPrice, DiscountAmount = disc, TaxRate = vat });
				view.Add(new { itemId = it.ID, name = Pick(it.Name, it.NameEn), code = it.ItemCode, qty = l.Qty, unitPrice = p.UnitPrice, discount = disc, taxRate = vat, total = lineTotal, priceSource = p.Source });
			}
			var (dBlock, dWarn) = await _pricing.EvaluateLineDiscountsAsync(companyId, lines.Select(x => (x.Qty, x.UnitPrice, x.DiscountAmount)), await _access.CanAsync("manage"));
			if (dBlock != null) return (lines, view, 0, dBlock, null);
			if (dWarn != null) warns.Add(dWarn);
			return (lines, view, total, null, warns.Count > 0 ? string.Join(" · ", warns) : null);
		}

		// POST /api/rep/order/preview — the priced lines, total and credit headroom, without saving
		[HttpPost("order/preview")]
		public async Task<IActionResult> Preview([FromBody] OrderDto dto, CancellationToken ct = default)
		{
			var auth = await _guard.AuthorizeAsync("read", null, ct);
			if (!auth.Ok) return auth.Error!;
			if (dto == null || dto.Lines == null || dto.Lines.Count == 0) return BadRequest(new { success = false, message = "The sales order must contain at least one line" });
			var c = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.ID == dto.CustomerId && x.CompanyID == auth.CompanyId, ct);
			if (c == null) return NotFound(new { success = false });
			var (_, view, total, block, warn) = await PriceAsync(auth.CompanyId, dto);
			var outstanding = await _ar.CustomerOutstandingAsync(auth.CompanyId, c.ID);
			return Ok(new { success = block == null, message = block, warning = warn, lines = view, total, creditLimit = c.CreditLimit, outstanding,
				overLimit = c.CreditLimit is > 0 && outstanding + total > c.CreditLimit.Value });
		}

		// POST /api/rep/order — save the sales order (inventory "doc", as on the web)
		[HttpPost("order")]
		public async Task<IActionResult> Order([FromBody] OrderDto dto, CancellationToken ct = default)
		{
			var ctx = await _context.TryGetCurrentAsync(ct);
			if (ctx == null || !await _access.CanAsync("doc")) return StatusCode(403, new { success = false, message = "You do not have permission to perform this action" });
			if (dto == null || dto.Lines == null || dto.Lines.Count == 0) return BadRequest(new { success = false, message = "The sales order must contain at least one line" });
			if (!await _db.Customers.AsNoTracking().AnyAsync(x => x.ID == dto.CustomerId && x.CompanyID == ctx.CompanyId && x.IsActive, ct)) return NotFound(new { success = false });
			if (dto.WarehouseId is > 0 && !await _access.CanUseWarehouseAsync(dto.WarehouseId.Value)) return StatusCode(403, new { success = false, message = "You do not have permission to perform this action" });
			var (lines, _, _, block, warn) = await PriceAsync(ctx.CompanyId, dto);
			if (block != null) return BadRequest(new { success = false, message = block });
			var (ok, err, so) = await _sell.CreateSalesOrderAsync(ctx.CompanyId, dto.CustomerId, dto.WarehouseId, DateTime.Today, null, dto.Notes, lines, null);
			if (!ok || so == null) return BadRequest(new { success = false, message = err });
			return Ok(new { success = true, id = so.ID, orderNo = so.OrderNo, total = so.GrandTotal, warning = warn });
		}

		// GET /api/rep/cash-accounts — where a collection may be received (the same cash & bank accounts as the web
		// receipt screen: postable accounts under 1101). Accounting "pay", as the receipt itself.
		[HttpGet("cash-accounts")]
		public async Task<IActionResult> CashAccounts(CancellationToken ct = default)
		{
			var auth = await _guard.AuthorizeAsync("pay", null, ct);
			if (!auth.Ok) return auth.Error!;
			var accounts = (await _coa.GetFlatAsync(auth.CompanyId, postableOnly: true)).Where(a => a.Code.StartsWith("1101"))
				.Select(a => new { id = a.Id, code = a.Code, name = Pick(a.Name, a.NameEn) });
			return Ok(new { success = true, data = accounts });
		}
	}
}
