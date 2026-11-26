using System;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // THE CHAT WRITE PATH — AddMemberAsync and RemoveMemberAsync, and the gate they share.
    //
    // Both are committed at HEAD. Both open with the same two-part guard, collapsed into one line:
    //
    //     if (ctx == null || !await MayAsync(ctx, CommunicationActions.Send, conversationId)) return false;
    //
    // and MayAsync asks CanAsync with PermissionTarget.ForConversation(conversationId, ctx.CompanyId).
    // That TARGET is the tenant boundary for group membership: the company inside it decides whether
    // a conversation id from one company can be reached while signed into another. A test that only
    // checked the action would not notice the target being built from the caller's argument instead
    // of the resolved scope, which is precisely the pre-F5 shape.
    //
    // As everywhere in these suites, the database is NULL on purpose: a refusal that returns false
    // proves the guard short-circuited, because reaching the store would throw instead.
    // =============================================================================================
    public class ChatWriteGuardTests
    {
        private const int WrongCompany = 999;
        private const int WrongEmployee = 4242;
        private const int RealCompany = 1;
        private const int RealEmployee = 7;
        private const int ConversationId = 55;
        private const int OtherMember = 88;

        private static BusinessContext Reader() =>
            new() { CompanyId = RealCompany, EmployeeId = RealEmployee, UserId = "u" };

        private static ChatService Chat(BusinessContext? ctx, ICommunicationAccessService access) =>
            new(null!, null!, null!, access, new FixedScopeContexts(ctx));

        public static TheoryData<string> Entries() => new() { "AddMemberAsync", "RemoveMemberAsync" };

        private static async Task<bool> CallAsync(ChatService svc, string entry) => entry switch
        {
            "AddMemberAsync" => await svc.AddMemberAsync(WrongCompany, WrongEmployee, ConversationId, OtherMember),
            "RemoveMemberAsync" => await svc.RemoveMemberAsync(WrongCompany, WrongEmployee, ConversationId, OtherMember),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "unknown entry point"),
        };

        // -----------------------------------------------------------------------------------------
        // AN UNRESOLVED SCOPE REFUSES, AND SHORT-CIRCUITS BEFORE THE POLICY.
        //
        // The guard is one line with `||`, so the order is a property of C# evaluation rather than of
        // two visible statements. The empty Asked list is what makes it an assertion: if the two sides
        // were ever swapped, a caller with no scope would reach MayAsync and dereference a null ctx
        // building the target.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(Entries))]
        public async Task An_unresolved_scope_refuses_without_asking_anything(string entry)
        {
            var access = new RecordingAccess(allow: true);

            Assert.False(await CallAsync(Chat(null, access), entry));
            Assert.Empty(access.Asked);
        }

        // -----------------------------------------------------------------------------------------
        // THE WRITE PATH ASKS FOR SEND, NOT READ.
        //
        // Changing who is in a group is a write, and it is gated on the same action as posting. This
        // is asserted explicitly because the list entry points next door ask for Read against a null
        // target, and a copy-paste between the two would be invisible without it.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(Entries))]
        public async Task A_denied_caller_refuses_after_exactly_one_send_question(string entry)
        {
            var access = new RecordingAccess(allow: false);

            Assert.False(await CallAsync(Chat(Reader(), access), entry));
            Assert.Equal(new[] { CommunicationActions.Send }, access.Asked);
        }

        [Theory]
        [MemberData(nameof(Entries))]
        public async Task A_policy_that_throws_is_a_refusal_not_an_exemption(string entry)
        {
            var access = new RecordingAccess(allow: true, throws: true);

            Assert.False(await CallAsync(Chat(Reader(), access), entry));
            Assert.Equal(new[] { CommunicationActions.Send }, access.Asked);
        }

        // -----------------------------------------------------------------------------------------
        // THE TENANT BOUNDARY. The target carries the RESOLVED company, never the caller's.
        //
        // The caller passes company 999; the scope resolves to company 1. The conversation id is the
        // one thing that legitimately comes from the request, so it must be carried through as given,
        // while the company must not be. If the target were built as ForConversation(conversationId,
        // companyId) — the obvious-looking edit, since companyId is right there as a parameter — a
        // caller could present another company's id alongside a conversation id from it.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(Entries))]
        public async Task The_conversation_target_carries_the_resolved_company_not_the_argument(string entry)
        {
            var access = new RecordingAccess(allow: false);

            await CallAsync(Chat(Reader(), access), entry);

            var target = Assert.Single(access.AskedAbout);
            Assert.NotNull(target);
            Assert.Equal(ConversationId, target!.ConversationId);
            Assert.Equal(RealCompany, target.CompanyId);
            Assert.NotEqual(WrongCompany, target.CompanyId);

            // The context carries it too, so neither half of the decision sees 999.
            var asked = Assert.Single(access.AskedWith);
            Assert.Equal(RealCompany, asked.CompanyId);
            Assert.Equal(RealEmployee, asked.EmployeeId);
        }

        // -----------------------------------------------------------------------------------------
        // THE DISCRIMINATOR. A permitted caller gets past the gate and reaches the store.
        //
        // Without this, every test above still passes against a method rewritten to `return false;`.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(Entries))]
        public async Task A_permitted_caller_reaches_the_store(string entry)
        {
            var access = new RecordingAccess(allow: true);

            await Assert.ThrowsAsync<NullReferenceException>(() => CallAsync(Chat(Reader(), access), entry));

            Assert.Equal(new[] { CommunicationActions.Send }, access.Asked);
        }

        // -----------------------------------------------------------------------------------------
        // A FINDING, PINNED. The ACCESS LAYER IS NOT WHAT ENFORCES OWNER-ONLY REMOVAL.
        //
        // RemoveMemberAsync is gated on Send — the same action as posting a message — so everyone who
        // can post in a group passes the authorization check on the way to removing somebody. What
        // actually stops them is a later query: `meOwner || memberId == meId`, read straight from
        // ConversationMembers.Role, well after the policy has said yes.
        //
        // That is a defensible design (the owner rule is a record rule, not a permission), but it
        // means an audit that reads only the access layer would conclude that any poster may remove
        // any member. The assertion below is deliberately narrow: it pins that the policy question is
        // identical for adding and for removing, so nobody later mistakes the gate for the rule.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task Adding_and_removing_ask_the_access_layer_exactly_the_same_question()
        {
            var add = new RecordingAccess(allow: false);
            var remove = new RecordingAccess(allow: false);

            await CallAsync(Chat(Reader(), add), "AddMemberAsync");
            await CallAsync(Chat(Reader(), remove), "RemoveMemberAsync");

            Assert.Equal(add.Asked, remove.Asked);
            Assert.Equal(add.AskedAbout[0]!.ConversationId, remove.AskedAbout[0]!.ConversationId);
            Assert.Equal(add.AskedAbout[0]!.CompanyId, remove.AskedAbout[0]!.CompanyId);
        }
    }
}
