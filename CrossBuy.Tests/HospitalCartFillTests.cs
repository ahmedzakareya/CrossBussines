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
	// HOSPITAL H138: the unit-dose cart fill. What must never happen:
	//   · a dose of "1 g" sent as one 500 mg tablet;
	//   · a dose outside the window counted, or one after the order stopped;
	//   · a medicine the ward's cabinet stocks filled again from the pharmacy;
	//   · an order with a delivery on its way filled twice;
	//   · more units promised than the store holds.
	// The deliveries themselves (FEFO issue, charge on the MAR, return) are the H21/H131 path's, proven there.
	// ==========================================================================================
	public class HospitalCartFillTests
	{
		private const int CompanyOne = 1;
		private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx() => new() { CompanyId = CompanyOne, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		[Theory]
		[InlineData("1 g", 500, "mg", 2, true)]
		[InlineData("500 mg", 500, "mg", 1, true)]
		[InlineData("250 mg", 500, "mg", 0.5, true)]
		[InlineData("300 mg", 500, "mg", 1, true)]          // 0.6 tablet: up to the next half
		[InlineData("100 mcg", 100, "mcg", 1, true)]
		[InlineData("500 ملغ", 500, "mg", 1, true)]         // written in Arabic
		[InlineData("10 mL", 500, "mg", 1, false)]          // a volume against a mass: one unit, to be checked
		[InlineData("two tablets", 500, "mg", 1, false)]
		public void A_dose_takes_the_units_its_strength_says(string dose, double strength, string unit, double units, bool exact)
			=> Assert.Equal(((decimal)units, exact), HospitalCartFillRules.UnitsPerDose(dose, (decimal)strength, unit));

		[Fact]
		public void Without_a_strength_a_dose_is_one_unit_to_check() => Assert.Equal((1m, false), HospitalCartFillRules.UnitsPerDose("1 g", null, null));

		[Fact]
		public void Only_the_doses_inside_the_window_count()
		{
			DateTime Id(DateTime t) => t;
			var start = new DateTime(2026, 11, 16, 12, 0, 0);
			int[] tid = { 6, 14, 22 };
			Assert.Equal(3, HospitalCartFillRules.DosesIn(tid, start.AddDays(-3), null, start, start.AddHours(24), Id, Id));                // 14, 22, then 06 tomorrow
			Assert.Equal(1, HospitalCartFillRules.DosesIn(tid, start.AddDays(-3), start.AddHours(8), start, start.AddHours(24), Id, Id));    // stopped at 20:00: only 14
			Assert.Equal(1, HospitalCartFillRules.DosesIn(tid, start.AddHours(16), null, start, start.AddHours(24), Id, Id));                 // started at 04:00 tomorrow: only 06
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates()
			=> HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h138_cart_fill.sql"), typeof(HospitalCartFill), "HospitalCartFills");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
			=> HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h138_cart_fill.sql"), typeof(HospitalCartFill), "HospitalCartFills");

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		[Fact]
		public async Task The_fill_list_says_for_every_order_what_it_will_do()
		{
			using var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var facility = new HospitalFacilityService(host.Db, access); var patients = new HospitalPatientService(host.Db, access); var admissions = new HospitalAdmissionService(host.Db, access);
			var ctx = Ctx();
			Assert.True((await facility.SeedDefaultRoomTypesAsync(ctx, 7)).Ok);
			var floor = (await facility.SaveFloorAsync(ctx, new HospitalFloorInput { Code = "F1", Name = "الأول" }, 7)).Id!.Value;
			var ward = (await facility.SaveWardAsync(ctx, new HospitalWardInput { FloorId = floor, Code = "W1", Name = "باطنة", SexPolicy = HospitalSexPolicy.Any }, 7)).Id!.Value;
			var type = await host.Db.HospitalRoomTypes.Where(t => t.Code == "private").Select(t => t.ID).SingleAsync();
			Assert.True((await facility.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, WardId = ward, RoomTypeId = type, Number = "101" }, 7)).Ok);
			var bed = await host.Db.HospitalBeds.Select(b => b.ID).SingleAsync();
			var pid = (await patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م اختبار", NameEn = "Test", Sex = HospitalPatientSex.Male }, 7, Now)).Id!.Value;
			var adm = (await admissions.AdmitAsync(ctx, new HospitalAdmitInput { PatientId = pid, BedId = bed }, 7, Now)).Id!.Value;

			// four medicines: one to fill (1 g of a 500 mg tablet), one the cabinet stocks, one short, one not stocked
			host.Seed.ItemCategories.Add(new ItemCategory { ID = 61, CompanyID = CompanyOne, Code = "MED", Name = "أدوية", NameEn = "Medicines" });
			foreach (var id in new[] { 71, 72, 73 })
				host.Seed.Items.Add(new Item { ID = id, CompanyID = CompanyOne, ItemCode = "M" + id, Barcode = "B" + id, Name = "دواء" + id, NameEn = "Drug" + id, ItemCategoryId = 61, BaseUoMId = 1, IsActive = true, CreatedAt = Now });
			host.Seed.Warehouses.Add(new Warehouse { ID = 5, CompanyID = CompanyOne, Code = "WH-PH", Name = "ص", NameEn = "Ph", IsActive = true });
			host.Seed.HospitalPharmacySettings.Add(new HospitalPharmacySettings { CompanyId = CompanyOne, WarehouseId = 5, NearExpiryDays = 60 });
			host.Seed.StockBalances.AddRange(new StockBalance { CompanyID = CompanyOne, ItemId = 71, WarehouseId = 5, QtyOnHand = 100, TotalValue = 10 },
				new StockBalance { CompanyID = CompanyOne, ItemId = 73, WarehouseId = 5, QtyOnHand = 1, TotalValue = 1 });
			host.Seed.Set<HospitalDrug>().Add(new HospitalDrug { CompanyId = CompanyOne, ItemId = 71, GenericName = "باراسيتامول", GenericNameEn = "paracetamol", Strength = 500, StrengthUnit = "mg", FormularyStatus = HospitalFormularyStatus.Formulary, UpdatedAt = Now });
			host.Seed.Set<HospitalSupplyUnit>().Add(new HospitalSupplyUnit { ID = 3, CompanyId = CompanyOne, Code = "U1", Name = "دولاب", NameEn = "Cabinet", WardId = ward, StoreWarehouseId = 5, SourceWarehouseId = 5, IsActive = true });
			host.Seed.Set<HospitalSupplyPar>().Add(new HospitalSupplyPar { CompanyId = CompanyOne, UnitId = 3, ItemId = 72, ParQty = 10 });
			HospitalMedOrder O(int? item, string drug, string dose) => new() { CompanyId = CompanyOne, AdmissionId = adm, PatientId = pid, Drug = drug, ItemId = item, Dose = dose, Route = HospitalMedRoute.Oral, Hours = "06,14,22", StartAt = Now.AddDays(-2), CreatedAt = Now };
			host.Seed.Set<HospitalMedOrder>().AddRange(O(71, "Paracetamol", "1 g"), O(72, "Cabinet drug", "1 tab"), O(73, "Short drug", "1 tab"), O(null, "Brought from home", "1 tab"));
			await host.Seed.SaveChangesAsync();

			var svc = new HospitalCartFillService(host.Db, access, new HospitalMedRoutingService(host.Db, access, null!, new HospitalBillingService(host.Db, access, new NoLedger())),
				new HospitalPharmacyService(host.Db, access, null!, new HospitalBillingService(host.Db, access, new NoLedger())));
			var start = HospitalNursingService.WardToUtc(HospitalNursingService.UtcToWard(Now).Date.AddDays(1));   // tomorrow at midnight, ward clock
			var p = (await svc.PreviewAsync(ctx, ward, start))!;
			var by = p.Lines.ToDictionary(l => l.Drug);
			Assert.Equal((HospitalCartLineState.Fill, 3, 2m, 6m), (by["Paracetamol"].State, by["Paracetamol"].Doses, by["Paracetamol"].UnitsPerDose, by["Paracetamol"].Units));
			Assert.Equal("paracetamol", by["Paracetamol"].DrugShown);
			Assert.Equal(HospitalCartLineState.Cabinet, by["Cabinet drug"].State);
			Assert.Equal(HospitalCartLineState.Short, by["Short drug"].State);           // 3 doses, the store holds 1
			Assert.Equal(HospitalCartLineState.NotStocked, by["Brought from home"].State);
			Assert.Equal((71, 6m, 100m), (p.Totals.Single().ItemId, p.Totals.Single().Units, p.Totals.Single().InStore));
			Assert.True(p.HasCabinet);

			// a delivery already on its way: not filled twice
			var orderId = await host.Db.Set<HospitalMedOrder>().Where(o => o.Drug == "Paracetamol").Select(o => o.ID).SingleAsync();
			host.Seed.Set<HospitalMedDelivery>().Add(new HospitalMedDelivery { CompanyId = CompanyOne, DeliveryNo = "MR-000001", MedOrderId = orderId, AdmissionId = adm, PatientId = pid, Quantity = 2, Stage = HospitalMedStage.Prepared, PrescribedAt = Now });
			await host.Seed.SaveChangesAsync();
			Assert.Equal(HospitalCartLineState.OnItsWay, (await svc.PreviewAsync(ctx, ward, start))!.Lines.Single(l => l.Drug == "Paracetamol").State);
			Assert.Null(await svc.PreviewAsync(new BusinessContext { CompanyId = 2, EmployeeId = 9, UserId = "u9", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() }, ward, start));
		}
	}
}
