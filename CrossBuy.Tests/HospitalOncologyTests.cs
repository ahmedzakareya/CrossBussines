using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H88 — chemotherapy, with no database: Mosteller, Cockcroft-Gault with its cap, Calvert, each basis and the
	// reduction, the lifetime limit across courses, each pre-cycle check at its edge, the next due day, what a regimen
	// and a course need, and the tables against their slice.
	// ==========================================================================================
	public class HospitalOncologyTests
	{
		private static readonly DateTime Day = new(2026, 11, 10);
		private static HospitalChemoCourse Course() => new() { Status = HospitalChemoCourseStatus.Active, HeightCm = 170, WeightKg = 70, StartDate = Day.AddDays(-21) };
		private static HospitalChemoRegimen Reg() => new() { CycleDays = 21, MaxCycles = 6 };
		private static HospitalChemoRegimenDrug Dox() => new() { DrugName = "Doxorubicin", Dose = 60, Basis = HospitalChemoBasis.PerM2, Route = "IV", Days = "1", LifetimeMgM2 = 450, SortOrder = 1 };
		private static HospitalChemoRegimenDrug Carbo() => new() { DrugName = "Carboplatin", Dose = 5, Basis = HospitalChemoBasis.Auc, Route = "IV", Days = "1", SortOrder = 2 };
		private static HospitalChemoCycleInput Ok(decimal w = 70) => new() { WeightKg = w, Anc = 2.1m, Platelets = 180, Creatinine = 1.0m };
		private static readonly Dictionary<string, decimal> None = new();

		[Fact]
		public void Mosteller_cockcroft_gault_and_calvert()
		{
			Assert.Equal(1.82m, HospitalOncologyRules.Bsa(170, 70));
			Assert.Equal(77.8m, HospitalOncologyRules.Gfr(60, 70, 1.0m, false));
			Assert.Equal(66.1m, HospitalOncologyRules.Gfr(60, 70, 1.0m, true));        // × 0.85
			Assert.Equal(125m, HospitalOncologyRules.Gfr(20, 100, 0.5m, false));         // capped
			Assert.Null(HospitalOncologyRules.Gfr(null, 70, 1.0m, false));
			Assert.Equal(514.0m, HospitalOncologyRules.DoseMg(HospitalChemoBasis.Auc, 5, 1.82m, 70, 77.8m, 0));   // 5 × (77.8 + 25)
			Assert.Null(HospitalOncologyRules.DoseMg(HospitalChemoBasis.Auc, 5, 1.82m, 70, null, 0));
		}

		[Fact]
		public void Each_basis_and_the_reduction()
		{
			Assert.Equal(136.5m, HospitalOncologyRules.DoseMg(HospitalChemoBasis.PerM2, 75, 1.82m, 70, null, 0));
			Assert.Equal(109.2m, HospitalOncologyRules.DoseMg(HospitalChemoBasis.PerM2, 75, 1.82m, 70, null, 20));
			Assert.Equal(560m, HospitalOncologyRules.DoseMg(HospitalChemoBasis.PerKg, 8, 1.82m, 70, null, 0));
			Assert.Equal(1000m, HospitalOncologyRules.DoseMg(HospitalChemoBasis.Flat, 1000, 1.82m, 70, null, 0));
			Assert.Equal(2, HospitalOncologyRules.DayCount("1, 8"));
		}

		[Fact]
		public void A_clean_cycle_has_no_check_and_freezes_its_doses()
		{
			var pv = HospitalOncologyRules.Preview(Course(), Reg(), new[] { Dox(), Carbo() }, 1, Day.AddDays(-21), null, None, 60, false, Ok(), Day);
			Assert.Null(pv.Refusal);
			Assert.Empty(pv.Checks);
			Assert.Equal(2, pv.CycleNo);
			Assert.Equal(109.2m, pv.Doses[0].DoseMg);   // 60 × 1.82
			Assert.Equal(60m, pv.Doses[0].CycleMgM2);
			Assert.Equal(514.0m, pv.Doses[1].DoseMg);
		}

		[Fact]
		public void Each_check_fires_at_its_edge()
		{
			IReadOnlyList<string> C(HospitalChemoCycleInput i, DateTime? last = null, int? tox = null, Dictionary<string, decimal>? prior = null) =>
				HospitalOncologyRules.Preview(Course(), Reg(), new[] { Dox() }, 1, last ?? Day.AddDays(-21), tox, prior ?? None, 60, false, i, Day).Checks;
			var i = Ok(); i.Anc = 1.49m; Assert.Contains(HospitalChemoCheck.LowAnc, C(i));
			i = Ok(); i.Anc = 1.5m; Assert.DoesNotContain(HospitalChemoCheck.LowAnc, C(i));
			i = Ok(); i.Platelets = 99; Assert.Contains(HospitalChemoCheck.LowPlatelets, C(i));
			i = Ok(); i.Anc = null; Assert.Contains(HospitalChemoCheck.NoCounts, C(i));
			Assert.Contains(HospitalChemoCheck.Early, C(Ok(), Day.AddDays(-18)));            // 21 − 2 = 19 days at least
			Assert.DoesNotContain(HospitalChemoCheck.Early, C(Ok(), Day.AddDays(-19)));
			Assert.Contains(HospitalChemoCheck.Toxicity, C(Ok(), tox: 3));                    // grade 3 and no reduction
			i = Ok(); i.DoseReductionPct = 25; Assert.DoesNotContain(HospitalChemoCheck.Toxicity, C(i, tox: 3));
			i = Ok(); i.ToxicityGrade = 4; Assert.Contains(HospitalChemoCheck.Toxicity, C(i));
			Assert.Contains(HospitalChemoCheck.Lifetime, C(Ok(), prior: new() { ["DOXORUBICIN"] = 420m }));   // 420 + 60 > 450
			Assert.DoesNotContain(HospitalChemoCheck.Lifetime, C(Ok(), prior: new() { ["DOXORUBICIN"] = 390m }));
			var heavy = C(Ok(78));                                                             // 11 % over the start
			Assert.Contains(HospitalChemoCheck.WeightChange, heavy);
			Assert.False(HospitalChemoCheck.Blocks(HospitalChemoCheck.WeightChange));
			Assert.True(HospitalChemoCheck.Blocks(HospitalChemoCheck.LowAnc));
		}

		[Fact]
		public void What_refuses_a_cycle_outright()
		{
			var done = HospitalOncologyRules.Preview(Course(), Reg(), new[] { Dox() }, 6, Day.AddDays(-21), null, None, 60, false, Ok(), Day);
			Assert.Equal("The regimen's cycles are all given", done.Refusal);
			var noW = Ok(); noW.WeightKg = null;
			Assert.Equal("Weigh the patient: the dose is computed on today's weight", HospitalOncologyRules.Preview(Course(), Reg(), new[] { Dox() }, 1, null, null, None, 60, false, noW, Day).Refusal);
			var noCr = Ok(); noCr.Creatinine = null;
			Assert.Equal("The carboplatin dose needs the creatinine and the patient's age", HospitalOncologyRules.Preview(Course(), Reg(), new[] { Carbo() }, 1, null, null, None, 60, false, noCr, Day).Refusal);
		}

		[Fact]
		public void The_next_cycle_is_due_a_cycle_after_the_last_or_when_the_hold_ends()
		{
			var c = Course();
			Assert.Equal(c.StartDate, HospitalOncologyRules.NextDue(c, 21, Array.Empty<HospitalChemoCycle>()));
			var given = new HospitalChemoCycle { CycleNo = 1, Status = HospitalChemoCycleStatus.Given, CycleDate = Day.AddDays(-21) };
			Assert.Equal(Day, HospitalOncologyRules.NextDue(c, 21, new[] { given }));
			var held = new HospitalChemoCycle { CycleNo = 2, Status = HospitalChemoCycleStatus.Held, CycleDate = Day, HeldUntil = Day.AddDays(7) };
			Assert.Equal(Day.AddDays(7), HospitalOncologyRules.NextDue(c, 21, new[] { given, held }));
		}

		[Fact]
		public void A_regimen_and_a_course_need_their_parts()
		{
			HospitalChemoRegimenInput R() => new() { Code = "AC", Name = "AC", CycleDays = 21, MaxCycles = 4, Drugs = new() { new() { DrugName = "Doxorubicin", Dose = 60, Basis = "MgM2", Route = "IV", Days = "1", LifetimeMgM2 = 450 } } };
			Assert.Null(HospitalOncologyRules.RegimenRefusal(R()));
			var r = R(); r.Drugs!.Clear(); Assert.Equal("Add at least one drug", HospitalOncologyRules.RegimenRefusal(r));
			r = R(); r.Drugs![0].Days = "1,x"; Assert.Equal("Write the days as numbers, like 1 or 1,8", HospitalOncologyRules.RegimenRefusal(r));
			r = R(); r.Drugs![0].Days = "1,28"; Assert.Equal("A drug's day falls after the cycle ends", HospitalOncologyRules.RegimenRefusal(r));
			r = R(); r.Drugs![0].Basis = "MgKg"; Assert.Equal("A lifetime limit is kept for a mg/m² dose only", HospitalOncologyRules.RegimenRefusal(r));
			r = R(); r.Drugs!.Add(new() { DrugName = "doxorubicin ", Dose = 1, Basis = "Mg", Route = "IV", Days = "1" }); Assert.Equal("A drug is written twice", HospitalOncologyRules.RegimenRefusal(r));
			r = R(); r.CycleDays = 5; Assert.Equal("A cycle lasts between 7 and 56 days", HospitalOncologyRules.RegimenRefusal(r));

			var c = new HospitalChemoCourseInput { Diagnosis = "Breast cancer", Intent = HospitalChemoIntent.Adjuvant, HeightCm = 160, WeightKg = 65 };
			Assert.Null(HospitalOncologyRules.CourseRefusal(c, Day, Day));
			c.Intent = "Maybe"; Assert.Equal("Choose the intent of the treatment", HospitalOncologyRules.CourseRefusal(c, Day, Day));
			c.Intent = HospitalChemoIntent.Adjuvant; c.HeightCm = 20; Assert.Equal("The height is between 40 and 250 cm", HospitalOncologyRules.CourseRefusal(c, Day, Day));
		}

		[Fact]
		public void The_tables_match_their_slice_and_the_patients_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h88_oncology.sql");
			var m = HospitalSliceParity.Model(HospitalOncologyModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalChemoRegimen), "HospitalChemoRegimens"), (typeof(HospitalChemoRegimenDrug), "HospitalChemoRegimenDrugs"),
				(typeof(HospitalChemoCourse), "HospitalChemoCourses"), (typeof(HospitalChemoCycle), "HospitalChemoCycles"), (typeof(HospitalChemoDose), "HospitalChemoDoses") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
			}
			foreach (var t in new[] { typeof(HospitalChemoCourse), typeof(HospitalChemoCycle), typeof(HospitalChemoDose) }) Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == t);
			foreach (var x in HospitalChemoBasis.All.Concat(HospitalChemoRoute.All).Concat(HospitalChemoIntent.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
		}
	}
}
