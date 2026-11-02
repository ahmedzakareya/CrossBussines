using System.Text.Json.Nodes;
using CrossBuy.BL;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
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
    // ==============================================================================================
    // PHASE 3 — BUSINESS EVENT -> TASK ORCHESTRATION
    //
    // These drive the REAL pieces: the real rule, the real orchestration service, the real
    // TaskService for every lifecycle transition, the real event publisher over the real
    // BusinessEventService, on in-memory SQLite with real transactions. The only thing stubbed is
    // the notification sink, which records AND persists so the duplicate checks below run for real.
    //
    // COMPANIES ARE 41 AND 77, NEVER 1 — for the reason Phases 1 and 2 both learned the hard way:
    // a fixture built in company 1 cannot tell "the company argument was used" from "the answer
    // happened to be right", and the literal-1 mutation walks straight through it.
    // ==============================================================================================
    internal sealed class OrchestrationFixture : IDisposable
    {
        public const int CompanyA = 41;
        public const int CompanyB = 77;

        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();

        public CrossDbContext Db { get; }
        public TaskOrchestrationService Orchestration { get; }
        public ITaskService Tasks { get; }
        public TaskWorkPolicyResolver Policies { get; }
        public TaskEscalationService Escalation { get; }
        public IOrgHierarchy Hierarchy { get; }

        public OrchestrationFixture(int dueOffsetDays = 3, int companyId = CompanyA,
            ITaskOrchestrationRule[]? rules = null)
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(companyId, null);

            var options = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            Db = new CrossDbContext(options, _holder);
            Db.Database.EnsureCreated();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }

            IBusinessContextAccessor accessor = new StubContextAccessor(new BusinessContext
            {
                CompanyId = companyId, EmployeeId = 10, UserId = "u10",
                Source = BusinessContextSource.System,
            });
            var events = new BusinessEventService(Db, new EntityRegistry(Db), accessor,
                NullLogger<BusinessEventService>.Instance);
            var publisher = new TaskCalendarEventPublisher(events);
            var notify = new TaskNotificationService(Db, new UatRecordingNotificationService(Db));

            Tasks = new TaskService(Db, publisher, notify);

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tasks:Orchestration:DefaultDueOffsetDays"] = dueOffsetDays.ToString(),
            }).Build();

            Policies = new TaskWorkPolicyResolver(Db, config);
            Orchestration = new TaskOrchestrationService(
                Db, rules ?? new ITaskOrchestrationRule[] { new DocumentVerificationRule() }, Tasks, Policies);

            // THE SAME escalation engine Phase 2 built - not a copy, and not a separate route for
            // generated work. A task that came from an event must age exactly like one somebody typed.
            Hierarchy = new OrgHierarchy(Db, NullLogger<OrgHierarchy>.Instance);
            Escalation = new TaskEscalationService(Db, notify, publisher, Hierarchy, config, accessor);
        }

        public Employee AddEmployee(int companyId, string tag)
        {
            var e = new Employee
            {
                FirstName = "O", LastName = "O", FullName = "orch-" + tag, FullNameEn = "orch-" + tag,
                EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
                Email = $"orch-{tag}-{Guid.NewGuid():N}@example.com", ProfileImage = "",
                Gender = "M", MaritalStatus = "S",
                UserId = "user-orch-" + tag + "-" + Guid.NewGuid().ToString("N")[..8],
            };
            Db.Employee.Add(e);
            Db.SaveChanges();
            return e;
        }

        public Hierarchical AddNode(int? parentNodeId, int? employeeId, string name)
        {
            var n = new Hierarchical
            {
                H_Name = name, H_NameEn = name, H_Parent = parentNodeId,
                H_Type = employeeId.HasValue ? 5 : 1, H_ObjectID = employeeId, IsActive = true,
            };
            Db.Hierarchicals.Add(n);
            Db.SaveChanges();
            return n;
        }

        public void AddRule(int companyId, string ruleType, int? defaultAssignee, bool active = true)
        {
            Db.TaskAutoRules.Add(new TaskAutoRule
            {
                CompanyId = companyId, RuleType = ruleType, IsActive = active,
                DefaultAssigneeEmployeeId = defaultAssignee, CreatedAt = DateTime.UtcNow,
            });
            Db.SaveChanges();
        }

        /// A document event exactly as PlatformDocumentService raises it: addressed to the OWNING
        /// entity, with the document id in the payload.
        public static BusinessEventEnvelope DocumentEvent(
            string action, int companyId, long documentId,
            int entityId = 500, string entityCode = "Employee",
            Guid? uid = null, DateTime? occurredAt = null, string? typeCode = "PASSPORT")
            => new()
            {
                EventUid = uid ?? Guid.NewGuid(),
                EventType = entityCode + "." + action,
                Entity = new BusinessEventEntity { Code = entityCode, Id = entityId },
                Actor = new BusinessEventActor { EmployeeId = 10 },
                Context = new BusinessEventContext { CompanyId = companyId },
                PayloadVersion = 1,
                Visibility = BusinessEventVisibility.Internal,
                OccurredAt = occurredAt ?? DateTime.UtcNow,
                Payload = new JsonObject
                {
                    ["documentId"] = documentId,
                    ["documentTypeCode"] = typeCode,
                    ["status"] = "Pending",
                },
            };

        public List<TaskItem> TasksFor(int companyId) =>
            Db.TaskItems.AsNoTracking().Where(t => t.CompanyId == companyId).ToList();

        public List<TaskAutoLog> Ledger(int companyId) =>
            Db.TaskAutoLogs.AsNoTracking().Where(l => l.CompanyId == companyId).ToList();

        public void Dispose() { Db.Dispose(); _conn.Dispose(); }
    }

    /// A rule with no opinion at all: it projects every event it is shown. It exists so the SERVICE
    /// guard can be tested on its own merits. With the real rule set, a Task.* event is refused twice
    /// over — once by the guard and once because no rule matches — and a test cannot tell which did
    /// it. The guard has to hold for rules that do not exist yet, which is exactly what this proves.
    internal sealed class AlwaysCreateRule : ITaskOrchestrationRule
    {
        public string RuleType => "AlwaysCreate";

        public DueDatePolicy DefaultDueDatePolicy => DueDatePolicy.OffsetFromEvent;

        public TaskProjectionDecision Decide(BusinessEventEnvelope envelope) =>
            TaskProjectionDecision.Create(new TaskIntent
            {
                CompanyId = envelope.Context.CompanyId,
                RuleType = RuleType,
                RuleKey = "TaskProjection:AlwaysCreate:" + envelope.EventUid.ToString("N"),
                TitleAr = "أي شيء", TitleEn = "anything",
                EntityType = envelope.Entity.Code, EntityId = envelope.Entity.Id,
            });
    }

}
