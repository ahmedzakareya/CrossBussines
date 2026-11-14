using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H116 — a field alert on the patient's own range, with no database: "TSH out of range in pregnancy" fires
	// for a pregnant woman whose TSH is outside HER range and never for anyone else; a pair is judged on both numbers; a
	// stored reading's flag raises it too; the designer's refusals; the column against its slice.
	// ==========================================================================================
	public class HospitalRangeAlertTests
	{
		// TSH (mIU/L): adult 0.4–4.0; in pregnancy (ATA 2017, no local ranges) 0.1–4.0, with 2.5 the first-trimester target
		private static HospitalConcept Tsh() => new() { ID = 11, Code = "3016-3", ValueType = HospitalConceptType.Number, NormalLow = 0.4m, NormalHigh = 4.0m };
		private static readonly HospitalConceptRange T1 = new() { ID = 1, ConceptId = 11, Pregnancy = HospitalRangePregnancy.T1, NormalLow = 0.1m, NormalHigh = 2.5m };
		private static readonly HospitalConceptRange Preg = new() { ID = 2, ConceptId = 11, Pregnancy = HospitalRangePregnancy.Pregnant, NormalLow = 0.1m, NormalHigh = 4.0m };
		private static HospitalClinicalTemplateField Field(string? mode) => new() { ID = 40, ConceptId = 11, AlertRange = mode };
		private static Dictionary<string, string> A(string v) => new() { ["40"] = v };
		private static (HospitalConcept, HospitalPatientFacts) For(HospitalPatientFacts f) => (HospitalRangeRules.Effective(Tsh(), HospitalRangeRules.Pick(new[] { T1, Preg }, f)), f);

		[Fact]
		public void Tsh_out_of_range_in_pregnancy_fires_only_in_pregnancy()
		{
			var first = For(new("F", 30 * 365, 1));
			var notPregnant = For(new("F", 30 * 365, null));
			var man = For(new("M", 30 * 365, null));
			var f = Field(HospitalAlertRange.Pregnant);
			Assert.True(HospitalFieldAlertRules.Fires(f, A("3.2"), first.Item1, first.Item2));          // above her first-trimester 2.5
			Assert.False(HospitalFieldAlertRules.Fires(f, A("2.1"), first.Item1, first.Item2));
			Assert.False(HospitalFieldAlertRules.Fires(f, A("3.2"), notPregnant.Item1, notPregnant.Item2));   // normal for her, and not pregnant
			Assert.False(HospitalFieldAlertRules.Fires(f, A("6.0"), notPregnant.Item1, notPregnant.Item2));   // abnormal, but the alert is for pregnancy
			Assert.False(HospitalFieldAlertRules.Fires(f, A("6.0"), man.Item1, man.Item2));
			Assert.True(HospitalFieldAlertRules.Fires(Field(HospitalAlertRange.Any), A("6.0"), man.Item1, man.Item2));   // "always" mode
			Assert.False(HospitalFieldAlertRules.Fires(Field(null), A("6.0"), man.Item1, man.Item2));                    // no range alert asked for
			Assert.False(HospitalFieldAlertRules.Fires(f, A("abc"), first.Item1, first.Item2));
		}

		[Fact]
		public void The_fixed_lines_still_fire_on_their_own()
		{
			var f = Field(HospitalAlertRange.Pregnant); f.AlertAbove = 10;
			Assert.True(HospitalFieldAlertRules.Fires(f, A("12"), null, null));
		}

		[Fact]
		public void A_pair_is_judged_on_both_numbers_and_a_stored_flag_raises_it()
		{
			var bp = new HospitalConcept { ID = 2, ValueType = HospitalConceptType.Panel, NormalHigh = 139, NormalHigh2 = 89 };
			var f = new HospitalClinicalTemplateField { ID = 7, ConceptId = 2, AlertRange = HospitalAlertRange.Pregnant };
			var preg = new HospitalPatientFacts("F", 30 * 365, 3);
			Assert.True(HospitalFieldAlertRules.OutsideRange(f, bp, preg, new[] { "132/95" }));   // the diastolic alone
			Assert.False(HospitalFieldAlertRules.OutsideRange(f, bp, preg, new[] { "132/85" }));
			Assert.True(HospitalFieldAlertRules.OutsideRange(f, HospitalObsFlag.High, preg));
			Assert.False(HospitalFieldAlertRules.OutsideRange(f, HospitalObsFlag.Normal, preg));
			Assert.False(HospitalFieldAlertRules.OutsideRange(f, HospitalObsFlag.High, new HospitalPatientFacts("F", 30 * 365, null)));
			Assert.False(HospitalFieldAlertRules.OutsideRange(f, HospitalObsFlag.Unknown, preg));   // no range to judge by: no alert
		}

		[Fact]
		public void The_designer_refuses_a_range_alert_that_cannot_work()
		{
			var c = Tsh();
			HospitalTemplateFieldInput I() => new() { ConceptId = 11, Section = "الغدة", SectionEn = "Thyroid", ValueSource = "Manual", AlertRange = HospitalAlertRange.Pregnant, AlertText = "تنبيه", AlertTextEn = "Alert" };
			var none = Array.Empty<HospitalClinicalTemplateField>();
			Assert.Null(HospitalTemplateRules.FieldRefusal(I(), c, none, null));
			var bad = I(); bad.AlertRange = "Sometimes"; Assert.Equal("Choose when the alert fires", HospitalTemplateRules.FieldRefusal(bad, c, none, null));
			var noText = I(); noText.AlertTextEn = null; Assert.Equal("Write the alert, in Arabic and in English", HospitalTemplateRules.FieldRefusal(noText, c, none, null));
			var notify = I(); notify.AlertNotify = true; Assert.Null(HospitalTemplateRules.FieldRefusal(notify, c, none, null));   // a range alert may notify on its own
			var choice = I(); Assert.Equal("An alert on the patient's range needs a measured number",
				HospitalTemplateRules.FieldRefusal(choice, new HospitalConcept { ID = 11, ValueType = HospitalConceptType.Choice, Choices = "1|نعم|Yes" }, none, null));
		}

		[Fact]
		public void The_column_matches_its_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h116_range_alert.sql");
			Assert.Contains("ALTER TABLE dbo.HospitalClinicalTemplateFields ADD AlertRange nvarchar(8) NULL", s);
			foreach (var x in HospitalAlertRange.All) Assert.Contains("'" + x + "'", s);
			var m = HospitalSliceParity.Model(HospitalClinicalContentModel.Configure).FindEntityType(typeof(HospitalClinicalTemplateField))!;
			Assert.Equal(8, m.FindProperty(nameof(HospitalClinicalTemplateField.AlertRange))!.GetMaxLength());
			Assert.All(HospitalAlertRange.All, x => Assert.True(x.Length <= 8));
		}
	}
}
