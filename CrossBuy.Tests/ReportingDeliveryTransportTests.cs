using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ============================================================================================
    // ADR-037 §10 #2 — THE MAIL TRANSPORT, AND THE SWITCH THAT KEEPS IT SHUT.
    //
    // NOT ONE OF THESE TESTS OPENS A SOCKET, AND THAT IS THE DESIGN RATHER THAN A LIMITATION.
    // This deployment has a LIVE provider in appsettings.json ("Smtp:Enabled": true, a real Gmail
    // account and app password). A test that proved delivery by delivering would send real mail from
    // the product's real account every time the suite ran. So the property under test is the GATE,
    // which is where the defect would be, and real transmission is left to an operator who has
    // approved it. ReportingScheduleTests already proves the runner's end-to-end path with a sender
    // that reports Skipped.
    //
    // THE POSITIVE CONTROL IS LOAD-BEARING. A fail-closed gate passes every negative test by being
    // broken shut, so Both_switches_on_makes_the_transport_available exists to prove the gate can
    // open. Without it this file would pass just as happily against `IsAvailable => false`.
    // ============================================================================================
    public class ReportingDeliveryTransportTests
    {
        private static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v =>
                    new KeyValuePair<string, string?>(v.Key, v.Value)))
                .Build();

        private static SmtpReportMailSender Sender(IConfiguration configuration) =>
            new(configuration, NullLogger<SmtpReportMailSender>.Instance);

        /// A transport that is configured and working, as this deployment's really is.
        private static (string, string)[] LiveSmtp() => new[]
        {
            ("Smtp:Enabled", "true"),
            ("Smtp:Host", "smtp.example.invalid"),
            ("Smtp:Port", "587"),
            ("Smtp:From", "reports@example.invalid"),
            ("Smtp:FromName", "CrossBuy"),
        };

        private static ReportMailMessage Message(string to = "someone@example.invalid") => new()
        {
            To = to,
            Subject = "Monthly sales",
            Body = "Attached.",
            AttachmentFileName = "sales.csv",
            AttachmentContentType = "text/csv",
            Attachment = new byte[] { 1, 2, 3 },
        };

        // ---- the registration boundary ---------------------------------------------------------

        // ADDING THE CLASS MUST NOT ACTIVATE IT. The platform still binds the null sender, so a build
        // that merely contains SmtpReportMailSender delivers nothing. If someone later registers it
        // inside AddCrossBusinessReporting, this fails and asks them to justify it.
        [Fact]
        public void The_platform_still_binds_the_null_sender_by_default()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCrossBusinessReporting();

            var descriptor = services.Single(d => d.ServiceType == typeof(IReportMailSender));

            Assert.Equal(typeof(NullReportMailSender), descriptor.ImplementationType);
        }

        // ---- the two switches mean different things --------------------------------------------

        // THE DEFECT THIS PINS. Keying availability on "Smtp:Enabled" alone would mean that registering
        // the sender in this deployment immediately began emailing financial attachments unattended,
        // because that flag is already true for interactive mail. The reporting switch is absent here,
        // exactly as it is absent from every appsettings file in the repository.
        [Fact]
        public void A_live_transport_is_not_enough_reporting_must_also_be_permitted()
        {
            var sender = Sender(Config(LiveSmtp()));

            Assert.False(sender.IsAvailable);
            Assert.Contains("Reporting:Delivery:Email:Enabled", sender.UnavailableReason);
        }

        // The refusal must name the switch that is ACTUALLY off. An operator told to check SMTP when
        // SMTP is fine will conclude the transport is broken and go looking in the wrong place.
        [Fact]
        public void Permitted_but_unconfigured_blames_smtp_and_not_the_reporting_switch()
        {
            var sender = Sender(Config(
                ("Reporting:Delivery:Email:Enabled", "true"),
                ("Smtp:Enabled", "false")));

            Assert.False(sender.IsAvailable);
            Assert.Contains("SMTP is not configured", sender.UnavailableReason);
            Assert.DoesNotContain("Reporting:Delivery:Email:Enabled", sender.UnavailableReason);
        }

        // Enabled with no Host, and enabled with no From, are both "not configured". IsConfigured
        // requires all three, so a half-filled section cannot present itself as a working transport.
        [Theory]
        [InlineData("Smtp:Host")]
        [InlineData("Smtp:From")]
        public void A_half_configured_section_is_not_a_transport(string omitted)
        {
            var settings = LiveSmtp()
                .Where(v => v.Item1 != omitted)
                .Append(("Reporting:Delivery:Email:Enabled", "true"))
                .ToArray();

            Assert.False(Sender(Config(settings)).IsAvailable);
        }

        // ---- THE POSITIVE CONTROL --------------------------------------------------------------

        // Proves the gate OPENS. Nothing is sent: IsAvailable is a property and the host is a
        // .invalid domain, which RFC 2606 guarantees will never resolve.
        [Fact]
        public void Both_switches_on_makes_the_transport_available()
        {
            var settings = LiveSmtp().Append(("Reporting:Delivery:Email:Enabled", "true")).ToArray();

            var sender = Sender(Config(settings));

            Assert.True(sender.IsAvailable);
            Assert.Null(sender.UnavailableReason);
            Assert.Equal("Smtp", sender.SenderName);
        }

        // ---- the send path refuses rather than pretending --------------------------------------

        // SKIPPED, NOT SENT, and NOT thrown. A null-object transport that reported success would
        // produce an audit trail that lies — ADR-037's own words — and the same applies to a real
        // transport that is switched off.
        [Fact]
        public async Task Sending_while_unpermitted_is_skipped_with_the_reason()
        {
            var outcome = await Sender(Config(LiveSmtp())).SendAsync(Message());

            Assert.Equal(ReportDeliveryStatus.Skipped, outcome.Status);
            Assert.Contains("Reporting:Delivery:Email:Enabled", outcome.Detail);
        }

        // A malformed address is rejected BEFORE any connection is attempted, so one bad row in a
        // distribution list costs nothing and cannot be confused with an unreachable server.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-an-address")]
        [InlineData("two@@at.example")]
        public async Task A_malformed_address_fails_without_reaching_the_network(string address)
        {
            var settings = LiveSmtp().Append(("Reporting:Delivery:Email:Enabled", "true")).ToArray();

            var outcome = await Sender(Config(settings)).SendAsync(Message(address));

            Assert.Equal(ReportDeliveryStatus.Failed, outcome.Status);
            Assert.Contains("not a usable email address", outcome.Detail);
        }

        // ---- body mode: plain text is the default, HTML is the exception ------------------------

        // THE DEFECT THIS CLOSES, found by reading ReportDeliveryEnvelope's own contract rather than
        // by a failure: the first implementation set IsBodyHtml unconditionally. The envelope
        // documents Body as PLAIN TEXT — "a report body is a covering note, the report itself is the
        // attachment" — so declaring HTML collapsed every newline into a space and silently ate
        // anything shaped like a tag. "invoices <30 days" lost the clause that carried the meaning.
        [Theory]
        [InlineData("Attached.\nGenerated overnight.")]
        [InlineData("Customers with invoices <30 days old.")]
        [InlineData("R&D spend, Q3.")]
        public void A_covering_note_with_an_attachment_is_sent_as_plain_text(string body)
        {
            var message = new ReportMailMessage
            {
                To = "someone@example.invalid",
                Subject = "s",
                Body = body,
                AttachmentFileName = "r.csv",
                AttachmentContentType = "text/csv",
                Attachment = new byte[] { 1 },
            };

            Assert.False(SmtpReportMailSender.IsHtmlBody(message));
        }

        // The inline path is the one that carries markup, and it is also the one with no attachment.
        [Theory]
        [InlineData("<!doctype html><html><body>x</body></html>")]
        [InlineData("Here is the report.\n\n<table><tr><td>1</td></tr></table>")]
        public void An_inline_report_with_no_attachment_is_sent_as_html(string body)
        {
            var message = new ReportMailMessage { To = "a@example.invalid", Subject = "s", Body = body };

            Assert.True(SmtpReportMailSender.IsHtmlBody(message));
        }

        // An attachment wins even when the covering note happens to contain markup, because the
        // attachment is what makes it the covering-note path.
        [Fact]
        public void An_attachment_forces_plain_text_even_if_the_note_contains_markup()
        {
            var message = new ReportMailMessage
            {
                To = "a@example.invalid",
                Subject = "s",
                Body = "<table> appears in this note",
                Attachment = new byte[] { 1 },
            };

            Assert.False(SmtpReportMailSender.IsHtmlBody(message));
        }

        // ---- the channel, end to end, over a FAKE transport -------------------------------------

        /// Records what it was asked to send and sends nothing. The only safe way to exercise the
        /// channel's fan-out behaviour against a deployment whose SMTP credentials are real.
        private sealed class FakeMailSender : IReportMailSender
        {
            private readonly ReportDeliveryOutcome _outcome;
            public List<ReportMailMessage> Sent { get; } = new();

            public FakeMailSender(ReportDeliveryOutcome? outcome = null) =>
                _outcome = outcome ?? ReportDeliveryOutcome.Sent("fake");

            public string SenderName => "fake";
            public bool IsAvailable => true;
            public string? UnavailableReason => null;

            public Task<ReportDeliveryOutcome> SendAsync(ReportMailMessage message,
                CancellationToken cancellationToken = default)
            {
                Sent.Add(message);
                return Task.FromResult(_outcome);
            }
        }

        private static EmailReportDeliveryChannel Channel(IReportMailSender sender) =>
            new(sender, NullLogger<EmailReportDeliveryChannel>.Instance);

        private static ReportDeliveryEnvelope Envelope(bool inline = false, string address = "a@example.invalid") =>
            new()
            {
                ChannelKey = ReportDeliveryChannels.Email,
                Address = address,
                Subject = "Monthly sales",
                Body = "Covering note.",
                Inline = inline,
                Artifact = new ReportArtifact
                {
                    FileName = "sales.csv",
                    ContentType = "text/csv",
                    Content = System.Text.Encoding.UTF8.GetBytes("Name\r\nACME\r\n"),
                },
            };

        // The attachment reaches the transport intact, with its name and type.
        [Fact]
        public async Task An_attached_report_reaches_the_transport_whole()
        {
            var sender = new FakeMailSender();

            var outcome = await Channel(sender).DeliverAsync(Envelope());

            Assert.Equal(ReportDeliveryStatus.Sent, outcome.Status);
            var sent = Assert.Single(sender.Sent);
            Assert.Equal("sales.csv", sent.AttachmentFileName);
            Assert.Equal("text/csv", sent.AttachmentContentType);
            Assert.Equal("Name\r\nACME\r\n", System.Text.Encoding.UTF8.GetString(sent.Attachment!));
        }

        // Inline sends the report IN the body and attaches nothing — the discriminator the body-mode
        // decision above relies on, asserted here rather than assumed.
        [Fact]
        public async Task An_inline_report_travels_in_the_body_with_no_attachment()
        {
            var sender = new FakeMailSender();

            await Channel(sender).DeliverAsync(Envelope(inline: true));

            var sent = Assert.Single(sender.Sent);
            Assert.Null(sent.Attachment);
            Assert.Null(sent.AttachmentFileName);
            Assert.Contains("ACME", sent.Body);
            Assert.Contains("Covering note.", sent.Body);
        }

        // ONE BAD MAILBOX MUST NOT CANCEL THE LIST. The channel validates before reaching the
        // transport, so a malformed address costs nothing and the sender is never called.
        [Fact]
        public async Task A_malformed_recipient_never_reaches_the_transport()
        {
            var sender = new FakeMailSender();

            var outcome = await Channel(sender).DeliverAsync(Envelope(address: "not-an-address"));

            Assert.Equal(ReportDeliveryStatus.Failed, outcome.Status);
            Assert.Empty(sender.Sent);
        }

        // A transport that reports Failed is reported as Failed, not swallowed into success.
        [Fact]
        public async Task A_transport_failure_is_reported_rather_than_swallowed()
        {
            var sender = new FakeMailSender(ReportDeliveryOutcome.Failed("mailbox full"));

            var outcome = await Channel(sender).DeliverAsync(Envelope());

            Assert.Equal(ReportDeliveryStatus.Failed, outcome.Status);
            Assert.Contains("mailbox full", outcome.Detail);
        }

        // ---- the channel's own behaviour over an unavailable sender -----------------------------

        // The CHANNEL stays available while the SENDER is not, which is what makes "40 generated, 0
        // delivered" visible. This pins that the pairing still behaves that way with the real class
        // in place of the null one.
        [Fact]
        public void The_email_channel_is_available_even_when_the_transport_is_not()
        {
            var sender = Sender(Config(LiveSmtp()));
            var channel = new EmailReportDeliveryChannel(
                sender, NullLogger<EmailReportDeliveryChannel>.Instance);

            Assert.False(sender.IsAvailable);
            Assert.True(channel.IsAvailable);
            Assert.Equal(ReportDeliveryChannels.Email, channel.ChannelKey);
        }
    }
}
