using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H66 — equipment work orders, with no database: a fault names its device and its problem; the path
	// Open → Assigned → InProgress → Done (or Cancelled); who may take each step; only a registered asset's repair is
	// posted; the hours to fix; and the table against its slice.
	// ==========================================================================================
	public class HospitalWorkOrderTests
	{
		private static HospitalWorkOrder O(string status, int? tech = null, int? asset = 5) => new() { Status = status, TechnicianEmployeeId = tech, AssetId = asset, ReportedAt = new DateTime(2026, 11, 8, 8, 0, 0) };
		private static HospitalWorkOrderMoveInput To(string to, Action<HospitalWorkOrderMoveInput>? f = null) { var m = new HospitalWorkOrderMoveInput { To = to }; f?.Invoke(m); return m; }

		[Fact]
		public void A_fault_names_its_device_its_problem_and_its_urgency()
		{
			Assert.Null(HospitalWorkOrderRules.ReportRefusal(new HospitalWorkOrderInput { AssetId = 3, Problem = "No display", Priority = HospitalWorkOrderPriority.Urgent }));
			Assert.Null(HospitalWorkOrderRules.ReportRefusal(new HospitalWorkOrderInput { Equipment = "Suction pump", Problem = "Weak", Priority = HospitalWorkOrderPriority.Normal }));
			Assert.Equal("Choose the device, or describe it", HospitalWorkOrderRules.ReportRefusal(new HospitalWorkOrderInput { Problem = "x", Priority = "Normal" }));
			Assert.Equal("Describe the problem", HospitalWorkOrderRules.ReportRefusal(new HospitalWorkOrderInput { AssetId = 3, Priority = "Normal" }));
			Assert.Equal("Choose how urgent it is", HospitalWorkOrderRules.ReportRefusal(new HospitalWorkOrderInput { AssetId = 3, Problem = "x", Priority = "Soon" }));
		}

		[Fact]
		public void The_administrator_assigns_and_the_technician_works_his_own()
		{
			Assert.Null(HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.Open), To(HospitalWorkOrderStatus.Assigned, m => m.TechnicianEmployeeId = 9), 1, true));
			Assert.Equal("forbidden", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.Open), To(HospitalWorkOrderStatus.Assigned, m => m.TechnicianEmployeeId = 9), 1, false));
			Assert.Equal("Choose the technician", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.Open), To(HospitalWorkOrderStatus.Assigned), 1, true));
			Assert.Null(HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.Assigned, 9), To(HospitalWorkOrderStatus.InProgress), 9, false));
			Assert.Equal("Only the assigned technician starts this order", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.Assigned, 9), To(HospitalWorkOrderStatus.InProgress), 8, false));
			Assert.Equal("Only the assigned technician closes this order", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.InProgress, 9), To(HospitalWorkOrderStatus.Done, m => m.Resolution = "fixed"), 8, false));
		}

		[Fact]
		public void Closing_says_what_was_done_and_only_an_asset_is_posted()
		{
			Assert.Equal("Write what was done", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.InProgress, 9), To(HospitalWorkOrderStatus.Done), 9, false));
			Assert.Equal("A cost cannot be negative", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.InProgress, 9), To(HospitalWorkOrderStatus.Done, m => { m.Resolution = "x"; m.Cost = -1; }), 9, false));
			Assert.Equal("Only a registered asset's repair is posted", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.InProgress, 9, null), To(HospitalWorkOrderStatus.Done, m => { m.Resolution = "x"; m.Cost = 10; m.PayFromAccountId = 3; }), 9, false));
			Assert.Null(HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.InProgress, 9), To(HospitalWorkOrderStatus.Done, m => { m.Resolution = "x"; m.Cost = 10; m.PayFromAccountId = 3; }), 9, false));
		}

		[Fact]
		public void Steps_out_of_order_are_refused_and_a_cancel_says_why()
		{
			Assert.Equal("This order cannot take that step from where it is", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.Open), To(HospitalWorkOrderStatus.Done, m => m.Resolution = "x"), 1, true));
			Assert.Equal("This order cannot take that step from where it is", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.InProgress, 9), To(HospitalWorkOrderStatus.Cancelled, m => m.Reason = "x"), 1, true));
			Assert.Equal("Write why it is cancelled", HospitalWorkOrderRules.MoveRefusal(O(HospitalWorkOrderStatus.Assigned, 9), To(HospitalWorkOrderStatus.Cancelled), 1, true));
			var done = O(HospitalWorkOrderStatus.Done, 9); done.DoneAt = done.ReportedAt.AddHours(5.5);
			Assert.Equal(5.5, HospitalWorkOrderRules.HoursToFix(done));
			Assert.Null(HospitalWorkOrderRules.HoursToFix(O(HospitalWorkOrderStatus.Open)));
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h66_work_orders.sql");
			var m = HospitalSliceParity.Model(HospitalWorkOrderModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalWorkOrder), "HospitalWorkOrders");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalWorkOrder), "HospitalWorkOrders");
			foreach (var x in HospitalWorkOrderStatus.All.Concat(HospitalWorkOrderPriority.All)) Assert.Contains("'" + x + "'", s);
			Assert.Contains("CHECK (AssetId IS NOT NULL OR Equipment IS NOT NULL)", s);
			Assert.Equal(new[] { "HospitalRooms" }, HospitalSliceParity.CheckedPrerequisites(s));
		}
	}
}
