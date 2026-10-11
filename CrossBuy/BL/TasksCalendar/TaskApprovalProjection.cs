using System.Linq;
using Microsoft.EntityFrameworkCore;
using CrossBuy.BL.Platform.Orchestration;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;

namespace CrossBuy.BL.TasksCalendar
{
	// =============================================================================================
	// WHICH TASKS ARE AN APPROVAL STEP  (Wave 3B-T)
	//
	// ONE DEFINITION, IN ONE PLACE, because "is this task waiting on an approval" is about to be asked
	// by more than one surface - the task list today, a workspace panel and a report later - and the
	// overdue rule already taught this module what happens when the same question is answered in
	// several files. See TaskLifecycle.cs, which exists for exactly that reason.
	//
	// THE AUTHORITATIVE WITNESS IS THE ORCHESTRATION LEDGER. A task represents an approval because a
	// COMMITTED business event was projected into work by one of the approval rules, and TaskAutoLog
	// records which rule and which source record. Nothing about that can be typed in by a user, which
	// is what makes it an answer rather than a guess.
	//
	// WHAT WAS DELIBERATELY NOT USED:
	//
	//   * TaskItem.EntityType. A task LINKED to a leave request is not necessarily the approval step
	//     for it - anybody can raise an ordinary task about the same record, and a filter keyed on the
	//     link would claim it.
	//   * The task's status, title or category. All three are free for a person to set, so any of them
	//     would make this filter a naming convention rather than a fact.
	//   * IApprovalInboxService. It answers a genuinely different question - what is waiting for ME TO
	//     APPROVE, as leave / employee-request / inventory rows that are not tasks and have no task id,
	//     assignee or lifecycle. The two are complementary; merging them would put rows in a task list
	//     that the task list cannot act on.
	//
	// OPEN IS PART OF THE DEFINITION. When the decision lands, the orchestration COMPLETES the projected
	// task through the canonical transition - so `not Done` is the whole difference between "still
	// waiting" and "was waiting", and no new column is needed to know it.
	// =============================================================================================
	public static class TaskApprovalProjection
	{
		/// Every projected task's ledger key starts with this; the rule name follows.
		public const string ProjectionPrefix = "TaskProjection:";

		// FIVE RULES, NAMED THROUGH THEIR OWN CONSTANTS so a rename upstream is a build error here
		// rather than a filter that silently stops matching.
		//
		// THE CLOSING RULES ARE ABSENT ON PURPOSE (…AdvanceClose): they complete the opener's task under
		// the OPENER's key and create no key of their own, so listing them would match nothing while
		// implying the opposite.
		//
		// ---- WHY DOCUMENT VERIFICATION IS HERE, HAVING BEEN LEFT OUT (3B-PLAT-1A) -----------------
		//
		// IT WAS EXCLUDED ON A LABEL AND INCLUDED ON EVIDENCE. The first version of this file reasoned
		// that "verification is not an approval decision". Reading DocumentVerificationRule settles it
		// the other way, and the rule says so in its own words: a submitted document "needs a human to
		// look at it", the work it projects asks for a DECISION, and BOTH outcomes close it -
		// DocumentVerified and DocumentRejected each complete the task, because "a rejection is a
		// decision, not a failure to decide". That is the same shape as the four silos below: an open
		// projected task means somebody with authority has not decided yet, and the decision event is
		// what ends it. A view that left it out under-reported work an approver is genuinely holding.
		//
		// DOCUMENT RESUBMISSION STAYS OUT, and on the same evidence rather than the same label. Its
		// task is "Re-file a rejected document", it is ASSIGNED TO THE OWNING EMPLOYEE by the event,
		// and it is closed by that employee filing again. Nobody is waiting to approve it - the filer
		// is waiting to act. Including it would put a person's own to-do into a queue that means
		// "waiting on a decision", which is exactly the over-reporting mirror of the defect above.
		//
		// THE REJECTED DOCUMENT NEEDS NO SPECIAL CASE. Rejection completes the verification task, so
		// the predicate below drops it on Status alone; the refiling work that a rejection may open is
		// a Resubmission row, which is not in this vocabulary. Nothing here reads a document state.
		private const string LeavePrefix = ProjectionPrefix + LeaveApprovalRule.Rule + ":";
		private const string EmployeeRequestPrefix = ProjectionPrefix + EmployeeRequestApprovalRule.Rule + ":";
		private const string StockApprovalPrefix = ProjectionPrefix + InventoryApprovalRule.Rule + ":";
		private const string ProjectBillingPrefix = ProjectionPrefix + ProjectBillingApprovalRule.Rule + ":";
		private const string DocumentVerificationPrefix = ProjectionPrefix + DocumentVerificationRule.Rule + ":";

		/// The five key prefixes, published so a test can assert the vocabulary rather than restate it.
		public static IReadOnlyList<string> ApprovalRuleKeyPrefixes { get; } = new[]
		{
			LeavePrefix, EmployeeRequestPrefix, StockApprovalPrefix, ProjectBillingPrefix,
			DocumentVerificationPrefix,
		};


		// =========================================================================================
		// WHICH APPROVAL FLOW  (3B-WORK-1)
		//
		// A MANAGEMENT REPORT HAS TO SAY WHICH BACKLOG A ROW BELONGS TO - leave, an employee request,
		// a stock movement, a progress billing, a document - and that is the same question this file
		// already answers when it decides membership. So it is answered HERE, once, from the same
		// prefixes, rather than a consumer re-deriving it from rule-key strings of its own. Reporting
		// owning a copy of these prefixes is exactly the drift this class exists to prevent.
		//
		// TECHNICAL CODES, NOT DISPLAY TEXT. The value is data: it is grouped on, filtered on and saved
		// into report templates, so it must survive a language change and a caption edit. Presentation
		// localizes it; this does not.
		//
		// THE CODES ARE THE RULE NAMES, deliberately. A flow IS its orchestration rule here, so inventing
		// a parallel vocabulary would create a second thing to keep in step.
		// =========================================================================================
		public static class ApprovalFlows
		{
			public const string Leave = "Leave";
			public const string EmployeeRequest = "EmployeeRequest";
			public const string Inventory = "Inventory";
			public const string ProjectBilling = "ProjectBilling";
			public const string DocumentVerification = "DocumentVerification";

			/// The five, in the order this file lists their prefixes. Published so a consumer can offer
			/// them as a filter vocabulary without restating them.
			public static IReadOnlyList<string> All { get; } = new[]
			{
				Leave, EmployeeRequest, Inventory, ProjectBilling, DocumentVerification,
			};
		}

		/// Which flow does this ledger key belong to, or null when it is not an approval at all.
		///
		/// PREFIX-ANCHORED, exactly as the membership predicate is: a key that merely CONTAINS a rule
		/// name - another rule's key mentioning a document, or a look-alike rule name - is not that flow.
		/// DocumentResubmission answers null here for the same reason it is not in the vocabulary: the
		/// filer is waiting to act, nobody is waiting to decide.
		public static string? FlowForRuleKey(string? ruleKey)
		{
			if (string.IsNullOrEmpty(ruleKey)) return null;
			if (ruleKey.StartsWith(LeavePrefix, StringComparison.Ordinal)) return ApprovalFlows.Leave;
			if (ruleKey.StartsWith(EmployeeRequestPrefix, StringComparison.Ordinal)) return ApprovalFlows.EmployeeRequest;
			if (ruleKey.StartsWith(StockApprovalPrefix, StringComparison.Ordinal)) return ApprovalFlows.Inventory;
			if (ruleKey.StartsWith(ProjectBillingPrefix, StringComparison.Ordinal)) return ApprovalFlows.ProjectBilling;
			if (ruleKey.StartsWith(DocumentVerificationPrefix, StringComparison.Ordinal)) return ApprovalFlows.DocumentVerification;
			return null;
		}

		/// The flow for each of these tasks, in ONE round trip.
		///
		/// SET-SHAPED ON PURPOSE. A report renders a page of rows and needs the flow for all of them; a
		/// per-row lookup would be the N+1 that every dataset in this product is written to avoid. The
		/// caller passes the ids it is about to render and gets a dictionary.
		///
		/// COMPANY-SCOPED ON THE LEDGER'S OWN COLUMN, beside whatever the caller already applied to the
		/// tasks. A ledger row from another tenant must not be able to label this company's work.
		///
		/// A TASK WITH TWO LEDGER ROWS keeps the FIRST flow that matches, and two approval flows on one
		/// task does not occur today: each projection rule opens its own task. The deterministic order
		/// is stated rather than left to the database so a repeated run cannot relabel a row.
		public static async Task<IReadOnlyDictionary<int, string>> FlowsForTasksAsync(
			CrossDbContext db, int companyId, IReadOnlyCollection<int> taskIds,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(db);
			var empty = new Dictionary<int, string>();
			if (companyId <= 0 || taskIds is null || taskIds.Count == 0) return empty;

			var rows = await db.TaskAutoLogs.AsNoTracking()
				.Where(l => l.CompanyId == companyId && taskIds.Contains(l.TaskId))
				.OrderBy(l => l.ID)
				.Select(l => new { l.TaskId, l.RuleKey })
				.ToListAsync(cancellationToken);

			var result = new Dictionary<int, string>();
			foreach (var row in rows)
			{
				if (result.ContainsKey(row.TaskId)) continue;
				var flow = FlowForRuleKey(row.RuleKey);
				if (flow != null) result[row.TaskId] = flow;
			}
			return result;
		}

		/// Narrow a task query to the approval steps still awaiting a decision.
		///
		/// IT NARROWS AND NEVER WIDENS. It composes onto whatever the caller has already scoped - the
		/// company, the read authorization, the employee breadth, any search - so it can only remove
		/// rows from a set the caller was already entitled to.
		///
		/// AN EXISTS SUBQUERY, NOT A JOIN. A task can carry more than one ledger row (two approval
		/// levels, a resubmission), and a join would return it twice and quietly break the caller's
		/// paging and its total.
		///
		/// THE LEDGER'S OWN CompanyId IS IN THE PREDICATE beside the task's. This is a tenant boundary,
		/// and a ledger row from another company must not be able to admit a task into this list.
		public static IQueryable<TaskItem> WaitingApproval(
			IQueryable<TaskItem> query, CrossDbContext db, int companyId)
		{
			ArgumentNullException.ThrowIfNull(query);
			ArgumentNullException.ThrowIfNull(db);
			if (companyId <= 0) return query.Where(_ => false);

			return query.Where(t => t.Status != TaskStatuses.Done
				&& db.TaskAutoLogs.Any(l => l.CompanyId == companyId
					&& l.TaskId == t.ID
					&& (l.RuleKey.StartsWith(LeavePrefix)
					 || l.RuleKey.StartsWith(EmployeeRequestPrefix)
					 || l.RuleKey.StartsWith(StockApprovalPrefix)
					 || l.RuleKey.StartsWith(ProjectBillingPrefix)
					 || l.RuleKey.StartsWith(DocumentVerificationPrefix))));
		}
	}
}
