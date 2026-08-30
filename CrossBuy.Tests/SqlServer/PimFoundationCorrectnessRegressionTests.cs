using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CrossBuy.Tests.SqlServer
{
	// TAB-5 — REGRESSION SUITE for the POS / Inventory / Manufacturing foundation-correctness batch.
	//
	// WHAT THIS FILE IS. Four defects were reproduced red before any fix, then fixed, and these are the same
	// tests kept as permanent regressions. Each one states the number the authoritative code produces, so
	// reverting the fix makes it fail rather than merely making it less tidy:
	//
	//   1. a released work order whose materials were worth NOTHING consumed its components twice
	//      (StockService: completion inferred "already issued" from a COST, wo.WipBalance > 0m);
	//   2. the Bundle explode route ignored planned scrap and skipped 4-decimal rounding
	//      (StockService: a bare req.Qty * c.Quantity, where every other BOM path applies the uplift);
	//   3. work-order stock movements carried no SourceId, so the physical half of a work order was
	//      untraceable while its journal entries were traceable;
	//   4. ManufService.CreateAsync enforced no tenancy at all — one company could open a work order on
	//      another company item, in another company warehouse, planning another company components.
	//
	// EVERY TEST HERE WAS OBSERVED RED FIRST. The measured before/after values are recorded next to each
	// assertion, because a green test that was never seen failing proves only that it compiles.
	//
	// WHY IT RUNS ON SQL SERVER. StockService reads the balance it is about to change with
	// `FROM StockBalances WITH (UPDLOCK, HOLDLOCK)` (FromSqlInterpolated) — T-SQL locking hints SQLite cannot
	// parse. Quantity arithmetic proven against a substitute writer would not be about the writer that
	// actually runs, so this family is SQL-Server-only and SKIPPED, never silently passed, without
	// CROSSBUY_TEST_SQL.
	//
	// DATA SAFETY. Own probe database per run (CrossBuyProbe_PIMFC_<guid>), created and dropped here. It never
	// opens CrossBuyDev, CrossBuyDB2 or production, and it does not share the platform fixture (RISK-036).
	[Collection(UatSqlProbeCollection.Name)]
	public class PimFoundationCorrectnessRegressionTests : IAsyncLifetime
	{
		private const int Co = 1;
		private const int Foreign = 2;   // an unrelated tenant
		private static readonly DateTime D = new(2026, 8, 25);

		private readonly UatSqlProbeFixture _sql;
		private readonly ITestOutputHelper _out;
		public PimFoundationCorrectnessRegressionTests(UatSqlProbeFixture sql, ITestOutputHelper output)
		{ _sql = sql; _out = output; }
		private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

		private UatSqlProbeFixture.ProbeDatabase? _probe;

		// Identity columns — assigned by the server and read back, never hardcoded.
		private int _wh, _catId;
		private int _rawScrap, _rawFractional, _rawFree, _bundle, _mfgItem, _freeParent;
		private int _whForeign, _itemForeign, _compForeign;

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("PIMFC");
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
			services.AddScoped<IBomExplosionService, BomExplosionService>();   // the canonical BOM explosion ManufService now resolves
			services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(new BusinessContext
			{
				CompanyId = companyId, EmployeeId = 1, UserId = "uat-pimfc",
				Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
			}));
			services.AddScoped<IBusinessEventService, BusinessEventService>();
			services.AddScoped<IManufService, ManufService>();
			return services.BuildServiceProvider();
		}

		private async Task ArrangeAsync()
		{
			using var db = _sql.ContextFor(_probe!, Co);

			// NOTE: this suite deliberately does NOT patch JournalEntries.EntryNo to nullable, unlike the sibling
			// acceptance family. It does not have to any more. JournalEntryService creates an entry as a DRAFT with
			// `EntryNo = null!` and numbers it on POST, and the EF model now declares `string? EntryNo` to match
			// that lifecycle, so the model-generated DDL this probe is built from already emits a nullable column.
			// If the draft insert here ever fails with "Cannot insert the value NULL into column 'EntryNo'", the
			// model has drifted back and THAT is the finding.

			// Currency FIRST. CurrencyRounding.DecimalsAsync THROWS rather than assuming 2 decimals when no
			// currency resolves, and the company falls back to whichever Currency has code "EGP". Without this row
			// every movement in the arrangement is refused before it reaches any arithmetic.
			var egp = new Currency { Code = "EGP", Name = "جنيه مصري", NameEn = "Egyptian Pound", DecimalPlaces = 2 };
			db.Currencies.Add(egp);
			await db.SaveChangesAsync();

			Account Acct(string code, string name, int companyId) =>
				new() { Code = code, Name = name, NameEn = name, CompanyID = companyId, AccountTypeId = 1, IsPostable = true, IsActive = true };
			var inv = Acct("110301", "المخزون", Co);
			var cogs = Acct("510101", "تكلفة المبيعات", Co);
			var wip = Acct("1105", "إنتاج تحت التشغيل", Co);
			var applied = Acct("520108", "تكاليف مطبقة", Co);
			var variance = Acct("520109", "فروق تكلفة", Co);
			var adj = Acct("520110", "تسويات مخزنية", Co);
			db.Accounts.AddRange(inv, cogs, wip, applied, variance, adj);
			await db.SaveChangesAsync();

			var cat = new ItemCategory
			{
				Code = "PIMFC", Name = "PIMFC", NameEn = "PIMFC", CompanyID = Co,
				InventoryAccountId = inv.ID, CogsAccountId = cogs.ID, AdjustmentAccountId = adj.ID,
			};
			db.ItemCategories.Add(cat);
			await db.SaveChangesAsync();
			_catId = cat.ID;

			// PeriodGuardAsync refuses every movement without an open period.
			var fy = new FiscalYear { CompanyID = Co, Name = "2026", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31), Status = "Open" };
			db.FiscalYears.Add(fy);
			await db.SaveChangesAsync();
			db.FiscalPeriods.Add(new FiscalPeriod { FiscalYearId = fy.ID, PeriodNo = 8, StartDate = new DateTime(2026, 8, 1), EndDate = new DateTime(2026, 8, 31), Status = "Open" });

			var wh = new Warehouse { Code = "PIMFC-WH", Name = "PIMFC-WH", NameEn = "PIMFC-WH", CompanyID = Co, WarehouseType = "Main", AllowNegativeStock = false, IsActive = true };
			db.Warehouses.Add(wh);
			await db.SaveChangesAsync();
			_wh = wh.ID;

			int seq = 0;
			Item It(string code, bool composite = false, string? compositeType = null) => new()
			{
				ItemCode = code, Barcode = "P" + code + (++seq), Name = code, NameEn = code,
				CompanyID = Co, ItemCategoryId = _catId, ItemType = "Stockable",
				BaseUoMId = 1, IsActive = true, IsComposite = composite, CompositeType = compositeType,
				ProductionMethod = "OrderBased",
			};
			var rawScrap = It("PF-RAW-SCRAP");            // 10% planned scrap component
			var rawFractional = It("PF-RAW-FRAC");        // fractional per-unit quantity, for the rounding proof
			var rawFree = It("PF-RAW-FREE");              // received at ZERO cost — the duplicate-consumption trigger
			var bundle = It("PF-BUNDLE", composite: true, compositeType: "Bundle");
			var mfgItem = It("PF-MFG", composite: true, compositeType: "Assembly");
			var freeParent = It("PF-MFG-FREE", composite: true, compositeType: "Assembly");
			db.Items.AddRange(rawScrap, rawFractional, rawFree, bundle, mfgItem, freeParent);
			await db.SaveChangesAsync();
			_rawScrap = rawScrap.ID; _rawFractional = rawFractional.ID; _rawFree = rawFree.ID;
			_bundle = bundle.ID; _mfgItem = mfgItem.ID; _freeParent = freeParent.ID;

			// The BOMs. Every quantity is chosen so the authoritative formula and the old Bundle formula give
			// DIFFERENT answers — that difference was the defect, and a BOM with no scrap could not show it.
			db.ItemComponents.AddRange(
				// 1 bundle draws 2 * 1.10 = 2.2 of PF-RAW-SCRAP
				new ItemComponent { CompanyID = Co, ParentItemId = _bundle, ComponentItemId = _rawScrap, Quantity = 2m, ScrapPct = 10m, SortOrder = 1 },
				// and 3 bundles draw 3 * 0.3333 * 1.05 = 1.0499475 -> 1.0499 at 4dp of PF-RAW-FRAC
				new ItemComponent { CompanyID = Co, ParentItemId = _bundle, ComponentItemId = _rawFractional, Quantity = 0.3333m, ScrapPct = 5m, SortOrder = 2 },
				// the manufacturing comparison BOM: identical components and scrap, taken through ManufService
				new ItemComponent { CompanyID = Co, ParentItemId = _mfgItem, ComponentItemId = _rawScrap, Quantity = 2m, ScrapPct = 10m, SortOrder = 1 },
				new ItemComponent { CompanyID = Co, ParentItemId = _mfgItem, ComponentItemId = _rawFractional, Quantity = 0.3333m, ScrapPct = 5m, SortOrder = 2 },
				// the zero-cost BOM for the duplicate-consumption regression
				new ItemComponent { CompanyID = Co, ParentItemId = _freeParent, ComponentItemId = _rawFree, Quantity = 4m, ScrapPct = 0m, SortOrder = 1 });
			await db.SaveChangesAsync();

			// ---- AN UNRELATED TENANT, for the cross-company work-order negatives. It gets everything a real
			// tenant has: its own accounts, category, fiscal period, warehouse, manufactured item and a real BOM.
			// The BOM matters: without one ManufService refuses for a reason that has nothing to do with tenancy,
			// and a test satisfied by that refusal would be green while proving nothing about isolation.
			var invF = Acct("110301", "Inventory-Co2", Foreign);
			var cogsF = Acct("510101", "COGS-Co2", Foreign);
			var adjF = Acct("520110", "Adj-Co2", Foreign);
			db.Accounts.AddRange(invF, cogsF, adjF);
			await db.SaveChangesAsync();

			var catF = new ItemCategory { Code = "PIMFC2", Name = "PIMFC2", NameEn = "PIMFC2", CompanyID = Foreign,
				InventoryAccountId = invF.ID, CogsAccountId = cogsF.ID, AdjustmentAccountId = adjF.ID };
			db.ItemCategories.Add(catF);
			await db.SaveChangesAsync();

			var fyF = new FiscalYear { CompanyID = Foreign, Name = "2026", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31), Status = "Open" };
			db.FiscalYears.Add(fyF);
			await db.SaveChangesAsync();
			db.FiscalPeriods.Add(new FiscalPeriod { FiscalYearId = fyF.ID, PeriodNo = 8, StartDate = new DateTime(2026, 8, 1), EndDate = new DateTime(2026, 8, 31), Status = "Open" });

			var whF = new Warehouse { Code = "PIMFC-WH2", Name = "PIMFC-WH2", NameEn = "PIMFC-WH2", CompanyID = Foreign, WarehouseType = "Main", AllowNegativeStock = false, IsActive = true };
			db.Warehouses.Add(whF);
			await db.SaveChangesAsync();
			_whForeign = whF.ID;

			var itemF = new Item { ItemCode = "PF-CO2-FIN", Barcode = "PF-CO2-FIN", Name = "PF-CO2-FIN", NameEn = "PF-CO2-FIN",
				CompanyID = Foreign, ItemCategoryId = catF.ID, ItemType = "Stockable", BaseUoMId = 1, IsActive = true,
				IsComposite = true, CompositeType = "Assembly", ProductionMethod = "OrderBased" };
			var compF = new Item { ItemCode = "PF-CO2-RAW", Barcode = "PF-CO2-RAW", Name = "PF-CO2-RAW", NameEn = "PF-CO2-RAW",
				CompanyID = Foreign, ItemCategoryId = catF.ID, ItemType = "Stockable", BaseUoMId = 1, IsActive = true };
			db.Items.AddRange(itemF, compF);
			await db.SaveChangesAsync();
			_itemForeign = itemF.ID; _compForeign = compF.ID;

			db.ItemComponents.Add(new ItemComponent { CompanyID = Foreign, ParentItemId = itemF.ID, ComponentItemId = compF.ID, Quantity = 2m, ScrapPct = 0m, SortOrder = 1 });
			await db.SaveChangesAsync();
		}

		// ---- helpers -------------------------------------------------------------------------------------
		private async Task MustReceiveAsync(int itemId, decimal qty, decimal unitCost)
		{
			await using var sp = Graph();
			var (ok, err, _) = await sp.GetRequiredService<IStockService>().PostMovementAsync(Co, new MovementRequest
			{
				Date = D, ItemId = itemId, WarehouseId = _wh, Direction = 1, Qty = qty,
				UnitCostInBase = unitCost, SourceType = "Opening", PostToGl = true,
			}, "uat");
			Assert.True(ok, $"arrangement receipt failed for item {itemId}: {err}");
		}

		private async Task<decimal> IssuedQtyAsync(int itemId)
		{
			using var db = _sql.ContextFor(_probe!, Co);
			return await db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == Co && m.ItemId == itemId && m.Direction == -1)
				.SumAsync(m => (decimal?)m.QtyBase) ?? 0m;
		}

		private async Task<List<StockMovement>> MovementsAsync(int itemId)
		{
			using var db = _sql.ContextFor(_probe!, Co);
			return await db.StockMovements.AsNoTracking()
				.Where(m => m.CompanyID == Co && m.ItemId == itemId)
				.OrderBy(m => m.ID).ToListAsync();
		}

		// ===================================================================================================
		// DEFECT 2 — THE BUNDLE ROUTE MUST APPLY PLANNED SCRAP, LIKE EVERY OTHER BOM PATH.
		//
		// There were two BOM-explosion formulas in the single stock writer and they did not agree:
		//
		//   authoritative   Math.Round(qty * c.Quantity * (1 + c.ScrapPct / 100m), 4)
		//   Bundle explode  req.Qty * c.Quantity
		//
		// The Bundle line applied neither the scrap uplift nor the 4-decimal rounding, so the SAME recipe
		// consumed a different amount of the SAME component depending only on which route the sale took — and
		// the Bundle route consumed LESS than planned, which silently overstates stock on hand and understates
		// the cost of the thing sold.
		//
		// MEASURED: 2.0000 before the fix, 2.2 after.
		// ===================================================================================================
		[SkippableFact]
		public async Task Bundle_explode_applies_planned_scrap_like_every_other_BOM_path()
		{
			Ready();
			await MustReceiveAsync(_rawScrap, 100m, 5m);
			await MustReceiveAsync(_rawFractional, 100m, 5m);

			await using var sp = Graph();
			// Issuing the bundle is what explodes it: a Bundle holds no stock of its own, so the write turns
			// into one issue per component.
			var (ok, err, _) = await sp.GetRequiredService<IStockService>().PostMovementAsync(Co, new MovementRequest
			{
				Date = D, ItemId = _bundle, WarehouseId = _wh, Direction = -1, Qty = 1m,
				SourceType = "SalesInvoice", PostToGl = true,
			}, "uat");
			Assert.True(ok, $"bundle issue refused: {err}");

			var scrapComponent = await IssuedQtyAsync(_rawScrap);
			_out.WriteLine($"[scrap] PF-RAW-SCRAP consumed for 1 bundle: {scrapComponent} (planned 2 * 1.10 = 2.2; was 2.0000 before the fix)");

			Assert.Equal(2.2m, scrapComponent);
		}

		[SkippableFact]
		public async Task Bundle_explode_rounds_component_quantity_to_four_decimals()
		{
			Ready();
			await MustReceiveAsync(_rawScrap, 100m, 5m);
			await MustReceiveAsync(_rawFractional, 100m, 5m);

			await using var sp = Graph();
			var (ok, err, _) = await sp.GetRequiredService<IStockService>().PostMovementAsync(Co, new MovementRequest
			{
				Date = D, ItemId = _bundle, WarehouseId = _wh, Direction = -1, Qty = 3m,
				SourceType = "SalesInvoice", PostToGl = true,
			}, "uat");
			Assert.True(ok, $"bundle issue refused: {err}");

			// 3 * 0.3333 * 1.05 = 1.0499475, which the authoritative formula rounds to 1.0499 at 4dp.
			var frac = await IssuedQtyAsync(_rawFractional);
			_out.WriteLine($"[scrap] PF-RAW-FRAC consumed for 3 bundles: {frac} (authoritative 1.0499; was 0.9999 before the fix)");
			Assert.Equal(1.0499m, frac);
		}

		// ===================================================================================================
		// DEFECT 2 — THE CONTROL. The identical BOM at the identical quantity, down the authoritative route.
		//
		// This test was GREEN before the fix too, and that is the point: it is what made the two failures above
		// a DISAGREEMENT rather than an opinion about what planned scrap ought to mean. It stays as the anchor
		// the Bundle route is held against, so if someone later changes the authoritative formula, the pair
		// fails together instead of drifting apart silently.
		//
		//   PF-RAW-SCRAP  3 * 2      * 1.10 = 6.6
		//   PF-RAW-FRAC   3 * 0.3333 * 1.05 = 1.0499475 -> 1.0499 at 4dp
		// ===================================================================================================
		[SkippableFact]
		public async Task Authoritative_BOM_route_applies_scrap_and_rounding()
		{
			Ready();
			await MustReceiveAsync(_rawScrap, 100m, 5m);
			await MustReceiveAsync(_rawFractional, 100m, 5m);

			await using var sp = Graph();
			var manuf = sp.GetRequiredService<IManufService>();
			var (cok, cerr, woId) = await manuf.CreateAsync(Co, _mfgItem, 3m, _wh, D, D, 0m, 0m, "pimfc-control", "uat");
			Assert.True(cok, $"work order create refused: {cerr}");

			// 1) the PLANNED quantities already carry the scrap uplift and the 4dp rounding
			using (var db = _sql.ContextFor(_probe!, Co))
			{
				var planned = await db.ManufWorkOrderComponents.AsNoTracking()
					.Where(c => c.CompanyID == Co && c.WorkOrderId == woId)
					.ToDictionaryAsync(c => c.ItemId, c => c.PlannedQty);
				foreach (var kv in planned) _out.WriteLine($"[control] planned item={kv.Key} qty={kv.Value}");
				Assert.Equal(6.6m, planned[_rawScrap]);
				Assert.Equal(1.0499m, planned[_rawFractional]);
			}

			// 2) and release ISSUES exactly those planned quantities, so the uplift reaches real stock
			var (rok, rerr) = await manuf.ReleaseAsync(Co, woId, D, "uat");
			Assert.True(rok, $"work order release refused: {rerr}");

			var scrapIssued = await IssuedQtyAsync(_rawScrap);
			var fracIssued = await IssuedQtyAsync(_rawFractional);
			_out.WriteLine($"[control] issued PF-RAW-SCRAP={scrapIssued} PF-RAW-FRAC={fracIssued} " +
				"(the Bundle route drew 2.0000 and 0.9999 for the same recipe before the fix)");
			Assert.Equal(6.6m, scrapIssued);
			Assert.Equal(1.0499m, fracIssued);
		}

		// ===================================================================================================
		// DEFECT 1 — A RELEASED WORK ORDER WHOSE MATERIALS WERE WORTH NOTHING MUST NOT CONSUME THEM TWICE.
		//
		// Release issued the components and recorded what it had done as a VALUE:
		//     wo.MaterialCost = material; wo.WipBalance = material;      (unconditional)
		// Completion then asked whether the issue had happened by reading that value back:
		//     bool alreadyIssued = wo.WipBalance > 0m;
		//
		// Value is the wrong evidence for a lifecycle question. When the issued materials are worth nothing —
		// a zero-cost component, a promotional or sample input, a raw with no cost yet loaded, a fully
		// written-down batch — `material` is 0, so WipBalance is 0, so completion concluded nothing was issued
		// and issued the WHOLE BOM a second time. The neighbouring line already treats 0 as a real possibility:
		// the release journal entry is posted only `if (material != 0m)`, so zero-valued material is an
		// ANTICIPATED state, not a freak input.
		//
		// The fix reads lifecycle evidence instead: `wo.ReleasedAt != null` (plus the Released/InProgress status
		// terms as a belt). ReleasedAt is stamped in the same statement that sets Status = "Released" and is
		// never cleared. Status ALONE would not do — an order that receives sourced labour moves
		// Released -> InProgress, so a status equality check would re-issue for exactly the orders that had
		// done the most work.
		//
		// MEASURED: 8 consumed before the fix (BOM of 4, issued twice), 4 after.
		// ===================================================================================================
		[SkippableFact]
		public async Task Zero_valued_material_issue_is_not_consumed_again_on_completion()
		{
			Ready();
			// Received at ZERO unit cost, and generously, so a second consumption would not be masked by a
			// stock shortage: the defect had to show up as a WRONG QUANTITY, not as a refusal.
			await MustReceiveAsync(_rawFree, 100m, 0m);

			await using var sp = Graph();
			var manuf = sp.GetRequiredService<IManufService>();

			// OrderBased so the order is staged: created, then released (issuing materials), then completed.
			var (cok, cerr, woId) = await manuf.CreateAsync(Co, _freeParent, 1m, _wh, D, D, 0m, 0m, "pimfc-free", "uat");
			Assert.True(cok, $"work order create refused: {cerr}");

			var (rok, rerr) = await manuf.ReleaseAsync(Co, woId, D, "uat");
			Assert.True(rok, $"work order release refused: {rerr}");

			var afterRelease = await IssuedQtyAsync(_rawFree);
			_out.WriteLine($"[dup] after RELEASE, PF-RAW-FREE issued = {afterRelease} (BOM says 4)");
			Assert.Equal(4m, afterRelease);

			// The exact state that used to fool completion: released, and worth nothing.
			using (var db = _sql.ContextFor(_probe!, Co))
			{
				var wo = await db.ManufWorkOrders.AsNoTracking().FirstAsync(w => w.ID == woId);
				_out.WriteLine($"[dup] work order {woId}: Status={wo.Status} MaterialCost={wo.MaterialCost} " +
					$"WipBalance={wo.WipBalance} ReleasedAt={(wo.ReleasedAt.HasValue ? "set" : "NULL")} " +
					"— WipBalance is still 0, so this passes on lifecycle evidence, not on value");
				Assert.Equal(0m, wo.WipBalance);          // the trigger condition is still present...
				Assert.NotNull(wo.ReleasedAt);            // ...and the evidence the fix relies on is what carries it
			}

			var (ok, err, unitCost) = await manuf.CompleteAsync(Co, woId, D, "uat");
			Assert.True(ok, $"work order completion refused: {err}");

			var afterComplete = await IssuedQtyAsync(_rawFree);
			_out.WriteLine($"[dup] after COMPLETE, PF-RAW-FREE issued = {afterComplete} (must still be 4; was 8 before the fix); unitCost={unitCost}");

			// THE ASSERTION THAT MATTERS. Completion must not re-issue what release already issued. One
			// quantity issue only, whatever that quantity happened to be WORTH.
			Assert.Equal(4m, afterComplete);
		}

		// ===================================================================================================
		// DEFECT 1, THE OTHER HALF OF THE LIFECYCLE. The fix must not break the direct/quick path.
		//
		// An Immediate-mode order is completed WITHOUT a separate Release, and in that case completion is the
		// thing that issues the materials. The old value-based check happened to allow this because WipBalance
		// was 0 on a Draft order; the new lifecycle check must allow it for the right reason — ReleasedAt is
		// null and the status is still Draft, so nothing has been issued yet.
		// ===================================================================================================
		[SkippableFact]
		public async Task Completing_without_releasing_still_issues_the_materials_once()
		{
			Ready();
			await MustReceiveAsync(_rawFree, 100m, 7m);

			await using var sp = Graph();
			var manuf = sp.GetRequiredService<IManufService>();
			var (cok, cerr, woId) = await manuf.CreateAsync(Co, _freeParent, 1m, _wh, D, D, 0m, 0m, "pimfc-direct", "uat");
			Assert.True(cok, $"work order create refused: {cerr}");

			using (var db = _sql.ContextFor(_probe!, Co))
			{
				var wo = await db.ManufWorkOrders.AsNoTracking().FirstAsync(w => w.ID == woId);
				Assert.Equal("Draft", wo.Status);
				Assert.Null(wo.ReleasedAt);   // nothing issued yet — completion must do it
			}

			var (ok, err, _) = await manuf.CompleteAsync(Co, woId, D, "uat");
			Assert.True(ok, $"direct completion refused: {err}");

			var issued = await IssuedQtyAsync(_rawFree);
			_out.WriteLine($"[dup] complete-without-release issued PF-RAW-FREE = {issued} (BOM says 4, exactly once)");
			Assert.Equal(4m, issued);
		}

		// ===================================================================================================
		// DEFECT 1, IDEMPOTENCY. Completing twice must stay refused by the existing lifecycle guard, and the
		// refusal must not consume anything on the way out.
		// ===================================================================================================
		[SkippableFact]
		public async Task Completing_a_completed_work_order_is_refused_and_consumes_nothing()
		{
			Ready();
			await MustReceiveAsync(_rawFree, 100m, 3m);

			await using var sp = Graph();
			var manuf = sp.GetRequiredService<IManufService>();
			var (cok, cerr, woId) = await manuf.CreateAsync(Co, _freeParent, 1m, _wh, D, D, 0m, 0m, "pimfc-twice", "uat");
			Assert.True(cok, $"work order create refused: {cerr}");
			Assert.True((await manuf.ReleaseAsync(Co, woId, D, "uat")).ok);
			Assert.True((await manuf.CompleteAsync(Co, woId, D, "uat")).ok);

			var afterFirst = await IssuedQtyAsync(_rawFree);

			var (ok, err, _) = await manuf.CompleteAsync(Co, woId, D, "uat");
			_out.WriteLine($"[dup] second COMPLETE refused={!ok} ({err})");
			Assert.False(ok, "a completed work order was completed a second time");

			var afterSecond = await IssuedQtyAsync(_rawFree);
			_out.WriteLine($"[dup] consumption before={afterFirst} after the refused repeat={afterSecond}");
			Assert.Equal(afterFirst, afterSecond);
		}

		// ===================================================================================================
		// DEFECT 3 — WORK-ORDER STOCK MOVEMENTS MUST SAY WHICH WORK ORDER THEY BELONG TO.
		//
		// The movements were stamped `SourceType = "WorkOrder"` but left `SourceId` null, while the JOURNAL
		// ENTRIES for the very same transitions DID carry `SourceId = wo.ID`. So the financial half of a work
		// order was traceable and the physical half was not: given a work order there was no query that
		// returned the component issues and the finished receipt belonging to it. `StockMovement.SourceId` is
		// an existing nullable column — this needed no new table, only the value it was built to hold.
		//
		// MEASURED: sourceId=NULL on both movements before the fix, sourceId=<wo id> after.
		// ===================================================================================================
		[SkippableFact]
		public async Task Work_order_stock_movements_carry_the_work_order_id_in_SourceId()
		{
			Ready();
			await MustReceiveAsync(_rawFree, 100m, 3m);

			await using var sp = Graph();
			var manuf = sp.GetRequiredService<IManufService>();
			var (cok, cerr, woId) = await manuf.CreateAsync(Co, _freeParent, 1m, _wh, D, D, 0m, 0m, "pimfc-trace", "uat");
			Assert.True(cok, $"work order create refused: {cerr}");
			var (rok, rerr) = await manuf.ReleaseAsync(Co, woId, D, "uat");
			Assert.True(rok, $"work order release refused: {rerr}");
			var (ok, err, _) = await manuf.CompleteAsync(Co, woId, D, "uat");
			Assert.True(ok, $"work order completion refused: {err}");

			// Every movement this work order caused, from BOTH ends of the chain.
			var component = await MovementsAsync(_rawFree);
			var finished = await MovementsAsync(_freeParent);
			var woMovements = component.Concat(finished).Where(m => m.SourceType == "WorkOrder").ToList();

			foreach (var m in woMovements)
				_out.WriteLine($"[trace] movement {m.ID}: item={m.ItemId} dir={m.Direction} qty={m.QtyBase} " +
					$"sourceType={m.SourceType} sourceId={(m.SourceId?.ToString() ?? "NULL")}");

			Assert.NotEmpty(woMovements);

			// THE TRACEABILITY CLAIM, stated as the query a user would actually ask: from the work-order id,
			// reach its exact component issues AND its finished receipt.
			var unstamped = woMovements.Where(m => m.SourceId != woId).ToList();
			Assert.True(unstamped.Count == 0,
				$"{unstamped.Count} of {woMovements.Count} WorkOrder movements do not point at work order {woId} " +
				$"(SourceId values: {string.Join(", ", unstamped.Select(m => m.SourceId?.ToString() ?? "NULL"))})");

			Assert.Contains(woMovements, m => m.ItemId == _rawFree && m.Direction == -1);   // component issue
			Assert.Contains(woMovements, m => m.ItemId == _freeParent && m.Direction == 1); // finished receipt
		}

		// ===================================================================================================
		// DEFECT 4 — ManufService.CreateAsync MUST ENFORCE THE COMPANY BOUNDARY ON EVERY INPUT.
		//
		// Found by the cross-company tests this batch required. Three omissions combined:
		//   * the BOM was read as `Where(c => c.ParentItemId == itemId)` with NO company predicate, so any
		//     company recipe was readable and usable by any other;
		//   * the manufactured item WAS read with a company predicate, but only to pick a production mode, and
		//     a null result silently defaulted to "OrderBased" instead of refusing;
		//   * warehouseId was checked for `> 0` and never for ownership.
		//
		// The result was not a stray row: the component rows are stamped `CompanyID = companyId` (the CALLER)
		// while `ItemId` pointed at the OTHER tenant components, so the order was a cross-tenant hybrid that
		// would consume caller stock against a recipe it was never entitled to read.
		//
		// MEASURED before the fix: both creates SUCCEEDED (work orders 1 and 2), and the planned component row
		// read `companyId=1 itemId=<company 2 component>`.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_company_cannot_open_a_work_order_on_another_company_item_or_warehouse()
		{
			Ready();
			await MustReceiveAsync(_rawFree, 100m, 3m);

			await using var sp = Graph(Co);
			var manuf = sp.GetRequiredService<IManufService>();

			// Company 1 naming the manufactured item of company 2, into its OWN warehouse.
			var (foreignItem, e1, id1) = await manuf.CreateAsync(Co, _itemForeign, 1m, _wh, D, D, 0m, 0m, "pimfc-iso", "uat");
			// Company 1 naming its own item, into the warehouse of company 2.
			var (foreignWh, e2, id2) = await manuf.CreateAsync(Co, _freeParent, 1m, _whForeign, D, D, 0m, 0m, "pimfc-iso", "uat");
			_out.WriteLine($"[iso-mfg] work order on a foreign ITEM      refused={!foreignItem} id={id1} ({e1})");
			_out.WriteLine($"[iso-mfg] work order into a foreign WAREHOUSE refused={!foreignWh} id={id2} ({e2})");

			Assert.False(foreignItem, "company 1 opened a work order on an item belonging to company 2");
			Assert.False(foreignWh, "company 1 opened a work order into a warehouse belonging to company 2");

			// A FOREIGN ID AND A MISSING ID MUST BE INDISTINGUISHABLE. If "belongs to someone else" and "does
			// not exist" said different things, an id here would become a tenant probe: a caller could discover
			// which ids exist in other companies by reading the refusal text.
			var (missingItem, e3, _) = await manuf.CreateAsync(Co, 987654, 1m, _wh, D, D, 0m, 0m, "pimfc-iso", "uat");
			var (missingWh, e4, _) = await manuf.CreateAsync(Co, _freeParent, 1m, 987654, D, D, 0m, 0m, "pimfc-iso", "uat");
			_out.WriteLine($"[iso-mfg] missing ITEM      refused={!missingItem} ({e3})");
			_out.WriteLine($"[iso-mfg] missing WAREHOUSE refused={!missingWh} ({e4})");
			Assert.False(missingItem);
			Assert.False(missingWh);
			Assert.Equal(e3, e1);   // foreign item      == missing item
			Assert.Equal(e4, e2);   // foreign warehouse == missing warehouse
		}

		[SkippableFact]
		public async Task A_refused_cross_company_work_order_writes_nothing_at_all()
		{
			Ready();

			int woBefore, compBefore;
			using (var db = _sql.ContextFor(_probe!, Co))
			{
				woBefore = await db.ManufWorkOrders.AsNoTracking().CountAsync();
				compBefore = await db.ManufWorkOrderComponents.AsNoTracking().CountAsync();
			}

			await using var sp = Graph(Co);
			var manuf = sp.GetRequiredService<IManufService>();
			Assert.False((await manuf.CreateAsync(Co, _itemForeign, 1m, _wh, D, D, 0m, 0m, "iso", "uat")).ok);
			Assert.False((await manuf.CreateAsync(Co, _freeParent, 1m, _whForeign, D, D, 0m, 0m, "iso", "uat")).ok);
			Assert.False((await manuf.CreateAsync(Co, _itemForeign, 1m, _whForeign, D, D, 0m, 0m, "iso", "uat")).ok);

			// REFUSAL BEFORE ANY PARTIAL BUSINESS WRITE. The header used to be inserted and saved before the
			// component rows were built, so a guard placed later would have left an orphan work order behind.
			using (var db = _sql.ContextFor(_probe!, Co))
			{
				var woAfter = await db.ManufWorkOrders.AsNoTracking().CountAsync();
				var compAfter = await db.ManufWorkOrderComponents.AsNoTracking().CountAsync();
				_out.WriteLine($"[iso-mfg] work orders {woBefore}->{woAfter}, planned components {compBefore}->{compAfter} after 3 refusals");
				Assert.Equal(woBefore, woAfter);
				Assert.Equal(compBefore, compAfter);

				// and nothing anywhere references the other tenant rows
				Assert.Equal(0, await db.ManufWorkOrders.AsNoTracking().CountAsync(w => w.ItemId == _itemForeign));
				Assert.Equal(0, await db.ManufWorkOrderComponents.AsNoTracking().CountAsync(c => c.ItemId == _compForeign));
			}
		}

		[SkippableFact]
		public async Task A_same_company_work_order_still_succeeds_and_leaks_no_foreign_component()
		{
			Ready();
			await MustReceiveAsync(_rawFree, 100m, 3m);

			await using var sp = Graph(Co);
			var manuf = sp.GetRequiredService<IManufService>();

			// The positive case. Tightening a boundary is only correct if the legitimate path still works —
			// a guard that refuses everything would pass every negative above and be useless.
			var (ok, err, woId) = await manuf.CreateAsync(Co, _freeParent, 2m, _wh, D, D, 0m, 0m, "pimfc-ok", "uat");
			_out.WriteLine($"[iso-mfg] same-company work order created={ok} id={woId} ({err})");
			Assert.True(ok, $"a legitimate same-company work order was refused: {err}");
			Assert.True(woId > 0);

			using (var db = _sql.ContextFor(_probe!, Co))
			{
				var rows = await db.ManufWorkOrderComponents.AsNoTracking()
					.Where(c => c.WorkOrderId == woId).ToListAsync();
				Assert.NotEmpty(rows);

				// NO FOREIGN COMPONENT IDENTITY IN CALLER ROWS. Every planned component must be stamped with the
				// caller company AND point at an item that actually belongs to that company. The old defect
				// produced exactly the opposite: CompanyID = caller, ItemId = other tenant.
				var ownedItemIds = await db.Items.AsNoTracking()
					.Where(i => i.CompanyID == Co).Select(i => i.ID).ToListAsync();
				foreach (var r in rows)
				{
					_out.WriteLine($"[iso-mfg] planned component: companyId={r.CompanyID} itemId={r.ItemId} qty={r.PlannedQty}");
					Assert.Equal(Co, r.CompanyID);
					Assert.Contains(r.ItemId, ownedItemIds);
				}
			}
		}
	}
}
