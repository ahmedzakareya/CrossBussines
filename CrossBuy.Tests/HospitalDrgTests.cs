using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H241 — DRG grouping. What must never happen: a shorter prefix beating a longer one; a surgical group
	// given to a stay with no operation; an age band ignored; an inactive group chosen; a case-mix index that counts
	// ungrouped stays as weight zero; a group saved without prefixes or weight; a model naming a table its slice lacks.
	// ==========================================================================================
	public class HospitalDrgTests
	{
		private static HospitalDrg G(int id, string code, string prefixes, decimal w, bool? surgical = null, int? min = null, int? max = null, bool active = true) =>
			new() { ID = id, Code = code, Name = code, DxPrefixes = prefixes, Weight = w, Surgical = surgical, MinAge = min, MaxAge = max, IsActive = active };

		private static readonly HospitalDrg[] Groups =
		{
			G(1, "F60", "I2", 1.0m),
			G(2, "F10", "I21,I22", 2.4m, surgical: true),
			G(3, "F61", "I21,I22", 1.3m, surgical: false),
			G(4, "P01", "J18", 0.9m, max: 17),
			G(5, "E62", "J18", 1.1m, min: 18),
			G(6, "OLD", "I214", 9m, active: false),
		};

		[Theory]
		[InlineData("I21.4", true, 60, "F10")]
		[InlineData("i214", false, 60, "F61")]
		[InlineData("I25.1", false, 60, "F60")]
		[InlineData("J18.9", false, 8, "P01")]
		[InlineData("J18.9", false, 40, "E62")]
		public void The_longest_fitting_prefix_wins(string dx, bool surgical, int age, string code) =>
			Assert.Equal(code, HospitalDrgRules.Group(Groups, dx, surgical, age)!.Code);

		[Fact]
		public void No_group_when_nothing_fits()
		{
			Assert.Null(HospitalDrgRules.Group(Groups, "K35.8", false, 30));
			Assert.Null(HospitalDrgRules.Group(Groups, "J1", false, 30));      // too short to be a code
			Assert.Null(HospitalDrgRules.Group(Groups, "J18.9", false, null)); // an age band needs the age
			Assert.Equal(new[] { "I21", "I22" }, HospitalDrgRules.Prefixes(" i21 ; I22,I21 "));
		}

		[Fact]
		public void The_case_mix_index_is_the_mean_weight_of_grouped_stays()
		{
			Assert.Equal(1.5m, HospitalDrgRules.CaseMixIndex(new[] { 1m, 2m, 0m }));
			Assert.Null(HospitalDrgRules.CaseMixIndex(Array.Empty<decimal>()));
			Assert.Equal(1.5m, new HospitalDrgCaseStat(1, 2, 3m, 6m, 4m).LosIndex);
		}

		[Fact]
		public void A_group_needs_code_name_prefixes_and_weight()
		{
			Assert.Null(HospitalDrgRules.DefinitionRefusal("F10", "AMI with PCI", "I21,I22", 2.4m, null, null, 4m));
			Assert.NotNull(HospitalDrgRules.DefinitionRefusal("", "x", "I21", 1m, null, null, null));
			Assert.NotNull(HospitalDrgRules.DefinitionRefusal("F10", "x", " , ", 1m, null, null, null));
			Assert.NotNull(HospitalDrgRules.DefinitionRefusal("F10", "x", "121", 1m, null, null, null));
			Assert.NotNull(HospitalDrgRules.DefinitionRefusal("F10", "x", "I21", 0m, null, null, null));
			Assert.NotNull(HospitalDrgRules.DefinitionRefusal("F10", "x", "I21", 1m, 40, 18, null));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalDrgModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h241_drg.sql");

		[Theory]
		[InlineData(typeof(HospitalDrg), "HospitalDrgs")]
		[InlineData(typeof(HospitalDrgAssignment), "HospitalDrgAssignments")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
