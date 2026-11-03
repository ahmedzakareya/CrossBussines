using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H6 — billing and the cashier.
	//
	// What must never happen: a signed visit without its charges, a night charged twice on a
	// transfer day, a bill posted out of balance or posted twice, a stay's bill closed while the
	// patient is still in bed, a payment above what is owed or outside an open shift, a counted
	// difference that never reaches the ledger, a ledger refusal that still leaves a payment or a
	// closed bill behind, another company's bill reached by id, or a doctor working the till.
	// The ledger is a recorder here: every posting is asserted line by line.
	// ==========================================================================================
	public class HospitalBillingTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		private const int Cash = 101, Bank = 102, Receivable = 103, Revenue = 104, LabRevenue = 105, Vat = 106, OverShort = 107;
		private static readonly DateTime Today = new(2026, 11, 15);   // Sunday
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

		private static BusinessContext Ctx(int employeeId, int companyId = CompanyOne) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
			Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		private sealed class Recorder : IHospitalLedger
		{
			public readonly List<(string Source, int SourceId, IReadOnlyList<HospitalLedgerLine> Lines)> Posted = new();
			public string? Refuse;
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
			{
				if (Refuse != null) return Task.FromResult<(bool, string?, int?)>((false, Refuse, null));
				Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));   // every posting balances
				Posted.Add((sourceType, sourceId, lines));
				return Task.FromResult<(bool, string?, int?)>((true, null, Posted.Count));
			}
		}

		private sealed class World
		{
			public required PlatformTestHost Host;
			public required Recorder Ledger;
			public required HospitalBillingService Billing;
			public required HospitalConsultationService Consult;
			public required HospitalOutpatientService Op;
			public required HospitalPatientService Patients;
			public required HospitalAdmissionService Admissions;
			public required HospitalFacilityService Facility;
		}

		private static World Make(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var ledger = new Recorder();
			var billing = new HospitalBillingService(host.Db, access, ledger);
			return new World
			{
				Host = host, Ledger = ledger, Billing = billing, Consult = new(host.Db, access, billing), Op = new(host.Db, access), Patients = new(host.Db, access),
				Admissions = new(host.Db, access, billing), Facility = new(host.Db, access),
			};
		}

		/// The GL accounts the hospital posts to, as postable accounts of the company, and the settings that map them.
		private static async Task SetUpBooksAsync(World w, int companyId = CompanyOne, decimal vat = 5m)
		{
			foreach (var id in new[] { Cash, Bank, Receivable, Revenue, LabRevenue, Vat, OverShort })
				w.Host.Seed.Accounts.Add(new CrossBuy.Models.Context.Accounting.Account { ID = id + companyId * 1000, CompanyID = companyId, Code = "A" + id, Name = "ح" + id, NameEn = "Acc " + id, IsPostable = true, IsActive = true, AccountTypeId = 1 });
			await w.Host.Seed.SaveChangesAsync();
			int A(int id) => id + companyId * 1000;
			Assert.True((await w.Billing.SaveSettingsAsync(Ctx(7, companyId), new HospitalBillingSettingsInput
			{ VatRatePercent = vat, CashAccountId = A(Cash), BankAccountId = A(Bank), ReceivableAccountId = A(Receivable), RevenueAccountId = A(Revenue), VatAccountId = A(Vat), CashOverShortAccountId = A(OverShort) }, 7)).Ok);
		}

		private static int A(int id, int companyId = CompanyOne) => id + companyId * 1000;

		private static async Task<int> ServiceAsync(World w, string code, string category, decimal price, bool taxable = false, int? revenue = null, int companyId = CompanyOne)
		{
			var r = await w.Billing.SaveServiceAsync(Ctx(7, companyId), new HospitalServiceInput { Code = code, Name = "خ " + code, NameEn = "S " + code, Category = category, Price = price, Taxable = taxable, RevenueAccountId = revenue }, 7);
			Assert.True(r.Ok, r.Error);
			return r.Id!.Value;
		}

		/// A signed visit with a 20.000 consultation and the given orders. Returns the bill id.
		private static async Task<int> SignedVisitAsync(World w, params int[] orders)
		{
			var ctx = Ctx(7);
			Assert.True((await w.Consult.SeedDefaultCodesAsync(ctx)).Ok);
			var fee = await ServiceAsync(w, "CONS-IM", HospitalServiceCategories.Consultation, 20m);
			var doc = (await w.Op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د", NameEn = "Dr", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600, ConsultationServiceId = fee }, 7)).Id!.Value;
			var pat = (await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M" }, 7, Now)).Id!.Value;
			var appt = (await w.Op.BookAsync(ctx, new HospitalBookingInput { PatientId = pat, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			Assert.True((await w.Op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var enc = (await w.Consult.OpenAsync(ctx, appt, 7, Now)).Id!.Value;
			var signed = await w.Consult.SignAsync(ctx, enc, new HospitalEncounterInput { ChiefComplaint = "Fever", Diagnoses = new() { new() { Code = "J02.9", Primary = true } }, OrderServiceIds = orders.ToList() }, 7, Now);
			Assert.True(signed.Ok, signed.Error);
			return await w.Host.Db.HospitalBills.Where(b => b.EncounterId == enc).Select(b => b.ID).SingleAsync();
		}

		[Fact]
		public async Task Signing_a_visit_opens_its_bill_with_the_doctors_fee_and_every_order()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await SetUpBooksAsync(w);
			var cbc = await ServiceAsync(w, "LAB-CBC", HospitalServiceCategories.Lab, 6m);
			var cxr = await ServiceAsync(w, "RAD-CXR", HospitalServiceCategories.Radiology, 12m);
			var billId = await SignedVisitAsync(w, cbc, cxr);

			var v = (await w.Billing.BillAsync(Ctx(7), billId, Now))!;
			Assert.Equal(new[] { ("CONS-IM", "consultation"), ("LAB-CBC", "order"), ("RAD-CXR", "order") }, v.Lines.Select(l => (l.Code, l.Source)).ToArray());
			Assert.Equal(38m, v.Totals.Gross);
			Assert.Equal(38m, v.Totals.Due);
			Assert.Equal(HospitalBillStatus.Open, v.Bill.Status);
		}

		[Fact]
		public async Task An_order_must_be_an_orderable_service_and_a_consultation_cannot_be_ordered()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await SetUpBooksAsync(w);
			var meal = await ServiceAsync(w, "NM-MEAL", HospitalServiceCategories.Other, 3m);
			var ctx = Ctx(7);
			Assert.True((await w.Consult.SeedDefaultCodesAsync(ctx)).Ok);
			var doc = (await w.Op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د", NameEn = "Dr", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600 }, 7)).Id!.Value;
			var pat = (await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M" }, 7, Now)).Id!.Value;
			var appt = (await w.Op.BookAsync(ctx, new HospitalBookingInput { PatientId = pat, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			await w.Op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now);
			var enc = (await w.Consult.OpenAsync(ctx, appt, 7, Now)).Id!.Value;
			var r = await w.Consult.SaveDraftAsync(ctx, enc, new HospitalEncounterInput { OrderServiceIds = new() { meal } }, 7);
			Assert.Equal("An order is not a service the doctor can order", r.Error);
		}

		[Fact]
		public async Task Closing_posts_receivable_against_revenue_per_account_and_tax_and_cannot_post_twice()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await SetUpBooksAsync(w, vat: 5m);
			var cbc = await ServiceAsync(w, "LAB-CBC", HospitalServiceCategories.Lab, 6m, revenue: A(LabRevenue));
			var billId = await SignedVisitAsync(w, cbc);
			var meal = await ServiceAsync(w, "NM-MEAL", HospitalServiceCategories.Other, 4m, taxable: true);
			Assert.True((await w.Billing.AddLineAsync(Ctx(7), billId, meal, 2, 7, Now)).Ok);   // 8.000 taxed at 5% = 0.400

			Assert.True((await w.Billing.CloseAsync(Ctx(7), billId, 7, Now)).Ok);
			var post = Assert.Single(w.Ledger.Posted);
			Assert.Equal(("HospitalBill", billId), (post.Source, post.SourceId));
			var lines = post.Lines.ToDictionary(l => l.AccountId);
			Assert.Equal(34.4m, lines[A(Receivable)].Debit);      // 20 + 6 + 8 + 0.4
			Assert.Equal(28m, lines[A(Revenue)].Credit);          // the fee and the meals
			Assert.Equal(6m, lines[A(LabRevenue)].Credit);        // the lab's own revenue account
			Assert.Equal(0.4m, lines[A(Vat)].Credit);
			Assert.Equal("This bill is closed", (await w.Billing.CloseAsync(Ctx(7), billId, 7, Now)).Error);
			Assert.Equal("This bill is closed", (await w.Billing.AddLineAsync(Ctx(7), billId, meal, 1, 7, Now)).Error);
			Assert.Single(w.Ledger.Posted);
		}

		[Fact]
		public async Task A_ledger_refusal_leaves_the_bill_open_and_writes_no_payment()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await SetUpBooksAsync(w);
			var billId = await SignedVisitAsync(w);
			Assert.True((await w.Billing.OpenShiftAsync(Ctx(7), "Desk", 10m, 7, Now)).Ok);
			w.Ledger.Refuse = "period closed";

			Assert.Equal("The ledger refused the posting: {0}", (await w.Billing.CloseAsync(Ctx(7), billId, 7, Now)).Error);
			Assert.Equal("The ledger refused the posting: {0}", (await w.Billing.CollectAsync(Ctx(7), billId, 5m, HospitalPaymentMethods.Cash, 7, Now)).Error);
			host.Db.ChangeTracker.Clear();
			Assert.Equal(HospitalBillStatus.Open, (await host.Db.HospitalBills.AsNoTracking().SingleAsync()).Status);
			Assert.Equal(0, await host.Db.HospitalPayments.CountAsync());
		}

		[Fact]
		public async Task A_payment_needs_an_open_shift_never_exceeds_what_is_owed_and_posts_cash_or_bank()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await SetUpBooksAsync(w);
			var billId = await SignedVisitAsync(w);   // 20.000 owed

			Assert.Equal("Open your cashier shift before taking a payment", (await w.Billing.CollectAsync(Ctx(7), billId, 5m, HospitalPaymentMethods.Cash, 7, Now)).Error);
			Assert.True((await w.Billing.OpenShiftAsync(Ctx(7), "Clinics desk", 50m, 7, Now)).Ok);
			Assert.Equal("You already have an open shift", (await w.Billing.OpenShiftAsync(Ctx(7), "Second", 0m, 7, Now)).Error);
			Assert.Equal("The amount is more than the patient owes ({0})", (await w.Billing.CollectAsync(Ctx(7), billId, 25m, HospitalPaymentMethods.Cash, 7, Now)).Error);

			Assert.True((await w.Billing.CollectAsync(Ctx(7), billId, 12m, HospitalPaymentMethods.Cash, 7, Now)).Ok);
			Assert.True((await w.Billing.CollectAsync(Ctx(7), billId, 8m, HospitalPaymentMethods.Card, 7, Now)).Ok);
			Assert.Equal(new[] { "RCPT-000001", "RCPT-000002" }, await host.Db.HospitalPayments.OrderBy(p => p.ID).Select(p => p.ReceiptNo).ToArrayAsync());
			Assert.Equal((A(Cash), 12m), (w.Ledger.Posted[0].Lines[0].AccountId, w.Ledger.Posted[0].Lines[0].Debit));
			Assert.Equal((A(Bank), 8m), (w.Ledger.Posted[1].Lines[0].AccountId, w.Ledger.Posted[1].Lines[0].Debit));
			Assert.All(w.Ledger.Posted, p => Assert.Equal(A(Receivable), p.Lines[1].AccountId));
			Assert.Equal(0m, (await w.Billing.BillAsync(Ctx(7), billId, Now))!.Totals.Due);
		}

		[Fact]
		public async Task Closing_a_shift_counts_the_till_and_posts_a_shortage_or_nothing_when_it_balances()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await SetUpBooksAsync(w);
			var billId = await SignedVisitAsync(w);
			var shift = (await w.Billing.OpenShiftAsync(Ctx(7), "Clinics desk", 50m, 7, Now)).Id!.Value;
			Assert.True((await w.Billing.CollectAsync(Ctx(7), billId, 20m, HospitalPaymentMethods.Cash, 7, Now)).Ok);   // expected 70.000
			int before = w.Ledger.Posted.Count;

			// 3 × 20 + 1 × 5 + 4 × 1 = 69.000 → one dinar short
			Assert.True((await w.Billing.CloseShiftAsync(Ctx(7), shift, new Dictionary<decimal, int> { [20m] = 3, [5m] = 1, [1m] = 4 }, "Main treasury", 7, Now)).Ok);
			var s = await host.Db.HospitalCashierShifts.AsNoTracking().SingleAsync();
			Assert.Equal((70m, 69m, -1m, HospitalShiftStatus.Closed), (s.ExpectedCash!.Value, s.CountedCash!.Value, s.Variance!.Value, s.Status));
			var post = w.Ledger.Posted[before];
			Assert.Equal(("HospitalShift", A(OverShort), 1m), (post.Source, post.Lines[0].AccountId, post.Lines[0].Debit));
			Assert.Equal((A(Cash), 1m), (post.Lines[1].AccountId, post.Lines[1].Credit));
			Assert.Equal("This shift is already closed", (await w.Billing.CloseShiftAsync(Ctx(7), shift, new Dictionary<decimal, int>(), null, 7, Now)).Error);

			// A balanced till posts nothing.
			var second = (await w.Billing.OpenShiftAsync(Ctx(7), "Clinics desk", 10m, 7, Now)).Id!.Value;
			int count = w.Ledger.Posted.Count;
			Assert.True((await w.Billing.CloseShiftAsync(Ctx(7), second, new Dictionary<decimal, int> { [10m] = 1 }, null, 7, Now)).Ok);
			Assert.Equal(count, w.Ledger.Posted.Count);
			Assert.Equal(new[] { 20m, 10m, 5m, 1m, 0.5m, 0.25m, 0.1m, 0.05m, 0.02m, 0.01m, 0.005m }, HospitalBillingService.Denominations("KWD").ToArray());
		}

		[Fact]
		public async Task A_stay_is_charged_one_night_per_midnight_to_the_bed_slept_in_and_its_bill_waits_for_discharge()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await SetUpBooksAsync(w);
			var ctx = Ctx(7);
			Assert.True((await w.Facility.SeedDefaultRoomTypesAsync(ctx, 7)).Ok);
			var floor = (await w.Facility.SaveFloorAsync(ctx, new HospitalFloorInput { Code = "F1", Name = "الأول" }, 7)).Id!.Value;
			async Task<int> Type(string c) => await host.Db.HospitalRoomTypes.Where(t => t.CompanyId == CompanyOne && t.Code == c).Select(t => t.ID).SingleAsync();
			foreach (var (num, code) in new[] { ("101", "private"), ("102", "vip") })
				Assert.True((await w.Facility.CreateRoomAsync(ctx, new HospitalRoomInput { FloorId = floor, RoomTypeId = await Type(code), Number = num }, 7)).Ok);
			foreach (var t in await host.Db.HospitalRoomTypes.ToListAsync()) t.DailyRate = t.Code == "vip" ? 120m : 75m;
			await host.Db.SaveChangesAsync();
			var beds = await host.Db.HospitalBeds.ToDictionaryAsync(b => b.Code, b => b.ID);
			var pat = (await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M" }, 7, Now)).Id!.Value;

			var day1 = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
			var adm = (await w.Admissions.AdmitAsync(ctx, new HospitalAdmitInput { PatientId = pat, BedId = beds["101"] }, 7, day1)).Id!.Value;
			var billId = await host.Db.HospitalBills.Where(b => b.AdmissionId == adm).Select(b => b.ID).SingleAsync();   // opened with the stay
			Assert.True((await w.Admissions.TransferAsync(ctx, adm, beds["102"], 7, day1.AddDays(2).AddHours(5))).Ok);      // 3 Nov, afternoon

			// Running: 1 and 2 Nov in 101, 3 Nov in 102 (the bed slept in that night).
			var running = (await w.Billing.BillAsync(ctx, billId, new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc)))!;
			Assert.Equal(new[] { (2m, 75m), (1m, 120m) }, running.Lines.Select(l => (l.Quantity, l.UnitPrice)).ToArray());
			Assert.All(running.Lines, l => Assert.True(l.Running));
			Assert.Equal("Discharge the patient before closing the stay's bill", (await w.Billing.CloseAsync(ctx, billId, 7, Now)).Error);

			Assert.True((await w.Admissions.DischargeAsync(ctx, adm, 7, new DateTime(2026, 11, 5, 11, 0, 0, DateTimeKind.Utc))).Ok);
			Assert.True((await w.Billing.CloseAsync(ctx, billId, 7, new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc))).Ok);
			var closed = (await w.Billing.BillAsync(ctx, billId, Now))!;
			Assert.Equal(new[] { (2m, 75m), (2m, 120m) }, closed.Lines.Select(l => (l.Quantity, l.UnitPrice)).ToArray());   // 4 nights, frozen
			Assert.All(closed.Lines, l => Assert.False(l.Running));
			Assert.Equal(390m, Assert.Single(w.Ledger.Posted).Lines.Single(l => l.Debit > 0).Debit);
		}

		[Fact]
		public async Task Another_companys_bill_is_out_of_reach_and_a_doctor_does_not_work_the_till()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await SetUpBooksAsync(w);
			var billId = await SignedVisitAsync(w);
			Assert.Null(await w.Billing.BillAsync(Ctx(9, CompanyTwo), billId, Now));
			Assert.Equal("not_found", (await w.Billing.CloseAsync(Ctx(9, CompanyTwo), billId, 9, Now)).Error);
			Assert.True((await w.Billing.OpenShiftAsync(Ctx(9, CompanyTwo), "Their desk", 0m, 9, Now)).Ok);
			Assert.Equal("not_found", (await w.Billing.CollectAsync(Ctx(9, CompanyTwo), billId, 5m, HospitalPaymentMethods.Cash, 9, Now)).Error);
			Assert.Empty(await w.Billing.BillsAsync(Ctx(9, CompanyTwo), null, null, Now));

			foreach (var (emp, role) in new[] { (1, HospitalRoles.HospitalAdministrator), (3, HospitalRoles.HospitalDoctor), (2, HospitalRoles.HospitalStaff) })
				host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment { CompanyID = CompanyOne, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = emp, Role = role, IsActive = true, CreatedAt = DateTime.UtcNow });
			await host.Seed.SaveChangesAsync();
			Assert.Equal("forbidden", (await w.Billing.OpenShiftAsync(Ctx(3), "Doctor desk", 0m, 3, Now)).Error);
			Assert.True((await w.Billing.OpenShiftAsync(Ctx(2), "Clinics desk", 0m, 2, Now)).Ok);
			Assert.Equal("forbidden", (await w.Billing.SaveServiceAsync(Ctx(2), new HospitalServiceInput { Code = "X", Name = "x", NameEn = "x", Category = "Lab" }, 2)).Error);
		}

		[Fact]
		public async Task Settings_accept_only_posting_accounts_of_the_company_and_a_bill_cannot_close_without_them()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			Assert.Equal("Choose active posting accounts of this company", (await w.Billing.SaveSettingsAsync(Ctx(7), new HospitalBillingSettingsInput { CashAccountId = 999 }, 7)).Error);
			Assert.Equal("The tax rate is 0 to 100%", (await w.Billing.SaveSettingsAsync(Ctx(7), new HospitalBillingSettingsInput { VatRatePercent = 120 }, 7)).Error);
			var billId = await SignedVisitAsync(w);
			Assert.Equal("Map the receivable and revenue accounts in billing settings", (await w.Billing.CloseAsync(Ctx(7), billId, 7, Now)).Error);
			Assert.Empty(w.Ledger.Posted);
		}
	}
}
