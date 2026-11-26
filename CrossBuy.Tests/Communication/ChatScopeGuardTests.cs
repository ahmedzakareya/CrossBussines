using System;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // THE TWO UNAUTHENTICATED-REACHABLE CHAT LIST ENTRY POINTS — MyConversationsAsync and
    // DirectoryAsync — and the guard sequence they share.
    //
    // Both are committed at HEAD. Both still take companyId and meId as ARGUMENTS, left over from
    // when the controller derived them from the caller's own Employee row, and both now overwrite
    // those arguments from the resolved scope before any query runs. That overwrite is the whole F5
    // property: the numbers a caller passes are used for nothing that decides access.
    //
    // WHY A DATABASE CANNOT PROVE THIS. A database-backed test passes a correct companyId, so it
    // cannot distinguish "authorized on the resolved scope" from "authorized on the argument" — the
    // two agree. Here the caller deliberately passes the WRONG company and employee, and the access
    // double records which context it was actually asked about. That disagreement is the assertion.
    //
    // The doubles (FixedScopeContexts, RecordingAccess) are declared in ChatSearchGuardTests.cs and
    // shared, so all three suites exercise the same seams rather than drifting apart.
    // =============================================================================================
    public class ChatScopeGuardTests
    {
        // What a caller passes. Neither value may reach an access decision or a query.
        private const int WrongCompany = 999;
        private const int WrongEmployee = 4242;

        // What the scope actually resolves to.
        private const int RealCompany = 1;
        private const int RealEmployee = 7;

        private static BusinessContext Reader() =>
            new() { CompanyId = RealCompany, EmployeeId = RealEmployee, UserId = "u" };

        // The null database IS the assertion: a refusal that returns an empty list proves the guard
        // short-circuited, because reaching the store would throw instead.
        private static ChatService Chat(BusinessContext? ctx, ICommunicationAccessService access) =>
            new(null!, null!, null!, access, new FixedScopeContexts(ctx));

        public static TheoryData<string> Entries() => new() { "MyConversationsAsync", "DirectoryAsync" };

        private static async Task<int> CallAsync(ChatService svc, string entry) => entry switch
        {
            "MyConversationsAsync" => (await svc.MyConversationsAsync(WrongCompany, WrongEmployee)).Count,
            "DirectoryAsync" => (await svc.DirectoryAsync(WrongCompany, WrongEmployee, "an")).Count,
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "unknown entry point"),
        };

        private static Task InvokeAsync(ChatService svc, string entry) => CallAsync(svc, entry);

        // -----------------------------------------------------------------------------------------
        // AN UNRESOLVED SCOPE REFUSES, AND REFUSES FIRST.
        //
        // The empty Asked list pins the ORDER as well as the outcome: a caller with no resolvable
        // company never reaches the policy, so an unscoped request cannot fall through to a read with
        // no company filter.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(Entries))]
        public async Task An_unresolved_scope_refuses_without_asking_anything(string entry)
        {
            var access = new RecordingAccess(allow: true);

            Assert.Equal(0, await CallAsync(Chat(null, access), entry));
            Assert.Empty(access.Asked);
        }

        [Theory]
        [MemberData(nameof(Entries))]
        public async Task A_denied_read_refuses_after_exactly_one_question(string entry)
        {
            var access = new RecordingAccess(allow: false);

            Assert.Equal(0, await CallAsync(Chat(Reader(), access), entry));
            Assert.Equal(new[] { CommunicationActions.Read }, access.Asked);
        }

        // A policy that cannot answer is not a licence to proceed. Both entry points wrap the call in
        // a catch that sets the answer to false, so an unavailable policy store becomes a refusal
        // rather than an unfiltered list.
        [Theory]
        [MemberData(nameof(Entries))]
        public async Task A_policy_that_throws_is_a_refusal_not_an_exemption(string entry)
        {
            var access = new RecordingAccess(allow: true, throws: true);

            Assert.Equal(0, await CallAsync(Chat(Reader(), access), entry));
            Assert.Equal(new[] { CommunicationActions.Read }, access.Asked);
        }

        // -----------------------------------------------------------------------------------------
        // THE F5 ASSERTION. The access decision is made on the RESOLVED scope, never on the caller's
        // arguments.
        //
        // The caller passes company 999 and employee 4242; the scope resolves to company 1 and
        // employee 7. If either entry point ever authorized against what it was handed — which is
        // exactly what the pre-F5 code did — the recorded context would carry 999 and a caller could
        // enumerate another company by supplying its id.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(Entries))]
        public async Task The_access_question_is_asked_about_the_resolved_scope_not_the_arguments(string entry)
        {
            var access = new RecordingAccess(allow: false);

            await CallAsync(Chat(Reader(), access), entry);

            var asked = Assert.Single(access.AskedWith);
            Assert.Equal(RealCompany, asked.CompanyId);
            Assert.Equal(RealEmployee, asked.EmployeeId);
            Assert.NotEqual(WrongCompany, asked.CompanyId);
            Assert.NotEqual(WrongEmployee, asked.EmployeeId);
        }

        // -----------------------------------------------------------------------------------------
        // THE DISCRIMINATOR. The guards are a filter, not a blanket refusal.
        //
        // Without this, every test above still passes against an entry point rewritten to `return
        // new();`. A permitted caller with a resolved scope must get past both guards and reach the
        // store, where the null database stops it.
        // -----------------------------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(Entries))]
        public async Task A_permitted_caller_reaches_the_store(string entry)
        {
            var access = new RecordingAccess(allow: true);

            await Assert.ThrowsAsync<NullReferenceException>(() => InvokeAsync(Chat(Reader(), access), entry));

            Assert.Equal(new[] { CommunicationActions.Read }, access.Asked);
        }
    }
}
