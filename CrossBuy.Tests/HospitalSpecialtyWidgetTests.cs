using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H190 — anticoagulation control, the thyroid in pregnancy and sudden hearing loss. What must never happen:
	// a TTR that counts an unfollowed gap, a pregnant woman's high TSH read against the non-pregnant range, or a sudden
	// sensorineural loss shown as an ordinary asymmetry.
	// ==========================================================================================
	public class HospitalSpecialtyWidgetTests
	{
		private static readonly DateTime D0 = new(2026, 9, 1);

		[Fact]
		public void TTR_interpolates_day_by_day_between_readings()
		{
			// 2.0 → 4.0 over 10 days: 2.0, 2.2 … 3.0 in range (6 of 10 days)
			Assert.Equal((60m, 10), HospitalSpecialtyWidgetRules.Ttr(new[] { (D0, 2.0m), (D0.AddDays(10), 4.0m) }, 2, 3));
			Assert.Equal((100m, 20), HospitalSpecialtyWidgetRules.Ttr(new[] { (D0, 2.5m), (D0.AddDays(10), 2.5m), (D0.AddDays(20), 2.8m) }, 2, 3));
			// a gap over 56 days is not followed: only the second interval counts
			Assert.Equal((0m, 14), HospitalSpecialtyWidgetRules.Ttr(new[] { (D0, 2.5m), (D0.AddDays(90), 4.0m), (D0.AddDays(104), 4.0m) }, 2, 3));
			Assert.Equal(((decimal?)null, 0), HospitalSpecialtyWidgetRules.Ttr(new[] { (D0, 2.5m) }, 2, 3));
		}

		[Fact]
		public void TTR_under_65_percent_is_poor_control()
		{
			var c = HospitalSpecialtyWidgetRules.Card(new[] { (D0, 2.0m), (D0.AddDays(10), 4.0m) }, 2, 3)!;
			Assert.True(c.Poor);
			Assert.Equal((4.0m, 2, 10), (c.LastInr, c.Readings, c.Days));
			Assert.False(HospitalSpecialtyWidgetRules.Card(new[] { (D0, 2.5m), (D0.AddDays(10), 2.5m) }, null, null)!.Poor);   // 2.0–3.0 by default
			Assert.Null(HospitalSpecialtyWidgetRules.Card(Array.Empty<(DateTime, decimal)>(), 2, 3));
		}

		[Fact]
		public void TSH_in_pregnancy_is_read_against_the_pregnancy_range()
		{
			var edd = new DateTime(2027, 4, 1);
			var at = edd.AddDays(-280 + 10 * 7);   // 10 weeks
			var hi = HospitalSpecialtyWidgetRules.PregnancyTsh(4.6m, at, null, edd)!;
			Assert.Equal(("High", 10, 1), (hi.Verdict, hi.GaWeeks, hi.Trimester));
			Assert.Equal("InRange", HospitalSpecialtyWidgetRules.PregnancyTsh(3.8m, at, null, edd)!.Verdict);   // 3.8: high for some labs' adult range, in range here
			Assert.Equal("Low", HospitalSpecialtyWidgetRules.PregnancyTsh(0.05m, at, null, edd)!.Verdict);
			Assert.Equal(2, HospitalSpecialtyWidgetRules.Trimester(20));
			Assert.Equal(3, HospitalSpecialtyWidgetRules.Trimester(28));
			Assert.Equal(12, HospitalSpecialtyWidgetRules.GaWeeks(new DateTime(2026, 8, 1), null, new DateTime(2026, 8, 1).AddDays(86)));   // from the last period
			Assert.Null(HospitalSpecialtyWidgetRules.PregnancyTsh(4.6m, at, null, null));   // no dates: no gestational age
		}

		[Fact]
		public void A_30_dB_sensorineural_loss_over_three_frequencies_is_found_and_made_urgent_by_its_onset()
		{
			var p = new Dictionary<string, int>
			{
				["AR250"] = 60, ["AR500"] = 60, ["AR1000"] = 60, ["AR2000"] = 10, ["AR4000"] = 10, ["AR8000"] = 10,
				["AL250"] = 20, ["AL500"] = 20, ["AL1000"] = 25, ["AL2000"] = 10, ["AL4000"] = 10, ["AL8000"] = 10,
			};
			Assert.True(HospitalAudioRules.Snhl(p, "AR"));
			Assert.False(HospitalAudioRules.Snhl(p, "AL"));
			var conductive = new Dictionary<string, int>(p) { ["BR500"] = 40 };   // an air–bone gap at 500 breaks the run
			Assert.False(HospitalAudioRules.Snhl(conductive, "AR"));
			var two = new Dictionary<string, int>(p) { ["AR1000"] = 40 };          // only two frequencies 30 dB apart
			Assert.False(HospitalAudioRules.Snhl(two, "AR"));

			string State(bool sudden) => System.Text.Json.JsonSerializer.Serialize(new { p, sudden });
			var (calm, _) = HospitalAudioRules.Read(State(false));
			var (urgent, _) = HospitalAudioRules.Read(State(true));
			Assert.True(calm!.SnhlR); Assert.False(calm.SuddenLoss);
			Assert.True(urgent!.SuddenLoss);
			Assert.Contains("\"sudden\":true", HospitalAudioRules.Store(urgent));
		}
	}
}
