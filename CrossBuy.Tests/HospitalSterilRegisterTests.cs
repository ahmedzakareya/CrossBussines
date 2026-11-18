using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H193 — one register of sterilisation cycles. What must never happen: a dental cycle kept in a second
	// register the central unit cannot see, a kit or a recall left pointing at a cycle that moved, or a cycle moved twice.
	// ==========================================================================================
	public class HospitalSterilRegisterTests
	{
		private static string Slice() => HospitalSliceParity.Slice("hospital_h193_one_steril_register.sql");

		[Fact]
		public void The_dental_rules_read_the_one_registers_cycle()
		{
			var c = new HospitalSterilisationCycle { PhysicalOk = true, Chemical = HospitalIndicator.Pass, Biological = HospitalIndicator.Pending, Load = "Exam kits", Autoclave = "Autoclave 1" };
			Assert.Null(HospitalDentalPracticeRules.ReleaseRefusal(c));
			c.ImplantLoad = true;
			Assert.Equal("A load with implantable items waits for its biological indicator", HospitalDentalPracticeRules.ReleaseRefusal(c));
			Assert.True(HospitalDentalPracticeRules.Held(new HospitalSterilisationCycle { PhysicalOk = false }));
		}

		[Fact]
		public void Every_dental_cycle_moves_once_with_its_kits_and_recalls()
		{
			var s = Slice();
			Assert.Contains("WHERE NOT EXISTS (SELECT 1 FROM dbo.HospitalSterilisationCycles s WHERE s.CompanyId = d.CompanyId AND s.LegacyDentalCycleId = d.ID)", s);
			// the kits and the recalls are repointed only while their keys still reference the old table, then the keys move
			Assert.Contains("referenced_object_id = OBJECT_ID('dbo.HospitalSterilCycles')", s);
			Assert.Contains("FK_HospitalSterilUses_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalSterilisationCycles (ID)", s);
			Assert.Contains("FK_HospitalSterilRecalls_Cycle FOREIGN KEY (CycleId) REFERENCES dbo.HospitalSterilisationCycles (ID)", s);
			Assert.DoesNotContain("DROP TABLE", s, StringComparison.OrdinalIgnoreCase);   // the old register stays, as its archive
			Assert.DoesNotContain("DELETE", s, StringComparison.OrdinalIgnoreCase);
		}

		[Fact]
		public void The_register_keeps_what_the_dental_cycles_held()
		{
			var model = HospitalSliceParity.Model(HospitalCssdModel.Configure);
			HospitalSliceParity.ColumnsMatch(model, HospitalSliceParity.Slice("hospital_h42_cssd.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h170_cssd_release.sql") + Environment.NewLine + Slice(),
				typeof(HospitalSterilisationCycle), "HospitalSterilisationCycles");
		}
	}
}
