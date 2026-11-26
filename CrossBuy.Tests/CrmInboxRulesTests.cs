using System.Text;
using Xunit;
using I = CrossBuy.BL.CrmIdentity.CrmInboxRules;

namespace CrossBuy.Tests
{
	// CRM C4 — the inbox rules, pinned with payloads shaped like the WhatsApp Cloud API's own.
	public class CrmInboxRulesTests
	{
		const string Sample = """
		{"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[{"field":"messages","value":{
		 "messaging_product":"whatsapp","metadata":{"display_phone_number":"96522221111","phone_number_id":"1234567890"},
		 "contacts":[{"profile":{"name":"Fahad"},"wa_id":"96599001122"}],
		 "messages":[{"from":"96599001122","id":"wamid.AAA","timestamp":"1762765200","type":"text","text":{"body":"أرسل لي العقد"}},
		             {"from":"96599001122","id":"wamid.BBB","timestamp":"1762765260","type":"image","image":{"id":"m1"}}],
		 "statuses":[{"id":"wamid.OUT1","status":"read","timestamp":"1762765300","recipient_id":"96599001122"},
		             {"id":"wamid.OUT2","status":"failed","errors":[{"code":131047,"title":"Re-engagement message"}]}]}}]}]}
		""";

		[Fact]
		public void The_webhook_yields_messages_names_and_statuses()
		{
			var (msgs, sts) = I.ParseWebhook(Sample);
			Assert.Equal(2, msgs.Count);
			Assert.Equal("1234567890", msgs[0].PhoneNumberId);
			Assert.Equal("Fahad", msgs[0].Name);
			Assert.Equal("أرسل لي العقد", msgs[0].Text);
			Assert.Equal("[image]", msgs[1].Text);                    // a photo is described, never dropped
			Assert.Equal(new DateTime(2025, 11, 10, 9, 0, 0, DateTimeKind.Utc), msgs[0].AtUtc);
			Assert.Equal(new[] { ("wamid.OUT1", "read", (string?)null), ("wamid.OUT2", "failed", "Re-engagement message") }, sts.Select(s => (s.MessageId, s.Status, s.Error)));
		}

		[Fact]
		public void Only_the_exact_signature_over_the_raw_body_is_valid()
		{
			var body = Encoding.UTF8.GetBytes(Sample);
			var sig = I.Sign("app-secret", body);
			Assert.True(I.SignatureValid("app-secret", body, sig));
			Assert.False(I.SignatureValid("other-secret", body, sig));
			Assert.False(I.SignatureValid("app-secret", Encoding.UTF8.GetBytes(Sample + " "), sig));   // one byte changed
			Assert.False(I.SignatureValid("app-secret", body, null));
			Assert.False(I.SignatureValid(null, body, sig));
			Assert.False(I.SignatureValid("app-secret", body, "sha256=zz"));
		}

		[Fact]
		public void The_reply_window_is_24_hours_from_the_customers_last_message()
		{
			var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
			Assert.True(I.InsideWindow(now.AddHours(-23), now));
			Assert.False(I.InsideWindow(now.AddHours(-25), now));
			Assert.False(I.InsideWindow(null, now));
		}

		[Fact]
		public void A_status_never_moves_a_message_backwards()
		{
			Assert.True(I.Advances("Sent", "Delivered"));
			Assert.True(I.Advances("Delivered", "Read"));
			Assert.False(I.Advances("Read", "Delivered"));
			Assert.False(I.Advances("Read", "Failed"));
			Assert.True(I.Advances("Sent", "Failed"));
		}

		[Fact]
		public void Party_keys_are_stable_and_a_visitor_token_is_never_stored_raw()
		{
			Assert.Equal("wa:96599001122", I.PhoneKey("+965 9900 1122".Replace("+", "")));
			Assert.Equal("mail:fahad@aldiwan.kw", I.EmailKey(" Fahad@AlDiwan.kw "));
			var token = I.NewToken();
			Assert.DoesNotContain(token, I.VisitorKey(token));
			Assert.Equal(I.VisitorKey(token), I.VisitorKey(token));
		}
	}
}
