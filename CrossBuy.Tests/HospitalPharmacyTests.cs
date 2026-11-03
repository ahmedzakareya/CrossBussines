using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H10 — the pharmacy (the rules before any stock moves; the stock itself is proven against a
	// real SQL Server in SqlServer/HospitalPharmacySqlTests).
	//
	// What must never happen: a signed prescription that never reaches the pharmacy (or reaches it twice),
	// a prescription line naming an item the pharmacy does not dispense, a dispense without the pharmacist's
	// review or without a pharmacy store, an item whose cost would post nowhere, someone without the
	// pharmacy role dispensing, an allergy on the file left unflagged — or another company's prescription.
	// ==========================================================================================
	public class HospitalPharmacyTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		private static readonly DateTime Today = new(2026, 11, 15);
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

		private static BusinessContext Ctx(int employeeId = 7, int companyId = CompanyOne) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
			Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		private sealed class NoLedger : IHospitalLedger
		{
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
				=> Task.FromResult<(bool, string?, int?)>((true, null, 1));
		}

		/// The rules under test stop before the stock; reaching it would be a failure of the rule. Every other
		/// member of the large stock interface throws.
		public class NoStock : System.Reflection.DispatchProxy
		{
			public int Calls;
			protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
			{
				if (method?.Name != nameof(IStockService.PostMovementAsync)) throw new NotSupportedException(method?.Name);
				Calls++;
				return Task.FromResult<(bool ok, string? error, StockMovement? movement)>((false, "stock not reached in these tests", null));
			}
		}

		private sealed class World
		{
			public required PlatformTestHost Host;
			public required HospitalPharmacyService Pharmacy;
			public required NoStock Stock;
			public required HospitalBillingService Billing;
			public required HospitalConsultationService Consult;
			public required HospitalOutpatientService Op;
			public required HospitalPatientService Patients;
		}

		private static World Make(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var billing = new HospitalBillingService(host.Db, access, new NoLedger());
			var stock = System.Reflection.DispatchProxy.Create<IStockService, NoStock>();
			return new World { Host = host, Pharmacy = new(host.Db, access, stock, billing), Stock = (NoStock)(object)stock, Billing = billing, Consult = new(host.Db, access, billing),
				Op = new(host.Db, access), Patients = new(host.Db, access) };
		}

		/// Two medicines in a mapped category (one with a COGS account, one without) and a food item in an unmapped one.
		private static async Task<(int Para, int Amox, int Food)> FormularyAsync(World w)
		{
			w.Host.Seed.ItemCategories.Add(new ItemCategory { ID = 61, CompanyID = CompanyOne, Code = "MED", Name = "أدوية", NameEn = "Medicines", CogsAccountId = 501, InventoryAccountId = 101 });
			w.Host.Seed.ItemCategories.Add(new ItemCategory { ID = 62, CompanyID = CompanyOne, Code = "MED2", Name = "أدوية ٢", NameEn = "Medicines 2" });
			w.Host.Seed.ItemCategories.Add(new ItemCategory { ID = 63, CompanyID = CompanyOne, Code = "FOOD", Name = "أغذية", NameEn = "Food" });
			foreach (var (id, code, cat) in new[] { (71, "MED-PARA", 61), (72, "MED-AMOX", 62), (73, "FOOD-1", 63) })
				w.Host.Seed.Items.Add(new Item { ID = id, CompanyID = CompanyOne, ItemCode = code, Barcode = "B" + id, Name = code, NameEn = code, ItemCategoryId = cat, BaseUoMId = 1, SalesPrice = 1m, IsActive = true, CreatedAt = Now });
			w.Host.Seed.HospitalItemCategories.Add(new HospitalItemCategory { CompanyId = CompanyOne, ItemCategoryId = 61, Kind = HospitalPriceGroups.Medicine });
			w.Host.Seed.HospitalItemCategories.Add(new HospitalItemCategory { CompanyId = CompanyOne, ItemCategoryId = 62, Kind = HospitalPriceGroups.Medicine });
			await w.Host.Seed.SaveChangesAsync();
			return (71, 72, 73);
		}

		/// A visit signed with a prescription for a new patient (allergies optional). Returns the encounter.
		private static async Task<int> SignedAsync(World w, string tag, string? allergies, params HospitalPrescriptionInput[] rx)
		{
			var ctx = Ctx();
			Assert.True((await w.Consult.SeedDefaultCodesAsync(ctx)).Ok);
			var fee = await w.Host.Db.HospitalServices.Where(s => s.Code == "CONS-IM").Select(s => (int?)s.ID).FirstOrDefaultAsync()
				?? (await w.Billing.SaveServiceAsync(ctx, new HospitalServiceInput { Code = "CONS-IM", Name = "ك", NameEn = "C", Category = HospitalServiceCategories.Consultation, Price = 10m }, 7)).Id!.Value;
			var doc = (await w.Op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د " + tag, NameEn = "Dr " + tag, WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600, ConsultationServiceId = fee }, 7)).Id!.Value;
			var saved = await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م " + tag, NameEn = "P " + tag, Sex = "M", Allergies = allergies }, 7, Now);
			Assert.True(saved.Ok, saved.Error);
			var appt = (await w.Op.BookAsync(ctx, new HospitalBookingInput { PatientId = saved.Id!.Value, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			Assert.True((await w.Op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var enc = (await w.Consult.OpenAsync(ctx, appt, 7, Now)).Id!.Value;
			var signed = await w.Consult.SignAsync(ctx, enc, new HospitalEncounterInput { ChiefComplaint = "Fever", Diagnoses = new() { new() { Code = "J02.9", Primary = true } }, Prescription = rx.ToList() }, 7, Now);
			Assert.True(signed.Ok, signed.Error);
			return enc;
		}

		[Theory]
		[InlineData("Penicillin V 500 mg", "Penicillin\nAspirin", "Penicillin")]
		[InlineData("Aspirin 81 mg", "بنسلين، Aspirin (rash)", "Aspirin")]
		[InlineData("Paracetamol 500 mg", "Penicillin", "")]
		[InlineData("Paracetamol 500 mg", null, "")]
		[InlineData("Co", "co", "")]   // a fragment shorter than three letters is not an allergy match
		public void A_drug_is_flagged_by_the_allergies_its_name_mentions(string drug, string? allergies, string expected)
			=> Assert.Equal(expected, string.Join(",", HospitalPharmacyService.AllergiesIn(drug, allergies)));

		[Fact]
		public async Task A_prescription_keeps_only_items_the_pharmacy_dispenses_and_reaches_the_queue_once()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (para, _, food) = await FormularyAsync(w);
			var enc = await SignedAsync(w, "1", null,
				new HospitalPrescriptionInput { Drug = "MED-PARA", ItemId = para, Quantity = 2 },
				new HospitalPrescriptionInput { Drug = "Food", ItemId = food, Quantity = 1 },          // not a mapped category → free text
				new HospitalPrescriptionInput { Drug = "Vitamin D 1000 IU", Quantity = 1 });            // typed, not stocked
			var lines = await host.Db.HospitalPrescriptionLines.Where(r => r.EncounterId == enc).OrderBy(r => r.SortOrder).Select(r => r.ItemId).ToListAsync();
			Assert.Equal(new int?[] { para, null, null }, lines.ToArray());

			Assert.Equal(1, await w.Pharmacy.SyncAsync(Ctx()));
			Assert.Equal(0, await w.Pharmacy.SyncAsync(Ctx()));
			var d = await host.Db.HospitalDispenses.SingleAsync();
			Assert.Equal(("RX-000001", HospitalDispenseStatus.New, 3), (d.RxNo, d.Status, await host.Db.HospitalDispenseLines.CountAsync(l => l.DispenseId == d.ID)));
			Assert.Equal(0, await w.Pharmacy.SyncAsync(Ctx(9, CompanyTwo)));
			Assert.Empty(await w.Pharmacy.QueueAsync(Ctx(9, CompanyTwo), null));
		}

		[Fact]
		public async Task Dispensing_needs_the_review_a_store_and_a_cost_account_before_any_stock_moves()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (para, amox, _) = await FormularyAsync(w);
			await SignedAsync(w, "1", null, new HospitalPrescriptionInput { Drug = "MED-PARA", ItemId = para, Quantity = 2 }, new HospitalPrescriptionInput { Drug = "MED-AMOX", ItemId = amox, Quantity = 1 });
			await w.Pharmacy.SyncAsync(Ctx());
			var id = (await host.Db.HospitalDispenses.SingleAsync()).ID;

			Assert.Equal("Review the prescription before dispensing", (await w.Pharmacy.DispenseAsync(Ctx(), id, 7, "u", Now)).Error);
			Assert.True((await w.Pharmacy.VerifyAsync(Ctx(), id, "ok", 7, Now)).Ok);
			Assert.Equal("This prescription was already reviewed", (await w.Pharmacy.VerifyAsync(Ctx(), id, null, 7, Now)).Error);
			Assert.Equal("Choose the pharmacy store in the pharmacy settings", (await w.Pharmacy.DispenseAsync(Ctx(), id, 7, "u", Now)).Error);

			host.Seed.Warehouses.Add(new Warehouse { ID = 5, CompanyID = CompanyOne, Code = "WH-PH", Name = "صيدلية", NameEn = "Pharmacy", IsActive = true });
			await host.Seed.SaveChangesAsync();
			Assert.Equal("Near expiry is 1 to 365 days", (await w.Pharmacy.SaveSettingsAsync(Ctx(), 5, 0)).Error);
			Assert.Equal("not_found", (await w.Pharmacy.SaveSettingsAsync(Ctx(9, CompanyTwo), 5, 60)).Error);
			Assert.True((await w.Pharmacy.SaveSettingsAsync(Ctx(), 5, 60)).Ok);

			var noCogs = await w.Pharmacy.DispenseAsync(Ctx(), id, 7, "u", Now);
			Assert.Equal("Map the cost-of-goods account of {0}'s inventory category first", noCogs.Error);   // MED-AMOX's category has none
			Assert.Equal(0, w.Stock.Calls);
			Assert.Equal(HospitalDispenseStatus.Verified, (await host.Db.HospitalDispenses.AsNoTracking().SingleAsync()).Status);
		}

		[Fact]
		public async Task Only_a_pharmacist_reviews_and_dispenses_and_the_review_shows_what_to_weigh()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var (para, _, _) = await FormularyAsync(w);
			await SignedAsync(w, "1", "Paracetamol (rash)", new HospitalPrescriptionInput { Drug = "MED-PARA Paracetamol", ItemId = para, Quantity = 2 },
				new HospitalPrescriptionInput { Drug = "MED-PARA Paracetamol", ItemId = para, Quantity = 1 }, new HospitalPrescriptionInput { Drug = "Vitamin D", Quantity = 1 });
			await w.Pharmacy.SyncAsync(Ctx());
			var id = (await host.Db.HospitalDispenses.SingleAsync()).ID;

			var v = (await w.Pharmacy.RxAsync(Ctx(), id, Now))!;
			Assert.Contains(HospitalPharmacyService.WarnAllergy, v.Lines[0].Warnings);
			Assert.Contains(HospitalPharmacyService.WarnDuplicate, v.Lines[0].Warnings);
			Assert.Contains(HospitalPharmacyService.WarnNotStocked, v.Lines[2].Warnings);
			Assert.True(Assert.Single(await w.Pharmacy.QueueAsync(Ctx(), null)).HasWarning);

			foreach (var (emp, role) in new[] { (1, HospitalRoles.HospitalAdministrator), (50, HospitalRoles.HospitalPharmacist), (3, HospitalRoles.HospitalDoctor) })
				host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment { CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = emp, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow });
			await host.Seed.SaveChangesAsync();
			Assert.Equal("forbidden", (await w.Pharmacy.VerifyAsync(Ctx(3), id, null, 3, Now)).Error);
			Assert.Equal("forbidden", (await w.Pharmacy.DispenseAsync(Ctx(3), id, 3, "u", Now)).Error);
			Assert.True((await w.Pharmacy.VerifyAsync(Ctx(50), id, "Patient told about the allergy", 50, Now)).Ok);
			Assert.Equal("forbidden", (await w.Pharmacy.SaveSettingsAsync(Ctx(50), null, 60)).Error);   // the store is the administrator's choice
		}
	}
}
