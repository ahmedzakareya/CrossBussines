using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // WHO MAY SEE THE WORK, when the work is ABOUT something.
    //
    // A task carries an optional link to a business record (TaskItem.EntityType/EntityId). The per-task
    // path has always honoured it: TasksAccessService.CanAsync asks IPlatformPermissionProvider whether the
    // caller may view the LINKED entity, and refuses the task when it may not — a task about an invoice must
    // not become a way to learn that the invoice exists. The SET-shaped path had no equivalent, so a list
    // returned rows the detail page would have refused.
    //
    // WHY NOT JUST CALL CanAsync PER ROW. Because that is the shape TaskScopeQuery exists to prevent, and its
    // header already says why: the permission cost grows with the data, the whole company is materialised
    // before anything is authorized, and an in-memory filter breaks the count and the paging around it. A
    // list must narrow in SQL.
    //
    // SO THE QUESTION IS ASKED THE OTHER WAY ROUND. Instead of "may the caller see the record behind THIS
    // row", once per row, a provider is asked "which records of your type may the caller see", once per
    // request. The answer becomes one narrowing predicate that composes into the same query as the scope,
    // the count and the page — so the count and the list cannot disagree, and no row is fetched and then
    // discarded.
    //
    // THE PROVIDER OWNS NO POLICY. That is the same rule IPlatformPermissionProvider follows and for the same
    // reason: a second copy of a business rule drifts from the first, and the drift shows up as a screen
    // offering work the service then refuses. A provider delegates to the module's own authority.
    public interface ITaskSourceVisibilityProvider
    {
        // The registered entity code this provider answers for — EntityRegistry.ProjectBilling and so on.
        string EntityType { get; }

        // The ids of THIS type the caller may see. Called ONCE per query, never per row.
        //
        // An empty set means "none of them", which is a real answer and not an error: a caller who holds no
        // billing-approve anywhere legitimately sees no billing approval work.
        Task<IReadOnlySet<int>> VisibleEntityIdsAsync(
            BusinessContext context, CancellationToken cancellationToken = default);
    }

    // The one place a task query is narrowed to the source records the caller may see.
    public interface ITaskSourceVisibility
    {
        Task<IQueryable<TaskItem>> RestrictAsync(
            IQueryable<TaskItem> source, BusinessContext context, CancellationToken cancellationToken = default);
    }

    public sealed class TaskSourceVisibility : ITaskSourceVisibility
    {
        private readonly IEnumerable<ITaskSourceVisibilityProvider> _providers;
        private readonly ILogger<TaskSourceVisibility> _log;

        public TaskSourceVisibility(
            IEnumerable<ITaskSourceVisibilityProvider> providers, ILogger<TaskSourceVisibility> log)
        { _providers = providers; _log = log; }

        // THE FALLBACK IS DELIBERATE AND IT IS "GENERIC SCOPE DECIDES".
        //
        // A task type with no registered provider is left to the task scope that already governs it. The
        // alternative — hiding every linked task until someone writes a provider — was measured against what
        // is actually in the repository and rejected: six orchestration rules produce tasks today
        // (documents ×2, POS shortage, manufacturing work orders, CRM follow-up, project billing) and TM-2
        // lets a user link a task to any record by hand. Denying all of those would empty working screens to
        // fix one of them, and a security control that empties the product is switched off, not obeyed.
        //
        // It is also not a hole being left open: every one of those types still passes through the generic
        // task scope, and the per-row path still asks IPlatformPermissionProvider. What a provider adds is
        // the RECORD-LEVEL dimension the platform cannot express in set form — which today is exactly one
        // type, because project billing is the one whose eligibility is per-project AND excludes the
        // preparer. Registering a provider is therefore how a module opts INTO the stricter treatment.
        public async Task<IQueryable<TaskItem>> RestrictAsync(
            IQueryable<TaskItem> source, BusinessContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(context);

            foreach (var provider in _providers)
            {
                string code = provider.EntityType;
                if (string.IsNullOrWhiteSpace(code)) continue;

                IReadOnlySet<int> visible;
                try
                {
                    visible = await provider.VisibleEntityIdsAsync(context, cancellationToken)
                              ?? (IReadOnlySet<int>)new HashSet<int>();
                }
                catch (OperationCanceledException)
                {
                    throw;   // a cancelled request is not a denied one; let it surface.
                }
                catch (Exception ex)
                {
                    // FAIL CLOSED. A provider that cannot answer has not said yes, and the failure mode that
                    // matters here is the quiet one: falling through to "show them" would turn an outage in
                    // one module into a disclosure in another. The rows of THIS type are dropped and the rest
                    // of the list still renders, so the screen degrades rather than breaking — but it never
                    // degrades open. Logged at Error because a provider throwing is a defect, not noise.
                    _log.LogError(ex,
                        "Task source visibility: provider for '{EntityType}' failed for employee {Employee} " +
                        "in company {Company}; every task linked to that type is hidden for this query.",
                        code, context.EmployeeId, context.CompanyId);
                    source = source.Where(t => t.EntityType != code);
                    continue;
                }

                // ONE narrowing clause per provider, and its shape is the whole point: a row of ANOTHER type
                // is untouched, and a row of THIS type survives only if its id is in the allowed set. `ids` is
                // a List so EF translates Contains into an IN (…) parameter list rather than closing over a
                // set object it cannot translate.
                var ids = visible.ToList();
                if (ids.Count == 0)
                {
                    source = source.Where(t => t.EntityType != code);
                    continue;
                }

                source = source.Where(t => t.EntityType != code
                    || (t.EntityId.HasValue && ids.Contains(t.EntityId.Value)));
            }

            return source;
        }
    }
}
