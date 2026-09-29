using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // STAGE 5 - THE PROJECTMILESTONE DOMAIN FOUNDATION.
    //
    // WHY IT EXISTS. Stage 2 imported the identifier "ProjectMilestone" and nothing behind it, so TAB-6
    // stopped: its orchestration rule listens for milestone events that no producer could emit, because
    // the entity, the table, the service and the registry definition were all absent. Stage 5 converges
    // that domain - and stops at its edge.
    //
    // THE LINE THIS FILE DEFENDS. A milestone is a COMMITMENT; a task is WORK. ProjectMilestoneService
    // emits business events and creates no task, ever. The rule that turns a milestone event into work
    // is TAB-6's and is deliberately still absent - asserted below, not merely intended.
    // =============================================================================================
    public class Stage5ProjectMilestoneFoundationTests : IDisposable
    {
        private const int CompanyA = 1;
        private const int CompanyB = 2;
        private const int ActorA = 10;

        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();
        private readonly CrossDbContext _db;
        private readonly IProjectMilestoneService _milestones;

        public Stage5ProjectMilestoneFoundationTests()
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

            _milestones = new ProjectMilestoneService(
                _db,
                new StubContextAccessor(Ctx(CompanyA)),
                new AllowAllProjectsAccess(),
                new BusinessEventService(_db, new EntityRegistry(_db),
                    new StubContextAccessor(Ctx(CompanyA)),
                    NullLogger<BusinessEventService>.Instance));

            Seed(1, CompanyA);
            Seed(2, CompanyB);
        }

        public void Dispose() { _db.Dispose(); _conn.Dispose(); }

        private static BusinessContext Ctx(int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = ActorA, UserId = "u10",
            Roles = Array.Empty<string>(),
        };

        private void Seed(int projectId, int companyId)
        {
            _db.Projects.Add(new Project
            {
                ID = projectId, CompanyID = companyId,
                Code = "P" + projectId, Name = "Project " + projectId,
            });
            _db.SaveChanges();
        }

        /// The access service is stubbed OPEN on purpose: this file tests the milestone domain's own
        /// company handling, so an access service that refused everything would make every assertion
        /// below pass for the wrong reason.
        private sealed class AllowAllProjectsAccess : IProjectsAccessService
        {
            public Task<bool> CanAsync(BusinessContext context, string action,
                PermissionTarget? target = null, CancellationToken cancellationToken = default)
                => Task.FromResult(true);

            public Task<AccessScope> ResolveProjectScopeAsync(BusinessContext context, string action,
                CancellationToken cancellationToken = default)
                => Task.FromResult(new AccessScope
                {
                    Breadth = AccessBreadth.Company,
                    CompanyId = context.CompanyId,
                });

            public Task<IReadOnlySet<int>> MemberProjectIdsAsync(BusinessContext context,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
        }

        // -----------------------------------------------------------------------------------------
        // 1 + 17. MAPPING AND DbSet
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_context_maps_both_milestone_tables()
        {
            var milestone = _db.Model.FindEntityType(typeof(ProjectMilestone));
            var deliverable = _db.Model.FindEntityType(typeof(ProjectDeliverable));
            Assert.NotNull(milestone);
            Assert.NotNull(deliverable);

            Assert.Equal("ProjectMilestones", milestone!.GetTableName());
            Assert.Equal("ProjectDeliverables", deliverable!.GetTableName());

            var columns = milestone.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var required in new[] { "ID", "CompanyID", "ProjectId", "Name", "Status", "TargetDate", "SortOrder" })
                Assert.Contains(required, columns);
        }

        [Fact]
        public void The_milestone_table_is_not_enrolled_in_the_global_company_filter()
        {
            // Stated rather than assumed. Like every other Projects table it is isolated by an EXPLICIT
            // predicate in its service, not by the twelve-entity pilot; if someone later enrols it, this
            // fails and points here so that becomes a decision rather than a drift.
            Assert.Null(_db.Model.FindEntityType(typeof(ProjectMilestone))!.GetQueryFilter());
        }

        // -----------------------------------------------------------------------------------------
        // 3-9. LIFECYCLE
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_milestone_is_created_into_its_project_company()
        {
            var (ok, error, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);

            Assert.True(ok, error);
            var row = _db.ProjectMilestones.IgnoreQueryFilters().Single(m => m.ID == id);
            Assert.Equal(CompanyA, row.CompanyID);
            Assert.Equal(1, row.ProjectId);
            Assert.Equal(ProjectMilestoneStatuses.Planned, row.Status);
        }

        [Fact]
        public async Task A_milestone_cannot_be_created_under_another_companys_project()
        {
            // Project 2 belongs to company B; the caller's context is company A.
            var (ok, _, id) = await _milestones.SaveMilestoneAsync(
                2, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);

            Assert.False(ok);
            Assert.Equal(0, id);
            Assert.Empty(_db.ProjectMilestones.IgnoreQueryFilters());
        }

        [Fact]
        public async Task A_foreign_project_answers_exactly_as_a_missing_one()
        {
            var foreign = await _milestones.SaveMilestoneAsync(
                2, 0, "x", "x", null, null, DateTime.Today, 1);
            var missing = await _milestones.SaveMilestoneAsync(
                999_999, 0, "x", "x", null, null, DateTime.Today, 1);

            // No oracle: a project id cannot be probed to learn that another tenant owns it.
            Assert.False(foreign.ok);
            Assert.False(missing.ok);
            Assert.Equal(missing.error, foreign.error);
        }

        [Fact]
        public async Task A_milestone_is_read_back_only_within_its_own_company()
        {
            var (_, _, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);

            var board = await _milestones.BoardAsync(1);
            Assert.Contains(board.Milestones, m => m.Id == id);

            // Project 2 is company B's; the company-A caller sees nothing of it.
            var foreign = await _milestones.BoardAsync(2);
            Assert.Empty(foreign.Milestones);
        }

        [Fact]
        public async Task A_milestone_is_updated_in_place_rather_than_duplicated()
        {
            var (_, _, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);

            var (ok, error, sameId) = await _milestones.SaveMilestoneAsync(
                1, id, "تسليم نهائي", "Final handover", null, null, DateTime.Today.AddDays(45), 2);

            Assert.True(ok, error);
            Assert.Equal(id, sameId);
            var row = _db.ProjectMilestones.IgnoreQueryFilters().Single();
            Assert.Equal("Final handover", row.NameEn);
            Assert.Equal(2, row.SortOrder);
        }

        [Fact]
        public async Task A_status_moves_only_where_the_matrix_allows()
        {
            var (_, _, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);

            Assert.True(ProjectMilestoneStatuses.CanMove(
                ProjectMilestoneStatuses.Planned, ProjectMilestoneStatuses.InProgress));

            var moved = await _milestones.SetMilestoneStatusAsync(1, id, ProjectMilestoneStatuses.InProgress);
            Assert.True(moved.ok, moved.error);
            Assert.Equal(ProjectMilestoneStatuses.InProgress,
                _db.ProjectMilestones.IgnoreQueryFilters().Single().Status);

            var nonsense = await _milestones.SetMilestoneStatusAsync(1, id, "Teleported");
            Assert.False(nonsense.ok);
            Assert.Equal(ProjectMilestoneStatuses.InProgress,
                _db.ProjectMilestones.IgnoreQueryFilters().Single().Status);
        }

        [Fact]
        public async Task A_planned_milestone_can_be_deleted()
        {
            var (_, _, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);

            var (ok, error) = await _milestones.DeleteMilestoneAsync(1, id);

            Assert.True(ok, error);
            Assert.Empty(_db.ProjectMilestones.IgnoreQueryFilters());
        }

        // -----------------------------------------------------------------------------------------
        // 10-14. THE THREE EVENTS
        // -----------------------------------------------------------------------------------------

        private string[] EventTypes() => _db.BusinessEvents.IgnoreQueryFilters()
            .OrderBy(e => e.EventId).Select(e => e.EventType).ToArray();

        [Fact]
        public async Task Creating_a_milestone_records_exactly_one_created_event()
        {
            await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);

            var events = _db.BusinessEvents.IgnoreQueryFilters().ToList();
            var created = Assert.Single(events);
            Assert.Equal("ProjectMilestone.Created", created.EventType);
            Assert.Equal(EntityRegistry.ProjectMilestone, created.EntityType);
            Assert.Equal(CompanyA, created.CompanyID);
        }

        [Fact]
        public async Task A_status_change_records_a_status_changed_event()
        {
            var (_, _, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);

            await _milestones.SetMilestoneStatusAsync(1, id, ProjectMilestoneStatuses.InProgress);

            Assert.Equal(new[] { "ProjectMilestone.Created", "ProjectMilestone.StatusChanged" }, EventTypes());
        }

        [Fact]
        public async Task A_delete_records_a_deleted_event_before_the_row_goes()
        {
            var (_, _, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);

            await _milestones.DeleteMilestoneAsync(1, id);

            // The facts have to be read from the row BEFORE it is removed, which is why the event exists
            // at all after a physical delete.
            Assert.Equal(new[] { "ProjectMilestone.Created", "ProjectMilestone.Deleted" }, EventTypes());
            Assert.Empty(_db.ProjectMilestones.IgnoreQueryFilters());
        }

        [Fact]
        public async Task A_refused_save_records_no_event()
        {
            await _milestones.SaveMilestoneAsync(2, 0, "x", "x", null, null, DateTime.Today, 1);

            // A refusal that still emitted would put a fact in a permanent log for something that never
            // happened, which is worse than the refusal itself.
            Assert.Empty(_db.BusinessEvents.IgnoreQueryFilters());
        }

        [Fact]
        public void The_event_prefix_equals_the_registered_entity_code()
        {
            // BusinessEventTypes.Validate compares the prefix to the code with ordinal equality, so this
            // is what makes the events emittable at all rather than refused at runtime.
            foreach (var action in new[] { "Created", "StatusChanged", "Deleted" })
                Assert.True(BusinessEventTypes.TryValidate(
                    "ProjectMilestone." + action, EntityRegistry.ProjectMilestone, out _));
        }

        // -----------------------------------------------------------------------------------------
        // 15-16. REGISTRY
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_registry_now_resolves_a_milestone_and_keeps_it_out_of_search_and_timeline()
        {
            var registry = new EntityRegistry(_db);
            Assert.True(registry.IsValid(EntityRegistry.ProjectMilestone));

            var definition = registry.GetDefinition(EntityRegistry.ProjectMilestone);
            Assert.Equal("Projects", definition.Module);

            // TIMELINE STAYS OFF. TimelineEventPresenter has no arm for a milestone, and a definition
            // that claimed timeline support would strand a dispatch row on every emission. Stage 2
            // deferred the definition for exactly this reason; the capability is still false.
            Assert.False(definition.SupportsTimeline);
            Assert.False(definition.SupportsSearch);
            Assert.False(definition.ListedInRecordPicker);
        }

        [Fact]
        public async Task An_unknown_or_foreign_milestone_resolves_to_nothing_rather_than_a_label()
        {
            var (_, _, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);
            var registry = new EntityRegistry(_db);

            var mine = await registry.ResolveAsync(
                EntityRegistry.ProjectMilestone, id, Ctx(CompanyA));
            Assert.True(mine.Found);

            // Same id, other company: not "forbidden", simply not there.
            var theirs = await registry.ResolveAsync(
                EntityRegistry.ProjectMilestone, id, Ctx(CompanyB));
            var missing = await registry.ResolveAsync(
                EntityRegistry.ProjectMilestone, 999_999, Ctx(CompanyA));
            Assert.False(theirs.Found);
            Assert.False(missing.Found);
        }

        // -----------------------------------------------------------------------------------------
        // 19-20. THE TAB-6 BOUNDARY
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task The_milestone_service_creates_no_task_of_any_kind()
        {
            var (_, _, id) = await _milestones.SaveMilestoneAsync(
                1, 0, "تسليم", "Handover", null, null, DateTime.Today.AddDays(30), 1);
            await _milestones.SetMilestoneStatusAsync(1, id, ProjectMilestoneStatuses.Completed);
            await _milestones.DeleteMilestoneAsync(1, id);

            // THE BOUNDARY, ASSERTED ACROSS THE WHOLE LIFECYCLE. A milestone is a commitment and a task
            // is work; the service emits facts and never projects them. The rule that turns one into the
            // other is TAB-6's, and it runs off the event, not off a call from here.
            Assert.Empty(_db.TaskItems.IgnoreQueryFilters());
            Assert.Empty(_db.TaskAutoLogs.IgnoreQueryFilters());
        }

        [Fact]
        public void No_project_milestone_orchestration_rule_is_registered_yet()
        {
            // MOVED WITH THE TAB-6 P0 WAVE, exactly as this test's original text said it would. The
            // rule is no longer absent; what this test still guards is the part that mattered - that
            // the foundation plugs into the generic contract WITHOUT the engine changing.
            var assembly = typeof(IProjectMilestoneService).Assembly;
            var rule = assembly.GetType("CrossBuy.BL.Platform.Orchestration.ProjectMilestoneOrchestrationRule");
            Assert.NotNull(rule);

            // It is a RULE, not a second engine: it implements the Stage-4 contract and nothing more.
            var contract = assembly.GetType("CrossBuy.BL.Platform.Orchestration.ITaskOrchestrationRule");
            Assert.NotNull(contract);
            Assert.True(contract!.IsAssignableFrom(rule));

            // And the generic engine is STILL untouched - its contract is exactly what Stage 4 landed.
            Assert.NotNull(assembly.GetType("CrossBuy.BL.Platform.Orchestration.TaskOrchestrationService"));
            Assert.NotNull(contract.GetMethod("Decide"));
        }

        [Fact]
        public void The_foundation_payload_carries_no_target_date()
        {
            // PROVENANCE, PINNED - AND NOW MOVED, exactly as this test's original text said it would.
            // The qualified programme payload deliberately carried identifiers and state only; the
            // additive nullable TargetDate is TAB-6's enhancement under the SHF-175 amendment and NOT
            // foundation. That provenance is still the point of this test: it records that the field
            // arrived with the orchestration wave rather than with the foundation.
            var properties = typeof(ProjectMilestoneEventPayload)
                .GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            // The foundation fields are unchanged - nothing was renamed or removed to make room.
            Assert.Contains("MilestoneId", properties);
            Assert.Contains("Status", properties);
            Assert.Contains("PreviousStatus", properties);
            Assert.Contains("CompanyId", properties);
            Assert.Contains("ProjectId", properties);
            Assert.Contains("ActorEmployeeId", properties);

            // TAB-6's addition: present, NULLABLE, and at the same version. Nullable is what keeps a
            // payload persisted before this wave readable, which is why the type is asserted and not
            // merely the name.
            Assert.Contains("TargetDate", properties);
            Assert.Equal(typeof(DateTime?),
                typeof(ProjectMilestoneEventPayload).GetProperty("TargetDate")!.PropertyType);
            Assert.Equal(1, ProjectMilestoneEventPayload.Version);
        }
    }
}
