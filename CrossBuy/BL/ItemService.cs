using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class ItemCategoryNode
	{
		public int Id { get; set; }
		public int? ParentId { get; set; }
		public int Level { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public bool IsActive { get; set; }
		public string Kind { get; set; } = "Category";
		public int? InventoryAccountId { get; set; }
		public int? CogsAccountId { get; set; }
		public string? StoreIcon { get; set; }   // storefront image (shown in the categories list)
	}

	// one alternate-unit row: 1 [UoMId] = [Factor] base units, with an optional barcode
	public class UoMRowInput
	{
		public int UoMId { get; set; }
		public decimal Factor { get; set; }
		public string? Barcode { get; set; }
	}

	// one BOM component row of a composite item
	public class ComponentRowInput
	{
		public int ComponentItemId { get; set; }
		public decimal Quantity { get; set; } = 1;
		public decimal ScrapPct { get; set; }
		public int? UoMId { get; set; }
	}

	public class ItemInput
	{
		public string ItemCode { get; set; } = "";
		public string Barcode { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public int ItemCategoryId { get; set; }
		public string ItemType { get; set; } = "Stockable";
		public int BaseUoMId { get; set; }
		public int? PurchaseUoMId { get; set; }
		public int? SalesUoMId { get; set; }
		public string? CostingMethod { get; set; }
		public bool TrackBatch { get; set; }
		public bool TrackExpiry { get; set; }
		public bool TrackSerial { get; set; }
		public bool IsWeighted { get; set; }                  // HM-3
		public int? ScaleCode { get; set; }                   // HM-3
		public int? DefaultTaxCodeId { get; set; }
		public decimal? SalesPrice { get; set; }
		public decimal? MinMarginPct { get; set; }
		public decimal? OpeningCost { get; set; }
		public string? ImagePath { get; set; }
		// E-commerce storefront display fields (optional)
		public decimal? StoreOldPrice { get; set; }
		public string? StoreBadge { get; set; }
		public decimal? StoreRating { get; set; }
		public string? StoreVendor { get; set; }
		public string? StoreHoverImage { get; set; }
		public bool IsActive { get; set; } = true;
		// composite (kit/bundle)
		public bool IsComposite { get; set; }
		public string? CompositeType { get; set; }            // Bundle / Assembly
		public string? ProductionMethod { get; set; }         // Immediate | OrderBased (Assembly items)
		// alternate units + barcodes (excludes the base unit which is implicit factor 1)
		public List<UoMRowInput> Units { get; set; } = new();
		// BOM components (only when IsComposite)
		public List<ComponentRowInput> Components { get; set; } = new();
	}

	public interface IItemService
	{
		// units
		Task<List<UnitOfMeasure>> GetUnitsAsync(int companyId);
		Task<(bool ok, string? error)> CreateUnitAsync(int companyId, string code, string name, string nameEn);
		// categories
		Task<List<ItemCategory>> GetCategoriesAsync(int companyId);
		Task<List<ItemCategoryNode>> GetCategoryTreeAsync(int companyId);
		Task<(bool ok, string? error)> CreateCategoryAsync(int companyId, ItemCategory c, string? userId);
		Task<(bool ok, string? error)> UpdateCategoryAsync(int companyId, int id, ItemCategory c, string? userId);
		// items
		Task<List<Item>> GetItemsAsync(int companyId);
		Task<(List<Item> rows, int total)> SearchItemsAsync(int companyId, string? q, int? categoryId, string? type, bool? active, int page, int pageSize);
		Task<List<(string code, string name)>> SuggestItemsAsync(int companyId, string? term, int take = 10);
		Task<Item?> GetItemAsync(int companyId, int id);
		Task<List<UoMRowInput>> GetItemUnitsAsync(int itemId);            // alternate units + per-unit barcode
		Task<List<ItemComponent>> GetItemComponentsAsync(int itemId);     // BOM rows
		Task<(bool ok, string? error, Item? item)> CreateItemAsync(int companyId, ItemInput input, string? userId);
		Task<(bool ok, string? error)> UpdateItemAsync(int companyId, int id, ItemInput input, string? userId);
		Task<List<ItemImage>> GetItemImagesAsync(int companyId, int itemId);                      // storefront gallery (extra images)
		Task AddItemImagesAsync(int companyId, int itemId, IEnumerable<string> paths);             // append gallery images
		Task<List<string>> RemoveItemImagesAsync(int companyId, int itemId, IEnumerable<int> imageIds);   // delete gallery rows; returns their paths
		Task RecomputeStoreHoverAsync(int companyId, int itemId);                                  // hover = first gallery image
	}

	public class ItemService : IItemService
	{
		private readonly CrossDbContext _context;
		public ItemService(CrossDbContext context) { _context = context; }

		// ---- units ----
		public async Task<List<UnitOfMeasure>> GetUnitsAsync(int companyId) =>
			await _context.UnitsOfMeasure.AsNoTracking().Where(u => u.CompanyID == companyId).OrderBy(u => u.Name).ToListAsync();

		public async Task<(bool ok, string? error)> CreateUnitAsync(int companyId, string code, string name, string nameEn)
		{
			if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return (false, "الكود والاسم مطلوبان");
			if (await _context.UnitsOfMeasure.AnyAsync(u => u.CompanyID == companyId && u.Code == code)) return (false, "كود الوحدة مستخدم من قبل");
			_context.UnitsOfMeasure.Add(new UnitOfMeasure { CompanyID = companyId, Code = code.Trim(), Name = name.Trim(), NameEn = (nameEn ?? "").Trim(), IsActive = true, CreatedAt = DateTime.UtcNow });
			await _context.SaveChangesAsync();
			return (true, null);
		}

		// ---- categories ----
		public async Task<List<ItemCategory>> GetCategoriesAsync(int companyId) =>
			await _context.ItemCategories.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.Code).ToListAsync();

		public async Task<List<ItemCategoryNode>> GetCategoryTreeAsync(int companyId)
		{
			var all = await GetCategoriesAsync(companyId);
			var nodes = new List<ItemCategoryNode>();
			void Walk(int? parent, int level)
			{
				foreach (var c in all.Where(x => x.ParentId == parent).OrderBy(x => x.Code))
				{
					nodes.Add(new ItemCategoryNode
					{
						Id = c.ID, ParentId = c.ParentId, Level = level, Code = c.Code, Name = c.Name, NameEn = c.NameEn,
						IsActive = c.IsActive, Kind = c.Kind, InventoryAccountId = c.InventoryAccountId, CogsAccountId = c.CogsAccountId,
						StoreIcon = c.StoreIcon,
					});
					Walk(c.ID, level + 1);
				}
			}
			Walk(null, 0);
			return nodes;
		}

		public async Task<(bool ok, string? error)> CreateCategoryAsync(int companyId, ItemCategory c, string? userId)
		{
			if (string.IsNullOrWhiteSpace(c.Code) || string.IsNullOrWhiteSpace(c.Name)) return (false, "Code and name are required");
			if (await _context.ItemCategories.AnyAsync(x => x.CompanyID == companyId && x.Code == c.Code)) return (false, "That category code is already in use");
			c.Kind = (c.Kind == "Group") ? "Group" : "Category";
			if (c.Kind == "Group")
			{
				if (c.ParentId == null) return (false, "A group must belong to a top-level category");
				var parent = await _context.ItemCategories.AsNoTracking().FirstOrDefaultAsync(x => x.ID == c.ParentId && x.CompanyID == companyId);
				if (parent == null) return (false, "The parent category was not found");
				if (parent.Kind == "Group") return (false, "A group cannot belong to another group (only two levels: category, then group)");
				// GL mapping inherited from the parent Category when left empty on the Group (copy-down; keeps every GL consumer unchanged)
				InheritGlFromParent(c, parent);
			}
			else { c.ParentId = null; }
			c.CompanyID = companyId; c.IsActive = true; c.CreatedBy = userId; c.CreatedAt = DateTime.UtcNow;
			_context.ItemCategories.Add(c);
			await _context.SaveChangesAsync();
			return (true, null);
		}

		// Fill any null GL account on a Group from its parent Category (so all existing GL consumers keep reading concrete accounts).
		private static void InheritGlFromParent(ItemCategory child, ItemCategory parent)
		{
			child.InventoryAccountId ??= parent.InventoryAccountId;
			child.CogsAccountId ??= parent.CogsAccountId;
			child.AdjustmentAccountId ??= parent.AdjustmentAccountId;
			child.GrniAccountId ??= parent.GrniAccountId;
			if (string.IsNullOrEmpty(child.DefaultCostingMethod)) child.DefaultCostingMethod = parent.DefaultCostingMethod;
		}

		public async Task<(bool ok, string? error)> UpdateCategoryAsync(int companyId, int id, ItemCategory c, string? userId)
		{
			var ex = await _context.ItemCategories.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (ex == null) return (false, "Category not found");
			if (await _context.ItemCategories.AnyAsync(x => x.CompanyID == companyId && x.Code == c.Code && x.ID != id)) return (false, "That category code is already in use");
			if (c.ParentId == id) return (false, "A category cannot be its own parent");

			// remember old GL values so we can cascade to child groups that were inheriting (value == old parent value)
			var (oldInv, oldCogs, oldAdj, oldGrni) = (ex.InventoryAccountId, ex.CogsAccountId, ex.AdjustmentAccountId, ex.GrniAccountId);

			var kind = (c.Kind == "Group") ? "Group" : "Category";
			if (kind == "Group")
			{
				if (c.ParentId == null) return (false, "A group must belong to a top-level category");
				var parent = await _context.ItemCategories.AsNoTracking().FirstOrDefaultAsync(x => x.ID == c.ParentId && x.CompanyID == companyId);
				if (parent == null) return (false, "The parent category was not found");
				if (parent.Kind == "Group") return (false, "A group cannot belong to another group (only two levels: category, then group)");
				InheritGlFromParent(c, parent);
			}
			else { c.ParentId = null; }

			ex.Code = c.Code; ex.Name = c.Name; ex.NameEn = c.NameEn; ex.ParentId = c.ParentId; ex.Kind = kind;
			ex.InventoryAccountId = c.InventoryAccountId; ex.CogsAccountId = c.CogsAccountId;
			ex.AdjustmentAccountId = c.AdjustmentAccountId; ex.GrniAccountId = c.GrniAccountId;
			ex.DefaultCostingMethod = c.DefaultCostingMethod; ex.IsActive = c.IsActive;
			ex.StoreIcon = c.StoreIcon; ex.StoreItemsCount = c.StoreItemsCount;   // storefront display (image + count)
			ex.ModifiedBy = userId; ex.ModifiedAt = DateTime.UtcNow;

			// cascade GL changes to child groups that were inheriting from this (root) category
			if (kind == "Category")
			{
				var children = await _context.ItemCategories.Where(x => x.CompanyID == companyId && x.ParentId == id && x.Kind == "Group").ToListAsync();
				foreach (var ch in children)
				{
					if (ch.InventoryAccountId == oldInv) ch.InventoryAccountId = ex.InventoryAccountId;
					if (ch.CogsAccountId == oldCogs) ch.CogsAccountId = ex.CogsAccountId;
					if (ch.AdjustmentAccountId == oldAdj) ch.AdjustmentAccountId = ex.AdjustmentAccountId;
					if (ch.GrniAccountId == oldGrni) ch.GrniAccountId = ex.GrniAccountId;
				}
			}
			await _context.SaveChangesAsync();
			return (true, null);
		}

		// ---- items ----
		public async Task<List<Item>> GetItemsAsync(int companyId) =>
			await _context.Items.AsNoTracking().Where(i => i.CompanyID == companyId).OrderBy(i => i.ItemCode).ToListAsync();

		// server-side search + pagination (scales to millions of rows — DB does the filtering/paging)
		public async Task<(List<Item> rows, int total)> SearchItemsAsync(int companyId, string? q, int? categoryId, string? type, bool? active, int page, int pageSize)
		{
			var query = _context.Items.AsNoTracking().Where(i => i.CompanyID == companyId);
			// Tagify multi-tag search: each tag is one term (delimited by '|'); a row matches if ANY tag hits ANY field (OR)
			var terms = SearchTerms.Parse(q);
			if (terms.Count > 0)
			{
				var pred = PredicateBuilder.AnyTerm<Item>(terms, s =>
					i => i.ItemCode.Contains(s) || i.Name.Contains(s)
						|| (i.NameEn != null && i.NameEn.Contains(s)) || (i.Barcode != null && i.Barcode.Contains(s)));
				if (pred != null) query = query.Where(pred);
			}
			if (categoryId.HasValue && categoryId.Value > 0) query = query.Where(i => i.ItemCategoryId == categoryId.Value);
			if (!string.IsNullOrWhiteSpace(type)) query = query.Where(i => i.ItemType == type);
			if (active.HasValue) query = query.Where(i => i.IsActive == active.Value);

			var total = await query.CountAsync();
			if (page < 1) page = 1;
			if (pageSize < 1) pageSize = 25; else if (pageSize > 100000) pageSize = 100000;   // high ceiling allows full-set export
			var rows = await query.OrderBy(i => i.ItemCode).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
			return (rows, total);
		}

		// Tagify autocomplete: top item matches for the typed term (code shown as the tag value, name as the hint)
		public async Task<List<(string code, string name)>> SuggestItemsAsync(int companyId, string? term, int take = 10)
		{
			var t = (term ?? "").Trim();
			var query = _context.Items.AsNoTracking().Where(i => i.CompanyID == companyId && i.IsActive);
			if (t.Length > 0)
				query = query.Where(i => i.ItemCode.Contains(t) || i.Name.Contains(t)
					|| (i.NameEn != null && i.NameEn.Contains(t)) || (i.Barcode != null && i.Barcode.Contains(t)));
			var rows = await query.OrderBy(i => i.ItemCode).Take(take <= 0 ? 10 : take)
				.Select(i => new { i.ItemCode, i.Name }).ToListAsync();
			return rows.Select(r => (r.ItemCode, r.Name)).ToList();
		}

		public async Task<Item?> GetItemAsync(int companyId, int id) =>
			await _context.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == companyId);

		// ---- storefront gallery (extra images per item; display only, no inventory/GL impact) ----
		public Task<List<ItemImage>> GetItemImagesAsync(int companyId, int itemId) =>
			_context.ItemImages.AsNoTracking()
				.Where(x => x.CompanyID == companyId && x.ItemId == itemId)
				.OrderBy(x => x.SortOrder).ThenBy(x => x.ID).ToListAsync();

		public async Task AddItemImagesAsync(int companyId, int itemId, IEnumerable<string> paths)
		{
			var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
			if (list.Count == 0) return;
			int next = (await _context.ItemImages.Where(x => x.CompanyID == companyId && x.ItemId == itemId)
							.Select(x => (int?)x.SortOrder).MaxAsync()) ?? -1;
			foreach (var p in list)
				_context.ItemImages.Add(new ItemImage { CompanyID = companyId, ItemId = itemId, Path = p, SortOrder = ++next, CreatedAt = DateTime.UtcNow });
			await _context.SaveChangesAsync();
		}

		public async Task<List<string>> RemoveItemImagesAsync(int companyId, int itemId, IEnumerable<int> imageIds)
		{
			var ids = imageIds.ToHashSet();
			if (ids.Count == 0) return new List<string>();
			var rows = await _context.ItemImages
				.Where(x => x.CompanyID == companyId && x.ItemId == itemId && ids.Contains(x.ID)).ToListAsync();
			if (rows.Count == 0) return new List<string>();
			var paths = rows.Select(r => r.Path).ToList();
			_context.ItemImages.RemoveRange(rows);
			await _context.SaveChangesAsync();
			return paths;   // caller deletes the physical files
		}

		// The storefront "hover" image (catalog card swap) is now DERIVED from the gallery = its first image (or null).
		public async Task RecomputeStoreHoverAsync(int companyId, int itemId)
		{
			var first = await _context.ItemImages
				.Where(x => x.CompanyID == companyId && x.ItemId == itemId)
				.OrderBy(x => x.SortOrder).ThenBy(x => x.ID)
				.Select(x => x.Path).FirstOrDefaultAsync();
			var item = await _context.Items.FirstOrDefaultAsync(i => i.ID == itemId && i.CompanyID == companyId);
			if (item != null && item.StoreHoverImage != first) { item.StoreHoverImage = first; await _context.SaveChangesAsync(); }
		}

		// alternate units of an item merged with their (UoM-scoped) barcodes
		public async Task<List<UoMRowInput>> GetItemUnitsAsync(int itemId)
		{
			var convs = await _context.UoMConversions.AsNoTracking().Where(c => c.ItemId == itemId).ToListAsync();
			var barcodes = await _context.ItemBarcodes.AsNoTracking().Where(b => b.ItemId == itemId && b.UoMId != null).ToListAsync();
			return convs.Select(c => new UoMRowInput
			{
				UoMId = c.FromUoMId,
				Factor = c.Factor,
				Barcode = barcodes.FirstOrDefault(b => b.UoMId == c.FromUoMId)?.Barcode
			}).ToList();
		}

		public async Task<List<ItemComponent>> GetItemComponentsAsync(int itemId) =>
			await _context.ItemComponents.AsNoTracking().Where(c => c.ParentItemId == itemId).OrderBy(c => c.SortOrder).ToListAsync();

		// replaces the stored alternate units (UoMConversions + per-unit barcodes) and BOM rows for an item
		private async Task SaveUnitsAndComponentsAsync(int companyId, Item item, ItemInput x)
		{
			// ---- alternate units ----
			var oldConv = await _context.UoMConversions.Where(c => c.ItemId == item.ID).ToListAsync();
			if (oldConv.Count > 0) _context.UoMConversions.RemoveRange(oldConv);
			// drop old per-unit barcodes (keep the base/primary barcode row, UoMId == base)
			var oldUnitBc = await _context.ItemBarcodes.Where(b => b.ItemId == item.ID && b.UoMId != null && b.UoMId != item.BaseUoMId).ToListAsync();
			if (oldUnitBc.Count > 0) _context.ItemBarcodes.RemoveRange(oldUnitBc);

			foreach (var u in x.Units ?? new())
			{
				if (u.UoMId <= 0 || u.UoMId == item.BaseUoMId || u.Factor <= 0) continue;
				_context.UoMConversions.Add(new UoMConversion { ItemId = item.ID, FromUoMId = u.UoMId, ToUoMId = item.BaseUoMId, Factor = u.Factor });
				if (!string.IsNullOrWhiteSpace(u.Barcode))
					_context.ItemBarcodes.Add(new ItemBarcode { ItemId = item.ID, Barcode = u.Barcode.Trim(), UoMId = u.UoMId });
			}

			// ---- BOM components ----
			var oldComp = await _context.ItemComponents.Where(c => c.ParentItemId == item.ID).ToListAsync();
			if (oldComp.Count > 0) _context.ItemComponents.RemoveRange(oldComp);
			if (item.IsComposite)
			{
				int order = 0;
				foreach (var c in x.Components ?? new())
				{
					if (c.ComponentItemId <= 0 || c.ComponentItemId == item.ID || c.Quantity <= 0) continue;
					_context.ItemComponents.Add(new ItemComponent
					{
						CompanyID = companyId, ParentItemId = item.ID, ComponentItemId = c.ComponentItemId,
						Quantity = c.Quantity, ScrapPct = c.ScrapPct < 0 ? 0 : c.ScrapPct, UoMId = c.UoMId, SortOrder = order++, CreatedAt = DateTime.UtcNow
					});
				}
			}
			await _context.SaveChangesAsync();
		}

		// friendly pre-check for duplicate barcodes (primary + per-unit) across Items.Barcode and ItemBarcodes,
		// plus duplicates within the same submission — avoids a raw DB unique-index exception.
		private async Task<string?> BarcodeConflictAsync(int companyId, int itemId, ItemInput x)
		{
			var subs = new List<string>();
			if (!string.IsNullOrWhiteSpace(x.Barcode)) subs.Add(x.Barcode.Trim());
			foreach (var u in x.Units ?? new())
				if (u.UoMId > 0 && u.UoMId != x.BaseUoMId && !string.IsNullOrWhiteSpace(u.Barcode)) subs.Add(u.Barcode.Trim());
			var dup = subs.GroupBy(s => s).FirstOrDefault(g => g.Count() > 1);
			if (dup != null) return $"Barcode «{dup.Key}» is duplicated within the same item";
			foreach (var b in subs.Distinct())
			{
				if (await _context.Items.AnyAsync(i => i.CompanyID == companyId && i.Barcode == b && i.ID != itemId)) return $"Barcode «{b}» is already used by another item";
				if (await _context.ItemBarcodes.AnyAsync(z => z.Barcode == b && z.ItemId != itemId)) return $"Barcode «{b}» is already used by another item";
			}
			// HM-3: a FIXED product barcode must not fall inside any scale-barcode prefix configured on the company's branches
			// (GS1 reserves that range for variable-measure). Deliberate company-wide guard over a branch-level setting
			// (see AUDIT-DEVIATIONS.md). Blocks only NEW saves; existing overlaps are surfaced by the counted classification.
			var scalePrefixes = await _context.BranchPosSettings.AsNoTracking()
				.Where(s => s.ScaleBarcodePrefix != null && s.ScaleBarcodePrefix != ""
					&& _context.Branches.Any(br => br.ID == s.BranchId && br.CompanyID == companyId))
				.Select(s => s.ScaleBarcodePrefix!).Distinct().ToListAsync();
			foreach (var b in subs.Distinct())
				foreach (var pfx in scalePrefixes)
					if (b.StartsWith(pfx)) return $"Barcode «{b}» falls inside the reserved scale-barcode range — that is not allowed for a fixed barcode.";
			return null;
		}

		// HM-3: a weighted item must have a weight base unit (KG) and a unique scale code.
		private async Task<string?> WeightedItemGuardAsync(int companyId, int itemId, ItemInput x)
		{
			if (!x.IsWeighted) return null;
			if (x.ScaleCode == null) return "A weighed item requires a scale code (ScaleCode).";
			var baseCode = await _context.UnitsOfMeasure.AsNoTracking().Where(u => u.ID == x.BaseUoMId).Select(u => u.Code).FirstOrDefaultAsync();
			if (!string.Equals(baseCode, "KG", StringComparison.OrdinalIgnoreCase)) return "A weighed item requires a weight-based base unit (kg).";
			if (await _context.Items.AnyAsync(i => i.CompanyID == companyId && i.ScaleCode == x.ScaleCode && i.ID != itemId)) return "That scale code is already used by another item.";
			return null;
		}

		// HM-6 (HM-D8): TrackBatch has no issue-ordering behaviour of its own (FEFO orders by EXPIRY), so enabling it
		// without TrackExpiry would fake a tracking the system does not enforce. Refuse it until batch-only tracking is
		// implemented. (All existing TrackBatch items already have TrackExpiry, so this breaks nothing.)
		private static string? TrackingGuard(ItemInput x)
			=> (x.TrackBatch && !x.TrackExpiry)
				? "Batch tracking without expiry tracking is not supported — enable expiry tracking (it orders issues by FEFO)."
				: null;

		private static (bool ok, string? error) ValidateItem(ItemInput x)
		{
			if (string.IsNullOrWhiteSpace(x.ItemCode)) return (false, "Item code is required");
			if (string.IsNullOrWhiteSpace(x.Barcode)) return (false, "A barcode is required for every item");
			if (string.IsNullOrWhiteSpace(x.Name)) return (false, "Item name is required");
			if (x.ItemCategoryId <= 0) return (false, "Category is required");
			if (x.BaseUoMId <= 0) return (false, "A base unit of measure is required");
			return (true, null);
		}

		public async Task<(bool ok, string? error, Item? item)> CreateItemAsync(int companyId, ItemInput x, string? userId)
		{
			var (vok, verr) = ValidateItem(x);
			if (!vok) return (false, verr, null);
			var code = x.ItemCode.Trim(); var bar = x.Barcode.Trim();   // compare the SAME (trimmed) value we store, else the DB unique index 500s on a stray space
			if (await _context.Items.AnyAsync(i => i.CompanyID == companyId && i.ItemCode == code)) return (false, "That item code is already in use", null);
			var bcErr = await BarcodeConflictAsync(companyId, 0, x);
			if (bcErr != null) return (false, bcErr, null);
			var wErr = await WeightedItemGuardAsync(companyId, 0, x);   // HM-3
			if (wErr != null) return (false, wErr, null);
			var tErr = TrackingGuard(x);   // HM-6/HM-D8
			if (tErr != null) return (false, tErr, null);

			var item = new Item
			{
				CompanyID = companyId, ItemCode = code, Barcode = bar,
				Name = x.Name.Trim(), NameEn = x.NameEn, ItemCategoryId = x.ItemCategoryId, ItemType = x.ItemType,
				BaseUoMId = x.BaseUoMId, PurchaseUoMId = x.PurchaseUoMId, SalesUoMId = x.SalesUoMId,
				CostingMethod = x.CostingMethod, TrackBatch = x.TrackBatch, TrackExpiry = x.TrackExpiry, TrackSerial = x.TrackSerial,
				IsWeighted = x.IsWeighted, ScaleCode = x.ScaleCode,   // HM-3
				DefaultTaxCodeId = x.DefaultTaxCodeId, SalesPrice = x.SalesPrice, MinMarginPct = x.MinMarginPct, OpeningCost = x.OpeningCost,
				ImagePath = x.ImagePath,
				StoreOldPrice = x.StoreOldPrice, StoreBadge = x.StoreBadge, StoreRating = x.StoreRating, StoreVendor = x.StoreVendor, StoreHoverImage = x.StoreHoverImage,
				IsComposite = x.IsComposite, CompositeType = x.IsComposite ? x.CompositeType : null,
				ProductionMethod = string.IsNullOrWhiteSpace(x.ProductionMethod) ? "OrderBased" : x.ProductionMethod,
				IsActive = x.IsActive, CreatedBy = userId, CreatedAt = DateTime.UtcNow,
			};
			_context.Items.Add(item);
			await _context.SaveChangesAsync();
			// register the primary barcode in the multi-barcode table too
			_context.ItemBarcodes.Add(new ItemBarcode { ItemId = item.ID, Barcode = item.Barcode, UoMId = item.BaseUoMId });
			await _context.SaveChangesAsync();
			await SaveUnitsAndComponentsAsync(companyId, item, x);
			return (true, null, item);
		}

		public async Task<(bool ok, string? error)> UpdateItemAsync(int companyId, int id, ItemInput x, string? userId)
		{
			var (vok, verr) = ValidateItem(x);
			if (!vok) return (false, verr);
			var item = await _context.Items.FirstOrDefaultAsync(i => i.ID == id && i.CompanyID == companyId);
			if (item == null) return (false, "Item not found");
			var code = x.ItemCode.Trim(); var bar = x.Barcode.Trim();
			if (await _context.Items.AnyAsync(i => i.CompanyID == companyId && i.ItemCode == code && i.ID != id)) return (false, "That item code is already in use");
			var bcErr = await BarcodeConflictAsync(companyId, id, x);
			if (bcErr != null) return (false, bcErr);
			var wErr = await WeightedItemGuardAsync(companyId, id, x);   // HM-3
			if (wErr != null) return (false, wErr);
			var tErr = TrackingGuard(x);   // HM-6/HM-D8
			if (tErr != null) return (false, tErr);
			item.ItemCode = code; item.Barcode = bar; item.Name = x.Name.Trim(); item.NameEn = x.NameEn;
			item.ItemCategoryId = x.ItemCategoryId; item.ItemType = x.ItemType; item.BaseUoMId = x.BaseUoMId;
			item.PurchaseUoMId = x.PurchaseUoMId; item.SalesUoMId = x.SalesUoMId; item.CostingMethod = x.CostingMethod;
			item.TrackBatch = x.TrackBatch; item.TrackExpiry = x.TrackExpiry; item.TrackSerial = x.TrackSerial;
			item.IsWeighted = x.IsWeighted; item.ScaleCode = x.ScaleCode;   // HM-3
			item.DefaultTaxCodeId = x.DefaultTaxCodeId; item.SalesPrice = x.SalesPrice; item.MinMarginPct = x.MinMarginPct; item.OpeningCost = x.OpeningCost;
			if (!string.IsNullOrEmpty(x.ImagePath)) item.ImagePath = x.ImagePath;
			item.StoreOldPrice = x.StoreOldPrice; item.StoreBadge = x.StoreBadge; item.StoreRating = x.StoreRating; item.StoreVendor = x.StoreVendor;
			if (!string.IsNullOrEmpty(x.StoreHoverImage)) item.StoreHoverImage = x.StoreHoverImage;
			item.IsComposite = x.IsComposite; item.CompositeType = x.IsComposite ? x.CompositeType : null;
			item.ProductionMethod = string.IsNullOrWhiteSpace(x.ProductionMethod) ? "OrderBased" : x.ProductionMethod;
			item.IsActive = x.IsActive;
			item.ModifiedBy = userId; item.ModifiedAt = DateTime.UtcNow;
			await _context.SaveChangesAsync();
			await SaveUnitsAndComponentsAsync(companyId, item, x);
			return (true, null);
		}
	}
}
