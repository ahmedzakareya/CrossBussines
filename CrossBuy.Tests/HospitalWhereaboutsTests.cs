using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H114 — where is the patient now, with no database: the most immediate place is read out first (an
	// operation before the ward bed the patient will return to), something happening this minute before something
	// waiting, and a desk search must name someone.
	// ==========================================================================================
	public class HospitalWhereaboutsTests
	{
		private static HospitalWhere W(string kind, bool live = false) => new(kind, kind, kind, null, null, live, null, null);

		[Fact]
		public void The_most_immediate_place_comes_first()
		{
			var order = HospitalWhereRules.Ordered(new[] { W(HospitalWhereKind.Ward), W(HospitalWhereKind.ClinicWaiting), W(HospitalWhereKind.Imaging, true), W(HospitalWhereKind.Theatre, true) })
				.Select(w => w.Kind).ToList();
			Assert.Equal(new[] { HospitalWhereKind.Theatre, HospitalWhereKind.Imaging, HospitalWhereKind.ClinicWaiting, HospitalWhereKind.Ward }, order);
		}

		[Fact]
		public void Within_a_kind_what_happens_now_comes_first()
		{
			var a = W(HospitalWhereKind.Imaging, false) with { Place = "waiting" };
			var b = W(HospitalWhereKind.Imaging, true) with { Place = "scanning" };
			Assert.Equal("scanning", HospitalWhereRules.Ordered(new[] { a, b })[0].Place);
		}

		[Fact]
		public void Every_kind_has_a_place_in_the_order()
		{
			Assert.Equal(6, HospitalWhereKind.Order.Distinct().Count());
			Assert.Equal(HospitalWhereKind.Ward, HospitalWhereKind.Order[^1]);   // where they belong comes after where they are
			Assert.True(HospitalWhereRules.MinQuery >= 2);
			Assert.Empty(HospitalWhereRules.Ordered(Array.Empty<HospitalWhere>()));
		}
	}
}
