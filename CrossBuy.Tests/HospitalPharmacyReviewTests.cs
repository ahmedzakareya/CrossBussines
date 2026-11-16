using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H136: the pharmacist's review — its rules and its table.
	//   · Priority: STAT 30 min, urgent 60, routine 240 to the review. An emergency prescription is urgent until the
	//     pharmacist says otherwise.
	//   · The review clock stops when the prescription is reviewed (or refused).
	//   · An intervention needs a kind, a severity, the problem and the recommendation.
	//   · The acceptance rate counts only answered interventions.
	// ==========================================================================================
	public class HospitalPharmacyReviewTests
	{
		private static readonly DateTime T0 = new(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);

		[Theory]
		[InlineData(null, HospitalDispenseSource.Emergency, HospitalRxPriority.Urgent)]
		[InlineData(null, HospitalDispenseSource.Outpatient, HospitalRxPriority.Routine)]
		[InlineData(HospitalRxPriority.Routine, HospitalDispenseSource.Emergency, HospitalRxPriority.Routine)]   // the pharmacist's word wins
		[InlineData(HospitalRxPriority.Stat, HospitalDispenseSource.Outpatient, HospitalRxPriority.Stat)]
		[InlineData("Soon", HospitalDispenseSource.Outpatient, HospitalRxPriority.Routine)]                      // not a priority: ignored
		public void The_priority_is_the_pharmacists_else_what_the_source_implies(string? set, string source, string expected)
			=> Assert.Equal(expected, HospitalRxPriority.Effective(set, source));

		[Theory]
		[InlineData(HospitalRxPriority.Stat, 30, false)]
		[InlineData(HospitalRxPriority.Stat, 31, true)]
		[InlineData(HospitalRxPriority.Urgent, 60, false)]
		[InlineData(HospitalRxPriority.Routine, 241, true)]
		public void A_prescription_is_late_past_its_prioritys_target(string priority, int minutes, bool late)
			=> Assert.Equal(late, HospitalReviewRules.IsLate(priority, T0, null, T0.AddMinutes(minutes)));

		[Fact]
		public void The_review_clock_stops_at_the_review()
		{
			Assert.Equal(20, HospitalReviewRules.WaitingMinutes(T0, T0.AddMinutes(20), T0.AddHours(5)));
			Assert.False(HospitalReviewRules.IsLate(HospitalRxPriority.Stat, T0, T0.AddMinutes(20), T0.AddHours(5)));
			Assert.Equal(0, HospitalReviewRules.WaitingMinutes(T0, null, T0.AddMinutes(-3)));   // a clock never runs backwards
		}

		[Fact]
		public void The_acceptance_rate_counts_answered_interventions_only()
		{
			Assert.Null(HospitalReviewRules.AcceptedPct(0, 0));
			Assert.Equal(67, HospitalReviewRules.AcceptedPct(2, 1));
			Assert.Equal(100, HospitalReviewRules.AcceptedPct(3, 0));
		}

		[Fact]
		public void Age_is_counted_in_whole_years()
		{
			Assert.Equal(38, HospitalReviewRules.AgeYears(new DateTime(1987, 11, 16), new DateTime(2026, 11, 15)));   // the birthday is tomorrow
			Assert.Equal(40, HospitalReviewRules.AgeYears(new DateTime(1986, 11, 15), new DateTime(2026, 11, 15)));
			Assert.Null(HospitalReviewRules.AgeYears(null, new DateTime(2026, 11, 15)));
		}

		[Theory]
		[InlineData("kind", "Choose the kind of problem")]
		[InlineData("severity", "Choose how serious it is")]
		[InlineData("problem", "Write the problem")]
		[InlineData("recommendation", "Write what you recommend")]
		[InlineData("minutes", "The minutes spent are 0 to 600")]
		public void An_intervention_needs_its_kind_severity_problem_and_recommendation(string what, string error)
		{
			var i = new HospitalInterventionInput { Category = HospitalInterventionCategory.Renal, Severity = HospitalInterventionSeverity.High, Problem = "eGFR 25 on 1 g q8h", Recommendation = "500 mg q12h", Minutes = 10 };
			Assert.Null(HospitalReviewRules.Validate(i));
			switch (what) { case "kind": i.Category = "Gut feeling"; break; case "severity": i.Severity = "Huge"; break; case "problem": i.Problem = " "; break; case "recommendation": i.Recommendation = null; break; case "minutes": i.Minutes = 601; break; }
			Assert.Equal(error, HospitalReviewRules.Validate(i)?.Error);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates()
			=> HospitalSliceParity.NamesItsTable(Model(), HospitalSliceParity.Slice("hospital_h136_pharmacist_review.sql"), typeof(HospitalPharmacyIntervention), "HospitalPharmacyInterventions");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates()
			=> HospitalSliceParity.ColumnsMatch(Model(), HospitalSliceParity.Slice("hospital_h136_pharmacist_review.sql"), typeof(HospitalPharmacyIntervention), "HospitalPharmacyInterventions");

		[Fact]
		public void The_slice_adds_the_dispense_columns_and_the_refused_state()
		{
			var sql = HospitalSliceParity.Slice("hospital_h136_pharmacist_review.sql");
			foreach (var c in new[] { "Priority nvarchar(10)", "RejectedAt datetime2", "RejectedByEmployeeId int", "RejectReason nvarchar(400)" })
				Assert.Contains("ALTER TABLE dbo.HospitalDispenses ADD " + c + " NULL", sql);
			Assert.Contains("CHECK (Status IN ('New','Verified','Dispensed','Rejected'))", sql);
			Assert.Equal(HospitalDispenseStatus.All.OrderBy(s => s), new[] { "Dispensed", "New", "Rejected", "Verified" });
		}
	}
}
