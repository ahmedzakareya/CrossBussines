using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H263 — no-show risk. What must never happen: a confirmed patient scored like an unreachable one; a
	// score with no reason shown; a negative score; a band boundary that drifts; calibration counting a case twice.
	// ==========================================================================================
	public class HospitalNoShowTests
	{
		[Fact]
		public void The_points_add_up_from_each_factor()
		{
			Assert.Equal(0, HospitalNoShowRules.Score(new(0, 3, 1, 50, null)));
			Assert.Equal(3 + 3 + 1 + 1, HospitalNoShowRules.Score(new(1, 0, 40, 25, null)));
			Assert.Equal(5 + 2 + 1, HospitalNoShowRules.Score(new(3, 2, 20, 60, HospitalConfirmStatus.NoAnswer)));
			Assert.Equal(0, HospitalNoShowRules.Score(new(0, 2, 1, 50, HospitalConfirmStatus.Confirmed)));   // never below zero
			Assert.True(HospitalNoShowRules.Score(new(1, 0, 20, 25, HospitalConfirmStatus.Confirmed)) < HospitalNoShowRules.Score(new(1, 0, 20, 25, HospitalConfirmStatus.NoAnswer)));
		}

		[Fact]
		public void Bands_and_reasons()
		{
			Assert.Equal(new[] { "Low", "Low", "Medium", "Medium", "High" }, new[] { 0, 2, 3, 5, 6 }.Select(HospitalNoShowRules.Band));
			var why = HospitalNoShowRules.Reasons(new(2, 0, 30, 22, HospitalConfirmStatus.NoAnswer));
			Assert.Equal(new[] { "Missed several earlier appointments", "Booked long in advance", "First visit", "Young adult", "Did not answer the confirmation call" }, why);
			Assert.Empty(HospitalNoShowRules.Reasons(new(0, 4, 3, 60, null)));
		}

		[Fact]
		public void Calibration_counts_each_case_once_in_its_band()
		{
			var cases = new[] { new HospitalRiskCase(0, false), new HospitalRiskCase(1, false), new HospitalRiskCase(4, true), new HospitalRiskCase(7, true), new HospitalRiskCase(8, false) };
			var cal = HospitalNoShowRules.Calibration(cases);
			Assert.Equal(cases.Length, cal.Sum(c => c.Cases));
			Assert.Equal(("High", 2, 1), cal.Single(c => c.Band == "High"));
			Assert.Equal(1m, HospitalReadmissionRules.Auc(new[] { new HospitalRiskCase(7, true), new HospitalRiskCase(1, false) }));
		}
	}
}
