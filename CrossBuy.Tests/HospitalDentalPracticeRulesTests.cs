using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H16 — the dental practice rules, DATABASE-FREE, and its model against its slice.
	//
	// What must never happen: two patients overlapping in one chair, a lab case skipping a stage or a remake
	// with no fault, a fit at risk not flagged two days ahead, a recall shown as booked with no visit, or a kit
	// used from a cycle whose indicator failed.
	// ==========================================================================================
	public class HospitalDentalPracticeRulesTests
	{
		[Theory]
		[InlineData(540, 60, 570, 30, true)]    // 09:00–10:00 against 09:30–10:00
		[InlineData(540, 60, 600, 30, false)]   // back to back is not an overlap
		[InlineData(600, 30, 540, 60, false)]
		[InlineData(540, 90, 480, 61, true)]
		public void Chair_time_overlaps_only_when_the_times_cross(int a, int am, int b, int bm, bool overlap) =>
			Assert.Equal(overlap, HospitalDentalPracticeRules.Overlaps(a, am, b, bm));

		[Fact]
		public void Occupancy_is_booked_minutes_over_the_open_day()
		{
			Assert.Equal(50, HospitalDentalPracticeRules.Occupancy(960, 4));   // 16 h over 4 chairs × 8 h
			Assert.Equal(100, HospitalDentalPracticeRules.Occupancy(9999, 1));
			Assert.Null(HospitalDentalPracticeRules.Occupancy(60, 0));
		}

		[Fact]
		public void A_lab_case_moves_one_stage_at_a_time_and_a_remake_goes_back_to_the_lab()
		{
			string? s = HospitalLabCaseStage.Prescribed;
			var seen = new List<string>();
			while (s != null) { seen.Add(s); var n = HospitalDentalPracticeRules.NextStage(s); if (n != null) Assert.True(HospitalDentalPracticeRules.CanMove(s, n)); s = n; }
			Assert.Equal(HospitalLabCaseStage.Pipeline, seen);
			Assert.False(HospitalDentalPracticeRules.CanMove(HospitalLabCaseStage.Prescribed, HospitalLabCaseStage.Fitted));
			Assert.False(HospitalDentalPracticeRules.CanMove(HospitalLabCaseStage.Fitted, HospitalLabCaseStage.Remake));
			Assert.False(HospitalDentalPracticeRules.CanMove(HospitalLabCaseStage.Sent, HospitalLabCaseStage.Remake));
			Assert.True(HospitalDentalPracticeRules.CanMove(HospitalLabCaseStage.Received, HospitalLabCaseStage.Remake));
			Assert.Equal(HospitalLabCaseStage.Sent, HospitalDentalPracticeRules.NextStage(HospitalLabCaseStage.Remake));
		}

		[Fact]
		public void A_case_still_out_two_days_before_its_fit_is_at_risk()
		{
			var now = new DateTime(2026, 11, 3, 9, 0, 0);
			Assert.True(HospitalDentalPracticeRules.AtRisk(HospitalLabCaseStage.Shipped, now.AddDays(2), now));
			Assert.True(HospitalDentalPracticeRules.AtRisk(HospitalLabCaseStage.Fabricating, now.AddHours(-1), now));   // the fit has passed
			Assert.False(HospitalDentalPracticeRules.AtRisk(HospitalLabCaseStage.Shipped, now.AddDays(2).AddMinutes(1), now));
			Assert.False(HospitalDentalPracticeRules.AtRisk(HospitalLabCaseStage.Received, now.AddHours(1), now));
			Assert.False(HospitalDentalPracticeRules.AtRisk(HospitalLabCaseStage.Sent, null, now));
		}

		[Fact]
		public void On_time_counts_only_the_cases_back_in_ninety_days()
		{
			var today = new DateTime(2026, 11, 3);
			var cases = new (DateTime, DateTime?)[] { (new(2026, 10, 30), new DateTime(2026, 10, 29)), (new(2026, 10, 20), new DateTime(2026, 10, 22)), (new(2026, 5, 1), new DateTime(2026, 5, 9)), (new(2026, 11, 9), null) };
			Assert.Equal(50m, HospitalDentalPracticeRules.OnTimeRate(cases, today));
			Assert.Null(HospitalDentalPracticeRules.OnTimeRate(Array.Empty<(DateTime, DateTime?)>(), today));
		}

		[Fact]
		public void A_recall_is_booked_only_with_a_coming_visit()
		{
			var today = new DateTime(2026, 11, 3);
			Assert.Equal("Booked", HospitalDentalPracticeRules.RecallState(new(2026, 10, 1), today, today));
			Assert.Equal("Overdue", HospitalDentalPracticeRules.RecallState(new(2026, 10, 18), null, today));
			Assert.Equal("Overdue", HospitalDentalPracticeRules.RecallState(new(2026, 10, 18), new DateTime(2026, 11, 2), today));   // a past visit books nothing
			Assert.Equal("NotDue", HospitalDentalPracticeRules.RecallState(new(2027, 2, 1), null, today));
			Assert.Equal(new DateTime(2027, 5, 3), HospitalDentalPracticeRules.NextRecall(new DateTime(2026, 11, 3, 15, 0, 0), 6));
		}

		[Fact]
		public void A_kit_is_never_used_from_a_failed_cycle()
		{
			// H169: usable means RELEASED; a released cycle whose biological is still incubating stays usable (not an implant load)
			HospitalSterilCycle C(string chem, string bio) => new() { Chemical = chem, Biological = bio, PhysicalOk = true, ReleasedAt = new DateTime(2026, 11, 3) };
			Assert.Null(HospitalDentalPracticeRules.UseRefusal(C(HospitalIndicator.Pass, HospitalIndicator.Pass)));
			Assert.Null(HospitalDentalPracticeRules.UseRefusal(C(HospitalIndicator.Pass, HospitalIndicator.Pending)));   // parametric release
			Assert.NotNull(HospitalDentalPracticeRules.UseRefusal(C(HospitalIndicator.Pass, HospitalIndicator.Fail)));
			Assert.NotNull(HospitalDentalPracticeRules.UseRefusal(C(HospitalIndicator.Fail, HospitalIndicator.Pending)));
			Assert.NotNull(HospitalDentalPracticeRules.UseRefusal(new HospitalSterilCycle { Chemical = HospitalIndicator.Pending, Biological = HospitalIndicator.Pending }));   // never released
		}

		[Fact]
		public void Numbers_run_per_prefix()
		{
			Assert.Equal("LC-0419", HospitalDentalPracticeRules.NextNo(new[] { "LC-0418", "LC-0002", "XX-9999" }, "LC-", 4));
			Assert.Equal("AC-2026-1103-01", HospitalDentalPracticeRules.NextNo(new[] { "AC-2026-1102-04" }, "AC-2026-1103-", 2));
		}

		// ---------------- model and slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h16_dental_practice.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h169_steril_release.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h187_lab_checks.sql");   // H169 release columns and the recall table, H187 the lab checks
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalDentalOpsModel.Configure);

		public static IEnumerable<object[]> Tables() => new[]
		{
			new object[] { typeof(HospitalDentalChair), "HospitalDentalChairs" }, new object[] { typeof(HospitalDentalVisit), "HospitalDentalVisits" },
			new object[] { typeof(HospitalDentalLab), "HospitalDentalLabs" }, new object[] { typeof(HospitalDentalLabCase), "HospitalDentalLabCases" },
			new object[] { typeof(HospitalDentalRecall), "HospitalDentalRecalls" }, new object[] { typeof(HospitalSterilCycle), "HospitalSterilCycles" },
			new object[] { typeof(HospitalSterilUse), "HospitalSterilUses" }, new object[] { typeof(HospitalSterilRecall), "HospitalSterilRecalls" },
		};

		[Theory]
		[MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory]
		[MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Fact]
		public void The_model_declares_no_index_filter() =>
			Assert.Empty(Model().GetEntityTypes().SelectMany(t => t.GetIndexes()).Where(i => i.GetFilter() != null));

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite() =>
			Assert.Equal(5, HospitalSliceParity.CheckedPrerequisites(HospitalSliceParity.Slice("hospital_h16_dental_practice.sql")).Count);   // practitioners, patients, plan lines, appointments, services
	}
}
