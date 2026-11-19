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

		// ---- the designed layouts (owner: "the papers must be the prototype's, not tables") ----

		[Theory]
		[InlineData(HospitalDocumentDatasetCodes.VisitSlip)]
		[InlineData(HospitalDocumentDatasetCodes.Wristband)]
		[InlineData(HospitalDocumentDatasetCodes.DepositReceipt)]
		[InlineData(HospitalDocumentDatasetCodes.RxLabel)]   // H147
		[InlineData(HospitalDocumentDatasetCodes.CreditNote)]   // H149
		[InlineData(HospitalDocumentDatasetCodes.CompoundLabel)]   // H153
		[InlineData(HospitalDocumentDatasetCodes.ReferralLetter)]   // H204
		[InlineData(HospitalDocumentDatasetCodes.SpecimenLabel)]   // H206
		public void Each_designed_paper_passes_the_studio_validator_whole_and_fits_its_paper(string code)
		{
			var dataset = HospitalDocumentDatasets.All().Single(d => d.DatasetCode == code);
			var layout = HospitalDeskLayouts.For(code)!;
			var permitted = dataset.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
			var result = new ReportVisualLayoutValidator().Validate(layout, dataset, permitted, new HashSet<int>());
			Assert.True(result.Ok, string.Join(" | ", result.Errors));
			Assert.Equal(layout.Bands.Sum(b => b.Elements.Count), result.Sanitised!.Bands.Sum(b => b.Elements.Count));   // nothing silently dropped
			double width = ReportPaper.ContentWidthMm(layout.Page);
			Assert.All(layout.Bands.SelectMany(b => b.Elements), e => Assert.True(e.XMm + e.WidthMm <= width + 0.01, $"{e.Kind} {e.FieldKey ?? e.Text} runs off the paper"));
			// every field the design draws is one the dataset gives
			Assert.All(layout.Bands.SelectMany(b => b.Elements).Where(e => e.FieldKey != null), e => Assert.Contains(e.FieldKey!, permitted));
		}

		[Theory]
		[InlineData(HospitalDocumentDatasetCodes.VisitSlip)]
		[InlineData(HospitalDocumentDatasetCodes.Wristband)]
		[InlineData(HospitalDocumentDatasetCodes.DepositReceipt)]
		[InlineData(HospitalDocumentDatasetCodes.RxLabel)]   // H147
		[InlineData(HospitalDocumentDatasetCodes.CreditNote)]   // H149
		[InlineData(HospitalDocumentDatasetCodes.CompoundLabel)]   // H153
		[InlineData(HospitalDocumentDatasetCodes.ReferralLetter)]   // H204
		[InlineData(HospitalDocumentDatasetCodes.SpecimenLabel)]   // H206
		public void No_text_is_given_less_height_than_its_font_needs_and_nothing_overlaps_its_neighbour_below(string code)
		{
			// the owner's "النصوص مقصوصة": Arabic at 9.5pt in a 6 mm box loses its marks
			var layout = HospitalDeskLayouts.For(code)!;
			var texts = layout.Bands.SelectMany(b => b.Elements).Where(e => e.Kind is ReportElementKind.Text or ReportElementKind.Field or ReportElementKind.SystemField).ToList();
			Assert.All(texts, e => Assert.True(e.HeightMm >= HospitalDeskLayouts.Need(e.Style.FontSizePt) - 0.01,
				$"{e.FieldKey ?? e.Text} at {e.Style.FontSizePt}pt has {e.HeightMm}mm, needs {HospitalDeskLayouts.Need(e.Style.FontSizePt)}mm"));
			Assert.All(layout.Bands, b => Assert.All(b.Elements, e => Assert.True(e.YMm + e.HeightMm <= b.HeightMm + 0.01, $"{e.FieldKey ?? e.Text} runs out of its band")));
			// two text boxes that share horizontal space never share vertical space
			foreach (var a in texts)
				foreach (var b in texts.Where(x => !ReferenceEquals(x, a)))
				{
					bool across = a.XMm < b.XMm + b.WidthMm - 0.01 && b.XMm < a.XMm + a.WidthMm - 0.01;
					bool down = a.YMm < b.YMm + b.HeightMm - 0.01 && b.YMm < a.YMm + a.HeightMm - 0.01;
					Assert.False(across && down, $"{a.FieldKey ?? a.Text} overlaps {b.FieldKey ?? b.Text}");
				}
		}

		[Fact]
		public void The_wristband_is_a_band_and_the_slips_are_a_receipt_roll()
		{
			Assert.Equal((280d, 25.4d), ReportPaper.SizeOf(ReportPageSize.Wristband));
			Assert.Equal(ReportPageSize.Wristband, HospitalDeskLayouts.Wristband().Page.PageSize);
			Assert.True(HospitalDeskLayouts.Wristband().Bands.Single().HeightMm <= 25.4 - 4);   // the band never runs to a second label
			Assert.Equal(ReportPageSize.Thermal80, HospitalDeskLayouts.VisitSlip().Page.PageSize);
			Assert.Equal(ReportPageSize.Thermal80, HospitalDeskLayouts.DepositReceipt().Page.PageSize);
			var provider = new HospitalReportDefinitionProvider().GetDefinitions().ToDictionary(d => d.Code);
			Assert.NotNull(provider[HospitalDocumentDatasetCodes.Wristband].ShippedVisual);
			Assert.Null(provider[HospitalDocumentDatasetCodes.Bill].ShippedVisual);   // the other papers keep what they had
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
			static decimal Num(object? v) => decimal.Parse((string)v!, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture);
			Assert.Equal((50m, 25m, "DEP-000001"), (Num(row["Amount"]), Num(row["Left"]), (string?)row["DocumentNo"]));   // text in the currency's decimals
			Assert.Empty((await src.FetchAsync(Query(HospitalDocumentDatasets.DepositReceipt(), Ctx(2), "DepositId", d.ID))).Rows);
		}
	}
}
