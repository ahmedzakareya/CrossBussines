using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H118 — an alert on a SUM SCORE is judged on save, not only at the signature that computes the score: the
	// compartment-syndrome check (two signs → call orthopaedics now) fires on the draft as the signs stand; an unfinished
	// score is never guessed; a draft's empty required field is not a refusal.
	// ==========================================================================================
	public class HospitalScoreAlertTests
	{
		private static readonly string[] Signs = { "CS-PAIN", "CS-STRETCH", "CS-PARA", "CS-TENSE", "CS-WEAK", "CS-PULSE" };

		private static List<HospitalTemplateFieldView> Form()
		{
			var views = new List<HospitalTemplateFieldView>();
			int id = 170;
			foreach (var s in Signs)
				views.Add(new(new HospitalClinicalTemplateField { ID = id++, ConceptId = id, Required = true }, new HospitalConcept { ID = id, Code = s, ValueType = HospitalConceptType.Number, NormalLow = 0, NormalHigh = 0 }, null));
			views.Add(new(new HospitalClinicalTemplateField { ID = 176, ConceptId = 300, AlertAbove = 2, AlertNotify = true },
				new HospitalConcept { ID = 300, Code = "CS-SCORE", ValueType = HospitalConceptType.Score, Calculator = HospitalConceptCalculator.Sum, CalcInputs = string.Join(", ", Signs) }, null));
			return views;
		}

		private static Dictionary<string, string> Answers(params string[] v) => Enumerable.Range(0, v.Length).Where(i => v[i] != "").ToDictionary(i => (170 + i).ToString(), i => v[i]);

		[Fact]
		public void Two_signs_make_the_score_two_and_the_alert_fires_on_save()
		{
			var form = Form();
			var withScores = HospitalFormRules.WithScores(form, Answers("1", "1", "0", "0", "0", "0"));
			Assert.Equal("2", withScores["176"]);
			Assert.True(HospitalFieldAlertRules.Fires(form[^1].Field, withScores));
			Assert.False(HospitalFieldAlertRules.Fires(form[^1].Field, HospitalFormRules.WithScores(form, Answers("1", "0", "0", "0", "0", "0"))));   // one sign: re-examine, no alarm
		}

		[Fact]
		public void An_unfinished_score_is_never_guessed()
		{
			var form = Form();
			var partial = HospitalFormRules.WithScores(form, Answers("1", "1", "", "", "", ""));   // two signs typed, four still empty
			Assert.False(partial.ContainsKey("176"));
			Assert.False(HospitalFieldAlertRules.Fires(form[^1].Field, partial));
			Assert.Equal(2, HospitalFormRules.Typed(form, partial).Count);   // the draft's empty required fields are not a refusal
			Assert.Single(HospitalFormRules.Typed(form, new Dictionary<string, string> { ["170"] = "1", ["171"] = "x" }));   // nor is a value still unreadable
		}

		[Fact]
		public void A_form_without_a_sum_score_is_returned_as_typed()
		{
			var form = Form().Take(6).ToList();
			var a = Answers("1", "0", "0", "0", "0", "0");
			Assert.Equal(a, HospitalFormRules.WithScores(form, a));
		}
	}
}
