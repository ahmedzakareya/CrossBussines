using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests
{
    /// EVERY MUTATING TASKS ENDPOINT ASKS BEFORE IT WRITES.
    ///
    /// Ten of them did not. They resolved the company — so a cross-company write was already impossible —
    /// and then handed the caller's id straight to a service that filters on company and nothing else. Any
    /// employee with a session could retitle, reassign, complete, delete or log time against ANY task in
    /// their own company, and could switch the auto-generator on for everybody. BoardMove, the drag-and-drop
    /// twin of ChangeStatus, had carried TasksActions.Edit the whole time, so the same operation was
    /// authorized from the board and unauthorized from the list.
    ///
    /// WHY NULL SERVICES ARE THE ASSERTION, not an oversight. Every test here drives a REFUSAL, and on a
    /// refusal the gate must return before any service is touched. Passing null for the services makes
    /// "it reached the service anyway" observable: a missing or late gate throws NullReferenceException
    /// instead of returning the refusal, and the test fails loudly. It is a recording stub with no stub.
    ///
    /// AND THE ACTION ASKED FOR IS PINNED, not just the refusal. RecordingAccess captures every
    /// (action, taskId) pair the controller asks about, so these assert the authority MAPPING — that
    /// finishing work asks Complete, that moving an assignee asks Reassign, that the generator asks Manage —
    /// rather than the much weaker "something was asked".
    public sealed class TaskMutationAuthorizationTests
    {
        private const int Company = 7;
        private const int Me = 11;

        private sealed class RecordingAccess : ITasksAccessService
        {
            public readonly List<(string Action, int? TaskId)> Asked = new();
            private readonly Func<string, bool> _answer;
            public RecordingAccess(Func<string, bool>? answer = null) => _answer = answer ?? (_ => false);

            public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
                CancellationToken cancellationToken = default)
            {
                Asked.Add((action, target?.TaskId));
                return Task.FromResult(_answer(action));
            }

            public Task<AccessScope> ResolveScopeAsync(BusinessContext context, string action,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class Company7 : IRequestCompanyResolver
        {
            public Task<CompanyResolution> ResolveAsync(int? requestSuppliedCompanyId = null,
                CancellationToken cancellationToken = default)
                => Task.FromResult(CompanyResolution.Resolved(Company, Me, null));
        }

        /// Only the four members these paths reach are implemented. Everything else throws, so a test that
        /// silently started exercising another code path fails instead of quietly passing.
        private sealed class StubTasks : ITaskService
        {
            public TaskItem? Row;
            public int Saves, StatusChanges, Deletes;

            public Task<TaskItem?> GetAsync(int companyId, int id) => Task.FromResult(Row);
            public Task<(bool ok, string? error, int id)> SaveAsync(int companyId, TaskSaveInput input, int currentEmployeeId)
            { Saves++; return Task.FromResult((true, (string?)null, 1)); }
            public Task<(bool ok, string? error)> ChangeStatusAsync(int companyId, int id, string status, int currentEmployeeId)
            { StatusChanges++; return Task.FromResult((true, (string?)null)); }
            public Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
            { Deletes++; return Task.FromResult((true, (string?)null)); }

            public Task<List<TaskRowDto>> GetTasksAsync(int c, string s, int e, string? st, string? p, string? q,
                int page = 1, int pageSize = 25, string? view = null, int? assignee = null, string? sort = null)
                => throw new NotSupportedException();
            public Task<int> CountTasksAsync(int c, string s, int e, string? st, string? p, string? q,
                string? view = null, int? assignee = null) => throw new NotSupportedException();
            public Task<TaskKpiDto> GetKpisAsync(int c, string s, int e) => throw new NotSupportedException();
            public Task<List<(int Id, string Name)>> ActiveEmployeesAsync(int c) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> RequestHoldAsync(int c, int i, int a, string? r, CancellationToken t = default) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> ApproveHoldAsync(int c, int i, int a, CancellationToken t = default) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> RejectHoldAsync(int c, int i, int a, string? d, CancellationToken t = default) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> DirectHoldAsync(int c, int i, int a, string? r, CancellationToken t = default) => throw new NotSupportedException();
            public Task<(bool ok, string? error)> ResumeHoldAsync(int c, int i, int a, CancellationToken t = default) => throw new NotSupportedException();
            public Task<bool> CanDecideHoldAsync(int c, int i, int a, CancellationToken t = default) => throw new NotSupportedException();
        }

        /// The refusal path writes its message through TempData (the redirect-to-toast bridge every
        /// non-AJAX mutation in this product uses), so the harness has to supply one or the refusal
        /// NullReferences on its way out and every test fails for a reason that is not the subject.
        private sealed class NoOpTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
        {
            public IDictionary<string, object?> LoadTempData(Microsoft.AspNetCore.Http.HttpContext c)
                => new Dictionary<string, object?>();
            public void SaveTempData(Microsoft.AspNetCore.Http.HttpContext c, IDictionary<string, object?> v) { }
        }

        /// Only the ALLOW paths need this: CurrentEmployeeId() reads the session, and a refused caller
        /// never gets that far — which is itself worth noticing, because it means the gate sits upstream
        /// of everything the action does, not beside it.
        private sealed class MemorySession : Microsoft.AspNetCore.Http.ISession
        {
            private readonly Dictionary<string, byte[]> _v = new();
            public bool IsAvailable => true;
            public string Id => "test";
            public IEnumerable<string> Keys => _v.Keys;
            public void Clear() => _v.Clear();
            public Task CommitAsync(CancellationToken t = default) => Task.CompletedTask;
            public Task LoadAsync(CancellationToken t = default) => Task.CompletedTask;
            public void Remove(string key) => _v.Remove(key);
            public void Set(string key, byte[] value) => _v[key] = value;
            public bool TryGetValue(string key, out byte[] value) => _v.TryGetValue(key, out value!);
        }

        private static TasksController Build(RecordingAccess access, ITaskService? tasks = null)
        {
            var controller = new TasksController(
                tasks: tasks!, links: null!, ts: null!, cost: null!, billing: null!, gen: null!, reports: null!,
                matcher: null!, tasksAccess: access,
                businessContexts: new StubContextAccessor(
                    new BusinessContext { CompanyId = Company, EmployeeId = Me, Source = BusinessContextSource.Test }),
                company: new Company7(), accounting: null!, checklist: null!, deps: null!, templates: null!,
                escalation: null!, services: new ServiceCollection().BuildServiceProvider(),
                files: null!, env: null!);
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { Session = new MemorySession() };
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };
            controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(http, new NoOpTempData());
            return controller;
        }

        /// A refused mutation redirects with a toast rather than writing. It is NOT Forbid(): these are
        /// posted forms answered by a redirect, and a bare 403 would blank the screen the user was on.
        private static void AssertRefused(IActionResult result) =>
            Assert.IsType<RedirectToActionResult>(result);

        // ----------------------------------------------------------------------------------------------
        // THE TEN. Each one refused, and the service never reached — proven by the null service above.
        // ----------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_refused_caller_cannot_change_a_task_status()
        {
            var access = new RecordingAccess();
            var tasks = new StubTasks { Row = new TaskItem { ID = 5, CompanyId = Company, Status = TaskStatuses.New } };
            AssertRefused(await Build(access, tasks).ChangeStatus(5, TaskStatuses.InProgress));
            Assert.Equal(0, tasks.StatusChanges);
        }

        [Fact]
        public async Task A_refused_caller_cannot_delete_a_task()
        {
            var access = new RecordingAccess();
            var tasks = new StubTasks();
            AssertRefused(await Build(access, tasks).Delete(5));
            Assert.Equal(0, tasks.Deletes);
        }

        [Fact]
        public async Task A_refused_caller_cannot_save_an_existing_task()
        {
            var access = new RecordingAccess();
            var tasks = new StubTasks { Row = new TaskItem { ID = 5, CompanyId = Company, AssigneeEmployeeId = Me } };
            AssertRefused(await Build(access, tasks).Save(new TaskSaveInput { Id = 5, Title = "x", AssigneeEmployeeId = Me }));
            Assert.Equal(0, tasks.Saves);
        }

        [Fact]
        public async Task A_refused_caller_cannot_create_a_task()
        {
            var access = new RecordingAccess();
            var tasks = new StubTasks();
            AssertRefused(await Build(access, tasks).Save(new TaskSaveInput { Id = 0, Title = "x", AssigneeEmployeeId = Me }));
            Assert.Equal(0, tasks.Saves);
            Assert.Contains(access.Asked, a => a.Action == TasksActions.Create);
        }

        [Theory]
        [InlineData("StartTimer")]
        [InlineData("AddTime")]
        [InlineData("SaveAutoRule")]
        [InlineData("RunGenerator")]
        public async Task A_refused_caller_reaches_no_timesheet_or_generator_service(string endpoint)
        {
            var access = new RecordingAccess();
            var c = Build(access);   // every service null: reaching one is a NullReferenceException, not a pass
            IActionResult result = endpoint switch
            {
                "StartTimer" => await c.StartTimer(5),
                "AddTime" => await c.AddTime(5, new DateTime(2026, 1, 1), 1m, null),
                "SaveAutoRule" => await c.SaveAutoRule("x", true, null),
                "RunGenerator" => await c.RunGenerator(),
                _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
            };
            AssertRefused(result);
            Assert.NotEmpty(access.Asked);
        }

        // ----------------------------------------------------------------------------------------------
        // THE MAPPING. Which right each operation actually asks for.
        // ----------------------------------------------------------------------------------------------

        /// FINISHING IS ITS OWN RIGHT. Edit, Complete and Reopen resolve to the same predicate today, so
        /// this changes nobody's access — it pins the QUESTION, so that if completing ever becomes stricter
        /// than editing the endpoint is already asking the stricter one.
        [Fact]
        public async Task Moving_a_task_to_done_asks_for_complete()
        {
            var access = new RecordingAccess();
            var tasks = new StubTasks { Row = new TaskItem { ID = 5, CompanyId = Company, Status = TaskStatuses.InProgress } };
            await Build(access, tasks).ChangeStatus(5, TaskStatuses.Done);
            Assert.Contains(access.Asked, a => a.Action == TasksActions.Complete && a.TaskId == 5);
            Assert.DoesNotContain(access.Asked, a => a.Action == TasksActions.Reopen);
        }

        [Fact]
        public async Task Moving_a_done_task_back_asks_for_reopen()
        {
            var access = new RecordingAccess();
            var tasks = new StubTasks { Row = new TaskItem { ID = 5, CompanyId = Company, Status = TaskStatuses.Done } };
            await Build(access, tasks).ChangeStatus(5, TaskStatuses.InProgress);
            Assert.Contains(access.Asked, a => a.Action == TasksActions.Reopen && a.TaskId == 5);
        }

        [Fact]
        public async Task An_ordinary_status_move_asks_for_edit()
        {
            var access = new RecordingAccess();
            var tasks = new StubTasks { Row = new TaskItem { ID = 5, CompanyId = Company, Status = TaskStatuses.New } };
            await Build(access, tasks).ChangeStatus(5, TaskStatuses.InProgress);
            Assert.Contains(access.Asked, a => a.Action == TasksActions.Edit && a.TaskId == 5);
            Assert.DoesNotContain(access.Asked, a => a.Action == TasksActions.Complete || a.Action == TasksActions.Reopen);
        }

        /// THE ONE DELIBERATE TIGHTENING, and the reason it needs a test of its own. TasksAccessService has
        /// always said Reassign is creator-or-manager and never the assignee — "never the assignee handing
        /// their work on" — and nothing in this controller ever asked for it, so an assignee could move their
        /// own task to somebody else through the ordinary save form. Edit alone must no longer be enough.
        [Fact]
        public async Task Changing_the_assignee_asks_for_reassign_and_not_merely_edit()
        {
            var access = new RecordingAccess(a => a == TasksActions.Edit);   // may edit, may NOT reassign
            var tasks = new StubTasks { Row = new TaskItem { ID = 5, CompanyId = Company, AssigneeEmployeeId = Me } };

            AssertRefused(await Build(access, tasks).Save(
                new TaskSaveInput { Id = 5, Title = "x", AssigneeEmployeeId = 99 }));

            Assert.Contains(access.Asked, a => a.Action == TasksActions.Reassign && a.TaskId == 5);
            Assert.Equal(0, tasks.Saves);
        }

        /// ...and the same save WITHOUT an assignee change must still go through on Edit alone, or the
        /// tightening would have quietly become "only a manager may edit a task".
        [Fact]
        public async Task Saving_without_moving_the_assignee_needs_only_edit()
        {
            var access = new RecordingAccess(a => a == TasksActions.Edit);
            var tasks = new StubTasks { Row = new TaskItem { ID = 5, CompanyId = Company, AssigneeEmployeeId = Me } };

            await Build(access, tasks).Save(new TaskSaveInput { Id = 5, Title = "x", AssigneeEmployeeId = Me });

            Assert.Equal(1, tasks.Saves);
            Assert.DoesNotContain(access.Asked, a => a.Action == TasksActions.Reassign);
        }

        /// Deletion and the generator are company-wide administration, so they ask the company-wide question.
        /// Delete on Manage is a TIGHTENING — any employee could delete any task in their company before —
        /// and it is Manage only because TasksActions has no Delete to ask for. See the baseline history note.
        [Theory]
        [InlineData("Delete")]
        [InlineData("SaveAutoRule")]
        [InlineData("RunGenerator")]
        public async Task Company_wide_task_administration_asks_for_manage(string endpoint)
        {
            var access = new RecordingAccess();
            var tasks = new StubTasks();
            var c = Build(access, tasks);
            IActionResult result = endpoint switch
            {
                "Delete" => await c.Delete(5),
                "SaveAutoRule" => await c.SaveAutoRule("x", true, null),
                "RunGenerator" => await c.RunGenerator(),
                _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
            };
            AssertRefused(result);
            Assert.Contains(access.Asked, a => a.Action == TasksActions.Manage);
        }

        /// A REFUSAL MUST NOT NAME ITS CAUSE. "No such task", "another company's task" and "yours but not for
        /// this action" are answered identically, exactly as TasksAccessService answers them identically
        /// underneath — a distinguishable refusal is a probe for which task ids exist.
        [Fact]
        public async Task Every_refusal_reads_the_same_whatever_the_cause()
        {
            var access = new RecordingAccess();
            var tasks = new StubTasks();   // GetAsync returns null: the "no such task" shape

            var absent = await Build(access, tasks).ChangeStatus(999999, TaskStatuses.InProgress);
            tasks.Row = new TaskItem { ID = 5, CompanyId = Company, Status = TaskStatuses.New };
            var present = await Build(access, tasks).ChangeStatus(5, TaskStatuses.InProgress);

            var a = Assert.IsType<RedirectToActionResult>(absent);
            var p = Assert.IsType<RedirectToActionResult>(present);
            Assert.Equal(a.ActionName, p.ActionName);
            Assert.Equal(a.ControllerName, p.ControllerName);
        }
    }
}
