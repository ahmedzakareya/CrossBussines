using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H122 — a dialysis session's consumables leave the unit's store. What must never happen: a kit drawn before
	// the session is finished, or twice; a kit drawn half (one component out, the next refused); a store or a kit the
	// unit never named; a component whose cost would post nowhere. The theatre (H37) draws through the same path.
	// ==========================================================================================
	public class HospitalDialysisKitTests
	{
		private const int CompanyOne = 1;
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

		private static BusinessContext Ctx(int employeeId = 7) => new()
		{
			CompanyId = CompanyOne, EmployeeId = employeeId, UserId = "user-" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		[Theory]
		[InlineData("Done", true, true, true, 2, "This session's kit has already left the store")]
		[InlineData("Running", false, true, true, 2, "The kit is issued when the session is finished")]
		[InlineData("Missed", false, true, true, 2, "The kit is issued when the session is finished")]
		[InlineData("Done", false, false, true, 2, "The dialysis unit has no kit set")]
		[InlineData("Done", false, true, false, 2, "Choose the dialysis unit's store first")]
		[InlineData("Done", false, true, true, 0, "The kit item has no components (Inventory › the item's BOM)")]
		[InlineData("Done", false, true, true, 2, null)]
		public void A_kit_leaves_only_a_finished_session_once(string status, bool issued, bool kit, bool store, int lines, string? why)
			=> Assert.Equal(why, HospitalDialysisKitRules.IssueRefusal(status, issued ? Now : null, kit, store, lines));

		[Fact]
		public void The_table_matches_its_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h122_unit_kits.sql");
			var m = HospitalSliceParity.Model(HospitalDialysisModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalUnitKit), "HospitalUnitKits");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalUnitKit), "HospitalUnitKits");
			Assert.Contains("ALTER TABLE dbo.HospitalDialysisSessions ADD KitIssuedAt datetime2 NULL", s);
			foreach (var u in HospitalKitUnits.All) Assert.Contains("'" + u + "'", s);
			Assert.Equal(new[] { "Items", "Warehouses" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
		}

		// ---- the issue itself, against a recording stock writer and a fixed bill of materials ----

		public class Stock : System.Reflection.DispatchProxy
		{
			public readonly List<MovementRequest> Posted = new();
			public int RefuseAt = int.MaxValue;
			protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
			{
				if (method?.Name != nameof(IStockService.PostMovementAsync)) throw new NotSupportedException(method?.Name);
				var r = (MovementRequest)args![1]!;
				if (Posted.Count + 1 >= RefuseAt) return Task.FromResult<(bool ok, string? error, StockMovement? movement)>((false, "short", null));
				Posted.Add(r);
				return Task.FromResult<(bool ok, string? error, StockMovement? movement)>((true, null, null));
			}
		}

		private sealed class Bom : IBomExplosionService
		{
			public Task<BomExplosionResult> ExplodeAsync(int companyId, int parentItemId, decimal qty, BomExplosionOptions? options = null, CancellationToken cancellationToken = default)
				=> Task.FromResult(new BomExplosionResult { Lines = parentItemId == 90 ? new[] { new BomLine { ComponentItemId = 91, Quantity = 1, Level = 1 }, new BomLine { ComponentItemId = 92, Quantity = 2, Level = 1 } } : Array.Empty<BomLine>() });
			public Task<BomRequirementsResult> BomRequirementsAsync(int companyId, IReadOnlyList<(int itemId, decimal qty)> demands, Dictionary<int, decimal> available, BomExplosionOptions? options = null, CancellationToken cancellationToken = default)
				=> throw new NotSupportedException();
		}

		private static async Task<(HospitalDialysisKitService Svc, Stock Stock, int SessionId)> MakeAsync(PlatformTestHost host, string status = HospitalDialysisSessionStatus.Done)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var stock = System.Reflection.DispatchProxy.Create<IStockService, Stock>();
			host.Seed.ItemCategories.Add(new ItemCategory { ID = 61, CompanyID = CompanyOne, Code = "DX", Name = "مستلزمات", NameEn = "Supplies", CogsAccountId = 501, InventoryAccountId = 101 });
			foreach (var (id, code, composite) in new[] { (90, "KIT-HD", true), (91, "DIALYSER", false), (92, "NEEDLE", false) })
				host.Seed.Items.Add(new Item { ID = id, CompanyID = CompanyOne, ItemCode = code, Barcode = "B" + id, Name = code, NameEn = code, ItemCategoryId = 61, BaseUoMId = 1, SalesPrice = 1m, IsActive = true, IsComposite = composite, CreatedAt = Now });
			host.Seed.ItemComponents.Add(new ItemComponent { CompanyID = CompanyOne, ParentItemId = 90, ComponentItemId = 91, Quantity = 1 });
			host.Seed.Warehouses.Add(new Warehouse { ID = 5, CompanyID = CompanyOne, Code = "WH-HD", Name = "مخزن الغسيل", NameEn = "Dialysis store", IsActive = true });
			foreach (var (item, qty) in new[] { (91, 10m), (92, 10m) }) host.Seed.StockBalances.Add(new StockBalance { CompanyID = CompanyOne, ItemId = item, WarehouseId = 5, QtyOnHand = qty });
			var s = new HospitalDialysisSession { CompanyId = CompanyOne, PlanId = 1, PatientId = 1, SessionDate = Now.Date, Shift = 1, Status = status, DryWeightKg = 70, PlannedMin = 240, CreatedAt = Now };
			host.Seed.Set<HospitalDialysisSession>().Add(s);
			await host.Seed.SaveChangesAsync();
			return (new HospitalDialysisKitService(host.Db, access, new Bom(), stock), (Stock)(object)stock, s.ID);
		}

		[Fact]
		public async Task A_finished_session_draws_its_kit_from_the_unit_store_once()
		{
			using var host = new PlatformTestHost();
			var (svc, stock, id) = await MakeAsync(host);
			Assert.Equal("The dialysis unit has no kit set", (await svc.IssueAsync(Ctx(), id, "u", Now)).Error);
			Assert.Equal("Choose a kit item that has a bill of materials", (await svc.SetAsync(Ctx(), 91, 5, 7, Now)).Error);   // a plain item is not a kit
			Assert.True((await svc.SetAsync(Ctx(), 90, null, 7, Now)).Ok);
			Assert.Equal("Choose the dialysis unit's store first", (await svc.IssueAsync(Ctx(), id, "u", Now)).Error);
			Assert.True((await svc.SetAsync(Ctx(), 90, 5, 7, Now)).Ok);

			var view = await svc.KitAsync(Ctx(), null);
			Assert.Equal((90, 5, 2), (view.KitItemId!.Value, view.StoreWarehouseId!.Value, view.Lines.Count));
			Assert.True((await svc.IssueAsync(Ctx(), id, "u", Now)).Ok);
			Assert.Equal(new[] { (91, 1m), (92, 2m) }, stock.Posted.Select(p => (p.ItemId, p.Qty)));
			Assert.All(stock.Posted, p => Assert.Equal((5, -1, HospitalDialysisKitRules.MovementSource, id, 501), (p.WarehouseId, (int)p.Direction, p.SourceType, p.SourceId, p.CounterAccountOverride!.Value)));
			Assert.Equal(Now, (await host.Db.Set<HospitalDialysisSession>().AsNoTracking().SingleAsync()).KitIssuedAt);

			Assert.Equal("This session's kit has already left the store", (await svc.IssueAsync(Ctx(), id, "u", Now)).Error);
			Assert.Equal(2, stock.Posted.Count);   // never twice
		}

		[Fact]
		public async Task A_short_store_leaves_the_kit_whole_in_the_store_and_the_session_not_issued()
		{
			using var host = new PlatformTestHost();
			var (svc, stock, id) = await MakeAsync(host);
			Assert.True((await svc.SetAsync(Ctx(), 90, 5, 7, Now)).Ok);
			stock.RefuseAt = 2;   // the dialyser leaves, the stock writer refuses the needles (a batch, an expiry…)
			var r = await svc.IssueAsync(Ctx(), id, "u", Now);
			Assert.Equal("{0}: {1}", r.Error);
			Assert.Null((await host.Db.Set<HospitalDialysisSession>().AsNoTracking().SingleAsync()).KitIssuedAt);   // still waiting: issued again once stocked
			stock.RefuseAt = int.MaxValue; stock.Posted.Clear();
			Assert.True((await svc.IssueAsync(Ctx(), id, "u", Now)).Ok);
		}

		/// Found live: the stock writer's refusal is English by design, so the shortfall is named first, in the reader's language.
		[Fact]
		public async Task A_store_short_of_a_component_says_which_and_how_many_before_anything_moves()
		{
			using var host = new PlatformTestHost();
			var (svc, stock, id) = await MakeAsync(host);
			Assert.True((await svc.SetAsync(Ctx(), 90, 5, 7, Now)).Ok);
			var needles = await host.Seed.StockBalances.SingleAsync(b => b.ItemId == 92);
			needles.QtyOnHand = 1; await host.Seed.SaveChangesAsync();
			var r = await svc.IssueAsync(Ctx(), id, "u", Now);
			Assert.Equal("Not enough {0} in the store: {1} of {2}", r.Error);
			Assert.Equal(new object[] { "NEEDLE", "1", "2" }, r.Args);
			Assert.Empty(stock.Posted);
			Assert.Null((await host.Db.Set<HospitalDialysisSession>().AsNoTracking().SingleAsync()).KitIssuedAt);
		}

		[Fact]
		public async Task A_running_session_draws_nothing()
		{
			using var host = new PlatformTestHost();
			var (svc, stock, id) = await MakeAsync(host, HospitalDialysisSessionStatus.Running);
			Assert.True((await svc.SetAsync(Ctx(), 90, 5, 7, Now)).Ok);
			Assert.Equal("The kit is issued when the session is finished", (await svc.IssueAsync(Ctx(), id, "u", Now)).Error);
			Assert.Empty(stock.Posted);
		}
	}
}
