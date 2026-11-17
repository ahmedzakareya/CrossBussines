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
			Assert.True(r.Refer);                  // H172: a full-thickness burn
			Assert.Equal(new[] { "full" }, r.Why);
			Assert.False(r.Depths.ContainsKey("legLB"));   // depth 0 is not a burn
			var stored = HospitalBurnRules.Store(r);
			Assert.DoesNotContain("script", stored);
			Assert.Contains("\"tbsa\":22.5", stored);
			// a superficial partial burn counts; below the fluids share there is no Parkland figure (H172)
			var (s, _) = HospitalBurnRules.Read("{\"d\":{\"armRF\":1},\"kg\":70}", 35);
			Assert.Equal(4.5m, s!.Tbsa); Assert.Null(s.FluidsMl); Assert.False(s.FluidsIndicated); Assert.False(s.Refer);
		}

		[Fact]
		public void Referral_follows_the_aba_criteria_each_reason_named()
		{
			var lim = new HospitalBurnThresholds();
			// 18 % partial on the trunk: above 10 % → refer; the fluids start at 20 % for an adult
			var (a, _) = HospitalBurnRules.Read("{\"d\":{\"trunkF\":2},\"kg\":70}", 35, lim);
			Assert.Equal((18m, true, false), (a!.Partial, a.Refer, a.FluidsIndicated));
			Assert.Equal(new[] { "partial" }, a.Why);
			// the face and the perineum, the hands and the mechanism the chart cannot draw
			var (b, _) = HospitalBurnRules.Read("{\"d\":{\"headF\":1,\"groinF\":1},\"hf\":true,\"mx\":true}", 35, lim);
			Assert.Equal(new[] { "face", "perineum", "hands", "mechanism" }, b!.Why);
			// a child under 15: fluids from 10 %
			var (c, _) = HospitalBurnRules.Read("{\"d\":{\"trunkF\":1},\"kg\":20}", 6, lim);
			Assert.True(c!.FluidsIndicated); Assert.Equal(1440, c.FluidsMl);   // 4 × 20 × 18
			// the clinic's approved thresholds change the answer
			var (d, _) = HospitalBurnRules.Read("{\"d\":{\"trunkF\":2}}", 35, new HospitalBurnThresholds(ReferPartialPct: 20));
			Assert.False(d!.Refer);
		}

		[Fact]
		public void The_paper_reads_what_the_server_stored()
		{
			var (r, _) = HospitalBurnRules.Read("{\"d\":{\"trunkF\":3,\"trunkB\":3},\"kg\":70,\"mx\":true}", 35);
			var (fl, why, refer) = HospitalBurnRules.Saved(HospitalBurnRules.Store(r!));
			Assert.Equal((10080, true), (fl!.Value, refer));   // 4 × 70 × 36
			Assert.Equal(new[] { "full", "mechanism" }, why);
			Assert.Empty(HospitalBurnRules.Saved("{\"refer\":true,\"why\":[\"<x>\"]}").Why);   // an unknown reason is not printed
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
