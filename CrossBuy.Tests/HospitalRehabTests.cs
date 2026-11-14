using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H89 — physiotherapy, with no database: the improvement whichever way a measure runs, the MCID edge, each
	// warning, what a referral / assessment / session / discharge needs, and the tables against their slice.
	// ==========================================================================================
	public class HospitalRehabTests
	{
		private static readonly DateTime Today = new(2026, 11, 10);
		private static HospitalRehabEpisode Ep(string measure = HospitalRehabMeasure.Oswestry, decimal baseline = 46, int? pain = 7, int planned = 10) => new()
			{ Status = HospitalRehabStatus.Active, MeasureCode = measure, BaselineScore = baseline, BaselinePain = pain, PlannedSessions = planned, ReferredAt = Today.AddDays(-20), AssessedAt = Today.AddDays(-20) };
		private static HospitalRehabSession S(int dayBack, string att = HospitalRehabAttendance.Attended, decimal? score = null, int? pain = null) =>
			new() { SessionDate = Today.AddDays(-dayBack), Attendance = att, Score = score, PainBefore = pain, Minutes = att == HospitalRehabAttendance.Attended ? 45 : null };

		[Fact]
		public void Improvement_runs_the_right_way_for_each_measure()
		{
			var odi = HospitalRehabMeasure.Of(HospitalRehabMeasure.Oswestry)!; var lefs = HospitalRehabMeasure.Of(HospitalRehabMeasure.Lefs)!;
			Assert.Equal(12m, HospitalRehabRules.Improvement(odi, 46, 34));     // disability falls: better
			Assert.Equal(12m, HospitalRehabRules.Improvement(lefs, 40, 52));    // function rises: better
			Assert.Equal(HospitalRehabTrend.Improved, HospitalRehabRules.Trend(odi, 10));
			Assert.Equal(HospitalRehabTrend.Same, HospitalRehabRules.Trend(odi, 9.9m));
			Assert.Equal(HospitalRehabTrend.Worse, HospitalRehabRules.Trend(lefs, -9));
			Assert.Null(HospitalRehabMeasure.Of("XYZ"));
		}

		[Fact]
		public void Progress_counts_attendance_and_judges_the_change()
		{
			var p = HospitalRehabRules.Progress(Ep(), new[] { S(9, score: 44, pain: 6), S(7, HospitalRehabAttendance.NoShow), S(5, HospitalRehabAttendance.Cancelled), S(2, score: 34, pain: 4) }, Today);
			Assert.Equal(2, p.Attended); Assert.Equal(1, p.NoShows); Assert.Equal(1, p.Cancelled);
			Assert.Equal(67, p.AttendancePct);          // a cancellation is not a miss
			Assert.Equal(34m, p.LatestScore); Assert.Equal(12m, p.Change); Assert.Equal(HospitalRehabTrend.Improved, p.Trend);
			Assert.Equal(3, p.PainChange);
			Assert.Empty(p.Warnings);
		}

		[Fact]
		public void Each_warning_fires_at_its_edge()
		{
			Assert.Contains(HospitalRehabWarning.TwoNoShows, HospitalRehabRules.Progress(Ep(), new[] { S(5), S(3, HospitalRehabAttendance.NoShow), S(2, HospitalRehabAttendance.Cancelled), S(1, HospitalRehabAttendance.NoShow) }, Today).Warnings);
			Assert.DoesNotContain(HospitalRehabWarning.TwoNoShows, HospitalRehabRules.Progress(Ep(), new[] { S(3, HospitalRehabAttendance.NoShow), S(1) }, Today).Warnings);
			var six = Enumerable.Range(1, 6).Select(i => S(i, score: 40)).ToArray();   // 46 → 40: 6 points, under the MCID of 10
			Assert.Contains(HospitalRehabWarning.NoProgress, HospitalRehabRules.Progress(Ep(), six, Today).Warnings);
			Assert.DoesNotContain(HospitalRehabWarning.NoProgress, HospitalRehabRules.Progress(Ep(), six.Take(5).ToArray(), Today).Warnings);
			Assert.Contains(HospitalRehabWarning.OverPlan, HospitalRehabRules.Progress(Ep(planned: 5), six, Today).Warnings);
			Assert.Contains(HospitalRehabWarning.PainWorse, HospitalRehabRules.Progress(Ep(pain: 5), new[] { S(1, pain: 7) }, Today).Warnings);
			Assert.Contains(HospitalRehabWarning.Worse, HospitalRehabRules.Progress(Ep(), new[] { S(1, score: 56) }, Today).Warnings);
			var waiting = new HospitalRehabEpisode { Status = HospitalRehabStatus.Referred, ReferredAt = Today.AddDays(-4), PlannedSessions = 5 };
			Assert.Contains(HospitalRehabWarning.Unassessed, HospitalRehabRules.Progress(waiting, Array.Empty<HospitalRehabSession>(), Today).Warnings);
			waiting.ReferredAt = Today.AddDays(-3);
			Assert.Empty(HospitalRehabRules.Progress(waiting, Array.Empty<HospitalRehabSession>(), Today).Warnings);
		}

		[Fact]
		public void A_referral_and_an_assessment_need_their_parts()
		{
			var r = new HospitalRehabReferInput { Region = "Back", Problem = "Low back pain", PlannedSessions = 10, PerWeek = 2 };
			Assert.Null(HospitalRehabRules.ReferRefusal(r));
			r.Region = "Tail"; Assert.Equal("Choose the body region", HospitalRehabRules.ReferRefusal(r));
			r.Region = "Back"; r.PlannedSessions = 0; Assert.Equal("The planned sessions are between 1 and 60", HospitalRehabRules.ReferRefusal(r));

			var e = new HospitalRehabEpisode { Status = HospitalRehabStatus.Referred };
			Assert.Null(HospitalRehabRules.AssessRefusal(e, new() { MeasureCode = "LEFS", BaselineScore = 40, BaselinePain = 6 }));
			Assert.Equal("Choose the outcome measure", HospitalRehabRules.AssessRefusal(e, new() { MeasureCode = "X", BaselineScore = 40 }));
			Assert.Equal("The score is outside the measure's range", HospitalRehabRules.AssessRefusal(e, new() { MeasureCode = "LEFS", BaselineScore = 81 }));
			Assert.Equal("This referral is already assessed or closed", HospitalRehabRules.AssessRefusal(Ep(), new() { MeasureCode = "LEFS", BaselineScore = 40 }));
		}

		[Fact]
		public void A_session_needs_its_treatment_and_a_miss_has_none()
		{
			var e = Ep();
			HospitalRehabSessionInput Came() => new() { Attendance = HospitalRehabAttendance.Attended, Minutes = 45, Interventions = new() { "Exercise", "Manual" }, PainBefore = 6, Score = 40 };
			Assert.Null(HospitalRehabRules.SessionRefusal(e, Came(), Today, Today));
			Assert.Equal("A session is recorded on its day or after it", HospitalRehabRules.SessionRefusal(e, Came(), Today.AddDays(1), Today));
			var fresh = Ep(); fresh.AssessedAt = Today.AddDays(-5);
			Assert.Equal("The day is before the assessment", HospitalRehabRules.SessionRefusal(fresh, Came(), Today.AddDays(-6), Today));
			Assert.Equal("A session can be written back two weeks at most", HospitalRehabRules.SessionRefusal(e, Came(), Today.AddDays(-15), Today));
			var i = Came(); i.Minutes = null; Assert.Equal("Write the minutes of the session (5 to 240)", HospitalRehabRules.SessionRefusal(e, i, Today, Today));
			i = Came(); i.Interventions = new() { "Magic" }; Assert.Equal("Choose what was done from the list", HospitalRehabRules.SessionRefusal(e, i, Today, Today));
			i = Came(); i.Score = 120; Assert.Equal("The score is outside the measure's range", HospitalRehabRules.SessionRefusal(e, i, Today, Today));
			Assert.Null(HospitalRehabRules.SessionRefusal(e, new() { Attendance = HospitalRehabAttendance.NoShow }, Today, Today));
			Assert.Equal("A missed session has no treatment to record", HospitalRehabRules.SessionRefusal(e, new() { Attendance = HospitalRehabAttendance.NoShow, Minutes = 30 }, Today, Today));
			Assert.Equal("Sessions are recorded on an active episode", HospitalRehabRules.SessionRefusal(new HospitalRehabEpisode { Status = HospitalRehabStatus.Referred }, Came(), Today, Today));
		}

		[Fact]
		public void Discharge_and_cancel()
		{
			Assert.Null(HospitalRehabRules.EndRefusal(Ep(), new() { Reason = "GoalsMet", Summary = "Back to work" }));
			Assert.Equal("Choose why the patient is discharged", HospitalRehabRules.EndRefusal(Ep(), new() { Reason = "Bored", Summary = "x" }));
			Assert.Equal("Write the discharge summary", HospitalRehabRules.EndRefusal(Ep(), new() { Reason = "Plateau" }));
			Assert.Equal("Only a referral not yet assessed can be cancelled", HospitalRehabRules.EndRefusal(Ep(), new() { Cancel = true, Summary = "x" }));
			Assert.Null(HospitalRehabRules.EndRefusal(new HospitalRehabEpisode { Status = HospitalRehabStatus.Referred }, new() { Cancel = true, Summary = "Moved city" }));
		}

		[Fact]
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h89_rehab.sql");
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h119_session_charges.sql");   // H119: the bill line a session was charged on
			var m = HospitalSliceParity.Model(HospitalRehabModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalRehabEpisode), "HospitalRehabEpisodes"), (typeof(HospitalRehabGoal), "HospitalRehabGoals"), (typeof(HospitalRehabSession), "HospitalRehabSessions") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
				Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == t);
			}
			foreach (var x in HospitalRehabStatus.All.Concat(HospitalRehabAttendance.All).Concat(HospitalRehabGoalStatus.All).Concat(HospitalRehabMeasure.All.Select(d => d.Code))) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(HospitalSliceParity.Slice("hospital_h89_rehab.sql")));   // each slice guards its own prerequisites
		}
	}
}
