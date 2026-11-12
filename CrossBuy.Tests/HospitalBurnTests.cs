using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H100 — the burn chart, with no database: the rule of nines adds up to 100 % at every age (Lund–Browder's
	// larger child head comes off the legs), %TBSA and Parkland are the server's, and an unknown region, depth or weight
	// is refused — nothing the browser sent besides the depths and the weight is stored.
	// ==========================================================================================
	public class HospitalBurnTests
	{
		[Theory]
		[InlineData(null)] [InlineData(0)] [InlineData(3)] [InlineData(7)] [InlineData(12)] [InlineData(40)]
		public void Every_age_adds_up_to_the_whole_body(int? age) => Assert.Equal(100m, HospitalBurnRules.Share(age).Values.Sum());

		[Fact]
		public void A_newborn_has_a_larger_head_and_smaller_legs()
		{
			var s = HospitalBurnRules.Share(0);
			Assert.Equal(9.5m, s["headF"]);       // Lund–Browder: 19 % for the whole head at birth
			Assert.Equal(6.5m, s["legRF"]);       // the extra 10 % comes off the four leg halves
			Assert.Equal(4.5m, HospitalBurnRules.Share(30)["headB"]);
		}

		[Fact]
		public void Tbsa_and_parkland_are_the_servers()
		{
			var (r, err) = HospitalBurnRules.Read("{\"d\":{\"trunkF\":3,\"armRF\":2,\"legLB\":0},\"kg\":70,\"tbsa\":99,\"x\":\"<script>\"}", 35);
			Assert.Null(err);
			Assert.Equal(22.5m, r!.Tbsa);          // 18 + 4.5: the browser's "tbsa" is ignored
			Assert.Equal(6300, r.FluidsMl);        // 4 mL × 70 kg × 22.5
			Assert.True(r.Refer);                  // ≥ 20 %
			Assert.False(r.Depths.ContainsKey("legLB"));   // depth 0 is not a burn
			var stored = HospitalBurnRules.Store(r);
			Assert.DoesNotContain("script", stored);
			Assert.Contains("\"tbsa\":22.5", stored);
			// a superficial partial burn counts; no weight → no fluids, said so on the page
			var (s, _) = HospitalBurnRules.Read("{\"d\":{\"headF\":1}}", 35);
			Assert.Equal(4.5m, s!.Tbsa); Assert.Null(s.FluidsMl); Assert.False(s.Refer);
		}

		[Fact]
		public void An_unknown_region_depth_or_weight_is_refused()
		{
			Assert.Equal("The drawing's data could not be read", HospitalBurnRules.Read("{\"d\":{\"tailF\":2}}", 30).Error);
			Assert.Equal("The drawing's data could not be read", HospitalBurnRules.Read("{\"d\":{\"groinB\":2}}", 30).Error);   // the perineum has no back
			Assert.Equal("The drawing's data could not be read", HospitalBurnRules.Read("{\"d\":{\"armRF\":4}}", 30).Error);
			Assert.Equal("The weight is between 0 and 400 kg", HospitalBurnRules.Read("{\"kg\":500}", 30).Error);
			Assert.Equal("The drawing's data could not be read", HospitalBurnRules.Read("[1]", 30).Error);
			Assert.Equal(0m, HospitalBurnRules.Read(null, 30).Result!.Tbsa);
		}
	}
}
