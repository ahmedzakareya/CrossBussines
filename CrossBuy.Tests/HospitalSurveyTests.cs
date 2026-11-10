using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H65 — satisfaction surveys, with no database: every question answered 1–5 and the recommend score 0–10,
	// the NPS, when a survey needs a follow-up, the standard questions, and the tables against their slice.
	// ==========================================================================================
	public class HospitalSurveyTests
	{
		private static HospitalSurveyInput Answers(int recommend = 9, params (int Q, int S)[] scores) =>
			new() { Kind = HospitalSurveyKind.Visit, EpisodeId = 1, Recommend = recommend, Scores = scores.ToDictionary(x => x.Q, x => x.S) };

		[Fact]
		public void Every_question_is_answered_and_the_recommend_score_is_0_to_10()
		{
			var qs = new[] { 1, 2 };
			Assert.Null(HospitalSurveyRules.AnswerRefusal(Answers(9, (1, 5), (2, 4)), qs));
			Assert.Equal("Answer every question", HospitalSurveyRules.AnswerRefusal(Answers(9, (1, 5)), qs));
			Assert.Equal("Answer every question", HospitalSurveyRules.AnswerRefusal(Answers(9, (1, 5), (2, 6)), qs));
			Assert.Equal("not_found", HospitalSurveyRules.AnswerRefusal(Answers(9, (1, 5), (2, 4), (3, 4)), qs));   // a question of another kind
			Assert.Equal("Answer how likely you are to recommend us", HospitalSurveyRules.AnswerRefusal(Answers(11, (1, 5), (2, 4)), qs));
			var none = Answers(9, (1, 5), (2, 4)); none.Recommend = null;
			Assert.Equal("Answer how likely you are to recommend us", HospitalSurveyRules.AnswerRefusal(none, qs));
			Assert.Equal("There are no questions for this kind of care yet", HospitalSurveyRules.AnswerRefusal(Answers(), Array.Empty<int>()));
		}

		[Fact]
		public void The_nps_is_promoters_less_detractors()
		{
			Assert.Null(HospitalSurveyRules.Nps(Array.Empty<int>()));
			Assert.Equal(100, HospitalSurveyRules.Nps(new[] { 9, 10 }));
			Assert.Equal(-100, HospitalSurveyRules.Nps(new[] { 0, 6 }));
			Assert.Equal(0, HospitalSurveyRules.Nps(new[] { 10, 3, 7, 8 }));   // one promoter, one detractor, two passives
			Assert.Equal(25, HospitalSurveyRules.Nps(new[] { 10, 9, 3, 8 }));
		}

		[Fact]
		public void A_low_survey_needs_a_follow_up()
		{
			Assert.True(HospitalSurveyRules.NeedsFollowUp(2.5m, 9));
			Assert.True(HospitalSurveyRules.NeedsFollowUp(4.6m, 6));
			Assert.False(HospitalSurveyRules.NeedsFollowUp(3m, 7));
			Assert.Equal(3.67m, HospitalSurveyRules.Average(new[] { 3, 4, 4 }));
		}

		[Fact]
		public void The_standard_questions_are_valid_and_cover_every_kind()
		{
			var all = HospitalSurveyRules.Standard();
			foreach (var q in all) Assert.Null(HospitalSurveyRules.QuestionRefusal(q));
			Assert.Equal(HospitalSurveyKind.All.OrderBy(k => k), all.Select(q => q.Kind!).Distinct().OrderBy(k => k));
			Assert.Equal(all.Count, all.Select(q => q.Code).Distinct().Count());
		}

		[Fact]
		public void The_tables_match_their_slice_and_a_survey_moves_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h65_surveys.sql");
			var m = HospitalSliceParity.Model(HospitalSurveyModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalSurveyQuestion), "HospitalSurveyQuestions"), (typeof(HospitalSurveyResponse), "HospitalSurveyResponses"), (typeof(HospitalSurveyAnswer), "HospitalSurveyAnswers") })
			{ HospitalSliceParity.NamesItsTable(m, s, t, n); HospitalSliceParity.ColumnsMatch(m, s, t, n); }
			foreach (var k in HospitalSurveyKind.All) Assert.Contains("'" + k + "'", s);
			Assert.Contains("CHECK (Recommend BETWEEN 0 AND 10)", s);
			Assert.Contains("CHECK (Score BETWEEN 1 AND 5)", s);
			Assert.Contains("UX_HospitalSurveyResponses_Encounter", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalSurveyResponse));
		}
	}
}
