using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H250 — questionnaires. What must never happen: a PHQ-9 with thoughts of self-harm not flagged urgent
	// because its total is low; a score on the wrong band; an incomplete set of answers accepted; an answer outside the
	// scale; an answer for someone the account does not hold; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalQuestionnaireTests
	{
		private static readonly HospitalInstrument Phq = HospitalQuestionnaireRules.Find("PHQ9")!, Gad = HospitalQuestionnaireRules.Find("GAD7")!, Audit = HospitalQuestionnaireRules.Find("AUDITC")!;

		[Fact]
		public void The_instruments_have_their_published_item_counts()
		{
			Assert.Equal(9, Phq.Items.Count); Assert.Equal(7, Gad.Items.Count); Assert.Equal(3, Audit.Items.Count);
			Assert.Equal(4, Phq.Options.Count); Assert.Equal(5, Audit.Options.Count);
		}

		[Theory]
		[InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, "Minimal", false)]
		[InlineData(new[] { 1, 1, 1, 1, 1, 0, 0, 0, 0 }, 5, "Mild", false)]
		[InlineData(new[] { 2, 2, 2, 2, 2, 0, 0, 0, 0 }, 10, "Moderate", false)]
		[InlineData(new[] { 3, 3, 3, 3, 3, 0, 0, 0, 0 }, 15, "ModeratelySevere", false)]
		[InlineData(new[] { 3, 3, 3, 3, 3, 3, 2, 0, 0 }, 20, "Severe", true)]
		[InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 }, 1, "Minimal", true)]   // item 9: urgent whatever the total
		public void Phq9_scores_and_flags(int[] a, int score, string sev, bool urgent) =>
			Assert.Equal((score, sev, urgent), HospitalQuestionnaireRules.Score(Phq, a));

		[Fact]
		public void Gad7_and_audit_c_bands()
		{
			Assert.Equal((15, "Severe", true), HospitalQuestionnaireRules.Score(Gad, new[] { 3, 3, 3, 2, 2, 1, 1 }));
			Assert.Equal((9, "Mild", false), HospitalQuestionnaireRules.Score(Gad, new[] { 2, 2, 2, 1, 1, 1, 0 }));
			Assert.Equal("Negative", HospitalQuestionnaireRules.Score(Audit, new[] { 1, 1, 1 }).Severity);
			Assert.Equal("Positive", HospitalQuestionnaireRules.Score(Audit, new[] { 1, 1, 1 }, female: true).Severity);   // 3+ for women
			Assert.Equal("Positive", HospitalQuestionnaireRules.Score(Audit, new[] { 2, 1, 1 }).Severity);
		}

		[Fact]
		public void Every_item_is_answered_on_its_scale()
		{
			Assert.NotNull(HospitalQuestionnaireRules.AnswerRefusal(Phq, new[] { 0, 0, 0 }));
			Assert.NotNull(HospitalQuestionnaireRules.AnswerRefusal(Phq, new[] { 0, 0, 0, 0, 0, 0, 0, 0, 4 }));
			Assert.NotNull(HospitalQuestionnaireRules.AnswerRefusal(Phq, null));
			Assert.Null(HospitalQuestionnaireRules.AnswerRefusal(Audit, new[] { 4, 4, 4 }));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalQuestionnaireModel.Configure);

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
		{
			var s = HospitalSliceParity.Slice("hospital_h250_questionnaires.sql");
			HospitalSliceParity.NamesItsTable(Model(), s, typeof(HospitalQuestionnaire), "HospitalQuestionnaires");
			HospitalSliceParity.ColumnsMatch(Model(), s, typeof(HospitalQuestionnaire), "HospitalQuestionnaires");
		}
	}
}
