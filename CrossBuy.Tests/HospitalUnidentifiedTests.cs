using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H92 — the unidentified patient, with no database: the temporary name, the estimated birth, what a
	// registration and an identification need, and the table against its slice.
	// ==========================================================================================
	public class HospitalUnidentifiedTests
	{
		private static readonly DateTime Today = new(2026, 11, 11);

		[Fact]
		public void The_temporary_name_cannot_be_mistaken_for_a_person()
		{
			var (ar, en) = HospitalUnidentifiedRules.TempName("M", Today, 3);
			Assert.Equal("مجهول ذكر 2026-11-11 رقم 03", ar);
			Assert.Equal("Unknown male 2026-11-11 #03", en);
			Assert.StartsWith("Unknown female", HospitalUnidentifiedRules.TempName("F", Today, 1).En);
			Assert.Equal(new DateTime(1986, 1, 1), HospitalUnidentifiedRules.EstimatedBirth(40, Today));
		}

		[Fact]
		public void A_registration_needs_a_description_and_the_emergency_details()
		{
			HospitalUnidentifiedInput Ok() => new() { Sex = "M", EstimatedAge = 40, Description = "Tall, scar on the left cheek, blue jacket", ArrivalMode = "Ambulance", Complaint = "Unconscious", Esi = 1 };
			Assert.Null(HospitalUnidentifiedRules.RegisterRefusal(Ok()));
			var i = Ok(); i.Description = "man"; Assert.Equal("Describe the patient: build, marks, clothing, where found", HospitalUnidentifiedRules.RegisterRefusal(i));
			i = Ok(); i.EstimatedAge = 130; Assert.Equal("The estimated age is between 0 and 120", HospitalUnidentifiedRules.RegisterRefusal(i));
			i = Ok(); i.Complaint = ""; Assert.Equal("Write why the patient is here", HospitalUnidentifiedRules.RegisterRefusal(i));
			i = Ok(); i.OpenEmergency = false; i.Complaint = ""; i.ArrivalMode = null; Assert.Null(HospitalUnidentifiedRules.RegisterRefusal(i));
			i = Ok(); i.Sex = "X"; Assert.Equal("Choose the sex", HospitalUnidentifiedRules.RegisterRefusal(i));
		}

		[Fact]
		public void An_identification_needs_how_and_a_real_name_or_the_person_file()
		{
			var u = new HospitalUnidentified { PatientId = 50 };
			Assert.Equal("Write who identified the patient, and how", HospitalUnidentifiedRules.IdentifyRefusal(u, new() { ExistingPatientId = 4 }, Today));
			Assert.Null(HospitalUnidentifiedRules.IdentifyRefusal(u, new() { ExistingPatientId = 4, How = "His brother, with his ID card" }, Today));
			Assert.Equal("Choose the person's own file, not the temporary one", HospitalUnidentifiedRules.IdentifyRefusal(u, new() { ExistingPatientId = 50, How = "x" }, Today));
			Assert.Null(HospitalUnidentifiedRules.IdentifyRefusal(u, new() { Name = "سامي عادل", NameEn = "Sami Adel", Sex = "M", How = "Police" }, Today));
			Assert.Equal("Write the real name, in Arabic and in English", HospitalUnidentifiedRules.IdentifyRefusal(u, new() { Name = "مجهول ذكر", NameEn = "Unknown", Sex = "M", How = "x" }, Today));
			Assert.Equal("This patient is already identified", HospitalUnidentifiedRules.IdentifyRefusal(new HospitalUnidentified { IdentifiedAt = Today }, new() { How = "x" }, Today));
		}

		[Fact]
		public void The_table_matches_its_slice_and_moves_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h92_unidentified.sql");
			var m = HospitalSliceParity.Model(HospitalUnidentifiedModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalUnidentified), "HospitalUnidentifiedPatients");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalUnidentified), "HospitalUnidentifiedPatients");
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalUnidentified));
			foreach (var x in HospitalUnidentifiedResolution.All) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
		}
	}
}
