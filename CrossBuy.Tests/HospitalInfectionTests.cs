using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H76 — infection prevention and control, with no database: what isolation needs, the NHSN day-3 rule for
	// hospital-onset, the device association (more than two days in place, still in that day or the day before), what a
	// device's insertion and removal need, infection control's decision, the day counting and the rates, and the tables
	// against their slice.
	// ==========================================================================================
	public class HospitalInfectionTests
	{
		private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

		[Fact]
		public void Isolation_needs_known_precautions_and_an_organism_or_a_reason()
		{
			Assert.Null(HospitalInfectionRules.IsolationRefusal(new HospitalIsolationInput { Precaution = HospitalPrecaution.Contact, Organism = "MRSA" }));
			Assert.Null(HospitalInfectionRules.IsolationRefusal(new HospitalIsolationInput { Precaution = HospitalPrecaution.Airborne, Reason = "suspected TB, awaiting smear" }));
			Assert.Equal("Choose the precautions", HospitalInfectionRules.IsolationRefusal(new HospitalIsolationInput { Precaution = "Strict", Organism = "x" }));
			Assert.Equal("Write the organism or why the patient is isolated", HospitalInfectionRules.IsolationRefusal(new HospitalIsolationInput { Precaution = HospitalPrecaution.Droplet, Organism = " " }));
			Assert.Equal("The organism's name is too long", HospitalInfectionRules.IsolationRefusal(new HospitalIsolationInput { Precaution = HospitalPrecaution.Contact, Organism = new string('x', 101) }));
		}

		[Fact]
		public void Hospital_onset_is_the_third_calendar_day_of_the_stay_or_later()
		{
			var admitted = new DateTime(2026, 11, 1);
			Assert.False(HospitalInfectionRules.HospitalAcquired(admitted, new DateTime(2026, 11, 1)));   // day 1
			Assert.False(HospitalInfectionRules.HospitalAcquired(admitted, new DateTime(2026, 11, 2)));   // day 2
			Assert.True(HospitalInfectionRules.HospitalAcquired(admitted, new DateTime(2026, 11, 3)));    // day 3
			Assert.True(HospitalInfectionRules.HospitalAcquired(admitted.AddHours(23), new DateTime(2026, 11, 3)));   // the hour does not matter
		}

		[Fact]
		public void A_device_infection_needs_the_device_in_for_more_than_two_days_and_in_that_day_or_the_day_before()
		{
			var ins = new DateTime(2026, 11, 1, 22, 0, 0);
			Assert.False(HospitalInfectionRules.DeviceAssociated(ins, null, new DateTime(2026, 11, 2)));   // device day 2
			Assert.True(HospitalInfectionRules.DeviceAssociated(ins, null, new DateTime(2026, 11, 3)));    // device day 3
			// removed the day before the event: still associated; removed two days before: not
			Assert.True(HospitalInfectionRules.DeviceAssociated(ins, new DateTime(2026, 11, 5, 9, 0, 0), new DateTime(2026, 11, 6)));
			Assert.False(HospitalInfectionRules.DeviceAssociated(ins, new DateTime(2026, 11, 5, 9, 0, 0), new DateTime(2026, 11, 7)));
		}

		[Fact]
		public void A_device_goes_in_during_the_stay_and_comes_out_after_it_went_in()
		{
			var admitted = Now.AddDays(-3);
			Assert.Null(HospitalInfectionRules.DeviceRefusal(HospitalDeviceKind.CentralLine, Now.AddHours(-1), admitted, Now));
			Assert.Equal("Choose the device", HospitalInfectionRules.DeviceRefusal("Drain", Now, admitted, Now));
			Assert.Equal("The time is in the future", HospitalInfectionRules.DeviceRefusal(HospitalDeviceKind.Ventilator, Now.AddHours(1), admitted, Now));
			Assert.Equal("The time is before the admission", HospitalInfectionRules.DeviceRefusal(HospitalDeviceKind.UrinaryCatheter, admitted.AddHours(-2), admitted, Now));
			var d = new HospitalInvasiveDevice { InsertedAt = Now.AddDays(-2) };
			Assert.Null(HospitalInfectionRules.RemoveRefusal(d, Now, Now));
			Assert.Equal("The device cannot come out before it went in", HospitalInfectionRules.RemoveRefusal(d, Now.AddDays(-3), Now));
			Assert.Equal("This device is already out", HospitalInfectionRules.RemoveRefusal(new HospitalInvasiveDevice { InsertedAt = Now.AddDays(-2), RemovedAt = Now.AddDays(-1) }, Now, Now));
		}

		[Fact]
		public void A_case_names_its_kind_its_date_within_the_stay_and_an_mdro_its_organism()
		{
			var today = new DateTime(2026, 11, 10); var adm = new DateTime(2026, 11, 1);
			Assert.Null(HospitalInfectionRules.CaseRefusal(HospitalInfectionKind.Clabsi, null, new DateTime(2026, 11, 5), today, adm));
			Assert.Equal("Choose the kind of infection", HospitalInfectionRules.CaseRefusal("Flu", null, today, today, adm));
			Assert.Equal("Write the resistant organism", HospitalInfectionRules.CaseRefusal(HospitalInfectionKind.Mdro, " ", today, today, adm));
			Assert.Equal("Write the date of the event", HospitalInfectionRules.CaseRefusal(HospitalInfectionKind.Ssi, null, null, today, adm));
			Assert.Equal("The date is in the future", HospitalInfectionRules.CaseRefusal(HospitalInfectionKind.Ssi, null, today.AddDays(1), today, adm));
			Assert.Equal("The date is before the admission", HospitalInfectionRules.CaseRefusal(HospitalInfectionKind.Ssi, null, adm.AddDays(-1), today, adm));
		}

		[Fact]
		public void Infection_control_decides_once_and_a_device_kind_needs_its_device()
		{
			var c = new HospitalInfectionCase { Kind = HospitalInfectionKind.Clabsi, Status = HospitalInfectionStatus.Suspected };
			Assert.Equal("No device was in place long enough for this kind of infection", HospitalInfectionRules.DecideRefusal(c, HospitalInfectionStatus.Confirmed, null));
			c.DeviceId = 4;
			Assert.Null(HospitalInfectionRules.DecideRefusal(c, HospitalInfectionStatus.Confirmed, null));
			Assert.Equal("Write why it is ruled out", HospitalInfectionRules.DecideRefusal(c, HospitalInfectionStatus.RuledOut, " "));
			Assert.Equal("Choose the decision", HospitalInfectionRules.DecideRefusal(c, HospitalInfectionStatus.Suspected, null));
			Assert.Equal("This case is already decided", HospitalInfectionRules.DecideRefusal(new HospitalInfectionCase { Status = HospitalInfectionStatus.RuledOut }, HospitalInfectionStatus.Confirmed, null));
			Assert.True(HospitalInfectionKind.NeedsContact(HospitalInfectionKind.Mdro));
			Assert.True(HospitalInfectionKind.NeedsContact(HospitalInfectionKind.Cdi));
			Assert.False(HospitalInfectionKind.NeedsContact(HospitalInfectionKind.Ssi));
			Assert.Equal(HospitalDeviceKind.Ventilator, HospitalInfectionKind.DeviceOf(HospitalInfectionKind.Vap));
			Assert.Null(HospitalInfectionKind.DeviceOf(HospitalInfectionKind.Mdro));
		}

		[Fact]
		public void Days_are_calendar_days_clipped_to_the_month_and_rates_are_per_thousand()
		{
			var from = new DateTime(2026, 11, 1); var to = new DateTime(2026, 11, 30);
			// in on the 28th of October, out on the 3rd: the 1st, 2nd and 3rd of November
			Assert.Equal(3, HospitalInfectionRules.Days(new DateTime(2026, 10, 28, 9, 0, 0), new DateTime(2026, 11, 3, 8, 0, 0), from, to, to).Count());
			// still in: counted up to today
			Assert.Equal(10, HospitalInfectionRules.Days(new DateTime(2026, 11, 1, 23, 0, 0), null, from, to, new DateTime(2026, 11, 10)).Count());
			Assert.Empty(HospitalInfectionRules.Days(new DateTime(2026, 12, 2), null, from, to, to));
			Assert.Equal(1, HospitalInfectionRules.DeviceDay(new DateTime(2026, 11, 10, 7, 0, 0), new DateTime(2026, 11, 10, 20, 0, 0)));
			Assert.Equal(5, HospitalInfectionRules.DeviceDay(new DateTime(2026, 11, 6, 23, 0, 0), new DateTime(2026, 11, 10, 1, 0, 0)));
			Assert.Equal(2.5, HospitalInfectionRules.Rate(1, 400));
			Assert.Null(HospitalInfectionRules.Rate(0, 0));
			var row = new HospitalIcRateRow(1, "x", "x", 500, 200, 0, 50, 2, 1, 0, 1);
			Assert.Equal(4.0, row.HaiRate);
			Assert.Equal(5.0, row.ClabsiRate);
			Assert.Null(row.CautiRate);
			Assert.Equal(20.0, row.VapRate);
			Assert.Equal(0.4, row.LineUse);
		}

		[Fact]
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h76_infection.sql");
			var m = HospitalSliceParity.Model(HospitalInfectionModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalIsolation), "HospitalIsolations"), (typeof(HospitalInvasiveDevice), "HospitalInvasiveDevices"), (typeof(HospitalInfectionCase), "HospitalInfectionCases") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
				Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == t);
			}
			foreach (var x in HospitalPrecaution.All.Concat(HospitalDeviceKind.All).Concat(HospitalInfectionKind.All).Concat(HospitalInfectionStatus.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalAdmissions", "HospitalPatients", "HospitalWards" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
		}
	}
}
