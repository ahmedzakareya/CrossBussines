using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// HR-8 — ESS self-service requests (letters + hourly permissions). Reuses the leave approval
	// chain (ManagerChainAsync) and notification patterns. No GL impact.
	public interface IEmployeeRequestService
	{
		Task<(bool ok, string? error, EmployeeRequest? req)> CreateAsync(EmployeeRequest draft);
		Task<(bool ok, string? error)> DecideAsync(int requestId, int approverEmployeeId, bool approve, string? note);
		/// Latest payslip figures for the salary certificate (net/gross/base), or null if none posted yet.
		Task<Payslip?> LatestPayslipAsync(int employeeId);

		/// Pending employee requests awaiting this approver, in the context's company. READ ONLY - the
		/// approval inbox's module reader (SHF-15), not part of the workflow.
		Task<IReadOnlyList<CrossBuy.BL.Approvals.PendingApprovalRow>> PendingForApproverAsync(
			CrossBuy.Models.Platform.BusinessContext context, int approverEmployeeId,
			CancellationToken cancellationToken = default);
	}

	public class EmployeeRequestService : IEmployeeRequestService
	{
		private readonly CrossDbContext _context;
		private readonly INotificationService _notifications;
		private readonly ILeaveWorkflowService _chain;   // reuse ManagerChainAsync
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;

		// REQUIRED, AND NOT NULLABLE. Employee request event-to-work exists because these facts reach
		// the platform; a transition that committed without its event would be a silent hole in the
		// backbone that nothing downstream could detect. So there is ONE constructor, every parameter
		// is mandatory, and the type system - not a runtime guard - is what makes a recorder-less
		// service unconstructible. This is deliberately unlike the optional-recorder shape carried as
		// RSK-25 elsewhere: that debt exists precisely because an optional dependency let a service
		// write rows and publish nothing with no test able to tell.
		private readonly CrossBuy.BL.Platform.IBusinessEventService _events;

		public EmployeeRequestService(CrossDbContext context, INotificationService notifications,
			ILeaveWorkflowService chain, CrossBuy.BL.Platform.IBusinessContextAccessor contexts,
			CrossBuy.BL.Platform.IBusinessEventService events)
		{
			_context = context; _notifications = notifications; _chain = chain;
			_contexts = contexts; _events = events;
		}

		// ---- the canonical employee request fact --------------------------------------------
		//
		// ONE emitter for all four transitions, so the payload shape and the identity rule cannot
		// drift between them. Called INSIDE the authoritative transaction and BEFORE its commit: the
		// kernel enforces that contract by THROWING without an ambient transaction (ADR-001), so an
		// event for a transition that rolls back is not merely unlikely, it is unwritable.
		//
		// THE IDENTITY IS THE REQUEST, THE LEVEL AND THE ACTION, because a chain produces several
		// facts about one request and a key without the level would make level 2's approval a
		// duplicate of level 1's and silently drop it.
		//
		// NO COMPANY IN THE PAYLOAD. The envelope carries it, taken from the ambient BusinessContext
		// by BusinessEventService - a second copy is a second answer waiting to disagree.
		private Task EmitAsync(EmployeeRequest req, string eventType, int level, int? approver,
			int? nextLevel, int? nextApprover, int? decidedBy, CancellationToken cancellationToken = default)
		=> _events.RecordAsync(new CrossBuy.Models.Platform.BusinessEventRecord
		{
			EntityCode = CrossBuy.BL.Platform.EntityRegistry.EmployeeRequest,
			EntityId = req.ID,
			EventType = eventType,
			PayloadVersion = CrossBuy.Models.Platform.EmployeeRequestEventPayload.Version,
			Visibility = CrossBuy.Models.Platform.BusinessEventVisibility.Internal,
			DedupKey = $"employee-request:{req.ID}:level:{level}:{CrossBuy.BL.Platform.BusinessEventTypes.ActionOf(eventType)}",
			Payload = new CrossBuy.Models.Platform.EmployeeRequestEventPayload
			{
				EmployeeRequestId = req.ID,
				RequesterEmployeeId = req.EmployeeID,
				RequestType = req.RequestType,
				Level = level,
				ApproverEmployeeId = approver,
				NextLevel = nextLevel,
				NextApproverEmployeeId = nextApprover,
				DecidedByEmployeeId = decidedBy,
			},
		}, cancellationToken);


		public Task<Payslip?> LatestPayslipAsync(int employeeId) =>
			_context.Payslips.AsNoTracking().Where(s => s.EmployeeID == employeeId)
				.OrderByDescending(s => s.Year).ThenByDescending(s => s.Month).FirstOrDefaultAsync();

		private static string Label(EmployeeRequest r, bool ar)
		{
			if (r.RequestType == "Permission") return ar ? "إذن" : "permission";
			return ar ? "خطاب" : "letter";
		}

		// =============================================================================================
		// THE APPROVAL INBOX'S EMPLOYEE-REQUEST READER (SHF-15 narrow read handoff).
		//
		// A READ. CreateAsync, DecideAsync, the workflow transitions, the approval writes and the
		// notification logic are all untouched.
		//
		// EmployeeRequest DOES carry CompanyID - written from the requester's own Employee.EmpCompanyID - so
		// the boundary is stated directly on the row rather than inferred through a join. The approver match
		// alone would not be isolation: Employee.ID is a global primary key, so it rules out an id COLLISION
		// across companies but holds only as long as approver assignment is correct.
		//
		// FAIL CLOSED: no company or no approver yields an empty list.
		// =============================================================================================
		public async Task<IReadOnlyList<CrossBuy.BL.Approvals.PendingApprovalRow>> PendingForApproverAsync(
			CrossBuy.Models.Platform.BusinessContext context, int approverEmployeeId,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);

			var empty = Array.Empty<CrossBuy.BL.Approvals.PendingApprovalRow>();
			if (context.CompanyId <= 0 || approverEmployeeId <= 0) return empty;

			var rows = await _context.EmployeeRequests.AsNoTracking()
				.Where(r => r.Status == 0
					&& r.CurrentApproverEmployeeID == approverEmployeeId
					&& r.CompanyID == context.CompanyId)
				.OrderByDescending(r => r.ID)
				.ToListAsync(cancellationToken);

			return rows.Select(r => new CrossBuy.BL.Approvals.PendingApprovalRow
			{
				Silo = CrossBuy.BL.Approvals.ApprovalSilos.Request,
				EntityId = r.ID,
				ApprovalType = r.RequestType,
				// The title is the request KIND, which the inbox localizes ("Letter" / "Permission"). The
				// discriminator travels in ApprovalType; no rendered string is invented here.
				RequesterEmployeeId = r.EmployeeID,
				SubmittedAt = r.CreatedAt,
				Status = "Pending",
				OnDate = r.PermissionDate,
				FromTime = r.FromTime,
				ToTime = r.ToTime,
				TitleEn = r.LetterType,
				Navigation = new CrossBuy.BL.Approvals.ApprovalNavigationTarget("People", "Requests"),
			}).ToList();
		}

		public async Task<(bool ok, string? error, EmployeeRequest? req)> CreateAsync(EmployeeRequest draft)
		{
			if (draft.EmployeeID <= 0) return (false, "Employee is required", null);
			if (draft.RequestType == "Permission")
			{
				if (draft.PermissionDate == null || draft.FromTime == null || draft.ToTime == null)
					return (false, "The date and the start/end time are required for a permission request", null);
				if (draft.ToTime <= draft.FromTime) return (false, "The end time must be after the start time", null);
			}
			else
			{
				draft.RequestType = "Letter";
				if (string.IsNullOrWhiteSpace(draft.LetterType)) return (false, "Letter type is required", null);
			}

			// Approver selection is SHARED with LeaveWorkflowService, so the company boundary that Batch C.1
			// added to the hierarchy climb applies here too — and so does its trap. `chain.Count == 0` below
			// means "the requester is at the top of the tree" and AUTO-APPROVES the request. Once the climb
			// intersects with the requester's company, a cross-company graft ALSO empties the chain, so
			// without this guard closing the isolation leak would silently approve employee requests that
			// have no approver at all. Same refusal as the leave path, for the same reason.
			var chainResult = await _chain.ApproverChainAsync(draft.EmployeeID);
			var chain = chainResult.Approvers;

			// MERGE: the SOURCE condition, the DESTINATION message. RSK-31 added the verified-root test -
			// an empty chain is approved only when the requester is POSITIVELY proven a structural root,
			// never merely because the climb returned nothing - and that repair must survive. The English
			// text is the destination line's translation of the same sentence, so keeping it loses no
			// meaning.
			//
			// THE SAME RULE AS LEAVE, AND THAT IS THE POINT (RSK-31). Both flows ask the same resolver,
			// so a root policy enforced in one and not the other would be worse than enforcing it in
			// neither: it would look closed. The request is refused BEFORE the transaction opens, so a
			// hierarchy that cannot be trusted produces no request, no business fact and no work.
			if (chain.Count == 0 && (chainResult.HierarchyDefect || !chainResult.RequesterIsVerifiedRoot))
				return (false, "The approval chain for this employee cannot be determined — the organisation structure is not correct. Please contact HR.", null);
			draft.CreatedAt = DateTime.UtcNow;
			draft.CreatedBy = draft.EmployeeID;
			if (chain.Count == 0)
			{
				draft.Status = 1; draft.CurrentLevel = 0; draft.CurrentApproverEmployeeID = null; draft.DecisionAt = DateTime.UtcNow;
			}
			else
			{
				draft.Status = 0; draft.CurrentLevel = 1; draft.CurrentApproverEmployeeID = chain[0];
			}
			// ONE AUTHORITATIVE UNIT. The request and its approval steps used to be two unprotected
			// saves, so a failure between them persisted a request with NO chain behind it - a row that
			// DecideAsync then refuses for ever, because there is genuinely no step to decide. Same repair,
			// same mechanism and same reason as the Leave path: BeginOrJoin, so a caller that already owns
			// a transaction keeps owning it and this one enlists rather than opening a second.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);

			_context.EmployeeRequests.Add(draft);
			await _context.SaveChangesAsync();

			if (chain.Count > 0)
			{
				for (var i = 0; i < chain.Count; i++)
					_context.EmployeeRequestSteps.Add(new EmployeeRequestStep
					{
						EmployeeRequestID = draft.ID, Level = i + 1, ApproverEmployeeID = chain[i], Status = 0,
						CreatedAt = DateTime.UtcNow, CreatedBy = draft.EmployeeID
					});
				await _context.SaveChangesAsync();

				// The steps exist, so the state the fact describes is internally valid: level 1 is owed by
				// a real approver with a real step behind it. Inside the transaction, before the commit.
				await EmitAsync(draft, CrossBuy.BL.Platform.EmployeeRequestEvents.Submitted,
					level: 1, approver: chain[0], nextLevel: null, nextApprover: null, decidedBy: null);
			}
			else
			{
				// TOP OF THE TREE. The domain auto-approves because nobody is above this requester, and that
				// behaviour is preserved exactly. Two facts are recorded because two things really happened -
				// the request was raised and it ended Approved - and LEVEL 0 is how the payload says NOBODY
				// WAS EVER ASKED. The Phase-A rule reads 0 and creates nothing, so there is no phantom Task:
				// work raised for an approver who does not exist is work nobody can do.
				await EmitAsync(draft, CrossBuy.BL.Platform.EmployeeRequestEvents.Submitted,
					level: 0, approver: null, nextLevel: null, nextApprover: null, decidedBy: null);
				await EmitAsync(draft, CrossBuy.BL.Platform.EmployeeRequestEvents.Approved,
					level: 0, approver: null, nextLevel: null, nextApprover: null, decidedBy: null);
			}

			await tx.CommitAsync();

			// AFTER THE COMMIT. A notification for a request that rolled back is a message about something
			// that did not happen. Recipients, wording and type are untouched - this call moved, it did not
			// change.
			if (chain.Count > 0)
			{
				var requester = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == draft.EmployeeID);
				var rAr = requester?.FullName ?? "موظف";
				var rEn = requester?.FullNameEn ?? requester?.FullName ?? "An employee";
				await _notifications.NotifyAsync(chain[0],
					$"New {Label(draft, true)} request", $"New {Label(draft, false)} request",
					$"قدّم {rAr} {Label(draft, true)} بانتظار اعتمادك.",
					$"{rEn} submitted a {Label(draft, false)} request awaiting your approval.",
					"emp_request_submitted", draft.ID);
			}
			return (true, null, draft);
		}

		// ===========================================================================================
		// THE DECISION IS AUTHORIZED HERE, NOT ONLY AT THE CONTROLLER (RSK-28).
		//
		// WHAT WAS WRONG, measured on this file. The request was read with no company predicate at all -
		// even though EmployeeRequest DOES carry its own CompanyID, and the READ path a few lines above
		// already filters on it, so the WRITE path was less isolated than the read beside it. The only
		// authority test was that the `approverEmployeeId` PARAMETER equalled the stored current
		// approver, which means a caller supplying somebody else's id decided AS them. And the guards
		// were a read followed by a write with no transaction and no claim, so two deciders could both
		// pass them and both write.
		//
		// WHAT IS DELIBERATELY NOT HERE: a separation-of-duties rule. Leave refuses a requester deciding
		// their own request; whether an employee request should do the same is an open PRODUCT question,
		// and inventing an answer inside a security repair would settle it by accident. The behaviour is
		// left exactly as it is and documented by test instead.
		// ===========================================================================================
		private async Task<int?> MayDecideAsync(EmployeeRequest req, int approverEmployeeId, CancellationToken ct)
		{
			var caller = await _contexts.TryGetCurrentAsync(ct);
			if (caller is null || caller.CompanyId <= 0) return null;
			if (caller.EmployeeId is not int me || me <= 0) return null;

			// THE PARAMETER IS CHECKED AGAINST THE IDENTITY, never trusted in place of it.
			if (approverEmployeeId != me) return null;

			// TENANCY FROM THE REQUEST'S OWN COLUMN. EmployeeRequest carries CompanyID, written server-side
			// from the requester's Employee row, so unlike Leave this needs no join and no inference.
			if (req.CompanyID != caller.CompanyId) return null;

			// THE STORED CURRENT APPROVER, and only them. Not a manager, not an HR role, not an admin.
			if (req.CurrentApproverEmployeeID != me) return null;

			// SEPARATION OF DUTIES AT THE WRITE BOUNDARY (RSK-32), and the LAST thing asked, so it can
			// never be satisfied by an earlier check passing. Every test above establishes WHO the caller
			// is; this one asks whether that person may decide THIS request, and the answer is no when
			// they are the one who raised it.
			//
			// THE HONEST SEVERITY: this was not a reachable exploit. The climb breaks on mgr == employeeId
			// on every iteration, so the requester cannot enter the chain at any level; the only production
			// caller reads the subject from the row and gates on the HrAccessService rule that refuses
			// subject == me; and the Task layer refuses self-assigned approval work. Three controls, all
			// upstream, all currently correct.
			//
			// WHICH IS EXACTLY THE PROBLEM. Nothing here made a FOURTH caller correct. This service is the
			// authoritative decision boundary, and an authority that depends on its callers to enforce a
			// rule is not enforcing it - it is hoping. Leave already declines to be the place where this
			// starts being possible; this is the same sentence, on the other flow that shares the resolver.
			if (req.EmployeeID == me) return null;

			return me;
		}

		public async Task<(bool ok, string? error)> DecideAsync(int requestId, int approverEmployeeId, bool approve, string? note)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_context);

			var req = await _context.EmployeeRequests.Include(r => r.ApprovalSteps).FirstOrDefaultAsync(r => r.ID == requestId);
			if (req == null) return (false, "Order not found");
			if (req.Status != 0) return (false, "The request has already been decided");

			// MERGE: the SOURCE authorization repair, with the DESTINATION translations.
			// MayDecideAsync SUPERSEDES the destination's bare CurrentApproverEmployeeID comparison - it
			// makes that same check AND resolves the caller from the context, verifies the company through
			// the requester, and applies separation of duties. Dropping the destination line loses nothing;
			// it is contained in what replaces it. The binding is also required: `actor` is consumed by the
			// atomic step claim below, so the source form is the only one that compiles.
			//
			// BEFORE ANY MUTATION AND BEFORE THE CLAIM. A refusal here alters no request and no step,
			// sends no notification and records no attribution: the transaction opened above is simply
			// never written to and rolls back on dispose.
			if (await MayDecideAsync(req, approverEmployeeId, CancellationToken.None) is not int actor)
				return (false, "You do not have permission to approve this request");

			var step = req.ApprovalSteps.FirstOrDefault(s => s.Level == req.CurrentLevel && s.Status == 0);
			if (step == null) return (false, "Approval step not found");

			// ---- ONE DECISION WINS ---------------------------------------------------------------
			//
			// The guards above are a READ followed by a WRITE, and between them two approvers pressing at
			// once both saw pending. Read-committed does not stop that and there is no RowVersion on any
			// table here, so the step is CLAIMED with one conditional statement and the DATABASE decides.
			// The predicate re-states every fact the decision rests on - the request, the level, the
			// approver and the pending status - so a row that moved between the read and this line no
			// longer matches. Exactly one caller sees 1 row affected; the loser sees 0 and is refused with
			// the message the domain already uses for a decided request, because that is what happened.
			int claimed = await _context.EmployeeRequestSteps
				.Where(x => x.ID == step.ID
					&& x.EmployeeRequestID == req.ID
					&& x.Level == req.CurrentLevel
					&& x.ApproverEmployeeID == actor
					&& x.Status == 0)
				.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, approve ? 1 : 2));
			if (claimed != 1) return (false, "تم البتّ في الطلب مسبقًا");

			// THE LEVEL BEING DECIDED, captured before anything moves it. The final branch zeroes
			// CurrentLevel, so reading it afterwards would name the wrong level in the fact and close the
			// wrong Task.
			int decidedLevel = req.CurrentLevel;

			var approver = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == actor);
			var aAr = approver?.FullName ?? "المدير";
			var aEn = approver?.FullNameEn ?? approver?.FullName ?? "Manager";
			var lblAr = Label(req, true); var lblEn = Label(req, false);
			var now = DateTime.UtcNow;

			if (!approve)
			{
				step.Status = 2; step.DecisionAt = now; step.DecisionNote = note; step.UpdatedAt = now; step.updatedBy = actor;
				req.Status = 2; req.ApproverEmployeeID = actor; req.DecisionAt = now; req.DecisionNote = note;
				req.CurrentApproverEmployeeID = null; req.UpdatedAt = now; req.updatedBy = actor;
				await _context.SaveChangesAsync();
				// A rejection ends the WHOLE request, at whatever level it happened. One terminal fact.
				await EmitAsync(req, CrossBuy.BL.Platform.EmployeeRequestEvents.Rejected,
					level: decidedLevel, approver: actor, nextLevel: null, nextApprover: null, decidedBy: actor);
				await tx.CommitAsync();
				await _notifications.NotifyAsync(req.EmployeeID, $"تم رفض {lblAr}", $"Your {lblEn} was rejected",
					$"تم رفض طلب {lblAr} بواسطة {aAr}.", $"Your {lblEn} request was rejected by {aEn}.", "emp_request_rejected", req.ID);
				return (true, null);
			}

			var maxLevel = req.ApprovalSteps.Count == 0 ? 0 : req.ApprovalSteps.Max(s => s.Level);
			if (req.CurrentLevel >= maxLevel)
			{
				step.Status = 1; step.DecisionAt = now; step.DecisionNote = note; step.UpdatedAt = now; step.updatedBy = actor;
				req.Status = 1; req.ApproverEmployeeID = actor; req.DecisionAt = now; req.DecisionNote = note;
				req.CurrentApproverEmployeeID = null; req.CurrentLevel = 0; req.UpdatedAt = now; req.updatedBy = actor;
				await _context.SaveChangesAsync();
				// The top of the chain approved. Terminal, and the level that took the decision is named so
				// the rule closes THAT level's work rather than guessing which one was open.
				await EmitAsync(req, CrossBuy.BL.Platform.EmployeeRequestEvents.Approved,
					level: decidedLevel, approver: actor, nextLevel: null, nextApprover: null, decidedBy: actor);
				await tx.CommitAsync();
				await _notifications.NotifyAsync(req.EmployeeID, $"تمت الموافقة على {lblAr}", $"Your {lblEn} was approved",
					$"تمت الموافقة على طلب {lblAr} نهائيًا بواسطة {aAr}.", $"Your {lblEn} request was fully approved by {aEn}.", "emp_request_approved", req.ID);
				return (true, null);
			}

			step.Status = 1; step.DecisionAt = now; step.DecisionNote = note; step.UpdatedAt = now; step.updatedBy = actor;
			var nextLevel = req.CurrentLevel + 1;
			var nextStep = req.ApprovalSteps.First(s => s.Level == nextLevel);
			req.CurrentLevel = nextLevel; req.CurrentApproverEmployeeID = nextStep.ApproverEmployeeID;
			req.UpdatedAt = now; req.updatedBy = actor;
			await _context.SaveChangesAsync();
			// NOT A TERMINAL FACT. The request is still pending; the work simply belongs to somebody else
			// now. Both levels travel because the rule has two things to do with one event: close the
			// level that decided, and open the level that now owes.
			await EmitAsync(req, CrossBuy.BL.Platform.EmployeeRequestEvents.Advanced,
				level: decidedLevel, approver: actor, nextLevel: nextLevel,
				nextApprover: nextStep.ApproverEmployeeID, decidedBy: actor);
			await tx.CommitAsync();
			await _notifications.NotifyAsync(nextStep.ApproverEmployeeID,
				$"طلب {lblAr} بانتظار اعتمادك", $"A {lblEn} request awaiting your approval",
				$"طلب {lblAr} بانتظار اعتمادك.", $"A {lblEn} request is awaiting your approval.", "emp_request_submitted", req.ID);
			return (true, null);
		}
	}
}
