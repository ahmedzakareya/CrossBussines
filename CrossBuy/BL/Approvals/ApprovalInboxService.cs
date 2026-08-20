using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Approvals
{
	/// <summary>
	/// One page of the approver's cross-silo inbox: the rows after paging, plus the totals computed
	/// BEFORE it, so "12 pending" and "showing 5" are both honest.
	/// </summary>
	public sealed record ApprovalInboxPage
	{
		public required IReadOnlyList<PendingApprovalRow> Rows { get; init; }

		/// <summary>Total pending across every silo, before <c>take</c> was applied.</summary>
		public required int TotalPending { get; init; }

		/// <summary>Pending count per silo key, before <c>take</c>. Silos with nothing pending are present with 0.</summary>
		public required IReadOnlyDictionary<string, int> CountsBySilo { get; init; }

		/// <summary>
		/// Silos this approver is entitled to see AT ALL, which is not the same as having rows in them.
		/// Inventory distinguishes the two: a non-manager sees no inventory section, while a manager with
		/// an empty queue still does. Kept as a neutral silo set rather than an inventory-shaped flag so
		/// the contract does not grow a field per module.
		/// </summary>
		public required IReadOnlySet<string> VisibleSilos { get; init; }

		public static ApprovalInboxPage Empty { get; } = new()
		{
			Rows = Array.Empty<PendingApprovalRow>(),
			TotalPending = 0,
			CountsBySilo = new Dictionary<string, int>(),
			VisibleSilos = new HashSet<string>(),
		};
	}

	/// <summary>
	/// The reusable READ-ONLY cross-silo approval inbox. Consumed by the Approvals screen and by
	/// Workspace; neither needs to know how many silos there are or how any of them decides visibility.
	/// </summary>
	public interface IApprovalInboxService
	{
		/// <summary>
		/// Pending approvals awaiting <paramref name="employeeId"/> in <paramref name="context"/>'s
		/// company, newest first. <paramref name="take"/> limits the ROWS only; the totals always
		/// describe the whole inbox. A non-positive <paramref name="take"/> means no limit.
		/// </summary>
		Task<ApprovalInboxPage> GetPendingForCurrentApproverAsync(
			BusinessContext context, int employeeId, int take = 0,
			CancellationToken cancellationToken = default);
	}

	// =================================================================================================
	// THE AGGREGATOR.
	//
	// WHAT IT DELIBERATELY CANNOT DO: there is no CrossDbContext in this constructor. It has no way to
	// reach LeaveRequests, EmployeeRequests or InventoryApprovals even by accident, so the rule "the
	// aggregator never queries a module table" is a property of the type rather than a convention
	// someone has to remember. Every company boundary stays with the module that owns the rows, which is
	// the only place that knows what the boundary IS - leave has no CompanyID column and bounds by the
	// requester's employee row, employee-requests bound on their own column, inventory bounds by company
	// AND a company-scoped role gate.
	//
	// ITS ENTIRE JOB: call three readers, merge, order deterministically, page, count. No predicates of
	// its own, no localization, no writes.
	// =================================================================================================
	public class ApprovalInboxService : IApprovalInboxService
	{
		private readonly ILeaveWorkflowService _leave;
		private readonly IEmployeeRequestService _requests;
		private readonly IInventoryApprovalService _inventory;

		public ApprovalInboxService(
			ILeaveWorkflowService leave, IEmployeeRequestService requests, IInventoryApprovalService inventory)
		{
			_leave = leave;
			_requests = requests;
			_inventory = inventory;
		}

		public async Task<ApprovalInboxPage> GetPendingForCurrentApproverAsync(
			BusinessContext context, int employeeId, int take = 0,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);

			// Fail closed. An unresolved company or identity yields an empty page rather than an
			// unfiltered one; the readers below would each refuse anyway, but refusing once here means a
			// future reader cannot be added without inheriting the guard.
			if (context.CompanyId <= 0 || employeeId <= 0) return ApprovalInboxPage.Empty;

			var leaveRows = await _leave.PendingForApproverAsync(context, employeeId, cancellationToken);
			var requestRows = await _requests.PendingForApproverAsync(context, employeeId, cancellationToken);

			// Inventory's contract predates this platform and returns its own entity plus the visibility
			// gate, so it is mapped here rather than in the module. This is the ONLY place that shape is
			// known, and it is a mapping of a returned value - not a query.
			var (isInventoryManager, inventoryPending) =
				await _inventory.ApprovalInboxAsync(context, employeeId, cancellationToken);

			var inventoryRows = inventoryPending.Select(a => new PendingApprovalRow
			{
				Silo = ApprovalSilos.Inventory,
				EntityId = a.ID,
				ApprovalType = a.DocType,
				RequesterEmployeeId = a.RequestedByEmployeeId,
				SubmittedAt = a.RequestedAt,
				Status = a.Status,
				Amount = a.Amount,
				Navigation = new ApprovalNavigationTarget("Inventory", "Approvals"),
			}).ToList();

			// Every silo appears in the counts, including the empty ones: a consumer rendering "Leave (0)"
			// should not have to know whether a missing key means zero or means the silo does not exist.
			var countsBySilo = new Dictionary<string, int>
			{
				[ApprovalSilos.Leave] = leaveRows.Count,
				[ApprovalSilos.Request] = requestRows.Count,
				[ApprovalSilos.Inventory] = inventoryRows.Count,
			};

			var visibleSilos = new HashSet<string>(StringComparer.Ordinal)
			{
				// Leave and employee-requests are visible to anyone: being the named approver IS the
				// entitlement. Inventory additionally requires the company-scoped manager role, and that
				// answer comes from the module, never recomputed here.
				ApprovalSilos.Leave,
				ApprovalSilos.Request,
			};
			if (isInventoryManager) visibleSilos.Add(ApprovalSilos.Inventory);

			// One deterministic order across all three sources. OrderKey carries negated ticks then silo
			// then negated id, so this is newest-first with a total tiebreak - two rows submitted in the
			// same tick cannot swap places between requests.
			var merged = leaveRows.Concat(requestRows).Concat(inventoryRows)
				.OrderBy(r => r.OrderKey)
				.ToList();

			var total = merged.Count;
			IReadOnlyList<PendingApprovalRow> rows = take > 0 && take < total
				? merged.Take(take).ToList()
				: merged;

			return new ApprovalInboxPage
			{
				Rows = rows,
				TotalPending = total,
				CountsBySilo = countsBySilo,
				VisibleSilos = visibleSilos,
			};
		}
	}
}
