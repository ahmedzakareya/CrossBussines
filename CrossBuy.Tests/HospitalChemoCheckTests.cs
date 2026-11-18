using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H181 — the independent double-check of chemotherapy. What must never happen: a cycle given on one
	// clinician's word — the one who prepared it never checks it — or a given or held cycle "checked" again.
	// ==========================================================================================
	public class HospitalChemoCheckTests
	{
		[Fact]
		public void Only_a_second_clinician_checks_a_prepared_cycle()
		{
			var prepared = new HospitalChemoCycle { Status = HospitalChemoCycleStatus.Prepared, CreatedByEmployeeId = 7 };
			Assert.Equal("The independent check is by a second clinician, not the one who prepared it", HospitalChemoCheckRules.VerifyRefusal(prepared, 7));
			Assert.Null(HospitalChemoCheckRules.VerifyRefusal(prepared, 9));
			Assert.Equal("Only a prepared cycle is checked", HospitalChemoCheckRules.VerifyRefusal(new HospitalChemoCycle { Status = HospitalChemoCycleStatus.Given, CreatedByEmployeeId = 7 }, 9));
			Assert.Equal("Only a prepared cycle is checked", HospitalChemoCheckRules.VerifyRefusal(new HospitalChemoCycle { Status = HospitalChemoCycleStatus.Held, CreatedByEmployeeId = 7 }, 9));
		}

		[Fact]
		public void The_slice_allows_the_prepared_status_and_adds_the_checker()
		{
			var s = HospitalSliceParity.Slice("hospital_h181_chemo_double_check.sql");
			Assert.Contains("'Prepared'", s);
			Assert.Contains("CheckedByEmployeeId int NULL", s);
		}
	}
}
