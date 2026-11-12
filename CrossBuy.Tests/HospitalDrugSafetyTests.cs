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

		// ==================================================================================
		// MATCHING A MEDICINE BY NAME. The rule was a bare substring test with a three-letter
		// floor, and the floor does not stop the thing it was meant to stop: a knowledge-base
		// entry written as "met" matched Metoprolol, Metformin AND Methotrexate at once, and
		// "ace" matched Paracetamol. An alert that fires on the wrong medicine is how a
		// prescriber learns to dismiss alerts without reading them.
		//
		// Measured on the live database before the change: all eight real matches there are
		// whole words and substring found no extra, so this cost nothing and closed the trap
		// for whoever extends the table next.
		// ==================================================================================
		[Fact]
		public void A_knowledge_base_word_matches_a_whole_word_only()
		{
			// the real matches, which must keep working
			Assert.True(HospitalDrugSafetyRules.Mentions("Aspirin", "aspirin"));
			Assert.True(HospitalDrugSafetyRules.Mentions("Paracetamol", "paracetamol"));
			Assert.True(HospitalDrugSafetyRules.Mentions("aspirin 300 mg", "aspirin"));
			Assert.True(HospitalDrugSafetyRules.Mentions("Paracetamol/Codeine", "paracetamol"));   // punctuation is a boundary
			Assert.True(HospitalDrugSafetyRules.Mentions("Insulin glargine", "insulin glargine")); // a two-word entry

			// letters inside a longer name are NOT the medicine
			Assert.False(HospitalDrugSafetyRules.Mentions("Paracetamol", "ace"));
			Assert.False(HospitalDrugSafetyRules.Mentions("Metoprolol", "met"));
			Assert.False(HospitalDrugSafetyRules.Mentions("Methotrexate", "met"));
			Assert.False(HospitalDrugSafetyRules.Mentions("Ciprofloxacin", "pro"));

			// the floor stays: one and two letters never match anything
			Assert.False(HospitalDrugSafetyRules.Mentions("Aspirin", "as"));
			Assert.False(HospitalDrugSafetyRules.Mentions("Aspirin", "a"));
		}

		// ==================================================================================
		// WHETHER THE TABLE HAS HEARD OF A MEDICINE AT ALL — the question CheckOrderAsync
		// could not answer, and the reason an empty alert list used to mean both "checked and
		// safe" and "nothing could be checked".
		// ==================================================================================
		[Fact]
		public void The_table_says_which_medicines_it_does_not_know()
		{
			var pairs = new[]
			{
				new HospitalDrugInteraction { DrugA = "aspirin", DrugB = "warfarin", Severity = HospitalInteractionSeverity.Major, IsActive = true },
				new HospitalDrugInteraction { DrugA = "lisinopril", DrugB = "spironolactone", Severity = HospitalInteractionSeverity.Moderate, IsActive = true },
			};
			var limits = new[]
			{
				new HospitalDoseLimit { Drug = "paracetamol", Unit = HospitalDoseUnit.Mg, MaxSingle = 1000m, IsActive = true },
				new HospitalDoseLimit { Drug = "morphine", Route = "IV", Unit = HospitalDoseUnit.Mg, MaxSingle = 10m, IsActive = true },
			};

			Assert.True(HospitalDrugSafetyRules.KnownToPairs("Aspirin", pairs));
			Assert.False(HospitalDrugSafetyRules.KnownToPairs("Ceftriaxone", pairs));

			// A ROUTE IS PART OF THE QUESTION. Morphine has an IV limit and no oral one, so an
			// oral order is unchecked even though the medicine is in the table — which is the
			// case a "do we know this drug" test would miss by asking only about the name.
			Assert.True(HospitalDrugSafetyRules.KnownToLimits("Morphine", "IV", limits));
			Assert.False(HospitalDrugSafetyRules.KnownToLimits("Morphine", "PO", limits));
			Assert.True(HospitalDrugSafetyRules.KnownToLimits("Paracetamol", "PO", limits));   // no route on the limit: any route
			Assert.False(HospitalDrugSafetyRules.KnownToLimits("Ceftriaxone", "IV", limits));

			// an inactive row is not knowledge
			var off = new[] { new HospitalDrugInteraction { DrugA = "aspirin", DrugB = "warfarin", Severity = HospitalInteractionSeverity.Major, IsActive = false } };
			Assert.False(HospitalDrugSafetyRules.KnownToPairs("Aspirin", off));
		}

		// A dose nobody can read must not be reported as a dose within its limits.
		[Fact]
		public void An_unreadable_dose_produces_no_hits_and_is_detectable_as_unread()
		{
			var limits = new[] { new HospitalDoseLimit { Drug = "paracetamol", Unit = HospitalDoseUnit.Mg, MaxSingle = 1000m, IsActive = true } };
			Assert.Null(HospitalDrugSafetyRules.ParseDose("one tablet"));
			Assert.Empty(HospitalDrugSafetyRules.DoseHits("Paracetamol", "one tablet", "PO", 4, limits));
			// the same drug with a readable dose over the limit DOES hit, so the emptiness above
			// is about the dose and not about the drug
			Assert.NotEmpty(HospitalDrugSafetyRules.DoseHits("Paracetamol", "2 g", "PO", 4, limits));
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h73_drug_safety.sql");
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h95_paediatrics.sql");   // H95: a child's dose by weight
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h104_renal_dosing.sql");   // H104: the kidney thresholds
			var m = HospitalSliceParity.Model(HospitalDrugSafetyModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalDrugInteraction), "HospitalDrugInteractions"), (typeof(HospitalDoseLimit), "HospitalDoseLimits"), (typeof(HospitalAlertOverride), "HospitalAlertOverrides") })
			{ HospitalSliceParity.NamesItsTable(m, s, t, n); HospitalSliceParity.ColumnsMatch(m, s, t, n); }
			foreach (var x in HospitalInteractionSeverity.All.Concat(HospitalDoseUnit.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalAlertOverride));
		}
	}
}
