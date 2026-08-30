using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// THE ONE PLACE A BILL OF MATERIALS BECOMES QUANTITIES.
	//
	// Before this service the same question — "how much of each component does N of this item need?" — was
	// answered in six places, and they did not agree. The quantities were reconciled earlier; the ROUNDING was
	// not, and neither was the unit:
	//
	//   PosOrderService  RecipeAtSale      qty*(1+scrap)      Round(4, AwayFromZero)   no UoM conversion
	//   StockService     Bundle explode    qty*(1+scrap)      Round(4)  -> ToEven      no UoM conversion
	//   StockService     immediate prod.   qty*(1+scrap)      Round(4)  -> ToEven      no UoM conversion
	//   ManufService     WO planning       qty*(1+scrap)      Round(4)  -> ToEven      UoM carried, not applied
	//   ManufService     MRP Require       qty*(1+scrap)      NO rounding              no UoM conversion
	//   ManufService     standard cost     qty*(1+scrap)      NO rounding              no UoM conversion
	//
	// So one recipe could consume three different quantities depending only on which route the sale or the
	// production took. This service is the single answer, and every one of those sites now asks it.
	//
	// THE ALGORITHM IS NOT NEW. The recursive core is lifted from ManufService.RunMrpAsync's local `Require`
	// function, which was already the strongest implementation in the repository: depth-capped, netting against
	// on-hand as it descends, applying scrap at every level, and recording the level at which each item was
	// first reached. That behaviour is preserved exactly (see BomRequirementsAsync) rather than being
	// re-derived, and MRP now calls this instead of carrying its own copy — a seventh copy is precisely what
	// this service exists to prevent.
	//
	// WHAT IS DELIBERATELY NOT HERE:
	//   * consumption timing. This service computes quantities; it never decides WHEN stock moves. POS still
	//     consumes at Pay, unchanged.
	//   * stock writing. StockService remains the only stock writer; this service returns numbers.
	//   * a second UoM engine. Conversion reads the same UoMConversions rows, with the same rule and the same
	//     refusal, that StockService.ToBaseAsync uses. There is no UoM service in this repository to call —
	//     every consumer reads that table directly — so this consumes the canonical MECHANISM (the rows and
	//     the rule), it does not invent a parallel one.
	public interface IBomExplosionService
	{
		/// Explode ONE parent into its components. Single level by default, which is what every caller except
		/// MRP does today.
		Task<BomExplosionResult> ExplodeAsync(int companyId, int parentItemId, decimal qty,
			BomExplosionOptions? options = null, CancellationToken cancellationToken = default);

		/// The MRP shape: several demands, netted against a running on-hand map, exploded only where a NET
		/// shortage remains. `available` is READ AND MUTATED, exactly as MRP's own loop did.
		Task<BomRequirementsResult> BomRequirementsAsync(int companyId,
			IReadOnlyList<(int itemId, decimal qty)> demands, Dictionary<int, decimal> available,
			BomExplosionOptions? options = null, CancellationToken cancellationToken = default);
	}

	public sealed class BomExplosionOptions
	{
		/// The proven cap from MRP. A BOM that nests deeper than this is a modelling error, not a recipe.
		public const int DefaultMaxDepth = 30;

		/// FALSE by default, and that default is load-bearing. Every caller except MRP explodes ONE level today,
		/// and a recipe whose component is a stocked semi-finished must CONSUME that semi-finished, not silently
		/// manufacture it. Turning this on is a caller's explicit statement that it plans production.
		public bool Recursive { get; init; }

		public int MaxDepth { get; init; } = DefaultMaxDepth;

		/// Convert each component quantity from the recipe's unit into the component's BASE unit.
		/// FALSE where the caller passes the recipe unit onward and the CONSUMER converts — converting here as
		/// well would apply the factor twice. ManufWorkOrderComponent is exactly that case: it stores PlannedQty
		/// with its UoMId and the issue path converts at PostMovementAsync.
		public bool ConvertToBaseUoM { get; init; } = true;

		/// Only a caller that plans (MRP) nets against stock. A sale must draw its whole recipe.
		public bool NetAgainstOnHand { get; init; }
	}

	public sealed class BomLine
	{
		public int ComponentItemId { get; init; }
		/// In the component's base unit when ConvertToBaseUoM is on; otherwise in UoMId.
		public decimal Quantity { get; init; }
		public int? UoMId { get; init; }
		public int SortOrder { get; init; }
		/// 1 = a direct child of the parent asked for.
		public int Level { get; init; }
		/// The component has a bill of materials of its own — it CAN be made. Whether it SHOULD be is the
		/// caller's business decision, which is why recursion is opt-in.
		public bool IsMakeItem { get; init; }
	}

	public sealed class BomExplosionResult
	{
		public IReadOnlyList<BomLine> Lines { get; init; } = Array.Empty<BomLine>();
		public string? Error { get; init; }
		public bool Ok => Error == null;

		public static BomExplosionResult Fail(string error) => new() { Error = error };
	}

	public sealed class BomRequirement
	{
		public int ItemId { get; init; }
		public decimal Gross { get; set; }
		public decimal Net { get; set; }
		public int Level { get; set; }
		public bool IsMakeItem { get; init; }
	}

	public sealed class BomRequirementsResult
	{
		public IReadOnlyList<BomRequirement> Requirements { get; init; } = Array.Empty<BomRequirement>();
		public string? Error { get; init; }
		public bool Ok => Error == null;

		public static BomRequirementsResult Fail(string error) => new() { Error = error };
	}

	public sealed class BomExplosionService : IBomExplosionService
	{
		private readonly CrossDbContext _db;
		public BomExplosionService(CrossDbContext db) { _db = db; }

		/// THE CANONICAL QUANTITY ROUNDING: 4 decimal places, AwayFromZero.
		///
		/// 4dp because that is what every stock quantity in this repository already carries. AwayFromZero because
		/// the repository requires midpoints to be explicit (CLAUDE.md: "explicit AwayFromZero ... never bake a
		/// 2-decimal assumption") and because the two paths that had a deliberate choice — POS RecipeAtSale and
		/// POS modifiers — already used it. The ToEven in the manufacturing paths was the C# DEFAULT, i.e. nobody
		/// chose it, and the two unrounded paths chose nothing at all. Those are not behaviours worth preserving;
		/// they are the inconsistency this batch removes.
		internal static decimal RoundQty(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

		/// One BOM row's contribution: quantity per parent unit, uplifted by planned scrap, times how many
		/// parents. Every caller used exactly this expression; it lives here now and nowhere else.
		private static decimal Extend(decimal perParent, decimal scrapPct, decimal parentQty)
			=> RoundQty(perParent * (1 + scrapPct / 100m) * parentQty);

		public async Task<BomExplosionResult> ExplodeAsync(int companyId, int parentItemId, decimal qty,
			BomExplosionOptions? options = null, CancellationToken cancellationToken = default)
		{
			var opt = options ?? new BomExplosionOptions();
			if (companyId <= 0) return BomExplosionResult.Fail("لم يتم تحديد الشركة");
			if (parentItemId <= 0) return BomExplosionResult.Fail("لم يتم تحديد الصنف");
			if (qty <= 0) return BomExplosionResult.Fail("الكمية يجب أن تكون أكبر من صفر");

			var graph = await LoadGraphAsync(companyId, cancellationToken);
			if (!graph.ByParent.ContainsKey(parentItemId))
				return new BomExplosionResult { Lines = Array.Empty<BomLine>() };

			var lines = new List<BomLine>();
			string? error = null;
			// The cycle guard is the PATH, not the visited set: a component may legitimately appear under two
			// different parents, but it may never appear beneath itself.
			var path = new HashSet<int> { parentItemId };

			void Walk(int itemId, decimal parentQty, int depth)
			{
				if (error != null || depth > opt.MaxDepth) { error ??= $"تجاوز عمق قائمة المواد المسموح ({opt.MaxDepth})"; return; }
				if (!graph.ByParent.TryGetValue(itemId, out var comps)) return;

				foreach (var c in comps)   // already ordered by SortOrder, then ComponentItemId — deterministic
				{
					if (error != null) return;
					var extended = Extend(c.Quantity, c.ScrapPct, parentQty);
					if (extended <= 0) continue;

					var (converted, convErr) = ConvertIfNeeded(graph, c, extended, opt);
					if (convErr != null) { error = convErr; return; }

					bool isMake = graph.ByParent.ContainsKey(c.ComponentItemId);
					bool descend = opt.Recursive && isMake;

					if (descend && !path.Add(c.ComponentItemId))
					{
						error = $"قائمة المواد تحتوي على دورة عند الصنف #{c.ComponentItemId}";
						return;
					}

					// A make-item that we DESCEND into is a production step, not a material to consume, so it is
					// not emitted as a line — its own components are. A make-item we do NOT descend into is
					// emitted, which is how a stocked semi-finished stays a thing you consume.
					if (descend)
					{
						Walk(c.ComponentItemId, converted, depth + 1);
						path.Remove(c.ComponentItemId);
					}
					else
					{
						lines.Add(new BomLine
						{
							ComponentItemId = c.ComponentItemId, Quantity = converted, UoMId = c.UoMId,
							SortOrder = c.SortOrder, Level = depth, IsMakeItem = isMake,
						});
					}
				}
			}

			Walk(parentItemId, qty, 1);
			return error != null ? BomExplosionResult.Fail(error) : new BomExplosionResult { Lines = lines };
		}

		// ===================================================================================================
		// THE MRP SHAPE — this IS RunMrpAsync's `Require`, moved here verbatim in behaviour.
		//
		// Preserved exactly: gross accumulation per item across all demands; netting against a RUNNING
		// `available` map that is decremented as the walk descends; explosion of the NET shortage only; scrap
		// applied at every level; the first (shallowest) depth recorded as the item's level; the depth cap; and
		// the make-item flag derived from "has a BOM". The one deliberate change is that quantities are now
		// rounded by the canonical policy instead of propagating unrounded — proven and documented by test.
		// ===================================================================================================
		public async Task<BomRequirementsResult> BomRequirementsAsync(int companyId,
			IReadOnlyList<(int itemId, decimal qty)> demands, Dictionary<int, decimal> available,
			BomExplosionOptions? options = null, CancellationToken cancellationToken = default)
		{
			var opt = options ?? new BomExplosionOptions { Recursive = true, NetAgainstOnHand = true };
			if (companyId <= 0) return BomRequirementsResult.Fail("لم يتم تحديد الشركة");
			ArgumentNullException.ThrowIfNull(available);
			if (demands == null || demands.Count == 0) return new BomRequirementsResult();

			var graph = await LoadGraphAsync(companyId, cancellationToken);
			var rows = new Dictionary<int, BomRequirement>();
			var level = new Dictionary<int, int>();
			string? error = null;
			var path = new HashSet<int>();

			void Require(int itemId, decimal qty, int depth)
			{
				if (error != null) return;
				if (depth > opt.MaxDepth || qty <= 0) return;

				bool isMake = graph.ByParent.ContainsKey(itemId);
				if (!rows.TryGetValue(itemId, out var r))
				{
					r = new BomRequirement { ItemId = itemId, IsMakeItem = isMake };
					rows[itemId] = r; level[itemId] = depth;
				}
				if (depth < level[itemId]) level[itemId] = depth;
				r.Gross += qty;

				decimal net = qty;
				if (opt.NetAgainstOnHand)
				{
					decimal avail = available.TryGetValue(itemId, out var a) ? a : 0m;
					decimal use = Math.Min(Math.Max(avail, 0m), qty);
					available[itemId] = avail - use;
					net = qty - use;
				}
				r.Net += net;

				if (net <= 0 || !opt.Recursive || !graph.ByParent.TryGetValue(itemId, out var comps)) return;
				if (!path.Add(itemId)) { error = $"قائمة المواد تحتوي على دورة عند الصنف #{itemId}"; return; }
				foreach (var c in comps)
				{
					if (error != null) break;
					var extended = Extend(c.Quantity, c.ScrapPct, net);
					var (converted, convErr) = ConvertIfNeeded(graph, c, extended, opt);
					if (convErr != null) { error = convErr; break; }
					Require(c.ComponentItemId, converted, depth + 1);
				}
				path.Remove(itemId);
			}

			foreach (var d in demands) Require(d.itemId, d.qty, 0);
			if (error != null) return BomRequirementsResult.Fail(error);

			foreach (var r in rows.Values) r.Level = level[r.ItemId];
			return new BomRequirementsResult { Requirements = rows.Values.ToList() };
		}

		// ---- shared internals ----------------------------------------------------------------------------

		/// Converts one component quantity into the component's base unit, using the SAME rows and the SAME rule
		/// as StockService: a conversion from the recipe's unit to the item's base unit must EXIST. A missing
		/// conversion is refused rather than silently treated as factor 1 — the identical decision StockService
		/// records ("never the old silent factor-1, which would deduct a wrong base quantity").
		private static (decimal qty, string? error) ConvertIfNeeded(BomGraph graph, BomComponent c, decimal extended, BomExplosionOptions opt)
		{
			if (!opt.ConvertToBaseUoM) return (extended, null);
			if (c.UoMId == null) return (extended, null);
			if (!graph.BaseUoM.TryGetValue(c.ComponentItemId, out var baseUoM)) return (extended, null);
			if (c.UoMId == baseUoM) return (extended, null);
			if (!graph.Factors.TryGetValue((c.ComponentItemId, c.UoMId.Value), out var factor))
				return (0m, $"لا يوجد تحويل وحدة معرَّف للمكوّن #{c.ComponentItemId} من الوحدة المطلوبة إلى الوحدة الأساس");
			return (RoundQty(extended * factor), null);
		}

		private sealed record BomComponent(int ComponentItemId, decimal Quantity, decimal ScrapPct, int? UoMId, int SortOrder);

		private sealed class BomGraph
		{
			public Dictionary<int, List<BomComponent>> ByParent = new();
			public Dictionary<int, int> BaseUoM = new();
			public Dictionary<(int itemId, int fromUoM), decimal> Factors = new();
		}

		/// Loads the company's component graph in ONE round trip, the way MRP already did. Recursion over a
		/// preloaded graph is what keeps a deep explosion from becoming N+1 queries, and it is also what makes
		/// the cycle guard cheap.
		///
		/// COMPANY ISOLATION: every read below is filtered by companyId. ItemComponent is NOT covered by the
		/// global company query filters (Item and Warehouse are), so this explicit predicate is the ONLY thing
		/// standing between one tenant's recipe and another's — the same hole that let a work order be built
		/// from another company's BOM before it was closed.
		private async Task<BomGraph> LoadGraphAsync(int companyId, CancellationToken ct)
		{
			var comps = await _db.ItemComponents.AsNoTracking()
				.Where(c => c.CompanyID == companyId)
				.OrderBy(c => c.SortOrder).ThenBy(c => c.ComponentItemId)
				.Select(c => new { c.ParentItemId, c.ComponentItemId, c.Quantity, c.ScrapPct, c.UoMId, c.SortOrder })
				.ToListAsync(ct);

			var graph = new BomGraph();
			foreach (var c in comps)
			{
				if (!graph.ByParent.TryGetValue(c.ParentItemId, out var list))
					graph.ByParent[c.ParentItemId] = list = new List<BomComponent>();
				list.Add(new BomComponent(c.ComponentItemId, c.Quantity, c.ScrapPct, c.UoMId, c.SortOrder));
			}

			var ids = comps.Select(c => c.ComponentItemId).Distinct().ToList();
			if (ids.Count > 0)
			{
				graph.BaseUoM = await _db.Items.AsNoTracking()
					.Where(i => i.CompanyID == companyId && ids.Contains(i.ID))
					.ToDictionaryAsync(i => i.ID, i => i.BaseUoMId, ct);

				var convs = await _db.UoMConversions.AsNoTracking()
					.Where(v => ids.Contains(v.ItemId))
					.Select(v => new { v.ItemId, v.FromUoMId, v.ToUoMId, v.Factor })
					.ToListAsync(ct);
				foreach (var v in convs)
					if (graph.BaseUoM.TryGetValue(v.ItemId, out var b) && v.ToUoMId == b)
						graph.Factors[(v.ItemId, v.FromUoMId)] = v.Factor;
			}
			return graph;
		}
	}
}
