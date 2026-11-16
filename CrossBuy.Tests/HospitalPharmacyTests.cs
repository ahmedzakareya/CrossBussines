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

		/// H121, found reading the H120 dispense: a chemotherapy cycle's request has no visit behind it, so it went on the
		/// patient's own outpatient bill even while they were admitted — the cycle's administration charge on the stay's
		/// bill, its drugs on another, priced by another list. It now follows the stay, and the pharmacist sees which bill.
		[Fact]
		public async Task A_request_with_no_visit_goes_on_the_stays_bill_while_the_patient_is_admitted()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var ctx = Ctx();
			var pid = (await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م ورم", NameEn = "P Onc", Sex = "F" }, 7, Now)).Id!.Value;
			host.Seed.HospitalDispenses.Add(new HospitalDispense { CompanyId = CompanyOne, RxNo = "RX-000001", PatientId = pid, Source = HospitalDispenseSource.Oncology, PrescribedAt = Now });
			await host.Seed.SaveChangesAsync();
			var rx = await host.Db.HospitalDispenses.AsNoTracking().SingleAsync();
			Assert.True(HospitalPharmacyService.BillsLikeAWardCharge(rx));
			Assert.False(HospitalPharmacyService.BillsLikeAWardCharge(new HospitalDispense { EncounterId = 5 }));   // a visit's request stays on the visit's bill

			Assert.Null((await w.Pharmacy.RxAsync(ctx, rx.ID, Now))!.BillNo);   // no bill yet: the preview creates none
			Assert.Equal(0, await host.Db.HospitalBills.CountAsync());

			var own = await w.Billing.BillForWardChargeAsync(ctx, pid, 7, Now);   // not admitted: the patient's own bill
			var v = (await w.Pharmacy.RxAsync(ctx, rx.ID, Now))!;
			Assert.Equal((own.BillNo, false), (v.BillNo, v.OnStay));

			host.Seed.HospitalAdmissions.Add(new HospitalAdmission { ID = 40, CompanyId = CompanyOne, PatientId = pid, Status = HospitalAdmissionStatus.Active, AdmittedAt = Now, CreatedAt = Now });
			await host.Seed.SaveChangesAsync();
			var stay = await w.Billing.BillForWardChargeAsync(ctx, pid, 7, Now);   // where the cycle's administration charge goes
			Assert.Equal(40, stay.AdmissionId);
			v = (await w.Pharmacy.RxAsync(ctx, rx.ID, Now))!;
			Assert.Equal((stay.BillNo, true), (v.BillNo, v.OnStay));
			Assert.Equal(stay.ID, (await w.Billing.PeekWardChargeBillAsync(ctx, pid))!.ID);

			var adm = await host.Seed.HospitalAdmissions.SingleAsync(a => a.ID == 40);
			adm.Status = HospitalAdmissionStatus.Discharged; await host.Seed.SaveChangesAsync();
			Assert.Equal(own.ID, (await w.Billing.PeekWardChargeBillAsync(ctx, pid))!.ID);   // discharged: back to the patient's own
			Assert.Null(await w.Billing.PeekWardChargeBillAsync(Ctx(9, CompanyTwo), pid));   // never another company's
		}

		// ==========================================================================================
		// H133: the drug master. The pharmacist describes a medicine once, and the dispense review reads it.
		// What must never happen:
		//   · a brand-name prescription slipping past an allergy to its generic;
		//   · a high-alert or look-alike medicine leaving with no word said;
		//   · a withdrawn medicine leaving the store;
		//   · the formulary and the ward's controlled register disagreeing;
		//   · another company's item being described.
		// ==========================================================================================

		private static HospitalDrugMasterService Master(World w) => new(w.Host.Db,
			new HospitalAccessService(new PlatformRoleDirectory(w.Host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance));

		private static HospitalDrugInput Amoxicillin(int item) => new()
		{
			ItemId = item, GenericName = "أموكسيسيلين", GenericNameEn = "amoxicillin", TallMan = "amOXicillin", Strength = 500, StrengthUnit = "mg",
			Form = HospitalDrugForm.Capsule, Routes = new() { HospitalMedRoute.Oral }, Atc = "j01ca04", FormularyStatus = HospitalFormularyStatus.Formulary,
		};

		[Fact]
		public async Task The_formulary_lists_the_medicines_and_the_pharmacist_describes_one()
		{
			using var host = new PlatformTestHost(); var w = Make(host); var m = Master(w);
			var (para, amox, food) = await FormularyAsync(w);
			var stats = await m.StatsAsync(Ctx());
			Assert.Equal((2, 0, 0), (stats.Medicines, stats.Described, stats.Incomplete));   // the food item is no medicine

			Assert.True((await m.SaveAsync(Ctx(), Amoxicillin(amox), 7, Now)).Ok);
			var (rows, total) = await m.ListAsync(Ctx(), null, null, 1, 25);
			Assert.Equal(2, total);
			Assert.Equal(para, rows[0].ItemId);   // the one still to describe comes first
			var d = rows[1].Drug!;
			Assert.Equal(("J01CA04", "PO", "amOXicillin", 0), (d.Atc, d.Routes, d.TallMan, rows[1].Missing.Count));
			Assert.Single((await m.ListAsync(Ctx(), "j01", null, 1, 25)).Rows);
			Assert.Equal(para, Assert.Single((await m.ListAsync(Ctx(), null, HospitalFormularyFilter.Undescribed, 1, 25)).Rows).ItemId);

			Assert.Equal("not_found", (await m.SaveAsync(Ctx(), Amoxicillin(food), 7, Now)).Error);              // not a medicine
			Assert.Equal("not_found", (await m.SaveAsync(Ctx(9, CompanyTwo), Amoxicillin(amox), 7, Now)).Error); // another company's
			var bad = Amoxicillin(amox); bad.FormularyStatus = HospitalFormularyStatus.Restricted;
			Assert.Equal("Write who may prescribe it, or why it is off the list", (await m.SaveAsync(Ctx(), bad, 7, Now)).Error);
			Assert.Equal(1, await host.Db.Set<HospitalDrug>().CountAsync());
		}

		[Fact]
		public async Task A_controlled_schedule_keeps_the_wards_controlled_register_in_step()
		{
			using var host = new PlatformTestHost(); var w = Make(host); var m = Master(w);
			var (_, amox, _) = await FormularyAsync(w);
			var i = Amoxicillin(amox); i.ControlledSchedule = HospitalControlledSchedule.Psychotropic;
			Assert.True((await m.SaveAsync(Ctx(), i, 7, Now)).Ok);
			Assert.True(await host.Db.Set<HospitalControlledItem>().AnyAsync(c => c.ItemId == amox));
			Assert.True((await m.SaveAsync(Ctx(), i, 7, Now)).Ok);   // saved again: still one flag
			Assert.Equal(1, await host.Db.Set<HospitalControlledItem>().CountAsync());
			i.ControlledSchedule = null;
			Assert.True((await m.SaveAsync(Ctx(), i, 7, Now)).Ok);
			Assert.False(await host.Db.Set<HospitalControlledItem>().AnyAsync());
		}

		[Fact]
		public async Task The_review_reads_the_record_and_a_withdrawn_medicine_never_leaves()
		{
			using var host = new PlatformTestHost(); var w = Make(host); var m = Master(w);
			var (para, amox, _) = await FormularyAsync(w);
			// the doctor wrote the brand; the file records an allergy to the generic
			await SignedAsync(w, "1", "Amoxicillin (hives)", new HospitalPrescriptionInput { Drug = "Augmentin 1 g", ItemId = amox, Quantity = 1 },
				new HospitalPrescriptionInput { Drug = "MED-PARA", ItemId = para, Quantity = 1 });
			await w.Pharmacy.SyncAsync(Ctx());
			var id = (await host.Db.HospitalDispenses.SingleAsync()).ID;
			Assert.DoesNotContain(HospitalPharmacyService.WarnAllergy, (await w.Pharmacy.RxAsync(Ctx(), id, Now))!.Lines[0].Warnings);   // no record: the brand hides it

			var i = Amoxicillin(amox); i.HighAlert = true; i.LasaWith = "amoxapine";
			Assert.True((await m.SaveAsync(Ctx(), i, 7, Now)).Ok);
			var v = (await w.Pharmacy.RxAsync(Ctx(), id, Now))!;
			Assert.Equal(new[] { HospitalPharmacyService.WarnAllergy, HospitalPharmacyService.WarnHighAlert, HospitalPharmacyService.WarnLasa },
				v.Lines[0].Warnings.Where(x => x is HospitalPharmacyService.WarnAllergy or HospitalPharmacyService.WarnHighAlert or HospitalPharmacyService.WarnLasa).ToArray());
			Assert.Equal("amOXicillin", HospitalDrugRules.Shown(v.Lines[0].Master));
			Assert.Null(v.Lines[1].Master);

			i.FormularyStatus = HospitalFormularyStatus.Unavailable; i.RestrictionNote = "Recalled batch, supplier notice";
			Assert.True((await m.SaveAsync(Ctx(), i, 7, Now)).Ok);
			Assert.Contains(HospitalPharmacyService.WarnUnavailable, (await w.Pharmacy.RxAsync(Ctx(), id, Now))!.Lines[0].Warnings);
			Assert.True((await w.Pharmacy.VerifyAsync(Ctx(), id, null, 7, Now)).Ok);
			host.Seed.Warehouses.Add(new Warehouse { ID = 5, CompanyID = CompanyOne, Code = "WH-PH", Name = "صيدلية", NameEn = "Pharmacy", IsActive = true });
			await host.Seed.SaveChangesAsync();
			Assert.True((await w.Pharmacy.SaveSettingsAsync(Ctx(), 5, 60)).Ok);
			Assert.Equal("{0} is withdrawn from the formulary and cannot be dispensed", (await w.Pharmacy.DispenseAsync(Ctx(), id, 7, "u", Now)).Error);
			Assert.Equal(0, w.Stock.Calls);
		}

		// ==========================================================================================
		// H136: the pharmacist's review. What must never happen:
		//   · a STAT prescription waiting behind routine ones;
		//   · a refused prescription dispensed, or refused without a reason;
		//   · an intervention filed against another prescription's line;
		//   · a prescriber's answer recorded twice;
		//   · the late count including what was already reviewed.
		// ==========================================================================================

		private static HospitalPharmacyReviewService Review(World w) => new(w.Host.Db,
			new HospitalAccessService(new PlatformRoleDirectory(w.Host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance));

		[Fact]
		public async Task The_queue_works_the_most_urgent_first_and_counts_what_is_late()
		{
			using var host = new PlatformTestHost(); var w = Make(host); var r = Review(w);
			var (para, _, _) = await FormularyAsync(w);
			await SignedAsync(w, "1", null, new HospitalPrescriptionInput { Drug = "MED-PARA", ItemId = para, Quantity = 1 });
			await SignedAsync(w, "2", null, new HospitalPrescriptionInput { Drug = "MED-PARA", ItemId = para, Quantity = 1 });
			await w.Pharmacy.SyncAsync(Ctx());
			var ids = await host.Db.HospitalDispenses.OrderBy(d => d.ID).Select(d => d.ID).ToListAsync();
			Assert.True((await r.SetPriorityAsync(Ctx(), ids[1], HospitalRxPriority.Stat)).Ok);
			Assert.Equal("Choose the priority", (await r.SetPriorityAsync(Ctx(), ids[1], "Soon")).Error);

			var q = await w.Pharmacy.QueueAsync(Ctx(), null);
			Assert.Equal(new[] { (ids[1], HospitalRxPriority.Stat), (ids[0], HospitalRxPriority.Routine) }, q.Select(x => (x.Id, x.Priority)).ToArray());

			// both were prescribed at Now: an hour later the STAT one is late, the routine one is not
			var s = await w.Pharmacy.StatsAsync(Ctx(), Now.Date, Now.AddHours(1));
			Assert.Equal((2, 1), (s.Waiting, s.Late));
			Assert.True((await w.Pharmacy.VerifyAsync(Ctx(), ids[1], null, 7, Now.AddMinutes(50))).Ok);
			Assert.Equal(0, (await w.Pharmacy.StatsAsync(Ctx(), Now.Date, Now.AddHours(1))).Late);   // reviewed: no longer waiting for the review
		}

		[Fact]
		public async Task A_refused_prescription_never_reaches_dispensing_and_interventions_are_answered_once()
		{
			using var host = new PlatformTestHost(); var w = Make(host); var r = Review(w);
			var (para, amox, _) = await FormularyAsync(w);
			await SignedAsync(w, "1", null, new HospitalPrescriptionInput { Drug = "MED-AMOX", ItemId = amox, Quantity = 1 });
			await SignedAsync(w, "2", null, new HospitalPrescriptionInput { Drug = "MED-PARA", ItemId = para, Quantity = 1 });
			await w.Pharmacy.SyncAsync(Ctx());
			var ds = await host.Db.HospitalDispenses.OrderBy(d => d.ID).ToListAsync();
			var lineOfOther = await host.Db.HospitalDispenseLines.Where(l => l.DispenseId == ds[1].ID).Select(l => l.ID).FirstAsync();
			var line = await host.Db.HospitalDispenseLines.Where(l => l.DispenseId == ds[0].ID).Select(l => l.ID).FirstAsync();

			var iv = new HospitalInterventionInput { LineId = lineOfOther, Category = HospitalInterventionCategory.Renal, Severity = HospitalInterventionSeverity.High, Problem = "eGFR 25", Recommendation = "Halve the dose", Minutes = 8 };
			Assert.Equal("not_found", (await r.RecordAsync(Ctx(), ds[0].ID, iv, 7, Now)).Error);   // another prescription's line
			iv.LineId = line;
			var rec = await r.RecordAsync(Ctx(), ds[0].ID, iv, 7, Now);
			Assert.True(rec.Ok, rec.Error);
			Assert.Equal("MED-AMOX", (await r.InterventionsAsync(Ctx(), ds[0].ID)).Single().I.Drug);
			Assert.True((await r.OutcomeAsync(Ctx(), rec.Id!.Value, HospitalInterventionOutcome.Accepted, 7, Now)).Ok);
			Assert.Equal("The prescriber's answer is already recorded", (await r.OutcomeAsync(Ctx(), rec.Id!.Value, HospitalInterventionOutcome.Declined, 7, Now)).Error);
			var month = await w.Pharmacy.StatsAsync(Ctx(), Now.Date, Now);
			Assert.Equal((1, (int?)100), (month.Interventions, month.AcceptedPct));

			Assert.Equal("Write why the prescription is refused", (await r.RejectAsync(Ctx(), ds[0].ID, " ", 7, Now)).Error);
			Assert.True((await r.RejectAsync(Ctx(), ds[0].ID, "Duplicate of the ward's order", 7, Now)).Ok);
			Assert.Equal("This prescription has left the pharmacy", (await r.RejectAsync(Ctx(), ds[0].ID, "again", 7, Now)).Error);
			Assert.Equal("This prescription has left the pharmacy", (await r.SetPriorityAsync(Ctx(), ds[0].ID, HospitalRxPriority.Stat)).Error);
			Assert.Equal("This prescription was already reviewed", (await w.Pharmacy.VerifyAsync(Ctx(), ds[0].ID, null, 7, Now)).Error);
			Assert.Equal("Review the prescription before dispensing", (await w.Pharmacy.DispenseAsync(Ctx(), ds[0].ID, 7, "u", Now)).Error);
			Assert.DoesNotContain(ds[0].ID, (await w.Pharmacy.QueueAsync(Ctx(), null)).Select(x => x.Id));   // off the open work
			Assert.Equal("not_found", (await r.RejectAsync(Ctx(9, CompanyTwo), ds[1].ID, "x", 9, Now)).Error);
		}
	}
}
