using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ================================================================================================
    // RSK-28 — THE EMPLOYEE REQUEST DECISION IS AUTHORIZED AND ATOMIC AT THE WRITE BOUNDARY.
    //
    // WHAT WAS WRONG, measured on the file rather than carried from a report. DecideAsync read the
    // request with NO company predicate — while the pending-approvals reader a few lines above it
    // already filtered on CompanyID, so the WRITE path was less isolated than the READ beside it. Its
    // only authority test was that the `approverEmployeeId` PARAMETER equalled the stored current
    // approver, so a caller supplying somebody else's id decided AS them. And the guards were a read
    // followed by a write with no transaction and no claim, so two deciders could both pass them.
    //
    // WHY THESE CALL THE SERVICE DIRECTLY. PeopleController is the only live caller and it is correct
    // today; the defect was that nothing made a second caller correct. A suite that went through the
    // controller would prove the controller works and say nothing about the boundary.
    //
    // WHAT IS DELIBERATELY NOT ASSERTED AS A RULE: self-approval. Leave refuses a requester deciding
    // their own request; whether an employee request should is an open PRODUCT question, so the current
    // behaviour is DOCUMENTED below rather than changed — settling it inside a security repair would
    // decide it by accident.
    //
    // COMPANIES ARE 41 AND 77, NEVER 1.
    // ================================================================================================
    public class EmployeeRequestDecisionAuthorizationTests
    {
        private const int CompanyA = 41;
        private const int CompanyB = 77;

        private const int Requester = 10;
        private const int Manager1 = 20;
        private const int Manager2 = 30;
        private const int Outsider = 40;

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
            var row = Row(rig, req.ID);
            Assert.Equal(0, row.Status);                       // still pending — level 2 owes now
            Assert.Equal(2, row.CurrentLevel);
            Assert.Equal(Manager2, row.CurrentApproverEmployeeID);
        }

        [Fact]
        public async Task The_authenticated_current_approver_can_reject()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var (ok, error) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: false);

            Assert.True(ok, error);
            var row = Row(rig, req.ID);
            Assert.Equal(2, row.Status);                       // rejected, terminal
            Assert.Null(row.CurrentApproverEmployeeID);
            Assert.Equal(Manager1, row.ApproverEmployeeID);
        }

        [Fact]
        public async Task Reapplying_after_rejection_creates_a_distinct_request_and_submission_fact()
        {
            using var rig = await ReadyAsync();
            var first = await SubmitAsync(rig);
            Assert.True((await rig.DecideAs(Manager1, first.ID, Manager1, approve: false)).ok);
            rig.Identity.EmployeeId = Requester;
            var second = await SubmitAsync(rig);

            Assert.NotEqual(first.ID, second.ID);
            Assert.Equal(2, Row(rig, first.ID).Status);
            Assert.Equal(0, Row(rig, second.ID).Status);
            var facts = rig.Events_(EmployeeRequestEvents.Submitted);
            Assert.Equal(2, facts.Count);
            Assert.Equal(2, facts.Select(e => e.DedupKey).Distinct().Count());
            Assert.Contains(facts, e => e.EntityId == first.ID);
            Assert.Contains(facts, e => e.EntityId == second.ID);
        }

        /// The decider recorded is the AUTHORITATIVE actor, in every column that carries attribution.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_successful_decision_records_the_authoritative_actor(bool approve)
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DecideAs(Manager1, req.ID, Manager1, approve);

            var step = rig.Steps(req.ID).Single(x => x.Level == 1);
            Assert.Equal(Manager1, step.updatedBy);
            Assert.Equal(approve ? 1 : 2, step.Status);
            Assert.NotNull(step.DecisionAt);
            Assert.Equal(Manager1, Row(rig, req.ID).updatedBy);
        }

        // ==========================================================================================
        // THE DEFECT ITSELF — THE PARAMETER IS NOT AUTHORITY
        // ==========================================================================================

        /// Authenticated as somebody else, passing the id of the REAL stored approver. Under the old
        /// code this succeeded: the parameter was compared against CurrentApproverEmployeeID, matched,
        /// and the decision was taken as that person.
        [Fact]
        public async Task A_caller_cannot_decide_by_naming_the_stored_approver_they_are_not()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            foreach (var impersonator in new[] { Manager2, Outsider, Requester })
            {
                var (ok, error) = await rig.DecideAs(impersonator, req.ID, Manager1, approve: true);
                Assert.False(ok);
                Assert.NotNull(error);
            }

            AssertUntouched(rig, req.ID);
        }

        /// The other direction, and the one no other check catches: the caller IS entitled, but names
        /// somebody else. The parameter is what the old code recorded as the decider.
        [Fact]
        public async Task The_real_approver_cannot_decide_as_someone_else()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            foreach (var borrowed in new[] { Manager2, Outsider, Requester })
            {
                var (ok, _) = await rig.DecideAs(Manager1, req.ID, borrowed, approve: true);
                Assert.False(ok);
            }

            AssertUntouched(rig, req.ID);
        }

        [Fact]
        public async Task A_caller_who_is_not_the_stored_current_approver_is_refused()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            foreach (var who in new[] { Manager2, Outsider })
            {
                var (ok, _) = await rig.DecideAs(who, req.ID, who, approve: true);
                Assert.False(ok);
            }

            AssertUntouched(rig, req.ID);
        }

        // ==========================================================================================
        // IDENTITY MUST EXIST AND BE USABLE
        // ==========================================================================================

        /// RSK-28 proved this at RUNTIME, because the identity was an optional constructor
        /// parameter and a service could be built without one. Phase B removed that possibility
        /// entirely: there is exactly ONE constructor and every dependency is mandatory, so a
        /// service with no caller identity and no event recorder is now UNCONSTRUCTIBLE rather than
        /// merely refused. The guarantee got stronger, so the test asserts the stronger thing - the
        /// shape of the type - instead of a runtime refusal that can no longer be reached.
        [Fact]
        public void The_service_cannot_be_built_without_an_identity_or_an_event_recorder()
        {
            var ctors = typeof(EmployeeRequestService).GetConstructors();
            var only = Assert.Single(ctors);

            Assert.Contains(only.GetParameters(), p => p.ParameterType == typeof(IBusinessContextAccessor));
            Assert.Contains(only.GetParameters(), p => p.ParameterType == typeof(IBusinessEventService));
            Assert.DoesNotContain(only.GetParameters(), p => p.HasDefaultValue);

            // and no nullable escape hatch behind the constructor either
            var code = System.IO.File.ReadAllText(SourcePath("CrossBuy", "BL", "EmployeeRequestService.cs"));
            Assert.DoesNotContain("IBusinessEventService? ", code);
            Assert.DoesNotContain("_events?", code);
            Assert.DoesNotContain("_events != null", code);
            Assert.DoesNotContain("_events is null", code);
        }

        [Fact]
        public async Task A_context_carrying_no_company_authorizes_nothing()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, true, companyOverride: 0);

            Assert.False(ok);
            AssertUntouched(rig, req.ID);
        }

        [Fact]
        public async Task A_context_carrying_no_employee_authorizes_nothing()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            // the request names 0 as its approver too, so only the identity guard can refuse this
            var row = await rig.Host.Db.EmployeeRequests.FirstAsync(r => r.ID == req.ID);
            row.CurrentApproverEmployeeID = 0;
            await rig.Host.Db.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(0, req.ID, 0, approve: true);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
        }

        // ==========================================================================================
        // TENANCY — the request's OWN CompanyID is the boundary
        // ==========================================================================================

        [Fact]
        public async Task A_request_belonging_to_another_company_is_refused()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            // the caller IS the real stored current approver — only the company differs
            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, true, companyOverride: CompanyB);

            Assert.False(ok);
            AssertUntouched(rig, req.ID);
        }

        // ==========================================================================================
        // STEP INTEGRITY
        // ==========================================================================================

        [Fact]
        public async Task A_request_whose_current_step_is_missing_fails_closed()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var step = await rig.Host.Db.EmployeeRequestSteps.FirstAsync(s => s.EmployeeRequestID == req.ID && s.Level == 1);
            rig.Host.Db.EmployeeRequestSteps.Remove(step);
            await rig.Host.Db.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: true);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
        }

        /// The claim re-states the approver, so a step whose approver disagrees with the request cannot
        /// be claimed even though the request-level check passed.
        [Fact]
        public async Task A_step_whose_approver_disagrees_with_the_request_is_not_claimable()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var step = await rig.Host.Db.EmployeeRequestSteps.FirstAsync(s => s.EmployeeRequestID == req.ID && s.Level == 1);
            step.ApproverEmployeeID = Outsider;
            await rig.Host.Db.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: true);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
        }

        /// The mirror of the step-disagreement test, and the one that proves the REQUEST-level check is
        /// not redundant with the claim: here the step still names the caller, so the claim alone would
        /// happily succeed - but the request has moved on and no longer names them. Both must agree.
        [Fact]
        public async Task A_request_whose_own_approver_disagrees_with_its_step_is_refused()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var row = await rig.Host.Db.EmployeeRequests.FirstAsync(r => r.ID == req.ID);
            row.CurrentApproverEmployeeID = Outsider;      // the step still names Manager1
            await rig.Host.Db.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: true);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
            Assert.All(rig.Steps(req.ID), x => Assert.Equal(0, x.Status));
        }

        /// THE CLAIM, PROVEN AGAINST A STALE READ - which is the shape a lost race actually takes.
        ///
        /// The service holds the steps from when the request was created, so its in-memory lookup still
        /// sees a PENDING step after somebody else has decided it in the database. Every guard above the
        /// claim therefore passes on stale data, exactly as two concurrent deciders would. Only the
        /// conditional update sees the truth: it re-states the pending status, matches nothing, and the
        /// decision is refused. Without that status guard this caller would write a second decision over
        /// the first one's.
        [Fact]
        public async Task A_decision_lost_to_a_concurrent_writer_is_refused_by_the_claim()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            // a DIFFERENT context on the same database - an independent scope, as a real competitor is
            await using (var other = rig.Host.NewContext())
            {
                var theirs = await other.EmployeeRequestSteps
                    .FirstAsync(x => x.EmployeeRequestID == req.ID && x.Level == 1);
                theirs.Status = 1;
                theirs.updatedBy = Manager1;
                await other.SaveChangesAsync();
            }

            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: true);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);          // the request never moved
            Assert.Empty(rig.Notifications.Sent.Skip(1));      // only the submission notification
        }

        /// Two deciders, two independent scopes, competing for the same level. Exactly one wins - and
        /// the losing one leaves nothing behind.
        [Fact]
        public async Task Only_one_of_two_competing_deciders_wins()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            await using var secondCtx = rig.Host.NewContext();
            var secondIdentity = new Acting(CompanyA, Manager1);
            // a fully independent service: its own context, its own identity and its own recorder, so
            // the competitor is a real second scope rather than a second handle on the same state
            var second = new EmployeeRequestService(
                secondCtx, rig.Notifications,
                new LeaveWorkflowService(secondCtx, rig.Notifications, new StubDashboard(),
                    NullLogger<LeaveWorkflowService>.Instance),
                secondIdentity,
                new BusinessEventService(secondCtx, new EntityRegistry(secondCtx), secondIdentity,
                    NullLogger<BusinessEventService>.Instance));

            rig.Identity.EmployeeId = Manager1;
            var results = await Task.WhenAll(
                rig.Requests.DecideAsync(req.ID, Manager1, approve: true, note: "a"),
                second.DecideAsync(req.ID, Manager1, approve: false, note: "b"));

            Assert.Equal(1, results.Count(r => r.ok));

            // and the persisted outcome belongs to the winner alone: one terminal-or-advanced state,
            // never a rejection written over an approval
            var row = Row(rig, req.ID);
            Assert.Single(rig.Steps(req.ID), x => x.Level == 1 && x.Status != 0);
            Assert.True(row.Status == 0 || row.Status == 2);
        }

        /// A zero company authorizes nothing even when the request carries zero too, so the guard is not
        /// merely shadowed by the tenant comparison.
        [Fact]
        public async Task A_zero_company_identity_cannot_decide_even_when_the_request_also_carries_zero()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var row = await rig.Host.Db.EmployeeRequests.FirstAsync(r => r.ID == req.ID);
            row.CompanyID = 0;
            await rig.Host.Db.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(Manager1, req.ID, Manager1, true, companyOverride: 0);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
        }

        /// And a zero employee authorizes nothing even when every row names zero as the approver.
        [Fact]
        public async Task A_zero_employee_identity_cannot_decide_even_when_every_row_names_zero()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var row = await rig.Host.Db.EmployeeRequests.FirstAsync(r => r.ID == req.ID);
            row.CurrentApproverEmployeeID = 0;
            var step = await rig.Host.Db.EmployeeRequestSteps
                .FirstAsync(x => x.EmployeeRequestID == req.ID && x.Level == 1);
            step.ApproverEmployeeID = 0;
            await rig.Host.Db.SaveChangesAsync();

            var (ok, _) = await rig.DecideAs(0, req.ID, 0, approve: true);

            Assert.False(ok);
            Assert.Equal(0, Row(rig, req.ID).Status);
        }

        // ==========================================================================================
        // THE STATE MACHINE IS TERMINAL, IN BOTH DIRECTIONS
        // ==========================================================================================

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task A_decided_request_cannot_be_decided_again(bool first, bool second)
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            // drive to a terminal state: reject at level 1, or approve through both levels
            if (!first)
            {
                Assert.True((await rig.DecideAs(Manager1, req.ID, Manager1, false)).ok);
            }
            else
            {
                Assert.True((await rig.DecideAs(Manager1, req.ID, Manager1, true)).ok);
                Assert.True((await rig.DecideAs(Manager2, req.ID, Manager2, true)).ok);
            }

            int terminal = Row(rig, req.ID).Status;
            int notifications = rig.Notifications.Sent.Count;

            foreach (var who in new[] { Manager1, Manager2 })
            {
                var (ok, _) = await rig.DecideAs(who, req.ID, who, second);
                Assert.False(ok);
            }

            Assert.Equal(terminal, Row(rig, req.ID).Status);
            Assert.Equal(notifications, rig.Notifications.Sent.Count);
        }

        [Fact]
        public async Task An_intermediate_approval_advances_exactly_one_level_to_the_persisted_approver()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            await rig.DecideAs(Manager1, req.ID, Manager1, approve: true);

            var row = Row(rig, req.ID);
            Assert.Equal(2, row.CurrentLevel);
            Assert.Equal(Manager2, row.CurrentApproverEmployeeID);   // the STORED next step, not a re-derived manager
            Assert.Equal(0, row.Status);
            Assert.Equal(1, rig.Steps(req.ID).Single(s => s.Level == 1).Status);
            Assert.Equal(0, rig.Steps(req.ID).Single(s => s.Level == 2).Status);
        }

        [Fact]
        public async Task The_final_approval_terminates_the_request()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DecideAs(Manager1, req.ID, Manager1, true);

            var (ok, error) = await rig.DecideAs(Manager2, req.ID, Manager2, approve: true);

            Assert.True(ok, error);
            var row = Row(rig, req.ID);
            Assert.Equal(1, row.Status);
            Assert.Equal(0, row.CurrentLevel);
            Assert.Null(row.CurrentApproverEmployeeID);
            Assert.Equal(Manager2, row.ApproverEmployeeID);
        }

        // ==========================================================================================
        // FAILURE INERTNESS — the whole observable surface, asserted together
        // ==========================================================================================

        [Fact]
        public async Task An_unauthorized_decision_changes_nothing_observable()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var before = Snapshot(rig, req.ID);
            int notifications = rig.Notifications.Sent.Count;
            int events = rig.Host.Seed.BusinessEvents.AsNoTracking().Count();
            int tasks = rig.Host.Seed.TaskItems.AsNoTracking().Count();

            var (ok, _) = await rig.DecideAs(Outsider, req.ID, Manager1, approve: true, note: "let me in");
            Assert.False(ok);

            Assert.Equal(before, Snapshot(rig, req.ID));
            Assert.Equal(notifications, rig.Notifications.Sent.Count);
            Assert.Equal(events, rig.Host.Seed.BusinessEvents.AsNoTracking().Count());
            Assert.Equal(tasks, rig.Host.Seed.TaskItems.AsNoTracking().Count());
        }

        /// SUPERSEDED BY PHASE B, ON PURPOSE. At RSK-28 this asserted that a successful decision records
        /// NO Business Event, because event-to-work did not exist yet and claiming it did would have been
        /// false. Phase B is exactly the change that makes these facts reach the platform, so the
        /// assertion is inverted rather than deleted - the behaviour changed by design, not to make a
        /// suite green, and the old expectation is recorded here so the flip is legible.
        [Fact]
        public async Task A_successful_decision_now_records_its_fact()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            Assert.True((await rig.DecideAs(Manager1, req.ID, Manager1, true)).ok);

            Assert.Equal(
                new[] { EmployeeRequestEvents.Submitted, EmployeeRequestEvents.Advanced },
                rig.Events_().Select(e => e.EventType).ToArray());
        }

        // ==========================================================================================
        // PHASE B — THE AUTHORITATIVE PRODUCER
        //
        // Everything below proves the FACT reaches the platform and becomes work. The rules consuming
        // these events are Phase A's, unmodified: the rig wires the real EmployeeRequestApprovalRule
        // and its advance-close counterpart, so a producer that emitted a plausible-looking event with
        // the wrong level would create the wrong Task here rather than pass quietly.
        // ==========================================================================================

        [Fact]
        public async Task A_submission_records_one_submitted_fact_naming_its_level_and_approver()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var stored = Assert.Single(rig.Events_());
            Assert.Equal(EmployeeRequestEvents.Submitted, stored.EventType);
            Assert.Equal(EntityRegistry.EmployeeRequest, stored.EntityType);
            Assert.Equal(req.ID, stored.EntityId);
            Assert.Equal(CompanyA, stored.CompanyID);
            Assert.Equal($"employee-request:{req.ID}:level:1:Submitted", stored.DedupKey);

            var p = Payload(stored);
            Assert.Equal(req.ID, p.EmployeeRequestId);
            Assert.Equal(Requester, p.RequesterEmployeeId);
            Assert.Equal("Letter", p.RequestType);
            Assert.Equal(1, p.Level);
            Assert.Equal(Manager1, p.ApproverEmployeeId);
            Assert.Null(p.NextLevel);
            Assert.Null(p.NextApproverEmployeeId);
            Assert.Null(p.DecidedByEmployeeId);
        }

        /// The envelope carries the company; the payload must not. A second copy is a second answer
        /// waiting to disagree - and the raw JSON is checked, not the typed view, because a field can
        /// only be proven ABSENT on the wire.
        [Fact]
        public async Task The_payload_carries_no_company_and_no_free_text()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DecideAs(Manager1, req.ID, Manager1, approve: false, note: "a private reason");

            foreach (var stored in rig.Events_())
            {
                var raw = System.Text.Json.Nodes.JsonNode.Parse(stored.Payload!)!.AsObject();
                Assert.Null(raw["companyId"]);
                Assert.Null(raw["companyID"]);
                Assert.Null(raw["decisionNote"]);
                Assert.DoesNotContain("a private reason", stored.Payload!);
                Assert.Equal(
                    new[] { "approverEmployeeId", "decidedByEmployeeId", "employeeRequestId", "level",
                            "nextApproverEmployeeId", "nextLevel", "requestType", "requesterEmployeeId" },
                    raw.Select(x => x.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
            }
        }

        [Fact]
        public async Task Two_companies_keep_their_own_event_tenant()
        {
            using var a = await ReadyAsync();
            using var b = await ReadyAsync(CompanyB);
            await SubmitAsync(a);
            await SubmitAsync(b);

            Assert.Equal(CompanyA, Assert.Single(a.Events_()).CompanyID);
            Assert.Equal(CompanyB, Assert.Single(b.Events_()).CompanyID);
        }

        [Fact]
        public async Task A_submission_becomes_one_task_for_the_persisted_approver()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            await rig.DispatchAllAsync();

            var task = Assert.Single(rig.Tasks());
            Assert.Equal(CompanyA, task.CompanyId);
            Assert.Equal(Manager1, task.AssigneeEmployeeId);
            Assert.Equal(req.ID, task.EntityId);
            Assert.Equal(EntityRegistry.EmployeeRequest, task.EntityType);
            Assert.Null(task.DueDate);

            // the request KIND must not travel into a title a company-breadth reader can see
            foreach (var secret in new[] { "Letter", "Salary", "emp10" })
            {
                Assert.DoesNotContain(secret, task.Title ?? "");
                Assert.DoesNotContain(secret, task.TitleEn ?? "");
            }
        }

        [Fact]
        public async Task An_advance_closes_the_deciding_level_and_opens_the_next()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            Assert.True((await rig.DecideAs(Manager1, req.ID, Manager1, true)).ok);

            var advanced = Assert.Single(rig.Events_(EmployeeRequestEvents.Advanced));
            Assert.Equal($"employee-request:{req.ID}:level:1:Advanced", advanced.DedupKey);
            var p = Payload(advanced);
            Assert.Equal(1, p.Level);                       // the level that DECIDED
            Assert.Equal(2, p.NextLevel);                   // the level that now owes
            Assert.Equal(Manager2, p.NextApproverEmployeeId);
            Assert.Equal(Manager1, p.DecidedByEmployeeId);

            await rig.DispatchAllAsync();
            var tasks = rig.Tasks();
            Assert.Equal(2, tasks.Count);
            Assert.Equal(TaskStatuses.Done, tasks.Single(t => t.AssigneeEmployeeId == Manager1).Status);
            Assert.Equal(TaskStatuses.New, tasks.Single(t => t.AssigneeEmployeeId == Manager2).Status);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_terminal_decision_closes_its_level_and_opens_nothing(bool approve)
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            if (approve)
            {
                await rig.DecideAs(Manager1, req.ID, Manager1, true);
                await rig.DispatchAllAsync();
                Assert.True((await rig.DecideAs(Manager2, req.ID, Manager2, true)).ok);
            }
            else
            {
                Assert.True((await rig.DecideAs(Manager1, req.ID, Manager1, false)).ok);
            }
            await rig.DispatchAllAsync();

            string type = approve ? EmployeeRequestEvents.Approved : EmployeeRequestEvents.Rejected;
            int level = approve ? 2 : 1;
            var terminal = Assert.Single(rig.Events_(type));
            Assert.Equal($"employee-request:{req.ID}:level:{level}:{BusinessEventTypes.ActionOf(type)}", terminal.DedupKey);
            Assert.Equal(level, Payload(terminal).Level);
            Assert.Null(Payload(terminal).NextLevel);

            Assert.Equal(approve ? 2 : 1, rig.Tasks().Count);
            Assert.All(rig.Tasks(), t => Assert.Equal(TaskStatuses.Done, t.Status));
        }

        /// Top of the tree: the domain auto-approves, two facts are still recorded at LEVEL 0, and the
        /// Phase-A rule reads 0 and creates nothing. No phantom Task, no invented approver.
        /// RSK-31 — LEAVE AND EMPLOYEE REQUEST SHARE ONE RESOLVER AND NOW SHARE ONE ROOT POLICY.
        ///
        /// Enforcing this in Leave alone would have been worse than enforcing it nowhere: the two
        /// flows call the same chain, so a fix visible in one would read as closed while this one
        /// still approved a broken tree. The refusal happens before the transaction opens, so an
        /// unverifiable hierarchy leaves no request, no business fact and no work behind it.
        [Fact]
        public async Task An_unverified_empty_chain_creates_no_request_no_fact_and_no_task()
        {
            using var rig = await ReadyAsync(chain: Array.Empty<int>(), verifiedRoot: false);

            var (ok, error, req) = await rig.Requests.CreateAsync(Draft());

            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Null(req);
            Assert.Empty(rig.Host.Seed.EmployeeRequests.AsNoTracking().ToList());
            Assert.Empty(rig.Events_());
            Assert.Empty(rig.Notifications.Sent);

            await rig.DispatchAllAsync();
            Assert.Empty(rig.Tasks());
        }

        [Fact]
        public async Task An_auto_approved_request_records_both_facts_at_level_zero_and_creates_no_task()
        {
            using var rig = await ReadyAsync(chain: Array.Empty<int>());

            var (ok, error, req) = await rig.Requests.CreateAsync(Draft());
            Assert.True(ok, error);
            Assert.Equal(1, Row(rig, req!.ID).Status);          // auto-approved, unchanged behaviour
            Assert.Empty(rig.Steps(req.ID));

            Assert.Equal(
                new[] { EmployeeRequestEvents.Submitted, EmployeeRequestEvents.Approved },
                rig.Events_().Select(e => e.EventType).ToArray());
            Assert.All(rig.Events_(), e => Assert.Equal(0, Payload(e).Level));
            Assert.Equal($"employee-request:{req.ID}:level:0:Submitted", rig.Events_()[0].DedupKey);
            Assert.Equal($"employee-request:{req.ID}:level:0:Approved", rig.Events_()[1].DedupKey);

            await rig.DispatchAllAsync();
            Assert.Empty(rig.Tasks());
        }

        /// A refused creation is not a fact. Nothing is committed, so nothing is published.
        [Fact]
        public async Task A_creation_refused_for_a_broken_hierarchy_records_nothing()
        {
            using var rig = await ReadyAsync(chain: Array.Empty<int>(), breakHierarchy: true);

            var (ok, _, _) = await rig.Requests.CreateAsync(Draft());

            Assert.False(ok);
            Assert.Empty(rig.Host.Seed.EmployeeRequests.AsNoTracking().ToList());
            Assert.Empty(rig.Events_());
            await rig.DispatchAllAsync();
            Assert.Empty(rig.Tasks());
        }

        /// EVERY refusal path, asserted as one table: no business state, no fact, no work, no message.
        [Fact]
        public async Task Every_refused_decision_publishes_nothing()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();

            int events = rig.Events_().Count;
            int tasks = rig.Tasks().Count;
            int notes = rig.Notifications.Sent.Count;
            var before = Snapshot(rig, req.ID);

            // wrong actor / borrowed parameter / foreign company / no identity
            Assert.False((await rig.DecideAs(Outsider, req.ID, Manager1, true)).ok);
            Assert.False((await rig.DecideAs(Manager1, req.ID, Outsider, true)).ok);
            Assert.False((await rig.DecideAs(Manager1, req.ID, Manager1, true, companyOverride: CompanyB)).ok);
            Assert.False((await rig.DecideAs(0, req.ID, 0, true)).ok);

            await rig.DispatchAllAsync();
            Assert.Equal(events, rig.Events_().Count);
            Assert.Equal(tasks, rig.Tasks().Count);
            Assert.Equal(notes, rig.Notifications.Sent.Count);
            Assert.Equal(before, Snapshot(rig, req.ID));
        }

        /// The claim's loser publishes nothing either - proven against the stale-read shape a lost race
        /// actually takes, the same way RSK-28 proved the claim itself.
        [Fact]
        public async Task A_decision_lost_to_a_concurrent_writer_publishes_nothing()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            int events = rig.Events_().Count;

            await using (var other = rig.Host.NewContext())
            {
                var theirs = await other.EmployeeRequestSteps
                    .FirstAsync(x => x.EmployeeRequestID == req.ID && x.Level == 1);
                theirs.Status = 1;
                await other.SaveChangesAsync();
            }

            Assert.False((await rig.DecideAs(Manager1, req.ID, Manager1, true)).ok);
            Assert.Equal(events, rig.Events_().Count);
        }

        /// Two competitors, one winner, and exactly ONE transition fact between them.
        [Fact]
        public async Task Two_competing_deciders_produce_exactly_one_transition_event()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            await using var secondCtx = rig.Host.NewContext();
            var secondIdentity = new Acting(CompanyA, Manager1);
            var second = new EmployeeRequestService(
                secondCtx, rig.Notifications,
                new LeaveWorkflowService(secondCtx, rig.Notifications, new StubDashboard(),
                    NullLogger<LeaveWorkflowService>.Instance),
                secondIdentity,
                new BusinessEventService(secondCtx, new EntityRegistry(secondCtx), secondIdentity,
                    NullLogger<BusinessEventService>.Instance));

            rig.Identity.EmployeeId = Manager1;
            var results = await Task.WhenAll(
                rig.Requests.DecideAsync(req.ID, Manager1, approve: true, note: null),
                second.DecideAsync(req.ID, Manager1, approve: false, note: null));

            Assert.Equal(1, results.Count(r => r.ok));
            Assert.Single(rig.Events_(), e => e.EventType != EmployeeRequestEvents.Submitted);
        }

        /// If the fact cannot be recorded, the transition does not happen. Event and state share one fate.
        [Fact]
        public async Task A_decision_whose_fact_cannot_be_recorded_rolls_the_state_back()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            var before = Snapshot(rig, req.ID);

            var brittle = new EmployeeRequestService(
                rig.Host.Db, rig.Notifications, rig.Leave, rig.Identity, new ThrowingEvents());
            rig.Identity.EmployeeId = Manager1;

            await Assert.ThrowsAnyAsync<Exception>(() =>
                brittle.DecideAsync(req.ID, Manager1, approve: true, note: null));

            Assert.Equal(before, Snapshot(rig, req.ID));
            Assert.Single(rig.Events_());                       // only the submission survives
        }

        /// Replaying every committed fact changes nothing - the ledger suppresses a second projection.
        [Fact]
        public async Task Replaying_every_fact_creates_no_duplicate_task()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await rig.DispatchAllAsync();
            await rig.DecideAs(Manager1, req.ID, Manager1, true);
            await rig.DispatchAllAsync();

            var before = rig.Tasks().Select(t => (t.ID, t.Status, t.AssigneeEmployeeId)).ToList();
            for (var i = 0; i < 3; i++) await rig.DispatchAllAsync();

            Assert.Equal(before, rig.Tasks().Select(t => (t.ID, t.Status, t.AssigneeEmployeeId)).ToList());
        }

        /// Notifications keep their recipients, their types and their post-commit timing, and projecting
        /// the facts into work sends nothing further.
        [Fact]
        public async Task Notifications_are_unchanged_and_not_duplicated_by_the_producer()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            Assert.Equal(new[] { (Manager1, "emp_request_submitted") }, rig.Notifications.Sent.ToArray());

            await rig.DecideAs(Manager1, req.ID, Manager1, true);
            Assert.Equal((Manager2, "emp_request_submitted"), rig.Notifications.Sent.Last());

            int before = rig.Notifications.Sent.Count;
            await rig.DispatchAllAsync();
            Assert.Equal(before, rig.Notifications.Sent.Count);
        }

        /// ONE production producer, and it is this service. A controller, a rule or a notifier emitting
        /// these facts would be a second authority, and the day the two disagreed the louder one wins.
        [Fact]
        public void The_only_production_producer_is_the_employee_request_service()
        {
            var root = SourcePath("CrossBuy", "BL", "EmployeeRequestService.cs");
            var bl = System.IO.Directory.GetParent(root)!.Parent!.FullName;

            var producers = System.IO.Directory
                .EnumerateFiles(bl, "*.cs", System.IO.SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
                .Where(f => System.IO.File.ReadAllText(f).Contains("EmployeeRequestEvents."))
                .Select(System.IO.Path.GetFileName)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            // the vocabulary itself, the consuming rule, and the ONE producer
            Assert.Contains("EmployeeRequestService.cs", producers);
            Assert.DoesNotContain("PeopleController.cs", producers);
            Assert.DoesNotContain("BusinessEventNotificationMapper.cs", producers);
        }

        // ==========================================================================================
        // SELF-APPROVAL — NOW DECIDED, AND ENFORCED HERE (RSK-32)
        // ==========================================================================================

        /// THE ASSERTION THIS TEST USED TO MAKE WAS `Assert.True(ok)`, AND IT WAS NOT A MISTAKE.
        ///
        /// It was called Self_approval_behaviour_is_documented_not_changed, and its comment said the
        /// service permitted this, that Leave refused it, that changing it was a product decision rather
        /// than a security repair, and that IF THIS ASSERTION EVER FLIPS, THE POLICY CHANGED. This is
        /// that flip. The test is kept and turned around rather than deleted, because the record that
        /// the old behaviour was known and deliberate is the thing worth preserving - a deleted test
        /// would leave the impression nobody had looked.
        ///
        /// THE STATE IS STILL CONSTRUCTED, and that is still the honest framing. The chain cannot
        /// produce it: the climb breaks on mgr == employeeId at every level, so a requester never enters
        /// their own approval chain. This test reaches past the chain and writes the row directly,
        /// because the question is not whether the chain can build this - it is what the authoritative
        /// decision boundary does when handed it. Depending on three upstream controls to prevent a
        /// state is not the same as refusing the state.
        [Fact]
        public async Task A_requester_cannot_decide_their_own_request_even_when_stored_as_its_approver()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await MakeRequesterTheStoredApproverAsync(rig, req.ID);

            var (ok, error) = await rig.DecideAs(Requester, req.ID, Requester, approve: true);

            Assert.False(ok);
            Assert.NotNull(error);
        }

        /// THE CONTROL FOR THE TEST ABOVE, and the reason it is here rather than left to the suite at
        /// large: it is the SAME rig, the SAME constructed row and the SAME call, changing exactly one
        /// thing - who the stored approver is. Without it, `Assert.False(ok)` above would also be
        /// satisfied by a service that had simply stopped approving anything.
        [Fact]
        public async Task The_same_constructed_row_is_decided_normally_when_the_approver_is_not_the_requester()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            var (ok, error) = await rig.DecideAs(Manager1, req.ID, Manager1, approve: true);

            Assert.True(ok, error);

            // it ADVANCED rather than finalised - the default rig has two levels - so the decision is
            // visible as level 1 closing by this actor and level 2 becoming the open one
            Assert.Equal(1, rig.Steps(req.ID).Single(x => x.Level == 1).Status);
            Assert.Equal(Manager1, rig.Steps(req.ID).Single(x => x.Level == 1).updatedBy);
            Assert.Equal(2, Row(rig, req.ID).CurrentLevel);
            Assert.Equal(Manager2, Row(rig, req.ID).CurrentApproverEmployeeID);
        }

        /// A REQUESTER CANNOT BORROW SOMEBODY ELSE'S EMPLOYEE ID TO GET PAST THE NEW RULE. The identity
        /// is resolved from the BusinessContext and the supplied parameter is validated against it, so
        /// this is refused by the parameter check BEFORE the segregation rule is even reached - which is
        /// the point. The new rule compares the stored requester to the AUTHORITATIVE actor, so there is
        /// no value a caller can pass that makes the comparison come out differently.
        [Fact]
        public async Task A_requester_cannot_pass_another_employee_id_to_escape_the_segregation_rule()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await MakeRequesterTheStoredApproverAsync(rig, req.ID);

            // authenticated as the requester, claiming to be the manager
            var (ok, _) = await rig.DecideAs(Requester, req.ID, Manager1, approve: true);

            Assert.False(ok);
            AssertUntouched(rig, req.ID);
        }

        /// A CROSS-COMPANY SELF-DECISION IS REFUSED TWICE OVER, and asserting it keeps the two reasons
        /// from being traded against one another. RSK-32 must not become a path that reaches a decision
        /// the tenant predicate would have stopped.
        [Fact]
        public async Task A_self_decision_from_another_company_is_still_refused()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await MakeRequesterTheStoredApproverAsync(rig, req.ID);

            var (ok, _) = await rig.DecideAs(Requester, req.ID, Requester, true, companyOverride: CompanyB);

            Assert.False(ok);
            AssertUntouched(rig, req.ID);
        }

        /// THE RATCHET, AND IT IS BEHAVIOURAL RATHER THAN TEXTUAL ON PURPOSE. A structural test could
        /// grep this service for `req.EmployeeID == me`, and it would pass for a rule that had been
        /// moved below the mutation, inverted, or made to compare the SUPPLIED parameter instead of the
        /// resolved identity. This asks the service the question directly and then checks that the
        /// refusal was INERT, which is the part that a re-ordered rule would break first.
        ///
        /// BOTH REQUEST TYPES AND BOTH DECISIONS, because there is deliberately no type-specific policy:
        /// Letter and Permission share one decision service, and a future branch that exempted either
        /// one - or that guarded only approval and left rejection open - fails here.
        ///
        /// FOUR CONSEQUENCES, ALL OF WHICH MUST BE ABSENT. A refused decision that still emitted an
        /// event, closed a Task, sent a notification or moved the row would be worse than one that
        /// simply approved: the request would read as pending while the platform had already been told
        /// it was decided, and no screen would show the disagreement.
        [Theory]
        [InlineData("Letter", true)]
        [InlineData("Letter", false)]
        [InlineData("Permission", true)]
        [InlineData("Permission", false)]
        public async Task A_refused_self_decision_changes_nothing_at_all(string requestType, bool approve)
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig, requestType);
            await MakeRequesterTheStoredApproverAsync(rig, req.ID);

            var eventsBefore = rig.Events_().Count;
            var notificationsBefore = rig.Notifications.Sent.Count;
            var before = Snapshot(rig, req.ID);

            var (ok, error) = await rig.DecideAs(Requester, req.ID, Requester, approve);

            Assert.False(ok);
            Assert.NotNull(error);

            // F - no state moved, field by field
            Assert.Equal(before, Snapshot(rig, req.ID));
            AssertUntouched(rig, req.ID);

            // G - no decision fact was published
            Assert.Equal(eventsBefore, rig.Events_().Count);
            Assert.Empty(rig.Events_(EmployeeRequestEvents.Approved));
            Assert.Empty(rig.Events_(EmployeeRequestEvents.Rejected));
            Assert.Empty(rig.Events_(EmployeeRequestEvents.Advanced));

            // S - and nobody was told a decision happened
            Assert.Equal(notificationsBefore, rig.Notifications.Sent.Count);

            // H - and no work moved, which is what dispatching the (unchanged) event log proves
            await rig.DispatchAllAsync();
            Assert.All(rig.Tasks(), t => Assert.NotEqual(TaskStatuses.Done, t.Status));
        }

        /// RSK-31 AND RSK-32 ARE SEPARATE CONTROLS, and this test exists so the two are not quietly
        /// conflated by whoever reads them next. A verified structural root auto-approves at CREATION,
        /// with an empty chain and level 0; nobody interactively decides anything, so there is no actor
        /// for a segregation rule to compare against and RSK-32 has nothing to say about it. Reading
        /// that level-0 outcome as 'the requester approved themselves' would break a governed behaviour
        /// that RSK-31 deliberately preserved.
        [Fact]
        public async Task A_verified_root_auto_approval_is_not_a_self_decision()
        {
            using var rig = await ReadyAsync(chain: Array.Empty<int>());

            rig.Identity.EmployeeId = Requester;
            var (ok, error, req) = await rig.Requests.CreateAsync(Draft());

            Assert.True(ok, error);
            Assert.Equal(1, Row(rig, req!.ID).Status);          // Approved
            Assert.Equal(0, Row(rig, req.ID).CurrentLevel);     // at level zero
            Assert.Null(Row(rig, req.ID).CurrentApproverEmployeeID);
            Assert.Empty(rig.Steps(req.ID));

            // both facts, exactly as RSK-31 left them, and no interactive decision anywhere
            Assert.Equal(
                new[] { EmployeeRequestEvents.Submitted, EmployeeRequestEvents.Approved },
                rig.Events_().Select(e => e.EventType).ToArray());

            await rig.DispatchAllAsync();
            Assert.Empty(rig.Tasks());
        }

        /// WHERE THE RULE SITS IS PART OF THE RULE, and this is the test that says so.
        ///
        /// A mutant that MOVED the segregation check from the authorization method to just after the
        /// atomic step claim survived every other test in this file. It still refused the decision, and
        /// every assertion about state, events, Tasks and notifications still held - because when this
        /// service OWNS its transaction, the claim it had already written was rolled back on the way
        /// out, so the refusal looked inert from the outside.
        ///
        /// IT IS NOT INERT WHEN THE TRANSACTION IS SOMEBODY ELSE'S. ScopedTx.BeginOrJoinAsync joins an
        /// ambient transaction rather than opening a second one, and a JOINER's Commit, Rollback and
        /// Dispose are all no-ops by design - the outer owner decides. So under a caller that already
        /// holds a transaction and goes on to commit, a claim written before the refusal SURVIVES: the
        /// step is marked decided, by the very person the rule exists to stop, on a request that was
        /// refused. Nothing else in this suite could see that, because nothing else refused from inside
        /// a caller's transaction.
        [Fact]
        public async Task A_refused_self_decision_claims_no_step_even_inside_a_callers_transaction()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);
            await MakeRequesterTheStoredApproverAsync(rig, req.ID);

            // the caller owns the transaction; this service will JOIN it, not open its own
            await using (var outer = await rig.Host.Db.Database.BeginTransactionAsync())
            {
                var (ok, _) = await rig.DecideAs(Requester, req.ID, Requester, approve: true);
                Assert.False(ok);

                // and the owner commits, which is what makes a premature write permanent
                await outer.CommitAsync();
            }

            Assert.All(rig.Steps(req.ID), x => Assert.Equal(0, x.Status));
            AssertUntouched(rig, req.ID);
            Assert.Empty(rig.Events_(EmployeeRequestEvents.Approved));
        }

        /// The constructed state both self-decision tests need: the requester is written in as the
        /// stored current approver at the open level. The approval chain cannot produce this - see the
        /// comment on the first test - so it is written directly, through the seeding context.
        private static async Task MakeRequesterTheStoredApproverAsync(Rig rig, int requestId)
        {
            var row = await rig.Host.Db.EmployeeRequests.FirstAsync(r => r.ID == requestId);
            row.CurrentApproverEmployeeID = Requester;
            var step = await rig.Host.Db.EmployeeRequestSteps
                .FirstAsync(s => s.EmployeeRequestID == requestId && s.Level == 1);
            step.ApproverEmployeeID = Requester;
            await rig.Host.Db.SaveChangesAsync();
        }

        // ==========================================================================================
        // CREATE ATOMICITY
        // ==========================================================================================

        [Fact]
        public async Task A_submission_writes_its_request_and_every_step_together()
        {
            using var rig = await ReadyAsync();
            var req = await SubmitAsync(rig);

            Assert.Equal(2, rig.Steps(req.ID).Count);
            Assert.Equal(Manager1, rig.Steps(req.ID).Single(s => s.Level == 1).ApproverEmployeeID);
            Assert.Equal(Manager2, rig.Steps(req.ID).Single(s => s.Level == 2).ApproverEmployeeID);
            Assert.Equal(CompanyA, Row(rig, req.ID).CompanyID);
            Assert.Single(rig.Notifications.Sent);
            Assert.Equal((Manager1, "emp_request_submitted"), rig.Notifications.Sent[0]);
        }

        /// The repair this proves: the request and its steps used to be two unprotected saves, so a
        /// failure between them left a request with no chain — a row DecideAsync then refuses for ever.
        [Fact]
        public async Task A_submission_that_fails_midway_leaves_no_request_and_no_step()
        {
            using var rig = await ReadyAsync();
            rig.Host.Db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS EmployeeRequestSteps;");

            await Assert.ThrowsAnyAsync<Exception>(() => rig.Requests.CreateAsync(Draft()));

            Assert.Empty(rig.Host.Seed.EmployeeRequests.AsNoTracking().ToList());
            Assert.Empty(rig.Notifications.Sent);
        }

        /// The chain stays the shared authority: this service reuses ILeaveWorkflowService rather than
        /// computing its own, and a broken hierarchy is refused rather than auto-approved.
        [Fact]
        public async Task The_approver_chain_remains_the_shared_authority()
        {
            using var rig = await ReadyAsync();

            var code = System.IO.File.ReadAllText(SourcePath("CrossBuy", "BL", "EmployeeRequestService.cs"));
            Assert.Contains("ApproverChainAsync", code);
            Assert.Contains("HierarchyDefect", code);
            Assert.DoesNotContain("Hierarchicals", code);          // no second climb lives here

            var req = await SubmitAsync(rig);
            Assert.Equal(Manager1, Row(rig, req.ID).CurrentApproverEmployeeID);
        }

        // ==========================================================================================
        // helpers
        // ==========================================================================================

        private static EmployeeRequest Row(Rig rig, int id) =>
            rig.Host.Seed.EmployeeRequests.AsNoTracking().First(r => r.ID == id);

        /// Every persisted field a decision can touch, as one comparable value.
        private static string Snapshot(Rig rig, int id)
        {
            var r = Row(rig, id);
            var steps = string.Join("|", rig.Steps(id)
                .Select(s => $"{s.ID}:{s.Status}:{s.DecisionAt:O}:{s.DecisionNote}:{s.updatedBy}"));
            return $"{r.Status}/{r.CurrentLevel}/{r.CurrentApproverEmployeeID}/{r.ApproverEmployeeID}/"
                 + $"{r.DecisionAt:O}/{r.DecisionNote}/{r.UpdatedAt:O}/{r.updatedBy}#{steps}";
        }

        private static void AssertUntouched(Rig rig, int id)
        {
            var r = Row(rig, id);
            Assert.Equal(0, r.Status);
            Assert.Equal(1, r.CurrentLevel);
            Assert.Null(r.ApproverEmployeeID);
            Assert.Null(r.DecisionAt);
            Assert.All(rig.Steps(id), s => Assert.Equal(0, s.Status));
            Assert.All(rig.Steps(id), s => Assert.Null(s.DecisionAt));
        }

        private static EmployeeRequest Draft() => new()
        {
            CompanyID = CompanyA, EmployeeID = Requester, RequestType = "Letter",
            LetterType = "Salary", Addressee = "To whom it may concern", Reason = "r",
        };

        /// A Permission request, which the service validates differently from a Letter but decides
        /// through exactly the same authorization path - which is why both types are worth submitting.
        private static EmployeeRequest DraftOf(string requestType) => requestType == "Permission"
            ? new EmployeeRequest
            {
                CompanyID = CompanyA, EmployeeID = Requester, RequestType = "Permission",
                PermissionDate = DateTime.Today.AddDays(3),
                FromTime = TimeSpan.FromHours(9), ToTime = TimeSpan.FromHours(11), Reason = "r",
            }
            : Draft();

        private static async Task<EmployeeRequest> SubmitAsync(Rig rig, string requestType = "Letter")
        {
            rig.Identity.EmployeeId = Requester;
            var (ok, error, req) = await rig.Requests.CreateAsync(DraftOf(requestType));
            Assert.True(ok, error);
            return req!;
        }

        private static string SourcePath(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            var rel = System.IO.Path.Combine(parts);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, rel)))
                dir = System.IO.Directory.GetParent(dir)?.FullName;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir!, rel);
        }

        private static EmployeeRequestEventPayload Payload(BusinessEvent e) =>
            System.Text.Json.JsonSerializer.Deserialize<EmployeeRequestEventPayload>(
                e.Payload!, new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web))!;

        private sealed class ThrowingEvents : IBusinessEventService
        {
            public Task<long> RecordAsync(BusinessEventRecord record, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("the event recorder failed");
            public BusinessEventEnvelope BuildEnvelope(BusinessEvent stored)
                => throw new InvalidOperationException("nothing was recorded");
        }

        /// `chain` empty seeds a TOP-OF-TREE requester, which the domain auto-approves.
        /// `breakHierarchy` grafts a foreign node so the climb is refused as defective.
        private static async Task<Rig> ReadyAsync(int companyId = CompanyA, int[]? chain = null,
            bool breakHierarchy = false, bool verifiedRoot = true)
        {
            var rig = new Rig(companyId);
            var db = rig.Host.Seed;
            foreach (var id in new[] { Requester, Manager1, Manager2, Outsider })
                db.Employee.Add(Emp(id, companyId));

            var ladder = new[] { Requester }.Concat(chain ?? new[] { Manager1, Manager2 }).ToArray();
            for (var i = 0; i < ladder.Length - 1; i++)
                db.Hierarchicals.AddRange(
                    new Hierarchical { H_ID = 100 + ladder[i], H_Type = 5, H_ObjectID = ladder[i], H_Parent = 200 + ladder[i] },
                    new Hierarchical { H_ID = 200 + ladder[i], H_Type = 4, H_ObjectID = null, H_Parent = 100 + ladder[i + 1] });

            if (breakHierarchy)
            {
                // the node above the requester belongs to ANOTHER company: the climb stops and reports
                // HierarchyDefect, and CreateAsync refuses rather than auto-approving
                db.Employee.Add(Emp(99, CompanyB));
                db.Hierarchicals.AddRange(
                    new Hierarchical { H_ID = 100 + Requester, H_Type = 5, H_ObjectID = Requester, H_Parent = 200 + Requester },
                    new Hierarchical { H_ID = 200 + Requester, H_Type = 4, H_ObjectID = null, H_Parent = 199 },
                    new Hierarchical { H_ID = 199, H_Type = 5, H_ObjectID = 99, H_Parent = null });
            }
            else if (verifiedRoot)
            {
                // A VERIFIED STRUCTURAL ROOT (RSK-31): the top of the ladder has its position attached
                // directly to a company node, which is positive proof rather than the mere absence of
                // anyone above. An unverified parentless node is seeded by passing verifiedRoot: false,
                // and it is refused instead of approved.
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

        private sealed class Rig : IDisposable
        {
            public PlatformTestHost Host { get; }
            public Sink Notifications { get; } = new();
            public ILeaveWorkflowService Leave { get; }
            public IEmployeeRequestService Requests { get; }
            public Acting Identity { get; }
            public IBusinessEventService Events { get; }
            public TaskOrchestrationConsumer Consumer { get; }
            public int CompanyId { get; }

            public Rig(int companyId)
            {
                CompanyId = companyId;
                Host = new PlatformTestHost(companyId: companyId);
                Identity = new Acting(companyId, Requester);
                Leave = new LeaveWorkflowService(
                    Host.Db, Notifications, new StubDashboard(),
                    NullLogger<LeaveWorkflowService>.Instance);

                // THE REAL RECORDER, not a double: Phase B's whole claim is that these facts reach
                // the platform, and a stubbed recorder would let the producer look right while the
                // envelope, the dedup and the company were never exercised at all.
                var events = new BusinessEventService(
                    Host.Db, new EntityRegistry(Host.Db), Identity,
                    NullLogger<BusinessEventService>.Instance);
                Events = events;

                Requests = new EmployeeRequestService(Host.Db, Notifications, Leave, Identity, events);

                // The PHASE-A rules, unmodified, so the producer is proved against the real
                // orchestration rather than against event rows alone.
                var tasks = new TaskService(
                    Host.Db, new TaskCalendarEventPublisher(events),
                    new TaskNotificationService(Host.Db, new UatRecordingNotificationService(Host.Db)));
                Consumer = new TaskOrchestrationConsumer(new TaskOrchestrationService(
                    Host.Db,
                    new ITaskOrchestrationRule[]
                    {
                        new EmployeeRequestApprovalRule(), new EmployeeRequestApprovalAdvanceCloseRule(),
                    },
                    tasks,
                    new TaskWorkPolicyResolver(Host.Db, null)));
            }

            /// A decision taken BY somebody. `parameterValue` is separate from `actingAs` on purpose:
            /// the two disagreeing is the impersonation case in both directions.
            public Task<(bool ok, string? error)> DecideAs(
                int actingAs, int requestId, int parameterValue, bool approve,
                string? note = null, int? companyOverride = null)
            {
                Identity.EmployeeId = actingAs;
                Identity.CompanyOverride = companyOverride;
                return Requests.DecideAsync(requestId, parameterValue, approve, note);
            }

            public List<BusinessEvent> Events_(string? type = null) => Host.Seed.BusinessEvents
                .AsNoTracking().Where(e => e.CompanyID == CompanyId)
                .Where(e => type == null || e.EventType == type)
                .OrderBy(e => e.EventId).ToList();

            public List<BusinessEvent> AllEvents() => Host.Seed.BusinessEvents
                .AsNoTracking().OrderBy(e => e.EventId).ToList();

            /// EXACTLY what BusinessEventDispatchWorker does with a committed row.
            public async Task DispatchAllAsync()
            {
                foreach (var stored in AllEvents())
                    await Consumer.HandleAsync(Events.BuildEnvelope(stored));
            }

            public List<TaskItem> Tasks() => Host.Seed.TaskItems
                .AsNoTracking().OrderBy(t => t.ID).ToList();

            public List<EmployeeRequestStep> Steps(int requestId) => Host.Seed.EmployeeRequestSteps
                .AsNoTracking().Where(s => s.EmployeeRequestID == requestId).OrderBy(s => s.Level).ToList();

            public void Dispose() => Host.Dispose();
        }

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
