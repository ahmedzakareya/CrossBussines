using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // CHAT MESSAGE SEARCH — the refusals that happen BEFORE the store is read.
    //
    // InternalChatSecurityTests already covers what search RETURNS, over a real database. This file
    // covers the half a database cannot prove: the order in which SearchMessagesAsync gives up.
    // Every test here runs with a NULL CrossDbContext on purpose. That is the whole technique — if a
    // guard were missing or reordered, the call would reach _db.ConversationMembers and throw, so
    // "it returned an empty list" is positive evidence that the short-circuit fired, not merely that
    // the query found nothing. A database-backed test cannot distinguish those two.
    //
    // THE RISK THIS EXISTS FOR is a widened guard. Every refusal path returns the same empty list, so
    // a change that refuses MORE than it should is invisible in the response: the screen shows "no
    // results" either way. The second test below is the discriminator — it asserts that a two-
    // character term actually reaches the store, which is what fails if the length rule is ever
    // loosened into a blanket refusal.
    // =============================================================================================

    // ---- doubles, shared by every ChatService scope-guard suite -----------------------------------
    // Neither double decides anything the tests are asking about; they exist so the service can be
    // driven to a chosen point and stopped there. ChatScopeGuardTests reuses both.

    // GetCurrentAsync THROWS deliberately. These entry points must use the non-throwing variant,
    // because a chat screen is reachable before a scope resolves; if that ever changed, the tests
    // fail loudly rather than silently exercising a different path.
    internal sealed class FixedScopeContexts : IBusinessContextAccessor
    {
        private readonly BusinessContext? _ctx;
        public FixedScopeContexts(BusinessContext? ctx) => _ctx = ctx;

        public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("search must resolve its scope with TryGetCurrentAsync");

        public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_ctx);
    }

    // Records the QUESTION as well as answering it, so a test can assert that no authorization call
    // was made at all — which is how "refused earlier" is told apart from "refused by the policy".
    //
    // AskedWith records the CONTEXT the question was asked about. That is the F5 property: these
    // entry points still take companyId/meId arguments, but the access decision must be made on the
    // RESOLVED scope and never on what the caller passed. Without recording the context, a service
    // that authorized against a caller-supplied company would pass every test in both suites.
    //
    // AskedAbout records the TARGET. The list entry points pass null (they authorize the module, then
    // filter), but the write path passes PermissionTarget.ForConversation(conversationId, company) —
    // and the company inside that target is the tenant boundary for group membership changes. A test
    // that only checked the action would not notice it being built from the caller's argument.
    internal sealed class RecordingAccess : ICommunicationAccessService
    {
        private readonly bool _allow;
        private readonly bool _throws;
        public readonly List<string> Asked = new();
        public readonly List<BusinessContext> AskedWith = new();
        public readonly List<PermissionTarget?> AskedAbout = new();

        public RecordingAccess(bool allow, bool throws = false) { _allow = allow; _throws = throws; }

        public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
            CancellationToken cancellationToken = default)
        {
            Asked.Add(action);
            AskedWith.Add(context);
            AskedAbout.Add(target);
            if (_throws) throw new InvalidOperationException("policy store unavailable");
            return Task.FromResult(_allow);
        }

        // None of the three suites' entry points use these. Search filters over the membership rows of
        // the caller, and the write path asks CanAsync with a conversation TARGET rather than asking
        // membership separately. They throw so a future rewrite that starts calling one is caught here
        // instead of quietly adding a query per hit.
        public Task<bool> IsConversationParticipantAsync(BusinessContext context, int conversationId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("search must not ask per-conversation membership");

        public Task<AccessScope> ResolveConversationScopeAsync(BusinessContext context, string action,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("search must not resolve a conversation scope");
    }

    public class ChatSearchGuardTests
    {
        private static BusinessContext Reader() =>
            new() { CompanyId = 1, EmployeeId = 7, UserId = "u" };

        // The null database IS the assertion. See the header.
        private static ChatService Search(BusinessContext? ctx, ICommunicationAccessService access) =>
            new(null!, null!, null!, access, new FixedScopeContexts(ctx));

        // -----------------------------------------------------------------------------------------
        // A TERM UNDER TWO CHARACTERS NEVER BECOMES A QUERY.
        //
        // A one-character search matches a large share of every message the caller can reach, so it is
        // refused as useless rather than served expensively. The assertion that matters is not the
        // empty list — it is that the access service was never even consulted, which places this guard
        // before the authorization call rather than after it.
        // -----------------------------------------------------------------------------------------
        public static TheoryData<string, string?> TooShort() => new()
        {
            { "a null term",              null },
            { "an empty term",            "" },
            { "whitespace only",          "   " },
            { "a single character",       "a" },
            { "one character after trim", "  x  " },
        };

        [Theory]
        [MemberData(nameof(TooShort))]
        public async Task A_term_under_two_characters_is_refused_before_anything_is_asked(
            string because, string? term)
        {
            var access = new RecordingAccess(allow: true);

            var hits = await Search(Reader(), access).SearchMessagesAsync(term);

            Assert.NotNull(hits);
            Assert.Empty(hits);
            Assert.Empty(access.Asked);
            Assert.False(string.IsNullOrEmpty(because));
        }

        // -----------------------------------------------------------------------------------------
        // THE DISCRIMINATOR. The length rule is a MINIMUM, not a refusal.
        //
        // Without this test every other test in the file still passes if SearchMessagesAsync is
        // replaced with "return new();". Two characters must get past the guard, ask exactly one
        // authorization question, and then reach the store — where the null database stops it.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task Two_characters_passes_the_length_rule_and_reaches_the_store()
        {
            var access = new RecordingAccess(allow: true);

            await Assert.ThrowsAsync<NullReferenceException>(
                () => Search(Reader(), access).SearchMessagesAsync("ab"));

            Assert.Equal(new[] { CommunicationActions.Read }, access.Asked);
        }

        // -----------------------------------------------------------------------------------------
        // AN UNRESOLVED SCOPE IS A REFUSAL, AND IT IS THE FIRST ONE AFTER THE LENGTH RULE.
        //
        // A null context means refuse — it must never fall through to an unscoped read. The empty
        // Asked list also pins the ORDER: the scope is resolved before the permission is asked, so a
        // caller with no company never reaches the policy at all.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task An_unresolved_scope_refuses_a_valid_term_without_a_query()
        {
            var access = new RecordingAccess(allow: true);

            var hits = await Search(null, access).SearchMessagesAsync("invoice");

            Assert.Empty(hits);
            Assert.Empty(access.Asked);
        }

        [Fact]
        public async Task A_denied_read_refuses_a_valid_term_without_a_query()
        {
            var access = new RecordingAccess(allow: false);

            var hits = await Search(Reader(), access).SearchMessagesAsync("invoice");

            Assert.Empty(hits);
            Assert.Equal(new[] { CommunicationActions.Read }, access.Asked);
        }

        // A policy that cannot answer is not a licence to proceed. This is the fail-closed half: the
        // service catches the throw and refuses, rather than letting an unavailable policy store turn
        // into an unfiltered search.
        [Fact]
        public async Task A_policy_that_throws_is_a_refusal_not_an_exemption()
        {
            var access = new RecordingAccess(allow: true, throws: true);

            var hits = await Search(Reader(), access).SearchMessagesAsync("invoice");

            Assert.Empty(hits);
            Assert.Equal(new[] { CommunicationActions.Read }, access.Asked);
        }

        // -----------------------------------------------------------------------------------------
        // A FINDING, PINNED. Three different failures look identical to whoever is searching.
        //
        // "Term too short", "no scope resolved" and "read denied" all return the same empty list. At
        // the boundary that is the right call — a denial that rendered differently would confirm that
        // matches exist in a conversation the caller cannot open. But it means an operator told "chat
        // search returns nothing" cannot tell a permission problem from an empty index by looking at
        // the response, and has to read the authorization trail to find out which it was.
        //
        // Recorded as an equality rather than three separate empties so that it FAILS if the shapes
        // are ever made distinguishable — at which point the boundary question above has to be
        // answered deliberately instead of by accident.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public async Task Too_short_unscoped_and_denied_are_indistinguishable_to_the_caller()
        {
            var tooShort = await Search(Reader(), new RecordingAccess(allow: true)).SearchMessagesAsync("a");
            var unscoped = await Search(null, new RecordingAccess(allow: true)).SearchMessagesAsync("invoice");
            var denied = await Search(Reader(), new RecordingAccess(allow: false)).SearchMessagesAsync("invoice");

            Assert.Equal(tooShort.Count, unscoped.Count);
            Assert.Equal(unscoped.Count, denied.Count);
            Assert.Equal(0, denied.Count);
        }
    }
}
