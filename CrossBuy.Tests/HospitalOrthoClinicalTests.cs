using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H185 — the orthodontic record. What must never happen: a case opened with no diagnosis, an appliance taken
	// off with no retainer, a retention case left without its reviews, or poor hygiene and repeated breakages unseen.
	// ==========================================================================================
	public class HospitalOrthoClinicalTests
	{
		private static readonly DateTime Today = new(2026, 11, 18);
		private static readonly (DateTime, string?, int?)[] None = Array.Empty<(DateTime, string?, int?)>();

		[Fact]
		public void A_case_opens_on_a_diagnosis_within_its_bounds()
		{
			Assert.Null(HospitalOrthoClinicalRules.DiagnosisRefusal("II1", 7, 4, 3, -2, 4));
			Assert.Equal("Choose the Angle class", HospitalOrthoClinicalRules.DiagnosisRefusal(null, 7, 4, 3, 2, 4));
			Assert.Equal("Choose the Angle class", HospitalOrthoClinicalRules.DiagnosisRefusal("IV", 7, 4, 3, 2, 4));
			Assert.Equal("Choose the IOTN grade (1 to 5)", HospitalOrthoClinicalRules.DiagnosisRefusal("I", 2, 2, 0, 0, null));
			Assert.Equal("Choose the IOTN grade (1 to 5)", HospitalOrthoClinicalRules.DiagnosisRefusal("I", 2, 2, 0, 0, 6));
			Assert.Equal("The overjet is −10 to 20 mm", HospitalOrthoClinicalRules.DiagnosisRefusal("II1", 21, 4, 0, 0, 5));
			Assert.Equal("The overbite is −10 to 15 mm", HospitalOrthoClinicalRules.DiagnosisRefusal("II2", 3, 16, 0, 0, 4));
			Assert.Equal("Crowding is −15 (spacing) to 20 mm", HospitalOrthoClinicalRules.DiagnosisRefusal("I", 3, 3, 0, -16, 3));
			Assert.Equal(("Little", "Borderline", "Great"), (HospitalOrthoClinicalRules.Need(2), HospitalOrthoClinicalRules.Need(3), HospitalOrthoClinicalRules.Need(5)));
		}

		[Fact]
		public void Taking_the_appliance_off_needs_a_retainer()
		{
			Assert.Null(HospitalOrthoClinicalRules.RetentionRefusal("Essix", "Bonded"));
			Assert.Null(HospitalOrthoClinicalRules.RetentionRefusal("Hawley", "None"));
			Assert.Equal("A case in retention wears a retainer on at least one arch", HospitalOrthoClinicalRules.RetentionRefusal("None", "None"));
			Assert.Equal("Choose the retainer of each arch", HospitalOrthoClinicalRules.RetentionRefusal("Essix", null));
			Assert.Null(HospitalOrthoClinicalRules.VisitRefusal("Poor", 2));
			Assert.Equal("Choose the oral hygiene", HospitalOrthoClinicalRules.VisitRefusal("Bad", null));
			Assert.Equal("Breakages are 0 to 20", HospitalOrthoClinicalRules.VisitRefusal(null, 21));
		}

		[Fact]
		public void Retention_reviews_come_at_three_six_and_twelve_months_then_yearly()
		{
			var off = new DateTime(2025, 1, 10);
			Assert.Equal(new DateTime(2025, 4, 10), HospitalOrthoClinicalRules.NextReview(off, off));
			Assert.Equal(new DateTime(2025, 7, 10), HospitalOrthoClinicalRules.NextReview(off, new DateTime(2025, 4, 12)));
			Assert.Equal(new DateTime(2026, 1, 10), HospitalOrthoClinicalRules.NextReview(off, new DateTime(2025, 7, 10)));
			Assert.Equal(new DateTime(2027, 1, 10), HospitalOrthoClinicalRules.NextReview(off, new DateTime(2026, 1, 11)));
			Assert.Equal(new DateTime(2028, 1, 10), HospitalOrthoClinicalRules.NextReview(off, new DateTime(2027, 2, 1)));
		}

		[Fact]
		public void A_case_warns_of_poor_hygiene_breakages_and_an_overdue_review()
		{
			var active = new HospitalOrthoCase { Status = HospitalOrthoStatus.Active };
			Assert.Empty(HospitalOrthoClinicalRules.Warnings(active, None, Today));
			var poor = new (DateTime, string?, int?)[] { (Today, "Poor", null), (Today.AddMonths(-1), "Poor", null) };
			Assert.Equal(new[] { HospitalOrthoClinicalRules.WarnHygiene }, HospitalOrthoClinicalRules.Warnings(active, poor, Today));
			var mixed = new (DateTime, string?, int?)[] { (Today, "Poor", null), (Today.AddMonths(-1), "Fair", null), (Today.AddMonths(-2), "Poor", null) };
			Assert.Empty(HospitalOrthoClinicalRules.Warnings(active, mixed, Today));   // not two running
			var broken = new (DateTime, string?, int?)[] { (Today, "Good", 2), (Today.AddMonths(-1), "Good", 1) };
			Assert.Equal(new[] { HospitalOrthoClinicalRules.WarnBreakages }, HospitalOrthoClinicalRules.Warnings(active, broken, Today));

			var retained = new HospitalOrthoCase { Status = HospitalOrthoStatus.Retention, DebondedOn = Today.AddMonths(-4) };
			Assert.Equal(new[] { HospitalOrthoClinicalRules.WarnReview }, HospitalOrthoClinicalRules.Warnings(retained, None, Today));   // the 3-month review passed unseen
			var seen = new (DateTime, string?, int?)[] { (Today.AddDays(-20), "Good", null) };
			Assert.Empty(HospitalOrthoClinicalRules.Warnings(retained, seen, Today));   // seen: the next is the 6-month review
			Assert.Empty(HospitalOrthoClinicalRules.Warnings(new HospitalOrthoCase { Status = HospitalOrthoStatus.Completed }, poor, Today));
		}

		[Fact]
		public void The_slice_holds_the_codes()
		{
			var s = HospitalSliceParity.Slice("hospital_h185_ortho_record.sql");
			foreach (var a in HospitalOrthoAngle.All) Assert.Contains("'" + a + "'", s);
			foreach (var r in HospitalOrthoRetainer.All) Assert.Contains("'" + r + "'", s);
			foreach (var h in HospitalOrthoHygiene.All) Assert.Contains("'" + h + "'", s);
		}
	}
}
