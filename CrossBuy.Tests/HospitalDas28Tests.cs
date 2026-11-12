using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H102 — DAS28-ESR, with no database: the 28 joints, the counts (both = tender AND swollen), the formula and
	// its EULAR bands; no score without an ESR and a patient global; what the joint map cannot hold is refused.
	// ==========================================================================================
	public class HospitalDas28Tests
	{
		[Fact]
		public void There_are_28_joints() => Assert.Equal(28, HospitalDas28Rules.Joints.Distinct().Count());

		[Fact]
		public void The_prototypes_patient()
		{
			// tender: WrR, WrL, MCP1R(both), MCP2L, PIP1L(both)…; the prototype's own sample: 6 tender, 3 swollen
			var (r, err) = HospitalDas28Rules.Read("{\"j\":{\"WrR\":1,\"WrL\":1,\"KnR\":3,\"MCP1R\":3,\"MCP2R\":1,\"PIP1R\":3,\"PIP2L\":2},\"esr\":32,\"pga\":55}");
			Assert.Null(err);
			Assert.Equal((6, 4), (r!.Tender, r.Swollen));
			// 0.56·√6 + 0.28·√4 + 0.70·ln 32 + 0.014·55 = 1.3717 + 0.56 + 2.4260 + 0.77 = 5.13
			Assert.Equal(5.13m, r.Das28);
			Assert.Equal("High", r.Band);
		}

		[Theory]
		[InlineData(5.11, "High")] [InlineData(5.1, "Moderate")] [InlineData(3.21, "Moderate")] [InlineData(3.2, "Low")] [InlineData(2.6, "Low")] [InlineData(2.59, "Remission")]
		public void Eular_bands(double das, string band) => Assert.Equal(band, HospitalDas28Rules.Band((decimal)das));

		[Fact]
		public void No_score_without_esr_and_global()
		{
			var (r, _) = HospitalDas28Rules.Read("{\"j\":{\"WrR\":1},\"esr\":20}");
			Assert.Null(r!.Das28); Assert.Null(r.Band); Assert.Equal(1, r.Tender);
		}

		[Fact]
		public void The_map_refuses_what_it_cannot_hold()
		{
			Assert.Equal("The drawing's data could not be read", HospitalDas28Rules.Read("{\"j\":{\"HipR\":1}}").Error);       // not one of the 28
			Assert.Equal("The drawing's data could not be read", HospitalDas28Rules.Read("{\"j\":{\"WrR\":4}}").Error);
			Assert.Equal("The ESR is between 1 and 150 mm/h", HospitalDas28Rules.Read("{\"esr\":0}").Error);
			Assert.Equal("The patient's global assessment is between 0 and 100", HospitalDas28Rules.Read("{\"pga\":120}").Error);
			Assert.Contains("\"das28\":5.13", HospitalDas28Rules.Store(HospitalDas28Rules.Read("{\"j\":{\"WrR\":1,\"WrL\":1,\"KnR\":3,\"MCP1R\":3,\"MCP2R\":1,\"PIP1R\":3,\"PIP2L\":2},\"esr\":32,\"pga\":55}").Result!));
		}
	}
}
