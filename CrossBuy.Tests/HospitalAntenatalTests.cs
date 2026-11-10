using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H85 — antenatal care, with no database: the due date (Naegele, or the ultrasound's), gestational age and
	// trimester, what opening a pregnancy and recording a visit need, the warnings each visit raises, closing a pregnancy,
	// and the tables against their slice.
	// ==========================================================================================
	public class HospitalAntenatalTests
	{
		private static readonly DateTime Today = new(2026, 11, 10);

		[Fact]
		public void The_due_date_is_naegele_unless_an_ultrasound_says_otherwise()
		{
			var lmp = new DateTime(2026, 3, 1);
			Assert.Equal(new DateTime(2026, 12, 6), HospitalAntenatalRules.Edd(lmp, null));
			Assert.Equal(new DateTime(2026, 12, 15), HospitalAntenatalRules.Edd(lmp, new DateTime(2026, 12, 15)));
			var edd = HospitalAntenatalRules.Edd(lmp, null);
			Assert.Equal(254, HospitalAntenatalRules.GaDays(edd, Today));
			Assert.Equal("36+2", HospitalAntenatalRules.Ga(254));
			Assert.Equal(1, HospitalAntenatalRules.Trimester(13 * 7 + 6));
			Assert.Equal(2, HospitalAntenatalRules.Trimester(14 * 7));
			Assert.Equal(3, HospitalAntenatalRules.Trimester(28 * 7));
		}

		[Fact]
		public void A_pregnancy_needs_a_female_file_a_date_and_coherent_counts()
		{
			HospitalPregnancyInput P(DateTime? lmp = null, DateTime? us = null, int g = 2, int p = 1, int a = 0) => new() { Lmp = lmp, UltrasoundEdd = us, Gravida = g, Para = p, Abortions = a };
			Assert.Null(HospitalAntenatalRules.PregnancyRefusal(P(new DateTime(2026, 6, 1)), "F", Today));
			Assert.Equal("A pregnancy is opened on a female patient's file", HospitalAntenatalRules.PregnancyRefusal(P(new DateTime(2026, 6, 1)), "M", Today));
			Assert.Equal("Write the last menstrual period or the ultrasound due date", HospitalAntenatalRules.PregnancyRefusal(P(), "F", Today));
			Assert.Equal("The last menstrual period must be within the last 44 weeks", HospitalAntenatalRules.PregnancyRefusal(P(Today.AddDays(1)), "F", Today));
			Assert.Equal("The last menstrual period must be within the last 44 weeks", HospitalAntenatalRules.PregnancyRefusal(P(Today.AddDays(-320)), "F", Today));
			Assert.Equal("Gravida counts this pregnancy: births and losses before it are fewer", HospitalAntenatalRules.PregnancyRefusal(P(new DateTime(2026, 6, 1), g: 2, p: 1, a: 1), "F", Today));
			Assert.Null(HospitalAntenatalRules.PregnancyRefusal(P(us: new DateTime(2027, 3, 1)), "F", Today));
		}

		[Fact]
		public void A_visit_has_plausible_measures_within_the_pregnancy()
		{
			var edd = new DateTime(2027, 2, 1);
			Assert.Null(HospitalAntenatalRules.VisitRefusal(new HospitalAntenatalVisitInput { BpSystolic = 118, BpDiastolic = 76, FetalHeartRate = 142 }, edd, Today));
			Assert.Equal("Write at least one measure or a note", HospitalAntenatalRules.VisitRefusal(new HospitalAntenatalVisitInput(), edd, Today));
			Assert.Equal("Write both blood pressure numbers", HospitalAntenatalRules.VisitRefusal(new HospitalAntenatalVisitInput { BpSystolic = 120 }, edd, Today));
			Assert.Equal("The blood pressure is not plausible", HospitalAntenatalRules.VisitRefusal(new HospitalAntenatalVisitInput { BpSystolic = 80, BpDiastolic = 90 }, edd, Today));
			Assert.Equal("The date is in the future", HospitalAntenatalRules.VisitRefusal(new HospitalAntenatalVisitInput { VisitDate = Today.AddDays(1), Note = "x" }, edd, Today));
			Assert.Equal("The visit cannot be before the pregnancy", HospitalAntenatalRules.VisitRefusal(new HospitalAntenatalVisitInput { VisitDate = edd.AddDays(-300), Note = "x" }, edd, Today));
			Assert.Equal("The next visit comes after this one", HospitalAntenatalRules.VisitRefusal(new HospitalAntenatalVisitInput { Note = "x", NextVisitOn = Today }, edd, Today));
			Assert.Equal("Choose the urine protein", HospitalAntenatalRules.VisitRefusal(new HospitalAntenatalVisitInput { UrineProtein = "4+" }, edd, Today));
		}

		[Fact]
		public void Each_visit_raises_its_own_warnings()
		{
			HospitalAntenatalVisit V(int? s = null, int? d = null, string? prot = null, int? fhr = null, decimal? fh = null) => new() { BpSystolic = s, BpDiastolic = d, UrineProtein = prot, FetalHeartRate = fhr, FundalHeightCm = fh };
			Assert.Empty(HospitalAntenatalRules.Warnings(V(118, 76, "Neg", 140, 30), 30 * 7));
			Assert.Equal(new[] { HospitalAntenatalWarning.Hypertension }, HospitalAntenatalRules.Warnings(V(142, 88), 30 * 7));
			Assert.Equal(new[] { HospitalAntenatalWarning.SevereHypertension }, HospitalAntenatalRules.Warnings(V(150, 112), 30 * 7));
			// pre-eclampsia only after 20 weeks
			Assert.Contains(HospitalAntenatalWarning.PreEclampsia, HospitalAntenatalRules.Warnings(V(145, 95, "2+"), 22 * 7));
			Assert.DoesNotContain(HospitalAntenatalWarning.PreEclampsia, HospitalAntenatalRules.Warnings(V(145, 95, "2+"), 18 * 7));
			Assert.DoesNotContain(HospitalAntenatalWarning.PreEclampsia, HospitalAntenatalRules.Warnings(V(145, 95, "Trace"), 30 * 7));
			Assert.Equal(new[] { HospitalAntenatalWarning.FetalHeart }, HospitalAntenatalRules.Warnings(V(fhr: 100), 30 * 7));
			Assert.Equal(new[] { HospitalAntenatalWarning.FundalHeight }, HospitalAntenatalRules.Warnings(V(fh: 25), 32 * 7));
			Assert.Empty(HospitalAntenatalRules.Warnings(V(fh: 14), 20 * 7));   // before 24 weeks the rule is not applied
		}

		[Fact]
		public void A_pregnancy_closes_once_and_a_loss_says_what_happened()
		{
			var open = new HospitalPregnancy { Status = HospitalPregnancyStatus.Active };
			Assert.Null(HospitalAntenatalRules.EndRefusal(open, HospitalPregnancyStatus.Delivered, null));
			Assert.Equal("Write what happened", HospitalAntenatalRules.EndRefusal(open, HospitalPregnancyStatus.Loss, " "));
			Assert.Equal("Choose delivered or lost", HospitalAntenatalRules.EndRefusal(open, HospitalPregnancyStatus.Active, null));
			Assert.Equal("This pregnancy is already closed", HospitalAntenatalRules.EndRefusal(new HospitalPregnancy { Status = HospitalPregnancyStatus.Loss }, HospitalPregnancyStatus.Delivered, null));
		}

		[Fact]
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h85_antenatal.sql");
			var m = HospitalSliceParity.Model(HospitalAntenatalModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalPregnancy), "HospitalPregnancies"), (typeof(HospitalAntenatalVisit), "HospitalAntenatalVisits") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
				Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == t);
			}
			foreach (var x in HospitalPregnancyStatus.All.Concat(HospitalUrineProtein.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalBirths", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
		}
	}
}
