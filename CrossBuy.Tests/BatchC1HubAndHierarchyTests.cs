using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Hubs;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Chat;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 1 Batch C.1 items 2 and 3.
    //
    // ITEM 2 — the SignalR hole. `ChatHub.JoinConversation(int conversationId)` called Groups.AddToGroupAsync with
    // no check at all. [Authorize] proved the caller was AN employee; it said nothing about THIS conversation. So
    // any signed-in employee — or any holder of a mobile JWT — could join any conversation id, including another
    // company's, and receive its messages live. `ChatService` guarded the REST history fetch, which is exactly why
    // the hole was invisible: the controller looked protected while the realtime stream had no gate.
    //
    // These tests drive the REAL hub with a fake HubCallerContext and a recording IGroupManager, so what is
    // asserted is the thing that actually leaked: whether the connection ends up in the conversation's group.
    //
    // ITEM 3 — the two legacy hierarchy walks that never intersected with a company.
    public class BatchC1HubAndHierarchyTests
    {
        private const int CompanyOne = 1;
        private const int CompanyTwo = 2;

        private static Employee Emp(int id, int companyId, bool active = true) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = active, Address = "-", PhoneNumber = "-",
            Email = $"e{id}@example.com", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
            UserId = "user-" + id,
        };

        // ============================================================================================
        // hub test doubles
        // ============================================================================================

        private sealed class RecordingGroups : IGroupManager
        {
            public readonly List<string> Added = new();
            public readonly List<string> Removed = new();

            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            { Added.Add(groupName); return Task.CompletedTask; }

            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            { Removed.Add(groupName); return Task.CompletedTask; }
        }

        private sealed class FakeCallerContext : HubCallerContext
        {
            private readonly Dictionary<object, object?> _items = new();
            public FakeCallerContext(int? employeeId)
            {
                if (employeeId is > 0) _items["empId"] = employeeId.Value;
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-" + employeeId) }, "test"));
            }

            public override string ConnectionId => "conn-1";
            public override string? UserIdentifier => null;
            public override ClaimsPrincipal? User { get; }
            public override IDictionary<object, object?> Items => _items;
            public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; }
                = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
            public override CancellationToken ConnectionAborted => CancellationToken.None;
            public override void Abort() { }
        }

        // The hub under test, wired to the REAL CommunicationAccessService and the REAL BusinessContextFactory
        // over the test host. IEmployeeService is never reached because the connection already cached its
        // employee id (as OnConnectedAsync does), which keeps the test on the authorization path.
        private static (ChatHub hub, RecordingGroups groups) Hub(PlatformTestHost host, int employeeId)
        {
            var access = new CommunicationAccessService(
                host.Db, new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
                NullLogger<CommunicationAccessService>.Instance);

            var contexts = new BusinessContextFactory(
                new HttpContextAccessor(), host.Db, host.Holder,
                NullLogger<BusinessContextFactory>.Instance);

            var groups = new RecordingGroups();
            var hub = new ChatHub(null!, access, contexts, NullLogger<ChatHub>.Instance)
            {
                Context = new FakeCallerContext(employeeId),
                Groups = groups,
            };
            return (hub, groups);
        }

        private static async Task<int> ConversationAsync(
            PlatformTestHost host, int companyId, string kind, int createdBy, params (int emp, string role)[] members)
        {
            var c = new Conversation
            {
                CompanyID = companyId, Kind = kind, CreatedByEmployeeId = createdBy, CreatedAt = DateTime.UtcNow,
            };
            host.Seed.Conversations.Add(c);
            await host.Seed.SaveChangesAsync();
            foreach (var (emp, role) in members)
                host.Seed.ConversationMembers.Add(new ConversationMember
                { ConversationId = c.ID, EmployeeId = emp, Role = role, JoinedAt = DateTime.UtcNow });
            await host.Seed.SaveChangesAsync();
            return c.ID;
        }

        // ============================================================================================
        // ITEM 2 — the five cases the brief names
        // ============================================================================================

        [Fact]
        public async Task A_participant_may_join_the_conversation_room()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();
            var conv = await ConversationAsync(host, CompanyOne, "Direct", 10, (10, "Member"), (11, "Member"));

            var (hub, groups) = Hub(host, 11);
            await hub.JoinConversation(conv);

            Assert.Contains(ChatHub.ConvGroup(conv), groups.Added);
        }

        [Fact]
        public async Task A_non_participant_is_denied_and_never_enters_the_group()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(12, CompanyOne));
            await host.Seed.SaveChangesAsync();
            var conv = await ConversationAsync(host, CompanyOne, "Direct", 10, (10, "Member"), (11, "Member"));

            // employee 12 belongs to the company but not to the conversation
            var (hub, groups) = Hub(host, 12);
            await hub.JoinConversation(conv);

            Assert.Empty(groups.Added);
        }

        [Fact]
        public async Task An_employee_of_another_company_is_denied_the_conversation_room()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(20, CompanyTwo));
            await host.Seed.SaveChangesAsync();
            var conv = await ConversationAsync(host, CompanyOne, "Group", 10, (10, "Owner"));

            // Worse than a non-member: a membership row is forged for the foreign employee. The conversation
            // ROW's company is what decides, so the row does not help.
            host.Seed.ConversationMembers.Add(new ConversationMember
            { ConversationId = conv, EmployeeId = 20, Role = "Member", JoinedAt = DateTime.UtcNow });
            await host.Seed.SaveChangesAsync();

            var (hub, groups) = Hub(host, 20);
            await hub.JoinConversation(conv);

            Assert.Empty(groups.Added);
        }

        [Fact]
        public async Task A_removed_member_can_no_longer_join()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();
            var conv = await ConversationAsync(host, CompanyOne, "Group", 10, (10, "Owner"), (11, "Member"));

            var (before, beforeGroups) = Hub(host, 11);
            await before.JoinConversation(conv);
            Assert.Contains(ChatHub.ConvGroup(conv), beforeGroups.Added);

            // the membership row is deleted, as removing someone from a group does
            var row = await host.Seed.ConversationMembers
                .FirstAsync(m => m.ConversationId == conv && m.EmployeeId == 11);
            host.Seed.ConversationMembers.Remove(row);
            await host.Seed.SaveChangesAsync();

            var (after, afterGroups) = Hub(host, 11);
            await after.JoinConversation(conv);
            Assert.Empty(afterGroups.Added);
        }

        [Fact]
        public async Task An_inactive_employee_is_denied_even_with_a_valid_membership_row()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne, active: false));
            await host.Seed.SaveChangesAsync();
            var conv = await ConversationAsync(host, CompanyOne, "Group", 10, (10, "Owner"), (11, "Member"));

            // A leaver whose cookie or JWT is still valid. Membership is intact; the employee is not.
            var (hub, groups) = Hub(host, 11);
            await hub.JoinConversation(conv);

            Assert.Empty(groups.Added);
        }

        [Fact]
        public async Task A_client_supplied_conversation_id_is_never_sufficient_on_its_own()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(11, CompanyOne));
            await host.Seed.SaveChangesAsync();

            var (hub, groups) = Hub(host, 11);

            // ids that do not exist, and ids that are not ids
            await hub.JoinConversation(4242);
            await hub.JoinConversation(0);
            await hub.JoinConversation(-1);
            await hub.JoinConversation(int.MaxValue);

            // An absent conversation and another company's conversation are refused identically, so an id
            // cannot be probed for existence through the hub.
            Assert.Empty(groups.Added);
        }

        [Fact]
        public async Task An_unresolvable_employee_is_denied_rather_than_defaulted()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.Add(Emp(10, CompanyOne));
            await host.Seed.SaveChangesAsync();
            var conv = await ConversationAsync(host, CompanyOne, "Group", 10, (10, "Owner"));

            // employee 77 has no Employee row at all — no company can be resolved, so nothing is granted
            var (hub, groups) = Hub(host, 77);
            await hub.JoinConversation(conv);

            Assert.Empty(groups.Added);
        }

        // The typing indicator is a WRITE into a room. Before C.1 it only checked that the caller had an
        // employee id cached, so a non-participant could inject "X is typing…" into any conversation.
        [Fact]
        public async Task Typing_is_refused_in_a_room_the_caller_was_never_authorized_to_join()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(12, CompanyOne));
            await host.Seed.SaveChangesAsync();
            var conv = await ConversationAsync(host, CompanyOne, "Direct", 10, (10, "Member"));

            var (hub, _) = Hub(host, 12);
            var clients = new RecordingClients();
            hub.Clients = clients;

            await hub.Typing(conv);         // never joined ⇒ nothing is broadcast
            Assert.Empty(clients.Sent);

            // and a genuine participant, having joined, still works
            var (member, _) = Hub(host, 10);
            var memberClients = new RecordingClients();
            member.Clients = memberClients;
            await member.JoinConversation(conv);
            await member.Typing(conv);
            Assert.Single(memberClients.Sent);
        }

        private sealed class RecordingClients : IHubCallerClients
        {
            public readonly List<string> Sent = new();

            private sealed class Proxy : IClientProxy
            {
                private readonly List<string> _sent;
                public Proxy(List<string> sent) { _sent = sent; }
                public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
                { _sent.Add(method); return Task.CompletedTask; }
            }

            public IClientProxy Caller => new Proxy(Sent);
            public IClientProxy Others => new Proxy(Sent);
            public IClientProxy All => new Proxy(Sent);
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new Proxy(Sent);
            public IClientProxy Client(string connectionId) => new Proxy(Sent);
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new Proxy(Sent);
            public IClientProxy Group(string groupName) => new Proxy(Sent);
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new Proxy(Sent);
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new Proxy(Sent);
            public IClientProxy OthersInGroup(string groupName) => new Proxy(Sent);
            public IClientProxy User(string userId) => new Proxy(Sent);
            public IClientProxy Users(IReadOnlyList<string> userIds) => new Proxy(Sent);
        }

        // ============================================================================================
        // ITEM 3 — the legacy hierarchy walks, now company-intersected
        // ============================================================================================

        private static CrmAccessService Crm(PlatformTestHost host)
        {
            var http = new HttpContextAccessor();
            var accessor = new BusinessContextAccessor(new BusinessContextFactory(
                http, host.Db, host.Holder, NullLogger<BusinessContextFactory>.Instance));
            return new CrmAccessService(host.Db, http, accessor,
                new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance));
        }

        // The leak: Hierarchical has no CompanyID, so a node grafted under this manager made another company's
        // employee a "team member", and VisibleOwnerIdsAsync handed that id set to CRM as an owner whitelist.
        [Fact]
        public async Task The_crm_team_set_excludes_another_companys_employee()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(20, CompanyTwo));
            host.Seed.Hierarchicals.AddRange(
                new Hierarchical { H_ID = 1, H_Type = 5, H_ObjectID = 10, H_Parent = null },
                new Hierarchical { H_ID = 2, H_Type = 5, H_ObjectID = 11, H_Parent = 1 },
                new Hierarchical { H_ID = 3, H_Type = 5, H_ObjectID = 20, H_Parent = 1 });   // other company
            await host.Seed.SaveChangesAsync();

            var team = await Crm(host).TeamOwnerIdsAsync(CompanyOne, 10);

            Assert.Contains(10, team);
            Assert.Contains(11, team);
            Assert.DoesNotContain(20, team);
        }

        // The legacy signature resolves the company from the manager's OWN row — it must reach the same answer,
        // because 37 CrmPerm call sites and CrmController still use it.
        [Fact]
        public async Task The_legacy_crm_signature_resolves_the_company_from_the_managers_own_row()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, CompanyOne), Emp(11, CompanyOne), Emp(20, CompanyTwo));
            host.Seed.Hierarchicals.AddRange(
                new Hierarchical { H_ID = 1, H_Type = 5, H_ObjectID = 10, H_Parent = null },
                new Hierarchical { H_ID = 2, H_Type = 5, H_ObjectID = 11, H_Parent = 1 },
                new Hierarchical { H_ID = 3, H_Type = 5, H_ObjectID = 20, H_Parent = 1 });
            await host.Seed.SaveChangesAsync();

            var team = await ((ICrmAccessService)Crm(host)).TeamOwnerIdsAsync(10);

            Assert.Contains(11, team);
            Assert.DoesNotContain(20, team);
        }

        // A manager with no company on their row gets nothing — never company 1 by default.
        [Fact]
        public async Task A_manager_with_no_company_on_their_row_gets_an_empty_crm_team_set()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(10, 0), Emp(11, CompanyOne));
            host.Seed.Hierarchicals.AddRange(
                new Hierarchical { H_ID = 1, H_Type = 5, H_ObjectID = 10, H_Parent = null },
                new Hierarchical { H_ID = 2, H_Type = 5, H_ObjectID = 11, H_Parent = 1 });
            await host.Seed.SaveChangesAsync();

            Assert.Empty(await ((ICrmAccessService)Crm(host)).TeamOwnerIdsAsync(10));
        }

        // ---- the leave approver chain ----

        private static LeaveWorkflowService Leave(PlatformTestHost host)
            => new(host.Db, new NoopNotifications(), new NoopLeaveDashboard(),
                NullLogger<LeaveWorkflowService>.Instance);

        // employee node → position node → manager employee node, which is the shape ManagerChainAsync walks
        private static void SeedChain(PlatformTestHost host, int subordinate, int manager)
        {
            host.Seed.Hierarchicals.AddRange(
                new Hierarchical { H_ID = 100 + subordinate, H_Type = 5, H_ObjectID = subordinate, H_Parent = 200 + subordinate },
                new Hierarchical { H_ID = 200 + subordinate, H_Type = 4, H_ObjectID = null, H_Parent = 100 + manager });
        }

        [Fact]
        public async Task The_leave_approver_chain_excludes_a_manager_from_another_company()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(20, CompanyTwo));
            // employee 11 (company 1) reports to employee 20 (COMPANY 2) through the shared tree
            host.Seed.Hierarchicals.Add(new Hierarchical { H_ID = 120, H_Type = 5, H_ObjectID = 20, H_Parent = null });
            SeedChain(host, 11, 20);
            await host.Seed.SaveChangesAsync();

            var result = await Leave(host).ApproverChainAsync(11);

            // The foreign manager is not an approver — before C.1 they were written into
            // CurrentApproverEmployeeID, notified, shown the request, and could approve it.
            Assert.DoesNotContain(20, result.Approvers);
            Assert.True(result.HierarchyDefect);
            Assert.Equal(1, result.DroppedNodes);
        }

        // The trap that closing the leak could have created: an empty chain means "requester is at the top" and
        // CreateAsync AUTO-APPROVES. A chain emptied by a cross-company graft must be refused instead.
        [Fact]
        public async Task A_chain_broken_by_a_cross_company_graft_refuses_the_request_instead_of_auto_approving()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(20, CompanyTwo));
            host.Seed.Hierarchicals.Add(new Hierarchical { H_ID = 120, H_Type = 5, H_ObjectID = 20, H_Parent = null });
            SeedChain(host, 11, 20);
            host.Seed.LeaveTypes.Add(new LeaveTypes { ID = 1, NameAr = "سنوية", NameEn = "Annual" });
            await host.Seed.SaveChangesAsync();

            var (ok, error, req) = await Leave(host).CreateAsync(
                11, 1, DateTime.Today.AddDays(3), DateTime.Today.AddDays(4), "r");

            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Null(req);
            Assert.Empty(await host.Db.LeaveRequests.ToListAsync());   // nothing was approved, nothing was written
        }

        [Fact]
        public async Task A_same_company_manager_is_still_a_valid_approver()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne));
            host.Seed.Hierarchicals.Add(new Hierarchical { H_ID = 110, H_Type = 5, H_ObjectID = 10, H_Parent = null });
            SeedChain(host, 11, 10);
            await host.Seed.SaveChangesAsync();

            var result = await Leave(host).ApproverChainAsync(11);

            Assert.Contains(10, result.Approvers);
            Assert.False(result.HierarchyDefect);
        }

        // Cycle protection is preserved by the change, not merely untouched: a tree where two employees are each
        // other's manager must terminate rather than climb forever.
        [Fact]
        public async Task A_cyclic_org_tree_still_terminates()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(11, CompanyOne), Emp(10, CompanyOne));

            // A genuine cycle cannot be INSERTED in one pass — Hierarchical's self-referencing FK makes EF
            // refuse the batch — so the nodes go in parentless and the cycle is closed by an UPDATE. That is
            // also how a cycle appears in reality: someone re-parents an existing node.
            var n = new[]
            {
                new Hierarchical { H_ID = 111, H_Type = 5, H_ObjectID = 11 },   // employee 11
                new Hierarchical { H_ID = 211, H_Type = 4, H_ObjectID = null }, // 11's position
                new Hierarchical { H_ID = 110, H_Type = 5, H_ObjectID = 10 },   // employee 10
                new Hierarchical { H_ID = 210, H_Type = 4, H_ObjectID = null }, // 10's position
            };
            host.Seed.Hierarchicals.AddRange(n);
            await host.Seed.SaveChangesAsync();

            n[0].H_Parent = 211; n[1].H_Parent = 110;   // 11 reports to 10
            n[2].H_Parent = 210; n[3].H_Parent = 111;   // …and 10 reports to 11
            await host.Seed.SaveChangesAsync();

            var result = await Leave(host).ApproverChainAsync(11);

            Assert.True(result.Approvers.Count <= 2);
            Assert.Contains(10, result.Approvers);
        }

        [Fact]
        public async Task An_employee_with_no_company_produces_no_approver_chain_and_is_reported_as_a_defect()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            host.Seed.Employee.AddRange(Emp(11, 0), Emp(10, CompanyOne));
            host.Seed.Hierarchicals.Add(new Hierarchical { H_ID = 110, H_Type = 5, H_ObjectID = 10, H_Parent = null });
            SeedChain(host, 11, 10);
            await host.Seed.SaveChangesAsync();

            var result = await Leave(host).ApproverChainAsync(11);

            Assert.Empty(result.Approvers);
            Assert.True(result.HierarchyDefect);
        }

        private sealed class NoopNotifications : INotificationService
        {
            public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
                string? bodyAr, string? bodyEn, string type, int? refId = null,
                string? url = null, int? companyId = null, int? actorEmployeeId = null,
                string? priority = null, string? category = null, string? dedupKey = null,
                DateTime? expiresAt = null, string? icon = null,
                string? entityType = null, int? entityId = null) => Task.CompletedTask;

            public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
                string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
                string type, int? refId = null, int? exceptEmployeeId = null) => Task.FromResult(0);
        }

        // Returns enough working days and enough balance that CreateAsync reaches the approver chain — the only
        // part of it these tests are about. Anything the chain tests do not exercise throws rather than
        // returning a plausible-looking zero.
        private sealed class NoopLeaveDashboard : ILeaveDashboardService
        {
            public Task<PeopleDashboardDto> BuildAsync(int employeeId) => throw new NotImplementedException();

            public Task<int> RemainingForTypeAsync(int employeeId, int leaveTypeId) => Task.FromResult(30);

            public Task<int> RemainingForTypeInYearAsync(int employeeId, int leaveTypeId, int year)
                => Task.FromResult(30);

            public Task<bool[]?> WorkDayFlagsAsync(int employeeId) => Task.FromResult<bool[]?>(null);

            public Task<int> WorkingDaysAsync(int employeeId, DateTime start, DateTime end)
                => Task.FromResult(Math.Max(1, (end.Date - start.Date).Days + 1));
        }
    }
}
