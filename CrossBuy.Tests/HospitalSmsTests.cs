using CrossBuy.BL.Comm;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H70 — SMS to patients, with no database: a template with its values put in, the numbers a message can go
	// to, how many SMS parts a body takes (Arabic is 70, Latin 160), what refuses a template or a typed message, that the
	// stand-in sender never pretends to send, and the tables against their slice.
	// ==========================================================================================
	public class HospitalSmsTests
	{
		[Fact]
		public void A_template_takes_its_values_and_drops_one_it_has_none_for()
		{
			var v = new Dictionary<string, string?> { ["patient"] = "سارة", ["doctor"] = "د. خالد", ["date"] = "10/11/2026", ["time"] = "09:30" };
			Assert.Equal("عزيزي سارة، تم حجز موعدك مع د. خالد يوم 10/11/2026 الساعة 09:30.",
				HospitalSmsRules.Render(HospitalSmsRules.DefaultTemplate(HospitalSmsKind.Booked).Ar, v));
			Assert.Equal("Hello Sara", HospitalSmsRules.Render("Hello {patient} {hospital}", new Dictionary<string, string?> { ["patient"] = "Sara" }));
			foreach (var k in HospitalSmsKind.Templated)
			{
				var (ar, en) = HospitalSmsRules.DefaultTemplate(k);
				Assert.Null(HospitalSmsRules.TemplateRefusal(new HospitalSmsTemplateInput { Kind = k, BodyAr = ar, BodyEn = en }));
			}
		}

		[Fact]
		public void Only_a_real_mobile_number_is_written()
		{
			Assert.Equal("+201001234567", HospitalSmsRules.Phone("+20 100 123 4567"));
			Assert.Equal("96501234567", HospitalSmsRules.Phone("965-0123-4567"));
			Assert.Equal("+96501234567", HospitalSmsRules.Phone("+965 01234567"));
			Assert.Null(HospitalSmsRules.Phone("1234"));
			Assert.Null(HospitalSmsRules.Phone("call me"));
			Assert.Null(HospitalSmsRules.Phone("0100x123456"));
			Assert.Null(HospitalSmsRules.Phone(null));
		}

		[Fact]
		public void Arabic_takes_more_parts_than_latin()
		{
			Assert.Equal(1, HospitalSmsRules.Parts(new string('a', 160)));
			Assert.Equal(2, HospitalSmsRules.Parts(new string('a', 161)));
			Assert.Equal(1, HospitalSmsRules.Parts(new string('ب', 70)));
			Assert.Equal(2, HospitalSmsRules.Parts(new string('ب', 71)));
			Assert.Equal(3, HospitalSmsRules.Parts(new string('ب', 140)));
			Assert.Equal(0, HospitalSmsRules.Parts(""));
		}

		[Fact]
		public void A_template_or_a_message_is_refused_for_what_it_lacks()
		{
			Assert.Equal("not_found", HospitalSmsRules.TemplateRefusal(new HospitalSmsTemplateInput { Kind = HospitalSmsKind.Custom, BodyAr = "x", BodyEn = "x" }));
			Assert.Equal("Write the message in Arabic and in English", HospitalSmsRules.TemplateRefusal(new HospitalSmsTemplateInput { Kind = HospitalSmsKind.Booked, BodyAr = "x", BodyEn = " " }));
			var clinic = new HospitalSmsTemplateInput { Kind = HospitalSmsKind.Booked, BodyAr = "{clinic}", BodyEn = "x" };
			Assert.Equal("Unknown placeholder: {0}", HospitalSmsRules.TemplateRefusal(clinic));
			Assert.Equal("{clinic}", HospitalSmsRules.UnknownPlaceholder(clinic));
			Assert.Null(HospitalSmsRules.UnknownPlaceholder(new HospitalSmsTemplateInput { BodyAr = "{patient}", BodyEn = "{date}" }));
			Assert.Equal("A message is at most 480 characters", HospitalSmsRules.TemplateRefusal(new HospitalSmsTemplateInput { Kind = HospitalSmsKind.Booked, BodyAr = new string('x', 481), BodyEn = "x" }));
			Assert.Equal("Write the message", HospitalSmsRules.ComposeRefusal(" ", "+201001234567"));
			Assert.Equal("Write a valid mobile number", HospitalSmsRules.ComposeRefusal("hello", null));
			Assert.Null(HospitalSmsRules.ComposeRefusal("hello", "+201001234567"));
			Assert.True(HospitalSmsRules.CanCancel(HospitalSmsStatus.Queued));
			Assert.True(HospitalSmsRules.CanCancel(HospitalSmsStatus.Failed));
			Assert.False(HospitalSmsRules.CanCancel(HospitalSmsStatus.Sent));
			Assert.False(HospitalSmsRules.CanCancel(HospitalSmsStatus.Skipped));
		}

		[Fact]
		public async Task Without_a_provider_nothing_is_pretended_sent()
		{
			ISmsSender s = new NoSmsSender();
			Assert.False(s.Configured);
			var r = await s.SendAsync("+201001234567", "x");
			Assert.False(r.Ok);
			Assert.Null(r.ProviderRef);
		}

		[Fact]
		public void The_tables_match_their_slice_and_move_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h70_sms.sql");
			var m = HospitalSliceParity.Model(HospitalSmsModel.Configure);
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalSmsMessage), "HospitalSmsMessages");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalSmsMessage), "HospitalSmsMessages");
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalSmsTemplate), "HospitalSmsTemplates");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalSmsTemplate), "HospitalSmsTemplates");
			HospitalSliceParity.NamesItsTable(m, s, typeof(HospitalSmsOptOut), "HospitalSmsOptOuts");
			HospitalSliceParity.ColumnsMatch(m, s, typeof(HospitalSmsOptOut), "HospitalSmsOptOuts");
			foreach (var x in HospitalSmsKind.All.Concat(HospitalSmsStatus.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalSmsMessage));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalSmsOptOut));
		}
	}
}
