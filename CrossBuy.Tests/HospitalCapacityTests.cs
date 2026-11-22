using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H239 — the capacity centre. What must never happen: an estimate made from too few past stays; an estimate
	// in the past for a patient still in bed; the team's plan overridden by an estimate; a forecast that loses or invents
	// a patient between days, or goes below zero; a plan dated before the admission or in the past; an unknown barrier
	// stored; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalCapacityTests
	{
		private static readonly DateTime Today = new(2026, 11, 22);

		[Fact]
		public void The_median_needs_five_stays()
		{
			Assert.Null(HospitalCapacityRules.MedianDays(new[] { 3, 4, 5, 6 }));
			Assert.Equal(5, HospitalCapacityRules.MedianDays(new[] { 9, 3, 5, 4, 6 }));
			Assert.Equal(5, HospitalCapacityRules.MedianDays(new[] { 3, 4, 5, 6, 7, 2 }));   // (4+5)/2 rounds away from zero
		}

		[Fact]
		public void The_plan_wins_then_the_diagnosis_then_the_house_and_never_in_the_past()
		{
			var adm = Today.AddDays(-2);
			Assert.Equal((Today.AddDays(5), true), HospitalCapacityRules.ExpectedDischarge(Today.AddDays(5), adm, 3, 4, Today));
			Assert.Equal((Today.AddDays(1), false), HospitalCapacityRules.ExpectedDischarge(null, adm, 3, 9, Today));
			Assert.Equal((Today.AddDays(2), false), HospitalCapacityRules.ExpectedDischarge(null, adm, null, 4, Today));
			Assert.Equal((Today, false), HospitalCapacityRules.ExpectedDischarge(null, Today.AddDays(-10), 3, null, Today));   // overdue: still here
			Assert.Null(HospitalCapacityRules.ExpectedDischarge(null, adm, null, null, Today));
		}

		[Fact]
		public void Expected_admissions_are_the_same_weekday_over_eight_weeks()
		{
			// Sunday 2026-11-22: two admissions on each of the last four Sundays, none on other Sundays, noise on Mondays
			var days = Enumerable.Range(1, 4).SelectMany(w => new[] { Today.AddDays(-7 * w), Today.AddDays(-7 * w).AddHours(5) })
				.Concat(new[] { Today.AddDays(-6), Today.AddDays(-13), Today.AddDays(-7 * 9) }).ToList();
			Assert.Equal(1.0m, HospitalCapacityRules.ExpectedAdmissions(days, Today, Today));       // 8 / 8 weeks
			Assert.Equal(0.3m, HospitalCapacityRules.ExpectedAdmissions(days, Today.AddDays(1), Today));   // 2 Mondays / 8
		}

		[Fact]
		public void The_forecast_carries_each_day_into_the_next()
		{
			var f = HospitalCapacityRules.Forecast(Today, 18, 20, new[] { Today, Today, Today.AddDays(1) }, d => d == Today ? 3m : 1m);
			Assert.Equal(4, f.Count);
			Assert.Equal((18, 2, 3, 19), (f[0].Occupied, f[0].Discharges, f[0].Admissions, f[0].Projected));
			Assert.Equal(19, f[1].Occupied); Assert.Equal(19, f[1].Projected);   // −1 +1
			Assert.Equal(20, f[2].Projected);
			Assert.Equal(95m, f[0].OccupancyPct);
			Assert.Equal("Critical", HospitalCapacityRules.Level(f[0].OccupancyPct));
			Assert.Equal("Strain", HospitalCapacityRules.Level(85m));
			Assert.Equal("Normal", HospitalCapacityRules.Level(84m));
			Assert.Equal(0, HospitalCapacityRules.Forecast(Today, 1, 10, new[] { Today, Today, Today }, _ => 0m)[0].Projected);   // never below zero
			Assert.Null(HospitalCapacityRules.Forecast(Today, 0, 0, Array.Empty<DateTime>(), _ => 0m)[0].OccupancyPct);
		}

		[Fact]
		public void A_plan_is_dated_from_today_after_the_admission_with_known_barriers()
		{
			var adm = Today.AddDays(-3);
			Assert.Null(HospitalCapacityRules.PlanRefusal(Today.AddDays(2), Today, adm, HospitalDischargeConfidence.Likely, new[] { HospitalDischargeBarrier.Transport }, null));
			Assert.NotNull(HospitalCapacityRules.PlanRefusal(Today.AddDays(-1), Today, adm, HospitalDischargeConfidence.Likely, null, null));
			Assert.NotNull(HospitalCapacityRules.PlanRefusal(Today.AddDays(61), Today, adm, HospitalDischargeConfidence.Likely, null, null));
			Assert.NotNull(HospitalCapacityRules.PlanRefusal(Today, Today, Today.AddDays(1), HospitalDischargeConfidence.Likely, null, null));
			Assert.NotNull(HospitalCapacityRules.PlanRefusal(Today, Today, adm, "Sure", null, null));
			Assert.Equal("not_found", HospitalCapacityRules.PlanRefusal(Today, Today, adm, HospitalDischargeConfidence.Possible, new[] { "Weather" }, null));
			Assert.Equal("Transport,Insurance", HospitalCapacityRules.BarrierText(new[] { "Insurance", "Transport", "Transport" }));
			Assert.Null(HospitalCapacityRules.BarrierText(Array.Empty<string>()));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalCapacityModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h239_capacity.sql");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalDischargePlan), "HospitalDischargePlans");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalDischargePlan), "HospitalDischargePlans");
		}
	}
}
