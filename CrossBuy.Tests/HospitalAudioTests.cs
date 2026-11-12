using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H101 — the audiogram, with no database: the pure-tone average of 500–4000 Hz and its WHO 2021 grade,
	// the air–bone gap (conductive), the asymmetry between the ears, and the points the chart cannot hold.
	// ==========================================================================================
	public class HospitalAudioTests
	{
		[Theory]
		[InlineData(10, "Normal")] [InlineData(19.9, "Normal")] [InlineData(20, "Mild")] [InlineData(35, "Moderate")] [InlineData(50, "Moderately severe")]
		[InlineData(65, "Severe")] [InlineData(80, "Profound")] [InlineData(95, "Complete")]
		public void Who_2021_grades(double pta, string grade) => Assert.Equal(grade, HospitalAudioRules.Grade((decimal)pta));

		[Fact]
		public void The_prototypes_patient()
		{
			// right: air 30/35/40/45 with bone 10/15/15/20 → PTA 37.5, moderate, conductive; left: air 15/15/20/30 → PTA 20, mild
			var s = "{\"p\":{\"AR250\":25,\"AR500\":30,\"AR1000\":35,\"AR2000\":40,\"AR4000\":45,\"AR8000\":50,\"BR500\":10,\"BR1000\":15,\"BR2000\":15,\"BR4000\":20," +
					"\"AL250\":10,\"AL500\":15,\"AL1000\":15,\"AL2000\":20,\"AL4000\":30,\"AL8000\":35}}";
			var (r, err) = HospitalAudioRules.Read(s);
			Assert.Null(err);
			Assert.Equal((37.5m, 20m), (r!.PtaR!.Value, r.PtaL!.Value));
			Assert.Equal(("Moderate", "Mild"), (r.GradeR, r.GradeL));
			Assert.True(r.GapR); Assert.False(r.GapL);
			Assert.True(r.Asymmetry);   // 17.5 dB apart
			Assert.Contains("\"ptaR\":37.5", HospitalAudioRules.Store(r));
		}

		[Fact]
		public void A_missing_frequency_gives_no_average()
		{
			var (r, _) = HospitalAudioRules.Read("{\"p\":{\"AR500\":30,\"AR1000\":35,\"AR2000\":40}}");
			Assert.Null(r!.PtaR); Assert.Null(r.GradeR); Assert.False(r.Asymmetry);
		}

		[Fact]
		public void The_chart_refuses_what_it_cannot_hold()
		{
			Assert.Equal("A threshold is between -10 and 120 dB, in 5 dB steps", HospitalAudioRules.Read("{\"p\":{\"AR500\":33}}").Error);
			Assert.Equal("A threshold is between -10 and 120 dB, in 5 dB steps", HospitalAudioRules.Read("{\"p\":{\"AR500\":130}}").Error);
			Assert.Equal("The drawing's data could not be read", HospitalAudioRules.Read("{\"p\":{\"AR750\":30}}").Error);
			Assert.Equal("The drawing's data could not be read", HospitalAudioRules.Read("{\"p\":{\"ZZ500\":30}}").Error);
			Assert.Equal("The drawing's data could not be read", HospitalAudioRules.Read("{\"p\":[1]}").Error);
		}
	}
}
