using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H29 — the theatre schedule, with no database: what a booking may be, who it clashes with, how a
	// case moves (the WHO checklist gating start and close), the checklist's own window, the prototype's two
	// figures; and the model against its slice.
	// ==========================================================================================
	public class HospitalTheatreTests
	{
		private static readonly DateTime Today = new(2026, 11, 4);

		// ---------------- booking ----------------

		[Theory]
		[InlineData(-1, 480, 60, true, null, "An operation cannot be booked in the past")]
		[InlineData(0, 480, 10, true, null, "An operation is 15 minutes to 12 hours, in steps of 5")]
		[InlineData(0, 480, 725, true, null, "An operation is 15 minutes to 12 hours, in steps of 5")]
		[InlineData(0, 480, 62, true, null, "An operation is 15 minutes to 12 hours, in steps of 5")]
		[InlineData(0, 1400, 60, true, null, "The operation must end the same day")]
		[InlineData(0, 480, 60, false, null, "The surgeon needs the surgery privilege")]
		[InlineData(0, 480, 60, true, false, "The anaesthetist needs the anaesthesia privilege")]
		[InlineData(0, 480, 60, true, true, null)]
		[InlineData(0, 480, 60, true, null, null)]
		[InlineData(3, 1380, 60, true, null, null)]
		public void A_booking_is_refused_when_it_does_not_hold_together(int days, int start, int minutes, bool surgeon, bool? anaes, string? why) =>
			Assert.Equal(why, HospitalTheatreRules.BookingRefusal(Today.AddDays(days), start, minutes, Today, surgeon, anaes));

		[Fact]
		public void A_theatre_a_surgeon_and_an_anaesthetist_are_each_in_one_case_at_a_time()
		{
			var day = new[] { (RoomId: 1, SurgeonId: 10, AnaesthetistId: (int?)20, Start: 480, Duration: 90) };
			Assert.Equal("The theatre is booked at that time", HospitalTheatreRules.ClashRefusal(1, 11, 21, 540, 60, day));
			Assert.Equal("The surgeon is in another operation at that time", HospitalTheatreRules.ClashRefusal(2, 10, 21, 540, 60, day));
			Assert.Equal("The anaesthetist is in another operation at that time", HospitalTheatreRules.ClashRefusal(2, 11, 20, 540, 60, day));
			// the surgeon of one case cannot be the anaesthetist of another at the same time, nor the other way round
			Assert.Equal("The surgeon is in another operation at that time", HospitalTheatreRules.ClashRefusal(2, 20, null, 540, 60, day));
			Assert.Equal("The anaesthetist is in another operation at that time", HospitalTheatreRules.ClashRefusal(2, 11, 10, 540, 60, day));
			// back to back is not a clash
			Assert.Null(HospitalTheatreRules.ClashRefusal(1, 10, 20, 570, 60, day));
			Assert.Null(HospitalTheatreRules.ClashRefusal(1, 10, 20, 420, 60, day));
		}

		// ---------------- moving a case ----------------

		private static readonly string[] BeforeStart = HospitalSurgeryChecklist.BeforeStart.ToArray();

		[Fact]
		public void A_case_starts_only_with_the_before_induction_checks_and_a_free_theatre()
		{
			Assert.Equal("Complete the checklist before induction", HospitalTheatreRules.MoveRefusal(HospitalSurgeryStatus.Planned, HospitalSurgeryStatus.InProgress, BeforeStart[..3], false, null));
			Assert.Equal("Another operation is in progress in this theatre", HospitalTheatreRules.MoveRefusal(HospitalSurgeryStatus.Planned, HospitalSurgeryStatus.InProgress, BeforeStart, true, null));
			Assert.Null(HospitalTheatreRules.MoveRefusal(HospitalSurgeryStatus.Planned, HospitalSurgeryStatus.InProgress, BeforeStart, false, null));
		}

		[Fact]
		public void A_case_closes_only_with_every_check_the_instrument_count_among_them()
		{
			Assert.Equal("Complete the checklist before the patient leaves the theatre", HospitalTheatreRules.MoveRefusal(HospitalSurgeryStatus.InProgress, HospitalSurgeryStatus.Done, BeforeStart.Append(HospitalSurgeryChecklist.Specimens).ToArray(), false, null));
			Assert.Null(HospitalTheatreRules.MoveRefusal(HospitalSurgeryStatus.InProgress, HospitalSurgeryStatus.Done, HospitalSurgeryChecklist.All.ToArray(), false, null));
		}

		[Theory]
		[InlineData(HospitalSurgeryStatus.Planned, HospitalSurgeryStatus.Done)]
		[InlineData(HospitalSurgeryStatus.InProgress, HospitalSurgeryStatus.Cancelled)]
		[InlineData(HospitalSurgeryStatus.Done, HospitalSurgeryStatus.InProgress)]
		[InlineData(HospitalSurgeryStatus.Cancelled, HospitalSurgeryStatus.Planned)]
		[InlineData(HospitalSurgeryStatus.Done, HospitalSurgeryStatus.Done)]
		public void Any_other_move_is_refused(string from, string to) =>
			Assert.Equal("This operation cannot make that move", HospitalTheatreRules.MoveRefusal(from, to, HospitalSurgeryChecklist.All.ToArray(), false, "x"));

		[Fact]
		public void Only_a_planned_case_is_cancelled_and_with_a_reason()
		{
			Assert.Equal("Write why the operation is cancelled", HospitalTheatreRules.MoveRefusal(HospitalSurgeryStatus.Planned, HospitalSurgeryStatus.Cancelled, Array.Empty<string>(), false, "  "));
			Assert.Null(HospitalTheatreRules.MoveRefusal(HospitalSurgeryStatus.Planned, HospitalSurgeryStatus.Cancelled, Array.Empty<string>(), false, "Patient unfit"));
		}

		[Fact]
		public void The_checklist_is_ticked_only_while_the_case_is_open_and_only_its_own_items()
		{
			Assert.Null(HospitalTheatreRules.CheckRefusal(HospitalSurgeryStatus.Planned, HospitalSurgeryChecklist.Identity));
			Assert.Null(HospitalTheatreRules.CheckRefusal(HospitalSurgeryStatus.InProgress, HospitalSurgeryChecklist.Count));
			Assert.Equal("The checklist closes with the operation", HospitalTheatreRules.CheckRefusal(HospitalSurgeryStatus.Done, HospitalSurgeryChecklist.Count));
			Assert.Equal("The checklist closes with the operation", HospitalTheatreRules.CheckRefusal(HospitalSurgeryStatus.Cancelled, HospitalSurgeryChecklist.Count));
			Assert.Equal("not_found", HospitalTheatreRules.CheckRefusal(HospitalSurgeryStatus.Planned, "Coffee"));
		}

		// ---------------- the prototype's figures ----------------

		[Fact]
		public void Utilisation_counts_booked_minutes_inside_the_session_over_what_the_theatres_offer()
		{
			// two theatres × 480 minutes = 960; 90 + 120 inside, a cancelled case ignored, a case before 08:00 counted only from 08:00
			var cases = new[] { (480, 90, HospitalSurgeryStatus.Done), (600, 120, HospitalSurgeryStatus.Planned), (700, 60, HospitalSurgeryStatus.Cancelled), (450, 60, HospitalSurgeryStatus.Done) };
			Assert.Equal((int)Math.Round((90 + 120 + 30) * 100.0 / 960), HospitalTheatreRules.Utilisation(cases, 2));
			Assert.Equal(0, HospitalTheatreRules.Utilisation(cases, 0));
		}

		[Fact]
		public void First_case_on_time_counts_each_theatre_first_started_case_within_the_grace()
		{
			var day = Today;
			var cases = new[]
			{
				(1, 480, HospitalSurgeryStatus.Done, (DateTime?)day.AddMinutes(490)),         // theatre 1: 10 min late, on time
				(1, 600, HospitalSurgeryStatus.Planned, (DateTime?)null),
				(2, 480, HospitalSurgeryStatus.Cancelled, (DateTime?)null),                   // cancelled: theatre 2's first is the next
				(2, 540, HospitalSurgeryStatus.InProgress, (DateTime?)day.AddMinutes(570)),   // 30 min late
				(3, 480, HospitalSurgeryStatus.Planned, (DateTime?)null),                     // not started: not counted
			};
			Assert.Equal((1, 2), HospitalTheatreRules.FirstCaseOnTime(cases));
		}

		// ---------------- the model against its slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h29_theatre.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalTheatreModel.Configure);

		public static TheoryData<Type, string> Tables => new()
		{
			{ typeof(HospitalSurgery), "HospitalSurgeries" },
			{ typeof(HospitalSurgeryCheck), "HospitalSurgeryChecks" },
		};

		[Theory, MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory, MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice() + HospitalSliceParity.Slice("hospital_h37_theatre_kits.sql"), entity, table);   // H37 adds KitIssuedAt

		[Fact]
		public void The_slice_checks_its_prerequisites_and_alters_no_existing_table()
		{
			Assert.Equal(4, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);   // patients, rooms, services, practitioners
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
		}

		[Fact]
		public void Every_status_and_checklist_item_the_code_knows_the_slice_allows()
		{
			foreach (var s in HospitalSurgeryStatus.All) Assert.Contains($"N'{s}'", Slice());
			foreach (var i in HospitalSurgeryChecklist.All) Assert.Contains($"N'{i}'", Slice());
		}
	
		// ---------------- H37: the procedure kit ----------------

		[Theory]
		[InlineData(HospitalSurgeryStatus.Done, false, true, true, 3, null)]
		[InlineData(HospitalSurgeryStatus.InProgress, false, true, true, 3, "The kit is issued when the case is closed")]
		[InlineData(HospitalSurgeryStatus.Done, true, true, true, 3, "This case's kit has already left the store")]
		[InlineData(HospitalSurgeryStatus.Done, false, false, true, 3, "This procedure has no kit set")]
		[InlineData(HospitalSurgeryStatus.Done, false, true, false, 3, "Choose the theatre store first")]
		[InlineData(HospitalSurgeryStatus.Done, false, true, true, 0, "The kit item has no components (Inventory › the item's BOM)")]
		public void A_kit_leaves_the_store_once_for_a_closed_case_with_a_kit_and_a_store(string status, bool issued, bool kit, bool store, int lines, string? why) =>
			Assert.Equal(why, HospitalTheatreKitRules.IssueRefusal(status, issued ? new DateTime(2026, 11, 5) : null, kit, store, lines));

		[Theory]
		[InlineData(4, 1, 4)]
		[InlineData(0.33335, 1, 0.3334)]   // rounded as the BOM service rounds: 4 places, away from zero
		[InlineData(2.5, 2, 5)]
		public void A_kit_quantity_is_per_kit_times_kits(decimal perKit, int kits, decimal need) => Assert.Equal(need, HospitalTheatreKitRules.Need(perKit, kits));

		[Fact]
		public void The_kit_tables_match_their_slice()
		{
			var sql = HospitalSliceParity.Slice("hospital_h37_theatre_kits.sql");
			foreach (var (t, name) in new (Type, string)[] { (typeof(HospitalProcedureKit), "HospitalProcedureKits"), (typeof(HospitalTheatreSetting), "HospitalTheatreSettings") })
			{
				HospitalSliceParity.NamesItsTable(Model(), sql, t, name);
				HospitalSliceParity.ColumnsMatch(Model(), sql, t, name);
			}
			Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(sql, "ALTER TABLE").Count);   // only KitIssuedAt
			Assert.Contains("IF COL_LENGTH('dbo.HospitalSurgeries', 'KitIssuedAt') IS NULL ALTER TABLE dbo.HospitalSurgeries ADD KitIssuedAt datetime2 NULL;", sql);
			Assert.DoesNotContain("UPDATE ", sql, StringComparison.OrdinalIgnoreCase);
			var ix = Model().FindEntityType(typeof(HospitalProcedureKit))!.GetIndexes().Single(i => i.IsUnique);
			Assert.Equal(new[] { "CompanyId", "ServiceId" }, ix.Properties.Select(x => x.Name));
		}
	}
}
