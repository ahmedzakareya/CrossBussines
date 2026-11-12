using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H110 — a drawing's results become observations, with no database: they are read from the state the SERVER
	// stored (its own figures) — burn %TBSA, the hearing average of each ear, DAS28 — never from what a browser sent; a
	// drawing with no result, or a state that cannot be read, gives none.
	// ==========================================================================================
	public class HospitalCanvasResultsTests
	{
		[Fact]
		public void Each_kind_gives_its_results_from_the_stored_state()
		{
			var burn = HospitalBurnRules.Store(HospitalBurnRules.Read("{\"d\":{\"trunkF\":3,\"armRF\":2},\"kg\":70}", 35).Result!);
			Assert.Equal(new[] { ("TBSA", (string?)null, 22.5m) }, HospitalCanvasRules.Results(HospitalCanvasKind.Burn, burn));

			var audio = HospitalAudioRules.Store(HospitalAudioRules.Read("{\"p\":{\"AR500\":30,\"AR1000\":35,\"AR2000\":40,\"AR4000\":45,\"AL500\":15,\"AL1000\":15,\"AL2000\":20,\"AL4000\":30}}").Result!);
			Assert.Equal(new[] { ("PTA", (string?)"R", 37.5m), ("PTA", (string?)"L", 20m) }, HospitalCanvasRules.Results(HospitalCanvasKind.Audio, audio));

			var das = HospitalDas28Rules.Store(HospitalDas28Rules.Read("{\"j\":{\"WrR\":1,\"WrL\":1,\"KnR\":3,\"MCP1R\":3,\"MCP2R\":1,\"PIP1R\":3,\"PIP2L\":2},\"esr\":32,\"pga\":55}").Result!);
			Assert.Equal(new[] { ("DAS28", (string?)null, 5.13m) }, HospitalCanvasRules.Results(HospitalCanvasKind.Joints, das));
		}

		[Fact]
		public void No_result_is_invented()
		{
			Assert.Empty(HospitalCanvasRules.Results(HospitalCanvasKind.Burn, HospitalBurnRules.Store(HospitalBurnRules.Read(null, 35).Result!)));   // nothing burnt
			Assert.Empty(HospitalCanvasRules.Results(HospitalCanvasKind.Joints, HospitalDas28Rules.Store(HospitalDas28Rules.Read("{\"j\":{\"WrR\":1}}").Result!)));   // no ESR: no score
			Assert.Empty(HospitalCanvasRules.Results(HospitalCanvasKind.Audio, "not json"));
			Assert.Empty(HospitalCanvasRules.Results(HospitalCanvasKind.Body, "{}"));
			Assert.Empty(HospitalCanvasRules.ResultCodes(HospitalCanvasKind.Pedigree));
		}

		[Fact]
		public void The_canvas_is_an_observation_source() => Assert.Contains(HospitalObsSource.Canvas, HospitalObsSource.All);
	}
}
