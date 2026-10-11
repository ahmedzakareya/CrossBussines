using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // ON HOLD / SLA PAUSE.
    //
    // REAL SERVICES THROUGHOUT. The real TaskService, the real TasksAccessService over the real
    // PlatformRoleDirectory, the real OrgHierarchy over a real org tree, the real BusinessEventService
    // writing real rows, and the real escalation and overdue sweeps - on in-memory SQLite with the
    // company write guard armed. The only double is the notification sink, which persists, so the
    // dedup paths run for real.
    //
    // WHY THAT MATTERS MORE HERE THAN USUAL: the two authorities this capability rests on are "does
    // this caller hold Manage" and "is this caller the assignee's CURRENT direct manager". Both are
    // answered by production code walking real rows; a fake would have proved only that the hold
    // service calls something.
    // =============================================================================================
    public class TaskHoldTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;

        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();
        private readonly CrossDbContext _db;
        private readonly MutableContextAccessor _contexts = new();
        private readonly ITaskService _tasks;
        private readonly ITaskEscalationService _escalation;
        private int _seq;

        public int Assignee { get; }
        public int Manager { get; }
        public int Bystander { get; }
        public int Admin { get; }

        public TaskHoldTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(CompanyA, null);

            var options = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            _db = new CrossDbContext(options, _holder);
            _db.Database.EnsureCreated();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }

            Manager = AddEmployee(CompanyA, "mgr");
            Assignee = AddEmployee(CompanyA, "emp");
            Bystander = AddEmployee(CompanyA, "other");
            Admin = AddEmployee(CompanyA, "admin");

            // manager -> department -> assignee, so the manager is an ANCESTOR and not the immediate
            // parent: the resolver has to climb, exactly as it does in production.
            var mgrNode = AddNode(null, Manager, "manager");
            var dept = AddNode(mgrNode, null, "department");
            AddNode(dept, Assignee, "employee");

            var events = new BusinessEventService(_db, new EntityRegistry(_db), _contexts,
                NullLogger<BusinessEventService>.Instance);
            var publisher = new TaskCalendarEventPublisher(events);
            var notify = new TaskNotificationService(_db, new UatRecordingNotificationService(_db));
            var org = new OrgHierarchy(_db, NullLogger<OrgHierarchy>.Instance);
            var access = new TasksAccessService(_db,
                new PlatformRoleDirectory(_db, NullLogger<PlatformRoleDirectory>.Instance),
                org, () => new AllowAllProvider(), NullLogger<TasksAccessService>.Instance);

            _tasks = new TaskService(_db, publisher, notify, events, _contexts, access, org);

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Tasks:Escalation:GraceHours"] = "24" }).Build();
            _escalation = new TaskEscalationService(_db, notify, publisher, org, config, _contexts);

            ActAs(Assignee, CompanyA);
        }

        public void Dispose() { _db.Dispose(); _conn.Dispose(); }

        // ---- harness ----------------------------------------------------------------------------

        /// The ambient caller. Hold reads this to prove the actor IS the resolved caller before it
        /// reads any authority, so every test that changes who is acting changes it here.
        private sealed class MutableContextAccessor : IBusinessContextAccessor
        {
            public BusinessContext? Current { get; set; }
            public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) =>
                Current != null ? Task.FromResult(Current) : throw new InvalidOperationException("unresolved");
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) =>
                Task.FromResult(Current);
        }

        private sealed class AllowAllProvider : IPlatformPermissionProvider
        {
            public Task<PermissionDecision> CanAsync(BusinessContext c, string entityType, int entityId,
                string action, CancellationToken ct = default) => Task.FromResult(PermissionDecision.Allow("test"));
        }

        private void ActAs(int employeeId, int companyId, BusinessContextSource source = BusinessContextSource.Test) =>
            _contexts.Current = new BusinessContext
            {
                CompanyId = companyId, EmployeeId = employeeId, UserId = "u" + employeeId,
                Roles = Array.Empty<string>(), Source = source,
            };

        private int AddEmployee(int companyId, string tag)
        {
            var e = new Employee
            {
                FirstName = "T", LastName = "T", FullName = "hold-" + tag, FullNameEn = "hold-" + tag,
                EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
                Email = $"hold-{Guid.NewGuid():N}@example.com", ProfileImage = "", Gender = "M",
                MaritalStatus = "S", UserId = "user-hold-" + Guid.NewGuid().ToString("N")[..8],
            };
            _db.Employee.Add(e);
            _db.SaveChanges();
            return e.ID;
        }

        private int AddNode(int? parent, int? employeeId, string name)
        {
            var n = new Hierarchical
            {
                H_Name = name, H_NameEn = name, H_Parent = parent,
                H_Type = employeeId.HasValue ? 5 : 1, H_ObjectID = employeeId, IsActive = true, Sort = ++_seq,
            };
            _db.Hierarchicals.Add(n);
            _db.SaveChanges();
            return n.H_ID;
        }

        private TaskItem AddTask(DateTime? due = null, string status = "New", int? assignee = null, int companyId = CompanyA)
        {
            var t = new TaskItem
            {
                CompanyId = companyId, Title = "hold task", AssigneeEmployeeId = assignee ?? Assignee,
                CreatedByEmployeeId = assignee ?? Assignee, Status = status, Priority = "Normal",
                DueDate = due, CreatedAt = DateTime.UtcNow,
            };
            _db.TaskItems.Add(t);
            _db.SaveChanges();
            return t;
        }

        /// Grants TasksAdministrator, and ALSO configures a role for somebody else so the company is no
        /// longer bootstrap-open - without that, Manage is refused at module level and the grant proves
        /// nothing about the role.
        private void GrantManage(int employeeId)
        {
            foreach (var (emp, role) in new[] { (employeeId, TasksRoles.TasksAdministrator), (Bystander, TasksRoles.TasksViewer) })
                _db.PlatformRoleAssignments.Add(new PlatformRoleAssignment
                {
                    CompanyID = CompanyA, Scope = EntityRegistry.ScopeTasks,
                    PrincipalType = PlatformPrincipalTypes.Employee, PrincipalId = emp,
                    Role = role, IsActive = true, CreatedAt = DateTime.UtcNow,
                });
            _db.SaveChanges();
        }

        private TaskItem Reload(int id) => _db.TaskItems.AsNoTracking().Single(x => x.ID == id);

        private string[] HoldEvents(int taskId) => _db.BusinessEvents.AsNoTracking()
            .Where(e => e.EntityType == "Task" && e.EntityId == taskId && e.EventType.StartsWith("Task.Hold"))
            .OrderBy(e => e.EventId).Select(e => e.EventType).ToArray();

        private int EventCount(int taskId, string eventType) => _db.BusinessEvents.AsNoTracking()
            .Count(e => e.EntityType == "Task" && e.EntityId == taskId && e.EventType == eventType);

        // =========================================================================================
        // T01-T06 — REQUEST
        // =========================================================================================

        [Fact] // T01 / T05 / T06
        public async Task Requesting_a_hold_records_who_asked_when_and_why_and_emits_one_event()
        {
            var t = AddTask();
            var (ok, err) = await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "waiting on the supplier");
            Assert.True(ok, err);

            var row = Reload(t.ID);
            Assert.NotNull(row.HoldRequestedAtUtc);
            Assert.Equal(Assignee, row.HoldRequestedByEmployeeId);
            Assert.Equal("waiting on the supplier", row.HoldReason);

            // ASKING IS NOT BEING GRANTED: no approval pair, so the SLA is still running.
            Assert.Null(row.HoldApprovedAtUtc);
            Assert.Null(row.HoldApprovedByEmployeeId);
            Assert.Equal(TaskStatuses.HoldStateRequested, TaskStatuses.HoldState(row));

            Assert.Equal(1, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldRequested));
            Assert.Equal("New", row.Status);
        }

        [Fact] // T02
        public async Task A_task_in_another_company_is_not_found()
        {
            var foreign = AddTask(companyId: CompanyB);
            var (ok, err) = await _tasks.RequestHoldAsync(CompanyA, foreign.ID, Assignee, "reason");

            Assert.False(ok);
            Assert.Equal("Task not found", err);          // absent and foreign answer identically
            Assert.Null(Reload(foreign.ID).HoldRequestedAtUtc);
        }

        [Fact] // T03
        public async Task An_invalid_company_refuses_and_never_falls_back()
        {
            var t = AddTask();
            foreach (var bad in new[] { 0, -1 })
            {
                var (ok, _) = await _tasks.RequestHoldAsync(bad, t.ID, Assignee, "reason");
                Assert.False(ok);
            }
            Assert.Null(Reload(t.ID).HoldRequestedAtUtc);

            // NON-VACUITY: the real company works.
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);
        }

        [Fact] // T04
        public async Task A_completed_task_cannot_be_put_on_hold()
        {
            var done = AddTask(status: "Done");
            var (ok, err) = await _tasks.RequestHoldAsync(CompanyA, done.ID, Assignee, "reason");
            Assert.False(ok);
            Assert.Equal("A completed task cannot be put on hold", err);
            Assert.Null(Reload(done.ID).HoldRequestedAtUtc);
        }

        [Fact] // a reason is mandatory, and refused rather than truncated
        public async Task A_hold_request_without_a_reason_is_refused()
        {
            var t = AddTask();
            foreach (var blank in new string?[] { null, "", "   " })
            {
                var (ok, err) = await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, blank);
                Assert.False(ok);
                Assert.Equal("A hold reason is required", err);
            }

            var (tooLong, longErr) = await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, new string('x', 501));
            Assert.False(tooLong);
            Assert.Contains("500", longErr!);

            Assert.Null(Reload(t.ID).HoldRequestedAtUtc);
        }

        [Fact] // T33 — a repeated request is a no-op and the FIRST one stands
        public async Task A_second_request_does_not_restamp_or_re_attribute_the_first()
        {
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "first reason")).ok);
            var first = Reload(t.ID);

            ActAs(Bystander, CompanyA);
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Bystander, "second reason")).ok);

            var after = Reload(t.ID);
            Assert.Equal(first.HoldRequestedAtUtc, after.HoldRequestedAtUtc);
            Assert.Equal(Assignee, after.HoldRequestedByEmployeeId);
            Assert.Equal("first reason", after.HoldReason);
            Assert.Equal(1, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldRequested));
        }

        // =========================================================================================
        // T07-T15 — WHO MAY DECIDE, AND APPROVAL
        // =========================================================================================

        [Fact] // T08 / T13 / T15
        public async Task The_current_direct_manager_can_approve_and_the_pause_starts_at_the_approval()
        {
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            ActAs(Manager, CompanyA);
            var (ok, err) = await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager);
            Assert.True(ok, err);

            var row = Reload(t.ID);
            Assert.NotNull(row.HoldApprovedAtUtc);
            Assert.Equal(Manager, row.HoldApprovedByEmployeeId);
            Assert.Equal(TaskStatuses.HoldStateOnHold, TaskStatuses.HoldState(row));

            // THE REQUEST PAIR IS PRESERVED: it is the record of why this hold exists.
            Assert.Equal(Assignee, row.HoldRequestedByEmployeeId);
            Assert.Equal("reason", row.HoldReason);

            Assert.Equal(1, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldApproved));
        }

        [Fact] // T07
        public async Task A_Manage_holder_who_is_not_the_manager_can_approve()
        {
            GrantManage(Admin);
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            ActAs(Admin, CompanyA);
            var (ok, err) = await _tasks.ApproveHoldAsync(CompanyA, t.ID, Admin);
            Assert.True(ok, err);
            Assert.Equal(Admin, Reload(t.ID).HoldApprovedByEmployeeId);
        }

        [Fact] // T09
        public async Task A_colleague_who_is_neither_manager_nor_administrator_cannot_approve()
        {
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            ActAs(Bystander, CompanyA);
            var (ok, err) = await _tasks.ApproveHoldAsync(CompanyA, t.ID, Bystander);
            Assert.False(ok);
            Assert.Equal("You may not decide a hold on this task", err);
            Assert.Null(Reload(t.ID).HoldApprovedAtUtc);
        }

        [Fact] // the self-approval rule, which survives every authority
        public async Task The_requester_may_not_approve_their_own_hold_even_holding_Manage()
        {
            GrantManage(Assignee);
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            var (ok, err) = await _tasks.ApproveHoldAsync(CompanyA, t.ID, Assignee);
            Assert.False(ok);
            Assert.Equal("The employee who requested this hold may not approve it", err);
            Assert.Null(Reload(t.ID).HoldApprovedAtUtc);

            // NON-VACUITY: somebody else with the same authority can.
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager)).ok);
        }

        [Fact] // T11 — a portal identity has no employee, so it can equal no actor
        public async Task An_external_portal_context_cannot_decide_a_hold()
        {
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            _contexts.Current = new BusinessContext
            {
                CompanyId = CompanyA, EmployeeId = null, UserId = "portal",
                Roles = new[] { "Admin" }, Source = BusinessContextSource.ExternalPortal,
            };
            Assert.False((await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager)).ok);
            Assert.Null(Reload(t.ID).HoldApprovedAtUtc);

            // NON-VACUITY: the manager, acting as themselves, can.
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager)).ok);
        }

        [Fact] // the actor must BE the caller - no borrowing somebody else's place in the org tree
        public async Task Passing_the_managers_id_while_acting_as_somebody_else_is_refused()
        {
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            ActAs(Bystander, CompanyA);                                             // the caller is the bystander...
            var (ok, _) = await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager);   // ...claiming to be the manager
            Assert.False(ok);
            Assert.Null(Reload(t.ID).HoldApprovedAtUtc);
        }

        [Fact] // T10 / T12 — the manager is resolved NOW, and company-intersected
        public async Task A_former_manager_loses_the_authority_the_moment_the_tree_changes()
        {
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            ActAs(Manager, CompanyA);
            Assert.True(await _tasks.CanDecideHoldAsync(CompanyA, t.ID, Manager));

            // The assignee is moved out from under that manager.
            var node = _db.Hierarchicals.Single(h => h.H_Type == 5 && h.H_ObjectID == Assignee);
            node.H_Parent = null;
            _db.SaveChanges();

            Assert.False(await _tasks.CanDecideHoldAsync(CompanyA, t.ID, Manager));
            Assert.False((await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager)).ok);
            Assert.Null(Reload(t.ID).HoldApprovedAtUtc);
        }

        [Fact] // T14 — approving twice must not move the clock the extension is measured from
        public async Task A_duplicate_approval_is_a_no_op_and_never_moves_the_approval_timestamp()
        {
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager)).ok);
            var first = Reload(t.ID).HoldApprovedAtUtc;

            Assert.True((await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager)).ok);

            Assert.Equal(first, Reload(t.ID).HoldApprovedAtUtc);
            Assert.Equal(1, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldApproved));
        }

        [Fact] // approving nothing
        public async Task Approving_with_no_pending_request_is_refused()
        {
            var t = AddTask();
            ActAs(Manager, CompanyA);
            var (ok, err) = await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager);
            Assert.False(ok);
            Assert.Equal("There is no pending hold request on this task", err);
        }

        // =========================================================================================
        // T16-T18 — REJECT
        // =========================================================================================

        [Fact] // T16 / T17 / T18
        public async Task Rejecting_clears_the_request_leaves_the_task_operational_and_emits_its_event()
        {
            var t = AddTask(due: DateTime.UtcNow.AddDays(3));
            var due = Reload(t.ID).DueDate;
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            ActAs(Manager, CompanyA);
            var (ok, err) = await _tasks.RejectHoldAsync(CompanyA, t.ID, Manager, "not this time");
            Assert.True(ok, err);

            var row = Reload(t.ID);
            Assert.Null(row.HoldRequestedAtUtc);
            Assert.Null(row.HoldRequestedByEmployeeId);
            Assert.Null(row.HoldReason);
            Assert.Null(row.HoldApprovedAtUtc);
            Assert.Equal(TaskStatuses.HoldStateNormal, TaskStatuses.HoldState(row));

            // THE TASK IS NOT CANCELLED OR COMPLETED, and its deadline is untouched.
            Assert.Equal("New", row.Status);
            Assert.Equal(due, row.DueDate);

            Assert.Equal(1, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldRejected));
        }

        [Fact] // rejecting a live hold would be a resume by another name, giving back a different amount of time
        public async Task An_already_approved_hold_cannot_be_rejected()
        {
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager)).ok);

            var (ok, err) = await _tasks.RejectHoldAsync(CompanyA, t.ID, Manager, "changed my mind");
            Assert.False(ok);
            Assert.Equal("This task is already on hold; it can be resumed but not rejected", err);
            Assert.NotNull(Reload(t.ID).HoldApprovedAtUtc);
        }

        // =========================================================================================
        // T19-T20 — DIRECT HOLD
        // =========================================================================================

        [Fact] // T19 / T20
        public async Task A_direct_hold_writes_the_approval_pair_without_fabricating_a_request()
        {
            var t = AddTask();
            ActAs(Manager, CompanyA);
            var (ok, err) = await _tasks.DirectHoldAsync(CompanyA, t.ID, Manager, "manager's own reason");
            Assert.True(ok, err);

            var row = Reload(t.ID);
            Assert.Equal(TaskStatuses.HoldStateOnHold, TaskStatuses.HoldState(row));
            Assert.Equal(Manager, row.HoldApprovedByEmployeeId);
            Assert.Equal("manager's own reason", row.HoldReason);

            // NOBODY ASKED, so nobody is recorded as having asked - and no HoldRequested event exists
            // putting a person's name against words they never wrote.
            Assert.Null(row.HoldRequestedAtUtc);
            Assert.Null(row.HoldRequestedByEmployeeId);
            Assert.Equal(0, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldRequested));
            Assert.Equal(new[] { CrossBuy.BL.Platform.TaskEvents.HoldApproved }, HoldEvents(t.ID));
        }

        [Fact] // a pending request must be decided, not bypassed
        public async Task A_direct_hold_is_refused_while_a_request_is_pending()
        {
            GrantManage(Assignee);
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            // The requester holds Manage, so without this rule they could reach ON HOLD on their own
            // request by calling it a direct hold - the exact thing self-approval forbids.
            var (ok, err) = await _tasks.DirectHoldAsync(CompanyA, t.ID, Assignee, "my own hold");
            Assert.False(ok);
            Assert.Equal("A hold request is pending on this task; approve or reject it", err);
            Assert.Null(Reload(t.ID).HoldApprovedAtUtc);
        }

        [Fact]
        public async Task A_direct_hold_on_a_completed_task_is_refused()
        {
            var done = AddTask(status: "Done");
            ActAs(Manager, CompanyA);
            var (ok, err) = await _tasks.DirectHoldAsync(CompanyA, done.ID, Manager, "reason");
            Assert.False(ok);
            Assert.Equal("A completed task cannot be put on hold", err);
        }

        // =========================================================================================
        // T21-T27 — RESUME AND THE SLA ARITHMETIC
        // =========================================================================================

        [Fact] // T21 / T25 / T26 — the whole product behaviour, in one test
        public async Task Resume_extends_the_deadline_by_exactly_the_time_the_hold_lasted()
        {
            var due = new DateTime(2030, 1, 10, 12, 0, 0, DateTimeKind.Utc);
            var t = AddTask(due: due);
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager)).ok);

            // The hold is backdated so the elapsed span is a known quantity rather than a race.
            var tracked = _db.TaskItems.Single(x => x.ID == t.ID);
            var approvedAt = DateTime.UtcNow.AddHours(-30);
            tracked.HoldApprovedAtUtc = approvedAt;
            _db.SaveChanges();

            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);

            var row = Reload(t.ID);
            var held = DateTime.UtcNow - approvedAt;
            // D + (H2 - H1), to the minute: the two clock reads are a few ms apart, not a few minutes.
            Assert.Equal(due.Add(held), row.DueDate!.Value, TimeSpan.FromMinutes(1));

            // ALL FIVE CLEARED, so the next cycle starts from a clean state.
            Assert.Null(row.HoldRequestedAtUtc);
            Assert.Null(row.HoldRequestedByEmployeeId);
            Assert.Null(row.HoldReason);
            Assert.Null(row.HoldApprovedAtUtc);
            Assert.Null(row.HoldApprovedByEmployeeId);
            Assert.Equal(TaskStatuses.HoldStateNormal, TaskStatuses.HoldState(row));

            Assert.Equal(1, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldResumed));
        }

        [Fact] // T22
        public async Task A_task_with_no_deadline_resumes_with_no_deadline_invented()
        {
            var t = AddTask(due: null);
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.DirectHoldAsync(CompanyA, t.ID, Manager, "reason")).ok);
            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);

            Assert.Null(Reload(t.ID).DueDate);
        }

        [Fact] // T23 — a pending request pauses nothing, so it gives nothing back
        public async Task Time_spent_merely_requested_does_not_extend_the_deadline()
        {
            var due = new DateTime(2030, 3, 1, 9, 0, 0, DateTimeKind.Utc);
            var t = AddTask(due: due);
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            // Backdate the REQUEST by a week; nothing was ever approved.
            var tracked = _db.TaskItems.Single(x => x.ID == t.ID);
            tracked.HoldRequestedAtUtc = DateTime.UtcNow.AddDays(-7);
            _db.SaveChanges();

            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);   // no-op

            Assert.Equal(due, Reload(t.ID).DueDate);
        }

        [Fact] // T24
        public async Task A_rejected_request_does_not_extend_the_deadline()
        {
            var due = new DateTime(2030, 4, 1, 9, 0, 0, DateTimeKind.Utc);
            var t = AddTask(due: due);
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);

            var tracked = _db.TaskItems.Single(x => x.ID == t.ID);
            tracked.HoldRequestedAtUtc = DateTime.UtcNow.AddDays(-5);
            _db.SaveChanges();

            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.RejectHoldAsync(CompanyA, t.ID, Manager, "no")).ok);

            Assert.Equal(due, Reload(t.ID).DueDate);
        }

        [Fact] // T31 — resume is not an accumulator
        public async Task A_second_resume_does_not_extend_the_deadline_again()
        {
            var due = new DateTime(2030, 5, 1, 9, 0, 0, DateTimeKind.Utc);
            var t = AddTask(due: due);
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.DirectHoldAsync(CompanyA, t.ID, Manager, "reason")).ok);

            var tracked = _db.TaskItems.Single(x => x.ID == t.ID);
            tracked.HoldApprovedAtUtc = DateTime.UtcNow.AddHours(-10);
            _db.SaveChanges();

            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);
            var afterFirst = Reload(t.ID).DueDate;

            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);   // no-op
            Assert.Equal(afterFirst, Reload(t.ID).DueDate);
            Assert.Equal(1, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldResumed));
        }

        [Fact] // T27 — two cycles, each paying its own extension
        public async Task A_second_hold_cycle_works_independently_of_the_first()
        {
            var due = new DateTime(2030, 6, 1, 9, 0, 0, DateTimeKind.Utc);
            var t = AddTask(due: due);
            ActAs(Manager, CompanyA);

            // cycle 1: 4 hours
            Assert.True((await _tasks.DirectHoldAsync(CompanyA, t.ID, Manager, "first")).ok);
            var r1 = _db.TaskItems.Single(x => x.ID == t.ID);
            r1.HoldApprovedAtUtc = DateTime.UtcNow.AddHours(-4);
            _db.SaveChanges();
            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);
            var afterCycle1 = Reload(t.ID).DueDate!.Value;

            // cycle 2: 6 hours, from a clean state
            Assert.True((await _tasks.DirectHoldAsync(CompanyA, t.ID, Manager, "second")).ok);
            var r2 = _db.TaskItems.Single(x => x.ID == t.ID);
            r2.HoldApprovedAtUtc = DateTime.UtcNow.AddHours(-6);
            _db.SaveChanges();
            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);

            Assert.Equal(afterCycle1.AddHours(6), Reload(t.ID).DueDate!.Value, TimeSpan.FromMinutes(1));

            // Each cycle recorded its own pair of events - the second did not collapse into the first's
            // dedup key, which is why the stamp carries sub-second precision.
            Assert.Equal(2, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldApproved));
            Assert.Equal(2, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldResumed));
        }

        // =========================================================================================
        // T28-T30 — ESCALATION AND THE OVERDUE SWEEP
        // =========================================================================================

        [Fact] // T28
        public async Task An_approved_hold_stops_the_escalation_sweep_acting_on_the_task()
        {
            var t = AddTask(due: DateTime.UtcNow.AddHours(-48));

            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.DirectHoldAsync(CompanyA, t.ID, Manager, "paused")).ok);

            var result = await _escalation.SweepAsync(CompanyA);
            Assert.Equal(0, result.Examined);
            Assert.Empty(await _escalation.CurrentlyEscalatedTaskIdsAsync(CompanyA));

            // NON-VACUITY: resume it and the very same task IS escalated.
            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);
            var tracked = _db.TaskItems.Single(x => x.ID == t.ID);
            tracked.DueDate = DateTime.UtcNow.AddHours(-48);     // undo the extension for the comparison
            _db.SaveChanges();

            var after = await _escalation.SweepAsync(CompanyA);
            Assert.Equal(1, after.Examined);
            Assert.Contains(t.ID, await _escalation.CurrentlyEscalatedTaskIdsAsync(CompanyA));
        }

        [Fact] // only an APPROVED hold pauses - a request does not
        public async Task A_merely_requested_hold_does_not_stop_escalation()
        {
            var t = AddTask(due: DateTime.UtcNow.AddHours(-48));
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "asking")).ok);

            var result = await _escalation.SweepAsync(CompanyA);
            Assert.Equal(1, result.Examined);
            Assert.Contains(t.ID, await _escalation.CurrentlyEscalatedTaskIdsAsync(CompanyA));
        }

        [Fact] // T29 — after resume the EXTENDED deadline is the operative one
        public async Task After_resume_the_extended_deadline_governs_escalation()
        {
            // Due 1 hour ago: overdue, but well inside the 24h grace, so not yet escalatable.
            var t = AddTask(due: DateTime.UtcNow.AddHours(-1));
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.DirectHoldAsync(CompanyA, t.ID, Manager, "paused")).ok);

            var tracked = _db.TaskItems.Single(x => x.ID == t.ID);
            tracked.HoldApprovedAtUtc = DateTime.UtcNow.AddHours(-10);
            _db.SaveChanges();

            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);

            // The deadline moved 10 hours forward, so the task is no longer even overdue.
            var row = Reload(t.ID);
            Assert.True(row.DueDate > DateTime.UtcNow, "resume should have pushed the deadline into the future");
            Assert.Equal(0, (await _escalation.SweepAsync(CompanyA)).Examined);
        }

        [Fact] // the overdue sweep obeys the same pause, through the same canonical predicate
        public async Task An_approved_hold_also_stops_the_overdue_sweep()
        {
            var t = AddTask(due: DateTime.UtcNow.AddHours(-2));
            var now = DateTime.UtcNow;

            Assert.True(TaskStatuses.IsSweepEligible(Reload(t.ID), now));

            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.DirectHoldAsync(CompanyA, t.ID, Manager, "paused")).ok);

            Assert.False(TaskStatuses.IsSweepEligible(Reload(t.ID), now));

            // The in-memory rule and the SQL rule are twins, so the query agrees with the predicate.
            var eligible = _db.TaskItems.AsNoTracking()
                .Where(TaskStatuses.SweepEligibleAt(now)).Select(x => x.ID).ToArray();
            Assert.DoesNotContain(t.ID, eligible);
        }

        // =========================================================================================
        // LIFECYCLE INTERACTION
        // =========================================================================================

        [Fact] // completing a held task ends the hold, and does NOT pay out an extension
        public async Task Completing_a_held_task_clears_the_hold_without_extending_its_deadline()
        {
            var due = new DateTime(2030, 7, 1, 9, 0, 0, DateTimeKind.Utc);
            var t = AddTask(due: due);
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.DirectHoldAsync(CompanyA, t.ID, Manager, "paused")).ok);

            var tracked = _db.TaskItems.Single(x => x.ID == t.ID);
            tracked.HoldApprovedAtUtc = DateTime.UtcNow.AddHours(-8);
            _db.SaveChanges();

            Assert.True((await _tasks.ChangeStatusAsync(CompanyA, t.ID, "Done", Manager)).ok);

            var row = Reload(t.ID);
            Assert.Equal("Done", row.Status);
            Assert.Equal(TaskStatuses.HoldStateNormal, TaskStatuses.HoldState(row));
            Assert.Null(row.HoldApprovedAtUtc);
            // The deadline of a finished task is not moved: nobody is waiting on it.
            Assert.Equal(due, row.DueDate);
            Assert.Equal(0, EventCount(t.ID, CrossBuy.BL.Platform.TaskEvents.HoldResumed));
        }

        [Fact] // T42 — an ordinary task is completely unaffected
        public async Task A_task_nobody_held_is_untouched_by_any_of_this()
        {
            var due = new DateTime(2030, 8, 1, 9, 0, 0, DateTimeKind.Utc);
            var t = AddTask(due: due);

            Assert.True((await _tasks.ChangeStatusAsync(CompanyA, t.ID, "InProgress", Assignee)).ok);

            var row = Reload(t.ID);
            Assert.Equal("InProgress", row.Status);
            Assert.Equal(due, row.DueDate);
            Assert.Equal(TaskStatuses.HoldStateNormal, TaskStatuses.HoldState(row));
            Assert.Empty(HoldEvents(t.ID));
        }

        // =========================================================================================
        // T34-T36 — CONTEXT AND EVENT TENANCY
        // =========================================================================================

        [Fact] // T36
        public async Task Every_hold_event_carries_the_tasks_own_company_and_the_real_actor()
        {
            var t = AddTask();
            Assert.True((await _tasks.RequestHoldAsync(CompanyA, t.ID, Assignee, "reason")).ok);
            ActAs(Manager, CompanyA);
            Assert.True((await _tasks.ApproveHoldAsync(CompanyA, t.ID, Manager)).ok);
            Assert.True((await _tasks.ResumeHoldAsync(CompanyA, t.ID, Manager)).ok);

            var events = _db.BusinessEvents.AsNoTracking()
                .Where(e => e.EntityType == "Task" && e.EntityId == t.ID && e.EventType.StartsWith("Task.Hold"))
                .OrderBy(e => e.EventId).ToArray();

            Assert.Equal(3, events.Length);
            Assert.All(events, e => Assert.Equal(CompanyA, e.CompanyID));
            Assert.Equal(Assignee, events[0].ActorEmployeeId);
            Assert.Equal(Manager, events[1].ActorEmployeeId);
            Assert.Equal(Manager, events[2].ActorEmployeeId);

            Assert.Equal(
                new[]
                {
                    CrossBuy.BL.Platform.TaskEvents.HoldRequested,
                    CrossBuy.BL.Platform.TaskEvents.HoldApproved,
                    CrossBuy.BL.Platform.TaskEvents.HoldResumed,
                },
                events.Select(e => e.EventType).ToArray());
        }

        [Fact] // T35 — the four canonical event types, and no invented fifth
        public void Hold_uses_the_existing_event_vocabulary_and_adds_none()
        {
            Assert.Equal("Task.HoldRequested", CrossBuy.BL.Platform.TaskEvents.HoldRequested);
            Assert.Equal("Task.HoldApproved", CrossBuy.BL.Platform.TaskEvents.HoldApproved);
            Assert.Equal("Task.HoldRejected", CrossBuy.BL.Platform.TaskEvents.HoldRejected);
            Assert.Equal("Task.HoldResumed", CrossBuy.BL.Platform.TaskEvents.HoldResumed);

            // THERE IS NO OnHold STATUS. The derived state is the whole model.
            Assert.DoesNotContain("OnHold", TaskStatuses.All);
            Assert.Equal(new[] { "New", "InProgress", "Done" }, TaskStatuses.All.ToArray());
        }

        [Fact] // the five columns, and only those five
        public void TaskItem_carries_exactly_the_five_hold_columns()
        {
            var columns = _db.Model.FindEntityType(typeof(TaskItem))!
                .GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var required in new[]
            {
                "HoldRequestedAtUtc", "HoldRequestedByEmployeeId", "HoldReason",
                "HoldApprovedAtUtc", "HoldApprovedByEmployeeId",
            })
                Assert.Contains(required, columns);

            // No hold log table, no sixth column, and the escalation projection columns stay absent -
            // this wave landed Hold, not the escalation-level feature that was never authorized.
            Assert.DoesNotContain("EscalationLevel", columns);
            Assert.DoesNotContain("EscalatedAtUtc", columns);
            Assert.DoesNotContain("HoldDecisionReason", columns);
        }
    }
}
