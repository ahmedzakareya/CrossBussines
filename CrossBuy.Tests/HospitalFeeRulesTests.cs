using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H19 — the doctors' fee rules, DATABASE-FREE, and the model against its slice.
	//
	// What must never happen: a category rule winning over a service's own, a per-unit fee paid twice (once per
	// part of the line), a patient's part counted before he finished paying, or a statement approved out of order.
	// ==========================================================================================
	public class HospitalFeeRulesTests
	{
		private static HospitalFeeRule R(string cat, int? svc, string basis, decimal v) => new() { Category = cat, ServiceId = svc, Basis = basis, Value = v };

		[Fact]
		public void A_services_own_rule_wins_over_its_categorys()
		{
			var rules = new[] { R("Consultation", null, "Percent", 40), R("Consultation", 9, "Percent", 25), R("Procedure", null, "Percent", 25) };
			Assert.Equal(25m, HospitalFeeRules.Pick(rules, 9, "Consultation")!.Value);
			Assert.Equal(40m, HospitalFeeRules.Pick(rules, 3, "Consultation")!.Value);
			Assert.Null(HospitalFeeRules.Pick(rules, 3, "Lab"));
		}

		[Fact]
		public void A_percent_is_of_what_the_part_collected()
		{
			var r = R("Consultation", null, "Percent", 40);
			Assert.Equal(120m, HospitalFeeRules.Share(r, HospitalFeePart.Patient, 300, 1, true, 3));
			Assert.Equal(15.39m, HospitalFeeRules.Share(R("Procedure", null, "Percent", 33.3m), HospitalFeePart.Payer, 46.21m, 1, true, 2));
		}

		[Fact]
		public void A_per_unit_fee_is_paid_once_on_the_lines_first_part()
		{
			var r = R("Accommodation", null, "PerUnit", 200);
			Assert.Equal(600m, HospitalFeeRules.Share(r, HospitalFeePart.Patient, 50, 3, true, 3));
			Assert.Equal(0m, HospitalFeeRules.Share(r, HospitalFeePart.Payer, 550, 3, true, 3));      // the patient's part already carried it
			Assert.Equal(600m, HospitalFeeRules.Share(r, HospitalFeePart.Payer, 600, 3, false, 3));    // fully covered: the payer's part carries it
		}

		[Fact]
		public void The_patients_part_is_collected_the_day_he_finished_paying()
		{
			var pays = new[] { (new DateTime(2026, 10, 4), 100m), (new DateTime(2026, 10, 20), 50m), (new DateTime(2026, 11, 2), 30m) };
			Assert.Equal(new DateTime(2026, 10, 20), HospitalFeeRules.PatientCollectedOn(150, pays));
			Assert.Equal(new DateTime(2026, 11, 2), HospitalFeeRules.PatientCollectedOn(170, pays));
			Assert.Null(HospitalFeeRules.PatientCollectedOn(500, pays));   // not yet
			Assert.Null(HospitalFeeRules.PatientCollectedOn(0, pays));     // he owed nothing
		}

		[Fact]
		public void A_statement_is_approved_one_step_at_a_time()
		{
			Assert.Equal(HospitalFeeStatus.DirectorApproved, HospitalFeeRules.Next(HospitalFeeStatus.Generated));
			Assert.Equal(HospitalFeeStatus.FinanceApproved, HospitalFeeRules.Next(HospitalFeeStatus.DirectorApproved));
			Assert.Null(HospitalFeeRules.Next(HospitalFeeStatus.FinanceApproved));
		}

		[Fact]
		public void A_rule_is_kept_only_in_range()
		{
			Assert.Null(HospitalFeeRules.RuleRefusal("Consultation", "Percent", 40));
			Assert.NotNull(HospitalFeeRules.RuleRefusal("Consultation", "Percent", 140));
			Assert.NotNull(HospitalFeeRules.RuleRefusal("Consultation", "PerUnit", -1));
			Assert.NotNull(HospitalFeeRules.RuleRefusal("Golf", "Percent", 10));
			Assert.True(HospitalFeeRules.IsPeriod("2026-10")); Assert.False(HospitalFeeRules.IsPeriod("2026-13")); Assert.False(HospitalFeeRules.IsPeriod("10-2026"));
		}

		// ---------------- model and slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h19_doctor_fees.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalFeesModel.Configure);

		public static IEnumerable<object[]> Tables() => new[]
		{
			new object[] { typeof(HospitalFeeRule), "HospitalFeeRules" }, new object[] { typeof(HospitalFeeSettings), "HospitalFeeSettings" },
			new object[] { typeof(HospitalFeeStatement), "HospitalFeeStatements" }, new object[] { typeof(HospitalFeeStatementLine), "HospitalFeeStatementLines" },
		};

		[Theory]
		[MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory]
		[MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Fact]
		public void Each_part_of_a_bill_line_is_counted_once_in_model_and_slice()
		{
			var ix = Model().FindEntityType(typeof(HospitalFeeStatementLine))!.GetIndexes().Single(i => i.Properties.Count == 2);
			Assert.True(ix.IsUnique);
			Assert.Equal("BillLineId,Part", string.Join(",", ix.Properties.Select(p => p.Name)));
			Assert.Contains("CREATE UNIQUE INDEX IX_HospitalFeeStatementLines_BillLineId_Part ON dbo.HospitalFeeStatementLines (BillLineId, Part);", Slice().Split('\n').Select(l => l.Trim()));
		}

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite() =>
			Assert.Equal(5, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);

		// found on BILL-000009: a self-pay bill (no split) that had also been claimed before H18 counted twice
		[Fact]
		public void A_claim_on_a_bill_closed_without_a_split_is_never_a_second_collection() =>
			Assert.False(HospitalFeeRules.PayerPartCounts(0m, null, 1));

		[Fact]
		public void A_claim_counts_for_the_payer_the_split_names() => Assert.True(HospitalFeeRules.PayerPartCounts(12.825m, 1, 1));

		[Fact]
		public void A_claim_by_another_payer_does_not_count() => Assert.False(HospitalFeeRules.PayerPartCounts(12.825m, 1, 2));

		[Fact]
		public void A_line_the_split_left_to_the_patient_has_no_payer_part() => Assert.False(HospitalFeeRules.PayerPartCounts(0m, 1, 1));
	}
}
