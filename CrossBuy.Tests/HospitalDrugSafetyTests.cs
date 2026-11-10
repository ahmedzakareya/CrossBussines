using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H73 — drug interactions and dose limits, with no database: a pair is matched whichever way round it is
	// written, a written dose is read and converted (g ↔ mg ↔ mcg, never mg ↔ units), a single dose and a day's doses are
	// held to their limits, what the knowledge base refuses, and the tables against their slice.
	// ==========================================================================================
	public class HospitalDrugSafetyTests
	{
		private static HospitalDrugInteraction P(string a, string b, string sev) { var (x, y) = HospitalDrugSafetyRules.Pair(a, b); return new() { DrugA = x, DrugB = y, Severity = sev, IsActive = true, EffectAr = "x", EffectEn = "x" }; }

		[Fact]
		public void A_pair_is_stored_once_and_matched_either_way_round()
		{
			Assert.Equal(("aspirin", "warfarin"), HospitalDrugSafetyRules.Pair(" Warfarin ", "ASPIRIN"));
			var kb = new[] { P("warfarin", "aspirin", HospitalInteractionSeverity.Major), P("sildenafil", "nitroglycerin", HospitalInteractionSeverity.Contraindicated), P("lisinopril", "spironolactone", HospitalInteractionSeverity.Moderate) };
			var hits = HospitalDrugSafetyRules.Interactions("Aspirin 81 mg tab", new[] { "Warfarin 5 mg", "Paracetamol" }, kb);
			Assert.Single(hits);
			Assert.Equal("Warfarin 5 mg", hits[0].Other);
			Assert.True(HospitalInteractionSeverity.Blocks(hits[0].Hit.Severity));
			// the more severe first; an inactive rule never matches
			var two = HospitalDrugSafetyRules.Interactions("Nitroglycerin SL", new[] { "Sildenafil 50", "x" }, kb.Append(new HospitalDrugInteraction { DrugA = "nitroglycerin", DrugB = "x-off", Severity = "Major", IsActive = false }));
			Assert.Equal(HospitalInteractionSeverity.Contraindicated, two[0].Hit.Severity);
			Assert.False(HospitalInteractionSeverity.Blocks(HospitalInteractionSeverity.Moderate));
			// a word under three letters matches nothing
			Assert.False(HospitalDrugSafetyRules.Mentions("anything", "an"));
		}

		[Theory]
		[InlineData("500 mg", 500, "mg")] [InlineData("1g", 1, "g")] [InlineData("1.5 g", 1.5, "g")] [InlineData("250mcg", 250, "mcg")] [InlineData("10 units", 10, "units")]
		[InlineData("5 ml", 5, "ml")] [InlineData("2,5 mg", 2.5, "mg")] [InlineData("0.4 IU", 0.4, "units")]
		public void A_written_dose_is_read(string text, double amount, string unit)
		{
			var d = HospitalDrugSafetyRules.ParseDose(text);
			Assert.NotNull(d);
			Assert.Equal((decimal)amount, d!.Value.Amount);
			Assert.Equal(unit, d.Value.Unit);
		}

		[Fact]
		public void An_unreadable_dose_and_unconvertible_units_check_nothing()
		{
			Assert.Null(HospitalDrugSafetyRules.ParseDose("one tablet"));
			Assert.Null(HospitalDrugSafetyRules.ParseDose(""));
			Assert.Equal(1000m, HospitalDrugSafetyRules.Convert(1, "g", "mg"));
			Assert.Equal(0.25m, HospitalDrugSafetyRules.Convert(250, "mcg", "mg"));
			Assert.Null(HospitalDrugSafetyRules.Convert(10, "units", "mg"));
			Assert.Null(HospitalDrugSafetyRules.Convert(5, "ml", "mg"));
		}

		[Fact]
		public void A_dose_is_held_to_its_single_and_daily_limit()
		{
			var paracetamol = new HospitalDoseLimit { Drug = "paracetamol", Unit = "mg", MaxSingle = 1000, MaxDaily = 4000, IsActive = true };
			var limits = new[] { paracetamol };
			Assert.Empty(HospitalDrugSafetyRules.DoseHits("Paracetamol", "1 g", "PO", 4, limits));
			var single = HospitalDrugSafetyRules.DoseHits("Paracetamol", "1.5 g", "PO", 1, limits);
			Assert.Single(single); Assert.False(single[0].Daily); Assert.Equal(1500m, single[0].Value);
			var daily = HospitalDrugSafetyRules.DoseHits("Paracetamol", "1 g", "PO", 6, limits);
			Assert.Single(daily); Assert.True(daily[0].Daily); Assert.Equal(6000m, daily[0].Value);
			// as needed: no day to add up
			Assert.Empty(HospitalDrugSafetyRules.DoseHits("Paracetamol", "1 g", "PO", null, limits));
			// a limit for one route leaves another alone
			var ivOnly = new[] { new HospitalDoseLimit { Drug = "morphine", Route = "IV", Unit = "mg", MaxSingle = 10, IsActive = true } };
			Assert.Single(HospitalDrugSafetyRules.DoseHits("Morphine", "15 mg", "IV", 1, ivOnly));
			Assert.Empty(HospitalDrugSafetyRules.DoseHits("Morphine", "15 mg", "PO", 1, ivOnly));
		}

		[Fact]
		public void The_knowledge_base_refuses_what_cannot_be_used()
		{
			Assert.Null(HospitalDrugSafetyRules.InteractionRefusal(new HospitalInteractionInput { DrugA = "warfarin", DrugB = "aspirin", Severity = "Major", EffectAr = "نزيف", EffectEn = "Bleeding" }));
			Assert.Equal("Write two different medicines", HospitalDrugSafetyRules.InteractionRefusal(new HospitalInteractionInput { DrugA = "Warfarin", DrugB = "warfarin ", Severity = "Major", EffectAr = "x", EffectEn = "x" }));
			Assert.Equal("Write both medicines (three letters at least)", HospitalDrugSafetyRules.InteractionRefusal(new HospitalInteractionInput { DrugA = "ab", DrugB = "aspirin", Severity = "Major", EffectAr = "x", EffectEn = "x" }));
			Assert.Equal("Choose the severity", HospitalDrugSafetyRules.InteractionRefusal(new HospitalInteractionInput { DrugA = "warfarin", DrugB = "aspirin", Severity = "Severe", EffectAr = "x", EffectEn = "x" }));
			Assert.Equal("Write the effect in Arabic and in English", HospitalDrugSafetyRules.InteractionRefusal(new HospitalInteractionInput { DrugA = "warfarin", DrugB = "aspirin", Severity = "Major", EffectAr = "x" }));
			Assert.Null(HospitalDrugSafetyRules.LimitRefusal(new HospitalDoseLimitInput { Drug = "paracetamol", Unit = "mg", MaxSingle = 1000, MaxDaily = 4000 }));
			Assert.Equal("Write a single or a daily limit", HospitalDrugSafetyRules.LimitRefusal(new HospitalDoseLimitInput { Drug = "paracetamol", Unit = "mg" }));
			Assert.Equal("The daily limit cannot be below the single one", HospitalDrugSafetyRules.LimitRefusal(new HospitalDoseLimitInput { Drug = "paracetamol", Unit = "mg", MaxSingle = 1000, MaxDaily = 500 }));
			Assert.Equal("Choose the unit", HospitalDrugSafetyRules.LimitRefusal(new HospitalDoseLimitInput { Drug = "paracetamol", Unit = "tabs", MaxSingle = 2 }));
			Assert.Equal("A limit is above zero", HospitalDrugSafetyRules.LimitRefusal(new HospitalDoseLimitInput { Drug = "paracetamol", Unit = "mg", MaxSingle = 0 }));
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h73_drug_safety.sql");
			var m = HospitalSliceParity.Model(HospitalDrugSafetyModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalDrugInteraction), "HospitalDrugInteractions"), (typeof(HospitalDoseLimit), "HospitalDoseLimits"), (typeof(HospitalAlertOverride), "HospitalAlertOverrides") })
			{ HospitalSliceParity.NamesItsTable(m, s, t, n); HospitalSliceParity.ColumnsMatch(m, s, t, n); }
			foreach (var x in HospitalInteractionSeverity.All.Concat(HospitalDoseUnit.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalAlertOverride));
		}
	}
}
