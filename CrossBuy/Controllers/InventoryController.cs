using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.ViewModel.Ai;
using System.Text.Json;
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
		// COMPANY RESOLUTION — replaces `private const int co = 1;`
		//
		// WHAT THE CONSTANT DID. Every read, write, report and lookup on this controller named company 1,
		// whoever was signed in. That is not a display bug: PostMovementAsync, TransferAsync, WriteOffAsync,
		// PostCountAsync, PostLandedCostAsync, the whole ManufService write surface and the item / category /
		// unit / warehouse creators all received the literal, so another tenant's stock and work orders were
		// readable AND writable from any session.
		//
		// WHY AN ACTION FILTER AND A PLAIN PROPERTY, rather than `await CompanyIdAsync()` at each call site.
		// Resolution is async, but the company is needed inside LINQ lambdas, projections, expression-bodied
		// actions and sync helpers, and `await` is illegal in a non-async lambda — doing it inline produced 138
		// CS4034 errors. Resolving ONCE in OnActionExecutionAsync, before the action body runs, makes the value
		// an ordinary int that every one of those places can read. It is also one resolution per request instead
		// of one per call site.
		//
		// FAIL CLOSED. `co` is 0 when nothing resolves, and 0 is a company id no row can hold — so every
		// downstream `CompanyId == companyId` predicate matches nothing, for reads AND for the row lookups the
		// writes perform. That is the floor beneath every action, reached without the action having to remember
		// to check. CompanyRefusedView/Json are for actions that additionally want to say so out loud.
		private int? _companyId;

		/// The resolved company for this request. 0 when none resolves — never 1, never a fallback.
		private int co => _companyId ?? 0;

		public override async Task OnActionExecutionAsync(
			Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context,
			Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
		{
			if (!_companyId.HasValue)
			{
				var scope = await _company.ResolveAsync();
				_companyId = scope.Ok ? scope.CompanyId : 0;
			}
			await next();
		}

		/// Page-shaped refusal, matching what InvPerm already does to a denied page request.
		private IActionResult CompanyRefusedView()
		{
			TempData["Err"] = "The company for this session could not be determined";
			return RedirectToAction("Index", "Home");
		}

		/// Endpoint-shaped refusal for the JSON actions these controllers expose.
		private IActionResult CompanyRefusedJson() =>
			Json(new { ok = false, error = "no_company_resolved" });
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
		private readonly IAiInsightsService _insights;
		private readonly IStringLocalizer<CrossBuy.SharedResources> L;
		public InventoryController(IItemService items, IWarehouseService warehouses, IChartOfAccountsService coa, CrossDbContext context, IWebHostEnvironment env, IStockService stock, IProcurementService proc, ISellingService sell, IInventoryAccessService access, IInventoryApprovalService approvals, IOpeningBalanceService opening, IIntegrityCheckService integrity, IPricingService pricing, IThreeWayMatchService match, ICurrencyService currency, IAccountingAccessService accAccess, IManufService manuf, IShelfLabelService labels, ICurrencyRounding rounding, CrossBuy.BL.Platform.IRequestCompanyResolver company, IAiInsightsService insights, IStringLocalizer<CrossBuy.SharedResources> localizer)
		{
			_items = items; _warehouses = warehouses; _coa = coa; _context = context; _env = env; _stock = stock; _proc = proc; _sell = sell; _access = access; _approvals = approvals; _opening = opening; _integrity = integrity; _pricing = pricing; _match = match; _currency = currency; _accAccess = accAccess; _manuf = manuf; _labels = labels; _rounding = rounding; L = localizer; _company = company; _insights = insights;
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
			var c = co;
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
		[HttpGet] public async Task<IActionResult> Units() => View(await _items.GetUnitsAsync(co));

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> CreateUnit(string code, string name, string nameEn)
		{
			var (ok, err) = await _items.CreateUnitAsync(co, code, name, nameEn);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Unit added"].Value : err;
			return RedirectToAction(nameof(Units));
		}

		// ---------------- Categories (tree) ----------------
		[HttpGet] public async Task<IActionResult> Categories()
		{
			ViewBag.Accounts = await _coa.GetFlatAsync(co, postableOnly: true);
			return View(await _items.GetCategoryTreeAsync(co));
		}

		private async Task PopulateCategoryFormListsAsync(int? excludeId = null)
		{
			ViewBag.Accounts = await _coa.GetFlatAsync(co, postableOnly: true);
			ViewBag.Categories = (await _items.GetCategoriesAsync(co))
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
			var cat = (await _items.GetCategoriesAsync(co)).FirstOrDefault(c => c.ID == id);
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
			var (ok, err) = await _items.CreateCategoryAsync(co, model, null);
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
			var (ok, err) = await _items.UpdateCategoryAsync(co, id, model, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(EditCategory), new { id }); }
			TempData["InvMsg"] = L["Category updated"].Value;
			return RedirectToAction(nameof(Categories));
		}

		// ---------------- Items ----------------
		[HttpGet] public async Task<IActionResult> Items()
		{
			ViewBag.Categories = await _items.GetCategoriesAsync(co);
			ViewBag.Units = await _items.GetUnitsAsync(co);
			return View();   // rows loaded server-side & paged via ItemsData
		}

		// server-side paged + filtered rows (handles millions of items). Returns the rows partial + paging headers.
		[HttpGet] public async Task<IActionResult> ItemsData(string? q, int? categoryId, string? type, bool? active, int page = 1, int pageSize = 25)
		{
			var (rows, total) = await _items.SearchItemsAsync(co, q, categoryId, type, active, page, pageSize);
			ViewBag.CatById = (await _items.GetCategoriesAsync(co)).ToDictionary(c => c.ID, c => c);
			ViewBag.UById = (await _items.GetUnitsAsync(co)).ToDictionary(u => u.ID, u => u);
			if (pageSize < 1) pageSize = 25; else if (pageSize > 200) pageSize = 200;
			var pages = (int)System.Math.Ceiling(total / (double)pageSize);
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = pages.ToString();
			return PartialView("_ItemRows", rows);
		}

		[HttpGet] public async Task<IActionResult> ItemsExport(string? q, int? categoryId, string? type, bool? active)
		{
			var (rows, _) = await _items.SearchItemsAsync(co, q, categoryId, type, active, 1, 100000);
			var cats = (await _items.GetCategoriesAsync(co)).ToDictionary(c => c.ID, c => c);
			var units = (await _items.GetUnitsAsync(co)).ToDictionary(u => u.ID, u => u);
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
			ViewBag.Categories = await _items.GetCategoriesAsync(co);
			ViewBag.Units = await _items.GetUnitsAsync(co);
			ViewBag.TaxCodes = await _context.TaxCodes.AsNoTracking().Where(t => t.CompanyID == co && t.Kind == "VAT" && t.IsActive).ToListAsync();
			// candidate components for a composite item (exclude itself + other composites)
			ViewBag.ComponentItems = await _context.Items.AsNoTracking()
				.Where(i => i.CompanyID == co && i.IsActive && !i.IsComposite && (excludeItemId == null || i.ID != excludeItemId))
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
			var item = await _items.GetItemAsync(co, id);
			if (item == null) { TempData["InvErr"] = L["Item not found"].Value; return RedirectToAction(nameof(Items)); }
			await PopulateItemFormListsAsync(id);
			ViewBag.IsEdit = true;
			ViewBag.ItemUnits = await _items.GetItemUnitsAsync(id);
			ViewBag.ItemComponents = await _items.GetItemComponentsAsync(id);
			ViewBag.ItemImages = await _items.GetItemImagesAsync(co, id);   // storefront gallery
			// analytics for the item card (last/avg/dates + balance trend) — totals across ALL warehouses
			var movements = await _stock.GetMovementsAsync(co, id, null);
			var itemBals = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == co && b.ItemId == id).ToListAsync();
			decimal bq = itemBals.Sum(b => b.QtyOnHand), bv = itemBals.Sum(b => b.TotalValue);
			decimal ba = bq != 0 ? Math.Round(bv / bq, 2, MidpointRounding.AwayFromZero) : 0m;
			ViewBag.Movements = movements; ViewBag.BalQty = bq; ViewBag.BalValue = bv; ViewBag.BalAvg = ba;
			// per-warehouse on-hand breakdown (only warehouses with a non-zero balance)
			var whById = (await _warehouses.GetWarehousesAsync(co)).ToDictionary(w => w.ID, w => w);
			ViewBag.WarehouseBalances = itemBals.Where(b => b.QtyOnHand != 0)
				.Select(b => { whById.TryGetValue(b.WarehouseId, out var w); return (code: w?.Code ?? ("#" + b.WarehouseId), nameAr: w?.Name ?? "", nameEn: w?.NameEn, qty: b.QtyOnHand, value: b.TotalValue); })
				.OrderByDescending(x => x.qty).ToList();

			// ---- 4-2: manufacturing summary card (only for Assembly = manufactured items) ----
			if (item.IsComposite && item.CompositeType == "Assembly")
			{
				var comps = (List<ItemComponent>)ViewBag.ItemComponents;
				var compIds = comps.Select(c => c.ComponentItemId).Distinct().ToList();
				var compItems = await _context.Items.AsNoTracking().Where(i => i.CompanyID == co && compIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i);
				// current average unit cost per component (Σ value / Σ qty across all warehouses)
				var compBals = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == co && compIds.Contains(b.ItemId))
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
				var (stdLabor, stdOverhead) = await _manuf.ComputeRoutingCostAsync(co, id, 1);
				ViewBag.IsManufactured = true;
				ViewBag.BomLines = bomLines;
				ViewBag.StdMaterial = stdMaterial;
				ViewBag.StdLabor = stdLabor;
				ViewBag.StdOverhead = stdOverhead;
				ViewBag.RoutingOps = await _manuf.GetRoutingAsync(co, id);
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
			var (ok, err, item) = await _items.CreateItemAsync(co, input, null);
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
				await _items.AddItemImagesAsync(co, item.ID, paths);
			}
			if (item != null) await _items.RecomputeStoreHoverAsync(co, item.ID);
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
			var (ok, err) = await _items.UpdateItemAsync(co, id, input, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(EditItem), new { id }); }
			// storefront gallery: remove the deselected images, then append any newly uploaded ones
			if (!string.IsNullOrWhiteSpace(removeImageIds))
			{
				var ids = removeImageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0);
				var deletedPaths = await _items.RemoveItemImagesAsync(co, id, ids);
				foreach (var pth in deletedPaths) DeleteUploadedFile(pth);   // also delete the physical files
			}
			if (galleryFiles != null && galleryFiles.Count > 0)
			{
				var paths = new List<string>();
				foreach (var f in galleryFiles) { var p = await SaveItemImageAsync(f); if (p != null) paths.Add(p); }
				await _items.AddItemImagesAsync(co, id, paths);
			}
			await _items.RecomputeStoreHoverAsync(co, id);   // hover = first gallery image (or null)
			TempData["InvMsg"] = L["Item updated"].Value;
			return RedirectToAction(nameof(Items));
		}

		// ---------------- Warehouses ----------------
		[HttpGet] public async Task<IActionResult> Warehouses()
		{
			ViewBag.Branches = await _context.Hierarchicals.AsNoTracking().Where(h => h.IsActive == true).OrderBy(h => h.H_Name).ToListAsync();
			ViewBag.Employees = await _context.Employee.AsNoTracking().OrderByDisplayName().ToListAsync();
			return View(await _warehouses.GetWarehousesAsync(co));
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> CreateWarehouse(Warehouse model)
		{
			var (ok, err) = await _warehouses.CreateWarehouseAsync(co, model, null);
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
			return Json(new { ok = true, id = w.ID, name = w.Code + " — " + (isAr ? w.Name : DisplayName.Or(w.NameEn, w.Name)) });
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> EditWarehouse(int id, Warehouse model)
		{
			var (ok, err) = await _warehouses.UpdateWarehouseAsync(co, id, model, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Warehouse updated"].Value : err;
			return RedirectToAction(nameof(Warehouses));
		}

		// ---------------- Warehouse sections / racks (BinLocation tree) ----------------
		[HttpGet] public async Task<IActionResult> WarehouseSections(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.FilterWarehouseId = warehouseId;
			ViewBag.Bins = warehouseId != null ? await _warehouses.GetBinLocationsAsync(warehouseId.Value) : new List<BinLocation>();
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> SaveBinLocation(int warehouseId, int id, string code, string? name, string locationType, int? parentId, bool isActive = true, string? nameEn = null)
		{
			var (ok, err) = await _warehouses.SaveBinLocationAsync(warehouseId, id, code, name, locationType, parentId, isActive, nameEn);
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
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				var bins = await _warehouses.GetBinLocationsAsync(warehouseId.Value);
				ViewBag.Sections = bins.Where(b => b.LocationType == "Section" && b.IsActive).ToList();
				ViewBag.Racks = bins.Where(b => b.LocationType == "Rack" && b.IsActive).ToList();
				var items = await _items.GetItemsAsync(co);
				var settings = (await _context.ItemWarehouseSettings.AsNoTracking().Where(s => s.WarehouseId == warehouseId).ToListAsync())
					.ToDictionary(s => s.ItemId, s => s);
				ViewBag.Rows = items.Where(i => i.ItemType == "Stockable").Select(i =>
				{
					settings.TryGetValue(i.ID, out var s);
					return new ItemLocationGridRow { ItemId = i.ID, ItemCode = i.ItemCode, ItemName = ItemDisplayName(i), SectionId = s?.DefaultSectionId, RackId = s?.DefaultBinLocationId };
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

		// The GRID's row, and it must be PUBLIC. Views are compiled at runtime here
		// (Program.cs: AddRazorRuntimeCompilation), so they live in their own assembly, and C# anonymous
		// types are internal to the assembly that declares them. ItemLocations.cshtml reads its rows as
		// IEnumerable<dynamic>, so an anonymous type left the runtime binder unable to see ANY member and
		// it reported the nearest accessible type instead:
		//
		//     RuntimeBinderException: 'object' does not contain a definition for 'SectionId'
		//
		// SectionId only because it is the first member the loop touches; ItemCode and ItemName would have
		// failed the same way. The page looked healthy until a warehouse was chosen, because Rows is only
		// populated when warehouseId is supplied and an empty grid never binds anything.
		//
		// Kept separate from ItemLocationRow above rather than extending it: that one is the JSON contract
		// SaveItemLocations deserializes, and display columns have no business widening a POST contract.
		// CrmController.CrmRoleRow is the same fix for the same reason.
		public class ItemLocationGridRow
		{
			public int ItemId { get; set; }
			public string ItemCode { get; set; } = "";
			public string ItemName { get; set; } = "";
			public int? SectionId { get; set; }
			public int? RackId { get; set; }
		}

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
		// THE NAME THE READER SEES, for the two entities this screen names. Written once here rather than
		// four times inline: the same rule was already spelled out wrong four times in this action, which is
		// how one of them ends up missed. Both English columns are optional, so both fall back to Arabic -
		// a name in the wrong language beats an empty cell in a stock report.
		private string ItemDisplayName(CrossBuy.Models.Context.Inventory.Item i) =>
			Ar() || string.IsNullOrWhiteSpace(i.NameEn) ? i.Name : i.NameEn!;

		private string BinDisplayName(CrossBuy.Models.Context.Inventory.BinLocation b) =>
			Ar() || string.IsNullOrWhiteSpace(b.NameEn) ? (b.Name ?? "") : b.NameEn!;

		[HttpGet] public async Task<IActionResult> RackBalances(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				var bins = (await _warehouses.GetBinLocationsAsync(warehouseId.Value)).ToDictionary(b => b.ID, b => b);
				ViewBag.Bins = bins;
				ViewBag.ActiveBins = bins.Values.Where(b => b.IsActive).OrderBy(b => b.LocationType == "Rack" ? 1 : 0).ThenBy(b => b.Code).ToList();
				var binStocks = await _stock.GetBinStocksAsync(co, warehouseId.Value);
				var itemsById = (await _items.GetItemsAsync(co)).ToDictionary(i => i.ID, i => i);
				ViewBag.Items = itemsById;
				ViewBag.Rows = binStocks
					.Select(bs => new InvRackStockRow
					{
						ItemId = bs.ItemId, BinLocationId = bs.BinLocationId, QtyOnHand = bs.QtyOnHand,
						ItemCode = itemsById.TryGetValue(bs.ItemId, out var it) ? it.ItemCode : ("#" + bs.ItemId),
						ItemName = itemsById.TryGetValue(bs.ItemId, out var it2) ? ItemDisplayName(it2) : "",
						BinCode = bins.TryGetValue(bs.BinLocationId, out var b) ? b.Code : ("#" + bs.BinLocationId),
						BinName = bins.TryGetValue(bs.BinLocationId, out var b2) ? BinDisplayName(b2) : "",
						BinType = bins.TryGetValue(bs.BinLocationId, out var b3) ? b3.LocationType : ""
					})
					.OrderBy(r => r.BinCode).ThenBy(r => r.ItemCode).ToList();
				// reconciliation: located (Σ bins) vs warehouse total, per item that has bin data
				var located = binStocks.GroupBy(b => b.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.QtyOnHand));
				var whBals = (await _stock.GetBalancesAsync(co, warehouseId.Value)).ToDictionary(b => b.ItemId, b => b.QtyOnHand);
				ViewBag.Recon = located.Keys.Union(whBals.Keys.Where(k => whBals[k] != 0))
					.Select(id => new InvRackReconRow
					{
						ItemCode = itemsById.TryGetValue(id, out var it) ? it.ItemCode : ("#" + id),
						ItemName = itemsById.TryGetValue(id, out var it2) ? ItemDisplayName(it2) : "",
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
			var (ok, err, n) = await _stock.InitializeBinStockFromDefaultsAsync(co, warehouseId, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Distributed {0} items to their default locations"].Value, n) : err;
			return RedirectToAction(nameof(RackBalances), new { warehouseId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> SetBinCount(int warehouseId, int binLocationId, int itemId, decimal countedQty)
		{
			var (ok, err) = await _stock.SetBinCountAsync(co, warehouseId, binLocationId, itemId, countedQty, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Rack count saved"].Value : err;
			return RedirectToAction(nameof(RackBalances), new { warehouseId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> RelocateBin(int warehouseId, int itemId, int fromBinId, int toBinId, decimal qty)
		{
			var (ok, err) = await _stock.RelocateBinAsync(co, warehouseId, itemId, fromBinId, toBinId, qty, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Quantity relocated between locations"].Value : err;
			return RedirectToAction(nameof(RackBalances), new { warehouseId });
		}

		// ================= Phase I1: stock movements / balances =================

		// shared lookups for the movement form + list rendering
		private async Task PopulateStockListsAsync()
		{
			ViewBag.Items = await _items.GetItemsAsync(co);
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.Units = await _items.GetUnitsAsync(co);
		}

		// shared: pagination headers consumed by the list views' fetch JS
		private void SetPaging(int total, int page, int pageSize)
		{
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
		}

		// ===== Inventory Risk Insights (AI, on-premises) =====
		//
		// PLACED HERE, next to StockMovements, because that is where this screen sends the reader: an
		// insight the user cannot act on from the page it appears on is a report, not a decision aid.
		//
		// READ-ONLY BY CONSTRUCTION. The action gathers nothing and writes nothing — it asks
		// IAiInsightsService, which is the governed path, and renders what comes back. No stock movement,
		// no reorder, no adjustment is created here or anywhere this screen links to.
		[HttpGet]
		public async Task<IActionResult> RiskInsights()
		{
			// SERVER-DERIVED, and the action deliberately takes no parameters at all — there is nothing a
			// caller could supply for a company, so there is nothing to validate or coerce. The rest of this
			// controller still uses the co constant; that is pre-existing debt this screen does
			// not inherit and does not fix.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok)
			{
				// The resolver's own reason is NOT rendered: it names companies, and "your company is 2, the
				// record is company 1" tells a caller that a record exists where they cannot see it.
				TempData["InvErr"] = L["You do not have permission to perform this action"].Value;
				return RedirectToAction(nameof(Index));
			}

			var vm = new InventoryRiskVm { CompanyId = scope.CompanyId, WindowDays = RiskWindowDays };
			var nowUtc = DateTime.UtcNow;

			try
			{
				var response = await _insights.AnalyzeInventoryAsync(scope.CompanyId, RiskWindowDays);
				if (response.Status != 200)
				{
					vm.Panel = AiInsightMapper.Map(response.Status, null, scope.CompanyId, nowUtc);
					return View(vm);
				}

				var parsed = JsonSerializer.Deserialize<InventoryResult>(
					response.Json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
				if (parsed == null)
				{
					vm.Panel = AiInsightMapper.Failed("ai-insight:unreadable-response", scope.CompanyId, nowUtc);
					return View(vm);
				}

				// ItemsAnalyzed, NOT FlaggedCount. Zero flags out of 900 items is a real, reassuring result;
				// zero flags out of ZERO items is silence, and rendering the two the same way would be the
				// false "all good" this screen exists to avoid.
				vm.Result = parsed;
				vm.Panel = AiInsightMapper.Map(200, parsed.ItemsAnalyzed, scope.CompanyId, nowUtc);
			}
			catch (JsonException)
			{
				vm.Panel = AiInsightMapper.Failed("ai-insight:unreadable-response", scope.CompanyId, nowUtc);
			}
			catch (HttpRequestException)
			{
				// The on-premises model is not running. Inventory keeps working; only this panel is empty.
				vm.Panel = AiInsightMapper.Unavailable("ai-insight:service-unavailable", scope.CompanyId, nowUtc);
			}
			catch (TaskCanceledException)
			{
				vm.Panel = AiInsightMapper.Unavailable("ai-insight:service-timeout", scope.CompanyId, nowUtc);
			}

			return View(vm);
		}

		/// The window the model treats as "recent". Shown on the screen so the classification is judgeable.
		private const int RiskWindowDays = 90;

		[HttpGet] public async Task<IActionResult> StockMovements(int? itemId, int? warehouseId)
		{
			await PopulateStockListsAsync();
			ViewBag.FilterItemId = itemId; ViewBag.FilterWarehouseId = warehouseId;
			return View();   // shell; rows via StockMovementsData
		}

		// ONE PLACE THAT BUILDS THE FILTER. The list and the summary MUST see the same rows: a total
		// that describes a different set than the table under it is worse than no total. Rebuilding
		// the predicate in two places is how they drift, so there is only one.
		private IQueryable<MovementRow> MovementsFiltered(string? q, int? warehouseId)
		{
			var src = _context.StockMovements.AsNoTracking().Where(m => m.CompanyID == co);
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
			return q0;
		}

		// THE TICKER. Quantity and value moved across the WHOLE filter, plus the items moving most.
		// Read-only, and it issues no query the list does not already issue.
		[HttpGet] public async Task<IActionResult> StockMovementsSummary(string? q, int? warehouseId)
		{
			var q0 = MovementsFiltered(q, warehouseId);

			// Direction is +1 in / -1 out. Summed separately so "moved 900 units" cannot hide 450 in
			// and 450 out, which nets to nothing and is a completely different day.
			var agg = await q0.GroupBy(r => 1).Select(g => new
			{
				Count = g.Count(),
				InQty = g.Sum(r => r.Direction > 0 ? r.QtyBase : 0m),
				OutQty = g.Sum(r => r.Direction < 0 ? r.QtyBase : 0m),
				InValue = g.Sum(r => r.Direction > 0 ? r.TotalCost : 0m),
				OutValue = g.Sum(r => r.Direction < 0 ? r.TotalCost : 0m),
			}).FirstOrDefaultAsync();

			var movers = await q0
				.GroupBy(r => new { r.ItemId, r.ItemCode, r.ItemName, r.ItemNameEn })
				.Select(g => new
				{
					g.Key.ItemId, g.Key.ItemCode, g.Key.ItemName, g.Key.ItemNameEn,
					Net = g.Sum(r => r.Direction > 0 ? r.QtyBase : -r.QtyBase),
					Moves = g.Count(),
					Gross = g.Sum(r => r.QtyBase),
				})
				.OrderByDescending(x => x.Gross)
				.Take(6)
				.ToListAsync();

			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			return Json(new
			{
				count = agg?.Count ?? 0,
				inQty = agg?.InQty ?? 0m,
				outQty = agg?.OutQty ?? 0m,
				netQty = (agg?.InQty ?? 0m) - (agg?.OutQty ?? 0m),
				inValue = agg?.InValue ?? 0m,
				outValue = agg?.OutValue ?? 0m,
				netValue = (agg?.InValue ?? 0m) - (agg?.OutValue ?? 0m),
				movers = movers.Select(x => new
				{
					code = x.ItemCode,
					name = ar ? x.ItemName : (string.IsNullOrWhiteSpace(x.ItemNameEn) ? x.ItemName : x.ItemNameEn),
					net = x.Net,
					gross = x.Gross,
					moves = x.Moves,
				})
			});
		}
		[HttpGet] public async Task<IActionResult> StockMovementsData(string? q, int? warehouseId, int page = 1, int pageSize = 25)
		{			var q0 = MovementsFiltered(q, warehouseId);
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
			var (ok, err, _) = await _stock.PostMovementAsync(co, new MovementRequest
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
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.FilterWarehouseId = warehouseId;
			return View();   // shell; rows loaded via StockBalancesData
		}

		[HttpGet] public async Task<IActionResult> ItemsSuggest(string? term)
		{
			var list = await _items.SuggestItemsAsync(co, term, 10);
			return Json(list.Select(x => new { value = x.code, name = x.name }));
		}

		// item picker for select2-ajax (returns id + display text) — used by the price-list line editor
		[HttpGet] public async Task<IActionResult> ItemPickData(string? term)
		{
			var t = (term ?? "").Trim();
			var query = _context.Items.AsNoTracking().Where(i => i.CompanyID == co && i.IsActive);
			if (t.Length > 0) query = query.Where(i => i.ItemCode.Contains(t) || i.Name.Contains(t) || (i.NameEn != null && i.NameEn.Contains(t)) || (i.Barcode != null && i.Barcode.Contains(t)));
			var rows = await query.OrderBy(i => i.ItemCode)
				.Select(i => new { id = i.ID, code = i.ItemCode, name = i.Name, nameEn = i.NameEn, price = i.SalesPrice ?? 0m, cost = i.OpeningCost, baseUom = i.BaseUoMId })
				.Take(20).ToListAsync();
			return Json(new { results = rows.Select(r => new { id = r.id, text = r.code + " — " + r.name, code = r.code, name = r.name, nameEn = r.nameEn, price = r.price, cost = r.cost, baseUom = r.baseUom }) });
		}

		// 3-way match report for a PO (P3-6)
		[HttpGet] public async Task<IActionResult> PoMatch(int id)
		{
			var result = await _match.CheckPoAsync(co, id);
			var po = await _context.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(p => p.ID == id && p.CompanyID == co);
			if (po == null) { TempData["InvErr"] = L["Purchase order not found"].Value; return RedirectToAction(nameof(PurchaseOrders)); }
			ViewBag.PoNo = po.OrderNo; ViewBag.PoId = id;
			return View(result);
		}

		// ---------------- Price lists (P3-5) ----------------
		[HttpGet] public IActionResult PriceLists() => View();   // shell; rows via PriceListsData

		[HttpGet] public async Task<IActionResult> PriceListsData(string? q, bool? active, int page = 1, int pageSize = 25)
		{
			var (rows, total) = await _pricing.SearchAsync(co, q, active, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_PriceListRows", rows);
		}

		[HttpGet] public async Task<IActionResult> PriceListEditor(int? id)
		{
			Models.Context.Inventory.PriceList model;
			if (id.HasValue && id.Value > 0)
			{
				model = await _pricing.GetAsync(co, id.Value) ?? new Models.Context.Inventory.PriceList { IsActive = true };
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
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(co, null);
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
			var (ok, err, newId) = await _pricing.SaveAsync(co, dto, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? (id > 0 ? L["Price list updated"].Value : L["Price list created"].Value) : err;
			return ok ? RedirectToAction(nameof(PriceLists)) : RedirectToAction(nameof(PriceListEditor), new { id });
		}

		// Pricing 2-2: clone a price list (+ its lines) as a new inactive draft
		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CopyPriceList(int id)
		{
			var src = await _pricing.GetAsync(co, id);
			if (src == null) { TempData["InvErr"] = L["Price list not found"].Value; return RedirectToAction(nameof(PriceLists)); }
			var suffix = (DateTime.UtcNow.Ticks % 100000).ToString();
			var dto = new Models.Context.Inventory.PriceList
			{
				ID = 0, Code = (src.Code + "-COPY" + suffix), Name = src.Name + " (نسخة)", NameEn = src.NameEn,
				CurrencyId = src.CurrencyId, CustomerId = src.CustomerId, Segment = src.Segment, Priority = src.Priority,
				IsDefault = false, IsActive = false, ValidFrom = src.ValidFrom, ValidTo = src.ValidTo,
				Lines = src.Lines.Select(l => new Models.Context.Inventory.PriceListLine { ItemId = l.ItemId, MinQty = l.MinQty, UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, ValidFrom = l.ValidFrom, ValidTo = l.ValidTo }).ToList()
			};
			var (ok, err, newId) = await _pricing.SaveAsync(co, dto, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Price list copied as a new draft"].Value : err;
			return ok ? RedirectToAction(nameof(PriceListEditor), new { id = newId }) : RedirectToAction(nameof(PriceLists));
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> DeletePriceList(int id)
		{
			var ok = await _pricing.DeleteAsync(co, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Price list deleted"].Value : L["Delete failed"].Value;
			return RedirectToAction(nameof(PriceLists));
		}

		// ---------------- HM-4: bulk price change ----------------
		[HttpGet] public async Task<IActionResult> BulkPriceChange()
		{
			ViewBag.PriceLists = await _context.PriceLists.AsNoTracking().Where(p => p.CompanyID == co && p.IsActive)
				.OrderBy(p => p.Name).Select(p => new InvIdNameOption { ID = p.ID, Name = p.Name }).ToListAsync();
			ViewBag.Categories = await _context.ItemCategories.AsNoTracking().Where(c => c.CompanyID == co)
				.OrderBy(c => c.Name).Select(c => new InvIdNameOption { ID = c.ID, Name = c.Name }).ToListAsync();
			return View();
		}

		// Preview — read-only; returns the before/after grid. Writes NOTHING.
		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> BulkPreview(int priceListId, int? categoryId, string adjustType, decimal value, string priceRounding)
		{
			var (ok, err, rows) = await _pricing.BulkPreviewAsync(co, priceListId, categoryId, adjustType, value, priceRounding ?? "None");
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
			var (ok, err, batchId, changed) = await _pricing.BulkExecuteAsync(co, priceListId, categoryId, adjustType, value, priceRounding ?? "None", reason ?? "", baseline, User?.Identity?.Name);
			return Json(new { ok, error = err, batchId, changed });
		}

		// Undo — writes the logged OLD prices back as a new batch. Mandatory reason.
		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> BulkUndo(Guid batchId, string reason)
		{
			var (ok, err, newBatchId, restored) = await _pricing.BulkUndoAsync(co, batchId, reason ?? "", User?.Identity?.Name);
			return Json(new { ok, error = err, newBatchId, restored });
		}

		// ---------------- HM-4: shelf labels (A4 grid, browser print) ----------------
		[HttpGet] public async Task<IActionResult> ShelfLabels(int? priceListId)
		{
			ViewBag.PriceLists = await _context.PriceLists.AsNoTracking().Where(p => p.CompanyID == co && p.IsActive)
				.OrderBy(p => p.Name).Select(p => new InvIdNameOption { ID = p.ID, Name = p.Name }).ToListAsync();
			ViewBag.SelectedList = priceListId;
			ViewBag.Cards = priceListId.HasValue ? await BuildLabelCardsAsync(priceListId.Value) : new List<ShelfLabelCard>();
			return View();
		}

		// Build the printable cards for a price list's Fixed lines: name · price (currency dp) · unit · EAN-13 SVG
		// (or the digits as text if the barcode is not a valid EAN-13) · scale code as text for weighted items.
		private async Task<List<ShelfLabelCard>> BuildLabelCardsAsync(int priceListId)
		{
			var pl = await _context.PriceLists.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyID == co && p.ID == priceListId);
			if (pl == null) return new();
			int functional = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			int dp = await _rounding.DecimalsAsync(co, pl.CurrencyId ?? functional);
			string fmt = dp > 0 ? "0." + new string('0', dp) : "0";
			var lines = await (from l in _context.PriceListLines.AsNoTracking()
							   join i in _context.Items.AsNoTracking() on l.ItemId equals i.ID
							   where l.PriceListId == priceListId && i.CompanyID == co && l.PricingMode == "Fixed" && l.UnitPrice != null
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
			var (rows, total) = await _pricing.SearchPromotionsAsync(co, q, active, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_PromotionRows", rows);
		}

		[HttpGet] public async Task<IActionResult> PromotionEditor(int? id)
		{
			Models.Context.Inventory.Promotion model = (id.HasValue && id.Value > 0
				? await _pricing.GetPromotionAsync(co, id.Value) : null)
				?? new Models.Context.Inventory.Promotion { IsActive = true, DiscountType = "Percent", MinQty = 1 };
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			ViewBag.Categories = await _context.ItemCategories.AsNoTracking().Where(c => c.CompanyID == co).OrderBy(c => c.Name).ToListAsync();
			ViewBag.Customers = await _context.Customers.AsNoTracking().Where(c => c.CompanyID == co).OrderBy(c => c.Name).ToListAsync();
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
			var (ok, err, warning, newId) = await _pricing.SavePromotionAsync(co, dto, User?.Identity?.Name);
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
			var ok = await _pricing.DeletePromotionAsync(co, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Promotion deleted"].Value : L["Delete failed"].Value;
			return RedirectToAction(nameof(Promotions));
		}

		// price lookup for the sales forms (P3-5d)
		[HttpGet] public async Task<IActionResult> PriceLookup(int itemId, int? customerId, int? currencyId, decimal qty = 1, DateTime? date = null)
		{
			string? segment = null;
			if (customerId.HasValue && customerId.Value > 0)
				segment = await _context.Customers.AsNoTracking().Where(c => c.ID == customerId.Value).Select(c => c.Segment).FirstOrDefaultAsync();
			var r = await _pricing.GetPriceAsync(co, itemId, customerId, segment, currencyId, qty, date ?? DateTime.UtcNow);
			return Json(new { unitPrice = r.UnitPrice, discountPercent = r.DiscountPercent, source = r.Source, currencyId = r.CurrencyId, listId = r.PriceListId, listName = r.PriceListName, listNameEn = r.PriceListNameEn, promotionId = r.PromotionId, promotionName = r.PromotionName, promotionNameEn = r.PromotionNameEn });
		}

		[HttpGet] public async Task<IActionResult> StockBalancesData(string? q, int? warehouseId, bool onlyInStock = false, int page = 1, int pageSize = 25)
		{
			var (rows, total, grandValue) = await _stock.SearchBalancesAsync(co, q, warehouseId, onlyInStock, page, pageSize);
			var pages = (int)Math.Ceiling(total / (double)(pageSize < 1 ? 25 : pageSize));
			Response.Headers["X-Total"] = total.ToString();
			Response.Headers["X-Page"] = (page < 1 ? 1 : page).ToString();
			Response.Headers["X-Pages"] = Math.Max(1, pages).ToString();
			Response.Headers["X-GrandValue"] = grandValue.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
			return PartialView("_BalanceRows", rows);
		}

		[HttpGet] public async Task<IActionResult> StockBalancesExport(string? q, int? warehouseId, bool onlyInStock = false)
		{
			var (rows, _, _) = await _stock.SearchBalancesAsync(co, q, warehouseId, onlyInStock, 1, 100000);
			var headers = new[] { L["Item code"].Value, L["Item"].Value, L["Warehouse"].Value, L["Balance"].Value, L["Average cost"].Value, L["Value"].Value };
			var data = rows.Select(b => (IReadOnlyList<object?>)new object?[] { b.ItemCode, b.ItemName, b.WarehouseCode, b.QtyOnHand, b.AvgCost, b.TotalValue });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Stock balances"].Value, headers, data, L["Stock balances — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "stock-balances.xlsx");
		}

		[HttpGet] public async Task<IActionResult> StockMovementsExport(string? q, int? warehouseId)
		{
			var src = _context.StockMovements.AsNoTracking().Where(m => m.CompanyID == co);
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
			var q0 = from p in _context.PurchaseOrders.AsNoTracking().Where(p => p.CompanyID == co)
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
			var q0 = from g in _context.GoodsReceipts.AsNoTracking().Where(g => g.CompanyID == co)
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
			var q0 = from p in _context.SalesOrders.AsNoTracking().Where(p => p.CompanyID == co)
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
			var q0 = from p in _context.Quotations.AsNoTracking().Where(p => p.CompanyID == co)
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
			var q0 = from g in _context.DeliveryNotes.AsNoTracking().Where(g => g.CompanyID == co)
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
			var (rows, _) = await _pricing.SearchAsync(co, q, active, 1, 100000);
			var headers = new[] { L["Code"].Value, L["Name"].Value, L["Segment"].Value, L["Priority"].Value, L["Line count"].Value, L["Valid from"].Value, L["Valid to"].Value, L["Status"].Value };
			var data = rows.Select(p => (IReadOnlyList<object?>)new object?[] { p.Code, p.Name, p.Segment, p.Priority, p.LineCount, p.ValidFrom, p.ValidTo, p.IsActive ? L["Active (list)"].Value : L["Inactive (list)"].Value });
			return File(CrossBuy.BL.ExcelExporter.Build(L["Price lists"].Value, headers, data, L["Price lists — CrossBuy"].Value), CrossBuy.BL.ExcelExporter.ContentType, "price-lists.xlsx");
		}

		[HttpGet] public async Task<IActionResult> ItemLedger(int itemId, int? warehouseId)
		{
			var item = await _items.GetItemAsync(co, itemId);
			if (item == null) { TempData["InvErr"] = L["Item not found"].Value; return RedirectToAction(nameof(StockBalances)); }
			ViewBag.Item = item;
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.FilterWarehouseId = warehouseId;
			decimal q, v, a;
			if (warehouseId.HasValue) { (q, v, a) = await _stock.GetBalanceAsync(co, itemId, warehouseId.Value); }
			else
			{
				var bals = await _context.StockBalances.AsNoTracking().Where(b => b.CompanyID == co && b.ItemId == itemId).ToListAsync();
				q = bals.Sum(b => b.QtyOnHand); v = bals.Sum(b => b.TotalValue); a = q != 0 ? Math.Round(v / q, 2, MidpointRounding.AwayFromZero) : 0m;
			}
			ViewBag.BalQty = q; ViewBag.BalValue = v; ViewBag.BalAvg = a;
			return View(await _stock.GetMovementsAsync(co, itemId, warehouseId));
		}

		// JSON: look up an item by its barcode (item barcode OR a per-unit barcode) for the scan popup
		[HttpGet] public async Task<IActionResult> ScanBarcode(string barcode)
		{
			barcode = (barcode ?? "").Trim();
			if (barcode.Length == 0) return Json(new { ok = false });
			var c = co;
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
			var item = await _items.GetItemAsync(co, itemId);
			if (item == null) return Json(new { ok = false, error = L["Item not found"].Value });
			var comps = await _items.GetItemComponentsAsync(itemId);
			var itemIds = comps.Select(c => c.ComponentItemId).Distinct().ToList();
			var items = await _context.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).ToListAsync();
			var units = await _items.GetUnitsAsync(co);
			var rows = new List<object>();
			foreach (var c in comps)
			{
				var ci = items.FirstOrDefault(i => i.ID == c.ComponentItemId);
				decimal avail = 0;
				if (warehouseId != null) { var (q, _, _) = await _stock.GetBalanceAsync(co, c.ComponentItemId, warehouseId.Value); avail = q; }
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
				.Where(i => i.CompanyID == co && i.IsActive && i.IsComposite && i.CompositeType == "Assembly")
				.OrderBy(i => i.ItemCode).ToListAsync();
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> PostAssembly(int assemblyItemId, int warehouseId, decimal qty, DateTime? date, bool disassemble)
		{
			var (ok, err, _) = await _stock.AssembleAsync(co, assemblyItemId, warehouseId, qty, date ?? DateTime.UtcNow, disassemble, null);
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
			var (rows, total) = await _manuf.SearchAsync(co, q, status, page, pageSize);
			Response.Headers["X-Total"] = total.ToString(); Response.Headers["X-Page"] = page.ToString();
			Response.Headers["X-Pages"] = ((int)Math.Ceiling(total / (double)(pageSize <= 0 ? 25 : pageSize))).ToString();
			return PartialView("_WorkOrderRows", rows);
		}

		[HttpGet][InvPerm("read")] public async Task<IActionResult> WorkOrderItemPickData(string? term)
		{
			var rows = await _manuf.ManufacturableItemsAsync(co, term);
			return Json(new { results = rows.Select(r => new { id = r.id, text = r.text }) });
		}

		[HttpGet][InvPerm("read")] public async Task<IActionResult> NewWorkOrder()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			// UI gating must agree with the server: the create form is only usable with "doc".
			ViewBag.CanDoc = await _access.CanAsync("doc");
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> CreateWorkOrder(int itemId, decimal qty, int warehouseId, DateTime? plannedStart, DateTime? plannedEnd, decimal laborCost, decimal overheadCost, string? notes)
		{
			var (ok, err, id) = await _manuf.CreateAsync(co, itemId, qty, warehouseId, plannedStart, plannedEnd, laborCost, overheadCost, notes, User?.Identity?.Name);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewWorkOrder)); }
			TempData["InvMsg"] = L["Work order created"].Value;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		[HttpGet][InvPerm("read")] public async Task<IActionResult> WorkOrderDetails(int id)
		{
			var wo = await _manuf.GetAsync(co, id);
			if (wo == null) { TempData["InvErr"] = L["Work order not found"].Value; return RedirectToAction(nameof(WorkOrders)); }
			// Stage 0: every lifecycle button on this screen posts to an [InvPerm("doc")] action, so the buttons
			// are shown only when the same permission holds. The server remains the authority — hiding a button
			// is never the control.
			ViewBag.CanDoc = await _access.CanAsync("doc");
			ViewBag.Components = await _manuf.GetComponentsAsync(co, id);
			// BOTH NAME COLUMNS, resolved by UI language. These three reads took the Arabic column only,
			// which is why an English work order said "فرع 2" for its warehouse. Written as a translatable
			// conditional rather than a helper call so the choice still happens in SQL.
			bool woAr = System.Globalization.CultureInfo.CurrentUICulture
				.TwoLetterISOLanguageName.Equals("ar", System.StringComparison.OrdinalIgnoreCase);

			ViewBag.ItemName = await _context.Items.AsNoTracking().Where(i => i.ID == wo.ItemId)
				.Select(i => i.ItemCode + " — " + (woAr || i.NameEn == null || i.NameEn == "" ? i.Name : i.NameEn))
				.FirstOrDefaultAsync();
			ViewBag.WarehouseName = await _context.Warehouses.AsNoTracking().Where(w => w.ID == wo.WarehouseId)
				.Select(w => woAr || w.NameEn == "" ? w.Name : w.NameEn)
				.FirstOrDefaultAsync();
			var compIds = (ViewBag.Components as List<CrossBuy.Models.Context.Inventory.ManufWorkOrderComponent>)!.Select(c => c.ItemId).ToList();
			ViewBag.CompNames = await _context.Items.AsNoTracking().Where(i => compIds.Contains(i.ID))
				.ToDictionaryAsync(i => i.ID,
					i => i.ItemCode + " — " + (woAr || i.NameEn == null || i.NameEn == "" ? i.Name : i.NameEn));
			// بند3: labor lines + pickers
			var labor = await _manuf.GetLaborAsync(co, id);
			ViewBag.Labor = labor;
			var empIds = labor.Where(l => l.EmployeeId != null).Select(l => l.EmployeeId!.Value).Distinct().ToList();
			ViewBag.LaborEmpNames = await _context.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID))
				.Select(e => new { e.ID, e.FullName, e.FullNameEn })
				.ToDictionaryAsync(e => e.ID, e => CrossBuy.BL.EmployeeNames.Of(e.FullName, e.FullNameEn));
			// NOTE: return full public entities (not anonymous types) — runtime-compiled Razor views cannot
			// access members of anonymous types declared in the controller assembly (RuntimeBinderException).
			ViewBag.Employees = await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == co && e.IsActive).OrderByDisplayName().ToListAsync();
			ViewBag.WhtCodes = await _context.TaxCodes.AsNoTracking().Where(t => t.CompanyID == co && t.Kind == "WHT" && t.IsActive).OrderBy(t => t.Name).ToListAsync();
			ViewBag.CashAccounts = await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == co && (a.Code == "110101" || a.Code.StartsWith("2101")) && a.IsPostable).OrderBy(a => a.Code).ToListAsync();
			// MC (بند ب): currencies for external-labor foreign entry
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			return View(wo);
		}

		// ---- بند3: work-order labor ----
		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> AddWorkOrderLabor(int workOrderId, string sourceType, int? employeeId, string? workerName, decimal hours, decimal? ratePerHour, int? whtCodeId, int? externalCreditAccountId, int? currencyId, decimal? exchangeRate, string? workerNameEn = null)
		{
			// picking a non-functional currency requires the currency-override permission (same rule as sales invoices)
			var functional = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			if (currencyId.HasValue && currencyId.Value != functional && !await _accAccess.CanAsync("currency-override"))
			{ TempData["InvErr"] = L["You do not have permission to use a foreign currency (currency-override)"].Value; return RedirectToAction(nameof(WorkOrderDetails), new { id = workOrderId }); }
			var (ok, err, _) = await _manuf.AddLaborAsync(co, workOrderId, sourceType, employeeId, workerName, hours, ratePerHour, whtCodeId, externalCreditAccountId, currencyId, exchangeRate, DateTime.UtcNow, User?.Identity?.Name, workerNameEn);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Labor charged to the work order (WIP)"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id = workOrderId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> RemoveWorkOrderLabor(int id, int workOrderId)
		{
			var (ok, err) = await _manuf.RemoveLaborAsync(co, id, DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Labor line removed and its entry reversed"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id = workOrderId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> SaveWorkOrder(int id, decimal qty, DateTime? plannedStart, DateTime? plannedEnd, decimal laborCost, decimal overheadCost, string? notes)
		{
			var (ok, err) = await _manuf.SaveHeaderAsync(co, id, qty, plannedStart, plannedEnd, laborCost, overheadCost, notes);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work order saved"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> ReleaseWorkOrder(int id, DateTime? date)
		{
			var (ok, err) = await _manuf.ReleaseAsync(co, id, date ?? DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work order released: materials issued to WIP"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> CancelWorkOrder(int id, DateTime? date)
		{
			var (ok, err) = await _manuf.CancelAsync(co, id, date ?? DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work order cancelled (issued materials returned if any)"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> CompleteWorkOrder(int id, DateTime? date)
		{
			var (ok, err, _) = await _manuf.CompleteAsync(co, id, date ?? DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work order completed: materials consumed, item produced, journal entry posted"].Value : err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		// بند5: partial production at standard cost (Release first). finalize=true closes the order and books the variance to 520109.
		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> ProducePartial(int id, decimal qty, bool finalize, DateTime? date)
		{
			var (ok, err, produced) = await _manuf.ProducePartialAsync(co, id, qty, finalize, date ?? DateTime.UtcNow, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok
				? (finalize ? string.Format(L["Produced {0} and closed the order (variance posted to account 520109)"].Value, produced) : string.Format(L["Produced {0} partially (WIP carries the remainder)"].Value, produced))
				: err;
			return RedirectToAction(nameof(WorkOrderDetails), new { id });
		}

		// ---- 4-2: work centers ----
		[HttpGet] public async Task<IActionResult> WorkCenters()
		{
			ViewBag.WorkCenters = await _manuf.GetWorkCentersAsync(co);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> SaveWorkCenter(int id, string? code, string name, string? nameEn, decimal costPerHour, decimal overheadPerHour, bool isActive = true)
		{
			var (ok, err) = await _manuf.SaveWorkCenterAsync(co, new CrossBuy.Models.Context.Inventory.ManufWorkCenter
			{ ID = id, Code = code, Name = name, NameEn = nameEn, CostPerHour = costPerHour, OverheadPerHour = overheadPerHour, IsActive = isActive });
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Work center saved"].Value : err;
			return RedirectToAction(nameof(WorkCenters));
		}

		// ---- 4-2: routing per item ----
		[HttpGet] public async Task<IActionResult> Routing(int itemId)
		{
			var item = await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.CompanyID == co && i.ID == itemId);
			if (item == null) { TempData["InvErr"] = L["Item not found"].Value; return RedirectToAction(nameof(WorkOrders)); }
			ViewBag.Item = item;
			ViewBag.Ops = await _manuf.GetRoutingAsync(co, itemId);
			ViewBag.WorkCenters = await _manuf.WorkCentersForPickAsync(co);
			var (labor, overhead) = await _manuf.ComputeRoutingCostAsync(co, itemId, 1);
			ViewBag.UnitLabor = labor; ViewBag.UnitOverhead = overhead;
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> SaveRoutingOp(int id, int itemId, int seq, int workCenterId, string? operationName, decimal setupMins, decimal runMinsPerUnit)
		{
			var (ok, err) = await _manuf.SaveRoutingOpAsync(co, new CrossBuy.Models.Context.Inventory.ManufRoutingOp
			{ ID = id, ItemId = itemId, Seq = seq, WorkCenterId = workCenterId, OperationName = operationName, SetupMins = setupMins, RunMinsPerUnit = runMinsPerUnit });
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Operation saved"].Value : err;
			return RedirectToAction(nameof(Routing), new { itemId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> DeleteRoutingOp(int id, int itemId)
		{
			var (ok, err) = await _manuf.DeleteRoutingOpAsync(co, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Operation deleted"].Value : err;
			return RedirectToAction(nameof(Routing), new { itemId });
		}

		// ---- 4-3: production planning (MRP-lite) ----
		[HttpGet] public async Task<IActionResult> ProductionPlanning()
		{
			ViewBag.Plans = await _manuf.GetPlansAsync(co);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> CreatePlan(string name, string? nameEn, DateTime? planDate)
		{
			var (ok, err, id) = await _manuf.CreatePlanAsync(co, name, nameEn, planDate, User?.Identity?.Name);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(ProductionPlanning)); }
			return RedirectToAction(nameof(PlanDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> DeletePlan(int id)
		{
			var (ok, err) = await _manuf.DeletePlanAsync(co, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Plan deleted"].Value : err;
			return RedirectToAction(nameof(ProductionPlanning));
		}

		[HttpGet] public async Task<IActionResult> PlanDetails(int id)
		{
			var plan = await _manuf.GetPlanAsync(co, id);
			if (plan == null) { TempData["InvErr"] = L["Plan not found"].Value; return RedirectToAction(nameof(ProductionPlanning)); }
			ViewBag.Plan = plan;
			var demands = await _manuf.GetPlanDemandsAsync(co, id);
			ViewBag.Demands = demands;
			var demandIds = demands.Select(d => d.ItemId).ToList();
			ViewBag.DemandItemNames = await _context.Items.AsNoTracking().Where(i => i.CompanyID == co && demandIds.Contains(i.ID))
				.ToDictionaryAsync(i => i.ID, i => i.ItemCode + " — " + i.Name);
			ViewBag.Mrp = await _manuf.RunMrpAsync(co, id);
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> AddPlanDemand(int planId, int itemId, decimal qty, DateTime? dueDate)
		{
			var (ok, err) = await _manuf.AddDemandAsync(co, planId, itemId, qty, dueDate);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Demand added"].Value : err;
			return RedirectToAction(nameof(PlanDetails), new { id = planId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> RemovePlanDemand(int id, int planId)
		{
			var (ok, err) = await _manuf.RemoveDemandAsync(co, id);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Demand removed"].Value : err;
			return RedirectToAction(nameof(PlanDetails), new { id = planId });
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
		public async Task<IActionResult> GeneratePlanWorkOrders(int planId, int warehouseId)
		{
			var (ok, err, created) = await _manuf.GeneratePlanWorkOrdersAsync(co, planId, warehouseId, User?.Identity?.Name);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Created {0} work orders from the plan"].Value, created) : err;
			return RedirectToAction(nameof(PlanDetails), new { id = planId });
		}

		// ---- 4-4: manufacturing reports ----
		[HttpGet] public async Task<IActionResult> ManufReports(DateTime? from, DateTime? to)
		{
			var f = from ?? DateTime.Today.AddMonths(-1);
			var t = to ?? DateTime.Today;
			ViewBag.From = f; ViewBag.To = t;
			ViewBag.Report = await _manuf.GetManufReportAsync(co, f, t);
			return View();
		}

		// Manufacturing statistics dashboard (the system's landing screen)
		[HttpGet] public async Task<IActionResult> ManufDashboard()
		{
			var c = co;
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
			ViewBag.Vendors = await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == co).OrderBy(v => v.Name).ToListAsync();
			// line items searched on-demand via ItemPickData (no full-catalog preload)
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.Units = await _items.GetUnitsAsync(co);
		}

		[HttpGet] public IActionResult PurchaseOrders() => View();   // shell; rows via PurchaseOrdersData

		[HttpGet] public async Task<IActionResult> PurchaseOrdersData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var q0 = from p in _context.PurchaseOrders.AsNoTracking().Where(p => p.CompanyID == co)
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
			var (ok, err, _) = await _proc.CreatePurchaseOrderAsync(co, vendorId, warehouseId, orderDate, expectedDate, notes, lines, null, projectId);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewPurchaseOrder)); }
			TempData["InvMsg"] = L["Purchase order created"].Value;
			return RedirectToAction(nameof(PurchaseOrders));
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("purchase")]
		public async Task<IActionResult> ConvertPoToInvoice(int id)
		{
			var (ok, err, invId) = await _proc.ConvertToInvoiceAsync(co, id, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Purchase order converted to a purchase invoice (#{0})"].Value, invId) : err;
			return RedirectToAction(nameof(PurchaseOrders));
		}

		[HttpGet] public IActionResult GoodsReceipts() => View();   // shell; rows via GoodsReceiptsData

		[HttpGet] public async Task<IActionResult> GoodsReceiptsData(string? q, int page = 1, int pageSize = 25)
		{
			var q0 = from g in _context.GoodsReceipts.AsNoTracking().Where(g => g.CompanyID == co)
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
			ViewBag.PurchaseOrders = await _proc.GetPurchaseOrdersAsync(co);
			if (poId != null) ViewBag.FromPO = await _proc.GetPurchaseOrderAsync(co, poId.Value);
			ViewBag.Currencies = await _context.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("purchase")]
		public async Task<IActionResult> CreateGoodsReceipt(int? vendorId, int warehouseId, int? poId, DateTime receiptDate, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate)
		{
			List<ReceiptLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<ReceiptLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			// currency-override guard (accounting RBAC): a foreign-currency receipt needs the permission
			var functional = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			if (currencyId.HasValue && currencyId.Value != functional && !await _accAccess.CanAsync("currency-override"))
			{ TempData["InvErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(NewGoodsReceipt), new { poId }); }
			var (ok, err, _) = await _proc.CreateReceiptAsync(co, vendorId, warehouseId, poId, receiptDate, notes, lines, null, currencyId, exchangeRate);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewGoodsReceipt), new { poId }); }
			TempData["InvMsg"] = L["Goods receipt posted; stock updated"].Value;
			return RedirectToAction(nameof(GoodsReceipts));
		}

		// ================= Phase I4: Sales (Sales Order + Delivery) =================
		private async Task PopulateSellingListsAsync()
		{
			ViewBag.Customers = await _context.Customers.AsNoTracking().Where(v => v.CompanyID == co).OrderBy(v => v.Name).ToListAsync();
			// line items searched on-demand via ItemPickData (no full-catalog preload)
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.Units = await _items.GetUnitsAsync(co);
		}

		[HttpGet] public IActionResult SalesOrders() => View();   // shell; rows via SalesOrdersData

		[HttpGet] public async Task<IActionResult> SalesOrdersData(string? q, string? status, int page = 1, int pageSize = 25)
		{
			var q0 = from p in _context.SalesOrders.AsNoTracking().Where(p => p.CompanyID == co)
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
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CreateSalesOrder(int customerId, int? warehouseId, DateTime orderDate, DateTime? expectedDate, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate, int? projectId)
		{
			List<SoLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<SoLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var functional = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			if (currencyId.HasValue && currencyId.Value != functional && !await _accAccess.CanAsync("currency-override"))
			{ TempData["InvErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(NewSalesOrder)); }
			// Pricing 2A — gross-margin floor: Block rejects the doc; Warn proceeds and notifies.
			var (mBlock, mWarn) = await CheckLineMarginsAsync(lines.Select(l => (l.ItemId, l.Qty, l.UnitPrice, l.DiscountAmount)), currencyId, exchangeRate, orderDate);
			if (mBlock != null) { TempData["InvErr"] = mBlock; return RedirectToAction(nameof(NewSalesOrder)); }
			// Pricing 2D — discount approval ceiling.
			var (dBlock, dWarn) = await _pricing.EvaluateLineDiscountsAsync(co, lines.Select(l => (l.Qty, l.UnitPrice, l.DiscountAmount)), await _access.CanAsync("manage"));
			if (dBlock != null) { TempData["InvErr"] = dBlock; return RedirectToAction(nameof(NewSalesOrder)); }
			var (ok, err, _) = await _sell.CreateSalesOrderAsync(co, customerId, warehouseId, orderDate, expectedDate, notes, lines, null, currencyId, exchangeRate, projectId);
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
				var mc = await _pricing.CheckMarginAsync(co, l.ItemId.Value, net, currencyId, exchangeRate, asOf);
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
			var q0 = from p in _context.Quotations.AsNoTracking().Where(p => p.CompanyID == co)
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
			ViewBag.FunctionalCurrencyId = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CreateQuotation(int customerId, int? warehouseId, DateTime quoteDate, DateTime? validUntil, string? notes, string? linesJson, int? currencyId, decimal? exchangeRate)
		{
			List<SoLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<SoLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var functional = await _currency.GetFunctionalCurrencyIdAsync(co, null);
			if (currencyId.HasValue && currencyId.Value != functional && !await _accAccess.CanAsync("currency-override"))
			{ TempData["InvErr"] = L["You do not have permission to issue a document in a currency other than the branch currency"].Value; return RedirectToAction(nameof(NewQuotation)); }
			var (ok, err, _) = await _sell.CreateQuotationAsync(co, customerId, warehouseId, quoteDate, validUntil, notes, lines, null, currencyId, exchangeRate);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewQuotation)); }
			TempData["InvMsg"] = L["Quotation created"].Value;
			return RedirectToAction(nameof(Quotations));
		}

		[HttpGet] public async Task<IActionResult> QuotationDetails(int id)
		{
			var q = await _sell.GetQuotationAsync(co, id);
			if (q == null) { TempData["InvErr"] = L["Quotation not found"].Value; return RedirectToAction(nameof(Quotations)); }
			ViewBag.Customer = await _context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.ID == q.CustomerId);
			var itemIds = q.Lines.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).ToList();
			ViewBag.Items = await _context.Items.AsNoTracking().Where(i => itemIds.Contains(i.ID)).ToDictionaryAsync(i => i.ID, i => i.ItemCode);
			return View(q);
		}

		// =============================================================================================
		// QUOTATION BUSINESS CONVERSATION — the authoritative store, not the legacy one.
		//
		// A Quotation used to carry its discussion in DocComments, reached through _DocTimeline and
		// /Comments/*. That was a SECOND comment store for an entity the Communication platform already
		// declares: EntityRegistry registers Quotation with SupportsComments = true, Module = "Inventory"
		// and PermissionScope = ScopeInventory. Two stores for one document is how a conversation ends up
		// half in each, which is the defect already removed from the invoice screens.
		//
		// These endpoints live in INVENTORY because the document does. Routing a quotation conversation
		// through AccountingController would have reused the existing endpoints for free and put an
		// Inventory document under the Accounting module policy — the module that owns the record must be
		// the module that authorizes talking about it.
		//
		// NOTHING ABOUT COMMENTS IS IMPLEMENTED HERE. Thread identity, body policy, mention parsing, the
		// audit row and any notification fan-out all belong to the platform; this is a gate plus a
		// projection. No new table, no quotation-specific comment service, no second notification channel.
		// =============================================================================================

		/// Resolved outcome of the gate. Ok == false deliberately carries no detail: a refusal must not
		/// say whether the quotation is absent, belongs to another company, or is simply not permitted.
		/// The document families this module answers conversation traffic for. One map, so adding the
		/// tenth document is a line here and a line in the row check below — not three more actions.
		private static readonly Dictionary<string, string> InventoryConversationFamilies =
			new(StringComparer.Ordinal)
			{
				["Quotation"] = CrossBuy.BL.Platform.EntityRegistry.Quotation,
				["PurchaseOrder"] = CrossBuy.BL.Platform.EntityRegistry.PurchaseOrder,
				["GoodsReceipt"] = CrossBuy.BL.Platform.EntityRegistry.GoodsReceipt,
				["SalesOrder"] = CrossBuy.BL.Platform.EntityRegistry.SalesOrder,
				["DeliveryNote"] = CrossBuy.BL.Platform.EntityRegistry.DeliveryNote,
				["StockTransfer"] = CrossBuy.BL.Platform.EntityRegistry.StockTransfer,
				["StockCount"] = CrossBuy.BL.Platform.EntityRegistry.StockCount,
				["StockWriteOff"] = CrossBuy.BL.Platform.EntityRegistry.StockWriteOff,
				["LandedCost"] = CrossBuy.BL.Platform.EntityRegistry.LandedCost,
			};

		private sealed class QuotationConversationGate
		{
			public bool Ok;
			public CrossBuy.Models.Platform.BusinessContext? Context;

			/// The canonical code this gate resolved to — the endpoints thread it into the entity
			/// reference so the thread is attached to the right document family.
			public string EntityCode = CrossBuy.BL.Platform.EntityRegistry.Quotation;
		}

		private CrossBuy.BL.Platform.IBusinessContextAccessor? QuotationBusinessContexts =>
			HttpContext.RequestServices.GetService(typeof(CrossBuy.BL.Platform.IBusinessContextAccessor))
				as CrossBuy.BL.Platform.IBusinessContextAccessor;

		/// One gate for both endpoints, so read and write cannot drift apart on authorization.
		/// action is the module ability being claimed: read to load the conversation, doc to add to it.
		private async Task<QuotationConversationGate> QuotationConversationGateAsync(
			int id, string action, CancellationToken ct, string? entity = null)
		{
			if (id <= 0) return new QuotationConversationGate();

			// An unknown or absent code resolves to the quotation, which is what this endpoint answered
			// before the other eight documents joined it. An UNKNOWN one refuses: a caller naming a
			// family this module does not serve must not be silently served a different document.
			string code = CrossBuy.BL.Platform.EntityRegistry.Quotation;
			if (!string.IsNullOrWhiteSpace(entity)
				&& !InventoryConversationFamilies.TryGetValue(entity!, out code!))
				return new QuotationConversationGate();

			// 1 — COMPANY IS RESOLVED, never assumed. An unresolved scope refuses before any row is read,
			// and co is not consulted here: the tenant authority for this path is the BusinessContext.
			var scope = await _company.ResolveAsync();
			if (!scope.Ok) return new QuotationConversationGate();

			var ctx = QuotationBusinessContexts == null ? null : await QuotationBusinessContexts.TryGetCurrentAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0 || ctx.EmployeeId is not > 0) return new QuotationConversationGate();

			// 2 — THE MODULE OWN AUTHORITY decides, with no new vocabulary invented for comments.
			// read and doc are the abilities Inventory already publishes; a comment is not a new kind of
			// permission, it is the existing ability applied to the existing document.
			//
			// The session-free overload lives on the CONCRETE service — IInventoryAccessService publishes
			// only CanAsync(action), which reads ambient session state. Taking the concrete type is the
			// same precedent AccountingController set for exactly this call. If the service cannot be
			// resolved the gate REFUSES rather than falling through to allow.
			var inventory = HttpContext.RequestServices.GetService(typeof(InventoryAccessService))
				as InventoryAccessService;
			if (inventory == null) return new QuotationConversationGate();

			if (!await inventory.CanAsync(ctx, action,
					CrossBuy.Models.Platform.PermissionTarget.ForEntity(code, id), ct))
				return new QuotationConversationGate();

			// 3 — THE ROW, in the caller own company. The company predicate is IN THE QUERY, so a
			// quotation belonging to another company is never materialised — it is not loaded and then
			// refused, which is what keeps foreign and absent indistinguishable to the caller.
			// EACH FAMILY CHECKS ITS OWN TABLE. A family in the map with no case here would be a
			// document nobody verified exists, so the default is REFUSE rather than fall through.
			bool exists = code switch
			{
				var c when c == CrossBuy.BL.Platform.EntityRegistry.Quotation =>
					await _context.Quotations.AsNoTracking().AnyAsync(d => d.ID == id && d.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.PurchaseOrder =>
					await _context.PurchaseOrders.AsNoTracking().AnyAsync(d => d.ID == id && d.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.GoodsReceipt =>
					await _context.GoodsReceipts.AsNoTracking().AnyAsync(d => d.ID == id && d.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.SalesOrder =>
					await _context.SalesOrders.AsNoTracking().AnyAsync(d => d.ID == id && d.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.DeliveryNote =>
					await _context.DeliveryNotes.AsNoTracking().AnyAsync(d => d.ID == id && d.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.StockTransfer =>
					await _context.StockTransfers.AsNoTracking().AnyAsync(d => d.ID == id && d.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.StockCount =>
					await _context.StockCounts.AsNoTracking().AnyAsync(d => d.ID == id && d.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.StockWriteOff =>
					await _context.StockWriteOffs.AsNoTracking().AnyAsync(d => d.ID == id && d.CompanyID == ctx.CompanyId, ct),
				var c when c == CrossBuy.BL.Platform.EntityRegistry.LandedCost =>
					await _context.LandedCosts.AsNoTracking().AnyAsync(d => d.ID == id && d.CompanyID == ctx.CompanyId, ct),
				_ => false,
			};
			if (!exists) return new QuotationConversationGate();

			return new QuotationConversationGate { Ok = true, Context = ctx, EntityCode = code };
		}

		/// The optional platform, asked for and never required. Both services come from ONE registration,
		/// so a half-present pair is treated as absent: a conversation that can list but not add is a
		/// worse answer than an honest 503.
		private (CrossBuy.BL.Communication.ICommThreadService Threads,
		         CrossBuy.BL.Communication.ICommCommentService Comments,
		         CrossBuy.BL.Communication.ICommEntitySurface Surface,
		         CrossBuy.BL.Communication.ICommReactionService? Reactions)? TryQuotationConversation()
		{
			var sp = HttpContext.RequestServices;
			var threads = sp.GetService(typeof(CrossBuy.BL.Communication.ICommThreadService))
				as CrossBuy.BL.Communication.ICommThreadService;
			var comments = sp.GetService(typeof(CrossBuy.BL.Communication.ICommCommentService))
				as CrossBuy.BL.Communication.ICommCommentService;
			var surface = sp.GetService(typeof(CrossBuy.BL.Communication.ICommEntitySurface))
				as CrossBuy.BL.Communication.ICommEntitySurface;

			// Reactions are optional where the other three are not: without them the panel simply
			// offers no emoji, which is a smaller loss than a thread that lists but cannot be added to.
			var reactions = sp.GetService(typeof(CrossBuy.BL.Communication.ICommReactionService))
				as CrossBuy.BL.Communication.ICommReactionService;

			return threads is null || comments is null || surface is null
				? null : (threads, comments, surface, reactions);
		}

		/// A machine CODE, not a sentence: the browser must be able to tell "the platform is switched off"
		/// apart from "you may not see this" and from "it broke". A translated sentence cannot carry that.
		public const string QuotationConversationUnavailableCode = "communication_unavailable";

		private IActionResult QuotationConversationUnavailable(string code = QuotationConversationUnavailableCode) =>
			StatusCode(StatusCodes.Status503ServiceUnavailable, new
			{
				ok = false,
				unavailable = true,
				code,
				error = L["Conversations are unavailable in this environment"].Value,
			});

		// GET /Inventory/QuotationConversation?id=123
		[SessionValidation][HttpGet]
		public async Task<IActionResult> QuotationConversation(int id, string? entity = null, CancellationToken ct = default)
		{
			var gate = await QuotationConversationGateAsync(id, "read", ct, entity);
			if (!gate.Ok) return NotFound(new { ok = false, code = "not_found" });

			var comm = TryQuotationConversation();
			if (comm == null) return QuotationConversationUnavailable();

			var reference = new CrossBuy.Models.Communication.CommEntityRef(gate.EntityCode, id);

			// The registry decides whether this family carries comments — not this controller.
			var allowed = await comm.Value.Surface.EvaluateAsync(
				reference, CrossBuy.BL.Communication.CommCapabilities.Comments);
			if (!allowed.Allowed) return QuotationConversationUnavailable("capability_disabled");

			var thread = await comm.Value.Threads.GetOrCreateAsync(gate.Context!,
				new CrossBuy.Models.Communication.CommThreadRequest { Entity = reference }, ct);

			var page = await comm.Value.Comments.ListAsync(gate.Context!, thread.Id, null, ct);
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			// THE AUTHORS' PHOTOGRAPHS, resolved once for the page rather than per comment. Scoped to
			// this company by the resolver: an author from outside it comes back with no photo and the
			// panel falls back to initials, so a thread cannot be used to read staff pictures out of a
			// company the caller cannot see.
			var avatars = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(
				_context, gate.Context!, page.Items.Select(c => c.Author.EmployeeId), ct);

			return Json(new
			{
				ok = true,
				threadId = thread.Id,
				entity = new { code = gate.EntityCode, id },
				canReact = comm.Value.Reactions is not null,
				me = await CrossBuy.BL.Communication.CommPanel.MeAsync(_context, gate.Context!, isAr, ct),

				// ONE PROJECTION, shared with every other module that renders this panel.
				comments = CrossBuy.BL.Communication.CommPanel.Project(page.Items, isAr, avatars),
			});
		}

		// POST /Inventory/QuotationConversationAdd
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		[RequestSizeLimit(21_000_000)]   // CommPanel.MaxUploadBytes plus the form envelope
		public async Task<IActionResult> QuotationConversationAdd(int id, string? body,
			long? parentCommentId, IFormFile? file, string? entity = null, CancellationToken ct = default)
		{
			// doc rather than read: adding to the record discussion is a mutation of the record history,
			// so it claims the module document ability. A reader who may not change the quotation may not
			// add to its conversation either.
			//
			// The refusal is byte-identical to the read refusal, so the write path is not an existence
			// oracle either.
			var gate = await QuotationConversationGateAsync(id, "doc", ct);
			if (!gate.Ok) return NotFound(new { ok = false, code = "not_found" });

			if (string.IsNullOrWhiteSpace(body))
				return Json(new { ok = false, error = L["Write a comment"].Value });

			var comm = TryQuotationConversation();
			if (comm == null) return QuotationConversationUnavailable();

			var reference = new CrossBuy.Models.Communication.CommEntityRef(gate.EntityCode, id);

			var allowed = await comm.Value.Surface.EvaluateAsync(
				reference, CrossBuy.BL.Communication.CommCapabilities.Comments);
			if (!allowed.Allowed) return QuotationConversationUnavailable("capability_disabled");

			// NOTHING REACHES DISK BEFORE THE GATE, so a refused caller never leaves an orphan file.
			CrossBuy.Models.Communication.CommAttachmentRequest? attachment = null;
			if (file is { Length: > 0 })
			{
				var (staged, refusal) = await CrossBuy.BL.Communication.CommPanel.StageAsync(
					file, _env.WebRootPath, ct);
				if (staged is null)
					return Json(new { ok = false, code = refusal, error = AttachmentRefusalText(refusal) });
				attachment = staged;
			}

			// The platform owns body policy, mention parsing, the audit row and any notification fan-out.
			var added = await comm.Value.Comments.AddAsync(gate.Context!,
				new CrossBuy.Models.Communication.CommCommentRequest
				{
					Entity = reference,
					Body = body ?? "",
					ParentCommentId = parentCommentId is > 0 ? parentCommentId : null,
					Attachments = attachment is null ? null : new[] { attachment },
				}, ct);

			return Json(new { ok = true, id = added.CommentId, threadId = added.ThreadId });
		}

		/// One sentence per machine code, so the browser never has to compose a refusal.
		private string AttachmentRefusalText(string code) => code switch
		{
			CrossBuy.BL.Communication.CommPanel.UploadRefusal.TooLarge =>
				L["The file is larger than 20 MB"].Value,
			CrossBuy.BL.Communication.CommPanel.UploadRefusal.Type =>
				L["This kind of file cannot be attached"].Value,
			_ => L["The file could not be attached"].Value,
		};

		// POST /Inventory/QuotationConversationReact
		//
		// The same gate as the ADD, not the read: reacting is a mutation of the record's history, and
		// this module already draws that line for comments.
		[SessionValidation][HttpPost][ValidateAntiForgeryToken]
		public async Task<IActionResult> QuotationConversationReact(int id, long commentId,
			string? key, bool on, string? entity = null, CancellationToken ct = default)
		{
			var gate = await QuotationConversationGateAsync(id, "doc", ct, entity);
			if (!gate.Ok) return NotFound(new { ok = false, code = "not_found" });

			var comm = TryQuotationConversation();
			if (comm?.Reactions is null) return QuotationConversationUnavailable();
			if (commentId <= 0 || string.IsNullOrWhiteSpace(key))
				return Json(new { ok = false, error = L["The reaction could not be saved"].Value });

			try
			{
				var summary = on
					? await comm.Value.Reactions.AddAsync(gate.Context!, commentId, key, ct)
					: await comm.Value.Reactions.RemoveAsync(gate.Context!, commentId, key, ct);

				return Json(new
				{
					ok = true,
					commentId,
					reactions = summary.Where(r => r.Count > 0)
						.Select(r => new { key = r.ReactionKey, count = r.Count, mine = r.Mine }),
				});
			}
			catch (CrossBuy.Models.Communication.CommAccessDeniedException)
			{
				return NotFound(new { ok = false, code = "not_found" });
			}
			catch (CrossBuy.Models.Communication.CommValidationException)
			{
				return Json(new { ok = false, error = L["The reaction could not be saved"].Value });
			}
		}


		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> SetQuoteStatus(int id, string status)
		{
			var (ok, err) = await _sell.SetQuotationStatusAsync(co, id, status);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Quotation status updated"].Value : err;
			return RedirectToAction(nameof(QuotationDetails), new { id });
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> ConvertQuoteToOrder(int id)
		{
			var (ok, err, soId) = await _sell.ConvertQuotationToOrderAsync(co, id, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(QuotationDetails), new { id }); }
			TempData["InvMsg"] = L["Quotation converted to a sales order"].Value;
			return RedirectToAction(nameof(SalesOrderDetails), new { id = soId });
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> ConvertSoToInvoice(int id)
		{
			var (ok, err, invId) = await _sell.ConvertToInvoiceAsync(co, id, null);
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Sales order converted to a sales invoice (#{0})"].Value, invId) : err;
			return RedirectToAction(nameof(SalesOrders));
		}

		[HttpGet] public IActionResult Deliveries() => View();   // shell; rows via DeliveriesData

		[HttpGet] public async Task<IActionResult> DeliveriesData(string? q, int page = 1, int pageSize = 25)
		{
			var q0 = from g in _context.DeliveryNotes.AsNoTracking().Where(g => g.CompanyID == co)
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
			if (soId != null) ViewBag.FromSO = await _sell.GetSalesOrderAsync(co, soId.Value);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("doc")]
		public async Task<IActionResult> CreateDelivery(int? customerId, int warehouseId, int? soId, DateTime deliveryDate, string? notes, string? linesJson)
		{
			List<DeliveryLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<DeliveryLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var (ok, err, _) = await _sell.CreateDeliveryAsync(co, customerId, warehouseId, soId, deliveryDate, notes, lines, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewDelivery), new { soId }); }
			TempData["InvMsg"] = L["Delivery note posted; stock deducted and cost recorded"].Value;
			return RedirectToAction(nameof(Deliveries));
		}

		// ================= Phase I6: Stock transfers =================
		[HttpGet] public async Task<IActionResult> StockTransfers()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			return View(await _context.StockTransfers.AsNoTracking().Where(t => t.CompanyID == co).OrderByDescending(t => t.ID).ToListAsync());
		}

		[HttpGet] public async Task<IActionResult> NewTransfer()
		{
			// line items searched on-demand via ItemPickData (no full-catalog preload)
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.Units = await _items.GetUnitsAsync(co);
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
			foreach (var l in lines) { var (_, _, avg) = await _stock.GetBalanceAsync(co, l.ItemId, fromWarehouseId); trEst += l.Qty * avg; }
			if (await _approvals.RequiresApprovalAsync(trEst))
			{
				await _approvals.SubmitAsync("StockTransfer", trEst, new TransferApprovalPayload { FromWarehouseId = fromWarehouseId, ToWarehouseId = toWarehouseId, Date = transferDate, Notes = notes, Lines = lines }, _access.CurrentEmployeeId());
				TempData["InvMsg"] = L["Transfer exceeds the approval limit — sent for approval"].Value;
				return RedirectToAction(nameof(Approvals));
			}
			var (ok, err, _) = await _stock.TransferAsync(co, fromWarehouseId, toWarehouseId, transferDate, notes, lines, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewTransfer)); }
			TempData["InvMsg"] = L["Inter-warehouse transfer posted"].Value;
			return RedirectToAction(nameof(StockTransfers));
		}

		// ================= Phase I7: Stock count =================
		[HttpGet] public async Task<IActionResult> StockCounts()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			return View(await _context.StockCounts.AsNoTracking().Where(t => t.CompanyID == co).OrderByDescending(t => t.ID).ToListAsync());
		}

		[HttpGet] public async Task<IActionResult> NewCount(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				// load current book balances for the warehouse + only the items actually in this warehouse (not the whole catalog)
				var balances = await _stock.GetBalancesAsync(co, warehouseId);
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
									   where m.CompanyID == co && m.WarehouseId == wid && trackedIds.Contains(m.ItemId)
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
			foreach (var l in lines) { var (bookQ, _, avg) = await _stock.GetBalanceAsync(co, l.ItemId, warehouseId); cntEst += Math.Abs(l.CountedQty - bookQ) * avg; }
			if (await _approvals.RequiresApprovalAsync(cntEst))
			{
				await _approvals.SubmitAsync("StockCount", cntEst, new CountApprovalPayload { WarehouseId = warehouseId, CountDate = countDate, Notes = notes, Lines = lines }, _access.CurrentEmployeeId());
				TempData["InvMsg"] = L["Stock count adjustment exceeds the approval limit — sent for approval"].Value;
				return RedirectToAction(nameof(Approvals));
			}
			var (ok, err, _) = await _stock.PostCountAsync(co, warehouseId, countDate, notes, lines, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewCount), new { warehouseId }); }
			TempData["InvMsg"] = L["Stock count posted; differences adjusted"].Value;
			return RedirectToAction(nameof(StockCounts));
		}

		// ================= Write-off / damage =================
		[HttpGet] public async Task<IActionResult> WriteOffs()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.Mode = await _context.InventorySettings.AsNoTracking().Where(x => x.CompanyID == co).Select(x => x.WriteOffMode).FirstOrDefaultAsync() ?? "SeparateDocument";
			return View(await _context.StockWriteOffs.AsNoTracking().Where(t => t.CompanyID == co).OrderByDescending(t => t.ID).ToListAsync());
		}

		[HttpGet] public async Task<IActionResult> NewWriteOff(int? warehouseId)
		{
			ViewBag.Mode = await _context.InventorySettings.AsNoTracking().Where(x => x.CompanyID == co).Select(x => x.WriteOffMode).FirstOrDefaultAsync() ?? "SeparateDocument";
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				var balances = await _stock.GetBalancesAsync(co, warehouseId);
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
			foreach (var l in lines) { var (_, _, avg) = await _stock.GetBalanceAsync(co, l.ItemId, warehouseId); est += l.Qty * avg; }
			if (await _approvals.RequiresApprovalAsync(est))
			{
				await _approvals.SubmitAsync("WriteOff", est, new WriteOffApprovalPayload { WarehouseId = warehouseId, WriteOffDate = writeOffDate, Reason = reason, Notes = notes, Lines = lines }, _access.CurrentEmployeeId());
				TempData["InvMsg"] = L["Write-off exceeds the approval limit — sent for approval"].Value;
				return RedirectToAction(nameof(Approvals));
			}
			var (ok, err, docNo, _, mode) = await _stock.WriteOffAsync(co, warehouseId, writeOffDate, reason, notes, lines, null);
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
			return (await _warehouses.GetWarehousesAsync(co)).ToDictionary(w => w.ID, w => w.Code + " — " + (ar ? w.Name : (string.IsNullOrEmpty(w.NameEn) ? w.Name : w.NameEn)));
		}

		[HttpGet] public async Task<IActionResult> ReceiptDetails(int id)
		{
			var d = await _proc.GetReceiptAsync(co, id);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(GoodsReceipts)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var vendor = d.VendorId == null ? "—" : await _context.Vendors.AsNoTracking().Where(v => v.ID == d.VendorId).Select(v => Ar() ? v.Name : (v.NameEn ?? v.Name)).FirstOrDefaultAsync() ?? "—";
			var vm = new DocDetailVm { Title = "إذن استلام", TitleEn = "Goods receipt", DocNo = d.ReceiptNo ?? ("#" + d.ID), DateStr = Dt(d.ReceiptDate), Status = d.Status, BackAction = nameof(GoodsReceipts), BackLabel = "أذون الاستلام", BackLabelEn = "Goods receipts",
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.GoodsReceipt, DocumentId = d.ID,
				// The screen is bound to its reports in ReportScreenBindings; naming it here is all
				// the shared view needs to offer every layout that exists for this document.
				PrintScreenKey = CrossBuy.BL.Reporting.ReportScreenKeys.InventoryReceiptDetail };
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
			var d = await _sell.GetDeliveryAsync(co, id);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(Deliveries)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var cust = d.CustomerId == null ? "—" : await _context.Customers.AsNoTracking().Where(v => v.ID == d.CustomerId).Select(v => Ar() ? v.Name : (v.NameEn ?? v.Name)).FirstOrDefaultAsync() ?? "—";
			var vm = new DocDetailVm { Title = "إذن صرف", TitleEn = "Delivery note", DocNo = d.DeliveryNo ?? ("#" + d.ID), DateStr = Dt(d.DeliveryDate), Status = d.Status, BackAction = nameof(Deliveries), BackLabel = "أذون الصرف", BackLabelEn = "Deliveries",
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.DeliveryNote, DocumentId = d.ID,
				// The screen is bound to its reports in ReportScreenBindings; naming it here is all
				// the shared view needs to offer every layout that exists for this document.
				PrintScreenKey = CrossBuy.BL.Reporting.ReportScreenKeys.InventoryDeliveryDetail };
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
			var d = await _proc.GetPurchaseOrderAsync(co, id);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(PurchaseOrders)); }
			var names = await ItemNamesAsync(d.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value)); var wh = await WhNamesAsync();
			var vendor = await _context.Vendors.AsNoTracking().Where(v => v.ID == d.VendorId).Select(v => Ar() ? v.Name : (v.NameEn ?? v.Name)).FirstOrDefaultAsync() ?? "—";
			var vm = new DocDetailVm { Title = "أمر شراء", TitleEn = "Purchase order", DocNo = d.OrderNo ?? ("#" + d.ID), DateStr = Dt(d.OrderDate), Status = d.Status, BackAction = nameof(PurchaseOrders), BackLabel = "أوامر الشراء", BackLabelEn = "Purchase orders",
				// The screen is bound to its reports in ReportScreenBindings; naming it here is all the
				// shared view needs to offer every layout that exists for this document.
				PrintScreenKey = CrossBuy.BL.Reporting.ReportScreenKeys.InventoryPurchaseOrderDetail, DocumentId = d.ID,
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.PurchaseOrder };
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
			var d = await _sell.GetSalesOrderAsync(co, id);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(SalesOrders)); }
			var names = await ItemNamesAsync(d.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value)); var wh = await WhNamesAsync();
			var cust = await _context.Customers.AsNoTracking().Where(v => v.ID == d.CustomerId).Select(v => Ar() ? v.Name : (v.NameEn ?? v.Name)).FirstOrDefaultAsync() ?? "—";
			var vm = new DocDetailVm { Title = "أمر بيع", TitleEn = "Sales order", DocNo = d.OrderNo ?? ("#" + d.ID), DateStr = Dt(d.OrderDate), Status = d.Status, BackAction = nameof(SalesOrders), BackLabel = "أوامر البيع", BackLabelEn = "Sales orders",
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.SalesOrder, DocumentId = d.ID,
				// The screen is bound to its reports in ReportScreenBindings; naming it here is all
				// the shared view needs to offer every layout that exists for this document.
				PrintScreenKey = CrossBuy.BL.Reporting.ReportScreenKeys.InventorySalesOrderDetail };
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
			var d = await _context.StockTransfers.AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == co);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(StockTransfers)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var vm = new DocDetailVm { Title = "تحويل مخزني", TitleEn = "Stock transfer", DocNo = d.TransferNo ?? ("#" + d.ID), DateStr = Dt(d.TransferDate), Status = d.Status, BackAction = nameof(StockTransfers), BackLabel = "التحويلات بين المخازن", BackLabelEn = "Transfers", JournalEntryId = d.JournalEntryId,
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.StockTransfer, DocumentId = d.ID,
				// The screen is bound to its reports in ReportScreenBindings; naming it here is all
				// the shared view needs to offer every layout that exists for this document.
				PrintScreenKey = CrossBuy.BL.Reporting.ReportScreenKeys.InventoryTransferDetail };
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
			var d = await _context.StockCounts.AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == co);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(StockCounts)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var vm = new DocDetailVm { Title = "تسوية جرد", TitleEn = "Stock count", DocNo = d.CountNo ?? ("#" + d.ID), DateStr = Dt(d.CountDate), Status = d.Status, BackAction = nameof(StockCounts), BackLabel = "الجرد والتسويات", BackLabelEn = "Stock counts",
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.StockCount, DocumentId = d.ID,
				// The screen is bound to its reports in ReportScreenBindings; naming it here is all
				// the shared view needs to offer every layout that exists for this document.
				PrintScreenKey = CrossBuy.BL.Reporting.ReportScreenKeys.InventoryCountDetail };
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
			var d = await _context.StockWriteOffs.AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == co);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(WriteOffs)); }
			var names = await ItemNamesAsync(d.Lines.Select(l => l.ItemId)); var wh = await WhNamesAsync();
			var vm = new DocDetailVm { Title = "مستند إعدام", TitleEn = "Write-off", DocNo = d.WriteOffNo ?? ("#" + d.ID), DateStr = Dt(d.WriteOffDate), Status = d.Status, BackAction = nameof(WriteOffs), BackLabel = "الإعدام والتلف", BackLabelEn = "Write-offs", JournalEntryId = d.JournalEntryId, Danger = true,
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.StockWriteOff, DocumentId = d.ID,
				// The screen is bound to its reports in ReportScreenBindings; naming it here is all
				// the shared view needs to offer every layout that exists for this document.
				PrintScreenKey = CrossBuy.BL.Reporting.ReportScreenKeys.InventoryWriteOffDetail };
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
			var d = await _context.LandedCosts.AsNoTracking().Include(t => t.Charges).FirstOrDefaultAsync(t => t.ID == id && t.CompanyID == co);
			if (d == null) { TempData["InvErr"] = L["Document not found"].Value; return RedirectToAction(nameof(LandedCosts)); }
			var grNo = await _context.GoodsReceipts.AsNoTracking().Where(g => g.ID == d.GoodsReceiptId).Select(g => g.ReceiptNo).FirstOrDefaultAsync() ?? ("#" + d.GoodsReceiptId);
			var accIds = d.Charges.Select(c => c.AccountId).Distinct().ToList();
			var accs = await _context.Accounts.AsNoTracking().Where(a => accIds.Contains(a.ID)).ToDictionaryAsync(a => a.ID, a => a.Code + " — " + (Ar() ? a.Name : (string.IsNullOrEmpty(a.NameEn) ? a.Name : a.NameEn)));
			var vm = new DocDetailVm { Title = "تكلفة إضافية", TitleEn = "Landed cost", DocNo = d.LandedNo ?? ("#" + d.ID), DateStr = Dt(d.LandedDate), Status = d.Status, BackAction = nameof(LandedCosts), BackLabel = "التكاليف الإضافية", BackLabelEn = "Landed costs", JournalEntryId = d.JournalEntryId,
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.LandedCost, DocumentId = d.ID };
			vm.Header.Add(new() { Label = "إذن الاستلام", LabelEn = "Goods receipt", Value = grNo });
			vm.Header.Add(new() { Label = "طريقة التوزيع", LabelEn = "Allocation", Value = d.AllocationMethod == "Qty" ? (Ar() ? "بالكمية" : "By quantity") : (Ar() ? "بالقيمة" : "By value") });
			vm.Header.Add(new() { Label = "ملاحظات", LabelEn = "Notes", Value = d.Notes ?? "—" });
			vm.Columns = new() { new() { Label = "البيان", LabelEn = "Description" }, new() { Label = "الحساب الدائن", LabelEn = "Credit account" }, new() { Label = "المبلغ", LabelEn = "Amount", Num = true } };
			foreach (var c in d.Charges.OrderBy(x => x.LineNo))
			{
				// The charge line's own description, in the reader's language. Ar() is the same flag the
				// account name two lines above already uses; this row took the Arabic column straight.
				var chDesc = Ar() || string.IsNullOrWhiteSpace(c.DescriptionEn) ? c.Description : c.DescriptionEn;
				vm.Rows.Add(new() { chDesc ?? "—", accs.GetValueOrDefault(c.AccountId, "#" + c.AccountId), N2(c.Amount) });
			}
			vm.Totals.Add(new() { Label = "إجمالي المصاريف", LabelEn = "Total charges", Value = N2(d.TotalAmount) });
			return View("DocumentDetails", vm);
		}

		// ================= Go-Live opening balances =================
		[HttpGet] public async Task<IActionResult> OpeningBalances()
		{
			var ctl = await _opening.GetControlAsync(co);
			ViewBag.Control = ctl;
			ViewBag.Checks = await _opening.VerifyAsync(co);
			ViewBag.Log = await _opening.GetLogAsync(co);
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.Items = await _context.Items.AsNoTracking().Where(i => i.CompanyID == co && i.IsActive && !i.IsComposite && i.ItemType == "Stockable").OrderBy(i => i.ItemCode).ToListAsync();
			ViewBag.Customers = await _context.Customers.AsNoTracking().Where(c => c.CompanyID == co && c.IsActive).OrderBy(c => c.Name).ToListAsync();
			ViewBag.Vendors = await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == co && v.IsActive).OrderBy(v => v.Name).ToListAsync();
			ViewBag.Accounts = await _coa.GetFlatAsync(co, postableOnly: true);
			ViewBag.AssetCategories = await _context.AssetCategories.AsNoTracking().Where(c => c.CompanyID == co).OrderBy(c => c.Name).ToListAsync();
			ViewBag.Cutoff = (ctl.CutoffDate ?? new DateTime(DateTime.Today.Year, 1, 1)).ToString("yyyy-MM-dd");
			return View();
		}

		private DateTime CutoffOr(DateTime? d) => (d ?? new DateTime(DateTime.Today.Year, 1, 1)).Date;
		private async Task SaveCutoffAsync(DateTime cutoff)
		{
			var c = await _opening.GetControlAsync(co);
			if (c.CutoffDate == null && !c.Finalized) { var e = await _context.OpeningBalanceControls.FirstAsync(x => x.CompanyID == co); e.CutoffDate = cutoff; await _context.SaveChangesAsync(); }
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddStock(int itemId, int warehouseId, decimal qty, decimal unitCost, string? batchNo, DateTime? expiry, DateTime? cutoff, int? binLocationId)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			var (ok, err, _) = await _opening.PostStockAsync(co, cu, new List<OpeningStockLineInput> { new() { ItemId = itemId, WarehouseId = warehouseId, Qty = qty, UnitCost = unitCost, BatchNo = batchNo, Expiry = expiry, BinLocationId = binLocationId } }, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening stock entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddAr(int customerId, decimal amount, DateTime? cutoff)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			var (ok, err) = await _opening.PostArAsync(co, cu, customerId, amount, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening customer balance entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddAp(int vendorId, decimal amount, DateTime? cutoff)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			var (ok, err) = await _opening.PostApAsync(co, cu, vendorId, amount, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening vendor balance entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddAsset(string name, decimal cost, decimal salvageValue, int usefulLifeMonths, DateTime acquisitionDate, decimal openingAccumDep, int? categoryId, DateTime? cutoff)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			var (ok, err) = await _opening.PostAssetAsync(co, cu, new FixedAssetInput { Name = name, Cost = cost, SalvageValue = salvageValue, UsefulLifeMonths = usefulLifeMonths, AcquisitionDate = acquisitionDate, CategoryId = categoryId }, openingAccumDep, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening asset entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpAddGl(string? linesJson, DateTime? cutoff)
		{
			var cu = CutoffOr(cutoff); await SaveCutoffAsync(cu);
			List<OpeningGlLineInput> lines;
			try { lines = System.Text.Json.JsonSerializer.Deserialize<List<OpeningGlLineInput>>(linesJson ?? "[]", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); } catch { lines = new(); }
			var (ok, err) = await _opening.PostGlAsync(co, cu, lines, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening account balances entered"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> OpFinalize()
		{
			var (ok, err) = await _opening.FinalizeAsync(co, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? L["Opening balances finalized successfully"].Value : err;
			return RedirectToAction(nameof(OpeningBalances));
		}

		// ================= Integrity reconciliation guard =================
		[HttpGet] public async Task<IActionResult> IntegrityReconciliation()
		{
			ViewBag.Checks = await _integrity.RunAsync(co);
			ViewBag.Runs = await _integrity.RecentRunsAsync(co, 15);
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> RunIntegrity()
		{
			var (run, _) = await _integrity.RunAndLogAsync(co, "Manual");
			TempData["InvMsg"] = run.AllOk ? L["Integrity check: all invariants match"].Value : string.Format(L["Integrity check: {0} deviations — administrators notified"].Value, run.FailedCount);
			return RedirectToAction(nameof(IntegrityReconciliation));
		}

		// ================= Capitalize asset from stock (إذن صرف أصول) =================
		[HttpGet] public async Task<IActionResult> CapitalizeAsset()
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.AssetItems = await _context.Items.AsNoTracking().Where(i => i.CompanyID == co && i.IsActive && i.ItemType == "Asset").OrderBy(i => i.ItemCode).ToListAsync();
			return View();
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> DoCapitalizeAsset(int itemId, int warehouseId, decimal qty, DateTime date)
		{
			var (ok, err, assetId, cost) = await _stock.CapitalizeFromStockAsync(co, itemId, warehouseId, qty, date, null, _access.CurrentEmployeeId()?.ToString());
			TempData[ok ? "InvMsg" : "InvErr"] = ok ? string.Format(L["Asset capitalized from stock (cost {0:N2}) — entry Dr fixed asset / Cr inventory"].Value, cost) : err;
			return RedirectToAction(ok ? nameof(CapitalizeAsset) : nameof(CapitalizeAsset));
		}

		// ================= Phase I10: Reports =================
		[HttpGet] public IActionResult Reports() => View();

		[HttpGet] public async Task<IActionResult> ValuationReport(int? warehouseId)
		{
			var items = (await _items.GetItemsAsync(co)).ToDictionary(i => i.ID, i => i);
			var whs = (await _warehouses.GetWarehousesAsync(co)).ToDictionary(w => w.ID, w => w);
			var balances = await _stock.GetBalancesAsync(co, warehouseId);
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
			var items = (await _items.GetItemsAsync(co)).ToDictionary(i => i.ID, i => i);
			var whs = (await _warehouses.GetWarehousesAsync(co)).ToDictionary(w => w.ID, w => w);
			var balances = await _stock.GetBalancesAsync(co, warehouseId);
			// last movement date per (item, warehouse)
			var last = await _context.StockMovements.AsNoTracking().Where(m => m.CompanyID == co)
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
			var items = (await _items.GetItemsAsync(co)).ToDictionary(i => i.ID, i => i);
			var whs = (await _warehouses.GetWarehousesAsync(co)).ToDictionary(w => w.ID, w => w);
			var balances = await _stock.GetBalancesAsync(co, warehouseId);
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
			var c = co;
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
			ViewBag.Employees = (await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == co && e.IsActive)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync())
				.Select(e => new CrossBuy.ViewModel.EmployeeViewModel { ID = e.ID, FullName = !isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName })
				.OrderBy(e => e.FullName).ToList();
			ViewBag.Branches = await _context.Hierarchicals.AsNoTracking().Where(h => h.IsActive == true).OrderBy(h => h.H_Name).Select(h => new InvBranchOption { H_ID = h.H_ID, H_Name = h.H_Name }).ToListAsync();
			ViewBag.Assignments = (await (from r in _context.InventoryUserRoles.AsNoTracking().Where(r => r.CompanyID == co)
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
			// EXPLICIT REFUSAL, because this action INSERTS. Most actions on this controller hand `co` to a service
			// that looks a row up by it, so an unresolved company (0) simply matches nothing and the operation fails
			// closed on its own. This one composes a NEW row and stamps `CompanyID = co` onto it, so a 0 would
			// PERSIST an orphan grant belonging to no company — and an inventory ROLE grant at that. Refuse instead.
			if (co == 0) return CompanyRefusedView();

			var allowed = new[] { "InventoryManager", "WarehouseKeeper", "PurchasingOfficer", "InventoryAuditor" };
			if (employeeId <= 0 || !allowed.Contains(role)) { TempData["InvErr"] = L["Invalid data"].Value; return RedirectToAction(nameof(InventoryRoles)); }
			var exists = await _context.InventoryUserRoles.AnyAsync(r => r.CompanyID == co && r.EmployeeId == employeeId && r.Role == role && r.ScopeBranchId == scopeBranchId);
			if (!exists)
			{
				_context.InventoryUserRoles.Add(new InventoryUserRole { CompanyID = co, EmployeeId = employeeId, Role = role, ScopeBranchId = role == "WarehouseKeeper" ? scopeBranchId : null, CreatedAt = DateTime.UtcNow });
				await _context.SaveChangesAsync();
			}
			TempData["InvMsg"] = L["Role assigned"].Value;
			return RedirectToAction(nameof(InventoryRoles));
		}

		[HttpPost][ValidateAntiForgeryToken][InvPerm("manage")]
		public async Task<IActionResult> RemoveRole(int id)
		{
			var r = await _context.InventoryUserRoles.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == co);
			if (r != null) { _context.InventoryUserRoles.Remove(r); await _context.SaveChangesAsync(); }
			TempData["InvMsg"] = L["Role removed"].Value;
			return RedirectToAction(nameof(InventoryRoles));
		}

		// ================= Inventory settings =================
		[HttpGet] public async Task<IActionResult> Settings()
		{
			var s = await _context.InventorySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == co)
				?? new InventorySettings { CompanyID = co, InterBranchTransferMode = "CostCenterPosting" };
			return View(s);
		}

		[HttpPost][ValidateAntiForgeryToken]
		[InvPerm("manage")]
		public async Task<IActionResult> SaveSettings(string interBranchTransferMode, string writeOffMode, decimal approvalThreshold, decimal minMarginPct, string minMarginMode, decimal maxLineDiscountPct, string discountApprovalMode)
		{
			// Same reason as AssignRole: the `s == null` branch below INSERTS and stamps `CompanyID = co`, so an
			// unresolved company would create a settings row owned by nobody — and inventory settings decide
			// write-off mode and approval thresholds, so an orphan row is a control surface, not just a stray row.
			if (co == 0) return CompanyRefusedView();

			var s = await _context.InventorySettings.FirstOrDefaultAsync(x => x.CompanyID == co);
			if (s == null) { s = new InventorySettings { CompanyID = co, CreatedAt = DateTime.UtcNow }; _context.InventorySettings.Add(s); }
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
			return View(await _context.LandedCosts.AsNoTracking().Where(l => l.CompanyID == co).OrderByDescending(l => l.ID).ToListAsync());
		}

		[HttpGet] public async Task<IActionResult> NewLandedCost(int? grId)
		{
			ViewBag.Receipts = await _proc.GetReceiptsAsync(co);
			ViewBag.Accounts = await _coa.GetFlatAsync(co, postableOnly: true);
			if (grId != null)
			{
				var gr = await _proc.GetReceiptAsync(co, grId.Value);
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
			var (ok, err, _) = await _stock.PostLandedCostAsync(co, goodsReceiptId, landedDate, allocationMethod ?? "Value", charges, notes, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(NewLandedCost), new { grId = goodsReceiptId }); }
			TempData["InvMsg"] = L["Landed cost posted; inventory value updated"].Value;
			return RedirectToAction(nameof(LandedCosts));
		}

		// ================= Phase I8: Planning (reorder settings + suggestions) =================
		[HttpGet] public async Task<IActionResult> ReorderSettings(int? warehouseId)
		{
			ViewBag.Warehouses = await _warehouses.GetWarehousesAsync(co);
			ViewBag.FilterWarehouseId = warehouseId;
			if (warehouseId != null)
			{
				var items = await _items.GetItemsAsync(co);
				var settings = (await _context.ItemWarehouseSettings.AsNoTracking().Where(s => s.WarehouseId == warehouseId).ToListAsync())
					.ToDictionary(s => s.ItemId, s => s);
				ViewBag.Rows = items.Where(i => i.ItemType == "Stockable").Select(i =>
				{
					settings.TryGetValue(i.ID, out var s);
					return new InvReportRow { ItemId = i.ID, ItemCode = i.ItemCode, ItemName = ItemDisplayName(i), ReorderPoint = s?.ReorderPoint ?? 0, MinQty = s?.MinQty ?? 0, MaxQty = s?.MaxQty ?? 0 };
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
			var items = (await _items.GetItemsAsync(co)).ToDictionary(i => i.ID, i => i);
			var whs = (await _warehouses.GetWarehousesAsync(co)).ToDictionary(w => w.ID, w => w);
			var balances = await _stock.GetBalancesAsync(co, warehouseId);
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
			ViewBag.Vendors = await _context.Vendors.AsNoTracking().Where(v => v.CompanyID == co).OrderBy(v => v.Name).ToListAsync();
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
			var (ok, err, po) = await _proc.CreatePurchaseOrderAsync(co, vendorId, warehouseId, DateTime.Today, null, "Generated from shortage planning", lines, null);
			if (!ok) { TempData["InvErr"] = err; return RedirectToAction(nameof(Planning), new { warehouseId }); }
			TempData["InvMsg"] = string.Format(L["Purchase order {0} created from planning"].Value, po?.OrderNo);
			return RedirectToAction(nameof(PurchaseOrders));
		}

		[HttpGet] public async Task<IActionResult> Serials(string? status)
		{
			var c = co;
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

	// Rack view payloads. NAMED and public on purpose: the view reads them through
	// IEnumerable<dynamic>, and an anonymous type is internal to this assembly - a
	// runtime-compiled Razor view sits in another one and cannot bind to it, which
	// raises "'object' does not contain a definition for 'Total'".
	public class InvRackStockRow
	{
		public int ItemId { get; set; }
		public int BinLocationId { get; set; }
		public decimal QtyOnHand { get; set; }
		public string? ItemCode { get; set; }
		public string? ItemName { get; set; }
		public string? BinCode { get; set; }
		public string? BinName { get; set; }
		public string? BinType { get; set; }
	}

	public class InvRackReconRow
	{
		public string? ItemCode { get; set; }
		public string? ItemName { get; set; }
		public decimal Located { get; set; }
		public decimal Total { get; set; }
	}

	// A simple {ID, Name} option list. NAMED, for the same reason as InvRackReconRow:
	// the views read these through IEnumerable<dynamic>, and an anonymous type is
	// internal to this assembly, so a runtime-compiled view cannot bind to it.
	public class InvIdNameOption
	{
		public int ID { get; set; }
		public string? Name { get; set; }
	}
}
