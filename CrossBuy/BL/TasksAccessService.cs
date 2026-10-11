using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
    // Stage 1 Batch C — task authorization, built on the anchors that actually exist.
    //
    // `TasksController` carried ZERO authorization on its 13 mutating actions: any signed-in employee could
    // read, edit, reassign and complete every task in the company.
    //
    // WHAT THE DATA SUPPORTS, and therefore what this service implements:
    //   AssigneeEmployeeId · CreatedByEmployeeId · EntityType/EntityId (the linked object) · CompanyId ·
    //   the company-intersected manager hierarchy.
    // WHAT IT DOES NOT: there is no team, no follower, no visibility tier and no branch on TaskItem, and no
    // workflow-task marker. Inventing any of them would be a rule with nothing behind it. See ADR-029.
    //
    // NOTE ON CASING: TaskItem.CompanyId — NOT CompanyID like every other entity. A predicate copied from
    // another service will not compile, which is the good outcome; a test asserts the property name so a
    // future rename cannot silently drop the company filter.
    public interface ITasksAccessService
    {
        Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null,
            CancellationToken cancellationToken = default);

        // The set-shaped companion. THE reason it exists: a Unified Inbox cannot ask per row.
        Task<AccessScope> ResolveScopeAsync(
            BusinessContext context, string action, CancellationToken cancellationToken = default);
    }

    // Eight actions. `delete` is absent — TasksController has no delete action. `confidential-view` is absent —
    // TaskItem has no visibility tier to confer it.
    public static class TasksActions
    {
        public const string Read = "read";
        public const string Create = "create";
        public const string Edit = "edit";
        public const string Assign = "assign";        // set the assignee when creating
        public const string Reassign = "reassign";    // move an existing task to someone else
        public const string Complete = "complete";
        public const string Reopen = "reopen";
        public const string Manage = "manage";        // company-wide task administration

        public static readonly IReadOnlyCollection<string> All = new[]
        { Read, Create, Edit, Assign, Reassign, Complete, Reopen, Manage };
    }

    public static class TasksRoles
    {
        public const string TasksAdministrator = "TasksAdministrator";  // every task in the company
        public const string TasksSupervisor = "TasksSupervisor";        // may reassign within their scope
        public const string TasksViewer = "TasksViewer";                // read-only, company-wide

        public static readonly IReadOnlyList<string> All =
            new[] { TasksAdministrator, TasksSupervisor, TasksViewer };
    }

    public sealed class TasksAccessService : ModuleAccessServiceBase, ITasksAccessService
    {
        private readonly CrossDbContext _db;
        private readonly IOrgHierarchy _org;
        private readonly Func<IPlatformPermissionProvider> _permissions;
        private readonly ILogger<TasksAccessService> _log;

        // IPlatformPermissionProvider is resolved LAZILY, and that is a correctness requirement, not a style
        // preference.
        //
        // THE CYCLE IT BREAKS — it hung the whole application at startup:
        //
        //   IEnumerable<IModuleAccessService>          (this service is registered as one)
        //     → TasksAccessService
        //       → IPlatformPermissionProvider
        //         → IEnumerable<IModulePermissionAdapter>
        //           → HrPermissionAdapter (…and the other four)
        //             → IEnumerable<IModuleAccessService>   ← back to the collection being built
        //
        // Nothing surfaced it until something resolved the WHOLE collection at boot
        // (PermissionScopeStartupValidator). Kestrel never started — no exception, no crash, just an
        // application that loads forever and serves a blank page.
        //
        // A Func<> defers resolution to the first linked-entity check, by which time the scope already holds a
        // constructed TasksAccessService, so the graph closes on a cached instance instead of recursing.
        //
        // Note that `ValidateOnBuild` did NOT catch this: it validates call sites without recursing through
        // IEnumerable<T>. Only RESOLVING the collection does — which is what Stage1DiWiringTests now asserts.
        // SOURCE-RECORD VISIBILITY, as a Func for exactly the reason the permission provider is one: the
        // object graph behind it reaches other IModuleAccessService instances, and this class IS one.
        // Deferring resolution past construction is what keeps startup from hanging on a cycle.
        //
        // OPTIONAL so the two existing construction sites keep compiling, and null means the check is not
        // available rather than that it passed - see the call site, which treats an absent visibility
        // service as 'no provider registered', the same answer it gets for a type nobody guards.
        private readonly Func<ITaskSourceVisibility>? _sources;

        // SHF-85 — SOURCE-EXPLICIT TASK READ ENTRANCE, and the only thing that entry authorises here.
        //
        // Some work names NOBODY on purpose. An inventory approval is authorised by whoever holds the role
        // minus the person who requested it, so there is no individual to write onto the task when it is
        // created, and AssigneeEmployeeId and CreatedByEmployeeId are both 0. Every breadth this service
        // resolves tests one of those two columns or the whole tenant, so the people entitled to that work
        // are excluded by Own and Team, and the only breadth that includes them - Company - includes every
        // other task in the company too.
        //
        // This asks the ONE question that closes that gap: has a source which explicitly opted into entrance
        // named this exact record for this caller. It grants READ and nothing else, it is consulted only
        // after the company, linked-entity and source-visibility gates have already passed, and every
        // uncertain answer is no. Deferred for the same construction-cycle reason as _sources, and optional
        // for the same reason: absent means no entrance, never an entrance that passed.
        private readonly Func<ITaskSourceReadEntrance>? _entrance;

        public TasksAccessService(
            CrossDbContext db, IPlatformRoleDirectory roles, IOrgHierarchy org,
            Func<IPlatformPermissionProvider> permissions, ILogger<TasksAccessService> log,
            Func<ITaskSourceVisibility>? sources = null,
            Func<ITaskSourceReadEntrance>? entrance = null) : base(roles, log)
        { _db = db; _org = org; _permissions = permissions; _log = log; _sources = sources; _entrance = entrance; }

        public override string Scope => EntityRegistry.ScopeTasks;
        public override IReadOnlyCollection<string> Actions => TasksActions.All;

        protected override async Task<bool> EvaluateAsync(
            BusinessContext context, string action, PermissionTarget? target,
            IReadOnlyList<RoleGrant> grants, bool bootstrapOpen, CancellationToken cancellationToken)
        {
            int me = context.EmployeeId!.Value;
            bool admin = Holds(grants, TasksRoles.TasksAdministrator);
            bool supervisor = Holds(grants, TasksRoles.TasksSupervisor);
            bool viewer = Holds(grants, TasksRoles.TasksViewer);

            // `create` is not about an existing record, so it is decided before any task lookup.
            if (action == TasksActions.Create)
                return bootstrapOpen || admin || supervisor;

            // Company-wide administration short-circuits the record rules — but still not across companies:
            // the task's own company is verified below.
            bool companyWide = action switch
            {
                TasksActions.Read => admin || supervisor || viewer,
                TasksActions.Edit or TasksActions.Complete or TasksActions.Reopen or TasksActions.Assign
                    => admin || supervisor,
                // REASSIGN is deliberately stronger than edit: moving someone else's work is a supervisory
                // act, so an ordinary assignee cannot do it even to their own task (see below).
                TasksActions.Reassign => admin || supervisor,
                TasksActions.Manage => admin,
                _ => false,
            };

            // No task named ⇒ this is a module-level question (e.g. "may I see the task screen at all").
            if (target?.TaskId is not int taskId || taskId <= 0)
                return bootstrapOpen ? action != TasksActions.Manage : companyWide;

            // ---- the task itself. Company comes from the ROW (TaskItem.CompanyId), never the request. ----
            var task = await _db.TaskItems.AsNoTracking()
                .Where(t => t.ID == taskId)
                .Select(t => new { t.CompanyId, t.AssigneeEmployeeId, t.CreatedByEmployeeId, t.EntityType, t.EntityId })
                .FirstOrDefaultAsync(cancellationToken);

            // Absent and other-company answer identically — ids cannot be probed.
            if (task == null || task.CompanyId != context.CompanyId) return false;

            // ---- the LINKED entity gate, checked through the platform provider ----
            //
            // A task about a SalesInvoice must not become a way to read that invoice's existence to someone
            // who may not see it. Asked through IPlatformPermissionProvider so the answer comes from the
            // owning module rather than being re-derived here.
            if (task.EntityType != null && task.EntityId is > 0)
            {
                var decision = await _permissions().CanAsync(
                    context, task.EntityType, task.EntityId.Value, PlatformActions.View, cancellationToken);
                if (!decision.Allowed)
                {
                    _log.LogInformation(
                        "Tasks: '{Action}' on task {Task} denied — the linked {EntityType} {EntityId} is not " +
                        "viewable by employee {Employee}. Reason: {Reason}",
                        action, taskId, task.EntityType, task.EntityId, me, decision.Reason);
                    return false;
                }
            }

            // ---- and the SAME source-record eligibility the LIST applies ----
            //
            // Without this the detail page would be weaker than the list it opens from, which is the classic
            // shape of this bug: filter the rows, forget the row. A caller who cannot see a billing approval
            // in any list must not reach it by typing its task id either.
            //
            // It is asked ONLY for types a provider guards. Everything else keeps the answer it had, because
            // the platform View check above is already the rule for those - see TaskSourceVisibility, whose
            // header explains why registering a provider is how a module opts into the stricter treatment.
            if (task.EntityType != null && task.EntityId is > 0 && _sources != null)
            {
                var probe = new[] { new CrossBuy.Models.Context.Tasks.TaskItem { ID = taskId, CompanyId = task.CompanyId, EntityType = task.EntityType, EntityId = task.EntityId } }
                    .AsQueryable();
                // The probe is a ONE-ROW in-memory queryable, so the same predicate the list composes in SQL is
                // evaluated here against a single row. Same code, same answer - not a second copy of the rule.
                var allowed = (await _sources().RestrictAsync(probe, context, cancellationToken)).Any();
                if (!allowed)
                {
                    _log.LogInformation(
                        "Tasks: '{Action}' on task {Task} denied - the caller is not eligible for the linked " +
                        "{EntityType} {EntityId} it represents.",
                        action, taskId, task.EntityType, task.EntityId);
                    return false;
                }
            }

            if (companyWide) return true;

            // ---- record-level, in order of strength ----
            bool isAssignee = task.AssigneeEmployeeId == me;
            bool isCreator = task.CreatedByEmployeeId == me;

            // A manager reaches a report's task through the company-intersected hierarchy — never through a
            // raw org walk, because Hierarchical carries no CompanyID.
            bool managesAssignee = false;
            if (!isAssignee && !isCreator)
            {
                var team = await _org.DirectAndIndirectReportsAsync(context.CompanyId, me, cancellationToken);
                managesAssignee = team.Contains(task.AssigneeEmployeeId);
            }

            bool related = isAssignee || isCreator || managesAssignee;

            // ---- SHF-85: source-explicit entrance, READ only ----
            //
            // Placed HERE deliberately, and the position is the whole safety argument. Everything above has
            // already run: the task's own CompanyId was checked against the context, IPlatformPermissionProvider
            // allowed the linked entity, and ITaskSourceVisibility did not refuse the row. So this can only
            // admit a task those three already permit — it relaxes none of them, and a caller refused by any
            // of them never reaches this line.
            //
            // READ ONLY. It is not consulted for edit, complete, reopen, reassign, assign or manage: being
            // entitled to REVIEW work that names nobody is not being entitled to reassign it, and the switch
            // below still governs all of those exactly as before.
            if (action == TasksActions.Read && !related && !companyWide && _entrance != null
                && await _entrance().AdmitsAsync(context, task.EntityType, task.EntityId, cancellationToken))
            {
                _log.LogInformation(
                    "Tasks: read on task {Task} admitted by its source. Employee {Employee} holds no task " +
                    "breadth covering it, and the {EntityType} {EntityId} it represents named them.",
                    taskId, me, task.EntityType, task.EntityId);
                return true;
            }

            // Bootstrap-open still requires a RELATIONSHIP for a specific task. This is the one place the
            // compatibility rule is deliberately narrower than "behave as today": before this batch every
            // employee could read every task, and preserving that for a named task would mean the module's
            // first record-level rule did nothing. Module-level questions stay open (above); a specific
            // task requires being related to it.
            if (bootstrapOpen) return related;

            return action switch
            {
                TasksActions.Read => related,
                TasksActions.Edit => isAssignee || isCreator || managesAssignee,
                // Completing/reopening requires access to THAT task — the assignee, its creator, or a manager.
                TasksActions.Complete or TasksActions.Reopen => related,
                // Reassign is supervisory: creator or manager, never the assignee handing their work on.
                TasksActions.Reassign => isCreator || managesAssignee,
                TasksActions.Assign => isCreator || managesAssignee,
                TasksActions.Manage => false,
                _ => false,
            };
        }

        // The SET-shaped answer. A consumer translates this into ONE predicate:
        //   Own     → AssigneeEmployeeId == me || CreatedByEmployeeId == me
        //   Team    → PrincipalIds.Contains(AssigneeEmployeeId)   (already company-intersected)
        //   Company → CompanyId == context.CompanyId
        public async Task<AccessScope> ResolveScopeAsync(
            BusinessContext context, string action, CancellationToken cancellationToken = default)
        {
            if (context == null || context.CompanyId <= 0) return AccessScope.None();
            if (!TasksActions.All.Contains(action, StringComparer.Ordinal)) return AccessScope.None();
            if (context.IsSystem) return SystemContextPolicy.Allows(action) ? AccessScope.Company(context.CompanyId) : AccessScope.None();
            if (context.Source == BusinessContextSource.Worker) return AccessScope.None();
            if (context.EmployeeId is not > 0) return AccessScope.None();

            var grants = await RoleDirectory.RolesAsync(context, Scope, cancellationToken);
            bool admin = Holds(grants, TasksRoles.TasksAdministrator);
            bool supervisor = Holds(grants, TasksRoles.TasksSupervisor);
            bool viewer = Holds(grants, TasksRoles.TasksViewer);

            bool companyWide = action switch
            {
                TasksActions.Read => admin || supervisor || viewer,
                TasksActions.Edit or TasksActions.Complete or TasksActions.Reopen
                    or TasksActions.Assign or TasksActions.Reassign => admin || supervisor,
                TasksActions.Manage => admin,
                _ => false,
            };
            if (companyWide) return AccessScope.Company(context.CompanyId);

            if (grants.Count == 0
                && !await RoleDirectory.AnyConfiguredAsync(context.CompanyId, Scope, cancellationToken))
            {
                // Bootstrap-open: the breadth matches the record rule above (a relationship is required), so
                // the two shapes agree. Returning Company here would make ResolveScopeAsync more permissive
                // than CanAsync — exactly the drift the agreement tests exist to catch.
                var bootstrapTeam = await _org.DirectAndIndirectReportsAsync(context.CompanyId, context.EmployeeId.Value, cancellationToken);
                return bootstrapTeam.Count > 1
                    ? AccessScope.Team(context.CompanyId, bootstrapTeam)
                    : AccessScope.Own(context.CompanyId);
            }

            if (action == TasksActions.Manage) return AccessScope.None();

            var team = await _org.DirectAndIndirectReportsAsync(context.CompanyId, context.EmployeeId.Value, cancellationToken);
            return team.Count > 1 ? AccessScope.Team(context.CompanyId, team) : AccessScope.Own(context.CompanyId);
        }
    }
}
