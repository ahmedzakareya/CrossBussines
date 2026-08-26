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
	// TAB-5 — COMPANY ISOLATION for the POS / Inventory / Manufacturing surface.
	//
	// WHAT THIS PINS. InventoryController and PosController used to carry `private const int DefaultCompanyId = 1;`
	// and hand that literal to every read, write, report and lookup — 267 and 36 uses respectively. Whoever was
	// signed in, the module addressed company 1. That was not a display bug: the constant reached
	// PostMovementAsync, TransferAsync, WriteOffAsync, PostCountAsync, PostLandedCostAsync and the whole
	// ManufService write surface, so the stock of another tenant was writable from any session. Both controllers
	// now resolve the company once per request through IRequestCompanyResolver and fail closed at 0.
	//
	// WHY THESE TESTS GO THROUGH SERVICES, NOT THROUGH THE CONTROLLERS. The isolation that has to hold is the one
	// the controller DELEGATES to: 135 of the converted call sites pass the resolved company as the companyId
	// argument of a service, so what protects a tenant is whether those services refuse a company they were not
	// given. Asserting that a controller action returns some ViewResult would prove routing, not isolation. These
	// tests drive the same services with the same arguments the converted controllers now produce — including 0,
	// the value an unresolved session yields.
	//
	// RELATIONAL, not single-table. The real hazard is a MIX: an item id from one company with a warehouse id
	// from another. Each id exists, each is valid alone, and only the relation between them is wrong — which is
	// exactly the shape a stale hardcoded company produced. Every negative below crosses at least one relation.
	//
	// SQL Server, own probe database per run (CrossBuyProbe_PIMISO_<guid>), created and dropped here. Never
	// CrossBuyDev, never CrossBuyDB2, never production, and not the shared platform fixture (RISK-036).
	[Collection(UatSqlProbeCollection.Name)]
	public class PimCompanyIsolationTests : IAsyncLifetime
	{
		private const int Co = 1;          // the tenant under test
		private const int Foreign = 2;     // an unrelated tenant
		private const int Unresolved = 0;  // what IRequestCompanyResolver yields when no company resolves
		private static readonly DateTime D = new(2026, 8, 25);

		private readonly UatSqlProbeFixture _sql;
		private readonly ITestOutputHelper _out;
		public PimCompanyIsolationTests(UatSqlProbeFixture sql, ITestOutputHelper output) { _sql = sql; _out = output; }
		private void Ready() => Skip.If(!_sql.Available, _sql.SkipReason);

		private UatSqlProbeFixture.ProbeDatabase? _probe;
		private int _whOwn, _whForeign, _itemOwn, _itemForeign;

		public async Task InitializeAsync()
		{
			if (!_sql.Available) return;
			_probe = await _sql.CreateProbeDatabaseAsync("PIMISO");
			await ArrangeAsync();
		}

		public async Task DisposeAsync()
		{
			if (_probe != null) await _sql.DropProbeDatabaseAsync(_probe);
		}

		private ServiceProvider Graph(int companyId)
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
			services.AddScoped<IBusinessContextAccessor>(_ => new StubContextAccessor(new BusinessContext
			{
				CompanyId = companyId, EmployeeId = 1, UserId = "uat-pimiso",
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
			var inv = Acct("110301", "المخزون", Co);
			var cogs = Acct("510101", "تكلفة المبيعات", Co);
			var adj = Acct("520110", "تسويات", Co);
			var invF = Acct("110301", "Inventory-Co2", Foreign);
			var cogsF = Acct("510101", "COGS-Co2", Foreign);
			var adjF = Acct("520110", "Adj-Co2", Foreign);
			db.Accounts.AddRange(inv, cogs, adj, invF, cogsF, adjF);
			await db.SaveChangesAsync();

			var cat = new ItemCategory { Code = "ISO1", Name = "ISO1", NameEn = "ISO1", CompanyID = Co, InventoryAccountId = inv.ID, CogsAccountId = cogs.ID, AdjustmentAccountId = adj.ID };
			var catF = new ItemCategory { Code = "ISO2", Name = "ISO2", NameEn = "ISO2", CompanyID = Foreign, InventoryAccountId = invF.ID, CogsAccountId = cogsF.ID, AdjustmentAccountId = adjF.ID };
			db.ItemCategories.AddRange(cat, catF);
			await db.SaveChangesAsync();

			foreach (var c in new[] { Co, Foreign })
			{
				var fy = new FiscalYear { CompanyID = c, Name = "2026", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 12, 31), Status = "Open" };
				db.FiscalYears.Add(fy);
				await db.SaveChangesAsync();
				db.FiscalPeriods.Add(new FiscalPeriod { FiscalYearId = fy.ID, PeriodNo = 8, StartDate = new DateTime(2026, 8, 1), EndDate = new DateTime(2026, 8, 31), Status = "Open" });
			}
			await db.SaveChangesAsync();

			Warehouse Wh(string code, int companyId) =>
				new() { Code = code, Name = code, NameEn = code, CompanyID = companyId, WarehouseType = "Main", AllowNegativeStock = false, IsActive = true };
			var own = Wh("ISO-WH-1", Co);
			var foreignWh = Wh("ISO-WH-2", Foreign);
			db.Warehouses.AddRange(own, foreignWh);
			await db.SaveChangesAsync();
			_whOwn = own.ID; _whForeign = foreignWh.ID;

			Item It(string code, int companyId, int catId) => new()
			{
				ItemCode = code, Barcode = "I" + code, Name = code, NameEn = code, CompanyID = companyId,
				ItemCategoryId = catId, ItemType = "Stockable", BaseUoMId = 1, IsActive = true,
			};
			var itemOwn = It("ISO-ITEM-1", Co, cat.ID);
			var itemForeign = It("ISO-ITEM-2", Foreign, catF.ID);
			db.Items.AddRange(itemOwn, itemForeign);
			await db.SaveChangesAsync();
			_itemOwn = itemOwn.ID; _itemForeign = itemForeign.ID;

			// A BOM for BOTH items. Without one, ManufService refuses a work order for a reason that has nothing
			// to do with tenancy ("this item has no bill of materials"), and a test that accepts that refusal
			// would be green while proving nothing about isolation. With a BOM on each side, the only remaining
			// reason to refuse a cross-tenant work order is the company guard itself.
			var compOwn = It("ISO-COMP-1", Co, cat.ID);
			var compForeign = It("ISO-COMP-2", Foreign, catF.ID);
			db.Items.AddRange(compOwn, compForeign);
			await db.SaveChangesAsync();
			db.ItemComponents.AddRange(
				new ItemComponent { CompanyID = Co, ParentItemId = _itemOwn, ComponentItemId = compOwn.ID, Quantity = 1m, ScrapPct = 0m, SortOrder = 1 },
				new ItemComponent { CompanyID = Foreign, ParentItemId = _itemForeign, ComponentItemId = compForeign.ID, Quantity = 1m, ScrapPct = 0m, SortOrder = 1 });
			await db.SaveChangesAsync();

			// Real stock on both sides, so a leak has something to show and a wrong refusal is distinguishable
			// from an empty database.
			await MustReceiveAsync(_itemOwn, _whOwn, 50m, 10m, Co);
			await MustReceiveAsync(_itemForeign, _whForeign, 70m, 20m, Foreign);
			await MustReceiveAsync(compOwn.ID, _whOwn, 40m, 5m, Co);
			await MustReceiveAsync(compForeign.ID, _whForeign, 40m, 5m, Foreign);
		}

		// ---- helpers -------------------------------------------------------------------------------------
		// The GRAPH is always built for a real company; only the companyId ARGUMENT varies. A company-0 graph is
		// not constructible (CompanyScopeHolder.Set refuses it), and varying only the argument is the sharper
		// test anyway: it catches a service that reads ambient scope instead of the company it was handed.
		private async Task<(bool ok, string? err)> ReceiveAsync(int itemId, int whId, decimal qty, decimal cost, int companyId)
		{
			await using var sp = Graph(companyId <= 0 ? Co : companyId);
			var (ok, err, _) = await sp.GetRequiredService<IStockService>().PostMovementAsync(companyId, new MovementRequest
			{
				Date = D, ItemId = itemId, WarehouseId = whId, Direction = 1, Qty = qty,
				UnitCostInBase = cost, SourceType = "Opening", PostToGl = true,
			}, "uat");
			return (ok, err);
		}

		private async Task MustReceiveAsync(int itemId, int whId, decimal qty, decimal cost, int companyId)
		{
			var (ok, err) = await ReceiveAsync(itemId, whId, qty, cost, companyId);
			Assert.True(ok, $"arrangement receipt failed (company {companyId}, item {itemId}, wh {whId}): {err}");
		}

		private async Task<(bool ok, string? err)> IssueAsync(int itemId, int whId, decimal qty, int companyId)
		{
			await using var sp = Graph(companyId <= 0 ? Co : companyId);
			var (ok, err, _) = await sp.GetRequiredService<IStockService>().PostMovementAsync(companyId, new MovementRequest
			{
				Date = D, ItemId = itemId, WarehouseId = whId, Direction = -1, Qty = qty,
				SourceType = "SalesInvoice", PostToGl = true,
			}, "uat");
			return (ok, err);
		}

		/// Counted with NO company predicate on purpose: a leak that wrote under the wrong company would be
		/// invisible to a query that already filters by the company it expects to see.
		private async Task<int> MovementCountAsync(int itemId)
		{
			using var db = _sql.ContextFor(_probe!, Co);
			return await db.StockMovements.AsNoTracking().CountAsync(m => m.ItemId == itemId);
		}

		// ===================================================================================================
		// AN UNRESOLVED COMPANY IS GUARDED TWICE, AND THE FIRST GUARD IS THE STRONGER ONE.
		//
		// The first attempt at this test tried to build a DbContext scoped to company 0 and could not: the platform
		// REFUSES to create one. CompanyScopeHolder.Set throws for any id <= 0 — "A company scope must be a real
		// company id. There is no default company." (CompanyScopeHolder.cs:89-93). So an unresolved request does not
		// get a company-0 scope, it gets NO scope, and `FilterCompanyId => CompanyId ?? 0` then makes every query
		// filter compare against 0. That refusal is asserted below rather than worked around, because it is the
		// reason a fallback to company 1 cannot be reintroduced by accident.
		//
		// The second guard is the one the converted controllers actually lean on: `co` is 0 when resolution fails
		// and 0 is handed to the service as its companyId argument. The tests below hand 0 to the services while
		// the ambient scope is a fully resolved REAL company — which is the harder case, because a service that
		// quietly read ambient state instead of its argument would leak precisely here and nowhere else.
		// ===================================================================================================
		[SkippableFact]
		public void The_platform_refuses_to_create_a_company_scope_for_an_unresolved_company()
		{
			Ready();
			var holder = new CompanyScopeHolder();
			var ex = Assert.Throws<ArgumentOutOfRangeException>(() => holder.Set(Unresolved, null));
			_out.WriteLine($"[iso] Set(0) refused: {ex.Message}");
			Assert.Throws<ArgumentOutOfRangeException>(() => holder.Set(-1, null));

			// Unset, an unresolved holder filters on 0 rather than on some default company.
			Assert.False(holder.IsResolved);
			Assert.Equal(0, holder.FilterCompanyId);
		}

		[SkippableFact]
		public async Task Unresolved_company_reads_no_stock_that_belongs_to_a_real_company()
		{
			Ready();
			// The ambient scope is company 1 — fully resolved. Only the ARGUMENT is unresolved, exactly as a
			// converted action produces it when IRequestCompanyResolver returns Ok = false.
			await using var sp = Graph(Co);
			var stock = sp.GetRequiredService<IStockService>();

			var leak = await stock.GetBalanceAsync(Unresolved, _itemOwn, _whOwn);
			var leakForeign = await stock.GetBalanceAsync(Unresolved, _itemForeign, _whForeign);
			_out.WriteLine($"[iso] companyId 0 reading company 1 stock: qty={leak.qty} value={leak.value}");
			_out.WriteLine($"[iso] companyId 0 reading company 2 stock: qty={leakForeign.qty} value={leakForeign.value}");

			Assert.Equal(0m, leak.qty);
			Assert.Equal(0m, leak.value);
			Assert.Equal(0m, leakForeign.qty);
			Assert.Equal(0m, leakForeign.value);

			// The stock really is there for its owner, so the zeros above are isolation, not an empty probe.
			var real = await stock.GetBalanceAsync(Co, _itemOwn, _whOwn);
			Assert.Equal(50m, real.qty);
		}

		[SkippableFact]
		public async Task Unresolved_company_cannot_write_a_movement_against_a_real_company_item()
		{
			Ready();
			var before = await MovementCountAsync(_itemOwn);

			var (issued, issueErr) = await IssueAsync(_itemOwn, _whOwn, 5m, Unresolved);
			var (received, receiveErr) = await ReceiveAsync(_itemOwn, _whOwn, 5m, 10m, Unresolved);
			_out.WriteLine($"[iso] companyId 0 issue refused={!issued} ({issueErr})");
			_out.WriteLine($"[iso] companyId 0 receipt refused={!received} ({receiveErr})");

			Assert.False(issued, "an unresolved company issued stock belonging to company 1");
			Assert.False(received, "an unresolved company received stock into company 1");

			// Refusing is only half of it. A refusal that still wrote a row would be worse than a leak, because
			// the balance and the movement log would then disagree with each other.
			var after = await MovementCountAsync(_itemOwn);
			_out.WriteLine($"[iso] movement rows for the company 1 item: before={before} after={after}");
			Assert.Equal(before, after);

			await using var owner = Graph(Co);
			var real = await owner.GetRequiredService<IStockService>().GetBalanceAsync(Co, _itemOwn, _whOwn);
			Assert.Equal(50m, real.qty);
		}

		// ===================================================================================================
		// THE RELATIONAL NEGATIVE — a real company naming another company row.
		//
		// This is the case a hardcoded company actually produced, and the case a single-table filter does not
		// catch. Both ids below exist and both are individually valid; only the RELATION between them crosses a
		// tenant boundary. Two directions are tested because they fail at different guards: a foreign ITEM is
		// caught by the item lookup, a foreign WAREHOUSE by the warehouse lookup, and a module that checked only
		// one of them would pass half of this test.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_company_cannot_move_stock_using_another_company_item_or_warehouse()
		{
			Ready();
			var beforeForeign = await MovementCountAsync(_itemForeign);

			// company 1 + item owned by company 2 + own warehouse
			var (foreignItem, e1) = await IssueAsync(_itemForeign, _whOwn, 1m, Co);
			// company 1 + own item + warehouse owned by company 2
			var (foreignWh, e2) = await IssueAsync(_itemOwn, _whForeign, 1m, Co);
			// company 1 naming BOTH of the company 2 rows — a fully foreign operation
			var (fullyForeign, e3) = await IssueAsync(_itemForeign, _whForeign, 1m, Co);

			_out.WriteLine($"[iso] co1 + foreign item      refused={!foreignItem} ({e1})");
			_out.WriteLine($"[iso] co1 + foreign warehouse refused={!foreignWh} ({e2})");
			_out.WriteLine($"[iso] co1 + both foreign      refused={!fullyForeign} ({e3})");

			Assert.False(foreignItem, "company 1 issued an item belonging to company 2");
			Assert.False(foreignWh, "company 1 issued into a warehouse belonging to company 2");
			Assert.False(fullyForeign, "company 1 performed a wholly company-2 movement");

			// Nothing was written on the far side, and the far side balance is intact.
			Assert.Equal(beforeForeign, await MovementCountAsync(_itemForeign));
			await using var foreignOwner = Graph(Foreign);
			var stillThere = await foreignOwner.GetRequiredService<IStockService>().GetBalanceAsync(Foreign, _itemForeign, _whForeign);
			_out.WriteLine($"[iso] company 2 balance after three cross-tenant attempts: qty={stillThere.qty}");
			Assert.Equal(70m, stillThere.qty);
		}

		// ===================================================================================================
		// THE READ DIRECTION of the same relation. A company asking for another company row must get nothing
		// rather than that row — this is what every converted report and lookup action now depends on.
		// ===================================================================================================
		[SkippableFact]
		public async Task A_company_reads_no_balance_for_another_company_item()
		{
			Ready();
			await using var sp = Graph(Co);
			var stock = sp.GetRequiredService<IStockService>();

			var acrossItem = await stock.GetBalanceAsync(Co, _itemForeign, _whForeign);
			var acrossWh = await stock.GetBalanceAsync(Co, _itemOwn, _whForeign);
			_out.WriteLine($"[iso] co1 reading co2 item+wh : qty={acrossItem.qty} value={acrossItem.value}");
			_out.WriteLine($"[iso] co1 reading own item in co2 wh: qty={acrossWh.qty} value={acrossWh.value}");

			Assert.Equal(0m, acrossItem.qty);
			Assert.Equal(0m, acrossItem.value);
			Assert.Equal(0m, acrossWh.qty);

			// The 70 units exist — for their owner only.
			await using var foreignOwner = Graph(Foreign);
			var owned = await foreignOwner.GetRequiredService<IStockService>().GetBalanceAsync(Foreign, _itemForeign, _whForeign);
			Assert.Equal(70m, owned.qty);
		}

	}
}
