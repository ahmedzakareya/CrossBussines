using CrossBuy.BL;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// Mobile inventory surface: item search + barcode scan with on-hand. JWT bearer.
	[ApiController]
	[Route("api/inv")]
	[Produces("application/json")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	public class InventoryApiController : ControllerBase
	{
		// THE COMPANY, RESOLVED PER REQUEST, NOT COMPILED IN.
		//
		// This was `private const int CompanyId = 1;`, read by nine lines across three endpoints
		// (Items, Scan, OnHand). The controller is JWT-authenticated, so every caller arrives with
		// an identity the resolver can answer from - the constant was not standing in for a missing
		// signal, it was ignoring one. A handheld scanner signed in to company 65 was reading
		// company 1’s catalogue and stock.
		//
		// ZERO WHEN IT CANNOT RESOLVE: the endpoints filter on it, so an unresolvable token reads an
		// empty company rather than somebody else’s.
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		private async Task<int> CompanyAsync()
		{
			var scope = await _company.ResolveAsync();
			return scope.Ok ? scope.CompanyId : 0;
		}
		private readonly IItemService _items;
		private readonly IWarehouseService _warehouses;
		private readonly IStockService _stock;
		private readonly CrossDbContext _db;
		public InventoryApiController(IItemService items, IWarehouseService warehouses, IStockService stock, CrossDbContext db, CrossBuy.BL.Platform.IRequestCompanyResolver company)
		{ _items = items; _warehouses = warehouses; _stock = stock; _db = db; _company = company; }

		private bool IsAr => (HttpContext.Items["Culture"]?.ToString() == "ar")
			|| (Request.Headers["Accept-Language"].ToString().StartsWith("ar"));

		// GET /api/inv/items?q=  → search items with total on-hand
		[HttpGet("items")]
		public async Task<IActionResult> Items(string? q, int take = 50)
		{
			var companyId = await CompanyAsync();
			var items = await _items.GetItemsAsync(companyId);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim().ToLowerInvariant();
				items = items.Where(i => (i.ItemCode ?? "").ToLowerInvariant().Contains(t)
					|| (i.Barcode ?? "").ToLowerInvariant().Contains(t)
					|| (i.Name ?? "").ToLowerInvariant().Contains(t)
					|| (i.NameEn ?? "").ToLowerInvariant().Contains(t)).ToList();
			}
			items = items.Take(take).ToList();
			var ids = items.Select(i => i.ID).ToList();
			var onHand = (await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && ids.Contains(b.ItemId))
				.GroupBy(b => b.ItemId).Select(g => new { ItemId = g.Key, Qty = g.Sum(x => x.QtyOnHand) }).ToListAsync())
				.ToDictionary(x => x.ItemId, x => x.Qty);
			return Ok(items.Select(i => new
			{
				id = i.ID, code = i.ItemCode, barcode = i.Barcode,
				name = IsAr ? i.Name : (string.IsNullOrEmpty(i.NameEn) ? i.Name : i.NameEn),
				image = i.ImagePath, type = i.ItemType, isComposite = i.IsComposite,
				salesPrice = i.SalesPrice, onHand = onHand.TryGetValue(i.ID, out var q2) ? q2 : 0m
			}));
		}

		// GET /api/inv/scan?barcode=  → item detail + on-hand per warehouse + units + components
		[HttpGet("scan")]
		public async Task<IActionResult> Scan(string barcode)
		{
			var companyId = await CompanyAsync();
			barcode = (barcode ?? "").Trim();
			if (barcode.Length == 0) return Ok(new { ok = false });
			var item = await _db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.CompanyID == companyId && i.Barcode == barcode);
			int? matchedUom = item?.BaseUoMId;
			if (item == null)
			{
				var bc = await _db.ItemBarcodes.AsNoTracking().FirstOrDefaultAsync(b => b.Barcode == barcode && _db.Items.Any(i => i.ID == b.ItemId && i.CompanyID == companyId));
				if (bc != null) { item = await _db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == bc.ItemId); matchedUom = bc.UoMId ?? item?.BaseUoMId; }
			}
			if (item == null) return Ok(new { ok = false, barcode });

			var whs = (await _warehouses.GetWarehousesAsync(companyId)).ToDictionary(w => w.ID, w => w);
			var balances = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && b.ItemId == item.ID).ToListAsync();
			var units = await _items.GetUnitsAsync(companyId);
			string uName(int id) => units.FirstOrDefault(u => u.ID == id) is { } u ? (IsAr ? u.Name : DisplayName.Or(u.NameEn, u.Name)) : "";
			var convs = await _db.UoMConversions.AsNoTracking().Where(x => x.ItemId == item.ID).ToListAsync();
			var unitList = new List<object> { new { id = item.BaseUoMId, name = uName(item.BaseUoMId), factor = 1m } };
			foreach (var cv in convs) unitList.Add(new { id = cv.FromUoMId, name = uName(cv.FromUoMId), factor = cv.Factor });

			var comps = new List<object>();
			if (item.IsComposite)
			{
				var rows = await _items.GetItemComponentsAsync(item.ID);
				var cids = rows.Select(r => r.ComponentItemId).ToList();
				var citems = await _db.Items.AsNoTracking().Where(i => cids.Contains(i.ID)).ToListAsync();
				foreach (var r in rows) comps.Add(new { name = citems.FirstOrDefault(x => x.ID == r.ComponentItemId)?.Name ?? "?", qty = r.Quantity });
			}

			return Ok(new
			{
				ok = true,
				id = item.ID, code = item.ItemCode, barcode = item.Barcode,
				name = IsAr ? item.Name : (string.IsNullOrEmpty(item.NameEn) ? item.Name : item.NameEn),
				image = item.ImagePath, type = item.ItemType, isComposite = item.IsComposite, compositeType = item.CompositeType,
				salesPrice = item.SalesPrice, matchedUomId = matchedUom, units = unitList, components = comps,
				totalOnHand = balances.Sum(b => b.QtyOnHand),
				totalValue = balances.Sum(b => b.TotalValue),
				byWarehouse = balances.Select(b => new { warehouse = whs.TryGetValue(b.WarehouseId, out var w) ? w.Code : ("#" + b.WarehouseId), qty = b.QtyOnHand, avgCost = b.AvgCost, value = b.TotalValue })
			});
		}

		// GET /api/inv/onhand?itemId=  → balances per warehouse
		[HttpGet("onhand")]
		public async Task<IActionResult> OnHand(int itemId)
		{
			var companyId = await CompanyAsync();
			var whs = (await _warehouses.GetWarehousesAsync(companyId)).ToDictionary(w => w.ID, w => w);
			var balances = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == companyId && b.ItemId == itemId).ToListAsync();
			return Ok(balances.Select(b => new { warehouse = whs.TryGetValue(b.WarehouseId, out var w) ? w.Code : ("#" + b.WarehouseId), qty = b.QtyOnHand, avgCost = b.AvgCost, value = b.TotalValue }));
		}
	}
}
