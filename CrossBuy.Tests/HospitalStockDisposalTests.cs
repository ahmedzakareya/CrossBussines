using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H135: expiry and disposal. What must never happen:
	//   · a batch destroyed without a witness, or with the pharmacist witnessing themselves;
	//   · an expired batch returned (the purchase return would take another batch);
	//   · a return that takes a batch other than the one named; and if it does, a record left behind;
	//   · a recall left open after its batch is all gone;
	//   · another company's stock touched.
	// The stock writer and the payables are stand-ins that write the movement they would, inside the same transaction.
	// ==========================================================================================
	public class HospitalStockDisposalTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2, Pharmacy = 5, Quarantine = 9, Item = 71, Witness = 20;
		private static readonly DateTime Today = new(2026, 11, 15);
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int companyId = CompanyOne) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		// ---------- the rules ----------

		[Theory]
		[InlineData(-1, false, HospitalDisposalState.Expired)]
		[InlineData(30, false, HospitalDisposalState.Near)]
		[InlineData(200, false, null)]
		[InlineData(200, true, HospitalDisposalState.Quarantined)]   // in quarantine, whatever its expiry
		public void A_batch_is_on_the_list_when_expired_near_or_quarantined(int days, bool quarantine, string? state)
			=> Assert.Equal(state, HospitalDisposalRules.State(Today.AddDays(days), Today, 60, quarantine));

		[Fact]
		public void A_return_must_take_exactly_the_named_batch()
		{
			var store = new List<(string, DateTime?, decimal)> { ("B0", Today.AddDays(30), 5), ("B1", Today.AddDays(200), 10), ("BX", Today.AddDays(-5), 3) };
			Assert.Null(HospitalDisposalRules.ReturnTakesThisBatch(store, "B0", 5, Today));
			Assert.Equal("Another batch of this medicine would leave the store first — quarantine this batch and return it from there", HospitalDisposalRules.ReturnTakesThisBatch(store, "B1", 1, Today)?.Error);
			Assert.Equal("An expired batch is destroyed, not returned to the supplier", HospitalDisposalRules.ReturnTakesThisBatch(store, "BX", 1, Today)?.Error);
			Assert.Equal("The store does not hold that much of this batch", HospitalDisposalRules.ReturnTakesThisBatch(store, "B0", 6, Today)?.Error);
		}

		[Fact]
		public void A_destruction_needs_a_reason_a_method_and_another_persons_password()
		{
			var i = new HospitalDisposalInput { Qty = 1, Reason = HospitalDisposalReason.Expired, Method = HospitalDestructionMethod.Contractor, WitnessId = Witness, Password = "x" };
			Assert.Null(HospitalDisposalRules.ValidateDestroy(i, 7));
			Assert.Equal("The witness must be someone else", HospitalDisposalRules.ValidateDestroy(i, Witness)?.Error);
			i.Password = ""; Assert.Equal("The witness enters their password", HospitalDisposalRules.ValidateDestroy(i, 7)?.Error);
			i.Method = "Burial"; Assert.Equal("Choose how it is destroyed", HospitalDisposalRules.ValidateDestroy(i, 7)?.Error);
			Assert.Equal(("Expired", "Expired", "Damaged", "Other"), (HospitalDisposalRules.WriteOffReason("Expired"), HospitalDisposalRules.WriteOffReason("NearExpiry"),
				HospitalDisposalRules.WriteOffReason("Damaged"), HospitalDisposalRules.WriteOffReason("Recalled")));
		}

		// ---------- the table ----------

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates()
			=> HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h135_disposals.sql"), typeof(HospitalStockDisposal), "HospitalStockDisposals");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
			=> HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h135_disposals.sql"), typeof(HospitalStockDisposal), "HospitalStockDisposals");

		// ---------- the service ----------

		/// The stock writer's write-off: writes the batch's movement out, at 0.5 a unit, as the real one would.
		public class Stock : System.Reflection.DispatchProxy
		{
			public CrossDbContext Db = null!;
			public readonly List<(int Wh, string? Reason, decimal Qty, string? Batch)> WrittenOff = new();
			protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
			{
				if (method?.Name != nameof(IStockService.WriteOffAsync)) throw new NotSupportedException(method?.Name);
				var line = ((List<WriteOffLineInput>)args![5]!)[0]; int wh = (int)args[1]!;
				WrittenOff.Add((wh, line.Reason, line.Qty, line.BatchNo));
				var batch = Db.StockBatches.First(b => b.ItemId == line.ItemId && b.BatchNo == line.BatchNo);
				Db.StockMovements.Add(new StockMovement { CompanyID = CompanyOne, ItemId = line.ItemId, WarehouseId = wh, BatchId = batch.ID, Direction = -1, QtyBase = line.Qty, UnitCost = 0.5m, TotalCost = line.Qty * 0.5m, SourceType = "StockWriteOff", SourceId = 77, MovementDate = Now, CreatedAt = Now });
				Db.SaveChanges();
				return Task.FromResult<(bool, string?, string?, int?, string)>((true, null, "WOF-2026-00077", 77, "SeparateDocument"));
			}
		}

		/// The payables' purchase return: writes the movement of the batch it would pick (TakeBatch), and the debit note.
		public class Payables : System.Reflection.DispatchProxy
		{
			public CrossDbContext Db = null!;
			public string TakeBatch = "";
			public int Calls;
			protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
			{
				if (method?.Name != nameof(IPayableService.CreatePurchaseReturnAsync)) throw new NotSupportedException(method?.Name);
				Calls++;
				var line = ((List<PurchaseLineInput>)args![4]!)[0];
				var batch = Db.StockBatches.First(b => b.ItemId == line.ItemId && b.BatchNo == TakeBatch);
				Db.StockMovements.Add(new StockMovement { CompanyID = CompanyOne, ItemId = line.ItemId!.Value, WarehouseId = line.WarehouseId!.Value, BatchId = batch.ID, Direction = -1, QtyBase = line.Qty, UnitCost = 0.5m, TotalCost = line.Qty * 0.5m, SourceType = "PurchaseReturn", SourceId = 88, MovementDate = Now, CreatedAt = Now });
				Db.SaveChanges();
				return Task.FromResult<(bool, string?, PurchaseReturn?)>((true, null, new PurchaseReturn { ID = 88, ReturnNo = "DN-2026-00088", SubTotal = line.Qty * 0.5m }));
			}
		}

		private sealed class PasswordIsOk : IHospitalWitness
		{
			public Task<bool> VerifyAsync(int companyId, int employeeId, string password, CancellationToken ct = default) => Task.FromResult(password == "ok");
		}

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		private sealed class World { public required PlatformTestHost Host; public required HospitalStockDisposalService Svc; public required Stock Stock; public required Payables Payables; }

		/// The pharmacy store holds B0 (near expiry, 5), B1 (fine, 10) and BX (expired, 3); quarantine holds BQ (4) under recall.
		private static async Task<World> MakeAsync(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var stock = System.Reflection.DispatchProxy.Create<IStockService, Stock>(); ((Stock)(object)stock).Db = host.Db;
			var pay = System.Reflection.DispatchProxy.Create<IPayableService, Payables>(); ((Payables)(object)pay).Db = host.Db;
			var pharmacy = new HospitalPharmacyService(host.Db, access, stock, new HospitalBillingService(host.Db, access, new NoLedger()));
			host.Seed.ItemCategories.Add(new ItemCategory { ID = 61, CompanyID = CompanyOne, Code = "MED", Name = "أدوية", NameEn = "Medicines" });
			host.Seed.Items.Add(new Item { ID = Item, CompanyID = CompanyOne, ItemCode = "MED-X", Barcode = "BX", Name = "دواء", NameEn = "Drug", ItemCategoryId = 61, BaseUoMId = 1, IsActive = true, TrackExpiry = true, CreatedAt = Now });
			foreach (var (id, code) in new[] { (Pharmacy, "WH-PH"), (Quarantine, "WH-QR") })
				host.Seed.Warehouses.Add(new Warehouse { ID = id, CompanyID = CompanyOne, Code = code, Name = code, NameEn = code, IsActive = true });
			host.Seed.Vendors.Add(new Vendor { ID = 3, CompanyID = CompanyOne, Name = "مورد", NameEn = "Supplier" });
			host.Seed.HospitalPharmacySettings.Add(new HospitalPharmacySettings { CompanyId = CompanyOne, WarehouseId = Pharmacy, QuarantineWarehouseId = Quarantine, NearExpiryDays = 60 });
			var batches = new[] { (81, "B0", 30, Pharmacy, 5m), (82, "B1", 200, Pharmacy, 10m), (83, "BX", -5, Pharmacy, 3m), (84, "BQ", 300, Quarantine, 4m) };
			foreach (var (id, no, days, wh, qty) in batches)
			{
				host.Seed.StockBatches.Add(new StockBatch { ID = id, CompanyID = CompanyOne, ItemId = Item, BatchNo = no, ExpiryDate = Today.AddDays(days) });
				host.Seed.StockMovements.Add(new StockMovement { CompanyID = CompanyOne, ItemId = Item, WarehouseId = wh, BatchId = id, Direction = 1, QtyBase = qty, UnitCost = 0.5m, TotalCost = qty * 0.5m, SourceType = "Receipt", SourceId = 1, MovementDate = Now, CreatedAt = Now });
			}
			host.Seed.StockBalances.Add(new StockBalance { CompanyID = CompanyOne, ItemId = Item, WarehouseId = Pharmacy, QtyOnHand = 18, TotalValue = 9 });
			host.Seed.StockBalances.Add(new StockBalance { CompanyID = CompanyOne, ItemId = Item, WarehouseId = Quarantine, QtyOnHand = 4, TotalValue = 2 });
			host.Seed.Set<HospitalLotRecall>().Add(new HospitalLotRecall { CompanyId = CompanyOne, RecallNo = "RCL-000001", ItemId = Item, BatchNo = "BQ", Reason = "Particles", QuarantineWarehouseId = Quarantine, QuarantinedQty = 4, OpenedAt = Now });
			host.Seed.Employee.Add(new CrossBuy.Models.Context.Admin.Employee { ID = Witness, EmpCompanyID = CompanyOne, FullName = "شاهد", FullNameEn = "Witness", IsActive = true,
				FirstName = "W", LastName = "T", Address = "-", PhoneNumber = "-", Email = "w@c1.local", ProfileImage = "-", Gender = "-", MaritalStatus = "-", UserId = "u20" });
			await host.Seed.SaveChangesAsync();
			return new World { Host = host, Stock = (Stock)(object)stock, Payables = (Payables)(object)pay,
				Svc = new HospitalStockDisposalService(host.Db, access, stock, pay, new PasswordIsOk(), pharmacy) };
		}

		private static HospitalDisposalInput Destroy(string batch, int wh, decimal qty, string password = "ok") => new()
		{ ItemId = Item, BatchNo = batch, WarehouseId = wh, Qty = qty, Reason = HospitalDisposalReason.Recalled, Method = HospitalDestructionMethod.Contractor, WitnessId = Witness, Password = password };

		[Fact]
		public async Task The_list_holds_what_expired_what_is_near_and_what_is_in_quarantine()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			var c = await w.Svc.CandidatesAsync(Ctx(), Today);
			Assert.Equal(new[] { ("BX", HospitalDisposalState.Expired), ("BQ", HospitalDisposalState.Quarantined), ("B0", HospitalDisposalState.Near) }, c.Select(x => (x.BatchNo, x.State)).ToArray());
			Assert.Equal(("RCL-000001", 1.5m), (c[1].RecallNo, c[0].Value));   // 3 units at the store's 0.5
			Assert.Empty(await w.Svc.CandidatesAsync(Ctx(CompanyTwo), Today));
		}

		[Fact]
		public async Task Destroying_a_recalled_batch_writes_it_off_and_closes_its_recall()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			Assert.Equal("The witness's password is not correct", (await w.Svc.DestroyAsync(Ctx(), Destroy("BQ", Quarantine, 4, "wrong"), 7, "u7", Now)).Error);
			Assert.Equal("The store does not hold that much of this batch", (await w.Svc.DestroyAsync(Ctx(), Destroy("BQ", Quarantine, 5), 7, "u7", Now)).Error);
			Assert.Equal("not_found", (await w.Svc.DestroyAsync(Ctx(), Destroy("BQ", 3, 1), 7, "u7", Now)).Error);   // neither the pharmacy's store nor quarantine
			Assert.Empty(w.Stock.WrittenOff);

			var done = await w.Svc.DestroyAsync(Ctx(), Destroy("BQ", Quarantine, 4), 7, "u7", Now);
			Assert.True(done.Ok, done.Error);
			Assert.Equal((Quarantine, (string?)"Other", 4m, (string?)"BQ"), Assert.Single(w.Stock.WrittenOff));
			var d = await host.Db.Set<HospitalStockDisposal>().AsNoTracking().SingleAsync();
			Assert.Equal(("DSP-000001", HospitalDisposalKind.Destroyed, 2m, "WOF-2026-00077", (int?)Witness), (d.DisposalNo, d.Kind, d.Value, d.DocumentNo, d.WitnessEmployeeId));
			var r = await host.Db.Set<HospitalLotRecall>().AsNoTracking().SingleAsync();
			Assert.Equal((HospitalLotRecallStatus.Closed, d.RecallId), (r.Status, (int?)r.ID));
			Assert.Contains("DSP-000001", r.EndNote);
		}

		[Fact]
		public async Task A_return_takes_only_the_named_batch_or_nothing_happens()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			HospitalDisposalInput Ret(string batch, decimal qty) => new() { ItemId = Item, BatchNo = batch, WarehouseId = Pharmacy, Qty = qty, Reason = HospitalDisposalReason.NearExpiry, VendorId = 3 };
			// the category links no goods-received account: the stock leaving would book to its adjustment account
			Assert.Equal("Link the goods-received (GRNI) account of {0}'s inventory category first", (await w.Svc.ReturnAsync(Ctx(), Ret("B0", 1), 7, Now, Today)).Error);
			(await host.Seed.ItemCategories.SingleAsync(c => c.ID == 61)).GrniAccountId = 31;
			await host.Seed.SaveChangesAsync();
			Assert.Equal("An expired batch is destroyed, not returned to the supplier", (await w.Svc.ReturnAsync(Ctx(), Ret("BX", 1), 7, Now, Today)).Error);
			Assert.Equal("Another batch of this medicine would leave the store first — quarantine this batch and return it from there", (await w.Svc.ReturnAsync(Ctx(), Ret("B1", 1), 7, Now, Today)).Error);
			Assert.Equal(0, w.Payables.Calls);

			w.Payables.TakeBatch = "B1";   // had the purchase return picked another batch after all…
			Assert.Equal("Another batch of this medicine would leave the store first — quarantine this batch and return it from there", (await w.Svc.ReturnAsync(Ctx(), Ret("B0", 2), 7, Now, Today)).Error);
			Assert.False(await host.Db.Set<HospitalStockDisposal>().AnyAsync());                        // …nothing is recorded
			Assert.False(await host.Db.StockMovements.AnyAsync(m => m.SourceType == "PurchaseReturn"));   // and its movement is undone

			w.Payables.TakeBatch = "B0";
			var done = await w.Svc.ReturnAsync(Ctx(), Ret("B0", 2), 7, Now, Today);
			Assert.True(done.Ok, done.Error);
			var d = await host.Db.Set<HospitalStockDisposal>().AsNoTracking().SingleAsync();
			Assert.Equal((HospitalDisposalKind.Returned, 1m, "DN-2026-00088", (int?)3), (d.Kind, d.Value, d.DocumentNo, d.VendorId));
		}
	}
}
