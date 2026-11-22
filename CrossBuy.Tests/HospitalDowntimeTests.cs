using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
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

		// H226: when the platform has no mail transport the run's archived PDF is handed back to download, so a ward is never
		// left without its copy; a transport that exists but sent nothing stays a refusal, and a failed run gives no copy.
		[Theory]
		[InlineData(false, 0, ReportRunStatus.Succeeded, true, null, 51L)]
		[InlineData(true, 0, ReportRunStatus.Succeeded, false, "The copy was generated but no e-mail could be sent", null)]
		[InlineData(true, 1, ReportRunStatus.Succeeded, true, null, null)]
		[InlineData(false, 0, ReportRunStatus.Failed, false, "boom", null)]
		public async Task Without_mail_the_copy_is_handed_back_instead(bool mailUp, int sent, ReportRunStatus status, bool ok, string? error, long? copy)
		{
			using var host = new PlatformTestHost(companyId: 1);
			host.Seed.ReportSchedules.Add(new CrossBuy.Models.Context.Reporting.ReportSchedule
			{
				CompanyID = 1, ReportCode = HospitalDocumentDatasetCodes.WardDowntime, Name = "x", ParametersJson = "{\"WardId\":\"4\"}",
				IsActive = true, OwnerEmpId = 7, AtHour = 6, AtMinute = 30,
			});
			await host.Seed.SaveChangesAsync();
			var svc = new HospitalDowntimeScheduleService(host.Db, null!, new Runner(status, sent), new Mail(mailUp));
			var ctx = new CrossBuy.Models.Platform.BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>() };
			var (r, id) = await svc.SendNowWithCopyAsync(ctx, 4);
			Assert.Equal((ok, error, copy), (r.Ok, r.Error, id));
			Assert.Equal("Set the ward's shift copies first", (await svc.SendNowWithCopyAsync(ctx, 5)).Result.Error);
		}

		private sealed class Runner : IReportScheduleRunner
		{
			private readonly ReportRunStatus _status; private readonly int _sent;
			public Runner(ReportRunStatus status, int sent) { _status = status; _sent = sent; }
			public Task<ReportScheduleRunOutcome> RunAsync(CrossBuy.Models.Context.Reporting.ReportSchedule schedule, CancellationToken cancellationToken = default) =>
				Task.FromResult(new ReportScheduleRunOutcome
				{
					ScheduleId = schedule.Id, Status = _status, DeliveriesAttempted = 1, DeliveriesSent = _sent,
					ArchiveEntryId = _status == ReportRunStatus.Succeeded ? 51 : null,
					Diagnostics = _status == ReportRunStatus.Succeeded ? Array.Empty<ReportDiagnostic>() : new[] { ReportDiagnostic.Error("x", "boom") },
				});
			public Task<IReadOnlyList<ReportScheduleRunOutcome>> RunDueAsync(int companyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		}

		private sealed class Mail : IReportMailSender
		{
			public Mail(bool up) => IsAvailable = up;
			public string SenderName => "test";
			public bool IsAvailable { get; }
			public string? UnavailableReason => IsAvailable ? null : "off";
			public Task<ReportDeliveryOutcome> SendAsync(ReportMailMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		}
	}
}
