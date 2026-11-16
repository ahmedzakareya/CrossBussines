using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H144: the front desk's papers through Report Studio. What must never happen:
	//   · a queue ticket telling the patient a wrong "ahead of you" (only the doctor's arrived patients before them count);
	//   · a wristband that drops an allergy;
	//   · a deposit receipt whose "left" disagrees with the deposit;
	//   · another company's visit, patient or deposit printed by changing the id in the URL.
	// ==========================================================================================
	public class HospitalDeskDocumentTests
	{
		private static readonly DateTime Today = new(2026, 11, 15);
		private static readonly DateTime Now = new(2026, 11, 15, 6, 0, 0, DateTimeKind.Utc);
		private static BusinessContext Ctx(int companyId = 1) => new() { CompanyId = companyId, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private static ReportDataQuery Query(ReportDatasetDefinition ds, BusinessContext ctx, string param, int id) => new()
		{
			Definition = new ReportDefinition
			{
				Code = "test." + ds.DatasetCode, Module = ds.Module, TitleAr = "اختبار", TitleEn = "Test", DataSourceKey = ds.DataSourceKey, PermissionKey = ds.RequiredPermissionKey,
				Columns = ds.Fields.Select(x => new ReportColumn { Key = x.Key, TitleAr = x.TitleAr, TitleEn = x.TitleEn, Type = x.Type }).ToList(),
			},
			Context = ctx,
			Parameters = new ReportParameterSet(new[] { new ReportParameterValue { Key = param, Type = ReportFieldType.Integer, Value = id } }, new Dictionary<string, string?>()),
		};

		private sealed record World(PlatformTestHost Host, HospitalOutpatientService Op, HospitalPatientService Patients);
		private static World Make(PlatformTestHost host)
		{
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			return new World(host, new HospitalOutpatientService(host.Db, access), new HospitalPatientService(host.Db, access));
		}

		private static async Task<int> Patient(World w, string en, string? allergies = null)
		{
			var r = await w.Patients.SaveAsync(Ctx(), new HospitalPatientInput { Name = "م " + en, NameEn = en, Sex = "M", Allergies = allergies, BloodGroup = "O+" }, 7, Now);
			Assert.True(r.Ok, r.Error); return r.Id!.Value;
		}

		[Fact]
		public async Task The_ticket_counts_only_the_doctors_arrived_patients_before_it()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var dr = (await w.Op.SavePractitionerAsync(Ctx(), new HospitalPractitionerInput { Name = "د", NameEn = "Dr", WorkDays = new() { 0 }, SessionStartMinute = 540, SessionEndMinute = 660, SlotMinutes = 15, Privileges = new() { "opd" } }, 7)).Id!.Value;
			var ids = new List<int>();
			foreach (var (en, minute) in new[] { ("Omar", 540), ("Sara", 555), ("Ali", 570) })
			{
				var p = await Patient(w, en);
				var a = (await w.Op.BookAsync(Ctx(), new HospitalBookingInput { PatientId = p, PractitionerId = dr, Date = Today, StartMinute = minute }, 7, Today)).Id!.Value;
				ids.Add(a);
			}
			Assert.True((await w.Op.MoveAsync(Ctx(), ids[0], HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			Assert.True((await w.Op.MoveAsync(Ctx(), ids[1], HospitalAppointmentStatus.Arrived, 7, Now)).Ok);
			var src = new HospitalVisitSlipSource(host.Db);
			var second = await src.FetchAsync(Query(HospitalDocumentDatasets.VisitSlip(), Ctx(), "AppointmentId", ids[1]));
			var row = Assert.Single(second.Rows);
			Assert.Equal((1, "09:15"), (Convert.ToInt32(row["AheadOfYou"]), (string?)row["VisitTime"]));
			Assert.NotNull(row["QueueNumber"]);
			var booked = Assert.Single((await src.FetchAsync(Query(HospitalDocumentDatasets.VisitSlip(), Ctx(), "AppointmentId", ids[2]))).Rows);
			Assert.Null(booked["AheadOfYou"]);   // not arrived: no place in the queue yet
			Assert.Empty((await src.FetchAsync(Query(HospitalDocumentDatasets.VisitSlip(), Ctx(2), "AppointmentId", ids[1]))).Rows);
		}

		[Fact]
		public async Task The_wristband_keeps_every_allergy_and_another_company_prints_nothing()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var p = await Patient(w, "Omar", "Penicillin\nLatex");
			var src = new HospitalWristbandSource(host.Db);
			var row = Assert.Single((await src.FetchAsync(Query(HospitalDocumentDatasets.Wristband(), Ctx(), "PatientId", p))).Rows);
			Assert.Equal("Penicillin · Latex", (string?)row["Allergies"]);
			Assert.NotNull(row["AllergyAlert"]);
			Assert.Equal("O+", (string?)row["BloodGroup"]);
			Assert.Empty((await src.FetchAsync(Query(HospitalDocumentDatasets.Wristband(), Ctx(2), "PatientId", p))).Rows);
		}

		[Fact]
		public async Task The_deposit_receipt_states_what_is_left_as_the_deposit_does()
		{
			using var host = new PlatformTestHost(); var w = Make(host);
			var p = await Patient(w, "Omar");
			var d = new HospitalPatientDeposit { CompanyId = 1, DepositNo = "DEP-000001", PatientId = p, Purpose = HospitalDepositPurpose.Procedure, Amount = 50m, Used = 20m, Refunded = 5m, Method = HospitalPaymentMethods.Cash, ShiftId = 1, CashierEmployeeId = 7, ReceivedAt = Now };
			host.Db.Add(d); await host.Db.SaveChangesAsync();
			var src = new HospitalDepositReceiptSource(host.Db);
			var row = Assert.Single((await src.FetchAsync(Query(HospitalDocumentDatasets.DepositReceipt(), Ctx(), "DepositId", d.ID))).Rows);
			Assert.Equal((50m, 25m, "DEP-000001"), (Convert.ToDecimal(row["Amount"]), Convert.ToDecimal(row["Left"]), (string?)row["DocumentNo"]));
			Assert.Empty((await src.FetchAsync(Query(HospitalDocumentDatasets.DepositReceipt(), Ctx(2), "DepositId", d.ID))).Rows);
		}
	}
}
