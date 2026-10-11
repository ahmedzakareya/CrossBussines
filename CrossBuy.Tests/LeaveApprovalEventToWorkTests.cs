using System.Text.Json.Nodes;
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
    // HR LEAVE APPROVAL — EVENT TO WORK.
    //
    // The claim: a leave request that becomes durably Pending records one canonical fact per approval
    // LEVEL, each level becomes exactly one Task assigned to the manager the domain already chose, an
    // intermediate approval closes that level and opens the next, and either terminal decision closes
    // the level that took it — with nothing invented and nothing leaked.
    //
    // WHY THE LEVEL IS EVERYWHERE IN THESE TESTS. This is the platform's first multi-level approval.
    // Inventory and project billing each have ONE reviewer, so a test that proved "the request became a
    // task" was enough. Here the request is not the unit of work; the (request, level) pair is. A suite
    // that only ever submitted and approved once could not tell a level-aware implementation from one
    // that collapses the whole chain into a single task — so the chain is walked, at two levels and at
    // three, and the intermediate hand-over is asserted from both ends.
    //
    // COMPANIES ARE 41 AND 77, NEVER 1 — company 1 is the value every historical bug defaulted to.
    // ================================================================================================
    public class LeaveApprovalEventToWorkTests
    {
        [Fact]
        public async Task Reapplying_after_rejection_preserves_old_work_and_opens_a_new_request()
        {
            using var rig = await TwoLevelAsync();
            var first = await SubmitAsync(rig);
            await rig.DispatchAllAsync();
            Assert.True((await rig.DecideAsync(first.ID, Manager1, false, null)).ok);
            await rig.DispatchAllAsync();
            rig.Identity.EmployeeId = Requester;
            var second = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            Assert.NotEqual(first.ID, second.ID);
            var facts = rig.Events_(LeaveRequestEvents.Submitted);
            Assert.Equal(2, facts.Count);
            Assert.Equal(2, facts.Select(e => e.DedupKey).Distinct().Count());
            Assert.Equal(TaskStatuses.Done, Assert.Single(rig.Tasks().Where(t => t.EntityId == first.ID)).Status);
            Assert.Equal(TaskStatuses.New, Assert.Single(rig.Tasks().Where(t => t.EntityId == second.ID)).Status);
        }
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int Requester = 10;
        private const int Manager1 = 20;   // level 1 — the direct manager
        private const int Manager2 = 30;   // level 2 — above them, the unit head
        private const int Outsider = 40;   // same company, nowhere in the chain
        private const int LeaveType = 1;

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = "u" + employeeId,
            Roles = Array.Empty<string>(),
            CorrelationId = Guid.NewGuid(),
        };

        // ==========================================================================================
        // A — SUBMISSION
        // ==========================================================================================

        [Fact]
        public async Task A_submission_records_one_submitted_fact_and_opens_level_one()
        {
            using var rig = await TwoLevelAsync();

            var (ok, error, req) = await rig.Leave.CreateAsync(
                Requester, LeaveType, Day(1), Day(3), "أسباب شخصية");

            Assert.True(ok, error);
            Assert.NotNull(req);

            // exactly one fact, and it is the submission
            var stored = Assert.Single(rig.Events_());
            Assert.Equal(LeaveRequestEvents.Submitted, stored.EventType);
            Assert.Equal(EntityRegistry.LeaveRequest, stored.EntityType);
            Assert.Equal(req!.ID, stored.EntityId);
            Assert.Equal(CompanyA, stored.CompanyID);
            Assert.Equal($"leave:{req.ID}:level:1:Submitted", stored.DedupKey);

            // the payload names the level and the person who owes it
            var p = Payload(stored);
            Assert.Equal(req.ID, p.LeaveRequestId);
            Assert.Equal(Requester, p.RequesterEmployeeId);
            Assert.Equal(1, p.Level);
            Assert.Equal(Manager1, p.ApproverEmployeeId);
            Assert.Null(p.NextLevel);
            Assert.Null(p.NextApproverEmployeeId);
            Assert.Null(p.DecidedByEmployeeId);

            // and the committed state the fact describes really is there
            Assert.Equal(0, Row(rig, req.ID).Status);
            Assert.Equal(1, Row(rig, req.ID).CurrentLevel);
            Assert.Equal(Manager1, Row(rig, req.ID).CurrentApproverEmployeeID);
            Assert.Equal(2, rig.Steps(req.ID).Count);
        }

        [Fact]
        public async Task A_submitted_fact_becomes_one_task_assigned_to_the_level_one_manager()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);

            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(CompanyA, task.CompanyId);
            Assert.Equal(Manager1, task.AssigneeEmployeeId);      // the domain's own answer
            Assert.NotEqual(Requester, task.AssigneeEmployeeId);
            Assert.Null(task.DueDate);                            // no invented SLA
            Assert.Equal(TaskStatuses.New, task.Status);
            Assert.Equal(0, task.CreatedByEmployeeId);            // the system
            Assert.Equal(EntityRegistry.LeaveRequest, task.EntityType);
            Assert.Equal(req.ID, task.EntityId);
            Assert.Equal(LeaveApprovalRule.KeyFor(req.ID, 1), Assert.Single(rig.Ledger()).RuleKey);
        }

        /// A company-breadth task reader can see titles without holding HR entitlement, so the title
        /// carries no name, no leave type, no dates and no reason.
        [Fact]
        public async Task A_task_title_discloses_nothing_about_the_person_or_the_leave()
        {
            using var rig = await TwoLevelAsync();
            await SubmitAsync(rig, reason: "عملية جراحية");

            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            foreach (var secret in new[] { "عملية جراحية", "emp10", "سنوية", "Annual", Day(1).ToString("yyyy") })
            {
                Assert.DoesNotContain(secret, task.Title ?? "");
                Assert.DoesNotContain(secret, task.TitleEn ?? "");
                Assert.DoesNotContain(secret, task.Description ?? "");
            }
            Assert.Equal("Leave request awaiting your approval", task.TitleEn);
        }

        // ==========================================================================================
        // B — TRANSACTIONAL INTEGRITY
        //
        // The defect this slice repairs: the request and its approval steps used to be written by two
        // separate saves, so a failure between them left a request pointing at level 1 with NO step
        // behind it — which DecideAsync then refuses forever, because there is genuinely nothing to
        // decide. State, steps and fact now share one fate.
        // ==========================================================================================

        [Fact]
        public async Task B_a_submission_that_cannot_record_its_fact_commits_nothing()
        {
            using var rig = await TwoLevelAsync(recorder: new ThrowingEvents());

            await Assert.ThrowsAnyAsync<Exception>(() =>
                rig.Leave.CreateAsync(Requester, LeaveType, Day(1), Day(3), null));

            Assert.Empty(rig.Host.Seed.LeaveRequests.AsNoTracking().ToList());
            Assert.Empty(rig.Host.Seed.LeaveApprovalSteps.AsNoTracking().ToList());
            Assert.Empty(rig.Events_());

            // and nothing to dispatch means no work
            await rig.DispatchAllAsync();
            Assert.Empty(rig.Tasks());
        }

        [Fact]
        public async Task B_a_decision_that_cannot_record_its_fact_leaves_the_request_pending()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);

            rig.Explode = true;
            await Assert.ThrowsAnyAsync<Exception>(() => rig.DecideAsync(req.ID, Manager1, true, "ok"));
            rig.Explode = false;

            var row = Row(rig, req.ID);
            Assert.Equal(0, row.Status);                       // still Pending
            Assert.Equal(1, row.CurrentLevel);                 // still level 1
            Assert.Equal(Manager1, row.CurrentApproverEmployeeID);
            Assert.All(rig.Steps(req.ID), s => Assert.Equal(0, s.Status));
            Assert.Single(rig.Events_());                      // only the submission survives
        }

        // ==========================================================================================
        // C — INTERMEDIATE APPROVAL: the hand-over
        // ==========================================================================================

        [Fact]
        public async Task C_an_intermediate_approval_records_advanced_naming_both_levels()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);

            var (ok, error) = await rig.DecideAsync(req.ID, Manager1, approve: true, "ok");
            Assert.True(ok, error);

            var advanced = Assert.Single(rig.Events_(LeaveRequestEvents.Advanced));
            Assert.Equal($"leave:{req.ID}:level:1:Advanced", advanced.DedupKey);

            var p = Payload(advanced);
            Assert.Equal(1, p.Level);                        // the level that decided
            Assert.Equal(Manager1, p.DecidedByEmployeeId);
            Assert.Equal(2, p.NextLevel);                    // the level that now owes
            Assert.Equal(Manager2, p.NextApproverEmployeeId);

            // the request is still Pending, and has genuinely moved
            var row = Row(rig, req.ID);
            Assert.Equal(0, row.Status);
            Assert.Equal(2, row.CurrentLevel);
            Assert.Equal(Manager2, row.CurrentApproverEmployeeID);
        }

        [Fact]
        public async Task C_the_advance_closes_level_one_and_opens_level_two()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            await rig.DecideAsync(req.ID, Manager1, approve: true, "ok");
            await rig.DispatchAllAsync();

            var tasks = rig.Tasks();
            Assert.Equal(2, tasks.Count);

            var l1 = tasks.Single(t => t.AssigneeEmployeeId == Manager1);
            var l2 = tasks.Single(t => t.AssigneeEmployeeId == Manager2);

            Assert.Equal(TaskStatuses.Done, l1.Status);      // the manager who decided is finished
            Assert.Equal(TaskStatuses.New, l2.Status);       // the next one is not
            Assert.Null(l2.DueDate);
            Assert.Equal(req.ID, l2.EntityId);

            // two ledger rows, one per level, and they are DIFFERENT keys
            var keys = rig.Ledger().Select(l => l.RuleKey).OrderBy(k => k).ToList();
            Assert.Equal(new[] { LeaveApprovalRule.KeyFor(req.ID, 1), LeaveApprovalRule.KeyFor(req.ID, 2) },
                keys);
        }

        /// Three levels, walked end to end. Two levels can pass by accident when a rule ignores the
        /// level entirely; three cannot.
        [Fact]
        public async Task C_a_three_level_chain_hands_over_twice_and_ends_once()
        {
            using var rig = await ThreeLevelAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            await rig.DecideAsync(req.ID, Manager1, true, null);
            await rig.DispatchAllAsync();
            await rig.DecideAsync(req.ID, Manager2, true, null);
            await rig.DispatchAllAsync();
            await rig.DecideAsync(req.ID, Outsider, true, null);   // Outsider is level 3 here
            await rig.DispatchAllAsync();

            Assert.Equal(3, rig.Tasks().Count);
            Assert.All(rig.Tasks(), t => Assert.Equal(TaskStatuses.Done, t.Status));
            Assert.Equal(new[] { Manager1, Manager2, Outsider },
                rig.Tasks().OrderBy(t => t.ID).Select(t => t.AssigneeEmployeeId).ToArray());

            Assert.Equal(2, rig.Events_(LeaveRequestEvents.Advanced).Count);
            Assert.Single(rig.Events_(LeaveRequestEvents.Approved));
            Assert.Equal(1, Row(rig, req.ID).Status);
        }

        // ==========================================================================================
        // D — FINAL APPROVAL
        // ==========================================================================================

        [Fact]
        public async Task D_the_final_approval_closes_its_level_and_opens_nothing()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();
            await rig.DecideAsync(req.ID, Manager1, true, null);
            await rig.DispatchAllAsync();

            var (ok, error) = await rig.DecideAsync(req.ID, Manager2, approve: true, "fine");
            Assert.True(ok, error);
            await rig.DispatchAllAsync();

            var approved = Assert.Single(rig.Events_(LeaveRequestEvents.Approved));
            Assert.Equal($"leave:{req.ID}:level:2:Approved", approved.DedupKey);
            var p = Payload(approved);
            Assert.Equal(2, p.Level);
            Assert.Equal(Manager2, p.DecidedByEmployeeId);
            Assert.Null(p.NextLevel);
            Assert.Null(p.NextApproverEmployeeId);

            Assert.Equal(2, rig.Tasks().Count);              // no third task
            Assert.All(rig.Tasks(), t => Assert.Equal(TaskStatuses.Done, t.Status));
            Assert.Equal(1, Row(rig, req.ID).Status);
        }

        // ==========================================================================================
        // E — REJECTION
        // ==========================================================================================

        [Fact]
        public async Task E_a_rejection_closes_its_level_and_opens_no_next_level()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            var (ok, error) = await rig.DecideAsync(req.ID, Manager1, approve: false, "لا");
            Assert.True(ok, error);
            await rig.DispatchAllAsync();

            var rejected = Assert.Single(rig.Events_(LeaveRequestEvents.Rejected));
            Assert.Equal($"leave:{req.ID}:level:1:Rejected", rejected.DedupKey);
            Assert.Equal(1, Payload(rejected).Level);
            Assert.Empty(rig.Events_(LeaveRequestEvents.Advanced));

            // the level-1 task is finished; level 2 was never opened, because the request is over
            var task = Assert.Single(rig.Tasks());
            Assert.Equal(Manager1, task.AssigneeEmployeeId);
            Assert.Equal(TaskStatuses.Done, task.Status);
            Assert.Equal(2, Row(rig, req.ID).Status);
        }

        /// A rejection at level 2 ends the request just as completely, and closes level 2's task —
        /// not level 1's, which was legitimately finished earlier.
        [Fact]
        public async Task E_a_rejection_at_a_later_level_closes_that_level()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();
            await rig.DecideAsync(req.ID, Manager1, true, null);
            await rig.DispatchAllAsync();

            await rig.DecideAsync(req.ID, Manager2, approve: false, "no");
            await rig.DispatchAllAsync();

            Assert.Equal(2, Payload(Assert.Single(rig.Events_(LeaveRequestEvents.Rejected))).Level);
            Assert.Equal(2, rig.Tasks().Count);
            Assert.All(rig.Tasks(), t => Assert.Equal(TaskStatuses.Done, t.Status));
        }

        // ==========================================================================================
        // F — INVALID AND REPEATED DECISIONS
        // ==========================================================================================

        [Fact]
        public async Task F_a_second_decision_is_refused_and_records_nothing()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();
            await rig.DecideAsync(req.ID, Manager1, true, null);
            await rig.DispatchAllAsync();

            int before = rig.Events_().Count;
            var (ok, error) = await rig.DecideAsync(req.ID, Manager1, approve: true, "again");

            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Equal(before, rig.Events_().Count);
            Assert.Equal(2, rig.Tasks().Count);
        }

        [Fact]
        public async Task F_a_decision_by_someone_who_is_not_the_current_approver_records_nothing()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            // level 2's manager cannot reach past level 1, and neither can a bystander
            foreach (var who in new[] { Manager2, Outsider, Requester })
            {
                var (ok, _) = await rig.DecideAsync(req.ID, who, approve: true, "let me");
                Assert.False(ok);
            }

            Assert.Single(rig.Events_());                    // only the submission
            await rig.DispatchAllAsync();
            Assert.Equal(TaskStatuses.New, Assert.Single(rig.Tasks()).Status);
        }

        // ==========================================================================================
        // G — SEPARATION OF DUTIES AT THE PROJECTION LAYER
        // ==========================================================================================

        /// Defence in depth, and deliberately tested at the rule rather than only at the service: the
        /// chain is built so a requester is never their own approver, and the rule refuses to be the
        /// place where that stops being true.
        [Fact]
        public void G_work_that_would_let_someone_approve_their_own_leave_is_not_created()
        {
            var decision = new LeaveApprovalRule().Decide(Envelope(
                LeaveRequestEvents.Submitted, 5,
                new { level = 1, approverEmployeeId = Requester, requesterEmployeeId = Requester }));

            Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
            Assert.Null(decision.Intent);
        }

        [Fact]
        public void G_a_level_with_no_named_approver_creates_no_work()
        {
            foreach (var approver in new object?[] { null, 0, -1 })
            {
                var decision = new LeaveApprovalRule().Decide(Envelope(
                    LeaveRequestEvents.Submitted, 5,
                    new { level = 1, approverEmployeeId = approver, requesterEmployeeId = Requester }));

                Assert.Equal(TaskProjectionAction.Ignore, decision.Action);
            }
        }

        // ==========================================================================================
        // H — COMPANY SEPARATION
        // ==========================================================================================

        [Fact]
        public async Task H_two_companies_keep_their_own_leave_work()
        {
            using var a = await TwoLevelAsync();
            using var b = await TwoLevelAsync(CompanyB);

            var ra = await SubmitAsync(a);
            var rb = await SubmitAsync(b);
            await a.DispatchAllAsync();
            await b.DispatchAllAsync();

            Assert.Equal(CompanyA, Assert.Single(a.Tasks()).CompanyId);
            Assert.Equal(CompanyB, Assert.Single(b.Tasks()).CompanyId);
            Assert.Equal(CompanyA, Assert.Single(a.Events_()).CompanyID);
            Assert.Equal(CompanyB, Assert.Single(b.Events_()).CompanyID);
            // each company's task points at its OWN request, and at an approver from its own company.
            // The two ids are deliberately NOT compared across rigs: each rig is its own database, so
            // both requests are id 1 and a cross-rig id comparison would prove nothing either way.
            Assert.Equal(ra.ID, Assert.Single(a.Tasks()).EntityId);
            Assert.Equal(rb.ID, Assert.Single(b.Tasks()).EntityId);
            Assert.Equal(Manager1, Assert.Single(a.Tasks()).AssigneeEmployeeId);
            Assert.Equal(Manager1, Assert.Single(b.Tasks()).AssigneeEmployeeId);
        }

        /// The registry arm is the one that cannot filter on its own row — LeaveRequest has no
        /// CompanyID — so tenancy comes from the requester's Employee row. A foreign request must
        /// look exactly like one that never existed.
        [Fact]
        public async Task H_the_registry_resolves_only_within_the_requesters_company()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);

            var registry = new EntityRegistry(rig.Host.Db);

            var mine = await registry.ResolveAsync(EntityRegistry.LeaveRequest, req.ID, Ctx(Manager1, CompanyA));
            Assert.True(mine.Found);
            Assert.Equal("#" + req.ID, mine.Label);

            // the SAME id asked for by the wrong company, and an id that exists nowhere at all
            var foreign = await registry.ResolveAsync(EntityRegistry.LeaveRequest, req.ID, Ctx(Manager1, CompanyB));
            var absent = await registry.ResolveAsync(EntityRegistry.LeaveRequest, 999999, Ctx(Manager1, CompanyB));

            Assert.False(foreign.Found);
            Assert.False(absent.Found);

            // INDISTINGUISHABLE. Both degrade to the same shape - a bare "#id" and no url - so a caller
            // cannot tell "exists in another company" from "never existed". That is the difference
            // between a resolver and a cross-tenant existence oracle, and it is why the labels are
            // compared by SHAPE against their own id rather than against each other.
            Assert.Equal("#" + req.ID, foreign.Label);
            Assert.Equal("#999999", absent.Label);
            Assert.Null(foreign.Url);
            Assert.Null(absent.Url);
        }

        [Fact]
        public void H_the_leave_entity_is_registered_under_the_hr_scope()
        {
            var def = new EntityRegistry(null!).GetDefinition(EntityRegistry.LeaveRequest);

            Assert.Equal(EntityRegistry.ScopeHr, def.PermissionScope);
            Assert.False(def.ListedInRecordPicker);   // nobody browses who asked for time off
            Assert.False(def.SupportsSearch);
        }

        // ==========================================================================================
        // I — TOP-OF-TREE AUTO-APPROVAL
        // ==========================================================================================

        /// RSK-31 — THE SAME EMPTY CHAIN, WITHOUT THE PROOF, IS NOT AN APPROVAL.
        ///
        /// The requester here has no hierarchy at all, so the climb returns nothing and raises no
        /// defect: byte for byte the same answer a real company head produces. That is precisely why
        /// emptiness stopped being evidence. Nothing is created, so there is no request to record a
        /// fact about and no work to project.
        [Fact]
        public async Task I_an_unverified_empty_chain_creates_no_request_no_fact_and_no_task()
        {
            using var rig = await UnverifiedRootAsync();

            var (ok, error, req) = await rig.Leave.CreateAsync(Requester, LeaveType, Day(1), Day(2), null);

            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Null(req);
            Assert.Empty(rig.Host.Seed.LeaveRequests.AsNoTracking().ToList());
            Assert.Empty(rig.Events_());

            await rig.DispatchAllAsync();
            Assert.Empty(rig.Tasks());
        }

        [Fact]
        public async Task I_an_auto_approved_request_records_both_facts_and_creates_no_task()
        {
            using var rig = await TopOfTreeAsync();

            var (ok, error, req) = await rig.Leave.CreateAsync(Requester, LeaveType, Day(1), Day(2), null);
            Assert.True(ok, error);

            // the existing domain behaviour is untouched
            Assert.Equal(1, Row(rig, req!.ID).Status);
            Assert.Equal(0, Row(rig, req.ID).CurrentLevel);
            Assert.Null(Row(rig, req.ID).CurrentApproverEmployeeID);
            Assert.Empty(rig.Steps(req.ID));

            // two facts, both at level 0 — the payload's way of saying nobody was ever asked
            Assert.Equal(2, rig.Events_().Count);
            Assert.Equal(0, Payload(Assert.Single(rig.Events_(LeaveRequestEvents.Submitted))).Level);
            Assert.Equal(0, Payload(Assert.Single(rig.Events_(LeaveRequestEvents.Approved))).Level);

            // and NO phantom work: not an unassigned task, not one raised only to be completed
            await rig.DispatchAllAsync();
            Assert.Empty(rig.Tasks());
            Assert.Empty(rig.Ledger());
        }

        // ==========================================================================================
        // J — IDEMPOTENCY AND REPLAY
        // ==========================================================================================

        [Fact]
        public async Task J_replaying_every_fact_changes_nothing()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();
            await rig.DecideAsync(req.ID, Manager1, true, null);
            await rig.DispatchAllAsync();

            var tasksBefore = rig.Tasks().Select(t => (t.ID, t.Status, t.AssigneeEmployeeId)).ToList();
            var ledgerBefore = rig.Ledger().Count;

            for (var i = 0; i < 3; i++) await rig.DispatchAllAsync();

            Assert.Equal(tasksBefore, rig.Tasks().Select(t => (t.ID, t.Status, t.AssigneeEmployeeId)).ToList());
            Assert.Equal(ledgerBefore, rig.Ledger().Count);
        }

        /// The dedup key is deterministic, so the SAME domain fact recorded twice is one row. This is
        /// also what stops two concurrent decisions on one level from becoming two events and two
        /// next-level tasks.
        [Fact]
        public async Task J_recording_the_same_fact_twice_stores_it_once()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);

            await using (var tx = await ScopedTx.BeginOrJoinAsync(rig.Host.Db))
            {
                await rig.Events.RecordAsync(new BusinessEventRecord
                {
                    EntityCode = EntityRegistry.LeaveRequest,
                    EntityId = req.ID,
                    EventType = LeaveRequestEvents.Submitted,
                    DedupKey = $"leave:{req.ID}:level:1:Submitted",
                    Payload = new LeaveRequestEventPayload { LeaveRequestId = req.ID, Level = 1 },
                });
                await tx.CommitAsync();
            }

            Assert.Single(rig.Events_(LeaveRequestEvents.Submitted));
        }

        // ==========================================================================================
        // K — MANAGER CHANGE MID-CHAIN
        //
        // The chain is resolved and STORED when the request is created. Moving the org tree afterwards
        // must not silently re-point work that a named person was already asked to do.
        // ==========================================================================================

        [Fact]
        public async Task K_moving_the_org_tree_does_not_reassign_work_already_asked_for()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            // the org tree changes AFTER submission: level 1 now reports to somebody else entirely
            var link = rig.Host.Seed.Hierarchicals.First(h => h.H_ID == 200 + Requester);
            link.H_Parent = 100 + Outsider;
            await rig.Host.Seed.SaveChangesAsync();

            await rig.DecideAsync(req.ID, Manager1, true, null);
            await rig.DispatchAllAsync();

            // level 2 follows the PERSISTED chain, not today's hierarchy
            Assert.Equal(Manager2, Row(rig, req.ID).CurrentApproverEmployeeID);
            var l2 = rig.Tasks().Single(t => t.Status == TaskStatuses.New);
            Assert.Equal(Manager2, l2.AssigneeEmployeeId);
            Assert.NotEqual(Outsider, l2.AssigneeEmployeeId);

            // and the level-1 task still belongs to the person who was actually asked
            Assert.Equal(Manager1, rig.Tasks().Single(t => t.Status == TaskStatuses.Done).AssigneeEmployeeId);
        }

        // ==========================================================================================
        // L — THE RULE'S OWN DISPATCH GUARDS
        // ==========================================================================================

        [Theory]
        [InlineData(EntityRegistry.SalesInvoice)]
        [InlineData(EntityRegistry.InventoryApproval)]
        [InlineData(EntityRegistry.ProjectBilling)]
        [InlineData(EntityRegistry.Employee)]
        public void L_the_rule_answers_only_for_the_leave_request_entity(string entityCode)
        {
            object payload = new { level = 1, nextLevel = 2, approverEmployeeId = Manager1,
                nextApproverEmployeeId = Manager2, requesterEmployeeId = Requester };

            // EACH RULE IS ASKED ON THE EVENT IT ACTUALLY ANSWERS. Handing the close rule a Submitted
            // envelope would prove nothing about its entity guard: it refuses Submitted on the event
            // type first, so the assertion would pass whether or not the entity was ever checked.
            Assert.Equal(TaskProjectionAction.Ignore,
                new LeaveApprovalRule().Decide(Envelope(LeaveRequestEvents.Submitted, 5, payload, entityCode)).Action);
            Assert.Equal(TaskProjectionAction.Ignore,
                new LeaveApprovalRule().Decide(Envelope(LeaveRequestEvents.Advanced, 5, payload, entityCode)).Action);
            Assert.Equal(TaskProjectionAction.Ignore,
                new LeaveApprovalRule().Decide(Envelope(LeaveRequestEvents.Approved, 5, payload, entityCode)).Action);
            Assert.Equal(TaskProjectionAction.Ignore,
                new LeaveApprovalAdvanceCloseRule().Decide(Envelope(LeaveRequestEvents.Advanced, 5, payload, entityCode)).Action);
        }

        [Theory]
        [InlineData("LeaveRequest.Cancelled")]
        [InlineData("LeaveRequest.Withdrawn")]
        [InlineData("LeaveRequest.Escalated")]
        [InlineData("Something.Else")]
        public void L_an_event_type_the_rule_does_not_know_is_inert(string unknown)
        {
            var envelope = Envelope(unknown, 5,
                new { level = 1, approverEmployeeId = Manager1, requesterEmployeeId = Requester });

            var d = new LeaveApprovalRule().Decide(envelope);
            Assert.Equal(TaskProjectionAction.Ignore, d.Action);
            Assert.Null(d.CompleteRuleKeyPrefix);
            Assert.Equal(TaskProjectionAction.Ignore, new LeaveApprovalAdvanceCloseRule().Decide(envelope).Action);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void L_an_envelope_with_no_identity_becomes_no_work(int noIdentity)
        {
            var envelope = Envelope(LeaveRequestEvents.Submitted, noIdentity,
                new { level = 1, approverEmployeeId = Manager1, requesterEmployeeId = Requester });

            Assert.Equal(TaskProjectionAction.Ignore, new LeaveApprovalRule().Decide(envelope).Action);
        }

        /// A payload missing the fields the rule needs must degrade to Ignore, never throw: a throwing
        /// rule fails its dispatch row forever and retries it for ever after.
        [Fact]
        public void L_a_malformed_payload_degrades_rather_than_throwing()
        {
            var rule = new LeaveApprovalRule();
            foreach (object p in new object[]
            {
                new { },
                new { level = "one", approverEmployeeId = Manager1 },
                new { level = 1 },
            })
            {
                Assert.Equal(TaskProjectionAction.Ignore,
                    rule.Decide(Envelope(LeaveRequestEvents.Submitted, 5, p)).Action);
            }
        }

        [Fact]
        public void L_the_projection_key_names_the_rule_the_entity_the_request_and_the_level()
        {
            Assert.Equal("TaskProjection:LeaveApproval:LeaveRequest:12:level:1:",
                LeaveApprovalRule.KeyFor(12, 1));

            // a level-1 prefix must not reach level 11, and request 5 must not reach request 50
            Assert.DoesNotContain(LeaveApprovalRule.KeyFor(12, 1), LeaveApprovalRule.KeyFor(12, 11));
            Assert.DoesNotContain(LeaveApprovalRule.KeyFor(5, 1), LeaveApprovalRule.KeyFor(50, 1));

            Assert.Equal(DueDatePolicy.None, new LeaveApprovalRule().DefaultDueDatePolicy);
            Assert.Equal(DueDatePolicy.None, new LeaveApprovalAdvanceCloseRule().DefaultDueDatePolicy);
        }

        [Fact]
        public void L_the_leave_event_vocabulary_is_exactly_four()
        {
            var names = typeof(LeaveRequestEvents).GetFields()
                .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).OrderBy(x => x).ToArray();

            Assert.Equal(
                new[] { "LeaveRequest.Advanced", "LeaveRequest.Approved", "LeaveRequest.Rejected", "LeaveRequest.Submitted" },
                names);
            Assert.All(names, n => BusinessEventTypes.Validate(n, EntityRegistry.LeaveRequest));
        }

        // ==========================================================================================
        // M — NOTIFICATION PARITY
        //
        // This slice is Business Event → Task work, NOT a notification migration. The existing inline
        // notifications keep their recipients, their count and their type strings.
        // ==========================================================================================

        [Fact]
        public async Task M_the_existing_notifications_are_unchanged_and_not_duplicated()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);

            Assert.Equal(new[] { (Manager1, "leave_submitted") }, rig.Notifications.Sent.ToArray());

            await rig.DecideAsync(req.ID, Manager1, true, null);
            Assert.Equal(
                new[] { (Manager1, "leave_submitted"), (Manager2, "leave_submitted"), (Requester, "leave_progress") },
                rig.Notifications.Sent.ToArray());

            await rig.DecideAsync(req.ID, Manager2, true, null);
            Assert.Equal((Requester, "leave_approved"), rig.Notifications.Sent.Last());

            // dispatching the facts into work sends nothing further
            int before = rig.Notifications.Sent.Count;
            await rig.DispatchAllAsync();
            Assert.Equal(before, rig.Notifications.Sent.Count);
        }

        [Fact]
        public async Task M_a_rejection_notifies_the_requester_exactly_once()
        {
            using var rig = await TwoLevelAsync();
            var req = await SubmitAsync(rig);
            rig.Notifications.Sent.Clear();

            await rig.DecideAsync(req.ID, Manager1, approve: false, "no");
            await rig.DispatchAllAsync();

            Assert.Equal(new[] { (Requester, "leave_rejected") }, rig.Notifications.Sent.ToArray());
        }

        // ==========================================================================================
        // N — WIRING
        // ==========================================================================================

        [Fact]
        public void N_both_halves_of_the_leave_rule_are_registered_and_the_others_are_intact()
        {
            var text = System.IO.File.ReadAllText(RepoFile("CrossBuy/Program.cs"));

            Assert.Contains("ITaskOrchestrationRule, CrossBuy.BL.Platform.Orchestration.LeaveApprovalRule>", text);
            Assert.Contains("ITaskOrchestrationRule, CrossBuy.BL.Platform.Orchestration.LeaveApprovalAdvanceCloseRule>", text);
            // Preserve the actual destination baseline's rules; unrelated donor rules are not imported.
            foreach (var existing in new[] { "InventoryApprovalRule", "ProjectMilestoneOrchestrationRule", "DocumentVerificationRule" })
                Assert.Contains(existing + ">", text);
        }

        /// The recorder is an optional constructor parameter (RSK-25's shape), so this pins that the
        /// constructor DI actually selects is the one that carries it.
        [Fact]
        public void N_the_constructor_dependency_injection_resolves_carries_the_event_recorder()
        {
            var greediest = typeof(LeaveWorkflowService).GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length).First();

            Assert.Contains(greediest.GetParameters(), p => p.ParameterType == typeof(IBusinessEventService));
        }

        // ==========================================================================================
        // helpers
        // ==========================================================================================

        private static DateTime Day(int d) => new DateTime(2026, 11, d, 0, 0, 0, DateTimeKind.Utc);

        private static LeaveRequest Row(Rig rig, int id) =>
            rig.Host.Seed.LeaveRequests.AsNoTracking().First(r => r.ID == id);

        private static LeaveRequestEventPayload Payload(BusinessEvent e) =>
            System.Text.Json.JsonSerializer.Deserialize<LeaveRequestEventPayload>(
                e.Payload!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        private static string RepoFile(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, relative)))
                dir = System.IO.Directory.GetParent(dir)?.FullName;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir!, relative);
        }

        private static async Task<LeaveRequest> SubmitAsync(Rig rig, string? reason = null)
        {
            var (ok, error, req) = await rig.Leave.CreateAsync(Requester, LeaveType, Day(1), Day(3), reason);
            Assert.True(ok, error);
            return req!;
        }

        /// The same envelope the dispatch worker builds, assembled by hand so a shape the producer
        /// would never emit can still be put in front of the rule.
        private static BusinessEventEnvelope Envelope(
            string eventType, int entityId, object payload, string? entityCode = null)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions
                { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

            return new BusinessEventEnvelope
            {
                EventUid = Guid.NewGuid(),
                EventType = eventType,
                Entity = new BusinessEventEntity { Code = entityCode ?? EntityRegistry.LeaveRequest, Id = entityId },
                Actor = new BusinessEventActor { EmployeeId = null },
                Context = new BusinessEventContext { CompanyId = CompanyA },
                OccurredAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                PayloadVersion = LeaveRequestEventPayload.Version,
                Visibility = BusinessEventVisibility.Internal,
                Payload = JsonNode.Parse(json),
            };
        }

        // ---- RSK-31 structural ratchet -------------------------------------------------------------

        /// THE ONE REGRESSION THE BEHAVIOURAL TESTS ABOVE CANNOT SEE COMING.
        ///
        /// Everything else in this file proves what the services DO today. This proves what the next
        /// person cannot quietly write: reaching a request's status with an empty chain and no proof of
        /// a root. That is the exact shape RSK-31 was about, and it is the exact shape someone
        /// simplifying an awkward-looking condition would restore, because the restored version reads
        /// cleaner and its failure is invisible - a broken org tree silently approving its own leave
        /// looks, from every screen, like a working system.
        ///
        /// WHAT IS ASSERTED IS DOMINANCE, NOT PURITY, and the distinction is the whole design of this
        /// test. Both services legitimately test `chain.Count == 0` a SECOND time, further down, to set
        /// Status/CurrentLevel once the request is being built. That branch does not decide policy: the
        /// refusal above has already returned for every unverified empty chain, so by the time control
        /// reaches it, an empty chain can only mean a proven root. Demanding the root condition on every
        /// occurrence would therefore fail on correct code - so what is required instead is that the
        /// FIRST empty-chain test in the file is the refusal, and that it refuses.
        ///
        /// That formulation is what makes the ratchet bite. Delete the guard and the status branch
        /// becomes first, carrying no root condition: fail. Weaken it to `HierarchyDefect` alone: fail.
        /// Insert a new approval path ABOVE the guard, which is the only place an unguarded one could do
        /// harm: fail. Leave the dominated branch alone, as correct code does: pass.
        ///
        /// BOTH SERVICES, ONE ASSERTION, deliberately. They share the resolver, so a root policy true of
        /// Leave alone would be worse than none: the defect would read as closed while EmployeeRequest
        /// still approved an unverifiable tree. Comments are stripped first - the prose beside these
        /// guards discusses `chain.Count == 0` at length and must not be read as if it were code.
        [Fact]
        public void An_empty_approver_chain_is_never_by_itself_grounds_for_approval()
        {
            foreach (var service in new[] { "LeaveWorkflowService.cs", "EmployeeRequestService.cs" })
            {
                var lines = StripComments(SourceOf("CrossBuy", "BL", service))
                    .Split('\n').Select(l => l.Trim()).ToList();
                var first = lines.FindIndex(l => l.Contains("chain.Count == 0", StringComparison.Ordinal));

                Assert.True(first >= 0, service + " no longer tests for an empty approver chain at all");
                Assert.Contains("RequesterIsVerifiedRoot", lines[first], StringComparison.Ordinal);
                Assert.Contains("HierarchyDefect", lines[first], StringComparison.Ordinal);

                // and it must REFUSE - a guard that merely logged would satisfy every assertion above
                var body = string.Concat(lines.Skip(first).Take(3));
                Assert.Contains("return (false", body, StringComparison.Ordinal);
            }

            // finally, the answer itself is DERIVED, not a constant: a resolver hard-coded to report
            // every requester as a root would satisfy all of the above while changing nothing at all.
            var resolver = StripComments(SourceOf("CrossBuy", "BL", "LeaveWorkflowService.cs"));
            Assert.Contains("IsUnitHead(employeeId)", resolver, StringComparison.Ordinal);

            // and the record's DEFAULT stays the refusing one. Both call sites pass the flag
            // explicitly today, so flipping this default changes no behaviour and no behavioural test
            // can see it - which is precisely why it is worth pinning. It is the value a future third
            // call site would silently inherit, and the safe inheritance is 'not proven a root'.
            Assert.Contains("bool RequesterIsVerifiedRoot = false)", resolver, StringComparison.Ordinal);
        }
        private static string SourceOf(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var path = Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), path);
            var text = File.ReadAllText(path);
            Assert.NotEmpty(text.Trim());
            return text;
        }

        private static string StripComments(string source) =>
            string.Join("\n", source.Split('\n').Select(l =>
            {
                var i = l.IndexOf("//", StringComparison.Ordinal);
                return i >= 0 ? l.Substring(0, i) : l;
            }));

        // ---- rigs ---------------------------------------------------------------------------------

        private static Task<Rig> TwoLevelAsync(int companyId = CompanyA, IBusinessEventService? recorder = null)
            => BuildAsync(companyId, new[] { Manager1, Manager2 }, recorder);

        private static Task<Rig> ThreeLevelAsync()
            => BuildAsync(CompanyA, new[] { Manager1, Manager2, Outsider }, null);

        private static Task<Rig> TopOfTreeAsync()
            => BuildAsync(CompanyA, Array.Empty<int>(), null);

        /// A requester with no hierarchy rows at all: the same empty chain, none of the evidence.
        private static Task<Rig> UnverifiedRootAsync()
            => BuildAsync(CompanyA, Array.Empty<int>(), null, verifiedRoot: false);

        private static async Task<Rig> BuildAsync(int companyId, int[] chain,
            IBusinessEventService? recorder, bool verifiedRoot = true)
        {
            var rig = new Rig(companyId, recorder);
            var db = rig.Host.Seed;

            var everyone = new[] { Requester }.Concat(chain).Distinct().ToList();
            foreach (var id in new[] { Requester, Manager1, Manager2, Outsider })
                db.Employee.Add(Emp(id, companyId));
            db.LeaveTypes.Add(new LeaveTypes { ID = LeaveType, NameAr = "سنوية", NameEn = "Annual" });

            // employee node -> position node -> manager employee node, the exact shape the climb walks
            var ladder = new[] { Requester }.Concat(chain).ToArray();
            for (var i = 0; i < ladder.Length - 1; i++)
            {
                db.Hierarchicals.AddRange(
                    new Hierarchical { H_ID = 100 + ladder[i], H_Type = 5, H_ObjectID = ladder[i], H_Parent = 200 + ladder[i] },
                    new Hierarchical { H_ID = 200 + ladder[i], H_Type = 4, H_ObjectID = null, H_Parent = 100 + ladder[i + 1] });
            }
            // THE TOP OF THE LADDER IS A VERIFIED ROOT (RSK-31), not merely a node with nothing above
            // it. Those two used to be indistinguishable and an empty chain was read as approval; now a
            // root must be PROVEN - the person's position attaches directly to a company node. An
            // unverified parentless node is covered by its own test below, where it is refused.
            if (verifiedRoot)
            {
                db.Hierarchicals.AddRange(
                    new Hierarchical { H_ID = 900, H_Type = 1, H_ObjectID = null, H_Parent = null },
                    new Hierarchical { H_ID = 200 + ladder[^1], H_Type = 4, H_ObjectID = null, H_Parent = 900 },
                    new Hierarchical { H_ID = 100 + ladder[^1], H_Type = 5, H_ObjectID = ladder[^1], H_Parent = 200 + ladder[^1] });
            }

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

        // ---- the harness: every piece production except the notification sink --------------------

        private sealed class Rig : IDisposable
        {
            public PlatformTestHost Host { get; }
            public Sink Notifications { get; } = new();
            public ILeaveWorkflowService Leave { get; }
            public ActingContext Identity { get; private set; } = null!;

            /// A decision, stated as the employee who took it. The service now resolves the caller from
            /// the ambient context, so a test that did not say who was acting would be testing nothing.
            public Task<(bool ok, string? error)> DecideAsync(int requestId, int who, bool approve, string? note)
            {
                Identity.EmployeeId = who;
                return Leave.DecideAsync(requestId, who, approve, note);
            }
            public IBusinessEventService Events { get; }
            public TaskOrchestrationConsumer Consumer { get; }
            public int CompanyId { get; }

            /// Set to make the NEXT recording throw, so a decision that cannot record its fact can be
            /// observed rather than simulated by breaking a database.
            public bool Explode { get => _switch.Explode; set => _switch.Explode = value; }
            private readonly ExplodingEvents _switch;

            public Rig(int companyId, IBusinessEventService? recorder)
            {
                CompanyId = companyId;
                Host = new PlatformTestHost(companyId: companyId);
                // WHO IS ACTING is now part of every decision, so the rig carries a settable identity
                // rather than a frozen one. ActAs is how a test says which employee pressed the button.
                Identity = new ActingContext(companyId, Requester);
                var accessor = Identity;
                var real = new BusinessEventService(
                    Host.Db, new EntityRegistry(Host.Db), accessor,
                    NullLogger<BusinessEventService>.Instance);

                _switch = new ExplodingEvents(real);
                Events = recorder ?? _switch;

                Leave = new LeaveWorkflowService(
                    Host.Db, Notifications, new StubDashboard(),
                    NullLogger<LeaveWorkflowService>.Instance, Events, accessor);

                var tasks = new TaskService(
                    Host.Db, new TaskCalendarEventPublisher(real),
                    new TaskNotificationService(Host.Db, new UatRecordingNotificationService(Host.Db)));
                Consumer = new TaskOrchestrationConsumer(new TaskOrchestrationService(
                    Host.Db,
                    new ITaskOrchestrationRule[] { new LeaveApprovalRule(), new LeaveApprovalAdvanceCloseRule() },
                    tasks,
                    new TaskWorkPolicyResolver(Host.Db, null)));
            }

            public List<BusinessEvent> Events_(string? type = null) => Host.Seed.BusinessEvents
                .AsNoTracking().Where(e => e.CompanyID == CompanyId)
                .Where(e => type == null || e.EventType == type)
                .OrderBy(e => e.EventId).ToList();

            /// EXACTLY what BusinessEventDispatchWorker does with a committed row.
            public async Task DispatchAllAsync()
            {
                foreach (var stored in Events_())
                    await Consumer.HandleAsync(Events.BuildEnvelope(stored));
            }

            public List<TaskItem> Tasks() => Host.Seed.TaskItems
                .AsNoTracking().Where(t => t.CompanyId == CompanyId).OrderBy(t => t.ID).ToList();

            public List<TaskAutoLog> Ledger() => Host.Seed.TaskAutoLogs
                .AsNoTracking().Where(l => l.CompanyId == CompanyId).OrderBy(l => l.ID).ToList();

            public List<LeaveApprovalStep> Steps(int requestId) => Host.Seed.LeaveApprovalSteps
                .AsNoTracking().Where(s => s.LeaveRequestID == requestId).OrderBy(s => s.Level).ToList();

            public void Dispose() => Host.Dispose();
        }

        private sealed class ExplodingEvents : IBusinessEventService
        {
            public IBusinessEventService Inner { get; }
            public bool Explode { get; set; }
            public ExplodingEvents(IBusinessEventService inner) { Inner = inner; }

            public Task<long> RecordAsync(BusinessEventRecord record, CancellationToken cancellationToken = default)
                => Explode
                    ? throw new InvalidOperationException("the event recorder failed")
                    : Inner.RecordAsync(record, cancellationToken);

            public BusinessEventEnvelope BuildEnvelope(BusinessEvent stored) => Inner.BuildEnvelope(stored);
        }

        private sealed class ThrowingEvents : IBusinessEventService
        {
            public Task<long> RecordAsync(BusinessEventRecord record, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("the event recorder failed");

            public BusinessEventEnvelope BuildEnvelope(BusinessEvent stored)
                => throw new InvalidOperationException("nothing was recorded, so nothing can be dispatched");
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

        /// The caller identity the service authorizes against. Settable, because a chain is decided by
        /// different people at different levels and the tests have to be able to say so.
        internal sealed class ActingContext : IBusinessContextAccessor
        {
            private readonly int _companyId;
            public int EmployeeId { get; set; }
            public ActingContext(int companyId, int employeeId) { _companyId = companyId; EmployeeId = employeeId; }

            public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(Build());
            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<BusinessContext?>(Build());

            private BusinessContext Build() => new()
            {
                CompanyId = _companyId, EmployeeId = EmployeeId, UserId = "u" + EmployeeId,
                Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
            };
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
