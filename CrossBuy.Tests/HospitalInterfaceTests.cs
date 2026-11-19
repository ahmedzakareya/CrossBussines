using CrossBuy.BL.Hospital;
using CrossBuy.Controllers;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H215 — the machine interface channel. What must never happen: a key kept in clear, two keys alike, a
	// result message acknowledged as accepted when it was refused, an acknowledgement that breaks on the message's own
	// separators, or a machine endpoint open without saying why it is.
	// ==========================================================================================
	public class HospitalInterfaceTests
	{
		private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);

		[Fact]
		public void A_key_is_random_and_kept_only_as_its_hash()
		{
			var a = HospitalInterfaceRules.NewKey(); var b = HospitalInterfaceRules.NewKey();
			Assert.NotEqual(a, b);
			Assert.Equal(48, a.Length);
			var h = HospitalInterfaceRules.Hash(a);
			Assert.Equal(64, h.Length);
			Assert.DoesNotContain(a, h);
			Assert.Equal(h, HospitalInterfaceRules.Hash(a));
		}

		[Theory]
		[InlineData("Accepted", "AA")]
		[InlineData("Duplicate", "AA")]   // nothing more to do: the sender must not send it again
		[InlineData("Partial", "AE")]
		[InlineData("Refused", "AR")]
		public void The_acknowledgement_says_what_became_of_the_message(string status, string code) => Assert.Equal(code, HospitalInterfaceRules.AckCode(status));

		[Fact]
		public void The_acknowledgement_names_the_message_and_escapes_its_text()
		{
			var ack = HospitalInterfaceRules.Ack("MSG-77", "AE", "code K^X|2 unknown", Now);
			var lines = ack.Split('\r', StringSplitOptions.RemoveEmptyEntries);
			Assert.StartsWith("MSH|^~\\&|CROSSBUSINESS|HOSPITAL|", lines[0]);
			Assert.Contains("|ACK^R01^ACK|", lines[0]);
			Assert.Equal("MSA|AE|MSG-77|code K\\S\\X\\F\\2 unknown", lines[1]);
		}

		[Fact]
		public void The_control_id_is_read_even_from_a_message_that_cannot_be_filed()
		{
			Assert.Equal("CTRL-1", HospitalInterfaceRules.ControlId("MSH|^~\\&|COBAS|LAB|||20261119||ORU^R01|CTRL-1|P|2.5\rPID|1"));
			Assert.Equal("X9", HospitalInterfaceRules.ControlId("\nMSH#^~\\&#A#B###T##ADT^A01#X9#P#2.5"));
			Assert.Null(HospitalInterfaceRules.ControlId("hello"));
		}

		[Fact]
		public void An_interface_has_a_code_a_name_and_one_kind()
		{
			Assert.Null(HospitalInterfaceRules.Refusal(new() { Code = "COBAS-1", Name = "Cobas c311", Kind = HospitalInterfaceKind.LabResults }));
			Assert.NotNull(HospitalInterfaceRules.Refusal(new() { Code = "a b", Name = "x", Kind = HospitalInterfaceKind.LabResults }));
			Assert.NotNull(HospitalInterfaceRules.Refusal(new() { Code = "MWL", Name = " ", Kind = HospitalInterfaceKind.ImagingWorklist }));
			Assert.NotNull(HospitalInterfaceRules.Refusal(new() { Code = "MWL", Name = "x", Kind = "Everything" }));
		}

		[Fact]
		public void Every_machine_endpoint_declares_why_it_is_open()
		{
			foreach (var m in typeof(HospitalInterfaceApiController).GetMethods().Where(m => m.DeclaringType == typeof(HospitalInterfaceApiController) && m.IsPublic))
			{
				Assert.NotNull(m.GetCustomAttributes(typeof(AllowAnonymousAttribute), false).FirstOrDefault());
				Assert.NotNull(m.GetCustomAttributes(typeof(CrossBuy.Models.Platform.AnonymousByDesignAttribute), false).FirstOrDefault());
			}
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalInterfaceModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h215_interfaces.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalInterface), "HospitalInterfaces");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalInterface), "HospitalInterfaces");
			foreach (var k in HospitalInterfaceKind.All) Assert.Contains("'" + k + "'", s);
		}
	}
}
