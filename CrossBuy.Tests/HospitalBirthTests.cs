using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H58 — births, with no database: the mother is a woman, the time is real, the measures are a baby's, the
	// newborn's file is named after the mother (twins told apart), the table against its slice (a live birth has the
	// newborn's file, a stillbirth none — the database keeps that), and the Report Studio notification.
	// ==========================================================================================
	public class HospitalBirthTests
	{
		private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
		private static HospitalBirthInput Ok() => new() { MotherPatientId = 6, BornAt = Now.AddHours(-2), Sex = "F", Outcome = HospitalBirthOutcome.Live, DeliveryMode = HospitalDeliveryMode.Normal, WeightGrams = 3200, Apgar1 = 8, Apgar5 = 9 };

		[Fact]
		public void A_well_formed_birth_of_a_womans_file_is_accepted()
		{
			Assert.Null(HospitalBirthRules.RegisterRefusal(Ok(), "F", Now));
			Assert.Equal("The mother's file must be a woman's", HospitalBirthRules.RegisterRefusal(Ok(), "M", Now));
		}

		[Fact]
		public void The_time_is_given_not_in_the_future_and_not_older_than_the_civil_registry_takes()
		{
			var i = Ok(); i.BornAt = null; Assert.Equal("Give the time of birth", HospitalBirthRules.RegisterRefusal(i, "F", Now));
			i = Ok(); i.BornAt = Now.AddMinutes(10); Assert.Equal("The time of birth is in the future", HospitalBirthRules.RegisterRefusal(i, "F", Now));
			i = Ok(); i.BornAt = Now.AddDays(-HospitalBirthRules.MaxDaysLate - 1); Assert.Equal("A birth older than {0} days is registered with the civil registry", HospitalBirthRules.RegisterRefusal(i, "F", Now));
		}

		[Theory]
		[InlineData(150, "The weight is 200 to 7000 grams")]
		[InlineData(7500, "The weight is 200 to 7000 grams")]
		[InlineData(3200, null)]
		public void The_weight_is_a_babys(int grams, string? why) { var i = Ok(); i.WeightGrams = grams; Assert.Equal(why, HospitalBirthRules.RegisterRefusal(i, "F", Now)); }

		[Fact]
		public void Apgar_gestation_order_and_codes_are_checked()
		{
			var i = Ok(); i.Apgar5 = 11; Assert.Equal("An Apgar score is 0 to 10", HospitalBirthRules.RegisterRefusal(i, "F", Now));
			i = Ok(); i.GestationWeeks = 50; Assert.Equal("The gestation is 20 to 45 weeks", HospitalBirthRules.RegisterRefusal(i, "F", Now));
			i = Ok(); i.BirthOrder = 0; Assert.Equal("The birth order is 1 to 6", HospitalBirthRules.RegisterRefusal(i, "F", Now));
			i = Ok(); i.Outcome = "Alive"; Assert.Equal("Choose live birth or stillbirth", HospitalBirthRules.RegisterRefusal(i, "F", Now));
			i = Ok(); i.DeliveryMode = "Water"; Assert.Equal("Choose how the baby was delivered", HospitalBirthRules.RegisterRefusal(i, "F", Now));
		}

		[Fact]
		public void The_newborn_is_named_after_the_mother_and_twins_are_told_apart()
		{
			Assert.Equal(("مولود فاطمة محمد", "Baby of Fatma Mohamed"), HospitalBirthRules.NewbornName("فاطمة محمد", "Fatma Mohamed", 1));
			Assert.Equal(("مولود فاطمة محمد (2)", "Baby of Fatma Mohamed (2)"), HospitalBirthRules.NewbornName("فاطمة محمد", "Fatma Mohamed", 2));
			Assert.Equal("Baby of فاطمة", HospitalBirthRules.NewbornName("فاطمة", null, 1).En);
		}

		[Fact]
		public void The_table_matches_its_slice_and_keeps_live_with_a_file()
		{
			var s = HospitalSliceParity.Slice("hospital_h58_births.sql");
			var m = HospitalSliceParity.Model(HospitalBirthModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalBirth), "HospitalBirths");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalBirth), "HospitalBirths");
			Assert.Contains("CK_HospitalBirths_Newborn", s);
			foreach (var x in HospitalBirthOutcome.All.Concat(HospitalDeliveryMode.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
		}

		[Fact]
		public void The_notification_prints_only_through_report_studio()
		{
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.BirthNotice);
			Assert.Contains(d.Parameters, p => p.Key == "BirthId" && p.Required);
			foreach (var k in new[] { "BornAt", "BabySex", "WeightGrams", "Apgar", "BabyMrn", "PatientName", "DocumentNo" }) Assert.Contains(d.Fields, f => f.Key == k);
			Assert.Contains(ReportScreenBindings.For(ReportScreenKeys.HospitalBirth), b => b.ReportCode == HospitalDocumentDatasetCodes.BirthNotice && b.IdParameter == "BirthId");
		}
	}
}
