using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // RSK-26 — THE LEAVE DECISION IS AUTHORIZED AT THE WRITE BOUNDARY.
    //
    // WHAT WAS WRONG, stated plainly because it is the reason every test below exists.
    // LeaveWorkflowService.DecideAsync took `approverEmployeeId` as a PARAMETER, and its only authority
    // test was that the value equalled the stored CurrentApproverEmployeeID. It received no
    // BusinessContext, asked no access service, and read the request with no company predicate. So a
    // caller that passed a DIFFERENT employee id decided AS that person — the id it supplied WAS the
    // current approver, which is exactly what the check was comparing against. The two live callers are
    // correct today; nothing made a third one correct, and an authoritative service that depends on its
    // callers being careful is a security ASSUMPTION rather than a boundary.
    //
    // WHY THESE ARE NOT CONTROLLER TESTS. The controller checks already existed and already passed, and
    // they stay — the architecture is controller AND service, defence in depth. Every test here calls
    // the SERVICE directly, because the defect was only ever reachable that way: a suite that went
    // through the controller would prove the controller works and say nothing about the boundary.
    //
    // COMPANIES ARE 41 AND 77, NEVER 1 — company 1 is the value every historical bug defaulted to.
    // ================================================================================================
    public class LeaveDecisionAuthorizationTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int Requester = 10;
        private const int Manager1 = 20;
        private const int Manager2 = 30;
        private const int Outsider = 40;
        private const int LeaveType = 1;

        // ==========================================================================================
        // THE PATH THAT MUST STILL WORK
        // ==========================================================================================

        [Fact]
        public async Task The_authenticated_current_approver_can_approve()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var (ok, error) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: true);

            Assert.True(ok, error);
            Assert.Equal(2, Row(rig, req.ID).CurrentLevel);          // advanced to level 2
            Assert.Single(rig.Events_(LeaveRequestEvents.Advanced));
        }

        [Fact]
        public async Task The_authenticated_current_approver_can_reject()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var (ok, error) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: false);

            Assert.True(ok, error);
            Assert.Equal(2, Row(rig, req.ID).Status);                 // Rejected
            Assert.Single(rig.Events_(LeaveRequestEvents.Rejected));
        }

        /// The parameter and the identity agreeing is the ONLY shape that authorizes.
        [Fact]
        public async Task The_approver_parameter_matching_the_resolved_caller_succeeds()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            Assert.True((await rig.DecideAs(Manager1, req.ID, Manager1, true)).ok);
        }

        // ==========================================================================================
        // THE DEFECT ITSELF
        // ==========================================================================================

        /// THE TEST THIS WHOLE RISK EXISTS FOR. The caller is authenticated as somebody else and passes
        /// the id of the REAL stored approver. Under the old code this succeeded — the parameter was
        /// compared against CurrentApproverEmployeeID and matched, so the decision was taken as that
        /// person. The parameter must never be the authority.
        [Fact]
        public async Task A_caller_cannot_decide_by_naming_the_stored_approver_they_are_not()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            foreach (var impersonator in new[] { Requester, Manager2, Outsider })
            {
                var (ok, error) = await rig.DecideAs(impersonator, req.ID, Manager1, approve: true);

                Assert.False(ok);
                Assert.NotNull(error);
            }

            await AssertNothingHappenedAsync(rig, req.ID);
        }

        /// The mirror of the above: the caller is who they say they are, but they are not the current
        /// approver. Authority comes from the stored chain, not from being a plausible manager.
        [Fact]
        public async Task A_caller_who_is_not_the_current_approver_is_refused()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            foreach (var who in new[] { Manager2, Outsider })
            {
                var (ok, _) = await rig.DecideAs(who, req.ID, who, approve: true);
                Assert.False(ok);
            }

            await AssertNothingHappenedAsync(rig, req.ID);
        }

        // ==========================================================================================
        // SEPARATION OF DUTIES, NOW AT THE WRITE BOUNDARY TOO
        // ==========================================================================================

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_requester_cannot_decide_their_own_request(bool approve)
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var (ok, _) = await rig.DecideAs(Requester, req.ID, Requester, approve);

            Assert.False(ok);
            await AssertNothingHappenedAsync(rig, req.ID);
        }

        /// Even when the requester somehow IS the stored current approver, the boundary refuses. The
        /// chain is built so this cannot arise; the service declines to be where it starts being able to.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_requester_is_refused_even_if_they_are_the_stored_approver(bool approve)
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            // THROUGH THE SERVICE'S OWN CONTEXT. Writing this through the seed context left the service
            // reading its own tracked copy, still naming the original approver - so the decision was
            // refused by the current-approver check and the separation-of-duties rule was never reached.
            // The test passed while proving nothing, which is the failure mode a mutant is for.
            var row = await rig.Host.Db.LeaveRequests.FirstAsync(r => r.ID == req.ID);
            row.CurrentApproverEmployeeID = Requester;
            var step = await rig.Host.Db.LeaveApprovalSteps.FirstAsync(s => s.LeaveRequestID == req.ID && s.Level == 1);
            step.ApproverEmployeeID = Requester;
            await rig.Host.Db.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(Requester, req.ID, Requester, approve);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
        }

        /// THE OTHER HALF OF THE PARAMETER RULE, and the one that is easy to miss. Here the caller IS
        /// the real current approver - they are entitled to decide - but they pass somebody else's id.
        /// The parameter is what the service records as the decider: it reaches DecidedByEmployeeId in
        /// the permanent event, ApproverEmployeeID on the request, updatedBy on the step and the
        /// notification. Accepting it would let an entitled approver file a true decision under a
        /// colleague's name, which is forgery rather than escalation - and no other check catches it,
        /// because every other check is satisfied.
        [Fact]
        public async Task The_real_approver_cannot_record_the_decision_as_someone_else()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            foreach (var borrowed in new[] { Manager2, Outsider, Requester })
            {
                var (ok, _) = await rig.DecideAs(Manager1, req.ID, borrowed, approve: true);
                Assert.False(ok);
            }

            await AssertNothingHappenedAsync(rig, req.ID);
        }

        /// A malformed identity must not authorize. Both of these are defence in depth - the domain does
        /// not produce a zero employee or a zero company - so the pathological state is constructed
        /// deliberately, because a guard nothing can reach is a guard nobody can trust.
        [Fact]
        public async Task A_context_carrying_no_employee_authorizes_nothing()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var row = await rig.Host.Db.LeaveRequests.FirstAsync(r => r.ID == req.ID);
            row.CurrentApproverEmployeeID = 0;
            await rig.Host.Db.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(0, req.ID, 0, approve: true);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
        }

        [Fact]
        public async Task A_context_carrying_no_company_authorizes_nothing()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            // the only row that carries this request's company loses it too, so the company predicate
            // alone would MATCH - which is exactly why the context guard has to exist on its own
            var emp = await rig.Host.Db.Employee.FirstAsync(e => e.ID == Requester);
            emp.EmpCompanyID = 0;
            await rig.Host.Db.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: true, companyOverride: 0);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
        }

        // ==========================================================================================
        // TENANCY — LeaveRequest carries no CompanyID, so company comes through the requester
        // ==========================================================================================

        [Fact]
        public async Task A_request_belonging_to_another_company_is_refused()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            // the caller is the real current approver, but authenticated in the WRONG company
            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: true, companyOverride: CompanyB);

            Assert.False(ok);
            await AssertNothingHappenedAsync(rig, req.ID);
        }

        [Fact]
        public async Task A_request_whose_requester_has_no_employee_row_is_refused()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            // the requester's Employee row is the ONLY basis for this request's company
            var emp = await rig.Host.Seed.Employee.FirstAsync(e => e.ID == Requester);
            rig.Host.Seed.Employee.Remove(emp);
            await rig.Host.Seed.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: true);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
        }

        [Fact]
        public async Task A_request_that_does_not_exist_fails_closed()
        {
            using var rig = await ReadyAsync();
            await SubmitAsync(rig);

            var (ok, error) = await rig.DecideAs(Manager1, 999999, Manager1, approve: true);

            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Empty(rig.Events_(LeaveRequestEvents.Advanced));
        }

        /// No injected identity means no resolvable caller, and the boundary refuses rather than
        /// falling back to the parameter. This is the property that makes the nullable field safe.
        [Fact]
        public async Task A_service_built_without_a_caller_identity_refuses_every_decision()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var blind = new LeaveWorkflowService(
                rig.Host.Db, rig.Notifications, new StubDashboard(),
                NullLogger<LeaveWorkflowService>.Instance, rig.Events);

            var (ok, _) = await blind.DecideAsync(req.ID, Manager1, approve: true, note: null);

            Assert.False(ok);
            await AssertNothingHappenedAsync(rig, req.ID);
        }

        // ==========================================================================================
        // THE FAILURE SHAPE IS PART OF THE CONTRACT
        // ==========================================================================================

        /// Asserted as one whole rather than as four separate near-duplicates: the claim is that a
        /// refusal is INERT, and inertness is only meaningful if every observable is checked at once.
        [Fact]
        public async Task An_unauthorized_decision_changes_nothing_observable()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            var eventsBefore = rig.Events_().Select(e => e.EventId).ToList();
            var tasksBefore = rig.Tasks().Select(t => (t.ID, t.Status, t.AssigneeEmployeeId)).ToList();
            var notifyBefore = rig.Notifications.Sent.Count;
            var rowBefore = Row(rig, req.ID);
            var stepsBefore = rig.Steps(req.ID)
                .Select(s => (s.ID, s.Status, s.DecisionAt, s.DecisionNote)).ToList();

            var (ok, _) = await rig.DecideAs(Outsider, req.ID, Manager1, approve: true, note: "let me in");
            Assert.False(ok);
            await rig.DispatchAllAsync();

            Assert.Equal(eventsBefore, rig.Events_().Select(e => e.EventId).ToList());
            Assert.Equal(tasksBefore, rig.Tasks().Select(t => (t.ID, t.Status, t.AssigneeEmployeeId)).ToList());
            Assert.Equal(notifyBefore, rig.Notifications.Sent.Count);

            var rowAfter = Row(rig, req.ID);
            Assert.Equal(rowBefore.Status, rowAfter.Status);
            Assert.Equal(rowBefore.CurrentLevel, rowAfter.CurrentLevel);
            Assert.Equal(rowBefore.CurrentApproverEmployeeID, rowAfter.CurrentApproverEmployeeID);
            Assert.Equal(rowBefore.DecisionAt, rowAfter.DecisionAt);
            Assert.Equal(rowBefore.DecisionNote, rowAfter.DecisionNote);
            Assert.Equal(rowBefore.ApproverEmployeeID, rowAfter.ApproverEmployeeID);

            Assert.Equal(stepsBefore,
                rig.Steps(req.ID).Select(s => (s.ID, s.Status, s.DecisionAt, s.DecisionNote)).ToList());
        }

        // ==========================================================================================
        // THE CLOSED EVENT-TO-WORK BEHAVIOUR STILL HOLDS THROUGH THE NEW BOUNDARY
        // ==========================================================================================

        [Fact]
        public async Task An_authorized_advance_still_closes_the_current_task_and_opens_the_next()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            Assert.True((await rig.DecideAs(Manager1, req.ID, Manager1, true)).ok);
            await rig.DispatchAllAsync();

            var tasks = rig.Tasks();
            Assert.Equal(2, tasks.Count);
            Assert.Equal(TaskStatuses.Done, tasks.Single(t => t.AssigneeEmployeeId == Manager1).Status);
            Assert.Equal(TaskStatuses.New, tasks.Single(t => t.AssigneeEmployeeId == Manager2).Status);
        }

        [Fact]
        public async Task An_authorized_final_approval_still_completes_the_current_task()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();
            await rig.DecideAs(Manager1, req.ID, Manager1, true);
            await rig.DispatchAllAsync();

            Assert.True((await rig.DecideAs(Manager2, req.ID, Manager2, true)).ok);
            await rig.DispatchAllAsync();

            Assert.Equal(2, rig.Tasks().Count);
            Assert.All(rig.Tasks(), t => Assert.Equal(TaskStatuses.Done, t.Status));
            Assert.Equal(1, Row(rig, req.ID).Status);
        }

        [Fact]
        public async Task An_authorized_rejection_still_completes_the_current_task()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            Assert.True((await rig.DecideAs(Manager1, req.ID, Manager1, false)).ok);
            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(TaskStatuses.Done, task.Status);
            Assert.Equal(2, Row(rig, req.ID).Status);
        }

        /// The atomic claim introduced by the event-to-work slice must COMPOSE with the new checks
        /// rather than be replaced by them.
        [Fact]
        public async Task Duplicate_decision_protection_survives_the_new_authorization()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();
            Assert.True((await rig.DecideAs(Manager1, req.ID, Manager1, true)).ok);
            await rig.DispatchAllAsync();

            int events = rig.Events_().Count;
            int tasks = rig.Tasks().Count;

            // the same authorized person, deciding the same level a second time
            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, true);

            Assert.False(ok);
            Assert.Equal(events, rig.Events_().Count);
            Assert.Equal(tasks, rig.Tasks().Count);
        }

        [Fact]
        public async Task The_notifications_of_an_authorized_decision_are_unchanged()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            rig.Notifications.Sent.Clear();

            await rig.DecideAs(Manager1, req.ID, Manager1, true);

            Assert.Equal(
                new[] { (Manager2, "leave_submitted"), (Requester, "leave_progress") },
                rig.Notifications.Sent.ToArray());
        }

        // ==========================================================================================
        // helpers
        // ==========================================================================================

        private static DateTime Day(int d) => new DateTime(2026, 12, d, 0, 0, 0, DateTimeKind.Utc);

        private static LeaveRequest Row(Rig rig, int id) =>
            rig.Host.Seed.LeaveRequests.AsNoTracking().First(r => r.ID == id);

        private static async Task<LeaveRequest> SubmitAsync(Rig rig)
        {
            rig.Identity.EmployeeId = Requester;
            var (ok, error, req) = await rig.Leave.CreateAsync(Requester, LeaveType, Day(1), Day(3), "r");
            Assert.True(ok, error);
            return req!;
        }

        private static async Task AssertNothingHappenedAsync(Rig rig, int requestId)
        {
            Assert.Equal(0, Row(rig, requestId).Status);
            Assert.Equal(1, Row(rig, requestId).CurrentLevel);
            Assert.All(rig.Steps(requestId), s => Assert.Equal(0, s.Status));
            Assert.Empty(rig.Events_(LeaveRequestEvents.Advanced));
            Assert.Empty(rig.Events_(LeaveRequestEvents.Approved));
            Assert.Empty(rig.Events_(LeaveRequestEvents.Rejected));
            await rig.DispatchAllAsync();
            Assert.DoesNotContain(rig.Tasks(), t => t.Status == TaskStatuses.Done);
        }

        private static async Task<Rig> ReadyAsync()
        {
            var rig = new Rig(CompanyA);
            var db = rig.Host.Seed;
            foreach (var id in new[] { Requester, Manager1, Manager2, Outsider })
                db.Employee.Add(Emp(id, CompanyA));
            db.LeaveTypes.Add(new LeaveTypes { ID = LeaveType, NameAr = "سنوية", NameEn = "Annual" });

            // Requester -> Manager1 -> Manager2 (top), the exact shape the climb walks
            var ladder = new[] { Requester, Manager1, Manager2 };
            for (var i = 0; i < ladder.Length - 1; i++)
                db.Hierarchicals.AddRange(
                    new Hierarchical { H_ID = 100 + ladder[i], H_Type = 5, H_ObjectID = ladder[i], H_Parent = 200 + ladder[i] },
                    new Hierarchical { H_ID = 200 + ladder[i], H_Type = 4, H_ObjectID = null, H_Parent = 100 + ladder[i + 1] });
            db.Hierarchicals.Add(new Hierarchical { H_ID = 100 + Manager2, H_Type = 5, H_ObjectID = Manager2, H_Parent = null });

            await db.SaveChangesAsync();
            return rig;
        }

        private static Employee Emp(int id, int companyId) => new()
        {
            ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
            EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
            Email = $"e{id}-{companyId}@example.invalid", ProfileImage = "-", Gender = "M",
            MaritalStatus = "S", UserId = $"user-{id}-{companyId}",
        };

        private sealed class Rig : IDisposable
        {
            public PlatformTestHost Host { get; }
            public Sink Notifications { get; } = new();
            public ILeaveWorkflowService Leave { get; }
            public IBusinessEventService Events { get; }
            public TaskOrchestrationConsumer Consumer { get; }
            public Acting Identity { get; }
            public int CompanyId { get; }

            public Rig(int companyId)
            {
                CompanyId = companyId;
                Host = new PlatformTestHost(companyId: companyId);
                Identity = new Acting(companyId, Requester);
                var events = new BusinessEventService(
                    Host.Db, new EntityRegistry(Host.Db), Identity,
                    NullLogger<BusinessEventService>.Instance);
                Events = events;

                Leave = new LeaveWorkflowService(
                    Host.Db, Notifications, new StubDashboard(),
                    NullLogger<LeaveWorkflowService>.Instance, events, Identity);

                var tasks = new TaskService(
                    Host.Db, new TaskCalendarEventPublisher(events),
                    new TaskNotificationService(Host.Db, new UatRecordingNotificationService(Host.Db)));
                Consumer = new TaskOrchestrationConsumer(new TaskOrchestrationService(
                    Host.Db,
                    new ITaskOrchestrationRule[] { new LeaveApprovalRule(), new LeaveApprovalAdvanceCloseRule() },
                    tasks,
                    new TaskWorkPolicyResolver(Host.Db, null)));
            }

            /// A decision taken BY somebody, which is the only way to call it now. `parameterValue` is
            /// separate from `actingAs` on purpose: the two disagreeing is the impersonation case.
            public Task<(bool ok, string? error)> DecideAs(
                int actingAs, int requestId, int parameterValue, bool approve,
                string? note = null, int? companyOverride = null)
            {
                Identity.EmployeeId = actingAs;
                Identity.CompanyOverride = companyOverride;
                return Leave.DecideAsync(requestId, parameterValue, approve, note);
            }

            public List<BusinessEvent> Events_(string? type = null) => Host.Seed.BusinessEvents
                .AsNoTracking().Where(e => e.CompanyID == CompanyId)
                .Where(e => type == null || e.EventType == type)
                .OrderBy(e => e.EventId).ToList();

            public async Task DispatchAllAsync()
            {
                foreach (var stored in Events_())
                    await Consumer.HandleAsync(Events.BuildEnvelope(stored));
            }

            public List<TaskItem> Tasks() => Host.Seed.TaskItems
                .AsNoTracking().Where(t => t.CompanyId == CompanyId).OrderBy(t => t.ID).ToList();

            public List<LeaveApprovalStep> Steps(int requestId) => Host.Seed.LeaveApprovalSteps
                .AsNoTracking().Where(s => s.LeaveRequestID == requestId).OrderBy(s => s.Level).ToList();

            public void Dispose() => Host.Dispose();
        }

        /// The resolved caller. Settable, because the whole subject of this suite is WHO is acting.
        internal sealed class Acting : IBusinessContextAccessor
        {
            private readonly int _companyId;
            public int EmployeeId { get; set; }
            public int? CompanyOverride { get; set; }

            public Acting(int companyId, int employeeId) { _companyId = companyId; EmployeeId = employeeId; }

            public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(Build());
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<BusinessContext?>(Build());

            private BusinessContext Build() => new()
            {
                CompanyId = CompanyOverride ?? _companyId,
                EmployeeId = EmployeeId,
                UserId = "u" + EmployeeId,
                Roles = Array.Empty<string>(),
                CorrelationId = Guid.NewGuid(),
            };
        }

        private sealed class Sink : INotificationService
        {
            public readonly List<(int Employee, string Type)> Sent = new();

            public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn, string? bodyAr,
                string? bodyEn, string type, int? refId = null, string? url = null, int? companyId = null,
                int? actorEmployeeId = null, string? priority = null, string? category = null,
                string? dedupKey = null, DateTime? expiresAt = null, string? icon = null,
                string? entityType = null, int? entityId = null)
            { Sent.Add((recipientEmployeeId, type)); return Task.CompletedTask; }

            public Task<int> NotifyRoleAsync(int companyId, string role, string[] actions, string? titleAr,
                string? titleEn, string? bodyAr, string? bodyEn, string type, int? refId = null,
                int? actorEmployeeId = null) => Task.FromResult(0);
        }

        private sealed class StubDashboard : ILeaveDashboardService
        {
            public Task<PeopleDashboardDto> BuildAsync(int employeeId) => throw new NotImplementedException();
            public Task<int> RemainingForTypeAsync(int employeeId, int leaveTypeId) => Task.FromResult(30);
            public Task<int> RemainingForTypeInYearAsync(int employeeId, int leaveTypeId, int year) => Task.FromResult(30);
            public Task<bool[]?> WorkDayFlagsAsync(int employeeId) => Task.FromResult<bool[]?>(null);
            public Task<int> WorkingDaysAsync(int employeeId, DateTime start, DateTime end)
                => Task.FromResult(Math.Max(1, (end.Date - start.Date).Days + 1));
        }
    }
}
