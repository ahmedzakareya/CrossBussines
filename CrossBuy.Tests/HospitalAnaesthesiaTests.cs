using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H202 — the anaesthesia record. What must never happen: induction without an assessment, induction on a
	// patient who ate two hours ago (unless an emergency with its reason), a difficult airway not flagged, a milestone
	// recorded twice, or a patient leaving recovery below Aldrete 9.
	// ==========================================================================================
	public class HospitalAnaesthesiaTests
	{
		private static readonly DateTime Now = new(2026, 11, 19, 10, 0, 0);

		private static HospitalAnaesthesia Rec(double solidsH = 8, double clearH = 3, bool emergency = false, string? why = null) => new()
		{
			Asa = 2, Emergency = emergency, FastingOverride = why, Technique = HospitalAnaesthesiaTechnique.General, AssessedAt = Now.AddHours(-1),
			LastSolidsAt = Now.AddHours(-solidsH), LastClearAt = Now.AddHours(-clearH),
		};

		[Fact]
		public void Induction_needs_the_assessment_and_complete_fasting()
		{
			Assert.Equal(HospitalAnaesthesiaRules.NoAssessment, HospitalAnaesthesiaRules.StartRefusal(null, Now)!.Value.Refusal);
			Assert.Null(HospitalAnaesthesiaRules.StartRefusal(Rec(), Now));
			var solids = HospitalAnaesthesiaRules.StartRefusal(Rec(solidsH: 2.5), Now)!.Value;
			Assert.Equal(("Fasting is not complete: solids {0} h ago, 6 h needed", "2.5"), (solids.Refusal, (string)solids.Args[0]));
			Assert.Equal("Fasting is not complete: clear fluids {0} h ago, 2 h needed", HospitalAnaesthesiaRules.StartRefusal(Rec(clearH: 1), Now)!.Value.Refusal);
			// breast milk 4 h (an infant)
			var infant = Rec(); infant.LastMilkAt = Now.AddHours(-3);
			Assert.Equal("Fasting is not complete: breast milk {0} h ago, 4 h needed", HospitalAnaesthesiaRules.StartRefusal(infant, Now)!.Value.Refusal);
			// an emergency starts only on its written reason
			Assert.NotNull(HospitalAnaesthesiaRules.StartRefusal(Rec(solidsH: 1, emergency: true), Now));
			Assert.Null(HospitalAnaesthesiaRules.StartRefusal(Rec(solidsH: 1, emergency: true, why: "Ruptured ectopic, haemodynamically unstable"), Now));
			// exactly at the limit is complete
			Assert.Null(HospitalAnaesthesiaRules.FastingShortfall(Now.AddHours(-6), null, Now.AddHours(-2), Now));
		}

		[Fact]
		public void The_assessment_is_checked_and_the_airway_flagged()
		{
			var ok = new HospitalAnaesthesiaInput { Asa = 2, Technique = HospitalAnaesthesiaTechnique.Spinal, LastSolidsAt = Now.AddHours(-8), LastClearAt = Now.AddHours(-3) };
			Assert.Null(HospitalAnaesthesiaRules.AssessmentRefusal(ok, Now));
			Assert.Equal("Choose the ASA class (1 to 6)", HospitalAnaesthesiaRules.AssessmentRefusal(new() { Asa = 7, Technique = "General" }, Now));
			Assert.Equal("Choose the planned technique", HospitalAnaesthesiaRules.AssessmentRefusal(new() { Asa = 1, Technique = "Hypnosis" }, Now));
			Assert.Equal("Record when the patient last ate and drank", HospitalAnaesthesiaRules.AssessmentRefusal(new() { Asa = 1, Technique = "General" }, Now));
			Assert.Equal("A fasting time cannot be in the future", HospitalAnaesthesiaRules.AssessmentRefusal(new() { Asa = 1, Technique = "General", LastSolidsAt = Now.AddHours(1), LastClearAt = Now }, Now));
			Assert.Equal("Only an emergency may start before fasting is complete", HospitalAnaesthesiaRules.AssessmentRefusal(new() { Asa = 1, Technique = "General", LastSolidsAt = Now, LastClearAt = Now, FastingOverride = "late" }, Now));
			Assert.Null(HospitalAnaesthesiaRules.AssessmentRefusal(new() { Asa = 4, Emergency = true, Technique = "General", FastingOverride = "Trauma" }, Now));
			Assert.True(HospitalAnaesthesiaRules.DifficultAirway(3, null, null));
			Assert.True(HospitalAnaesthesiaRules.DifficultAirway(1, 2.5m, null));
			Assert.True(HospitalAnaesthesiaRules.DifficultAirway(null, null, 5.5m));
			Assert.False(HospitalAnaesthesiaRules.DifficultAirway(2, 4m, 7m));
		}

		[Fact]
		public void The_record_takes_each_milestone_once_and_sane_readings()
		{
			var a = Rec();
			var ev = new HospitalAnaesthesiaEntryInput { Kind = HospitalAnaesthesiaEntryKind.Event, Code = HospitalAnaesthesiaEvent.Induction };
			Assert.Equal("Milestones are recorded once the operation has started", HospitalAnaesthesiaRules.EntryRefusal(ev, HospitalSurgeryStatus.Planned, a, Array.Empty<string>(), Now, Now));
			Assert.Null(HospitalAnaesthesiaRules.EntryRefusal(ev, HospitalSurgeryStatus.InProgress, a, Array.Empty<string>(), Now, Now));
			Assert.Equal("This milestone is already recorded", HospitalAnaesthesiaRules.EntryRefusal(ev, HospitalSurgeryStatus.InProgress, a, new[] { "Induction" }, Now, Now));
			Assert.Equal("Enter the dose", HospitalAnaesthesiaRules.EntryRefusal(new() { Kind = "Drug", Code = "Propofol", Unit = "mg" }, "InProgress", a, Array.Empty<string>(), Now, Now));
			Assert.Null(HospitalAnaesthesiaRules.EntryRefusal(new() { Kind = "Drug", Code = "Propofol", Dose = 150, Unit = "mg", Route = "IV" }, "InProgress", a, Array.Empty<string>(), Now, Now));
			Assert.Null(HospitalAnaesthesiaRules.EntryRefusal(new() { Kind = "Vitals", EtCo2 = 38 }, "InProgress", a, Array.Empty<string>(), Now, Now));
			Assert.Equal("Enter both pressures", HospitalAnaesthesiaRules.EntryRefusal(new() { Kind = "Vitals", SystolicBp = 120 }, "InProgress", a, Array.Empty<string>(), Now, Now));
			Assert.Equal("SpO₂ must be 50 to 100%", HospitalAnaesthesiaRules.EntryRefusal(new() { Kind = "Vitals", SpO2 = 30 }, "InProgress", a, Array.Empty<string>(), Now, Now));
			Assert.Equal("The time cannot be in the future", HospitalAnaesthesiaRules.EntryRefusal(new() { Kind = "Vitals", HeartRate = 80 }, "InProgress", a, Array.Empty<string>(), Now.AddHours(1), Now));
		}

		[Fact]
		public void Recovery_is_left_only_at_Aldrete_nine()
		{
			Assert.Equal((10, (string?)null), HospitalAnaesthesiaRules.Aldrete(new[] { 2, 2, 2, 2, 2 }));
			Assert.NotNull(HospitalAnaesthesiaRules.Aldrete(new[] { 2, 2, 3, 2, 2 }).Refusal);
			Assert.NotNull(HospitalAnaesthesiaRules.Aldrete(new[] { 2, 2 }).Refusal);
			var a = Rec();
			Assert.Equal("The patient has not arrived in recovery", HospitalAnaesthesiaRules.PacuOutRefusal(a, 10));
			Assert.Equal("Record the arrival in recovery first", HospitalAnaesthesiaRules.EntryRefusal(new() { Kind = "Aldrete", Items = new() { 2, 2, 2, 2, 2 } }, "Done", a, Array.Empty<string>(), Now, Now));
			a.PacuInAt = Now;
			Assert.Equal("Score Aldrete before the patient leaves recovery", HospitalAnaesthesiaRules.PacuOutRefusal(a, null));
			Assert.Equal("Aldrete {0}: 9 or more is needed to leave recovery", HospitalAnaesthesiaRules.PacuOutRefusal(a, 8));
			Assert.Null(HospitalAnaesthesiaRules.PacuOutRefusal(a, 9));
			a.PacuOutAt = Now;
			Assert.Equal("The record closed when the patient left recovery", HospitalAnaesthesiaRules.EntryRefusal(new() { Kind = "Vitals", HeartRate = 80 }, "Done", a, Array.Empty<string>(), Now, Now));
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var model = HospitalSliceParity.Model(HospitalAnaesthesiaModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h202_anaesthesia.sql");
			foreach (var (t, n) in new[] { (typeof(HospitalAnaesthesia), "HospitalAnaesthesias"), (typeof(HospitalAnaesthesiaEntry), "HospitalAnaesthesiaEntries") })
			{
				HospitalSliceParity.NamesItsTable(model, s, t, n);
				HospitalSliceParity.ColumnsMatch(model, s, t, n);
			}
			foreach (var k in HospitalAnaesthesiaEntryKind.All) Assert.Contains("'" + k + "'", s);
			foreach (var k in HospitalAnaesthesiaTechnique.All) Assert.Contains("'" + k + "'", s);
		}
	}
}
