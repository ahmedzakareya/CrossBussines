using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H143: the patient's deposit at the counter. What must never happen:
	//   · a deposit taken with no liability account to hold it (the owner maps it; unmapped = refused);
	//   · a deposit spent twice, or used for more than a bill's due, or refunded beyond what is left;
	//   · a refund without a reason;
	//   · the till blind to deposits (a shift that took a deposit in cash must expect it in the drawer, and a refund out
	//     of it), or a deposit used on a bill counted again as money in;
	//   · another company's deposit reached by id.
	// The ledger is a recorder: every posting is asserted line by line.
	// ==========================================================================================
	public class HospitalDepositTests
	{
		private const int Cash = 101, Bank = 102, Receivable = 103, Revenue = 104, Vat = 106, OverShort = 107, Deposits = 108;
		private static readonly DateTime Today = new(2026, 11, 15);
		private static readonly DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
		private static int A(int id, int companyId = 1) => id + companyId * 1000;
		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private sealed class Recorder : IHospitalLedger
		{
			public readonly List<(string Source, IReadOnlyList<HospitalLedgerLine> Lines)> Posted = new();
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
			{
				Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
				Posted.Add((sourceType, lines));
				return Task.FromResult<(bool, string?, int?)>((true, null, Posted.Count));
			}
		}

		private sealed record World(PlatformTestHost Host, Recorder Ledger, HospitalBillingService Billing, HospitalDepositService Deposits, HospitalConsultationService Consult, HospitalOutpatientService Op, HospitalPatientService Patients);

		private static World Make(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var ledger = new Recorder(); var billing = new HospitalBillingService(host.Db, access, ledger);
			return new World(host, ledger, billing, new HospitalDepositService(host.Db, access, ledger, billing), new HospitalConsultationService(host.Db, access, billing), new HospitalOutpatientService(host.Db, access), new HospitalPatientService(host.Db, access));
		}

		private static async Task Books(World w, bool depositAccount = true, int companyId = 1)
		{
			foreach (var id in new[] { Cash, Bank, Receivable, Revenue, Vat, OverShort, Deposits })
				w.Host.Seed.Accounts.Add(new CrossBuy.Models.Context.Accounting.Account { ID = A(id, companyId), CompanyID = companyId, Code = "A" + id, Name = "ح" + id, NameEn = "Acc " + id, IsPostable = true, IsActive = true, AccountTypeId = 1 });
			await w.Host.Seed.SaveChangesAsync();
			var r = await w.Billing.SaveSettingsAsync(Ctx(companyId), new HospitalBillingSettingsInput
			{ VatRatePercent = 0, CashAccountId = A(Cash, companyId), BankAccountId = A(Bank, companyId), ReceivableAccountId = A(Receivable, companyId), RevenueAccountId = A(Revenue, companyId), VatAccountId = A(Vat, companyId), CashOverShortAccountId = A(OverShort, companyId), PatientDepositAccountId = depositAccount ? A(Deposits, companyId) : null }, 7);
			Assert.True(r.Ok, r.Error);
		}

		/// A patient with a signed visit whose bill holds a 20.000 consultation. Returns (patient, bill).
		private static async Task<(int Patient, int Bill)> Visit(World w)
		{
			var ctx = Ctx();
			Assert.True((await w.Consult.SeedDefaultCodesAsync(ctx)).Ok);
			var fee = (await w.Billing.SaveServiceAsync(ctx, new HospitalServiceInput { Code = "CONS-IM", Name = "ك", NameEn = "Cons", Category = HospitalServiceCategories.Consultation, Price = 20m }, 7)).Id!.Value;
			var doc = (await w.Op.SavePractitionerAsync(ctx, new HospitalPractitionerInput { Name = "د", NameEn = "Dr", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 600, ConsultationServiceId = fee }, 7)).Id!.Value;
			var pat = (await w.Patients.SaveAsync(ctx, new HospitalPatientInput { Name = "م", NameEn = "P", Sex = "M" }, 7, Now)).Id!.Value;
			var appt = (await w.Op.BookAsync(ctx, new HospitalBookingInput { PatientId = pat, PractitionerId = doc, Date = Today, StartMinute = 540 }, 7, Today)).Id!.Value;
			Assert.True((await w.Op.MoveAsync(ctx, appt, HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var enc = (await w.Consult.OpenAsync(ctx, appt, 7, Now)).Id!.Value;
			Assert.True((await w.Consult.SignAsync(ctx, enc, new HospitalEncounterInput { ChiefComplaint = "Fever", Diagnoses = new() { new() { Code = "J02.9", Primary = true } } }, 7, Now)).Ok);
			return (pat, await w.Host.Db.HospitalBills.Where(b => b.EncounterId == enc).Select(b => b.ID).SingleAsync());
		}

		[Fact]
		public void Draw_takes_the_oldest_deposits_first_and_refuses_what_they_do_not_hold()
		{
			var held = new[] { (2, Now.AddHours(1), 15m), (1, Now, 10m), (3, Now.AddHours(2), 0m) };
			Assert.Equal(new[] { (1, 10m), (2, 5m) }, HospitalDepositRules.Draw(held, 15m)!.ToArray());
			Assert.Equal(new[] { (1, 4m) }, HospitalDepositRules.Draw(held, 4m)!.ToArray());
			Assert.Null(HospitalDepositRules.Draw(held, 25.5m));
			Assert.Equal("Write the reason for the refund", HospitalDepositRules.RefuseRefund(5m, 10m, HospitalPaymentMethods.Cash, " "));
			Assert.Equal("The refund is more than is left on the deposit", HospitalDepositRules.RefuseRefund(11m, 10m, HospitalPaymentMethods.Cash, "changed plan"));
			Assert.Equal("Unknown payment method", HospitalDepositRules.RefuseTake(5m, HospitalPaymentMethods.Deposit, HospitalDepositPurpose.General));   // "Deposit" is not a way to take money
		}

		[Fact]
		public async Task Without_the_deposits_account_a_deposit_is_refused()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await Books(w, depositAccount: false);
			var (pat, _) = await Visit(w);
			Assert.True((await w.Billing.OpenShiftAsync(Ctx(), "Desk", 0m, 7, Now)).Ok);
			Assert.Equal("Map the patient deposits account in billing settings", (await w.Deposits.TakeAsync(Ctx(), pat, 50m, HospitalPaymentMethods.Cash, HospitalDepositPurpose.Procedure, null, 7, Now)).Error);
			Assert.Empty(await host.Db.Set<HospitalPatientDeposit>().ToListAsync());
		}

		[Fact]
		public async Task Take_use_and_refund_post_against_the_deposits_account_and_the_till_follows_the_money()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await Books(w);
			var (pat, bill) = await Visit(w);
			Assert.Equal("Open your cashier shift before taking a payment", (await w.Deposits.TakeAsync(Ctx(), pat, 50m, HospitalPaymentMethods.Cash, HospitalDepositPurpose.Procedure, null, 7, Now)).Error);
			var shift = (await w.Billing.OpenShiftAsync(Ctx(), "Desk", 10m, 7, Now)).Id!.Value;

			// take 50 in cash
			var dep = (await w.Deposits.TakeAsync(Ctx(), pat, 50m, HospitalPaymentMethods.Cash, HospitalDepositPurpose.Procedure, "MRI next week", 7, Now)).Id!.Value;
			var (src, lines) = w.Ledger.Posted[^1];
			Assert.Equal("HospitalPatientDeposit", src);
			Assert.Equal(new[] { (A(Cash), 50m, 0m), (A(Deposits), 0m, 50m) }, lines.Select(l => (l.AccountId, l.Debit, l.Credit)).ToArray());
			var s1 = (await w.Billing.ShiftSummaryAsync(Ctx(), shift))!;
			Assert.Equal((60m, 1), (s1.Expected, s1.Receipts));

			// use it on the bill: never more than the due, and no money moves
			Assert.Equal("The amount is more than the patient owes ({0})", (await w.Deposits.UseAsync(Ctx(), bill, 25m, 7, Now)).Error);
			Assert.True((await w.Deposits.UseAsync(Ctx(), bill, 20m, 7, Now)).Ok);
			Assert.Equal(new[] { (A(Deposits), 20m, 0m), (A(Receivable), 0m, 20m) }, w.Ledger.Posted[^1].Lines.Select(l => (l.AccountId, l.Debit, l.Credit)).ToArray());
			Assert.Equal(0m, (await w.Billing.BillAsync(Ctx(), bill, Now))!.Totals.Due);
			var pay = await host.Db.HospitalPayments.AsNoTracking().SingleAsync(p => p.BillId == bill);
			Assert.Equal((HospitalPaymentMethods.Deposit, 20m), (pay.Method, pay.Amount));
			var s2 = (await w.Billing.ShiftSummaryAsync(Ctx(), shift))!;
			Assert.Equal((60m, 1), (s2.Expected, s2.Receipts));   // the drawer did not change
			Assert.Equal(30m, await w.Deposits.LeftAsync(Ctx(), pat));

			// refund what is left, with a reason; never more
			Assert.Equal("The refund is more than is left on the deposit", (await w.Deposits.RefundAsync(Ctx(), dep, 31m, HospitalPaymentMethods.Cash, "plan changed", 7, Now)).Error);
			Assert.True((await w.Deposits.RefundAsync(Ctx(), dep, 30m, HospitalPaymentMethods.Cash, "plan changed", 7, Now)).Ok);
			Assert.Equal(new[] { (A(Deposits), 30m, 0m), (A(Cash), 0m, 30m) }, w.Ledger.Posted[^1].Lines.Select(l => (l.AccountId, l.Debit, l.Credit)).ToArray());
			Assert.Equal(30m, (await w.Billing.ShiftSummaryAsync(Ctx(), shift))!.Expected);   // 10 float + 50 in − 30 out
			Assert.Equal(0m, await w.Deposits.LeftAsync(Ctx(), pat));
			Assert.Equal("The refund is more than is left on the deposit", (await w.Deposits.RefundAsync(Ctx(), dep, 1m, HospitalPaymentMethods.Cash, "again", 7, Now)).Error);
		}

		[Fact]
		public async Task A_bill_is_paid_from_several_deposits_oldest_first_and_never_beyond_what_they_hold()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await Books(w);
			var (pat, bill) = await Visit(w);
			await w.Billing.OpenShiftAsync(Ctx(), "Desk", 0m, 7, Now);
			var first = (await w.Deposits.TakeAsync(Ctx(), pat, 8m, HospitalPaymentMethods.Card, HospitalDepositPurpose.General, null, 7, Now)).Id!.Value;
			var second = (await w.Deposits.TakeAsync(Ctx(), pat, 5m, HospitalPaymentMethods.Cash, HospitalDepositPurpose.General, null, 7, Now.AddMinutes(5))).Id!.Value;
			Assert.Equal("The deposits hold less than that ({0})", (await w.Deposits.UseAsync(Ctx(), bill, 14m, 7, Now)).Error);
			Assert.True((await w.Deposits.UseAsync(Ctx(), bill, 10m, 7, Now)).Ok);
			var d = await host.Db.Set<HospitalPatientDeposit>().AsNoTracking().ToDictionaryAsync(x => x.ID);
			Assert.Equal((8m, 2m), (d[first].Used, d[second].Used));
			Assert.Equal(2, await host.Db.HospitalPayments.CountAsync(p => p.BillId == bill));
			Assert.Equal(10m, (await w.Billing.BillAsync(Ctx(), bill, Now))!.Totals.Due);
		}

		[Fact]
		public async Task Another_companys_deposit_and_bill_are_not_found()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			await Books(w);
			var (pat, bill) = await Visit(w);
			await w.Billing.OpenShiftAsync(Ctx(), "Desk", 0m, 7, Now);
			var dep = (await w.Deposits.TakeAsync(Ctx(), pat, 50m, HospitalPaymentMethods.Cash, HospitalDepositPurpose.General, null, 7, Now)).Id!.Value;
			Assert.Equal("not_found", (await w.Deposits.RefundAsync(Ctx(2), dep, 5m, HospitalPaymentMethods.Cash, "theirs", 7, Now)).Error);
			Assert.Equal("not_found", (await w.Deposits.UseAsync(Ctx(2), bill, 5m, 7, Now)).Error);
			Assert.Equal("not_found", (await w.Deposits.TakeAsync(Ctx(2), pat, 5m, HospitalPaymentMethods.Cash, HospitalDepositPurpose.General, null, 7, Now)).Error);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_model_names_the_tables_its_slice_creates()
		{
			HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h143_deposits.sql"), typeof(HospitalPatientDeposit), "HospitalPatientDeposits");
			HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h143_deposits.sql"), typeof(HospitalDepositMove), "HospitalDepositMoves");
		}

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
		{
			HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h143_deposits.sql"), typeof(HospitalPatientDeposit), "HospitalPatientDeposits");
			HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h143_deposits.sql"), typeof(HospitalDepositMove), "HospitalDepositMoves");
		}
	}
}
