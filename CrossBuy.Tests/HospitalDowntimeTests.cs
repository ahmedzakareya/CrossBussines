using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H211 — the downtime ward summary. What must never happen: a paper copy without the time it was printed,
	// a patient's medicines not grouped under that patient, an allergy or isolation missing from the patient's head, or
	// a medicine line without boxes to chart the doses given on paper.
	// ==========================================================================================
	public class HospitalDowntimeTests
	{
		[Fact]
		public void The_summary_is_a_ward_report_grouped_by_patient_with_the_time_it_was_printed()
		{
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.WardDowntime);
			Assert.Contains(d.Parameters, p => p.Key == "WardId" && p.Required);
			Assert.True(d.Fields.Single(f => f.Key == "PatientKey").Groupable);
			Assert.Contains(ReportScreenBindings.For(ReportScreenKeys.HospitalWardDowntime), b => b.ReportCode == HospitalDocumentDatasetCodes.WardDowntime && b.IdParameter == "WardId");
			Assert.Contains(HospitalDocumentDatasetCodes.WardDowntime, HospitalDocumentDatasets.ClinicalDocuments);

			var layout = HospitalDeskLayouts.WardDowntime();
			Assert.Equal(ReportOrientation.Landscape, layout.Page.Orientation);
			var pageHead = layout.Bands.Single(b => b.Kind == ReportBandKind.PageHeader);
			Assert.Contains(pageHead.Elements, e => e.FieldKey == "DocumentDate");   // every page says when it was printed
			var group = layout.Bands.Single(b => b.Kind == ReportBandKind.GroupHeader);
			Assert.Equal("PatientKey", group.GroupFieldKey);
			foreach (var k in new[] { "Bed", "PatientName", "PatientMrn", "Allergies", "Isolation", "BloodGroup", "Diagnosis" }) Assert.Contains(group.Elements, e => e.FieldKey == k);
			var detail = layout.Bands.Single(b => b.Kind == ReportBandKind.Detail);
			Assert.Equal(4, detail.Elements.Count(e => e.Kind == ReportElementKind.Rectangle));   // four boxes to chart doses on paper
			foreach (var k in new[] { "Drug", "Dose", "Route", "Schedule", "LastGiven", "NextDue" }) Assert.Contains(detail.Elements, e => e.FieldKey == k);
		}
	
		[Fact]
		public void Only_the_downtime_summary_may_be_scheduled_and_as_a_pdf()
		{
			// Found live: the shift copies were refused because no hospital document could be scheduled.
			var defs = new HospitalReportDefinitionProvider().GetDefinitions().ToList();
			var dt = defs.Single(d => d.Code == HospitalDocumentDatasetCodes.WardDowntime);
			Assert.True(dt.Capabilities.AllowSchedule);
			Assert.True(dt.Capabilities.SupportsFormat(ReportOutputFormat.Pdf));
			Assert.All(defs.Where(d => d.Code != HospitalDocumentDatasetCodes.WardDowntime), d => Assert.False(d.Capabilities.AllowSchedule));
		}

		[Fact]
		public void The_shift_copies_go_to_valid_addresses_at_valid_times()
		{
			var ok = HospitalDowntimeScheduleRules.Read(new() { Emails = "icu@hospital.example; charge.nurse@hospital.example" });
			Assert.Null(ok.Refusal);
			Assert.Equal(2, ok.Emails.Count);
			Assert.Equal(new[] { (6, 30), (14, 30), (22, 30) }, ok.Times);   // the three shifts by default
			Assert.Equal("Write the address the copies go to", HospitalDowntimeScheduleRules.Read(new() { Emails = " " }).Refusal);
			Assert.Equal("An address is not a valid e-mail", HospitalDowntimeScheduleRules.Read(new() { Emails = "icu at hospital" }).Refusal);
			Assert.Equal("A time is not hh:mm", HospitalDowntimeScheduleRules.Read(new() { Emails = "a@b.example", Times = new() { "25:00" } }).Refusal);
			Assert.Equal("At most four copies a day", HospitalDowntimeScheduleRules.Read(new() { Emails = "a@b.example", Times = new() { "01:00", "02:00", "03:00", "04:00", "05:00" } }).Refusal);
			Assert.Equal("{\"WardId\":\"4\"}", HospitalDowntimeScheduleRules.ParamsFor(4));
		}
}
}
