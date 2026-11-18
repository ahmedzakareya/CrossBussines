using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H201 — bedside monitors. What must never happen: a monitor's set charted on the wrong patient (the bed
	// and the file number disagree), charted without a nurse, charted without the level of consciousness NEWS2 needs,
	// an artefact (SpO2 3%, a temperature in °F read as °C) charted as a reading, or a stale set charted as current.
	// ==========================================================================================
	public class HospitalDeviceVitalTests
	{
		private const string Msg =
			"MSH|^~\\&|PHILIPS-MX450|ICU|CROSSBUSINESS|HOSPITAL|20261118220500||ORU^R01^ORU_R01|MX-000981|P|2.6\r" +
			"PID|1||MRN-100008^^^HOSP||TEST^PATIENT\r" +
			"PV1|1|I|ICU^211^211-A\r" +
			"OBR|1|||VITALS|||20261118220000\r" +
			"OBX|1|NM|147842^MDC_ECG_HEART_RATE^MDC||112|/min|||||F|||20261118220000\r" +
			"OBX|2|NM|149530^MDC_PULS_OXIM_PULS_RATE^MDC||109|/min|||||F\r" +
			"OBX|3|NM|150456^MDC_PULS_OXIM_SAT_O2^MDC||93|%|||||F\r" +
			"OBX|4|NM|151562^MDC_RESP_RATE^MDC||24|/min|||||F\r" +
			"OBX|5|NM|8480-6^Systolic BP^LN||96|mm[Hg]|||||F\r" +
			"OBX|6|NM|8462-4^Diastolic BP^LN||58|mm[Hg]|||||F\r" +
			"OBX|7|NM|8310-5^Body temperature^LN||101.3|[degF]|||||F\r";

		[Fact]
		public void A_monitor_set_is_read_by_its_codes_and_units()
		{
			var (s, why) = HospitalDeviceVitalRules.Parse(Msg);
			Assert.Null(why);
			Assert.Equal(("PHILIPS-MX450", "MX-000981", "211-A", "MRN-100008"), (s!.Device, s.MessageId, s.BedCode, s.Mrn));
			// the ECG's rate wins over the oximeter's pulse; °F becomes °C
			Assert.Equal((112, 93, 24, 96, 58, 38.5m), (s.HeartRate!.Value, s.SpO2!.Value, s.RespRate!.Value, s.SystolicBp!.Value, s.DiastolicBp!.Value, s.TempC!.Value));
			Assert.NotNull(s.TakenAt);
			Assert.Empty(s.Left);
			// only the oximeter's pulse: it is the heart rate
			var po = HospitalDeviceVitalRules.Parse(Msg.Replace("OBX|1|NM|147842^MDC_ECG_HEART_RATE^MDC||112|/min|||||F|||20261118220000\r", "")).Set!;
			Assert.Equal(109, po.HeartRate);
		}

		[Fact]
		public void An_artefact_or_a_foreign_unit_is_left_out_and_named()
		{
			var (s, _) = HospitalDeviceVitalRules.Parse(Msg.Replace("||93|%|", "||3|%|").Replace("||24|/min|", "||24|L/min|"));
			Assert.Null(s!.SpO2);
			Assert.Null(s.RespRate);
			Assert.Contains(HospitalDeviceVitalRules.Pack(HospitalDeviceVitalRules.LeftRange, "SpO2", 3), s.Left);
			Assert.Contains(HospitalDeviceVitalRules.Pack(HospitalDeviceVitalRules.LeftUnit, "RR", "L/min"), s.Left);
			// a blood pressure needs both halves
			var half = HospitalDeviceVitalRules.Parse(Msg.Replace("OBX|6|NM|8462-4^Diastolic BP^LN||58|mm[Hg]|||||F\r", "")).Set!;
			Assert.Null(half.SystolicBp);
			// a value the monitor could not obtain (X) is not a value
			Assert.Null(HospitalDeviceVitalRules.Parse(Msg.Replace("||93|%|||||F", "||93|%|||||X")).Set!.SpO2);
			Assert.Equal("The message holds no vital signs", HospitalDeviceVitalRules.Parse("MSH|^~\\&|M|W|||20261118||ORU^R01|1|P|2.6\rPID|1||X\rOBX|1|NM|1234^Other||5|U|||||F\r").Refusal);
			var (k, a) = HospitalDeviceVitalRules.Unpack(HospitalDeviceVitalRules.Pack("key", "a", "b"));
			Assert.Equal("key", k);
			Assert.Equal(new[] { "a", "b" }, a);
		}

		[Fact]
		public void The_set_is_placed_only_when_bed_and_file_agree()
		{
			Assert.Null(HospitalDeviceVitalRules.WhoRefusal("211-A", "MRN-1", 8, true, 8, true));
			Assert.Null(HospitalDeviceVitalRules.WhoRefusal("211-A", null, 8, true, null, false));
			Assert.Null(HospitalDeviceVitalRules.WhoRefusal(null, "MRN-1", null, false, 8, true));
			Assert.Equal("The patient in bed {0} is not {1}: the monitor may have moved", HospitalDeviceVitalRules.WhoRefusal("211-A", "MRN-2", 8, true, 9, true));
			Assert.Equal("Nobody is in bed {0}", HospitalDeviceVitalRules.WhoRefusal("211-B", null, null, true, null, false));
			Assert.Equal("No such bed: {0}", HospitalDeviceVitalRules.WhoRefusal("ZZ", null, null, false, null, false));
			Assert.Equal("The patient {1} is not admitted", HospitalDeviceVitalRules.WhoRefusal(null, "MRN-3", null, false, null, true));
			Assert.Equal("The message names no bed (PV1-3) and no patient (PID-3)", HospitalDeviceVitalRules.WhoRefusal(null, null, null, false, null, false));
		}

		[Fact]
		public void A_nurse_charts_with_consciousness_and_not_a_stale_set()
		{
			var now = new DateTime(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc);
			var v = new HospitalDeviceVital { Status = HospitalDeviceVitalStatus.Pending, TakenAt = now.AddMinutes(-10) };
			Assert.Equal("Choose the level of consciousness", HospitalDeviceVitalRules.AcceptRefusal(v, new HospitalVitalAcceptInput(), now));
			Assert.Null(HospitalDeviceVitalRules.AcceptRefusal(v, new HospitalVitalAcceptInput { Consciousness = HospitalConsciousness.Alert }, now));
			Assert.Equal("A reading older than 12 hours is not charted: take a new set",
				HospitalDeviceVitalRules.AcceptRefusal(new HospitalDeviceVital { Status = HospitalDeviceVitalStatus.Pending, TakenAt = now.AddHours(-13) }, new HospitalVitalAcceptInput { Consciousness = HospitalConsciousness.Alert }, now));
			v.TakenAt = now; v.Status = HospitalDeviceVitalStatus.Accepted;
			Assert.Equal("This reading was already decided", HospitalDeviceVitalRules.AcceptRefusal(v, new HospitalVitalAcceptInput { Consciousness = HospitalConsciousness.Alert }, now));
		}

		[Fact]
		public void Times_are_read_with_their_offset()
		{
			Assert.Equal(new DateTime(2026, 11, 18, 19, 0, 0, DateTimeKind.Utc), HospitalDeviceVitalRules.Time("20261118220000+0300"));
			Assert.Equal(new DateTime(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc), HospitalDeviceVitalRules.Time("202611182200+0000"));
			Assert.Null(HospitalDeviceVitalRules.Time("yesterday"));
			Assert.Equal(38.5m, HospitalDeviceVitalRules.Celsius(101.3m, "[degF]"));
			Assert.Null(HospitalDeviceVitalRules.Celsius(310m, "K"));
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalDeviceVitalModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h201_device_vitals.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalDeviceVital), "HospitalDeviceVitals");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalDeviceVital), "HospitalDeviceVitals");
			foreach (var st in HospitalDeviceVitalStatus.All) Assert.Contains("'" + st + "'", s);
		}
	}
}
