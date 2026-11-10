using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H44 — the central kitchen. The rules are HospitalKitchenRules' (no database).
	//   · A recipe's standard cost: its bill of materials (IBomExplosionService, the ONE place a recipe becomes
	//     quantities) × each ingredient's latest average cost.
	//   · Today: the rounds (patients' trays from H43; staff and cafeteria meals the kitchen counts), the trays by diet,
	//     and a recipe card. Ingredients leave the kitchen store into the pool account (`admit`), as production or as
	//     recorded waste, through IStockService — so the pool's ingredient line IS the store's issues.
	//   · The month: the pool is what was booked to the pool account (by the source that booked it), the consumers are
	//     the wards' trays and the destinations' counted meals, each × its recipe's standard cost; the allocation
	//     splits the whole pool by that weight (to the last fils) and posts ONE journal — debit each consumer's
	//     account on its cost centre, credit the pool — once per month (`manage`, owner ruling: a real journal).
	// ==========================================================================================
	public sealed record HospitalRecipeLine(int ItemId, string Code, string Name, string NameEn, decimal Qty, decimal UnitCost, decimal Cost);
	public sealed record HospitalRecipeCard(int ItemId, string Name, string NameEn, IReadOnlyList<HospitalRecipeLine> Lines, decimal Total);
	public sealed record HospitalKitchenRound(string Round, int Patients, int Staff, int Cafeteria, string State);
	public sealed record HospitalKitchenShare(string Stream, int? WardId, int? DestinationId, string Name, string NameEn, int Meals, decimal StandardCost, int? CostCenterId, decimal Allocated);
	public sealed record HospitalKitchenPoolSource(string Source, decimal Amount);
	public sealed record HospitalKitchenMonth(DateTime Month, IReadOnlyList<HospitalKitchenPoolSource> Pool, decimal PoolTotal, IReadOnlyList<HospitalKitchenShare> Shares,
		int Meals, decimal Theoretical, decimal Actual, decimal Waste, HospitalKitchenAllocation? Posted, string? PostedEntryNo);

	public sealed record HospitalCafeOutlet(int DestinationId, string Name, string NameEn, string? Branch, string? BranchAr, decimal Sales, int Orders);
	public sealed record HospitalCafeteria(IReadOnlyList<HospitalCafeOutlet> Outlets, IReadOnlyList<(string Method, decimal Amount)> Payments, decimal Sales, decimal MealCost);

	public static class HospitalKitchenRules
	{
		public const string AllocationSource = "HospitalKitchenAllocation";

		/// A round's state from its trays: none sent → planned, some served → serving, every sent one served → served.
		public static string RoundState(int sent, int served) => sent == 0 ? "Planned" : served >= sent ? "Served" : "Serving";

		/// Splits `pool` over the weights (meals × standard cost), rounded to `decimals`; the rounding remainder goes to the
		/// largest share, so the split always adds up to the pool exactly. Null when nothing carries a weight.
		public static IReadOnlyList<decimal>? Split(decimal pool, IReadOnlyList<decimal> weights, int decimals = 3)
		{
			decimal total = weights.Sum();
			if (total <= 0 || weights.Any(w => w < 0)) return null;
			var parts = weights.Select(w => Math.Round(pool * w / total, decimals, MidpointRounding.AwayFromZero)).ToArray();
			int big = Array.IndexOf(weights.ToArray(), weights.Max());
			parts[big] += pool - parts.Sum();
			return parts;
		}

		/// The journal's source label for a pool line: where that cost was booked from.
		public static string SourceOf(string? sourceType) => sourceType switch
		{
			"Inventory" => "Inventory",
			var s when s != null && s.StartsWith("Payroll", StringComparison.OrdinalIgnoreCase) => "Payroll",
			var s when s != null && (s.Contains("Asset", StringComparison.OrdinalIgnoreCase) || s.Contains("Depreciation", StringComparison.OrdinalIgnoreCase)) => "FixedAssets",
			_ => "Expenses",
		};

		/// Why this month cannot be allocated, or null.
		public static string? AllocateRefusal(DateTime month, DateTime today, bool posted, bool accountsMapped, decimal pool, decimal weight)
		{
			if (month.Day != 1) return "Choose the month";
			if (month > today) return "A month is allocated once it has begun";
			if (posted) return "This month is already allocated";
			if (!accountsMapped) return "Map the kitchen's four accounts in its setting first";
			if (pool <= 0) return "Nothing is booked to the kitchen cost pool this month";
			if (weight <= 0) return "No meal with a costed recipe this month";
			return null;
		}
	}

	public sealed class HospitalKitchenService
	{
		public const string NoTables = "The kitchen tables are not created yet (hospital_h44_kitchen.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IBomExplosionService _bom;
		private readonly IStockService _stock;
		private readonly IHospitalLedger _ledger;
		private readonly HospitalDietaryService _diet;

		public HospitalKitchenService(CrossDbContext db, IHospitalAccessService access, IBomExplosionService bom, IStockService stock, IHospitalLedger ledger)
		{ _db = db; _access = access; _bom = bom; _stock = stock; _ledger = ledger; _diet = new HospitalDietaryService(db, access); }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private static DateTime Today(DateTime nowUtc) => HospitalDietaryService.Today(nowUtc);

		public async Task<HospitalKitchenSetting?> SettingAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.Set<HospitalKitchenSetting>().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId, ct);

		public async Task<IReadOnlyDictionary<string, int>> DietRecipesAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.Set<HospitalDietRecipe>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId).ToDictionaryAsync(r => r.Diet, r => r.RecipeItemId, ct);

		public async Task<IReadOnlyList<HospitalKitchenDestination>> DestinationsAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.Set<HospitalKitchenDestination>().AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).OrderBy(d => d.Stream).ThenBy(d => d.Name).ToListAsync(ct);

		/// The latest average cost of each item: its stock's value over its quantity, else its opening cost.
		private async Task<Dictionary<int, decimal>> CostsAsync(BusinessContext ctx, IReadOnlyCollection<int> items, CancellationToken ct)
		{
			var bal = await _db.StockBalances.AsNoTracking().Where(b => b.CompanyID == ctx.CompanyId && items.Contains(b.ItemId))
				.GroupBy(b => b.ItemId).Select(g => new { g.Key, V = g.Sum(x => x.TotalValue), Q = g.Sum(x => x.QtyOnHand), A = g.Max(x => x.AvgCost) }).ToListAsync(ct);
			var open = await _db.Items.AsNoTracking().Where(i => items.Contains(i.ID)).Select(i => new { i.ID, i.OpeningCost }).ToDictionaryAsync(i => i.ID, i => i.OpeningCost ?? 0, ct);
			return items.ToDictionary(i => i, i =>
			{
				var b = bal.FirstOrDefault(x => x.Key == i);
				return b != null && b.Q > 0 ? Math.Round(b.V / b.Q, 6) : b?.A > 0 ? b.A : open.GetValueOrDefault(i);
			});
		}

		/// A recipe's card: its ingredients for one meal, their cost, and the standard cost.
		public async Task<HospitalRecipeCard?> RecipeAsync(BusinessContext ctx, int recipeItemId, CancellationToken ct = default)
		{
			var item = await _db.Items.AsNoTracking().Where(i => i.ID == recipeItemId && i.CompanyID == ctx.CompanyId).Select(i => new { i.ID, i.Name, i.NameEn }).FirstOrDefaultAsync(ct);
			if (item == null) return null;
			var x = await _bom.ExplodeAsync(ctx.CompanyId, recipeItemId, 1m, new BomExplosionOptions { ConvertToBaseUoM = true }, ct);
			if (!x.Ok) return new HospitalRecipeCard(item.ID, item.Name, item.NameEn ?? item.Name, Array.Empty<HospitalRecipeLine>(), 0);
			var ids = x.Lines.Select(l => l.ComponentItemId).Distinct().ToList();
			var names = await _db.Items.AsNoTracking().Where(i => ids.Contains(i.ID)).Select(i => new { i.ID, i.ItemCode, i.Name, i.NameEn }).ToDictionaryAsync(i => i.ID, ct);
			var costs = await CostsAsync(ctx, ids, ct);
			var lines = x.Lines.OrderBy(l => l.SortOrder).Select(l =>
			{
				var n = names.GetValueOrDefault(l.ComponentItemId); var c = costs.GetValueOrDefault(l.ComponentItemId);
				return new HospitalRecipeLine(l.ComponentItemId, n?.ItemCode ?? "", n?.Name ?? "", n?.NameEn ?? n?.Name ?? "", l.Quantity, c, Math.Round(l.Quantity * c, 3));
			}).ToList();
			return new HospitalRecipeCard(item.ID, item.Name, item.NameEn ?? item.Name, lines, lines.Sum(l => l.Cost));
		}

		private async Task<decimal> StandardCostAsync(BusinessContext ctx, int? recipeItemId, Dictionary<int, decimal> memo, CancellationToken ct)
		{
			if (recipeItemId is not int id) return 0;
			if (!memo.TryGetValue(id, out var c)) memo[id] = c = (await RecipeAsync(ctx, id, ct))?.Total ?? 0;
			return c;
		}

		/// Today's rounds: patients' trays (H43) and the meals counted for staff and the cafeteria.
		public async Task<IReadOnlyList<HospitalKitchenRound>> RoundsAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			var day = Today(nowUtc);
			var trays = await _diet.DayByRoundAsync(ctx, day, ct);
			var counts = await (from c in _db.Set<HospitalKitchenCount>().AsNoTracking()
								join d in _db.Set<HospitalKitchenDestination>().AsNoTracking() on c.DestinationId equals d.ID
								where c.CompanyId == ctx.CompanyId && c.Day == day
								group c by new { c.Round, d.Stream } into g
								select new { g.Key.Round, g.Key.Stream, N = g.Sum(x => x.Meals) }).ToListAsync(ct);
			return HospitalMealRound.All.Select(r =>
			{
				var t = trays.GetValueOrDefault(r);
				return new HospitalKitchenRound(r, t.Sent, counts.Where(c => c.Round == r && c.Stream == HospitalKitchenStream.Staff).Sum(c => c.N),
					counts.Where(c => c.Round == r && c.Stream == HospitalKitchenStream.Cafeteria).Sum(c => c.N), HospitalKitchenRules.RoundState(t.Sent, t.Served));
			}).ToList();
		}

		/// A month: the pool by source, every consumer's meals and standard cost, the split, theoretical vs actual, and
		/// the allocation already posted (its split as posted) if there is one.
		public async Task<HospitalKitchenMonth> MonthAsync(BusinessContext ctx, DateTime month, CancellationToken ct = default)
		{
			month = new DateTime(month.Year, month.Month, 1);
			DateTime next = month.AddMonths(1);
			var s = await SettingAsync(ctx, ct);
			var posted = await _db.Set<HospitalKitchenAllocation>().AsNoTracking().FirstOrDefaultAsync(a => a.CompanyId == ctx.CompanyId && a.Month == month, ct);
			// the pool: what was booked to the pool account this month, by source (the allocation's own credit excluded)
			var pool = new List<HospitalKitchenPoolSource>();
			if (s?.PoolAccountId is int pa)
			{
				var rows = await (from l in _db.JournalEntryLines.AsNoTracking()
								  join e in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
								  where e.CompanyID == ctx.CompanyId && e.Status == "Posted" && l.AccountId == pa && e.EntryDate >= month && e.EntryDate < next && e.SourceType != HospitalKitchenRules.AllocationSource
								  group l by e.SourceType into g
								  select new { Source = g.Key, Amount = g.Sum(x => x.Debit - x.Credit) }).ToListAsync(ct);
				pool = rows.GroupBy(r => HospitalKitchenRules.SourceOf(r.Source)).Select(g => new HospitalKitchenPoolSource(g.Key, g.Sum(x => x.Amount))).Where(p => p.Amount != 0).OrderByDescending(p => p.Amount).ToList();
			}
			decimal poolTotal = posted?.PoolTotal ?? pool.Sum(p => p.Amount);

			List<HospitalKitchenShare> shares;
			if (posted != null)
				shares = (await _db.Set<HospitalKitchenAllocationLine>().AsNoTracking().Where(l => l.AllocationId == posted.ID).OrderBy(l => l.ID).ToListAsync(ct))
					.Select(l => new HospitalKitchenShare(l.Stream, l.WardId, l.DestinationId, l.Name, l.NameEn ?? l.Name, l.Meals, l.StandardCost, l.CostCenterId, l.Allocated)).ToList();
			else
			{
				var memo = new Dictionary<int, decimal>();
				var recipes = await DietRecipesAsync(ctx, ct);
				var dests = await DestinationsAsync(ctx, ct);
				var wards = await _db.HospitalWards.AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId).ToDictionaryAsync(w => w.ID, ct);
				var trays = await _db.Set<HospitalMealOrder>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.Day >= month && m.Day < next && m.Status != HospitalMealStatus.Held)
					.GroupBy(m => new { m.WardId, m.Diet }).Select(g => new { g.Key.WardId, g.Key.Diet, N = g.Count() }).ToListAsync(ct);
				var raw = new List<HospitalKitchenShare>();
				foreach (var w in trays.GroupBy(t => t.WardId))
				{
					int n = w.Sum(x => x.N); decimal cost = 0;
					foreach (var d in w) cost += d.N * await StandardCostAsync(ctx, recipes.TryGetValue(d.Diet, out var r) ? r : recipes.TryGetValue(HospitalDiet.Regular, out var rr) ? rr : null, memo, ct);
					var ward = wards.GetValueOrDefault(w.Key); var dest = dests.FirstOrDefault(x => x.Stream == HospitalKitchenStream.Patient && x.WardId == w.Key);
					raw.Add(new HospitalKitchenShare(HospitalKitchenStream.Patient, w.Key, dest?.ID, ward?.Name ?? "", ward?.NameEn ?? ward?.Name ?? "", n, n == 0 ? 0 : Math.Round(cost / n, 3), dest?.CostCenterId, 0));
				}
				var counted = await _db.Set<HospitalKitchenCount>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && c.Day >= month && c.Day < next)
					.GroupBy(c => c.DestinationId).Select(g => new { g.Key, N = g.Sum(x => x.Meals) }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				foreach (var d in dests.Where(d => d.Stream != HospitalKitchenStream.Patient && counted.GetValueOrDefault(d.ID) > 0))
					raw.Add(new HospitalKitchenShare(d.Stream, null, d.ID, d.Name, d.NameEn ?? d.Name, counted[d.ID], await StandardCostAsync(ctx, d.RecipeItemId, memo, ct), d.CostCenterId, 0));
				var split = HospitalKitchenRules.Split(poolTotal, raw.Select(x => x.Meals * x.StandardCost).ToList());
				shares = raw.Select((x, i) => x with { Allocated = split?[i] ?? 0 }).ToList();
			}
			// theoretical vs actual: recipes × meals against what left the kitchen store (as production, and as waste)
			decimal theoretical = shares.Sum(x => x.Meals * x.StandardCost), actual = 0, waste = 0;
			if (s?.KitchenWarehouseId is int wh)
			{
				var mv = await _db.StockMovements.AsNoTracking().Where(m => m.CompanyID == ctx.CompanyId && m.WarehouseId == wh && m.Direction == -1 && m.CreatedAt >= month && m.CreatedAt < next
					&& (m.SourceType == HospitalKitchenIssueKind.Production || m.SourceType == HospitalKitchenIssueKind.Waste)).GroupBy(m => m.SourceType).Select(g => new { g.Key, V = g.Sum(x => x.TotalCost) }).ToListAsync(ct);
				actual = mv.Sum(x => x.V); waste = mv.Where(x => x.Key == HospitalKitchenIssueKind.Waste).Sum(x => x.V);
			}
			string? entryNo = posted?.JournalEntryId is int je ? await _db.JournalEntries.AsNoTracking().Where(e => e.ID == je).Select(e => e.EntryNo).FirstOrDefaultAsync(ct) : null;
			return new HospitalKitchenMonth(month, pool, poolTotal, shares, shares.Sum(x => x.Meals), theoretical, actual, waste, posted, entryNo);
		}

		/// The cafeteria in a month: each outlet's paid point-of-sale orders (net of tax), the payment mix, and its share of
		/// the kitchen (the month's allocation to the cafeteria). An outlet is a cafeteria destination linked to a POS branch.
		public async Task<HospitalCafeteria> CafeteriaAsync(BusinessContext ctx, HospitalKitchenMonth m, CancellationToken ct = default)
		{
			DateTime from = m.Month, to = m.Month.AddMonths(1);
			var outlets = (await DestinationsAsync(ctx, ct)).Where(d => d.Stream == HospitalKitchenStream.Cafeteria).ToList();
			var branches = outlets.Where(o => o.PosBranchId != null).Select(o => o.PosBranchId!.Value).Distinct().ToList();
			var names = await _db.Branches.AsNoTracking().Where(b => branches.Contains(b.ID) && b.CompanyID == ctx.CompanyId).Select(b => new { b.ID, b.Name, b.NameAr }).ToDictionaryAsync(b => b.ID, ct);
			var paid = _db.PosOrders.AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && branches.Contains(o.BranchId) && o.Status == "Paid" && o.ClosedAt >= from && o.ClosedAt < to);
			var sales = await paid.GroupBy(o => o.BranchId).Select(g => new { g.Key, Net = g.Sum(x => x.GrandTotal - x.TaxTotal), N = g.Count() }).ToDictionaryAsync(x => x.Key, ct);
			var pays = await (from p in _db.PosPayments.AsNoTracking() join o in paid on p.OrderId equals o.ID group p by p.PaymentMethod into g select new { g.Key, A = g.Sum(x => x.Amount) }).ToListAsync(ct);
			var rows = outlets.Select(o =>
			{
				var b = o.PosBranchId is int bid ? names.GetValueOrDefault(bid) : null; var sl = o.PosBranchId is int bb ? sales.GetValueOrDefault(bb) : null;
				return new HospitalCafeOutlet(o.ID, o.Name, o.NameEn ?? o.Name, b?.Name, b?.NameAr, sl?.Net ?? 0, sl?.N ?? 0);
			}).ToList();
			// two outlets on one branch would count its sales twice: the total counts each branch once
			decimal total = sales.Values.Sum(x => x.Net);
			return new HospitalCafeteria(rows, pays.OrderByDescending(x => x.A).Select(x => (x.Key, x.A)).ToList(), total,
				m.Shares.Where(x => x.Stream == HospitalKitchenStream.Cafeteria).Sum(x => x.Allocated));
		}

		public async Task<HospitalResult> SaveSettingAsync(BusinessContext ctx, HospitalKitchenSetting input, IReadOnlyDictionary<string, int?> dietRecipes, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var accounts = new[] { input.PoolAccountId, input.PatientMealsAccountId, input.StaffMealsAccountId, input.CafeteriaCogsAccountId }.Where(a => a != null).Select(a => a!.Value).ToList();
			if (accounts.Count != accounts.Distinct().Count()) return HospitalResult.Fail("Each of the four accounts is a different account");
			// the meals' three accounts are expenses; the pool is a clearing account (an asset or an expense) — all active posting accounts
			foreach (var a in new[] { input.PatientMealsAccountId, input.StaffMealsAccountId, input.CafeteriaCogsAccountId })
				if (a is int id && !await HospitalClaimAccounts.OfKindAsync(_db, ctx, id, ct, HospitalClaimAccounts.Expense))
					return HospitalResult.Fail("The meals' accounts are active expense posting accounts");
			if (input.PoolAccountId is int pa && !await HospitalClaimAccounts.OfKindAsync(_db, ctx, pa, ct, HospitalClaimAccounts.Asset, HospitalClaimAccounts.Expense))
				return HospitalResult.Fail("The kitchen cost pool is an active asset or expense posting account");
			if (input.KitchenWarehouseId is int w && !await _db.Warehouses.AnyAsync(x => x.ID == w && x.CompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var recipeIds = dietRecipes.Values.Where(v => v != null).Select(v => v!.Value).Distinct().ToList();
			if (recipeIds.Count > 0 && await _db.Items.CountAsync(i => recipeIds.Contains(i.ID) && i.CompanyID == ctx.CompanyId, ct) != recipeIds.Count) return HospitalResult.Fail("not_found");
			try
			{
				var s = await _db.Set<HospitalKitchenSetting>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId, ct);
				if (s == null) _db.Add(s = new HospitalKitchenSetting { CompanyId = ctx.CompanyId });
				s.KitchenWarehouseId = input.KitchenWarehouseId; s.PoolAccountId = input.PoolAccountId; s.PatientMealsAccountId = input.PatientMealsAccountId;
				s.StaffMealsAccountId = input.StaffMealsAccountId; s.CafeteriaCogsAccountId = input.CafeteriaCogsAccountId; s.UpdatedAt = nowUtc;
				var rows = await _db.Set<HospitalDietRecipe>().Where(r => r.CompanyId == ctx.CompanyId).ToListAsync(ct);
				foreach (var (diet, item) in dietRecipes.Where(d => HospitalDiet.All.Contains(d.Key)))
				{
					var row = rows.FirstOrDefault(r => r.Diet == diet);
					if (item is int i) { if (row == null) _db.Add(new HospitalDietRecipe { CompanyId = ctx.CompanyId, Diet = diet, RecipeItemId = i }); else row.RecipeItemId = i; }
					else if (row != null) _db.Remove(row);
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> SaveDestinationAsync(BusinessContext ctx, HospitalKitchenDestination input, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalKitchenStream.All.Contains(input.Stream)) return HospitalResult.Fail("Choose who eats there");
			if (input.Stream == HospitalKitchenStream.Patient ? input.WardId == null : string.IsNullOrWhiteSpace(input.Name)) return HospitalResult.Fail(input.Stream == HospitalKitchenStream.Patient ? "Choose the ward" : "Write its name");
			try
			{
				var d = input.ID > 0 ? await _db.Set<HospitalKitchenDestination>().FirstOrDefaultAsync(x => x.ID == input.ID && x.CompanyId == ctx.CompanyId, ct) : null;
				if (input.ID > 0 && d == null) return HospitalResult.Fail("not_found");
				if (d == null) _db.Add(d = new HospitalKitchenDestination { CompanyId = ctx.CompanyId, CreatedAt = nowUtc });
				d.Stream = input.Stream; d.WardId = input.Stream == HospitalKitchenStream.Patient ? input.WardId : null;
				if (d.WardId is int wid)
				{
					var w = await _db.HospitalWards.AsNoTracking().FirstOrDefaultAsync(x => x.ID == wid && x.CompanyId == ctx.CompanyId, ct);
					if (w == null) return HospitalResult.Fail("not_found");
					d.Name = w.Name; d.NameEn = w.NameEn;
				}
				else { d.Name = input.Name.Trim(); d.NameEn = string.IsNullOrWhiteSpace(input.NameEn) ? null : input.NameEn.Trim(); }
				d.RecipeItemId = input.RecipeItemId; d.CostCenterId = input.CostCenterId; d.PosBranchId = input.Stream == HospitalKitchenStream.Cafeteria ? input.PosBranchId : null; d.IsActive = input.IsActive;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(d.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
		}

		/// The meals the kitchen served a staff group or outlet in a round today (entered again, it is corrected).
		public async Task<HospitalResult> CountAsync(BusinessContext ctx, int destinationId, string round, int meals, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalMealRound.All.Contains(round)) return HospitalResult.Fail("Choose the round");
			if (meals < 0 || meals > 100000) return HospitalResult.Fail("A count is a number of meals");
			try
			{
				var d = await _db.Set<HospitalKitchenDestination>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == destinationId && x.CompanyId == ctx.CompanyId && x.IsActive, ct);
				if (d == null || d.Stream == HospitalKitchenStream.Patient) return HospitalResult.Fail("Patients' trays come from the wards' meal orders");
				var day = Today(nowUtc);
				var c = await _db.Set<HospitalKitchenCount>().FirstOrDefaultAsync(x => x.DestinationId == d.ID && x.Day == day && x.Round == round, ct);
				if (c == null) _db.Add(c = new HospitalKitchenCount { CompanyId = ctx.CompanyId, DestinationId = d.ID, Day = day, Round = round });
				c.Meals = meals; c.RecordedByEmployeeId = employeeId; c.RecordedAt = nowUtc;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}

		/// Ingredients leave the kitchen store into the pool account — for production, or as recorded waste.
		public async Task<HospitalResult> IssueAsync(BusinessContext ctx, int itemId, decimal qty, bool waste, string? note, string? userId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Admit, ct)) return HospitalResult.Fail("forbidden");
			if (qty <= 0) return HospitalResult.Fail("A quantity is more than zero");
			if (waste && string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write why it was wasted");
			var s = await SettingAsync(ctx, ct);
			if (s?.KitchenWarehouseId is not int wh || s.PoolAccountId is not int pool) return HospitalResult.Fail("Set the kitchen store and its cost pool account first");
			if (!await _db.Items.AnyAsync(i => i.ID == itemId && i.CompanyID == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var (ok, error, _) = await _stock.PostMovementAsync(ctx.CompanyId, new MovementRequest
			{
				Date = nowUtc, ItemId = itemId, WarehouseId = wh, Direction = -1, Qty = qty, SourceType = waste ? HospitalKitchenIssueKind.Waste : HospitalKitchenIssueKind.Production,
				PostToGl = true, CounterAccountOverride = pool, Notes = waste ? "Kitchen waste: " + note!.Trim() : "Issued to kitchen production" + (string.IsNullOrWhiteSpace(note) ? "" : ": " + note.Trim()),
			}, userId);
			return ok ? HospitalResult.Success(itemId) : HospitalResult.Fail("The kitchen store: {0}", error ?? "");
		}

		/// Posts a month's allocation: one journal (each consumer's account on its cost centre, the pool credited).
		public async Task<HospitalResult> AllocateAsync(BusinessContext ctx, DateTime month, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			month = new DateTime(month.Year, month.Month, 1);
			try
			{
				var s = await SettingAsync(ctx, ct);
				var m = await MonthAsync(ctx, month, ct);
				bool mapped = s is { PoolAccountId: not null, PatientMealsAccountId: not null, StaffMealsAccountId: not null, CafeteriaCogsAccountId: not null };
				if (HospitalKitchenRules.AllocateRefusal(month, Today(nowUtc), m.Posted != null, mapped, m.PoolTotal, m.Theoretical) is string why) return HospitalResult.Fail(why);
				int AccountOf(string stream) => stream switch { HospitalKitchenStream.Patient => s!.PatientMealsAccountId!.Value, HospitalKitchenStream.Staff => s!.StaffMealsAccountId!.Value, _ => s!.CafeteriaCogsAccountId!.Value };
				var label = month.ToString("yyyy-MM");
				var lines = m.Shares.Where(x => x.Allocated > 0).Select(x => new HospitalLedgerLine(AccountOf(x.Stream), x.Allocated, 0, "Kitchen " + label + " · " + x.NameEn, x.CostCenterId)).ToList();
				lines.Add(new HospitalLedgerLine(s!.PoolAccountId!.Value, 0, m.PoolTotal, "Kitchen cost pool " + label));

				await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
				var a = new HospitalKitchenAllocation { CompanyId = ctx.CompanyId, Month = month, PoolTotal = m.PoolTotal, Meals = m.Meals, PostedAt = nowUtc, PostedByEmployeeId = employeeId };
				_db.Add(a); await _db.SaveChangesAsync(ct);
				// the entry is dated the month's last day, or today for the month still running
				var last = month.AddMonths(1).AddDays(-1); var date = last < Today(nowUtc) ? last : Today(nowUtc);
				var (ok, error, entryId) = await _ledger.PostAsync(ctx, date, "توزيع تكلفة المطبخ " + label, "Kitchen cost allocation " + label, HospitalKitchenRules.AllocationSource, a.ID, lines, ct);
				if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The journal: {0}", error ?? ""); }
				a.JournalEntryId = entryId;
				foreach (var x in m.Shares)
					_db.Add(new HospitalKitchenAllocationLine { AllocationId = a.ID, Stream = x.Stream, WardId = x.WardId, DestinationId = x.DestinationId, Name = x.Name, NameEn = x.NameEn,
						Meals = x.Meals, StandardCost = x.StandardCost, Allocated = x.Allocated, CostCenterId = x.CostCenterId });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync();
				return HospitalResult.Success(a.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("This month is already allocated"); }
		}
	}
}
