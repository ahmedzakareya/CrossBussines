using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H132 — the pharmacy's stock as the pharmacist reads it: a batch is expired, near its expiry (within the
	// pharmacy's near-expiry days) or fine; a medicine shows its most urgent state — nothing on hand before an expired batch on
	// the shelf, before a batch near expiry, before being under its reorder point.
	// ==========================================================================================
	public class HospitalPharmacyStockTests
	{
		private static readonly DateTime Today = new(2026, 11, 15);

		[Theory]
		[InlineData("2026-11-14", HospitalPharmacyStockState.Expired)]   // yesterday
		[InlineData("2026-11-15", HospitalPharmacyStockState.Near)]      // today is the last day it may be used
		[InlineData("2027-01-14", HospitalPharmacyStockState.Near)]      // the 60th day
		[InlineData("2027-01-15", HospitalPharmacyStockState.Ok)]        // the 61st
		[InlineData(null, HospitalPharmacyStockState.Ok)]                // no expiry recorded: nothing to warn about
		public void A_batch_is_expired_near_or_fine(string? expiry, string state)
			=> Assert.Equal(state, HospitalPharmacyStockRules.BatchState(expiry == null ? null : DateTime.Parse(expiry), Today, 60));

		[Fact]
		public void A_medicine_shows_its_most_urgent_state()
		{
			string[] ok = { HospitalPharmacyStockState.Ok }, near = { HospitalPharmacyStockState.Ok, HospitalPharmacyStockState.Near },
				gone = { HospitalPharmacyStockState.Near, HospitalPharmacyStockState.Expired };
			Assert.Equal(HospitalPharmacyStockState.Out, HospitalPharmacyStockRules.MedicineState(0, 10, gone));          // nothing on hand beats everything
			Assert.Equal(HospitalPharmacyStockState.Expired, HospitalPharmacyStockRules.MedicineState(5, 10, gone));      // an expired batch on the shelf
			Assert.Equal(HospitalPharmacyStockState.Near, HospitalPharmacyStockRules.MedicineState(5, 10, near));         // before being low
			Assert.Equal(HospitalPharmacyStockState.Low, HospitalPharmacyStockRules.MedicineState(10, 10, ok));           // at the reorder point is low
			Assert.Equal(HospitalPharmacyStockState.Ok, HospitalPharmacyStockRules.MedicineState(11, 10, ok));
			Assert.Equal(HospitalPharmacyStockState.Ok, HospitalPharmacyStockRules.MedicineState(1, null, ok));           // no reorder point set
		}

		[Fact]
		public void Days_left_count_whole_days_and_go_negative_once_past()
		{
			Assert.Equal(0, HospitalPharmacyStockRules.DaysLeft(Today.AddHours(15), Today));
			Assert.Equal(30, HospitalPharmacyStockRules.DaysLeft(Today.AddDays(30), Today));
			Assert.Equal(-3, HospitalPharmacyStockRules.DaysLeft(Today.AddDays(-3), Today));
			Assert.Null(HospitalPharmacyStockRules.DaysLeft(null, Today));
		}
	}
}
