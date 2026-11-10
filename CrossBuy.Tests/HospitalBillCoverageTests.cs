using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H18 — the bill's split, DATABASE-FREE: the pure split (HospitalCoverage.Of) and the model against
	// its slice.
	//
	// What must never happen again: an insured patient asked for the whole bill while the payer is claimed its
	// share of the same services. The split must give the payer only covered lines, net of the contract's
	// discount, minus the co-pay (capped); the two shares must make the amount; and the bill must never claim
	// the discount from either side.
	// ==========================================================================================
	public class HospitalBillCoverageTests
	{
		private static HospitalPayer Payer(decimal copay = 20, decimal cap = 0, decimal discount = 0) =>
			new() { ID = 1, CompanyId = 1, CopayPct = copay, CopayCap = cap, DiscountPct = discount, IsActive = true, ValidFrom = new DateTime(2026, 1, 1) };
		private static readonly HospitalPatientPolicy Policy = new() { ID = 7, CompanyId = 1, PayerId = 1, PatientId = 3, IsActive = true, ValidFrom = new DateTime(2026, 1, 1) };
		private static HospitalSplitLine L(int id, decimal price, bool outside = false, decimal qty = 1) => new(id, "S" + id, "Consultation", qty, price, outside);

		[Fact]
		public void The_patient_pays_the_co_pay_and_the_payer_the_rest()
		{
			var s = HospitalCoverage.Of(Payer(20), Policy, 5, new[] { L(1, 30), L(2, 70) }, 3);   // priced on the payer's list: no discount
			Assert.Equal(100m, s.Split.Gross);
			Assert.Equal(80m, s.Split.InsurerShare);
			Assert.Equal(20m, s.Split.PatientShare);
			Assert.Equal(new[] { 24m, 56m }, s.Split.InsurerPerLine);
			Assert.Equal(s.Split.Gross, s.Split.InsurerShare + s.Split.PatientShare);
		}

		[Fact]
		public void A_line_outside_the_contract_is_the_patients_in_full()
		{
			var s = HospitalCoverage.Of(Payer(10), Policy, 5, new[] { L(1, 100), L(2, 50, outside: true) }, 3);
			Assert.Equal(90m, s.Split.InsurerShare);
			Assert.Equal(60m, s.Split.PatientShare);   // 10 co-pay + 50 not covered
			Assert.Equal(0m, s.Split.InsurerPerLine[1]);
			Assert.False(s.Lines[1].Covered);
		}

		[Fact]
		public void The_co_pay_stops_at_its_cap()
		{
			var s = HospitalCoverage.Of(Payer(20, cap: 15), Policy, 5, new[] { L(1, 500) }, 3);
			Assert.Equal(15m, s.Split.PatientShare);
			Assert.Equal(485m, s.Split.InsurerShare);
		}

		[Fact]
		public void On_the_main_list_the_contract_discount_is_paid_by_neither_side()
		{
			var s = HospitalCoverage.Of(Payer(20, discount: 10), Policy, null, new[] { L(1, 100) }, 3);   // main list: 10% off the price
			Assert.Equal(90m, s.Lines[0].Amount);
			Assert.Equal(72m, s.Split.InsurerShare);
			Assert.Equal(18m, s.Split.PatientShare);
			Assert.Equal(90m, s.Split.InsurerShare + s.Split.PatientShare);   // the 10 discount is in neither share
			var own = HospitalCoverage.Of(Payer(20, discount: 10), Policy, 5, new[] { L(1, 100) }, 3);        // the payer's own list has it built in
			Assert.Equal(100m, own.Lines[0].Amount);
		}

		[Fact]
		public void The_payers_share_is_spread_by_line_and_the_last_takes_the_rounding()
		{
			var s = HospitalCoverage.Of(Payer(15), Policy, 5, new[] { L(1, 10), L(2, 10), L(3, 10) }, 3);
			Assert.Equal(s.Split.InsurerShare, s.Split.InsurerPerLine.Sum());
			Assert.All(s.Split.InsurerPerLine, x => Assert.InRange(x, 8.49m, 8.51m));
		}

		// ---------------- model and slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h18_bill_coverage.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalBillCoverageModel.Configure);

		public static IEnumerable<object[]> Tables() => new[]
		{
			new object[] { typeof(HospitalBillCoverage), "HospitalBillCoverages" }, new object[] { typeof(HospitalBillCoverageLine), "HospitalBillCoverageLines" },
		};

		[Theory]
		[MemberData(nameof(Tables))]
		public void The_model_names_the_tables_its_slice_creates(Type entity, string table) => HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);

		[Theory]
		[MemberData(nameof(Tables))]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table) => HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);

		[Fact]
		public void A_bill_is_split_once_in_model_and_slice()
		{
			Assert.True(Model().FindEntityType(typeof(HospitalBillCoverage))!.GetIndexes().Single(i => i.Properties.Single().Name == "BillId").IsUnique);
			Assert.Contains("CREATE UNIQUE INDEX IX_HospitalBillCoverages_BillId ON dbo.HospitalBillCoverages (BillId);", Slice().Split('\n').Select(l => l.Trim()));
		}

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite() =>
			Assert.Equal(4, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);   // bills, bill lines, payers, policies
	}
}
