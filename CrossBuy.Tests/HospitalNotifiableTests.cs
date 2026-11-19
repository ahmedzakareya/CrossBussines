using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H209 — notifiable diseases. What must never happen: a notifiable diagnosis not matched (or matched to the
	// less specific disease), a group A case given a week, a case closed without the Ministry's reference or a reason,
	// a closed case closed again, an open case past its time not shown overdue, or a bad code put on the list.
	// ==========================================================================================
	public class HospitalNotifiableTests
	{
		private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
		private sealed record D(string Prefix, string Name);
		private static readonly D[] List = { new("A00", "Cholera"), new("A05", "Food poisoning"), new("A05.1", "Botulism"), new("B17.1", "Hepatitis C"), new("A15", "TB") };

		[Theory]
		[InlineData("A00.9", "Cholera")]
		[InlineData("a00", "Cholera")]
		[InlineData("A05.1", "Botulism")]   // the most specific prefix wins
		[InlineData("A051", "Botulism")]
		[InlineData("A05.9", "Food poisoning")]
		[InlineData("B17.1", "Hepatitis C")]
		[InlineData("A15.0", "TB")]
		public void A_diagnosis_falls_under_the_most_specific_disease(string code, string disease) =>
			Assert.Equal(disease, HospitalNotifiableRules.Match(code, List, d => d.Prefix)!.Name);

		[Theory]
		[InlineData("B17.2")]   // hepatitis E is not hepatitis C
		[InlineData("J18.9")]
		[InlineData("A0")]
		[InlineData("")]
		[InlineData(null)]
		public void Other_diagnoses_open_no_case(string? code) => Assert.Null(HospitalNotifiableRules.Match(code, List, d => d.Prefix));

		[Fact]
		public void Group_A_is_due_in_24_hours_and_group_B_in_7_days_and_overdue_follows()
		{
			Assert.Equal(Now.AddHours(24), HospitalNotifiableRules.DueAt(HospitalNotifiableUrgency.Immediate, Now));
			Assert.Equal(Now.AddDays(7), HospitalNotifiableRules.DueAt(HospitalNotifiableUrgency.Weekly, Now));
			var r = new HospitalDiseaseReport { Status = HospitalDiseaseReportStatus.Due, DueAt = Now };
			Assert.False(HospitalNotifiableRules.Overdue(r, Now));
			Assert.True(HospitalNotifiableRules.Overdue(r, Now.AddMinutes(1)));
			r.Status = HospitalDiseaseReportStatus.Reported;
			Assert.False(HospitalNotifiableRules.Overdue(r, Now.AddDays(3)));
		}

		[Fact]
		public void A_case_closes_once_with_the_ministrys_reference_or_a_reason()
		{
			var due = new HospitalDiseaseReport { Status = HospitalDiseaseReportStatus.Due };
			Assert.Null(HospitalNotifiableRules.CloseRefusal(due, HospitalDiseaseReportStatus.Reported, "PHD-2026-4412"));
			Assert.Equal("Write the Ministry's reference for this report", HospitalNotifiableRules.CloseRefusal(due, HospitalDiseaseReportStatus.Reported, " "));
			Assert.Equal("The reference is too long", HospitalNotifiableRules.CloseRefusal(due, HospitalDiseaseReportStatus.Reported, new string('x', 61)));
			Assert.Null(HospitalNotifiableRules.CloseRefusal(due, HospitalDiseaseReportStatus.Dismissed, "Diagnosis revised to J18.9"));
			Assert.Equal("Write why this is not a case to report", HospitalNotifiableRules.CloseRefusal(due, HospitalDiseaseReportStatus.Dismissed, ""));
			Assert.Equal("not_found", HospitalNotifiableRules.CloseRefusal(due, HospitalDiseaseReportStatus.Due, "x"));
			Assert.Equal("This case is closed", HospitalNotifiableRules.CloseRefusal(new HospitalDiseaseReport { Status = HospitalDiseaseReportStatus.Reported }, HospitalDiseaseReportStatus.Dismissed, "x"));
		}

		[Fact]
		public void A_disease_on_the_list_has_a_real_code_a_name_and_a_group()
		{
			Assert.Null(HospitalNotifiableRules.DiseaseRefusal(new() { CodePrefix = "A05.1", Name = "التسمم الوشيقي", Urgency = HospitalNotifiableUrgency.Immediate }));
			Assert.NotNull(HospitalNotifiableRules.DiseaseRefusal(new() { CodePrefix = "A0", Name = "x", Urgency = HospitalNotifiableUrgency.Immediate }));
			Assert.NotNull(HospitalNotifiableRules.DiseaseRefusal(new() { CodePrefix = "105", Name = "x", Urgency = HospitalNotifiableUrgency.Immediate }));
			Assert.NotNull(HospitalNotifiableRules.DiseaseRefusal(new() { CodePrefix = "A00", Name = " ", Urgency = HospitalNotifiableUrgency.Immediate }));
			Assert.NotNull(HospitalNotifiableRules.DiseaseRefusal(new() { CodePrefix = "A00", Name = "x", Urgency = "Monthly" }));
		}

		[Fact]
		public void The_seeded_list_is_unique_by_code_named_in_both_languages_and_grouped()
		{
			var d = HospitalNotifiableRules.Defaults;
			Assert.Equal(d.Count, d.Select(x => HospitalNotifiableRules.Norm(x.Prefix)).Distinct().Count());
			Assert.All(d, x => { Assert.False(string.IsNullOrWhiteSpace(x.Ar)); Assert.False(string.IsNullOrWhiteSpace(x.En)); Assert.Contains(x.Urgency, HospitalNotifiableUrgency.All);
				Assert.Null(HospitalNotifiableRules.DiseaseRefusal(new() { CodePrefix = x.Prefix, Name = x.Ar, NameEn = x.En, Urgency = x.Urgency })); });
			Assert.Equal(HospitalNotifiableUrgency.Immediate, d.Single(x => x.Prefix == "A00").Urgency);   // cholera: the same day
			Assert.Equal(HospitalNotifiableUrgency.Immediate, d.Single(x => x.Prefix == "B05").Urgency);   // measles: the same day
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var model = HospitalSliceParity.Model(HospitalNotifiableModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h209_notifiable_diseases.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalNotifiableDisease), "HospitalNotifiableDiseases");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalNotifiableDisease), "HospitalNotifiableDiseases");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalDiseaseReport), "HospitalDiseaseReports");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalDiseaseReport), "HospitalDiseaseReports");
			foreach (var x in HospitalDiseaseReportStatus.All.Concat(HospitalNotifiableUrgency.All)) Assert.Contains("'" + x + "'", s);
			Assert.Contains("CK_HospitalDiseaseReports_Close", s);
		}

		[Fact]
		public void It_prints_only_through_report_studio_by_its_id()
		{
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.DiseaseReport);
			Assert.Contains(d.Parameters, p => p.Key == "DiseaseReportId" && p.Required);
			foreach (var k in new[] { "Disease", "Group", "Diagnosis", "DueBy", "CivilId", "MohRef", "PatientName", "DoctorName" }) Assert.Contains(d.Fields, f => f.Key == k);
			Assert.Contains(ReportScreenBindings.For(ReportScreenKeys.HospitalDiseaseReport), b => b.ReportCode == HospitalDocumentDatasetCodes.DiseaseReport && b.IdParameter == "DiseaseReportId");
			Assert.Contains(HospitalDocumentDatasetCodes.DiseaseReport, HospitalDocumentDatasets.ClinicalDocuments);
		}
	}
}
