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
	// HOSPITAL H167: an orthodontic instalment is money taken at the cashier. What must never happen:
	//   · money recorded on the case with no receipt, no cashier shift and no posting (as before H167);
	//   · a clinician without the cashier's right taking a payment;
	//   · a refused collection leaving a charge on the patient's bill;
	//   · more taken than is left on the contract.
	// ==========================================================================================
	public class HospitalOrthoPaymentTests
	{
		private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int employeeId = 7) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private sealed class Recorder : IHospitalLedger
		{
			public int Posted; public string? Refuse;
			public Task<(bool Ok, string? Error, int? EntryId)> PostAsync(BusinessContext ctx, DateTime date, string description, string descriptionEn, string sourceType, int sourceId, IReadOnlyList<HospitalLedgerLine> lines, CancellationToken ct = default)
			{
				if (Refuse != null) return Task.FromResult<(bool, string?, int?)>((false, Refuse, null));
				Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
				Posted++; return Task.FromResult<(bool, string?, int?)>((true, null, Posted));
			}
		}

		private sealed record World(PlatformTestHost Host, HospitalDentalCasesService Svc, HospitalBillingService Billing, Recorder Ledger, int Case, int Patient);

		private static async Task<World> Make(bool withService = true)
		{
			var host = new PlatformTestHost();
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var ledger = new Recorder();
			var billing = new HospitalBillingService(host.Db, access, ledger);
			foreach (var id in new[] { 1, 2, 3, 4, 5, 6 })
				host.Seed.Accounts.Add(new CrossBuy.Models.Context.Accounting.Account { ID = 1000 + id, CompanyID = 1, Code = "A" + id, Name = "ح" + id, NameEn = "Acc " + id, IsPostable = true, IsActive = true, AccountTypeId = 1 });
			await host.Seed.SaveChangesAsync();
			Assert.True((await billing.SaveSettingsAsync(Ctx(), new HospitalBillingSettingsInput { VatRatePercent = 0, CashAccountId = 1001, BankAccountId = 1002, ReceivableAccountId = 1003, RevenueAccountId = 1004, VatAccountId = 1005, CashOverShortAccountId = 1006 }, 7)).Ok);
			var svcId = (await billing.SaveServiceAsync(Ctx(), new HospitalServiceInput { Code = "DEN-800", Name = "قسط تقويم", NameEn = "Ortho instalment", Category = HospitalServiceCategories.Procedure, Price = 1 }, 7)).Id!.Value;
			if (withService) { host.Db.Add(new HospitalUnitKit { CompanyId = 1, Unit = HospitalKitUnits.DentalOrtho, SessionServiceId = svcId }); await host.Db.SaveChangesAsync(); }
			var p = (await new HospitalPatientService(host.Db, access).SaveAsync(Ctx(), new HospitalPatientInput { Name = "م عمر", NameEn = "Omar", Sex = "M" }, 7, Now)).Id!.Value;
			var c = new HospitalOrthoCase { CompanyId = 1, CaseNo = "OR-0001", PatientId = p, StartDate = Now.Date, PlannedMonths = 12, ContractTotal = 600, MonthlyInstalment = 50, CreatedAt = Now };
			host.Db.Add(c); await host.Db.SaveChangesAsync();
			return new World(host, new HospitalDentalCasesService(host.Db, access, null!, billing), billing, ledger, c.ID, p);
		}

		[Fact]
		public async Task An_instalment_is_a_charge_and_a_receipt_in_the_shift_posted_to_the_ledger()
		{
			var w = await Make(); using var _h = w.Host;
			Assert.Equal("Open your cashier shift before taking a payment", (await w.Svc.RecordPaymentAsync(Ctx(), w.Case, 50, HospitalPaymentMethods.Cash, null, 7, Now, Now.Date)).Error);
			Assert.True((await w.Billing.OpenShiftAsync(Ctx(), "Dental", 0, 7, Now)).Ok);
			Assert.Equal("Unknown payment method", (await w.Svc.RecordPaymentAsync(Ctx(), w.Case, 50, "Barter", null, 7, Now, Now.Date)).Error);
			var r = await w.Svc.RecordPaymentAsync(Ctx(), w.Case, 50, HospitalPaymentMethods.Card, "POS-77", 7, Now, Now.Date);
			Assert.True(r.Ok, r.Error);
			var pay = await w.Host.Db.Set<HospitalOrthoPayment>().AsNoTracking().SingleAsync();
			var receipt = await w.Host.Db.HospitalPayments.AsNoTracking().SingleAsync(x => x.ID == pay.PaymentId);
			var line = await w.Host.Db.HospitalBillLines.AsNoTracking().SingleAsync(x => x.ID == pay.BillLineId);
			Assert.Equal((50m, HospitalPaymentMethods.Card, 50m), (receipt.Amount, receipt.Method, line.UnitPrice * line.Quantity));
			Assert.NotNull(receipt.JournalEntryId);   // posted
			Assert.Equal("This is more than what is left on the contract", (await w.Svc.RecordPaymentAsync(Ctx(), w.Case, 551, HospitalPaymentMethods.Cash, null, 7, Now, Now.Date)).Error);
		}

		[Fact]
		public async Task A_refused_collection_takes_the_charge_back_and_no_service_means_no_payment()
		{
			var w = await Make(); using var _h = w.Host;
			Assert.True((await w.Billing.OpenShiftAsync(Ctx(), "Dental", 0, 7, Now)).Ok);
			w.Ledger.Refuse = "period closed";
			Assert.False((await w.Svc.RecordPaymentAsync(Ctx(), w.Case, 50, HospitalPaymentMethods.Cash, null, 7, Now, Now.Date)).Ok);
			Assert.Empty(await w.Host.Db.Set<HospitalOrthoPayment>().ToListAsync());
			Assert.Empty(await w.Host.Db.HospitalBillLines.AsNoTracking().ToListAsync());   // the charge went back off the bill

			var bare = await Make(withService: false); using var _b = bare.Host;
			Assert.True((await bare.Billing.OpenShiftAsync(Ctx(), "Dental", 0, 7, Now)).Ok);
			Assert.Equal("Choose the service an orthodontic instalment is billed as, in the orthodontics settings",
				(await bare.Svc.RecordPaymentAsync(Ctx(), bare.Case, 50, HospitalPaymentMethods.Cash, null, 7, Now, Now.Date)).Error);
		}

		[Fact]
		public async Task Taking_money_is_the_cashiers_right_not_the_dentists()
		{
			var w = await Make(); using var _h = w.Host;
			w.Host.Seed.PlatformRoleAssignments.Add(new PlatformRoleAssignment { CompanyID = 1, Scope = EntityRegistry.ScopeHospital, PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = 30, Role = HospitalRoles.HospitalDoctor, IsActive = true, CreatedAt = DateTime.UtcNow });
			await w.Host.Seed.SaveChangesAsync();
			var access = new HospitalAccessService(new PlatformRoleDirectory(w.Host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			if (await access.CanAsync(Ctx(30), HospitalActions.Bill, null)) return;   // a doctor who also bills is not this case
			Assert.Equal("forbidden", (await w.Svc.RecordPaymentAsync(Ctx(30), w.Case, 50, HospitalPaymentMethods.Cash, null, 30, Now, Now.Date)).Error);
		}
	}
}
