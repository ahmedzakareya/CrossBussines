using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H97 — the allergy visit, with no database: the skin prick test against its controls, the specific-IgE
	// class, and the immunotherapy schedule — step up, repeat after a large local reaction, held by a systemic one and
	// resumed two steps lower; the 30-minute observation; the two tables against their slice.
	// ==========================================================================================
	public class HospitalAllergyTests
	{
		[Fact]
		public void The_skin_test_reads_against_its_controls()
		{
			// the prototype's test
			var spt = HospitalVisitWidgetRules.SkinTest(new (string, string, string, string?)[]
			{
				("SPT-HIST", "هيستامين", "Histamine", "6/18"), ("SPT-SAL", "محلول ملحي", "Saline", "0/0"), ("SPT-DPT", "عثة", "Mite D. pteronyssinus", "9/25"),
				("SPT-CAT", "القطط", "Cat", "2/5"), ("SPT-PALM", "النخيل", "Date-palm pollen", "5/14"), ("SPT-ROACH", "الصراصير", "Cockroach", "1/3"),
			});
			Assert.True(spt.Valid);
			Assert.Equal(new[] { "Histamine", "Saline" }, spt.Rows.Where(r => r.Control).Select(r => r.LabelEn));   // controls first
			Assert.Equal(new[] { "Mite D. pteronyssinus", "Date-palm pollen" }, spt.Rows.Where(r => r.Positive == true).Select(r => r.LabelEn));
			Assert.False(spt.Rows.Single(r => r.LabelEn == "Cat").Positive);
			// a wheal on saline (dermographism) makes the test unreadable, and lifts the bar for every allergen
			var bad = HospitalVisitWidgetRules.SkinTest(new (string, string, string, string?)[] { ("SPT-HIST", "", "Histamine", "6"), ("SPT-SAL", "", "Saline", "4"), ("SPT-DPT", "", "Mite", "6") });
			Assert.False(bad.Valid); Assert.False(bad.Rows.Single(r => r.LabelEn == "Mite").Positive);
			Assert.Null(HospitalVisitWidgetRules.SkinTest(new (string, string, string, string?)[] { ("SPT-DPT", "", "Mite", "6") }).Valid);   // no controls: no reading
		}

		[Fact]
		public void Specific_IgE_falls_into_its_class()
		{
			Assert.Equal(0, HospitalClinicalCalc.IgeClass(0.2m));
			Assert.Equal(1, HospitalClinicalCalc.IgeClass(0.35m));
			Assert.Equal(3, HospitalClinicalCalc.IgeClass(17.4m));
			Assert.Equal(4, HospitalClinicalCalc.IgeClass(22.4m));   // the prototype's mite
			Assert.Equal(6, HospitalClinicalCalc.IgeClass(150m));
			Assert.Null(HospitalClinicalCalc.IgeClass(null));
		}

		private static HospitalImmunoDose D(int seq, string vial, decimal ml, string reaction) => new() { Seq = seq, Vial = vial, DoseMl = ml, Reaction = reaction };

		[Fact]
		public void The_schedule_steps_up_repeats_after_a_large_reaction_and_holds_after_a_systemic_one()
		{
			var c = new HospitalImmunoCourse();
			var n = HospitalImmunoRules.Next(c, Array.Empty<HospitalImmunoDose>()); Assert.Equal(("1:1000", 0.05m), (n.Vial, n.DoseMl));
			n = HospitalImmunoRules.Next(c, new[] { D(1, "1:1000", 0.05m, "None") }); Assert.Equal(("1:1000", 0.10m), (n.Vial, n.DoseMl));
			n = HospitalImmunoRules.Next(c, new[] { D(7, "1:1000", 0.50m, "Local") }); Assert.Equal(("1:100", 0.05m), (n.Vial, n.DoseMl));   // the next vial
			n = HospitalImmunoRules.Next(c, new[] { D(8, "1:10", 0.40m, "LargeLocal") }); Assert.Equal(("1:10", 0.40m), (n.Vial, n.DoseMl));   // repeat
			n = HospitalImmunoRules.Next(c, new[] { D(28, "1:1", 0.50m, "None") }); Assert.Equal(("1:1", 0.50m), (n.Vial, n.DoseMl));   // maintenance
			n = HospitalImmunoRules.Next(c, new[] { D(3, "1:10", 0.30m, "Pending") }); Assert.Equal("Record the reaction to dose {0} first", n.Refusal);
			Assert.NotNull(HospitalImmunoRules.Next(new HospitalImmunoCourse { Status = "Held" }, new[] { D(3, "1:10", 0.30m, "Systemic") }).Refusal);
			// resumed by the doctor: two steps below the dose that caused it
			n = HospitalImmunoRules.Next(c, new[] { D(3, "1:10", 0.30m, "Systemic") }); Assert.Equal(("1:10", 0.15m), (n.Vial, n.DoseMl));
			n = HospitalImmunoRules.Next(c, new[] { D(3, "1:10", 0.05m, "Systemic") }); Assert.Equal(("1:100", 0.40m), (n.Vial, n.DoseMl));   // back into the weaker vial
			Assert.NotNull(HospitalImmunoRules.Next(new HospitalImmunoCourse { Status = "Stopped" }, Array.Empty<HospitalImmunoDose>()).Refusal);
		}

		[Fact]
		public void A_reaction_respects_the_30_minute_observation()
		{
			var now = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
			var d = new HospitalImmunoDose { Reaction = "Pending", GivenAt = now, ObserveUntil = now.AddMinutes(30) };
			Assert.Equal("The 30-minute observation is not over: the patient stays until {0}", HospitalImmunoRules.Classify(new() { Reaction = "None" }, now.AddMinutes(10), d).Error);
			Assert.Equal("None", HospitalImmunoRules.Classify(new() { Reaction = "None" }, now.AddMinutes(31), d).Reaction);
			Assert.Equal("LargeLocal", HospitalImmunoRules.Classify(new() { Reaction = "Local", WhealMm = 28 }, now.AddMinutes(31), d).Reaction);
			Assert.Equal("Local", HospitalImmunoRules.Classify(new() { Reaction = "Local", WhealMm = 10 }, now.AddMinutes(31), d).Reaction);
			// a systemic reaction can be recorded at once — but never without what happened
			Assert.Equal("Describe the systemic reaction and its treatment", HospitalImmunoRules.Classify(new() { Reaction = "Systemic" }, now.AddMinutes(5), d).Error);
			Assert.Equal("Systemic", HospitalImmunoRules.Classify(new() { Reaction = "Systemic", Note = "Wheeze, adrenaline 0.3 mg IM" }, now.AddMinutes(5), d).Reaction);
			Assert.Equal("This dose's reaction is already recorded", HospitalImmunoRules.Classify(new() { Reaction = "None" }, now.AddHours(1), new HospitalImmunoDose { Reaction = "None" }).Error);
		}

		[Fact]
		public void The_tables_match_their_slice_and_follow_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h97_allergy.sql");
			var m = HospitalSliceParity.Model(HospitalImmunotherapyModel.Configure);
			foreach (var (t, n) in new (Type, string)[] { (typeof(HospitalImmunoCourse), "HospitalImmunoCourses"), (typeof(HospitalImmunoDose), "HospitalImmunoDoses") })
			{ HospitalSliceParity.NamesItsTable(m, s, t, n); HospitalSliceParity.ColumnsMatch(m, s, t, n); }
			foreach (var x in HospitalImmunoStatus.All.Concat(HospitalImmunoReaction.All)) Assert.Contains("'" + x + "'", s);
			Assert.Contains("'IgeClass'", s);
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalImmunoCourse));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalImmunoDose));
		}
	}
}
