using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.Hubs;
using CrossBuy.Models.Context.Chat;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Internal Chat — FUNCTIONAL coverage.
    //
    // The security suite (InternalChatSecurityTests, 47 tests) proves what chat REFUSES. This suite
    // proves what it DOES, for behaviour that already exists. It deliberately adds no feature and
    // asserts nothing aspirational: every test here describes the engine as committed today, so it can
    // catch a regression rather than describe a wish.
    //
    // The previous audit found the functional gaps this file closes: paging, group lifecycle, reaction
    // toggle-off, notification targeting, conversation-list ordering and preview, directory search, and
    // the hub join gate — which had no test at all, only the service rule it delegates to.
    //
    // It reuses ChatHost from the security suite on purpose. A second harness would be a second set of
    // assumptions about how a conversation is seeded, and the two would drift.
    // =============================================================================================

    // Records what was sent, so notification TARGETING and the dedup key are observable. The dedup
    // decision itself lives in NotificationService; what ChatService owes is a stable key per
    // conversation and the right recipients, which is what this asserts.
    internal sealed class RecordingNotifications : INotificationService
    {
        public readonly List<(int Recipient, string Type, string? DedupKey, string? Url, int? RefId)> Sent = new();

        public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
            string? bodyAr, string? bodyEn, string type, int? refId = null,
            string? url = null, int? companyId = null, int? actorEmployeeId = null,
            string? priority = null, string? category = null, string? dedupKey = null,
            DateTime? expiresAt = null, string? icon = null,
            string? entityType = null, int? entityId = null)
        {
            Sent.Add((recipientEmployeeId, type, dedupKey, url, refId));
            return Task.CompletedTask;
        }

        public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
            string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
            string type, int? refId = null, int? exceptEmployeeId = null)
            => Task.FromResult(0);
    }

    // ---- minimal SignalR caller context, so the hub's own methods can be invoked --------------
    internal sealed class FakeHubCallerContext : HubCallerContext
    {
        private readonly Dictionary<object, object?> _items = new();
        public FakeHubCallerContext(ClaimsPrincipal? user) { User = user; }
        public override string ConnectionId => "test-connection";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    internal sealed class RecordingGroupManager : IGroupManager
    {
        public readonly List<string> Added = new();
        public readonly List<string> Removed = new();
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        { Added.Add(groupName); return Task.CompletedTask; }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        { Removed.Add(groupName); return Task.CompletedTask; }
    }

    public class InternalChatFunctionalTests
    {
        private static ChatService ServiceWith(ChatHost h, RecordingNotifications notify)
            => new ChatService(h.Platform.Db, h.Hub, notify, h.RealAccess(h.Platform.Db), h.Contexts);

        // ---- paging --------------------------------------------------------------------------
        [Fact]
        public async Task History_pages_backwards_without_gaps_or_duplicates()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            for (int i = 1; i <= 75; i++) h.SeedMessage(conv, i % 2 == 0 ? ChatHost.Bob : ChatHost.Alice, "m" + i);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service();

            // The screen asks for the newest page first, then walks backwards using the oldest id it holds.
            var page1 = await svc.GetMessagesAsync(ChatHost.Alice, conv, null, 30);
            var page2 = await svc.GetMessagesAsync(ChatHost.Alice, conv, page1.First().Id, 30);
            var page3 = await svc.GetMessagesAsync(ChatHost.Alice, conv, page2.First().Id, 30);
            var page4 = await svc.GetMessagesAsync(ChatHost.Alice, conv, page3.First().Id, 30);

            Assert.Equal(30, page1.Count);
            Assert.Equal(30, page2.Count);
            Assert.Equal(15, page3.Count);
            Assert.Empty(page4);                                    // walked off the start, cleanly

            var walked = page3.Concat(page2).Concat(page1).Select(m => m.Id).ToList();
            Assert.Equal(75, walked.Count);
            Assert.Equal(walked.Count, walked.Distinct().Count());   // no page overlaps another
            Assert.Equal(walked.OrderBy(x => x).ToList(), walked);    // and the walk is contiguous ascending
        }

        [Fact]
        public async Task Each_page_is_ordered_oldest_first_so_the_client_can_prepend()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            for (int i = 1; i <= 10; i++) h.SeedMessage(conv, ChatHost.Alice, "m" + i);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);

            var page = await h.Service().GetMessagesAsync(ChatHost.Alice, conv, null, 5);

            Assert.Equal(5, page.Count);
            Assert.Equal(page.Select(m => m.Id).OrderBy(x => x).ToList(), page.Select(m => m.Id).ToList());
            Assert.Equal(new[] { "m6", "m7", "m8", "m9", "m10" }, page.Select(m => m.Body).ToArray());
        }

        [Fact]
        public async Task An_absurd_page_size_is_clamped_rather_than_honoured()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            for (int i = 1; i <= 150; i++) h.SeedMessage(conv, ChatHost.Alice, "m" + i);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);

            var huge = await h.Service().GetMessagesAsync(ChatHost.Alice, conv, null, 10_000);
            var zero = await h.Service().GetMessagesAsync(ChatHost.Alice, conv, null, 0);

            Assert.Equal(30, huge.Count);    // clamped to the default, not unbounded
            Assert.Equal(30, zero.Count);
        }

        // ---- conversation list ---------------------------------------------------------------
        [Fact]
        public async Task The_conversation_list_is_ordered_by_most_recent_activity_and_carries_a_preview()
        {
            using var h = new ChatHost();
            var withBob = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var withCarol = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Carol);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service();

            await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, withBob, "to Bob first", null, null, null, null, null);
            await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, withCarol, "to Carol second", null, null, null, null, null);

            var list = await svc.MyConversationsAsync(ChatHost.CompanyA, ChatHost.Alice);

            Assert.Equal(withCarol, list.First().Id);                       // most recent first
            Assert.Equal("to Carol second", list.First().Preview);
            Assert.Equal("to Bob first", list.Single(x => x.Id == withBob).Preview);
            Assert.All(list, x => Assert.Equal("Direct", x.Kind));
            Assert.All(list, x => Assert.NotNull(x.LastAt));
        }

        [Fact]
        public async Task A_direct_conversation_is_titled_by_the_other_participant()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);

            var item = Assert.Single(await h.Service().MyConversationsAsync(ChatHost.CompanyA, ChatHost.Alice));
            Assert.Equal(ChatHost.Bob, item.OtherEmployeeId);
            Assert.False(string.IsNullOrWhiteSpace(item.Title));

            var header = await h.Service().GetHeaderAsync(ChatHost.CompanyA, ChatHost.Alice, conv);
            Assert.NotNull(header);
            Assert.Equal("Direct", header!.Kind);
            Assert.Equal(2, header.MemberCount);
            Assert.Contains(ChatHost.Bob, header.MemberIds);
        }

        // ---- directory ----------------------------------------------------------------------
        [Fact]
        public async Task The_directory_filters_by_the_search_term()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service();

            var all = await svc.DirectoryAsync(ChatHost.CompanyA, ChatHost.Alice, null);
            var hit = await svc.DirectoryAsync(ChatHost.CompanyA, ChatHost.Alice, "Bob");
            var miss = await svc.DirectoryAsync(ChatHost.CompanyA, ChatHost.Alice, "Nobody-By-That-Name");

            Assert.Equal(2, all.Count);                       // Bob and Carol, never Alice herself
            Assert.Equal(ChatHost.Bob, Assert.Single(hit).Id);
            Assert.Empty(miss);
        }

        // ---- reactions ----------------------------------------------------------------------
        [Fact]
        public async Task A_reaction_toggles_off_when_applied_twice_and_is_counted_per_emoji()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var msg = h.SeedMessage(conv, ChatHost.Bob, "react to me");
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service();

            await svc.ToggleReactionAsync(ChatHost.Alice, msg, "\U0001F44D");
            Assert.Equal(1, await h.Platform.Db.ChatReactions.CountAsync(r => r.MessageId == msg));

            await svc.ToggleReactionAsync(ChatHost.Alice, msg, "\U0001F44D");     // same emoji again -> removed
            Assert.Equal(0, await h.Platform.Db.ChatReactions.CountAsync(r => r.MessageId == msg));

            await svc.ToggleReactionAsync(ChatHost.Alice, msg, "\U0001F44D");
            await svc.ToggleReactionAsync(ChatHost.Alice, msg, "\U0001F525");     // a different emoji coexists
            var rendered = (await svc.GetMessagesAsync(ChatHost.Alice, conv, null)).Single(m => m.Id == msg);
            Assert.Equal(2, rendered.Reactions.Count);
            Assert.All(rendered.Reactions, r => Assert.True(r.Mine));
            Assert.All(rendered.Reactions, r => Assert.Equal(1, r.Count));
        }

        // ---- group lifecycle (already implemented; NOT extended here) -------------------------
        [Fact]
        public async Task A_group_is_created_with_its_creator_as_owner_and_the_named_members()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);

            var g = await h.Service().CreateGroupAsync(ChatHost.CompanyA, ChatHost.Alice, "Finance",
                new[] { ChatHost.Bob, ChatHost.Carol });

            Assert.True(g > 0);
            var conv = await h.Platform.Db.Conversations.AsNoTracking().SingleAsync(c => c.ID == g);
            Assert.Equal("Group", conv.Kind);
            Assert.Equal("Finance", conv.Title);
            Assert.Null(conv.DirectKeyLow);              // groups are exempt from the direct pair key
            Assert.Null(conv.DirectKeyHigh);

            var members = await h.Platform.Db.ConversationMembers.AsNoTracking()
                .Where(m => m.ConversationId == g).ToListAsync();
            Assert.Equal(3, members.Count);
            Assert.Equal("Owner", members.Single(m => m.EmployeeId == ChatHost.Alice).Role);
            Assert.All(members.Where(m => m.EmployeeId != ChatHost.Alice), m => Assert.Equal("Member", m.Role));
        }

        [Fact]
        public async Task A_group_never_admits_another_companys_employee_at_creation_or_afterwards()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var svc = h.Service();

            var g = await svc.CreateGroupAsync(ChatHost.CompanyA, ChatHost.Alice, "Finance",
                new[] { ChatHost.Bob, ChatHost.Mallory });          // Mallory is company B

            var members = await h.Platform.Db.ConversationMembers.AsNoTracking()
                .Where(m => m.ConversationId == g).Select(m => m.EmployeeId).ToListAsync();
            Assert.DoesNotContain(ChatHost.Mallory, members);

            Assert.False(await svc.AddMemberAsync(ChatHost.CompanyA, ChatHost.Alice, g, ChatHost.Mallory));
            Assert.Equal(2, await h.Platform.Db.ConversationMembers.CountAsync(m => m.ConversationId == g));
        }

        [Fact]
        public async Task Any_member_may_add_but_only_the_owner_may_remove_someone_else()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var g = await h.Service().CreateGroupAsync(ChatHost.CompanyA, ChatHost.Alice, "Finance", new[] { ChatHost.Bob });

            // Bob is a plain member: he may add Carol...
            h.ActAs(ChatHost.Bob, ChatHost.CompanyA);
            Assert.True(await h.Service().AddMemberAsync(ChatHost.CompanyA, ChatHost.Bob, g, ChatHost.Carol));
            Assert.Equal(3, await h.Platform.Db.ConversationMembers.CountAsync(m => m.ConversationId == g));

            // ...but he may not remove her.
            Assert.False(await h.Service().RemoveMemberAsync(ChatHost.CompanyA, ChatHost.Bob, g, ChatHost.Carol));
            Assert.Equal(3, await h.Platform.Db.ConversationMembers.CountAsync(m => m.ConversationId == g));

            // He may always remove himself — that is "leave group".
            Assert.True(await h.Service().RemoveMemberAsync(ChatHost.CompanyA, ChatHost.Bob, g, ChatHost.Bob));
            Assert.Equal(2, await h.Platform.Db.ConversationMembers.CountAsync(m => m.ConversationId == g));

            // The owner may remove anyone.
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            Assert.True(await h.Service().RemoveMemberAsync(ChatHost.CompanyA, ChatHost.Alice, g, ChatHost.Carol));
            Assert.Equal(1, await h.Platform.Db.ConversationMembers.CountAsync(m => m.ConversationId == g));
        }

        [Fact]
        public async Task Adding_an_existing_member_is_idempotent()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var g = await h.Service().CreateGroupAsync(ChatHost.CompanyA, ChatHost.Alice, "Finance", new[] { ChatHost.Bob });

            Assert.True(await h.Service().AddMemberAsync(ChatHost.CompanyA, ChatHost.Alice, g, ChatHost.Bob));
            Assert.Equal(2, await h.Platform.Db.ConversationMembers.CountAsync(m => m.ConversationId == g));
        }

        [Fact]
        public async Task A_group_message_is_readable_by_every_member_and_unread_for_each_of_them()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var g = await h.Service().CreateGroupAsync(ChatHost.CompanyA, ChatHost.Alice, "Finance",
                new[] { ChatHost.Bob, ChatHost.Carol });
            await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, g, "morning all", null, null, null, null, null);

            foreach (var who in new[] { ChatHost.Bob, ChatHost.Carol })
            {
                h.ActAs(who, ChatHost.CompanyA);
                var msgs = await h.Service().GetMessagesAsync(who, g, null);
                Assert.Equal("morning all", Assert.Single(msgs).Body);
                Assert.Equal(1, Assert.Single(await h.Service().MyConversationsAsync(ChatHost.CompanyA, who)).Unread);
            }

            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);   // the sender is not unread to herself
            Assert.Equal(0, Assert.Single(await h.Service().MyConversationsAsync(ChatHost.CompanyA, ChatHost.Alice)).Unread);
        }

        // ---- notifications -------------------------------------------------------------------
        [Fact]
        public async Task A_message_notifies_every_other_member_and_never_the_sender()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var g = await h.Service().CreateGroupAsync(ChatHost.CompanyA, ChatHost.Alice, "Finance",
                new[] { ChatHost.Bob, ChatHost.Carol });

            var notify = new RecordingNotifications();
            await ServiceWith(h, notify).SendAsync(ChatHost.CompanyA, ChatHost.Alice, g, "hello", null, null, null, null, null);

            Assert.Equal(2, notify.Sent.Count);
            Assert.Contains(notify.Sent, s => s.Recipient == ChatHost.Bob);
            Assert.Contains(notify.Sent, s => s.Recipient == ChatHost.Carol);
            Assert.DoesNotContain(notify.Sent, s => s.Recipient == ChatHost.Alice);
        }

        [Fact]
        public async Task Repeated_messages_in_one_conversation_carry_a_STABLE_dedup_key()
        {
            // This is the contract ChatService owes the notification layer: one key per conversation, so
            // NotificationService can collapse an unread burst into a single bell entry. If the key varied
            // per message, twenty messages would produce twenty notifications.
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var notify = new RecordingNotifications();
            var svc = ServiceWith(h, notify);

            await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "one", null, null, null, null, null);
            await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "two", null, null, null, null, null);
            await svc.SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "three", null, null, null, null, null);

            Assert.Equal(3, notify.Sent.Count);
            Assert.Single(notify.Sent.Select(s => s.DedupKey).Distinct());
            Assert.Equal("chat-" + conv, notify.Sent[0].DedupKey);
            Assert.All(notify.Sent, s => Assert.Equal(NotificationTypes.ChatMessage, s.Type));
            Assert.All(notify.Sent, s => Assert.Equal("/Chat?c=" + conv, s.Url));
            Assert.All(notify.Sent, s => Assert.Equal(conv, s.RefId));
        }

        [Fact]
        public async Task A_mention_uses_a_DIFFERENT_dedup_key_so_it_is_not_collapsed_into_an_ordinary_message()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var notify = new RecordingNotifications();

            await ServiceWith(h, notify).SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "look at this",
                null, null, null, null, new[] { ChatHost.Bob });

            var sent = Assert.Single(notify.Sent);
            Assert.Equal(ChatHost.Bob, sent.Recipient);
            Assert.Equal(NotificationTypes.ChatMention, sent.Type);
            Assert.Equal("chat-mention-" + conv, sent.DedupKey);
        }

        [Fact]
        public async Task Being_added_to_a_group_notifies_the_new_member()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var g = await h.Service().CreateGroupAsync(ChatHost.CompanyA, ChatHost.Alice, "Finance", new[] { ChatHost.Bob });

            var notify = new RecordingNotifications();
            await ServiceWith(h, notify).AddMemberAsync(ChatHost.CompanyA, ChatHost.Alice, g, ChatHost.Carol);

            var sent = Assert.Single(notify.Sent);
            Assert.Equal(ChatHost.Carol, sent.Recipient);
            Assert.Equal(NotificationTypes.ChatAdded, sent.Type);
        }

        // ---- real-time fan-out ---------------------------------------------------------------
        [Fact]
        public async Task Sending_reaches_the_conversation_room_and_each_other_members_own_channel()
        {
            using var h = new ChatHost();
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var g = await h.Service().CreateGroupAsync(ChatHost.CompanyA, ChatHost.Alice, "Finance",
                new[] { ChatHost.Bob, ChatHost.Carol });
            h.Hub.Recorder.Sent.Clear();

            await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, g, "hello", null, null, null, null, null);

            Assert.Contains(h.Hub.Recorder.Sent, s => s.Target == ChatHub.ConvGroup(g) && s.Method == "message");
            Assert.Contains(h.Hub.Recorder.Sent, s => s.Target == ChatHub.UserGroup(ChatHost.Bob) && s.Method == "conversation");
            Assert.Contains(h.Hub.Recorder.Sent, s => s.Target == ChatHub.UserGroup(ChatHost.Carol) && s.Method == "conversation");
            // The sender's own channel is not bumped — her client already rendered the message.
            Assert.DoesNotContain(h.Hub.Recorder.Sent, s => s.Target == ChatHub.UserGroup(ChatHost.Alice));
        }

        [Fact]
        public async Task Marking_read_tells_the_room_how_far_the_reader_has_got()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "seen?", null, null, null, null, null);

            h.ActAs(ChatHost.Bob, ChatHost.CompanyA);
            h.Hub.Recorder.Sent.Clear();
            await h.Service().MarkReadAsync(ChatHost.Bob, conv);

            Assert.Contains(h.Hub.Recorder.Sent, s => s.Target == ChatHub.ConvGroup(conv) && s.Method == "read");
        }

        [Fact]
        public async Task Editing_and_deleting_announce_themselves_to_the_room()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var id = (await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "typo", null, null, null, null, null))!.Id;

            h.Hub.Recorder.Sent.Clear();
            await h.Service().EditAsync(ChatHost.Alice, id, "fixed");
            Assert.Contains(h.Hub.Recorder.Sent, s => s.Target == ChatHub.ConvGroup(conv) && s.Method == "edited");

            h.Hub.Recorder.Sent.Clear();
            await h.Service().DeleteAsync(ChatHost.Alice, id);
            Assert.Contains(h.Hub.Recorder.Sent, s => s.Target == ChatHub.ConvGroup(conv) && s.Method == "deleted");
        }

        [Fact]
        public async Task A_deleted_message_keeps_its_row_but_surrenders_its_body()
        {
            // Soft delete, so a conversation keeps its shape and a reply that quoted the message still
            // resolves. What must not survive is the content.
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            h.ActAs(ChatHost.Alice, ChatHost.CompanyA);
            var id = (await h.Service().SendAsync(ChatHost.CompanyA, ChatHost.Alice, conv, "regrettable", null, null, null, null, null))!.Id;

            await h.Service().DeleteAsync(ChatHost.Alice, id);

            var rendered = Assert.Single(await h.Service().GetMessagesAsync(ChatHost.Alice, conv, null));
            Assert.True(rendered.Deleted);
            Assert.Null(rendered.Body);
            Assert.Equal(1, await h.Platform.Db.ChatMessages.CountAsync());          // the row is still there
            Assert.Null((await h.Platform.Db.ChatMessages.AsNoTracking().SingleAsync()).Body);
        }

        // ---- the hub itself ------------------------------------------------------------------
        [Fact]
        public async Task A_member_may_join_the_conversation_room()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var groups = new RecordingGroupManager();
            var hub = HubFor(h, ChatHost.Alice, groups);

            await hub.JoinConversation(conv);

            Assert.Equal(ChatHub.ConvGroup(conv), Assert.Single(groups.Added));
        }

        [Fact]
        public async Task A_non_member_and_another_company_are_both_refused_the_room_silently()
        {
            // The gate the audit found untested. Refusal is silent: joining a group you cannot access must
            // not confirm that the conversation exists.
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);

            var carol = new RecordingGroupManager();
            await HubFor(h, ChatHost.Carol, carol).JoinConversation(conv);          // same company, not a member
            Assert.Empty(carol.Added);

            var mallory = new RecordingGroupManager();
            await HubFor(h, ChatHost.Mallory, mallory).JoinConversation(conv);      // another company
            Assert.Empty(mallory.Added);

            var absent = new RecordingGroupManager();
            await HubFor(h, ChatHost.Alice, absent).JoinConversation(999999);       // does not exist
            Assert.Empty(absent.Added);

            var nonsense = new RecordingGroupManager();
            await HubFor(h, ChatHost.Alice, nonsense).JoinConversation(0);
            Assert.Empty(nonsense.Added);
        }

        [Fact]
        public async Task Leaving_a_room_is_always_allowed_so_a_client_can_tidy_up()
        {
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);
            var groups = new RecordingGroupManager();

            await HubFor(h, ChatHost.Carol, groups).LeaveConversation(conv);        // not even a member

            Assert.Equal(ChatHub.ConvGroup(conv), Assert.Single(groups.Removed));
        }

        [Fact]
        public async Task Rejoining_after_a_reconnect_still_works_for_a_member()
        {
            // A dropped socket reconnects and replays JoinConversation. The gate must not be stateful in a
            // way that refuses the second attempt.
            using var h = new ChatHost();
            var conv = h.SeedDirect(ChatHost.CompanyA, ChatHost.Alice, ChatHost.Bob);

            var first = new RecordingGroupManager();
            await HubFor(h, ChatHost.Alice, first).JoinConversation(conv);
            var second = new RecordingGroupManager();
            await HubFor(h, ChatHost.Alice, second).JoinConversation(conv);

            Assert.Single(first.Added);
            Assert.Single(second.Added);
        }

        private static ChatHub HubFor(ChatHost h, int employeeId, RecordingGroupManager groups)
        {
            // IEmployeeService is passed null deliberately: JoinConversation reads the employee id from
            // Context.Items, which OnConnectedAsync has already populated on a live connection, so the
            // directory lookup is never reached. Passing null proves that — if the gate ever started
            // resolving the employee per call, this test would throw instead of quietly getting slower.
            var hub = new ChatHub(null!, h.RealAccess(h.Platform.Db), h.Platform.Db)
            {
                Context = new FakeHubCallerContext(new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "u" + employeeId) }, "test"))),
                Groups = groups,
            };
            // The connection has already authenticated, so the employee id is cached exactly as
            // OnConnectedAsync leaves it. Company still comes from the Employee row, never from the client.
            hub.Context.Items["empId"] = employeeId;
            return hub;
        }
    }
}
