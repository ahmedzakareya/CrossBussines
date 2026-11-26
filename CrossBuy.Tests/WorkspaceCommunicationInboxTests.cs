using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.Communication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests;

public sealed class WorkspaceCommunicationInboxTests
{
    private sealed class Context(BusinessContext? value) : IBusinessContextAccessor
    {
        public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken=default) => Task.FromResult(value);
        public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken=default) => value is null ? throw new BusinessContextUnresolvedException("test") : Task.FromResult(value);
    }
    private sealed class Identity : IWorkspaceIdentityResolver
    {
        public Task<(string EmployeeName,string? CompanyName)> ResolveAsync(BusinessContext context,CancellationToken cancellationToken=default) => Task.FromResult(("test",(string?)"test"));
    }
    private static WorkspaceService Workspace(CommunicationTestHost host,IServiceProvider services,BusinessContext? context) => new(
        new Context(context),services,new PlatformNotificationWorkspaceSource(host.Db),new Identity(),
        Array.Empty<IWorkspaceFavoritesSource>(),Array.Empty<IWorkspaceActivitySource>(),Array.Empty<IWorkspaceReportSource>(),Array.Empty<IWorkspaceKpiSource>(), NullLogger<WorkspaceService>.Instance);
    private static BusinessContext Me(int? employee=11) => new(){CompanyId=1,EmployeeId=employee,Source=BusinessContextSource.Test};
    private static CommNotification Notice(string title,int employee=11) => new()
    {
        CompanyID=1,RecipientEmployeeId=employee,TemplateKey=CommTemplateKeys.MentionedInComment,
        Category=CommNotificationCategories.Mentions,Priority="Normal",LegacyType="doc_comment",
        EntityType="SalesInvoice",EntityId=42,TitleAr=title,TitleEn=title,BodyAr=title,BodyEn=title,
        DedupKey=Guid.NewGuid().ToString("N"),CreatedAt=DateTime.UtcNow
    };

    // =============================================================================================
    // CONVERSATIONS ON RECORDS — the Hub's business-conversation read, consumed by Workspace.
    //
    // THE FAKE IS A FAITHFUL HUB, NOT AN AGREEABLE ONE. It answers only the one method Workspace
    // calls and throws on anything else, so a future change that quietly starts asking the Hub a
    // second question fails here instead of passing against a double that said yes to everything.
    // =============================================================================================
    private sealed class Hub : ICommTaskIntegrationService
    {
        private readonly IReadOnlyList<CommBusinessConversation>? _rows;
        private readonly Exception? _throw;
        public Hub(IReadOnlyList<CommBusinessConversation>? rows = null, Exception? fail = null)
        { _rows = rows; _throw = fail; }

        public int Calls { get; private set; }
        public int LastTake { get; private set; }
        public BusinessContext? LastContext { get; private set; }

        public Task<IReadOnlyList<CommBusinessConversation>> MyBusinessConversationsAsync(
            BusinessContext context, int take = 30, CancellationToken cancellationToken = default)
        {
            Calls++; LastTake = take; LastContext = context;
            if (_throw != null) throw _throw;
            return Task.FromResult(_rows ?? Array.Empty<CommBusinessConversation>());
        }

        private static Task<T> No<T>([System.Runtime.CompilerServices.CallerMemberName] string m = "")
            => throw new InvalidOperationException($"Workspace must not call ICommTaskIntegrationService.{m}.");

        // ALIGNED TO THE REAL ICommTaskIntegrationService (TAB-3).
        //
        // These were written against an assumed contract and did not match: PanelForEntityAsync
        // returned a CommPanelView that does not exist, TasksForEntityAsync is not on the interface
        // (the two reads are RelatedTasksAsync for shared business context and LinkedTasksAsync for
        // explicit links), and LinkTaskAsync/bool is really LinkExistingAsync/CommTaskResult. Five
        // further members were absent, so the double did not satisfy the interface and the whole test
        // project stopped compiling.
        //
        // The design above is preserved exactly: every member Workspace must NOT call still throws,
        // so a future change that quietly asks the Hub a second question fails here rather than
        // passing against a double that agreed to everything.
        public Task<CommTaskResult> CreateFromMessageAsync(BusinessContext c, CommTaskCreateRequest r, CancellationToken k = default) => No<CommTaskResult>();
        public Task<IReadOnlyList<CommTaskRef>> RelatedTasksAsync(BusinessContext c, long threadId, CancellationToken k = default) => No<IReadOnlyList<CommTaskRef>>();
        public Task<IReadOnlyList<CommTaskRef>> LinkedTasksAsync(BusinessContext c, long threadId, CancellationToken k = default) => No<IReadOnlyList<CommTaskRef>>();
        public bool LinkingAvailable => false;
        public Task<CommTaskResult> LinkExistingAsync(BusinessContext c, CommTaskLinkRequest r, CancellationToken k = default) => No<CommTaskResult>();
        public Task<CommTaskResult> UnlinkAsync(BusinessContext c, long linkId, CancellationToken k = default) => No<CommTaskResult>();
        public Task<CommTaskConversation?> ConversationForTaskAsync(BusinessContext c, int t, CancellationToken k = default) => No<CommTaskConversation?>();
        public Task<CommConversationTasksPanel?> PanelForEntityAsync(BusinessContext c, CommEntityRef e, CancellationToken k = default) => No<CommConversationTasksPanel?>();
        public Task<CommConversationApprovals> ApprovalsForEntityAsync(BusinessContext c, CommEntityRef e, CancellationToken k = default) => No<CommConversationApprovals>();
    }

    private static CommBusinessConversation Conv(string entityType, int entityId, long threadId = 1,
        string? subject = null, int comments = 3, bool muted = false) => new()
    {
        ThreadId = threadId, EntityType = entityType, EntityId = entityId, Kind = "Entity",
        SubjectAr = subject, SubjectEn = subject, CommentCount = comments,
        LastActivityAt = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc),
        Role = "Participant", Muted = muted,
    };

    private static ServiceProvider WithHub(CommunicationTestHost host, Hub hub, bool withRegistry = true)
    {
        var services = new ServiceCollection()
            .AddSingleton<ICommNotificationService>(host.Notifications())
            .AddSingleton<ICommTaskIntegrationService>(hub);
        if (withRegistry) services.AddSingleton<IEntityRegistry>(new EntityRegistry(host.Db));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Conversations_are_off_unless_a_screen_asks_for_them()
    {
        using var host = new CommunicationTestHost();
        var hub = new Hub(new[] { Conv(EntityRegistry.SalesInvoice, 42) });
        using var provider = WithHub(host, hub);

        // The paged notifications screen does not render them, so it must not pay to read them.
        var without = await Workspace(host, provider, Me()).GetCommunicationInboxAsync();
        Assert.Equal(WorkspacePanelState.Unavailable, without.Conversations.State);
        Assert.Empty(without.Conversations.Items);
        Assert.Equal(0, hub.Calls);

        // THE ALLOW: the dashboard asks, and only then is the Hub consulted.
        var with = await Workspace(host, provider, Me()).GetCommunicationInboxAsync(includeConversations: true);
        Assert.Equal(WorkspacePanelState.Ready, with.Conversations.State);
        Assert.Equal(1, hub.Calls);
    }

    [Fact]
    public async Task A_conversation_carries_the_registrys_label_and_route_and_the_hubs_facts()
    {
        using var host = new CommunicationTestHost();
        var hub = new Hub(new[] { Conv(EntityRegistry.SalesInvoice, 42, threadId: 77, subject: "عن الفاتورة", comments: 5) });
        using var provider = WithHub(host, hub);

        var inbox = await Workspace(host, provider, Me()).GetCommunicationInboxAsync(includeConversations: true);
        var row = Assert.Single(inbox.Conversations.Items);

        // The Hub's own facts, carried verbatim.
        Assert.Equal(77, row.ThreadId);
        Assert.Equal(EntityRegistry.SalesInvoice, row.EntityType);
        Assert.Equal(42, row.EntityId);
        Assert.Equal(5, row.CommentCount);
        Assert.Equal("Participant", row.Role);
        Assert.Equal("عن الفاتورة", row.SubjectAr);

        // The registry's answer - a real route, not a guess, and it names the record it points at.
        Assert.True(row.IsResolved);
        Assert.False(string.IsNullOrWhiteSpace(row.EntityLabelEn));
        Assert.False(string.IsNullOrWhiteSpace(row.Url));
        Assert.Contains("42", row.Url!, StringComparison.Ordinal);

        // The viewer's own context reached the Hub - not a substituted or elevated one.
        Assert.Equal(1, hub.LastContext!.CompanyId);
        Assert.Equal(11, hub.LastContext.EmployeeId);
    }

    [Fact]
    public async Task An_unregistered_entity_is_listed_without_an_invented_anchor()
    {
        using var host = new CommunicationTestHost();
        // A code the registry does not publish. The conversation is real and the caller takes part in
        // it, so hiding the row would lose work; naming a controller for it would be a fabrication.
        var hub = new Hub(new[] { Conv("PayrollRun", 9) });
        using var provider = WithHub(host, hub);

        var inbox = await Workspace(host, provider, Me()).GetCommunicationInboxAsync(includeConversations: true);
        var row = Assert.Single(inbox.Conversations.Items);

        Assert.Equal("PayrollRun", row.EntityType);
        Assert.Null(row.Url);
        Assert.Null(row.EntityLabelEn);
        Assert.False(row.IsResolved);

        // THE ALLOW that makes the nulls above mean something: a registered code in the same call
        // DOES get a route.
        var known = new Hub(new[] { Conv(EntityRegistry.SalesInvoice, 42) });
        using var p2 = WithHub(host, known);
        Assert.NotNull(Assert.Single((await Workspace(host, p2, Me())
            .GetCommunicationInboxAsync(includeConversations: true)).Conversations.Items).Url);
    }

    [Fact]
    public async Task The_two_halves_of_the_panel_fail_independently()
    {
        using var host = new CommunicationTestHost();
        host.Db.Add(Notice("a notification"));
        await host.Db.SaveChangesAsync();

        // A Hub that throws must not cost the caller their notifications.
        var angry = new Hub(fail: new InvalidOperationException("the hub is having a bad day"));
        using var provider = WithHub(host, angry);

        var inbox = await Workspace(host, provider, Me()).GetCommunicationInboxAsync(includeConversations: true);
        Assert.Equal(WorkspacePanelState.TemporaryFailure, inbox.Conversations.State);
        Assert.Equal(WorkspacePanelState.Ready, inbox.State);
        Assert.NotEmpty(inbox.Page.Items);
    }

    [Fact]
    public async Task An_absent_hub_is_unavailable_and_an_empty_hub_is_empty()
    {
        using var host = new CommunicationTestHost();

        // Communication is optional in some deployments: absent is a state, not a failure.
        using var noHub = new ServiceCollection()
            .AddSingleton<ICommNotificationService>(host.Notifications()).BuildServiceProvider();
        Assert.Equal(WorkspacePanelState.Unavailable,
            (await Workspace(host, noHub, Me()).GetCommunicationInboxAsync(includeConversations: true)).Conversations.State);

        // Present but with nothing to show reads differently, so "no conversations" is never confused
        // with "conversations are switched off".
        using var empty = WithHub(host, new Hub(Array.Empty<CommBusinessConversation>()));
        Assert.Equal(WorkspacePanelState.Empty,
            (await Workspace(host, empty, Me()).GetCommunicationInboxAsync(includeConversations: true)).Conversations.State);
    }

    [Fact]
    public async Task An_unresolved_company_or_identity_never_reaches_the_hub()
    {
        using var host = new CommunicationTestHost();
        var hub = new Hub(new[] { Conv(EntityRegistry.SalesInvoice, 42) });
        using var provider = WithHub(host, hub);

        // Fail closed BEFORE the Hub is asked - an unidentified caller must not become a query.
        Assert.Equal(WorkspacePanelState.AccessDenied,
            (await Workspace(host, provider, Me(null)).GetCommunicationInboxAsync(includeConversations: true)).State);
        Assert.Equal(WorkspacePanelState.AccessDenied,
            (await Workspace(host, provider, null).GetCommunicationInboxAsync(includeConversations: true)).State);
        Assert.Equal(0, hub.Calls);

        // THE ALLOW.
        Assert.Equal(WorkspacePanelState.Ready,
            (await Workspace(host, provider, Me()).GetCommunicationInboxAsync(includeConversations: true)).Conversations.State);
        Assert.Equal(1, hub.Calls);
    }

    [Fact]
    public async Task Communication_rows_are_reachable_through_owner_service_with_independent_cursor_and_recipient_scope()
    {
        using var host=new CommunicationTestHost();
        host.Db.AddRange(Notice("first"),Notice("second"),Notice("other recipient",12));
        host.Db.Notifications.Add(new CrossBuy.Models.Context.Admin.Notification {CompanyID=1,RecipientEmployeeID=11,TitleEn="legacy",TitleAr="legacy",CreatedAt=DateTime.UtcNow});
        await host.Db.SaveChangesAsync();
        using var fresh=host.NewContext();
        using var provider=new ServiceCollection().AddSingleton<ICommNotificationService>(host.Notifications(fresh)).BuildServiceProvider();
        var service=Workspace(host,provider,Me());
        var first=await service.GetCommunicationInboxAsync(take:1);
        Assert.Equal(WorkspacePanelState.Ready,first.State);
        Assert.Equal("second",Assert.Single(first.Page.Items).TitleEn);
        Assert.Equal(2,first.UnreadTotal);
        Assert.NotNull(first.Page.NextCursor);
        var next=await service.GetCommunicationInboxAsync(afterId:first.Page.NextCursor,take:1);
        Assert.Equal("first",Assert.Single(next.Page.Items).TitleEn);
        Assert.Null(next.Page.NextCursor);
        Assert.NotEqual(first.Page.Items[0].NotificationId,next.Page.Items[0].NotificationId);
        var legacy=await service.GetNotificationsAsync();
        Assert.Equal("legacy",Assert.Single(legacy.Items).Title);
        Assert.DoesNotContain(first.Page.Items,x=>x.TitleEn=="other recipient" || x.TitleEn=="legacy");
    }

    [Fact]
    public async Task Missing_platform_and_unresolved_identity_remain_distinct_states()
    {
        using var host=new CommunicationTestHost();
        using var provider=new ServiceCollection().BuildServiceProvider();
        Assert.Equal(WorkspacePanelState.Unavailable,(await Workspace(host,provider,Me()).GetCommunicationInboxAsync()).State);
        Assert.Equal(WorkspacePanelState.AccessDenied,(await Workspace(host,provider,Me(null)).GetCommunicationInboxAsync()).State);
        Assert.Equal(WorkspacePanelState.AccessDenied,(await Workspace(host,provider,null).GetCommunicationInboxAsync()).State);
    }

    [Fact]
    public async Task Cancellation_is_propagated_instead_of_rendering_an_empty_inbox()
    {
        using var host=new CommunicationTestHost();
        using var provider=new ServiceCollection().AddSingleton<ICommNotificationService>(host.Notifications()).BuildServiceProvider();
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Workspace(host,provider,Me()).GetCommunicationInboxAsync(cancellationToken:cancellation.Token));
    }
}
