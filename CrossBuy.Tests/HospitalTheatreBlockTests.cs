using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H244 — block time and preference cards. What must never happen: a case outside the block window counted
	// in it; another surgeon's case credited to the owner; a session on the wrong weekday; two blocks overlapping in one
	// theatre; an empty preference card; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalTheatreBlockTests
	{
		// Sunday blocks, 08:00-14:00 in theatre 5, owned by surgeon 11
		private static readonly HospitalTheatreBlock B = new() { ID = 1, RoomId = 5, Weekday = 0, StartMinute = 480, EndMinute = 840, SurgeonId = 11, IsActive = true };
		private static readonly DateTime From = new(2026, 11, 1), To = new(2026, 11, 14);   // Sundays 1 and 8

		[Fact]
		public void Sessions_are_the_blocks_weekdays_in_the_window()
		{
			Assert.Equal(new[] { new DateTime(2026, 11, 1), new DateTime(2026, 11, 8) }, HospitalTheatreBlockRules.Sessions(0, From, To));
			Assert.Equal(2, HospitalTheatreBlockRules.Sessions(6, From, To).Count);   // Saturdays 7 and 14
		}

		[Fact]
		public void Only_the_owners_minutes_inside_the_window_are_used()
		{
			var cases = new[]
			{
				new HospitalBlockCase(5, new DateTime(2026, 11, 1), 450, 120, 11),   // 07:30-09:30: 90 inside
				new HospitalBlockCase(5, new DateTime(2026, 11, 1), 600, 180, 11),   // 10:00-13:00: 180
				new HospitalBlockCase(5, new DateTime(2026, 11, 8), 540, 60, 12),    // another surgeon: 60 apart
				new HospitalBlockCase(6, new DateTime(2026, 11, 8), 540, 60, 11),    // another theatre
				new HospitalBlockCase(5, new DateTime(2026, 11, 2), 540, 60, 11),    // a Monday
			};
			var u = HospitalTheatreBlockRules.Use(B, cases, From, To);
			Assert.Equal((2, 720, 270, 60), (u.Sessions, u.BlockMinutes, u.UsedMinutes, u.OthersMinutes));
			Assert.Equal(38m, u.UtilisationPct);
			Assert.Equal("Release", HospitalTheatreBlockRules.Level(u.UtilisationPct));
			Assert.Equal("Good", HospitalTheatreBlockRules.Level(75m));
			Assert.Equal("Low", HospitalTheatreBlockRules.Level(60m));
			var open = new HospitalTheatreBlock { RoomId = 5, Weekday = 0, StartMinute = 480, EndMinute = 840, Label = "Orthopaedics" };
			Assert.Equal(330, HospitalTheatreBlockRules.Use(open, cases, From, To).UsedMinutes);   // a service-line block counts every case
		}

		[Fact]
		public void A_block_is_an_hour_or_more_and_never_overlaps_another()
		{
			Assert.Null(HospitalTheatreBlockRules.BlockRefusal(0, 840, 1020, 12, null, new[] { B }, null));      // 14:00-17:00 touches, no overlap
			Assert.NotNull(HospitalTheatreBlockRules.BlockRefusal(0, 780, 900, 12, null, new[] { B }, null));    // 13:00-15:00 overlaps
			Assert.Null(HospitalTheatreBlockRules.BlockRefusal(0, 480, 840, 11, null, new[] { B }, 1));          // editing itself
			Assert.NotNull(HospitalTheatreBlockRules.BlockRefusal(0, 480, 510, 11, null, Array.Empty<HospitalTheatreBlock>(), null));
			Assert.NotNull(HospitalTheatreBlockRules.BlockRefusal(0, 480, 840, null, " ", Array.Empty<HospitalTheatreBlock>(), null));
			Assert.NotNull(HospitalTheatreBlockRules.BlockRefusal(7, 480, 840, 11, null, Array.Empty<HospitalTheatreBlock>(), null));
			Assert.NotNull(HospitalTheatreBlockRules.CardRefusal(null, " ", ""));
			Assert.Null(HospitalTheatreBlockRules.CardRefusal(null, "Supine"));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalTheatreBlockModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h244_theatre_blocks.sql");

		[Theory]
		[InlineData(typeof(HospitalPreferenceCard), "HospitalPreferenceCards")]
		[InlineData(typeof(HospitalTheatreBlock), "HospitalTheatreBlocks")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
