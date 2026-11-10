using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H41 — the blood bank, with no database: the compatibility tables (red cells, plasma, whole blood), which
	// bags a cross-match reserves, the shelf the prototype counts, the request's steps, and the model against its slice.
	// ==========================================================================================
	public class HospitalBloodBankTests
	{
		// recipient → the donor groups whose RED CELLS it may receive (the standard table)
		[Theory]
		[InlineData("O-", "O-")]
		[InlineData("O+", "O+,O-")]
		[InlineData("A-", "A-,O-")]
		[InlineData("A+", "A+,A-,O+,O-")]
		[InlineData("B-", "B-,O-")]
		[InlineData("B+", "B+,B-,O+,O-")]
		[InlineData("AB-", "AB-,A-,B-,O-")]
		[InlineData("AB+", "O+,O-,A+,A-,B+,B-,AB+,AB-")]
		public void Red_cells_follow_the_ABO_and_Rh_table(string recipient, string donors)
		{
			var ok = donors.Split(',').ToHashSet();
			foreach (var d in HospitalBloodGroups.All)
				Assert.True(HospitalBloodRules.RedCellsFit(recipient, d) == ok.Contains(d), $"{d} → {recipient}");
		}

		// recipient → the donor groups whose PLASMA it may receive (reversed; Rh does not matter)
		[Theory]
		[InlineData("O+", "O,A,B,AB")]
		[InlineData("A-", "A,AB")]
		[InlineData("B+", "B,AB")]
		[InlineData("AB-", "AB")]
		public void Plasma_follows_the_reversed_table(string recipient, string donorAbo)
		{
			var ok = donorAbo.Split(',').ToHashSet();
			foreach (var d in HospitalBloodGroups.All)
				Assert.True(HospitalBloodRules.PlasmaFits(recipient, d) == ok.Contains(d.TrimEnd('+', '-')), $"{d} plasma → {recipient}");
		}

		[Fact]
		public void Whole_blood_is_the_same_group_and_platelets_follow_plasma()
		{
			Assert.True(HospitalBloodRules.Fits(HospitalBloodProduct.WholeBlood, "A+", "A+"));
			Assert.False(HospitalBloodRules.Fits(HospitalBloodProduct.WholeBlood, "A+", "O-"));
			Assert.True(HospitalBloodRules.Fits(HospitalBloodProduct.Platelets, "A+", "AB-"));
			Assert.False(HospitalBloodRules.Fits(HospitalBloodProduct.Platelets, "A+", "O+"));
		}

		private static readonly DateTime Today = new(2026, 11, 5);
		private static HospitalBloodUnit Bag(int id, string g, int days, string product = HospitalBloodProduct.PackedCells, string status = HospitalBloodUnitStatus.Available) =>
			new() { ID = id, UnitNo = "U" + id, BloodGroup = g, Product = product, ExpiresOn = Today.AddDays(days), Status = status };

		[Fact]
		public void A_cross_match_takes_the_patients_own_group_first_then_the_nearest_expiry()
		{
			var shelf = new[] { Bag(1, "O-", 2), Bag(2, "A+", 20), Bag(3, "A+", 5), Bag(4, "B+", 1), Bag(5, "A+", -1), Bag(6, "A+", 1, status: HospitalBloodUnitStatus.Reserved), Bag(7, "A+", 1, HospitalBloodProduct.Plasma) };
			Assert.Equal(new[] { 3, 2 }, HospitalBloodRules.Pick(shelf, HospitalBloodProduct.PackedCells, "A+", 2, Today)!.Select(u => u.ID));
			// a third bag comes from O- (compatible), never B+, the expired, the reserved or the plasma
			Assert.Equal(new[] { 3, 2, 1 }, HospitalBloodRules.Pick(shelf, HospitalBloodProduct.PackedCells, "A+", 3, Today)!.Select(u => u.ID));
			Assert.Null(HospitalBloodRules.Pick(shelf, HospitalBloodProduct.PackedCells, "A+", 4, Today));
		}

		[Fact]
		public void The_shelf_counts_available_unexpired_red_cells_per_group()
		{
			var s = HospitalBloodRules.Shelf(new[] { Bag(1, "O+", 3), Bag(2, "O+", 3, HospitalBloodProduct.WholeBlood), Bag(3, "O+", -1), Bag(4, "O+", 3, HospitalBloodProduct.Plasma), Bag(5, "O+", 3, status: HospitalBloodUnitStatus.Reserved) }, Today);
			Assert.Equal(2, s["O+"]);
			Assert.Equal(8, s.Count);
		}

		[Theory]
		[InlineData(HospitalTransfusionStatus.Requested, HospitalTransfusionStatus.CrossMatching, true)]
		[InlineData(HospitalTransfusionStatus.CrossMatching, HospitalTransfusionStatus.Compatible, true)]
		[InlineData(HospitalTransfusionStatus.CrossMatching, HospitalTransfusionStatus.Incompatible, true)]
		[InlineData(HospitalTransfusionStatus.Compatible, HospitalTransfusionStatus.Issued, true)]
		[InlineData(HospitalTransfusionStatus.Compatible, HospitalTransfusionStatus.Cancelled, true)]
		[InlineData(HospitalTransfusionStatus.Requested, HospitalTransfusionStatus.Issued, false)]
		[InlineData(HospitalTransfusionStatus.Requested, HospitalTransfusionStatus.Compatible, false)]
		[InlineData(HospitalTransfusionStatus.Issued, HospitalTransfusionStatus.Cancelled, false)]
		[InlineData(HospitalTransfusionStatus.Incompatible, HospitalTransfusionStatus.Compatible, false)]
		public void A_request_moves_one_step_at_a_time(string from, string to, bool ok) => Assert.Equal(ok, HospitalBloodRules.CanMove(from, to));

		[Fact]
		public void A_request_needs_the_patients_group_and_a_sane_number()
		{
			Assert.Null(HospitalBloodRules.RequestRefusal(HospitalBloodProduct.PackedCells, 2, "O+"));
			Assert.Equal("Record the patient's blood group on the file first", HospitalBloodRules.RequestRefusal(HospitalBloodProduct.PackedCells, 2, null));
			Assert.Equal("Between 1 and 10 units are requested at a time", HospitalBloodRules.RequestRefusal(HospitalBloodProduct.PackedCells, 11, "O+"));
			Assert.Equal("Choose the blood product", HospitalBloodRules.RequestRefusal("Serum", 1, "O+"));
		}

		[Fact]
		public void Bags_are_received_once_each_and_unexpired()
		{
			Assert.Null(HospitalBloodRules.ReceiveRefusal(new[] { "BB-1", "BB-2" }, "O+", HospitalBloodProduct.PackedCells, Today, Today));
			Assert.Equal("A bag number is entered twice", HospitalBloodRules.ReceiveRefusal(new[] { "bb-1", "BB-1" }, "O+", HospitalBloodProduct.PackedCells, Today, Today));
			Assert.Equal("This bag has already expired", HospitalBloodRules.ReceiveRefusal(new[] { "BB-1" }, "O+", HospitalBloodProduct.PackedCells, Today.AddDays(-1), Today));
			Assert.Equal("Choose the blood group", HospitalBloodRules.ReceiveRefusal(new[] { "BB-1" }, "C+", HospitalBloodProduct.PackedCells, Today, Today));
		}

		private static string Slice() => HospitalSliceParity.Slice("hospital_h41_blood_bank.sql");
		private static string H51() => Slice() + Environment.NewLine + HospitalSliceParity.Slice("hospital_h51_blood_billing.sql");

		[Fact]
		public void H51_charges_by_product_once_and_only_adds()
		{
			var s = HospitalSliceParity.Slice("hospital_h51_blood_billing.sql");
			Assert.Contains("ALTER TABLE dbo.HospitalTransfusionRequests ADD BillLineId int NULL;", s);
			Assert.Contains("CREATE UNIQUE INDEX IX_HospitalBloodProductServices_CompanyId_Product ON dbo.HospitalBloodProductServices (CompanyId, Product);", s);
			foreach (var p in HospitalBloodProduct.All) Assert.Contains("'" + p + "'", s);
			Assert.DoesNotContain("DROP ", s, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(new[] { "HospitalServices" }, HospitalSliceParity.CheckedPrerequisites(s));
			Assert.Equal(new[] { "CompanyId", "Product" }, Model().FindEntityType(typeof(HospitalBloodProductService))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
		}
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalBloodBankModel.Configure);

		[Fact]
		public void The_tables_match_their_slice()
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalBloodUnit), "HospitalBloodUnits");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalBloodUnit), "HospitalBloodUnits");
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalTransfusionRequest), "HospitalTransfusionRequests");
			HospitalSliceParity.ColumnsMatch(Model(), H51(), typeof(HospitalTransfusionRequest), "HospitalTransfusionRequests");   // + H51's BillLineId
			HospitalSliceParity.NamesItsTable(Model(), H51(), typeof(HospitalBloodProductService), "HospitalBloodProductServices");
			HospitalSliceParity.ColumnsMatch(Model(), H51(), typeof(HospitalBloodProductService), "HospitalBloodProductServices");
			foreach (var v in HospitalBloodProduct.All.Concat(HospitalBloodUnitStatus.All).Concat(HospitalTransfusionStatus.All).Concat(HospitalBloodGroups.All)) Assert.Contains($"N'{v}'", Slice());
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(Slice()));
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
			Assert.Equal(new[] { "CompanyId", "UnitNo" }, Model().FindEntityType(typeof(HospitalBloodUnit))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
		}
	}
}
