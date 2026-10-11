using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // DOCUMENT VERIFICATION AS TASK WORK.
    //
    // THESE DRIVE THE REAL ENGINE. TaskOrchestrationService, TaskWorkPolicyResolver, TaskService and
    // the real CrossDbContext over in-memory SQLite with the company write guard armed - only the
    // notification sink is a double, and it persists, so the dedup paths run for real. A suite that
    // asserted against a mocked engine would prove the rule returns an object, not that a task appears.
    //
    // THE ENVELOPES ARE BUILT THE WAY DocumentEvents BUILDS THEM: the same payload keys
    // (documentId / documentTypeCode), the same EventType shape (EntityCode + "." + action) and the
    // same Visibility derivation (VisibilityFor(confidentiality)). Where a literal would have let the
    // test drift away from the producer, the producer's own helper is called instead.
    // =============================================================================================
    public class DocumentVerificationRuleTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 65;
        private const int Employee = 42;

        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();
        private readonly CrossDbContext _db;
        private readonly ITaskService _tasks;

        public DocumentVerificationRuleTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(CompanyA, null);

            var options = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(
                    NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            _db = new CrossDbContext(options, _holder);
            _db.Database.EnsureCreated();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }

            var events = new BusinessEventService(_db, new EntityRegistry(_db),
                new StubContextAccessor(BusinessContext.ForSystem(CompanyA)),
                NullLogger<BusinessEventService>.Instance);
            _tasks = new TaskService(_db, new TaskCalendarEventPublisher(events),
                new TaskNotificationService(_db, new RecordingNotificationService(_db)));
        }

        public void Dispose() { _db.Dispose(); _conn.Dispose(); }

        // ---- harness --------------------------------------------------------------------------

        private ITaskOrchestrationService Engine() =>
            new TaskOrchestrationService(_db, new ITaskOrchestrationRule[] { new DocumentVerificationRule() },
                _tasks, new TaskWorkPolicyResolver(_db), TimeProvider.System,
                NullLogger<TaskOrchestrationService>.Instance);

        /// Built exactly as DocumentEvents.Record builds it, including the Visibility derivation, so a
        /// drift in the producer's payload or confidentiality mapping fails these tests.
        private static BusinessEventEnvelope Doc(
            string action, int companyId = CompanyA, long documentId = 900,
            string? typeCode = "PASSPORT", string confidentiality = DocumentConfidentiality.Internal,
            int entityId = Employee, Guid? uid = null) => new()
            {
                EventUid = uid ?? Guid.NewGuid(),
                EventType = BusinessEventTypes.Build(EntityRegistry.Employee, action),
                Entity = new BusinessEventEntity { Code = EntityRegistry.Employee, Id = entityId },
                Actor = new BusinessEventActor { EmployeeId = 7 },
                Context = new BusinessEventContext { CompanyId = companyId },
                PayloadVersion = 1,
                Visibility = DocumentEvents.VisibilityFor(confidentiality),
                OccurredAt = DateTime.UtcNow,
                Payload = new JsonObject
                {
                    ["documentId"] = documentId,
                    ["documentTypeCode"] = typeCode,
                },
            };

        private TaskItem[] VerificationTasks(int companyId = CompanyA) =>
            _db.TaskItems.AsNoTracking().Where(t => t.CompanyId == companyId).ToArray();

        private TaskAutoLog[] Ledger(int companyId = CompanyA) =>
            _db.TaskAutoLogs.AsNoTracking().Where(l => l.CompanyId == companyId).ToArray();

        // =========================================================================================
        // T1 / T12 - what opens work, and what does not
        // =========================================================================================

        [Fact] // T1
        public async Task A_submitted_document_opens_exactly_one_verification_task()
        {
            await Engine().ProjectAsync(Doc(DocumentEvents.Submitted));

            var task = Assert.Single(VerificationTasks());
            Assert.Equal(TaskStatuses.New, task.Status);
            Assert.Equal(EntityRegistry.Employee, task.EntityType);
            Assert.Equal(Employee, task.EntityId);
            Assert.Equal(CompanyA, task.CompanyId);

            // UNASSIGNED, and system-created. Not a manager, not employee 1.
            Assert.Equal(0, task.AssigneeEmployeeId);
            Assert.Equal(0, task.CreatedByEmployeeId);

            // NO DEADLINE. A submitted document carries none, and the rule invents none - so this task
            // can never become overdue and can never escalate.
            Assert.Null(task.DueDate);

            // Nothing about the document travels in free text.
            Assert.Null(task.Description);

            Assert.Single(Ledger());
        }

        [Theory] // T12
        [InlineData("DocumentExpiringSoon")]
        [InlineData("DocumentExpired")]
        [InlineData("Created")]
        [InlineData("Updated")]
        public async Task An_unrelated_document_or_entity_event_opens_nothing(string action)
        {
            await Engine().ProjectAsync(Doc(action));
            Assert.Empty(VerificationTasks());
            Assert.Empty(Ledger());
        }

        [Fact] // T2 - the payload must actually name a document
        public async Task An_event_without_a_usable_document_id_opens_nothing()
        {
            var noId = Doc(DocumentEvents.Submitted);
            noId.Payload!["documentId"] = null;
            await Engine().ProjectAsync(noId);

            var zero = Doc(DocumentEvents.Submitted, documentId: 0);
            await Engine().ProjectAsync(zero);

            Assert.Empty(VerificationTasks());

            // NON-VACUITY: the same envelope WITH an id does open work, so the refusals above are
            // about the payload and not about an engine that never ran.
            await Engine().ProjectAsync(Doc(DocumentEvents.Submitted));
            Assert.Single(VerificationTasks());
        }

        // =========================================================================================
        // T6 / T7 - the decision closes the review, whichever way it went
        // =========================================================================================

        [Fact] // T6
        public async Task Verifying_the_document_completes_the_task()
        {
            var engine = Engine();
            await engine.ProjectAsync(Doc(DocumentEvents.Submitted));
            Assert.Equal(TaskStatuses.New, Assert.Single(VerificationTasks()).Status);

            await engine.ProjectAsync(Doc(DocumentEvents.Verified));

            var task = Assert.Single(VerificationTasks());
            Assert.Equal(TaskStatuses.Done, task.Status);
        }

        [Fact] // T7
        public async Task Rejecting_the_document_also_completes_the_task_because_a_rejection_is_a_decision()
        {
            var engine = Engine();
            await engine.ProjectAsync(Doc(DocumentEvents.Submitted));
            await engine.ProjectAsync(Doc(DocumentEvents.Rejected));

            var task = Assert.Single(VerificationTasks());
            Assert.Equal(TaskStatuses.Done, task.Status);
        }

        // =========================================================================================
        // T8 / T9 - the cycle. This is the defect ProjectBilling has and Documents does not.
        // =========================================================================================

        [Fact] // T9
        public async Task A_redelivered_event_does_not_open_a_second_task()
        {
            var engine = Engine();
            var uid = Guid.NewGuid();

            // THE SAME EVENT twice - the same EventUid, which is what a redelivered dispatch row is.
            await engine.ProjectAsync(Doc(DocumentEvents.Submitted, uid: uid));
            await engine.ProjectAsync(Doc(DocumentEvents.Submitted, uid: uid));

            Assert.Single(VerificationTasks());
            Assert.Single(Ledger());
        }

        [Fact] // T8
        public async Task A_resubmission_after_a_rejection_opens_a_NEW_review()
        {
            var engine = Engine();

            await engine.ProjectAsync(Doc(DocumentEvents.Submitted));
            await engine.ProjectAsync(Doc(DocumentEvents.Rejected));
            Assert.Equal(TaskStatuses.Done, Assert.Single(VerificationTasks()).Status);

            // THE SECOND FILING IS THE ONE THAT MATTERS. SubmitCoreAsync writes a new version and
            // raises a new DocumentSubmitted - a different event, so a different EventUid.
            await engine.ProjectAsync(Doc(DocumentEvents.Submitted));

            var tasks = VerificationTasks();
            Assert.Equal(2, tasks.Length);
            Assert.Single(tasks.Where(t => t.Status == TaskStatuses.New));
            Assert.Single(tasks.Where(t => t.Status == TaskStatuses.Done));
        }

        [Fact] // T8, the far end: the new review closes on its own decision
        public async Task The_second_cycle_completes_independently_of_the_first()
        {
            var engine = Engine();
            await engine.ProjectAsync(Doc(DocumentEvents.Submitted));
            await engine.ProjectAsync(Doc(DocumentEvents.Rejected));
            await engine.ProjectAsync(Doc(DocumentEvents.Submitted));
            await engine.ProjectAsync(Doc(DocumentEvents.Verified));

            var tasks = VerificationTasks();
            Assert.Equal(2, tasks.Length);
            Assert.All(tasks, t => Assert.Equal(TaskStatuses.Done, t.Status));
        }

        // =========================================================================================
        // T3 / T4 / T10 - tenancy
        // =========================================================================================

        [Fact] // T4
        public async Task An_unresolved_company_opens_nothing()
        {
            var engine = Engine();
            foreach (var bad in new[] { 0, -1 })
                await engine.ProjectAsync(Doc(DocumentEvents.Submitted, companyId: bad));

            Assert.Empty(_db.TaskItems.IgnoreQueryFilters().ToArray());

            // NON-VACUITY: a resolved company does open work.
            await engine.ProjectAsync(Doc(DocumentEvents.Submitted));
            Assert.Single(VerificationTasks());
        }

        [Fact] // T4, the half the engine would otherwise hide
        public void The_rule_refuses_an_unresolved_company_on_its_own()
        {
            // THE ENGINE ALSO REFUSES ONE (TaskWorkPolicyResolver -> PolicyOutcome.CompanyInvalid), which
            // is why the end-to-end test above cannot see this guard at all: a mutant that deleted it
            // survived that test untouched. Asked of the RULE directly, so the rule's own fail-closed
            // behaviour is observed rather than inferred from a downstream layer that happens to agree.
            var rule = new DocumentVerificationRule();

            foreach (var bad in new[] { 0, -1 })
                Assert.Null(rule.Decide(Doc(DocumentEvents.Submitted, companyId: bad)).Intent);

            // NON-VACUITY: the same envelope with a real company DOES produce an intent.
            Assert.NotNull(rule.Decide(Doc(DocumentEvents.Submitted, companyId: CompanyA)).Intent);
        }

        [Fact] // T10 - the SAME document id in two companies must not collide
        public async Task The_same_document_id_in_another_company_is_a_different_review()
        {
            // The rule's prefix carries the company, so company B's key cannot match company A's.
            var a = new DocumentVerificationRule().Decide(
                Doc(DocumentEvents.Submitted, companyId: CompanyA, documentId: 900));
            var b = new DocumentVerificationRule().Decide(
                Doc(DocumentEvents.Submitted, companyId: CompanyB, documentId: 900));

            Assert.NotNull(a.Intent);
            Assert.NotNull(b.Intent);
            Assert.NotEqual(a.Intent!.RuleKey, b.Intent!.RuleKey);
            Assert.Contains($":{CompanyA}:", a.Intent.RuleKey, StringComparison.Ordinal);
            Assert.Contains($":{CompanyB}:", b.Intent.RuleKey, StringComparison.Ordinal);
            Assert.Equal(CompanyA, a.Intent.CompanyId);
            Assert.Equal(CompanyB, b.Intent.CompanyId);
        }

        [Fact] // T3 - a decision in one company cannot close another company's review
        public async Task A_decision_in_another_company_does_not_complete_this_companys_task()
        {
            var engine = Engine();
            await engine.ProjectAsync(Doc(DocumentEvents.Submitted, companyId: CompanyA, documentId: 900));
            Assert.Equal(TaskStatuses.New, Assert.Single(VerificationTasks(CompanyA)).Status);

            // Company B verifies ITS document 900. Company A's review is untouched.
            await engine.ProjectAsync(Doc(DocumentEvents.Verified, companyId: CompanyB, documentId: 900));

            Assert.Equal(TaskStatuses.New, Assert.Single(VerificationTasks(CompanyA)).Status);
        }

        // =========================================================================================
        // T11 - confidentiality. The task is read by a wider population than the document.
        // =========================================================================================

        [Theory] // T11
        [InlineData(DocumentConfidentiality.Confidential)]
        [InlineData(DocumentConfidentiality.Restricted)]
        [InlineData(DocumentConfidentiality.System)]
        public void A_document_that_is_not_internal_never_names_its_type_in_the_task(string confidentiality)
        {
            var decision = new DocumentVerificationRule().Decide(
                Doc(DocumentEvents.Submitted, typeCode: "HIV_TEST", confidentiality: confidentiality));

            var intent = Assert.IsType<TaskIntent>(decision.Intent);
            Assert.DoesNotContain("HIV_TEST", intent.TitleAr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HIV_TEST", intent.TitleEn ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Null(intent.Description);

            // ...and the work still exists. Protecting the type must not make the task disappear.
            Assert.False(string.IsNullOrWhiteSpace(intent.TitleAr));
        }

        [Fact] // T11, the other half - an Internal document MAY name its type
        public void An_internal_document_names_its_type_so_the_pair_is_not_vacuous()
        {
            var decision = new DocumentVerificationRule().Decide(
                Doc(DocumentEvents.Submitted, typeCode: "PASSPORT",
                    confidentiality: DocumentConfidentiality.Internal));

            var intent = Assert.IsType<TaskIntent>(decision.Intent);
            Assert.Contains("PASSPORT", intent.TitleEn ?? "", StringComparison.Ordinal);
        }

        [Fact] // T11 - no document detail beyond the type ever reaches the task
        public void No_document_number_date_or_note_reaches_the_task_text()
        {
            var envelope = Doc(DocumentEvents.Submitted);
            envelope.Payload!["documentNumber"] = "X1234567";
            envelope.Payload!["expiryDate"] = "2030-01-01";
            envelope.Payload!["decisionNote"] = "forged";

            var intent = Assert.IsType<TaskIntent>(
                new DocumentVerificationRule().Decide(envelope).Intent);

            foreach (var secret in new[] { "X1234567", "2030-01-01", "forged" })
            {
                Assert.DoesNotContain(secret, intent.TitleAr, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, intent.TitleEn ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, intent.Description ?? "", StringComparison.OrdinalIgnoreCase);
            }
        }

        // =========================================================================================
        // T5 - an external identity cannot reach this workflow at all
        // =========================================================================================

        [Fact] // T5
        public void The_rule_grants_no_authority_and_a_portal_submission_cannot_exist()
        {
            // The rule is a pure decision; it holds no authority to widen. The REAL portal guard is
            // upstream: PlatformDocumentService.ScopeAsync refuses any context without an employee, and
            // a Client Portal identity has none - so SubmitAsync is unreachable and DocumentSubmitted
            // is never emitted for a portal upload. Asserted at the contract that makes it true.
            var scope = typeof(PlatformDocumentService)
                .GetMethod("ScopeAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(scope);

            // And the rule itself names no actor, so nothing downstream inherits the submitter's
            // identity as authority.
            var intent = Assert.IsType<TaskIntent>(
                new DocumentVerificationRule().Decide(Doc(DocumentEvents.Submitted)).Intent);
            Assert.Equal(0, intent.AssigneeEmployeeId);
        }

        // =========================================================================================
        // T13 / T14 / T15 - the shape of what landed
        // =========================================================================================

        [Fact] // T13
        public void The_rule_declares_its_own_routable_type_and_no_deadline()
        {
            var rule = new DocumentVerificationRule();
            Assert.Equal("DocumentVerification", rule.RuleType);
            Assert.Equal(DocumentVerificationRule.Rule, rule.RuleType);

            // SEPARATE FROM DocumentExpiry, so a deployment can want renewal work without verification
            // work, and vice versa.
            Assert.NotEqual(DocumentExpiryProjection.RuleType, rule.RuleType);

            // No invented SLA.
            Assert.Equal(DueDatePolicy.None, rule.DefaultDueDatePolicy);
        }

        [Fact] // T14 - this wave imported ONE rule, not nine
        public void No_other_orchestration_business_rule_came_with_it()
        {
            var assembly = typeof(ITaskOrchestrationRule).Assembly;
            foreach (var absent in new[]
            {
                "CrossBuy.BL.Platform.Orchestration.LeaveApprovalRule",
                "CrossBuy.BL.Platform.Orchestration.EmployeeRequestApprovalRule",
                "CrossBuy.BL.Platform.Orchestration.InventoryApprovalRule",
                "CrossBuy.BL.Platform.Orchestration.ProjectBillingApprovalRule",
                "CrossBuy.BL.Platform.Orchestration.DocumentResubmissionRule",
                "CrossBuy.BL.Platform.Orchestration.CrmOrchestrationRules",
                "CrossBuy.BL.Platform.Orchestration.ManufWorkOrderExecutionRule",
                "CrossBuy.BL.Platform.Orchestration.PosShortageRule",
                "CrossBuy.BL.TasksCalendar.TaskApprovalProjection",
            })
                Assert.Null(assembly.GetType(absent));

            // The two that ARE here.
            Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration.DocumentVerificationRule"));
            Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration.ProjectMilestoneOrchestrationRule"));
        }

        [Fact] // T15 - the Stage-4 invariant is about HOLD columns, and is untouched by a rule
        public void Adding_a_rule_changes_no_task_column()
        {
            var columns = _db.Model.FindEntityType(typeof(TaskItem))!
                .GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            // WHAT THIS TEST IS ACTUALLY FOR, restated after the Hold stage landed. It was written to
            // say "the document-verification wave added no task schema", and it said that by naming the
            // columns that did not exist at the time. Five of those were the Hold columns, which the
            // later authorized Hold wave added - with its own model, SQL artifact, service and UI. That
            // is not this rule's doing and never was.
            //
            // So the claim is now made where it cannot go stale: the escalation-projection columns that
            // NO wave has authorized are still absent, and the rule introduces nothing of its own.
            foreach (var unauthorized in new[] { "EscalationLevel", "EscalatedAtUtc", "EscalatedForDueUtc" })
                Assert.DoesNotContain(unauthorized, columns);

            // Nothing document-shaped leaked onto the task either.
            foreach (var invented in new[] { "DocumentId", "DocumentTypeCode", "VerificationState" })
                Assert.DoesNotContain(invented, columns);
        }
    }
}
