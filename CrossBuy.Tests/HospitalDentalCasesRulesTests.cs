using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H17 — the long dental cases' rules, DATABASE-FREE, and the model against its slice.
	//
	// What must never happen: a contract asking more than it is worth, a missed adjustment not flagged, stages
	// on braces or none on aligners, an implant placed with no lot, crowned below a loadable stability, or
	// moved backwards from restored to healing.
	// ==========================================================================================
	public class HospitalDentalCasesRulesTests
	{
		[Theory]
		[InlineData("2025-09-14", "2026-11-03", 13)]
		[InlineData("2025-09-14", "2026-11-14", 14)]
		[InlineData("2026-11-03", "2026-11-03", 0)]
		[InlineData("2026-12-01", "2026-11-03", 0)]
		public void Months_elapsed_count_whole_months(string start, string today, int months) =>
			Assert.Equal(months, HospitalDentalCasesRules.MonthsElapsed(DateTime.Parse(start), DateTime.Parse(today)));

		[Fact]
		public void Progress_is_months_over_the_plan_and_never_past_full()
		{
			Assert.Equal(54, HospitalDentalCasesRules.Progress(new(2025, 9, 14), 24, new(2026, 11, 3)));
			Assert.Equal(100, HospitalDentalCasesRules.Progress(new(2024, 1, 10), 24, new(2026, 11, 3)));
		}

		[Fact]
		public void What_is_due_is_the_instalments_so_far_less_what_was_paid_never_more_than_the_contract()
		{
			var start = new DateTime(2025, 9, 14); var today = new DateTime(2026, 11, 3);   // 13 whole months: 14 instalments by today
			Assert.Equal(5250m, HospitalDentalCasesRules.ExpectedPaid(9000, 375, start, today));
			Assert.Equal(0m, HospitalDentalCasesRules.Due(9000, 375, start, today, 6000));   // paid ahead
			Assert.Equal(750m, HospitalDentalCasesRules.Due(9000, 375, start, today, 4500));
			Assert.Equal(9000m, HospitalDentalCasesRules.ExpectedPaid(9000, 375, new(2020, 1, 1), today));   // capped at the contract
			Assert.Equal(8000m, HospitalDentalCasesRules.ExpectedPaid(8000, 0, start, today));                 // no instalments: paid at the start
			Assert.Equal(0m, HospitalDentalCasesRules.ExpectedPaid(8000, 400, new(2027, 1, 1), today));        // not started
		}

		[Fact]
		public void A_missed_adjustment_is_an_open_case_past_its_next_visit()
		{
			var today = new DateTime(2026, 11, 3);
			Assert.True(HospitalDentalCasesRules.Missed(HospitalOrthoStatus.Active, new(2026, 11, 2), today));
			Assert.False(HospitalDentalCasesRules.Missed(HospitalOrthoStatus.Active, today, today));
			Assert.False(HospitalDentalCasesRules.Missed(HospitalOrthoStatus.Completed, new(2026, 1, 1), today));
			Assert.False(HospitalDentalCasesRules.Missed(HospitalOrthoStatus.Active, null, today));
		}

		[Fact]
		public void Only_clear_aligners_are_staged_and_an_instalment_never_exceeds_the_contract()
		{
			Assert.Null(HospitalDentalCasesRules.CaseRefusal("ClearAligners", 15, 30, 14000, 700));
			Assert.Null(HospitalDentalCasesRules.CaseRefusal("FixedMetal", 24, null, 9000, 375));
			Assert.NotNull(HospitalDentalCasesRules.CaseRefusal("ClearAligners", 15, null, 14000, 700));
			Assert.NotNull(HospitalDentalCasesRules.CaseRefusal("FixedMetal", 24, 10, 9000, 375));
			Assert.NotNull(HospitalDentalCasesRules.CaseRefusal("FixedMetal", 24, null, 300, 375));
			Assert.NotNull(HospitalDentalCasesRules.CaseRefusal("FixedMetal", 0, null, 9000, 375));
			Assert.NotNull(HospitalDentalCasesRules.CaseRefusal("Headgear", 24, null, 9000, 375));
		}

		[Fact]
		public void An_implant_moves_forward_only_and_may_fail_once_placed()
		{
			Assert.True(HospitalDentalCasesRules.CanMove(HospitalImplantStage.Planned, HospitalImplantStage.Healing));
			Assert.True(HospitalDentalCasesRules.CanMove(HospitalImplantStage.Healing, HospitalImplantStage.Restored));
			Assert.True(HospitalDentalCasesRules.CanMove(HospitalImplantStage.Restored, HospitalImplantStage.Failed));
			Assert.False(HospitalDentalCasesRules.CanMove(HospitalImplantStage.Planned, HospitalImplantStage.Restored));
			Assert.False(HospitalDentalCasesRules.CanMove(HospitalImplantStage.Planned, HospitalImplantStage.Failed));
			Assert.False(HospitalDentalCasesRules.CanMove(HospitalImplantStage.Restored, HospitalImplantStage.Healing));
		}

		[Fact]
		public void A_placed_fixture_has_its_lot_and_a_real_day()
		{
			var today = new DateTime(2026, 11, 3);
			Assert.Null(HospitalDentalCasesRules.PlaceRefusal("STR-24K118", today, 74, today));
			Assert.NotNull(HospitalDentalCasesRules.PlaceRefusal(" ", today, 74, today));
			Assert.NotNull(HospitalDentalCasesRules.PlaceRefusal("STR-24K118", today.AddDays(1), null, today));
			Assert.NotNull(HospitalDentalCasesRules.PlaceRefusal("STR-24K118", today, 0, today));
		}

		[Fact]
		public void Survival_counts_the_implants_placed_in_three_years()
		{
			var today = new DateTime(2026, 11, 3);
			var list = new (DateTime?, string)[] { (new(2026, 7, 14), "Restored"), (new(2026, 9, 2), "Healing"), (new(2025, 1, 1), "Failed"), (new(2026, 2, 1), "Restored"), (null, "Planned"), (new(2019, 1, 1), "Failed") };
			Assert.Equal(75m, HospitalDentalCasesRules.SurvivalRate(list, today));
			Assert.Null(HospitalDentalCasesRules.SurvivalRate(new (DateTime?, string)[] { (null, "Planned") }, today));
		}

		// ---------------- model and slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h17_dental_cases.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h167_ortho_payments.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h168_implant_stability.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h185_ortho_record.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h186_implant_reviews.sql");   // H167 payment columns, H168 implant loading columns, H185 the ortho record, H186 implant traceability
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalDentalCasesModel.Configure);

		public static IEnumerable<object[]> Tables() => new[]
		{
			new object[] { typeof(HospitalOrthoCase), "HospitalOrthoCases" }, new object[] { typeof(HospitalOrthoVisit), "HospitalOrthoVisits" },
			new object[] { typeof(HospitalOrthoPayment), "HospitalOrthoPayments" }, new object[] { typeof(HospitalImplant), "HospitalImplants" },
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
			Assert.Equal(2, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);   // patients, practitioners
	}
}
