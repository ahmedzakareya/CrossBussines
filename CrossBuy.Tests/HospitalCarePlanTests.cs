using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H79 — the problem list and the nursing care plan, with no database: what a problem needs, what a plan needs
	// (goal, interventions, a review within 30 days), closing as met or discontinued with an outcome, the suggestions from
	// Morse and Braden, review due, and the tables against their slice.
	// ==========================================================================================
	public class HospitalCarePlanTests
	{
		private static readonly DateTime Today = new(2026, 11, 10);

		[Fact]
		public void A_problem_is_a_chosen_diagnosis_or_a_written_name_with_its_kind()
		{
			Assert.Null(HospitalCarePlanRules.ProblemRefusal(true, null, HospitalProblemKind.Chronic, null, Today));
			Assert.Null(HospitalCarePlanRules.ProblemRefusal(false, "Post-operative wound, left knee", HospitalProblemKind.Acute, Today, Today));
			Assert.Equal("Choose the diagnosis or write the problem", HospitalCarePlanRules.ProblemRefusal(false, " ", HospitalProblemKind.Acute, null, Today));
			Assert.Equal("The problem's name is too long", HospitalCarePlanRules.ProblemRefusal(false, new string('x', 201), HospitalProblemKind.Acute, null, Today));
			Assert.Equal("Choose chronic or acute", HospitalCarePlanRules.ProblemRefusal(true, null, "Sometimes", null, Today));
			Assert.Equal("The date is in the future", HospitalCarePlanRules.ProblemRefusal(true, null, HospitalProblemKind.Chronic, Today.AddDays(1), Today));
		}

		[Fact]
		public void A_plan_needs_its_need_a_goal_interventions_and_a_review_within_thirty_days()
		{
			HospitalCarePlanInput P(string need = HospitalCareNeed.Falls, string? text = null, string? goal = "No fall this stay", string? iv = "Bed low, call bell in reach", int review = 2) =>
				new() { Need = need, NeedText = text, Goal = goal, Interventions = iv, ReviewBy = Today.AddDays(review) };
			Assert.Null(HospitalCarePlanRules.PlanRefusal(P(), Today));
			Assert.Equal("Choose the nursing need", HospitalCarePlanRules.PlanRefusal(P(need: "Sleep"), Today));
			Assert.Equal("Write the nursing need", HospitalCarePlanRules.PlanRefusal(P(need: HospitalCareNeed.Other), Today));
			Assert.Null(HospitalCarePlanRules.PlanRefusal(P(need: HospitalCareNeed.Other, text: "Anxiety before surgery"), Today));
			Assert.Equal("Write the goal", HospitalCarePlanRules.PlanRefusal(P(goal: " "), Today));
			Assert.Equal("Write the interventions", HospitalCarePlanRules.PlanRefusal(P(iv: null), Today));
			Assert.Equal("The review date has passed", HospitalCarePlanRules.PlanRefusal(P(review: -1), Today));
			Assert.Equal("Review it within 30 days", HospitalCarePlanRules.PlanRefusal(P(review: 31), Today));
			Assert.Equal("Choose when it is reviewed", HospitalCarePlanRules.PlanRefusal(new HospitalCarePlanInput { Need = HospitalCareNeed.Pain, Goal = "x", Interventions = "y" }, Today));
		}

		[Fact]
		public void A_plan_closes_once_as_met_or_discontinued_with_its_outcome()
		{
			var open = new HospitalCarePlanItem { Status = HospitalCarePlanStatus.Active };
			Assert.Null(HospitalCarePlanRules.CloseRefusal(open, HospitalCarePlanStatus.Met, "No fall in five days"));
			Assert.Equal("Choose met or discontinued", HospitalCarePlanRules.CloseRefusal(open, HospitalCarePlanStatus.Active, "x"));
			Assert.Equal("Write the outcome", HospitalCarePlanRules.CloseRefusal(open, HospitalCarePlanStatus.Discontinued, " "));
			Assert.Equal("This plan is already closed", HospitalCarePlanRules.CloseRefusal(new HospitalCarePlanItem { Status = HospitalCarePlanStatus.Met }, HospitalCarePlanStatus.Met, "x"));
			Assert.True(HospitalCarePlanRules.ReviewDue(new HospitalCarePlanItem { Status = HospitalCarePlanStatus.Active, ReviewBy = Today }, Today));
			Assert.False(HospitalCarePlanRules.ReviewDue(new HospitalCarePlanItem { Status = HospitalCarePlanStatus.Active, ReviewBy = Today.AddDays(1) }, Today));
			Assert.False(HospitalCarePlanRules.ReviewDue(new HospitalCarePlanItem { Status = HospitalCarePlanStatus.Met, ReviewBy = Today.AddDays(-3) }, Today));
		}

		[Fact]
		public void Morse_and_Braden_suggest_a_plan_only_when_none_is_open_for_that_need()
		{
			var morseHigh = new HospitalRiskAssessment { Kind = HospitalRiskKind.Morse, Score = 55, Level = "High" };
			var morseLow = new HospitalRiskAssessment { Kind = HospitalRiskKind.Morse, Score = 15, Level = "Low" };
			var bradenMild = new HospitalRiskAssessment { Kind = HospitalRiskKind.Braden, Score = 16, Level = "Mild" };
			var bradenNone = new HospitalRiskAssessment { Kind = HospitalRiskKind.Braden, Score = 20, Level = "None" };
			var both = HospitalCarePlanRules.Suggest(morseHigh, bradenMild, Array.Empty<string>());
			Assert.Equal(new[] { HospitalCareNeed.Falls, HospitalCareNeed.PressureInjury }, both.Select(s => s.Need));
			Assert.Equal(55, both[0].Score);
			Assert.Empty(HospitalCarePlanRules.Suggest(morseLow, bradenNone, Array.Empty<string>()));
			Assert.Equal(new[] { HospitalCareNeed.PressureInjury }, HospitalCarePlanRules.Suggest(morseHigh, bradenMild, new[] { HospitalCareNeed.Falls }).Select(s => s.Need));
			Assert.Empty(HospitalCarePlanRules.Suggest(null, null, Array.Empty<string>()));
		}

		[Fact]
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h79_care_plans.sql");
			var m = HospitalSliceParity.Model(HospitalCarePlanModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalProblem), "HospitalProblems"), (typeof(HospitalCarePlanItem), "HospitalCarePlanItems") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
				Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == t);
			}
			foreach (var x in HospitalProblemKind.All.Concat(HospitalProblemStatus.All).Concat(HospitalCareNeed.All).Concat(HospitalCarePlanStatus.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
		}
	}
}
