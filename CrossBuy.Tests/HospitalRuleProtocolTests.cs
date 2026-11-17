using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H174 — the protocol values of two rules come from the register, not constants: the immunotherapy
	// restart after a systemic reaction and the large local wheal, and the carboplatin GFR cap.
	// ==========================================================================================
	public class HospitalRuleProtocolTests
	{
		private static HospitalImmunoDose D(int seq, string vial, decimal ml, string reaction) => new() { Seq = seq, Vial = vial, DoseMl = ml, Reaction = reaction };

		[Fact]
		public void Immunotherapy_restarts_the_approved_number_of_steps_lower()
		{
			var c = new HospitalImmunoCourse { Status = HospitalImmunoStatus.Active };
			var last = new[] { D(10, "1:10", 0.30m, HospitalImmunoReaction.Systemic) };   // the 5th step of 1:10
			Assert.Equal(("1:10", 0.15m), (HospitalImmunoRules.Next(c, last).Vial, HospitalImmunoRules.Next(c, last).DoseMl));   // published: 2 steps
			var three = HospitalImmunoRules.Next(c, last, new HospitalImmunoProtocol(RestartStepsDown: 3));
			Assert.Equal(("1:10", 0.10m), (three.Vial, three.DoseMl));
		}

		[Fact]
		public void A_large_local_reaction_is_the_approved_wheal()
		{
			var now = new DateTime(2026, 11, 17, 10, 0, 0);
			var d = new HospitalImmunoDose { Reaction = HospitalImmunoReaction.Pending, ObserveUntil = now };
			var input = new HospitalImmunoReactionInput { Reaction = HospitalImmunoReaction.Local, WhealMm = 22 };
			Assert.Equal(HospitalImmunoReaction.Local, HospitalImmunoRules.Classify(input, now.AddMinutes(1), d).Reaction);
			Assert.Equal(HospitalImmunoReaction.LargeLocal, HospitalImmunoRules.Classify(input, now.AddMinutes(1), d, new HospitalImmunoProtocol(LargeLocalMm: 20)).Reaction);
		}

		[Fact]
		public void The_carboplatin_gfr_cap_is_the_approved_one()
		{
			Assert.Equal(125m, HospitalOncologyRules.Gfr(20, 100, 0.5m, false));
			Assert.Equal(110m, HospitalOncologyRules.Gfr(20, 100, 0.5m, false, cap: 110));
		}
	}
}
