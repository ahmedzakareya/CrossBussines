using System;
using System.Linq;
using System.Text;
using CrossBuy.BL;
using CrossBuy.BL.Comm;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================================
    // EMAIL PHASE 2 — MAIL FROM OUTSIDE (owner, 2026-11-23). The two things the reader stands on: the TAG in an address
    // (who a message is for, unguessable) and the MIME parser (what it says). Both are pure, so they are pinned here;
    // the IMAP conversation itself was verified live against the company account (a round trip Sara -> Khaled ->
    // Sara, threaded).
    // =============================================================================================================
    public class MailInboundTests
    {
        static CommService.SmtpOptions Smtp() => new()
        {
            Enabled = true, Host = "smtp.example.com", User = "desk@example.com", Password = "secret", From = "desk@example.com",
        };

        [Fact]
        public void An_employee_tag_reads_back_to_that_employee()
        {
            var s = Smtp();
            var a = MailTags.ForEmployee(s, 1045)!;
            Assert.StartsWith("desk+e1045k", a);
            Assert.EndsWith("@example.com", a);
            Assert.Equal(('e', 1045), MailTags.Read(s, a));
            Assert.Equal(('e', 1045), MailTags.Read(s, a.ToUpperInvariant()));       // addresses are not case-sensitive
        }

        [Fact]
        public void A_guessed_or_altered_tag_is_not_ours()
        {
            var s = Smtp();
            var a = MailTags.ForMessage(s, 2031)!;
            Assert.Equal(('m', 2031), MailTags.Read(s, a));
            Assert.Null(MailTags.Read(s, a.Replace("m2031", "m2032")));                // counting ids does not work
            Assert.Null(MailTags.Read(s, "desk@example.com"));                          // the bare account carries no one
            Assert.Null(MailTags.Read(s, a.Replace("@example.com", "@other.com")));     // another domain is not this account
            var other = Smtp(); other.Password = "changed";
            Assert.Null(MailTags.Read(other, a));                                       // the check is keyed
        }

        [Fact]
        public void Our_message_id_names_its_message_and_nothing_else()
        {
            var s = Smtp();
            var id = MailTags.MessageId(s, 77);
            Assert.Equal(77, MailTags.ReadMessageId(s, id));
            Assert.Null(MailTags.ReadMessageId(s, id.Replace("cb.77.", "cb.78.")));
            Assert.Null(MailTags.ReadMessageId(s, "<abc@mail.gmail.com>"));
        }

        [Fact]
        public void A_multipart_message_in_arabic_is_read_whole()
        {
            var html = Convert.ToBase64String(Encoding.UTF8.GetBytes("<p>السلام عليكم، مرفق العرض.</p>"));
            var raw = string.Join("\r\n",
                "From: =?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("شركة النور")) + "?= <buyer@noor.example>",
                "To: desk+e1045kabcdef01@example.com",
                "Cc: a@x.example, \"B\" <b@x.example>",
                "Reply-To: orders@noor.example",
                "Subject: =?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("عرض سعر")) + "?=",
                "  =?UTF-8?Q?_=D8=B1=D9=82=D9=85_7?=",
                "Message-ID: <m1@noor.example>",
                "In-Reply-To: <cb.5.deadbeef@crossbuy.mail>",
                "Date: Mon, 23 Nov 2026 14:46:00 +0300",
                "MIME-Version: 1.0",
                "Content-Type: multipart/mixed; boundary=\"XX\"",
                "",
                "--XX",
                "Content-Type: multipart/alternative; boundary=YY",
                "",
                "--YY",
                "Content-Type: text/plain; charset=utf-8",
                "Content-Transfer-Encoding: quoted-printable",
                "",
                "Hello =3D world=",
                " soft",
                "--YY",
                "Content-Type: text/html; charset=utf-8",
                "Content-Transfer-Encoding: base64",
                "",
                html,
                "--YY--",
                "--XX",
                "Content-Type: application/pdf",
                "Content-Disposition: attachment; filename*=utf-8''%D8%B9%D8%B1%D8%B6.pdf",
                "Content-Transfer-Encoding: base64",
                "",
                Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.4")),
                "--XX--",
                "");
            var m = MimeMail.Parse(Encoding.Latin1.GetBytes(raw));

            Assert.Equal("buyer@noor.example", m.From);
            Assert.Equal("شركة النور", m.FromName);
            Assert.Equal("orders@noor.example", m.ReplyTo);
            Assert.Equal(new[] { "desk+e1045kabcdef01@example.com" }, m.To);
            Assert.Equal(new[] { "a@x.example", "b@x.example" }, m.Cc);
            Assert.Equal("عرض سعر رقم 7", m.Subject);
            Assert.Equal("<m1@noor.example>", m.MessageId);
            Assert.Equal("<cb.5.deadbeef@crossbuy.mail>", m.InReplyTo);
            Assert.Equal(new DateTime(2026, 11, 23, 11, 46, 0, DateTimeKind.Utc), m.Date);
            Assert.Equal("Hello = world soft", m.Text!.Trim().Replace("\n", " "));
            Assert.Equal("<p>السلام عليكم، مرفق العرض.</p>", m.Html!.Trim());
            var file = Assert.Single(m.Files);
            Assert.Equal("عرض.pdf", file.name);
            Assert.Equal("%PDF-1.4", Encoding.ASCII.GetString(file.data));
        }

        [Fact]
        public void A_single_part_windows_1256_message_is_decoded_by_its_charset()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var body = Encoding.GetEncoding(1256).GetBytes("مرحبا");
            var head = Encoding.ASCII.GetBytes("From: x@y.example\r\nSubject: hi\r\nContent-Type: text/plain; charset=windows-1256\r\n\r\n");
            var m = MimeMail.Parse(head.Concat(body).ToArray());
            Assert.Equal("مرحبا", m.Text);
            Assert.Null(m.Html);
            Assert.Empty(m.Files);
        }
    }
}
