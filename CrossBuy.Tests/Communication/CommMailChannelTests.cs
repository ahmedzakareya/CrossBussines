using CrossBuy.BL;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Communication;

// =================================================================================================
// PHASE 4 — the outbound email channel.
//
// NO TEST HERE CAN SEND MAIL, AND NOT BECAUSE IT IS CAREFUL.
//
// ICommService is doubled in every test, so the real CommService — the only type in the system that
// opens an SMTP connection — is never constructed. That matters more than usual here: this
// deployment has a live provider configured (Smtp.Enabled = true, a real account), and
// ICommService.SendAsync persists AND transmits. The adapter under test calls QueueAsync, which is
// the persist half alone; the double asserts that, so a future change to SendAsync cannot quietly
// turn these tests into a mail blast.
// =================================================================================================
public sealed class CommMailChannelTests : IDisposable
{
    private readonly CommunicationTestHost _host = new();
    private readonly RecordingMailService _mail = new();

    public void Dispose() => _host.Dispose();

    private CommMailNotificationChannel Channel() =>
        new(_mail, _host.Db, NullLogger<CommMailNotificationChannel>.Instance);

    private static CommChannelMessage Message(
        int employeeId = CommunicationTestHost.Author,
        int companyId = CommunicationTestHost.CompanyId) => new()
        {
            NotificationId = 55,
            DeliveryId = 77,
            Channel = CommChannel.Email,
            RecipientEmployeeId = employeeId,
            CompanyId = companyId,
            TemplateKey = "MentionedInComment",
            Entity = CommunicationTestHost.Invoice(),
            DedupKey = "notif:55:Email",
            Rendered = new CommRenderedNotification
            {
                TitleAr = "ذُكرت في تعليق",
                TitleEn = "You were mentioned",
                BodyAr = "ذكرك أحمد في فاتورة",
                BodyEn = "Ahmed mentioned you on an invoice",
                Category = "Mention",
                Priority = "Normal",
                LegacyNotificationType = "comment",
                Url = "/Accounting/SalesInvoiceDetail?id=1001",
            },
        };

    private async Task GiveEmailAsync(int employeeId, string? email)
    {
        var row = await _host.Db.Employee.FindAsync(employeeId);
        Assert.NotNull(row);
        row!.Email = email!;
        await _host.Db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task It_queues_and_never_transmits()
    {
        await GiveEmailAsync(CommunicationTestHost.Author, "ahmed@example.com");

        var result = await Channel().SendAsync(Message());

        Assert.True(result.Delivered, result.Reason);

        // THE ASSERTION THIS WHOLE FILE EXISTS FOR.
        Assert.Equal(1, _mail.QueueCalls);
        Assert.Equal(0, _mail.SendCalls);
        Assert.Equal(0, _mail.TransmitCalls);
    }

    [Fact]
    public async Task The_address_comes_from_the_employee_row_not_from_the_message()
    {
        await GiveEmailAsync(CommunicationTestHost.Author, "ahmed@example.com");

        await Channel().SendAsync(Message());

        Assert.Equal("ahmed@example.com", _mail.LastTo);
    }

    // Both languages survive the hop. A mail client shows one subject line, so the English title leads
    // and the Arabic sentence rides in the body rather than being dropped.
    [Fact]
    public async Task Both_languages_and_the_link_reach_the_outbox()
    {
        await GiveEmailAsync(CommunicationTestHost.Author, "ahmed@example.com");

        await Channel().SendAsync(Message());

        Assert.Equal("You were mentioned", _mail.LastSubject);
        Assert.Contains("Ahmed mentioned you on an invoice", _mail.LastBody);
        Assert.Contains("ذكرك أحمد في فاتورة", _mail.LastBody);
        Assert.Contains("/Accounting/SalesInvoiceDetail?id=1001", _mail.LastBody);
    }

    // ---------------------------------------------------------------------------------------------
    // COMPANY ISOLATION. The address lookup is company-scoped, so a notification in one company cannot
    // address a person in another — asserted asymmetrically, with the employee really existing.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_employee_in_another_company_is_never_addressed()
    {
        await GiveEmailAsync(CommunicationTestHost.OtherCompanyEmployee, "badr@other.example.com");

        // Our company's notification naming THEIR employee id.
        var result = await Channel().SendAsync(
            Message(employeeId: CommunicationTestHost.OtherCompanyEmployee));

        Assert.False(result.Delivered);
        Assert.Equal(0, _mail.QueueCalls);
        Assert.Null(_mail.LastTo);

        // The control: in their own company the same employee IS addressable, so the refusal above is
        // about the company boundary and not about that employee being unreachable.
        var theirs = await Channel().SendAsync(Message(
            employeeId: CommunicationTestHost.OtherCompanyEmployee,
            companyId: CommunicationTestHost.OtherCompanyId));

        Assert.True(theirs.Delivered, theirs.Reason);
        Assert.Equal("badr@other.example.com", _mail.LastTo);
    }

    [Fact]
    public async Task An_unresolved_company_queues_nothing()
    {
        await GiveEmailAsync(CommunicationTestHost.Author, "ahmed@example.com");

        var result = await Channel().SendAsync(Message(companyId: 0));

        Assert.False(result.Delivered);
        Assert.Equal(0, _mail.QueueCalls);
    }

    // An employee with no address is a SKIP with a reason, not a failure: retrying five times cannot
    // give somebody an email address, and a Pending row that never drains is worse than an honest park.
    [Fact]
    public async Task A_recipient_with_no_address_is_skipped_with_a_reason()
    {
        await GiveEmailAsync(CommunicationTestHost.Author, "");

        var result = await Channel().SendAsync(Message());

        Assert.False(result.Delivered);
        Assert.Contains("no email address", result.Reason);
        Assert.Equal(0, _mail.QueueCalls);
    }

    // An address the outbox refuses is also a skip — the same reasoning, decided by the outbox.
    [Fact]
    public async Task An_address_the_outbox_refuses_is_skipped_rather_than_retried()
    {
        await GiveEmailAsync(CommunicationTestHost.Author, "not-an-address");
        _mail.NextStatus = "Failed";
        _mail.NextError = "Invalid recipient address";

        var result = await Channel().SendAsync(Message());

        Assert.False(result.Delivered);
        Assert.Equal("Invalid recipient address", result.Reason);
    }

    // The channel reports enabled even with no provider configured: queued rows go out when one is,
    // whereas reporting disabled would park every row as Skipped and lose the notification.
    [Fact]
    public void It_is_enabled_even_without_a_configured_provider()
    {
        _mail.SmtpConfiguredValue = false;
        Assert.True(Channel().IsEnabled);
        Assert.Equal(CommChannel.Email, Channel().Channel);
    }

    // ---------------------------------------------------------------------------------------------
    // The double. It implements the WHOLE interface and counts the transmitting members separately, so
    // "this adapter does not transmit" is measured rather than assumed.
    // ---------------------------------------------------------------------------------------------
    private sealed class RecordingMailService : ICommService
    {
        public int QueueCalls { get; private set; }
        public int SendCalls { get; private set; }
        public int TransmitCalls { get; private set; }

        public string? LastTo { get; private set; }
        public string? LastSubject { get; private set; }
        public string LastBody { get; private set; } = "";

        public string NextStatus { get; set; } = "Queued";
        public string? NextError { get; set; }
        public bool SmtpConfiguredValue { get; set; } = true;

        public Task<(int id, string status, string? error)> QueueAsync(int companyId, int createdBy, string to,
            string? cc, string subject, string body, IEnumerable<MailFile>? attachments, int? parentId, string kind)
        {
            QueueCalls++;
            LastTo = to; LastSubject = subject; LastBody = body;
            return Task.FromResult((1, NextStatus, NextError));
        }

        public Task<(int id, string status, string? error)> SendAsync(int companyId, int createdBy, string to,
            string? cc, string subject, string body, IEnumerable<MailFile>? attachments, int? parentId, string kind)
        {
            SendCalls++;   // never expected: this is the member that reaches SMTP
            return Task.FromResult((1, "Sent", (string?)null));
        }

        public Task<bool> TrySendAsync(int id) { TransmitCalls++; return Task.FromResult(true); }

        public Task<(bool ok, string? error)> TransmitAsync(int id, CancellationToken cancellationToken = default)
        { TransmitCalls++; return Task.FromResult((true, (string?)null)); }

        public bool SmtpConfigured => SmtpConfiguredValue;

        public Task<List<MailListItem>> ListAsync(int companyId, int employeeId, string folder, string? q, bool manager = false)
            => Task.FromResult(new List<MailListItem>());
        public Task<MailCounts> CountsAsync(int companyId, int employeeId, bool manager = false) => Task.FromResult(new MailCounts());
        public Task<MailFull?> GetAsync(int companyId, int employeeId, int id, bool manager = false, bool markRead = false) => Task.FromResult<MailFull?>(null);
        public Task<int> TrashAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids) => Task.FromResult(0);
        public Task<int> RestoreAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids) => Task.FromResult(0);
        public Task<int> DeleteForeverAsync(int companyId, int employeeId, IReadOnlyCollection<int> ids) => Task.FromResult(0);
        public Task<int> EmptyTrashAsync(int companyId, int employeeId) => Task.FromResult(0);
        public Task<bool> ToggleStarAsync(int companyId, int employeeId, int id) => Task.FromResult(false);
        public Task<bool> IsSenderAsync(int companyId, int employeeId, int id) => Task.FromResult(false);
        public Task<List<MailThreadItem>> ThreadAsync(int companyId, int employeeId, int threadId, bool manager = false)
            => Task.FromResult(new List<MailThreadItem>());
        public string? ExternalAddressFor(int employeeId) => null;
    }
}
