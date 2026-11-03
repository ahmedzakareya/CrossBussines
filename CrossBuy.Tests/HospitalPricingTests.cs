using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H7 — price lists.
	//
	// What must never happen: a new list that skips a catalogue item or starts "up to date", a list
	// approved with a price nobody reviewed, a main-price change that leaves another list silently
	// stale, a new catalogue item slipped into a list without anyone adding it, a medicine priced above
	// its public price, a draft (or another company's) list pricing a bill, a foreign-currency bill
	// posted in its own currency, or paid at a new rate without the difference reaching the ledger.
	// ==========================================================================================
	public class HospitalPricingTests
	{
		private const int CompanyOne = 1, CompanyTwo = 2;
		private const int Kwd = 5, Sar = 4;
		private const int Cash = 101, Bank = 102, Receivable = 103, Revenue = 104, Vat = 106, OverShort = 107, Fx = 108;
		private static readonly DateTime Today = new(2026, 11, 15);   // Sunday
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

		private static BusinessContext Ctx(int employeeId = 7, int companyId = CompanyOne) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
			Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
		};

		private static int A(int id, int companyId = CompanyOne) => id + companyId * 1000;

		/// Rates quoted in Kuwaiti dinars per unit; the test moves them to make an exchange difference.
		private sealed class Rates : ICurrencyService
		{
			public readonly Dictionary<int, decimal> Kwd = new() { [5] = 1m, [4] = 0.082m, [2] = 0.307m };
			public Task<int> GetFunctionalCurrencyIdAsync(int companyId, int? branchId) => Task.FromResult(5);
			public Task<Currency?> GetCurrencyAsync(int currencyId) => Task.FromResult<Currency?>(null);
			public Task<(decimal rate, bool found)> GetRateToEgpAsync(int currencyId, DateTime date, string rateType) => Task.FromResult((Kwd[currencyId], true));
			public Task<(decimal baseAmount, decimal effectiveRate)> ToBaseAsync(decimal amount, int fromCurrencyId, int functionalCurrencyId, DateTime date, string rateType)
			{
				if (!Kwd.ContainsKey(fromCurrencyId)) throw new InvalidOperationException("no rate");
				var r = Kwd[fromCurrencyId] / Kwd[functionalCurrencyId];
				return Task.FromResult((Math.Round(amount * r, 4), Math.Round(r, 4)));
			}
			public Task<(bool stale, int ageDays, int maxAgeDays, string behavior)> RateStalenessAsync(int companyId, int currencyId, DateTime asOf) => Task.FromResult((false, 0, 0, "Warn"));
		}

		private sealed class Recorder : IHospitalLedger
		{
			public readonly List<(string Source, int SourceId, IReadOnlyList<HospitalLedgerLine> Lines)> Posted = new();
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
			{
				Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));   // every posting balances, in the facility's currency
				Posted.Add((sourceType, sourceId, lines));
				return Task.FromResult<(bool, string?, int?)>((true, null, Posted.Count));
			}
		}

		private sealed class World
		{
			public required PlatformTestHost Host;
			public required Rates Rates;
			public required Recorder Ledger;
			public required HospitalPricingService Pricing;
			public required HospitalBillingService Billing;
			public required HospitalConsultationService Consult;
			public required HospitalOutpatientService Op;
			public required HospitalPatientService Patients;
		}

		private static async Task<World> MakeAsync(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var rates = new Rates();
			var pricing = new HospitalPricingService(host.Db, access, rates);
			var ledger = new Recorder();
			var billing = new HospitalBillingService(host.Db, access, ledger, pricing);
			var w = new World { Host = host, Rates = rates, Ledger = ledger, Pricing = pricing, Billing = billing, Consult = new(host.Db, access, billing), Op = new(host.Db, access), Patients = new(host.Db, access) };
			foreach (var (id, code, dec) in new[] { (Kwd, "KWD", (byte)3), (Sar, "SAR", (byte)2), (2, "USD", (byte)2) })
				host.Seed.Currencies.Add(new Currency { ID = id, Code = code, Name = code, NameEn = code, DecimalPlaces = dec });
			foreach (var c in new[] { CompanyOne, CompanyTwo })
				host.Seed.Companies.Add(new Companies { CompanyID = c, CompanyName = "C" + c, ComoanyNameAr = "C" + c, CountryID = 1, CompanyTypeId = 1, Address = "-", PhoneNumber = "-", Email = $"c{c}@example.com", DefaultCurrencyId = Kwd });
			foreach (var id in new[] { Cash, Bank, Receivable, Revenue, Vat, OverShort, Fx })
				host.Seed.Accounts.Add(new Account { ID = A(id), CompanyID = CompanyOne, Code = "A" + id, Name = "ح" + id, NameEn = "Acc " + id, IsPostable = true, IsActive = true, AccountTypeId = 1 });
			await host.Seed.SaveChangesAsync();
			Assert.True((await billing.SaveSettingsAsync(Ctx(), new HospitalBillingSettingsInput
			{ VatRatePercent = 0, CashAccountId = A(Cash), BankAccountId = A(Bank), ReceivableAccountId = A(Receivable), RevenueAccountId = A(Revenue), VatAccountId = A(Vat), CashOverShortAccountId = A(OverShort), FxDifferenceAccountId = A(Fx) }, 7)).Ok);
			return w;
		}

		private static async Task<int> ServiceAsync(World w, string code, decimal price, string category = HospitalServiceCategories.Lab, int companyId = CompanyOne)
		{
			var r = await w.Billing.SaveServiceAsync(Ctx(7, companyId), new HospitalServiceInput { Code = code, Name = "خ " + code, NameEn = "S " + code, Category = category, Price = price }, 7);
			Assert.True(r.Ok, r.Error);
			return r.Id!.Value;
		}

		private static HospitalPriceListInput ListInput(int currency = Kwd, decimal svc = -15, decimal med = 0, decimal sup = 0, decimal? step = 0.25m) => new()
		{ Name = "قائمة", NameEn = "List", Kind = HospitalPriceListKinds.Insurer, CurrencyId = currency, ServicesAdjPct = svc, MedicinesAdjPct = med, ConsumablesAdjPct = sup, RoundingStep = step, ValidFrom = new(2026, 1, 1), ValidTo = new(2026, 12, 31) };

		private static async Task<int> ApprovedListAsync(World w, HospitalPriceListInput input)
		{
			var id = (await w.Pricing.CreateAsync(Ctx(), input, 7, Now)).Id!.Value;
			Assert.True((await w.Pricing.ApproveLinesAsync(Ctx(), id, null, 7, Now)).Ok);
			Assert.True((await w.Pricing.ApproveListAsync(Ctx(), id, 7, Now)).Ok);
			return id;
		}

		/// Two inventory items in a category the hospital maps as medicines, one in a consumables category.
		private static async Task<(int Para, int Augm, int Glove)> InventoryAsync(World w)
		{
			w.Host.Seed.ItemCategories.Add(new ItemCategory { ID = 61, CompanyID = CompanyOne, Code = "MED", Name = "أدوية", NameEn = "Medicines" });
			w.Host.Seed.ItemCategories.Add(new ItemCategory { ID = 62, CompanyID = CompanyOne, Code = "SUP", Name = "مستهلكات", NameEn = "Consumables" });
			w.Host.Seed.ItemCategories.Add(new ItemCategory { ID = 63, CompanyID = CompanyOne, Code = "FOOD", Name = "أغذية", NameEn = "Food" });
			foreach (var (id, code, cat, price) in new[] { (71, "MED-PARA", 61, 0.750m), (72, "MED-AUGM", 61, 3.950m), (73, "SUP-GLOVE", 62, 0.150m), (74, "FOOD-1", 63, 1m) })
				w.Host.Seed.Items.Add(new Item { ID = id, CompanyID = CompanyOne, ItemCode = code, Barcode = "B" + id, Name = code, NameEn = code, ItemCategoryId = cat, BaseUoMId = 1, SalesPrice = price, IsActive = true, CreatedAt = Now });
			await w.Host.Seed.SaveChangesAsync();
			Assert.True((await w.Pricing.SaveItemCategoriesAsync(Ctx(), new Dictionary<int, string> { [61] = HospitalPriceGroups.Medicine, [62] = HospitalPriceGroups.Consumable })).Ok);
			return (71, 72, 73);
		}

		[Theory]
		[InlineData(20, 1, -15, 3, 0.25, true, 17)]             // KWD, a 15% discount, to the nearest quarter
		[InlineData(6, 1, -15, 3, 0.25, true, 5)]               // 5.1 snaps down to 5.000
		[InlineData(20, 12.18026797, -12, 2, 5, true, 215)]      // into riyals at the frozen rate, to the nearest 5
		[InlineData(0.75, 12.18026797, -5, 2, 5, false, 8.68)]   // an item is not snapped, only rounded to the currency
		public void The_rule_is_main_price_times_rate_times_adjustment_then_rounded(decimal main, decimal rate, decimal adj, int dec, double step, bool snap, decimal expected)
			=> Assert.Equal(expected, HospitalPricingService.Rule(main, rate, adj, dec, (decimal)step, snap));

		[Fact]
		public async Task A_new_list_copies_every_catalogue_item_all_not_updated_and_cannot_be_approved_until_reviewed()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			await ServiceAsync(w, "LAB-CBC", 6m); await ServiceAsync(w, "CONS-GEN", 15m, HospitalServiceCategories.Consultation);
			await InventoryAsync(w);

			var id = (await w.Pricing.CreateAsync(Ctx(), ListInput(), 7, Now)).Id!.Value;
			var v = (await w.Pricing.ListAsync(Ctx(), id))!;
			Assert.Equal(new[] { "CONS-GEN", "LAB-CBC", "MED-AUGM", "MED-PARA", "SUP-GLOVE" }, v.Lines.Select(l => l.Entry.Code).OrderBy(c => c).ToArray());   // the food item's category is not the hospital's
			Assert.All(v.Lines, l => Assert.True(l.Stale));
			Assert.Equal(HospitalPriceListStatus.Draft, v.List.Status);
			Assert.Equal("{0} prices are not updated. Review them before approving", (await w.Pricing.ApproveListAsync(Ctx(), id, 7, Now)).Error);

			Assert.True((await w.Pricing.ApproveLinesAsync(Ctx(), id, null, 7, Now)).Ok);
			Assert.True((await w.Pricing.ApproveListAsync(Ctx(), id, 7, Now)).Ok);
			Assert.Equal(12.75m, (await w.Pricing.ListAsync(Ctx(), id))!.Lines.Single(l => l.Entry.Code == "CONS-GEN").Price);   // 15 − 15% = 12.75
		}

		[Fact]
		public async Task A_main_price_change_marks_the_line_not_updated_everywhere_and_a_new_item_lands_on_the_main_list_only()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			var gen = await ServiceAsync(w, "CONS-GEN", 15m, HospitalServiceCategories.Consultation);
			var a = await ApprovedListAsync(w, ListInput());
			var b = await ApprovedListAsync(w, ListInput(svc: -20, step: null));

			Assert.True((await w.Pricing.SaveServicePriceAsync(Ctx(), gen, 17m, 7)).Ok);
			var vitd = await ServiceAsync(w, "LAB-VITD", 12m);
			foreach (var id in new[] { a, b })
			{
				var v = (await w.Pricing.ListAsync(Ctx(), id))!;
				var line = v.Lines.Single(l => l.Entry.Code == "CONS-GEN");
				Assert.True(line.Stale);                                              // not updated on every list
				Assert.Equal(id == a ? 14.5m : 13.6m, line.Price);                   // and the rule already follows 17
				Assert.Equal("LAB-VITD", Assert.Single(v.Pending).Code);             // new — not added
				Assert.DoesNotContain(v.Lines, l => l.Entry.Code == "LAB-VITD");
			}
			Assert.True((await w.Pricing.AddLinesAsync(Ctx(), a, new[] { "s" + vitd }, Now)).Ok);
			var after = (await w.Pricing.ListAsync(Ctx(), a))!;
			Assert.True(after.Lines.Single(l => l.Entry.Code == "LAB-VITD").Stale);   // added, but not updated until reviewed
			Assert.Empty(after.Pending);
			Assert.Single((await w.Pricing.ListAsync(Ctx(), b))!.Pending);           // the other list is untouched
		}

		[Fact]
		public async Task A_medicine_never_exceeds_its_public_price_by_rule_or_by_hand()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			var (para, augm, _) = await InventoryAsync(w);
			Assert.True((await w.Pricing.SavePublicPriceAsync(Ctx(), augm, 3.750m, 7)).Ok);
			Assert.Equal("Only a medicine has a public price", (await w.Pricing.SavePublicPriceAsync(Ctx(), 73, 0.1m, 7)).Error);
			var id = (await w.Pricing.CreateAsync(Ctx(), ListInput(med: 10), 7, Now)).Id!.Value;   // a 10% mark-up on medicines

			var v = (await w.Pricing.ListAsync(Ctx(), id))!;
			var aug = v.Lines.Single(l => l.Entry.ItemId == augm);
			Assert.Equal((3.750m, "ceiling"), (aug.Price!.Value, aug.Source));
			Assert.Equal((0.825m, "rule"), (v.Lines.Single(l => l.Entry.ItemId == para).Price!.Value, v.Lines.Single(l => l.Entry.ItemId == para).Source));
			Assert.Equal("Above the medicine's public price ({0})", (await w.Pricing.SaveLineAsync(Ctx(), aug.Line!.ID, new HospitalPriceLineInput { FixedPrice = 3.8m }, 7, Now)).Error);
			Assert.True((await w.Pricing.SaveLineAsync(Ctx(), aug.Line!.ID, new HospitalPriceLineInput { FixedPrice = 3.5m }, 7, Now)).Ok);
		}

		/// A signed visit for a patient on `listId` with the given orders. Returns the bill id.
		private static async Task<int> SignedVisitAsync(World w, int? listId, params int[] orders)
		{
			var ctx = Ctx();
			Assert.True((await w.Consult.SeedDefaultCodesAsync(ctx)).Ok);
			// The doctor is billed as CONS-IM; the test creates it before the list so the list holds it.
			var fee = await w.Host.Db.HospitalServices.Where(s => s.Code == "CONS-IM").Select(s => (int?)s.ID).FirstOrDefaultAsync() ?? await ServiceAsync(w, "CONS-IM", 20m, HospitalServiceCategories.Consultation);
			var doc = (await w.Op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د", NameEn = "Dr", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600, ConsultationServiceId = fee }, 7)).Id!.Value;
			var saved = await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M", PriceListId = listId }, 7, Now);
			Assert.True(saved.Ok, saved.Error);
			var appt = (await w.Op.BookAsync(ctx, new HospitalBookingInput { PatientId = saved.Id!.Value, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			Assert.True((await w.Op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var enc = (await w.Consult.OpenAsync(ctx, appt, 7, Now)).Id!.Value;
			var signed = await w.Consult.SignAsync(ctx, enc, new HospitalEncounterInput { ChiefComplaint = "Fever", Diagnoses = new() { new() { Code = "J02.9", Primary = true } }, OrderServiceIds = orders.ToList() }, 7, Now);
			Assert.True(signed.Ok, signed.Error);
			return await w.Host.Db.HospitalBills.Where(b => b.EncounterId == enc).Select(b => b.ID).SingleAsync();
		}

		[Fact]
		public async Task A_bill_is_priced_from_the_patients_approved_list_and_an_excluded_service_from_the_main_list()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			var cbc = await ServiceAsync(w, "LAB-CBC", 6m);
			var stress = await ServiceAsync(w, "PROC-STRESS", 60m, HospitalServiceCategories.Procedure);
			await ServiceAsync(w, "CONS-IM", 20m, HospitalServiceCategories.Consultation);
			var id = (await w.Pricing.CreateAsync(Ctx(), ListInput(), 7, Now)).Id!.Value;
			var stressLine = (await w.Pricing.ListAsync(Ctx(), id))!.Lines.Single(l => l.Entry.ServiceId == stress).Line!.ID;
			Assert.True((await w.Pricing.SaveLineAsync(Ctx(), stressLine, new HospitalPriceLineInput { IsExcluded = true }, 7, Now)).Ok);

			// a draft list is refused on the patient, and never prices a bill
			Assert.Equal("Choose an approved price list", (await w.Patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M", PriceListId = id }, 7, Now)).Error);
			Assert.True((await w.Pricing.ApproveLinesAsync(Ctx(), id, null, 7, Now)).Ok);
			Assert.True((await w.Pricing.ApproveListAsync(Ctx(), id, 7, Now)).Ok);

			var billId = await SignedVisitAsync(w, id, cbc, stress);
			var v = (await w.Billing.BillAsync(Ctx(), billId, Now))!;
			Assert.Equal(id, v.Bill.PriceListId);
			Assert.Null(v.Bill.CurrencyId);
			Assert.Equal(new[] { ("CONS-IM", 17m, false), ("LAB-CBC", 5m, false), ("PROC-STRESS", 60m, true) }, v.Lines.Select(l => (l.Code, l.UnitPrice, l.OutsideList)).ToArray());
		}

		[Fact]
		public async Task A_foreign_currency_bill_posts_in_the_facility_currency_and_a_new_rate_on_payment_reaches_the_fx_account()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			var cbc = await ServiceAsync(w, "LAB-CBC", 6m);
			await ServiceAsync(w, "CONS-IM", 20m, HospitalServiceCategories.Consultation);
			var sar = await ApprovedListAsync(w, ListInput(currency: Sar, svc: -12, step: 5));

			var billId = await SignedVisitAsync(w, sar, cbc);
			var v = (await w.Billing.BillAsync(Ctx(), billId, Now))!;
			Assert.Equal(Sar, v.Bill.CurrencyId);
			Assert.Equal("SAR", v.Currency!.Code);
			// 20 KWD ÷ 0.082 × 0.88 = 214.6 → 215; 6 KWD → 64.4 → 65
			Assert.Equal(new[] { 215m, 65m }, v.Lines.Select(l => l.UnitPrice).ToArray());

			Assert.True((await w.Billing.OpenShiftAsync(Ctx(), "Desk", 0m, 7, Now)).Ok);
			Assert.Equal("A foreign-currency bill is collected after it is closed", (await w.Billing.CollectAsync(Ctx(), billId, 10m, HospitalPaymentMethods.Cash, 7, Now)).Error);
			Assert.True((await w.Billing.CloseAsync(Ctx(), billId, 7, Now)).Ok);
			var close = Assert.Single(w.Ledger.Posted).Lines;
			Assert.Equal(22.96m, close.Single(l => l.AccountId == A(Receivable)).Debit);   // 280 SAR × 0.082, in dinars
			Assert.Equal(0.082m, (await host.Db.HospitalBills.AsNoTracking().SingleAsync(b => b.ID == billId)).ExchangeRate);

			w.Rates.Kwd[Sar] = 0.080m;                                                  // the riyal weakens before the patient pays
			Assert.True((await w.Billing.CollectAsync(Ctx(), billId, 280m, HospitalPaymentMethods.Cash, 7, Now)).Ok);
			var pay = w.Ledger.Posted[1].Lines;
			Assert.Equal(22.4m, pay.Single(l => l.AccountId == A(Cash)).Debit);        // what came in
			Assert.Equal(22.96m, pay.Single(l => l.AccountId == A(Receivable)).Credit); // what was owed
			Assert.Equal(0.56m, pay.Single(l => l.AccountId == A(Fx)).Debit);          // the loss
			Assert.Equal(22.4m, (await host.Db.HospitalPayments.AsNoTracking().SingleAsync()).BaseAmount);
			Assert.Equal(22.4m, (await w.Billing.ShiftSummaryAsync(Ctx(), 1))!.CashReceipts);   // the till counts dinars
		}

		[Fact]
		public async Task Another_companys_list_is_never_reached()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			await ServiceAsync(w, "LAB-CBC", 6m);
			var id = await ApprovedListAsync(w, ListInput());
			var two = Ctx(7, CompanyTwo);
			Assert.Null(await w.Pricing.ListAsync(two, id));
			Assert.Equal("not_found", (await w.Pricing.ApproveLinesAsync(two, id, null, 7, Now)).Error);
			Assert.Equal("not_found", (await w.Pricing.CopyVersionAsync(two, id, 7, Now)).Error);
			Assert.Equal("Choose an approved price list", (await w.Patients.SaveAsync(two, new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M", PriceListId = id }, 7, Now)).Error);
			Assert.Empty(await w.Pricing.ListsAsync(two));
		}

		[Fact]
		public async Task A_new_version_copies_own_prices_goes_back_to_review_and_replaces_the_old_one_on_approval()
		{
			using var host = new PlatformTestHost(); var w = await MakeAsync(host);
			var mri = await ServiceAsync(w, "RAD-MRI", 120m, HospitalServiceCategories.Radiology);
			var v1 = (await w.Pricing.CreateAsync(Ctx(), ListInput(), 7, Now)).Id!.Value;
			var line = (await w.Pricing.ListAsync(Ctx(), v1))!.Lines.Single().Line!.ID;
			Assert.True((await w.Pricing.SaveLineAsync(Ctx(), line, new HospitalPriceLineInput { FixedPrice = 100m }, 7, Now)).Ok);
			Assert.True((await w.Pricing.ApproveListAsync(Ctx(), v1, 7, Now)).Ok);
			var pat = (await w.Patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M", PriceListId = v1 }, 7, Now)).Id!.Value;

			var v2 = (await w.Pricing.CopyVersionAsync(Ctx(), v1, 7, Now)).Id!.Value;
			Assert.Equal("A draft version of this list already exists", (await w.Pricing.CopyVersionAsync(Ctx(), v1, 7, Now)).Error);
			var copy = (await w.Pricing.ListAsync(Ctx(), v2))!;
			Assert.Equal((2, 100m, true), (copy.List.Version, copy.Lines.Single().Price!.Value, copy.Lines.Single().Stale));
			Assert.True((await w.Pricing.ApproveLinesAsync(Ctx(), v2, null, 7, Now)).Ok);
			Assert.True((await w.Pricing.ApproveListAsync(Ctx(), v2, 7, Now)).Ok);
			host.Db.ChangeTracker.Clear();
			Assert.Equal(HospitalPriceListStatus.Archived, (await host.Db.HospitalPriceLists.SingleAsync(l => l.ID == v1)).Status);
			Assert.Equal(v2, (await host.Db.HospitalPatients.SingleAsync(p => p.ID == pat)).PriceListId);   // patients move to the new version
		}
	}
}
