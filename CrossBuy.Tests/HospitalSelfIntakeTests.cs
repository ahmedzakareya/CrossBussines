using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H91 — the self-intake, with no database: Arabic name parts folded and order-free, the deterministic and
	// the weighted match, what a submission needs (and that danger signs skip the rest), what the desk's decisions
	// need, and the table against its slice.
	// ==========================================================================================
	public class HospitalSelfIntakeTests
	{
		private static readonly DateTime Today = new(2026, 11, 10);
		private static HospitalSelfIntake I(string name = "أحمد محمد علي حسن", string? id = null, string? phone = "01001234567", DateTime? dob = null, string sex = "M") =>
			new() { Name = name, IdNumber = id, Phone = phone, BirthDate = dob ?? new DateTime(1985, 4, 2), Sex = sex };
		private static HospitalPatient P(string name = "احمد علي محمد حسن", string? nid = null, string? phone = null, DateTime? dob = null, string sex = "M") =>
			new() { Name = name, NameEn = "", NationalId = nid, Phone = phone, BirthDate = dob ?? new DateTime(1985, 4, 2), Sex = sex };

		[Fact]
		public void Name_parts_fold_and_ignore_order_and_particles()
		{
			Assert.Equal(new[] { "احمد", "محمد", "علي" }.ToHashSet(), HospitalIntakeRules.Tokens("أحمد بن محمد عليّ").ToHashSet());
			Assert.Equal(new[] { "فاطمه", "زهراء" }.ToHashSet(), HospitalIntakeRules.Tokens("فاطمة الزهراء").ToHashSet());
			Assert.Contains("ali", HospitalIntakeRules.Tokens("Ali Al-Hassan"));
		}

		[Fact]
		public void Identity_number_with_birth_date_is_deterministic()
		{
			var (s, m, f) = HospitalIntakeRules.Score(I(id: "2850402-0112233"), P(nid: "28504020112233", name: "شخص اخر"));
			Assert.Equal(100m, s); Assert.Equal(HospitalIntakeMethod.Deterministic, m); Assert.Contains("IdNumber", f);
		}

		[Fact]
		public void Weighted_score_reaches_strong_on_name_birth_and_phone()
		{
			var (s, m, f) = HospitalIntakeRules.Score(I(), P(phone: "+20 100 123 4567"));
			Assert.True(s >= HospitalIntakeRules.Strong, $"score {s}");                // reordered name + DOB + phone
			Assert.Equal(HospitalIntakeMethod.Probabilistic, m);
			Assert.Equal(new[] { "BirthDate", "Phone", "Name" }, f);
			var (s2, _, _) = HospitalIntakeRules.Score(I(), P(dob: new DateTime(1990, 1, 1)));   // the name alone
			Assert.True(s2 < HospitalIntakeRules.Review, $"name only {s2}");
			var (s3, _, _) = HospitalIntakeRules.Score(I(), P(sex: "F", phone: "01001234567"));
			Assert.True(s3 < HospitalIntakeRules.Strong, $"sex mismatch {s3}");
		}

		[Fact]
		public void A_file_with_both_names_still_matches_an_intake_with_one()
		{
			// the live case: the file "منى إبراهيم" + "Mona Ibrahim", the tablet "منى إبراهيم محمد" with no English name
			var intake = new HospitalSelfIntake { Name = "منى إبراهيم محمد", BirthDate = new DateTime(2001, 12, 1), Sex = "F", Phone = "34445555" };
			var file = new HospitalPatient { Name = "منى إبراهيم", NameEn = "Mona Ibrahim", BirthDate = new DateTime(2001, 12, 1), Sex = "F", Phone = "+965 34445555" };
			var (s, _, f) = HospitalIntakeRules.Score(intake, file);
			Assert.Equal(80m, s);   // 25 birth + 20 mobile + 35 name (both Arabic parts of the shorter name)
			Assert.True(s >= HospitalIntakeRules.Strong);
			Assert.Contains("Name", f);
		}

		[Fact]
		public void Danger_signs_skip_every_other_question()
		{
			Assert.Null(HospitalIntakeRules.SubmitRefusal(new() { RedFlags = new() { "ChestPain" } }, Today));
			Assert.Equal("Choose from the list", HospitalIntakeRules.SubmitRefusal(new() { RedFlags = new() { "Hiccups" } }, Today));
		}

		[Fact]
		public void A_submission_needs_its_identity_and_consent()
		{
			HospitalIntakeSubmitInput Ok() => new() { VisitType = "WalkIn", Name = "أحمد محمد علي", BirthDate = new DateTime(1985, 4, 2), Sex = "M", Phone = "01001234567", Consent = true };
			Assert.Null(HospitalIntakeRules.SubmitRefusal(Ok(), Today));
			var i = Ok(); i.Name = "أحمد"; Assert.Equal("Write your full name", HospitalIntakeRules.SubmitRefusal(i, Today));
			i = Ok(); i.BirthDate = Today.AddDays(1); Assert.Equal("Write your date of birth", HospitalIntakeRules.SubmitRefusal(i, Today));
			i = Ok(); i.Phone = "123"; Assert.Equal("Write your mobile number", HospitalIntakeRules.SubmitRefusal(i, Today));
			i = Ok(); i.IdNumber = "12"; Assert.Equal("The identity number is 5 to 20 letters or digits", HospitalIntakeRules.SubmitRefusal(i, Today));
			i = Ok(); i.Consent = false; Assert.Equal("Agree to the use of your data to continue", HospitalIntakeRules.SubmitRefusal(i, Today));
			i = Ok(); i.VisitType = "Shopping"; Assert.Equal("Choose why you came", HospitalIntakeRules.SubmitRefusal(i, Today));
		}

		[Fact]
		public void The_desk_verifies_acknowledges_and_does_not_duplicate()
		{
			var s = new HospitalSelfIntake { Status = HospitalIntakeStatus.Submitted };
			Assert.Equal("Check two identifiers against the identity document first", HospitalIntakeRules.DecisionRefusal(s, new() { PatientId = 1 }, true, 0));
			Assert.Null(HospitalIntakeRules.DecisionRefusal(s, new() { PatientId = 1, Verified = true }, true, 0));
			Assert.Equal("Choose the file to link", HospitalIntakeRules.DecisionRefusal(s, new() { Verified = true }, true, 0));
			Assert.Equal("A file very likely already exists: link it, or write why a new file is right", HospitalIntakeRules.DecisionRefusal(s, new() { Verified = true }, false, 85));
			Assert.Null(HospitalIntakeRules.DecisionRefusal(s, new() { Verified = true, Reason = "Twin brother" }, false, 85));
			var urgent = new HospitalSelfIntake { Status = HospitalIntakeStatus.Submitted, Urgent = true };
			Assert.Equal("Acknowledge the danger signs first", HospitalIntakeRules.DecisionRefusal(urgent, new() { Verified = true, PatientId = 1 }, true, 0));
			Assert.Equal("This intake is already settled", HospitalIntakeRules.DecisionRefusal(new HospitalSelfIntake { Status = HospitalIntakeStatus.Linked }, new() { Verified = true, PatientId = 1 }, true, 0));
		}

		[Fact]
		public void The_table_matches_its_slice_and_moves_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h91_self_intake.sql");
			var m = HospitalSliceParity.Model(HospitalSelfIntakeModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalSelfIntake), "HospitalSelfIntakes");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalSelfIntake), "HospitalSelfIntakes");
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalSelfIntake));
			foreach (var x in HospitalIntakeStatus.All.Concat(HospitalIntakeVisit.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
		}
	}
}
