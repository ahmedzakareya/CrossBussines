using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H184 — dental work marked done by mistake, taken back. What must never happen: work taken back without a
	// reason, planned work "taken back", the signed session left saying the work was done, or the patient still charged.
	// ==========================================================================================
	public class HospitalDentalCorrectionTests
	{
		[Fact]
		public void Only_done_work_is_taken_back_and_always_with_a_reason()
		{
			Assert.Null(HospitalDentalCorrectionRules.Refusal(HospitalDentalLineStatus.Completed, "wrong tooth recorded"));
			Assert.Equal(HospitalDentalCorrectionRules.NotDone, HospitalDentalCorrectionRules.Refusal(HospitalDentalLineStatus.Planned, "wrong tooth recorded"));
			Assert.Equal(HospitalDentalCorrectionRules.NotDone, HospitalDentalCorrectionRules.Refusal(HospitalDentalLineStatus.Cancelled, "wrong tooth recorded"));
			Assert.Equal("Write why the work is taken back", HospitalDentalCorrectionRules.Refusal(HospitalDentalLineStatus.Completed, "  "));
			Assert.Equal("Write why the work is taken back", HospitalDentalCorrectionRules.Refusal(HospitalDentalLineStatus.Completed, "oops"));
		}

		[Fact]
		public void The_session_addendum_names_the_work_and_its_tooth()
		{
			Assert.Equal("Not done — recorded by mistake: Composite filling (tooth 36)", HospitalDentalCorrectionRules.AddendumText("Composite filling", 36));
			Assert.Equal("Not done — recorded by mistake: Scaling", HospitalDentalCorrectionRules.AddendumText("Scaling", null));
			Assert.Equal("لم يُنجز — سُجّل خطأً: حشو تجميلي (السن 36)", HospitalDentalCorrectionRules.AddendumText("حشو تجميلي", 36, arabic: true));
		}

		[Fact]
		public void A_closed_bill_is_reversed_by_a_credit_note_of_its_own_kind()
		{
			Assert.Equal("HospitalDentalCorrection", HospitalCreditNoteSource.DentalCorrection);
			Assert.NotEqual(HospitalCreditNoteSource.DispenseReturn, HospitalCreditNoteSource.DentalCorrection);
			// the line's money as the bill priced it (net, then VAT at its rate)
			Assert.Equal((150m, 21m), HospitalReturnRules.Money(1, 150m, true, 14m));
		}

		[Fact]
		public void The_table_matches_its_slice_and_follows_the_patient()
		{
			var model = HospitalSliceParity.Model(HospitalDentalSessionModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h184_dental_corrections.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalDentalCorrection), "HospitalDentalCorrections");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalDentalCorrection), "HospitalDentalCorrections");
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalDentalCorrection));
			Assert.DoesNotContain("REFERENCES dbo.HospitalBillLines", s);   // an open bill's line is removed by the correction itself
		}
	}
}
