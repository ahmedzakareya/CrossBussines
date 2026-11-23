using System.Globalization;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H268 — the specialty pack. What must never happen: a concept the designer itself would refuse; a score
	// that sums a code the pack does not define; bands out of order; a field on a concept that is not there; a form with
	// no required field; a clinic the catalogue names but the pack does not create; an instrument whose total is wrong.
	// ==========================================================================================
	public class HospitalSpecialtyPackTests
	{
		private static readonly Dictionary<string, HospitalPackConcept> ByCode = HospitalSpecialtyPack.Concepts.ToDictionary(c => c.Code);

		[Fact]
		public void Every_concept_passes_the_designers_own_rules()
		{
			Assert.Equal(HospitalSpecialtyPack.Concepts.Count, ByCode.Count);   // no code twice
			foreach (var c in HospitalSpecialtyPack.Concepts)
			{
				var why = HospitalConceptRules.Refusal(new HospitalConceptInput { Code = c.Code, CodeSystem = c.System, Name = c.Ar, NameEn = c.En, ValueType = c.Type, Unit = c.Unit, Decimals = c.Decimals,
					NormalLow = c.NormalLow, NormalHigh = c.NormalHigh, Choices = c.Choices, Calculator = c.Calculator, CalcInputs = c.Inputs, LabAnalyteCode = c.Lab });
				Assert.True(why == null, c.Code + ": " + why);
				Assert.True((c.Unit ?? "").Length <= 30 && c.Ar.Length <= 150 && c.En.Length <= 150, c.Code);
			}
		}

		[Fact]
		public void Scores_sum_items_the_pack_defines_and_bands_rise()
		{
			foreach (var s in HospitalSpecialtyPack.Concepts.Where(c => c.Type == HospitalConceptType.Score))
			{
				foreach (var input in HospitalConceptRules.Inputs(s.Inputs)) Assert.True(ByCode.ContainsKey(input), s.Code + " reads " + input);
				var mins = HospitalConceptRules.Choices(s.Choices).Select(b => decimal.Parse(b.Code, CultureInfo.InvariantCulture)).ToList();
				Assert.Equal(0m, mins[0]);
				Assert.Equal(mins.OrderBy(x => x), mins);
			}
			// the instruments' totals: IPSS /35, CAT /40, MIDAS 5 items, CDAI 28+28+10+10
			Assert.Equal(7, HospitalConceptRules.Inputs(ByCode["LOCAL-IPSS"].Inputs).Count);
			Assert.Equal(35m, HospitalConceptRules.Inputs(ByCode["LOCAL-IPSS"].Inputs).Sum(i => ByCode[i].NormalHigh!.Value));
			Assert.Equal(40m, HospitalConceptRules.Inputs(ByCode["LOCAL-CAT"].Inputs).Sum(i => ByCode[i].NormalHigh!.Value));
			Assert.Equal(76m, HospitalConceptRules.Inputs(ByCode["LOCAL-CDAI"].Inputs).Sum(i => ByCode[i].NormalHigh!.Value));
			Assert.Equal(5, HospitalConceptRules.Inputs(ByCode["LOCAL-MIDAS"].Inputs).Count);
		}

		[Fact]
		public void Every_form_reads_pack_concepts_has_a_required_field_and_valid_alerts()
		{
			Assert.Equal(7, HospitalSpecialtyPack.Templates.Count);
			Assert.Equal(HospitalSpecialtyPack.Templates.Count, HospitalSpecialtyPack.Templates.Select(t => t.Code).Distinct().Count());
			foreach (var t in HospitalSpecialtyPack.Templates)
			{
				Assert.Null(HospitalTemplateRules.CreateRefusal(new HospitalTemplateInput { Code = t.Code, Name = t.Ar, NameEn = t.En }));
				Assert.Contains(t.Fields, f => f.Required);
				foreach (var f in t.Fields)
				{
					Assert.True(ByCode.ContainsKey(f.Concept), t.Code + " → " + f.Concept);
					var c = ByCode[f.Concept];
					var concept = new HospitalConcept { Code = c.Code, ValueType = c.Type, IsActive = true, LabAnalyteCode = c.Lab };
					var why = HospitalTemplateRules.FieldRefusal(new HospitalTemplateFieldInput { ConceptId = 1, Section = f.SectionAr, SectionEn = f.SectionEn, Required = f.Required,
						ValueSource = f.Source ?? HospitalValueSource.Manual, LookbackDays = f.Lookback, AlertAbove = f.AlertAbove, AlertBelow = f.AlertBelow, AlertText = f.AlertAr, AlertTextEn = f.AlertEn },
						concept, Array.Empty<HospitalClinicalTemplateField>(), null);
					Assert.True(why == null, t.Code + "/" + f.Concept + ": " + why);
				}
			}
		}

		[Fact]
		public void The_catalogue_and_the_pack_name_the_same_clinics_and_forms()
		{
			var packDepts = HospitalSpecialtyPack.Departments.Select(d => d.Code).Append("SURG").ToHashSet();
			foreach (var t in HospitalSpecialtyPack.Templates)
			{
				Assert.Contains(t.Dept, packDepts);
				Assert.Contains(HospitalSpecialtyCatalog.Others, e => e.Dept == t.Dept && e.TemplateCodes.Contains(t.Code));
			}
			Assert.DoesNotContain(HospitalSpecialtyCatalog.Others, e => e.Icon.Contains("cross"));   // the owner's rule: no cross symbol
		}
	}
}
