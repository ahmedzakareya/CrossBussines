using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H40 — intensive care, with no database: which hours may be entered and with what values, which days
	// may be assessed, the flowsheet's columns, and the model against its slice (the CHECKs say what the rules say).
	// ==========================================================================================
	public class HospitalIcuTests
	{
		private static readonly DateTime Now = new(2026, 11, 5, 12, 20, 0);

		[Fact]
		public void An_hour_that_has_begun_within_a_day_is_entered()
		{
			Assert.Null(HospitalIcuRules.HourRefusal(new DateTime(2026, 11, 5, 12, 0, 0), Now, 102, 73, 95, 20, 37.5m, 50));
			Assert.Null(HospitalIcuRules.HourRefusal(new DateTime(2026, 11, 4, 13, 0, 0), Now, 102, null, null, null, null, null));
			Assert.Equal("An hour is entered once it has begun", HospitalIcuRules.HourRefusal(new DateTime(2026, 11, 5, 13, 0, 0), Now, 102, null, null, null, null, null));
			Assert.Equal("Only the last 24 hours are entered here", HospitalIcuRules.HourRefusal(new DateTime(2026, 11, 4, 12, 0, 0), Now, 102, null, null, null, null, null));
			Assert.Equal("Enter at least one value", HospitalIcuRules.HourRefusal(new DateTime(2026, 11, 5, 12, 0, 0), Now, null, null, null, null, null, null));
		}

		[Theory]
		[InlineData(19, null, null, null, null, null)]
		[InlineData(null, 201, null, null, null, null)]
		[InlineData(null, null, 101, null, null, null)]
		[InlineData(null, null, null, 3, null, null)]
		[InlineData(null, null, null, null, 45.1, null)]
		[InlineData(null, null, null, null, null, -1)]
		public void A_value_a_monitor_cannot_read_is_refused(int? hr, int? map, int? spo2, int? rr, double? temp, int? urine) =>
			Assert.Equal("A value is outside what a monitor can read", HospitalIcuRules.HourRefusal(new DateTime(2026, 11, 5, 12, 0, 0), Now, hr, map, spo2, rr, (decimal?)temp, urine));

		[Fact]
		public void The_rules_ranges_are_the_slices_checks()
		{
			var s = Slice();
			foreach (var (col, (_, min, max)) in new[] { "HeartRate", "Map", "SpO2", "RespRate", "TempC", "UrineMl" }.Zip(HospitalIcuRules.Ranges))
				Assert.Contains($"{col} BETWEEN {min:0} AND {max:0}", s);
		}

		[Fact]
		public void A_day_of_the_stay_is_assessed_and_SOFA_is_0_to_24()
		{
			DateTime today = Now.Date, admitted = Now.Date.AddDays(-3);
			Assert.Null(HospitalIcuRules.AssessRefusal(today, today, admitted, 7));
			Assert.Null(HospitalIcuRules.AssessRefusal(today.AddDays(-1), today, admitted, null));
			Assert.Equal("A day is assessed once it has begun", HospitalIcuRules.AssessRefusal(today.AddDays(1), today, admitted, 7));
			Assert.Equal("That day is before the admission", HospitalIcuRules.AssessRefusal(admitted.AddDays(-1), today, admitted, 7));
			Assert.Equal("SOFA is between 0 and 24", HospitalIcuRules.AssessRefusal(today, today, admitted, 25));
			Assert.Equal("SOFA is between 0 and 24", HospitalIcuRules.AssessRefusal(today, today, admitted, -1));
		}

		[Fact]
		public void The_flowsheet_ends_with_the_current_hour()
		{
			var h = HospitalIcuRules.Hours(Now, 6);
			Assert.Equal(6, h.Count);
			Assert.Equal(new DateTime(2026, 11, 5, 12, 0, 0), h[^1]);
			Assert.Equal(new DateTime(2026, 11, 5, 7, 0, 0), h[0]);
			Assert.Equal(new DateTime(2026, 11, 4, 23, 0, 0), HospitalIcuRules.Hours(new DateTime(2026, 11, 5, 0, 10, 0), 2)[0]);   // across midnight
		}

		private static string Slice() => HospitalSliceParity.Slice("hospital_h40_icu.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalIcuModel.Configure);

		[Fact]
		public void The_tables_match_their_slice()
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalIcuObservation), "HospitalIcuObservations");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalIcuObservation), "HospitalIcuObservations");
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalIcuAssessment), "HospitalIcuAssessments");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalIcuAssessment), "HospitalIcuAssessments");
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(Slice()).OrderBy(x => x));
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
			Assert.Contains("Sofa BETWEEN 0 AND 24", Slice());
			Assert.Equal(new[] { "AdmissionId", "HourAt" }, Model().FindEntityType(typeof(HospitalIcuObservation))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
			Assert.Equal(new[] { "AdmissionId", "Day" }, Model().FindEntityType(typeof(HospitalIcuAssessment))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
		}
	}
}
