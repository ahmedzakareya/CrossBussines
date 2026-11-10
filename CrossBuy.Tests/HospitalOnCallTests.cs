using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H68 — on-call, with no database: the span a plan asks for (a night runs into the next morning), what
	// refuses a shift (a doctor twice, a department's two firsts, leave, licence, an ended shift), who a department
	// calls at a moment, the Saturday week, and the table against its slice.
	// ==========================================================================================
	public class HospitalOnCallTests
	{
		private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0);
		private static HospitalPractitioner Doc(int id = 7, DateTime? licence = null, bool active = true) => new() { ID = id, Name = "د", NameEn = "D", IsActive = active, LicenseExpiry = licence };
		private static HospitalOnCallShift S(int id, int dept, int doc, string level, DateTime start, DateTime end, bool cancelled = false) =>
			new() { ID = id, DepartmentId = dept, PractitionerId = doc, Level = level, StartsAt = start, EndsAt = end, CancelledAt = cancelled ? Now : null, CancelReason = cancelled ? "x" : null };
		private static HospitalOnCallInput In(string level = HospitalOnCallLevel.First) => new() { DepartmentId = 2, PractitionerId = 7, Level = level, Date = "2026-11-10", From = "20:00", To = "08:00" };
		private static (DateTime, DateTime)? Night => (new DateTime(2026, 11, 10, 20, 0, 0), new DateTime(2026, 11, 11, 8, 0, 0));

		[Fact]
		public void A_to_at_or_before_from_ends_the_next_morning()
		{
			Assert.Equal(Night, HospitalOnCallRules.Span("2026-11-10", "20:00", "08:00"));
			Assert.Equal((new DateTime(2026, 11, 10, 8, 0, 0), new DateTime(2026, 11, 10, 20, 0, 0)), HospitalOnCallRules.Span("2026-11-10", "08:00", "20:00"));
			Assert.Equal((new DateTime(2026, 11, 10, 8, 0, 0), new DateTime(2026, 11, 11, 8, 0, 0)), HospitalOnCallRules.Span("2026-11-10", "08:00", "08:00"));
			Assert.Null(HospitalOnCallRules.Span("10/11/2026", "08:00", "20:00"));
			Assert.Null(HospitalOnCallRules.Span("2026-11-10", "25:00", "08:00"));
		}

		[Fact]
		public void A_shift_is_refused_for_what_it_would_break()
		{
			var none = Array.Empty<HospitalOnCallShift>(); var noLeave = Array.Empty<HospitalPractitionerLeave>();
			Assert.Null(HospitalOnCallRules.PlanRefusal(In(), Night, Doc(), true, none, noLeave, Now));
			Assert.Equal("Choose the department", HospitalOnCallRules.PlanRefusal(In(), Night, Doc(), false, none, noLeave, Now));
			Assert.Equal("Choose the doctor", HospitalOnCallRules.PlanRefusal(In(), Night, null, true, none, noLeave, Now));
			Assert.Equal("Choose first on call or backup", HospitalOnCallRules.PlanRefusal(In("Third"), Night, Doc(), true, none, noLeave, Now));
			Assert.Equal("Write the day and the times", HospitalOnCallRules.PlanRefusal(In(), null, Doc(), true, none, noLeave, Now));
			Assert.Equal("This shift has already ended", HospitalOnCallRules.PlanRefusal(In(), Night, Doc(), true, none, noLeave, new DateTime(2026, 11, 11, 9, 0, 0)));
			Assert.Equal("This doctor is not active", HospitalOnCallRules.PlanRefusal(In(), Night, Doc(active: false), true, none, noLeave, Now));
			Assert.Equal("The doctor's licence has expired", HospitalOnCallRules.PlanRefusal(In(), Night, Doc(licence: new DateTime(2026, 11, 10)), true, none, noLeave, Now));
			// a whole-day leave on the next day touches the night; a part-day leave that ends before 20:00 does not
			Assert.Equal("The doctor is on leave then", HospitalOnCallRules.PlanRefusal(In(), Night, Doc(), true, none,
				new[] { new HospitalPractitionerLeave { PractitionerId = 7, FromDate = new DateTime(2026, 11, 11), ToDate = new DateTime(2026, 11, 11) } }, Now));
			Assert.Null(HospitalOnCallRules.PlanRefusal(In(), Night, Doc(), true, none,
				new[] { new HospitalPractitionerLeave { PractitionerId = 7, FromDate = new DateTime(2026, 11, 10), ToDate = new DateTime(2026, 11, 10), StartMinute = 9 * 60, EndMinute = 13 * 60 } }, Now));
		}

		[Fact]
		public void A_doctor_is_never_on_two_shifts_and_a_department_holds_one_first_and_one_backup()
		{
			var noLeave = Array.Empty<HospitalPractitionerLeave>();
			var otherDept = S(1, 5, 7, HospitalOnCallLevel.Backup, new DateTime(2026, 11, 11, 6, 0, 0), new DateTime(2026, 11, 11, 14, 0, 0));
			Assert.Equal("The doctor is already on call then", HospitalOnCallRules.PlanRefusal(In(), Night, Doc(), true, new[] { otherDept }, noLeave, Now));
			var firstHere = S(2, 2, 9, HospitalOnCallLevel.First, new DateTime(2026, 11, 10, 22, 0, 0), new DateTime(2026, 11, 11, 6, 0, 0));
			Assert.Equal("The department already has a first on call then", HospitalOnCallRules.PlanRefusal(In(), Night, Doc(), true, new[] { firstHere }, noLeave, Now));
			Assert.Null(HospitalOnCallRules.PlanRefusal(In(HospitalOnCallLevel.Backup), Night, Doc(), true, new[] { firstHere }, noLeave, Now));
			// a cancelled shift and one that only touches the edge hold nothing
			var cancelled = S(3, 2, 9, HospitalOnCallLevel.First, new DateTime(2026, 11, 10, 22, 0, 0), new DateTime(2026, 11, 11, 6, 0, 0), cancelled: true);
			var before = S(4, 2, 7, HospitalOnCallLevel.First, new DateTime(2026, 11, 10, 8, 0, 0), new DateTime(2026, 11, 10, 20, 0, 0));
			Assert.Null(HospitalOnCallRules.PlanRefusal(In(), Night, Doc(), true, new[] { cancelled, before }, noLeave, Now));
		}

		[Fact]
		public void A_department_calls_its_first_then_its_backup()
		{
			var at = new DateTime(2026, 11, 10, 23, 0, 0);
			var list = new[]
			{
				S(1, 2, 9, HospitalOnCallLevel.Backup, new DateTime(2026, 11, 10, 20, 0, 0), new DateTime(2026, 11, 11, 8, 0, 0)),
				S(2, 2, 7, HospitalOnCallLevel.First, new DateTime(2026, 11, 10, 20, 0, 0), new DateTime(2026, 11, 11, 8, 0, 0)),
				S(3, 2, 8, HospitalOnCallLevel.First, new DateTime(2026, 11, 10, 8, 0, 0), new DateTime(2026, 11, 10, 20, 0, 0)),
				S(4, 3, 6, HospitalOnCallLevel.First, new DateTime(2026, 11, 10, 20, 0, 0), new DateTime(2026, 11, 11, 8, 0, 0)),
				S(5, 2, 5, HospitalOnCallLevel.First, new DateTime(2026, 11, 10, 20, 0, 0), new DateTime(2026, 11, 11, 8, 0, 0), cancelled: true),
			};
			Assert.Equal(new[] { 2, 1 }, HospitalOnCallRules.At(list, 2, at).Select(s => s.ID));
			Assert.Empty(HospitalOnCallRules.At(list, 2, new DateTime(2026, 11, 11, 8, 0, 0)));
		}

		[Fact]
		public void The_week_opens_on_saturday()
		{
			Assert.Equal(new DateTime(2026, 11, 7), HospitalOnCallRules.WeekStart(new DateTime(2026, 11, 9)));
			Assert.Equal(new DateTime(2026, 11, 7), HospitalOnCallRules.WeekStart(new DateTime(2026, 11, 7)));
			Assert.Equal(new DateTime(2026, 11, 7), HospitalOnCallRules.WeekStart(new DateTime(2026, 11, 13, 23, 0, 0)));
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h68_on_call.sql");
			var m = HospitalSliceParity.Model(HospitalOnCallModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalOnCallShift), "HospitalOnCallShifts");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalOnCallShift), "HospitalOnCallShifts");
			foreach (var x in HospitalOnCallLevel.All) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalDepartments", "HospitalPractitioners" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
		}
	}
}
