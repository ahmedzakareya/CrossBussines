using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // THE EMAIL CHANNEL — platform notification -> the existing CommMessages outbox (Phase 4).
    //
    // WHY THIS IS OFF BY DEFAULT, AND WHY THAT IS NOT TIMIDITY.
    //
    // UnwiredCommNotificationChannel.Email() records the objection this adapter has to answer:
    // bridging makes THIS platform a second producer for a queue with its own status vocabulary and
    // retry policy — the "second notification path bypasses the outbox" conflict CLAUDE.md records as
    // HM-D46. That objection is not removed by writing an adapter; it is a decision for whoever owns
    // production wiring. So the adapter exists, is tested, and is registered only when a deployment
    // says CommunicationPlatform:EmailChannelEnabled = true.
    //
    // IT QUEUES; IT NEVER TRANSMITS.
    //
    // ICommService.SendAsync persists AND calls TrySendAsync, so it reaches SMTP — and this deployment
    // has a live provider configured. An automated channel calling it would email real people the first
    // time a notification fired. This adapter calls QueueAsync instead, which is the persist half alone.
    // Delivery stays where it already lives: CommMessageDispatcherHostedService claims the row and calls
    // TransmitAsync, and that worker has its own activation switch.
    //
    // The safety is therefore STRUCTURAL rather than disciplinary: there is no code path from this class
    // to an SMTP socket, so no test can accidentally find one.
    // =============================================================================================
    public sealed class CommMailNotificationChannel : ICommNotificationChannel
    {
        private readonly ICommService _mail;
        private readonly CrossDbContext _db;
        private readonly ILogger<CommMailNotificationChannel> _log;

        public CommMailNotificationChannel(
            ICommService mail, CrossDbContext db, ILogger<CommMailNotificationChannel> log)
        { _mail = mail; _db = db; _log = log; }

        public string Channel => CommChannel.Email;

        // Enabled regardless of SmtpConfigured. Queueing is useful even with no provider: the rows
        // accumulate honestly and go out when one is configured. Reporting "disabled" here would park
        // every row as Skipped and lose the notification instead of deferring it.
        public bool IsEnabled => true;

        public async Task<CommChannelResult> SendAsync(
            CommChannelMessage message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.CompanyId <= 0)
                return CommChannelResult.Skip("no company scope resolved for the notification");

            // The recipient's address is read from THEIR employee row, inside the notification's company.
            // Never from the message: an address supplied by a caller is an open relay waiting to happen.
            var address = await ResolveAddressAsync(message.CompanyId, message.RecipientEmployeeId, cancellationToken);

            if (string.IsNullOrWhiteSpace(address))
                return CommChannelResult.Skip(
                    $"employee {message.RecipientEmployeeId} has no email address in company {message.CompanyId}");

            // DEDUPLICATION. Deterministic per (notification, channel) and handed straight to the outbox,
            // so a redelivered delivery row cannot produce a second email. The dispatcher already passes
            // its own DedupKey; it is used verbatim rather than regenerated.
            // Bilingual by contract: the English sentence is the subject and the Arabic one rides in the body,
            // so neither language is dropped on the way to a mail client that shows only one line.
            string subject = message.Rendered.TitleEn;
            string body = string.Join(
                Environment.NewLine + Environment.NewLine,
                new[] { message.Rendered.BodyEn, message.Rendered.BodyAr, message.Rendered.Url }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

            var (id, status, error) = await _mail.QueueAsync(
                message.CompanyId,
                createdBy: message.RecipientEmployeeId,
                to: address!,
                cc: null,
                subject: subject,
                body: body,
                attachments: null,
                parentId: null,
                kind: "Notification");

            if (status == "Failed")
            {
                // A refused address is not retryable — five more attempts cannot make it parse.
                _log.LogInformation(
                    "Email notification {NotificationId} not queued: {Error}", message.NotificationId, error);
                return CommChannelResult.Skip(error ?? "the outbox refused the address");
            }

            return CommChannelResult.Ok("commmessage:" + id);
        }

        private async Task<string?> ResolveAddressAsync(
            int companyId, int employeeId, CancellationToken cancellationToken)
        {
            if (employeeId <= 0) return null;

            // CompanyId is compared on the ROW. Resolving an address without it would let a notification
            // in one company address a person in another.
            return await _db.Employee.AsNoTracking()
                .Where(e => e.ID == employeeId && e.EmpCompanyID == companyId)
                .Select(e => e.Email)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }
}
