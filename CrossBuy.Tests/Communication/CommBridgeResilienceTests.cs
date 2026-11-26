using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication;

// =================================================================================================
// PHASE 2 — the platform-kernel bridge, and the coupling it must not inherit.
//
// THE RECORDED FAILURE. HM-D44: a missing BusinessEvents table failed a whole SALE with SQL-208,
// because the event write sits inside the caller's transaction, before commit. HM-D53: the same
// shape reached the GL writer — ReverseAsync calls RecordAsync, RecordAsync has no swallowing catch,
// so a kernel fault rolls back the correction and it does not stand.
//
// CommEventPublisher awaits ForwardAsync inside the comment's CommTransaction, so switching the
// bridge on would reproduce that shape on every commenting screen in the product. The first test
// here REPRODUCES it rather than assuming it, so the fix is measured against a demonstrated
// failure. The rest prove the isolated mode keeps the user's comment and still delivers the event.
//
// A comment is the user's work. A kernel event is our bookkeeping. When the bookkeeping breaks, the
// user's work is what must survive.
// =================================================================================================
public sealed class CommBridgeResilienceTests
{
    private static CommunicationPlatformOptions BridgeOn(string failureMode) => new()
    {
        BridgeToBusinessEvents = true,
        BridgeFailureMode = failureMode,
        EnabledEntityCodes = new List<string> { EntityRegistry.SalesInvoice },
        EnabledChannels = new List<string> { CommChannel.InApp },
    };

    // ---------------------------------------------------------------------------------------------
    // 1. REPRODUCTION. The coupled mode is today's only behaviour, and it destroys the comment.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Reproduction_the_coupled_mode_loses_the_comment_when_the_kernel_fails()
    {
        using var host = new CommunicationTestHost(BridgeOn(CommBridgeFailureMode.FailOperation))
        {
            BridgeOverride = ThrowingBusinessEventBridge.MissingBusinessEventsTable(),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => host.Comments().AddAsync(
            CommunicationTestHost.Context(), host.CommentOn(CommunicationTestHost.Invoice(), "this is lost")));

        // The whole point of the reproduction: the user's comment is gone, not merely unbridged.
        using var fresh = host.NewContext();
        Assert.Empty(await fresh.Set<CommComment>().AsNoTracking().ToListAsync());
        Assert.Empty(await fresh.Set<CommAuditEntry>().AsNoTracking()
            .Where(a => a.Action == CommAuditActions.CommentAdded).ToListAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // 2. THE FIX. Same failure, isolated mode: the comment stands.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_isolated_mode_keeps_the_comment_when_the_kernel_fails()
    {
        using var host = new CommunicationTestHost(BridgeOn(CommBridgeFailureMode.Isolate))
        {
            BridgeOverride = ThrowingBusinessEventBridge.MissingBusinessEventsTable(),
        };

        var added = await host.Comments().AddAsync(
            CommunicationTestHost.Context(), host.CommentOn(CommunicationTestHost.Invoice(), "this survives"));

        Assert.True(added.CommentId > 0);

        using var fresh = host.NewContext();
        var stored = await fresh.Set<CommComment>().AsNoTracking().SingleAsync(c => c.Id == added.CommentId);
        Assert.Equal("this survives", stored.Body);

        // Still audited — the durable communication event is written first and never depends on the kernel.
        Assert.NotEmpty(await fresh.Set<CommAuditEntry>().AsNoTracking()
            .Where(a => a.Action == CommAuditActions.CommentAdded).ToListAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // 3. A swallowed failure that leaves no trace is worse than a crash: it is a lost event nobody
    //    can find. Isolation must be LOUD in the audit log.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_isolated_bridge_failure_is_recorded_for_diagnosis()
    {
        using var host = new CommunicationTestHost(BridgeOn(CommBridgeFailureMode.Isolate))
        {
            BridgeOverride = ThrowingBusinessEventBridge.MissingBusinessEventsTable(),
        };

        await host.Comments().AddAsync(
            CommunicationTestHost.Context(), host.CommentOn(CommunicationTestHost.Invoice(), "text"));

        using var fresh = host.NewContext();
        var failures = await fresh.Set<CommAuditEntry>().AsNoTracking()
            .Where(a => a.Action == CommAuditActions.BridgeFailed).ToListAsync();

        // Adding the first comment publishes TWO bridgeable events — the thread was opened and the comment
        // was added — so both failures are recorded. Asserted per event type rather than as a single row,
        // because "one row" would be an accident of how many events this particular call happens to raise.
        Assert.Contains(failures, f => f.EventType == CommEventTypes.CommentAdded);
        Assert.Contains(failures, f => f.EventType == CommEventTypes.ThreadCreated);

        var failure = failures.First(f => f.EventType == CommEventTypes.CommentAdded);
        Assert.Equal(EntityRegistry.SalesInvoice, failure.EntityType);
        Assert.Equal(CommunicationTestHost.CompanyId, failure.CompanyID);
        Assert.NotNull(failure.DetailJson);
        // The operator needs the reason, not just the fact.
        Assert.Contains("BusinessEvents", failure.DetailJson!);
    }

    // ---------------------------------------------------------------------------------------------
    // 4. The default matters more than the feature: a deployment that turns the bridge on without
    //    reading this file must get the safe shape.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void Isolation_is_the_default_so_turning_the_bridge_on_cannot_silently_couple_commenting()
    {
        Assert.Equal(CommBridgeFailureMode.Isolate, new CommunicationPlatformOptions().BridgeFailureMode);
    }

    // ---------------------------------------------------------------------------------------------
    // 5. BACKWARD COMPATIBILITY. A healthy kernel behaves exactly as before in the isolated mode.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_successful_bridge_is_unchanged_by_the_isolated_mode()
    {
        using var host = new CommunicationTestHost(BridgeOn(CommBridgeFailureMode.Isolate));

        await host.Comments().AddAsync(
            CommunicationTestHost.Context(), host.CommentOn(CommunicationTestHost.Invoice(), "text"));

        var forwarded = host.Bridge.Forwarded.Select(e => e.EventType).ToList();
        Assert.Contains(CommEventTypes.CommentAdded, forwarded);
        Assert.Contains(CommEventTypes.ThreadCreated, forwarded);

        using var fresh = host.NewContext();
        Assert.Empty(await fresh.Set<CommAuditEntry>().AsNoTracking()
            .Where(a => a.Action == CommAuditActions.BridgeFailed).ToListAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // 6. RETRY. What the inline bridge dropped, the replay pass delivers.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Replay_forwards_an_event_the_inline_bridge_failed_to_deliver()
    {
        var failing = ThrowingBusinessEventBridge.MissingBusinessEventsTable();
        using var host = new CommunicationTestHost(BridgeOn(CommBridgeFailureMode.Isolate))
        {
            BridgeOverride = failing,
        };

        await host.Comments().AddAsync(
            CommunicationTestHost.Context(), host.CommentOn(CommunicationTestHost.Invoice(), "text"));

        // The kernel comes back.
        var healthy = new RecordingBusinessEventBridge();
        var report = await host.BridgeReplay(bridge: healthy)
            .ReplayAsync(CommunicationTestHost.CompanyId);

        Assert.True(report.Forwarded > 0);
        Assert.Contains(CommEventTypes.CommentAdded, healthy.Forwarded.Select(e => e.EventType));

        // The payload the kernel receives on replay is the one the audit row preserved, not an invention.
        var replayed = healthy.Forwarded.Single(e => e.EventType == CommEventTypes.CommentAdded);
        Assert.Equal(EntityRegistry.SalesInvoice, replayed.Entity.EntityCode);
        Assert.Equal(CommunicationTestHost.CompanyId, replayed.CompanyId);
        Assert.NotNull(replayed.DedupKey);
    }

    // ---------------------------------------------------------------------------------------------
    // 7. NO DUPLICATE DELIVERY. Replay is a window scan, so it re-offers events it already delivered;
    //    the kernel's DedupKey idempotency is what makes that safe, and this asserts we actually
    //    hand it a stable key rather than a fresh one each pass.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Replay_hands_the_kernel_a_stable_dedup_key_so_a_second_pass_cannot_duplicate()
    {
        using var host = new CommunicationTestHost(BridgeOn(CommBridgeFailureMode.Isolate))
        {
            BridgeOverride = ThrowingBusinessEventBridge.MissingBusinessEventsTable(),
        };

        await host.Comments().AddAsync(
            CommunicationTestHost.Context(), host.CommentOn(CommunicationTestHost.Invoice(), "text"));

        var kernel = new DedupAwareBusinessEventBridge();

        await host.BridgeReplay(bridge: kernel).ReplayAsync(CommunicationTestHost.CompanyId);
        int acceptedAfterFirstPass = kernel.Accepted;

        await host.BridgeReplay(bridge: kernel).ReplayAsync(CommunicationTestHost.CompanyId);

        Assert.True(acceptedAfterFirstPass > 0, "the first pass should have delivered something to match against");

        // The second pass re-offered the very same events and the kernel recognised every one of them.
        // Asserted as two asymmetric equalities rather than "DistinctKeys == Accepted", which is true by
        // construction and would pass even if every pass minted a fresh key.
        Assert.Equal(acceptedAfterFirstPass, kernel.Accepted);     // nothing new was written
        Assert.Equal(acceptedAfterFirstPass, kernel.Duplicates);   // and every re-offer was seen as a repeat

        // The keys are STABLE because replay reads the one the audit row preserved instead of re-deriving
        // it. Two event types build their key from DateTime.UtcNow.Ticks (thread lock, comment restore);
        // re-deriving would mint a new key on every pass and duplicate them silently.
        Assert.Equal(acceptedAfterFirstPass, kernel.DistinctKeys);
    }

    // ---------------------------------------------------------------------------------------------
    // 8. COMPANY ISOLATION. A replay pass pinned to one company must not forward another's events.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Replay_stays_inside_the_company_it_was_pinned_to()
    {
        using var host = new CommunicationTestHost(BridgeOn(CommBridgeFailureMode.Isolate))
        {
            BridgeOverride = ThrowingBusinessEventBridge.MissingBusinessEventsTable(),
        };

        await host.Comments().AddAsync(
            CommunicationTestHost.Context(), host.CommentOn(CommunicationTestHost.Invoice(), "ours"));
        await host.Comments().AddAsync(
            CommunicationTestHost.Context(
                employeeId: CommunicationTestHost.OtherCompanyEmployee,
                companyId: CommunicationTestHost.OtherCompanyId),
            host.CommentOn(CommunicationTestHost.Invoice(2002), "theirs"));

        var kernel = new RecordingBusinessEventBridge();
        var report = await host.BridgeReplay(bridge: kernel).ReplayAsync(CommunicationTestHost.CompanyId);

        // Asymmetric on purpose: a count alone cannot tell which company's rows survived.
        Assert.All(kernel.Forwarded, e => Assert.Equal(CommunicationTestHost.CompanyId, e.CompanyId));
        Assert.DoesNotContain(2002, kernel.Forwarded.Select(e => e.Entity.EntityId));
        Assert.True(report.Scanned > 0);
    }

    // ---------------------------------------------------------------------------------------------
    // 9. THE ISOLATION PATH MUST NOT BE ABLE TO THROW.
    //
    // CommAuditWriter REFUSES a detail containing a key such as "body": — a deliberate rule that stops
    // comment content leaking into the audit log. A provider exception message is arbitrary text, so a
    // kernel fault whose message happens to spell that key would make the record-keeping throw from
    // inside the catch that exists to protect the comment, and the operation would fail after all.
    //
    // This is the adversarial case for the fix itself rather than for the bridge.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_hostile_failure_message_cannot_break_the_isolation_that_protects_the_comment()
    {
        using var host = new CommunicationTestHost(BridgeOn(CommBridgeFailureMode.Isolate))
        {
            BridgeOverride = new ThrowingBusinessEventBridge(
                () => new InvalidOperationException("kernel rejected payload {\"body\": \"leaked\"}")),
        };

        var added = await host.Comments().AddAsync(
            CommunicationTestHost.Context(), host.CommentOn(CommunicationTestHost.Invoice(), "still here"));

        using var fresh = host.NewContext();
        var stored = await fresh.Set<CommComment>().AsNoTracking().SingleAsync(c => c.Id == added.CommentId);
        Assert.Equal("still here", stored.Body);
    }
}
