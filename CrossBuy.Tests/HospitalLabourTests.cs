using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H86 / H175 — labour on the WHO Labour Care Guide (2020), with no database: active labour from 5 cm (or
	// when diagnosed), the time limit at each centimetre and in the second stage, each item's alert threshold, what an
	// assessment and the labour's characteristics need, closing a labour, and the tables against their slices.
	// ==========================================================================================
	public class HospitalLabourTests
	{
		private static readonly DateTime T0 = new(2026, 11, 10, 8, 0, 0);
		private static HospitalLabourObservation O(double h, decimal? cm, int? fhr = null, string? liq = null, int? ctx = null, int? s = null, int? d = null, decimal? t = null) =>
			new() { At = T0.AddHours(h), CervixCm = cm, FetalHeartRate = fhr, Liquor = liq, ContractionsPer10 = ctx, BpSystolic = s, BpDiastolic = d, TemperatureC = t };

		[Fact]
		public void Active_labour_starts_at_five_centimetres_or_when_diagnosed()
		{
			Assert.Null(HospitalLabourRules.ActiveStart(new[] { O(0, 3), O(2, 4) }));   // 4 cm is still latent under the guide
			var s = HospitalLabourRules.ActiveStart(new[] { O(0, 3), O(3, 5), O(5, 6) });
			Assert.Equal((T0.AddHours(3), 5m), (s!.Value.At, s.Value.Cm));
			Assert.Equal(T0.AddHours(1), HospitalLabourRules.ActiveStart(new[] { O(0, 4) }, T0.AddHours(1))!.Value.At);   // the midwife's diagnosis wins
		}

		[Theory]
		[InlineData(5, 6.0)]
		[InlineData(6, 5.0)]
		[InlineData(7, 3.0)]
		[InlineData(8, 2.5)]
		[InlineData(9, 2.0)]
		public void Each_centimetre_has_its_time_limit(int cm, double hours) => Assert.Equal(hours, HospitalLabourRules.CmLimitHours(cm));

		[Fact]
		public void The_cervix_alerts_when_it_stays_at_a_centimetre_past_its_limit()
		{
			var obs = new[] { O(0, 5), O(4, 6), O(8.5, 6) };   // 6 cm since 4 h; at 8.5 h it has been there 4.5 h of its 5
			var start = HospitalLabourRules.ActiveStart(obs);
			Assert.Empty(HospitalLabourRules.ProgressWarnings(obs, start, 1, T0.AddHours(8.5)));
			Assert.Equal(new[] { HospitalLabourWarning.CervixDelay }, HospitalLabourRules.ProgressWarnings(obs, start, 1, T0.AddHours(9)));   // time keeps running
			Assert.Equal(0.5, HospitalLabourRules.HoursLeft(obs, start, 1, T0.AddHours(8.5)));
			Assert.Empty(HospitalLabourRules.ProgressWarnings(new[] { O(0, 3) }, null, 1, T0.AddHours(20)));   // latent: no time limit
		}

		[Fact]
		public void The_second_stage_alerts_at_three_hours_for_a_first_birth_two_otherwise()
		{
			var obs = new[] { O(0, 6), O(3, 10) };
			var start = HospitalLabourRules.ActiveStart(obs);
			Assert.Empty(HospitalLabourRules.ProgressWarnings(obs, start, 0, T0.AddHours(5.5)));
			Assert.Equal(new[] { HospitalLabourWarning.SecondStageDelay }, HospitalLabourRules.ProgressWarnings(obs, start, 0, T0.AddHours(6)));
			Assert.Equal(new[] { HospitalLabourWarning.SecondStageDelay }, HospitalLabourRules.ProgressWarnings(obs, start, 2, T0.AddHours(5)));
			Assert.Equal(new[] { HospitalLabourWarning.SecondStageDelay }, HospitalLabourRules.ProgressWarnings(obs, start, null, T0.AddHours(5)));   // unknown parity: 2 h
		}

		[Fact]
		public void Each_item_has_the_guides_alert_threshold()
		{
			Assert.Empty(HospitalLabourRules.ItemWarnings(new HospitalLabourObservation { FetalHeartRate = 140, Liquor = "M", ContractionsPer10 = 4, ContractionSeconds = 45, BpSystolic = 120, BpDiastolic = 80, TemperatureC = 37.2m, Pulse = 90,
				Companion = true, PainRelief = true, OralFluid = true, Posture = "MO", Deceleration = "E", FetalPosition = "OA", Caput = 2, Moulding = 2, UrineProtein = 1, UrineAcetone = 1 }));
			HospitalLabourObservation B(Action<HospitalLabourObservation> f) { var o = new HospitalLabourObservation(); f(o); return o; }
			Assert.Equal(new[] { HospitalLabourWarning.FetalHeart }, HospitalLabourRules.ItemWarnings(B(o => o.FetalHeartRate = 160)));   // 160 is an alert now
			Assert.Equal(new[] { HospitalLabourWarning.Meconium }, HospitalLabourRules.ItemWarnings(B(o => o.Liquor = "T")));           // thick meconium only
			Assert.Equal(new[] { HospitalLabourWarning.LateDecelerations }, HospitalLabourRules.ItemWarnings(B(o => o.Deceleration = "L")));
			Assert.Equal(new[] { HospitalLabourWarning.Malposition }, HospitalLabourRules.ItemWarnings(B(o => o.FetalPosition = "OP")));
			Assert.Equal(new[] { HospitalLabourWarning.Temperature }, HospitalLabourRules.ItemWarnings(B(o => o.TemperatureC = 37.5m)));
			Assert.Equal(new[] { HospitalLabourWarning.FewContractions }, HospitalLabourRules.ItemWarnings(B(o => o.ContractionsPer10 = 2)));
			Assert.Equal(new[] { HospitalLabourWarning.NoCompanion, HospitalLabourWarning.Supine }, HospitalLabourRules.ItemWarnings(B(o => { o.Companion = false; o.Posture = "SP"; })));
			Assert.Equal(new[] { HospitalLabourWarning.Proteinuria, HospitalLabourWarning.Ketonuria }, HospitalLabourRules.ItemWarnings(B(o => { o.UrineProtein = 2; o.UrineAcetone = 2; })));
			Assert.Equal(new[] { HospitalLabourWarning.Hypotension }, HospitalLabourRules.ItemWarnings(B(o => { o.BpSystolic = 75; o.BpDiastolic = 40; })));
		}

		[Fact]
		public void An_assessment_is_plausible_and_within_the_labour()
		{
			var now = T0.AddHours(4);
			Assert.Null(HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { CervixCm = 6 }, now, T0, now, 5m));
			Assert.Null(HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { Companion = true }, now, T0, now, null));   // one guide item is enough
			Assert.Equal("Write at least one measure or a note", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput(), now, T0, now, null));
			Assert.Equal("The time is in the future", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { CervixCm = 6 }, now.AddHours(1), T0, now, null));
			Assert.Equal("The cervix cannot close by more than a centimetre: check the reading", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { CervixCm = 4 }, now, T0, now, 7m));
			Assert.Equal("Choose the liquor", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { Liquor = "X" }, now, T0, now, null));
			Assert.Equal("Choose the fetal position", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { FetalPosition = "LOA" }, now, T0, now, null));
			Assert.Equal("Caput and moulding are 0 to +++", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { Caput = 4 }, now, T0, now, null));
			Assert.Equal("Write both blood pressure numbers", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { BpSystolic = 120 }, now, T0, now, null));
		}

		[Fact]
		public void The_characteristics_are_bounded()
		{
			var now = T0.AddHours(4);
			Assert.Null(HospitalLabourRules.InfoRefusal(new HospitalLabourInfoInput { Parity = 0, Onset = "Induced" }, now.AddHours(-10), T0.AddHours(1), T0, now));
			Assert.Equal("Choose how labour began", HospitalLabourRules.InfoRefusal(new HospitalLabourInfoInput { Onset = "X" }, null, null, T0, now));
			Assert.Equal("Active labour cannot begin before the labour started", HospitalLabourRules.InfoRefusal(new HospitalLabourInfoInput(), null, T0.AddHours(-2), T0, now));
			Assert.Equal("The time is in the future", HospitalLabourRules.InfoRefusal(new HospitalLabourInfoInput(), now.AddHours(1), null, T0, now));
		}

		[Fact]
		public void The_start_may_be_written_back_a_day_but_not_before_the_admission()
		{
			var now = T0.AddHours(10);
			Assert.Null(HospitalLabourRules.StartRefusal(now.AddHours(-6), now.AddDays(-1), now));
			Assert.Equal("The labour's start can be written back a day at most", HospitalLabourRules.StartRefusal(now.AddHours(-25), now.AddDays(-3), now));
			Assert.Equal("The time is before the admission", HospitalLabourRules.StartRefusal(now.AddHours(-6), now.AddHours(-2), now));
		}

		[Fact]
		public void A_labour_closes_once_with_how_it_ended()
		{
			var open = new HospitalLabour { Status = HospitalLabourStatus.Active };
			Assert.Null(HospitalLabourRules.CloseRefusal(open, HospitalLabourOutcome.Vaginal));
			Assert.Equal("Choose how the labour ended", HospitalLabourRules.CloseRefusal(open, "Water"));
			Assert.Equal("This labour is already closed", HospitalLabourRules.CloseRefusal(new HospitalLabour { Status = HospitalLabourStatus.Closed }, HospitalLabourOutcome.Caesarean));
		}

		[Fact]
		public void The_tables_match_their_slices_and_move_in_a_merge()
		{
			var h86 = HospitalSliceParity.Slice("hospital_h86_labour.sql");
			var s = h86 + Environment.NewLine + HospitalSliceParity.Slice("hospital_h175_labour_care_guide.sql");
			var m = HospitalSliceParity.Model(HospitalLabourModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalLabour), "HospitalLabours"), (typeof(HospitalLabourObservation), "HospitalLabourObservations") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
				Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == t);
			}
			foreach (var x in HospitalLabourOutcome.All.Concat(HospitalLiquor.All).Concat(HospitalLcg.Onsets).Concat(HospitalLcg.Postures).Concat(HospitalLcg.Decelerations).Concat(HospitalLcg.Positions))
				Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(h86).OrderBy(x => x));
		}
	}
}
