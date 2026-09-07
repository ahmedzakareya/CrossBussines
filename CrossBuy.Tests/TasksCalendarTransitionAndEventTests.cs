using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // PHASES 1 + 3 — TASKSERVICE TRANSITION WIRING AND TRANSACTIONAL EVENT PUBLISHING
    //
    // These use the REAL BusinessEventService, not a stub. The whole claim being tested is that an
    // event and the state change it describes are ONE commit — a stub would record the call and prove
    // nothing about the transaction.
    // ==========================================================================================
    internal sealed class TransitionFixture : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();

        public CrossDbContext Db { get; }
        public RecordingNotificationService Notifier { get; }
        public ITaskService Tasks { get; }
        public IBusinessEventService Events { get; }

        public TransitionFixture(int companyId = 1)
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _holder.Set(companyId, null);

            Db = NewContext();
            Db.Database.EnsureCreated();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                cmd.ExecuteNonQuery();
            }

            Notifier = new RecordingNotificationService(Db);
            var notify = new TaskNotificationService(Db, Notifier);
            Events = new BusinessEventService(
                Db, new EntityRegistry(Db),
                new StubContextAccessor(BusinessContext.ForSystem(companyId)),
                NullLogger<BusinessEventService>.Instance);
            Tasks = new TaskService(Db, new TaskCalendarEventPublisher(Events), notify);
        }

        public CrossDbContext NewContext()
        {
            var o = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            return new CrossDbContext(o, _holder);
        }

        public async Task SeedEmployeeAsync(int id, int companyId = 1, bool active = true)
        {
            Db.Employee.Add(new Employee
            {
                ID = id, EmpCompanyID = companyId, IsActive = active,
                FirstName = $"Emp{id}", LastName = "Test", FullName = $"Emp {id}",
                Address = "-", PhoneNumber = "-", Email = $"emp{id}@test.local",
                ProfileImage = "-", Gender = "-", MaritalStatus = "-", UserId = $"u{id}"
            });
            await Db.SaveChangesAsync();
        }

        public async Task<List<string>> EventTypesAsync(int taskId)
        {
            await using var db = NewContext();
            return await db.BusinessEvents.AsNoTracking()
                .Where(e => e.EntityType == "Task" && e.EntityId == taskId)
                .OrderBy(e => e.EventId).Select(e => e.EventType).ToListAsync();
        }

        public async Task<int> EventCountAsync()
        {
            await using var db = NewContext();
            return await db.BusinessEvents.AsNoTracking().CountAsync();
        }

        public async Task<string?> PayloadAsync(int taskId, string eventType)
        {
            await using var db = NewContext();
            return await db.BusinessEvents.AsNoTracking()
                .Where(e => e.EntityId == taskId && e.EventType == eventType)
                .Select(e => e.Payload).FirstOrDefaultAsync();
        }

        public void Dispose() { Db.Dispose(); _conn.Dispose(); }
    }

    public class TasksCalendarTransitionTests
    {
        private static TaskSaveInput New(string title = "Inspect the site", int assignee = 10,
            DateTime? due = null, string priority = "Normal") => new()
            {
                Title = title, AssigneeEmployeeId = assignee, Priority = priority, DueDate = due
            };

        // ---- creation -------------------------------------------------------------------------
        [Fact]
        public async Task Creating_a_task_publishes_created_and_assigned_and_notifies_the_assignee()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var (ok, err, id) = await f.Tasks.SaveAsync(1, New(), currentEmployeeId: 20);

            Assert.True(ok, err);
            Assert.Equal(new[] { "Task.Created", "Task.Assigned" }, await f.EventTypesAsync(id));

            // Created notifies nobody; the ASSIGNMENT is what needs someone's attention.
            var sent = Assert.Single(f.Notifier.Sent);
            Assert.Equal(10, sent.RecipientEmployeeId);
            Assert.Equal(TaskNotificationKinds.Assigned, sent.Type);
        }

        [Fact]
        public async Task A_rejected_save_writes_no_event_and_no_notification()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);

            // Both validation failures: no title, and no assignee.
            var a = await f.Tasks.SaveAsync(1, New(title: "   "), 20);
            var b = await f.Tasks.SaveAsync(1, New(assignee: 0), 20);

            Assert.False(a.ok);
            Assert.False(b.ok);
            Assert.Equal(0, await f.EventCountAsync());
            Assert.Empty(f.Notifier.Sent);
        }

        // ---- assignment / reassignment ---------------------------------------------------------
        [Fact]
        public async Task Reassigning_publishes_reassigned_and_notifies_both_sides()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(11);
            await f.SeedEmployeeAsync(20);

            var (_, _, id) = await f.Tasks.SaveAsync(1, New(assignee: 10), 20);
            f.Notifier.Sent.Clear();

            var input = New(assignee: 11);
            input.Id = id;
            var (ok, err, _) = await f.Tasks.SaveAsync(1, input, 20);

            Assert.True(ok, err);
            Assert.Contains("Task.Reassigned", await f.EventTypesAsync(id));
            Assert.Equal(new[] { 10, 11 }, f.Notifier.Sent.Select(s => s.RecipientEmployeeId).OrderBy(x => x).ToArray());
        }

        [Fact]
        public async Task Saving_with_no_change_publishes_no_transition_event_and_notifies_nobody()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var (_, _, id) = await f.Tasks.SaveAsync(1, New(), 20);
            var before = await f.EventTypesAsync(id);
            f.Notifier.Sent.Clear();

            // Same assignee, same due date — only the title differs.
            var input = New(title: "Inspect the site again");
            input.Id = id;
            await f.Tasks.SaveAsync(1, input, 20);

            // An unchanged value is not a transition. No Assigned, no Reassigned, no DueDateChanged.
            Assert.Equal(before, await f.EventTypesAsync(id));
            Assert.Empty(f.Notifier.Sent);
        }

        // ---- due date ---------------------------------------------------------------------------
        [Fact]
        public async Task Changing_the_due_date_publishes_the_change_with_both_values()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var first = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
            var (_, _, id) = await f.Tasks.SaveAsync(1, New(due: first), 20);
            f.Notifier.Sent.Clear();

            var input = New(due: new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc));
            input.Id = id;
            await f.Tasks.SaveAsync(1, input, 20);

            Assert.Contains("Task.DueDateChanged", await f.EventTypesAsync(id));
            var payload = await f.PayloadAsync(id, "Task.DueDateChanged");
            Assert.Contains("2026-09-01", payload);      // previous
            Assert.Contains("2026-09-05", payload);      // new
            Assert.Equal(TaskNotificationKinds.DueDateChanged, Assert.Single(f.Notifier.Sent).Type);
        }

        // ---- status -----------------------------------------------------------------------------
        [Fact]
        public async Task Completing_publishes_status_changed_and_completed_and_notifies_the_creator()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var (_, _, id) = await f.Tasks.SaveAsync(1, New(), 20);
            f.Notifier.Sent.Clear();

            await f.Tasks.ChangeStatusAsync(1, id, "InProgress", 10);
            await f.Tasks.ChangeStatusAsync(1, id, "Done", 10);

            var types = await f.EventTypesAsync(id);
            Assert.Contains("Task.StatusChanged", types);
            Assert.Contains("Task.Completed", types);

            // The assignee completed it, so the CREATOR is told and the actor is not.
            var sent = Assert.Single(f.Notifier.Sent);
            Assert.Equal(20, sent.RecipientEmployeeId);
            Assert.Equal(TaskNotificationKinds.Completed, sent.Type);
        }

        [Fact]
        public async Task Reopening_publishes_reopened_and_notifies_the_assignee()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var (_, _, id) = await f.Tasks.SaveAsync(1, New(), 20);
            await f.Tasks.ChangeStatusAsync(1, id, "InProgress", 10);
            await f.Tasks.ChangeStatusAsync(1, id, "Done", 10);
            f.Notifier.Sent.Clear();

            var (ok, err) = await f.Tasks.ChangeStatusAsync(1, id, "InProgress", 20);

            Assert.True(ok, err);
            Assert.Contains("Task.Reopened", await f.EventTypesAsync(id));
            Assert.Equal(10, Assert.Single(f.Notifier.Sent).RecipientEmployeeId);
        }

        [Fact]
        public async Task A_refused_transition_publishes_nothing_and_notifies_nobody()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var (_, _, id) = await f.Tasks.SaveAsync(1, New(), 20);
            await f.Tasks.ChangeStatusAsync(1, id, "Done", 10);
            var before = await f.EventTypesAsync(id);
            f.Notifier.Sent.Clear();

            // The transition map allows New->{InProgress,Done}, InProgress->{New,Done}, Done->{InProgress}.
            // So Done -> New is the refused one; New -> Done is legitimate and would have proved nothing.
            var (ok, err) = await f.Tasks.ChangeStatusAsync(1, id, "New", 20);

            Assert.False(ok);
            Assert.Contains("انتقال غير مسموح", err);
            Assert.Equal(before, await f.EventTypesAsync(id));   // no event for a change that did not happen
            Assert.Empty(f.Notifier.Sent);
        }

        [Fact]
        public async Task Setting_the_same_status_again_changes_nothing()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var (_, _, id) = await f.Tasks.SaveAsync(1, New(), 20);
            var before = await f.EventTypesAsync(id);
            f.Notifier.Sent.Clear();

            var (ok, _) = await f.Tasks.ChangeStatusAsync(1, id, "New", 20);

            Assert.True(ok);                                    // idempotent success, not an error
            Assert.Equal(before, await f.EventTypesAsync(id));
            Assert.Empty(f.Notifier.Sent);
        }

        // ---- transactional guarantees ----------------------------------------------------------
        [Fact]
        public async Task An_event_and_its_state_change_are_one_commit()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var (_, _, id) = await f.Tasks.SaveAsync(1, New(), 20);

            // Read BOTH from a new context: the task exists and so do its events, or neither would.
            await using var verify = f.NewContext();
            Assert.True(await verify.TaskItems.AnyAsync(t => t.ID == id));
            Assert.Equal(2, await verify.BusinessEvents.CountAsync(e => e.EntityId == id && e.EntityType == "Task"));
        }

        [Fact]
        public async Task No_event_survives_a_rolled_back_outer_transaction()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            // An OUTER transaction the caller controls. ScopedTx.BeginOrJoinAsync JOINS it rather than
            // opening its own, so the commit decision stays with whoever opened it — and rolling it back
            // must take the events with it.
            int id;
            await using (var outer = await f.Db.Database.BeginTransactionAsync())
            {
                var (ok, err, newId) = await f.Tasks.SaveAsync(1, New(), 20);
                Assert.True(ok, err);
                id = newId;

                // Visible inside the transaction...
                Assert.True(await f.Db.BusinessEvents.AnyAsync(e => e.EntityId == id && e.EntityType == "Task"));

                await outer.RollbackAsync();
            }

            // ...and gone after the rollback. The task did not happen, so neither did its events.
            await using var verify = f.NewContext();
            Assert.False(await verify.TaskItems.AnyAsync(t => t.ID == id));
            Assert.Equal(0, await verify.BusinessEvents.CountAsync(e => e.EntityType == "Task"));
        }

        [Fact]
        public async Task Every_published_event_carries_company_entity_actor_and_a_utc_instant()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var before = DateTime.UtcNow.AddSeconds(-5);
            var (_, _, id) = await f.Tasks.SaveAsync(1, New(), 20);

            await using var verify = f.NewContext();
            var rows = await verify.BusinessEvents.AsNoTracking()
                .Where(e => e.EntityId == id && e.EntityType == "Task").ToListAsync();

            Assert.NotEmpty(rows);
            Assert.All(rows, e =>
            {
                Assert.Equal(1, e.CompanyID);
                Assert.Equal("Task", e.EntityType);
                Assert.Equal(id, e.EntityId);
                Assert.Equal(20, e.ActorEmployeeId);
                Assert.True(e.CreatedAt >= before, "the stored instant must be a real UTC instant");
                Assert.NotEqual(Guid.Empty, e.CorrelationId ?? Guid.Empty);
            });

            // One user action, one correlation id across every event it produced.
            Assert.Single(rows.Select(e => e.CorrelationId).Distinct());
        }

        [Fact]
        public async Task No_task_event_payload_carries_the_description_or_commercial_data()
        {
            using var f = new TransitionFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);

            var input = New();
            input.Description = "CONFIDENTIAL: budget overrun detail";
            input.IsBillable = true;
            input.BillRate = 999m;
            input.CustomerId = 77;

            var (_, _, id) = await f.Tasks.SaveAsync(1, input, 20);

            await using var verify = f.NewContext();
            var payloads = await verify.BusinessEvents.AsNoTracking()
                .Where(e => e.EntityId == id && e.EntityType == "Task")
                .Select(e => e.Payload).ToListAsync();

            Assert.NotEmpty(payloads);
            Assert.All(payloads, p =>
            {
                Assert.DoesNotContain("CONFIDENTIAL", p ?? "");
                Assert.DoesNotContain("999", p ?? "");
                Assert.DoesNotContain("billRate", p ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("customerId", p ?? "", StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    // ==========================================================================================
    // PHASE 2 — the registration itself, verified through the registry rather than the request object
    // ==========================================================================================
    public class TasksCalendarRegistrationTests
    {
        [Fact]
        public void Task_and_CalendarEvent_are_registered_with_the_intended_capabilities()
        {
            using var host = new PlatformTestHost();
            var registry = host.Registry();

            var task = registry.GetDefinitions().Single(d => d.Code == "Task");
            Assert.Equal("Tasks", task.PermissionScope);        // NOT ScopeNone
            Assert.True(task.SupportsTimeline);
            Assert.True(task.SupportsComments);
            Assert.True(task.SupportsFiles);
            Assert.True(task.SupportsFollowers);
            Assert.True(task.SupportsSearch);

            var cal = registry.GetDefinitions().Single(d => d.Code == "CalendarEvent");
            Assert.True(cal.SupportsTimeline);
            Assert.True(cal.SupportsComments);
            // Attendees ARE the follower set; a second concept would diverge from the attendee list.
            Assert.False(cal.SupportsFollowers);
        }

        [Fact]
        public void An_unknown_entity_code_is_still_refused()
        {
            using var host = new PlatformTestHost();
            var registry = host.Registry();

            Assert.False(registry.IsValid("Taks"));             // fails closed on a typo
            Assert.False(registry.IsValid("CalendarEvents"));
            Assert.True(registry.IsValid("Task"));
            Assert.True(registry.IsValid("CalendarEvent"));
        }

        [Fact]
        public void The_record_picker_contract_is_unchanged_by_this_registration()
        {
            using var host = new PlatformTestHost();
            ITaskLinkResolver resolver = new TaskLinkResolver(host.Registry());

            // EntityRegistryTests pins this list. Registering Task/CalendarEvent deliberately sets
            // ListedInRecordPicker = false so a behaviour another tab's test guards is not changed
            // silently — enabling it is a separate, owner-visible decision.
            Assert.Equal(
                new[] { "SalesInvoice", "Customer", "ManufWorkOrder", "PosOrder", "Employee", "Project", "Item" },
                resolver.Types().Select(t => t.Key).ToArray());
        }

        [Fact]
        public void A_registered_event_name_validates_and_a_mismatched_one_does_not()
        {
            // This is what the registration BUYS: RecordAsync validates the name against the entity code,
            // so these are now publishable — and the earlier CalendarAttendee.Added shape would not be.
            Assert.True(BusinessEventTypes.TryValidate("Task.Created", "Task", out _));
            Assert.True(BusinessEventTypes.TryValidate("CalendarEvent.AttendeeAdded", "CalendarEvent", out _));
            Assert.False(BusinessEventTypes.TryValidate("CalendarAttendee.Added", "CalendarEvent", out _));
        }
    }
}
