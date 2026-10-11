using CrossBuy.Models.Context.Tasks;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL
{
    // The authorized starting point for a task LIST.
    //
    // THE DEFECT THIS CLOSES. A generic task list read `TaskItems.Where(t => t.CompanyId == companyId)` and
    // called it a scope. Company is a TENANCY boundary, not an authorization one: it says the rows belong to
    // this installation's customer, not that this employee may read them. So any signed-in employee whose
    // company resolved could page the entire company — every title, description, assignee and linked-record
    // id — holding no task role and no right to the records behind the work.
    //
    // The per-task path was never like that. TasksAccessService.CanAsync checks the row AND asks
    // IPlatformPermissionProvider about the linked entity, so /Tasks/Detail refused what the list handed out.
    // Nothing here is a new rule; this is the rule the detail page already enforced, finally reaching the
    // list that went around it.
    //
    // TWO LAYERS, DELIBERATELY, because they answer different questions:
    //
    //   1. TASK SCOPE — may this caller read tasks, and whose? Answered by TasksAccessService.ResolveScopeAsync
    //      and turned into ONE predicate by TaskScopeQuery.WithinScope. This is the generic breadth: own,
    //      team, or the whole company for a role that genuinely carries it.
    //
    //   2. SOURCE-RECORD ELIGIBILITY — and of those, which are about records the caller may see? Answered by
    //      ITaskSourceVisibility, which asks the owning module.
    //
    // THEY INTERSECT AND NEVER UNION, and the order in the code is the proof: layer 2 composes onto the
    // IQueryable layer 1 returned, so it can only ever remove rows. A task administrator holding company
    // breadth still does not see a billing approval they are not eligible for; the two are AND, not OR.
    // Getting that backwards is the one mistake that would make this file worse than what it replaced.
    //
    //   3. SOURCE-EXPLICIT ENTRANCE — and, separately, is there work that names NOBODY which this caller is
    //      nonetheless authorised to do? Answered by ITaskSourceReadEntrance.
    //
    // THE THIRD LAYER IS A UNION, AND IT IS THE ONLY ONE, so it is worth being precise about what it is not.
    // It does not relax layers 1 and 2 and it does not apply to sources generally. It contributes a SECOND,
    // separately-built query whose every row had to be named — by entity type AND id — by a source that
    // explicitly implements ITaskSourceReadEntranceProvider. A visibility provider contributes nothing here;
    // a provider-less type contributes nothing here; a failure contributes nothing here. Read
    // TaskSourceReadEntrance for why the opt-in is a distinct interface rather than a flag.
    //
    // WHY A UNION IS NEEDED AT ALL rather than a wider scope. Work that names nobody cannot be reached by
    // widening Own or Team — there is no employee id on the row to match — and widening to Company reaches
    // every other task in the tenant. The set of admissible rows is not expressible as a breadth, so it is
    // contributed as a set. The two branches are then UNION-ed in SQL, so paging, ordering, counting and
    // every filter a caller composes afterwards see one query and cannot disagree with each other.
    public sealed class TaskReadAuthorization
    {
        public required BusinessContext Context { get; init; }
        public required AccessScope Scope { get; init; }
        public int? CallerEmployeeId { get; init; }
    }

    public interface ITaskReadAuthorizer
    {
        // The caller's authority to read a task list. NULL means refused — no context, or a scope that
        // resolves to nothing. Null is not "unrestricted"; a caller who gets null must be shown a refusal.
        Task<TaskReadAuthorization?> ForListAsync(CancellationToken cancellationToken = default);

        // Narrow a task query to what that authority permits.
        Task<IQueryable<TaskItem>> ApplyAsync(
            IQueryable<TaskItem> source, TaskReadAuthorization authorization,
            CancellationToken cancellationToken = default);
    }

    public sealed class TaskReadAuthorizer : ITaskReadAuthorizer
    {
        private readonly IBusinessContextAccessor _contexts;
        private readonly ITasksAccessService _access;
        private readonly ITaskSourceVisibility _sources;
        private readonly ITaskSourceReadEntrance? _entrance;

        // `entrance` is optional so that every existing construction of this class keeps its exact previous
        // behaviour: no entrance service, no third layer, intersection only. A test or a consumer that has
        // not opted in cannot accidentally acquire widening semantics by being recompiled.
        public TaskReadAuthorizer(
            IBusinessContextAccessor contexts, ITasksAccessService access, ITaskSourceVisibility sources,
            ITaskSourceReadEntrance? entrance = null)
        { _contexts = contexts; _access = access; _sources = sources; _entrance = entrance; }

        public async Task<TaskReadAuthorization?> ForListAsync(CancellationToken cancellationToken = default)
        {
            var context = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (context == null) return null;

            var scope = await _access.ResolveScopeAsync(context, TasksActions.Read, cancellationToken);

            // An empty scope is a REFUSAL, not an empty list to render politely. The distinction matters:
            // rendering zero rows tells a caller their company has no work, which is a different and false
            // statement from telling them they may not read this.
            if (scope.IsEmpty) return null;

            return new TaskReadAuthorization
            {
                Context = context, Scope = scope, CallerEmployeeId = context.EmployeeId,
            };
        }

        public async Task<IQueryable<TaskItem>> ApplyAsync(
            IQueryable<TaskItem> source, TaskReadAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(authorization);

            // Layers 1 and 2, unchanged: breadth, then source-record eligibility narrowing it.
            var scoped = source.WithinScope(authorization.Scope, authorization.CallerEmployeeId);
            var narrowed = await _sources.RestrictAsync(scoped, authorization.Context, cancellationToken);

            if (_entrance == null) return narrowed;

            // Layer 3, built from `source` rather than from `scoped` — that is the entire point of it, and
            // the reason it is a separate query rather than another clause.
            var admitted = await _entrance.AdmittedAsync(source, authorization.Context, cancellationToken);

            // SOURCE ELIGIBILITY IS APPLIED TO THE ENTRANCE BRANCH TOO. For the source that admitted them
            // this is the same answer twice, so it changes nothing — and that is exactly why it belongs
            // here. It means entrance can never be a way AROUND layer 2: if the visibility provider for the
            // admitting type refuses or fails, the admitted rows drop with everything else of that type, and
            // an admission cannot outlive the eligibility it was derived from.
            admitted = await _sources.RestrictAsync(admitted, authorization.Context, cancellationToken);

            // UNION, so the two branches become ONE query. Everything the caller composes afterwards —
            // filters, search, ordering, Skip/Take, Count, aggregates — is applied to the union, which is
            // what keeps the list, its total and its pages describing the same set. Distinct by construction,
            // so a row that is both in scope and admitted appears once.
            return narrowed.Union(admitted);
        }
    }
}
