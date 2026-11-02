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

		/// <summary>
		/// The pending approvals THIS caller may act on that concern one canonical business entity -
		/// the integration seam a conversation-scoped consumer needs ("what is waiting on this record?").
		///
		/// IT DISCLOSES NOTHING THE CALLER COULD NOT ALREADY SEE, and that is the whole security design:
		/// this is a FILTER over <see cref="GetPendingForCurrentApproverAsync"/>, not a second query
		/// path. It opens no new module query, asks for no new permission, and cannot reach a silo the
		/// caller is not already entitled to - because the rows it filters are the rows their own inbox
		/// returned. A consumer who is not an approver for a record gets an empty list, which is the same
		/// answer their inbox gives.
		///
		/// An unknown or unsupported <paramref name="entityType"/> returns EMPTY rather than throwing:
		/// an unmapped silo is a real state (see <c>ApprovalEntityMap</c>), and a caller asking about a
		/// record with no approval workflow should read "nothing is pending", not an exception.
		/// Comparison is ORDINAL, matching how <c>IEntityRegistry</c> codes are compared everywhere else.
		/// </summary>
		Task<IReadOnlyList<PendingApprovalRow>> GetPendingForEntityAsync(
			BusinessContext context, int employeeId, string entityType, int entityId,
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
		// The Projects module's own reader. Like the other three, it returns rows THIS caller may act on;
		// this service still holds no CrossDbContext and still decides no permission of its own.
		private readonly IProgressBillingService _billing;

		public ApprovalInboxService(
			ILeaveWorkflowService leave, IEmployeeRequestService requests, IInventoryApprovalService inventory, IProgressBillingService billing)
		{
			_leave = leave;
			_requests = requests;
			_inventory = inventory;
		_billing = billing; }

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

			// Project billing. The reader has ALREADY applied preparer != approver and billing-approve per
			// project, so anything it returns is genuinely this caller's to act on.
			var billingPending = await _billing.PendingApprovalsForAsync(context, cancellationToken);

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

			var billingRows = billingPending.Select(b => new PendingApprovalRow
			{
				Silo = ApprovalSilos.ProjectBilling,
				EntityId = b.BillingId,
				// The stage IS the discriminator: the same document appears as an approval task, then as a
				// posting task, and the reader has already proved this caller may do that specific one.
				ApprovalType = b.Status == CrossBuy.Models.Context.Accounting.ProgressBillingStatuses.Submitted
					? "ProgressBilling.Approve"
					: "ProgressBilling.Post",
				TitleAr = b.Status == CrossBuy.Models.Context.Accounting.ProgressBillingStatuses.Submitted
					? $"اعتماد مستخلص #{b.BillingNo} - {b.ProjectName}"
					: $"ترحيل مستخلص #{b.BillingNo} - {b.ProjectName}",
				TitleEn = b.Status == CrossBuy.Models.Context.Accounting.ProgressBillingStatuses.Submitted
					? $"Approve progress billing #{b.BillingNo} - {b.ProjectNameEn ?? b.ProjectName}"
					: $"Post progress billing #{b.BillingNo} - {b.ProjectNameEn ?? b.ProjectName}",
				RequesterEmployeeId = b.PreparedBy,
				SubmittedAt = b.SubmittedAt,
				Status = b.Status,
				Amount = b.NetDue,
				// Into the REAL billing screen, not a clone of it.
				Navigation = new ApprovalNavigationTarget("Project", "Billing",
					new Dictionary<string, string?> { ["id"] = b.ProjectId.ToString(), ["b"] = b.BillingId.ToString() }),
			}).ToList();

			// Every silo appears in the counts, including the empty ones: a consumer rendering "Leave (0)"
			// should not have to know whether a missing key means zero or means the silo does not exist.
			var countsBySilo = new Dictionary<string, int>
			{
				[ApprovalSilos.Leave] = leaveRows.Count,
				[ApprovalSilos.Request] = requestRows.Count,
				[ApprovalSilos.Inventory] = inventoryRows.Count,
				[ApprovalSilos.ProjectBilling] = billingRows.Count,
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
			// Project billing entitlement is per project, so there is no company-wide flag to ask for: the
			// silo is visible exactly when the module returned something this caller may approve.
			if (billingRows.Count > 0) visibleSilos.Add(ApprovalSilos.ProjectBilling);

			// One deterministic order across all three sources. OrderKey carries negated ticks then silo
			// then negated id, so this is newest-first with a total tiebreak - two rows submitted in the
			// same tick cannot swap places between requests.
			var merged = leaveRows.Concat(requestRows).Concat(inventoryRows).Concat(billingRows)
				// CANONICAL IDENTITY, STAMPED IN EXACTLY ONE PLACE.
				//
				// The silo→entity-code mapping belongs to the approvals platform, not to each module, so
				// it is applied here rather than asked of four readers that would then be four places to
				// get it wrong. The modules keep producing the facts they own; nothing in their contract
				// changed and none of their files was touched.
				//
				// THE COMPANY STAMPED IS THE ONE THE READERS WERE GIVEN - this same `context` - so it
				// cannot describe a boundary other than the one that was actually applied a few lines
				// above. It is provenance for a consumer holding rows from several calls, never a second
				// authority; the guard at the top of this method is what fails closed.
				//
				// An unmapped silo gets NULL and stays unresolved. It is still a perfectly good inbox row
				// - it renders, it counts, it navigates - it simply cannot be addressed by platform
				// entity identity, and saying so is the point.
				.Select(r => r with
				{
					EntityType = ApprovalEntityMap.CanonicalEntityType(r.Silo),
					CompanyId = context.CompanyId,
				})
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

		public async Task<IReadOnlyList<PendingApprovalRow>> GetPendingForEntityAsync(
			BusinessContext context, int employeeId, string entityType, int entityId,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);

			// Fail closed on the same three things the inbox does, plus the two this method adds. An
			// unusable argument yields an empty list rather than an unfiltered one - there is no code
			// path here that returns rows without an entityType AND an entityId to match them against.
			if (string.IsNullOrWhiteSpace(entityType) || entityId <= 0) return Array.Empty<PendingApprovalRow>();
			if (context.CompanyId <= 0 || employeeId <= 0) return Array.Empty<PendingApprovalRow>();

			// THE CALLER'S OWN INBOX, UNPAGED, THEN FILTERED.
			//
			// take: 0 on purpose - paging the source first would make the answer depend on where a record
			// happened to fall in the newest-first order, so a genuinely pending approval could vanish
			// because thirty newer ones existed. The filter is cheap; the wrong answer is not.
			//
			// Every row here has already passed its module's company boundary and its approver
			// entitlement. This method adds no authority and removes none.
			var page = await GetPendingForCurrentApproverAsync(context, employeeId, take: 0, cancellationToken);

			// Ordinal, like every other IEntityRegistry code comparison. An unmapped row carries a null
			// EntityType and therefore matches nothing - an unresolved silo is excluded by construction
			// rather than by a special case, which is what stops it being silently mistaken for a
			// resolved one.
			return page.Rows
				.Where(r => string.Equals(r.EntityType, entityType, StringComparison.Ordinal)
						 && r.EntityId == entityId)
				.ToList();
		}
	}
}
