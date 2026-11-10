using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H86 — the partograph, with no database: where the active phase starts, the alert and action lines, the
	// other warnings of an observation, what an observation needs, closing a labour, and the tables against their slice.
	// ==========================================================================================
	public class HospitalLabourTests
	{
		private static readonly DateTime T0 = new(2026, 11, 10, 8, 0, 0);
		private static HospitalLabourObservation O(double h, decimal? cm, int? fhr = null, string? liq = null, int? ctx = null, int? s = null, int? d = null, decimal? t = null) =>
			new() { At = T0.AddHours(h), CervixCm = cm, FetalHeartRate = fhr, Liquor = liq, ContractionsPer10 = ctx, BpSystolic = s, BpDiastolic = d, TemperatureC = t };

		[Fact]
		public void The_active_phase_starts_at_the_first_dilatation_of_four_centimetres()
		{
			Assert.Null(HospitalLabourRules.ActiveStart(new[] { O(0, 2), O(2, 3) }));
			var s = HospitalLabourRules.ActiveStart(new[] { O(0, 2), O(3, 4), O(5, 6) });
			Assert.Equal((T0.AddHours(3), 4m), s);
			Assert.Equal(7m, HospitalLabourRules.AlertCm(4m, 3));
			Assert.Equal(10m, HospitalLabourRules.AlertCm(8m, 5));   // capped at full dilatation
		}

		[Fact]
		public void Behind_one_centimetre_an_hour_crosses_the_alert_line_and_four_hours_more_the_action_line()
		{
			var start = HospitalLabourRules.ActiveStart(new[] { O(0, 4) });
			Assert.Empty(HospitalLabourRules.LineWarnings(start, O(2, 6)));                                       // on the line
			Assert.Empty(HospitalLabourRules.LineWarnings(start, O(2, 7)));                                       // ahead of it
			Assert.Equal(new[] { HospitalLabourWarning.AlertLine }, HospitalLabourRules.LineWarnings(start, O(3, 5)));
			Assert.Equal(new[] { HospitalLabourWarning.ActionLine }, HospitalLabourRules.LineWarnings(start, O(6, 5)));   // expected 6 four hours earlier
			Assert.Empty(HospitalLabourRules.LineWarnings(start, O(9, 10)));                                      // fully dilated
			Assert.Empty(HospitalLabourRules.LineWarnings(null, O(5, 3)));                                        // latent phase: no lines
		}

		[Fact]
		public void Each_observation_raises_its_own_warnings()
		{
			Assert.Empty(HospitalLabourRules.Warnings(null, O(1, null, fhr: 140, liq: "C", ctx: 4, s: 120, d: 80, t: 37.2m)));
			Assert.Equal(new[] { HospitalLabourWarning.FetalHeart }, HospitalLabourRules.Warnings(null, O(1, null, fhr: 100)));
			Assert.Equal(new[] { HospitalLabourWarning.Meconium }, HospitalLabourRules.Warnings(null, O(1, null, liq: "M")));
			Assert.Equal(new[] { HospitalLabourWarning.Tachysystole }, HospitalLabourRules.Warnings(null, O(1, null, ctx: 6)));
			Assert.Equal(new[] { HospitalLabourWarning.Hypertension }, HospitalLabourRules.Warnings(null, O(1, null, s: 150, d: 95)));
			Assert.Equal(new[] { HospitalLabourWarning.Fever }, HospitalLabourRules.Warnings(null, O(1, null, t: 38.2m)));
		}

		[Fact]
		public void An_observation_is_plausible_and_within_the_labour()
		{
			var now = T0.AddHours(4);
			Assert.Null(HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { CervixCm = 6 }, now, T0, now, 5m));
			Assert.Equal("Write at least one measure or a note", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput(), now, T0, now, null));
			Assert.Equal("The time is in the future", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { CervixCm = 6 }, now.AddHours(1), T0, now, null));
			Assert.Equal("The time is before the labour started", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { CervixCm = 6 }, T0.AddHours(-1), T0, now, null));
			Assert.Equal("The cervix cannot close by more than a centimetre: check the reading", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { CervixCm = 4 }, now, T0, now, 7m));
			Assert.Equal("The cervix is between 0 and 10 cm", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { CervixCm = 11 }, now, T0, now, null));
			Assert.Equal("Choose the liquor", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { Liquor = "X" }, now, T0, now, null));
			Assert.Equal("Write both blood pressure numbers", HospitalLabourRules.ObservationRefusal(new HospitalLabourObservationInput { BpSystolic = 120 }, now, T0, now, null));
		}

		[Fact]
		public void The_start_may_be_written_back_a_day_but_not_before_the_admission()
		{
			var now = T0.AddHours(10);
			Assert.Null(HospitalLabourRules.StartRefusal(now.AddHours(-6), now.AddDays(-1), now));
			Assert.Equal("The time is in the future", HospitalLabourRules.StartRefusal(now.AddHours(1), now.AddDays(-1), now));
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
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h86_labour.sql");
			var m = HospitalSliceParity.Model(HospitalLabourModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalLabour), "HospitalLabours"), (typeof(HospitalLabourObservation), "HospitalLabourObservations") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
				Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == t);
			}
			foreach (var x in HospitalLabourOutcome.All.Concat(HospitalLiquor.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
		}
	}
}
