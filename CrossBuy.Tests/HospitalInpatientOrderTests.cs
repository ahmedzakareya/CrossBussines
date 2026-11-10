using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H75 — lab tests and imaging for inpatients, with no database: what an order needs, when one can still be
	// cancelled (only before anything happened to it), an order set line that names a test or exam, and the table against
	// its slice.
	// ==========================================================================================
	public class HospitalInpatientOrderTests
	{
		[Fact]
		public void An_order_names_its_tests_and_a_known_priority()
		{
			Assert.Null(HospitalInpatientOrderRules.Refusal(new HospitalInpatientOrderInput { ServiceIds = new() { 1, 2 } }));
			Assert.Null(HospitalInpatientOrderRules.Refusal(new HospitalInpatientOrderInput { ServiceIds = new() { 1 }, Priority = HospitalLabPriority.Stat }));
			Assert.Equal("Choose at least one test or exam", HospitalInpatientOrderRules.Refusal(new HospitalInpatientOrderInput()));
			Assert.Equal("Choose the priority", HospitalInpatientOrderRules.Refusal(new HospitalInpatientOrderInput { ServiceIds = new() { 1 }, Priority = "Soon" }));
			Assert.Equal("At most 30 tests and exams at once", HospitalInpatientOrderRules.Refusal(new HospitalInpatientOrderInput { ServiceIds = Enumerable.Range(1, 31).ToList() }));
		}

		[Fact]
		public void Only_an_order_nothing_has_happened_to_can_be_cancelled()
		{
			Assert.True(HospitalInpatientOrderRules.Cancellable(HospitalInpatientOrderKind.Lab, HospitalLabStatus.Ordered));
			Assert.False(HospitalInpatientOrderRules.Cancellable(HospitalInpatientOrderKind.Lab, HospitalLabStatus.Collected));
			Assert.False(HospitalInpatientOrderRules.Cancellable(HospitalInpatientOrderKind.Lab, HospitalLabStatus.Verified));
			Assert.True(HospitalInpatientOrderRules.Cancellable(HospitalInpatientOrderKind.Imaging, HospitalRadStatus.Scheduled));
			Assert.False(HospitalInpatientOrderRules.Cancellable(HospitalInpatientOrderKind.Imaging, HospitalRadStatus.Arrived));
			Assert.False(HospitalInpatientOrderRules.Cancellable(HospitalInpatientOrderKind.Lab, null));
		}

		[Fact]
		public void An_order_set_line_can_name_a_test_or_an_exam()
		{
			Assert.Null(HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Lab, ServiceId = 7 }));
			Assert.Null(HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Imaging, ServiceId = 9 }));
			Assert.Equal("Choose the test or exam", HospitalOrderSetRules.ItemRefusal(new HospitalOrderSetItemInput { Kind = HospitalOrderSetItemKind.Lab }));
			Assert.Equal(new[] { "Med", "Task", "Lab", "Rad" }, HospitalOrderSetItemKind.All);
		}

		[Fact]
		public void The_table_matches_its_slice_and_moves_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h75_inpatient_orders.sql");
			var m = HospitalSliceParity.Model(HospitalInpatientOrderModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalInpatientOrder), "HospitalInpatientOrders");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalInpatientOrder), "HospitalInpatientOrders");
			foreach (var x in HospitalInpatientOrderKind.All) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalInpatientOrder));
		}
	}
}
