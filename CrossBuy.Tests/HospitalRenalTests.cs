using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H104 — dosing by the kidney, with no database: below "stop" the medicine stops, below "review" the dose is
	// reviewed, a FALLING eGFR within 5 of the review line is shown before it crosses (the prototype's metformin at 47),
	// a steady one is not; no eGFR, no reading; a rule by the kidney alone needs no dose limit.
	// ==========================================================================================
	public class HospitalRenalTests
	{
		private static readonly HospitalDoseLimit Metformin = new() { ID = 1, Drug = "metformin", Unit = "mg", MaxDaily = 2000, RenalReviewBelow = 45, RenalStopBelow = 30, IsActive = true };
		private static readonly HospitalDoseLimit Paracetamol = new() { ID = 2, Drug = "paracetamol", Unit = "mg", MaxSingle = 1000, IsActive = true };
		private static readonly HospitalDoseLimit[] Kb = { Metformin, Paracetamol };
		private static HospitalEgfr E(decimal v, decimal? prev = null) => new(v, DateTime.UtcNow, prev);

		[Fact]
		public void Stop_review_and_approaching()
		{
			Assert.Equal((true, false), Single(HospitalRenalRules.Hits("Metformin 500 mg tablets", null, E(28), Kb)));
			Assert.Equal((false, false), Single(HospitalRenalRules.Hits("Metformin 500 mg tablets", null, E(40), Kb)));
			Assert.Equal((false, true), Single(HospitalRenalRules.Hits("Metformin 500 mg tablets", null, E(47, 55), Kb)));   // the prototype: falling, 47
			Assert.Empty(HospitalRenalRules.Hits("Metformin 500 mg tablets", null, E(47, 45), Kb));                         // rising: not yet
			Assert.Empty(HospitalRenalRules.Hits("Metformin 500 mg tablets", null, E(60, 70), Kb));
			Assert.Empty(HospitalRenalRules.Hits("Metformin 500 mg tablets", null, null, Kb));                               // no eGFR: no reading
			Assert.Empty(HospitalRenalRules.Hits("Paracetamol 500 mg", null, E(10), Kb));                                     // no kidney rule
			Assert.True(E(47, 55).Falling); Assert.False(E(47).Falling);
		}

		private static (bool Stop, bool Approaching) Single(IReadOnlyList<(HospitalDoseLimit Limit, bool Stop, bool Approaching)> hits)
		{ var h = Assert.Single(hits); Assert.Same(Metformin, h.Limit); return (h.Stop, h.Approaching); }

		[Fact]
		public void A_kidney_rule_needs_no_dose_limit_but_must_make_sense()
		{
			Assert.Null(HospitalDrugSafetyRules.LimitRefusal(new HospitalDoseLimitInput { Drug = "metformin", Unit = "mg", RenalReviewBelow = 45, RenalStopBelow = 30 }));
			Assert.Equal("Stopping comes below the review threshold", HospitalDrugSafetyRules.LimitRefusal(new HospitalDoseLimitInput { Drug = "metformin", Unit = "mg", RenalReviewBelow = 30, RenalStopBelow = 45 }));
			Assert.Equal("An eGFR threshold is between 5 and 120", HospitalDrugSafetyRules.LimitRefusal(new HospitalDoseLimitInput { Drug = "metformin", Unit = "mg", RenalStopBelow = 200 }));
			Assert.Equal("Write a single or a daily limit", HospitalDrugSafetyRules.LimitRefusal(new HospitalDoseLimitInput { Drug = "metformin", Unit = "mg" }));
		}
	}
}
