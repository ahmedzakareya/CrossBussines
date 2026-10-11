using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // WHEN THE WORK BELONGS TO NOBODY YET.
    //
    // Generic task scope answers "whose tasks may this caller read" by looking at the ROW: Own tests
    // AssigneeEmployeeId and CreatedByEmployeeId, Team tests the same two columns against a team, Company
    // tests the tenant. Every one of those needs the row to NAME somebody, and some work legitimately names
    // nobody — an inventory approval is authorised by whoever holds the role, minus the person who asked for
    // it, so no individual can be written onto the task when it is created.
    //
    // Such a task is unreachable by construction. Its eligible reviewer matches neither column, so Own and
    // Team exclude them; and the only breadth that would include them, Company, includes every other task in
    // the company as well. ITaskSourceVisibilityProvider cannot help, because it NARROWS: by the time it is
    // asked, the row is already gone. The measured consequence is in
    // InventoryApprovalReachabilityEvidenceTests — the ladder has two rungs, "cannot see unassigned work"
    // and "can see all of it", and neither is the right answer.
    //
    // THIS IS THE MISSING RUNG, and it is deliberately not a general one.
    //
    // ================================================================================================
    // THE SECURITY MODEL: SOURCE-EXPLICIT, OPT-IN, ENTITYTYPE-LOCAL, FAIL-CLOSED.
    // ================================================================================================
    //
    // WHAT THIS IS NOT. It is NOT `generic scope UNION source visibility`. That version was considered and
    // refused: it would silently promote every existing and future ITaskSourceVisibilityProvider into an
    // authorization GRANT, so a provider written to hide rows would begin revealing them, and the two
    // providers that exist today would change meaning without their authors touching a line. A narrowing
    // mechanism must not become a widening one by being redefined underneath its implementers.
    //
    // SO OPT-IN IS A SEPARATE TYPE, not a flag, not a convention, not a marker on the existing interface.
    // Implementing ITaskSourceVisibilityProvider admits nothing, ever. A source that wants to admit work
    // must ALSO implement ITaskSourceReadEntranceProvider and be registered as one, which is a deliberate
    // act by someone who has read this file. That is the whole reason the capability is expressed as a
    // second interface rather than as `bool GrantsEntrance => false` on the first: a default-false property
    // is one careless override away from being true, and it puts the decision in the same place as the
    // narrowing logic where it reads like a detail.
    //
    // AND THE AUTHORITY IS LOCAL TO THE SOURCE. A provider admits tasks of ITS OWN EntityType carrying ITS
    // OWN returned ids, and nothing else. It cannot admit a plain task, another module's task, or a task of
    // its own type whose id it did not return. EntityType and EntityId are tested as a PAIR, because ids
    // collide across types constantly — approval #7 and work order #7 both exist, and admitting the second
    // because the first was authorised would be a cross-module disclosure caused by an integer.
    //
    // ENTRANCE IS READ-ONLY. It admits a row into a read; it confers no edit, complete, reassign or manage
    // right. Those still require the relationship or the role that always governed them.
    //
    // EVERY UNCERTAIN ANSWER IS "NO". No provider for the type, provider opted out, provider returned an
    // empty set, provider threw, no employee in the context, company mismatch — each of those admits
    // nothing. There is no path through this file where a failure widens a result.
    public interface ITaskSourceReadEntranceProvider
    {
        // The registered entity code this provider speaks for. Its entrance authority stops at this code.
        string EntityType { get; }

        // The ids of THIS type the caller may be ADMITTED on, even where generic task scope would not have
        // reached them. Asked ONCE per query, never per row.
        //
        // This is a stronger statement than VisibleEntityIdsAsync and a source may answer it differently:
        // "which of mine may they see if they already have the row" is not the same question as "which of
        // mine entitle them to the row in the first place". A source that wants one answer for both is free
        // to delegate, and the inventory approval provider does exactly that — but the two remain separate
        // questions so that a source CAN distinguish them without changing this contract.
        Task<IReadOnlySet<int>> AdmissibleEntityIdsAsync(
            BusinessContext context, CancellationToken cancellationToken = default);
    }

    // One source's answer, already fail-closed: an entry exists only when a registered, opted-in provider
    // returned at least one id for this caller.
    public sealed record TaskSourceAdmission(string EntityType, IReadOnlyList<int> EntityIds);

    // The one place source-explicit entrance is computed and composed.
    public interface ITaskSourceReadEntrance
    {
        // What this caller may be admitted on, per source. Empty when nothing admits them.
        Task<IReadOnlyList<TaskSourceAdmission>> AdmissionsAsync(
            BusinessContext context, CancellationToken cancellationToken = default);

        // The SET-shaped answer: the entrance branch, as a query composable with the scoped one.
        Task<IQueryable<TaskItem>> AdmittedAsync(
            IQueryable<TaskItem> source, BusinessContext context, CancellationToken cancellationToken = default);

        // The ROW-shaped answer, for the per-task gate. Same admissions, same fail-closed rules, so the
        // detail page and the list cannot disagree about one row.
        Task<bool> AdmitsAsync(
            BusinessContext context, string? entityType, int? entityId,
            CancellationToken cancellationToken = default);
    }

    public sealed class TaskSourceReadEntrance : ITaskSourceReadEntrance
    {
        private readonly IEnumerable<ITaskSourceReadEntranceProvider> _providers;
        private readonly ILogger<TaskSourceReadEntrance> _log;

        public TaskSourceReadEntrance(
            IEnumerable<ITaskSourceReadEntranceProvider> providers, ILogger<TaskSourceReadEntrance> log)
        { _providers = providers; _log = log; }

        public async Task<IReadOnlyList<TaskSourceAdmission>> AdmissionsAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // No company and no identity are both "cannot authorize", and entrance is the one place where
            // guessing would be expensive: it is the mechanism that reaches PAST the ordinary scope.
            if (context.CompanyId <= 0 || context.EmployeeId is not > 0)
                return Array.Empty<TaskSourceAdmission>();

            var admissions = new List<TaskSourceAdmission>();

            foreach (var provider in _providers)
            {
                string code = provider.EntityType;
                if (string.IsNullOrWhiteSpace(code)) continue;

                IReadOnlySet<int> ids;
                try
                {
                    ids = await provider.AdmissibleEntityIdsAsync(context, cancellationToken)
                          ?? (IReadOnlySet<int>)new HashSet<int>();
                }
                catch (OperationCanceledException)
                {
                    throw;   // a cancelled request is not a refused one.
                }
                catch (Exception ex)
                {
                    // FAIL CLOSED, and here that means the source simply admits nothing. Note the asymmetry
                    // with TaskSourceVisibility, which on failure HIDES the type: there, failing closed means
                    // showing less than before; here it means granting nothing extra. Both land on "the
                    // outage does not become a disclosure", which is the property that matters.
                    _log.LogError(ex,
                        "Task source entrance: provider for '{EntityType}' failed for employee {Employee} in " +
                        "company {Company}; it admits nothing for this request.",
                        code, context.EmployeeId, context.CompanyId);
                    continue;
                }

                // An empty answer is not an admission. Said explicitly because "no ids" and "every id" are
                // one careless predicate apart, and only one of them is safe.
                if (ids.Count == 0) continue;

                admissions.Add(new TaskSourceAdmission(code, ids.ToList()));
            }

            return admissions;
        }

        public async Task<IQueryable<TaskItem>> AdmittedAsync(
            IQueryable<TaskItem> source, BusinessContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(context);

            var admissions = await AdmissionsAsync(context, cancellationToken);

            // NOTHING ADMITS THEM ⇒ an EMPTY QUERY, never the source untouched. Returning `source` here
            // would turn "no entrance" into "every task", which is the single worst mistake this file could
            // make, so it is written as the first branch rather than as a fallthrough.
            if (admissions.Count == 0) return source.Where(_ => false);

            int companyId = context.CompanyId;
            IQueryable<TaskItem>? admitted = null;

            foreach (var admission in admissions)
            {
                // Captured into locals: a closure over the loop variable would compile every branch against
                // the LAST admission, silently authorising one source with another source's ids.
                string code = admission.EntityType;
                var ids = admission.EntityIds.ToList();

                // EntityType AND EntityId, as a pair, plus the company on the ROW. The company predicate is
                // repeated here rather than assumed from the caller's query: this branch deliberately
                // bypasses the scope predicate that would otherwise have carried it.
                var branch = source.Where(t => t.CompanyId == companyId
                    && t.EntityType == code
                    && t.EntityId.HasValue
                    && ids.Contains(t.EntityId.Value));

                admitted = admitted == null ? branch : admitted.Union(branch);
            }

            return admitted!;
        }

        public async Task<bool> AdmitsAsync(
            BusinessContext context, string? entityType, int? entityId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // A task with no source cannot be admitted by a source. That is not an edge case to tidy away:
            // it is the rule that keeps plain tasks — the ones no provider guards — exactly as they were.
            if (string.IsNullOrWhiteSpace(entityType) || entityId is not > 0) return false;

            var admissions = await AdmissionsAsync(context, cancellationToken);

            // Ordinal comparison, matching how EntityRegistry codes are compared everywhere else.
            foreach (var admission in admissions)
                if (string.Equals(admission.EntityType, entityType, StringComparison.Ordinal)
                    && admission.EntityIds.Contains(entityId.Value))
                    return true;

            return false;
        }
    }
}
