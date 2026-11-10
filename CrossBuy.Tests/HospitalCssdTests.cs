using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H42 — central sterile supply, with no database: the round a set takes (and the steps it may not skip),
	// an expired set never reaching a case, the shown state, the sterile-until date, and the model against its slice.
	// ==========================================================================================
	public class HospitalCssdTests
	{
		private static readonly DateTime Today = new(2026, 11, 5);

		[Fact]
		public void A_set_goes_round_one_step_at_a_time()
		{
			var s = HospitalSetStatus.AwaitingWash;
			DateTime? until = null;
			foreach (var (step, to) in new[] { (HospitalSetEventKind.Washed, HospitalSetStatus.Packed), (HospitalSetEventKind.Loaded, HospitalSetStatus.InAutoclave),
				(HospitalSetEventKind.Sterilised, HospitalSetStatus.Sterile), (HospitalSetEventKind.Issued, HospitalSetStatus.InUse), (HospitalSetEventKind.Returned, HospitalSetStatus.AwaitingWash) })
			{
				if (to == HospitalSetStatus.Sterile) until = HospitalCssdRules.SterileUntil(Today, 30);
				Assert.Equal(to, HospitalCssdRules.After(s, step, until, Today));
				s = to;
			}
		}

		[Theory]
		[InlineData(HospitalSetStatus.AwaitingWash, HospitalSetEventKind.Loaded)]       // never loaded unwashed
		[InlineData(HospitalSetStatus.AwaitingWash, HospitalSetEventKind.Issued)]       // never issued dirty
		[InlineData(HospitalSetStatus.Packed, HospitalSetEventKind.Issued)]             // never issued unsterilised
		[InlineData(HospitalSetStatus.InAutoclave, HospitalSetEventKind.Issued)]
		[InlineData(HospitalSetStatus.InUse, HospitalSetEventKind.Issued)]              // never on two cases
		[InlineData(HospitalSetStatus.InUse, HospitalSetEventKind.Washed)]
		[InlineData(HospitalSetStatus.Sterile, HospitalSetEventKind.Loaded)]
		public void A_set_cannot_skip_a_step(string from, string step) =>
			Assert.Null(HospitalCssdRules.After(from, step, Today.AddDays(10), Today));

		[Fact]
		public void A_failed_cycle_sends_its_sets_back_to_be_loaded_again() =>
			Assert.Equal(HospitalSetStatus.Packed, HospitalCssdRules.After(HospitalSetStatus.InAutoclave, HospitalSetEventKind.CycleFailed, null, Today));

		[Fact]
		public void An_expired_set_is_never_issued_and_goes_back_to_be_reprocessed()
		{
			Assert.Null(HospitalCssdRules.After(HospitalSetStatus.Sterile, HospitalSetEventKind.Issued, Today.AddDays(-1), Today));
			Assert.Equal(HospitalSetStatus.InUse, HospitalCssdRules.After(HospitalSetStatus.Sterile, HospitalSetEventKind.Issued, Today, Today));   // good through its last day
			Assert.Equal(HospitalSetStatus.AwaitingWash, HospitalCssdRules.After(HospitalSetStatus.Sterile, HospitalSetEventKind.Reprocess, Today.AddDays(-1), Today));
			Assert.Equal(HospitalCssdRules.Expired, HospitalCssdRules.State(HospitalSetStatus.Sterile, Today.AddDays(-1), Today));
			Assert.Equal(HospitalSetStatus.Sterile, HospitalCssdRules.State(HospitalSetStatus.Sterile, Today, Today));
		}

		[Fact]
		public void Sterile_until_is_today_plus_the_shelf_life_and_cycles_are_numbered()
		{
			Assert.Equal(new DateTime(2026, 12, 5), HospitalCssdRules.SterileUntil(Today.AddHours(15), 30));
			Assert.Equal("CY-000012", HospitalCssdRules.CycleNo(12));
		}

		[Fact]
		public void A_set_needs_a_code_a_name_and_a_shelf_life()
		{
			Assert.Null(HospitalCssdRules.SetRefusal("SET-ORTHO-04", "طقم عظام كبير", 30));
			Assert.Equal("A set code is 1 to 30 characters", HospitalCssdRules.SetRefusal(" ", "x", 30));
			Assert.Equal("Write the set's name", HospitalCssdRules.SetRefusal("S1", "", 30));
			Assert.Equal("The shelf life is 1 to 365 days", HospitalCssdRules.SetRefusal("S1", "x", 0));
		}

		private static string Slice() => HospitalSliceParity.Slice("hospital_h42_cssd.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalCssdModel.Configure);

		[Fact]
		public void The_tables_match_their_slice()
		{
			foreach (var (type, table) in new[] { (typeof(HospitalInstrumentSet), "HospitalInstrumentSets"), (typeof(HospitalSterilisationCycle), "HospitalSterilisationCycles"), (typeof(HospitalSetEvent), "HospitalSetEvents") })
			{
				HospitalSliceParity.NamesItsTable(Model(), Slice(), type, table);
				HospitalSliceParity.ColumnsMatch(Model(), Slice(), type, table);
			}
			foreach (var v in HospitalSetStatus.All.Concat(HospitalCycleResult.All).Concat(HospitalSetEventKind.All)) Assert.Contains($"N'{v}'", Slice());
			Assert.Equal(new[] { "HospitalSurgeries" }, HospitalSliceParity.CheckedPrerequisites(Slice()));
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
			Assert.Equal(new[] { "CompanyId", "Code" }, Model().FindEntityType(typeof(HospitalInstrumentSet))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
		}
	}
}
