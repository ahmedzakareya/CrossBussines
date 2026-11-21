using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H216 — anatomic pathology. What must never happen: a step skipped (a report signed on a specimen never
	// described, a description of one never received), a receipt with a container missing, a report without its
	// diagnosis, a signed report edited instead of amended, an amendment without its reason, or a late case not shown.
	// ==========================================================================================
	public class HospitalPathologyTests
	{
		private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
		private static HospitalPathCase C(string status, int containers = 2) => new() { Status = status, Containers = containers };

		[Fact]
		public void A_request_names_its_source_its_specimen_and_its_containers()
		{
			Assert.Null(HospitalPathRules.RequestRefusal(new() { Source = HospitalPathSource.Endoscopy, Specimen = "Gastric antrum biopsy", Containers = 2 }));
			Assert.NotNull(HospitalPathRules.RequestRefusal(new() { Source = "Post", Specimen = "x", Containers = 1 }));
			Assert.NotNull(HospitalPathRules.RequestRefusal(new() { Source = HospitalPathSource.Clinic, Specimen = " ", Containers = 1 }));
			Assert.NotNull(HospitalPathRules.RequestRefusal(new() { Source = HospitalPathSource.Clinic, Specimen = "x", Containers = 0 }));
		}

		[Fact]
		public void Each_step_follows_the_one_before()
		{
			Assert.Null(HospitalPathRules.StepRefusal(C(HospitalPathStatus.Requested), new() { To = HospitalPathStatus.Received, Containers = 2 }));
			Assert.Equal("The containers received do not match the request: count them again", HospitalPathRules.StepRefusal(C(HospitalPathStatus.Requested), new() { To = HospitalPathStatus.Received, Containers = 1 }));
			Assert.Equal("Receive the specimen before describing it", HospitalPathRules.StepRefusal(C(HospitalPathStatus.Requested), new() { To = HospitalPathStatus.Grossed, Gross = "x", Blocks = 2 }));
			Assert.Null(HospitalPathRules.StepRefusal(C(HospitalPathStatus.Received), new() { To = HospitalPathStatus.Grossed, Gross = "Two tan fragments, 3 mm", Blocks = 1 }));
			Assert.Equal("Write how many blocks were cut", HospitalPathRules.StepRefusal(C(HospitalPathStatus.Received), new() { To = HospitalPathStatus.Grossed, Gross = "x" }));
			Assert.Equal("Describe the specimen before signing the report", HospitalPathRules.StepRefusal(C(HospitalPathStatus.Received), new() { To = HospitalPathStatus.Reported, Microscopic = "x", Diagnosis = "y" }));
			Assert.Equal("Write the diagnosis", HospitalPathRules.StepRefusal(C(HospitalPathStatus.Grossed), new() { To = HospitalPathStatus.Reported, Microscopic = "x" }));
			Assert.Null(HospitalPathRules.StepRefusal(C(HospitalPathStatus.Grossed), new() { To = HospitalPathStatus.Reported, Microscopic = "Chronic active gastritis", Diagnosis = "H. pylori gastritis" }));
			Assert.Equal("Only a request not yet received is cancelled", HospitalPathRules.StepRefusal(C(HospitalPathStatus.Received), new() { To = HospitalPathStatus.Cancelled, Reason = "x" }));
			Assert.Equal("Receive the specimen before describing it", HospitalPathRules.StepRefusal(C(HospitalPathStatus.Reported), new() { To = HospitalPathStatus.Grossed }));   // a signed case never goes back
			Assert.Equal("not_found", HospitalPathRules.StepRefusal(C(HospitalPathStatus.Reported), new() { To = "Archived" }));
		}

		[Fact]
		public void A_signed_report_is_amended_with_its_reason_never_edited()
		{
			Assert.Null(HospitalPathRules.AmendRefusal(C(HospitalPathStatus.Reported), "Immunostains: CD20 positive", "Immunohistochemistry completed"));
			Assert.Equal("Write why the report is amended", HospitalPathRules.AmendRefusal(C(HospitalPathStatus.Reported), "x", " "));
			Assert.Equal("Only a signed report is amended", HospitalPathRules.AmendRefusal(C(HospitalPathStatus.Grossed), "x", "y"));
			Assert.NotNull(HospitalPathRules.StepRefusal(C(HospitalPathStatus.Reported), new() { To = HospitalPathStatus.Reported, Microscopic = "x", Diagnosis = "y" }));
		}

		[Fact]
		public void A_malignant_diagnosis_carries_its_synoptic()
		{
			HospitalPathStepInput Ok() => new() { To = HospitalPathStatus.Reported, Microscopic = "Invasive carcinoma", Diagnosis = "Adenocarcinoma", Malignant = true,
				TumourType = "Adenocarcinoma, NOS", Grade = "G2", TumourSizeMm = 23.5m, Margins = HospitalPathMargins.Clear, MarginMm = 4, Lvi = HospitalPathLvi.Absent, PT = "pT2", PN = "pN0", NodesExamined = 14, NodesPositive = 0 };
			var c = C(HospitalPathStatus.Grossed);
			Assert.Null(HospitalPathRules.StepRefusal(c, Ok()));
			var i = Ok(); i.TumourType = null; Assert.Equal("Write the tumour type", HospitalPathRules.StepRefusal(c, i));
			i = Ok(); i.MarginMm = null; Assert.Equal("Write the closest margin in mm", HospitalPathRules.StepRefusal(c, i));
			i = Ok(); i.Margins = HospitalPathMargins.Involved; i.MarginMm = null; Assert.Null(HospitalPathRules.StepRefusal(c, i));   // an involved margin has no distance
			i = Ok(); i.NodesPositive = 15; Assert.Equal("The positive nodes cannot exceed those examined", HospitalPathRules.StepRefusal(c, i));
			i = Ok(); i.PT = " "; Assert.Equal("Write the pT stage", HospitalPathRules.StepRefusal(c, i));
			var benign = new HospitalPathStepInput { To = HospitalPathStatus.Reported, Microscopic = "x", Diagnosis = "Benign naevus" };
			Assert.Null(HospitalPathRules.StepRefusal(c, benign));   // no synoptic for a benign diagnosis
			var signed = new HospitalPathCase { Malignant = true, TumourType = "Adenocarcinoma", Grade = "G2", TumourSizeMm = 23.5m, Margins = "Clear", MarginMm = 4, Lvi = "Absent", PT = "pT2", PN = "pN0", NodesExamined = 14, NodesPositive = 0 };
			Assert.Equal("Adenocarcinoma · grade G2 · 23.5 mm · Margins: clear 4 mm · LVI: absent · pT2 pN0 · nodes 0/14", HospitalPathRules.Synoptic(signed, false));
		}

		[Fact]
		public void Every_block_and_its_slide_has_a_label()
		{
			var l = HospitalPathRules.Labels("PATH-000007", 2);
			Assert.Equal(new[] { "PATH-000007-A1", "PATH-000007-A1-S1", "PATH-000007-A2", "PATH-000007-A2-S1" }, l.Select(x => x.Code));
			Assert.Equal(new[] { "Block", "Slide", "Block", "Slide" }, l.Select(x => x.Kind));
			Assert.Empty(HospitalPathRules.Labels("PATH-000007", 0));
		}

		[Fact]
		public void A_case_received_more_than_3_days_ago_and_not_signed_is_late()
		{
			var c = new HospitalPathCase { Status = HospitalPathStatus.Grossed, ReceivedAt = Now.AddDays(-3).AddMinutes(-1) };
			Assert.True(HospitalPathRules.Late(c, Now));
			c.ReceivedAt = Now.AddDays(-2);
			Assert.False(HospitalPathRules.Late(c, Now));
			c.ReceivedAt = Now.AddDays(-9); c.Status = HospitalPathStatus.Reported;
			Assert.False(HospitalPathRules.Late(c, Now));
		}

		[Fact]
		public void The_tables_match_their_slice_and_the_report_prints_from_report_studio()
		{
			var model = HospitalSliceParity.Model(HospitalPathologyModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h216_pathology.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalPathCase), "HospitalPathCases");
			HospitalSliceParity.ColumnsMatch(model, s + Environment.NewLine + HospitalSliceParity.Slice("hospital_h222_path_synoptic.sql"), typeof(HospitalPathCase), "HospitalPathCases");   // + H222's synoptic
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalPathAmendment), "HospitalPathAmendments");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalPathAmendment), "HospitalPathAmendments");
			foreach (var x in HospitalPathStatus.All.Concat(HospitalPathSource.All)) Assert.Contains("'" + x + "'", s);
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.PathReport);
			Assert.Contains(d.Parameters, p => p.Key == "PathCaseId" && p.Required);
			Assert.Contains(ReportScreenBindings.For(ReportScreenKeys.HospitalPathology), b => b.ReportCode == HospitalDocumentDatasetCodes.PathReport && b.IdParameter == "PathCaseId");
			Assert.Contains(HospitalDocumentDatasetCodes.PathReport, HospitalDocumentDatasets.ClinicalDocuments);
			var layout = HospitalDeskLayouts.PathReport();   // one A4 page, the amendments included
			Assert.True(layout.Bands.Single().HeightMm <= ReportPaper.ContentHeightMm(layout.Page));
		}
	}
}
