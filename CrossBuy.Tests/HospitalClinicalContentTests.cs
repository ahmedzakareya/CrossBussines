using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H93 — the clinical content platform, with no database: how the dictionary shows and flags a value,
	// what a concept and a field need, the calculators (BMI, eGFR CKD-EPI 2021, KDIGO), what a signature records
	// (required, conditional, per side, panels, choices, scores), and the six tables against their slice.
	// ==========================================================================================
	public class HospitalClinicalContentTests
	{
		private static HospitalConcept A1c() => new() { ID = 1, Code = "4548-4", Name = "السكر التراكمي", NameEn = "HbA1c", ValueType = HospitalConceptType.Number, Unit = "%", NormalLow = 4.0m, NormalHigh = 5.6m, CriticalHigh = 10m };
		private static HospitalConcept Bp() => new() { ID = 2, Code = "85354-9", Name = "ضغط الدم", NameEn = "Blood pressure", ValueType = HospitalConceptType.Panel, Unit = "mmHg", NormalHigh = 129m, NormalHigh2 = 79m, CriticalHigh = 180m, CriticalHigh2 = 120m, Decimals = 0 };
		private static HospitalTemplateFieldView F(int id, HospitalConcept c, bool req = false, int? when = null, decimal? above = null) =>
			new(new HospitalClinicalTemplateField { ID = id, ConceptId = c.ID, Section = "S", SectionEn = "S", Required = req, ShowWhenFieldId = when, ShowWhenAbove = above }, c, null);

		[Fact]
		public void The_dictionary_shows_its_limits_and_flags_a_value()
		{
			Assert.Equal("4–5.6", HospitalConceptRules.NormalDisplay(A1c(), true));
			Assert.Equal("≥ 10", HospitalConceptRules.CriticalDisplay(A1c(), false));
			Assert.Equal("≤ 129/79", HospitalConceptRules.NormalDisplay(Bp(), false));
			Assert.Equal("≥ 180/120", HospitalConceptRules.CriticalDisplay(Bp(), false));
			var w = new HospitalConcept { NormalText = "حسب العمر", NormalTextEn = "by age" };
			Assert.Equal("حسب العمر", HospitalConceptRules.NormalDisplay(w, true));
			Assert.Equal("by age", HospitalConceptRules.NormalDisplay(w, false));
			Assert.Equal(HospitalObsFlag.Normal, HospitalConceptRules.Flag(A1c(), 5.2m));
			Assert.Equal(HospitalObsFlag.High, HospitalConceptRules.Flag(A1c(), 8.3m));
			Assert.Equal(HospitalObsFlag.CriticalHigh, HospitalConceptRules.Flag(A1c(), 10m));
			Assert.Equal(HospitalObsFlag.Low, HospitalConceptRules.Flag(A1c(), 3.5m));
			// a panel is flagged on EITHER number: 128/92 is high on the diastolic
			Assert.Equal(HospitalObsFlag.High, HospitalConceptRules.Flag(Bp(), 128m, 92m));
			Assert.Equal(HospitalObsFlag.CriticalHigh, HospitalConceptRules.Flag(Bp(), 150m, 125m));
		}

		[Fact]
		public void A_concept_needs_its_code_names_type_and_coherent_limits()
		{
			HospitalConceptInput Ok() => new() { Code = "4548-4", CodeSystem = "LOINC", Name = "السكر التراكمي", NameEn = "HbA1c", ValueType = "Number", NormalLow = 4, NormalHigh = 5.6m, CriticalHigh = 10 };
			Assert.Null(HospitalConceptRules.Refusal(Ok()));
			var i = Ok(); i.CodeSystem = "ICD"; Assert.Equal("Choose the code system", HospitalConceptRules.Refusal(i));
			i = Ok(); i.NameEn = ""; Assert.Equal("Write the name, in Arabic and in English", HospitalConceptRules.Refusal(i));
			i = Ok(); i.NormalLow = 7; Assert.Equal("The normal low is above the normal high", HospitalConceptRules.Refusal(i));
			i = Ok(); i.CriticalHigh = 5; Assert.Equal("The critical high must be at or above the normal range", HospitalConceptRules.Refusal(i));
			i = Ok(); i.ValueType = "Choice"; i.Choices = "N|طبيعي|Normal"; Assert.Equal("A choice needs at least two answers", HospitalConceptRules.Refusal(i));
			i.Choices = "N|طبيعي|Normal\nR|ناقص|Reduced"; Assert.Null(HospitalConceptRules.Refusal(i));
			i = Ok(); i.ValueType = "Calculated"; Assert.Equal("Choose the calculator", HospitalConceptRules.Refusal(i));
			i.Calculator = "Kdigo"; Assert.Equal("List the codes the calculator reads", HospitalConceptRules.Refusal(i));
			i.CalcInputs = "62238-1, 9318-7"; Assert.Null(HospitalConceptRules.Refusal(i));
			i = Ok(); i.ValueType = "Score"; i.Calculator = "Bmi"; i.CalcInputs = "x"; Assert.Equal("A score is the sum of its items: choose Sum", HospitalConceptRules.Refusal(i));
			i.Calculator = null; i.CalcInputs = null; Assert.Null(HospitalConceptRules.Refusal(i));   // a typed total (the ACT)
			Assert.Equal(new[] { ("N", "طبيعي", "Normal"), ("R", "ناقص", "Reduced") }, HospitalConceptRules.Choices("N|طبيعي|Normal\n R | ناقص | Reduced \n"));
		}

		[Fact]
		public void The_calculators_match_their_published_formulas()
		{
			Assert.Equal(31.8m, HospitalClinicalCalc.Bmi(94, 172));
			Assert.Null(HospitalClinicalCalc.Bmi(94, null));
			// CKD-EPI 2021 (race-free): a 50-year-old man with creatinine 1.0 ≈ 92; a 60-year-old woman with 1.2 ≈ 52
			Assert.InRange(HospitalClinicalCalc.EgfrCkdEpi2021(1.0m, 50, false)!.Value, 91m, 92m);
			Assert.InRange(HospitalClinicalCalc.EgfrCkdEpi2021(1.2m, 60, true)!.Value, 51m, 53m);
			Assert.Null(HospitalClinicalCalc.EgfrCkdEpi2021(1.0m, 12, false));   // not for children
			Assert.Equal("G3a", HospitalClinicalCalc.GCategory(47)); Assert.Equal("G1", HospitalClinicalCalc.GCategory(90)); Assert.Equal("G5", HospitalClinicalCalc.GCategory(14));
			Assert.Equal("A2", HospitalClinicalCalc.ACategory(120)); Assert.Equal("A1", HospitalClinicalCalc.ACategory(29)); Assert.Equal("A3", HospitalClinicalCalc.ACategory(301));
			// the KDIGO 2024 heat map
			Assert.Equal("Low", HospitalClinicalCalc.KdigoRisk("G2", "A1"));
			Assert.Equal("Moderate", HospitalClinicalCalc.KdigoRisk("G1", "A2"));
			Assert.Equal("High", HospitalClinicalCalc.KdigoRisk("G3a", "A2"));
			Assert.Equal("VeryHigh", HospitalClinicalCalc.KdigoRisk("G3b", "A2"));
			Assert.Equal("VeryHigh", HospitalClinicalCalc.KdigoRisk("G4", "A1"));
			Assert.Equal(39, HospitalClinicalCalc.AgeOn(new DateTime(1986, 11, 12), new DateTime(2026, 11, 11)));   // the birthday is tomorrow
			Assert.Equal(40, HospitalClinicalCalc.AgeOn(new DateTime(1986, 11, 12), new DateTime(2026, 11, 12)));
		}

		[Fact]
		public void A_signature_records_the_visible_values_per_side_and_refuses_a_missing_required_one()
		{
			var hypo = new HospitalConcept { ID = 3, Code = "LOCAL-HYPO", Name = "نوبات هبوط", NameEn = "Hypo episodes", ValueType = HospitalConceptType.Number, Decimals = 0 };
			var sev = new HospitalConcept { ID = 4, Code = "LOCAL-HYPO-SEV", Name = "شدة الهبوط", NameEn = "Hypo severity", ValueType = HospitalConceptType.Choice, Choices = "M|خفيف|Mild\nS|شديد|Severe" };
			var mono = new HospitalConcept { ID = 5, Code = "401191002", Name = "الخيط الأحادي", NameEn = "Monofilament", ValueType = HospitalConceptType.Choice, Choices = "N|طبيعي|Normal\nR|ناقص|Reduced", HasLaterality = true };
			var fields = new[] { F(10, A1c(), req: true), F(11, hypo), F(12, sev, req: true, when: 11, above: 0), F(13, mono), F(14, Bp()) };

			// the severity is required, but only when hypo > 0: with no episodes it is hidden — neither required nor recorded
			var ans = new Dictionary<string, string> { ["10"] = "8.3", ["11"] = "0", ["13.R"] = "N", ["13.L"] = "R", ["14"] = "148/92" };
			var (vals, err, _) = HospitalFormRules.Read(fields, ans, true);
			Assert.Null(err);
			Assert.Equal(5, vals.Count);   // A1c, hypo, monofilament right AND left, BP
			Assert.Contains(vals, v => v.Concept.ID == 5 && v.Side == "L" && v.Code == "R");
			Assert.Contains(vals, v => v.Concept.ID == 2 && v.Value == 148 && v.Value2 == 92);

			// one episode: the severity shows and is required
			ans["11"] = "1";
			(_, err, var args) = HospitalFormRules.Read(fields, ans, true);
			Assert.Equal("Fill the required field: {0}", err); Assert.Equal("Hypo severity", args![0]);
			ans["12"] = "X";
			(_, err, _) = HospitalFormRules.Read(fields, ans, true);
			Assert.Equal("{0}: Choose one of the answers", err);
			ans["12"] = "S"; ans["14"] = "148"; (_, err, _) = HospitalFormRules.Read(fields, ans, true);
			Assert.Equal("{0}: Write it as two numbers: 120/80", err);
			ans.Remove("10"); ans["14"] = "148/92"; (_, err, args) = HospitalFormRules.Read(fields, ans, false);
			Assert.Equal("Fill the required field: {0}", err); Assert.Equal("السكر التراكمي", args![0]);
		}

		[Fact]
		public void Calculated_concepts_are_computed_at_sign_off_never_typed()
		{
			var egfr = new HospitalConcept { ID = 20, Code = "62238-1", NameEn = "eGFR", ValueType = HospitalConceptType.Calculated, Calculator = "EgfrCkdEpi2021", CalcInputs = "2160-0" };
			var acr = new HospitalConcept { ID = 21, Code = "9318-7", NameEn = "ACR", ValueType = HospitalConceptType.Number };
			var kd = new HospitalConcept { ID = 22, Code = "CALC-KDIGO", NameEn = "KDIGO", ValueType = HospitalConceptType.Calculated, Calculator = "Kdigo", CalcInputs = "62238-1, 9318-7" };
			var q1 = new HospitalConcept { ID = 30, Code = "Q1", ValueType = HospitalConceptType.Number, Decimals = 0 };
			var q2 = new HospitalConcept { ID = 31, Code = "Q2", ValueType = HospitalConceptType.Number, Decimals = 0 };
			var sum = new HospitalConcept { ID = 32, Code = "PHQ", NameEn = "PHQ", ValueType = HospitalConceptType.Score, Calculator = "Sum", CalcInputs = "Q1,Q2" };
			var fields = new[] { F(1, acr), F(2, egfr, req: true), F(3, kd), F(4, q1), F(5, q2), F(6, sum, req: true) };
			// typing into a calculated field is ignored; a required calculated field does not block the signature
			var (vals, err, _) = HospitalFormRules.Read(fields, new Dictionary<string, string> { ["1"] = "120", ["2"] = "999", ["4"] = "2", ["5"] = "3" }, false);
			Assert.Null(err);
			Assert.DoesNotContain(vals, v => v.Concept.ID == 20);
			// the creatinine is not on the form: it comes from the patient's latest value
			var made = HospitalFormRules.Compute(fields, vals, 60, true, new Dictionary<string, decimal> { ["2160-0"] = 1.2m });
			var e = made.Single(m => m.Concept.ID == 20).Value!.Value;
			Assert.InRange(e, 51m, 53m);
			Assert.Equal("G3a A2", made.Single(m => m.Concept.ID == 22).Code);
			Assert.Equal(5m, made.Single(m => m.Concept.ID == 32).Value);
			// a missing item leaves the score out rather than summing a partial one
			var (v2, _, _) = HospitalFormRules.Read(fields, new Dictionary<string, string> { ["4"] = "2" }, false);
			Assert.DoesNotContain(HospitalFormRules.Compute(fields, v2, 60, true, new Dictionary<string, decimal>()), m => m.Concept.ID == 32);
		}

		[Fact]
		public void A_field_needs_a_dictionary_concept_a_section_and_a_coherent_rule()
		{
			var c = A1c(); var sib = new List<HospitalClinicalTemplateField> { new() { ID = 7 } };
			HospitalTemplateFieldInput Ok() => new() { ConceptId = 1, Section = "المراجعة", SectionEn = "Review", ValueSource = "Manual" };
			Assert.Null(HospitalTemplateRules.FieldRefusal(Ok(), c, sib, null));
			Assert.Equal("Choose a concept from the dictionary", HospitalTemplateRules.FieldRefusal(Ok(), null, sib, null));
			var i = Ok(); i.ValueSource = "LastLab"; Assert.Equal("This concept is not linked to a lab test", HospitalTemplateRules.FieldRefusal(i, c, sib, null));
			c.LabAnalyteCode = "HBA1C"; Assert.Null(HospitalTemplateRules.FieldRefusal(i, c, sib, null));
			i = Ok(); i.ShowWhenFieldId = 99; i.ShowWhenAbove = 0; Assert.Equal("A field can only depend on another field of the same template", HospitalTemplateRules.FieldRefusal(i, c, sib, null));
			i.ShowWhenFieldId = 7; Assert.Null(HospitalTemplateRules.FieldRefusal(i, c, sib, null));
			Assert.Equal("A field can only depend on another field of the same template", HospitalTemplateRules.FieldRefusal(i, c, sib, 7));
			i = Ok(); i.AlertAbove = 8; Assert.Equal("Write the alert, in Arabic and in English", HospitalTemplateRules.FieldRefusal(i, c, sib, null));
			i.AlertText = "فوق الهدف"; i.AlertTextEn = "Above target"; Assert.Null(HospitalTemplateRules.FieldRefusal(i, c, sib, null));
			i.AlertBelow = 9; Assert.Equal("The low alert must be below the high alert", HospitalTemplateRules.FieldRefusal(i, c, sib, null));
			i = Ok(); i.CareGapDays = 0; Assert.Equal("Days are between 1 and 3650", HospitalTemplateRules.FieldRefusal(i, c, sib, null));
		}

		[Fact]
		public void The_tables_match_their_slice_and_follow_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h93_clinical_content.sql");
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h94_specialty_visit.sql");   // H94 adds two columns
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h97_allergy.sql");   // H97: the IgeClass calculator
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h106_field_alert_escalation.sql");   // H106: the alert notifies, opens a template
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h110_canvas_results.sql");   // H110: the canvas source
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h112_patient_pain.sql");   // H112: the patient's own report
			var m = HospitalSliceParity.Model(HospitalClinicalContentModel.Configure);
			foreach (var (t, n) in new (Type, string)[] { (typeof(HospitalConcept), "HospitalConcepts"), (typeof(HospitalClinicalTemplate), "HospitalClinicalTemplates"),
				(typeof(HospitalClinicalTemplateVersion), "HospitalClinicalTemplateVersions"), (typeof(HospitalClinicalTemplateField), "HospitalClinicalTemplateFields"),
				(typeof(HospitalClinicalForm), "HospitalClinicalForms"), (typeof(HospitalClinicalObservation), "HospitalClinicalObservations") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
			}
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalClinicalForm));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalClinicalObservation));
			foreach (var x in HospitalConceptType.All.Concat(HospitalCodeSystem.All).Concat(HospitalConceptCalculator.All).Concat(HospitalTemplateStatus.All)
				.Concat(HospitalValueSource.All).Concat(HospitalFormStatus.All).Concat(HospitalObsFlag.All).Concat(HospitalObsSource.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalDepartments", "HospitalEncounters", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x).ToArray());
		}
	}
}
