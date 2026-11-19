using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H56 — sick leave certificates, with no database: the period's rules (order, how far back, how long),
	// the recommendation, the diagnosis printed only when written, the table against its slice (the database itself
	// keeps days = the period, a printed diagnosis present, a void with its reason), and the Report Studio print.
	// ==========================================================================================
	public class HospitalSickLeaveTests
	{
		private static readonly DateTime Today = new(2026, 11, 8);
		private static HospitalSickLeaveInput Ok() => new() { FromDate = Today, ToDate = Today.AddDays(2), Kind = HospitalSickLeaveKind.Rest };

		[Fact]
		public void A_period_inclusive_of_both_days()
		{
			Assert.Equal(3, HospitalSickLeaveRules.Days(Today, Today.AddDays(2)));
			Assert.Equal(1, HospitalSickLeaveRules.Days(Today, Today));
			Assert.Null(HospitalSickLeaveRules.IssueRefusal(Ok(), Today));
		}

		[Fact]
		public void The_period_must_be_given_in_order_not_too_far_back_nor_too_long()
		{
			var i = Ok(); i.ToDate = null; Assert.Equal("Give the first and the last day", HospitalSickLeaveRules.IssueRefusal(i, Today));
			i = Ok(); i.ToDate = Today.AddDays(-1); Assert.Equal("The last day is before the first", HospitalSickLeaveRules.IssueRefusal(i, Today));
			i = Ok(); i.FromDate = Today.AddDays(-HospitalSickLeaveRules.MaxBackDays); Assert.Null(HospitalSickLeaveRules.IssueRefusal(i, Today));
			i = Ok(); i.FromDate = Today.AddDays(-HospitalSickLeaveRules.MaxBackDays - 1); Assert.Equal("A sick leave cannot start more than {0} days ago", HospitalSickLeaveRules.IssueRefusal(i, Today));
			i = Ok(); i.ToDate = Today.AddDays(HospitalSickLeaveRules.MaxDays - 1); Assert.Null(HospitalSickLeaveRules.IssueRefusal(i, Today));
			i = Ok(); i.ToDate = Today.AddDays(HospitalSickLeaveRules.MaxDays); Assert.Equal("One certificate covers at most {0} days", HospitalSickLeaveRules.IssueRefusal(i, Today));
		}

		[Fact]
		public void The_recommendation_is_known_and_a_printed_diagnosis_is_written()
		{
			var i = Ok(); i.Kind = "Holiday"; Assert.Equal("Choose rest, light duty or in hospital", HospitalSickLeaveRules.IssueRefusal(i, Today));
			i = Ok(); i.ShowDiagnosis = true; Assert.Equal("Write the diagnosis to print it", HospitalSickLeaveRules.IssueRefusal(i, Today));
			i.Diagnosis = "Acute gastroenteritis"; Assert.Null(HospitalSickLeaveRules.IssueRefusal(i, Today));
			i = Ok(); i.Diagnosis = "Kept on the file, not printed"; Assert.Null(HospitalSickLeaveRules.IssueRefusal(i, Today));
		}

		[Fact]
		public void The_table_matches_its_slice_and_the_database_keeps_the_rules()
		{
			var s = HospitalSliceParity.Slice("hospital_h56_sick_leave.sql");
			var m = HospitalSliceParity.Model(HospitalSickLeaveModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalSickLeave), "HospitalSickLeaves");
			HospitalSliceParity.ColumnsMatch(m, s + Environment.NewLine + HospitalSliceParity.Slice("hospital_h208_sick_leave_kuwait.sql"), typeof(HospitalSickLeave), "HospitalSickLeaves");   // + H208's code and MOH authentication
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
			foreach (var k in HospitalSickLeaveKind.All) Assert.Contains("'" + k + "'", s);
			Assert.Contains("Days = DATEDIFF(day, FromDate, ToDate) + 1", s);
			Assert.Contains("CK_HospitalSickLeaves_Diagnosis", s);
			Assert.Contains("CK_HospitalSickLeaves_Void", s);
			var unique = m.FindEntityType(typeof(HospitalSickLeave))!.GetIndexes().Where(i => i.IsUnique).Select(i => string.Join(",", i.Properties.Select(x => x.Name))).OrderBy(x => x).ToArray();
			Assert.Equal(new[] { "CompanyId,LeaveNo", "VerifyCode" }, unique);   // H208: the QR's code is unique across companies
		}

		[Fact]
		public void It_prints_only_through_report_studio_by_its_id()
		{
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.SickLeave);
			Assert.Contains(d.Parameters, p => p.Key == "SickLeaveId" && p.Required);
			foreach (var k in new[] { "FromDate", "ToDate", "Days", "Kind", "Diagnosis", "Void", "DocumentNo", "PatientName", "DoctorName" })
				Assert.Contains(d.Fields, f => f.Key == k);
			Assert.Contains(ReportScreenBindings.For(ReportScreenKeys.HospitalSickLeave), b => b.ReportCode == HospitalDocumentDatasetCodes.SickLeave && b.IdParameter == "SickLeaveId");
			foreach (var k in new[] { "VerifyUrl", "VerifyCode", "MohAuth" }) Assert.Contains(d.Fields, f => f.Key == k);   // H208
		}

		// ---- H208: Kuwait — seen in person, never before the visit, a QR anyone can check, the Ministry's authentication ----
		private static readonly DateTime NowUtc = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);

		[Fact]
		public void A_certificate_needs_a_visit_in_the_last_24_hours()
		{
			Assert.Equal(HospitalSickLeaveRules.NotSeen, HospitalSickLeaveRules.SeenRefusal(null, NowUtc.Date, NowUtc));
			Assert.Equal(HospitalSickLeaveRules.NotSeen, HospitalSickLeaveRules.SeenRefusal(NowUtc.AddHours(-25), NowUtc.Date, NowUtc));
			Assert.Null(HospitalSickLeaveRules.SeenRefusal(NowUtc.AddHours(-2), NowUtc.Date, NowUtc));
		}

		[Fact]
		public void A_certificate_never_starts_before_its_visit()
		{
			var visit = NowUtc.AddHours(-1);
			var visitDay = HospitalNursingService.UtcToWard(visit).Date;
			Assert.Equal(HospitalSickLeaveRules.BeforeVisit, HospitalSickLeaveRules.SeenRefusal(visit, visitDay.AddDays(-1), NowUtc));
			Assert.Null(HospitalSickLeaveRules.SeenRefusal(visit, visitDay, NowUtc));
			Assert.Null(HospitalSickLeaveRules.SeenRefusal(visit, visitDay.AddDays(1), NowUtc));
		}

		[Fact]
		public void The_verification_code_is_ten_unambiguous_characters_and_differs_each_time()
		{
			var codes = Enumerable.Range(0, 200).Select(_ => HospitalSickLeaveRules.NewVerifyCode()).ToList();
			Assert.All(codes, c => { Assert.Equal(10, c.Length); Assert.DoesNotContain(c, ch => "01OI".Contains(ch) || !char.IsLetterOrDigit(ch) || char.IsLower(ch)); });
			Assert.Equal(codes.Count, codes.Distinct().Count());
		}

		[Fact]
		public void The_ministrys_authentication_is_recorded_once_on_a_valid_certificate_with_its_reference()
		{
			var l = new HospitalSickLeave();
			Assert.NotNull(HospitalSickLeaveRules.MohRefusal(l, "  "));
			Assert.NotNull(HospitalSickLeaveRules.MohRefusal(l, new string('x', 61)));
			Assert.Null(HospitalSickLeaveRules.MohRefusal(l, "MOH-2026-118822"));
			Assert.NotNull(HospitalSickLeaveRules.MohRefusal(new HospitalSickLeave { MohAuthAt = NowUtc, MohAuthRef = "A" }, "B"));
			Assert.NotNull(HospitalSickLeaveRules.MohRefusal(new HospitalSickLeave { VoidedAt = NowUtc, VoidReason = "x" }, "B"));
		}

		[Fact]
		public void The_public_page_shows_initials_only_and_the_qr_carries_the_verification_address()
		{
			Assert.Equal("M. S. O.", HospitalSickLeaveService.Initials("Mahmoud Saeed Osman"));
			Assert.Equal("", HospitalSickLeaveService.Initials(null));
			Assert.Equal("https://clinic.example/patientportal/verify/sl/ABC23", HospitalSickLeaveSource.VerifyUrl("https://clinic.example/", "ABC23"));
			Assert.EndsWith("/patientportal/verify/sl/ABC23", HospitalSickLeaveSource.VerifyUrl(null, "ABC23"));
		}
	}
}
