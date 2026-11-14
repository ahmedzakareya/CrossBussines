using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H115 — reference ranges by who the patient is, with no database: which range fits a patient, the most
	// specific winning; the concept read through it (a copy, its second value untouched); the same haemoglobin flagged
	// differently for a man, a woman and a pregnant woman; the refusals; the table against its slice.
	// ==========================================================================================
	public class HospitalConceptRangeTests
	{
		private const int Y = 365;
		private static HospitalConcept Hb() => new() { ID = 7, Code = "718-7", ValueType = HospitalConceptType.Number, Unit = "g/dL", NormalLow = 12, NormalHigh = 17, CriticalLow = 7 };
		private static readonly HospitalConceptRange Men = new() { ID = 1, ConceptId = 7, Sex = "M", NormalLow = 13, NormalHigh = 17, CriticalLow = 7 };
		private static readonly HospitalConceptRange Women = new() { ID = 2, ConceptId = 7, Sex = "F", NormalLow = 12, NormalHigh = 15.5m, CriticalLow = 7 };
		private static readonly HospitalConceptRange Pregnant = new() { ID = 3, ConceptId = 7, Sex = "F", Pregnancy = HospitalRangePregnancy.Pregnant, NormalLow = 11, NormalHigh = 14, CriticalLow = 7 };
		private static readonly HospitalConceptRange SecondTri = new() { ID = 4, ConceptId = 7, Pregnancy = HospitalRangePregnancy.T2, NormalLow = 10.5m, NormalHigh = 14, CriticalLow = 7 };
		private static readonly HospitalConceptRange Child = new() { ID = 5, ConceptId = 7, AgeFromDays = 6 * 30, AgeToDays = 5 * Y, NormalLow = 11, NormalHigh = 14 };
		private static readonly HospitalConceptRange[] All = { Men, Women, Pregnant, SecondTri, Child };

		[Fact]
		public void The_most_specific_range_that_fits_wins()
		{
			Assert.Same(Men, HospitalRangeRules.Pick(All, new("M", 40 * Y, null)));
			Assert.Same(Women, HospitalRangeRules.Pick(All, new("F", 40 * Y, null)));
			Assert.Same(Pregnant, HospitalRangeRules.Pick(All, new("F", 30 * Y, 1)));    // sex + pregnant (3) beats sex (1)
			Assert.Same(SecondTri, HospitalRangeRules.Pick(All, new("F", 30 * Y, 2)));   // a trimester (4) beats sex + "pregnant" (3)
			Assert.Same(Child, HospitalRangeRules.Pick(new[] { Child }, new("M", 2 * Y, null)));
			Assert.Same(Child, HospitalRangeRules.Pick(All, new("F", 2 * Y, null)));         // an age band (2) beats the women's range (1)
			Assert.Null(HospitalRangeRules.Pick(new[] { Child }, new("M", 6 * Y, null)));       // past the band: the concept's own range
			Assert.Null(HospitalRangeRules.Pick(new[] { Child }, new("M", null, null)));        // an unknown age never fits an age band
			Assert.Null(HospitalRangeRules.Pick(new[] { Pregnant }, new("F", 30 * Y, null)));   // not pregnant
			var notPregnant = new HospitalConceptRange { ID = 9, Sex = "F", Pregnancy = HospitalRangePregnancy.NotPregnant, NormalLow = 12 };
			Assert.Same(notPregnant, HospitalRangeRules.Pick(new[] { notPregnant }, new("F", 30 * Y, null)));
			Assert.Null(HospitalRangeRules.Pick(new[] { notPregnant }, new("F", 30 * Y, 3)));
		}

		[Fact]
		public void The_same_reading_is_flagged_for_who_the_patient_is()
		{
			string F(HospitalPatientFacts f, decimal v) => HospitalConceptRules.Flag(HospitalRangeRules.Effective(Hb(), HospitalRangeRules.Pick(All, f)), v);
			Assert.Equal(HospitalObsFlag.Low, F(new("M", 40 * Y, null), 12.5m));      // anaemic for a man (WHO: < 13)
			Assert.Equal(HospitalObsFlag.Normal, F(new("F", 40 * Y, null), 12.5m));   // normal for a woman
			Assert.Equal(HospitalObsFlag.Normal, F(new("F", 30 * Y, 1), 11.2m));       // normal in pregnancy (WHO: < 11)
			Assert.Equal(HospitalObsFlag.Low, F(new("F", 40 * Y, null), 11.2m));
			Assert.Equal(HospitalObsFlag.CriticalLow, F(new("M", 40 * Y, null), 6.5m));
		}

		[Fact]
		public void The_concept_is_read_through_a_copy()
		{
			var c = new HospitalConcept { ID = 3, Code = "85354-9", ValueType = HospitalConceptType.Panel, NormalHigh = 140, NormalHigh2 = 90, CriticalHigh = 180, CriticalHigh2 = 120 };
			var r = new HospitalConceptRange { Pregnancy = HospitalRangePregnancy.Pregnant, NormalHigh = 135, CriticalHigh = 160 };
			var e = HospitalRangeRules.Effective(c, r);
			Assert.NotSame(c, e);
			Assert.Equal(140m, c.NormalHigh);   // the stored concept is never touched
			Assert.Equal((135m, 160m, 90m, 120m), (e.NormalHigh!.Value, e.CriticalHigh!.Value, e.NormalHigh2!.Value, e.CriticalHigh2!.Value));
			Assert.Same(c, HospitalRangeRules.Effective(c, null));
			// in pregnancy a blood pressure is high from 140/90 (ACOG): the range carries the diastolic's limits too
			var adult = new HospitalConcept { ID = 3, ValueType = HospitalConceptType.Panel, NormalHigh = 129, NormalHigh2 = 79, CriticalHigh = 180, CriticalHigh2 = 120 };   // AHA 2017
			var preg = HospitalRangeRules.Effective(adult, new HospitalConceptRange { Pregnancy = HospitalRangePregnancy.Pregnant, NormalHigh = 139, NormalHigh2 = 89, CriticalHigh = 160, CriticalHigh2 = 110 });
			Assert.Equal(HospitalObsFlag.Normal, HospitalConceptRules.Flag(preg, 132, 85));
			Assert.Equal(HospitalObsFlag.High, HospitalConceptRules.Flag(adult, 132, 85));   // the same reading outside pregnancy
			Assert.Equal(HospitalObsFlag.CriticalHigh, HospitalConceptRules.Flag(preg, 150, 112));
		}

		[Fact]
		public void A_range_must_say_who_and_hold_together()
		{
			var c = Hb();
			HospitalConceptRangeInput Ok() => new() { Sex = "F", NormalLow = 12, NormalHigh = 15.5m };
			string? E(Action<HospitalConceptRangeInput> f) { var i = Ok(); f(i); return HospitalRangeRules.Refusal(i, c); }
			Assert.Null(HospitalRangeRules.Refusal(Ok(), c));
			Assert.Equal("not_found", HospitalRangeRules.Refusal(Ok(), null));
			Assert.Equal("A range applies to a measured number", HospitalRangeRules.Refusal(Ok(), new HospitalConcept { ValueType = HospitalConceptType.Choice }));
			Assert.Equal("A second number's limits are for a pair of values", E(i => i.NormalHigh2 = 80));
			Assert.Equal("Say who the range is for: a sex, an age band or a pregnancy", E(i => i.Sex = null));
			Assert.Equal("Choose the sex", E(i => i.Sex = "X"));
			Assert.Equal("A pregnancy range is for a female patient", E(i => { i.Sex = "M"; i.Pregnancy = HospitalRangePregnancy.T1; }));
			Assert.Equal("Choose the pregnancy state", E(i => i.Pregnancy = "T4"));
			Assert.Equal("Choose the age unit", E(i => { i.AgeFrom = 1; i.AgeUnit = "weeks"; }));
			Assert.Equal("The age band must end after it starts", E(i => { i.AgeFrom = 5; i.AgeTo = 5; i.AgeUnit = "years"; }));
			Assert.Equal("Give at least one limit", E(i => { i.NormalLow = null; i.NormalHigh = null; }));
			Assert.Equal("The normal range's low must be below its high", E(i => i.NormalHigh = 11));
			Assert.Equal("A critical low lies below the normal range", E(i => i.CriticalLow = 12));
			Assert.Equal("A critical high lies above the normal range", E(i => i.CriticalHigh = 15));
			Assert.Equal(183, HospitalRangeRules.ToDays(6, "months"));   // 6 × 30.4375
			Assert.Equal(1826, HospitalRangeRules.ToDays(5, "years"));
			Assert.Equal(10, HospitalRangeRules.ToDays(10, "days"));
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h115_concept_ranges.sql");
			var m = HospitalSliceParity.Model(HospitalClinicalContentModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalConceptRange), "HospitalConceptRanges");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalConceptRange), "HospitalConceptRanges");
			foreach (var p in HospitalRangePregnancy.All) Assert.Contains("'" + p + "'", s);
			Assert.Equal(new[] { "HospitalConcepts" }, HospitalSliceParity.CheckedPrerequisites(s));
		}
	}
}
