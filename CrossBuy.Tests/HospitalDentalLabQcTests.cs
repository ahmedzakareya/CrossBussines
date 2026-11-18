using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H187 — the lab case's checks and each lab's record. What must never happen: a conventional impression sent
	// without its disinfection, work received or fitted without its check, or a lab's record blended with another's.
	// ==========================================================================================
	public class HospitalDentalLabQcTests
	{
		private static readonly DateTime Today = new(2026, 11, 18);
		private static readonly string[] All = { "Fit", "Margins", "Contacts", "Occlusion", "Shade" };

		[Fact]
		public void The_impression_is_named_and_a_conventional_one_leaves_disinfected()
		{
			Assert.Null(HospitalDentalLabQcRules.ImpressionRefusal("Digital"));
			Assert.Equal("Choose the impression: digital scan or conventional", HospitalDentalLabQcRules.ImpressionRefusal(null));
			Assert.Equal("Record the disinfectant the impression was treated with before it leaves", HospitalDentalLabQcRules.SendRefusal("Conventional", " "));
			Assert.Null(HospitalDentalLabQcRules.SendRefusal("Conventional", "hypochlorite 1%"));
			Assert.Null(HospitalDentalLabQcRules.SendRefusal("Digital", null));
			Assert.Null(HospitalDentalLabQcRules.SendRefusal(null, null));   // a case prescribed before H187
		}

		[Fact]
		public void Work_is_received_and_fitted_only_after_every_check()
		{
			Assert.Null(HospitalDentalLabQcRules.QcRefusal(HospitalLabCaseStage.Received, null, All));
			Assert.Equal(HospitalDentalLabQcRules.QcFailed, HospitalDentalLabQcRules.QcRefusal(HospitalLabCaseStage.Received, null, All.Take(4).ToList()));
			Assert.Equal(HospitalDentalLabQcRules.QcFailed, HospitalDentalLabQcRules.QcRefusal(HospitalLabCaseStage.Received, null, null));
			Assert.Equal(HospitalDentalLabQcRules.QcFailed, HospitalDentalLabQcRules.QcRefusal(HospitalLabCaseStage.Fitted, null, null));   // received before H187: checked on its way to the chair
			Assert.Null(HospitalDentalLabQcRules.QcRefusal(HospitalLabCaseStage.Fitted, Today, null));   // checked on receipt
			Assert.Null(HospitalDentalLabQcRules.QcRefusal(HospitalLabCaseStage.Shipped, null, null));
		}

		[Fact]
		public void Each_lab_keeps_its_own_record()
		{
			var labs = new[] { new HospitalDentalLab { ID = 1, Name = "أ", NameEn = "A" }, new HospitalDentalLab { ID = 2, Name = "ب", NameEn = "B" }, new HospitalDentalLab { ID = 3, Name = "ج", NameEn = "C" } };
			var cases = new List<(int, DateTime, DateTime?, DateTime?, int, string?)>
			{
				(1, Today.AddDays(-10), Today.AddDays(-20), Today.AddDays(-12), 0, null),   // on time, 8 days
				(1, Today.AddDays(-10), Today.AddDays(-20), Today.AddDays(-8), 1, "Lab"),   // late, 12 days, a lab-fault remake
				(2, Today.AddDays(-5), Today.AddDays(-9), Today.AddDays(-6), 1, "Clinic"),
			};
			var p = HospitalDentalLabQcRules.Performance(labs, cases, Today);
			Assert.Equal(2, p.Count);   // a lab with no case is not listed
			var a = p.First(x => x.LabId == 1);
			Assert.Equal((2, 50m, 50m, 1, 10m), (a.Cases, a.OnTimeRate, a.RemakeRate, a.LabFaultRemakes, a.AvgTurnaroundDays));
			var b = p.First(x => x.LabId == 2);
			Assert.Equal((1, 100m, 0), (b.Cases, b.OnTimeRate, b.LabFaultRemakes));
		}

		[Fact]
		public void The_slice_holds_the_impression_codes()
		{
			var s = HospitalSliceParity.Slice("hospital_h187_lab_checks.sql");
			foreach (var k in HospitalLabImpression.All) Assert.Contains("'" + k + "'", s);
		}
	}
}
