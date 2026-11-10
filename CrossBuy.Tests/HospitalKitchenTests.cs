using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H44 — the central kitchen, with no database: the split of the pool (it always adds up to the pool to the
	// last fils, in proportion to meals × standard cost), a round's state, where a pool line came from, when a month
	// may be allocated, and the model against its slice.
	// ==========================================================================================
	public class HospitalKitchenTests
	{
		[Fact]
		public void The_split_follows_meals_times_standard_cost()
		{
			// the prototype's first rows: Ward 3 2480 × 18.4, Ward 2 1980 × 18.4, maternity 1560 × 19.6
			var w = new[] { 2480 * 18.4m, 1980 * 18.4m, 1560 * 19.6m };
			var s = HospitalKitchenRules.Split(100_000m, w)!;
			Assert.Equal(100_000m, s.Sum());
			Assert.True(s[0] > s[1]);
			Assert.Equal(Math.Round(100_000m * w[1] / w.Sum(), 3), s[1]);
		}

		[Fact]
		public void The_split_always_adds_up_to_the_pool_exactly()
		{
			// three equal shares of 100.000 cannot round evenly: the remainder goes to one share, never lost
			var s = HospitalKitchenRules.Split(100m, new[] { 1m, 1m, 1m })!;
			Assert.Equal(100m, s.Sum());
			Assert.Equal(2, s.Count(x => x == 33.333m));
			var r = new Random(7);
			for (int i = 0; i < 200; i++)
			{
				var weights = Enumerable.Range(0, r.Next(1, 12)).Select(_ => (decimal)r.Next(1, 5000) * 0.37m).ToList();
				decimal pool = r.Next(1, 900_000) + 0.123m;
				Assert.Equal(pool, HospitalKitchenRules.Split(pool, weights)!.Sum());
			}
		}

		[Fact]
		public void Nothing_to_weigh_is_no_split()
		{
			Assert.Null(HospitalKitchenRules.Split(500m, new[] { 0m, 0m }));
			Assert.Null(HospitalKitchenRules.Split(500m, Array.Empty<decimal>()));
			Assert.Null(HospitalKitchenRules.Split(500m, new[] { 5m, -1m }));
			Assert.Equal(new[] { 0m, 500m }, HospitalKitchenRules.Split(500m, new[] { 0m, 3m }));   // a meal with no costed recipe takes nothing
		}

		[Theory]
		[InlineData(0, 0, "Planned")]
		[InlineData(12, 0, "Serving")]
		[InlineData(12, 7, "Serving")]
		[InlineData(12, 12, "Served")]
		public void A_rounds_state_follows_its_trays(int sent, int served, string state) => Assert.Equal(state, HospitalKitchenRules.RoundState(sent, served));

		[Theory]
		[InlineData("Inventory", "Inventory")]
		[InlineData("PayrollRun", "Payroll")]
		[InlineData("FixedAssetDepreciation", "FixedAssets")]
		[InlineData("Manual", "Expenses")]
		[InlineData(null, "Expenses")]
		public void A_pool_line_is_labelled_by_what_booked_it(string? source, string label) => Assert.Equal(label, HospitalKitchenRules.SourceOf(source));

		[Fact]
		public void A_month_is_allocated_once_with_its_accounts_a_pool_and_costed_meals()
		{
			DateTime nov = new(2026, 11, 1), today = new(2026, 11, 5);
			Assert.Null(HospitalKitchenRules.AllocateRefusal(nov, today, false, true, 100, 50));
			Assert.Equal("A month is allocated once it has begun", HospitalKitchenRules.AllocateRefusal(nov.AddMonths(1), today, false, true, 100, 50));
			Assert.Equal("This month is already allocated", HospitalKitchenRules.AllocateRefusal(nov, today, true, true, 100, 50));
			Assert.Equal("Map the kitchen's four accounts in its setting first", HospitalKitchenRules.AllocateRefusal(nov, today, false, false, 100, 50));
			Assert.Equal("Nothing is booked to the kitchen cost pool this month", HospitalKitchenRules.AllocateRefusal(nov, today, false, true, 0, 50));
			Assert.Equal("No meal with a costed recipe this month", HospitalKitchenRules.AllocateRefusal(nov, today, false, true, 100, 0));
		}

		private static string Slice() => HospitalSliceParity.Slice("hospital_h44_kitchen.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalKitchenModel.Configure);

		[Fact]
		public void The_tables_match_their_slice()
		{
			foreach (var (type, table) in new[] { (typeof(HospitalKitchenSetting), "HospitalKitchenSettings"), (typeof(HospitalDietRecipe), "HospitalDietRecipes"),
				(typeof(HospitalKitchenDestination), "HospitalKitchenDestinations"), (typeof(HospitalKitchenCount), "HospitalKitchenCounts"),
				(typeof(HospitalKitchenAllocation), "HospitalKitchenAllocations"), (typeof(HospitalKitchenAllocationLine), "HospitalKitchenAllocationLines") })
			{
				HospitalSliceParity.NamesItsTable(Model(), Slice(), type, table);
				HospitalSliceParity.ColumnsMatch(Model(), Slice(), type, table);
			}
			foreach (var v in HospitalKitchenStream.All.Concat(HospitalDiet.All).Concat(HospitalMealRound.All)) Assert.Contains($"N'{v}'", Slice());
			Assert.Equal(new[] { "HospitalWards" }, HospitalSliceParity.CheckedPrerequisites(Slice()));
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
			Assert.Equal(new[] { "CompanyId", "Month" }, Model().FindEntityType(typeof(HospitalKitchenAllocation))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
		}
	}
}
