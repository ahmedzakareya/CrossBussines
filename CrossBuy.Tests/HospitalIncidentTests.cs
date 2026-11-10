using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H45 — incident reports, with no database: a serious harm starts in root-cause analysis and closes only
	// through it, what a report needs, what closing needs, patient-days and falls per 1,000, and the model against its
	// slice (the database itself refuses a reporter on an anonymous report, and a serious harm outside an analysis).
	// ==========================================================================================
	public class HospitalIncidentTests
	{
		[Theory]
		[InlineData(HospitalIncidentHarm.NearMiss, HospitalIncidentStatus.New)]
		[InlineData(HospitalIncidentHarm.NoHarm, HospitalIncidentStatus.New)]
		[InlineData(HospitalIncidentHarm.Mild, HospitalIncidentStatus.New)]
		[InlineData(HospitalIncidentHarm.Moderate, HospitalIncidentStatus.Rca)]
		[InlineData(HospitalIncidentHarm.Severe, HospitalIncidentStatus.Rca)]
		public void A_serious_harm_opens_an_analysis_at_once(string harm, string status) => Assert.Equal(status, HospitalIncidentRules.InitialStatus(harm));

		[Theory]
		[InlineData(HospitalIncidentStatus.New, HospitalIncidentStatus.Review, HospitalIncidentHarm.Mild, true)]
		[InlineData(HospitalIncidentStatus.Review, HospitalIncidentStatus.Rca, HospitalIncidentHarm.Mild, true)]
		[InlineData(HospitalIncidentStatus.Review, HospitalIncidentStatus.Closed, HospitalIncidentHarm.Mild, true)]
		[InlineData(HospitalIncidentStatus.New, HospitalIncidentStatus.Closed, HospitalIncidentHarm.NearMiss, true)]
		[InlineData(HospitalIncidentStatus.Rca, HospitalIncidentStatus.Closed, HospitalIncidentHarm.Severe, true)]
		[InlineData(HospitalIncidentStatus.Review, HospitalIncidentStatus.Closed, HospitalIncidentHarm.Moderate, false)]   // never past the analysis
		[InlineData(HospitalIncidentStatus.Closed, HospitalIncidentStatus.Review, HospitalIncidentHarm.Mild, false)]
		[InlineData(HospitalIncidentStatus.Rca, HospitalIncidentStatus.Review, HospitalIncidentHarm.Mild, false)]
		public void Quality_management_moves_one_step_at_a_time(string from, string to, string harm, bool ok) => Assert.Equal(ok, HospitalIncidentRules.CanMove(from, to, harm));

		[Fact]
		public void An_analysis_closes_with_its_root_cause_and_actions_anything_else_with_its_outcome()
		{
			Assert.Equal("Write the root cause before closing the analysis", HospitalIncidentRules.CloseRefusal(HospitalIncidentStatus.Rca, " ", "x", null));
			Assert.Equal("Write the actions agreed before closing the analysis", HospitalIncidentRules.CloseRefusal(HospitalIncidentStatus.Rca, "x", null, "y"));
			Assert.Null(HospitalIncidentRules.CloseRefusal(HospitalIncidentStatus.Rca, "x", "y", null));
			Assert.Equal("Write the outcome before closing", HospitalIncidentRules.CloseRefusal(HospitalIncidentStatus.Review, "x", "y", null));
			Assert.Null(HospitalIncidentRules.CloseRefusal(HospitalIncidentStatus.New, null, null, "Labelled at the bench; no delay"));
		}

		[Fact]
		public void A_report_needs_its_type_harm_place_and_a_sentence()
		{
			Assert.Null(HospitalIncidentRules.ReportRefusal(HospitalIncidentKind.Fall, HospitalIncidentHarm.Mild, HospitalIncidentPlace.Ward, 2, "Patient slipped beside bed 104."));
			Assert.Equal("Choose the ward", HospitalIncidentRules.ReportRefusal(HospitalIncidentKind.Fall, HospitalIncidentHarm.Mild, HospitalIncidentPlace.Ward, null, "Patient slipped beside the bed."));
			Assert.Null(HospitalIncidentRules.ReportRefusal(HospitalIncidentKind.Other, HospitalIncidentHarm.NoHarm, "Emergency", null, "Needle-stick while recapping."));
			Assert.Equal("Write what happened (a sentence at least)", HospitalIncidentRules.ReportRefusal(HospitalIncidentKind.Fall, HospitalIncidentHarm.Mild, "Emergency", null, "fell"));
			Assert.Equal("Choose the harm level", HospitalIncidentRules.ReportRefusal(HospitalIncidentKind.Fall, "Catastrophic", "Emergency", null, "Patient slipped beside the bed."));
		}

		[Fact]
		public void Patient_days_count_only_the_days_inside_the_month()
		{
			DateTime from = new(2026, 11, 1), to = new(2026, 12, 1);
			Assert.Equal(4m, HospitalIncidentRules.DaysIn(new DateTime(2026, 10, 28), new DateTime(2026, 11, 5), from, to));
			Assert.Equal(30m, HospitalIncidentRules.DaysIn(new DateTime(2026, 10, 1), null, from, to));
			Assert.Equal(0m, HospitalIncidentRules.DaysIn(new DateTime(2026, 10, 1), new DateTime(2026, 10, 30), from, to));
			Assert.Equal(0.8m, HospitalIncidentRules.Per1000(2, 2500));
			Assert.Null(HospitalIncidentRules.Per1000(2, 0));
			Assert.Equal("INC-0412", HospitalIncidentRules.Number(412));
		}

		private static string Slice() => HospitalSliceParity.Slice("hospital_h45_incidents.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalIncidentsModel.Configure);

		[Fact]
		public void The_table_matches_its_slice()
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalIncident), "HospitalIncidents");
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalIncident), "HospitalIncidents");
			foreach (var v in HospitalIncidentKind.All.Concat(HospitalIncidentHarm.All).Concat(HospitalIncidentStatus.All).Concat(HospitalIncidentPlace.All)) Assert.Contains($"N'{v}'", Slice());
			Assert.Contains("Anonymous = 0 OR ReporterEmployeeId IS NULL", Slice());
			Assert.Contains("Harm NOT IN (N'Moderate', N'Severe') OR Status IN (N'Rca', N'Closed')", Slice());
			Assert.Equal(new[] { "HospitalPatients", "HospitalWards" }, HospitalSliceParity.CheckedPrerequisites(Slice()).OrderBy(x => x));
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
		}

		// ---------------- H54: the follow-up tasks ----------------

		private static readonly DateTime Today54 = new(2026, 11, 8);

		[Fact]
		public void A_follow_up_task_is_opened_only_during_the_analysis_with_what_who_and_when()
		{
			Assert.Null(HospitalIncidentService.FollowUpRefusal(HospitalIncidentStatus.Rca, "Double-check insulin doses", 5, Today54, Today54));
			Assert.Equal("Follow-up tasks are opened during the root-cause analysis", HospitalIncidentService.FollowUpRefusal(HospitalIncidentStatus.Review, "Check", 5, Today54, Today54));
			Assert.Equal("Follow-up tasks are opened during the root-cause analysis", HospitalIncidentService.FollowUpRefusal(HospitalIncidentStatus.Closed, "Check", 5, Today54, Today54));
			Assert.Equal("Write what is to be done", HospitalIncidentService.FollowUpRefusal(HospitalIncidentStatus.Rca, " ab ", 5, Today54, Today54));
			Assert.Equal("Choose who will do it", HospitalIncidentService.FollowUpRefusal(HospitalIncidentStatus.Rca, "Check", 0, Today54, Today54));
			Assert.Equal("Give a due date from today on", HospitalIncidentService.FollowUpRefusal(HospitalIncidentStatus.Rca, "Check", 5, Today54.AddDays(-1), Today54));
			Assert.Equal("Give a due date from today on", HospitalIncidentService.FollowUpRefusal(HospitalIncidentStatus.Rca, "Check", 5, null, Today54));
		}

		[Fact]
		public void The_link_table_matches_its_slice_and_a_task_belongs_to_one_incident()
		{
			var h54 = HospitalSliceParity.Slice("hospital_h54_incident_followups.sql");
			HospitalSliceParity.NamesItsTable(Model(), h54, typeof(HospitalIncidentTask), "HospitalIncidentTasks");
			HospitalSliceParity.ColumnsMatch(Model(), h54, typeof(HospitalIncidentTask), "HospitalIncidentTasks");
			Assert.Equal(new[] { "HospitalIncidents", "TaskItems" }, HospitalSliceParity.CheckedPrerequisites(h54).OrderBy(x => x));
			Assert.Equal(new[] { "TaskId" }, Model().FindEntityType(typeof(HospitalIncidentTask))!.GetIndexes().Single(i => i.IsUnique).Properties.Select(x => x.Name));
			Assert.DoesNotContain("DROP ", h54, StringComparison.OrdinalIgnoreCase);
		}
	}
}
