using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H94 — the specialty visit, with no database: which order sets a visit's diagnoses propose, how chosen
	// lines join the consultation's draft (once each), the problem list, the red "out of range" field, and the three
	// new columns against their slices.
	// ==========================================================================================
	public class HospitalSpecialtyVisitTests
	{
		[Fact]
		public void Diagnosis_prefixes_are_read_and_matched_by_prefix()
		{
			Assert.Equal(("E11, I10", (string?)null, (string?)null), HospitalOrderSetRules.DiagnosisCodes(" e11 ,I10، e11 "));
			Assert.Equal((null, null, null), HospitalOrderSetRules.DiagnosisCodes("  "));
			var (_, err, bad) = HospitalOrderSetRules.DiagnosisCodes("E11, diabetes");
			Assert.Equal("Not a diagnosis code: {0}", err); Assert.Equal("DIABETES", bad);
			Assert.True(HospitalOrderSetRules.Proposes("E11, I10", new[] { "E11.21" }));
			Assert.True(HospitalOrderSetRules.Proposes("E11.2", new[] { "J45", "e11.21" }));
			Assert.False(HospitalOrderSetRules.Proposes("E11.2", new[] { "E11.9" }));
			Assert.False(HospitalOrderSetRules.Proposes(null, new[] { "E11" }));
		}

		[Fact]
		public void Order_set_lines_join_the_draft_once_each()
		{
			var input = new HospitalEncounterInput { OrderServiceIds = new() { 7 }, PlanNote = "Diet advice", Prescription = new() { new HospitalPrescriptionInput { Drug = "Metformin 500 mg" } } };
			var items = new[]
			{
				new HospitalOrderSetItem { Kind = HospitalOrderSetItemKind.Lab, ServiceId = 7 },            // already ordered
				new HospitalOrderSetItem { Kind = HospitalOrderSetItemKind.Lab, ServiceId = 9 },
				new HospitalOrderSetItem { Kind = HospitalOrderSetItemKind.Imaging, ServiceId = 12 },
				new HospitalOrderSetItem { Kind = HospitalOrderSetItemKind.Medication, Drug = "metformin 500 mg", Dose = "1 tab" },   // already prescribed
				new HospitalOrderSetItem { Kind = HospitalOrderSetItemKind.Medication, Drug = "Empagliflozin 10 mg", Dose = "1 tab", Route = "PO", Hours = "08" },
				new HospitalOrderSetItem { Kind = HospitalOrderSetItemKind.Task, TitleAr = "تحويل عيون — قاع العين", TitleEn = "Eye referral — fundus" },
			};
			Assert.Equal(4, HospitalVisitRules.Apply(input, items));
			Assert.Equal(new[] { 7, 9, 12 }, input.OrderServiceIds);
			Assert.Equal(2, input.Prescription.Count);
			Assert.Equal("1 tab PO 08", input.Prescription[1].Instructions);
			Assert.Equal("Diet advice\n• تحويل عيون — قاع العين", input.PlanNote);
			Assert.Equal(0, HospitalVisitRules.Apply(input, items));   // a second press adds nothing
		}

		[Fact]
		public void The_consultation_draft_round_trips_into_its_own_input()
		{
			var v = new HospitalEncounterView(new HospitalEncounter { ChiefComplaint = "Follow-up", BpSystolic = 148, BpDiastolic = 92, FollowUpDays = 90 },
				new[] { new HospitalEncounterDiagnosis { Code = "E11.21", IsPrimary = true }, new HospitalEncounterDiagnosis { Code = "I10" } },
				new[] { new HospitalPrescriptionLine { Drug = "Metformin", ItemId = 5, Instructions = "1x2", Quantity = 60 } },
				new[] { new HospitalEncounterOrder { ServiceId = 3 } });
			var i = HospitalVisitRules.InputOf(v);
			Assert.Equal("Follow-up", i.ChiefComplaint); Assert.Equal(148, i.BpSystolic); Assert.Equal(90, i.FollowUpDays);
			Assert.Equal(new[] { ("E11.21", true), ("I10", false) }, i.Diagnoses.Select(d => (d.Code, d.Primary)));
			Assert.Equal(5, i.Prescription.Single().ItemId); Assert.Equal(60, i.Prescription.Single().Quantity);
			Assert.Equal(new[] { 3 }, i.OrderServiceIds);
		}

		[Fact]
		public void A_score_falls_in_its_band()
		{
			var phq = new HospitalConcept { ValueType = HospitalConceptType.Score, Choices = "0|لا يوجد|None\n5|خفيف|Mild\n10|متوسط|Moderate\n15|متوسط إلى شديد|Moderately severe\n20|شديد|Severe" };
			Assert.Equal("None", HospitalConceptRules.Band(phq, 4)!.Value.En);
			Assert.Equal("Mild", HospitalConceptRules.Band(phq, 5)!.Value.En);
			Assert.Equal("Moderately severe", HospitalConceptRules.Band(phq, 19)!.Value.En);
			Assert.Equal("شديد", HospitalConceptRules.Band(phq, 27)!.Value.Ar);
			Assert.Null(HospitalConceptRules.Band(phq, null));
			Assert.Null(HospitalConceptRules.Band(new HospitalConcept { ValueType = HospitalConceptType.Number, Choices = "0|x|y" }, 3));   // only a score has bands
			var i = new HospitalConceptInput { Code = "PHQ", CodeSystem = "LOINC", Name = "س", NameEn = "x", ValueType = "Score", Calculator = "Sum", CalcInputs = "Q1", Choices = "low|منخفض|Low" };
			Assert.Equal("A score's band starts with the lowest value of the band", HospitalConceptRules.Refusal(i));
			Assert.Equal(7, HospitalSpecialtyCatalog.Others.Count);   // the prototype's seven
		}

		[Fact]
		public void Every_field_of_the_consultation_input_survives_the_rebuild()
		{
			// InputOf rebuilds the consultation's input from its saved draft (an order set, a gap's test, the one signature
			// all go through it): a field added to HospitalEncounterInput and not copied there would silently vanish.
			// Every property of the input must be set by InputOf from a draft that holds a value in each.
			var e = new HospitalEncounter { ChiefComplaint = "c", History = "h", Examination = "x", PlanNote = "p", BpSystolic = 120, BpDiastolic = 80, HeartRate = 70, TemperatureC = 37m,
				SpO2 = 98, WeightKg = 70m, HeightCm = 170m, FollowUpDays = 30, ReferToDepartmentId = 2 };
			var i = HospitalVisitRules.InputOf(new HospitalEncounterView(e, new[] { new HospitalEncounterDiagnosis { Code = "I10", IsPrimary = true } },
				new[] { new HospitalPrescriptionLine { Drug = "d", Quantity = 1 } }, new[] { new HospitalEncounterOrder { ServiceId = 1 } }));
			foreach (var prop in typeof(HospitalEncounterInput).GetProperties())
			{
				var v = prop.GetValue(i);
				Assert.True(v != null && !(v is System.Collections.ICollection col && col.Count == 0), "InputOf does not copy HospitalEncounterInput." + prop.Name);
			}
		}

		[Fact]
		public void Problems_are_added_once_and_out_of_range_reads_the_concept()
		{
			Assert.Equal("Hypertension\nE11.21 Diabetes with nephropathy", HospitalVisitRules.WithProblems("Hypertension", new[] { "E11.21 Diabetes with nephropathy", "hypertension" }));
			Assert.Null(HospitalVisitRules.WithProblems(null, new[] { " " }));
			var a1c = new HospitalConcept { ValueType = HospitalConceptType.Number, NormalLow = 4, NormalHigh = 5.6m, Decimals = 1 };
			var bp = new HospitalConcept { ValueType = HospitalConceptType.Panel, NormalHigh = 129, NormalHigh2 = 79 };
			Assert.True(HospitalVisitRules.OutOfRange(a1c, "8.3")); Assert.False(HospitalVisitRules.OutOfRange(a1c, "5.2"));
			Assert.False(HospitalVisitRules.OutOfRange(a1c, "")); Assert.False(HospitalVisitRules.OutOfRange(a1c, "x"));   // unreadable is the signature's to refuse, not "red"
			Assert.True(HospitalVisitRules.OutOfRange(bp, "128/92")); Assert.False(HospitalVisitRules.OutOfRange(bp, "120/78"));
			Assert.Equal("kdigo:High", HospitalVisitRules.AdviceKey("High"));
		}

		[Fact]
		public void A_value_with_nothing_to_compare_against_is_unknown_never_normal()
		{
			// TARGET's review: 9 of the 19 concepts carry no limits — their values must not be stored as "normal"
			var bare = new HospitalConcept { ValueType = HospitalConceptType.Number };
			Assert.Equal(HospitalObsFlag.Unknown, HospitalConceptRules.Flag(bare, 42));
			Assert.Equal(HospitalObsFlag.Unknown, HospitalConceptRules.Flag(new HospitalConcept { NormalHigh = 5 }, null));
			Assert.False(HospitalObsFlag.Abnormal(HospitalObsFlag.Unknown)); Assert.False(HospitalObsFlag.Abnormal(HospitalObsFlag.Normal));
			Assert.True(HospitalObsFlag.Abnormal(HospitalObsFlag.CriticalLow));
			Assert.False(HospitalVisitRules.OutOfRange(bare, "42"));   // not red: unknown is not abnormal either
			Assert.Contains("'U'", HospitalSliceParity.Slice("hospital_h94_specialty_visit.sql"));
		}

		[Fact]
		public void KDIGO_reads_this_visits_eGFR_whatever_the_field_order()
		{
			// found live: KDIGO sat before eGFR in the form and took the PREVIOUS visit's eGFR (G2 while today's was G3a)
			HospitalTemplateFieldView F(int id, HospitalConcept c) => new(new HospitalClinicalTemplateField { ID = id, ConceptId = c.ID }, c, null);
			var scr = new HospitalConcept { ID = 1, Code = "2160-0", ValueType = HospitalConceptType.Number, Decimals = 2 };
			var acr = new HospitalConcept { ID = 2, Code = "9318-7", ValueType = HospitalConceptType.Number };
			var egfr = new HospitalConcept { ID = 3, Code = "62238-1", ValueType = HospitalConceptType.Calculated, Calculator = "EgfrCkdEpi2021", CalcInputs = "2160-0" };
			var kd = new HospitalConcept { ID = 4, Code = "CALC-KDIGO", ValueType = HospitalConceptType.Calculated, Calculator = "Kdigo", CalcInputs = "62238-1, 9318-7" };
			var fields = new[] { F(1, kd), F(2, scr), F(3, acr), F(4, egfr) };   // KDIGO FIRST
			var (vals, err, _) = HospitalFormRules.Read(fields, new Dictionary<string, string> { ["2"] = "1.5", ["3"] = "120" }, false);
			Assert.Null(err);
			var made = HospitalFormRules.Compute(fields, vals, 68, false, new Dictionary<string, decimal> { ["62238-1"] = 90 });   // a stale eGFR from before
			Assert.Equal(50m, made.Single(m => m.Concept.ID == 3).Value);
			Assert.Equal("G3a A2", made.Single(m => m.Concept.ID == 4).Code);
		}

		[Fact]
		public void The_new_columns_match_their_slices()
		{
			var s93 = HospitalSliceParity.Slice("hospital_h93_clinical_content.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h94_specialty_visit.sql");
			var m = HospitalSliceParity.Model(HospitalClinicalContentModel.Configure);
			HospitalSliceParity.ColumnsMatch(m, s93, typeof(HospitalClinicalTemplateVersion), "HospitalClinicalTemplateVersions");
			HospitalSliceParity.ColumnsMatch(m, s93, typeof(HospitalClinicalTemplateField), "HospitalClinicalTemplateFields");
			var s74 = HospitalSliceParity.Slice("hospital_h74_order_sets.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h75_inpatient_orders.sql") + Environment.NewLine + HospitalSliceParity.Slice("hospital_h94_specialty_visit.sql");
			HospitalSliceParity.ColumnsMatch(HospitalSliceParity.Model(HospitalOrderSetModel.Configure), s74, typeof(HospitalOrderSet), "HospitalOrderSets");
		}
	}
}
