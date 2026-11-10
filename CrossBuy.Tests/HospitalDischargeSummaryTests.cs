using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H55 — the discharge summary, with no database: what signing needs, what any save refuses, the stay's
	// length, the operations as the "procedures" text starts from, the table against its slice, and the print as a
	// registered Report Studio document bound to the page by the stay's id.
	// ==========================================================================================
	public class HospitalDischargeSummaryTests
	{
		private static readonly DateTime Today = new(2026, 11, 8);
		private static HospitalDischargeInput Ok() => new() { FinalDiagnosis = "Community-acquired pneumonia", Condition = HospitalDischargeCondition.Improved };

		[Fact]
		public void Signing_needs_the_final_diagnosis_and_the_condition()
		{
			Assert.Null(HospitalDischargeRules.SignRefusal(Ok(), Today));
			var i = Ok(); i.FinalDiagnosis = "  ";
			Assert.Equal("Write the final diagnosis before signing", HospitalDischargeRules.SignRefusal(i, Today));
			i = Ok(); i.Condition = null;
			Assert.Equal("Choose the condition at discharge before signing", HospitalDischargeRules.SignRefusal(i, Today));
			i = Ok(); i.Condition = "Cured";
			Assert.Equal("Choose the condition at discharge before signing", HospitalDischargeRules.SignRefusal(i, Today));
		}

		[Fact]
		public void A_follow_up_in_the_past_is_refused_at_signing_today_is_fine()
		{
			var i = Ok(); i.FollowUpOn = Today.AddDays(-1);
			Assert.Equal("The follow-up date is in the past", HospitalDischargeRules.SignRefusal(i, Today));
			i.FollowUpOn = Today;
			Assert.Null(HospitalDischargeRules.SignRefusal(i, Today));
		}

		[Fact]
		public void A_draft_may_be_incomplete_but_never_unknown_or_too_long()
		{
			Assert.Null(HospitalDischargeRules.SaveRefusal(new HospitalDischargeInput()));
			Assert.Null(HospitalDischargeRules.SaveRefusal(new HospitalDischargeInput { Condition = "" }));
			Assert.Equal("Choose the condition at discharge", HospitalDischargeRules.SaveRefusal(new HospitalDischargeInput { Condition = "Cured" }));
			Assert.Equal("A text is too long", HospitalDischargeRules.SaveRefusal(new HospitalDischargeInput { Course = new string('x', 4001) }));
			Assert.Null(HospitalDischargeRules.SaveRefusal(new HospitalDischargeInput { Course = new string('x', 4000) }));
		}

		[Fact]
		public void The_stay_is_counted_to_discharge_or_to_now()
		{
			var inAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
			Assert.Equal(3.5m, HospitalDischargeRules.StayDays(inAt, inAt.AddHours(84), inAt.AddDays(30)));
			Assert.Equal(2m, HospitalDischargeRules.StayDays(inAt, null, inAt.AddDays(2)));
		}

		[Fact]
		public void The_operations_start_the_procedures_text_in_the_readers_language()
		{
			var ops = new[] { new HospitalDischargeOperation("استئصال المرارة بالمنظار", "Laparoscopic cholecystectomy", new DateTime(2026, 11, 4), null, null) };
			Assert.Equal("2026-11-04 — استئصال المرارة بالمنظار", HospitalDischargeRules.ProceduresText(ops, true));
			Assert.Equal("2026-11-04 — Laparoscopic cholecystectomy", HospitalDischargeRules.ProceduresText(ops, false));
		}

		[Fact]
		public void The_table_matches_its_slice_one_summary_per_stay()
		{
			var s = HospitalSliceParity.Slice("hospital_h55_discharge_summary.sql");
			var m = HospitalSliceParity.Model(HospitalDischargeSummaryModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalDischargeSummary), "HospitalDischargeSummaries");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalDischargeSummary), "HospitalDischargeSummaries");
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
			foreach (var c in HospitalDischargeCondition.All) Assert.Contains("'" + c + "'", s);
			Assert.Equal(new[] { "AdmissionId" }, m.FindEntityType(typeof(HospitalDischargeSummary))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
			Assert.Contains("CK_HospitalDischargeSummaries_Signed", s);
		}

		[Fact]
		public void It_prints_only_through_report_studio_bound_by_the_stay()
		{
			var d = HospitalDocumentDatasets.All().Single(x => x.DatasetCode == HospitalDocumentDatasetCodes.DischargeSummary);
			Assert.Equal(HospitalReportPermissions.View, d.RequiredPermissionKey);
			Assert.Contains(d.Parameters, p => p.Key == "AdmissionId" && p.Required);
			foreach (var k in new[] { "FinalDiagnosis", "Course", "Condition", "Medications", "Instructions", "FollowUpOn", "SignedBy", "PatientName", "PatientMrn" })
				Assert.Contains(d.Fields, f => f.Key == k);
			Assert.Contains(new HospitalReportDefinitionProvider().GetDefinitions(), r => r.Code == HospitalDocumentDatasetCodes.DischargeSummary);
			Assert.Equal("Improved", HospitalDischargeLabels.Condition(HospitalDischargeCondition.Improved, false));
			Assert.Equal("خروج على مسؤوليته", HospitalDischargeLabels.Condition(HospitalDischargeCondition.AgainstAdvice, true));
		}
	}
}
