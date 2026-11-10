using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H43 — clinical dietary, with no database: the round by the clock, a tray's status by its diet (nil by
	// mouth holds it), what a diet change does to a tray already sent, the default diet, and the model against its
	// slice (the database refuses a tray for a patient nil by mouth, and a second open diet).
	// ==========================================================================================
	public class HospitalDietaryTests
	{
		[Theory]
		[InlineData(6, HospitalMealRound.Breakfast)]
		[InlineData(9, HospitalMealRound.Breakfast)]
		[InlineData(10, HospitalMealRound.Lunch)]
		[InlineData(14, HospitalMealRound.Lunch)]
		[InlineData(15, HospitalMealRound.Snack)]
		[InlineData(17, HospitalMealRound.Dinner)]
		[InlineData(23, HospitalMealRound.Dinner)]
		public void The_round_follows_the_clock(int hour, string round) =>
			Assert.Equal(round, HospitalDietaryRules.RoundAt(new DateTime(2026, 11, 5, hour, 10, 0)));

		[Fact]
		public void Nil_by_mouth_holds_the_tray_and_a_diet_releases_it()
		{
			Assert.Equal(HospitalMealStatus.Held, HospitalDietaryRules.StatusFor(HospitalDiet.Npo));
			Assert.Equal(HospitalMealStatus.Sent, HospitalDietaryRules.StatusFor(HospitalDiet.Diabetic));
			Assert.Equal(HospitalMealStatus.Held, HospitalDietaryRules.AfterDietChange(HospitalMealStatus.Sent, HospitalDiet.Npo));
			Assert.Equal(HospitalMealStatus.Sent, HospitalDietaryRules.AfterDietChange(HospitalMealStatus.Held, HospitalDiet.Soft));
			Assert.Null(HospitalDietaryRules.AfterDietChange(HospitalMealStatus.Served, HospitalDiet.Npo));   // a served tray is history
		}

		[Fact]
		public void A_patient_with_no_diet_ordered_eats_regular()
		{
			Assert.Equal(HospitalDiet.Regular, HospitalDietaryRules.Effective(null));
			Assert.Equal(HospitalDiet.Renal, HospitalDietaryRules.Effective(HospitalDiet.Renal));
		}

		private static string Slice() => HospitalSliceParity.Slice("hospital_h43_dietary.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalDietaryModel.Configure);

		[Fact]
		public void The_tables_match_their_slice()
		{
			foreach (var (type, table) in new[] { (typeof(HospitalDietOrder), "HospitalDietOrders"), (typeof(HospitalMealOrder), "HospitalMealOrders") })
			{
				HospitalSliceParity.NamesItsTable(Model(), Slice(), type, table);
				HospitalSliceParity.ColumnsMatch(Model(), Slice(), type, table);
			}
			foreach (var v in HospitalDiet.All.Concat(HospitalMealRound.All).Concat(HospitalMealStatus.All)) Assert.Contains($"N'{v}'", Slice());
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients", "HospitalWards" }, HospitalSliceParity.CheckedPrerequisites(Slice()).OrderBy(x => x));
			Assert.Contains("Diet <> N'NPO' OR Status = N'Held'", Slice());
			Assert.Contains("WHERE EndedAt IS NULL", Slice());
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
			Assert.Equal(new[] { "AdmissionId", "Day", "Round" }, Model().FindEntityType(typeof(HospitalMealOrder))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
		}
	}
}
