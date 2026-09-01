using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // TASKS & CALENDAR INTEGRATION — Phase 10 tests.
    //
    // Storage is SQLite over a shared in-memory connection with the REAL CrossDbContext model, the same
    // choice PlatformTestHost makes and for the same reason: it honours transactions, and the mapping
    // under test is the production mapping.
    //
    // The notification recorder captures what WOULD be delivered. That is deliberate: this increment
    // makes no claim about email/push/WhatsApp, so the tests assert the decision (who is told, once)
    // rather than a delivery nobody implemented.
    // ==========================================================================================

    internal sealed class RecordedNotification
    {
        public int RecipientEmployeeId { get; init; }
        public string? Type { get; init; }
        public int? RefId { get; init; }
        public int? CompanyId { get; init; }
        public int? ActorEmployeeId { get; init; }
        public string? DedupKey { get; init; }
        public string? EntityType { get; init; }
        public int? EntityId { get; init; }
        public string? Url { get; init; }
        public string? BodyAr { get; init; }
    }

    /// A recorder that ALSO persists a Notification row, so the service's own duplicate check — which
    /// reads the Notifications table — is exercised for real rather than mocked away.
    internal sealed class RecordingNotificationService : INotificationService
    {
        private readonly CrossDbContext _db;
        public List<RecordedNotification> Sent { get; } = new();

        public RecordingNotificationService(CrossDbContext db) { _db = db; }

        public async Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
            string? bodyAr, string? bodyEn, string type, int? refId = null,
            string? url = null, int? companyId = null, int? actorEmployeeId = null,
            string? priority = null, string? category = null, string? dedupKey = null,
            DateTime? expiresAt = null, string? icon = null,
            string? entityType = null, int? entityId = null)
        {
            Sent.Add(new RecordedNotification
            {
                RecipientEmployeeId = recipientEmployeeId, Type = type, RefId = refId, CompanyId = companyId,
                ActorEmployeeId = actorEmployeeId, DedupKey = dedupKey, EntityType = entityType,
                EntityId = entityId, Url = url, BodyAr = bodyAr
            });

            _db.Notifications.Add(new Notification
            {
                RecipientEmployeeID = recipientEmployeeId, TitleAr = titleAr, TitleEn = titleEn,
                BodyAr = bodyAr, BodyEn = bodyEn, Type = type, RefId = refId, CompanyID = companyId,
                ActorEmployeeID = actorEmployeeId, DedupKey = dedupKey, Url = url,
                EntityType = entityType, EntityId = entityId, IsRead = false
            });
            await _db.SaveChangesAsync();
        }

        public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
            string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
            string type, int? refId = null, int? exceptEmployeeId = null) => Task.FromResult(0);
    }

    internal sealed class TcFixture : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly CompanyScopeHolder _holder = new();

        public CrossDbContext Db { get; }
        public RecordingNotificationService Notifier { get; }
        public ITaskNotificationService Notifications { get; }
        public ITaskOverdueSweepService Overdue { get; }

        /// The REAL publisher over the REAL BusinessEventService, not a recorder. UAT defect 4 was that the
        /// overdue sweep recorded no Task.BecameOverdue event, and its idempotency lives in the kernel's
        /// DedupKey handling — a stub publisher would assert that we called something, which is the part
        /// that was never in doubt. With the real one, a test can count rows in BusinessEvents.
        public CrossBuy.BL.TasksCalendar.ITaskCalendarEventPublisher Events { get; }

        public TcFixture(int companyId = 1)
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
            Notifications = new TaskNotificationService(Db, Notifier);

            // ONE accessor, shared by the event service and the sweep. The sweep needs it too: it will only
            // publish a Task.BecameOverdue event when a context is resolvable, because RecordAsync throws
            // otherwise and a background sweep must not lose its notifications over a missing event. See
            // TaskOverdueSweepService.EventsSkippedNoContext.
            var contexts = new StubContextAccessor(PlatformTestHost.DefaultContext(companyId));

            Events = new CrossBuy.BL.TasksCalendar.TaskCalendarEventPublisher(
                new CrossBuy.BL.Platform.BusinessEventService(
                    Db,
                    new CrossBuy.BL.Platform.EntityRegistry(Db),
                    contexts,
                    NullLogger<CrossBuy.BL.Platform.BusinessEventService>.Instance));

            Overdue = new TaskOverdueSweepService(Db, Notifications, Events, contexts);
        }

        public CrossDbContext NewContext()
        {
            var o = new DbContextOptionsBuilder<CrossDbContext>()
                .UseSqlite(_conn)
                .AddInterceptors(new CompanyWriteGuardInterceptor(NullLogger<CompanyWriteGuardInterceptor>.Instance))
                .Options;
            return new CrossDbContext(o, _holder);
        }

        public async Task<Employee> SeedEmployeeAsync(int id, int companyId = 1, bool active = true)
        {
            // Employee declares ten NON-NULLABLE string columns. A seed that omits any of them fails at
            // the database, which would look like a defect in the code under test and is not one — so all
            // ten are set here even though only the company, the active flag and the id matter to a test.
            var e = new Employee
            {
                ID = id, EmpCompanyID = companyId, IsActive = active,
                FirstName = $"Emp{id}", LastName = "Test", FullName = $"Emp {id}",
                Address = "-", PhoneNumber = "-", Email = $"emp{id}@test.local",
                ProfileImage = "-", Gender = "-", MaritalStatus = "-", UserId = $"u{id}"
            };
            Db.Employee.Add(e);
            await Db.SaveChangesAsync();
            return e;
        }

        public async Task<TaskItem> SeedTaskAsync(int companyId = 1, int assignee = 10, int creator = 20,
            string status = "New", DateTime? dueUtc = null, string title = "Inspect the site")
        {
            var t = new TaskItem
            {
                CompanyId = companyId, Title = title, AssigneeEmployeeId = assignee,
                CreatedByEmployeeId = creator, Status = status, Priority = "Normal",
                DueDate = dueUtc, CreatedAt = DateTime.UtcNow
            };
            Db.TaskItems.Add(t);
            await Db.SaveChangesAsync();
            return t;
        }

        public void Dispose() { Db.Dispose(); _conn.Dispose(); }
    }

    // ==========================================================================================
    // PHASE 2 — TASK NOTIFICATIONS
    // ==========================================================================================
    public class TasksCalendarNotificationTests
    {
        [Fact]
        public async Task Assignment_notifies_the_assignee_with_company_actor_and_deep_link()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);
            var task = await f.SeedTaskAsync(assignee: 10, creator: 20);

            var outcomes = await f.Notifications.TaskAssignedAsync(task.ID, actorEmployeeId: 20, Guid.NewGuid());

            var sent = Assert.Single(f.Notifier.Sent);
            Assert.Equal(10, sent.RecipientEmployeeId);
            Assert.Equal(TaskNotificationKinds.Assigned, sent.Type);
            Assert.Equal(1, sent.CompanyId);
            Assert.Equal(20, sent.ActorEmployeeId);
            Assert.Equal(task.ID, sent.RefId);
            Assert.Equal(TaskCalendarEntityCodes.Task, sent.EntityType);
            Assert.Equal(task.ID, sent.EntityId);
            Assert.Contains($"taskId={task.ID}", sent.Url);
            Assert.True(Assert.Single(outcomes).Delivered);
        }

        [Fact]
        public async Task Reassignment_notifies_both_the_new_and_the_previous_assignee()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(11);
            await f.SeedEmployeeAsync(20);
            var task = await f.SeedTaskAsync(assignee: 11, creator: 20);

            await f.Notifications.TaskReassignedAsync(task.ID, previousAssigneeId: 10, actorEmployeeId: 20, Guid.NewGuid());

            var recipients = f.Notifier.Sent.Select(s => s.RecipientEmployeeId).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 10, 11 }, recipients);
        }

        [Fact]
        public async Task Completion_notifies_the_creator_and_not_the_actor()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);
            var task = await f.SeedTaskAsync(assignee: 10, creator: 20, status: "Done");

            // The assignee completed it; the creator is told, the assignee is not told about their own act.
            await f.Notifications.TaskCompletedAsync(task.ID, actorEmployeeId: 10, Guid.NewGuid());

            var sent = Assert.Single(f.Notifier.Sent);
            Assert.Equal(20, sent.RecipientEmployeeId);
        }

        [Fact]
        public async Task The_actor_is_never_notified_of_their_own_action()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            var task = await f.SeedTaskAsync(assignee: 10, creator: 10);

            var outcomes = await f.Notifications.TaskAssignedAsync(task.ID, actorEmployeeId: 10, Guid.NewGuid());

            Assert.Empty(f.Notifier.Sent);
            Assert.Equal("SelfActor", Assert.Single(outcomes).Result);
        }

        [Fact]
        public async Task A_repeat_of_the_same_event_does_not_notify_twice()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);
            var task = await f.SeedTaskAsync(assignee: 10, creator: 20);
            var correlation = Guid.NewGuid();

            await f.Notifications.TaskAssignedAsync(task.ID, 20, correlation);
            var second = await f.Notifications.TaskAssignedAsync(task.ID, 20, correlation);
            var third = await f.Notifications.TaskAssignedAsync(task.ID, 20, Guid.NewGuid());   // different correlation

            Assert.Single(f.Notifier.Sent);
            Assert.Equal("Duplicate", Assert.Single(second).Result);
            // Even a NEW correlation id does not re-notify: the occurrence, not the call, is what is unique.
            Assert.Equal("Duplicate", Assert.Single(third).Result);
        }

        [Fact]
        public async Task Idempotency_survives_the_recipient_reading_the_notification()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);
            var task = await f.SeedTaskAsync(assignee: 10, creator: 20);

            await f.Notifications.TaskAssignedAsync(task.ID, 20, Guid.NewGuid());

            // The platform's own dedupKey only guards UNREAD rows. Mark it read and retry: a second copy
            // here would be exactly the duplicate the requirement forbids.
            foreach (var n in f.Db.Notifications) { n.IsRead = true; n.ReadAt = DateTime.UtcNow; }
            await f.Db.SaveChangesAsync();

            var retry = await f.Notifications.TaskAssignedAsync(task.ID, 20, Guid.NewGuid());

            Assert.Single(f.Notifier.Sent);
            Assert.Equal("Duplicate", Assert.Single(retry).Result);
        }

        [Fact]
        public async Task An_employee_of_another_company_is_never_notified()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10, companyId: 2);        // assignee belongs to company 2
            await f.SeedEmployeeAsync(20, companyId: 1);
            var task = await f.SeedTaskAsync(companyId: 1, assignee: 10, creator: 20);

            var outcomes = await f.Notifications.TaskAssignedAsync(task.ID, 20, Guid.NewGuid());

            Assert.Empty(f.Notifier.Sent);
            Assert.Equal("NotAuthorized", Assert.Single(outcomes).Result);
        }

        [Fact]
        public async Task An_inactive_employee_is_not_notified()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10, active: false);
            await f.SeedEmployeeAsync(20);
            var task = await f.SeedTaskAsync(assignee: 10, creator: 20);

            var outcomes = await f.Notifications.TaskAssignedAsync(task.ID, 20, Guid.NewGuid());

            Assert.Empty(f.Notifier.Sent);
            Assert.Equal("NotAuthorized", Assert.Single(outcomes).Result);
        }

        [Fact]
        public async Task A_due_date_change_notifies_once_per_change_not_once_per_call()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);
            var original = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
            var task = await f.SeedTaskAsync(assignee: 10, creator: 20, dueUtc: original);

            // moved to the 3rd
            task.DueDate = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
            await f.Db.SaveChangesAsync();
            await f.Notifications.TaskDueDateChangedAsync(task.ID, original, 20, Guid.NewGuid());
            await f.Notifications.TaskDueDateChangedAsync(task.ID, original, 20, Guid.NewGuid());   // retry
            Assert.Single(f.Notifier.Sent);

            // moved again to the 5th — a genuinely new change, so a second notification is correct
            var second = task.DueDate!.Value;
            task.DueDate = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);
            await f.Db.SaveChangesAsync();
            await f.Notifications.TaskDueDateChangedAsync(task.ID, second, 20, Guid.NewGuid());
            Assert.Equal(2, f.Notifier.Sent.Count);
        }

        [Fact]
        public async Task A_task_whose_assignee_is_also_its_creator_is_notified_once_not_twice()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            var task = await f.SeedTaskAsync(assignee: 10, creator: 10);

            // A system actor (null) so the self-actor rule does not mask the de-duplication being tested.
            await f.Notifications.TaskCancelledAsync(task.ID, actorEmployeeId: null, Guid.NewGuid());

            Assert.Single(f.Notifier.Sent);
        }

        [Fact]
        public async Task A_notification_body_carries_the_title_and_never_the_description()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedEmployeeAsync(20);
            var task = await f.SeedTaskAsync(assignee: 10, creator: 20, title: "Inspect the site");
            task.Description = "CONFIDENTIAL: budget overrun detail";
            await f.Db.SaveChangesAsync();

            await f.Notifications.TaskAssignedAsync(task.ID, 20, Guid.NewGuid());

            var sent = Assert.Single(f.Notifier.Sent);
            Assert.Equal("Inspect the site", sent.BodyAr);
            Assert.DoesNotContain("CONFIDENTIAL", sent.BodyAr!);
        }
    }

    // ==========================================================================================
    // PHASE 9 — OVERDUE SWEEP
    // ==========================================================================================
    public class TasksCalendarOverdueTests
    {
        [Fact]
        public async Task An_overdue_task_notifies_once_however_often_the_sweep_runs()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedTaskAsync(assignee: 10, dueUtc: DateTime.UtcNow.AddDays(-1));

            var first = await f.Overdue.SweepAsync(1);
            var second = await f.Overdue.SweepAsync(1);       // the 15-minute timer ticks again
            var third = await f.Overdue.SweepAsync(1);

            Assert.Equal(1, first.Notified);
            Assert.Equal(0, second.Notified);
            Assert.Equal(1, second.AlreadyNotified);
            Assert.Equal(0, third.Notified);
            Assert.Single(f.Notifier.Sent);
        }

        [Fact]
        public async Task A_completed_task_is_never_reported_overdue()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedTaskAsync(assignee: 10, status: "Done", dueUtc: DateTime.UtcNow.AddDays(-5));

            var result = await f.Overdue.SweepAsync(1);

            Assert.Equal(0, result.Examined);
            Assert.Empty(f.Notifier.Sent);
        }

        [Fact]
        public async Task Moving_the_due_date_forward_makes_it_a_new_occurrence()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            var task = await f.SeedTaskAsync(assignee: 10, dueUtc: DateTime.UtcNow.AddDays(-2));

            await f.Overdue.SweepAsync(1);
            Assert.Single(f.Notifier.Sent);

            // Rescheduled — and then missed again. That is a new fact and deserves a new notification.
            task.DueDate = DateTime.UtcNow.AddDays(-1);
            await f.Db.SaveChangesAsync();
            var after = await f.Overdue.SweepAsync(1);

            Assert.Equal(1, after.Notified);
            Assert.Equal(2, f.Notifier.Sent.Count);
        }

        [Fact]
        public async Task A_future_due_date_is_not_overdue()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedTaskAsync(assignee: 10, dueUtc: DateTime.UtcNow.AddDays(3));

            var result = await f.Overdue.SweepAsync(1);

            Assert.Equal(0, result.Examined);
            Assert.Empty(f.Notifier.Sent);
        }

        [Fact]
        public async Task The_sweep_touches_only_the_company_it_was_given()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10, companyId: 1);
            await f.SeedEmployeeAsync(11, companyId: 2);
            await f.SeedTaskAsync(companyId: 1, assignee: 10, dueUtc: DateTime.UtcNow.AddDays(-1));
            await f.SeedTaskAsync(companyId: 2, assignee: 11, dueUtc: DateTime.UtcNow.AddDays(-1));

            var one = await f.Overdue.SweepAsync(1);

            Assert.Equal(1, one.Examined);
            Assert.Equal(10, Assert.Single(f.Notifier.Sent).RecipientEmployeeId);
        }

        [Fact]
        public async Task An_unresolved_company_sweeps_nothing_rather_than_everything()
        {
            using var f = new TcFixture();
            await f.SeedEmployeeAsync(10);
            await f.SeedTaskAsync(assignee: 10, dueUtc: DateTime.UtcNow.AddDays(-1));

            var result = await f.Overdue.SweepAsync(0);

            Assert.Equal(0, result.Examined);
            Assert.Empty(f.Notifier.Sent);
        }
    }
}
