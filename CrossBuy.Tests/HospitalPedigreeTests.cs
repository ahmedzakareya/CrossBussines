using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H103 — the family tree, with no database: a new tree is the patient alone; the links must make sense
	// (a father is a man one generation up, partners share a generation, one patient); what the tree yields —
	// consanguinity, a condition across generations, whom to screen — comes from the people and links alone.
	// ==========================================================================================
	public class HospitalPedigreeTests
	{
		// the prototype's family: grandfather (diabetes, deceased) + grandmother (carrier); their son (diabetes 40) married
		// to his cousin; the patient (diabetes 16) and his sister (12)
		private const string Family = "{\"people\":[" +
			"{\"id\":1,\"sex\":\"M\",\"gen\":0,\"father\":4,\"mother\":6,\"proband\":true,\"affected\":true,\"cond\":\"Diabetes\",\"note\":\"16\"}," +
			"{\"id\":2,\"sex\":\"M\",\"gen\":-2,\"affected\":true,\"deceased\":true,\"cond\":\"diabetes\",\"note\":\"d. 68\"}," +
			"{\"id\":3,\"sex\":\"F\",\"gen\":-2,\"carrier\":true}," +
			"{\"id\":4,\"sex\":\"M\",\"gen\":-1,\"father\":2,\"mother\":3,\"affected\":true,\"cond\":\"Diabetes\",\"note\":\"40\"}," +
			"{\"id\":5,\"sex\":\"F\",\"gen\":-1,\"father\":2,\"mother\":3}," +
			"{\"id\":6,\"sex\":\"F\",\"gen\":-1,\"note\":\"Cousin\"}," +
			"{\"id\":7,\"sex\":\"F\",\"gen\":0,\"father\":4,\"mother\":6,\"note\":\"12\",\"x\":\"<script>\"}]," +
			"\"couples\":[{\"a\":4,\"b\":6,\"cons\":true}]}";

		[Fact]
		public void A_new_tree_is_the_patient_alone()
		{
			var (r, err) = HospitalPedigreeRules.Read(null, "F");
			Assert.Null(err);
			Assert.Equal(("F", true), (r!.People.Single().Sex, r.People.Single().Proband));
		}

		[Fact]
		public void What_the_tree_yields()
		{
			var (r, err) = HospitalPedigreeRules.Read(Family, "M");
			Assert.Null(err);
			Assert.True(r!.Yield.Consanguinity);
			Assert.Equal(("Diabetes", 3), r.Yield.Conditions.Single());          // "diabetes" and "Diabetes" are one condition
			Assert.Equal(new[] { 6, 7 }, r.Yield.Screen.Single().People.OrderBy(x => x));   // the unaffected mother and sister
			Assert.Contains(r.Couples, c => c.A == 2 && c.B == 3);              // parents become a couple by themselves
			Assert.DoesNotContain("script", HospitalPedigreeRules.Store(r));
		}

		[Fact]
		public void The_links_must_make_sense()
		{
			Assert.Equal("A father is a man one generation up", HospitalPedigreeRules.Read("{\"people\":[{\"id\":1,\"gen\":0,\"proband\":true,\"father\":2},{\"id\":2,\"sex\":\"F\",\"gen\":-1}]}", "M").Error);
			Assert.Equal("A mother is a woman one generation up", HospitalPedigreeRules.Read("{\"people\":[{\"id\":1,\"gen\":0,\"proband\":true,\"mother\":2},{\"id\":2,\"sex\":\"F\",\"gen\":-2}]}", "M").Error);
			Assert.Equal("The tree has exactly one patient", HospitalPedigreeRules.Read("{\"people\":[{\"id\":1,\"gen\":0}]}", "M").Error);
			Assert.Equal("Partners are two people of one generation", HospitalPedigreeRules.Read("{\"people\":[{\"id\":1,\"gen\":0,\"proband\":true},{\"id\":2,\"gen\":-1}],\"couples\":[{\"a\":1,\"b\":2}]}", "M").Error);
			Assert.Equal("Write the condition of every affected relative", HospitalPedigreeRules.Read("{\"people\":[{\"id\":1,\"gen\":0,\"proband\":true,\"affected\":true}]}", "M").Error);
			Assert.Equal("The family tree could not be read", HospitalPedigreeRules.Read("{\"people\":[{\"id\":1,\"gen\":0,\"proband\":true},{\"id\":1,\"gen\":0}]}", "M").Error);
			Assert.Equal("A family tree holds 1 to 60 people", HospitalPedigreeRules.Read("{\"people\":\"x\"}", "M").Error);   // not a list: nobody in it
		}
	}
}
