using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H39 — the controlled-drugs register, with no database: what an entry needs before the witness is asked
	// (a dose names its patient, a waste its reason, the witness is a second person), the opening balance, and the
	// model against its slice (the database refuses one person signing twice, too).
	// ==========================================================================================
	public class HospitalControlledTests
	{
		[Fact]
		public void A_dose_names_its_patient_and_a_waste_its_reason()
		{
			Assert.Null(HospitalControlledRules.RecordRefusal(HospitalControlledKind.Given, 1, 9, null, 5, 4));
			Assert.Null(HospitalControlledRules.RecordRefusal(HospitalControlledKind.Wasted, 0.5m, null, "Half ampoule drawn up, not given", 5, 4));
			Assert.Equal("Choose the patient the dose was given to", HospitalControlledRules.RecordRefusal(HospitalControlledKind.Given, 1, null, "x", 5, 4));
			Assert.Equal("Write why it was wasted", HospitalControlledRules.RecordRefusal(HospitalControlledKind.Wasted, 1, 9, "  ", 5, 4));
		}

		[Theory]
		[InlineData("Taken", 1, "Choose a dose or a waste")]
		[InlineData(HospitalControlledKind.Given, 0, "A quantity is more than zero")]
		[InlineData(HospitalControlledKind.Given, -2, "A quantity is more than zero")]
		public void The_kind_and_quantity_are_checked(string kind, decimal qty, string why) =>
			Assert.Equal(why, HospitalControlledRules.RecordRefusal(kind, qty, 9, "x", 5, 4));

		[Fact]
		public void Two_people_sign_never_one()
		{
			Assert.Equal("The witness is a second person, not the nurse who records", HospitalControlledRules.RecordRefusal(HospitalControlledKind.Given, 1, 9, null, 5, 5));
			Assert.Equal("Choose the witness", HospitalControlledRules.RecordRefusal(HospitalControlledKind.Given, 1, 9, null, 5, 0));
			Assert.Equal("Only a signed-in nurse records the register", HospitalControlledRules.RecordRefusal(HospitalControlledKind.Given, 1, 9, null, null, 4));
		}

		[Fact]
		public void The_opening_balance_is_today_unwound()
		{
			// the prototype's morphine row: open 10, in 5, given 4, wasted 1 → balance 10
			Assert.Equal(10m, HospitalControlledRules.Open(10, 5, 5));
			// fentanyl: open 6, nothing in, 2 given → 4
			Assert.Equal(6m, HospitalControlledRules.Open(4, 0, 2));
		}

		private static string Slice() => HospitalSliceParity.Slice("hospital_h39_controlled_drugs.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalControlledModel.Configure);

		[Fact]
		public void The_tables_match_their_slice()
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalControlledItem), "HospitalControlledItems");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalControlledItem), "HospitalControlledItems");
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalControlledEntry), "HospitalControlledEntries");
			HospitalSliceParity.ColumnsMatch(Model(), Slice() + Environment.NewLine + HospitalSliceParity.Slice("hospital_h52_ward_dose_billing.sql"), typeof(HospitalControlledEntry), "HospitalControlledEntries");   // + H52's BillLineId
			foreach (var k in HospitalControlledKind.All) Assert.Contains($"N'{k}'", Slice());
			Assert.Contains("WitnessEmployeeId <> RecordedByEmployeeId", Slice());
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(Slice()));
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
			var ix = Model().FindEntityType(typeof(HospitalControlledItem))!.GetIndexes().Single(i => i.IsUnique);
			Assert.Equal(new[] { "CompanyId", "ItemId" }, ix.Properties.Select(x => x.Name));
		}
	}
}
