using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
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
	// TAB-5 — RESTAURANT INVENTORY INTELLIGENCE: actual vs theoretical, waste, replenishment, shortage.
	//
	// Batch 2 made physical consumption a fact. This suite proves the four numbers a manager asks for are
	// derived from that evidence WITHOUT counting anything twice — which is the entire difficulty.
	//
	// THE EQUATION UNDER TEST, stated once so every assertion below can be read against it:
	//
	//   Theoretical       = canonical BOM explosion over the dishes that were SOLD (paid)
	//   Actual            = every operational outbound movement (PosPrep, SalesInvoice, write-off/adjustment)
	//   CancellationWaste = the PosPrep movements of orders cancelled after preparation — a SUBSET of Actual
	//   ManualWaste       = write-off movements — also part of Actual, but its own depletion
	//   ProductiveActual  = Actual - CancellationWaste - ManualWaste
	//   Variance          = ProductiveActual - Theoretical
	//
	// A cancelled dish is never in Theoretical (it was not sold) and is subtracted from Actual once. That is
	// what stops cancellation waste appearing as both waste and unexplained variance.
	//
	// SQL Server only, own probe database per run (CrossBuyProbe_PIMINT_<guid>), created and dropped here.
	[Collection(UatSqlProbeCollection.Name)]
	public class PimInventoryIntelligenceTests : IAsyncLifetime
	{
		private const int Co = 1;
		private const int Foreign = 2;
		/// The business date is TODAY, never a literal. The code under test records at "now"
		/// (PosPreparationService dates its movement DateTime.Today, PayAsync stamps ClosedAt = UtcNow),
		/// so a pinned date stops matching the day after it is written — which is exactly how four of
		/// these tests rotted. The fiscal period below is derived from it for the same reason.
		private static readonly DateTime D = DateTime.Today;

		private readonly UatSqlProbeFixture _sql;
		private readonly ITestOutputHelper _out;
		public PimInventoryIntelligenceTests(UatSqlProbeFixture sql, ITestOutputHelper output) { _sql = sql; _out = output; }
		private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

		private UatSqlProbeFixture.ProbeDatabase? _probe;
		private int _wh, _whForeign, _catId, _uomKg, _uomG;
		private int _flour, _cheese, _tomato, _sauce, _pizza;
		private int _brDispatch, _brOther, _brForeign;

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("PIMINT");
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
			services.AddScoped<IPosPreparationService, PosPreparationService>();
			services.AddScoped<IRestaurantInventoryIntelligenceService, RestaurantInventoryIntelligenceService>();
			services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(new BusinessContext
			{
				CompanyId = companyId, EmployeeId = 1, UserId = "uat-pimint",
				Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
			}));
			services.AddScoped<IBusinessEventService, BusinessEventService>();
			services.AddScoped<IManufService, ManufService>();
			services.AddScoped<IPricingService, PricingService>();
			services.AddScoped<IReceivableService, ReceivableService>();
			services.AddScoped<INotificationService, UatRecordingNotificationService>();
			services.AddScoped<IPosOrderService, PosOrderService>();
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
			var adj = Acct("520110", "تسويات مخزنية", Co); var rev = Acct("4101", "إيرادات", Co);
			var cash = Acct("110101", "الصندوق", Co); var ar = Acct("1102", "العملاء", Co);
			var vat = Acct("210201", "ض.ق.م", Co);
			// 510103 is the write-off EXPENSE account StockService.WriteOffAsync resolves. Without it every
			// write-off is refused for a reason that has nothing to do with stock or tenancy — which would make
			// the negative-stock and cross-company assertions below pass vacuously.
			var wof = Acct("510103", "مصروف إعدام", Co); var wofF = Acct("510103", "WriteOff-Co2", Foreign);
			var invF = Acct("110301", "Inv-Co2", Foreign); var cogsF = Acct("510101", "COGS-Co2", Foreign);
			var adjF = Acct("520110", "Adj-Co2", Foreign);
			db.Accounts.AddRange(inv, cogs, adj, rev, cash, ar, vat, wof, invF, cogsF, adjF, wofF);
			await db.SaveChangesAsync();

			var cat = new ItemCategory { Code = "IN1", Name = "IN1", NameEn = "IN1", CompanyID = Co, InventoryAccountId = inv.ID, CogsAccountId = cogs.ID, AdjustmentAccountId = adj.ID };
			var catF = new ItemCategory { Code = "IN2", Name = "IN2", NameEn = "IN2", CompanyID = Foreign, InventoryAccountId = invF.ID, CogsAccountId = cogsF.ID, AdjustmentAccountId = adjF.ID };
			db.ItemCategories.AddRange(cat, catF);
			await db.SaveChangesAsync();
			_catId = cat.ID;

			foreach (var c in new[] { Co, Foreign })
			{
				var fy = new FiscalYear { CompanyID = c, Name = D.Year.ToString(), StartDate = new DateTime(D.Year, 1, 1), EndDate = new DateTime(D.Year, 12, 31), Status = "Open" };
				db.FiscalYears.Add(fy); await db.SaveChangesAsync();
				db.FiscalPeriods.Add(new FiscalPeriod { FiscalYearId = fy.ID, PeriodNo = (byte)D.Month, StartDate = new DateTime(D.Year, D.Month, 1), EndDate = new DateTime(D.Year, D.Month, DateTime.DaysInMonth(D.Year, D.Month)), Status = "Open" });
			}
			await db.SaveChangesAsync();

			var kg = new UnitOfMeasure { Code = "KG", Name = "كجم", NameEn = "Kilogram", CompanyID = Co };
			var g = new UnitOfMeasure { Code = "G", Name = "جم", NameEn = "Gram", CompanyID = Co };
			db.UnitsOfMeasure.AddRange(kg, g);
			await db.SaveChangesAsync();
			_uomKg = kg.ID; _uomG = g.ID;

			var wh = new Warehouse { Code = "IN-WH", Name = "IN-WH", NameEn = "IN-WH", CompanyID = Co, WarehouseType = "Main", AllowNegativeStock = false, IsActive = true };
			var whF = new Warehouse { Code = "IN-WH2", Name = "IN-WH2", NameEn = "IN-WH2", CompanyID = Foreign, WarehouseType = "Main", AllowNegativeStock = false, IsActive = true };
			db.Warehouses.AddRange(wh, whF);
			await db.SaveChangesAsync();
			_wh = wh.ID; _whForeign = whF.ID;

			Branch Br(string code, int companyId) => new() { Name = code, NameAr = code, CompanyID = companyId, Location = code, PhoneNumber = "", Email = "", Description = code };
			var b1 = Br("IN-DISPATCH", Co); var b2 = Br("IN-OTHER", Co); var b3 = Br("IN-CO2", Foreign);
			db.Branches.AddRange(b1, b2, b3);
			await db.SaveChangesAsync();
			_brDispatch = b1.ID; _brOther = b2.ID; _brForeign = b3.ID;

			db.BranchPosSettings.AddRange(
				new BranchPosSetting { BranchId = _brDispatch, DefaultSalesWarehouseId = _wh, DefaultCurrencyId = 1 },
				new BranchPosSetting { BranchId = _brOther, DefaultSalesWarehouseId = _wh, DefaultCurrencyId = 1 },
				new BranchPosSetting { BranchId = _brForeign, DefaultSalesWarehouseId = _whForeign, DefaultCurrencyId = 1 });
			db.BranchCapabilities.Add(new BranchCapability { BranchId = _brDispatch, CapabilityKey = PosPrepCapabilities.KitchenConsumption, Enabled = true });
			await db.SaveChangesAsync();

			int seq = 0;
			Item It(string code, int companyId, int catId, int baseUoM, bool composite = false, string? ctype = null) => new()
			{
				ItemCode = code, Barcode = "N" + code + (++seq), Name = code, NameEn = code, CompanyID = companyId,
				ItemCategoryId = catId, ItemType = "Stockable", BaseUoMId = baseUoM, IsActive = true,
				IsComposite = composite, CompositeType = ctype, ProductionMethod = "OrderBased", SalesPrice = 100m,
			};
			var flour = It("IN-FLOUR", Co, _catId, kg.ID);
			var cheese = It("IN-CHEESE", Co, _catId, kg.ID);
			var tomato = It("IN-TOMATO", Co, _catId, kg.ID);
			var sauce = It("IN-SAUCE", Co, _catId, kg.ID, composite: true, ctype: "Assembly");
			var pizza = It("IN-PIZZA", Co, _catId, kg.ID, composite: true, ctype: "Assembly");
			db.Items.AddRange(flour, cheese, tomato, sauce, pizza);
			await db.SaveChangesAsync();
			_flour = flour.ID; _cheese = cheese.ID; _tomato = tomato.ID; _sauce = sauce.ID; _pizza = pizza.ID;

			db.UoMConversions.Add(new UoMConversion { ItemId = flour.ID, FromUoMId = g.ID, ToUoMId = kg.ID, Factor = 0.001m });
			await db.SaveChangesAsync();

			// PIZZA -> 500 g flour (grams against a kg base), 0.2 stocked sauce.  SAUCE -> 3 tomato @20% scrap.
			db.ItemComponents.AddRange(
				new ItemComponent { CompanyID = Co, ParentItemId = _pizza, ComponentItemId = _flour, Quantity = 500m, ScrapPct = 0m, UoMId = g.ID, SortOrder = 1 },
				new ItemComponent { CompanyID = Co, ParentItemId = _pizza, ComponentItemId = _sauce, Quantity = 0.2m, ScrapPct = 0m, SortOrder = 2 },
				new ItemComponent { CompanyID = Co, ParentItemId = _sauce, ComponentItemId = _tomato, Quantity = 3m, ScrapPct = 20m, SortOrder = 1 });
			foreach (var b in new[] { _brDispatch, _brOther })
				db.BranchItemSourcings.Add(new BranchItemSourcing { BranchId = b, ItemId = _pizza, Method = "RecipeAtSale", IsActive = true });
			await db.SaveChangesAsync();

			// Replenishment configuration lives per (item, warehouse) — the real table, not a field on Item.
			db.ItemWarehouseSettings.AddRange(
				new ItemWarehouseSetting { ItemId = _flour, WarehouseId = _wh, ReorderPoint = 10m, MaxQty = 50m },
				new ItemWarehouseSetting { ItemId = _cheese, WarehouseId = _wh, MinQty = 5m },              // MinQty only, no MaxQty
				new ItemWarehouseSetting { ItemId = _sauce, WarehouseId = _wh, ReorderPoint = 8m, MaxQty = 20m },   // a MAKE item
				new ItemWarehouseSetting { ItemId = _tomato, WarehouseId = _wh, ReorderPoint = 0m, MaxQty = 99m }); // zero config
			await db.SaveChangesAsync();
		}

		// ---- helpers -------------------------------------------------------------------------------------
		private async Task ReceiveAsync(int itemId, decimal qty, decimal cost, int companyId = Co, int? wh = null)
		{
			await using var sp = Graph(companyId);
			var (ok, err, _) = await sp.GetRequiredService<IStockService>().PostMovementAsync(companyId, new MovementRequest
			{ Date = D, ItemId = itemId, WarehouseId = wh ?? _wh, Direction = 1, Qty = qty, UnitCostInBase = cost, SourceType = "Opening", PostToGl = true }, "uat");
			Assert.True(ok, $"receipt failed for {itemId}: {err}");
		}

		private async Task<decimal> OnHandAsync(int itemId, int companyId = Co, int? wh = null)
		{
			await using var sp = Graph(companyId);
			var (q, _, _) = await sp.GetRequiredService<IStockService>().GetBalanceAsync(companyId, itemId, wh ?? _wh);
			return q;
		}

		private async Task<int> SellPizzaAsync(ServiceProvider sp, int branchId, decimal qty, bool cancelAfterPrep = false, bool pay = true)
		{
			var pos = sp.GetRequiredService<IPosOrderService>();
			var (ok, err, orderId) = await pos.CreateOrderAsync(Co, branchId, "Takeaway", null, 1);
			Assert.True(ok, err);
			Assert.True((await pos.AddLineAsync(Co, orderId, _pizza, qty)).ok);
			Assert.True((await pos.SendToKitchenAsync(Co, orderId)).ok);
			if (cancelAfterPrep) { var (vok, verr) = await pos.VoidOrderAsync(Co, orderId); Assert.True(vok, verr); }
			else if (pay) { var p = await pos.PayAsync(Co, orderId, "Cash", 1); Assert.True(p.ok, p.error); }
			return orderId;
		}

		private static ConsumptionVarianceRow Row(ConsumptionVarianceResult r, int itemId) =>
			r.Rows.Single(x => x.ItemId == itemId);

		// ===================================================================================================
		// VARIANCE — the four buckets, on a recipe with a real unit conversion.
		//
		// Two pizzas sold. Each draws 500 g flour = 0.5 kg, so theoretical flour = 1.0 kg in the BASE unit.
		// Nothing else touched the flour, so actual = 1.0 and the variance is exactly zero. If the calculation
		// ever compared 500 to 0.5 the variance would be 999 instead of 0 — that is the regression this guards.
		// ===================================================================================================
		[SkippableFact]
		public async Task Variance_uses_base_units_so_grams_are_never_compared_with_kilograms()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_sauce, 100m, 5m);

			await using var sp = Graph();
			await SellPizzaAsync(sp, _brDispatch, 1m);
			await SellPizzaAsync(sp, _brDispatch, 1m);

			var v = await sp.GetRequiredService<IRestaurantInventoryIntelligenceService>()
				.VarianceAsync(Co, _brDispatch, D.AddDays(-1), D.AddDays(1));
			Assert.True(v.Ok, v.Error);
			_out.WriteLine($"[V] {v.Equation}");
			foreach (var r in v.Rows)
				_out.WriteLine($"[V] {r.ItemCode,-12} theo={r.Theoretical} actual={r.Actual} cancel={r.CancellationWaste} manual={r.ManualWaste} variance={r.VarianceQty} ({r.VariancePct}%)");

			var flour = Row(v, _flour);
			Assert.Equal(1.0m, flour.Theoretical);     // 2 x 500 g expressed in kg
			Assert.Equal(1.0m, flour.Actual);
			Assert.Equal(0m, flour.VarianceQty);
			Assert.Equal(0m, flour.VariancePct);
		}

		// ===================================================================================================
		// CANCELLATION WASTE IS NOT COUNTED TWICE.
		//
		// One pizza sold, one cooked then cancelled. Actual flour = 1.0 (both were cooked). Theoretical = 0.5
		// (only one was SOLD). The 0.5 difference is KNOWN cancellation waste, so the UNEXPLAINED variance must
		// be zero — not 0.5. Getting this wrong is the single easiest way to make the report lie.
		// ===================================================================================================
		[SkippableFact]
		public async Task Cancellation_waste_is_known_waste_and_not_unexplained_variance()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_sauce, 100m, 5m);

			await using var sp = Graph();
			await SellPizzaAsync(sp, _brDispatch, 1m);                        // sold
			await SellPizzaAsync(sp, _brDispatch, 1m, cancelAfterPrep: true); // cooked, then cancelled

			var v = await sp.GetRequiredService<IRestaurantInventoryIntelligenceService>().VarianceAsync(Co, _brDispatch, D.AddDays(-1), D.AddDays(1));
			Assert.True(v.Ok, v.Error);
			var flour = Row(v, _flour);
			_out.WriteLine($"[C] flour theo={flour.Theoretical} actual={flour.Actual} cancelWaste={flour.CancellationWaste} " +
				$"productive={flour.ProductiveActual} variance={flour.VarianceQty}");

			Assert.Equal(0.5m, flour.Theoretical);          // one pizza SOLD
			Assert.Equal(1.0m, flour.Actual);               // two pizzas COOKED
			Assert.Equal(0.5m, flour.CancellationWaste);    // the cancelled one
			Assert.Equal(0.5m, flour.ProductiveActual);
			Assert.Equal(0m, flour.VarianceQty);            // fully explained — NOT 0.5
		}

		// ===================================================================================================
		// SEMI-FINISHED. The stocked sauce is its own consumption line; its tomatoes are never touched by a
		// sale. Batch-1 and Batch-2 semantics, restated as an intelligence assertion.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_stocked_semi_finished_is_consumed_as_stock_and_its_raws_stay_untouched()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_sauce, 100m, 5m); await ReceiveAsync(_tomato, 100m, 2m);

			await using var sp = Graph();
			await SellPizzaAsync(sp, _brDispatch, 1m);

			var v = await sp.GetRequiredService<IRestaurantInventoryIntelligenceService>().VarianceAsync(Co, _brDispatch, D.AddDays(-1), D.AddDays(1));
			Assert.True(v.Ok, v.Error);
			var sauce = Row(v, _sauce);
			_out.WriteLine($"[S] sauce theo={sauce.Theoretical} actual={sauce.Actual}; tomato rows={v.Rows.Count(r => r.ItemId == _tomato)}");
			Assert.Equal(0.2m, sauce.Theoretical);
			Assert.Equal(0.2m, sauce.Actual);
			Assert.Equal(100m, await OnHandAsync(_tomato));                  // no raw was consumed
			Assert.DoesNotContain(v.Rows, r => r.ItemId == _tomato);         // and it is not even a variance row
		}

		// ===================================================================================================
		// MANUAL WASTE — through the write-off document that already exists. Stock falls once, the reason and
		// the actor are recorded, the cost is posted once, and it lands in KNOWN waste rather than in variance.
		// ===================================================================================================
		[SkippableFact]
		public async Task Manual_waste_decreases_stock_once_and_records_reason_actor_and_cost()
		{
			Ready();
			await ReceiveAsync(_cheese, 20m, 30m);
			decimal before = await OnHandAsync(_cheese);

			await using var sp = Graph();
			var (ok, err, docNo, docId, mode) = await sp.GetRequiredService<IStockService>().WriteOffAsync(
				Co, _wh, D, "Spoilage", "تلف في الثلاجة",
				new List<WriteOffLineInput> { new() { ItemId = _cheese, Qty = 2m, Reason = "Spoilage" } }, "chef-1");
			Assert.True(ok, err);
			_out.WriteLine($"[W] write-off {docNo} (mode={mode}) — cheese {before} -> {await OnHandAsync(_cheese)}");

			Assert.Equal(before - 2m, await OnHandAsync(_cheese));

			using var db = _sql.ContextFor(_probe!, Co);
			if (mode == "SeparateDocument")
			{
				var doc = await db.StockWriteOffs.AsNoTracking().FirstAsync(w => w.ID == docId);
				var line = await db.StockWriteOffLines.AsNoTracking().FirstAsync(l => l.StockWriteOffId == docId);
				_out.WriteLine($"[W] doc reason={doc.Reason} by={doc.CreatedBy} value={doc.TotalValue}; line reason={line.Reason} qty={line.Qty} movement={line.MovementId}");
				Assert.Equal("Spoilage", line.Reason);
				Assert.Equal("chef-1", doc.CreatedBy);
				Assert.NotNull(line.MovementId);          // the operational record points at the ledger row
				Assert.True(doc.TotalValue > 0);          // cost snapshot captured
				Assert.NotNull(doc.JournalEntryId);       // financial treatment, exactly once
			}

			// exactly ONE outbound movement for that write-off
			var moves = await db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == Co && m.ItemId == _cheese && m.Direction == -1).ToListAsync();
			Assert.Single(moves);
		}

		[SkippableFact]
		public async Task Manual_waste_is_known_waste_not_unexplained_variance()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_sauce, 100m, 5m);

			await using var sp = Graph();
			await SellPizzaAsync(sp, _brDispatch, 1m);
			var (wok, werr, _, _, _) = await sp.GetRequiredService<IStockService>().WriteOffAsync(
				Co, _wh, D, "Expired", null,
				new List<WriteOffLineInput> { new() { ItemId = _flour, Qty = 3m, Reason = "Expired" } }, "chef-1");
			Assert.True(wok, werr);

			var v = await sp.GetRequiredService<IRestaurantInventoryIntelligenceService>().VarianceAsync(Co, _brDispatch, D.AddDays(-1), D.AddDays(1));
			var flour = Row(v, _flour);
			_out.WriteLine($"[M] flour theo={flour.Theoretical} actual={flour.Actual} manual={flour.ManualWaste} variance={flour.VarianceQty}");
			Assert.Equal(0.5m, flour.Theoretical);
			Assert.Equal(3.5m, flour.Actual);            // 0.5 cooked + 3 written off
			Assert.Equal(3m, flour.ManualWaste);
			Assert.Equal(0m, flour.VarianceQty);         // explained, not variance
		}

		[SkippableFact]
		public async Task Manual_waste_cannot_go_negative_or_cross_a_company()
		{
			Ready();
			await ReceiveAsync(_cheese, 1m, 30m);

			await using var sp = Graph();
			var stock = sp.GetRequiredService<IStockService>();
			var (tooMuch, err1, _, _, _) = await stock.WriteOffAsync(Co, _wh, D, "Damaged", null,
				new List<WriteOffLineInput> { new() { ItemId = _cheese, Qty = 99m } }, "chef-1");
			_out.WriteLine($"[N] write-off beyond stock refused={!tooMuch} ({err1})");
			Assert.False(tooMuch);
			Assert.True(await OnHandAsync(_cheese) >= 0m);

			// company 2 writing off company 1's item, into company 1's warehouse
			await using var foreignSp = Graph(Foreign);
			var (cross, err2, _, _, _) = await foreignSp.GetRequiredService<IStockService>().WriteOffAsync(
				Foreign, _wh, D, "Damaged", null,
				new List<WriteOffLineInput> { new() { ItemId = _cheese, Qty = 1m } }, "chef-2");
			_out.WriteLine($"[N] cross-company write-off refused={!cross} ({err2})");
			Assert.False(cross);
			Assert.Equal(1m, await OnHandAsync(_cheese));
		}

		// ===================================================================================================
		// REPLENISHMENT — deterministic, explainable, and never an order.
		// ===================================================================================================
		[SkippableFact]
		public async Task Replenishment_is_silent_above_the_threshold_and_speaks_at_or_below_it()
		{
			Ready();
			await ReceiveAsync(_flour, 40m, 10m);    // threshold 10, target 50 ⇒ above the line
			await using var sp = Graph();
			var svc = sp.GetRequiredService<IRestaurantInventoryIntelligenceService>();

			var quiet = await svc.ReplenishmentAsync(Co, _brDispatch);
			_out.WriteLine($"[R] flour on hand 40 (threshold 10): recommendations={quiet.Count(r => r.ItemId == _flour)}");
			Assert.DoesNotContain(quiet, r => r.ItemId == _flour);

			// drop it to the threshold exactly — "at or below" must trigger
			var (ok, err, _, _, _) = await sp.GetRequiredService<IStockService>().WriteOffAsync(Co, _wh, D, "Damaged", null,
				new List<WriteOffLineInput> { new() { ItemId = _flour, Qty = 30m } }, "uat");
			Assert.True(ok, err);

			var loud = await svc.ReplenishmentAsync(Co, _brDispatch);
			var rec = loud.Single(r => r.ItemId == _flour);
			_out.WriteLine($"[R] available={rec.Available} threshold={rec.Threshold} target={rec.Target} recommend={rec.RecommendedQty} — {rec.Reason}");
			Assert.Equal(10m, rec.Available);
			Assert.Equal(10m, rec.Threshold);
			Assert.Equal(50m, rec.Target);
			Assert.Equal(40m, rec.RecommendedQty);      // deterministic: target - available
			Assert.False(string.IsNullOrWhiteSpace(rec.Reason));
		}

		[SkippableFact]
		public async Task Replenishment_falls_back_to_MinQty_and_tops_up_to_the_threshold_without_a_target()
		{
			Ready();
			await ReceiveAsync(_cheese, 2m, 30m);      // MinQty 5, no MaxQty ⇒ target == threshold
			await using var sp = Graph();
			var rec = (await sp.GetRequiredService<IRestaurantInventoryIntelligenceService>()
				.ReplenishmentAsync(Co, _brDispatch)).Single(r => r.ItemId == _cheese);
			_out.WriteLine($"[R] cheese available={rec.Available} threshold={rec.Threshold} target={rec.Target} recommend={rec.RecommendedQty}");
			Assert.Equal(5m, rec.Threshold);
			Assert.Equal(5m, rec.Target);
			Assert.Equal(3m, rec.RecommendedQty);
		}

		[SkippableFact]
		public async Task Replenishment_flags_a_make_item_without_manufacturing_it()
		{
			Ready();
			await ReceiveAsync(_sauce, 1m, 5m); await ReceiveAsync(_tomato, 100m, 2m);
			decimal tomatoBefore = await OnHandAsync(_tomato);

			await using var sp = Graph();
			var rec = (await sp.GetRequiredService<IRestaurantInventoryIntelligenceService>()
				.ReplenishmentAsync(Co, _brDispatch)).Single(r => r.ItemId == _sauce);
			_out.WriteLine($"[R] sauce isMake={rec.IsMakeItem} recommend={rec.RecommendedQty} — {rec.Reason}");

			Assert.True(rec.IsMakeItem);
			Assert.Equal(19m, rec.RecommendedQty);                     // target 20 - available 1
			Assert.Equal(tomatoBefore, await OnHandAsync(_tomato));    // NOTHING was manufactured
			using var db = _sql.ContextFor(_probe!, Co);
			Assert.Empty(await db.ManufWorkOrders.AsNoTracking().ToListAsync());   // and no work order appeared
		}

		[SkippableFact]
		public async Task Replenishment_ignores_a_zero_or_missing_configuration()
		{
			Ready();
			await ReceiveAsync(_tomato, 0m + 1m, 2m);   // ReorderPoint configured as 0 ⇒ no opinion
			await using var sp = Graph();
			var recs = await sp.GetRequiredService<IRestaurantInventoryIntelligenceService>().ReplenishmentAsync(Co, _brDispatch);
			_out.WriteLine($"[R] zero-threshold item recommendations={recs.Count(r => r.ItemId == _tomato)}; " +
				$"unconfigured item recommendations={recs.Count(r => r.ItemId == _pizza)}");
			Assert.DoesNotContain(recs, r => r.ItemId == _tomato);   // zero threshold: explicitly no opinion
			Assert.DoesNotContain(recs, r => r.ItemId == _pizza);    // no configuration row at all
		}

		// ===================================================================================================
		// COMPANY AND BRANCH ISOLATION.
		// ===================================================================================================
		[SkippableFact]
		public async Task Intelligence_never_crosses_a_company_or_reads_an_unowned_branch()
		{
			Ready();
			await ReceiveAsync(_flour, 5m, 10m);   // below threshold ⇒ company 1 would see a recommendation

			await using var own = Graph(Co);
			var mine = await own.GetRequiredService<IRestaurantInventoryIntelligenceService>().ReplenishmentAsync(Co, _brDispatch);
			Assert.Contains(mine, r => r.ItemId == _flour);

            // company 2 naming company 1's branch by its exact id
			await using var other = Graph(Foreign);
			var svc = other.GetRequiredService<IRestaurantInventoryIntelligenceService>();
			var leaked = await svc.ReplenishmentAsync(Foreign, _brDispatch);
			var leakedVar = await svc.VarianceAsync(Foreign, _brDispatch, D, D);
			var leakedShort = await svc.ShortagesAsync(Foreign, _brDispatch, D, D);
			_out.WriteLine($"[I] company 2 reading company 1's branch: replenishment={leaked.Count} variance.ok={leakedVar.Ok} shortages={leakedShort.Count}");
			Assert.Empty(leaked);
			Assert.False(leakedVar.Ok);        // fails closed, naming nothing
			Assert.Empty(leakedShort);
		}

		// ===================================================================================================
		// SHORTAGE INTELLIGENCE — the events Batch 2 already writes, read back. No parallel engine.
		// ===================================================================================================
		[SkippableFact]
		public async Task Shortage_intelligence_reads_the_existing_events()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m);
			await ReceiveAsync(_sauce, 0.05m, 5m);    // a pizza needs 0.2 — short
			using (var db = _sql.ContextFor(_probe!, Co))
			{
				db.BranchCapabilities.Add(new BranchCapability { BranchId = _brDispatch, CapabilityKey = PosPrepCapabilities.ShortageAllowed, Enabled = true });
				await db.SaveChangesAsync();
			}

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var (ok, err, orderId) = await pos.CreateOrderAsync(Co, _brDispatch, "Takeaway", null, 1);
			Assert.True(ok, err);
			Assert.True((await pos.AddLineAsync(Co, orderId, _pizza, 1m)).ok);
			Assert.True((await pos.SendToKitchenAsync(Co, orderId)).ok);

			var signals = await sp.GetRequiredService<IRestaurantInventoryIntelligenceService>()
				.ShortagesAsync(Co, _brDispatch, D.AddDays(-1), D.AddDays(1));
			foreach (var s in signals)
				_out.WriteLine($"[H] order={s.OrderId} line={s.LineId} item={s.ItemCode} required={s.Required} consumed={s.Consumed} short={s.Shortfall}");

			var sauce = signals.Single(s => s.ItemId == _sauce);
			Assert.Equal(orderId, sauce.OrderId);
			Assert.Equal(0.2m, sauce.Required);
			Assert.Equal(0.05m, sauce.Consumed);
			Assert.Equal(0.15m, sauce.Shortfall);
			Assert.True(sauce.LineId > 0);
		}
	}
}
