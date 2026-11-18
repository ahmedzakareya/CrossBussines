using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H87 — haemodialysis, with no database: the weekday mask, the volume to remove, URR and Daugirdas' spKt/V,
	// each computed warning at its edge, what a plan / start / observation / finish / missed day needs, and the tables
	// against their slice.
	// ==========================================================================================
	public class HospitalDialysisTests
	{
		private static readonly DateTime T0 = new(2026, 11, 10, 7, 0, 0);

		private static HospitalDialysisSession S(decimal pre = 74m, decimal? post = 71.5m, int min = 240, int? uf = null, decimal? preU = null, decimal? postU = null, int? preSbp = 140, int? postSbp = 130) => new()
		{
			Status = HospitalDialysisSessionStatus.Done, DryWeightKg = 71m, PlannedMin = 240, StartedAt = T0, EndedAt = T0.AddMinutes(min),
			PreWeightKg = pre, PostWeightKg = post, UfMl = uf, PreUrea = preU, PostUrea = postU, PreSbp = preSbp, PostSbp = postSbp,
		};

		private static HospitalDialysisPlanInput Plan() => new() { PatientId = 1, DryWeightKg = 71, Access = HospitalDialysisAccess.Fistula, DurationMin = 240, BloodFlow = 300, DaysMask = 2 | 8 | 32, Shift = 1 };

		[Fact]
		public void The_weekday_mask_reads_sunday_as_one()
		{
			var tuesday = new DateTime(2026, 11, 10);   // a Tuesday
			Assert.Equal(4, HospitalDialysisRules.DayBit(tuesday));
			Assert.True(HospitalDialysisRules.Scheduled(2 | 8 | 32, tuesday.AddDays(-1)));   // Monday
			Assert.False(HospitalDialysisRules.Scheduled(2 | 8 | 32, tuesday));
			Assert.Equal(3, HospitalDialysisRules.DaysPerWeek(2 | 8 | 32));
		}

		[Fact]
		public void The_volume_to_remove_is_the_weight_above_the_dry_weight()
		{
			Assert.Equal(3000, HospitalDialysisRules.UfGoal(74m, 71m));
			Assert.Equal(0, HospitalDialysisRules.UfGoal(70.5m, 71m));   // under the dry weight: nothing to remove
		}

		[Fact]
		public void Urr_and_daugirdas_ktv()
		{
			Assert.Equal(70m, HospitalDialysisRules.Urr(100m, 30m));
			Assert.Null(HospitalDialysisRules.Urr(100m, 120m));
			Assert.Equal(1.44m, HospitalDialysisRules.KtV(100m, 30m, 4, 3000, 70m));    // −ln(0.268) + 2.95 × 3 / 70
			Assert.Equal(1.24m, HospitalDialysisRules.KtV(100m, 35m, 4, 2500, 72m));
			Assert.Null(HospitalDialysisRules.KtV(100m, 30m, 0, 3000, 70m));
		}

		[Fact]
		public void A_good_session_has_no_warning()
		{
			var o = HospitalDialysisRules.Outcome(S(preU: 100m, postU: 30m), 71.2m, Array.Empty<HospitalDialysisObservation>());
			Assert.Equal(2.8m, o.GainKg);
			Assert.Equal(3.9m, o.GainPct);
			Assert.Equal(2500, o.UfMl);                 // the weight lost, when the machine's volume is not written
			Assert.Equal(8.7m, o.UfRate);               // 2500 / 71.5 / 4
			Assert.Empty(o.Warnings);
		}

		[Fact]
		public void Each_warning_fires_at_its_edge()
		{
			var none = Array.Empty<HospitalDialysisObservation>();
			Assert.Contains(HospitalDialysisWarning.HighGain, HospitalDialysisRules.Outcome(S(pre: 74m), 70.9m, none).Warnings);           // 3.1 kg = 4.4 %
			Assert.Contains(HospitalDialysisWarning.HighUfRate, HospitalDialysisRules.Outcome(S(min: 180, uf: 3200), null, none).Warnings); // 3200 / 71.5 / 3 = 14.9
			Assert.Contains(HospitalDialysisWarning.LowUrr, HospitalDialysisRules.Outcome(S(preU: 100m, postU: 40m), null, none).Warnings);
			Assert.Contains(HospitalDialysisWarning.LowKtv, HospitalDialysisRules.Outcome(S(preU: 100m, postU: 40m), null, none).Warnings);
			Assert.Contains(HospitalDialysisWarning.Short, HospitalDialysisRules.Outcome(S(min: 220), null, none).Warnings);
			Assert.DoesNotContain(HospitalDialysisWarning.Short, HospitalDialysisRules.Outcome(S(min: 226), null, none).Warnings);
			Assert.Contains(HospitalDialysisWarning.AboveDry, HospitalDialysisRules.Outcome(S(post: 71.6m), null, none).Warnings);
			Assert.DoesNotContain(HospitalDialysisWarning.AboveDry, HospitalDialysisRules.Outcome(S(post: 71.5m), null, none).Warnings);
		}

		[Fact]
		public void Hypotension_is_a_nadir_under_90_or_a_symptomatic_fall_of_20()
		{
			HospitalDialysisObservation At(int sbp) => new() { At = T0.AddHours(1), Sbp = sbp, Dbp = 50 };
			Assert.Contains(HospitalDialysisWarning.Hypotension, HospitalDialysisRules.Outcome(S(preSbp: 110, postSbp: 105), null, new[] { At(88) }).Warnings);
			Assert.DoesNotContain(HospitalDialysisWarning.Hypotension, HospitalDialysisRules.Outcome(S(preSbp: 160, postSbp: 150), null, new[] { At(140) }).Warnings);   // no symptom
			var cramps = S(preSbp: 160, postSbp: 150); cramps.Complications = "Clotting,Cramps";
			Assert.Contains(HospitalDialysisWarning.Hypotension, HospitalDialysisRules.Outcome(cramps, null, new[] { At(140) }).Warnings);
			var clot = S(preSbp: 160, postSbp: 150); clot.Complications = "Clotting";
			Assert.DoesNotContain(HospitalDialysisWarning.Hypotension, HospitalDialysisRules.Outcome(clot, null, new[] { At(140) }).Warnings);
			var c2 = S(preSbp: 160, postSbp: 150); c2.Complications = "Cramps";
			Assert.DoesNotContain(HospitalDialysisWarning.Hypotension, HospitalDialysisRules.Outcome(c2, null, new[] { At(141) }).Warnings);
		}

		[Fact]
		public void A_plan_needs_its_prescription()
		{
			Assert.Null(HospitalDialysisRules.PlanRefusal(Plan()));
			var p = Plan(); p.DryWeightKg = 10; Assert.Equal("The dry weight is between 20 and 250 kg", HospitalDialysisRules.PlanRefusal(p));
			p = Plan(); p.Access = "XYZ"; Assert.Equal("Choose the vascular access", HospitalDialysisRules.PlanRefusal(p));
			p = Plan(); p.DurationMin = 30; Assert.Equal("A session lasts between 1 and 8 hours", HospitalDialysisRules.PlanRefusal(p));
			p = Plan(); p.DaysMask = 0; Assert.Equal("Choose the dialysis days", HospitalDialysisRules.PlanRefusal(p));
			p = Plan(); p.Shift = 4; Assert.Equal("Choose the shift", HospitalDialysisRules.PlanRefusal(p));
		}

		[Fact]
		public void A_session_starts_weighed_and_ends_weighed()
		{
			var now = T0.AddHours(5);
			Assert.Null(HospitalDialysisRules.StartRefusal(new HospitalDialysisStartInput { PreWeightKg = 74, PreSbp = 140, PreDbp = 80 }, now, now));
			Assert.Equal("Weigh the patient before the session", HospitalDialysisRules.StartRefusal(new HospitalDialysisStartInput(), now, now));
			Assert.Equal("Write both blood pressure numbers", HospitalDialysisRules.StartRefusal(new HospitalDialysisStartInput { PreWeightKg = 74, PreSbp = 140 }, now, now));
			Assert.Equal("The time is in the future", HospitalDialysisRules.StartRefusal(new HospitalDialysisStartInput { PreWeightKg = 74 }, now.AddHours(1), now));

			var running = S(); running.Status = HospitalDialysisSessionStatus.Running; running.EndedAt = null;
			Assert.Null(HospitalDialysisRules.FinishRefusal(running, new HospitalDialysisFinishInput { PostWeightKg = 71.5m }, now, now));
			Assert.Equal("Weigh the patient after the session", HospitalDialysisRules.FinishRefusal(running, new HospitalDialysisFinishInput(), now, now));
			Assert.Equal("The end is before the start", HospitalDialysisRules.FinishRefusal(running, new HospitalDialysisFinishInput { PostWeightKg = 71.5m }, T0, now));
			Assert.Equal("Write both urea values, before and after", HospitalDialysisRules.FinishRefusal(running, new HospitalDialysisFinishInput { PostWeightKg = 71.5m, PreUrea = 100 }, now, now));
			Assert.Equal("The urea after must be below the urea before", HospitalDialysisRules.FinishRefusal(running, new HospitalDialysisFinishInput { PostWeightKg = 71.5m, PreUrea = 30, PostUrea = 100 }, now, now));
			Assert.Equal("Choose the complications from the list", HospitalDialysisRules.FinishRefusal(running, new HospitalDialysisFinishInput { PostWeightKg = 71.5m, Complications = new() { "Hiccups" } }, now, now));
			Assert.Equal("This session is not running", HospitalDialysisRules.FinishRefusal(S(), new HospitalDialysisFinishInput { PostWeightKg = 71.5m }, now, now));

			Assert.Equal("Write at least one measure or a note", HospitalDialysisRules.ObservationRefusal(new HospitalDialysisObservationInput(), now, T0, now));
			Assert.Equal("The time is before the session started", HospitalDialysisRules.ObservationRefusal(new HospitalDialysisObservationInput { Pulse = 80 }, T0.AddHours(-1), T0, now));
		}

		[Fact]
		public void A_missed_day_is_a_scheduled_day_with_a_reason()
		{
			var p = new HospitalDialysisPlan { Status = HospitalDialysisPlanStatus.Active, DaysMask = 2 | 8 | 32 };
			var today = new DateTime(2026, 11, 11);   // a Wednesday
			Assert.Null(HospitalDialysisRules.MissRefusal(p, today, today, "Travelling"));
			Assert.Equal("The patient is not scheduled on that day", HospitalDialysisRules.MissRefusal(p, today.AddDays(-1), today, "x"));
			Assert.Equal("A day that has not come cannot be missed", HospitalDialysisRules.MissRefusal(p, today.AddDays(2), today, "x"));
			Assert.Equal("Write why the session was missed", HospitalDialysisRules.MissRefusal(p, today, today, " "));
		}

		[Fact]
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h87_dialysis.sql");
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h119_session_charges.sql");   // H119: the bill line a session was charged on
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h122_unit_kits.sql");   // H122: when the session's kit left the store
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h183_independent_checks.sql");   // H183: the prescription's check, the machine check
			var m = HospitalSliceParity.Model(HospitalDialysisModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalDialysisPlan), "HospitalDialysisPlans"), (typeof(HospitalDialysisSession), "HospitalDialysisSessions"), (typeof(HospitalDialysisObservation), "HospitalDialysisObservations") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
				Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == t);
			}
			foreach (var x in HospitalDialysisAccess.All.Concat(HospitalDialysisSessionStatus.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(HospitalSliceParity.Slice("hospital_h87_dialysis.sql")));   // each slice guards its own prerequisites
		}
	}
}
