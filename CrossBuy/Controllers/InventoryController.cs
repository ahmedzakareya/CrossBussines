using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	/// نظام المخازن — Inventory / Warehouse. Same Metronic shell as the Admin/Accounting modules.
	/// Phase I0: master data (items, categories, units, warehouses). Posting/stock engine comes in I1.
	[SessionValidation]
	public class InventoryController : Controller
	{
		private const int DefaultCompanyId = 1;
		private readonly IItemService _items;
		private readonly IWarehouseService _warehouses;
		private readonly IChartOfAccountsService _coa;
		private readonly CrossDbContext _context;
		private readonly IWebHostEnvironment _env;
		private readonly IStockService _stock;
		private readonly IProcurementService _proc;
		private readonly ISellingService _sell;
		private readonly IInventoryAccessService _access;
		private readonly IInventoryApprovalService _approvals;
		private readonly IOpeningBalanceService _opening;
		private readonly IIntegrityCheckService _integrity;
		private readonly IPricingService _pricing;
		private readonly IThreeWayMatchService _match;
		private readonly ICurrencyService _currency;
		private readonly IAccountingAccessService _accAccess;
		private readonly IManufService _manuf;
		private readonly IShelfLabelService _labels;                    // HM-4: EAN-13 SVG for shelf labels
		private readonly ICurrencyRounding _rounding;                   // HM-4: currency decimals for label/price display
		// D1 Wave 1 / CORRECTION-005: validated company source for the remediated WarehouseQuickAdd.
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public InventoryController(IItemService items, IWarehouseService warehouses, IChartOfAccountsService coa, CrossDbContext context, IWebHostEnvironment env, IStockService stock, IProcurementService proc, ISellingService sell, IInventoryAccessService access, IInventoryApprovalService approvals, IOpeningBalanceService opening, IIntegrityCheckService integrity, IPricingService pricing, IThreeWayMatchService match, ICurrencyService currency, IAccountingAccessService accAccess, IManufService manuf, IShelfLabelService labels, ICurrencyRounding rounding, CrossBuy.BL.Platform.IRequestCompanyResolver company, IStringLocalizer<CrossBuy.SharedResources> localizer)
		{
			_items = items; _warehouses = warehouses; _coa = coa; _context = context; _env = env; _stock = stock; _proc = proc; _sell = sell; _access = access; _approvals = approvals; _opening = opening; _integrity = integrity; _pricing = pricing; _match = match; _currency = currency; _accAccess = accAccess; _manuf = manuf; _labels = labels; _rounding = rounding; L = localizer; _company = company;
		}

		// saves an uploaded item image to wwwroot/uploads/items and returns the public path (null if no file)
		private async Task<string?> SaveItemImageAsync(IFormFile? file, string subfolder = "items")
		{
			if (file == null || file.Length == 0 || string.IsNullOrEmpty(_env.WebRootPath)) return null;
			var dir = System.IO.Path.Combine(_env.WebRootPath, "uploads", subfolder);
			if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
			var fileName = Guid.NewGuid() + System.IO.Path.GetExtension(file.FileName);
			using (var stream = System.IO.File.Create(System.IO.Path.Combine(dir, fileName)))
				await file.CopyToAsync(stream);
			return "/uploads/" + subfolder + "/" + fileName;
		}

		// Delete a physical uploaded file by its web path. GUARD: only files we manage under /uploads/ —
		// never touch shared theme/seed assets (e.g. /assets/imgs/...), which many items may reference.
		private void DeleteUploadedFile(string? webPath)
		{
			if (string.IsNullOrWhiteSpace(webPath) || string.IsNullOrEmpty(_env.WebRootPath)) return;
			var p = webPath.Replace("\\", "/");
			if (!p.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)) return;
			var full = System.IO.Path.Combine(_env.WebRootPath, p.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar));
			try { if (System.IO.File.Exists(full)) System.IO.File.Delete(full); } catch { /* best-effort cleanup */ }
		}

		[HttpGet] public async Task<IActionResult> Index()
		{
			var c = DefaultCompanyId;
			var items = await _items.GetItemsAsync(c);
			var whs = await _warehouses.GetWarehousesAsync(c);
			var balances = await _stock.GetBalancesAsync(c);
			var itemName = items.ToDictionary(i => i.ID, i => i.ItemCode + " — " + i.Name);
			var whName = whs.ToDictionary(w => w.ID, w => w.Code);

			var dto = new InventoryDashboardDto
			{
				ItemCount = items.Count,
				CategoryCount = await _context.ItemCategories.CountAsync(x => x.CompanyID == c),
				WarehouseCount = whs.Count,
				UnitCount = await _context.UnitsOfMeasure.CountAsync(u => u.CompanyID == c),
				CompositeCount = items.Count(i => i.IsComposite),
				OutOfStock = balances.Count(b => b.QtyOnHand <= 0),
				TotalValue = balances.Sum(b => b.TotalValue),
				TotalQty = balances.Sum(b => b.QtyOnHand),
				Recent = await _stock.GetMovementsAsync(c, take: 10),
				ItemName = itemName,
				WhName = whName,
			};

			// top 5 items by stock value
			var topV = balances.GroupBy(b => b.ItemId).Select(g => new { id = g.Key, val = g.Sum(x => x.TotalValue) })
				.OrderByDescending(x => x.val).Take(5).ToList();
			var maxV = topV.Count > 0 ? topV.Max(x => x.val) : 0m;
			dto.TopItems = topV.Select(x => new InvNameValue { Name = itemName.TryGetValue(x.id, out var n) ? n : ("#" + x.id), Value = x.val, Pct = maxV > 0 ? (int)Math.Round(x.val / maxV * 100) : 0 }).ToList();

			// value by warehouse
			var byWh = balances.GroupBy(b => b.WarehouseId).Select(g => new { id = g.Key, val = g.Sum(x => x.TotalValue) }).OrderByDescending(x => x.val).ToList();
			var maxW = byWh.Count > 0 ? byWh.Max(x => x.val) : 0m;
			dto.ByWarehouse = byWh.Select(x => new InvNameValue { Name = whName.TryGetValue(x.id, out var n) ? n : ("#" + x.id), Value = x.val, Pct = maxW > 0 ? (int)Math.Round(x.val / maxW * 100) : 0 }).ToList();

			// 6-month in/out trend
			var since = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-5);
			var movs = await _context.StockMovements.AsNoTracking().Where(m => m.CompanyID == c && m.MovementDate >= since)
				.Select(m => new { m.MovementDate, m.Direction, m.TotalCost }).ToListAsync();
			for (int i = 0; i < 6; i++)
			{
				var mo = since.AddMonths(i);
				dto.Months.Add(new InvMonthRow
				{
					Year = mo.Year, Month = mo.Month,
					InValue = movs.Where(x => x.MovementDate.Year == mo.Year && x.MovementDate.Month == mo.Month && x.Direction == 1).Sum(x => x.TotalCost),
					OutValue = movs.Where(x => x.MovementDate.Year == mo.Year && x.MovementDate.Month == mo.Month && x.Direction == -1).Sum(x => x.TotalCost),
				});
			}
			return View(dto);
		}

		// ---------------- Units ----------------
		[HttpGet] public async Task<IActionResult> Units() => View(await _items.GetUnitsAsync(DefaultCompanyId));

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> CreateUnit(string code, string name, string nameEn)
		{
			var (ok, err) = await _items.CreateUnitAsync(DefaultCompanyId, code, name, nameEn);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Unit added"].Value : err;
			return RedirectToAction(nameof(Units));
		}

		// ---------------- Categories (tree) ----------------
		[HttpGet] public async Task<IActionResult> Categories()
		{
			ViewBag.Accounts = await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true);
			return View(await _items.GetCategoryTreeAsync(DefaultCompanyId));
		}

		private async Task PopulateCategoryFormListsAsync(int? excludeId = null)
		{
			ViewBag.Accounts = await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true);
			ViewBag.Categories = (await _items.GetCategoriesAsync(DefaultCompanyId))
				.Where(c => excludeId == null || c.ID != excludeId).ToList();
		}

		[HttpGet] public async Task<IActionResult> CreateCategory()
		{
			await PopulateCategoryFormListsAsync();
			ViewBag.IsEdit = false;
			return View("CategoryForm", new ItemCategory { IsActive = true });
		}

		[HttpGet] public async Task<IActionResult> EditCategory(int id)
		{
			var cat = (await _items.GetCategoriesAsync(DefaultCompanyId)).FirstOrDefault(c => c.ID == id);
			if (cat == null) { TempData["InvErr"] = L["Category not found"].Value; return RedirectToAction(nameof(Categories)); }
			await PopulateCategoryFormListsAsync(id);
			ViewBag.IsEdit = true;
			return View("CategoryForm", cat);
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> CreateCategory(ItemCategory model, IFormFile? iconFile)
		{
			var savedIcon = await SaveItemImageAsync(iconFile, "categories");
			if (savedIcon != null) model.StoreIcon = savedIcon;
			var (ok, err) = await _items.CreateCategoryAsync(DefaultCompanyId, model, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(CreateCategory)); }
			TempData["InvMsg"] = L["Category added"].Value;
			return RedirectToAction(nameof(Categories));
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> EditCategory(int id, ItemCategory model, IFormFile? iconFile)
		{
			var savedIcon = await SaveItemImageAsync(iconFile, "categories");
			if (savedIcon != null) model.StoreIcon = savedIcon;   // else the posted hidden StoreIcon keeps the current image
			var (ok, err) = await _items.UpdateCategoryAsync(DefaultCompanyId, id, model, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(EditCategory), new { id }); }
			TempData["InvMsg"] = L["Category updated"].Value;
			return RedirectToAction(nameof(Categories));
		}

		// ---------------- Items ----------------
		[HttpGet] public async Task<IActionResult> Items()
		{
			ViewBag.Categories = await _items.GetCategoriesAsync(DefaultCompanyId);
			ViewBag.Units = await _items.GetUnitsAsync(DefaultCompanyId);
			return View();   // rows loaded server-side & paged via ItemsData
		}

		// server-side paged + filtered rows (handles millions of items). Returns the rows partial + paging headers.
		[HttpGet] public async Task<IActionResult> ItemsData(string? q, int? categoryId, string? type, bool? active, int page = 1, int pageSize = 25)
		{
			var (rows, total) = await _items.SearchItemsAsync(DefaultCompanyId, q, categoryId, type, active, page, pageSize);
			ViewBag.CatById = (await _items.GetCategoriesAsync(DefaultCompanyId)).ToDictionary(c => c.ID, c => c);
			ViewBag.UById = (await _items.GetUnitsAsync(DefaultCompanyId)).ToDictionary(u => u.ID, u => u);
			if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var pages = (int)System.Math.Ceiling(total / (double)pageSize);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = pages.ToString();
			return PartialView("_ItemRows", rows);
		}

		[HttpGet] public async Task<IActionResult> ItemsExport(string? q, int? categoryId, string? type, bool? active)
		{
			var (rows, _) = await _items.SearchItemsAsync(DefaultCompanyId, q, categoryId, type, active, 1, 100000);
			var cats = (await _items.GetCategoriesAsync(DefaultCompanyId)).ToDictionary(c => c.ID, c => c);
			var units = (await _items.GetUnitsAsync(DefaultCompanyId)).ToDictionary(u => u.ID, u => u);
			var headers = new[] { L["Item code"].Value, L["Name"].Value, "Name (EN)", L["Category"].Value, L["Unit"].Value, L["Barcode"].Value, L["Sales price"].Value, L["Type"].Value, L["Status"].Value };
			var data = rows.Select(i => (IReadOnlyList<object?>)new object?[] {
				i.ItemCode, i.Name, i.NameEn,
				cats.TryGetValue(i.ItemCategoryId, out var c) ? c.Name : "",
				units.TryGetValue(i.BaseUoMId, out var u) ? u.Name : "",
				i.Barcode, i.SalesPrice, i.ItemType, i.IsActive ? L["Active"].Value : L["Inactive"].Value });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Items"].Value, headers, data, L["Items — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "items.xlsx");
		}

		// populate the dropdown lists used by the standalone item form
		private async Task PopulateItemFormListsAsync(int? excludeItemId = null)
		{
			ViewBag.Categories = await _items.GetCategoriesAsync(DefaultCompanyId);
			ViewBag.Units = await _items.GetUnitsAsync(DefaultCompanyId);
			ViewBag.TaxCodes = await _context.TaxCodes.AsNoTracking().Where(t => t.CompanyID == DefaultCompanyId && t.Kind == "VAT" && t.IsActive).ToListAsync();
			// candidate components for a composite item (exclude itself + other composites)
			ViewBag.ComponentItems = await _context.Items.AsNoTracking()
				.Where(i => i.CompanyID == DefaultCompanyId && i.IsActive && !i.IsComposite && (excludeItemId == null || i.ID != excludeItemId))
				.OrderBy(i => i.ItemCode).ToListAsync();
		}

		// standalone "Add item" page (Metronic add-product design)
		[HttpGet] public async Task<IActionResult> CreateItem(bool embed = false)
		{
			await PopulateItemFormListsAsync();
			ViewBag.IsEdit = false;
			ViewBag.Embed = embed;   // when true, ItemForm uses the chrome-less _LayoutEmbed (shown inside a modal iframe)
			ViewBag.ItemUnits = new List<UoMRowInput>();
			ViewBag.ItemComponents = new List<ItemComponent>();
			return View("ItemForm", new Item { IsActive = true });
		}

		// standalone "Edit item" page
		[HttpGet] public async Task<IActionResult> EditItem(int id)
		{
			var item = await _items.GetItemAsync(DefaultCompanyId, id);
			if (item == null) { TempData["InvErr"] = L["Item not found"].Value; return RedirectToAction(nameof(Items)); }
			await PopulateItemFormListsAsync(id);
			ViewBag.IsEdit = true;
			ViewBag.ItemUnits = await _items.GetItemUnitsAsync(id);
			ViewBag.ItemComponents = await _items.GetItemComponentsAsync(id);
			ViewBag.ItemImages = await _items.GetItemImagesAsync(DefaultCompanyId, id);   // storefront gallery
			// analytics for the item card (last/avg/dates + balance trend) — totals across ALL warehouses
			var movements = await _stock.GetMovementsAsync(DefaultCompanyId, id, null);
			var itemBals = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == DefaultCompanyId && b.ItemId == id).ToListAsync();
			decimal bq = itemBals.Sum(b => b.QtyOnHand), bv = itemBals.Sum(b => b.TotalValue);
			decimal ba = bq != 0 ? Math.Round(bv / bq, 2, MidpointRounding.AwayFromZero) : 0m;
			ViewBag.Movements = movements; ViewBag.BalQty = bq; ViewBag.BalValue = bv; ViewBag.BalAvg = ba;
			// per-warehouse on-hand breakdown (only warehouses with a non-zero balance)
			var whById = (await _warehouses.GetWarehousesAsync(DefaultCompanyId)).ToDictionary(w => w.ID, w => w);
			ViewBag.WarehouseBalances = itemBals.Where(b => b.QtyOnHand != 0)
				.Select(b => { whById.TryGetValue(b.WarehouseId, out var w); return (code: w?.Code ?? ("#" + b.WarehouseId), nameAr: w?.Name ?? "", nameEn: w?.NameEn, qty: b.QtyOnHand, value: b.TotalValue); })
				.OrderByDescending(x => x.qty).ToList();

			// ---- 4-2: manufacturing summary card (only for Assembly = manufactured items) ----
			if (item.IsComposite && item.CompositeType == "Assembly")
			{
				var comps = (List<ItemComponent>)ViewBag.ItemComponents;
				var compIds = comps.Select(c => c.ComponentItemId).Distinct().ToList();
				var compItems = await _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && compIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i);
				// current average unit cost per component (Σ value / Σ qty across all warehouses)
				var compBals = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == DefaultCompanyId && compIds.Contains(b.ItemId))
					.GroupBy(b => b.ItemId).Select(g => new { ItemId = g.Key, Qty = g.Sum(x => x.QtyOnHand), Val = g.Sum(x => x.TotalValue) }).ToDictionaryAsync(x => x.ItemId, x => x);
				var bomLines = new List<(string name, decimal qty, decimal scrapPct, decimal unitCost, decimal lineCost)>();
				decimal stdMaterial = 0m;
				foreach (var c in comps)
				{
					compItems.TryGetValue(c.ComponentItemId, out var ci);
					var nm = ci == null ? ("#" + c.ComponentItemId) : (ci.ItemCode + " — " + ci.Name);
					decimal uc = (compBals.TryGetValue(c.ComponentItemId, out var bb) && bb.Qty != 0) ? Math.Round(bb.Val / bb.Qty, 4, MidpointRounding.AwayFromZero) : 0m;
					var line = Math.Round(c.Quantity * (1 + c.ScrapPct / 100m) * uc, 4); stdMaterial += line;
					bomLines.Add((nm, c.Quantity, c.ScrapPct, uc, line));
				}
				var (stdLabor, stdOverhead) = await _manuf.ComputeRoutingCostAsync(DefaultCompanyId, id, 1);
				ViewBag.IsManufactured = true;
				ViewBag.BomLines = bomLines;
				ViewBag.StdMaterial = stdMaterial;
				ViewBag.StdLabor = stdLabor;
				ViewBag.StdOverhead = stdOverhead;
				ViewBag.RoutingOps = await _manuf.GetRoutingAsync(DefaultCompanyId, id);
			}
			return View("ItemForm", item);
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> CreateItem(ItemInput input, IFormFile? imageFile, List<IFormFile>? galleryFiles, bool embed = false)
		{
			var saved = await SaveItemImageAsync(imageFile);
			if (saved != null) input.ImagePath = saved;
			input.StoreHoverImage = null;   // hover is derived from the gallery (first image), not a separate field
			var (ok, err, item) = await _items.CreateItemAsync(DefaultCompanyId, input, null);
			if (!ok)
			{
				TempData["InvErr"] = err;
				return RedirectToAction(nameof(CreateItem), embed ? new { embed = true } : null);
			}
			// storefront gallery (extra images) — display only; first image also becomes the catalog hover
			if (item != null && galleryFiles != null && galleryFiles.Count > 0)
			{
				var paths = new List<string>();
				foreach (var f in galleryFiles) { var p = await SaveItemImageAsync(f); if (p != null) paths.Add(p); }
				await _items.AddItemImagesAsync(DefaultCompanyId, item.ID, paths);
			}
			if (item != null) await _items.RecomputeStoreHoverAsync(DefaultCompanyId, item.ID);
			if (embed && item != null)
			{
				// inside a modal iframe → tell the parent screen about the new item so it can append+select it
				var isAr = (HttpContext.Items["Culture"]?.ToString() == "ar");
				var nm = isAr ? item.Name : (item.NameEn ?? item.Name);
				var payload = System.Text.Json.JsonSerializer.Serialize(new { id = item.ID, name = nm, code = item.ItemCode });
				return Content($"<!doctype html><html><body><script>parent.postMessage({{cbQuickAdd:{payload}}},'*');</script></body></html>", "text/html");
			}
			TempData["InvMsg"] = L["Item added"].Value;
			return RedirectToAction(nameof(Items));
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> EditItem(int id, ItemInput input, IFormFile? imageFile, List<IFormFile>? galleryFiles, string? removeImageIds)
		{
			var saved = await SaveItemImageAsync(imageFile);
			if (saved != null) input.ImagePath = saved;
			input.StoreHoverImage = null;   // hover is derived from the gallery (first image), not a separate field
			var (ok, err) = await _items.UpdateItemAsync(DefaultCompanyId, id, input, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(EditItem), new { id }); }
			// storefront gallery: remove the deselected images, then append any newly uploaded ones
			if (!string.IsNullOrWhiteSpace(removeImageIds))
			{
				var ids = removeImageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0);
				var deletedPaths = await _items.RemoveItemImagesAsync(DefaultCompanyId, id, ids);
				foreach (var pth in deletedPaths) DeleteUploadedFile(pth);   // also delete the physical files
			}
			if (galleryFiles != null && galleryFiles.Count > 0)
			{
				var paths = new List<string>();
				foreach (var f in galleryFiles) { var p = await SaveItemImageAsync(f); if (p != null) paths.Add(p); }
				await _items.AddItemImagesAsync(DefaultCompanyId, id, paths);
			}
			await _items.RecomputeStoreHoverAsync(DefaultCompanyId, id);   // hover = first gallery image (or null)
			TempData["InvMsg"] = L["Item updated"].Value;
			return RedirectToAction(nameof(Items));
		}

		// ---------------- Warehouses ----------------
		[HttpGet] public async Task<IActionResult> Warehouses()
		{
			ViewBag.Branches = await _context.Hierarchicals.AsNoTracking().Where(h => h.IsActive == true).OrderBy(h => h.H_Name).ToListAsync();
			ViewBag.Employees = await _context.Employee.AsNoTracking().OrderBy(e => e.FullName).ToListAsync();
			return View(await _warehouses.GetWarehousesAsync(DefaultCompanyId));
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> CreateWarehouse(Warehouse model)
		{
			var (ok, err) = await _warehouses.CreateWarehouseAsync(DefaultCompanyId, model, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Warehouse added"].Value : err;
			return RedirectToAction(nameof(Warehouses));
		}

		// Inline "quick add" for the Warehouse dropdown on document screens → {ok,id,name}.
		// D1 WAVE 1 — CRITICAL (stock structure). A warehouse is WHERE STOCK LIVES: creating one unauthorized
		// creates a location that stock can be moved into and out of, outside any approved setup. The right is
		// derived from the neighbouring full warehouse maintenance action, which carries InvPerm("manage").
		// ApiPerm because this returns JSON to an inline dropdown.
		[HttpPost][ValidateAntiForgeryToken]
		[CrossBuy.Models.ApiPerm(CrossBuy.Models.ApiPermAttribute.Inventory, "manage")]
		public async Task<IActionResult> WarehouseQuickAdd(string code, string name, string? nameEn)
		{
			if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return Json(new { ok = false, error = L["Code and name are required"].Value });
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return Json(new { ok = false, error = L["You do not have permission to perform this action"].Value });
			// NameEn is a required (NOT NULL) column — when the English name is left blank fall back to the Arabic name
			// (never null, else the INSERT fails). Trim once.
			var arName = name.Trim();
			var enName = string.IsNullOrWhiteSpace(nameEn) ? arName : nameEn.Trim();
			var w = new Warehouse { Code = code.Trim(), Name = arName, NameEn = enName };
			var (ok, err) = await _warehouses.CreateWarehouseAsync(scope.CompanyId, w, null);
			if (!ok) return Json(new { ok = false, error = err });
			// return the SAME "Code — Name" label the server-rendered options use, so the appended option is consistent
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			return Json(new { ok = true, id = w.ID, name = w.Code + " — " + (isAr ? w.Name : w.NameEn) });
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> EditWarehouse(int id, Warehouse model)
		{
			var (ok, err) = await _warehouses.UpdateWarehouseAsync(DefaultCompanyId, id, model, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Warehouse updated"].Value : err;
			return RedirectToAction(nameof(Warehouses));
		}

		// ---------------- Warehouse sections / racks (BinLocation tree) ----------------
		[HttpGet] public async Task<IActionResult> WarehouseSections(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.FilterWarehouseId = warehouseId;
			ViewBag.Bins = warehouseId != null ? await _warehouses.GetBinLocationsAsync(warehouseId.Value) : new List<BinLocation>();
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> SaveBinLocation(int warehouseId, int id, string code, string? name, string locationType, int? parentId, bool isActive = true)
		{
			var (ok, err) = await _warehouses.SaveBinLocationAsync(warehouseId, id, code, name, locationType, parentId, isActive);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Saved"].Value : err;
			return RedirectToAction(nameof(WarehouseSections), new { warehouseId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> DeleteBinLocation(int id, int warehouseId)
		{
			var (ok, err) = await _warehouses.DeleteBinLocationAsync(id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Deleted"].Value : err;
			return RedirectToAction(nameof(WarehouseSections), new { warehouseId });
		}

		// ---------------- Item default locations per warehouse (section required, rack optional) ----------------
		[HttpGet] public async Task<IActionResult> ItemLocations(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				var bins = await _warehouses.GetBinLocationsAsync(warehouseId.Value);
				ViewBag.Sections = bins.Where(b => b.LocationType == "Section" && b.IsActive).ToList();
				ViewBag.Racks = bins.Where(b => b.LocationType == "Rack" && b.IsActive).ToList();
				var items = await _items.GetItemsAsync(DefaultCompanyId);
				var settings = (await _context.ItemWarehouseSettings.AsNoTracking().Where(s => s.WarehouseId == warehouseId).ToListAsync())
					.ToDictionary(s => s.ItemId, s => s);
				ViewBag.Rows = items.Where(i => i.ItemType == "Stockable").Select(i =>
				{
					settings.TryGetValue(i.ID, out var s);
					return new { ItemId = i.ID, i.ItemCode, ItemName = i.Name, SectionId = s?.DefaultSectionId, RackId = s?.DefaultBinLocationId };
				}).ToList();
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> SaveItemLocations(int warehouseId, string? rowsJson)
		{
			List<ItemLocationRow> rows;
			try { rows = System.Text.Json.JsonSerializer.Deserialize<List<ItemLocationRow>>(rowsJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { rows = new(); }
			// validate: chosen rack (if any) must belong to the chosen section
			var bins = (await _warehouses.GetBinLocationsAsync(warehouseId)).ToDictionary(b => b.ID, b => b);
			foreach (var r in rows)
			{
				if (r.ItemId <= 0) continue;
				if (r.SectionId == null || r.SectionId <= 0) continue;   // section is UI-required; skip rows left unset (backward compat)
				if (!bins.TryGetValue(r.SectionId.Value, out var sec) || sec.LocationType != "Section") { TempData["InvErr"] = L["Invalid section"].Value; return RedirectToAction(nameof(ItemLocations), new { warehouseId }); }
				int? rackId = (r.RackId != null && r.RackId > 0) ? r.RackId : null;
				if (rackId != null && (!bins.TryGetValue(rackId.Value, out var rk) || rk.LocationType != "Rack" || rk.ParentId != r.SectionId))
				{ TempData["InvErr"] = L["Rack must belong to the selected section"].Value; return RedirectToAction(nameof(ItemLocations), new { warehouseId }); }

				var s = await _context.ItemWarehouseSettings.FirstOrDefaultAsync(x => x.ItemId == r.ItemId && x.WarehouseId == warehouseId);
				if (s == null) { s = new ItemWarehouseSetting { ItemId = r.ItemId, WarehouseId = warehouseId }; _context.ItemWarehouseSettings.Add(s); }
				s.DefaultSectionId = r.SectionId; s.DefaultBinLocationId = rackId;
			}
			await _context.SaveChangesAsync();
			TempData["InvMsg"] = L["Item locations saved"].Value;
			return RedirectToAction(nameof(ItemLocations), new { warehouseId });
		}

		public class ItemLocationRow { public int ItemId { get; set; } public int? SectionId { get; set; } public int? RackId { get; set; } }

		// JSON feed for inbound document line editors: the warehouse's sections + racks + each item's default location.
		// Views use it to render section/rack pickers per line and prefill from the item's default; the picked (most specific) id is stamped on the movement.
		[HttpGet] public async Task<IActionResult> WarehouseBinsData(int warehouseId)
		{
			var bins = await _warehouses.GetBinLocationsAsync(warehouseId);
			var sections = bins.Where(b => b.LocationType == "Section" && b.IsActive)
				.OrderBy(b => b.Code).Select(b => new { id = b.ID, code = b.Code, name = b.Name }).ToList();
			var racks = bins.Where(b => b.LocationType == "Rack" && b.IsActive)
				.OrderBy(b => b.Code).Select(b => new { id = b.ID, code = b.Code, name = b.Name, sectionId = b.ParentId }).ToList();
			var defaults = await _context.ItemWarehouseSettings.AsNoTracking()
				.Where(s => s.WarehouseId == warehouseId && s.DefaultSectionId != null)
				.Select(s => new { s.ItemId, s.DefaultSectionId, s.DefaultBinLocationId }).ToListAsync();
			var itemDefaults = defaults.ToDictionary(d => d.ItemId.ToString(), d => new { sectionId = d.DefaultSectionId, rackId = d.DefaultBinLocationId });
			return Json(new { sections, racks, itemDefaults });
		}

		// ---------------- Rack-level stock (BinStock): balances + count + relocate ----------------
		[HttpGet] public async Task<IActionResult> RackBalances(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				var bins = (await _warehouses.GetBinLocationsAsync(warehouseId.Value)).ToDictionary(b => b.ID, b => b);
				ViewBag.Bins = bins;
				ViewBag.ActiveBins = bins.Values.Where(b => b.IsActive).OrderBy(b => b.LocationType == "Rack" ? 1 : 0).ThenBy(b => b.Code).ToList();
				var binStocks = await _stock.GetBinStocksAsync(DefaultCompanyId, warehouseId.Value);
				var itemsById = (await _items.GetItemsAsync(DefaultCompanyId)).ToDictionary(i => i.ID, i => i);
				ViewBag.Items = itemsById;
				ViewBag.Rows = binStocks
					.Select(bs => new
					{
						bs.ItemId, bs.BinLocationId, bs.QtyOnHand,
						ItemCode = itemsById.TryGetValue(bs.ItemId, out var it) ? it.ItemCode : ("#" + bs.ItemId),
						ItemName = itemsById.TryGetValue(bs.ItemId, out var it2) ? it2.Name : "",
						BinCode = bins.TryGetValue(bs.BinLocationId, out var b) ? b.Code : ("#" + bs.BinLocationId),
						BinName = bins.TryGetValue(bs.BinLocationId, out var b2) ? b2.Name : "",
						BinType = bins.TryGetValue(bs.BinLocationId, out var b3) ? b3.LocationType : ""
					})
					.OrderBy(r => r.BinCode).ThenBy(r => r.ItemCode).ToList();
				// reconciliation: located (Σ bins) vs warehouse total, per item that has bin data
				var located = binStocks.GroupBy(b => b.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.QtyOnHand));
				var whBals = (await _stock.GetBalancesAsync(DefaultCompanyId, warehouseId.Value)).ToDictionary(b => b.ItemId, b => b.QtyOnHand);
				ViewBag.Recon = located.Keys.Union(whBals.Keys.Where(k => whBals[k] != 0))
					.Select(id => new
					{
						ItemCode = itemsById.TryGetValue(id, out var it) ? it.ItemCode : ("#" + id),
						ItemName = itemsById.TryGetValue(id, out var it2) ? it2.Name : "",
						Located = located.TryGetValue(id, out var lq) ? lq : 0m,
						Total = whBals.TryGetValue(id, out var tq) ? tq : 0m
					})
					.Where(x => x.Located != 0m || x.Total != 0m)
					.OrderBy(x => x.ItemCode).ToList();
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> InitializeBinStock(int warehouseId)
		{
			var (ok, err, n) = await _stock.InitializeBinStockFromDefaultsAsync(DefaultCompanyId, warehouseId, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Distributed {0} items to their default locations"].Value, n) : err;
			return RedirectToAction(nameof(RackBalances), new { warehouseId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> SetBinCount(int warehouseId, int binLocationId, int itemId, decimal countedQty)
		{
			var (ok, err) = await _stock.SetBinCountAsync(DefaultCompanyId, warehouseId, binLocationId, itemId, countedQty, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Rack count saved"].Value : err;
			return RedirectToAction(nameof(RackBalances), new { warehouseId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> RelocateBin(int warehouseId, int itemId, int fromBinId, int toBinId, decimal qty)
		{
			var (ok, err) = await _stock.RelocateBinAsync(DefaultCompanyId, warehouseId, itemId, fromBinId, toBinId, qty, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Quantity relocated between locations"].Value : err;
			return RedirectToAction(nameof(RackBalances), new { warehouseId });
		}

		// ================= Phase I1: stock movements / balances =================

		// shared lookups for the movement form + list rendering
		private async Task PopulateStockListsAsync()
		{
			ViewBag.Items = await _items.GetItemsAsync(DefaultCompanyId);
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.Units = await _items.GetUnitsAsync(DefaultCompanyId);
		}

		// shared: pagination headers consumed by the list views' fetch JS
		private void SetPaging(int total, int page, int pageSize)
		{
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
		}

		[HttpGet] public async Task<IActionResult> StockMovements(int? itemId, int? warehouseId)
		{
			await PopulateStockListsAsync();
			ViewBag.FilterItemId = itemId; ViewBag.FilterWarehouseId = warehouseId;
			return View();   // shell; rows via StockMovementsData
		}

		[HttpGet] public async Task<IActionResult> StockMovementsData(string? q, int? warehouseId, int page = 1, int pageSize = 25)
		{
			var src = _context.StockMovements.AsNoTracking().Where(m => m.CompanyID == DefaultCompanyId);
			if (warehouseId.HasValue && warehouseId.Value > 0) src = src.Where(m => m.WarehouseId == warehouseId.Value);
			var q0 = from m in src
					 join i in _context.Items.AsNoTracking() on m.ItemId equals i.ID
					 join w in _context.Warehouses.AsNoTracking() on m.WarehouseId equals w.ID into wj
					 from w in wj.DefaultIfEmpty()
					 join bl in _context.BinLocations.AsNoTracking() on m.BinLocationId equals bl.ID into blj
					 from bl in blj.DefaultIfEmpty()
					 select new MovementRow
					 {
						 Id = m.ID, ItemId = m.ItemId, ItemCode = i.ItemCode, ItemName = i.Name, ItemNameEn = i.NameEn,
						 WarehouseCode = w != null ? w.Code : "", BinLocationCode = bl != null ? bl.Code : null,
						 MovementDate = m.MovementDate, Direction = m.Direction,
						 QtyBase = m.QtyBase, UnitCost = m.UnitCost, TotalCost = m.TotalCost, SourceType = m.SourceType, JournalEntryId = m.JournalEntryId
					 };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<MovementRow>(terms, s => r => r.ItemCode.Contains(s) || r.ItemName.Contains(s)
					|| (r.ItemNameEn != null && r.ItemNameEn.Contains(s)) || (r.SourceType != null && r.SourceType.Contains(s)) || r.WarehouseCode.Contains(s));
				if (pred != null) q0 = q0.Where(pred);
			}
			var total = await q0.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await q0.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_MovementRows", rows);
		}

		[HttpGet] public async Task<IActionResult> NewMovement()
		{
			await PopulateStockListsAsync();
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> PostMovement(string movementType, int itemId, int warehouseId, decimal qty, int? uomId,
			decimal? unitCost, string? batchNo, DateTime? expiry, string? serialNo, DateTime? date, string? notes, int? binLocationId)
		{
			// movementType encodes direction + source: opening / receipt / issue / adjust_in / adjust_out
			short dir = (movementType == "issue" || movementType == "adjust_out") ? (short)-1 : (short)1;
			var src = movementType switch
			{
				"opening" => "Opening",
				"receipt" => "Receipt",
				"issue" => "Issue",
				_ => "Adjustment"
			};
			var (ok, err, _) = await _stock.PostMovementAsync(DefaultCompanyId, new MovementRequest
			{
				Date = date ?? DateTime.UtcNow, ItemId = itemId, WarehouseId = warehouseId, Direction = dir,
				Qty = qty, UoMId = uomId, UnitCostInBase = unitCost, BatchNo = batchNo, Expiry = expiry, SerialNo = serialNo,
				BinLocationId = binLocationId,   // inbound = where goods land; outbound = which rack they were picked from
				SourceType = src, PostToGl = true, Notes = notes
			}, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewMovement)); }
			TempData["InvMsg"] = L["Movement posted; balance and journal entry updated"].Value;
			return RedirectToAction(nameof(StockMovements));
		}

		[HttpGet] public async Task<IActionResult> StockBalances(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.FilterWarehouseId = warehouseId;
			return View();   // shell; rows loaded via StockBalancesData
		}

		[HttpGet] public async Task<IActionResult> ItemsSuggest(string? term)
		{
			var list = await _items.SuggestItemsAsync(DefaultCompanyId, term, 10);
			return Json(list.Select(x => new { value = x.code, name = x.name }));
		}

		// item picker for select2-ajax (returns id + display text) — used by the price-list line editor
		[HttpGet] public async Task<IActionResult> ItemPickData(string? term)
		{
			var t = (term ?? "").Trim();
			var query = _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && i.IsActive);
			if (t.Length > 0) query = query.Where(i => i.ItemCode.Contains(t) || i.Name.Contains(t) || (i.NameEn != null && i.NameEn.Contains(t)) || (i.Barcode != null && i.Barcode.Contains(t)));
			var rows = await query.OrderBy(i => i.ItemCode)
				.Select(i => new { id = i.ID, code = i.ItemCode, name = i.Name, nameEn = i.NameEn, price = i.SalesPrice ?? 0m, cost = i.OpeningCost, baseUom = i.BaseUoMId })
				.Take(20).ToListAsync();
			return Json(new { results = rows.Select(r => new { id = r.id, text = r.code + " — " + r.name, code = r.code, name = r.name, nameEn = r.nameEn, price = r.price, cost = r.cost, baseUom = r.baseUom }) });
		}

		// 3-way match report for a PO (P3-6)
		[HttpGet] public async Task<IActionResult> PoMatch(int id)
		{
			var result = await _match.CheckPoAsync(DefaultCompanyId, id);
			var po = await _context.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(p => p.ID == id && p.CompanyID == DefaultCompanyId);
			if (po == null) { TempData["InvErr"] = L["Purchase order not found"].Value; return RedirectToAction(nameof(PurchaseOrders)); }
			ViewBag.PoNo = po.OrderNo; ViewBag.PoId = id;
			return View(result);
		}

		// ---------------- Price lists (P3-5) ----------------
		[HttpGet] public IActionResult PriceLists() => View();   // shell; rows via PriceListsData

		[HttpGet] public async Task<IActionResult> PriceListsData(string? q, bool? active, int page = 1, int pageSize = 25)
		{
			var (rows, total) = await _pricing.SearchAsync(DefaultCompanyId, q, active, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_PriceListRows", rows);
		}

		[HttpGet] public async Task<IActionResult> PriceListEditor(int? id)
		{
			Models.Context.Inventory.PriceList model;
			if (id.HasValue && id.Value > 0)
			{
				model = await _pricing.GetAsync(DefaultCompanyId, id.Value) ?? new Models.Context.Inventory.PriceList { IsActive = true };
				// resolve item code/name for the existing lines so the editor can display them
				var ids = model.Lines.Select(l => l.ItemId).Distinct().ToList();
				ViewBag.LineItems = await _context.Items.AsNoTracking().Where(i => ids.Contains(i.ID))
					.Select(i => new { i.ID, i.ItemCode, i.Name }).ToDictionaryAsync(x => x.ID, x => x.ItemCode + " — " + x.Name);
			}
			else
			{
				model = new Models.Context.Inventory.PriceList { IsActive = true };
				ViewBag.LineItems = new Dictionary<int, string>();
			}
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			ViewBag.CustomerName = model.CustomerId.HasValue
				? await _context.Customers.AsNoTracking().Where(c => c.ID == model.CustomerId.Value).Select(c => c.Name).FirstOrDefaultAsync()
				: null;
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> SavePriceList(int id, string code, string name, string? nameEn, string? segment, int priority,
			bool isDefault, bool isActive, DateTime? validFrom, DateTime? validTo, string? linesJson, int? currencyId, int? customerId)
		{
			List<Models.Context.Inventory.PriceListLine> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<Models.Context.Inventory.PriceListLine>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { lines = new(); }
			var dto = new Models.Context.Inventory.PriceList
			{
				ID = id, Code = code ?? "", Name = name ?? "", NameEn = nameEn, Segment = segment, Priority = priority,
				CurrencyId = currencyId, CustomerId = customerId,
				IsDefault = isDefault, IsActive = isActive, ValidFrom = validFrom, ValidTo = validTo, Lines = lines
			};
			var (ok, err, newId) = await _pricing.SaveAsync(DefaultCompanyId, dto, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? (id > 0 ? L["Price list updated"].Value : L["Price list created"].Value) : err;
			return ok ? RedirectToAction(nameof(PriceLists)) : RedirectToAction(nameof(PriceListEditor), new { id });
		}

		// Pricing 2-2: clone a price list (+ its lines) as a new inactive draft
		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CopyPriceList(int id)
		{
			var src = await _pricing.GetAsync(DefaultCompanyId, id);
			if (src == null) { TempData["InvErr"] = L["Price list not found"].Value; return RedirectToAction(nameof(PriceLists)); }
			var suffix = (DateTime.UtcNow.Ticks % 100000).ToString();
			var dto = new Models.Context.Inventory.PriceList
			{
				ID = 0, Code = (src.Code + "-COPY" + suffix), Name = src.Name + " (نسخة)", NameEn = src.NameEn,
				CurrencyId = src.CurrencyId, CustomerId = src.CustomerId, Segment = src.Segment, Priority = src.Priority,
				IsDefault = false, IsActive = false, ValidFrom = src.ValidFrom, ValidTo = src.ValidTo,
				Lines = src.Lines.Select(l => new Models.Context.Inventory.PriceListLine { ItemId = l.ItemId, MinQty = l.MinQty, UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, ValidFrom = l.ValidFrom, ValidTo = l.ValidTo }).ToList()
			};
			var (ok, err, newId) = await _pricing.SaveAsync(DefaultCompanyId, dto, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Price list copied as a new draft"].Value : err;
			return ok ? RedirectToAction(nameof(PriceListEditor), new { id = newId }) : RedirectToAction(nameof(PriceLists));
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> DeletePriceList(int id)
		{
			var ok = await _pricing.DeleteAsync(DefaultCompanyId, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Price list deleted"].Value : L["Delete failed"].Value;
			return RedirectToAction(nameof(PriceLists));
		}

		// ---------------- HM-4: bulk price change ----------------
		[HttpGet] public async Task<IActionResult> BulkPriceChange()
		{
			ViewBag.PriceLists = await _context.PriceLists.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId && p.IsActive)
				.OrderBy(p => p.Name).Select(p => new { p.ID, p.Name }).ToListAsync();
			ViewBag.Categories = await _context.ItemCategories.AsNoTracking().Where(c => c.CompanyID == DefaultCompanyId)
				.OrderBy(c => c.Name).Select(c => new { c.ID, c.Name }).ToListAsync();
			return View();
		}

		// Preview — read-only; returns the before/after grid. Writes NOTHING.
		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> BulkPreview(int priceListId, int? categoryId, string adjustType, decimal value, string priceRounding)
		{
			var (ok, err, rows) = await _pricing.BulkPreviewAsync(DefaultCompanyId, priceListId, categoryId, adjustType, value, priceRounding ?? "None");
			if (!ok) return Json(new { ok = false, error = err });
			return Json(new { ok = true, rows = rows.Select(r => new { r.ItemId, r.UoMId, r.ItemCode, r.ItemName, oldPrice = r.OldPrice, newPrice = r.NewPrice }) });
		}

		// Execute — re-verifies the shown baseline, then writes prices + audit in one transaction. Mandatory reason.
		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> BulkExecute(int priceListId, int? categoryId, string adjustType, decimal value, string priceRounding, string reason, string? baselineJson)
		{
			List<BulkBaselineItem> baseline;
			try { baseline = System.Text.Json.JsonSerializer.Deserialize<List<BulkBaselineItem>>(baselineJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
			catch { baseline = new(); }
			var (ok, err, batchId, changed) = await _pricing.BulkExecuteAsync(DefaultCompanyId, priceListId, categoryId, adjustType, value, priceRounding ?? "None", reason ?? "", baseline, User?.Identity?.Name);
			return Json(new { ok, error = err, batchId, changed });
		}

		// Undo — writes the logged OLD prices back as a new batch. Mandatory reason.
		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> BulkUndo(Guid batchId, string reason)
		{
			var (ok, err, newBatchId, restored) = await _pricing.BulkUndoAsync(DefaultCompanyId, batchId, reason ?? "", User?.Identity?.Name);
			return Json(new { ok, error = err, newBatchId, restored });
		}

		// ---------------- HM-4: shelf labels (A4 grid, browser print) ----------------
		[HttpGet] public async Task<IActionResult> ShelfLabels(int? priceListId)
		{
			ViewBag.PriceLists = await _context.PriceLists.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId && p.IsActive)
				.OrderBy(p => p.Name).Select(p => new { p.ID, p.Name }).ToListAsync();
			ViewBag.SelectedList = priceListId;
			ViewBag.Cards = priceListId.HasValue ? await BuildLabelCardsAsync(priceListId.Value) : new List<ShelfLabelCard>();
			return View();
		}

		// Build the printable cards for a price list's Fixed lines: name · price (currency dp) · unit · EAN-13 SVG
		// (or the digits as text if the barcode is not a valid EAN-13) · scale code as text for weighted items.
		private async Task<List<ShelfLabelCard>> BuildLabelCardsAsync(int priceListId)
		{
			var pl = await _context.PriceLists.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyID == DefaultCompanyId && p.ID == priceListId);
			if (pl == null) return new();
			int functional = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			int dp = await _rounding.DecimalsAsync(DefaultCompanyId, pl.CurrencyId ?? functional);
			string fmt = dp > 0 ? "0." + new string('0', dp) : "0";
			var lines = await (from l in _context.PriceListLines.AsNoTracking()
							   join i in _context.Items.AsNoTracking() on l.ItemId equals i.ID
							   where l.PriceListId == priceListId && i.CompanyID == DefaultCompanyId && l.PricingMode == "Fixed" && l.UnitPrice != null
							   join u in _context.UnitsOfMeasure.AsNoTracking() on i.BaseUoMId equals u.ID into uj
							   from u in uj.DefaultIfEmpty()
							   orderby i.Name
							   select new { i.Name, i.Barcode, i.IsWeighted, i.ScaleCode, l.UnitPrice, UnitName = u != null ? u.Name : null }).ToListAsync();
			var cards = new List<ShelfLabelCard>();
			foreach (var ln in lines)
			{
				var (svg, ok, _) = _labels.BuildEan13Svg(ln.Barcode);
				cards.Add(new ShelfLabelCard
				{
					Name = ln.Name, Price = (ln.UnitPrice ?? 0m).ToString(fmt, System.Globalization.CultureInfo.InvariantCulture),
					Unit = ln.UnitName ?? "", Weighted = ln.IsWeighted, ScaleCode = ln.ScaleCode,
					BarcodeSvg = ok ? svg : null, BarcodeText = ln.Barcode ?? ""
				});
			}
			return cards;
		}

		// ---------------- Promotions (Pricing 2B) ----------------
		[HttpGet] public IActionResult Promotions() => View();   // shell; rows via PromotionsData

		[HttpGet] public async Task<IActionResult> PromotionsData(string? q, bool? active, int page = 1, int pageSize = 25)
		{
			var (rows, total) = await _pricing.SearchPromotionsAsync(DefaultCompanyId, q, active, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_PromotionRows", rows);
		}

		[HttpGet] public async Task<IActionResult> PromotionEditor(int? id)
		{
			Models.Context.Inventory.Promotion model = (id.HasValue && id.Value > 0
				? await _pricing.GetPromotionAsync(DefaultCompanyId, id.Value) : null)
				?? new Models.Context.Inventory.Promotion { IsActive = true, DiscountType = "Percent", MinQty = 1 };
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			ViewBag.Categories = await _context.ItemCategories.AsNoTracking().Where(c => c.CompanyID == DefaultCompanyId).OrderBy(c => c.Name).ToListAsync();
			ViewBag.Customers = await _context.Customers.AsNoTracking().Where(c => c.CompanyID == DefaultCompanyId).OrderBy(c => c.Name).ToListAsync();
			ViewBag.ItemName = model.ItemId.HasValue
				? await _context.Items.AsNoTracking().Where(i => i.ID == model.ItemId.Value).Select(i => i.ItemCode + " — " + i.Name).FirstOrDefaultAsync()
				: null;
			return View(model);
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> SavePromotion(int id, string code, string name, string? nameEn, string discountType, decimal value,
			int? currencyId, int? itemId, int? itemCategoryId, int? customerId, string? segment, decimal minQty, int priority,
			DateTime? validFrom, DateTime? validTo, bool isActive)
		{
			var dto = new Models.Context.Inventory.Promotion
			{
				ID = id, Code = code ?? "", Name = name ?? "", NameEn = nameEn, DiscountType = discountType, Value = value,
				CurrencyId = currencyId, ItemId = (itemId.HasValue && itemId.Value > 0) ? itemId : null,
				ItemCategoryId = (itemCategoryId.HasValue && itemCategoryId.Value > 0) ? itemCategoryId : null,
				CustomerId = (customerId.HasValue && customerId.Value > 0) ? customerId : null, Segment = segment,
				MinQty = minQty, Priority = priority, ValidFrom = validFrom, ValidTo = validTo, IsActive = isActive
			};
			var (ok, err, warning, newId) = await _pricing.SavePromotionAsync(DefaultCompanyId, dto, User?.Identity?.Name);
			// HM-5: on success, append the soft typo warning (high percent) to the success message (rendered in the
			// existing InvMsg slot — no new layout slot). Hard errors still block as before.
			var okMsg = (id > 0 ? L["Promotion updated"].Value : L["Promotion created"].Value) + (!string.IsNullOrEmpty(warning) ? " — " + warning : "");
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? okMsg : err;
			return ok ? RedirectToAction(nameof(Promotions)) : RedirectToAction(nameof(PromotionEditor), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> DeletePromotion(int id)
		{
			var ok = await _pricing.DeletePromotionAsync(DefaultCompanyId, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Promotion deleted"].Value : L["Delete failed"].Value;
			return RedirectToAction(nameof(Promotions));
		}

		// price lookup for the sales forms (P3-5d)
		[HttpGet] public async Task<IActionResult> PriceLookup(int itemId, int? customerId, int? currencyId, decimal qty = 1, DateTime? date = null)
		{
			string? segment = null;
			if (customerId.HasValue && customerId.Value > 0)
				segment = await _context.Customers.AsNoTracking().Where(c => c.ID == customerId.Value).Select(c => c.Segment).FirstOrDefaultAsync();
			var r = await _pricing.GetPriceAsync(DefaultCompanyId, itemId, customerId, segment, currencyId, qty, date ?? DateTime.UtcNow);
			return Json(new { unitPrice = r.UnitPrice, discountPercent = r.DiscountPercent, source = r.Source, currencyId = r.CurrencyId, listId = r.PriceListId, listName = r.PriceListName, listNameEn = r.PriceListNameEn, promotionId = r.PromotionId, promotionName = r.PromotionName, promotionNameEn = r.PromotionNameEn });
		}

		[HttpGet] public async Task<IActionResult> StockBalancesData(string? q, int? warehouseId, bool onlyInStock = false, int page = 1, int pageSize = 25)
		{
			var (rows, total, grandValue) = await _stock.SearchBalancesAsync(DefaultCompanyId, q, warehouseId, onlyInStock, page, pageSize);
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
			Response.Headers["X-GrandValue"] = grandValue.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
			return PartialView("_BalanceRows", rows);
		}

		[HttpGet] public async Task<IActionResult> StockBalancesExport(string? q, int? warehouseId, bool onlyInStock = false)
		{
			var (rows, _, _) = await _stock.SearchBalancesAsync(DefaultCompanyId, q, warehouseId, onlyInStock, 1, 100000);
			var headers = new[] { L["Item code"].Value, L["Item"].Value, L["Warehouse"].Value, L["Balance"].Value, L["Average cost"].Value, L["Value"].Value };
			var data = rows.Select(b => (IReadOnlyList<object?>)new object?[] { b.ItemCode, b.ItemName, b.WarehouseCode, b.QtyOnHand, b.AvgCost, b.TotalValue });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Stock balances"].Value, headers, data, L["Stock balances — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "stock-balances.xlsx");
		}

		[HttpGet] public async Task<IActionResult> StockMovementsExport(string? q, int? warehouseId)
		{
			var src = _context.StockMovements.AsNoTracking().Where(m => m.CompanyID == DefaultCompanyId);
			if (warehouseId.HasValue && warehouseId.Value > 0) src = src.Where(m => m.WarehouseId == warehouseId.Value);
			var q0 = from m in src
					 join i in _context.Items.AsNoTracking() on m.ItemId equals i.ID
					 join w in _context.Warehouses.AsNoTracking() on m.WarehouseId equals w.ID into wj from w in wj.DefaultIfEmpty()
					 select new MovementRow { Id = m.ID, ItemCode = i.ItemCode, ItemName = i.Name, WarehouseCode = w != null ? w.Code : "", MovementDate = m.MovementDate, Direction = m.Direction, QtyBase = m.QtyBase, UnitCost = m.UnitCost, TotalCost = m.TotalCost, SourceType = m.SourceType, JournalEntryId = m.JournalEntryId };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<MovementRow>(terms, s => r => r.ItemCode.Contains(s) || r.ItemName.Contains(s) || (r.SourceType != null && r.SourceType.Contains(s)) || r.WarehouseCode.Contains(s)); if (pred != null) q0 = q0.Where(pred); }
			var rows = await q0.OrderByDescending(r => r.Id).ToListAsync();
			var headers = new[] { L["Date"].Value, L["Item code"].Value, L["Item"].Value, L["Warehouse"].Value, L["Type"].Value, L["In"].Value, L["Out"].Value, L["Unit cost"].Value, L["Value"].Value, L["Journal entry no."].Value };
			var data = rows.Select(m => (IReadOnlyList<object?>)new object?[] { m.MovementDate, m.ItemCode, m.ItemName, m.WarehouseCode, m.SourceType, m.Direction == 1 ? m.QtyBase : 0m, m.Direction == -1 ? m.QtyBase : 0m, m.UnitCost, m.TotalCost, m.JournalEntryId });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Stock movements"].Value, headers, data, L["Stock movements — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "stock-movements.xlsx");
		}

		[HttpGet] public async Task<IActionResult> PurchaseOrdersExport(string? q, string? status)
		{
			var q0 = from p in _context.PurchaseOrders.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId)
					 join v in _context.Vendors.AsNoTracking() on p.VendorId equals v.ID into vj from v in vj.DefaultIfEmpty()
					 select new PoRow { Id = p.ID, OrderNo = p.OrderNo, OrderDate = p.OrderDate, PartyName = v != null ? v.Name : null, GrandTotal = p.GrandTotal, Status = p.Status };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<PoRow>(terms, s => r => (r.OrderNo != null && r.OrderNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s))); if (pred != null) q0 = q0.Where(pred); }
			if (!string.IsNullOrWhiteSpace(status)) q0 = q0.Where(r => r.Status == status);
			var rows = await q0.OrderByDescending(r => r.Id).ToListAsync();
			var headers = new[] { L["Order no."].Value, L["Date"].Value, L["Vendor"].Value, L["Total"].Value, L["Status"].Value };
			var data = rows.Select(p => (IReadOnlyList<object?>)new object?[] { p.OrderNo, p.OrderDate, p.PartyName, p.GrandTotal, p.Status });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Purchase orders"].Value, headers, data, L["Purchase orders — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "purchase-orders.xlsx");
		}

		[HttpGet] public async Task<IActionResult> GoodsReceiptsExport(string? q)
		{
			var q0 = from g in _context.GoodsReceipts.AsNoTracking().Where(g => g.CompanyID == DefaultCompanyId)
					 join v in _context.Vendors.AsNoTracking() on g.VendorId equals (int?)v.ID into vj from v in vj.DefaultIfEmpty()
					 join w in _context.Warehouses.AsNoTracking() on g.WarehouseId equals w.ID into wj from w in wj.DefaultIfEmpty()
					 select new GrRow { Id = g.ID, ReceiptNo = g.ReceiptNo, ReceiptDate = g.ReceiptDate, PartyName = v != null ? v.Name : null, WarehouseCode = w != null ? w.Code : "", RefId = g.PurchaseOrderId, TotalCost = g.TotalCost };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<GrRow>(terms, s => r => (r.ReceiptNo != null && r.ReceiptNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s)) || r.WarehouseCode.Contains(s)); if (pred != null) q0 = q0.Where(pred); }
			var rows = await q0.OrderByDescending(r => r.Id).ToListAsync();
			var headers = new[] { L["Note no."].Value, L["Date"].Value, L["Vendor"].Value, L["Warehouse"].Value, L["Purchase order"].Value, L["Total cost"].Value };
			var data = rows.Select(g => (IReadOnlyList<object?>)new object?[] { g.ReceiptNo, g.ReceiptDate, g.PartyName, g.WarehouseCode, g.RefId, g.TotalCost });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Goods receipts"].Value, headers, data, L["Goods receipts — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "goods-receipts.xlsx");
		}

		[HttpGet] public async Task<IActionResult> SalesOrdersExport(string? q, string? status)
		{
			var q0 = from p in _context.SalesOrders.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId)
					 join c in _context.Customers.AsNoTracking() on p.CustomerId equals c.ID into cj from c in cj.DefaultIfEmpty()
					 select new SoRow { Id = p.ID, OrderNo = p.OrderNo, OrderDate = p.OrderDate, PartyName = c != null ? c.Name : null, GrandTotal = p.GrandTotal, Status = p.Status };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<SoRow>(terms, s => r => (r.OrderNo != null && r.OrderNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s))); if (pred != null) q0 = q0.Where(pred); }
			if (!string.IsNullOrWhiteSpace(status)) q0 = q0.Where(r => r.Status == status);
			var rows = await q0.OrderByDescending(r => r.Id).ToListAsync();
			var headers = new[] { L["Order no."].Value, L["Date"].Value, L["Customer"].Value, L["Total"].Value, L["Status"].Value };
			var data = rows.Select(p => (IReadOnlyList<object?>)new object?[] { p.OrderNo, p.OrderDate, p.PartyName, p.GrandTotal, p.Status });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Sales orders"].Value, headers, data, L["Sales orders — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "sales-orders.xlsx");
		}

		[HttpGet] public async Task<IActionResult> QuotationsExport(string? q, string? status)
		{
			var q0 = from p in _context.Quotations.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId)
					 join c in _context.Customers.AsNoTracking() on p.CustomerId equals c.ID into cj from c in cj.DefaultIfEmpty()
					 select new QuoteRow { Id = p.ID, QuoteNo = p.QuoteNo, QuoteDate = p.QuoteDate, ValidUntil = p.ValidUntil, PartyName = c != null ? c.Name : null, GrandTotal = p.GrandTotal, Status = p.Status };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<QuoteRow>(terms, s => r => (r.QuoteNo != null && r.QuoteNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s))); if (pred != null) q0 = q0.Where(pred); }
			if (!string.IsNullOrWhiteSpace(status)) q0 = q0.Where(r => r.Status == status);
			var rows = await q0.OrderByDescending(r => r.Id).ToListAsync();
			var headers = new[] { L["Quotation no."].Value, L["Date"].Value, L["Valid until"].Value, L["Customer"].Value, L["Total"].Value, L["Status"].Value };
			var data = rows.Select(p => (IReadOnlyList<object?>)new object?[] { p.QuoteNo, p.QuoteDate, p.ValidUntil, p.PartyName, p.GrandTotal, p.Status });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Quotations"].Value, headers, data, L["Quotations — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "quotations.xlsx");
		}

		[HttpGet] public async Task<IActionResult> DeliveriesExport(string? q)
		{
			var q0 = from g in _context.DeliveryNotes.AsNoTracking().Where(g => g.CompanyID == DefaultCompanyId)
					 join c in _context.Customers.AsNoTracking() on g.CustomerId equals (int?)c.ID into cj from c in cj.DefaultIfEmpty()
					 join w in _context.Warehouses.AsNoTracking() on g.WarehouseId equals w.ID into wj from w in wj.DefaultIfEmpty()
					 select new DeliveryRow { Id = g.ID, DeliveryNo = g.DeliveryNo, DeliveryDate = g.DeliveryDate, PartyName = c != null ? c.Name : null, WarehouseCode = w != null ? w.Code : "", RefId = g.SalesOrderId, TotalCost = g.TotalCost };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0) { var pred = PredicateBuilder.AnyTerm<DeliveryRow>(terms, s => r => (r.DeliveryNo != null && r.DeliveryNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s)) || r.WarehouseCode.Contains(s)); if (pred != null) q0 = q0.Where(pred); }
			var rows = await q0.OrderByDescending(r => r.Id).ToListAsync();
			var headers = new[] { L["Note no."].Value, L["Date"].Value, L["Customer"].Value, L["Warehouse"].Value, L["Sales order"].Value, L["Total cost"].Value };
			var data = rows.Select(g => (IReadOnlyList<object?>)new object?[] { g.DeliveryNo, g.DeliveryDate, g.PartyName, g.WarehouseCode, g.RefId, g.TotalCost });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Deliveries"].Value, headers, data, L["Deliveries — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "deliveries.xlsx");
		}

		[HttpGet] public async Task<IActionResult> PriceListsExport(string? q, bool? active)
		{
			var (rows, _) = await _pricing.SearchAsync(DefaultCompanyId, q, active, 1, 100000);
			var headers = new[] { L["Code"].Value, L["Name"].Value, L["Segment"].Value, L["Priority"].Value, L["Line count"].Value, L["Valid from"].Value, L["Valid to"].Value, L["Status"].Value };
			var data = rows.Select(p => (IReadOnlyList<object?>)new object?[] { p.Code, p.Name, p.Segment, p.Priority, p.LineCount, p.ValidFrom, p.ValidTo, p.IsActive ? L["Active (list)"].Value : L["Inactive (list)"].Value });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Price lists"].Value, headers, data, L["Price lists — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "price-lists.xlsx");
		}

		[HttpGet] public async Task<IActionResult> ItemLedger(int itemId, int? warehouseId)
		{
			var item = await _items.GetItemAsync(DefaultCompanyId, itemId);
			if (item == null) { TempData["InvErr"] = L["Item not found"].Value; return RedirectToAction(nameof(StockBalances)); }
			ViewBag.Item = item;
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.FilterWarehouseId = warehouseId;
			decimal q, v, a;
			if (warehouseId.HasValue) { (q, v, a) = await _stock.GetBalanceAsync(DefaultCompanyId, itemId, warehouseId.Value); }
			else
			{
				var bals = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == DefaultCompanyId && b.ItemId == itemId).ToListAsync();
				q = bals.Sum(b => b.QtyOnHand); v = bals.Sum(b => b.TotalValue); a = q != 0 ? Math.Round(v / q, 2, MidpointRounding.AwayFromZero) : 0m;
			}
			ViewBag.BalQty = q; ViewBag.BalValue = v; ViewBag.BalAvg = a;
			return View(await _stock.GetMovementsAsync(DefaultCompanyId, itemId, warehouseId));
		}

		// JSON: look up an item by its barcode (item barcode OR a per-unit barcode) for the scan popup
		[HttpGet] public async Task<IActionResult> ScanBarcode(string barcode)
		{
			barcode = (barcode ?? "").Trim();
			if (barcode.Length == 0) return Json(new { ok = false });
			var c = DefaultCompanyId;
			int? matchedUom = null;
			var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.CompanyID == c && i.Barcode == barcode);
			if (item != null) matchedUom = item.BaseUoMId;
			if (item == null)
			{
				var bc = await _context.ItemBarcodes.AsNoTracking().FirstOrDefaultAsync(b => b.Barcode == barcode
					&& _context.Items.Any(i => i.ID == b.ItemId && i.CompanyID == c));
				if (bc != null) { item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == bc.ItemId); matchedUom = bc.UoMId ?? item?.BaseUoMId; }
			}
			if (item == null) return Json(new { ok = false, barcode });

			var units = await _items.GetUnitsAsync(c);
			string uName(int id) => units.FirstOrDefault(u => u.ID == id) is { } u ? (HttpContext.Items["Culture"]?.ToString() == "ar" ? u.Name : u.NameEn) : "";
			var convs = await _context.UoMConversions.AsNoTracking().Where(x => x.ItemId == item.ID).ToListAsync();
			var unitList = new List<object> { new { id = item.BaseUoMId, name = uName(item.BaseUoMId), factor = 1m, baseUnit = true } };
			foreach (var cv in convs) unitList.Add(new { id = cv.FromUoMId, name = uName(cv.FromUoMId), factor = cv.Factor, baseUnit = false });

			var comps = new List<object>();
			if (item.IsComposite)
			{
				var rows = await _items.GetItemComponentsAsync(item.ID);
				var ids = rows.Select(r => r.ComponentItemId).ToList();
				var citems = await _context.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).ToListAsync();
				foreach (var r in rows)
					comps.Add(new { name = citems.FirstOrDefault(x => x.ID == r.ComponentItemId)?.Name ?? "?", qty = r.Quantity, uom = uName(r.UoMId ?? 0) });
			}

			return Json(new
			{
				ok = true,
				id = item.ID, code = item.ItemCode, name = item.Name, nameEn = item.NameEn,
				image = string.IsNullOrWhiteSpace(item.ImagePath) ? null : Url.Content("~" + item.ImagePath!.Replace("\\", "/")),
				isComposite = item.IsComposite, compositeType = item.CompositeType, itemType = item.ItemType,
				salesPrice = item.SalesPrice, cost = item.OpeningCost,
				matchedUomId = matchedUom, units = unitList, components = comps
			});
		}

		// ---------------- Assembly / Disassembly ----------------
		// JSON: BOM of a composite item + current on-hand of each component in a warehouse
		[HttpGet] public async Task<IActionResult> ItemBom(int itemId, int? warehouseId)
		{
			var item = await _items.GetItemAsync(DefaultCompanyId, itemId);
			if (item == null) return Json(new { ok = false, error = L["Item not found"].Value });
			var comps = await _items.GetItemComponentsAsync(itemId);
			var itemIds = comps.Select(c => c.ComponentItemId).Distinct().ToList();
			var items = await _context.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).ToListAsync();
			var units = await _items.GetUnitsAsync(DefaultCompanyId);
			var rows = new List<object>();
			foreach (var c in comps)
			{
				var ci = items.FirstOrDefault(i => i.ID == c.ComponentItemId);
				decimal avail = 0;
				if (warehouseId != null) { var (q, _, _) = await _stock.GetBalanceAsync(DefaultCompanyId, c.ComponentItemId, warehouseId.Value); avail = q; }
				rows.Add(new
				{
					code = ci?.ItemCode,
					name = ci == null ? ("#" + c.ComponentItemId) : ci.Name,
					qtyPerUnit = c.Quantity,
					uom = units.FirstOrDefault(u => u.ID == (c.UoMId ?? ci?.BaseUoMId))?.Name,
					available = avail
				});
			}
			return Json(new { ok = true, type = item.CompositeType, isComposite = item.IsComposite, components = rows });
		}

		[HttpGet] public async Task<IActionResult> NewAssembly()
		{
			ViewBag.AssemblyItems = await _context.Items.AsNoTracking()
				.Where(i => i.CompanyID == DefaultCompanyId && i.IsActive && i.IsComposite && i.CompositeType == "Assembly")
				.OrderBy(i => i.ItemCode).ToListAsync();
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> PostAssembly(int assemblyItemId, int warehouseId, decimal qty, DateTime? date, bool disassemble)
		{
			var (ok, err, _) = await _stock.AssembleAsync(DefaultCompanyId, assemblyItemId, warehouseId, qty, date ?? DateTime.UtcNow, disassemble, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewAssembly)); }
			TempData["InvMsg"] = disassemble ? L["Disassembly posted; stock and journal entry updated"].Value : L["Assembly posted; stock and journal entry updated"].Value;
			return RedirectToAction(nameof(StockMovements));
		}

		// ================= Module 4: Manufacturing — Work Orders (4-1) =================
		// Stage 0 (Slice-003) — DEFENSE IN DEPTH, not a write-security fix.
		// The nine work-order WRITE actions below already carried [InvPerm("doc")] + [ValidateAntiForgeryToken];
		// an earlier discovery pass reported them as unguarded and that finding was WRONG (its attribute scanner
		// could not read several attributes concatenated on one line). What was genuinely missing was a READ gate
		// on the manufacturing screens, which expose work-order cost, WIP balance and component data.
		// [InvPerm("read")] routes through the module's own policy: InventoryAccessService grants "read" to any
		// authenticated user today, so this changes no behaviour — it makes the gate explicit and gives
		// manufacturing a single place to tighten when a real Manufacturing RBAC lands (Stage 1).
		[HttpGet][InvPerm("read")] public async Task<IActionResult> WorkOrders()
		{
			ViewBag.CanDoc = await _access.CanAsync("doc");   // gates the "New work order" button
			return View();
		}

		[HttpGet][InvPerm("read")] public async Task<IActionResult> WorkOrdersData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var (rows, total) = await _manuf.SearchAsync(DefaultCompanyId, q, status, page, pageSize);
			Response.Headers["X-Total"] = total.ToString(); Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = ((int)Math.Ceiling(total / (double)(pageSize <= 0 ? 25 : pageSize))).ToString();
			return PartialView("_WorkOrderRows", rows);
		}

		[HttpGet][InvPerm("read")] public async Task<IActionResult> WorkOrderItemPickData(string? term)
		{
			var rows = await _manuf.ManufacturableItemsAsync(DefaultCompanyId, term);
			return Json(new { results = rows.Select(r => new { id = r.id, text = r.text }) });
		}

		[HttpGet][InvPerm("read")] public async Task<IActionResult> NewWorkOrder()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			// UI gating must agree with the server: the create form is only usable with "doc".
			ViewBag.CanDoc = await _access.CanAsync("doc");
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> CreateWorkOrder(int itemId, decimal qty, int warehouseId, DateTime? plannedStart, DateTime? plannedEnd, decimal laborCost, decimal overheadCost, string? notes)
		{
			var (ok, err, id) = await _manuf.CreateAsync(DefaultCompanyId, itemId, qty, warehouseId, plannedStart, plannedEnd, laborCost, overheadCost, notes, User?.Identity?.Name);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewWorkOrder)); }
			TempData["InvMsg"] = L["Work order created"].Value;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		[HttpGet][InvPerm("read")] public async Task<IActionResult> WorkOrderDetails(int id)
		{
			var wo = await _manuf.GetAsync(DefaultCompanyId, id);
			if (wo == null) { TempData["InvErr"] = L["Work order not found"].Value; return RedirectToAction(nameof(WorkOrders)); }
			// Stage 0: every lifecycle button on this screen posts to an [InvPerm("doc")] action, so the buttons
			// are shown only when the same permission holds. The server remains the authority — hiding a button
			// is never the control.
			ViewBag.CanDoc = await _access.CanAsync("doc");
			ViewBag.Components = await _manuf.GetComponentsAsync(DefaultCompanyId, id);
			ViewBag.ItemName = await _context.Items.AsNoTracking().Where(i => i.ID == wo.ItemId).Select(i => i.ItemCode + " — " + i.Name).FirstOrDefaultAsync();
			ViewBag.WarehouseName = await _context.Warehouses.AsNoTracking().Where(w => w.ID == wo.WarehouseId).Select(w => w.Name).FirstOrDefaultAsync();
			var compIds = (ViewBag.Components as List<CrossBuy.Models.Context.Inventory.ManufWorkOrderComponent>)!.Select(c => c.ItemId).ToList();
			ViewBag.CompNames = await _context.Items.AsNoTracking().Where(i => compIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemCode + " — " + i.Name);
			// بند3: labor lines + pickers
			var labor = await _manuf.GetLaborAsync(DefaultCompanyId, id);
			ViewBag.Labor = labor;
			var empIds = labor.Where(l => l.EmployeeId != null).Select(l => l.EmployeeId!.Value).Distinct().ToList();
			ViewBag.LaborEmpNames = await _context.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID)).ToDictionaryAsync(e => e.ID, e => e.FullName);
			// NOTE: return full public entities (not anonymous types) — runtime-compiled Razor views cannot
			// access members of anonymous types declared in the controller assembly (RuntimeBinderException).
			ViewBag.Employees = await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == DefaultCompanyId && e.IsActive).OrderBy(e => e.FullName).ToListAsync();
			ViewBag.WhtCodes = await _context.TaxCodes.AsNoTracking().Where(t => t.CompanyID == DefaultCompanyId && t.Kind == "WHT" && t.IsActive).OrderBy(t => t.Name).ToListAsync();
			ViewBag.CashAccounts = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == DefaultCompanyId && (a.Code == "110101" || a.Code.StartsWith("2101")) && a.IsPostable).OrderBy(a => a.Code).ToListAsync();
			// MC (بند ب): currencies for external-labor foreign entry
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			return View(wo);
		}

		// ---- بند3: work-order labor ----
		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> AddWorkOrderLabor(int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal? ratePerHour, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate)
		{
			// picking a non-functional currency requires the currency-override permission (same rule as sales invoices)
			var functional = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			if (currencyId.HasValue && currencyId.Value != functional && !await _accAccess.CanAsync("currency-override"))
			{ TempData["InvErr"] = L["You do not have permission to use a foreign currency (currency-override)"].Value; return RedirectToAction(nameof(WorkOrderDetails), new { id = workOrderId }); }
			var (ok, err, _) = await _manuf.AddLaborAsync(DefaultCompanyId, workOrderId, sourceType, employeeId, workerName, hours, ratePerHour, whtCodeId, externalCreditAccountId, currencyId, exchangeRate, DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Labor charged to the work order (WIP)"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id = workOrderId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> RemoveWorkOrderLabor(int id, int workOrderId)
		{
			var (ok, err) = await _manuf.RemoveLaborAsync(DefaultCompanyId, id, DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Labor line removed and its entry reversed"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id = workOrderId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> SaveWorkOrder(int id, decimal qty, DateTime? plannedStart, DateTime? plannedEnd, decimal laborCost, decimal overheadCost, string? notes)
		{
			var (ok, err) = await _manuf.SaveHeaderAsync(DefaultCompanyId, id, qty, plannedStart, plannedEnd, laborCost, overheadCost, notes);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work order saved"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> ReleaseWorkOrder(int id, DateTime? date)
		{
			var (ok, err) = await _manuf.ReleaseAsync(DefaultCompanyId, id, date ?? DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work order released: materials issued to WIP"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> CancelWorkOrder(int id, DateTime? date)
		{
			var (ok, err) = await _manuf.CancelAsync(DefaultCompanyId, id, date ?? DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work order cancelled (issued materials returned if any)"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> CompleteWorkOrder(int id, DateTime? date)
		{
			var (ok, err, _) = await _manuf.CompleteAsync(DefaultCompanyId, id, date ?? DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work order completed: materials consumed, item produced, journal entry posted"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		// بند5: partial production at standard cost (Release first). finalize=true closes the order and books the variance to 520109.
		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> ProducePartial(int id, decimal qty, bool finalize, DateTime? date)
		{
			var (ok, err, produced) = await _manuf.ProducePartialAsync(DefaultCompanyId, id, qty, finalize, date ?? DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok
				? (finalize ? string.Format(L["Produced {0} and closed the order (variance posted to account 520109)"].Value, produced) : string.Format(L["Produced {0} partially (WIP carries the remainder)"].Value, produced))
				: err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		// ---- 4-2: work centers ----
		[HttpGet] public async Task<IActionResult> WorkCenters()
		{
			ViewBag.WorkCenters = await _manuf.GetWorkCentersAsync(DefaultCompanyId);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> SaveWorkCenter(int id, string? code, string name, decimal costPerHour, decimal overheadPerHour, bool isActive = true)
		{
			var (ok, err) = await _manuf.SaveWorkCenterAsync(DefaultCompanyId, new CrossBuy.Models.Context.Inventory.ManufWorkCenter
			{ ID = id, Code = code, Name = name, CostPerHour = costPerHour, OverheadPerHour = overheadPerHour, IsActive = isActive });
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work center saved"].Value : err;
			return RedirectToAction(nameof(WorkCenters));
		}

		// ---- 4-2: routing per item ----
		[HttpGet] public async Task<IActionResult> Routing(int itemId)
		{
			var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.CompanyID == DefaultCompanyId && i.ID == itemId);
			if (item == null) { TempData["InvErr"] = L["Item not found"].Value; return RedirectToAction(nameof(WorkOrders)); }
			ViewBag.Item = item;
			ViewBag.Ops = await _manuf.GetRoutingAsync(DefaultCompanyId, itemId);
			ViewBag.WorkCenters = await _manuf.WorkCentersForPickAsync(DefaultCompanyId);
			var (labor, overhead) = await _manuf.ComputeRoutingCostAsync(DefaultCompanyId, itemId, 1);
			ViewBag.UnitLabor = labor; ViewBag.UnitOverhead = overhead;
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> SaveRoutingOp(int id, int itemId, int seq, int workCenterId, string? operationName, decimal setupMins, decimal runMinsPerUnit)
		{
			var (ok, err) = await _manuf.SaveRoutingOpAsync(DefaultCompanyId, new CrossBuy.Models.Context.Inventory.ManufRoutingOp
			{ ID = id, ItemId = itemId, Seq = seq, WorkCenterId = workCenterId, OperationName = operationName, SetupMins = setupMins, RunMinsPerUnit = runMinsPerUnit });
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Operation saved"].Value : err;
			return RedirectToAction(nameof(Routing), new { itemId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> DeleteRoutingOp(int id, int itemId)
		{
			var (ok, err) = await _manuf.DeleteRoutingOpAsync(DefaultCompanyId, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Operation deleted"].Value : err;
			return RedirectToAction(nameof(Routing), new { itemId });
		}

		// ---- 4-3: production planning (MRP-lite) ----
		[HttpGet] public async Task<IActionResult> ProductionPlanning()
		{
			ViewBag.Plans = await _manuf.GetPlansAsync(DefaultCompanyId);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> CreatePlan(string name, DateTime? planDate)
		{
			var (ok, err, id) = await _manuf.CreatePlanAsync(DefaultCompanyId, name, planDate, User?.Identity?.Name);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(ProductionPlanning)); }
			return RedirectToAction(nameof(PlanDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> DeletePlan(int id)
		{
			var (ok, err) = await _manuf.DeletePlanAsync(DefaultCompanyId, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Plan deleted"].Value : err;
			return RedirectToAction(nameof(ProductionPlanning));
		}

		[HttpGet] public async Task<IActionResult> PlanDetails(int id)
		{
			var plan = await _manuf.GetPlanAsync(DefaultCompanyId, id);
			if (plan == null) { TempData["InvErr"] = L["Plan not found"].Value; return RedirectToAction(nameof(ProductionPlanning)); }
			ViewBag.Plan = plan;
			var demands = await _manuf.GetPlanDemandsAsync(DefaultCompanyId, id);
			ViewBag.Demands = demands;
			var demandIds = demands.Select(d => d.ItemId).ToList();
			ViewBag.DemandItemNames = await _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && demandIds.Contains(i.ID))
				.ToDictionaryAsync(i => i.ID, i => i.ItemCode + " — " + i.Name);
			ViewBag.Mrp = await _manuf.RunMrpAsync(DefaultCompanyId, id);
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> AddPlanDemand(int planId, int itemId, decimal qty, DateTime? dueDate)
		{
			var (ok, err) = await _manuf.AddDemandAsync(DefaultCompanyId, planId, itemId, qty, dueDate);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Demand added"].Value : err;
			return RedirectToAction(nameof(PlanDetails), new { id = planId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> RemovePlanDemand(int id, int planId)
		{
			var (ok, err) = await _manuf.RemoveDemandAsync(DefaultCompanyId, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Demand removed"].Value : err;
			return RedirectToAction(nameof(PlanDetails), new { id = planId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> GeneratePlanWorkOrders(int planId, int warehouseId)
		{
			var (ok, err, created) = await _manuf.GeneratePlanWorkOrdersAsync(DefaultCompanyId, planId, warehouseId, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Created {0} work orders from the plan"].Value, created) : err;
			return RedirectToAction(nameof(PlanDetails), new { id = planId });
		}

		// ---- 4-4: manufacturing reports ----
		[HttpGet] public async Task<IActionResult> ManufReports(DateTime? from, DateTime? to)
		{
			var f = from ?? DateTime.Today.AddMonths(-1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t;
			ViewBag.Report = await _manuf.GetManufReportAsync(DefaultCompanyId, f, t);
			return View();
		}

		// Manufacturing statistics dashboard (the system's landing screen)
		[HttpGet] public async Task<IActionResult> ManufDashboard()
		{
			var c = DefaultCompanyId;
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var wos = await _context.ManufWorkOrders.AsNoTracking().Where(w => w.CompanyID == c).ToListAsync();
			var itemIds = wos.Select(w => w.ItemId).Distinct().ToList();
			var itemName = await _context.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID))
				.Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn }).ToListAsync();
			var nameById = itemName.ToDictionary(i => i.ID, i => i.ItemCode + " — " + (!isAr && !string.IsNullOrWhiteSpace(i.NameEn) ? i.NameEn! : i.Name));

			decimal WoCost(CrossBuy.Models.Context.Inventory.ManufWorkOrder w) => w.MaterialCost + w.LaborCost + w.OverheadCost;
			bool IsActive(string s) => s == "Released" || s == "InProgress";

			var today = DateTime.Today;
			var monthStart = new DateTime(today.Year, today.Month, 1);
			var completed = wos.Where(w => w.Status == "Completed").ToList();

			var dto = new ManufDashboardDto
			{
				TotalWos = wos.Count,
				Draft = wos.Count(w => w.Status == "Draft"),
				Active = wos.Count(w => IsActive(w.Status)),
				Completed = completed.Count,
				Cancelled = wos.Count(w => w.Status == "Cancelled"),
				OpenWip = wos.Where(w => IsActive(w.Status)).Sum(w => w.WipBalance),
				TotalProducedValue = completed.Sum(WoCost),
				MonthCompleted = completed.Count(w => w.CompletedAt != null && w.CompletedAt >= monthStart),
				MonthProducedValue = completed.Where(w => w.CompletedAt != null && w.CompletedAt >= monthStart).Sum(WoCost),
			};

			// cost breakdown (all completed)
			dto.TotMaterial = completed.Sum(w => w.MaterialCost);
			dto.TotLabor = completed.Sum(w => w.LaborCost);
			dto.TotOverhead = completed.Sum(w => w.OverheadCost);

			// 6-month completed trend (count + produced value)
			var since = monthStart.AddMonths(-5);
			for (int i = 0; i < 6; i++)
			{
				var mo = since.AddMonths(i);
				var inMonth = completed.Where(w => w.CompletedAt != null && w.CompletedAt.Value.Year == mo.Year && w.CompletedAt.Value.Month == mo.Month).ToList();
				dto.Months.Add(new ManufMonthRow { Year = mo.Year, Month = mo.Month, Count = inMonth.Count, Value = inMonth.Sum(WoCost) });
			}

			// recent 10 work orders (any status)
			dto.Recent = wos.OrderByDescending(w => w.CreatedAt ?? DateTime.MinValue).ThenByDescending(w => w.ID).Take(10)
				.Select(w => new ManufWoLine
				{
					Id = w.ID, WoNo = w.WoNo, ItemName = nameById.TryGetValue(w.ItemId, out var n) ? n : ("#" + w.ItemId),
					Qty = w.Qty, ProducedQty = w.ProducedQty, Status = w.Status,
					When = w.CompletedAt ?? w.CreatedAt, TotalCost = WoCost(w)
				}).ToList();

			// top 5 manufactured items by produced value
			var topV = completed.GroupBy(w => w.ItemId).Select(g => new { id = g.Key, val = g.Sum(WoCost) })
				.OrderByDescending(x => x.val).Take(5).ToList();
			var maxV = topV.Count > 0 ? topV.Max(x => x.val) : 0m;
			dto.TopItems = topV.Select(x => new InvNameValue { Name = nameById.TryGetValue(x.id, out var n) ? n : ("#" + x.id), Value = x.val, Pct = maxV > 0 ? (int)Math.Round(x.val / maxV * 100) : 0 }).ToList();

			return View(dto);
		}

		// ================= Phase I3: Procurement (PO + Goods Receipt) =================
		private async Task PopulateProcurementListsAsync()
		{
			ViewBag.Vendors = await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == DefaultCompanyId).OrderBy(v => v.Name).ToListAsync();
			// line items searched on-demand via ItemPickData (no full-catalog preload)
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.Units = await _items.GetUnitsAsync(DefaultCompanyId);
		}

		[HttpGet] public IActionResult PurchaseOrders() => View();   // shell; rows via PurchaseOrdersData

		[HttpGet] public async Task<IActionResult> PurchaseOrdersData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var q0 = from p in _context.PurchaseOrders.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId)
					 join v in _context.Vendors.AsNoTracking() on p.VendorId equals v.ID into vj
					 from v in vj.DefaultIfEmpty()
					 select new PoRow { Id = p.ID, OrderNo = p.OrderNo, OrderDate = p.OrderDate, PartyName = v != null ? v.Name : null, PartyNameEn = v != null ? v.NameEn : null, GrandTotal = p.GrandTotal, Status = p.Status };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<PoRow>(terms, s => r => (r.OrderNo != null && r.OrderNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s)) || (r.PartyNameEn != null && r.PartyNameEn.Contains(s)));
				if (pred != null) q0 = q0.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) q0 = q0.Where(r => r.Status == status);
			var total = await q0.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await q0.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_PoRows", rows);
		}

		[HttpGet] public async Task<IActionResult> NewPurchaseOrder()
		{
			await PopulateProcurementListsAsync();
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("purchase")]
		public async Task<IActionResult> CreatePurchaseOrder(int vendorId, int? warehouseId, DateTime orderDate, DateTime? expectedDate, string? notes, string? linesJson, int? projectId)
		{
			List<PoLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<PoLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			// governance: PO above threshold needs approval before it's created
			var poTotal = lines.Sum(l => l.Qty * l.UnitPrice - l.DiscountAmount + (l.Qty * l.UnitPrice - l.DiscountAmount) * l.TaxRate / 100m);
			if (await _approvals.RequiresApprovalAsync(poTotal))
			{
				await _approvals.SubmitAsync("PurchaseOrder", poTotal, new PoApprovalPayload { VendorId = vendorId, WarehouseId = warehouseId, OrderDate = orderDate, ExpectedDate = expectedDate, Notes = notes, Lines = lines }, _access.CurrentEmployeeId());
				TempData["InvMsg"] = L["Purchase order exceeds the approval limit — sent for approval"].Value;
				return RedirectToAction(nameof(Approvals));
			}
			var (ok, err, _) = await _proc.CreatePurchaseOrderAsync(DefaultCompanyId, vendorId, warehouseId, orderDate, expectedDate, notes, lines, null, projectId);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewPurchaseOrder)); }
			TempData["InvMsg"] = L["Purchase order created"].Value;
			return RedirectToAction(nameof(PurchaseOrders));
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("purchase")]
		public async Task<IActionResult> ConvertPoToInvoice(int id)
		{
			var (ok, err, invId) = await _proc.ConvertToInvoiceAsync(DefaultCompanyId, id, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Purchase order converted to a purchase invoice (#{0})"].Value, invId) : err;
			return RedirectToAction(nameof(PurchaseOrders));
		}

		[HttpGet] public IActionResult GoodsReceipts() => View();   // shell; rows via GoodsReceiptsData

		[HttpGet] public async Task<IActionResult> GoodsReceiptsData(string? q, int page = 1, int pageSize = 25)
		{
			var q0 = from g in _context.GoodsReceipts.AsNoTracking().Where(g => g.CompanyID == DefaultCompanyId)
					 join v in _context.Vendors.AsNoTracking() on g.VendorId equals (int?)v.ID into vj
					 from v in vj.DefaultIfEmpty()
					 join w in _context.Warehouses.AsNoTracking() on g.WarehouseId equals w.ID into wj
					 from w in wj.DefaultIfEmpty()
					 select new GrRow { Id = g.ID, ReceiptNo = g.ReceiptNo, ReceiptDate = g.ReceiptDate, PartyName = v != null ? v.Name : null, PartyNameEn = v != null ? v.NameEn : null, WarehouseCode = w != null ? w.Code : "", RefId = g.PurchaseOrderId, TotalCost = g.TotalCost };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<GrRow>(terms, s => r => (r.ReceiptNo != null && r.ReceiptNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s)) || (r.PartyNameEn != null && r.PartyNameEn.Contains(s)) || r.WarehouseCode.Contains(s));
				if (pred != null) q0 = q0.Where(pred);
			}
			var total = await q0.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await q0.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_GrRows", rows);
		}

		[HttpGet] public async Task<IActionResult> NewGoodsReceipt(int? poId)
		{
			await PopulateProcurementListsAsync();
			ViewBag.PurchaseOrders = await _proc.GetPurchaseOrdersAsync(DefaultCompanyId);
			if (poId != null) ViewBag.FromPO = await _proc.GetPurchaseOrderAsync(DefaultCompanyId, poId.Value);
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("purchase")]
		public async Task<IActionResult> CreateGoodsReceipt(int? vendorId, int warehouseId, int? poId, DateTime receiptDate, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate)
		{
			List<ReceiptLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<ReceiptLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			// currency-override guard (accounting RBAC): a foreign-currency receipt needs the permission
			var functional = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			if (currencyId.HasValue && currencyId.Value != functional && !await _accAccess.CanAsync("currency-override"))
			{ TempData["InvErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(NewGoodsReceipt), new { poId }); }
			var (ok, err, _) = await _proc.CreateReceiptAsync(DefaultCompanyId, vendorId, warehouseId, poId, receiptDate, notes, lines, null, currencyId, exchangeRate);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewGoodsReceipt), new { poId }); }
			TempData["InvMsg"] = L["Goods receipt posted; stock updated"].Value;
			return RedirectToAction(nameof(GoodsReceipts));
		}

		// ================= Phase I4: Sales (Sales Order + Delivery) =================
		private async Task PopulateSellingListsAsync()
		{
			ViewBag.Customers = await _context.Customers.AsNoTracking().Where(v => v.CompanyID == DefaultCompanyId).OrderBy(v => v.Name).ToListAsync();
			// line items searched on-demand via ItemPickData (no full-catalog preload)
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.Units = await _items.GetUnitsAsync(DefaultCompanyId);
		}

		[HttpGet] public IActionResult SalesOrders() => View();   // shell; rows via SalesOrdersData

		[HttpGet] public async Task<IActionResult> SalesOrdersData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var q0 = from p in _context.SalesOrders.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId)
					 join c in _context.Customers.AsNoTracking() on p.CustomerId equals c.ID into cj
					 from c in cj.DefaultIfEmpty()
					 select new SoRow { Id = p.ID, OrderNo = p.OrderNo, OrderDate = p.OrderDate, PartyName = c != null ? c.Name : null, PartyNameEn = c != null ? c.NameEn : null, GrandTotal = p.GrandTotal, Status = p.Status };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<SoRow>(terms, s => r => (r.OrderNo != null && r.OrderNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s)) || (r.PartyNameEn != null && r.PartyNameEn.Contains(s)));
				if (pred != null) q0 = q0.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) q0 = q0.Where(r => r.Status == status);
			var total = await q0.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await q0.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_SoRows", rows);
		}

		[HttpGet] public async Task<IActionResult> NewSalesOrder()
		{
			await PopulateSellingListsAsync();
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CreateSalesOrder(int customerId, int? warehouseId, DateTime orderDate, DateTime? expectedDate, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate, int? projectId)
		{
			List<SoLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<SoLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var functional = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			if (currencyId.HasValue && currencyId.Value != functional && !await _accAccess.CanAsync("currency-override"))
			{ TempData["InvErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(NewSalesOrder)); }
			// Pricing 2A — gross-margin floor: Block rejects the doc; Warn proceeds and notifies.
			var (mBlock, mWarn) = await CheckLineMarginsAsync(lines.Select(l => (l.ItemId, l.Qty, l.UnitPrice, l.DiscountAmount)), currencyId, exchangeRate, orderDate);
			if (mBlock != null) { TempData["InvErr"] = mBlock; return RedirectToAction(nameof(NewSalesOrder)); }
			// Pricing 2D — discount approval ceiling.
			var (dBlock, dWarn) = await _pricing.EvaluateLineDiscountsAsync(DefaultCompanyId, lines.Select(l => (l.Qty, l.UnitPrice, l.DiscountAmount)), await _access.CanAsync("manage"));
			if (dBlock != null) { TempData["InvErr"] = dBlock; return RedirectToAction(nameof(NewSalesOrder)); }
			var (ok, err, _) = await _sell.CreateSalesOrderAsync(DefaultCompanyId, customerId, warehouseId, orderDate, expectedDate, notes, lines, null, currencyId, exchangeRate, projectId);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewSalesOrder)); }
			var okMsg = string.Join(" · ", new[] { mWarn, dWarn }.Where(m => m != null));
			TempData[okMsg.Length > 0 ? "InvWarn" : "InvMsg"] = okMsg.Length > 0 ? okMsg : L["Sales order created"].Value;
			return RedirectToAction(nameof(SalesOrders));
		}

		// Pricing 2A — evaluate the gross-margin floor for each stock line. Returns (blockMsg, warnMsg):
		// blockMsg != null → any line violated in Block mode (caller must reject); warnMsg != null → Warn-mode violations to surface.
		private async Task<(string? block, string? warn)> CheckLineMarginsAsync(
			IEnumerable<(int? ItemId, decimal Qty, decimal UnitPrice, decimal DiscountAmount)> lines,
			int? currencyId, decimal? exchangeRate, DateTime asOf)
		{
			var warns = new List<string>();
			foreach (var l in lines)
			{
				if (!l.ItemId.HasValue || l.ItemId.Value <= 0) continue;
				decimal net = l.Qty > 0 ? (l.UnitPrice - l.DiscountAmount / l.Qty) : l.UnitPrice;
				var mc = await _pricing.CheckMarginAsync(DefaultCompanyId, l.ItemId.Value, net, currencyId, exchangeRate, asOf);
				if (mc.Mode == "Off" || mc.Ok || mc.Skipped) continue;
				string msg = string.Format(L["«{0}»: price {1:N2} is below the minimum {2:N2} (cost {3:N2} + margin {4:N2}%)"].Value, mc.ItemName, mc.PriceFunctional, mc.FloorFunctional, mc.CostFunctional, mc.MarginPct);
				if (mc.Mode == "Block") return (L["Minimum profit floor block — "].Value + msg, null);
				warns.Add(msg);
			}
			return (null, warns.Count > 0 ? L["Minimum profit floor warning — "].Value + string.Join(" · ", warns) : null);
		}

		// ---------------- Quotations (P3-2) ----------------
		[HttpGet] public IActionResult Quotations() => View();   // shell; rows via QuotationsData

		[HttpGet] public async Task<IActionResult> QuotationsData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var q0 = from p in _context.Quotations.AsNoTracking().Where(p => p.CompanyID == DefaultCompanyId)
					 join c in _context.Customers.AsNoTracking() on p.CustomerId equals c.ID into cj
					 from c in cj.DefaultIfEmpty()
					 select new QuoteRow { Id = p.ID, QuoteNo = p.QuoteNo, QuoteDate = p.QuoteDate, ValidUntil = p.ValidUntil, PartyName = c != null ? c.Name : null, PartyNameEn = c != null ? c.NameEn : null, GrandTotal = p.GrandTotal, Status = p.Status };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<QuoteRow>(terms, s => r => (r.QuoteNo != null && r.QuoteNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s)) || (r.PartyNameEn != null && r.PartyNameEn.Contains(s)));
				if (pred != null) q0 = q0.Where(pred);
			}
			if (!string.IsNullOrWhiteSpace(status)) q0 = q0.Where(r => r.Status == status);
			var total = await q0.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await q0.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_QuoteRows", rows);
		}

		[HttpGet] public async Task<IActionResult> NewQuotation()
		{
			await PopulateSellingListsAsync();
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CreateQuotation(int customerId, int? warehouseId, DateTime quoteDate, DateTime? validUntil, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate)
		{
			List<SoLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<SoLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var functional = await _currency.GetFunctionalCurrencyIdAsync(DefaultCompanyId, null);
			if (currencyId.HasValue && currencyId.Value != functional && !await _accAccess.CanAsync("currency-override"))
			{ TempData["InvErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(NewQuotation)); }
			var (ok, err, _) = await _sell.CreateQuotationAsync(DefaultCompanyId, customerId, warehouseId, quoteDate, validUntil, notes, lines, null, currencyId, exchangeRate);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewQuotation)); }
			TempData["InvMsg"] = L["Quotation created"].Value;
			return RedirectToAction(nameof(Quotations));
		}

		[HttpGet] public async Task<IActionResult> QuotationDetails(int id)
		{
			var q = await _sell.GetQuotationAsync(DefaultCompanyId, id);
			if (q == null) { TempData["InvErr"] = L["Quotation not found"].Value; return RedirectToAction(nameof(Quotations)); }
			ViewBag.Customer = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.ID == q.CustomerId);
			var itemIds = q.Lines.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).ToList();
			ViewBag.Items = await _context.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemCode);
			return View(q);
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> SetQuoteStatus(int id, string status)
		{
			var (ok, err) = await _sell.SetQuotationStatusAsync(DefaultCompanyId, id, status);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Quotation status updated"].Value : err;
			return RedirectToAction(nameof(QuotationDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> ConvertQuoteToOrder(int id)
		{
			var (ok, err, soId) = await _sell.ConvertQuotationToOrderAsync(DefaultCompanyId, id, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(QuotationDetails), new { id }); }
			TempData["InvMsg"] = L["Quotation converted to a sales order"].Value;
			return RedirectToAction(nameof(SalesOrderDetails), new { id = soId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> ConvertSoToInvoice(int id)
		{
			var (ok, err, invId) = await _sell.ConvertToInvoiceAsync(DefaultCompanyId, id, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Sales order converted to a sales invoice (#{0})"].Value, invId) : err;
			return RedirectToAction(nameof(SalesOrders));
		}

		[HttpGet] public IActionResult Deliveries() => View();   // shell; rows via DeliveriesData

		[HttpGet] public async Task<IActionResult> DeliveriesData(string? q, int page = 1, int pageSize = 25)
		{
			var q0 = from g in _context.DeliveryNotes.AsNoTracking().Where(g => g.CompanyID == DefaultCompanyId)
					 join c in _context.Customers.AsNoTracking() on g.CustomerId equals (int?)c.ID into cj
					 from c in cj.DefaultIfEmpty()
					 join w in _context.Warehouses.AsNoTracking() on g.WarehouseId equals w.ID into wj
					 from w in wj.DefaultIfEmpty()
					 select new DeliveryRow { Id = g.ID, DeliveryNo = g.DeliveryNo, DeliveryDate = g.DeliveryDate, PartyName = c != null ? c.Name : null, PartyNameEn = c != null ? c.NameEn : null, WarehouseCode = w != null ? w.Code : "", RefId = g.SalesOrderId, TotalCost = g.TotalCost };
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<DeliveryRow>(terms, s => r => (r.DeliveryNo != null && r.DeliveryNo.Contains(s)) || (r.PartyName != null && r.PartyName.Contains(s)) || (r.PartyNameEn != null && r.PartyNameEn.Contains(s)) || r.WarehouseCode.Contains(s));
				if (pred != null) q0 = q0.Where(pred);
			}
			var total = await q0.CountAsync();
			if (page < 1) page = 1; if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var rows = await q0.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			SetPaging(total, page, pageSize);
			return PartialView("_DeliveryRows", rows);
		}

		[HttpGet] public async Task<IActionResult> NewDelivery(int? soId)
		{
			await PopulateSellingListsAsync();
			if (soId != null) ViewBag.FromSO = await _sell.GetSalesOrderAsync(DefaultCompanyId, soId.Value);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CreateDelivery(int? customerId, int warehouseId, int? soId, DateTime deliveryDate, string? notes, string? linesJson)
		{
			List<DeliveryLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<DeliveryLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var (ok, err, _) = await _sell.CreateDeliveryAsync(DefaultCompanyId, customerId, warehouseId, soId, deliveryDate, notes, lines, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewDelivery), new { soId }); }
			TempData["InvMsg"] = L["Delivery note posted; stock deducted and cost recorded"].Value;
			return RedirectToAction(nameof(Deliveries));
		}

		// ================= Phase I6: Stock transfers =================
		[HttpGet] public async Task<IActionResult> StockTransfers()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			return View(await _context.StockTransfers.AsNoTracking().Where(t => t.CompanyID == DefaultCompanyId).OrderByDescending(t => t.ID).ToListAsync());
		}

		[HttpGet] public async Task<IActionResult> NewTransfer()
		{
			// line items searched on-demand via ItemPickData (no full-catalog preload)
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.Units = await _items.GetUnitsAsync(DefaultCompanyId);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CreateTransfer(int fromWarehouseId, int toWarehouseId, DateTime transferDate, string? notes, string? linesJson)
		{
			List<TransferLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<TransferLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			// governance: estimate value (qty × avg cost at source) — above threshold needs approval
			decimal trEst = 0;
			foreach (var l in lines) { var (_, _, avg) = await _stock.GetBalanceAsync(DefaultCompanyId, l.ItemId, fromWarehouseId); trEst += l.Qty * avg; }
			if (await _approvals.RequiresApprovalAsync(trEst))
			{
				await _approvals.SubmitAsync("StockTransfer", trEst, new TransferApprovalPayload { FromWarehouseId = fromWarehouseId, ToWarehouseId = toWarehouseId, Date = transferDate, Notes = notes, Lines = lines }, _access.CurrentEmployeeId());
				TempData["InvMsg"] = L["Transfer exceeds the approval limit — sent for approval"].Value;
				return RedirectToAction(nameof(Approvals));
			}
			var (ok, err, _) = await _stock.TransferAsync(DefaultCompanyId, fromWarehouseId, toWarehouseId, transferDate, notes, lines, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewTransfer)); }
			TempData["InvMsg"] = L["Inter-warehouse transfer posted"].Value;
			return RedirectToAction(nameof(StockTransfers));
		}

		// ================= Phase I7: Stock count =================
		[HttpGet] public async Task<IActionResult> StockCounts()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			return View(await _context.StockCounts.AsNoTracking().Where(t => t.CompanyID == DefaultCompanyId).OrderByDescending(t => t.ID).ToListAsync());
		}

		[HttpGet] public async Task<IActionResult> NewCount(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				// load current book balances for the warehouse + only the items actually in this warehouse (not the whole catalog)
				var balances = await _stock.GetBalancesAsync(DefaultCompanyId, warehouseId);
				ViewBag.BookBalances = balances.Select(b => new InvBookBalanceRow { ItemId = b.ItemId, QtyOnHand = b.QtyOnHand, AvgCost = b.AvgCost }).ToList();
				var ids = balances.Select(b => b.ItemId).Distinct().ToList();
				var itemsInWh = await _context.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).OrderBy(i => i.ItemCode).ToListAsync();
				ViewBag.Items = itemsInWh;
				// HM-7: per-batch on-hand for expiry-tracked items in this warehouse, so the count auto-decomposes them into
				// one row per batch (batch qty = Σ Direction×QtyBase over its movements — same basis as FEFO/BatchOnHand).
				var trackedIds = itemsInWh.Where(i => i.TrackExpiry).Select(i => i.ID).ToList();
				var batchBal = new List<object>();
				if (trackedIds.Count > 0 && warehouseId is int wid)
					batchBal = (await (from m in _context.StockMovements.AsNoTracking()
									   join b in _context.StockBatches.AsNoTracking() on m.BatchId equals b.ID
									   where m.CompanyID == DefaultCompanyId && m.WarehouseId == wid && trackedIds.Contains(m.ItemId)
									   group new { m, b } by new { m.ItemId, b.BatchNo, b.ExpiryDate } into g
									   select new { g.Key.ItemId, g.Key.BatchNo, g.Key.ExpiryDate, Qty = g.Sum(x => x.m.Direction * x.m.QtyBase) })
								   .ToListAsync()).Where(x => x.Qty != 0m).Cast<object>().ToList();
				ViewBag.BatchBalances = batchBal;
			}
			else { ViewBag.Items = new List<Item>(); ViewBag.BatchBalances = new List<object>(); }
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> PostCount(int warehouseId, DateTime countDate, string? notes, string? linesJson)
		{
			List<CountLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<CountLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			// governance: estimate adjustment value (|counted-book| × avg cost) — above threshold needs approval
			decimal cntEst = 0;
			foreach (var l in lines) { var (bookQ, _, avg) = await _stock.GetBalanceAsync(DefaultCompanyId, l.ItemId, warehouseId); cntEst += Math.Abs(l.CountedQty - bookQ) * avg; }
			if (await _approvals.RequiresApprovalAsync(cntEst))
			{
				await _approvals.SubmitAsync("StockCount", cntEst, new CountApprovalPayload { WarehouseId = warehouseId, CountDate = countDate, Notes = notes, Lines = lines }, _access.CurrentEmployeeId());
				TempData["InvMsg"] = L["Stock count adjustment exceeds the approval limit — sent for approval"].Value;
				return RedirectToAction(nameof(Approvals));
			}
			var (ok, err, _) = await _stock.PostCountAsync(DefaultCompanyId, warehouseId, countDate, notes, lines, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewCount), new { warehouseId }); }
			TempData["InvMsg"] = L["Stock count posted; differences adjusted"].Value;
			return RedirectToAction(nameof(StockCounts));
		}

		// ================= Write-off / damage =================
		[HttpGet] public async Task<IActionResult> WriteOffs()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.Mode = await _context.InventorySettings.AsNoTracking().Where(x => x.CompanyID == DefaultCompanyId).Select(x => x.WriteOffMode).FirstOrDefaultAsync() ?? "SeparateDocument";
			return View(await _context.StockWriteOffs.AsNoTracking().Where(t => t.CompanyID == DefaultCompanyId).OrderByDescending(t => t.ID).ToListAsync());
		}

		[HttpGet] public async Task<IActionResult> NewWriteOff(int? warehouseId)
		{
			ViewBag.Mode = await _context.InventorySettings.AsNoTracking().Where(x => x.CompanyID == DefaultCompanyId).Select(x => x.WriteOffMode).FirstOrDefaultAsync() ?? "SeparateDocument";
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				var balances = await _stock.GetBalancesAsync(DefaultCompanyId, warehouseId);
				ViewBag.BookBalances = balances.Select(b => new InvBookBalanceRow { ItemId = b.ItemId, QtyOnHand = b.QtyOnHand, AvgCost = b.AvgCost }).ToList();
				var ids = balances.Select(b => b.ItemId).Distinct().ToList();
				ViewBag.Items = await _context.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).OrderBy(i => i.ItemCode).ToListAsync();
			}
			else { ViewBag.Items = new List<Item>(); }
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CreateWriteOff(int warehouseId, DateTime writeOffDate, string? reason, string? notes, string? linesJson)
		{
			List<WriteOffLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<WriteOffLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			lines = lines.Where(l => l.ItemId > 0 && l.Qty > 0).ToList();
			if (lines.Count == 0) { TempData["InvErr"] = L["Add at least one line"].Value; return RedirectToAction(nameof(NewWriteOff), new { warehouseId }); }

			// governance: estimate value (qty × avg cost) — above threshold needs approval (SoD applies on approve)
			decimal est = 0;
			foreach (var l in lines) { var (_, _, avg) = await _stock.GetBalanceAsync(DefaultCompanyId, l.ItemId, warehouseId); est += l.Qty * avg; }
			if (await _approvals.RequiresApprovalAsync(est))
			{
				await _approvals.SubmitAsync("WriteOff", est, new WriteOffApprovalPayload { WarehouseId = warehouseId, WriteOffDate = writeOffDate, Reason = reason, Notes = notes, Lines = lines }, _access.CurrentEmployeeId());
				TempData["InvMsg"] = L["Write-off exceeds the approval limit — sent for approval"].Value;
				return RedirectToAction(nameof(Approvals));
			}
			var (ok, err, docNo, _, mode) = await _stock.WriteOffAsync(DefaultCompanyId, warehouseId, writeOffDate, reason, notes, lines, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewWriteOff), new { warehouseId }); }
			TempData["InvMsg"] = mode == "AdjustmentReason" ? string.Format(L["Write-off recorded as an adjustment ({0})"].Value, docNo) : string.Format(L["Write-off document {0} posted"].Value, docNo);
			return RedirectToAction(mode == "AdjustmentReason" ? nameof(StockCounts) : nameof(WriteOffs));
		}

		// ================= Document details (read-only drill-down) =================
		private bool Ar() => HttpContext.Items["Culture"]?.ToString() == "ar";
		private static string N2(decimal d) => d.ToString("N2");
		private static string Q(decimal d) => d.ToString("0.####");
		private static string Dt(DateTime d) => d.ToString("yyyy-MM-dd");
		private static string DtN(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "—";
		private string ReasonName(string? r) => r switch { "Damaged" => Ar() ? "تالف" : "Damaged", "Expired" => Ar() ? "منتهي الصلاحية" : "Expired", "Lost" => Ar() ? "فاقد" : "Lost", "Other" => Ar() ? "أخرى" : "Other", "WriteOff" => Ar() ? "إعدام" : "Write-off", _ => string.IsNullOrEmpty(r) ? "—" : r! };
		private async Task<Dictionary<int, string>> ItemNamesAsync(IEnumerable<int> ids)
		{
			var list = ids.Distinct().ToList(); bool ar = Ar();
			return await _context.Items.AsNoTracking().Where(i => list.Contains(i.ID))
				.ToDictionaryAsync(i => i.ID, i => i.ItemCode + " — " + (ar ? i.Name : (string.IsNullOrEmpty(i.NameEn) ? i.Name : i.NameEn)));
		}
		private async Task<Dictionary<int, string>> WhNamesAsync()
		{
			bool ar = Ar();
			return (await _warehouses.GetWarehousesAsync(DefaultCompanyId)).ToDictionary(w => w.ID, w => w.Code + " — " + (ar ? w.Name : (string.IsNullOrEmpty(w.NameEn) ? w.Name : w.NameEn)));
		}

		[HttpGet] public async Task<IActionResult> ReceiptDetails(int id)
		{
			var d = await _proc.GetReceiptAsync(DefaultCompanyId, id);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(GoodsReceipts)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var vendor = d.VendorId == null ? "—" : await _context.Vendors.AsNoTracking().Where(v => v.ID == d.VendorId).Select(v => Ar() ? v.Name : (v.NameEn ?? v.Name)).FirstOrDefaultAsync() ?? "—";
			var vm = new DocDetailVm { Title = "إذن استلام", TitleEn = "Goods receipt", DocNo = d.ReceiptNo ?? ("#" + d.ID), DateStr = Dt(d.ReceiptDate), Status = d.Status, BackAction = nameof(GoodsReceipts), BackLabel = "أذون الاستلام", BackLabelEn = "Goods receipts" };
			vm.Header.Add(new() { Label = "المخزن", LabelEn = "Warehouse", Value = wh.GetValueOrDefault(d.WarehouseId, "—") });
			vm.Header.Add(new() { Label = "المورد", LabelEn = "Vendor", Value = vendor });
			vm.Header.Add(new() { Label = "ملاحظات", LabelEn = "Notes", Value = d.Notes ?? "—" });
			vm.Columns = new() { new() { Label = "الصنف", LabelEn = "Item" }, new() { Label = "الكمية", LabelEn = "Qty", Num = true }, new() { Label = "التكلفة", LabelEn = "Unit cost", Num = true }, new() { Label = "الإجمالي", LabelEn = "Total", Num = true }, new() { Label = "الدفعة", LabelEn = "Batch" }, new() { Label = "الصلاحية", LabelEn = "Expiry" }, new() { Label = "السيريال", LabelEn = "Serial" } };
			foreach (var l in d.Lines.OrderBy(x => x.LineNo))
				vm.Rows.Add(new() { names.GetValueOrDefault(l.ItemId, "#" + l.ItemId), Q(l.Qty), N2(l.UnitCost), N2(l.LineTotal), l.BatchNo ?? "—", DtN(l.ExpiryDate), l.SerialNo ?? "—" });
			vm.Totals.Add(new() { Label = "إجمالي التكلفة", LabelEn = "Total cost", Value = N2(d.TotalCost) });
			return View("DocumentDetails", vm);
		}

		[HttpGet] public async Task<IActionResult> DeliveryDetails(int id)
		{
			var d = await _sell.GetDeliveryAsync(DefaultCompanyId, id);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(Deliveries)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var cust = d.CustomerId == null ? "—" : await _context.Customers.AsNoTracking().Where(v => v.ID == d.CustomerId).Select(v => Ar() ? v.Name : (v.NameEn ?? v.Name)).FirstOrDefaultAsync() ?? "—";
			var vm = new DocDetailVm { Title = "إذن صرف", TitleEn = "Delivery note", DocNo = d.DeliveryNo ?? ("#" + d.ID), DateStr = Dt(d.DeliveryDate), Status = d.Status, BackAction = nameof(Deliveries), BackLabel = "أذون الصرف", BackLabelEn = "Deliveries" };
			vm.Header.Add(new() { Label = "المخزن", LabelEn = "Warehouse", Value = wh.GetValueOrDefault(d.WarehouseId, "—") });
			vm.Header.Add(new() { Label = "العميل", LabelEn = "Customer", Value = cust });
			vm.Header.Add(new() { Label = "ملاحظات", LabelEn = "Notes", Value = d.Notes ?? "—" });
			vm.Columns = new() { new() { Label = "الصنف", LabelEn = "Item" }, new() { Label = "الكمية", LabelEn = "Qty", Num = true }, new() { Label = "التكلفة", LabelEn = "Unit cost", Num = true }, new() { Label = "الإجمالي", LabelEn = "Total", Num = true }, new() { Label = "الدفعة", LabelEn = "Batch" }, new() { Label = "السيريال", LabelEn = "Serial" } };
			foreach (var l in d.Lines.OrderBy(x => x.LineNo))
				vm.Rows.Add(new() { names.GetValueOrDefault(l.ItemId, "#" + l.ItemId), Q(l.Qty), N2(l.UnitCost), N2(l.LineTotal), l.BatchNo ?? "—", l.SerialNo ?? "—" });
			vm.Totals.Add(new() { Label = "إجمالي التكلفة", LabelEn = "Total cost", Value = N2(d.TotalCost) });
			return View("DocumentDetails", vm);
		}

		[HttpGet] public async Task<IActionResult> PurchaseOrderDetails(int id)
		{
			var d = await _proc.GetPurchaseOrderAsync(DefaultCompanyId, id);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(PurchaseOrders)); }
			var names = await ItemNamesAsync(d.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value)); var wh = await WhNamesAsync();
			var vendor = await _context.Vendors.AsNoTracking().Where(v => v.ID == d.VendorId).Select(v => Ar() ? v.Name : (v.NameEn ?? v.Name)).FirstOrDefaultAsync() ?? "—";
			var vm = new DocDetailVm { Title = "أمر شراء", TitleEn = "Purchase order", DocNo = d.OrderNo ?? ("#" + d.ID), DateStr = Dt(d.OrderDate), Status = d.Status, BackAction = nameof(PurchaseOrders), BackLabel = "أوامر الشراء", BackLabelEn = "Purchase orders" };
			vm.Header.Add(new() { Label = "المورد", LabelEn = "Vendor", Value = vendor });
			vm.Header.Add(new() { Label = "المخزن", LabelEn = "Warehouse", Value = d.WarehouseId == null ? "—" : wh.GetValueOrDefault(d.WarehouseId.Value, "—") });
			vm.Header.Add(new() { Label = "تاريخ التوريد", LabelEn = "Expected", Value = DtN(d.ExpectedDate) });
			vm.Header.Add(new() { Label = "ملاحظات", LabelEn = "Notes", Value = d.Notes ?? "—" });
			vm.Columns = new() { new() { Label = "الصنف/البيان", LabelEn = "Item/Desc" }, new() { Label = "الكمية", LabelEn = "Qty", Num = true }, new() { Label = "السعر", LabelEn = "Unit price", Num = true }, new() { Label = "الخصم", LabelEn = "Disc.", Num = true }, new() { Label = "الضريبة %", LabelEn = "Tax %", Num = true }, new() { Label = "الإجمالي", LabelEn = "Total", Num = true }, new() { Label = "المستلَم", LabelEn = "Received", Num = true } };
			foreach (var l in d.Lines.OrderBy(x => x.LineNo))
				vm.Rows.Add(new() { l.ItemId != null ? names.GetValueOrDefault(l.ItemId.Value, l.ItemDescription ?? ("#" + l.ItemId)) : (l.ItemDescription ?? "—"), Q(l.Qty), N2(l.UnitPrice), N2(l.DiscountAmount), Q(l.TaxRate), N2(l.LineTotal), Q(l.ReceivedQty) });
			vm.Totals.Add(new() { Label = "الإجمالي قبل الضريبة", LabelEn = "Subtotal", Value = N2(d.SubTotal) });
			vm.Totals.Add(new() { Label = "الضريبة", LabelEn = "Tax", Value = N2(d.TaxTotal) });
			vm.Totals.Add(new() { Label = "الإجمالي", LabelEn = "Grand total", Value = N2(d.GrandTotal) });
			return View("DocumentDetails", vm);
		}

		[HttpGet] public async Task<IActionResult> SalesOrderDetails(int id)
		{
			var d = await _sell.GetSalesOrderAsync(DefaultCompanyId, id);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(SalesOrders)); }
			var names = await ItemNamesAsync(d.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value)); var wh = await WhNamesAsync();
			var cust = await _context.Customers.AsNoTracking().Where(v => v.ID == d.CustomerId).Select(v => Ar() ? v.Name : (v.NameEn ?? v.Name)).FirstOrDefaultAsync() ?? "—";
			var vm = new DocDetailVm { Title = "أمر بيع", TitleEn = "Sales order", DocNo = d.OrderNo ?? ("#" + d.ID), DateStr = Dt(d.OrderDate), Status = d.Status, BackAction = nameof(SalesOrders), BackLabel = "أوامر البيع", BackLabelEn = "Sales orders" };
			vm.Header.Add(new() { Label = "العميل", LabelEn = "Customer", Value = cust });
			vm.Header.Add(new() { Label = "المخزن", LabelEn = "Warehouse", Value = d.WarehouseId == null ? "—" : wh.GetValueOrDefault(d.WarehouseId.Value, "—") });
			vm.Header.Add(new() { Label = "تاريخ التسليم", LabelEn = "Expected", Value = DtN(d.ExpectedDate) });
			vm.Header.Add(new() { Label = "ملاحظات", LabelEn = "Notes", Value = d.Notes ?? "—" });
			vm.Columns = new() { new() { Label = "الصنف/البيان", LabelEn = "Item/Desc" }, new() { Label = "الكمية", LabelEn = "Qty", Num = true }, new() { Label = "السعر", LabelEn = "Unit price", Num = true }, new() { Label = "الخصم", LabelEn = "Disc.", Num = true }, new() { Label = "الضريبة %", LabelEn = "Tax %", Num = true }, new() { Label = "الإجمالي", LabelEn = "Total", Num = true }, new() { Label = "المُسلَّم", LabelEn = "Delivered", Num = true } };
			foreach (var l in d.Lines.OrderBy(x => x.LineNo))
				vm.Rows.Add(new() { l.ItemId != null ? names.GetValueOrDefault(l.ItemId.Value, l.ItemDescription ?? ("#" + l.ItemId)) : (l.ItemDescription ?? "—"), Q(l.Qty), N2(l.UnitPrice), N2(l.DiscountAmount), Q(l.TaxRate), N2(l.LineTotal), Q(l.DeliveredQty) });
			vm.Totals.Add(new() { Label = "الإجمالي قبل الضريبة", LabelEn = "Subtotal", Value = N2(d.SubTotal) });
			vm.Totals.Add(new() { Label = "الضريبة", LabelEn = "Tax", Value = N2(d.TaxTotal) });
			vm.Totals.Add(new() { Label = "الإجمالي", LabelEn = "Grand total", Value = N2(d.GrandTotal) });
			return View("DocumentDetails", vm);
		}

		[HttpGet] public async Task<IActionResult> TransferDetails(int id)
		{
			var d = await _context.StockTransfers.AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == DefaultCompanyId);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(StockTransfers)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var vm = new DocDetailVm { Title = "تحويل مخزني", TitleEn = "Stock transfer", DocNo = d.TransferNo ?? ("#" + d.ID), DateStr = Dt(d.TransferDate), Status = d.Status, BackAction = nameof(StockTransfers), BackLabel = "التحويلات بين المخازن", BackLabelEn = "Transfers", JournalEntryId = d.JournalEntryId };
			vm.Header.Add(new() { Label = "من مخزن", LabelEn = "From", Value = wh.GetValueOrDefault(d.FromWarehouseId, "—") });
			vm.Header.Add(new() { Label = "إلى مخزن", LabelEn = "To", Value = wh.GetValueOrDefault(d.ToWarehouseId, "—") });
			vm.Header.Add(new() { Label = "ملاحظات", LabelEn = "Notes", Value = d.Notes ?? "—" });
			vm.Columns = new() { new() { Label = "الصنف", LabelEn = "Item" }, new() { Label = "الكمية", LabelEn = "Qty", Num = true }, new() { Label = "التكلفة", LabelEn = "Unit cost", Num = true }, new() { Label = "الإجمالي", LabelEn = "Total", Num = true }, new() { Label = "الدفعة", LabelEn = "Batch" }, new() { Label = "السيريال", LabelEn = "Serial" } };
			foreach (var l in d.Lines.OrderBy(x => x.LineNo))
				vm.Rows.Add(new() { names.GetValueOrDefault(l.ItemId, "#" + l.ItemId), Q(l.Qty), N2(l.UnitCost), N2(l.LineTotal), l.BatchNo ?? "—", l.SerialNo ?? "—" });
			vm.Totals.Add(new() { Label = "إجمالي التكلفة المنقولة", LabelEn = "Total moved cost", Value = N2(d.TotalCost) });
			return View("DocumentDetails", vm);
		}

		[HttpGet] public async Task<IActionResult> CountDetails(int id)
		{
			var d = await _context.StockCounts.AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == DefaultCompanyId);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(StockCounts)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var vm = new DocDetailVm { Title = "تسوية جرد", TitleEn = "Stock count", DocNo = d.CountNo ?? ("#" + d.ID), DateStr = Dt(d.CountDate), Status = d.Status, BackAction = nameof(StockCounts), BackLabel = "الجرد والتسويات", BackLabelEn = "Stock counts" };
			vm.Header.Add(new() { Label = "المخزن", LabelEn = "Warehouse", Value = wh.GetValueOrDefault(d.WarehouseId, "—") });
			vm.Header.Add(new() { Label = "ملاحظات", LabelEn = "Notes", Value = d.Notes ?? "—" });
			vm.Columns = new() { new() { Label = "الصنف", LabelEn = "Item" }, new() { Label = "الدفتري", LabelEn = "Book", Num = true }, new() { Label = "المعدود", LabelEn = "Counted", Num = true }, new() { Label = "الفرق", LabelEn = "Diff", Num = true }, new() { Label = "التكلفة", LabelEn = "Unit cost", Num = true }, new() { Label = "قيمة الفرق", LabelEn = "Diff value", Num = true }, new() { Label = "السبب", LabelEn = "Reason" } };
			foreach (var l in d.Lines.OrderBy(x => x.LineNo))
				vm.Rows.Add(new() { names.GetValueOrDefault(l.ItemId, "#" + l.ItemId), Q(l.BookQty), Q(l.CountedQty), Q(l.DiffQty), N2(l.UnitCost), N2(l.DiffValue), ReasonName(l.Reason) });
			vm.Totals.Add(new() { Label = "صافي التسوية", LabelEn = "Net adjustment", Value = N2(d.TotalAdjValue) });
			return View("DocumentDetails", vm);
		}

		[HttpGet] public async Task<IActionResult> WriteOffDetails(int id)
		{
			var d = await _context.StockWriteOffs.AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == DefaultCompanyId);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(WriteOffs)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var vm = new DocDetailVm { Title = "مستند إعدام", TitleEn = "Write-off", DocNo = d.WriteOffNo ?? ("#" + d.ID), DateStr = Dt(d.WriteOffDate), Status = d.Status, BackAction = nameof(WriteOffs), BackLabel = "الإعدام والتلف", BackLabelEn = "Write-offs", JournalEntryId = d.JournalEntryId, Danger = true };
			vm.Header.Add(new() { Label = "المخزن", LabelEn = "Warehouse", Value = wh.GetValueOrDefault(d.WarehouseId, "—") });
			vm.Header.Add(new() { Label = "السبب", LabelEn = "Reason", Value = ReasonName(d.Reason) });
			vm.Header.Add(new() { Label = "ملاحظات", LabelEn = "Notes", Value = d.Notes ?? "—" });
			vm.Columns = new() { new() { Label = "الصنف", LabelEn = "Item" }, new() { Label = "الكمية", LabelEn = "Qty", Num = true }, new() { Label = "التكلفة", LabelEn = "Unit cost", Num = true }, new() { Label = "القيمة", LabelEn = "Value", Num = true }, new() { Label = "الدفعة", LabelEn = "Batch" }, new() { Label = "السيريال", LabelEn = "Serial" }, new() { Label = "السبب", LabelEn = "Reason" } };
			foreach (var l in d.Lines.OrderBy(x => x.LineNo))
				vm.Rows.Add(new() { names.GetValueOrDefault(l.ItemId, "#" + l.ItemId), Q(l.Qty), N2(l.UnitCost), N2(l.LineValue), l.BatchNo ?? "—", l.SerialNo ?? "—", ReasonName(l.Reason) });
			vm.Totals.Add(new() { Label = "إجمالي قيمة الإعدام", LabelEn = "Total write-off", Value = N2(d.TotalValue) });
			return View("DocumentDetails", vm);
		}

		[HttpGet] public async Task<IActionResult> LandedCostDetails(int id)
		{
			var d = await _context.LandedCosts.AsNoTracking().Include(t => t.Charges).FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == DefaultCompanyId);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(LandedCosts)); }
			var grNo = await _context.GoodsReceipts.AsNoTracking().Where(g => g.ID == d.GoodsReceiptId).Select(g => g.ReceiptNo).FirstOrDefaultAsync() ?? ("#" + d.GoodsReceiptId);
			var accIds = d.Charges.Select(c => c.AccountId).Distinct().ToList();
			var accs = await _context.Accounts.AsNoTracking().Where(a => accIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => a.Code + " — " + (Ar() ? a.Name : (string.IsNullOrEmpty(a.NameEn) ? a.Name : a.NameEn)));
			var vm = new DocDetailVm { Title = "تكلفة إضافية", TitleEn = "Landed cost", DocNo = d.LandedNo ?? ("#" + d.ID), DateStr = Dt(d.LandedDate), Status = d.Status, BackAction = nameof(LandedCosts), BackLabel = "التكاليف الإضافية", BackLabelEn = "Landed costs", JournalEntryId = d.JournalEntryId };
			vm.Header.Add(new() { Label = "إذن الاستلام", LabelEn = "Goods receipt", Value = grNo });
			vm.Header.Add(new() { Label = "طريقة التوزيع", LabelEn = "Allocation", Value = d.AllocationMethod == "Qty" ? (Ar() ? "بالكمية" : "By quantity") : (Ar() ? "بالقيمة" : "By value") });
			vm.Header.Add(new() { Label = "ملاحظات", LabelEn = "Notes", Value = d.Notes ?? "—" });
			vm.Columns = new() { new() { Label = "البيان", LabelEn = "Description" }, new() { Label = "الحساب الدائن", LabelEn = "Credit account" }, new() { Label = "المبلغ", LabelEn = "Amount", Num = true } };
			foreach (var c in d.Charges.OrderBy(x => x.LineNo))
				vm.Rows.Add(new() { c.Description ?? "—", accs.GetValueOrDefault(c.AccountId, "#" + c.AccountId), N2(c.Amount) });
			vm.Totals.Add(new() { Label = "إجمالي المصاريف", LabelEn = "Total charges", Value = N2(d.TotalAmount) });
			return View("DocumentDetails", vm);
		}

		// ================= Go-Live opening balances =================
		[HttpGet] public async Task<IActionResult> OpeningBalances()
		{
			var ctl = await _opening.GetControlAsync(DefaultCompanyId);
			ViewBag.Control = ctl;
			ViewBag.Checks = await _opening.VerifyAsync(DefaultCompanyId);
			ViewBag.Log = await _opening.GetLogAsync(DefaultCompanyId);
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.Items = await _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && i.IsActive && !i.IsComposite && i.ItemType == "Stockable").OrderBy(i => i.ItemCode).ToListAsync();
			ViewBag.Customers = await _context.Customers.AsNoTracking().Where(c => c.CompanyID == DefaultCompanyId && c.IsActive).OrderBy(c => c.Name).ToListAsync();
			ViewBag.Vendors = await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == DefaultCompanyId && v.IsActive).OrderBy(v => v.Name).ToListAsync();
			ViewBag.Accounts = await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true);
			ViewBag.AssetCategories = await _context.AssetCategories.AsNoTracking().Where(c => c.CompanyID == DefaultCompanyId).OrderBy(c => c.Name).ToListAsync();
			ViewBag.Cutoff = (ctl.CutoffDate ?? new DateTime(DateTime.Today.Year, 1, 1)).ToString("yyyy-MM-dd");
			return View();
		}

		private DateTime CutoffOr(DateTime? d) => (d ?? new DateTime(DateTime.Today.Year, 1, 1)).Date;
		private async Task SaveCutoffAsync(DateTime cutoff)
		{
			var c = await _opening.GetControlAsync(DefaultCompanyId);
			if (c.CutoffDate == null && !c.Finalized) { var e = await _context.OpeningBalanceControls.FirstAsync(x => x.CompanyID == DefaultCompanyId); e.CutoffDate = cutoff; await _context.SaveChangesAsync(); }
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddStock(int itemId, int warehouseId, decimal qty, decimal unitCost, string? batchNo, DateTime? expiry, DateTime? cutoff, int? binLocationId)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			var (ok, err, _) = await _opening.PostStockAsync(DefaultCompanyId, cu, new List<OpeningStockLineInput> { new() { ItemId = itemId, WarehouseId = warehouseId, Qty = qty, UnitCost = unitCost, BatchNo = batchNo, Expiry = expiry, BinLocationId = binLocationId } }, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening stock entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddAr(int customerId, decimal amount, DateTime? cutoff)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			var (ok, err) = await _opening.PostArAsync(DefaultCompanyId, cu, customerId, amount, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening customer balance entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddAp(int vendorId, decimal amount, DateTime? cutoff)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			var (ok, err) = await _opening.PostApAsync(DefaultCompanyId, cu, vendorId, amount, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening vendor balance entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddAsset(string name, decimal cost, decimal salvageValue, int usefulLifeMonths, DateTime acquisitionDate, decimal openingAccumDep, int? categoryId, DateTime? cutoff)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			var (ok, err) = await _opening.PostAssetAsync(DefaultCompanyId, cu, new FixedAssetInput { Name = name, Cost = cost, SalvageValue = salvageValue, UsefulLifeMonths = usefulLifeMonths, AcquisitionDate = acquisitionDate, CategoryId = categoryId }, openingAccumDep, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening asset entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddGl(string? linesJson, DateTime? cutoff)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			List<OpeningGlLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<OpeningGlLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var (ok, err) = await _opening.PostGlAsync(DefaultCompanyId, cu, lines, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening account balances entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpFinalize()
		{
			var (ok, err) = await _opening.FinalizeAsync(DefaultCompanyId, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening balances finalized successfully"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		// ================= Integrity reconciliation guard =================
		[HttpGet] public async Task<IActionResult> IntegrityReconciliation()
		{
			ViewBag.Checks = await _integrity.RunAsync(DefaultCompanyId);
			ViewBag.Runs = await _integrity.RecentRunsAsync(DefaultCompanyId, 15);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> RunIntegrity()
		{
			var (run, _) = await _integrity.RunAndLogAsync(DefaultCompanyId, "Manual");
			TempData["InvMsg"] = run.AllOk ? L["Integrity check: all invariants match"].Value : string.Format(L["Integrity check: {0} deviations — administrators notified"].Value, run.FailedCount);
			return RedirectToAction(nameof(IntegrityReconciliation));
		}

		// ================= Capitalize asset from stock (إذن صرف أصول) =================
		[HttpGet] public async Task<IActionResult> CapitalizeAsset()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.AssetItems = await _context.Items.AsNoTracking().Where(i => i.CompanyID == DefaultCompanyId && i.IsActive && i.ItemType == "Asset").OrderBy(i => i.ItemCode).ToListAsync();
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> DoCapitalizeAsset(int itemId, int warehouseId, decimal qty, DateTime date)
		{
			var (ok, err, assetId, cost) = await _stock.CapitalizeFromStockAsync(DefaultCompanyId, itemId, warehouseId, qty, date, null, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Asset capitalized from stock (cost {0:N2}) — entry Dr fixed asset / Cr inventory"].Value, cost) : err;
			return RedirectToAction(ok ? nameof(CapitalizeAsset) : nameof(CapitalizeAsset));
		}

		// ================= Phase I10: Reports =================
		[HttpGet] public IActionResult Reports() => View();

		[HttpGet] public async Task<IActionResult> ValuationReport(int? warehouseId)
		{
			var items = (await _items.GetItemsAsync(DefaultCompanyId)).ToDictionary(i => i.ID, i => i);
			var whs = (await _warehouses.GetWarehousesAsync(DefaultCompanyId)).ToDictionary(w => w.ID, w => w);
			var balances = await _stock.GetBalancesAsync(DefaultCompanyId, warehouseId);
			var rows = balances.Where(b => b.QtyOnHand != 0 || b.TotalValue != 0).Select(b => new InvReportRow
			{
				ItemCode = items.TryGetValue(b.ItemId, out var it) ? it.ItemCode : ("#" + b.ItemId),
				ItemName = items.TryGetValue(b.ItemId, out var it2) ? (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar" && !string.IsNullOrWhiteSpace(it2.NameEn) ? it2.NameEn : it2.Name) : "",
				Warehouse = whs.TryGetValue(b.WarehouseId, out var w) ? w.Code : "—",
				Qty = b.QtyOnHand, AvgCost = b.AvgCost, Value = b.TotalValue
			}).OrderByDescending(r => r.Value).ToList();
			ViewBag.Warehouses = whs.Values.ToList();
			ViewBag.FilterWarehouseId = warehouseId;
			ViewBag.GrandTotal = rows.Sum(r => r.Value);
			return View(rows);
		}

		[HttpGet] public async Task<IActionResult> StagnantReport(int days = 60, int? warehouseId = null)
		{
			var items = (await _items.GetItemsAsync(DefaultCompanyId)).ToDictionary(i => i.ID, i => i);
			var whs = (await _warehouses.GetWarehousesAsync(DefaultCompanyId)).ToDictionary(w => w.ID, w => w);
			var balances = await _stock.GetBalancesAsync(DefaultCompanyId, warehouseId);
			// last movement date per (item, warehouse)
			var last = await _context.StockMovements.AsNoTracking().Where(m => m.CompanyID == DefaultCompanyId)
				.GroupBy(m => new { m.ItemId, m.WarehouseId }).Select(g => new { g.Key.ItemId, g.Key.WarehouseId, Last = g.Max(x => x.MovementDate) }).ToListAsync();
			var lastMap = last.ToDictionary(x => (x.ItemId, x.WarehouseId), x => x.Last);
			var today = DateTime.Today;
			var rows = balances.Where(b => b.QtyOnHand > 0).Select(b =>
			{
				lastMap.TryGetValue((b.ItemId, b.WarehouseId), out var lm);
				return new InvReportRow
				{
					ItemCode = items.TryGetValue(b.ItemId, out var it) ? it.ItemCode : ("#" + b.ItemId),
					ItemName = items.TryGetValue(b.ItemId, out var it2) ? (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar" && !string.IsNullOrWhiteSpace(it2.NameEn) ? it2.NameEn : it2.Name) : "",
					Warehouse = whs.TryGetValue(b.WarehouseId, out var w) ? w.Code : "—",
					Qty = b.QtyOnHand, AvgCost = b.AvgCost, Value = b.TotalValue,
					LastMovement = lm == default ? (DateTime?)null : lm,
					Days = lm == default ? 9999 : (int)(today - lm.Date).TotalDays
				};
			}).Where(r => r.Days >= days).OrderByDescending(r => r.Days).ToList();
			ViewBag.Warehouses = whs.Values.ToList();
			ViewBag.FilterWarehouseId = warehouseId; ViewBag.Days = days;
			ViewBag.GrandTotal = rows.Sum(r => r.Value);
			return View(rows);
		}

		[HttpGet] public async Task<IActionResult> ReorderReport(int? warehouseId)
		{
			var items = (await _items.GetItemsAsync(DefaultCompanyId)).ToDictionary(i => i.ID, i => i);
			var whs = (await _warehouses.GetWarehousesAsync(DefaultCompanyId)).ToDictionary(w => w.ID, w => w);
			var balances = await _stock.GetBalancesAsync(DefaultCompanyId, warehouseId);
			var settings = await _context.ItemWarehouseSettings.AsNoTracking().ToListAsync();
			var rows = new List<InvReportRow>();
			foreach (var b in balances)
			{
				var rp = settings.FirstOrDefault(s => s.ItemId == b.ItemId && s.WarehouseId == b.WarehouseId)?.ReorderPoint ?? 0m;
				if (b.QtyOnHand <= rp || b.QtyOnHand <= 0)
					rows.Add(new InvReportRow
					{
						ItemCode = items.TryGetValue(b.ItemId, out var it) ? it.ItemCode : ("#" + b.ItemId),
						ItemName = items.TryGetValue(b.ItemId, out var it2) ? (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar" && !string.IsNullOrWhiteSpace(it2.NameEn) ? it2.NameEn : it2.Name) : "",
						Warehouse = whs.TryGetValue(b.WarehouseId, out var w) ? w.Code : "—",
						Qty = b.QtyOnHand, ReorderPoint = rp, Shortage = Math.Max(0, rp - b.QtyOnHand)
					});
			}
			ViewBag.Warehouses = whs.Values.ToList(); ViewBag.FilterWarehouseId = warehouseId;
			return View(rows.OrderBy(r => r.Qty).ToList());
		}

		// ================= Phase I5: Batch / Expiry / Serial tracking =================
		private async Task<List<BatchRow>> BuildBatchRowsAsync(int? withinDays = null)
		{
			var c = DefaultCompanyId;
			var items = (await _items.GetItemsAsync(c)).ToDictionary(i => i.ID, i => i);
			var batches = await _context.StockBatches.AsNoTracking().Where(b => b.CompanyID == c).ToListAsync();
			var qtyByBatch = (await _context.StockMovements.AsNoTracking().Where(m => m.CompanyID == c && m.BatchId != null)
				.GroupBy(m => m.BatchId!.Value).Select(g => new { BatchId = g.Key, Qty = g.Sum(x => x.Direction * x.QtyBase) }).ToListAsync())
				.ToDictionary(x => x.BatchId, x => x.Qty);
			var today = DateTime.Today;
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var rows = new List<BatchRow>();
			foreach (var b in batches)
			{
				var onHand = qtyByBatch.TryGetValue(b.ID, out var q) ? q : 0m;
				int? d = b.ExpiryDate.HasValue ? (int)(b.ExpiryDate.Value.Date - today).TotalDays : (int?)null;
				string status = d == null ? "None" : (d < 0 ? "Expired" : (d <= 30 ? "Near" : "OK"));
				rows.Add(new BatchRow
				{
					ItemCode = items.TryGetValue(b.ItemId, out var it) ? it.ItemCode : ("#" + b.ItemId),
					ItemName = items.TryGetValue(b.ItemId, out var it2) ? (!isAr && !string.IsNullOrWhiteSpace(it2.NameEn) ? it2.NameEn : it2.Name) : "",
					BatchNo = b.BatchNo, Expiry = b.ExpiryDate, OnHand = onHand, DaysToExpiry = d, Status = status
				});
			}
			if (withinDays != null) rows = rows.Where(r => r.OnHand > 0 && r.Expiry != null && r.DaysToExpiry <= withinDays).ToList();
			return rows;
		}

		[HttpGet] public async Task<IActionResult> Batches()
		{
			var rows = await BuildBatchRowsAsync();
			return View(rows.OrderBy(r => r.DaysToExpiry ?? int.MaxValue).ToList());
		}

		[HttpGet] public async Task<IActionResult> ExpiryAlerts(int days = 30)
		{
			ViewBag.Days = days;
			var rows = await BuildBatchRowsAsync(days);
			return View(rows.OrderBy(r => r.DaysToExpiry ?? int.MaxValue).ToList());
		}

		// ================= Inventory approvals inbox =================
		[HttpGet][InvPerm("manage")] public async Task<IActionResult> Approvals()
		{
			var list = await _approvals.RecentAsync(80);
			var empIds = list.Where(a => a.RequestedByEmployeeId != null).Select(a => a.RequestedByEmployeeId!.Value)
				.Concat(list.Where(a => a.DecidedByEmployeeId != null).Select(a => a.DecidedByEmployeeId!.Value)).Distinct().ToList();
			var apprIsAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			ViewBag.EmpNames = (await _context.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.ToDictionary(e => e.ID, e => !apprIsAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName);
			ViewBag.MyEmpId = _access.CurrentEmployeeId();
			return View(list);
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> ApproveDoc(int id, string? note)
		{
			var (ok, err) = await _approvals.ApproveAsync(id, _access.CurrentEmployeeId() ?? 0, note);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Approved and executed"].Value : err;
			return RedirectToAction(nameof(Approvals));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> RejectDoc(int id, string? note)
		{
			var (ok, err) = await _approvals.RejectAsync(id, _access.CurrentEmployeeId() ?? 0, note);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Document rejected"].Value : err;
			return RedirectToAction(nameof(Approvals));
		}

		// ================= Inventory roles (RBAC admin) =================
		[HttpGet][InvPerm("manage")] public async Task<IActionResult> InventoryRoles()
		{
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			ViewBag.Employees = (await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == DefaultCompanyId && e.IsActive)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.Select(e => new CrossBuy.ViewModel.EmployeeViewModel { ID = e.ID, FullName = !isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName })
				.OrderBy(e => e.FullName).ToList();
			ViewBag.Branches = await _context.Hierarchicals.AsNoTracking().Where(h => h.IsActive == true).OrderBy(h => h.H_Name).Select(h => new InvBranchOption { H_ID = h.H_ID, H_Name = h.H_Name }).ToListAsync();
			ViewBag.Assignments = (await (from r in _context.InventoryUserRoles.AsNoTracking().Where(r => r.CompanyID == DefaultCompanyId)
										 join e in _context.Employee.AsNoTracking() on r.EmployeeId equals e.ID into ej
										 from e in ej.DefaultIfEmpty()
										 join h in _context.Hierarchicals.AsNoTracking() on r.ScopeBranchId equals h.H_ID into hj
										 from h in hj.DefaultIfEmpty()
										 orderby r.ID descending
										 select new { r.ID, r.EmployeeId, e.FullName, e.FullNameEn, r.Role, Branch = h != null ? h.H_Name : null }).ToListAsync())
				.Select(x => new InvRoleAssignmentRow { ID = x.ID, EmployeeId = x.EmployeeId, EmployeeName = x.FullName != null ? (!isAr && !string.IsNullOrWhiteSpace(x.FullNameEn) ? x.FullNameEn : x.FullName) : ("#" + x.EmployeeId), Role = x.Role, Branch = x.Branch }).ToList();
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> AssignRole(int employeeId, string role, int? scopeBranchId)
		{
			var allowed = new[] { "InventoryManager", "WarehouseKeeper", "PurchasingOfficer", "InventoryAuditor" };
			if (employeeId <= 0 || !allowed.Contains(role)) { TempData["InvErr"] = L["Invalid data"].Value; return RedirectToAction(nameof(InventoryRoles)); }
			var exists = await _context.InventoryUserRoles.AnyAsync(r => r.CompanyID == DefaultCompanyId && r.EmployeeId == employeeId && r.Role == role && r.ScopeBranchId == scopeBranchId);
			if (!exists)
			{
				_context.InventoryUserRoles.Add(new InventoryUserRole { CompanyID = DefaultCompanyId, EmployeeId = employeeId, Role = role, ScopeBranchId = role == "WarehouseKeeper" ? scopeBranchId : null, CreatedAt = DateTime.UtcNow });
				await _context.SaveChangesAsync();
			}
			TempData["InvMsg"] = L["Role assigned"].Value;
			return RedirectToAction(nameof(InventoryRoles));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> RemoveRole(int id)
		{
			var r = await _context.InventoryUserRoles.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == DefaultCompanyId);
			if (r != null) { _context.InventoryUserRoles.Remove(r); await _context.SaveChangesAsync(); }
			TempData["InvMsg"] = L["Role removed"].Value;
			return RedirectToAction(nameof(InventoryRoles));
		}

		// ================= Inventory settings =================
		[HttpGet] public async Task<IActionResult> Settings()
		{
			var s = await _context.InventorySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == DefaultCompanyId)
				?? new InventorySettings { CompanyID = DefaultCompanyId, InterBranchTransferMode = "CostCenterPosting" };
			return View(s);
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> SaveSettings(string interBranchTransferMode, string writeOffMode, decimal approvalThreshold, decimal minMarginPct, string minMarginMode, decimal maxLineDiscountPct, string discountApprovalMode)
		{
			var s = await _context.InventorySettings.FirstOrDefaultAsync(x => x.CompanyID == DefaultCompanyId);
			if (s == null) { s = new InventorySettings { CompanyID = DefaultCompanyId, CreatedAt = DateTime.UtcNow }; _context.InventorySettings.Add(s); }
			s.InterBranchTransferMode = interBranchTransferMode == "NoGL" ? "NoGL" : "CostCenterPosting";
			s.WriteOffMode = writeOffMode == "AdjustmentReason" ? "AdjustmentReason" : "SeparateDocument";
			s.ApprovalThreshold = approvalThreshold < 0 ? 0 : approvalThreshold;
			s.MinMarginPct = minMarginPct < 0 ? 0 : minMarginPct;
			s.MinMarginMode = (minMarginMode == "Warn" || minMarginMode == "Block") ? minMarginMode : "Off";
			s.MaxLineDiscountPct = maxLineDiscountPct < 0 ? 0 : maxLineDiscountPct;
			s.DiscountApprovalMode = (discountApprovalMode == "Warn" || discountApprovalMode == "Block") ? discountApprovalMode : "Off";
			await _context.SaveChangesAsync();
			TempData["InvMsg"] = L["Inventory settings saved"].Value;
			return RedirectToAction(nameof(Settings));
		}

		// ================= Phase I9: Landed cost =================
		[HttpGet] public async Task<IActionResult> LandedCosts()
		{
			return View(await _context.LandedCosts.AsNoTracking().Where(l => l.CompanyID == DefaultCompanyId).OrderByDescending(l => l.ID).ToListAsync());
		}

		[HttpGet] public async Task<IActionResult> NewLandedCost(int? grId)
		{
			ViewBag.Receipts = await _proc.GetReceiptsAsync(DefaultCompanyId);
			ViewBag.Accounts = await _coa.GetFlatAsync(DefaultCompanyId, postableOnly: true);
			if (grId != null)
			{
				var gr = await _proc.GetReceiptAsync(DefaultCompanyId, grId.Value);
				ViewBag.GR = gr;
				if (gr != null)
				{
					var itemIds = gr.Lines.Select(x => x.ItemId).ToList();
					ViewBag.ItemNames = await _context.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemCode + " — " + i.Name);
				}
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CreateLandedCost(int goodsReceiptId, DateTime landedDate, string allocationMethod, string? notes, string? chargesJson)
		{
			List<LandedChargeInput> charges;
			try { charges = System.Text.Json.JsonSerializer.Deserialize<List<LandedChargeInput>>(chargesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { charges = new(); }
			var (ok, err, _) = await _stock.PostLandedCostAsync(DefaultCompanyId, goodsReceiptId, landedDate, allocationMethod ?? "Value", charges, notes, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewLandedCost), new { grId = goodsReceiptId }); }
			TempData["InvMsg"] = L["Landed cost posted; inventory value updated"].Value;
			return RedirectToAction(nameof(LandedCosts));
		}

		// ================= Phase I8: Planning (reorder settings + suggestions) =================
		[HttpGet] public async Task<IActionResult> ReorderSettings(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(DefaultCompanyId);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				var items = await _items.GetItemsAsync(DefaultCompanyId);
				var settings = (await _context.ItemWarehouseSettings.AsNoTracking().Where(s => s.WarehouseId == warehouseId).ToListAsync())
					.ToDictionary(s => s.ItemId, s => s);
				ViewBag.Rows = items.Where(i => i.ItemType == "Stockable").Select(i =>
				{
					settings.TryGetValue(i.ID, out var s);
					return new InvReportRow { ItemId = i.ID, ItemCode = i.ItemCode, ItemName = i.Name, ReorderPoint = s?.ReorderPoint ?? 0, MinQty = s?.MinQty ?? 0, MaxQty = s?.MaxQty ?? 0 };
				}).ToList();
			}
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> SaveReorderSettings(int warehouseId, string? settingsJson)
		{
			List<InvReportRow> rows;
			try { rows = System.Text.Json.JsonSerializer.Deserialize<List<InvReportRow>>(settingsJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { rows = new(); }
			foreach (var r in rows)
			{
				if (r.ItemId <= 0) continue;
				var s = await _context.ItemWarehouseSettings.FirstOrDefaultAsync(x => x.ItemId == r.ItemId && x.WarehouseId == warehouseId);
				if (s == null) { s = new ItemWarehouseSetting { ItemId = r.ItemId, WarehouseId = warehouseId }; _context.ItemWarehouseSettings.Add(s); }
				s.ReorderPoint = r.ReorderPoint; s.MinQty = r.MinQty; s.MaxQty = r.MaxQty;
			}
			await _context.SaveChangesAsync();
			TempData["InvMsg"] = L["Reorder points saved"].Value;
			return RedirectToAction(nameof(ReorderSettings), new { warehouseId });
		}

		[HttpGet] public async Task<IActionResult> Planning(int? warehouseId)
		{
			var items = (await _items.GetItemsAsync(DefaultCompanyId)).ToDictionary(i => i.ID, i => i);
			var whs = (await _warehouses.GetWarehousesAsync(DefaultCompanyId)).ToDictionary(w => w.ID, w => w);
			var balances = await _stock.GetBalancesAsync(DefaultCompanyId, warehouseId);
			var settings = await _context.ItemWarehouseSettings.AsNoTracking().ToListAsync();
			var rows = new List<InvReportRow>();
			foreach (var b in balances)
			{
				var s = settings.FirstOrDefault(x => x.ItemId == b.ItemId && x.WarehouseId == b.WarehouseId);
				var rp = s?.ReorderPoint ?? 0m; var mx = s?.MaxQty ?? 0m;
				if (rp <= 0 || b.QtyOnHand > rp) continue;   // only items at/below reorder
				var target = mx > rp ? mx : rp;
				var suggested = Math.Max(0, target - b.QtyOnHand);
				if (suggested <= 0) continue;
				items.TryGetValue(b.ItemId, out var it);
				rows.Add(new InvReportRow
				{
					ItemId = b.ItemId, ItemCode = it?.ItemCode ?? ("#" + b.ItemId), ItemName = it?.Name ?? "",
					Warehouse = whs.TryGetValue(b.WarehouseId, out var w) ? w.Code : "—",
					Qty = b.QtyOnHand, ReorderPoint = rp, MaxQty = mx, Suggested = suggested, Cost = it?.OpeningCost ?? b.AvgCost
				});
			}
			ViewBag.Warehouses = whs.Values.ToList(); ViewBag.FilterWarehouseId = warehouseId;
			ViewBag.Vendors = await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == DefaultCompanyId).OrderBy(v => v.Name).ToListAsync();
			return View(rows.OrderByDescending(r => r.Suggested).ToList());
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("purchase")]
		public async Task<IActionResult> GeneratePO(int vendorId, int? warehouseId, string? linesJson)
		{
			List<InvReportRow> sel;
			try { sel = System.Text.Json.JsonSerializer.Deserialize<List<InvReportRow>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { sel = new(); }
			var lines = sel.Where(r => r.ItemId > 0 && r.Suggested > 0).Select(r => new PoLineInput { ItemId = r.ItemId, ItemDescription = r.ItemName, Qty = r.Suggested, UnitPrice = r.Cost, TaxRate = 0 }).ToList();
			if (lines.Count == 0) { TempData["InvErr"] = L["Select at least one item"].Value; return RedirectToAction(nameof(Planning), new { warehouseId }); }
			var (ok, err, po) = await _proc.CreatePurchaseOrderAsync(DefaultCompanyId, vendorId, warehouseId, DateTime.Today, null, "مولّد من تخطيط النواقص", lines, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(Planning), new { warehouseId }); }
			TempData["InvMsg"] = string.Format(L["Purchase order {0} created from planning"].Value, po?.OrderNo);
			return RedirectToAction(nameof(PurchaseOrders));
		}

		[HttpGet] public async Task<IActionResult> Serials(string? status)
		{
			var c = DefaultCompanyId;
			var items = (await _items.GetItemsAsync(c)).ToDictionary(i => i.ID, i => i);
			var whs = (await _warehouses.GetWarehousesAsync(c)).ToDictionary(w => w.ID, w => w);
			var q = _context.StockSerials.AsNoTracking().Where(s => s.CompanyID == c);
			if (!string.IsNullOrEmpty(status)) q = q.Where(s => s.Status == status);
			var serials = await q.OrderByDescending(s => s.ID).ToListAsync();
			ViewBag.FilterStatus = status;
			return View(serials.Select(s => new SerialRow
			{
				ItemCode = items.TryGetValue(s.ItemId, out var it) ? it.ItemCode : ("#" + s.ItemId),
				ItemName = items.TryGetValue(s.ItemId, out var it2) ? it2.Name : "",
				SerialNo = s.SerialNo,
				Warehouse = s.WarehouseId != null && whs.TryGetValue(s.WarehouseId.Value, out var w) ? w.Code : "—",
				Status = s.Status
			}).ToList());
		}
	}

	public class InvReportRow
	{
		public string ItemCode { get; set; } = "";
		public string ItemName { get; set; } = "";
		public string Warehouse { get; set; } = "";
		public decimal Qty, AvgCost, Value, ReorderPoint, Shortage;
		public decimal MinQty, MaxQty, Suggested, Cost;
		public int ItemId;
		public DateTime? LastMovement;
		public int Days;
	}

	public class BatchRow
	{
		public string ItemCode { get; set; } = "";
		public string ItemName { get; set; } = "";
		public string BatchNo { get; set; } = "";
		public DateTime? Expiry;
		public decimal OnHand;
		public int? DaysToExpiry;
		public string Status { get; set; } = "";   // OK / Near / Expired / None
	}

	public class SerialRow
	{
		public string ItemCode { get; set; } = "";
		public string ItemName { get; set; } = "";
		public string SerialNo { get; set; } = "";
		public string Warehouse { get; set; } = "";
		public string Status { get; set; } = "";   // InStock / Issued
	}

	// ---- Inventory home dashboard view models ----
	public class InventoryDashboardDto
	{
		public int ItemCount, CategoryCount, WarehouseCount, UnitCount, CompositeCount, OutOfStock;
		public decimal TotalValue, TotalQty;
		public List<InvMonthRow> Months { get; set; } = new();
		public List<CrossBuy.Models.Context.Inventory.StockMovement> Recent { get; set; } = new();
		public Dictionary<int, string> ItemName { get; set; } = new();
		public Dictionary<int, string> WhName { get; set; } = new();
		public List<InvNameValue> TopItems { get; set; } = new();
		public List<InvNameValue> ByWarehouse { get; set; } = new();
	}
	public class InvMonthRow { public int Year, Month; public decimal InValue, OutValue; }
	public class InvNameValue { public string Name { get; set; } = ""; public decimal Value; public int Pct; }
	// ===== Manufacturing dashboard (mirrors InventoryDashboardDto shape) =====
	public class ManufDashboardDto
	{
		public int TotalWos, Draft, Active, Completed, Cancelled;
		public decimal OpenWip, MonthProducedValue, TotalProducedValue;
		public int MonthCompleted;
		public decimal TotMaterial, TotLabor, TotOverhead;
		public List<ManufMonthRow> Months { get; set; } = new();
		public List<ManufWoLine> Recent { get; set; } = new();
		public List<InvNameValue> TopItems { get; set; } = new();
	}
	public class ManufMonthRow { public int Year, Month; public int Count; public decimal Value; }
	public class ManufWoLine
	{
		public int Id; public string? WoNo; public string ItemName = ""; public decimal Qty, ProducedQty; public string Status = "";
		public DateTime? When; public decimal TotalCost;
	}
	// public DTOs for InventoryRoles ViewBag (avoid anonymous-type dynamic binding across the Views assembly)
	public class InvBranchOption { public int H_ID { get; set; } public string? H_Name { get; set; } }
	public class InvRoleAssignmentRow { public int ID { get; set; } public int EmployeeId { get; set; } public string? EmployeeName { get; set; } public string Role { get; set; } = ""; public string? Branch { get; set; } }
	public class InvBookBalanceRow { public int ItemId { get; set; } public decimal QtyOnHand { get; set; } public decimal AvgCost { get; set; } }
}
