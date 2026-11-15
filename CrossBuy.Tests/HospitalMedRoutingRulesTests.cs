using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H21 — medication routing, DATABASE-FREE: the six steps in order (the cabinet's two skipped), the
	// controlled drug's porter and second signature, the wristband, lateness and turnaround, which dose a delivery
	// answers, and the model against its slice.
	// ==========================================================================================
	public class HospitalMedRoutingRulesTests
	{
		private static readonly DateTime T0 = new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);

		// ---------------- the steps ----------------

		[Theory]
		[InlineData(HospitalMedStage.Prescribed, HospitalMedStage.Verified)]
		[InlineData(HospitalMedStage.Verified, HospitalMedStage.Prepared)]
		[InlineData(HospitalMedStage.Prepared, HospitalMedStage.Dispatched)]
		[InlineData(HospitalMedStage.Dispatched, HospitalMedStage.Received)]
		[InlineData(HospitalMedStage.Received, HospitalMedStage.Administered)]
		public void By_tube_every_step_follows_the_last(byte from, byte to) => Assert.Equal(to, HospitalMedRoutingRules.Next(HospitalMedVia.Tube, from));

		[Fact]
		public void From_the_cabinet_preparing_and_dispatching_are_not_needed()
		{
			Assert.Equal(HospitalMedStage.Received, HospitalMedRoutingRules.Next(HospitalMedVia.Cabinet, HospitalMedStage.Verified));
			Assert.True(HospitalMedRoutingRules.Skips(HospitalMedVia.Cabinet, HospitalMedStage.Prepared));
			Assert.True(HospitalMedRoutingRules.Skips(HospitalMedVia.Cabinet, HospitalMedStage.Dispatched));
			Assert.False(HospitalMedRoutingRules.Skips(HospitalMedVia.Porter, HospitalMedStage.Prepared));
		}

		[Fact]
		public void Nothing_follows_administration() => Assert.Null(HospitalMedRoutingRules.Next(HospitalMedVia.Tube, HospitalMedStage.Administered));

		[Fact]
		public void A_step_out_of_order_is_refused() =>
			Assert.NotNull(HospitalMedRoutingRules.StepRefusal(HospitalMedStage.Verified, HospitalMedStage.Received, HospitalMedVia.Tube, false, false, 1, null));

		[Fact]
		public void A_skipped_step_cannot_be_done_on_the_cabinet_route() =>
			Assert.NotNull(HospitalMedRoutingRules.StepRefusal(HospitalMedStage.Verified, HospitalMedStage.Prepared, HospitalMedVia.Cabinet, false, false, 1, null));

		[Fact]
		public void A_step_done_twice_is_refused() =>
			Assert.NotNull(HospitalMedRoutingRules.StepRefusal(HospitalMedStage.Verified, HospitalMedStage.Verified, HospitalMedVia.Tube, false, false, 1, null));

		[Fact]
		public void A_cancelled_delivery_takes_no_step() =>
			Assert.NotNull(HospitalMedRoutingRules.StepRefusal(HospitalMedStage.Prescribed, HospitalMedStage.Verified, HospitalMedVia.Tube, true, false, 1, null));

		[Fact]
		public void A_given_dose_takes_no_step() =>
			Assert.NotNull(HospitalMedRoutingRules.StepRefusal(HospitalMedStage.Administered, HospitalMedStage.Administered, HospitalMedVia.Tube, false, false, 1, null));

		[Fact]
		public void The_next_step_is_accepted() =>
			Assert.Null(HospitalMedRoutingRules.StepRefusal(HospitalMedStage.Prescribed, HospitalMedStage.Verified, HospitalMedVia.Tube, false, false, 1, null));

		// ---------------- controlled drugs ----------------

		[Theory]
		[InlineData(HospitalMedStage.Dispatched, HospitalMedStage.Received)]
		[InlineData(HospitalMedStage.Received, HospitalMedStage.Administered)]
		public void A_controlled_drug_needs_a_second_signature_on_receipt_and_administration(byte from, byte to)
		{
			Assert.NotNull(HospitalMedRoutingRules.StepRefusal(from, to, HospitalMedVia.Porter, false, true, 1, null));
			Assert.NotNull(HospitalMedRoutingRules.StepRefusal(from, to, HospitalMedVia.Porter, false, true, 1, 1));   // not himself
			Assert.Null(HospitalMedRoutingRules.StepRefusal(from, to, HospitalMedVia.Porter, false, true, 1, 2));
		}

		[Fact]
		public void The_pharmacy_steps_of_a_controlled_drug_need_no_witness() =>
			Assert.Null(HospitalMedRoutingRules.StepRefusal(HospitalMedStage.Prescribed, HospitalMedStage.Verified, HospitalMedVia.Porter, false, true, 1, null));

		[Theory]
		[InlineData(HospitalMedVia.Tube)]
		[InlineData(HospitalMedVia.Cabinet)]
		public void A_controlled_drug_goes_only_by_porter(string via) =>
			Assert.NotNull(HospitalMedRoutingRules.CreateRefusal(HospitalMedPriority.Routine, via, true, 1, true));

		[Theory]
		[InlineData(HospitalMedPriority.Routine, false, HospitalMedVia.Tube)]
		[InlineData(HospitalMedPriority.Urgent, false, HospitalMedVia.Tube)]
		[InlineData(HospitalMedPriority.Stat, false, HospitalMedVia.Porter)]
		[InlineData(HospitalMedPriority.Routine, true, HospitalMedVia.Porter)]
		public void The_suggested_route_follows_the_rules(string priority, bool controlled, string via) =>
			Assert.Equal(via, HospitalMedRoutingRules.SuggestedVia(priority, controlled));

		// ---------------- raising a delivery ----------------

		[Theory]
		[InlineData(null, HospitalMedVia.Tube, 1)]
		[InlineData("Whenever", HospitalMedVia.Tube, 1)]
		[InlineData(HospitalMedPriority.Routine, null, 1)]
		[InlineData(HospitalMedPriority.Routine, "Drone", 1)]
		[InlineData(HospitalMedPriority.Routine, HospitalMedVia.Tube, -1)]
		[InlineData(HospitalMedPriority.Routine, HospitalMedVia.Tube, 0)]
		[InlineData(HospitalMedPriority.Routine, HospitalMedVia.Tube, 1001)]
		public void A_delivery_with_a_bad_priority_route_or_quantity_is_refused(string? priority, string? via, int qty) =>
			Assert.NotNull(HospitalMedRoutingRules.CreateRefusal(priority, via, false, qty, true));

		[Fact]
		public void A_drug_the_pharmacy_does_not_stock_carries_no_quantity() =>
			Assert.Null(HospitalMedRoutingRules.CreateRefusal(HospitalMedPriority.Routine, HospitalMedVia.Tube, false, 0, false));

		// ---------------- the bedside ----------------

		[Theory]
		[InlineData("MRN-100231", true)]
		[InlineData(" mrn-100231 ", true)]
		[InlineData("MRN-100232", false)]
		[InlineData("", false)]
		[InlineData(null, false)]
		public void The_wristband_must_read_the_patients_own_mrn(string? scanned, bool ok) =>
			Assert.Equal(ok, HospitalMedRoutingRules.WristbandMatches(scanned, "MRN-100231"));

		[Fact]
		public void A_delivery_answers_the_earliest_unrecorded_dose_due_within_the_hour()
		{
			var doses = new[] { T0.AddHours(-2), T0, T0.AddHours(6) };
			Assert.Equal(T0, HospitalMedRoutingRules.DoseToAnswer(doses, new[] { T0.AddHours(-2) }, T0.AddMinutes(10)));
			Assert.Equal(T0.AddHours(-2), HospitalMedRoutingRules.DoseToAnswer(doses, Array.Empty<DateTime>(), T0));
			Assert.Null(HospitalMedRoutingRules.DoseToAnswer(doses, new[] { T0.AddHours(-2), T0 }, T0.AddMinutes(10)));   // next is 6 h away
		}

		// ---------------- lateness and turnaround ----------------

		[Theory]
		[InlineData(HospitalMedPriority.Stat, 16, true)]
		[InlineData(HospitalMedPriority.Stat, 15, false)]
		[InlineData(HospitalMedPriority.Urgent, 31, true)]
		[InlineData(HospitalMedPriority.Routine, 59, false)]
		[InlineData(HospitalMedPriority.Routine, 61, true)]
		public void A_delivery_is_late_past_its_priority_target(string priority, int minutes, bool late) =>
			Assert.Equal(late, HospitalMedRoutingRules.IsLate(priority, T0, null, null, T0.AddMinutes(minutes)));

		[Fact]
		public void A_given_or_cancelled_delivery_is_never_late()
		{
			Assert.False(HospitalMedRoutingRules.IsLate(HospitalMedPriority.Stat, T0, T0.AddMinutes(40), null, T0.AddHours(2)));
			Assert.False(HospitalMedRoutingRules.IsLate(HospitalMedPriority.Stat, T0, null, T0.AddMinutes(5), T0.AddHours(2)));
		}

		[Fact]
		public void Turnaround_is_the_average_minutes_to_the_bedside()
		{
			Assert.Equal(13, HospitalMedRoutingRules.Turnaround(new[] { (T0, T0.AddMinutes(12)), (T0, T0.AddMinutes(14)) }));
			Assert.Null(HospitalMedRoutingRules.Turnaround(Array.Empty<(DateTime, DateTime)>()));
		}

		// ---------------- the model against its slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h21_med_routing.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalMedRoutingModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates() => HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalMedDelivery), "HospitalMedDeliveries");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates() => HospitalSliceParity.ColumnsMatch(Model(), Slice() + Environment.NewLine + HospitalSliceParity.Slice("hospital_h131_med_returns.sql"), typeof(HospitalMedDelivery), "HospitalMedDeliveries");   // H131: ReturnedAt

		[Fact]
		public void Every_table_the_slice_references_but_does_not_create_is_a_checked_prerequisite() =>
			Assert.Equal(5, HospitalSliceParity.CheckedPrerequisites(Slice()).Count);   // orders, MAR, admissions, patients, bill lines

		[Fact]
		public void The_slice_alters_no_existing_table() => Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);

		[Fact]
		public void The_stage_column_is_a_byte_read_as_tinyint() =>
			Assert.Equal(typeof(byte), Model().FindEntityType(typeof(HospitalMedDelivery))!.FindProperty(nameof(HospitalMedDelivery.Stage))!.ClrType);
		/// H131: a dose not yet given can be cancelled; if the store issued it, the cancel returns it; a given or cancelled one cannot.
		[Theory]
		[InlineData(HospitalMedStage.Verified, false, null)]
		[InlineData(HospitalMedStage.Received, false, null)]
		[InlineData(HospitalMedStage.Administered, false, "The dose was given: it cannot be cancelled")]
		[InlineData(HospitalMedStage.Prepared, true, "This delivery is already cancelled")]
		public void A_dose_not_given_can_be_cancelled(byte stage, bool cancelled, string? why)
			=> Assert.Equal(why, HospitalMedRoutingRules.CancelRefusal(stage, cancelled));

		[Fact]
		public void Cancelling_returns_the_dose_only_when_the_store_issued_it()
		{
			Assert.True(HospitalMedRoutingRules.Returns(3017));
			Assert.False(HospitalMedRoutingRules.Returns(null));
		}

		[Fact]
		public void The_returns_slice_checks_its_own_prerequisite()
		{
			Assert.Empty(HospitalSliceParity.CheckedPrerequisites(HospitalSliceParity.Slice("hospital_h131_med_returns.sql")));
		}
	}
}
