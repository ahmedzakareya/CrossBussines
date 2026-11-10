using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H38 — consignment implants, with no database: what may be received, when an implant may be used on a
	// case or go back, the prototype's states (they follow the purchase order), and the model against its slice.
	// ==========================================================================================
	public class HospitalConsignmentTests
	{
		[Fact]
		public void Serials_are_received_once_each_with_the_agreed_price()
		{
			Assert.Null(HospitalConsignmentRules.ReceiveRefusal(new[] { "STN-88120", "STN-88121" }, 450));
			Assert.Equal("Enter at least one serial number", HospitalConsignmentRules.ReceiveRefusal(Array.Empty<string>(), 450));
			Assert.Equal("A serial number is entered twice", HospitalConsignmentRules.ReceiveRefusal(new[] { "A1", "a1" }, 450));
			Assert.Equal("A serial number is up to 60 characters", HospitalConsignmentRules.ReceiveRefusal(new[] { new string('x', 61) }, 450));
			Assert.Equal("Enter the price agreed with the supplier", HospitalConsignmentRules.ReceiveRefusal(new[] { "A1" }, 0));
		}

		private static readonly DateTime Day = new(2026, 11, 5);

		[Theory]
		[InlineData(HospitalConsignmentStatus.InStore, HospitalSurgeryStatus.InProgress, null)]
		[InlineData(HospitalConsignmentStatus.InStore, HospitalSurgeryStatus.Done, null)]
		[InlineData(HospitalConsignmentStatus.InStore, HospitalSurgeryStatus.Planned, "An implant is recorded on a case in progress or just closed")]
		[InlineData(HospitalConsignmentStatus.Used, HospitalSurgeryStatus.InProgress, "This implant is no longer in the store")]
		[InlineData(HospitalConsignmentStatus.Returned, HospitalSurgeryStatus.InProgress, "This implant is no longer in the store")]
		public void An_implant_from_the_store_goes_on_a_case_in_progress_or_just_closed(string status, string caseStatus, string? why) =>
			Assert.Equal(why, HospitalConsignmentRules.UseRefusal(status, null, caseStatus, Day));

		[Fact]
		public void An_expired_implant_is_never_used()
		{
			Assert.Equal("This implant has expired", HospitalConsignmentRules.UseRefusal(HospitalConsignmentStatus.InStore, Day.AddDays(-1), HospitalSurgeryStatus.InProgress, Day));
			Assert.Null(HospitalConsignmentRules.UseRefusal(HospitalConsignmentStatus.InStore, Day, HospitalSurgeryStatus.InProgress, Day));   // good through its last day
		}

		[Fact]
		public void Only_an_implant_in_the_store_goes_back()
		{
			Assert.Null(HospitalConsignmentRules.ReturnRefusal(HospitalConsignmentStatus.InStore));
			Assert.NotNull(HospitalConsignmentRules.ReturnRefusal(HospitalConsignmentStatus.Used));
			Assert.NotNull(HospitalConsignmentRules.ReturnRefusal(HospitalConsignmentStatus.Returned));
		}

		[Theory]
		[InlineData(HospitalConsignmentStatus.InStore, null, "Consignment")]
		[InlineData(HospitalConsignmentStatus.Returned, null, "Returned")]
		[InlineData(HospitalConsignmentStatus.Used, "Draft", "DraftPO")]
		[InlineData(HospitalConsignmentStatus.Used, "Approved", "POApproved")]
		[InlineData(HospitalConsignmentStatus.Used, "Received", "Invoiced")]
		[InlineData(HospitalConsignmentStatus.Used, "Closed", "Invoiced")]
		[InlineData(HospitalConsignmentStatus.Used, "Cancelled", "POCancelled")]
		public void The_state_follows_the_purchase_order(string status, string? po, string state) =>
			Assert.Equal(state, HospitalConsignmentRules.State(status, po));

		private static string Slice() => HospitalSliceParity.Slice("hospital_h38_consignment.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalConsignmentModel.Configure);

		[Fact]
		public void The_table_matches_its_slice()
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalConsignment), "HospitalConsignments");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalConsignment), "HospitalConsignments");
			foreach (var s in HospitalConsignmentStatus.All) Assert.Contains($"N'{s}'", Slice());
			Assert.Equal(new[] { "HospitalPatients", "HospitalSurgeries" }, HospitalSliceParity.CheckedPrerequisites(Slice()).OrderBy(x => x));
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
			var ix = Model().FindEntityType(typeof(HospitalConsignment))!.GetIndexes().Single(i => i.IsUnique);
			Assert.Equal(new[] { "CompanyId", "ItemId", "SerialNo" }, ix.Properties.Select(x => x.Name));
		}
	}
}
