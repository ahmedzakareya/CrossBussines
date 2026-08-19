using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — EMAIL DELIVERY ARCHITECTURE.
    //
    // Layered so that each layer has exactly one reason to change:
    //
    //      IReportDeliveryService     — orchestrates: fan a result out to a schedule's recipients, record attempts
    //        └─ IReportDeliveryChannel — one destination KIND (Email today; Chat/Webhook/Storage later)
    //             └─ IReportMailSender — the actual transport (SMTP / the platform's Comm outbox / a provider API)
    //
    // WHY THE TRANSPORT IS A SEAM AND NOT WIRED TO THE EXISTING MAIL CODE.
    // CLAUDE.md records a standing decision for parallel tracks: do not bind to the other team's queue and
    // dispatcher while their work is uncommitted, because their change or rollback would then reach our path. The
    // Comm outbox (comm_outbox_slice_003.sql, CommMessages dispatch) is exactly such a queue. So delivery is built
    // against IReportMailSender and the shipped implementation SKIPS — it does not send, and it does not pretend
    // to. Binding it later is one class and one DI line.
    //
    // WHAT "SKIPPED" MEANS, and why it is not "Sent".
    // A report nobody received must never read as delivered. ReportDeliveryStatus.Skipped is recorded with the
    // reason, the attempt row exists, and an operator can see that N reports were generated and 0 were delivered.
    // A null-object mailer that returned success would produce an audit trail that lies.
    // ============================================================================================

    public static class ReportDeliveryChannels
    {
        public const string Email = "Email";
    }

    // One artifact addressed to one place.
    public sealed class ReportDeliveryEnvelope
    {
        public required string ChannelKey { get; init; }
        public required string Address { get; init; }
        public bool IsCc { get; init; }

        public required string Subject { get; init; }

        // Plain-text body. Deliberately not HTML: a report body is a covering note, the report itself is the
        // attachment, and an HTML body would need the same escaping discipline as the renderer for no gain.
        public required string Body { get; init; }

        public required ReportArtifact Artifact { get; init; }

        // true = put the report in the message body instead of attaching it. Only meaningful for inline HTML.
        public bool Inline { get; init; }

        public int? RecipientEmployeeId { get; init; }
        public Guid? CorrelationId { get; init; }
    }

    public sealed class ReportDeliveryOutcome
    {
        public ReportDeliveryStatus Status { get; init; }
        public string? Detail { get; init; }

        public static ReportDeliveryOutcome Sent(string? detail = null) =>
            new() { Status = ReportDeliveryStatus.Sent, Detail = detail };

        public static ReportDeliveryOutcome Failed(string detail) =>
            new() { Status = ReportDeliveryStatus.Failed, Detail = detail };

        public static ReportDeliveryOutcome Skipped(string detail) =>
            new() { Status = ReportDeliveryStatus.Skipped, Detail = detail };
    }

    // A destination KIND.
    public interface IReportDeliveryChannel
    {
        string ChannelKey { get; }

        bool IsAvailable { get; }

        // Must not throw for a delivery failure — a failed recipient returns Failed so the other recipients still
        // get their copy. Throwing would make one bad address cancel the whole fan-out.
        Task<ReportDeliveryOutcome> DeliverAsync(ReportDeliveryEnvelope envelope,
            CancellationToken cancellationToken = default);
    }

    // ---- the transport seam ------------------------------------------------------------------------------
    public sealed class ReportMailMessage
    {
        public required string To { get; init; }
        public bool IsCc { get; init; }
        public required string Subject { get; init; }
        public required string Body { get; init; }
        public string? AttachmentFileName { get; init; }
        public string? AttachmentContentType { get; init; }
        public byte[]? Attachment { get; init; }
    }

    public interface IReportMailSender
    {
        string SenderName { get; }
        bool IsAvailable { get; }
        string? UnavailableReason { get; }

        Task<ReportDeliveryOutcome> SendAsync(ReportMailMessage message,
            CancellationToken cancellationToken = default);
    }

    // The shipped sender: records the intent, sends nothing, says so.
    //
    // Binding a real one is a small class — SMTP via System.Net.Mail, or an insert into the platform's Comm outbox
    // once that work is committed. Whichever it is, it belongs to a change that also owns the operational
    // questions this slice must not answer for someone else: which From address, what retry policy, and whether an
    // attachment containing financial data may leave the network at all.
    public sealed class NullReportMailSender : IReportMailSender
    {
        public string SenderName => "none";
        public bool IsAvailable => false;

        public string? UnavailableReason =>
            "No mail transport is bound to the reporting platform. Report delivery is recorded as Skipped rather " +
            "than Sent so the audit trail stays truthful. Implement IReportMailSender (SMTP, or the platform Comm " +
            "outbox once that work is committed) and register it in place of NullReportMailSender.";

        public Task<ReportDeliveryOutcome> SendAsync(ReportMailMessage message,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReportDeliveryOutcome.Skipped(UnavailableReason!));
    }

    // ---- the email channel ------------------------------------------------------------------------------
    public class EmailReportDeliveryChannel : IReportDeliveryChannel
    {
        private readonly IReportMailSender _sender;
        private readonly ILogger<EmailReportDeliveryChannel> _logger;

        public EmailReportDeliveryChannel(IReportMailSender sender, ILogger<EmailReportDeliveryChannel> logger)
        {
            _sender = sender;
            _logger = logger;
        }

        public string ChannelKey => ReportDeliveryChannels.Email;

        // The CHANNEL is available even when the SENDER is not. That is deliberate: the channel still validates
        // addresses and records attempts, which is what makes "we generated 40 reports and delivered none"
        // visible instead of invisible.
        public bool IsAvailable => true;

        public async Task<ReportDeliveryOutcome> DeliverAsync(ReportDeliveryEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            if (!LooksLikeEmail(envelope.Address))
                return ReportDeliveryOutcome.Failed($"'{envelope.Address}' is not a usable email address.");

            if (!_sender.IsAvailable)
                return ReportDeliveryOutcome.Skipped(_sender.UnavailableReason ?? "No mail transport is bound.");

            try
            {
                return await _sender.SendAsync(new ReportMailMessage
                {
                    To = envelope.Address,
                    IsCc = envelope.IsCc,
                    Subject = envelope.Subject,
                    Body = envelope.Inline
                        ? envelope.Body + "\n\n" + envelope.Artifact.AsText()
                        : envelope.Body,
                    AttachmentFileName = envelope.Inline ? null : envelope.Artifact.FileName,
                    AttachmentContentType = envelope.Inline ? null : envelope.Artifact.ContentType,
                    Attachment = envelope.Inline ? null : envelope.Artifact.Content,
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                // Caught per recipient so one unreachable mailbox does not stop the rest of the distribution list.
                _logger.LogWarning(ex, "Report delivery to {Address} failed.", envelope.Address);
                return ReportDeliveryOutcome.Failed(ex.Message);
            }
        }

        // Intentionally a shape check, not RFC 5322 validation. A full validator is famously either wrong or
        // enormous, and the authority on whether an address exists is the mail server. This catches the typos
        // that make an attempt pointless.
        private static bool LooksLikeEmail(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            var at = address.IndexOf('@');
            if (at <= 0 || at == address.Length - 1) return false;
            if (address.IndexOf('@', at + 1) >= 0) return false;
            return address[(at + 1)..].Contains('.');
        }
    }

    // ---- orchestration ----------------------------------------------------------------------------------
    public sealed class ReportDeliverySummary
    {
        public int Attempted { get; init; }
        public int Sent { get; init; }
        public int Failed { get; init; }
        public int Skipped { get; init; }
        public IReadOnlyList<ReportDiagnostic> Diagnostics { get; init; } = Array.Empty<ReportDiagnostic>();
    }

    public interface IReportDeliveryService
    {
        // Fans one generated result out to a schedule's recipients and records an attempt row per recipient.
        Task<ReportDeliverySummary> DeliverScheduleAsync(ReportSchedule schedule, ReportResult result,
            BusinessContext context, Guid? correlationId = null, CancellationToken cancellationToken = default);

        // One-off send ("email me this report"). Same channels, same attempt records — there is no second,
        // unaudited delivery path.
        Task<ReportDeliverySummary> DeliverAsync(ReportResult result, IReadOnlyList<ReportDeliveryEnvelope> envelopes,
            BusinessContext context, CancellationToken cancellationToken = default);

        IReadOnlyList<string> AvailableChannels { get; }
    }

    public class ReportDeliveryService : IReportDeliveryService
    {
        private readonly CrossDbContext _db;
        private readonly IReportCatalog _catalog;
        private readonly Dictionary<string, IReportDeliveryChannel> _channels;
        private readonly IReportClock _clock;
        private readonly ILogger<ReportDeliveryService> _logger;

        public ReportDeliveryService(CrossDbContext db, IReportCatalog catalog,
            IEnumerable<IReportDeliveryChannel> channels, IReportClock clock,
            ILogger<ReportDeliveryService> logger)
        {
            _db = db;
            _catalog = catalog;
            _clock = clock;
            _logger = logger;
            _channels = channels.ToDictionary(c => c.ChannelKey, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<string> AvailableChannels =>
            _channels.Where(kv => kv.Value.IsAvailable).Select(kv => kv.Key).ToList();

        public async Task<ReportDeliverySummary> DeliverScheduleAsync(ReportSchedule schedule, ReportResult result,
            BusinessContext context, Guid? correlationId = null, CancellationToken cancellationToken = default)
        {
            if (result.Artifact == null)
                return new ReportDeliverySummary
                {
                    Diagnostics = new[]
                    {
                        ReportDiagnostic.Warning("delivery_nothing_to_send",
                            "The scheduled report produced no artifact, so nothing was delivered."),
                    },
                };

            var recipients = await _db.ReportScheduleRecipients.AsNoTracking()
                .Where(r => r.ScheduleId == schedule.Id && r.CompanyID == schedule.CompanyID)
                .ToListAsync(cancellationToken);

            if (recipients.Count == 0)
                // Not an error: a schedule may exist purely to ARCHIVE a report on a cadence. Reported as info so
                // it is visible without looking like a fault.
                return new ReportDeliverySummary
                {
                    Diagnostics = new[]
                    {
                        ReportDiagnostic.Info("delivery_no_recipients",
                            $"Schedule {schedule.Id} has no recipients; the report was generated and archived only."),
                    },
                };

            _catalog.TryGetDefinition(schedule.ReportCode, out var definition);
            var title = definition?.TitleAr ?? schedule.ReportCode;

            var envelopes = recipients.Select(r => new ReportDeliveryEnvelope
            {
                ChannelKey = r.ChannelKey,
                Address = r.Address,
                IsCc = r.IsCc,
                Subject = $"{schedule.Name} — {title}",
                Body = BuildBody(schedule, result, title),
                Artifact = result.Artifact,
                RecipientEmployeeId = r.EmployeeId,
                CorrelationId = correlationId,
            }).ToList();

            return await SendAllAsync(envelopes, schedule.CompanyID, schedule.Id, result.Run?.RunId,
                result.Run?.ArchiveEntryId, correlationId, cancellationToken);
        }

        public Task<ReportDeliverySummary> DeliverAsync(ReportResult result,
            IReadOnlyList<ReportDeliveryEnvelope> envelopes, BusinessContext context,
            CancellationToken cancellationToken = default) =>
            SendAllAsync(envelopes, context.CompanyId, scheduleId: null, result.Run?.RunId,
                result.Run?.ArchiveEntryId, result.Run?.RunId is null ? null : envelopes.FirstOrDefault()?.CorrelationId,
                cancellationToken);

        private async Task<ReportDeliverySummary> SendAllAsync(IReadOnlyList<ReportDeliveryEnvelope> envelopes,
            int companyId, int? scheduleId, long? runId, long? archiveEntryId, Guid? correlationId,
            CancellationToken cancellationToken)
        {
            var diagnostics = new List<ReportDiagnostic>();
            var sent = 0;
            var failed = 0;
            var skipped = 0;
            var now = _clock.LocalNow;

            foreach (var envelope in envelopes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ReportDeliveryOutcome outcome;

                if (!_channels.TryGetValue(envelope.ChannelKey, out var channel))
                {
                    outcome = ReportDeliveryOutcome.Failed(
                        $"No delivery channel is registered for '{envelope.ChannelKey}'.");
                    diagnostics.Add(ReportDiagnostic.Warning("delivery_channel_unknown", outcome.Detail!));
                }
                else if (!channel.IsAvailable)
                {
                    outcome = ReportDeliveryOutcome.Skipped(
                        $"Delivery channel '{envelope.ChannelKey}' is not available in this deployment.");
                    diagnostics.Add(ReportDiagnostic.Warning("delivery_channel_unavailable", outcome.Detail!));
                }
                else
                {
                    outcome = await channel.DeliverAsync(envelope, cancellationToken);
                }

                switch (outcome.Status)
                {
                    case ReportDeliveryStatus.Sent: sent++; break;
                    case ReportDeliveryStatus.Failed:
                        failed++;
                        diagnostics.Add(ReportDiagnostic.Warning("delivery_failed",
                            $"Delivery to '{envelope.Address}' failed: {outcome.Detail}"));
                        break;
                    case ReportDeliveryStatus.Skipped:
                        skipped++;
                        diagnostics.Add(ReportDiagnostic.Warning("delivery_skipped",
                            $"Delivery to '{envelope.Address}' was skipped: {outcome.Detail}"));
                        break;
                }

                // An attempt row per recipient, whatever the outcome. This is the record that makes "was it
                // delivered?" answerable — including the answer "no, and here is why".
                _db.ReportDeliveryAttempts.Add(new ReportDeliveryAttempt
                {
                    CompanyID = companyId,
                    ScheduleId = scheduleId,
                    RunId = runId,
                    ArchiveEntryId = archiveEntryId,
                    ChannelKey = envelope.ChannelKey,
                    Address = envelope.Address,
                    Status = outcome.Status,
                    Detail = outcome.Detail is { Length: > 900 } d ? d[..900] : outcome.Detail,
                    AttemptedAt = now,
                    CorrelationId = correlationId,
                    CreatedAt = now,
                });
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Same reasoning as the history swallow: the attempt log is observability, and losing it must not
                // turn a delivered report into an error. Logged loudly instead.
                _logger.LogError(ex, "Report delivery attempts could not be recorded for company {CompanyId}.",
                    companyId);
            }

            return new ReportDeliverySummary
            {
                Attempted = envelopes.Count,
                Sent = sent,
                Failed = failed,
                Skipped = skipped,
                Diagnostics = diagnostics,
            };
        }

        private static string BuildBody(ReportSchedule schedule, ReportResult result, string title)
        {
            var lines = new List<string>
            {
                title,
                $"Schedule: {schedule.Name}",
                $"Generated: {result.Run?.StartedAt:yyyy-MM-dd HH:mm}",
                $"Rows: {result.Run?.RowCount ?? 0}",
            };

            // A truncated report says so in the covering note as well as on the page. The person who receives an
            // emailed report may never open the attachment's header.
            if (result.Run?.Truncated == true)
                lines.Add("NOTE: the result was truncated — rows are missing from this output.");

            return string.Join('\n', lines);
        }
    }
}