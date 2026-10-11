using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	public interface ILeaveWorkflowService
	{
		/// Ordered chain of approver employee IDs from the direct manager up to (and including)
		/// the head of the NEAREST organizational unit (company/branch/administrative body)
		/// the employee belongs to. Level 1 = direct manager, last = the unit head (final approver).
		/// Stage 1 Batch C.1: every approver is now intersected with the REQUESTER'S OWN COMPANY.
		Task<List<int>> ManagerChainAsync(int employeeId);

		/// The same chain plus whether building it hit a cross-company graft. `CreateAsync` needs the
		/// distinction, because "no approver above me" and "my chain is broken" must not act alike.
		Task<ApproverChain> ApproverChainAsync(int employeeId);

		/// Pending leave approvals awaiting this approver, in the context's company. READ ONLY - it is the
		/// approval inbox's module reader (SHF-14), not part of the workflow.
		Task<IReadOnlyList<CrossBuy.BL.Approvals.PendingApprovalRow>> PendingForApproverAsync(
			CrossBuy.Models.Platform.BusinessContext context, int approverEmployeeId,
			CancellationToken cancellationToken = default);

		/// Create a leave request, build its approval chain, notify the first approver
		/// (or auto-approve when the requester is at/above the top of the chain).
		Task<(bool ok, string? error, LeaveRequest? req)> CreateAsync(
			int employeeId, int leaveTypeId, DateTime start, DateTime end, string? reason);

		/// Approve/reject the CURRENT level. Approval advances up the chain; rejection ends it.
		Task<(bool ok, string? error)> DecideAsync(int requestId, int approverEmployeeId, bool approve, string? note);
	}

	/// The approver chain plus an explicit defect signal.
	///
	/// `HierarchyDefect` means the upward climb was STOPPED because the next node above belonged to another
	/// company. It exists because the two "empty chain" cases demand opposite handling: a genuine top-of-tree
	/// requester is auto-approved, whereas a requester whose chain is broken by a cross-company graft must be
	/// refused — auto-approving them would turn an isolation defect into an unapproved leave.
	/// The approver chain, plus the two things an EMPTY one has to be told apart by.
	///
	/// `HierarchyDefect` says the climb was STOPPED by something wrong - today a requester with no
	/// company, or a cross-company graft. `RequesterIsVerifiedRoot` says the opposite kind of thing:
	/// that the requester's own position attaches DIRECTLY to a company, branch or administrative
	/// body, which is what actually makes somebody the top of a tree.
	///
	/// THEY ARE NOT COMPLEMENTS, and that is the whole point of RSK-31. An empty chain used to be
	/// read as 'nobody is above this person, so approve it', but a chain empties for twelve reasons
	/// and only two of them raised the defect flag. The other ten - a missing employee node, a
	/// position with no parent, a parent that is not a person, a self-managing node, a broken
	/// reference - are ABSENCE OF EVIDENCE, and absence of evidence was being treated as proof of a
	/// root. So the root is now proven positively or it is not a root.
	public sealed record ApproverChain(
		List<int> Approvers, bool HierarchyDefect, int DroppedNodes, bool RequesterIsVerifiedRoot = false);

	public class LeaveWorkflowService : ILeaveWorkflowService
	{
		private readonly CrossDbContext _context;
		private readonly INotificationService _notifications;
		private readonly ILeaveDashboardService _dashboard;
		private readonly ILogger<LeaveWorkflowService> _log;
		private readonly CrossBuy.BL.Platform.IBusinessEventService? _events;
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor? _contexts;

		public LeaveWorkflowService(
			CrossDbContext context, INotificationService notifications, ILeaveDashboardService dashboard,
			ILogger<LeaveWorkflowService> log)
		{
			_context = context;
			_notifications = notifications;
			_dashboard = dashboard;
			_log = log;
		}

		// THE CANONICAL EVENT RECORDER, and the constructor the container actually picks.
		//
		// Optional by overload for one reason only: hand-built test fixtures elsewhere in the repository
		// construct this service with the four-parameter shape and are owned by other tabs. DI selects the
		// greediest constructor whose parameters all resolve, so production always lands here and always
		// has a recorder, and a guard test asserts exactly that. The same debt inventory approvals carry
		// (RSK-25) and it is recorded rather than hidden.
		public LeaveWorkflowService(
			CrossDbContext context, INotificationService notifications, ILeaveDashboardService dashboard,
			ILogger<LeaveWorkflowService> log, CrossBuy.BL.Platform.IBusinessEventService events)
			: this(context, notifications, dashboard, log) { _events = events; }

		// THE CONSTRUCTOR DI ACTUALLY PICKS, and the one that makes the decision path a security
		// boundary. The container selects the greediest constructor whose parameters all resolve, so
		// production always lands here and always has a caller identity; a guard test asserts it.
		//
		// The accessor is nullable ONLY because hand-built fixtures in other tabs' test files still
		// construct the shorter shapes and cannot be edited from here. It is NOT a permissive default:
		// DecideAsync REFUSES when no identity can be resolved - see AuthorizeDecisionAsync. An
		// absent accessor denies; it never waves a decision through.
		public LeaveWorkflowService(
			CrossDbContext context, INotificationService notifications, ILeaveDashboardService dashboard,
			ILogger<LeaveWorkflowService> log, CrossBuy.BL.Platform.IBusinessEventService events,
			CrossBuy.BL.Platform.IBusinessContextAccessor contexts)
			: this(context, notifications, dashboard, log, events) { _contexts = contexts; }

		// ---- the canonical leave fact -------------------------------------------------------
		//
		// ONE emitter for all four transitions, so the payload shape and the identity rule cannot drift
		// between them. Called INSIDE the authoritative transaction and BEFORE its commit, which is the
		// contract the kernel enforces: RecordAsync THROWS without an ambient transaction (ADR-001), so an
		// event for a decision that rolls back is not merely unlikely - it is unwritable.
		//
		// THE IDENTITY IS THE REQUEST, THE LEVEL AND THE ACTION. Not the request alone: a chain produces
		// several facts about one request, and a key without the level would make level 2's approval a
		// duplicate of level 1's and silently drop it. The level is what makes each fact its own fact.
		private Task EmitAsync(LeaveRequest req, string eventType, int level, int? approver,
			int? nextLevel, int? nextApprover, int? decidedBy, CancellationToken cancellationToken = default)
		{
			if (_events is null) return Task.CompletedTask;
			return _events.RecordAsync(new CrossBuy.Models.Platform.BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.LeaveRequest,
				EntityId = req.ID,
				EventType = eventType,
				PayloadVersion = CrossBuy.Models.Platform.LeaveRequestEventPayload.Version,
				Visibility = CrossBuy.Models.Platform.BusinessEventVisibility.Internal,
				DedupKey = $"leave:{req.ID}:level:{level}:{CrossBuy.BL.Platform.BusinessEventTypes.ActionOf(eventType)}",
				Payload = new CrossBuy.Models.Platform.LeaveRequestEventPayload
				{
					LeaveRequestId = req.ID,
					RequesterEmployeeId = req.EmployeeID,
					Level = level,
					ApproverEmployeeId = approver,
					NextLevel = nextLevel,
					NextApproverEmployeeId = nextApprover,
					DecidedByEmployeeId = decidedBy,
				},
			}, cancellationToken);
		}

		// the position/seat title of an employee in the org tree (e.g., "رئيس قسم", "مدير الفرع")
		private async Task<(string ar, string en)> PositionTitleAsync(int employeeId)
		{
			var node = await _context.Hierarchicals.AsNoTracking()
				.FirstOrDefaultAsync(h => h.H_Type == 5 && h.H_ObjectID == employeeId);
			if (node?.H_Parent != null)
			{
				var pos = await _context.Hierarchicals.AsNoTracking()
					.FirstOrDefaultAsync(h => h.H_ID == node.H_Parent.Value);
				if (pos != null && !string.IsNullOrWhiteSpace(pos.H_Name))
					return (pos.H_Name!, string.IsNullOrWhiteSpace(pos.H_NameEn) ? pos.H_Name! : pos.H_NameEn!);
			}
			return ("المدير", "the manager");
		}

		public async Task<List<int>> ManagerChainAsync(int employeeId)
			=> (await ApproverChainAsync(employeeId)).Approvers;

		// STAGE 1 BATCH C.1 — THE LEAK THIS CLOSES.
		//
		// This climb had NO company predicate. `Hierarchical` carries no CompanyID (it is one shared org tree,
		// which is why Batch B's global filters deliberately skip it), so on a multi-company install the node
		// above an employee's position could be another company's employee. That person then became a real
		// approver: they were written into `LeaveRequest.CurrentApproverEmployeeID`, notified, shown the
		// request — name, dates, reason — on their approvals screen, and could APPROVE OR REJECT it. A leave
		// request is HR data about a person in a company that approver has no relationship to.
		//
		// The intersection is on Employee.EmpCompanyID — the requester's own company, read from their row, not
		// from a caller argument and never defaulted. A foreign node STOPS the climb rather than being skipped
		// over: continuing upward through it would keep walking a subtree that belongs to someone else's
		// company, so everything above it is equally untrustworthy.
		//
		// It is not silently discarded: the stop is logged at Warning naming the requester, the foreign node
		// and the company, and it is surfaced to the caller as `HierarchyDefect` so `CreateAsync` refuses the
		// request instead of falling into its auto-approve branch.
		public async Task<ApproverChain> ApproverChainAsync(int employeeId)
		{
			var all = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var byId = all.ToDictionary(h => h.H_ID);

			// the requester's own company — the only permitted basis for the intersection
			var companyId = await _context.Employee.AsNoTracking()
				.Where(e => e.ID == employeeId)
				.Select(e => (int?)e.EmpCompanyID)
				.FirstOrDefaultAsync();

			if (companyId is not > 0)
			{
				// No company on the requester's row ⇒ no basis to validate ANY approver. Fail closed and
				// report it as a defect, so the caller refuses rather than auto-approving.
				_log.LogWarning(
					"Leave: employee {Employee} has no company on their Employee row; no approver chain can be " +
					"validated, so none is produced.", employeeId);
				// Not a root either, and said out loud rather than left to the default: with no company we
				// cannot validate an approver OR a root, so neither claim can be made.
				return new ApproverChain(new List<int>(), true, 0, RequesterIsVerifiedRoot: false);
			}

			// every employee the tree references, restricted to the requester's company in ONE query
			var treeEmployeeIds = all
				.Where(h => h.H_Type == 5 && h.H_ObjectID.HasValue)
				.Select(h => h.H_ObjectID!.Value).Distinct().ToList();

			var sameCompany = (await _context.Employee.AsNoTracking()
				.Where(e => treeEmployeeIds.Contains(e.ID) && e.EmpCompanyID == companyId.Value)
				.Select(e => e.ID)
				.ToListAsync()).ToHashSet();

			// the manager directly above: employee node → its position → the parent employee node
			int? ManagerOf(int empId)
			{
				var node = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == empId);
				if (node?.H_Parent == null || !byId.ContainsKey(node.H_Parent.Value)) return null;
				var pos = byId[node.H_Parent.Value];                 // position node (type 4)
				if (pos.H_Parent == null || !byId.ContainsKey(pos.H_Parent.Value)) return null;
				var mgr = byId[pos.H_Parent.Value];                  // manager employee node (type 5)
				return mgr.H_Type == 5 ? mgr.H_ObjectID : null;
			}

			// an employee is a unit head when their position attaches DIRECTLY to a unit node
			// (1 = company, 2 = branch, 3 = administrative body)
			bool IsUnitHead(int empId)
			{
				var node = all.FirstOrDefault(h => h.H_Type == 5 && h.H_ObjectID == empId);
				if (node?.H_Parent == null || !byId.ContainsKey(node.H_Parent.Value)) return false;
				var pos = byId[node.H_Parent.Value];
				if (pos.H_Parent == null || !byId.ContainsKey(pos.H_Parent.Value)) return false;
				var parent = byId[pos.H_Parent.Value];
				return parent.H_Type == 1 || parent.H_Type == 2 || parent.H_Type == 3;
			}

			// climb from the direct manager upward, stopping at the FIRST unit head reached (inclusive)
			var chain = new List<int>();
			var current = employeeId;
			var guard = 0;
			var defect = false;
			var dropped = 0;
			while (guard++ < 50)   // cycle protection preserved: bounded climb + the chain.Contains check below
			{
				var mgr = ManagerOf(current);
				if (mgr == null || mgr == employeeId || chain.Contains(mgr.Value)) break;

				// the company intersection — a foreign node ends the chain, it is never approved past
				if (!sameCompany.Contains(mgr.Value))
				{
					dropped++;
					defect = true;
					_log.LogWarning(
						"Leave: the approver chain for employee {Employee} (company {Company}) was stopped at org " +
						"node employee {Manager}, who does not belong to that company. Hierarchical has no " +
						"CompanyID, so a cross-company graft is possible; the request will be refused rather " +
						"than approved by a foreign manager or auto-approved on an empty chain.",
						employeeId, companyId.Value, mgr.Value);
					break;
				}

				chain.Add(mgr.Value);
				if (IsUnitHead(mgr.Value)) break;   // reached the nearest unit head → final approver
				current = mgr.Value;
			}
			// THE SAME TEST, ASKED OF THE REQUESTER. IsUnitHead has always been here; it was only ever
			// called on a MANAGER, to stop the climb. Asking it about the requester is what turns 'no
			// manager was produced' into a question with two different answers: a company head whose
			// position hangs off the company node, and a broken row that resolves to nothing, are no
			// longer indistinguishable. The climb above is untouched - this reads the tree, it does not
			// change how the tree is walked.
			return new ApproverChain(chain, defect, dropped, IsUnitHead(employeeId));
		}

		// =============================================================================================
		// THE APPROVAL INBOX'S LEAVE READER (SHF-14 narrow read handoff).
		//
		// This is a READ. It adds nothing to the workflow and changes none of it: the approver chain,
		// CreateAsync, DecideAsync, the step writes, the notifications and the escalation are untouched.
		//
		// THE COMPANY BOUNDARY IS THE REQUESTER'S OWN ROW. LeaveRequest has no CompanyID column and none is
		// invented here; a request belongs to this inbox only when its REQUESTER belongs to the resolved
		// company. That is the same predicate ApprovalsController has applied since 96cb210, moved to the
		// module that owns the rows so a second consumer cannot get it wrong by re-implementing it.
		//
		// It stays a read-side defence in its own right. 87ec8fa closed the assignment path that could graft
		// a foreign approver onto a request, but rows written BEFORE that fix are still in the database, and
		// this predicate is what keeps them off a foreign approver's screen.
		//
		// FAIL CLOSED: no company or no approver yields an empty list, never an unfiltered read.
		// =============================================================================================
		public async Task<IReadOnlyList<CrossBuy.BL.Approvals.PendingApprovalRow>> PendingForApproverAsync(
			CrossBuy.Models.Platform.BusinessContext context, int approverEmployeeId,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);

			var empty = Array.Empty<CrossBuy.BL.Approvals.PendingApprovalRow>();
			if (context.CompanyId <= 0 || approverEmployeeId <= 0) return empty;

			var rows = await _context.LeaveRequests.AsNoTracking()
				.Include(r => r.Employee).Include(r => r.LeaveType)
				.Where(r => r.Status == 0
					&& r.CurrentApproverEmployeeID == approverEmployeeId
					&& r.Employee != null && r.Employee.EmpCompanyID == context.CompanyId)
				.OrderByDescending(r => r.ID)
				.ToListAsync(cancellationToken);

			return rows.Select(r => new CrossBuy.BL.Approvals.PendingApprovalRow
			{
				Silo = CrossBuy.BL.Approvals.ApprovalSilos.Leave,
				EntityId = r.ID,
				ApprovalType = r.LeaveTypeID.ToString(),
				TitleAr = r.LeaveType?.NameAr,
				TitleEn = r.LeaveType?.NameEn,
				RequesterEmployeeId = r.EmployeeID,
				RequesterNameAr = r.Employee?.FullName,
				RequesterNameEn = r.Employee?.FullNameEn,
				// The inbox has always dated a leave row by CreatedAt, falling back to the period start for
				// rows predating that column. Preserved exactly, or the ordering would change.
				SubmittedAt = r.CreatedAt ?? r.StartDate,
				Status = "Pending",
				PeriodStart = r.StartDate,
				PeriodEnd = r.EndDate,
				Days = r.Days,
				Navigation = new CrossBuy.BL.Approvals.ApprovalNavigationTarget("People", "Leaves"),
			}).ToList();
		}

		// ONE AUTHORITATIVE UNIT. The request, its approval steps and the canonical fact now commit
		// together or not at all. Before this they did not: the request was saved, then the steps were
		// saved separately, and a failure in between left a request pointing at CurrentLevel 1 with NO
		// step behind it - a row DecideAsync then refuses forever with 'the approval step is missing',
		// because there is genuinely nothing to decide. BeginOrJoin rather than Begin: a caller that
		// already opened a transaction owns it, and this one enlists instead of opening a second.
		public async Task<(bool ok, string? error, LeaveRequest? req)> CreateAsync(
			int employeeId, int leaveTypeId, DateTime start, DateTime end, string? reason)
		{
			if (end.Date < start.Date) return (false, "The end date is before the start date", null);
			if (!await _context.LeaveTypes.AnyAsync(t => t.ID == leaveTypeId)) return (false, "Invalid leave type", null);

			// no overlapping leave: the employee can't be on two leaves (ANY type) on the same day(s).
			// blocks against any existing request that is pending (0) or approved (1).
			var s = start.Date;
			var e = end.Date;
			var conflict = await _context.LeaveRequests.AsNoTracking()
				.Include(r => r.LeaveType)
				.Where(r => r.EmployeeID == employeeId && (r.Status == 0 || r.Status == 1)
					&& r.StartDate <= e && r.EndDate >= s)
				.OrderBy(r => r.StartDate)
				.FirstOrDefaultAsync();
			if (conflict != null)
			{
				var tname = conflict.LeaveType?.NameAr ?? "إجازة";
				var st = conflict.Status == 1 ? "موافَق عليه" : "معلّق";
				return (false,
					$"There is a {tname} request ({st}) that overlaps this period ({conflict.StartDate:yyyy/MM/dd} → {conflict.EndDate:yyyy/MM/dd}). You cannot take two leaves on the same days.",
					null);
			}

			// working days only (exclude weekly rest days defined by the policy)
			var days = await _dashboard.WorkingDaysAsync(employeeId, start, end);
			if (days <= 0) return (false, "Every day you selected is a rest day. Choose dates that fall on working days.", null);

			// strict balance check
			var remaining = await _dashboard.RemainingForTypeAsync(employeeId, leaveTypeId);
			if (days > remaining)
				return (false, $"Insufficient balance. {(remaining < 0 ? 0 : remaining)} day(s) remain and {days} day(s) are requested.", null);

			var chainResult = await ApproverChainAsync(employeeId);
			var chain = chainResult.Approvers;

			// MERGE: the SOURCE condition and BOTH explanations. The two sides document DIFFERENT closed
			// risks - the destination the cross-company graft, the source the verified structural root -
			// and neither contradicts the other, so both are kept. The source condition already contains
			// the destination's: HierarchyDefect is still tested, with the root test added beside it.
			//
			// A chain broken by a cross-company graft must NOT reach the auto-approve branch below. Without
			// this, closing the isolation leak would have created a worse defect: the requester's foreign
			// manager gets filtered out, the chain comes back empty, and `chain.Count == 0` reads that as
			// "requester is at the top of the tree" and approves the leave with no approver at all.
			//
			// AN EMPTY CHAIN IS NOT PROOF OF A ROOT (RSK-31). It used to be read as one: no approver came
			// back, therefore nobody is above this person, therefore approve. But a chain empties for many
			// reasons and most of them are a broken org tree rather than a company head - a missing
			// employee node, a position with no parent, a parent that is not a person, a self-managing
			// node. Those are the ABSENCE of evidence, and this branch was treating them as proof.
			//
			// So auto-approval now requires the requester to be POSITIVELY verified as a structural root,
			// and the defect flag stays beside it because the two say different things: one is 'the climb
			// hit something wrong', the other is 'this person really is the top'. Either failing refuses.
			// A verified root keeps exactly the behaviour it has today; only the unproven case changes.
			if (chain.Count == 0 && (chainResult.HierarchyDefect || !chainResult.RequesterIsVerifiedRoot))
				return (false, "The approval chain for this employee cannot be determined — the organisation structure is not correct. Please contact HR.", null);

			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);

			var req = new LeaveRequest
			{
				EmployeeID = employeeId,
				LeaveTypeID = leaveTypeId,
				StartDate = start.Date,
				EndDate = end.Date,
				Days = days,
				Reason = reason,
				CreatedAt = DateTime.UtcNow,
				CreatedBy = employeeId,
			};

			if (chain.Count == 0)
			{
				// requester is at/above the top of the chain → auto-approve
				req.Status = 1;
				req.CurrentLevel = 0;
				req.CurrentApproverEmployeeID = null;
				req.DecisionAt = DateTime.UtcNow;
			}
			else
			{
				req.Status = 0;
				req.CurrentLevel = 1;
				req.CurrentApproverEmployeeID = chain[0];
			}

			_context.LeaveRequests.Add(req);
			await _context.SaveChangesAsync();

			if (chain.Count > 0)
			{
				for (var i = 0; i < chain.Count; i++)
				{
					_context.LeaveApprovalSteps.Add(new LeaveApprovalStep
					{
						LeaveRequestID = req.ID,
						Level = i + 1,
						ApproverEmployeeID = chain[i],
						Status = 0,
						CreatedAt = DateTime.UtcNow,
						CreatedBy = employeeId,
					});
				}
				await _context.SaveChangesAsync();

				// The steps exist, so the state the fact describes is now internally valid: level 1 is owed by
				// a real approver with a real step behind it. Inside the transaction and before the commit.
				await EmitAsync(req, CrossBuy.BL.Platform.LeaveRequestEvents.Submitted,
					level: 1, approver: chain[0], nextLevel: null, nextApprover: null, decidedBy: null);
			}
			else
			{
				// TOP OF THE TREE. The domain auto-approved because nobody is above this requester, and that
				// behaviour is preserved exactly as it was. Two facts are still recorded, because two things
				// really did happen - the request was raised and it ended Approved - and level 0 is how the
				// payload says NOBODY WAS EVER ASKED. The rule reads that and creates no work: a Task for an
				// approver who does not exist is work nobody can do.
				//
				// No approval step is written and none is invented. That this path leaves no decision history
				// is a real gap in the domain, and it is carried as debt rather than papered over by
				// fabricating a step whose approver would be the requester themselves.
				await EmitAsync(req, CrossBuy.BL.Platform.LeaveRequestEvents.Submitted,
					level: 0, approver: null, nextLevel: null, nextApprover: null, decidedBy: null);
				await EmitAsync(req, CrossBuy.BL.Platform.LeaveRequestEvents.Approved,
					level: 0, approver: null, nextLevel: null, nextApprover: null, decidedBy: null);
			}

			await tx.CommitAsync();

			if (chain.Count > 0)
			{
				var requester = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == employeeId);
				var rAr = requester?.FullName ?? "موظف";
				var rEn = requester?.FullNameEn ?? requester?.FullName ?? "An employee";
				await _notifications.NotifyAsync(chain[0],
					"طلب إجازة جديد", "New leave request",
					$"قدّم {rAr} طلب إجازة لمدة {days} يوم بانتظار اعتمادك.",
					$"{rEn} submitted a leave request for {days} day(s) awaiting your approval.",
					"leave_submitted", req.ID);
			}

			return (true, null, req);
		}

		// ===========================================================================================
		// THE DECISION IS AUTHORIZED HERE, NOT ONLY AT THE CONTROLLER (RSK-26).
		//
		// WHAT WAS WRONG. This method took `approverEmployeeId` as a PARAMETER and its only authority
		// test was that the value equalled the stored CurrentApproverEmployeeID. It received no
		// BusinessContext, asked no access service, and read the request with no company predicate. A
		// caller passing a DIFFERENT employee id decided AS that person, because the id it passed WAS
		// the current approver. Both live callers happen to be correct today; nothing made a third one
		// correct, and that is the defect - an authoritative service was a security ASSUMPTION.
		//
		// THE PARAMETER STILL EXISTS AND NO LONGER AUTHORIZES ANYTHING. It is kept so the two existing
		// callers need no change, and it is now VALIDATED against the resolved identity: a mismatch is
		// refused even when the value names the real stored approver. That last case is the whole point
		// - it is precisely the impersonation the old code accepted.
		//
		// AUTHORITY COMES FROM THE STORED CHAIN, NOT FROM THE ORG TREE. The chain was resolved and
		// persisted when the request was submitted; asking IOrgHierarchy who manages whom TODAY would be
		// a second, disagreeing authority - and after a mid-chain reorganisation it would refuse the very
		// person the domain asked. HrAccessService keeps answering that question at the controller,
		// which is defence in depth and stays exactly as it is.
		// ===========================================================================================
		private async Task<bool> MayDecideAsync(LeaveRequest req, int approverEmployeeId, CancellationToken ct)
		{
			// NO ACCESSOR MEANS NO IDENTITY MEANS NO. The nullable field exists for foreign test fixtures,
			// not as a permissive default, and this is the line that makes that true.
			if (_contexts is null) return false;

			var caller = await _contexts.TryGetCurrentAsync(ct);
			if (caller is null || caller.CompanyId <= 0) return false;
			if (caller.EmployeeId is not int me || me <= 0) return false;

			// THE PARAMETER IS CHECKED AGAINST THE IDENTITY, never trusted in place of it.
			if (approverEmployeeId != me) return false;

			// COMPANY THROUGH THE REQUESTER, because LeaveRequest carries no CompanyID of its own. This is
			// the same basis ApproverChainAsync intersects on, so the write boundary and the chain agree
			// about what company a request belongs to instead of each inventing an answer. A requester with
			// no company row, or one in another tenant, matches nothing and is refused.
			bool sameCompany = await _context.Employee.AsNoTracking()
				.AnyAsync(e => e.ID == req.EmployeeID && e.EmpCompanyID == caller.CompanyId, ct);
			if (!sameCompany) return false;

			// THE STORED CURRENT APPROVER, and only them. Not a manager, not an HR role, not an admin.
			if (req.CurrentApproverEmployeeID != me) return false;

			// SEPARATION OF DUTIES AT THE WRITE BOUNDARY. The chain is built so this cannot arise and the
			// controller refuses it too; this is the authoritative service declining to be the place where
			// it starts being possible.
			if (req.EmployeeID == me) return false;

			return true;
		}

		public async Task<(bool ok, string? error)> DecideAsync(int requestId, int approverEmployeeId, bool approve, string? note)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);

			var req = await _context.LeaveRequests
				.Include(r => r.LeaveType)
				.Include(r => r.ApprovalSteps)
				.FirstOrDefaultAsync(r => r.ID == requestId);
			if (req == null) return (false, "Order not found");
			if (req.Status != 0) return (false, "The request has already been decided");

			// MERGE: the SOURCE authorization repair, with the DESTINATION translations. MayDecideAsync
			// SUPERSEDES the destination's bare CurrentApproverEmployeeID comparison - it makes that same
			// check AND resolves the caller from the context, verifies the company through the requester
			// Employee, and applies separation of duties. The destination line is contained in what
			// replaces it, so nothing is lost by dropping it.
			//
			// BEFORE ANY MUTATION AND BEFORE THE CLAIM. A refusal here emits no Business Event, moves no
			// Task, sends no notification and leaves the request and every step exactly as they were - the
			// transaction opened above is simply never written to and rolls back on dispose.
			if (!await MayDecideAsync(req, approverEmployeeId, CancellationToken.None))
				return (false, "You do not have permission to approve this request");

			var step = req.ApprovalSteps.FirstOrDefault(s => s.Level == req.CurrentLevel && s.Status == 0);
			if (step == null) return (false, "Approval step not found");

			// THE LEVEL BEING DECIDED, captured before anything moves it. Both approval branches below
			// change CurrentLevel - the final one zeroes it - so reading it afterwards would name the
			// wrong level in the fact and complete the wrong Task.
			int decidedLevel = req.CurrentLevel;

			// ---- ONE DECISION WINS -------------------------------------------------------------
			//
			// The guards above are a READ followed by a WRITE, and between them two approvers pressing at
			// once both saw Pending. Read-committed does not stop that, and there is no rowversion on any
			// table in this repository to make it optimistic. So the step is CLAIMED with a single
			// conditional statement - the same row, guarded on the status it must still have - and the DB
			// decides. Exactly one caller sees 1 row affected; the loser sees 0 and is refused with the
			// message the domain already uses for a request that has been decided, because that is what
			// happened to it.
			//
			// Everything after this point therefore runs for the winner only: one transition, one event,
			// one notification, one Task movement. The tracked entity is updated with the same values
			// immediately below, so the existing domain writes are left exactly as they were.
			int claimed = await _context.LeaveApprovalSteps
				.Where(x => x.ID == step.ID && x.Status == 0)
				.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, approve ? 1 : 2));
			if (claimed != 1) return (false, "تم البتّ في الطلب مسبقًا");

			var approver = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == approverEmployeeId);
			var aAr = approver?.FullName ?? "المدير";
			var aEn = approver?.FullNameEn ?? approver?.FullName ?? "Manager";
			var typeAr = req.LeaveType?.NameAr ?? "إجازة";
			var typeEn = req.LeaveType?.NameEn ?? "leave";

			var now = DateTime.UtcNow;

			// ---- rejection: ends the whole request immediately ----
			if (!approve)
			{
				step.Status = 2; step.DecisionAt = now; step.DecisionNote = note;
				step.UpdatedAt = now; step.updatedBy = approverEmployeeId;
				req.Status = 2; req.ApproverEmployeeID = approverEmployeeId;
				req.DecisionAt = now; req.DecisionNote = note; req.CurrentApproverEmployeeID = null;
				req.UpdatedAt = now; req.updatedBy = approverEmployeeId;
				await _context.SaveChangesAsync();

				// A rejection ends the WHOLE request, at whatever level it happened. One terminal fact.
				await EmitAsync(req, CrossBuy.BL.Platform.LeaveRequestEvents.Rejected,
					level: decidedLevel, approver: approverEmployeeId, nextLevel: null, nextApprover: null,
					decidedBy: approverEmployeeId);
				await tx.CommitAsync();

				await _notifications.NotifyAsync(req.EmployeeID,
					"تم رفض طلب إجازتك", "Your leave was rejected",
					$"طلب {typeAr} ({req.Days} يوم) تم رفضه بواسطة {aAr}.",
					$"Your {typeEn} request ({req.Days} day(s)) was rejected by {aEn}.",
					"leave_rejected", req.ID);
				return (true, null);
			}

			var maxLevel = req.ApprovalSteps.Count == 0 ? 0 : req.ApprovalSteps.Max(s => s.Level);
			var isFinal = req.CurrentLevel >= maxLevel;

			// ---- final approval ----
			if (isFinal)
			{
				// strict balance re-check (other approved requests may have consumed it meanwhile)
				var remaining = await _dashboard.RemainingForTypeAsync(req.EmployeeID, req.LeaveTypeID);
				if (req.Days > remaining)
					return (false, $"Cannot approve: insufficient balance ({(remaining < 0 ? 0 : remaining)} day(s) remaining, {req.Days} requested).");

				step.Status = 1; step.DecisionAt = now; step.DecisionNote = note;
				step.UpdatedAt = now; step.updatedBy = approverEmployeeId;
				req.Status = 1; req.ApproverEmployeeID = approverEmployeeId;
				req.DecisionAt = now; req.DecisionNote = note;
				req.CurrentApproverEmployeeID = null; req.CurrentLevel = 0;
				req.UpdatedAt = now; req.updatedBy = approverEmployeeId;
				await _context.SaveChangesAsync();

				// The top of the chain approved. Terminal, and the level that took the decision is named so
				// the rule closes THAT level's work rather than guessing which one was open.
				await EmitAsync(req, CrossBuy.BL.Platform.LeaveRequestEvents.Approved,
					level: decidedLevel, approver: approverEmployeeId, nextLevel: null, nextApprover: null,
					decidedBy: approverEmployeeId);
				await tx.CommitAsync();

				await _notifications.NotifyAsync(req.EmployeeID,
					"تمت الموافقة على إجازتك", "Your leave was approved",
					$"طلب {typeAr} ({req.Days} يوم) تمت الموافقة عليه نهائيًا بواسطة {aAr}.",
					$"Your {typeEn} request ({req.Days} day(s)) was fully approved by {aEn}.",
					"leave_approved", req.ID);
				return (true, null);
			}

			// ---- intermediate approval: advance to the next level ----
			step.Status = 1; step.DecisionAt = now; step.DecisionNote = note;
			step.UpdatedAt = now; step.updatedBy = approverEmployeeId;

			var nextLevel = req.CurrentLevel + 1;
			var nextStep = req.ApprovalSteps.First(s => s.Level == nextLevel);
			req.CurrentLevel = nextLevel;
			req.CurrentApproverEmployeeID = nextStep.ApproverEmployeeID;
			req.UpdatedAt = now; req.updatedBy = approverEmployeeId;
			await _context.SaveChangesAsync();

			// NOT A TERMINAL FACT. The request is still Pending; the work simply belongs to somebody else
			// now. Both levels travel in the payload because the rule has two things to do with one event:
			// close the level that decided, and open the level that now owes.
			await EmitAsync(req, CrossBuy.BL.Platform.LeaveRequestEvents.Advanced,
				level: decidedLevel, approver: approverEmployeeId, nextLevel: nextLevel,
				nextApprover: nextStep.ApproverEmployeeID, decidedBy: approverEmployeeId);
			await tx.CommitAsync();

			var requesterEmp = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == req.EmployeeID);
			var reqAr = requesterEmp?.FullName ?? "موظف";
			var reqEn = requesterEmp?.FullNameEn ?? requesterEmp?.FullName ?? "An employee";
			var nextTitle = await PositionTitleAsync(nextStep.ApproverEmployeeID);

			await _notifications.NotifyAsync(nextStep.ApproverEmployeeID,
				"طلب إجازة بانتظار اعتمادك", "Leave request awaiting your approval",
				$"طلب إجازة من {reqAr} ({req.Days} يوم) بانتظار اعتمادك.",
				$"A leave request from {reqEn} ({req.Days} day(s)) is awaiting your approval.",
				"leave_submitted", req.ID);

			await _notifications.NotifyAsync(req.EmployeeID,
				"تقدّم طلب إجازتك", "Your leave request advanced",
				$"وافق {aAr}، والطلب الآن بانتظار موافقة {nextTitle.ar}.",
				$"{aEn} approved; your request now awaits {nextTitle.en}'s approval.",
				"leave_progress", req.ID);

			return (true, null);
		}
	}
}
