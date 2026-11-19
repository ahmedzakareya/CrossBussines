using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H214 — video visits. What must never happen: a patient joining long before or long after the time, anyone
	// joining an ended visit, a doctor holding the visit on another day, an unknown or oversized set-up message passed,
	// or a side reading its own messages back.
	// ==========================================================================================
	public class HospitalTeleVisitTests
	{
		private static readonly DateTime At = new(2026, 11, 19, 10, 0, 0);

		[Fact]
		public void The_patient_joins_from_15_minutes_before_to_2_hours_after()
		{
			var v = new HospitalTeleVisit { Status = HospitalTeleStatus.Scheduled };
			Assert.Equal("The video visit opens 15 minutes before its time", HospitalTeleRules.PatientJoinRefusal(v, At, At.AddMinutes(-16)));
			Assert.Null(HospitalTeleRules.PatientJoinRefusal(v, At, At.AddMinutes(-15)));
			Assert.Null(HospitalTeleRules.PatientJoinRefusal(v, At, At.AddHours(2)));
			Assert.Equal("This video visit's time has passed", HospitalTeleRules.PatientJoinRefusal(v, At, At.AddHours(2).AddMinutes(1)));
			v.Status = HospitalTeleStatus.Ended;
			Assert.Equal("This video visit has ended", HospitalTeleRules.PatientJoinRefusal(v, At, At));
		}

		[Fact]
		public void The_doctor_holds_it_on_its_day_until_it_ends()
		{
			var v = new HospitalTeleVisit { Status = HospitalTeleStatus.Live };
			Assert.Null(HospitalTeleRules.DoctorJoinRefusal(v, At, At.AddHours(-3)));
			Assert.Equal("A video visit is held on its own day", HospitalTeleRules.DoctorJoinRefusal(v, At, At.AddDays(1)));
			v.Status = HospitalTeleStatus.Ended;
			Assert.Equal("This video visit has ended", HospitalTeleRules.DoctorJoinRefusal(v, At, At));
		}

		[Fact]
		public void Only_the_five_set_up_messages_pass_and_none_after_the_end()
		{
			var v = new HospitalTeleVisit { Status = HospitalTeleStatus.Live };
			foreach (var k in HospitalTeleSignalKind.All) Assert.Null(HospitalTeleRules.SignalRefusal(v, k, "{}"));
			Assert.Equal("not_found", HospitalTeleRules.SignalRefusal(v, "chat", "hi"));
			Assert.Equal("The message is too long", HospitalTeleRules.SignalRefusal(v, HospitalTeleSignalKind.Offer, new string('x', HospitalTeleRules.MaxPayload + 1)));
			v.Status = HospitalTeleStatus.Ended;
			Assert.Equal("This video visit has ended", HospitalTeleRules.SignalRefusal(v, HospitalTeleSignalKind.Ice, "{}"));
		}

		[Fact]
		public void Each_side_reads_the_other()
		{
			Assert.Equal(HospitalTeleSide.Patient, HospitalTeleRules.Other(HospitalTeleSide.Doctor));
			Assert.Equal(HospitalTeleSide.Doctor, HospitalTeleRules.Other(HospitalTeleSide.Patient));
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var model = HospitalSliceParity.Model(HospitalTeleModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h214_televisits.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalTeleVisit), "HospitalTeleVisits");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalTeleVisit), "HospitalTeleVisits");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalTeleSignal), "HospitalTeleSignals");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalTeleSignal), "HospitalTeleSignals");
			foreach (var x in HospitalTeleStatus.All.Concat(HospitalTeleSignalKind.All)) Assert.Contains("'" + x + "'", s);
		}
	}
}
