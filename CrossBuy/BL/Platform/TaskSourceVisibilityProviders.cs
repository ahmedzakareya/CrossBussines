using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Inventory approval task reads delegate to the same actionable inbox used by Inventory.
    public sealed class InventoryApprovalTaskVisibilityProvider
        : ITaskSourceVisibilityProvider, ITaskSourceReadEntranceProvider
    {
        private readonly IInventoryApprovalService _approvals;

        public InventoryApprovalTaskVisibilityProvider(IInventoryApprovalService approvals)
            { _approvals = approvals; }

        public string EntityType => EntityRegistry.InventoryApproval;

        public async Task<IReadOnlySet<int>> VisibleEntityIdsAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // FAIL CLOSED on a caller with no employee identity. The inbox would answer the same way, but
            // the question is not worth asking without one and an empty set is the honest answer either way.
            if (context.EmployeeId is not > 0) return new HashSet<int>();

            // The bool IS the manager gate, and the list is already company-scoped, Pending-only and
            // requester-excluded. A non-manager gets (false, empty), so the two arms cannot disagree.
            var (isInventoryManager, pending) = await _approvals.ApprovalInboxAsync(
                context, context.EmployeeId.Value, cancellationToken);
            if (!isInventoryManager) return new HashSet<int>();

            return pending.Select(a => a.ID).ToHashSet();
        }

        // ENTRANCE. Same question, same authority, same answer — and that identity is intentional rather
        // than lazy: for this source, being eligible to ACT on an approval is exactly what entitles you to
        // the task about it, because the task exists only to carry that review. So the actionable inbox is
        // the honest answer to both, and delegating keeps them from drifting apart the way two hand-written
        // copies of one rule always do.
        //
        // Everything the entrance inherits comes from ApprovalInboxAsync and nothing is restated here: the
        // caller holds InventoryManager IN THIS COMPANY, the approval is still Pending, and the requester is
        // excluded from their own submission. A manager who is the requester is admitted to nothing, which
        // is separation of duties surviving a mechanism built to widen access.
        //
        // ACTIONABILITY, PRESERVED. An Approved or Rejected approval is not in the inbox, so it admits
        // nobody — the review is over and the task stops being reachable this way for everyone, the
        // deciding manager included. Entrance does not reopen historical visibility.
        public Task<IReadOnlySet<int>> AdmissibleEntityIdsAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
            => VisibleEntityIdsAsync(context, cancellationToken);
    }
}
