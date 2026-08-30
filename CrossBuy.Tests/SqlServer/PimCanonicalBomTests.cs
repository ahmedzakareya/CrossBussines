using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Pos;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CrossBuy.Tests.SqlServer
{
	// TAB-5 — THE CANONICAL BOM EXPLOSION.
	//
	// One recipe used to become six different sets of quantities. The maths agreed after an earlier batch, but
	// the ROUNDING did not — POS rounded AwayFromZero, the manufacturing paths took the C# default ToEven, and
	// MRP and standard costing did not round at all — and only one path even looked at the unit a recipe line
	// was written in. This suite holds the single answer: IBomExplosionService.
	//
	// WHY SQL SERVER. Half of these tests drive the real POS pay path and the real StockService, which reads the
	// balance it is about to change with UPDLOCK/HOLDLOCK hints that SQLite cannot parse. Proving quantities
	// against a substitute writer would not be proving them about the writer that actually runs, so the family
	// is SQL-Server-only and SKIPPED, never silently passed, without CROSSBUY_TEST_SQL.
	//
	// DATA SAFETY. Own probe database per run (CrossBuyProbe_PIMBOM_<guid>), created and dropped here. Never
	// CrossBuyDev, never CrossBuyDB2, never production, and not the shared platform fixture (RISK-036).
	[Collection(UatSqlProbeCollection.Name)]
	public class PimCanonicalBomTests : IAsyncLifetime
	{
		private const int Co = 1;
		private const int Foreign = 2;
		private const int Branch = 1;
		private static readonly DateTime D = new(2026, 8, 30);

		private readonly UatSqlProbeFixture _sql;
		private readonly ITestOutputHelper _out;
		public PimCanonicalBomTests(UatSqlProbeFixture sql, ITestOutputHelper output) { _sql = sql; _out = output; }
		private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

		private UatSqlProbeFixture.ProbeDatabase? _probe;
		private int _wh, _catId, _uomKg, _uomG;
		private int _flour, _oil, _cheese, _tomato, _sauce, _pizza;
		private int _midpointRaw, _midpointParent;      // the recipe that separates ToEven from AwayFromZero
		private int _cycleA, _cycleB, _deepTop;
		private int _foreignParent, _foreignComp;

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("PIMBOM");
			await ArrangeAsync();
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private ServiceProvider Graph(int companyId = Co)
		{
			var services = new ServiceCollection();
			services.AddLogging();
			services.AddLocalization();
			services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?> { ["Numbering:JvAllocationMode"] = "Ambient" })
				.Build());
			services.AddScoped(_ => _sql.ContextFor(_probe!, companyId));
			services.AddScoped<ICurrencyService, CurrencyService>();
			services.AddScoped<ICurrencyRounding, CurrencyRounding>();
			services.AddScoped<IFiscalPeriodService, FiscalPeriodService>();
			services.AddScoped<IJournalEntryService, JournalEntryService>();
			services.AddScoped<IStockService, StockService>();
			services.AddScoped<IEmployeeCostService, EmployeeCostService>();
			services.AddScoped<IEntityRegistry, EntityRegistry>();
			services.AddScoped<IBomExplosionService, BomExplosionService>();
			services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(new BusinessContext
			{
				CompanyId = companyId, EmployeeId = 1, UserId = "uat-pimbom",
				Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
			}));
			services.AddScoped<IBusinessEventService, BusinessEventService>();
			services.AddScoped<IManufService, ManufService>();
			return services.BuildServiceProvider();
		}

		private async Task ArrangeAsync()
		{
			using var db = _sql.ContextFor(_probe!, Co);

			db.Currencies.Add(new Currency { Code = "EGP", Name = "جنيه", NameEn = "Egyptian Pound", DecimalPlaces = 2 });
			await db.SaveChangesAsync();

			Account Acct(string code, string name, int companyId) =>
				new() { Code = code, Name = name, NameEn = name, CompanyID = companyId, AccountTypeId = 1, IsPostable = true, IsActive = true };
			var inv = Acct("110301", "المخزون", Co); var cogs = Acct("510101", "تكلفة المبيعات", Co);
			var wip = Acct("1105", "إنتاج", Co); var applied = Acct("520108", "مطبقة", Co);
			var variance = Acct("520109", "فروق", Co); var adj = Acct("520110", "تسويات", Co);
			var rev = Acct("410101", "إيرادات", Co); var cash = Acct("110101", "الصندوق", Co);
			var invF = Acct("110301", "Inv-Co2", Foreign); var cogsF = Acct("510101", "COGS-Co2", Foreign);
			db.Accounts.AddRange(inv, cogs, wip, applied, variance, adj, rev, cash, invF, cogsF);
			await db.SaveChangesAsync();

			var cat = new ItemCategory { Code = "BOM1", Name = "BOM1", NameEn = "BOM1", CompanyID = Co, InventoryAccountId = inv.ID, CogsAccountId = cogs.ID, AdjustmentAccountId = adj.ID };
			var catF = new ItemCategory { Code = "BOM2", Name = "BOM2", NameEn = "BOM2", CompanyID = Foreign, InventoryAccountId = invF.ID, CogsAccountId = cogsF.ID };
			db.ItemCategories.AddRange(cat, catF);
			await db.SaveChangesAsync();
			_catId = cat.ID;

			foreach (var c in new[] { Co, Foreign })
			{
				var fy = new FiscalYear { CompanyID = c, Name = "2026", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31), Status = "Open" };
				db.FiscalYears.Add(fy); await db.SaveChangesAsync();
				db.FiscalPeriods.Add(new FiscalPeriod { FiscalYearId = fy.ID, PeriodNo = 8, StartDate = new DateTime(2026, 8, 1), EndDate = new DateTime(2026, 8, 31), Status = "Open" });
			}
			await db.SaveChangesAsync();

			// UNITS: kilogram is the base, gram is the alternate. This is what makes the "500 g of flour when
			// flour is stocked in kg" case real rather than hypothetical.
			var kg = new UnitOfMeasure { Code = "KG", Name = "كجم", NameEn = "Kilogram", CompanyID = Co };
			var g = new UnitOfMeasure { Code = "G", Name = "جم", NameEn = "Gram", CompanyID = Co };
			db.UnitsOfMeasure.AddRange(kg, g);
			await db.SaveChangesAsync();
			_uomKg = kg.ID; _uomG = g.ID;

			var wh = new Warehouse { Code = "BOM-WH", Name = "BOM-WH", NameEn = "BOM-WH", CompanyID = Co, WarehouseType = "Main", AllowNegativeStock = false, IsActive = true };
			db.Warehouses.Add(wh);
			await db.SaveChangesAsync();
			_wh = wh.ID;

			int seq = 0;
			Item It(string code, int companyId, int catId, int baseUoM, bool composite = false, string? ctype = null) => new()
			{
				ItemCode = code, Barcode = "B" + code + (++seq), Name = code, NameEn = code, CompanyID = companyId,
				ItemCategoryId = catId, ItemType = "Stockable", BaseUoMId = baseUoM, IsActive = true,
				IsComposite = composite, CompositeType = ctype, ProductionMethod = "OrderBased",
			};
			var flour = It("FLOUR", Co, _catId, kg.ID);
			var oil = It("OIL", Co, _catId, kg.ID);
			var cheese = It("CHEESE", Co, _catId, kg.ID);
			var tomato = It("TOMATO", Co, _catId, kg.ID);
			var sauce = It("SAUCE", Co, _catId, kg.ID, composite: true, ctype: "Assembly");   // a MADE semi-finished
			var pizza = It("PIZZA", Co, _catId, kg.ID, composite: true, ctype: "Assembly");
			var midRaw = It("MID-RAW", Co, _catId, kg.ID);
			var midParent = It("MID-PARENT", Co, _catId, kg.ID, composite: true, ctype: "Bundle");
			var cycA = It("CYC-A", Co, _catId, kg.ID, composite: true, ctype: "Assembly");
			var cycB = It("CYC-B", Co, _catId, kg.ID, composite: true, ctype: "Assembly");
			var deepTop = It("DEEP-0", Co, _catId, kg.ID, composite: true, ctype: "Assembly");
			var fParent = It("CO2-PARENT", Foreign, catF.ID, kg.ID, composite: true, ctype: "Assembly");
			var fComp = It("CO2-COMP", Foreign, catF.ID, kg.ID);
			db.Items.AddRange(flour, oil, cheese, tomato, sauce, pizza, midRaw, midParent, cycA, cycB, deepTop, fParent, fComp);
			await db.SaveChangesAsync();
			_flour = flour.ID; _oil = oil.ID; _cheese = cheese.ID; _tomato = tomato.ID; _sauce = sauce.ID; _pizza = pizza.ID;
			_midpointRaw = midRaw.ID; _midpointParent = midParent.ID;
			_cycleA = cycA.ID; _cycleB = cycB.ID; _deepTop = deepTop.ID;
			_foreignParent = fParent.ID; _foreignComp = fComp.ID;

			// 1000 g = 1 kg, for flour only — so the "no conversion defined" case stays testable on another item.
			db.UoMConversions.Add(new UoMConversion { ItemId = flour.ID, FromUoMId = g.ID, ToUoMId = kg.ID, Factor = 0.001m });
			await db.SaveChangesAsync();

			db.ItemComponents.AddRange(
				// PIZZA -> 500 g flour (declared in GRAMS, stocked in KG), 0.02 oil, 0.15 cheese, 0.2 sauce
				new ItemComponent { CompanyID = Co, ParentItemId = _pizza, ComponentItemId = _flour, Quantity = 500m, ScrapPct = 0m, UoMId = g.ID, SortOrder = 1 },
				new ItemComponent { CompanyID = Co, ParentItemId = _pizza, ComponentItemId = _oil, Quantity = 0.02m, ScrapPct = 0m, SortOrder = 2 },
				new ItemComponent { CompanyID = Co, ParentItemId = _pizza, ComponentItemId = _cheese, Quantity = 0.15m, ScrapPct = 10m, SortOrder = 3 },
				new ItemComponent { CompanyID = Co, ParentItemId = _pizza, ComponentItemId = _sauce, Quantity = 0.2m, ScrapPct = 0m, SortOrder = 4 },
				// SAUCE -> tomato, with its OWN scrap. This is the second level.
				new ItemComponent { CompanyID = Co, ParentItemId = _sauce, ComponentItemId = _tomato, Quantity = 3m, ScrapPct = 20m, SortOrder = 1 },
				// the midpoint recipe: 0.12345 lands exactly on a 4dp midpoint
				new ItemComponent { CompanyID = Co, ParentItemId = _midpointParent, ComponentItemId = _midpointRaw, Quantity = 0.12345m, ScrapPct = 0m, SortOrder = 1 },
				// a cycle: A -> B -> A
				new ItemComponent { CompanyID = Co, ParentItemId = _cycleA, ComponentItemId = _cycleB, Quantity = 1m, ScrapPct = 0m, SortOrder = 1 },
				new ItemComponent { CompanyID = Co, ParentItemId = _cycleB, ComponentItemId = _cycleA, Quantity = 1m, ScrapPct = 0m, SortOrder = 1 },
				// the foreign tenant's recipe
				new ItemComponent { CompanyID = Foreign, ParentItemId = _foreignParent, ComponentItemId = _foreignComp, Quantity = 2m, ScrapPct = 0m, SortOrder = 1 });
			await db.SaveChangesAsync();

			// A chain deeper than the cap: DEEP-0 -> DEEP-1 -> ... -> DEEP-40
			int prev = deepTop.ID;
			for (int i = 1; i <= 40; i++)
			{
				var next = It($"DEEP-{i}", Co, _catId, kg.ID, composite: i < 40, ctype: i < 40 ? "Assembly" : null);
				db.Items.Add(next); await db.SaveChangesAsync();
				db.ItemComponents.Add(new ItemComponent { CompanyID = Co, ParentItemId = prev, ComponentItemId = next.ID, Quantity = 1m, ScrapPct = 0m, SortOrder = 1 });
				prev = next.ID;
			}
			await db.SaveChangesAsync();
		}

		private IBomExplosionService Bom(ServiceProvider sp) => sp.GetRequiredService<IBomExplosionService>();

		private async Task MustReceiveAsync(int itemId, decimal qty, decimal cost, int companyId = Co)
		{
			await using var sp = Graph(companyId);
			var (ok, err, _) = await sp.GetRequiredService<IStockService>().PostMovementAsync(companyId, new MovementRequest
			{ Date = D, ItemId = itemId, WarehouseId = _wh, Direction = 1, Qty = qty, UnitCostInBase = cost, SourceType = "Opening", PostToGl = true }, "uat");
			Assert.True(ok, $"receipt failed for {itemId}: {err}");
		}

		// ===================================================================================================
		// 1 — SINGLE-LEVEL PIZZA. The default, and what every caller except MRP does.
		// ===================================================================================================
		[SkippableFact]
		public async Task Single_level_pizza_explodes_into_its_direct_ingredients()
		{
			Ready();
			await using var sp = Graph();
			var r = await Bom(sp).ExplodeAsync(Co, _pizza, 1m);
			Assert.True(r.Ok, r.Error);

			foreach (var l in r.Lines) _out.WriteLine($"[1] item={l.ComponentItemId} qty={l.Quantity} level={l.Level} make={l.IsMakeItem}");

			Assert.Equal(4, r.Lines.Count);
			// SAUCE is a made item and is still EMITTED, not exploded — single level means "consume the sauce".
			var sauce = r.Lines.Single(l => l.ComponentItemId == _sauce);
			Assert.True(sauce.IsMakeItem);
			Assert.Equal(0.2m, sauce.Quantity);
			Assert.All(r.Lines, l => Assert.Equal(1, l.Level));
		}

		// ===================================================================================================
		// 2 + 3 — PIZZA -> SAUCE -> TOMATO, with scrap applied at EVERY level.
		//
		//   sauce needed  = 0.2 per pizza
		//   tomato needed = 0.2 * 3 * 1.20 = 0.72
		//
		// The sauce line itself disappears from the result when recursion is on, because it is then a
		// production step rather than something to consume — that distinction is the whole point.
		// ===================================================================================================
		[SkippableFact]
		public async Task Multi_level_pizza_sauce_tomato_explodes_with_scrap_at_each_level()
		{
			Ready();
			await using var sp = Graph();
			var r = await Bom(sp).ExplodeAsync(Co, _pizza, 1m, new BomExplosionOptions { Recursive = true });
			Assert.True(r.Ok, r.Error);

			foreach (var l in r.Lines) _out.WriteLine($"[2] item={l.ComponentItemId} qty={l.Quantity} level={l.Level}");

			Assert.DoesNotContain(r.Lines, l => l.ComponentItemId == _sauce);   // exploded, not consumed
			var tomato = r.Lines.Single(l => l.ComponentItemId == _tomato);
			Assert.Equal(0.72m, tomato.Quantity);      // 0.2 * 3 * 1.20
			Assert.Equal(2, tomato.Level);
			Assert.Equal(0.165m, r.Lines.Single(l => l.ComponentItemId == _cheese).Quantity);   // 0.15 * 1.10
		}

		// ===================================================================================================
		// 4 — THE ROUNDING POLICY, on a number where the two modes genuinely disagree.
		//
		//   0.12345 at 4dp  →  ToEven 0.1234   ·   AwayFromZero 0.1235
		//
		// A test that used an easy number would pass under either policy and prove nothing.
		// ===================================================================================================
		[SkippableFact]
		public async Task Canonical_rounding_is_four_decimals_away_from_zero()
		{
			Ready();
			Assert.Equal(0.1234m, Math.Round(0.12345m, 4));                                  // the old default
			Assert.Equal(0.1235m, Math.Round(0.12345m, 4, MidpointRounding.AwayFromZero));   // the canonical one

			await using var sp = Graph();
			var r = await Bom(sp).ExplodeAsync(Co, _midpointParent, 1m);
			Assert.True(r.Ok, r.Error);
			var q = r.Lines.Single().Quantity;
			_out.WriteLine($"[4] midpoint recipe 0.12345 -> canonical {q} (ToEven would give 0.1234)");
			Assert.Equal(0.1235m, q);
		}

		// ===================================================================================================
		// 5 + 6 — a malformed bill of materials is refused, not followed forever.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_cycle_is_detected_and_refused()
		{
			Ready();
			await using var sp = Graph();
			var r = await Bom(sp).ExplodeAsync(Co, _cycleA, 1m, new BomExplosionOptions { Recursive = true });
			_out.WriteLine($"[5] cycle A->B->A: ok={r.Ok} error={r.Error}");
			Assert.False(r.Ok);
			Assert.Contains("دورة", r.Error);
		}

		[SkippableFact]
		public async Task A_chain_deeper_than_the_cap_is_refused()
		{
			Ready();
			await using var sp = Graph();
			var r = await Bom(sp).ExplodeAsync(Co, _deepTop, 1m, new BomExplosionOptions { Recursive = true, MaxDepth = 30 });
			_out.WriteLine($"[6] 40-deep chain with cap 30: ok={r.Ok} error={r.Error}");
			Assert.False(r.Ok);

			// and the same chain inside the cap is fine
			var shallow = await Bom(sp).ExplodeAsync(Co, _deepTop, 1m, new BomExplosionOptions { Recursive = true, MaxDepth = 45 });
			Assert.True(shallow.Ok, shallow.Error);
		}

		// ===================================================================================================
		// 7 — UNIT OF MEASURE. 500 g of flour against a base unit of kg must become 0.5, not 500.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_recipe_line_in_grams_resolves_against_a_kilogram_base()
		{
			Ready();
			await using var sp = Graph();
			var converted = await Bom(sp).ExplodeAsync(Co, _pizza, 1m);
			Assert.True(converted.Ok, converted.Error);
			var flour = converted.Lines.Single(l => l.ComponentItemId == _flour);
			_out.WriteLine($"[7] 500 g flour -> {flour.Quantity} kg (base)");
			Assert.Equal(0.5m, flour.Quantity);

			// and with conversion OFF — the mode used where the CONSUMER converts — the recipe unit survives
			// untouched, so the factor is never applied twice.
			var raw = await Bom(sp).ExplodeAsync(Co, _pizza, 1m, new BomExplosionOptions { ConvertToBaseUoM = false });
			Assert.True(raw.Ok, raw.Error);
			var rawFlour = raw.Lines.Single(l => l.ComponentItemId == _flour);
			Assert.Equal(500m, rawFlour.Quantity);
			Assert.Equal(_uomG, rawFlour.UoMId);
		}

		[SkippableFact]
		public async Task A_recipe_unit_with_no_conversion_is_refused_rather_than_silently_wrong()
		{
			Ready();
			using (var db = _sql.ContextFor(_probe!, Co))
			{
				// oil is declared in GRAMS but no gram→kg conversion exists for oil
				var oilRow = await db.ItemComponents.FirstAsync(c => c.ParentItemId == _pizza && c.ComponentItemId == _oil);
				oilRow.UoMId = _uomG;
				await db.SaveChangesAsync();
			}
			try
			{
				await using var sp = Graph();
				var r = await Bom(sp).ExplodeAsync(Co, _pizza, 1m);
				_out.WriteLine($"[7b] undefined unit: ok={r.Ok} error={r.Error}");
				Assert.False(r.Ok);   // the same decision StockService makes — never a silent factor of 1
			}
			finally
			{
				using var db = _sql.ContextFor(_probe!, Co);
				var oilRow = await db.ItemComponents.FirstAsync(c => c.ParentItemId == _pizza && c.ComponentItemId == _oil);
				oilRow.UoMId = null;
				await db.SaveChangesAsync();
			}
		}

		// ===================================================================================================
		// 8 — DETERMINISM. Same question, same answer, in the same order, every time.
		// ===================================================================================================
		[SkippableFact]
		public async Task The_explosion_is_deterministic()
		{
			Ready();
			await using var sp = Graph();
			var a = await Bom(sp).ExplodeAsync(Co, _pizza, 3m);
			var b = await Bom(sp).ExplodeAsync(Co, _pizza, 3m);
			Assert.True(a.Ok && b.Ok);
			Assert.Equal(a.Lines.Select(x => (x.ComponentItemId, x.Quantity)), b.Lines.Select(x => (x.ComponentItemId, x.Quantity)));
			Assert.Equal(a.Lines.Select(x => x.SortOrder), a.Lines.Select(x => x.SortOrder).OrderBy(x => x));
		}

		// ===================================================================================================
		// 9 — COMPANY ISOLATION. ItemComponent is NOT covered by the global company query filters, so this
		// predicate is the only thing between one tenant's recipe and another's.
		// ===================================================================================================
		[SkippableFact]
		public async Task One_company_cannot_explode_another_companys_recipe()
		{
			Ready();
			await using var sp = Graph(Co);
			var leak = await Bom(sp).ExplodeAsync(Co, _foreignParent, 1m);
			_out.WriteLine($"[9] company 1 exploding company 2's recipe: ok={leak.Ok} lines={leak.Lines.Count}");
			Assert.True(leak.Ok);
			Assert.Empty(leak.Lines);   // the recipe exists — for its owner only

			await using var owner = Graph(Foreign);
			var owned = await Bom(owner).ExplodeAsync(Foreign, _foreignParent, 1m);
			Assert.True(owned.Ok, owned.Error);
			Assert.Equal(_foreignComp, owned.Lines.Single().ComponentItemId);
		}

		// ===================================================================================================
		// 10 + 11 + 12 — the caller's options, each proven to change exactly what it claims to.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_stocked_semi_finished_is_consumed_without_recursion_and_exploded_with_it()
		{
			Ready();
			await using var sp = Graph();
			var consume = await Bom(sp).ExplodeAsync(Co, _pizza, 1m);
			var produce = await Bom(sp).ExplodeAsync(Co, _pizza, 1m, new BomExplosionOptions { Recursive = true });

			Assert.Contains(consume.Lines, l => l.ComponentItemId == _sauce);       // consume the sauce
			Assert.DoesNotContain(produce.Lines, l => l.ComponentItemId == _sauce); // make the sauce
			Assert.DoesNotContain(consume.Lines, l => l.ComponentItemId == _tomato);
			Assert.Contains(produce.Lines, l => l.ComponentItemId == _tomato);
			_out.WriteLine("[10] the same recipe, two legitimate readings — chosen by the caller, never guessed");
		}

		[SkippableFact]
		public async Task Netting_only_happens_when_the_caller_asks_for_it()
		{
			Ready();
			await using var sp = Graph();
			var demands = new List<(int, decimal)> { (_pizza, 10m) };

			var netted = await Bom(sp).BomRequirementsAsync(Co, demands, new Dictionary<int, decimal> { [_sauce] = 1m },
				new BomExplosionOptions { Recursive = true, NetAgainstOnHand = true });
			var gross = await Bom(sp).BomRequirementsAsync(Co, demands, new Dictionary<int, decimal> { [_sauce] = 1m },
				new BomExplosionOptions { Recursive = true, NetAgainstOnHand = false });
			Assert.True(netted.Ok && gross.Ok);

			var nSauce = netted.Requirements.Single(r => r.ItemId == _sauce);
			var gSauce = gross.Requirements.Single(r => r.ItemId == _sauce);
			_out.WriteLine($"[12] sauce gross={gSauce.Gross} net(with 1 on hand)={nSauce.Net} vs no-netting net={gSauce.Net}");
			Assert.Equal(2m, nSauce.Gross);      // 10 pizzas * 0.2
			Assert.Equal(1m, nSauce.Net);        // one already on hand
			Assert.Equal(2m, gSauce.Net);        // netting off ⇒ net == gross
		}

		// ===================================================================================================
		// 13 + 14 — MRP AND WORK-ORDER PLANNING still behave, now through the shared walk.
		// ===================================================================================================
		[SkippableFact]
		public async Task Work_order_planning_stores_the_canonical_quantities()
		{
			Ready();
			await MustReceiveAsync(_flour, 100m, 5m);
			await MustReceiveAsync(_oil, 100m, 5m);
			await MustReceiveAsync(_cheese, 100m, 5m);
			await MustReceiveAsync(_sauce, 100m, 5m);

			await using var sp = Graph();
			var (ok, err, woId) = await sp.GetRequiredService<IManufService>()
				.CreateAsync(Co, _pizza, 2m, _wh, D, D, 0m, 0m, "bom", "uat");
			Assert.True(ok, err);

			using var db = _sql.ContextFor(_probe!, Co);
			var planned = await db.ManufWorkOrderComponents.AsNoTracking().Where(c => c.WorkOrderId == woId).ToListAsync();
			foreach (var p in planned) _out.WriteLine($"[14] planned item={p.ItemId} qty={p.PlannedQty} uom={p.UoMId}");

			// PlannedQty stays in the RECIPE's unit (the issue path converts), so flour is 1000 g for 2 pizzas.
			Assert.Equal(1000m, planned.Single(p => p.ItemId == _flour).PlannedQty);
			Assert.Equal(_uomG, planned.Single(p => p.ItemId == _flour).UoMId);
			Assert.Equal(0.33m, planned.Single(p => p.ItemId == _cheese).PlannedQty);   // 2 * 0.15 * 1.10
		}

		[SkippableFact]
		public async Task Mrp_still_nets_explodes_and_levels_through_the_shared_walk()
		{
			Ready();
			await using var sp = Graph();
			// one sauce on hand: 10 pizzas need 2, so 1 must be made, which needs 3*1.2 = 3.6 tomato
			var req = await Bom(sp).BomRequirementsAsync(Co, new List<(int, decimal)> { (_pizza, 10m) },
				new Dictionary<int, decimal> { [_sauce] = 1m },
				new BomExplosionOptions { Recursive = true, NetAgainstOnHand = true });
			Assert.True(req.Ok, req.Error);

			foreach (var r in req.Requirements.OrderBy(r => r.Level))
				_out.WriteLine($"[13] item={r.ItemId} gross={r.Gross} net={r.Net} level={r.Level} make={r.IsMakeItem}");

			var pizza = req.Requirements.Single(r => r.ItemId == _pizza);
			Assert.Equal(0, pizza.Level);
			Assert.True(pizza.IsMakeItem);
			var sauce = req.Requirements.Single(r => r.ItemId == _sauce);
			Assert.Equal(1, sauce.Level);
			Assert.Equal(1m, sauce.Net);
			var tomato = req.Requirements.Single(r => r.ItemId == _tomato);
			Assert.Equal(2, tomato.Level);
			Assert.Equal(3.6m, tomato.Net);   // scrap applied at the second level, on the NET sauce only
		}

		// ===================================================================================================
		// 18 — THE SIX-SITE CONSISTENCY PROOF, on the midpoint recipe where ToEven and AwayFromZero differ.
		//
		// Every migrated site is asked for the SAME recipe at the SAME quantity and must produce 0.1235. Before
		// this batch the manufacturing paths would have produced 0.1234 and POS 0.1235.
		// ===================================================================================================
		[SkippableFact]
		public async Task Every_migrated_site_resolves_the_same_canonical_quantity()
		{
			Ready();
			await MustReceiveAsync(_midpointRaw, 100m, 4m);
			await using var sp = Graph();
			var results = new List<(string site, decimal qty)>();

			// SITE: the canonical service itself
			var direct = await Bom(sp).ExplodeAsync(Co, _midpointParent, 1m);
			Assert.True(direct.Ok, direct.Error);
			results.Add(("canonical service", direct.Lines.Single().Quantity));

			// SITE: StockService Bundle explode (MID-PARENT is a Bundle) — issue 1 and read what moved
			var before = await IssuedAsync(_midpointRaw);
			var (bok, berr, _) = await sp.GetRequiredService<IStockService>().PostMovementAsync(Co, new MovementRequest
			{ Date = D, ItemId = _midpointParent, WarehouseId = _wh, Direction = -1, Qty = 1m, SourceType = "SalesInvoice", PostToGl = true }, "uat");
			Assert.True(bok, berr);
			results.Add(("StockService Bundle", await IssuedAsync(_midpointRaw) - before));

			// SITE: work-order planning
			var (cok, cerr, woId) = await sp.GetRequiredService<IManufService>()
				.CreateAsync(Co, _midpointParent, 1m, _wh, D, D, 0m, 0m, "mid", "uat");
			Assert.True(cok, cerr);
			using (var db = _sql.ContextFor(_probe!, Co))
				results.Add(("WO planning", (await db.ManufWorkOrderComponents.AsNoTracking().SingleAsync(c => c.WorkOrderId == woId)).PlannedQty));

			// SITE: MRP requirements (no stock netting, so gross == the exploded quantity)
			var mrp = await Bom(sp).BomRequirementsAsync(Co, new List<(int, decimal)> { (_midpointParent, 1m) },
				new Dictionary<int, decimal>(), new BomExplosionOptions { Recursive = true, NetAgainstOnHand = false });
			Assert.True(mrp.Ok, mrp.Error);
			results.Add(("MRP walk", mrp.Requirements.Single(r => r.ItemId == _midpointRaw).Gross));

			foreach (var (site, q) in results) _out.WriteLine($"[18] {site,-22} -> {q}");
			Assert.All(results, r => Assert.Equal(0.1235m, r.qty));
			Assert.NotEqual(0.1234m, results[0].qty);   // the ToEven answer must NOT be what any site produces
		}

		private async Task<decimal> IssuedAsync(int itemId)
		{
			using var db = _sql.ContextFor(_probe!, Co);
			return await db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == Co && m.ItemId == itemId && m.Direction == -1)
				.SumAsync(m => (decimal?)m.QtyBase) ?? 0m;
		}
	}
}
