using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H183 — independent safety checks on the therapeutic units. What must never happen: a dialysis session run on a
	// prescription nobody but its writer has read, or on an unchecked machine; a vaccine given with no lot, without the
	// screening, against a contraindication, or too soon after the previous dose.
	// ==========================================================================================
	public class HospitalIndependentCheckTests
	{
		private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);

		private static HospitalDialysisPlan Plan(int writer, int? updater = null) => new()
		{ Status = HospitalDialysisPlanStatus.Active, CreatedByEmployeeId = writer, UpdatedByEmployeeId = updater };

		[Fact]
		public void A_dialysis_prescription_is_checked_by_someone_other_than_its_last_writer()
		{
			Assert.Equal("The independent check is by a second clinician, not the one who wrote the prescription", HospitalDialysisCheckRules.VerifyRefusal(Plan(9), 9));
			Assert.Null(HospitalDialysisCheckRules.VerifyRefusal(Plan(9), 5));
			// a change by another clinician makes that clinician the writer: the original author may now check it
			Assert.Equal("The independent check is by a second clinician, not the one who wrote the prescription", HospitalDialysisCheckRules.VerifyRefusal(Plan(9, 5), 5));
			Assert.Null(HospitalDialysisCheckRules.VerifyRefusal(Plan(9, 5), 9));
			var done = Plan(9); done.VerifiedAt = Now;
			Assert.Equal("This prescription is already checked", HospitalDialysisCheckRules.VerifyRefusal(done, 5));
			var stopped = Plan(9); stopped.Status = HospitalDialysisPlanStatus.Stopped;
			Assert.Equal("This dialysis plan is stopped", HospitalDialysisCheckRules.VerifyRefusal(stopped, 5));
		}

		private static string? Safety(string code = "PENTA", string? lot = "L1", DateTime? expiry = null, string? screening = HospitalVaccineScreening.None, string? why = null, DateTime? prev = null, DateTime? given = null)
			=> HospitalImmunizationRules.SafetyRefusal(code, given ?? Now, Now, lot, expiry ?? Now.AddMonths(6), screening, why, prev);

		[Fact]
		public void A_dose_given_now_carries_its_lot_and_screening()
		{
			Assert.Null(Safety());
			Assert.Equal("Write the lot and its expiry", Safety(lot: " "));
			Assert.Equal("Answer the screening questions", Safety(screening: null));
			Assert.Equal("Answer the screening questions", Safety(screening: "Maybe"));
			// a dose copied from another clinic's card a year ago: no lot or screening asked
			Assert.Null(HospitalImmunizationRules.SafetyRefusal("PENTA", Now.AddYears(-1), Now, null, null, null, null, null));
			Assert.True(HospitalImmunizationRules.IsCurrent(Now.AddHours(-23), Now));
			Assert.False(HospitalImmunizationRules.IsCurrent(Now.AddHours(-25), Now));
		}

		[Fact]
		public void The_screening_stops_a_contraindicated_dose_and_a_precaution_needs_a_reason()
		{
			Assert.Equal("A severe reaction to a previous dose or a component: this vaccine is not given", Safety(screening: HospitalVaccineScreening.PriorSevere, why: "parents insist"));
			Assert.Equal("A live vaccine is not given in immunosuppression or pregnancy", Safety("MMR", screening: HospitalVaccineScreening.Pregnant));
			Assert.Equal("A live vaccine is not given in immunosuppression or pregnancy", Safety("bcg", screening: HospitalVaccineScreening.Immunosuppressed));
			Assert.Null(Safety("PENTA", screening: HospitalVaccineScreening.Immunosuppressed));   // an inactivated vaccine is given
			Assert.Equal("Moderate or severe illness: defer the dose, or write why it is given now", Safety(screening: HospitalVaccineScreening.Fever));
			Assert.Null(Safety(screening: HospitalVaccineScreening.Fever, why: "travelling tomorrow; mild cold only"));
		}

		[Fact]
		public void Two_doses_of_one_vaccine_keep_the_minimum_interval()
		{
			Assert.Equal("Two doses of the same vaccine are at least 28 days apart", Safety(prev: Now.AddDays(-20)));
			Assert.Null(Safety(prev: Now.AddDays(-28)));
			// a history entry inside the interval is refused too: the record would not count
			Assert.Equal("Two doses of the same vaccine are at least 28 days apart", HospitalImmunizationRules.SafetyRefusal("OPV", Now.AddYears(-1), Now, null, null, null, null, Now.AddYears(-1).AddDays(10)));
		}

		[Fact]
		public void The_screening_column_holds_only_its_codes()
		{
			var s = HospitalSliceParity.Slice("hospital_h183_independent_checks.sql");
			foreach (var c in HospitalVaccineScreening.All) Assert.Contains("'" + c + "'", s);
			Assert.Contains("SET VerifiedAt = CreatedAt", s);   // the plans in use before the check are taken as checked
		}
	}
}
