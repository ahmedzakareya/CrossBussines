using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H145: partial dispensing. What must never happen:
	//   · more leaving the store than the prescription still owes, or a negative quantity;
	//   · a short dispense marked "dispensed" (the patient would never get the rest);
	//   · the patient charged for what did not leave;
	//   · a second dispense giving again what was already given;
	//   · a dispense with nothing in it.
	// The stock writer is a recorder that writes each movement as a row, so the service reads back what really left.
	// ==========================================================================================
	public class HospitalPartialDispenseTests
	{
		private static readonly DateTime Today = new(2026, 11, 15);
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int employeeId = 7, int companyId = 1) => new() { CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		public class Movements : System.Reflection.DispatchProxy
		{
			public CrossDbContext Db = null!;
			public readonly List<MovementRequest> Asked = new();
			protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
			{
				if (method?.Name != nameof(IStockService.PostMovementAsync)) throw new NotSupportedException(method?.Name);
				var r = (MovementRequest)args![1]!; Asked.Add(r);
				var m = new StockMovement { CompanyID = (int)args[0]!, MovementDate = r.Date, ItemId = r.ItemId, WarehouseId = r.WarehouseId, Direction = r.Direction, QtyBase = r.Qty,
					UnitCost = 2m, TotalCost = 2m * r.Qty, SourceType = r.SourceType, SourceId = r.SourceId, SourceLineId = r.SourceLineId, CreatedAt = r.Date };
				Db.StockMovements.Add(m); Db.SaveChanges();
				return Task.FromResult<(bool ok, string? error, StockMovement? movement)>((true, null, m));
			}
		}

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		private sealed record World(PlatformTestHost Host, HospitalPharmacyService Pharmacy, Movements Stock, HospitalBillingService Billing);

		/// A verified prescription for 10 paracetamol and 4 amoxicillin, the pharmacy store set. Returns the world and the dispense.
		private static async Task<(World W, HospitalDispense Rx, int Para, int Amox)> Prescribed()
		{
			var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var billing = new HospitalBillingService(host.Db, access, new NoLedger());
			var stock = System.Reflection.DispatchProxy.Create<IStockService, Movements>();
			((Movements)(object)stock).Db = host.Db;
			var w = new World(host, new HospitalPharmacyService(host.Db, access, stock, billing), (Movements)(object)stock, billing);
			var ctx = Ctx();

			host.Seed.ItemCategories.Add(new ItemCategory { ID = 61, CompanyID = 1, Code = "MED", Name = "أدوية", NameEn = "Medicines", CogsAccountId = 501, InventoryAccountId = 101 });
			foreach (var (id, code) in new[] { (71, "MED-PARA"), (72, "MED-AMOX") })
				host.Seed.Items.Add(new Item { ID = id, CompanyID = 1, ItemCode = code, Barcode = "B" + id, Name = code, NameEn = code, ItemCategoryId = 61, BaseUoMId = 1, SalesPrice = 3m, IsActive = true, CreatedAt = Now });
			host.Seed.HospitalItemCategories.Add(new HospitalItemCategory { CompanyId = 1, ItemCategoryId = 61, Kind = HospitalPriceGroups.Medicine });
			host.Seed.Warehouses.Add(new Warehouse { ID = 5, CompanyID = 1, Code = "WH-PH", Name = "صيدلية", NameEn = "Pharmacy", IsActive = true });
			await host.Seed.SaveChangesAsync();
			Assert.True((await w.Pharmacy.SaveSettingsAsync(ctx, 5, 60)).Ok);

			var consult = new HospitalConsultationService(host.Db, access, billing); var op = new HospitalOutpatientService(host.Db, access); var patients = new HospitalPatientService(host.Db, access);
			Assert.True((await consult.SeedDefaultCodesAsync(ctx)).Ok);
			var fee = (await billing.SaveServiceAsync(ctx, new HospitalServiceInput { Code = "CONS-IM", Name = "ك", NameEn = "C", Category = HospitalServiceCategories.Consultation, Price = 10m }, 7)).Id!.Value;
			var doc = (await op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د", NameEn = "Dr", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600, ConsultationServiceId = fee }, 7)).Id!.Value;
			var pat = (await patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M" }, 7, Now)).Id!.Value;
			var appt = (await op.BookAsync(ctx, new HospitalBookingInput { PatientId = pat, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			Assert.True((await op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var enc = (await consult.OpenAsync(ctx, appt, 7, Now)).Id!.Value;
			Assert.True((await consult.SignAsync(ctx, enc, new HospitalEncounterInput { ChiefComplaint = "Fever", Diagnoses = new() { new() { Code = "J02.9", Primary = true } },
				Prescription = new() { new() { Drug = "MED-PARA", ItemId = 71, Quantity = 10 }, new() { Drug = "MED-AMOX", ItemId = 72, Quantity = 4 } } }, 7, Now)).Ok);
			await w.Pharmacy.SyncAsync(ctx);
			var rx = await host.Db.HospitalDispenses.AsNoTracking().SingleAsync();
			Assert.True((await w.Pharmacy.VerifyAsync(ctx, rx.ID, null, 7, Now)).Ok);
			return (w, rx, 71, 72);
		}

		private static async Task<Dictionary<int, HospitalDispenseLine>> Lines(World w, int rx)
			=> await w.Host.Db.HospitalDispenseLines.AsNoTracking().Where(l => l.DispenseId == rx).ToDictionaryAsync(l => l.ItemId!.Value);

		[Fact]
		public void The_plan_never_gives_more_than_is_owed_nor_nothing_at_all()
		{
			var a = new HospitalDispenseLine { ID = 1, ItemId = 71, Quantity = 10, DispensedQty = 6 };
			var b = new HospitalDispenseLine { ID = 2, ItemId = 72, Quantity = 4 };
			Assert.Equal(4m, HospitalPartialDispenseRules.Owed(a));
			var (all, _) = HospitalPartialDispenseRules.Plan(new[] { a, b }, null);
			Assert.Equal((4m, 4m), (all![1], all[2]));                                         // nothing asked: all that is owed
			Assert.Equal("{0}: more than the prescription still owes ({1})", HospitalPartialDispenseRules.Plan(new[] { a }, new Dictionary<int, decimal> { [1] = 5 }).Error);
			Assert.Equal("A quantity cannot be negative", HospitalPartialDispenseRules.Plan(new[] { a }, new Dictionary<int, decimal> { [1] = -1 }).Error);
			Assert.Equal("Nothing to dispense: enter a quantity", HospitalPartialDispenseRules.Plan(new[] { a, b }, new Dictionary<int, decimal> { [1] = 0, [2] = 0 }).Error);
			Assert.Equal(HospitalDispenseStatus.Partial, HospitalPartialDispenseRules.StatusAfter(new[] { a, b }));
			Assert.Equal(HospitalDispenseStatus.Dispensed, HospitalPartialDispenseRules.StatusAfter(new[] { new HospitalDispenseLine { Quantity = 3, DispensedQty = 3 } }));
		}

		[Fact]
		public async Task A_short_store_gives_what_there_is_charges_only_that_and_the_rest_is_owed_until_given()
		{
			var (w, rx, para, amox) = await Prescribed();
			using var _ = w.Host;
			var lines = await Lines(w, rx.ID);

			// the store is short: 6 of the 10 paracetamol now, the amoxicillin in full
			var first = await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now, default, new Dictionary<int, decimal> { [lines[para].ID] = 6 });
			Assert.True(first.Ok, first.Error);
			var d = await w.Host.Db.HospitalDispenses.AsNoTracking().SingleAsync();
			Assert.Equal(HospitalDispenseStatus.Partial, d.Status);
			lines = await Lines(w, rx.ID);
			Assert.Equal((6m, 4m), (lines[para].DispensedQty!.Value, lines[amox].DispensedQty!.Value));
			Assert.Equal(new[] { 6m, 4m }, w.Stock.Asked.Select(a => a.Qty).ToArray());
			var billed = await w.Host.Db.HospitalBillLines.AsNoTracking().Where(l => l.BillId == d.BillId && l.Source == HospitalBillLineSources.Pharmacy).Select(l => l.Quantity).ToListAsync();
			Assert.Equal(new[] { 4m, 6m }, billed.OrderBy(x => x).ToArray());   // only what left is on the bill

			// a second dispense gives only what is owed, never again what was given
			Assert.Equal("{0}: more than the prescription still owes ({1})", (await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now, default, new Dictionary<int, decimal> { [lines[para].ID] = 5 })).Error);
			var second = await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now.AddDays(1));   // nothing asked: all that is owed
			Assert.True(second.Ok, second.Error);
			Assert.Equal(4m, w.Stock.Asked[^1].Qty);
			Assert.Equal(3, w.Stock.Asked.Count);   // the amoxicillin, already given, did not leave again
			d = await w.Host.Db.HospitalDispenses.AsNoTracking().SingleAsync();
			Assert.Equal(HospitalDispenseStatus.Dispensed, d.Status);
			Assert.Equal(10m, (await Lines(w, rx.ID))[para].DispensedQty);

			var fills = await w.Host.Db.Set<HospitalDispenseFill>().AsNoTracking().OrderBy(f => f.ID).ToListAsync();
			Assert.Equal(new[] { 6m, 4m, 4m }, fills.Select(f => f.Quantity).ToArray());
			Assert.All(fills, f => Assert.NotNull(f.BillLineId));
			Assert.Equal(new[] { 6m, 4m, 4m }, (await w.Host.Db.HospitalBillLines.AsNoTracking().Where(l => fills.Select(f => f.BillLineId).Contains(l.ID)).OrderBy(l => l.ID).Select(l => l.Quantity).ToListAsync()).ToArray());
			Assert.Equal("This prescription was already dispensed", (await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Error);
		}

		[Fact]
		public async Task Nothing_asked_means_nothing_leaves_and_the_prescription_stays_as_it_was()
		{
			var (w, rx, para, amox) = await Prescribed();
			using var _ = w.Host;
			var lines = await Lines(w, rx.ID);
			Assert.Equal("Nothing to dispense: enter a quantity", (await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now, default,
				new Dictionary<int, decimal> { [lines[para].ID] = 0, [lines[amox].ID] = 0 })).Error);
			Assert.Empty(w.Stock.Asked);
			Assert.Equal(HospitalDispenseStatus.Verified, (await w.Host.Db.HospitalDispenses.AsNoTracking().SingleAsync()).Status);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates()
			=> HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h145_partial_dispense.sql"), typeof(HospitalDispenseFill), "HospitalDispenseFills");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
			=> HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h145_partial_dispense.sql"), typeof(HospitalDispenseFill), "HospitalDispenseFills");
	}
}
