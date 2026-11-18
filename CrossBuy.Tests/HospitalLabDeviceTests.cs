using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H198 — analyser results by HL7 v2 ORU^R01. What must never happen: a result filed on the wrong order, a
	// preliminary result filed as final, a value filed in other units than its analyte's, a message filed twice, or a
	// message we could not read acknowledged as accepted.
	// ==========================================================================================
	public class HospitalLabDeviceTests
	{
		private const string Cbc =
			"MSH|^~\\&|SYSMEX-XN|LAB|CROSSBUSINESS|HOSPITAL|20261118103000||ORU^R01^ORU_R01|MSG000123|P|2.5\r" +
			"PID|1||MRN-100023^^^HOSP||TEST^PATIENT\r" +
			"OBR|1|LAB-000016|SX-889|CBC^Complete blood count\r" +
			"OBX|1|NM|WBC^White blood cells||7.2|10*9/L|4.0-11.0|N|||F\r" +
			"OBX|2|NM|HGB^Haemoglobin||13.5|g/dL|13-17|N|||F\r" +
			"OBX|3|NM|PLT^Platelets||250|10*9/L|150-400|N|||P\r" +
			"OBX|4|NM|XYZ^Unknown||1|U||N|||F\r";

		[Fact]
		public void An_ORU_message_is_read_field_by_field()
		{
			var (m, why) = HospitalHl7.ParseOru(Cbc);
			Assert.Null(why);
			Assert.Equal(("SYSMEX-XN", "MSG000123", "ORU^R01", "LAB-000016", "SX-889"), (m!.Device, m.MessageId, m.Type, m.Placer, m.Filler));
			Assert.Equal(4, m.Results.Count);
			Assert.Equal(new HospitalHl7Obx("HGB", "Haemoglobin", "13.5", "g/dL", "F"), m.Results[1]);
			Assert.False(HospitalHl7.Files(m.Results[2]));   // preliminary: never filed
			Assert.True(HospitalHl7.Files(m.Results[0]));
			Assert.True(HospitalHl7.Files(m.Results[0] with { Status = "C" }));   // a correction is filed
			// line feeds instead of carriage returns, as a file may carry them
			Assert.Null(HospitalHl7.ParseOru(Cbc.Replace("\r", "\n")).Refusal);
		}

		[Fact]
		public void A_message_that_cannot_be_filed_says_why()
		{
			Assert.Equal("The message is empty", HospitalHl7.ParseOru(" ").Refusal);
			Assert.Equal("This is not an HL7 message (no MSH segment)", HospitalHl7.ParseOru("PID|1").Refusal);
			Assert.Equal("Only result messages (ORU^R01) are read", HospitalHl7.ParseOru(Cbc.Replace("ORU^R01^ORU_R01", "ADT^A01")).Refusal);
			Assert.Equal("The message names no sending device (MSH-3)", HospitalHl7.ParseOru(Cbc.Replace("|SYSMEX-XN|", "||")).Refusal);
			Assert.Equal("The message has no control id (MSH-10)", HospitalHl7.ParseOru(Cbc.Replace("|MSG000123|", "||")).Refusal);
			Assert.Equal("The message names no order (OBR-2 or OBR-3)", HospitalHl7.ParseOru(Cbc.Replace("OBR|1|LAB-000016|SX-889|", "OBR|1|||")).Refusal);
			Assert.Equal("The message holds no results (OBX)", HospitalHl7.ParseOru(string.Join("\r", Cbc.Split('\r').Where(s => !s.StartsWith("OBX")))).Refusal);
		}

		[Fact]
		public void The_acknowledgement_names_the_message_and_its_outcome()
		{
			var ack = HospitalHl7.Ack("SYSMEX-XN", "MSG000123", "AE", "XYZ (unknown code)", new DateTime(2026, 11, 18, 10, 30, 5));
			Assert.StartsWith("MSH|^~\\&|CROSSBUSINESS|HOSPITAL|SYSMEX-XN||20261118103005||ACK^R01|", ack);
			Assert.Contains("\rMSA|AE|MSG000123|XYZ (unknown code)\r", ack);
		}

		[Fact]
		public void Units_must_agree_and_the_status_counts_what_was_filed()
		{
			Assert.True(HospitalLabDeviceRules.SameUnit("g/dL", "G/DL"));
			Assert.True(HospitalLabDeviceRules.SameUnit(null, "g/dL"));
			Assert.False(HospitalLabDeviceRules.SameUnit("g/L", "g/dL"));
			Assert.Equal(HospitalDeviceMessageStatus.Accepted, HospitalLabDeviceRules.Status(3, 0));
			Assert.Equal(HospitalDeviceMessageStatus.Partial, HospitalLabDeviceRules.Status(2, 2));
			Assert.Equal(HospitalDeviceMessageStatus.Refused, HospitalLabDeviceRules.Status(0, 4));
		}

		[Fact]
		public void The_tables_match_their_slice()
		{
			var model = HospitalSliceParity.Model(HospitalLabDeviceModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h198_lab_devices.sql");
			foreach (var (t, n) in new[] { (typeof(HospitalDeviceMessage), "HospitalDeviceMessages"), (typeof(HospitalDeviceCodeMap), "HospitalDeviceCodeMaps") })
			{
				HospitalSliceParity.NamesItsTable(model, s, t, n);
				HospitalSliceParity.ColumnsMatch(model, s, t, n);
			}
			foreach (var st in HospitalDeviceMessageStatus.All) Assert.Contains("'" + st + "'", s);
		}
	}
}
