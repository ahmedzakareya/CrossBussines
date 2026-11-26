using System.Reflection;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests.Communication;

// =================================================================================================
// DELIVERY WORKER — CONTROLLED END-TO-END ACCEPTANCE (scenarios R and S).
//
// WHAT THE EXISTING SUITES ALREADY PROVE, AND WHY THIS IS STILL NEEDED.
// CommNotificationHostedServiceTests drives the worker against a STUB dispatcher: it proves
// activation, the gate and per-company scope binding, but nothing ever reaches a delivery row.
// CommTimelineAndDispatchTests drives the real dispatcher DIRECTLY: it proves Sent/Skipped/Failed,
// the attempt cap and dedup keys, but the worker is not in the picture. Neither answers the
// question acceptance actually asks — "does the worker process a queued message end to end" — so
// this suite joins the two halves: real worker, real dispatcher, real outbox rows, fake transport.
//
// NOTHING LEAVES THE PROCESS. The only channel registered is an in-memory recorder. ICommService is
// never constructed, no SMTP type is referenced, and the live Gmail configuration is unreachable
// from here by construction rather than by care.
// =================================================================================================
public sealed class CommDeliveryWorkerAcceptanceTests : IDisposable
{
    private const int Mine = CommunicationTestHost.CompanyId;          // 1 — in the allow-list
    private const int Theirs = CommunicationTestHost.OtherCompanyId;   // 2 — deliberately NOT

    private readonly CommunicationTestHost _host = new();

    public void Dispose() => _host.Dispose();

    // ---- the fake transport ---------------------------------------------------------------------
    private sealed class RecordingChannel : ICommNotificationChannel
    {
        private readonly Func<CommChannelMessage, CommChannelResult> _behaviour;

        public RecordingChannel(Func<CommChannelMessage, CommChannelResult>? behaviour = null)
            => _behaviour = behaviour ?? (_ => CommChannelResult.Ok("recorded"));

        public List<CommChannelMessage> Sent { get; } = new();
        public string Channel => CommChannel.InApp;
        public bool IsEnabled => true;

        public Task<CommChannelResult> SendAsync(CommChannelMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(_behaviour(message));
        }
    }

    private sealed class AllowGate : IWorkerGate
    {
        public Task<bool> WaitUntilAllowedAsync(string workerName, CancellationToken token) => Task.FromResult(true);
    }

    private static Task Pass(CommNotificationHostedService worker) =>
        (Task)typeof(CommNotificationHostedService)
            .GetMethod("RunPassAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, new object[] { CancellationToken.None })!;

    // THE TEST-ONLY CONFIGURATION. Enabled, and scoped to ONE company by an explicit allow-list —
    // the same two switches production requires, set here in memory and nowhere else.
    private CommunicationPlatformOptions WorkerOptions(params int[] companies)
    {
        var o = _host.Options;
        o.NotificationWorkerEnabled = true;
        o.NotificationWorkerCompanyIds = companies.ToList();
        return o;
    }

    private (ServiceProvider Provider, RecordingChannel Channel) Container(RecordingChannel? channel = null)
    {
        var recorder = channel ?? new RecordingChannel();
        var services = new ServiceCollection();
        services.AddScoped<ICompanyScopeHolder, CompanyScopeHolder>();
        services.AddScoped<CrossDbContext>(sp => _host.NewContext(sp.GetRequiredService<ICompanyScopeHolder>()));
        services.AddScoped<IBusinessContextFactory>(sp => new BusinessContextFactory(
            new HttpContextAccessor(), sp.GetRequiredService<CrossDbContext>(),
            sp.GetRequiredService<ICompanyScopeHolder>(), NullLogger<BusinessContextFactory>.Instance));
        services.AddSingleton<ICommNotificationChannel>(recorder);
        services.AddScoped<ICommNotificationDispatcher>(sp =>
        {
            var db = sp.GetRequiredService<CrossDbContext>();
            return new CommNotificationDispatcher(
                db, new EfCommDeliveryClaimStore(db, _host.Opt),
                new ICommNotificationChannel[] { recorder },
                new CommAuditWriter(db), _host.Opt,
                NullLogger<CommNotificationDispatcher>.Instance);
        });

        return (services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }), recorder);
    }

    private CommNotificationHostedService Worker(ServiceProvider provider, CommunicationPlatformOptions options) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), new AllowGate(),
            Options.Create(options), NullLogger<CommNotificationHostedService>.Instance);

    // Queues a REAL notification through the platform, in the given company, so the worker finds a
    // genuine outbox row rather than one this test hand-built.
    private async Task<long> QueueAsync(int companyId, int recipient)
    {
        var context = CommunicationTestHost.Context(
            employeeId: companyId == Mine ? CommunicationTestHost.Author : CommunicationTestHost.OtherCompanyEmployee,
            companyId: companyId);

        // A MENTION is what produces a recipient. A plain comment notifies nobody — the author is
        // excluded from their own activity — so seeding one would have queued no delivery at all,
        // and the first version of this suite failed on exactly that wrong premise.
        await _host.Comments().AddAsync(context,
            _host.CommentOn(CommunicationTestHost.Invoice(companyId == Mine ? 1001 : 2002),
                $"@employee:{recipient} needs attention"));

        using var fresh = _host.NewContext();
        return await fresh.Set<CommNotificationDelivery>().AsNoTracking()
            .Where(d => d.CompanyID == companyId).Select(d => d.Id).FirstOrDefaultAsync();
    }

    // SCOPED TO THE IN-APP CHANNEL, deliberately. A notification fans out to every channel its
    // template names, and the Email row is correctly parked as Skipped here because no Email adapter
    // is registered in this container — that is the platform behaving, not a failure. Asserting over
    // every row would therefore fail on correct behaviour, which is how the first run of this suite
    // misread a Skipped Email row as a broken delivery.
    private async Task<List<CommNotificationDelivery>> DeliveriesAsync(int companyId)
    {
        using var fresh = _host.NewContext();
        return await fresh.Set<CommNotificationDelivery>().AsNoTracking()
            .Where(d => d.CompanyID == companyId && d.Channel == CommChannel.InApp).ToListAsync();
    }

    // The channels with no adapter, for the one assertion that is ABOUT them.
    private async Task<List<CommNotificationDelivery>> UnwiredDeliveriesAsync(int companyId)
    {
        using var fresh = _host.NewContext();
        return await fresh.Set<CommNotificationDelivery>().AsNoTracking()
            .Where(d => d.CompanyID == companyId && d.Channel != CommChannel.InApp).ToListAsync();
    }

    // ---------------------------------------------------------------------------------------------
    // R — the worker processes a controlled queued message, end to end.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_worker_processes_a_queued_delivery_and_marks_it_sent()
    {
        await QueueAsync(Mine, CommunicationTestHost.Colleague);

        var before = await DeliveriesAsync(Mine);
        Assert.NotEmpty(before);
        Assert.All(before, d => Assert.Equal(CommDeliveryStatus.Pending, d.Status));

        var (provider, channel) = Container();
        using (provider)
        using (var worker = Worker(provider, WorkerOptions(Mine)))
            await Pass(worker);

        Assert.NotEmpty(channel.Sent);                       // the transport really was called

        var after = await DeliveriesAsync(Mine);
        Assert.NotEmpty(after);
        Assert.All(after, d => Assert.Equal(CommDeliveryStatus.Sent, d.Status));
        Assert.All(after, d => Assert.NotNull(d.SentAt));
        Assert.All(after, d => Assert.Null(d.Error));

        // A channel with no adapter is parked as Skipped WITH A REASON — never left Pending forever
        // and never retried for a permanent condition. Asserted here because it is the honest other
        // half of "the worker processed this notification".
        foreach (var unwired in await UnwiredDeliveriesAsync(Mine))
        {
            Assert.Equal(CommDeliveryStatus.Skipped, unwired.Status);
            Assert.False(string.IsNullOrWhiteSpace(unwired.Error));
        }
    }

    // §9 — a second pass must not deliver the same row again.
    [Fact]
    public async Task A_second_pass_does_not_deliver_the_same_row_twice()
    {
        await QueueAsync(Mine, CommunicationTestHost.Colleague);

        var (provider, channel) = Container();
        using (provider)
        using (var worker = Worker(provider, WorkerOptions(Mine)))
        {
            await Pass(worker);
            int afterFirst = channel.Sent.Count;
            Assert.True(afterFirst > 0);

            await Pass(worker);
            Assert.Equal(afterFirst, channel.Sent.Count);    // nothing re-sent
        }

        Assert.All(await DeliveriesAsync(Mine), d => Assert.Equal(CommDeliveryStatus.Sent, d.Status));
    }

    // ---------------------------------------------------------------------------------------------
    // §12 — COMPANY ISOLATION. A company absent from the allow-list is never processed. Asserted
    // asymmetrically: the allowed company's rows move while the other company's stay Pending, so an
    // inverted predicate could not pass by leaving everything untouched.
    // ---------------------------------------------------------------------------------------------
    // The allow-list is proven by running the SAME queued work under two different lists. The other
    // company cannot supply the second dataset: the host seeds exactly one employee there, so a
    // mention would be a self-mention and self-notification is excluded — which is why an earlier
    // version of this test found nothing queued and failed on its own premise rather than on the
    // worker. Excluding the company that DOES have work is the stronger arrangement anyway, because
    // the rows exist and are provably reachable, and only the allow-list keeps the worker off them.
    [Fact]
    public async Task A_company_outside_the_allow_list_is_never_dispatched_for()
    {
        await QueueAsync(Mine, CommunicationTestHost.Colleague);

        var queued = await DeliveriesAsync(Mine);
        Assert.NotEmpty(queued);
        Assert.All(queued, d => Assert.Equal(CommDeliveryStatus.Pending, d.Status));

        // PASS ONE — the allow-list names only the OTHER company. The work is right there and must
        // not be touched.
        var (excludedProvider, excludedChannel) = Container();
        using (excludedProvider)
        using (var worker = Worker(excludedProvider, WorkerOptions(Theirs)))
            await Pass(worker);

        Assert.Empty(excludedChannel.Sent);                                   // transport never called
        Assert.All(await DeliveriesAsync(Mine),
            d => Assert.Equal(CommDeliveryStatus.Pending, d.Status));         // nothing moved

        // PASS TWO — the same rows, the same worker, the allow-list now naming this company. They
        // move. So the refusal above was the allow-list and not an empty queue.
        var (allowedProvider, allowedChannel) = Container();
        using (allowedProvider)
        using (var worker = Worker(allowedProvider, WorkerOptions(Mine)))
            await Pass(worker);

        Assert.NotEmpty(allowedChannel.Sent);
        Assert.All(allowedChannel.Sent, m => Assert.Equal(Mine, m.CompanyId));
        Assert.All(await DeliveriesAsync(Mine),
            d => Assert.Equal(CommDeliveryStatus.Sent, d.Status));
    }

    // ---------------------------------------------------------------------------------------------
    // S — a failed delivery is OBSERVABLE and RETRYABLE.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_failing_transport_records_an_observable_failure_and_retries_next_pass()
    {
        await QueueAsync(Mine, CommunicationTestHost.Colleague);

        var failing = new RecordingChannel(_ => throw new InvalidOperationException("transport refused"));
        var (provider, channel) = Container(failing);

        using (provider)
        using (var worker = Worker(provider, WorkerOptions(Mine)))
        {
            await Pass(worker);

            var afterFirst = await DeliveriesAsync(Mine);
            Assert.All(afterFirst, d => Assert.Equal(CommDeliveryStatus.Failed, d.Status));
            Assert.All(afterFirst, d => Assert.Equal(1, d.Attempts));
            Assert.All(afterFirst, d => Assert.False(string.IsNullOrWhiteSpace(d.Error)));  // observable
            Assert.All(afterFirst, d => Assert.Contains("transport refused", d.Error!));

            // RETRYABLE: the next pass picks it up again rather than parking it forever.
            await Pass(worker);
            Assert.All(await DeliveriesAsync(Mine), d => Assert.Equal(2, d.Attempts));
        }

        Assert.True(channel.Sent.Count >= 2);                // attempted twice, not abandoned after one
    }

    // A failure must not be permanent when the transport recovers — the row is still deliverable.
    [Fact]
    public async Task A_recovered_transport_delivers_the_previously_failed_row()
    {
        await QueueAsync(Mine, CommunicationTestHost.Colleague);

        var (badProvider, _) = Container(new RecordingChannel(_ => CommChannelResult.Fail("down")));
        using (badProvider)
        using (var worker = Worker(badProvider, WorkerOptions(Mine)))
            await Pass(worker);

        Assert.All(await DeliveriesAsync(Mine), d => Assert.Equal(CommDeliveryStatus.Failed, d.Status));

        var (goodProvider, good) = Container();
        using (goodProvider)
        using (var worker = Worker(goodProvider, WorkerOptions(Mine)))
            await Pass(worker);

        Assert.NotEmpty(good.Sent);
        Assert.All(await DeliveriesAsync(Mine), d => Assert.Equal(CommDeliveryStatus.Sent, d.Status));
    }

    // ---------------------------------------------------------------------------------------------
    // §14 — THE PRODUCTION DEFAULT STAYS OFF. Not "a flag is false somewhere": with no configuration
    // at all the worker is not registered, and a bare `true` without a company allow-list is refused
    // at startup rather than quietly running for whichever company a background scope resolves.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void A_deployment_with_no_configuration_registers_no_worker()
    {
        Assert.False(new CommunicationPlatformOptions().NotificationWorkerEnabled);
        Assert.Empty(new CommunicationPlatformOptions().NotificationWorkerCompanyIds);
        Assert.False(new CommunicationPlatformOptions().BridgeReplayEnabled);
    }

    [Fact]
    public async Task Enabling_the_worker_without_a_company_allow_list_is_refused()
    {
        var (provider, _) = Container();
        using (provider)
        using (var worker = Worker(provider, WorkerOptions(/* none */)))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Pass(worker));
    }

    // §13 — the worker holds nothing open. Each pass takes its own scope per company and releases it;
    // a leaked scope would keep a DbContext alive on the shared connection and break the next test.
    [Fact]
    public async Task Repeated_passes_leave_no_scope_or_connection_behind()
    {
        await QueueAsync(Mine, CommunicationTestHost.Colleague);

        var (provider, _) = Container();
        using (provider)
        using (var worker = Worker(provider, WorkerOptions(Mine)))
            for (int i = 0; i < 3; i++)
                await Pass(worker);

        // The host's own context still works after the worker has come and gone.
        using var fresh = _host.NewContext();
        Assert.True(await fresh.Set<CommNotificationDelivery>().AsNoTracking().AnyAsync());
    }
}
