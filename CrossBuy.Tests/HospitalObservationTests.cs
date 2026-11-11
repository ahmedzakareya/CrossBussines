using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H71 — NEWS2 and the Morse / Braden scales, with no database. Every band edge of the RCP chart is pinned
	// (a score that drifts by one band at a boundary is exactly the error that hides), the clinical risk and the response
	// frequency, an incomplete set left unscored, the scales' items and levels, and the tables against their slice.
	// ==========================================================================================
	public class HospitalObservationTests
	{
		private static HospitalObservationInput Normal() => new()
		{ RespRate = 16, SpO2 = 97, OnOxygen = false, SystolicBp = 120, DiastolicBp = 80, HeartRate = 75, Consciousness = "A", TempC = 37.0m };

		[Theory]
		[InlineData(8, 3)] [InlineData(9, 1)] [InlineData(11, 1)] [InlineData(12, 0)] [InlineData(20, 0)] [InlineData(21, 2)] [InlineData(24, 2)] [InlineData(25, 3)]
		public void Respiration_bands(int rr, int score) => Assert.Equal(score, HospitalNews2Rules.Resp(rr));

		[Theory]
		[InlineData(91, 3)] [InlineData(92, 2)] [InlineData(93, 2)] [InlineData(94, 1)] [InlineData(95, 1)] [InlineData(96, 0)]
		public void SpO2_scale_1_bands(int s, int score) => Assert.Equal(score, HospitalNews2Rules.SpO2Scale1(s));

		[Theory]
		[InlineData(83, false, 3)] [InlineData(84, false, 2)] [InlineData(85, false, 2)] [InlineData(86, false, 1)] [InlineData(87, false, 1)]
		[InlineData(88, false, 0)] [InlineData(92, false, 0)] [InlineData(97, false, 0)]
		[InlineData(92, true, 0)] [InlineData(93, true, 1)] [InlineData(94, true, 1)] [InlineData(95, true, 2)] [InlineData(96, true, 2)] [InlineData(97, true, 3)]
		public void SpO2_scale_2_bands(int s, bool oxygen, int score) => Assert.Equal(score, HospitalNews2Rules.SpO2Scale2(s, oxygen));

		[Theory]
		[InlineData(90, 3)] [InlineData(91, 2)] [InlineData(100, 2)] [InlineData(101, 1)] [InlineData(110, 1)] [InlineData(111, 0)] [InlineData(219, 0)] [InlineData(220, 3)]
		public void Systolic_bands(int sbp, int score) => Assert.Equal(score, HospitalNews2Rules.Systolic(sbp));

		[Theory]
		[InlineData(40, 3)] [InlineData(41, 1)] [InlineData(50, 1)] [InlineData(51, 0)] [InlineData(90, 0)] [InlineData(91, 1)] [InlineData(110, 1)] [InlineData(111, 2)] [InlineData(130, 2)] [InlineData(131, 3)]
		public void Pulse_bands(int hr, int score) => Assert.Equal(score, HospitalNews2Rules.Pulse(hr));

		[Theory]
		[InlineData("35.0", 3)] [InlineData("35.1", 1)] [InlineData("36.0", 1)] [InlineData("36.1", 0)] [InlineData("38.0", 0)] [InlineData("38.1", 1)] [InlineData("39.0", 1)] [InlineData("39.1", 2)]
		public void Temperature_bands(string t, int score) => Assert.Equal(score, HospitalNews2Rules.Temp(decimal.Parse(t, System.Globalization.CultureInfo.InvariantCulture)));

		[Fact]
		public void Consciousness_and_oxygen()
		{
			Assert.Equal(0, HospitalNews2Rules.Conscious("A"));
			foreach (var c in new[] { "C", "V", "P", "U" }) Assert.Equal(3, HospitalNews2Rules.Conscious(c));
			Assert.Equal(2, HospitalNews2Rules.Air(true));
			Assert.Equal(0, HospitalNews2Rules.Air(false));
		}

		[Fact]
		public void A_full_set_is_scored_and_given_its_risk_and_its_next_reading()
		{
			var n = Normal();
			Assert.Equal((0, false), HospitalNews2Rules.Score(n));
			Assert.Equal(12, HospitalNews2Rules.HoursToNext(0, HospitalNews2Rules.Risk(0, false)));

			// new confusion alone: a single 3 — low-medium, hourly, the doctor is told
			n.Consciousness = "C";
			var (t, three) = HospitalNews2Rules.Score(n)!.Value;
			Assert.Equal(3, t); Assert.True(three);
			Assert.Equal(HospitalNews2Risk.LowSingle, HospitalNews2Rules.Risk(t, three));
			Assert.Equal(1, HospitalNews2Rules.HoursToNext(t, HospitalNews2Risk.LowSingle));
			Assert.True(HospitalNews2Rules.Escalates(HospitalNews2Risk.LowSingle));

			// a septic picture: RR 24 (2), SpO2 93 (2), oxygen (2), SBP 98 (2), HR 118 (2), alert (0), 38.6 (1) = 11
			var septic = new HospitalObservationInput { RespRate = 24, SpO2 = 93, OnOxygen = true, SystolicBp = 98, HeartRate = 118, Consciousness = "A", TempC = 38.6m };
			Assert.Equal((11, false), HospitalNews2Rules.Score(septic));
			Assert.Equal(HospitalNews2Risk.High, HospitalNews2Rules.Risk(11, false));

			Assert.Equal(HospitalNews2Risk.Medium, HospitalNews2Rules.Risk(5, false));
			Assert.Equal(HospitalNews2Risk.Medium, HospitalNews2Rules.Risk(6, true));
			Assert.Equal(HospitalNews2Risk.Low, HospitalNews2Rules.Risk(4, false));
			Assert.Equal(4, HospitalNews2Rules.HoursToNext(4, HospitalNews2Risk.Low));
			Assert.False(HospitalNews2Rules.Escalates(HospitalNews2Risk.Low));
		}

		[Fact]
		public void An_incomplete_set_is_kept_but_not_scored_and_a_typo_is_refused()
		{
			var n = Normal(); n.SystolicBp = null;
			Assert.Null(HospitalNews2Rules.Score(n));
			Assert.Null(HospitalNews2Rules.Refusal(n));
			Assert.Equal("Enter at least one reading", HospitalNews2Rules.Refusal(new HospitalObservationInput()));
			Assert.Equal("SpO₂ must be 50 to 100%", HospitalNews2Rules.Refusal(new HospitalObservationInput { SpO2 = 970 }));
			Assert.Equal("The diastolic pressure must be below the systolic", HospitalNews2Rules.Refusal(new HospitalObservationInput { SystolicBp = 80, DiastolicBp = 120 }));
			Assert.Equal("Temperature must be 30 to 45 °C", HospitalNews2Rules.Refusal(new HospitalObservationInput { TempC = 370m }));
			Assert.Equal("Choose the level of consciousness", HospitalNews2Rules.Refusal(new HospitalObservationInput { Consciousness = "X" }));
		}

		[Fact]
		public void Pain_of_four_or_more_is_reassessed_within_the_hour()
		{
			var at = new DateTime(2026, 11, 9, 10, 0, 0);
			Assert.Equal(at.AddHours(1), HospitalNews2Rules.PainReassessAt(new HospitalObservation { TakenAt = at, PainScore = 6 }));
			Assert.Null(HospitalNews2Rules.PainReassessAt(new HospitalObservation { TakenAt = at, PainScore = 3 }));
			Assert.Null(HospitalNews2Rules.PainReassessAt(null));
		}

		[Fact]
		public void The_escalation_task_shows_in_the_readers_language()
		{
			var title = HospitalObservationService.EscalationTitle(7, HospitalNews2Risk.High);
			Assert.Equal("NEWS2|High|7", title);
			Assert.Equal("NEWS2 7 — emergency", HospitalNews2Text.TaskText(title, k => k == "news2task:High" ? "NEWS2 {0} — emergency" : k));
			Assert.Equal("Turn the patient", HospitalNews2Text.TaskText("Turn the patient", k => "x"));
		}

		[Fact]
		public void Morse_and_Braden_score_their_items_into_levels()
		{
			var morse = new HospitalRiskInput { Kind = HospitalRiskKind.Morse, Items = new() { 25, 15, 15, 20, 10, 0 } };
			Assert.Null(HospitalRiskRules.Refusal(morse));
			Assert.Equal((85, "High"), HospitalRiskRules.Score(morse));
			Assert.Equal("Moderate", HospitalRiskRules.MorseLevel(25));
			Assert.Equal("Low", HospitalRiskRules.MorseLevel(24));
			Assert.Equal("High", HospitalRiskRules.MorseLevel(45));
			Assert.Equal("Answer every item", HospitalRiskRules.Refusal(new HospitalRiskInput { Kind = HospitalRiskKind.Morse, Items = new() { 25, 15, 15, 20, 10, 5 } }));
			Assert.Equal("Answer every item", HospitalRiskRules.Refusal(new HospitalRiskInput { Kind = HospitalRiskKind.Morse, Items = new() { 25 } }));

			var braden = new HospitalRiskInput { Kind = HospitalRiskKind.Braden, Items = new() { 2, 2, 1, 2, 2, 1 } };
			Assert.Equal((10, "High"), HospitalRiskRules.Score(braden));
			Assert.Equal("VeryHigh", HospitalRiskRules.BradenLevel(9));
			Assert.Equal("Moderate", HospitalRiskRules.BradenLevel(13));
			Assert.Equal("Mild", HospitalRiskRules.BradenLevel(18));
			Assert.Equal("None", HospitalRiskRules.BradenLevel(19));
			Assert.Equal("Answer every item", HospitalRiskRules.Refusal(new HospitalRiskInput { Kind = HospitalRiskKind.Braden, Items = new() { 2, 2, 1, 2, 2, 4 } }));
			Assert.Equal("Choose the assessment", HospitalRiskRules.Refusal(new HospitalRiskInput { Kind = "Waterlow" }));

			Assert.True(HospitalRiskRules.Flags(new HospitalRiskAssessment { Kind = HospitalRiskKind.Morse, Score = 45 }));
			Assert.False(HospitalRiskRules.Flags(new HospitalRiskAssessment { Kind = HospitalRiskKind.Morse, Score = 40 }));
			Assert.True(HospitalRiskRules.Flags(new HospitalRiskAssessment { Kind = HospitalRiskKind.Braden, Score = 12 }));
			Assert.False(HospitalRiskRules.Flags(new HospitalRiskAssessment { Kind = HospitalRiskKind.Braden, Score = 13 }));
			var now = new DateTime(2026, 11, 9, 10, 0, 0);
			Assert.True(HospitalRiskRules.Due(null, now));
			Assert.True(HospitalRiskRules.Due(new HospitalRiskAssessment { AssessedAt = now.AddHours(-24) }, now));
			Assert.False(HospitalRiskRules.Due(new HospitalRiskAssessment { AssessedAt = now.AddHours(-23) }, now));
		}

		[Fact]
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h71_observations.sql");
			var m = HospitalSliceParity.Model(HospitalObservationModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalObservation), "HospitalObservations");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalObservation), "HospitalObservations");
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalRiskAssessment), "HospitalRiskAssessments");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalRiskAssessment), "HospitalRiskAssessments");
			foreach (var x in HospitalConsciousness.All.Concat(HospitalNews2Risk.All).Concat(HospitalRiskKind.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalObservation));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalRiskAssessment));
		}

		// ==================================================================================
		// AN INCOMPLETE SET IS NOT A WELL PATIENT.
		//
		// Score() already returns null for a set missing any parameter. What the ward board did
		// with that null was the defect: it gave an incomplete set FOUR HOURS until the next
		// reading — the interval for a patient scored 1–4 — which is a claim about the risk of a
		// patient who was never scored. The code's own comment said "an incomplete one: the next
		// is due now" and the code did something else; the comment was the specification.
		// ==================================================================================
		[Fact]
		public void A_set_that_could_not_be_scored_is_due_at_once()
		{
			var taken = new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);

			// nothing recorded yet
			Assert.Null(HospitalNews2Rules.NextDueAt(null));

			// pulse and temperature only: no score, so no interval is earned
			var partial = new HospitalObservation { TakenAt = taken, HeartRate = 88, TempC = 37.4m, News2 = null, News2Risk = null };
			Assert.Equal(taken, HospitalNews2Rules.NextDueAt(partial));

			// a scored set earns its band: 0 buys twelve hours, a single 3 buys one
			var well = new HospitalObservation { TakenAt = taken, News2 = 0, News2Risk = HospitalNews2Risk.Low };
			Assert.Equal(taken.AddHours(12), HospitalNews2Rules.NextDueAt(well));

			var single3 = new HospitalObservation { TakenAt = taken, News2 = 3, News2Risk = HospitalNews2Risk.LowSingle };
			Assert.Equal(taken.AddHours(1), HospitalNews2Rules.NextDueAt(single3));

			// the incomplete set must NEVER be treated as the 1-4 band, which is what it used to get
			Assert.NotEqual(taken.AddHours(4), HospitalNews2Rules.NextDueAt(partial));
		}
	}
}
