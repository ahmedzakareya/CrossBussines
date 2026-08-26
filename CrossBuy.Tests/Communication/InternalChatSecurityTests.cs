using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Hubs;
using CrossBuy.Models.Context.Chat;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Internal Chat — security closure (F1..F8).
    //
    // These are BEHAVIOURAL tests against a real ChatService over a real CrossDbContext, with the real
    // CommunicationAccessService making the access decisions. Only three things are doubled: the SignalR
    // hub context (so a broadcast can be OBSERVED, which is itself an assertion), the notification service,
    // and the role directory. Nothing that decides access is stubbed out, because a test that stubs the
    // decision proves only that the stub works.
    //
    // WHY A RECORDING HUB MATTERS. "Refused" is not just "no row written" — ToggleReactionAsync used to
    // broadcast into a conversation group, so a refusal that still fired an event would leak the fact that
    // something happened to people in a conversation the caller cannot read. The recorder makes the absence
    // of that event provable rather than assumed.
    // =============================================================================================

    // ---- doubles -------------------------------------------------------------------------------
    internal sealed class RecordingHubClients : IHubClients
    {
        public readonly List<(string Target, string Method)> Sent = new();
        private sealed class Proxy : IClientProxy
        {
            private readonly RecordingHubClients _owner; private readonly string _target;
            public Proxy(RecordingHubClients o, string t) { _owner = o; _target = t; }
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            { _owner.Sent.Add((_target, method)); return Task.CompletedTask; }
        }
        public IClientProxy All => new Proxy(this, "all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new Proxy(this, "allExcept");
        public IClientProxy Client(string connectionId) => new Proxy(this, "client:" + connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new Proxy(this, "clients");
        public IClientProxy Group(string groupName) => new Proxy(this, groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new Proxy(this, groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new Proxy(this, string.Join(",", groupNames));
        public IClientProxy User(string userId) => new Proxy(this, "user:" + userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new Proxy(this, "users");
    }

    internal sealed class RecordingHubContext : IHubContext<ChatHub>
    {
        public readonly RecordingHubClients Recorder = new();
        public IHubClients Clients => Recorder;
        public IGroupManager Groups { get; } = new NoopGroups();
        private sealed class NoopGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }

    internal sealed class SilentNotifications : INotificationService
    {
        public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
            string? bodyAr, string? bodyEn, string type, int? refId = null,
            string? url = null, int? companyId = null, int? actorEmployeeId = null,
            string? priority = null, string? category = null, string? dedupKey = null,
            DateTime? expiresAt = null, string? icon = null,
            string? entityType = null, int? entityId = null)
            => Task.CompletedTask;

        public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
            string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
            string type, int? refId = null, int? exceptEmployeeId = null)
            => Task.FromResult(0);
    }

    // No grants and nothing configured => bootstrapOpen. Deliberately the most PERMISSIVE realistic posture:
    // if a refusal still holds here, it holds when roles are configured too. Conversation-scoped read/send
    // require membership even under bootstrap-open, which is the property under test.
    internal sealed class OpenRoleDirectory : IPlatformRoleDirectory
    {
        public Task<IReadOnlyList<RoleGrant>> RolesAsync(BusinessContext context, string scope, CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<RoleGrant>)Array.Empty<RoleGrant>());
        public Task<bool> AnyConfiguredAsync(int companyId, string scope, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    internal sealed class SwitchableContext : IBusinessContextAccessor
    {
        public BusinessContext? Current;
        public bool Throw;
        public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
            => Current == null ? throw new BusinessContextUnresolvedException("none") : Task.FromResult(Current);
        public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
            => Throw ? throw new InvalidOperationException("resolver exploded") : Task.FromResult(Current);
    }

    // A permission service that is present but cannot answer. Models "the service is unavailable" — the
    // case that must fail CLOSED rather than fall through to allowed.
    internal sealed class ExplodingAccess : ICommunicationAccessService
    {
        public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("permission service unavailable");
        public Task<bool> IsConversationParticipantAsync(BusinessContext context, int conversationId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("permission service unavailable");
        public Task<AccessScope> ResolveConversationScopeAsync(BusinessContext context, string action, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("permission service unavailable");
    }

    // ---- harness -------------------------------------------------------------------------------
    internal sealed class ChatHost : IDisposable
    {
        public readonly PlatformTestHost Platform = new(companyId: null);   // unfiltered: these tests seed two companies
        public readonly RecordingHubContext Hub = new();
        public readonly SwitchableContext Contexts = new();

        public const int CompanyA = 1, CompanyB = 2;
        public const int Alice = 11, Bob = 12, Carol = 13, Mallory = 91;   // Mallory belongs to CompanyB

        public ChatHost()
        {
            // Employees: three in company A, one in company B.
            Add(Alice, CompanyA, "أليس", "Alice");
            Add(Bob, CompanyA, "بوب", "Bob");
            Add(Carol, CompanyA, "كارول", "Carol");
            Add(Mallory, CompanyB, "مالوري", "Mallory");
            Platform.Db.SaveChanges();

            // The SAME partial unique index deploy/sql/comm_chat_identity_001.sql creates. EnsureCreated()
            // builds tables from the model and knows nothing about the slice, so without this line the
            // concurrency test would be asserting against a database that cannot refuse anything — it would
            // pass for the wrong reason. A companion test asserts the slice still declares this index, so
            // the two cannot drift apart silently.
            Platform.Db.Database.ExecuteSqlRaw(
                "CREATE UNIQUE INDEX IF NOT EXISTS UX_Conversations_DirectPair " +
                "ON Conversations (CompanyID, DirectKeyLow, DirectKeyHigh) " +
                "WHERE Kind = 'Direct' AND DirectKeyLow IS NOT NULL AND DirectKeyHigh IS NOT NULL;");
        }

        private void Add(int id, int companyId, string ar, string en)
            => Platform.Db.Employee.Add(new CrossBuy.Models.Context.Admin.Employee
            {
                ID = id, EmpCompanyID = companyId, FullName = ar, FullNameEn = en, JobTitleID = 1,
                IsActive = true, FirstName = en, LastName = id.ToString(),
                Address = "", PhoneNumber = "", Email = $"e{id}@test.local",
                Gender = "", MaritalStatus = "", ProfileImage = "", UserId = "u" + id,
            });

        public void ActAs(int employeeId, int companyId)
            => Contexts.Current = new BusinessContext { CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId };

        public void ActAsNobody() => Contexts.Current = null;

        public ICommunicationAccessService RealAccess(CrossBuy.Models.Context.CrossDbContext db)
            => new CommunicationAccessService(db, new OpenRoleDirectory(), NullLogger<CommunicationAccessService>.Instance);

        public ChatService Service(ICommunicationAccessService? access = null)
        {
            var db = Platform.Db;
            return new ChatService(db, Hub, new SilentNotifications(), access ?? RealAccess(db), Contexts);
        }

        // Seeds a conversation directly, bypassing the service, so a test can arrange a state the service
        // would refuse to create (a foreign-company conversation, for instance).
        public int SeedDirect(int companyId, int a, int b)
        {
            var (low, high) = Conversation.DirectKey(a, b);
            var conv = new Conversation
            {
                CompanyID = companyId, Kind = "Direct", CreatedByEmployeeId = a, CreatedAt = DateTime.UtcNow,
                DirectKeyLow = low, DirectKeyHigh = high,
            };
            Platform.Db.Conversations.Add(conv);
            Platform.Db.SaveChanges();
            Platform.Db.ConversationMembers.AddRange(
                new ConversationMember { ConversationId = conv.ID, EmployeeId = a, Role = "Member", JoinedAt = DateTime.UtcNow },
                new ConversationMember { ConversationId = conv.ID, EmployeeId = b, Role = "Member", JoinedAt = DateTime.UtcNow });
            Platform.Db.SaveChanges();
            return conv.ID;
        }

        public int SeedMessage(int conversationId, int senderId, string body)
        {
            var m = new ChatMessage { ConversationId = conversationId, SenderEmployeeId = senderId, Body = body, CreatedAt = DateTime.UtcNow };
            Platform.Db.ChatMessages.Add(m);
            Platform.Db.SaveChanges();
            return m.ID;
        }

        public void Dispose() => Platform.Dispose();
    }

    public class InternalChatSecurityTests
    {
        // ---- F4 / identity -------------------------------------------------------------------
        [Fact]
        public async Task Alice_can_start_a_direct_conversation_with_an_eligible_colleague()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var id = await h.Service().GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            Assert.True(id > 0);
        }

        [Fact]
        public async Task A_to_B_and_B_to_A_resolve_the_SAME_conversation()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var first = await h.Service().GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Bob, ChatHost.CompanyA);
            var second = await h.Service().GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Bob, ChatHost.Alice);
            Assert.Equal(first, second);
        }

        [Fact]
        public async Task Repeated_get_or_create_returns_the_same_conversation_and_creates_no_duplicate()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service();
            var a = await svc.GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var b = await svc.GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var c = await svc.GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            Assert.Equal(a, b);
            Assert.Equal(b, c);
            Assert.Equal(1, await h.Platform.Db.Conversations.CountAsync(x => x.Kind == "Direct"));
        }

        [Fact]
        public async Task The_database_refuses_a_second_direct_conversation_for_the_same_pair()
        {
            // The persistence-level guarantee, asserted against the index rather than the service: if the
            // service's own lookup were removed tomorrow, this is what still prevents the duplicate.
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            await h.Service().GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);

            var (low, high) = Conversation.DirectKey(ChatHost.Alice, ChatHost.Bob);
            h.Platform.Db.Conversations.Add(new Conversation
            {
                CompanyID = ChatHost.CompanyA, Kind = "Direct", CreatedByEmployeeId = ChatHost.Alice,
                CreatedAt = DateTime.UtcNow, DirectKeyLow = low, DirectKeyHigh = high,
            });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => h.Platform.Db.SaveChangesAsync());
        }

        [Fact]
        public async Task A_lost_creation_race_returns_the_winning_conversation_rather_than_an_error()
        {
            // Simulates the race outcome the index produces: the row already exists by the time this caller
            // tries to insert. The caller must receive the existing conversation, not an exception and not a
            // second conversation.
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var winner = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);

            var got = await h.Service().GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            Assert.Equal(winner, got);
            Assert.Equal(1, await h.Platform.Db.Conversations.CountAsync(x => x.Kind == "Direct"));
        }

        [Fact]
        public async Task Two_companies_may_each_hold_a_direct_conversation_for_the_same_employee_ids()
        {
            // Company is part of the identity, so the index must not let the first company block the second.
            using var h = new ChatHost();
            var a = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var b = h.SeedDirect(ChatHost.CompanyB, ChatHost.Alice, ChatHost.Bob);
            Assert.NotEqual(a, b);
            Assert.Equal(2, await h.Platform.Db.Conversations.CountAsync(x => x.Kind == "Direct"));
        }

        [Fact]
        public async Task Self_chat_is_refused()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            Assert.Equal(0, await h.Service().GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Alice));
            Assert.Equal(0, await h.Platform.Db.Conversations.CountAsync());
        }

        [Fact]
        public async Task Starting_a_conversation_with_another_companys_employee_is_refused()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            Assert.Equal(0, await h.Service().GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Mallory));
            Assert.Equal(0, await h.Platform.Db.Conversations.CountAsync());
        }

        // ---- F1 / F2 reply boundary ----------------------------------------------------------
        [Fact]
        public async Task A_reply_to_a_message_in_the_same_conversation_succeeds()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var target = h.SeedMessage(conv, ChatHost.Bob, "original");
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var dto = await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "replying", null, null, null, target, null);
            Assert.NotNull(dto);
            Assert.Equal(target, dto!.ReplyToId);
            Assert.Equal("original", dto.ReplyToBody);
        }

        [Fact]
        public async Task A_reply_targeting_another_conversation_is_refused_and_stores_nothing()
        {
            using var h = new ChatHost();
            var mine = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var other = h.SeedDirect(ChatHost.CompanyA, ChatHost.Bob, ChatHost.Carol);
            var foreign = h.SeedMessage(other, ChatHost.Carol, "not for Alice");

            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var dto = await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, mine, "quote it", null, null, null, foreign, null);

            Assert.Null(dto);
            Assert.Equal(0, await h.Platform.Db.ChatMessages.CountAsync(m => m.ConversationId == mine));
        }

        [Fact]
        public async Task A_reply_targeting_another_COMPANYS_message_is_refused()
        {
            using var h = new ChatHost();
            var mine = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var theirs = h.SeedDirect(ChatHost.CompanyB, ChatHost.Mallory, ChatHost.Alice);
            var foreign = h.SeedMessage(theirs, ChatHost.Mallory, "another company's secret");

            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var dto = await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, mine, "quote it", null, null, null, foreign, null);
            Assert.Null(dto);
        }

        [Fact]
        public async Task A_persisted_foreign_ReplyToId_leaks_neither_body_nor_sender_at_read_time()
        {
            // THE MUTATION TEST FOR F2. The bad row is written directly, exactly as a pre-fix build or a
            // future unvalidated path would write it, so the write-side check (F1) cannot be what saves us.
            // If ReplyPreviewsAsync ever loses its conversation predicate, this test fails.
            using var h = new ChatHost();
            var mine = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var theirs = h.SeedDirect(ChatHost.CompanyB, ChatHost.Mallory, ChatHost.Carol);
            var foreign = h.SeedMessage(theirs, ChatHost.Mallory, "SECRET-BODY-MUST-NOT-APPEAR");

            var poisoned = new ChatMessage
            {
                ConversationId = mine, SenderEmployeeId = ChatHost.Alice, Body = "innocent",
                ReplyToId = foreign, CreatedAt = DateTime.UtcNow,
            };
            h.Platform.Db.ChatMessages.Add(poisoned);
            await h.Platform.Db.SaveChangesAsync();

            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var msgs = await h.Service().GetMessagesAsync(ChatHost.Alice, mine, null);

            var rendered = Assert.Single(msgs);
            Assert.Null(rendered.ReplyToBody);
            Assert.Null(rendered.ReplyToSender);
            Assert.DoesNotContain("SECRET-BODY-MUST-NOT-APPEAR", rendered.ReplyToBody ?? "");
        }

        // ---- F3 reactions --------------------------------------------------------------------
        [Fact]
        public async Task A_member_may_react()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var msg = h.SeedMessage(conv, ChatHost.Bob, "hello");
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);

            await h.Service().ToggleReactionAsync(ChatHost.Alice, msg, "👍");

            Assert.Equal(1, await h.Platform.Db.ChatReactions.CountAsync(r => r.MessageId == msg));
            Assert.Contains(h.Hub.Recorder.Sent, s => s.Method == "reaction");
        }

        [Fact]
        public async Task A_non_member_reaction_writes_nothing_and_broadcasts_nothing()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var msg = h.SeedMessage(conv, ChatHost.Bob, "private to Alice and Bob");
            h.ActAs(ChatHost.Carol, ChatHost.CompanyA);          // same company, NOT a member
            h.Hub.Recorder.Sent.Clear();

            await h.Service().ToggleReactionAsync(ChatHost.Carol, msg, "👍");

            Assert.Equal(0, await h.Platform.Db.ChatReactions.CountAsync());
            Assert.Empty(h.Hub.Recorder.Sent);
        }

        [Fact]
        public async Task A_cross_company_reaction_writes_nothing_and_broadcasts_nothing()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var msg = h.SeedMessage(conv, ChatHost.Bob, "company A only");
            h.ActAs(ChatHost.Mallory, ChatHost.CompanyB);
            h.Hub.Recorder.Sent.Clear();

            await h.Service().ToggleReactionAsync(ChatHost.Mallory, msg, "🔥");

            Assert.Equal(0, await h.Platform.Db.ChatReactions.CountAsync());
            Assert.Empty(h.Hub.Recorder.Sent);
        }

        // ---- F5 context + permissions --------------------------------------------------------
        [Fact]
        public async Task An_unresolved_company_refuses_every_operation()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.SeedMessage(conv, ChatHost.Bob, "hello");
            h.ActAsNobody();
            var svc = h.Service();

            Assert.Empty(await svc.GetMessagesAsync(ChatHost.Alice, conv, null));
            Assert.Empty(await svc.MyConversationsAsync(ChatHost.CompanyA, ChatHost.Alice));
            Assert.Empty(await svc.DirectoryAsync(ChatHost.CompanyA, ChatHost.Alice, null));
            Assert.Null(await svc.GetHeaderAsync(ChatHost.CompanyA, ChatHost.Alice, conv));
            Assert.Null(await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "hi", null, null, null, null, null));
            Assert.Equal(0, await svc.GetOrCreateDirectAsync(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Carol));
        }

        [Fact]
        public async Task A_context_resolver_that_throws_fails_closed()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            h.Contexts.Throw = true;

            Assert.Null(await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "hi", null, null, null, null, null));
            Assert.Equal(0, await h.Platform.Db.ChatMessages.CountAsync());
        }

        [Fact]
        public async Task An_unavailable_permission_service_fails_closed()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.SeedMessage(conv, ChatHost.Bob, "hello");
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service(new ExplodingAccess());   // present, cannot answer

            Assert.Null(await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "hi", null, null, null, null, null));
            Assert.Empty(await svc.GetMessagesAsync(ChatHost.Alice, conv, null));
            Assert.False(await svc.CanSendToAsync(conv));
            Assert.Equal(1, await h.Platform.Db.ChatMessages.CountAsync());   // only the seeded one
        }

        [Fact]
        public async Task A_non_member_cannot_read_messages_and_the_refusal_looks_like_an_empty_conversation()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.SeedMessage(conv, ChatHost.Bob, "private");
            h.ActAs(ChatHost.Carol, ChatHost.CompanyA);

            var refused = await h.Service().GetMessagesAsync(ChatHost.Carol, conv, null);
            var absent = await h.Service().GetMessagesAsync(ChatHost.Carol, 999999, null);

            Assert.Empty(refused);
            Assert.Empty(absent);            // inaccessible and nonexistent are indistinguishable
        }

        [Fact]
        public async Task Another_company_cannot_read_send_or_mark_read()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var msg = h.SeedMessage(conv, ChatHost.Bob, "company A only");
            h.ActAs(ChatHost.Mallory, ChatHost.CompanyB);
            var svc = h.Service();

            Assert.Empty(await svc.GetMessagesAsync(ChatHost.Mallory, conv, null));
            Assert.Null(await svc.SendAsync(ChatHost.CompanyB, ChatHost.Mallory, conv, "intrusion", null, null, null, null, null));
            await svc.MarkReadAsync(ChatHost.Mallory, conv);

            Assert.Equal(1, await h.Platform.Db.ChatMessages.CountAsync());
            // The victim's own read marker is untouched by the intruder's MarkRead.
            var bobMarker = await h.Platform.Db.ConversationMembers
                .Where(m => m.ConversationId == conv && m.EmployeeId == ChatHost.Bob)
                .Select(m => m.LastReadMessageId).FirstAsync();
            Assert.Equal(0, bobMarker);
            Assert.True(msg > 0);
        }

        [Fact]
        public async Task The_employee_directory_is_scoped_to_the_resolved_company()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);

            // Even asked for company B explicitly, the resolved company wins.
            var listed = await h.Service().DirectoryAsync(ChatHost.CompanyB, ChatHost.Alice, null);

            Assert.DoesNotContain(listed, e => e.Id == ChatHost.Mallory);
            Assert.DoesNotContain(listed, e => e.Id == ChatHost.Alice);   // never offers the caller
            Assert.Contains(listed, e => e.Id == ChatHost.Bob);
        }

        // ---- F6 length ----------------------------------------------------------------------
        [Fact]
        public async Task An_oversized_message_is_refused_and_a_message_at_the_limit_is_accepted()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service();

            Assert.Null(await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv,
                new string('x', ChatService.MaxMessageLength + 1), null, null, null, null, null));
            Assert.NotNull(await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv,
                new string('y', ChatService.MaxMessageLength), null, null, null, null, null));
            Assert.Equal(1, await h.Platform.Db.ChatMessages.CountAsync());
        }

        [Fact]
        public async Task An_empty_or_whitespace_message_is_still_refused()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service();

            Assert.Null(await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "", null, null, null, null, null));
            Assert.Null(await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "   \t\n ", null, null, null, null, null));
            Assert.Null(await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, null, null, null, null, null, null));
            Assert.Equal(0, await h.Platform.Db.ChatMessages.CountAsync());
        }

        // ---- messages, ordering, read/unread ------------------------------------------------
        [Fact]
        public async Task A_normal_message_persists_with_sender_body_and_timestamp_in_deterministic_order()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service();

            await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "first", null, null, null, null, null);
            await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "second", null, null, null, null, null);
            await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "third", null, null, null, null, null);

            var msgs = await svc.GetMessagesAsync(ChatHost.Alice, conv, null);
            Assert.Equal(new[] { "first", "second", "third" }, msgs.Select(m => m.Body).ToArray());
            Assert.All(msgs, m => Assert.Equal(ChatHost.Alice, m.SenderId));
            Assert.All(msgs, m => Assert.NotEqual(default, m.CreatedAt));
        }

        [Fact]
        public async Task A_script_shaped_body_is_stored_verbatim_and_never_interpreted()
        {
            // The body is data. It is stored exactly as typed; escaping belongs to the renderer, and storing
            // a mangled version would corrupt legitimate messages about code.
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            const string payload = "<script>alert('x')</script><img src=x onerror=alert(1)>";

            var dto = await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, payload, null, null, null, null, null);

            Assert.Equal(payload, dto!.Body);
            var stored = await h.Platform.Db.ChatMessages.AsNoTracking().SingleAsync();
            Assert.Equal(payload, stored.Body);
        }

        [Fact]
        public async Task Unread_is_created_for_the_recipient_and_not_for_the_sender()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "hello Bob", null, null, null, null, null);

            var mine = Assert.Single(await h.Service().MyConversationsAsync(ChatHost.CompanyA, ChatHost.Alice));
            Assert.Equal(0, mine.Unread);           // my own message is not unread for me

            h.ActAs(ChatHost.Bob, ChatHost.CompanyA);
            var theirs = Assert.Single(await h.Service().MyConversationsAsync(ChatHost.CompanyA, ChatHost.Bob));
            Assert.Equal(1, theirs.Unread);
        }

        [Fact]
        public async Task Opening_a_conversation_clears_unread_and_the_cleared_state_survives_a_new_service_instance()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "hello Bob", null, null, null, null, null);

            h.ActAs(ChatHost.Bob, ChatHost.CompanyA);
            await h.Service().MarkReadAsync(ChatHost.Bob, conv);

            // A brand-new service over the same database — the state is persisted, not in-memory.
            var after = Assert.Single(await h.Service().MyConversationsAsync(ChatHost.CompanyA, ChatHost.Bob));
            Assert.Equal(0, after.Unread);
        }

        [Fact]
        public async Task Unread_is_tracked_independently_per_conversation()
        {
            using var h = new ChatHost();
            var one = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var two = h.SeedDirect(ChatHost.CompanyA, ChatHost.Carol, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, one, "from Alice", null, null, null, null, null);
            h.ActAs(ChatHost.Carol, ChatHost.CompanyA);
            await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Carol, two, "from Carol", null, null, null, null, null);

            h.ActAs(ChatHost.Bob, ChatHost.CompanyA);
            await h.Service().MarkReadAsync(ChatHost.Bob, one);

            var list = await h.Service().MyConversationsAsync(ChatHost.CompanyA, ChatHost.Bob);
            Assert.Equal(0, list.Single(x => x.Id == one).Unread);
            Assert.Equal(1, list.Single(x => x.Id == two).Unread);
        }

        // ---- edit / delete preserved ---------------------------------------------------------
        [Fact]
        public async Task Edit_and_delete_still_work_for_the_sender_and_refuse_for_everyone_else()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var mine = (await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "typo", null, null, null, null, null))!.Id;

            Assert.True(await h.Service().EditAsync(ChatHost.Alice, mine, "fixed"));
            Assert.Equal("fixed", (await h.Platform.Db.ChatMessages.AsNoTracking().SingleAsync(m => m.ID == mine)).Body);

            h.ActAs(ChatHost.Bob, ChatHost.CompanyA);                     // a member, but not the author
            Assert.False(await h.Service().EditAsync(ChatHost.Bob, mine, "tampered"));
            Assert.False(await h.Service().DeleteAsync(ChatHost.Bob, mine));
            Assert.Equal("fixed", (await h.Platform.Db.ChatMessages.AsNoTracking().SingleAsync(m => m.ID == mine)).Body);

            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            Assert.True(await h.Service().DeleteAsync(ChatHost.Alice, mine));
            Assert.NotNull((await h.Platform.Db.ChatMessages.AsNoTracking().SingleAsync(m => m.ID == mine)).DeletedAt);
        }

        [Fact]
        public async Task An_edit_cannot_push_a_message_past_the_length_limit()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var id = (await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "short", null, null, null, null, null))!.Id;

            Assert.False(await h.Service().EditAsync(ChatHost.Alice, id, new string('z', ChatService.MaxMessageLength + 1)));
            Assert.Equal("short", (await h.Platform.Db.ChatMessages.AsNoTracking().SingleAsync(m => m.ID == id)).Body);
        }

        // ---- F8 attachment policy -----------------------------------------------------------
        [Theory]
        [InlineData(".html", "text/html")]
        [InlineData(".htm", "text/html")]
        [InlineData(".svg", "image/svg+xml")]
        [InlineData(".js", "application/javascript")]
        [InlineData(".exe", "application/octet-stream")]
        [InlineData(".aspx", "text/html")]
        [InlineData("", "application/pdf")]
        public void Active_and_unlisted_attachment_types_are_refused(string ext, string contentType)
            => Assert.False(ChatAttachments.IsAcceptedType(ext, contentType));

        [Theory]
        [InlineData(".pdf", "application/pdf")]
        [InlineData(".png", "image/png")]
        [InlineData(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
        [InlineData(".xlsx", "application/vnd.ms-excel")]
        public void Ordinary_business_attachments_are_allowed(string ext, string contentType)
            => Assert.True(ChatAttachments.IsAcceptedType(ext, contentType));

        [Fact]
        public void An_image_extension_with_a_non_image_declared_type_is_refused()
            => Assert.False(ChatAttachments.IsAcceptedType(".png", "text/html"));

        [Fact]
        public async Task CanSendToAsync_refuses_a_non_member_so_no_file_is_ever_written()
        {
            // The controller calls this BEFORE saving the upload. If it said yes to a non-member, an
            // unauthorized file would land under wwwroot even though the message is refused.
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);

            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            Assert.True(await h.Service().CanSendToAsync(conv));

            h.ActAs(ChatHost.Carol, ChatHost.CompanyA);
            Assert.False(await h.Service().CanSendToAsync(conv));

            h.ActAs(ChatHost.Mallory, ChatHost.CompanyB);
            Assert.False(await h.Service().CanSendToAsync(conv));

            h.ActAsNobody();
            Assert.False(await h.Service().CanSendToAsync(conv));
        }

        // ---- structural guards ---------------------------------------------------------------
        [Fact]
        public void The_deployment_slice_still_declares_the_unique_index_the_tests_rely_on()
        {
            // The harness creates the index by hand for SQLite. If the slice ever stopped creating it, the
            // suite would still pass while production lost the guarantee — so the slice is asserted too.
            var sql = ReadRepoFile("CrossBuy/deploy/sql/comm_chat_identity_001.sql");
            var code = StripSqlComments(sql);
            Assert.Contains("CREATE UNIQUE INDEX UX_Conversations_DirectPair", code);
            Assert.Contains("CompanyID, DirectKeyLow, DirectKeyHigh", code);
            Assert.Contains("WHERE Kind = 'Direct'", code);
        }

        [Fact]
        public void The_reply_preview_lookup_cannot_be_called_without_a_conversation()
        {
            // A signature-level guard: ReplyPreviewsAsync must REQUIRE the conversation, so a future caller
            // cannot reintroduce the unscoped lookup by simply forgetting to pass it.
            var m = typeof(ChatService).GetMethod("ReplyPreviewsAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(m);
            var ps = m!.GetParameters();
            Assert.Equal(2, ps.Length);
            Assert.Equal("conversationId", ps[1].Name);
            Assert.False(ps[1].IsOptional);      // not optional: it cannot be silently omitted
        }

        [Fact]
        public void The_bare_Chat_route_is_registered()
        {
            var program = StripCsComments(ReadRepoFile("CrossBuy/Program.cs"));
            Assert.Contains("pattern: \"Chat\"", program);
            Assert.Contains("controller = \"Chat\"", program);
        }

        [Fact]
        public void A_commented_out_chat_route_would_not_count_as_registered()
        {
            // Proves the guard above reads code and not prose — the same trap that made an earlier route
            // test pass against a commented-out registration.
            const string sample = "// app.MapControllerRoute(name: \"chat-home\", pattern: \"Chat\");";
            Assert.DoesNotContain("pattern: \"Chat\"", StripCsComments(sample));
        }

        // ---- helpers -------------------------------------------------------------------------
        private static string ReadRepoFile(string relative)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                var candidate = System.IO.Path.Combine(dir, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
                dir = System.IO.Directory.GetParent(dir)?.FullName;
            }
            throw new System.IO.FileNotFoundException("could not locate " + relative + " above " + AppContext.BaseDirectory);
        }

        private static string StripCsComments(string s)
        {
            s = System.Text.RegularExpressions.Regex.Replace(s, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(s, @"//[^\n]*", " ");
        }

        private static string StripSqlComments(string s)
        {
            s = System.Text.RegularExpressions.Regex.Replace(s, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(s, @"--[^\n]*", " ");
        }
    }
}
