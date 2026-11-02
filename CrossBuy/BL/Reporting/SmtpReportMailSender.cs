using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // ADR-037 §10 #2 — THE MAIL TRANSPORT, BOUND.
    //
    // This is the class NullReportMailSender's own comment asks for. It changes nothing on its own:
    // it is NOT registered by AddCrossBusinessReporting, and binding it is a host decision, because
    // the operational questions that come with real outbound mail are the host's to answer.
    //
    // IT REUSES THE PRODUCT'S EXISTING "Smtp" SECTION AND DOES NOT INVENT A SECOND ONE. CommService
    // already binds that section with exactly this shape. Two SMTP configurations that could disagree
    // is a worse outcome than one shared one: an operator who rotates the password would otherwise fix
    // interactive mail and leave scheduled reports authenticating with the old credential.
    //
    // ────────────────────────────────────────────────────────────────────────────────────────────
    // WHY THERE IS A SECOND SWITCH, AND WHY IT IS NOT OPTIONAL.
    //
    // "Smtp:Enabled" is already TRUE in this deployment and a live provider is configured. If
    // availability were keyed on that alone, then merely REGISTERING this class would turn every
    // scheduled report into real outbound mail carrying a financial attachment — a transmission
    // nobody reviewed, triggered by a DI line. CommService records the same hazard about its own
    // SendAsync for the same reason.
    //
    // So delivery additionally requires "Reporting:Delivery:Email:Enabled". The two switches mean
    // different things and must not be collapsed:
    //
    //      Smtp:Enabled                        a transport exists and works
    //      Reporting:Delivery:Email:Enabled    reporting is ALLOWED to use it unattended
    //
    // Absent the second, IsAvailable is false and the channel records Skipped WITH THE REASON — the
    // same truthful audit trail NullReportMailSender produces, rather than a silent no-op.
    //
    // WHAT THIS CLASS STILL DOES NOT DECIDE, because they are not transport questions: whether an
    // attachment containing financial data may leave the network at all, and what the retry policy
    // is. There is no retry here. One attempt, one recorded outcome; ReportDeliveryAttempts is the
    // record and an operator can see a failure rather than having it buried under silent retries.
    // ============================================================================================
    public sealed class SmtpReportMailSender : IReportMailSender
    {
        private readonly SmtpTransportOptions _smtp;
        private readonly bool _reportingDeliveryEnabled;
        private readonly ILogger<SmtpReportMailSender> _log;

        public SmtpReportMailSender(IConfiguration configuration, ILogger<SmtpReportMailSender> log)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            _smtp = new SmtpTransportOptions();
            configuration.GetSection("Smtp").Bind(_smtp);
            _reportingDeliveryEnabled =
                configuration.GetValue<bool>("Reporting:Delivery:Email:Enabled");
            _log = log;
        }

        // Mirrors CommService.SmtpOptions deliberately — same section, same names, same defaults.
        public sealed class SmtpTransportOptions
        {
            public bool Enabled { get; set; }
            public string Host { get; set; } = "";
            public int Port { get; set; } = 587;
            public string User { get; set; } = "";
            public string Password { get; set; } = "";
            public string From { get; set; } = "";
            public string FromName { get; set; } = "CrossBuy";
            public bool EnableSsl { get; set; } = true;

            public bool IsConfigured =>
                Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
        }

        public string SenderName => "Smtp";

        public bool IsAvailable => _reportingDeliveryEnabled && _smtp.IsConfigured;

        // The reason names the ONE switch that is actually off, so an operator is not sent to read both.
        public string? UnavailableReason
        {
            get
            {
                if (IsAvailable) return null;

                if (!_reportingDeliveryEnabled)
                    return "Reporting is not permitted to send mail unattended. The SMTP transport may be " +
                           "configured, but 'Reporting:Delivery:Email:Enabled' is false, so report delivery " +
                           "is recorded as Skipped rather than Sent. Set it to true only after an owner has " +
                           "approved unattended transmission of report attachments.";

                return "SMTP is not configured. 'Smtp:Enabled' must be true and 'Smtp:Host' and 'Smtp:From' " +
                       "must be set before the reporting platform can deliver by email.";
            }
        }

        public async Task<ReportDeliveryOutcome> SendAsync(ReportMailMessage message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            // Re-asked here and not only in the channel. This method is public and a future caller that
            // reached it directly would otherwise transmit on a deployment that never approved it.
            if (!IsAvailable) return ReportDeliveryOutcome.Skipped(UnavailableReason!);

            if (!LooksLikeAddress(message.To))
                return ReportDeliveryOutcome.Failed($"'{message.To}' is not a usable email address.");

            try
            {
                var body = message.Body ?? "";

                using var mail = new MailMessage
                {
                    From = new MailAddress(_smtp.From, _smtp.FromName),
                    Subject = message.Subject ?? "",
                    Body = body,

                    // NOT "always HTML", which is what this said first and was wrong in the common case.
                    // ReportDeliveryEnvelope.Body is documented PLAIN TEXT — "a report body is a covering
                    // note, the report itself is the attachment". Declaring HTML for it collapses every
                    // newline into a space and silently eats anything shaped like a tag, so a note saying
                    // "invoices <30 days" loses the clause that matters.
                    //
                    // The channel only puts markup in the body on the INLINE path, and that path is the one
                    // that sends no attachment (EmailReportDeliveryChannel sets Attachment to null when
                    // Inline). So "no attachment AND the body actually looks like markup" is the inline
                    // case, derived from what ReportMailMessage already carries rather than guessed.
                    IsBodyHtml = IsHtmlBody(message),
                };

                // Cc vs To is the ENVELOPE's distinction and it is preserved. Collapsing a Cc into To
                // would misrepresent who a report was addressed to in the recipient's own client.
                if (message.IsCc) mail.CC.Add(message.To.Trim());
                else mail.To.Add(message.To.Trim());

                using var attachmentStream = message.Attachment is { Length: > 0 }
                    ? new MemoryStream(message.Attachment)
                    : null;

                if (attachmentStream is not null)
                {
                    var name = string.IsNullOrWhiteSpace(message.AttachmentFileName)
                        ? "report"
                        : message.AttachmentFileName!;
                    var type = string.IsNullOrWhiteSpace(message.AttachmentContentType)
                        ? "application/octet-stream"
                        : message.AttachmentContentType!;

                    mail.Attachments.Add(new Attachment(attachmentStream, name, type));
                }

                using var client = new SmtpClient(_smtp.Host, _smtp.Port)
                {
                    EnableSsl = _smtp.EnableSsl,
                    Credentials = string.IsNullOrWhiteSpace(_smtp.User)
                        ? null
                        : new NetworkCredential(_smtp.User, _smtp.Password),
                };

                await client.SendMailAsync(mail, cancellationToken);

                return ReportDeliveryOutcome.Sent($"Sent via {_smtp.Host}:{_smtp.Port}.");
            }
            catch (Exception ex)
            {
                // FAILED, NOT THROWN. IReportDeliveryChannel's contract says a delivery failure must not
                // throw, because one unreachable mailbox must not cancel the rest of a distribution list.
                // The channel also catches, and this is still the right place for the primary handling:
                // a guard that relies on its caller having one is not a guard.
                _log.LogWarning(ex, "Report mail to {Address} failed.", message.To);
                return ReportDeliveryOutcome.Failed(ex.Message);
            }
        }

        /// <summary>
        /// Whether this message's body should be sent as HTML. PUBLIC so it can be asserted without
        /// opening a socket — the decision is the part that can be wrong, and a test that had to send
        /// mail to check it would be a test nobody dares run against a live provider.
        /// </summary>
        /// <remarks>
        /// Narrow on purpose: the markers an inline report body actually opens with. A loose test
        /// ("contains a '&lt;'") would misclassify a plain covering note mentioning "&lt;30 days" as
        /// HTML — the exact failure this guards against. A flag on ReportMailMessage would beat
        /// sniffing and is recommended to the integration owner; it changes a type three classes
        /// share, so it is not made here.
        /// </remarks>
        public static bool IsHtmlBody(ReportMailMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.Attachment is { Length: > 0 }) return false;

            var body = message.Body ?? "";
            return body.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)
                || body.Contains("<html", StringComparison.OrdinalIgnoreCase)
                || body.Contains("<table", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeAddress(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            try { _ = new MailAddress(value.Trim()); return true; }
            catch { return false; }
        }
    }
}
