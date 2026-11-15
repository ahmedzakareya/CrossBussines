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
	// HOSPITAL H134: batch recalls. What must never happen:
	//   · a recalled batch left within reach in any store (the ward's included);
	//   · a recall opened with no quarantine store;
	//   · the same batch recalled twice at once;
	//   · a patient whose units all came back still listed, or one who received the batch missed;
	//   · another company's batch touched.
	// The transfers themselves are the stock writer's (proven in the inventory's own tests): here a stand-in records the
	// moves it was asked for.
	// ==========================================================================================
	public class HospitalLotRecallTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2, Pharmacy = 5, Ward = 6, Quarantine = 9, Item = 71;
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int companyId = CompanyOne) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		// ---------- the rules ----------

		[Theory]
		[InlineData("batch", "Choose the batch")]
		[InlineData("class", "Choose the recall class")]
		[InlineData("source", "Choose who issued the recall")]
		[InlineData("reason", "Write why the batch is recalled")]
		public void An_unsound_recall_is_refused(string what, string error)
		{
			var i = new HospitalLotRecallInput { ItemId = 1, BatchNo = "B1", RecallClass = "I", Source = HospitalLotRecallSource.Regulator, Reason = "Particles in vials" };
			Assert.Null(HospitalLotRecallRules.Validate(i));
			switch (what) { case "batch": i.BatchNo = " "; break; case "class": i.RecallClass = "IV"; break; case "source": i.Source = "Press"; break; case "reason": i.Reason = " "; break; }
			Assert.Equal(error, HospitalLotRecallRules.Validate(i)?.Error);
		}

		[Fact]
		public void A_patient_counts_what_reached_them_less_what_came_back()
		{
			var t0 = Now;
			var reached = HospitalLotRecallRules.Reached(new[]
			{
				new HospitalLotRecallIssue(1, -1, 2, t0, HospitalLotRecallWays.Dispensed),
				new HospitalLotRecallIssue(2, -1, 1, t0.AddHours(1), HospitalLotRecallWays.Delivered),
				new HospitalLotRecallIssue(2, +1, 1, t0.AddHours(2), HospitalLotRecallWays.Delivered),   // the dose came back: patient 2 never had it
				new HospitalLotRecallIssue(3, -1, 1, t0.AddHours(3), HospitalLotRecallWays.Delivered),
				new HospitalLotRecallIssue(3, -1, 1, t0.AddHours(4), HospitalLotRecallWays.Kit),
			});
			Assert.Equal(new[] { 3, 1 }, reached.Select(r => r.PatientId).ToArray());   // most recent first
			Assert.Equal((2m, t0.AddHours(4)), (reached[0].Qty, reached[0].LastAt));
			Assert.Equal(new[] { HospitalLotRecallWays.Delivered, HospitalLotRecallWays.Kit }, reached[0].Ways.ToArray());
		}

		// ---------- the tables ----------

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates()
			=> HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h134_recalls.sql"), typeof(HospitalLotRecall), "HospitalLotRecalls");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
			=> HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h134_recalls.sql"), typeof(HospitalLotRecall), "HospitalLotRecalls");

		[Fact]
		public void The_settings_quarantine_store_is_added_by_its_slice()
		{
			var sql = HospitalSliceParity.Slice("hospital_h134_recalls.sql");
			Assert.Contains("ALTER TABLE dbo.HospitalPharmacySettings ADD QuarantineWarehouseId int NULL", sql);
			Assert.NotNull(Model().FindEntityType(typeof(HospitalPharmacySettings))!.FindProperty(nameof(HospitalPharmacySettings.QuarantineWarehouseId)));
		}

		// ---------- the service ----------

		/// Records the transfers asked of the stock writer and answers them as done; any other stock call is a failure.
		public class Transfers : System.Reflection.DispatchProxy
		{
			public readonly List<(int From, int To, decimal Qty, string? Batch)> Asked = new();
			public string? Refuse;
			protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
			{
				if (method?.Name != nameof(IStockService.TransferAsync)) throw new NotSupportedException(method?.Name);
				var line = ((List<TransferLineInput>)args![5]!)[0];
				if (Refuse != null) return Task.FromResult<(bool, string?, StockTransfer?)>((false, Refuse, null));
				Asked.Add(((int)args[1]!, (int)args[2]!, line.Qty, line.BatchNo));
				return Task.FromResult<(bool, string?, StockTransfer?)>((true, null, new StockTransfer { TransferNo = "TRF-" + Asked.Count }));
			}
		}

		private sealed class World
		{
			public required PlatformTestHost Host;
			public required HospitalLotRecallService Recalls;
			public required HospitalPharmacyService Pharmacy;
			public required Transfers Stock;
			public required int PatientA, PatientB;
		}

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		/// A medicine whose batch B1 came in (15 to the pharmacy, 3 to a ward), two units dispensed to patient A, one dose
		/// delivered to patient B and returned (so B is not reached).
		private static async Task<World> MakeAsync(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var billing = new HospitalBillingService(host.Db, access, new NoLedger());
			var stock = System.Reflection.DispatchProxy.Create<IStockService, Transfers>();
			var pharmacy = new HospitalPharmacyService(host.Db, access, stock, billing);
			var patients = new HospitalPatientService(host.Db, access);
			int a = (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م أ", NameEn = "P A", Sex = "F", Phone = "01000000001" }, 7, Now)).Id!.Value;
			int b = (await patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م ب", NameEn = "P B", Sex = "M" }, 7, Now)).Id!.Value;

			host.Seed.ItemCategories.Add(new ItemCategory { ID = 61, CompanyID = CompanyOne, Code = "MED", Name = "أدوية", NameEn = "Medicines" });
			host.Seed.Items.Add(new Item { ID = Item, CompanyID = CompanyOne, ItemCode = "MED-X", Barcode = "BX", Name = "دواء", NameEn = "Drug", ItemCategoryId = 61, BaseUoMId = 1, IsActive = true, TrackExpiry = true, CreatedAt = Now });
			foreach (var (id, code) in new[] { (Pharmacy, "WH-PH"), (Ward, "WH-W1"), (Quarantine, "WH-QR") })
				host.Seed.Warehouses.Add(new Warehouse { ID = id, CompanyID = CompanyOne, Code = code, Name = code, NameEn = code, IsActive = true });
			host.Seed.StockBatches.Add(new StockBatch { ID = 81, CompanyID = CompanyOne, ItemId = Item, BatchNo = "B1", ExpiryDate = Now.AddDays(200) });
			host.Seed.HospitalDispenses.Add(new HospitalDispense { ID = 30, CompanyId = CompanyOne, RxNo = "RX-000030", PatientId = a, PrescribedAt = Now });
			host.Seed.Set<HospitalMedDelivery>().Add(new HospitalMedDelivery { ID = 40, CompanyId = CompanyOne, DeliveryNo = "MR-000040", PatientId = b, PrescribedAt = Now });
			StockMovement Mv(int wh, short dir, decimal q, string src, int? sid) => new() { CompanyID = CompanyOne, ItemId = Item, WarehouseId = wh, BatchId = 81, Direction = dir, QtyBase = q, SourceType = src, SourceId = sid, MovementDate = Now, CreatedAt = Now };
			host.Seed.StockMovements.AddRange(
				Mv(Pharmacy, 1, 15, "Receipt", 1), Mv(Ward, 1, 3, "TransferIn", 2),
				Mv(Pharmacy, -1, 2, HospitalPharmacyService.MovementSource, 30),
				Mv(Pharmacy, -1, 1, HospitalMedRoutingService.MovementSource, 40), Mv(Pharmacy, 1, 1, HospitalMedRoutingService.ReturnSource, 40));
			await host.Seed.SaveChangesAsync();
			return new World { Host = host, Pharmacy = pharmacy, Recalls = new HospitalLotRecallService(host.Db, access, stock, pharmacy), Stock = (Transfers)(object)stock, PatientA = a, PatientB = b };
		}

		private static HospitalLotRecallInput B1() => new() { ItemId = Item, BatchNo = "B1", RecallClass = HospitalLotRecallClass.I, Source = HospitalLotRecallSource.Regulator, Reference = "EDA 12/2026", Reason = "Particles in vials" };

		[Fact]
		public async Task Opening_a_recall_moves_the_batch_from_every_store_into_quarantine()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			Assert.Equal("Choose the quarantine store in the pharmacy settings first", (await w.Recalls.OpenAsync(Ctx(), B1(), 7, "u7", Now)).Error);
			Assert.Equal("The quarantine store must be another store than the pharmacy's", (await w.Pharmacy.SaveSettingsAsync(Ctx(), Pharmacy, 60, default, Pharmacy)).Error);
			Assert.True((await w.Pharmacy.SaveSettingsAsync(Ctx(), Pharmacy, 60, default, Quarantine)).Ok);

			var opened = await w.Recalls.OpenAsync(Ctx(), B1(), 7, "u7", Now);
			Assert.True(opened.Ok, opened.Error);
			Assert.Equal(new[] { (Pharmacy, Quarantine, 13m, (string?)"B1"), (Ward, Quarantine, 3m, (string?)"B1") }, w.Stock.Asked.OrderBy(x => x.From).ToArray());
			var r = await host.Db.Set<HospitalLotRecall>().AsNoTracking().SingleAsync();
			Assert.Equal(("RCL-000001", HospitalLotRecallStatus.Quarantined, 16m), (r.RecallNo, r.Status, r.QuarantinedQty));
			Assert.Equal(new[] { "TRF-1", "TRF-2" }, r.Transfers!.Split(',').OrderBy(x => x).ToArray());

			Assert.Equal("This batch is already under recall {0}", (await w.Recalls.OpenAsync(Ctx(), B1(), 7, "u7", Now)).Error);
			Assert.Equal("not_found", (await w.Recalls.OpenAsync(Ctx(CompanyTwo), B1(), 7, "u7", Now)).Error);   // another company knows no such batch

			var v = (await w.Recalls.ViewAsync(Ctx(), r.ID))!;
			var reached = Assert.Single(v.Patients);   // B's dose came back
			Assert.Equal((w.PatientA, 2m, "01000000001"), (reached.PatientId, reached.Qty, reached.Phone));
			Assert.Null(await w.Recalls.ViewAsync(Ctx(CompanyTwo), r.ID));
		}

		[Fact]
		public async Task A_store_that_refuses_its_move_undoes_the_whole_recall()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			Assert.True((await w.Pharmacy.SaveSettingsAsync(Ctx(), Pharmacy, 60, default, Quarantine)).Ok);
			w.Stock.Refuse = "not enough stock";
			Assert.Equal("The batch could not be moved into quarantine: {0}", (await w.Recalls.OpenAsync(Ctx(), B1(), 7, "u7", Now)).Error);
			Assert.False(await host.Db.Set<HospitalLotRecall>().AnyAsync());
		}

		[Fact]
		public async Task A_recall_ends_once_released_or_closed_and_each_end_needs_its_reason()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			Assert.True((await w.Pharmacy.SaveSettingsAsync(Ctx(), Pharmacy, 60, default, Quarantine)).Ok);
			int id = (await w.Recalls.OpenAsync(Ctx(), B1(), 7, "u7", Now)).Id!.Value;
			// what the real transfers write: the batch leaves both stores for quarantine
			StockMovement Mv(int wh, short dir, decimal q) => new() { CompanyID = CompanyOne, ItemId = Item, WarehouseId = wh, BatchId = 81, Direction = dir, QtyBase = q, SourceType = "TransferOut", SourceId = 99, MovementDate = Now, CreatedAt = Now };
			host.Seed.StockMovements.AddRange(Mv(Pharmacy, -1, 13), Mv(Ward, -1, 3), Mv(Quarantine, 1, 16));
			await host.Seed.SaveChangesAsync();
			Assert.Equal("Nothing of this batch is left outside quarantine", (await w.Recalls.SweepAsync(Ctx(), id, "u7", Now)).Error);
			host.Seed.StockMovements.Add(Mv(Ward, 1, 1));   // a dose of the batch reaches the ward later
			await host.Seed.SaveChangesAsync();
			Assert.True((await w.Recalls.SweepAsync(Ctx(), id, "u7", Now)).Ok);
			Assert.Equal((Ward, Quarantine, 1m, (string?)"B1"), w.Stock.Asked.Last());
			Assert.Equal(17m, (await host.Db.Set<HospitalLotRecall>().AsNoTracking().SingleAsync()).QuarantinedQty);

			Assert.Equal("Write why the recall ends", (await w.Recalls.CloseAsync(Ctx(), id, " ", 7, Now)).Error);
			Assert.True((await w.Recalls.CloseAsync(Ctx(), id, "Returned to the supplier, credit note 55", 7, Now)).Ok);
			Assert.Equal("This recall has ended", (await w.Recalls.ReleaseAsync(Ctx(), id, "withdrawn", 7, "u7", Now)).Error);
			Assert.Equal("This recall has ended", (await w.Recalls.SweepAsync(Ctx(), id, "u7", Now)).Error);
		}
	}
}
