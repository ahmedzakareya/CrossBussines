using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H72 — medication reconciliation, with no database: what a line needs, what a decision needs at each
	// stage, when a stage can be signed (every home medicine decided, every hospital medicine decided at discharge, or
	// "takes none at home"), the list the patient goes home with, the overdue flag, and the tables against their slice.
	// ==========================================================================================
	public class HospitalMedReconTests
	{
		private static HospitalMedReconLine Home(string? admit = null, string? discharge = null) => new() { Kind = HospitalMedLineKind.Home, Drug = "Metformin", Dose = "500 mg", Route = "PO", Frequency = "twice a day", AdmitDecision = admit, DischargeDecision = discharge };

		[Fact]
		public void A_line_needs_its_medicine_and_a_new_one_its_dose()
		{
			Assert.Null(HospitalMedReconRules.LineRefusal(new HospitalMedLineInput { Kind = HospitalMedLineKind.Home, Drug = "Metformin", Route = "PO", Source = "Patient" }));
			Assert.Equal("Write the medicine", HospitalMedReconRules.LineRefusal(new HospitalMedLineInput { Kind = HospitalMedLineKind.Home, Drug = " " }));
			Assert.Equal("Choose the route", HospitalMedReconRules.LineRefusal(new HospitalMedLineInput { Kind = HospitalMedLineKind.Home, Drug = "x", Route = "ORAL" }));
			Assert.Equal("Choose where the history came from", HospitalMedReconRules.LineRefusal(new HospitalMedLineInput { Kind = HospitalMedLineKind.Home, Drug = "x", Source = "Neighbour" }));
			Assert.Equal("Write the dose", HospitalMedReconRules.LineRefusal(new HospitalMedLineInput { Kind = HospitalMedLineKind.New, Drug = "Amoxicillin" }));
			Assert.Equal("not_found", HospitalMedReconRules.LineRefusal(new HospitalMedLineInput { Kind = HospitalMedLineKind.Inpatient, Drug = "x" }));
		}

		[Fact]
		public void A_decision_fits_its_stage_and_anything_but_continue_says_why()
		{
			var A = HospitalMedReconStage.Admission; var D = HospitalMedReconStage.Discharge;
			Assert.Null(HospitalMedReconRules.DecideRefusal(A, Home(), false, HospitalMedDecision.Continue, null));
			Assert.Equal("Write why", HospitalMedReconRules.DecideRefusal(A, Home(), false, HospitalMedDecision.Hold, " "));
			Assert.Equal("Write the new dose or how it changes", HospitalMedReconRules.DecideRefusal(A, Home(), false, HospitalMedDecision.Change, null));
			Assert.Null(HospitalMedReconRules.DecideRefusal(A, Home(), false, HospitalMedDecision.Stop, "kidney function"));
			Assert.Equal("On admission only the home medicines are reconciled", HospitalMedReconRules.DecideRefusal(A, null, true, HospitalMedDecision.Continue, null));
			// hold is an admission decision, not one for home
			Assert.Equal("Choose the decision", HospitalMedReconRules.DecideRefusal(D, Home(), false, HospitalMedDecision.Hold, "x"));
			Assert.Null(HospitalMedReconRules.DecideRefusal(D, null, true, HospitalMedDecision.Continue, null));
			Assert.Equal("A medicine new for home is already on the list", HospitalMedReconRules.DecideRefusal(D, new HospitalMedReconLine { Kind = HospitalMedLineKind.New }, false, HospitalMedDecision.Stop, "x"));
		}

		[Fact]
		public void The_admission_stage_is_signed_when_every_home_medicine_is_decided_or_none_is_taken()
		{
			var A = HospitalMedReconStage.Admission; var none = Array.Empty<int>();
			Assert.Equal("List the home medicines, or record that the patient takes none", HospitalMedReconRules.CompleteRefusal(A, Array.Empty<HospitalMedReconLine>(), none, false));
			Assert.Null(HospitalMedReconRules.CompleteRefusal(A, Array.Empty<HospitalMedReconLine>(), none, true));
			Assert.StartsWith("Home medicines are listed", HospitalMedReconRules.CompleteRefusal(A, new[] { Home() }, none, true));
			Assert.Equal("Decide every home medicine first", HospitalMedReconRules.CompleteRefusal(A, new[] { Home(HospitalMedDecision.Continue), Home() }, none, false));
			Assert.Null(HospitalMedReconRules.CompleteRefusal(A, new[] { Home(HospitalMedDecision.Continue), Home(HospitalMedDecision.Stop) }, none, false));
		}

		[Fact]
		public void The_discharge_stage_needs_every_hospital_medicine_decided_too()
		{
			var D = HospitalMedReconStage.Discharge;
			var lines = new List<HospitalMedReconLine> { Home(HospitalMedDecision.Continue, HospitalMedDecision.Continue) };
			Assert.Equal("Decide every medicine still given in hospital first", HospitalMedReconRules.CompleteRefusal(D, lines, new[] { 7 }, false));
			lines.Add(new HospitalMedReconLine { Kind = HospitalMedLineKind.Inpatient, MedOrderId = 7, Drug = "Ceftriaxone", DischargeDecision = HospitalMedDecision.Stop });
			Assert.Null(HospitalMedReconRules.CompleteRefusal(D, lines, new[] { 7 }, false));
			lines.Add(Home(HospitalMedDecision.Hold));
			Assert.Equal("Decide every home medicine first", HospitalMedReconRules.CompleteRefusal(D, lines, new[] { 7 }, false));
		}

		[Fact]
		public void The_patient_goes_home_with_what_continues_changes_or_is_new()
		{
			var lines = new[]
			{
				Home(HospitalMedDecision.Continue, HospitalMedDecision.Continue),
				new HospitalMedReconLine { Kind = HospitalMedLineKind.Home, Drug = "Aspirin", Dose = "81 mg", DischargeDecision = HospitalMedDecision.Stop },
				new HospitalMedReconLine { Kind = HospitalMedLineKind.Inpatient, Drug = "Furosemide", Dose = "40 mg", Route = "PO", DischargeDecision = HospitalMedDecision.Change, DischargeNote = "20 mg from day 3" },
				new HospitalMedReconLine { Kind = HospitalMedLineKind.New, Drug = "Amoxicillin", Dose = "500 mg", Frequency = "three times a day", DischargeNote = "for 5 days" },
			};
			Assert.Equal(new[] { "Metformin 500 mg PO twice a day", "Furosemide 40 mg PO — 20 mg from day 3", "Amoxicillin 500 mg three times a day — for 5 days" }, HospitalMedReconRules.HomeList(lines));
		}

		[Fact]
		public void The_admission_reconciliation_is_overdue_after_a_day()
		{
			var now = new DateTime(2026, 11, 9, 12, 0, 0);
			Assert.True(HospitalMedReconRules.AdmissionOverdue(now.AddHours(-25), null, now));
			Assert.False(HospitalMedReconRules.AdmissionOverdue(now.AddHours(-5), null, now));
			Assert.False(HospitalMedReconRules.AdmissionOverdue(now.AddHours(-25), new HospitalMedRecon(), now));
			Assert.Equal(new[] { "Continue", "Change", "Hold", "Stop" }, HospitalMedDecision.For(HospitalMedReconStage.Admission));
			Assert.Equal(new[] { "Continue", "Change", "Stop" }, HospitalMedDecision.For(HospitalMedReconStage.Discharge));
		}

		[Fact]
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h72_med_recon.sql");
			var m = HospitalSliceParity.Model(HospitalMedReconModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalMedReconLine), "HospitalMedReconLines");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalMedReconLine), "HospitalMedReconLines");
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalMedRecon), "HospitalMedRecons");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalMedRecon), "HospitalMedRecons");
			foreach (var x in HospitalMedReconStage.All.Concat(HospitalMedSource.All).Concat(HospitalMedDecision.For(HospitalMedReconStage.Admission))) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalMedReconLine));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalMedRecon));
		}
	}
}
