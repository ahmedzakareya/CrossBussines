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
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalSickLeave), "HospitalSickLeaves");
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
			foreach (var k in HospitalSickLeaveKind.All) Assert.Contains("'" + k + "'", s);
			Assert.Contains("Days = DATEDIFF(day, FromDate, ToDate) + 1", s);
			Assert.Contains("CK_HospitalSickLeaves_Diagnosis", s);
			Assert.Contains("CK_HospitalSickLeaves_Void", s);
			Assert.Equal(new[] { "CompanyId", "LeaveNo" }, m.FindEntityType(typeof(HospitalSickLeave))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
		}

		[Fact]
		public void It_prints_only_through_report_studio_by_its_id()
		{
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.SickLeave);
			Assert.Contains(d.Parameters, p => p.Key == "SickLeaveId" && p.Required);
			foreach (var k in new[] { "FromDate", "ToDate", "Days", "Kind", "Diagnosis", "Void", "DocumentNo", "PatientName", "DoctorName" })
				Assert.Contains(d.Fields, f => f.Key == k);
			Assert.Contains(ReportScreenBindings.For(ReportScreenKeys.HospitalSickLeave), b => b.ReportCode == HospitalDocumentDatasetCodes.SickLeave && b.IdParameter == "SickLeaveId");
		}
	}
}
