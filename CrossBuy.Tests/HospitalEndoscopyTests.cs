using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H217 — endoscopy. What must never happen: a scope used on a patient without a passed reprocessing after
	// its last use, or after the 7-day hang time; a cycle counted as passed without its leak test, manual clean and
	// disinfection; a report closed without findings; a colonoscopy closed without its quality figures; biopsies taken
	// with nowhere written.
	// ==========================================================================================
	public class HospitalEndoscopyTests
	{
		private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
		private static HospitalScopeCycle Cycle(bool passed, DateTime at) => new() { Passed = passed, At = at };

		[Fact]
		public void A_scope_is_ready_only_after_a_passed_cycle_since_its_last_use_and_within_7_days()
		{
			Assert.Equal(HospitalEndoRules.NeverCleaned, HospitalEndoRules.NotReady(null, null, Now));
			Assert.Equal(HospitalEndoRules.LastFailed, HospitalEndoRules.NotReady(Cycle(false, Now.AddHours(-1)), null, Now));
			Assert.Equal(HospitalEndoRules.UsedSince, HospitalEndoRules.NotReady(Cycle(true, Now.AddHours(-2)), Now.AddHours(-1), Now));
			Assert.Equal(HospitalEndoRules.HangExpired, HospitalEndoRules.NotReady(Cycle(true, Now.AddDays(-8)), Now.AddDays(-9), Now));
			Assert.Null(HospitalEndoRules.NotReady(Cycle(true, Now.AddHours(-1)), Now.AddHours(-3), Now));
			Assert.Null(HospitalEndoRules.NotReady(Cycle(true, Now.AddDays(-6)), null, Now));
		}

		[Fact]
		public void A_cycle_passes_only_with_its_leak_test_manual_clean_and_disinfection()
		{
			Assert.True(HospitalEndoRules.CyclePasses(new() { LeakTestPassed = true, ManualClean = true, Agent = "OPA 0.55%", Minutes = 12 }));
			Assert.False(HospitalEndoRules.CyclePasses(new() { LeakTestPassed = false, ManualClean = true, Agent = "OPA", Minutes = 12 }));
			Assert.False(HospitalEndoRules.CyclePasses(new() { LeakTestPassed = true, ManualClean = false, Agent = "OPA", Minutes = 12 }));
			Assert.False(HospitalEndoRules.CyclePasses(new() { LeakTestPassed = true, ManualClean = true, Agent = "OPA", Minutes = 0 }));
			Assert.Equal("Write the disinfectant used", HospitalEndoRules.CycleRefusal(new() { Agent = " ", Minutes = 5 }));
		}

		[Fact]
		public void A_report_has_findings_and_a_colonoscopy_its_quality_figures()
		{
			var gastro = new HospitalEndoscopy { Kind = HospitalEndoKind.Gastroscopy, Status = HospitalEndoStatus.Started };
			Assert.Equal("Write the findings", HospitalEndoRules.CompleteRefusal(gastro, new() { Findings = " " }));
			Assert.Null(HospitalEndoRules.CompleteRefusal(gastro, new() { Findings = "Antral erythema" }));
			Assert.Equal("Write where the biopsies were taken", HospitalEndoRules.CompleteRefusal(gastro, new() { Findings = "x", Biopsies = 2 }));
			var colon = new HospitalEndoscopy { Kind = HospitalEndoKind.Colonoscopy, Status = HospitalEndoStatus.Started };
			Assert.Equal("Say whether the caecum was reached", HospitalEndoRules.CompleteRefusal(colon, new() { Findings = "x" }));
			Assert.Equal("Write the Boston bowel preparation score (0 to 9)", HospitalEndoRules.CompleteRefusal(colon, new() { Findings = "x", CecumReached = true }));
			Assert.Equal("Write the withdrawal time in minutes", HospitalEndoRules.CompleteRefusal(colon, new() { Findings = "x", CecumReached = true, BowelPrepScore = 7 }));
			Assert.Null(HospitalEndoRules.CompleteRefusal(colon, new() { Findings = "x", CecumReached = true, BowelPrepScore = 7, WithdrawalMinutes = 8 }));
			colon.Status = HospitalEndoStatus.Completed;
			Assert.Equal("This procedure is already completed", HospitalEndoRules.CompleteRefusal(colon, new() { Findings = "x" }));
		}

		[Theory]
		[InlineData(true, 6, 6, true)]
		[InlineData(true, 5, 9, false)]   // withdrawn too fast
		[InlineData(false, 9, 9, false)]  // caecum not reached
		[InlineData(true, 8, 5, false)]   // inadequate preparation
		public void A_colonoscopy_meets_quality_when_complete_slow_and_well_prepared(bool cecum, int withdrawal, int prep, bool met) =>
			Assert.Equal(met, HospitalEndoRules.QualityMet(new() { Kind = HospitalEndoKind.Colonoscopy, CecumReached = cecum, WithdrawalMinutes = withdrawal, BowelPrepScore = prep }));

		[Fact]
		public void The_tables_match_their_slice()
		{
			var model = HospitalSliceParity.Model(HospitalEndoscopyModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h217_endoscopy.sql");
			foreach (var (t, n) in new[] { (typeof(HospitalScope), "HospitalScopes"), (typeof(HospitalScopeCycle), "HospitalScopeCycles"), (typeof(HospitalEndoscopy), "HospitalEndoscopies") })
			{
				HospitalSliceParity.NamesItsTable(model, s, t, n);
				HospitalSliceParity.ColumnsMatch(model, s, t, n);
			}
			foreach (var x in HospitalEndoKind.All.Concat(HospitalEndoSedation.All).Concat(HospitalEndoStatus.All)) Assert.Contains("'" + x + "'", s);
		}
	}
}
