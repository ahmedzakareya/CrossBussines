using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H212 — clinical surveillance. What must never happen: a qSOFA of 2 not flagged, SIRS alone (no infection)
	// flagged as sepsis, a second sepsis alert while one is open or within 12 hours, a "not sepsis" or "not indicated"
	// answer without its reason, an answer from the other kind's list, or an alert answered twice.
	// ==========================================================================================
	public class HospitalSurveillanceTests
	{
		private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
		private static HospitalObservation O(int? rr = 16, int? sbp = 125, string? avpu = "A", decimal? t = 37m, int? hr = 80) =>
			new() { ID = 5, RespRate = rr, SystolicBp = sbp, Consciousness = avpu, TempC = t, HeartRate = hr, TakenAt = Now };

		[Fact]
		public void QSOFA_of_two_fires_whatever_the_infection_record()
		{
			var hit = HospitalSurveillanceRules.Sepsis(O(rr: 24, sbp: 96), infection: false);
			Assert.NotNull(hit);
			Assert.Equal(2, hit!.Value.Score);
			Assert.Equal("qSOFA;RR=24;SBP=96", hit.Value.Criteria);
			Assert.Equal("qSOFA;RR=22;AVPU=C", HospitalSurveillanceRules.Sepsis(O(rr: 22, avpu: "C"), false)!.Value.Criteria);
			Assert.Null(HospitalSurveillanceRules.Sepsis(O(rr: 24), false));   // one criterion is not enough
			Assert.Null(HospitalSurveillanceRules.Sepsis(O(), true));
		}

		[Fact]
		public void SIRS_fires_only_with_an_infection_on_record()
		{
			var febrile = O(t: 38.6m, hr: 112);
			Assert.Null(HospitalSurveillanceRules.Sepsis(febrile, infection: false));
			var hit = HospitalSurveillanceRules.Sepsis(febrile, infection: true);
			Assert.Equal("SIRS;T=38.6;HR=112;INF", hit!.Value.Criteria);
			Assert.Equal(2, HospitalSurveillanceRules.Sirs(35.5m, null, 21).Score);
			Assert.Equal(0, HospitalSurveillanceRules.Sirs(38m, 90, 20).Score);   // the limits themselves are normal
		}

		[Fact]
		public void Sepsis_fires_once_while_open_and_again_only_twelve_hours_later()
		{
			var open = new HospitalSurveillanceAlert { Kind = HospitalSurveillanceKind.Sepsis, Status = HospitalSurveillanceStatus.Open, RaisedAt = Now.AddHours(-20) };
			Assert.False(HospitalSurveillanceRules.SepsisMayFire(new[] { open }, Now));
			var answered = new HospitalSurveillanceAlert { Kind = HospitalSurveillanceKind.Sepsis, Status = HospitalSurveillanceStatus.Answered, RaisedAt = Now.AddHours(-3) };
			Assert.False(HospitalSurveillanceRules.SepsisMayFire(new[] { answered }, Now));
			Assert.True(HospitalSurveillanceRules.SepsisMayFire(new[] { answered }, Now.AddHours(9)));
			Assert.True(HospitalSurveillanceRules.SepsisMayFire(new[] { new HospitalSurveillanceAlert { Kind = HospitalSurveillanceKind.Vte, Status = HospitalSurveillanceStatus.Open } }, Now));
		}

		[Fact]
		public void An_answer_is_from_its_kinds_list_and_a_no_carries_its_reason()
		{
			var sepsis = new HospitalSurveillanceAlert { Kind = HospitalSurveillanceKind.Sepsis, Status = HospitalSurveillanceStatus.Open };
			Assert.Null(HospitalSurveillanceRules.AnswerRefusal(sepsis, HospitalSurveillanceResponse.BundleStarted, null));
			Assert.Equal("Write the reason for this decision", HospitalSurveillanceRules.AnswerRefusal(sepsis, HospitalSurveillanceResponse.NotSepsis, " "));
			Assert.Null(HospitalSurveillanceRules.AnswerRefusal(sepsis, HospitalSurveillanceResponse.NotSepsis, "Fever from a transfusion reaction"));
			Assert.Equal("Choose the decision", HospitalSurveillanceRules.AnswerRefusal(sepsis, HospitalSurveillanceResponse.ProphylaxisOrdered, null));
			var vte = new HospitalSurveillanceAlert { Kind = HospitalSurveillanceKind.Vte, Status = HospitalSurveillanceStatus.Open };
			Assert.Null(HospitalSurveillanceRules.AnswerRefusal(vte, HospitalSurveillanceResponse.ProphylaxisOrdered, null));
			Assert.NotNull(HospitalSurveillanceRules.AnswerRefusal(vte, HospitalSurveillanceResponse.Contraindicated, null));
			vte.Status = HospitalSurveillanceStatus.Answered;
			Assert.Equal("This alert is already answered", HospitalSurveillanceRules.AnswerRefusal(vte, HospitalSurveillanceResponse.NotIndicated, "x"));
		}

		[Fact]
		public void An_open_alert_past_its_time_is_overdue()
		{
			var a = new HospitalSurveillanceAlert { Status = HospitalSurveillanceStatus.Open, DueAt = Now };
			Assert.False(HospitalSurveillanceRules.Overdue(a, Now));
			Assert.True(HospitalSurveillanceRules.Overdue(a, Now.AddMinutes(1)));
			a.Status = HospitalSurveillanceStatus.Answered;
			Assert.False(HospitalSurveillanceRules.Overdue(a, Now.AddDays(1)));
		}

		[Fact]
		public void The_laboratory_counts_a_white_count_and_a_raised_lactate()
		{
			// SIRS of two from one vital sign and the white count
			Assert.Equal("SIRS;HR=104;WBC=15.2;INF", HospitalSurveillanceRules.Sepsis(O(hr: 104), infection: true, wbc: 15.2m)!.Value.Criteria);
			Assert.Null(HospitalSurveillanceRules.Sepsis(O(hr: 104), infection: true, wbc: 8m));
			// a lactate of 2 or more with an infection fires even when the vital signs do not
			Assert.Equal("LACTATE;LAC=2.6;INF", HospitalSurveillanceRules.Sepsis(O(), infection: true, lactate: 2.6m)!.Value.Criteria);
			Assert.Null(HospitalSurveillanceRules.Sepsis(O(), infection: false, lactate: 2.6m));
			Assert.Null(HospitalSurveillanceRules.Sepsis(O(), infection: true, lactate: 1.4m));
			// qSOFA carries the lactate beside it
			Assert.Equal("qSOFA;RR=24;SBP=96;LAC=4.1", HospitalSurveillanceRules.Sepsis(O(rr: 24, sbp: 96), false, lactate: 4.1m)!.Value.Criteria);
		}

		[Fact]
		public void A_VTE_answer_is_scored_by_Padua_and_a_high_risk_patient_is_never_not_indicated()
		{
			Assert.Equal(0, HospitalSurveillanceRules.Padua(Array.Empty<string>()));
			Assert.Equal(5, HospitalSurveillanceRules.Padua(new[] { "cancer", "age70", "obesity" }));
			Assert.Equal(3, HospitalSurveillanceRules.Padua(new[] { "immobility", "immobility" }));   // an item counts once
			Assert.Equal("Score the Padua items first", HospitalSurveillanceRules.VteRefusal(HospitalSurveillanceResponse.ProphylaxisOrdered, null));
			Assert.Null(HospitalSurveillanceRules.VteRefusal(HospitalSurveillanceResponse.NotIndicated, new[] { "age70" }));
			Assert.NotNull(HospitalSurveillanceRules.VteRefusal(HospitalSurveillanceResponse.NotIndicated, new[] { "cancer", "age70" }));
			Assert.Null(HospitalSurveillanceRules.VteRefusal(HospitalSurveillanceResponse.Contraindicated, new[] { "cancer", "age70" }));
			Assert.Equal("not_found", HospitalSurveillanceRules.VteRefusal(HospitalSurveillanceResponse.ProphylaxisOrdered, new[] { "smoking" }));
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalSurveillanceModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h212_surveillance.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalSurveillanceAlert), "HospitalSurveillanceAlerts");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalSurveillanceAlert), "HospitalSurveillanceAlerts");
			foreach (var x in HospitalSurveillanceKind.All.Concat(HospitalSurveillanceStatus.All).Concat(HospitalSurveillanceResponse.All)) Assert.Contains("'" + x + "'", s);
		}
	}
}
