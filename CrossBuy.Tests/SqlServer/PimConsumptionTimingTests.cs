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
	// TAB-5 — RESTAURANT STOCK REALITY: consumption timing, shortage, waste.
	//
	// Stock consumption and financial posting are different facts. Until now the system had only one moment
	// for both — Pay — so a dish cooked and then voided consumed real food that the books never saw, and stock
	// was wrong for the whole time an order stayed open.
	//
	// This suite drives the WHOLE POS lifecycle, which no committed test did before: CreateOrder → AddLine →
	// SendToKitchen → Pay / Void / Return. The sibling acceptance family deliberately stops short of PayAsync,
	// so everything below about ordering, dispatch, payment and cancellation is exercised here for the first
	// time end to end.
	//
	// THE STATES ARE THE APPLICATION'S OWN, not invented for the test:
	//   ORDERED           PosOrderLine exists, SentQty = 0
	//   SENT_TO_KITCHEN   SentQty > 0, KdsStatus "New"        (SendToKitchenAsync)
	//   PREPARING/READY   KdsStatus, forward-only New→Preparing→Ready
	//   PAID              PosOrder.Status "Paid"
	//   CANCELLED         "Void" (open) / "Voided" (paid)
	//   WASTED            consumed-then-cancelled — the one state this batch adds, as a cost reclassification
	//
	// SQL Server only, own probe database per run (CrossBuyProbe_PIMCT_<guid>), created and dropped here.
	// Never CrossBuyDev, never CrossBuyDB2, never production, and not the shared platform fixture.
	[Collection(UatSqlProbeCollection.Name)]
	public class PimConsumptionTimingTests : IAsyncLifetime
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
		public PimConsumptionTimingTests(UatSqlProbeFixture sql, ITestOutputHelper output) { _sql = sql; _out = output; }
		private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

		private UatSqlProbeFixture.ProbeDatabase? _probe;
		private int _wh, _catId, _uomKg, _uomG;
		private int _flour, _cheese, _tomato, _sauce, _pizza, _cola;
		private int _brDispatch, _brPay, _brShortBlock, _brShortAllow, _brForeign;
		private int _whForeign, _pizzaForeign;

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("PIMCT");
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
			services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(new BusinessContext
			{
				CompanyId = companyId, EmployeeId = 1, UserId = "uat-pimct",
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
			// The exact codes the sale path resolves: 1102 is the AR control CreateCustomerAsync stamps on the
			// walk-in customer, and 210201 is VAT output. Seeding the wrong codes leaves ControlAccountId at 0
			// and the invoice fails with "account 0 does not exist in this company".
			var cash = Acct("110101", "الصندوق", Co); var ar = Acct("1102", "العملاء", Co);
			var vat = Acct("210201", "ض.ق.م مخرجات", Co);
			var invF = Acct("110301", "Inv-Co2", Foreign); var cogsF = Acct("510101", "COGS-Co2", Foreign);
			db.Accounts.AddRange(inv, cogs, adj, rev, cash, ar, vat, invF, cogsF);
			await db.SaveChangesAsync();

			var cat = new ItemCategory { Code = "CT1", Name = "CT1", NameEn = "CT1", CompanyID = Co, InventoryAccountId = inv.ID, CogsAccountId = cogs.ID, AdjustmentAccountId = adj.ID };
			var catF = new ItemCategory { Code = "CT2", Name = "CT2", NameEn = "CT2", CompanyID = Foreign, InventoryAccountId = invF.ID, CogsAccountId = cogsF.ID };
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

			var wh = new Warehouse { Code = "CT-WH", Name = "CT-WH", NameEn = "CT-WH", CompanyID = Co, WarehouseType = "Main", AllowNegativeStock = false, IsActive = true };
			var whF = new Warehouse { Code = "CT-WH2", Name = "CT-WH2", NameEn = "CT-WH2", CompanyID = Foreign, WarehouseType = "Main", AllowNegativeStock = false, IsActive = true };
			db.Warehouses.AddRange(wh, whF);
			await db.SaveChangesAsync();
			_wh = wh.ID; _whForeign = whF.ID;

			// FOUR BRANCHES, one per policy combination, so a single probe proves every branch of the decision
			// without any test mutating another test's configuration.
			Branch Br(string code, int companyId) => new() { Name = code, NameAr = code, CompanyID = companyId, Location = code, PhoneNumber = "", Email = "", Description = code };
			var b1 = Br("CT-DISPATCH", Co); var b2 = Br("CT-PAY", Co);
			var b3 = Br("CT-BLOCK", Co); var b4 = Br("CT-ALLOW", Co); var b5 = Br("CT-CO2", Foreign);
			db.Branches.AddRange(b1, b2, b3, b4, b5);
			await db.SaveChangesAsync();
			_brDispatch = b1.ID; _brPay = b2.ID; _brShortBlock = b3.ID; _brShortAllow = b4.ID; _brForeign = b5.ID;

			db.BranchPosSettings.AddRange(
				new BranchPosSetting { BranchId = _brDispatch, DefaultSalesWarehouseId = _wh, DefaultCurrencyId = 1 },
				new BranchPosSetting { BranchId = _brPay, DefaultSalesWarehouseId = _wh, DefaultCurrencyId = 1 },
				new BranchPosSetting { BranchId = _brShortBlock, DefaultSalesWarehouseId = _wh, DefaultCurrencyId = 1 },
				new BranchPosSetting { BranchId = _brShortAllow, DefaultSalesWarehouseId = _wh, DefaultCurrencyId = 1 },
				new BranchPosSetting { BranchId = _brForeign, DefaultSalesWarehouseId = _whForeign, DefaultCurrencyId = 1 });

			// THE POLICIES ARE CAPABILITIES, and _brPay deliberately gets NO row at all — an absent capability
			// is disabled, which is the whole regression guarantee: an unconfigured branch behaves as before.
			db.BranchCapabilities.AddRange(
				new BranchCapability { BranchId = _brDispatch, CapabilityKey = PosPrepCapabilities.KitchenConsumption, Enabled = true },
				new BranchCapability { BranchId = _brShortBlock, CapabilityKey = PosPrepCapabilities.KitchenConsumption, Enabled = true },
				new BranchCapability { BranchId = _brShortAllow, CapabilityKey = PosPrepCapabilities.KitchenConsumption, Enabled = true },
				new BranchCapability { BranchId = _brShortAllow, CapabilityKey = PosPrepCapabilities.ShortageAllowed, Enabled = true },
				new BranchCapability { BranchId = _brForeign, CapabilityKey = PosPrepCapabilities.KitchenConsumption, Enabled = true });
			await db.SaveChangesAsync();

			int seq = 0;
			Item It(string code, int companyId, int catId, int baseUoM, bool composite = false, string? ctype = null) => new()
			{
				ItemCode = code, Barcode = "C" + code + (++seq), Name = code, NameEn = code, CompanyID = companyId,
				ItemCategoryId = catId, ItemType = "Stockable", BaseUoMId = baseUoM, IsActive = true,
				IsComposite = composite, CompositeType = ctype, ProductionMethod = "OrderBased", SalesPrice = 100m,
			};
			var flour = It("CT-FLOUR", Co, _catId, kg.ID);
			var cheese = It("CT-CHEESE", Co, _catId, kg.ID);
			var tomato = It("CT-TOMATO", Co, _catId, kg.ID);
			var sauce = It("CT-SAUCE", Co, _catId, kg.ID, composite: true, ctype: "Assembly");   // stocked semi-finished
			var pizza = It("CT-PIZZA", Co, _catId, kg.ID, composite: true, ctype: "Assembly");
			var cola = It("CT-COLA", Co, _catId, kg.ID);                                          // sold as itself, no recipe
			var pizzaF = It("CT-PIZZA-CO2", Foreign, catF.ID, kg.ID, composite: true, ctype: "Assembly");
			db.Items.AddRange(flour, cheese, tomato, sauce, pizza, cola, pizzaF);
			await db.SaveChangesAsync();
			_flour = flour.ID; _cheese = cheese.ID; _tomato = tomato.ID; _sauce = sauce.ID; _pizza = pizza.ID; _cola = cola.ID;
			_pizzaForeign = pizzaF.ID;

			db.UoMConversions.Add(new UoMConversion { ItemId = flour.ID, FromUoMId = g.ID, ToUoMId = kg.ID, Factor = 0.001m });
			await db.SaveChangesAsync();

			db.ItemComponents.AddRange(
				// PIZZA -> 500 g flour (grams against a kg base), 0.15 cheese @10% scrap, 0.2 stocked sauce
				new ItemComponent { CompanyID = Co, ParentItemId = _pizza, ComponentItemId = _flour, Quantity = 500m, ScrapPct = 0m, UoMId = g.ID, SortOrder = 1 },
				new ItemComponent { CompanyID = Co, ParentItemId = _pizza, ComponentItemId = _cheese, Quantity = 0.15m, ScrapPct = 10m, SortOrder = 2 },
				new ItemComponent { CompanyID = Co, ParentItemId = _pizza, ComponentItemId = _sauce, Quantity = 0.2m, ScrapPct = 0m, SortOrder = 3 },
				new ItemComponent { CompanyID = Co, ParentItemId = _sauce, ComponentItemId = _tomato, Quantity = 3m, ScrapPct = 20m, SortOrder = 1 },
				new ItemComponent { CompanyID = Foreign, ParentItemId = _pizzaForeign, ComponentItemId = _flour, Quantity = 1m, ScrapPct = 0m, SortOrder = 1 });
			await db.SaveChangesAsync();

			foreach (var b in new[] { _brDispatch, _brPay, _brShortBlock, _brShortAllow })
				db.BranchItemSourcings.Add(new BranchItemSourcing { BranchId = b, ItemId = _pizza, Method = "RecipeAtSale", IsActive = true });
			db.BranchItemSourcings.Add(new BranchItemSourcing { BranchId = _brForeign, ItemId = _pizzaForeign, Method = "RecipeAtSale", IsActive = true });
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

		private async Task<List<StockMovement>> MovesAsync(int itemId, int companyId = Co)
		{
			using var db = _sql.ContextFor(_probe!, companyId);
			return await db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == companyId && m.ItemId == itemId).OrderBy(m => m.ID).ToListAsync();
		}

		private async Task<List<string>> EventsAsync(int orderId, int companyId = Co)
		{
			using var db = _sql.ContextFor(_probe!, companyId);
			return await db.BusinessEvents.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.EntityType == "PosOrder" && e.EntityId == orderId)
				.OrderBy(e => e.EventId).Select(e => e.EventType).ToListAsync();
		}

		/// Order → line → (optionally) kitchen. Returns the order id.
		private async Task<int> OpenOrderAsync(ServiceProvider sp, int branchId, int itemId, decimal qty)
		{
			var pos = sp.GetRequiredService<IPosOrderService>();
			var (ok, err, orderId) = await pos.CreateOrderAsync(Co, branchId, "Takeaway", null, 1);
			Assert.True(ok, err);
			var (aok, aerr) = await pos.AddLineAsync(Co, orderId, itemId, qty);
			Assert.True(aok, aerr);
			return orderId;
		}

		// ===================================================================================================
		// 1 — PIZZA, DIRECT INGREDIENTS: the ingredients leave at DISPATCH, not at Pay.
		//
		// Timeline proven here — Open: nothing. SendToKitchen: flour/cheese/sauce out. Pay: revenue only.
		// ===================================================================================================
		[SkippableFact]
		public async Task Sending_to_the_kitchen_consumes_the_recipe_and_paying_does_not_consume_it_again()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_cheese, 100m, 20m); await ReceiveAsync(_sauce, 100m, 5m);
			decimal f0 = await OnHandAsync(_flour), c0 = await OnHandAsync(_cheese), s0 = await OnHandAsync(_sauce);

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var orderId = await OpenOrderAsync(sp, _brDispatch, _pizza, 1m);

			_out.WriteLine($"[T1] ORDERED        flour={await OnHandAsync(_flour)} cheese={await OnHandAsync(_cheese)} sauce={await OnHandAsync(_sauce)}");
			Assert.Equal(f0, await OnHandAsync(_flour));   // ordering moves nothing

			var (sok, serr, sent) = await pos.SendToKitchenAsync(Co, orderId);
			Assert.True(sok, serr);
			decimal f1 = await OnHandAsync(_flour), c1 = await OnHandAsync(_cheese), s1 = await OnHandAsync(_sauce);
			_out.WriteLine($"[T1] SENT_TO_KITCHEN flour={f1} cheese={c1} sauce={s1}");
			Assert.Equal(f0 - 0.5m, f1);      // 500 g against a kg base
			Assert.Equal(c0 - 0.165m, c1);    // 0.15 * 1.10 scrap
			Assert.Equal(s0 - 0.2m, s1);      // the STOCKED sauce is consumed, not manufactured

			var (pok, perr, invId) = await pos.PayAsync(Co, orderId, "Cash", 1);
			Assert.True(pok, perr);
			decimal f2 = await OnHandAsync(_flour), c2 = await OnHandAsync(_cheese), s2 = await OnHandAsync(_sauce);
			_out.WriteLine($"[T1] PAID           flour={f2} cheese={c2} sauce={s2}  (invoice {invId})");

			// THE CRITICAL ASSERTION: Pay raised revenue but moved no stock, because it already moved.
			Assert.Equal(f1, f2); Assert.Equal(c1, c2); Assert.Equal(s1, s2);

			var ev = await EventsAsync(orderId);
			_out.WriteLine($"[T1] events: {string.Join(", ", ev)}");
			Assert.Contains(PosPrepEvents.ConsumptionRecorded, ev);
		}

		// ===================================================================================================
		// 2 — THE STOCKED SEMI-FINISHED IS CONSUMED, NEVER SILENTLY MANUFACTURED.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_stocked_sauce_is_drawn_from_stock_and_no_tomato_is_touched()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_cheese, 100m, 20m);
			await ReceiveAsync(_sauce, 100m, 5m); await ReceiveAsync(_tomato, 100m, 2m);
			decimal t0 = await OnHandAsync(_tomato);

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var orderId = await OpenOrderAsync(sp, _brDispatch, _pizza, 1m);
			Assert.True((await pos.SendToKitchenAsync(Co, orderId)).ok);

			_out.WriteLine($"[T2] tomato before={t0} after={await OnHandAsync(_tomato)} — the sauce was consumed, not made");
			Assert.Equal(t0, await OnHandAsync(_tomato));                 // Batch-1 rule holds
			Assert.Equal(0.2m, 100m - await OnHandAsync(_sauce));         // the sauce itself went
		}

		// ===================================================================================================
		// 3 — SHORTAGE, BLOCK. The refusal is explicit and nothing partial is left behind.
		// ===================================================================================================
		[SkippableFact]
		public async Task Shortage_under_Block_refuses_the_dispatch_and_consumes_nothing()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_sauce, 100m, 5m);
			// cheese deliberately NOT received: 0 on hand
			decimal f0 = await OnHandAsync(_flour), s0 = await OnHandAsync(_sauce);

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var orderId = await OpenOrderAsync(sp, _brShortBlock, _pizza, 1m);
			var (ok, err, _) = await pos.SendToKitchenAsync(Co, orderId);
			_out.WriteLine($"[T3] dispatch refused={!ok} ({err})");
			Assert.False(ok);

			// ALL OR NOTHING: the flour that WOULD have been consumed before the cheese failed is still there.
			Assert.Equal(f0, await OnHandAsync(_flour));
			Assert.Equal(s0, await OnHandAsync(_sauce));
			Assert.Empty(await EventsAsync(orderId));
		}

		// ===================================================================================================
		// 4 — SHORTAGE, ALLOW_AND_RECORD. Service continues, the shortfall becomes evidence, stock never
		// goes negative, and Warehouse.AllowNegativeStock is not involved.
		// ===================================================================================================
		[SkippableFact]
		public async Task Shortage_under_AllowAndRecord_consumes_what_exists_and_records_the_shortfall()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_sauce, 100m, 5m);
			await ReceiveAsync(_cheese, 0.1m, 20m);   // needs 0.165, only 0.1 exists

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var orderId = await OpenOrderAsync(sp, _brShortAllow, _pizza, 1m);
			var (ok, err, _) = await pos.SendToKitchenAsync(Co, orderId);
			Assert.True(ok, err);

			decimal cheeseLeft = await OnHandAsync(_cheese);
			_out.WriteLine($"[T4] cheese needed=0.165 available=0.1 -> on hand now {cheeseLeft}");
			Assert.Equal(0m, cheeseLeft);                 // consumed to zero
			Assert.True(cheeseLeft >= 0m, "stock must never go negative under AllowAndRecord");

			var ev = await EventsAsync(orderId);
			_out.WriteLine($"[T4] events: {string.Join(", ", ev)}");
			Assert.Contains(PosPrepEvents.ShortageRecorded, ev);

			// the warehouse flag was never the mechanism
			using var db = _sql.ContextFor(_probe!, Co);
			Assert.False(await db.Warehouses.AsNoTracking().Where(w => w.ID == _wh).Select(w => w.AllowNegativeStock).FirstAsync());
		}

		// ===================================================================================================
		// 5 — IDEMPOTENCY. A retried kitchen send consumes exactly once.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_retried_kitchen_send_consumes_exactly_once()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_cheese, 100m, 20m); await ReceiveAsync(_sauce, 100m, 5m);

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var prep = sp.GetRequiredService<IPosPreparationService>();
			var orderId = await OpenOrderAsync(sp, _brDispatch, _pizza, 1m);
			Assert.True((await pos.SendToKitchenAsync(Co, orderId)).ok);
			decimal after1 = await OnHandAsync(_flour);

			using var db = _sql.ContextFor(_probe!, Co);
			var lineIds = await db.PosOrderLines.AsNoTracking().Where(l => l.OrderId == orderId).Select(l => l.ID).ToListAsync();

			// the kitchen action retried directly — and again through the order entry point
			var again = await prep.ConsumeForDispatchAsync(Co, orderId, lineIds, "uat");
			Assert.True(again.Ok, again.Error);
			var (sok2, _, sent2) = await pos.SendToKitchenAsync(Co, orderId);

			_out.WriteLine($"[T5] first={100m - after1} after retries={100m - await OnHandAsync(_flour)}; skipped={again.AlreadyConsumedLineIds.Count}, resent={sent2}");
			Assert.Equal(after1, await OnHandAsync(_flour));
			Assert.Equal(lineIds.Count, again.AlreadyConsumedLineIds.Count);
			Assert.Empty(again.ConsumedLineIds);

			// exactly one PosPrep movement per component
			var flourPrep = (await MovesAsync(_flour)).Count(m => m.SourceType == PosPreparationService.PrepSourceType && m.SourceId == orderId);
			Assert.Equal(1, flourPrep);
		}

		// ===================================================================================================
		// 6 — CANCEL BEFORE PREPARATION: nothing was consumed, so nothing is wasted.
		// ===================================================================================================
		[SkippableFact]
		public async Task Cancelling_before_preparation_consumes_nothing_and_wastes_nothing()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_cheese, 100m, 20m); await ReceiveAsync(_sauce, 100m, 5m);
			decimal f0 = await OnHandAsync(_flour);

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var orderId = await OpenOrderAsync(sp, _brDispatch, _pizza, 1m);
			var (vok, verr) = await pos.VoidOrderAsync(Co, orderId);
			Assert.True(vok, verr);

			_out.WriteLine($"[T6] flour before={f0} after cancel={await OnHandAsync(_flour)}");
			Assert.Equal(f0, await OnHandAsync(_flour));

			using var db = _sql.ContextFor(_probe!, Co);
			Assert.False(await db.JournalEntries.AsNoTracking().AnyAsync(j => j.SourceType == "PosWaste" && j.SourceId == orderId));
			Assert.DoesNotContain(PosPrepEvents.WasteRecorded, await EventsAsync(orderId));
		}

		// ===================================================================================================
		// 7 — CANCEL AFTER PREPARATION: the food is gone. Stock STAYS consumed, and the cost is reclassified
		// out of cost-of-sales into inventory adjustments, because nothing was sold.
		// ===================================================================================================
		[SkippableFact]
		public async Task Cancelling_after_preparation_keeps_the_stock_consumed_and_records_waste()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_cheese, 100m, 20m); await ReceiveAsync(_sauce, 100m, 5m);

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var orderId = await OpenOrderAsync(sp, _brDispatch, _pizza, 1m);
			Assert.True((await pos.SendToKitchenAsync(Co, orderId)).ok);
			decimal consumedFlour = 100m - await OnHandAsync(_flour);
			decimal afterPrep = await OnHandAsync(_flour);

			var (vok, verr) = await pos.VoidOrderAsync(Co, orderId);
			Assert.True(vok, verr);

			_out.WriteLine($"[T7] flour consumed at prep={consumedFlour}; after cancel on hand={await OnHandAsync(_flour)} (unchanged — the food is gone)");
			Assert.Equal(afterPrep, await OnHandAsync(_flour));   // NOT restored

			using var db = _sql.ContextFor(_probe!, Co);
			var waste = await db.JournalEntries.AsNoTracking().FirstOrDefaultAsync(j => j.SourceType == "PosWaste" && j.SourceId == orderId);
			Assert.NotNull(waste);
			var wl = await db.JournalEntryLines.AsNoTracking().Where(l => l.JournalEntryId == waste!.ID).ToListAsync();
			_out.WriteLine($"[T7] waste JE {waste!.ID}: " + string.Join(" | ", wl.Select(x => $"acct={x.AccountId} dr={x.Debit} cr={x.Credit}")));
			Assert.Equal(2, wl.Count);
			Assert.True(wl.Sum(x => x.Debit) == wl.Sum(x => x.Credit) && wl.Sum(x => x.Debit) > 0);
			Assert.Contains(PosPrepEvents.WasteRecorded, await EventsAsync(orderId));

			// idempotent: voiding again must not waste twice (the order is no longer Open, but the service
			// itself is the guard being proven)
			var prep = sp.GetRequiredService<IPosPreparationService>();
			var (aok, _, second) = await prep.RecordWasteForCancelledOrderAsync(Co, orderId, "retry", "uat");
			Assert.True(aok);
			Assert.Equal(0m, second);
			Assert.Equal(1, await db.JournalEntries.AsNoTracking().CountAsync(j => j.SourceType == "PosWaste" && j.SourceId == orderId));
		}

		// ===================================================================================================
		// 8 — THE DEFAULT IS UNCHANGED BEHAVIOUR. A branch that never chose a timing still deducts at Pay.
		// This is the regression guard for every existing installation.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_branch_with_no_capability_still_deducts_at_payment()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_cheese, 100m, 20m); await ReceiveAsync(_sauce, 100m, 5m);

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var orderId = await OpenOrderAsync(sp, _brPay, _pizza, 1m);
			Assert.True((await pos.SendToKitchenAsync(Co, orderId)).ok);

			decimal afterSend = await OnHandAsync(_flour);
			_out.WriteLine($"[T8] branch with NO capability row — after SendToKitchen flour={afterSend} (must be untouched)");
			Assert.Equal(100m, afterSend);   // dispatch consumed NOTHING

			var pay8 = await pos.PayAsync(Co, orderId, "Cash", 1);
			Assert.True(pay8.ok, pay8.error);
			_out.WriteLine($"[T8] after Pay flour={await OnHandAsync(_flour)} (deducted here, exactly as before)");
			Assert.Equal(99.5m, await OnHandAsync(_flour));
			Assert.Empty(await EventsAsync(orderId));
		}

		// ===================================================================================================
		// 9 — PAID RETURN. The financial reversal is preserved; eaten ingredients are not put back.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_paid_return_reverses_the_money_without_restoring_consumed_ingredients()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m); await ReceiveAsync(_cheese, 100m, 20m); await ReceiveAsync(_sauce, 100m, 5m);

			await using var sp = Graph();
			var pos = sp.GetRequiredService<IPosOrderService>();
			var orderId = await OpenOrderAsync(sp, _brDispatch, _pizza, 1m);
			Assert.True((await pos.SendToKitchenAsync(Co, orderId)).ok);
			var pay9 = await pos.PayAsync(Co, orderId, "Cash", 1);
			Assert.True(pay9.ok, pay9.error);
			decimal afterPay = await OnHandAsync(_flour);

			using var db = _sql.ContextFor(_probe!, Co);
			var line = await db.PosOrderLines.AsNoTracking().FirstAsync(l => l.OrderId == orderId);
			var (rok, rerr, retId) = await pos.ReturnOrderLinesAsync(Co, orderId,
				new List<SplitAllocation> { new() { LineId = line.ID, Qty = 1m } }, 1);
			_out.WriteLine($"[T9] return ok={rok} id={retId} ({rerr}); flour after pay={afterPay} after return={await OnHandAsync(_flour)}");
			Assert.True(rok, rerr);

			// the money came back; the flour did not
			Assert.Equal(afterPay, await OnHandAsync(_flour));
			Assert.True(await db.SalesReturns.AsNoTracking().AnyAsync(r => r.ID == retId));
		}

		// ===================================================================================================
		// 10 — COMPANY ISOLATION. One company's dispatch never reaches another company's stock or recipe.
		// ===================================================================================================
		[SkippableFact]
		public async Task Consumption_never_crosses_a_company_boundary()
		{
			Ready();
			await ReceiveAsync(_flour, 100m, 10m);
			decimal ownFlour = await OnHandAsync(_flour);

			await using var sp = Graph(Foreign);
			var prep = sp.GetRequiredService<IPosPreparationService>();
			// company 2 asking about company 1's order gets nothing, and consuming it is refused
			Assert.Empty(await prep.ConsumedLineIdsAsync(Foreign, 1));
			var r = await prep.ConsumeForDispatchAsync(Foreign, 999999, new List<int> { 1 }, "uat");
			_out.WriteLine($"[T10] foreign consume ok={r.Ok} error={r.Error}");
			Assert.False(r.Ok);

			Assert.Equal(ownFlour, await OnHandAsync(_flour));   // company 1 untouched
		}
	}
}
