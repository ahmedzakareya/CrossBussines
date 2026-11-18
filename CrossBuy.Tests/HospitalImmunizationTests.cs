using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H84 — immunizations, with no database: when a dose is due, overdue or upcoming, what recording a dose
	// needs (not in the future, not before birth, a lot in date, old enough with the four-day grace, once), what a schedule
	// row needs, the default schedule, and the tables against their slice.
	// ==========================================================================================
	public class HospitalImmunizationTests
	{
		private static readonly DateTime Birth = new(2026, 5, 1);

		[Fact]
		public void A_dose_is_due_from_its_age_overdue_thirty_days_later_and_upcoming_before()
		{
			var due = HospitalImmunizationRules.DueOn(Birth, 60);
			Assert.Equal(new DateTime(2026, 6, 30), due);
			Assert.Equal(HospitalImmunizationStatus.Upcoming, HospitalImmunizationRules.Status(due, false, due.AddDays(-1)));
			Assert.Equal(HospitalImmunizationStatus.Due, HospitalImmunizationRules.Status(due, false, due));
			Assert.Equal(HospitalImmunizationStatus.Due, HospitalImmunizationRules.Status(due, false, due.AddDays(30)));
			Assert.Equal(HospitalImmunizationStatus.Overdue, HospitalImmunizationRules.Status(due, false, due.AddDays(31)));
			Assert.Equal(HospitalImmunizationStatus.Given, HospitalImmunizationRules.Status(due, true, due.AddDays(90)));
		}

		[Fact]
		public void A_dose_is_recorded_once_in_date_and_not_too_young()
		{
			var now = new DateTime(2026, 11, 10, 10, 0, 0);
			Assert.Null(HospitalImmunizationRules.RecordRefusal(now, now, Birth, now.AddMonths(6), "IM", 180, false));
			Assert.Equal("The time is in the future", HospitalImmunizationRules.RecordRefusal(now.AddHours(1), now, Birth, null, "IM", null, false));
			Assert.Equal("The dose cannot be before the patient's birth", HospitalImmunizationRules.RecordRefusal(Birth.AddDays(-1), now, Birth, null, "IM", null, false));
			Assert.Equal("Choose the route", HospitalImmunizationRules.RecordRefusal(now, now, Birth, null, "IV", null, false));
			Assert.Equal("This lot has expired: it cannot be given", HospitalImmunizationRules.RecordRefusal(now, now, Birth, now.AddDays(-1), "IM", null, false));
			Assert.Equal("This dose is already recorded", HospitalImmunizationRules.RecordRefusal(now, now, Birth, null, "IM", null, true));
			// MMR at 365 days: four days early still counts, five days early does not
			Assert.Null(HospitalImmunizationRules.RecordRefusal(Birth.AddDays(361), now.AddYears(2), Birth, null, "SC", 365, false));
			Assert.Equal("The patient is too young for this dose", HospitalImmunizationRules.RecordRefusal(Birth.AddDays(360), now.AddYears(2), Birth, null, "SC", 365, false));
		}

		[Fact]
		public void A_schedule_row_names_its_vaccine_dose_age_and_route()
		{
			Assert.Null(HospitalImmunizationRules.ScheduleRefusal("MMR", "الحصبة", "Measles", 1, 365, "SC"));
			Assert.Equal("Write the vaccine code (up to 12 characters)", HospitalImmunizationRules.ScheduleRefusal(" ", "x", "x", 1, 0, "IM"));
			Assert.Equal("Write the vaccine's name in Arabic and English", HospitalImmunizationRules.ScheduleRefusal("X", "x", " ", 1, 0, "IM"));
			Assert.Equal("The dose number is between 1 and 10", HospitalImmunizationRules.ScheduleRefusal("X", "x", "x", 0, 0, "IM"));
			Assert.Equal("The age is out of range", HospitalImmunizationRules.ScheduleRefusal("X", "x", "x", 1, -1, "IM"));
			Assert.Equal("Choose the route", HospitalImmunizationRules.ScheduleRefusal("X", "x", "x", 1, 0, "Patch"));
		}

		[Fact]
		public void The_default_schedule_is_well_formed()
		{
			var d = HospitalImmunizationRules.Defaults();
			Assert.True(d.Count >= 15);
			Assert.Equal(d.Count, d.Select(x => (x.Code, x.Dose)).Distinct().Count());   // one row per vaccine dose
			Assert.All(d, x => Assert.Null(HospitalImmunizationRules.ScheduleRefusal(x.Code, x.Name, x.NameEn, x.Dose, x.AgeDays, x.Route)));
			// within a vaccine, later doses come later
			foreach (var g in d.GroupBy(x => x.Code)) Assert.Equal(g.OrderBy(x => x.Dose).Select(x => x.AgeDays), g.OrderBy(x => x.AgeDays).Select(x => x.AgeDays));
		}

		[Fact]
		public void The_tables_match_their_slice_and_the_doses_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h84_immunizations.sql");
			var m = HospitalSliceParity.Model(HospitalImmunizationModel.Configure);
			var full = s + Environment.NewLine + HospitalSliceParity.Slice("hospital_h183_independent_checks.sql");   // H183: the screening
			foreach (var (t, n) in new[] { (typeof(HospitalVaccineScheduleItem), "HospitalVaccineScheduleItems"), (typeof(HospitalImmunization), "HospitalImmunizations") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, full, t, n);
			}
			foreach (var r in HospitalVaccineRoute.All) Assert.Contains("'" + r + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalImmunization));
		}
	}
}
