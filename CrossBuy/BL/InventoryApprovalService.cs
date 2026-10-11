using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// payloads captured at submit-time and replayed on approval
	public class PoApprovalPayload { public int VendorId { get; set; } public int? WarehouseId { get; set; } public DateTime OrderDate { get; set; } public DateTime? ExpectedDate { get; set; } public string? Notes { get; set; } public List<PoLineInput> Lines { get; set; } = new(); }
	public class TransferApprovalPayload { public int FromWarehouseId { get; set; } public int ToWarehouseId { get; set; } public DateTime Date { get; set; } public string? Notes { get; set; } public List<TransferLineInput> Lines { get; set; } = new(); }
	public class CountApprovalPayload { public int WarehouseId { get; set; } public DateTime CountDate { get; set; } public string? Notes { get; set; } public List<CountLineInput> Lines { get; set; } = new(); }
	public class WriteOffApprovalPayload { public int WarehouseId { get; set; } public DateTime WriteOffDate { get; set; } public string? Reason { get; set; } public string? Notes { get; set; } public List<WriteOffLineInput> Lines { get; set; } = new(); }

	public interface IInventoryApprovalService
	{
		Task<bool> RequiresApprovalAsync(decimal amount);
		Task<int> SubmitAsync(string docType, decimal amount, object payload, int? requestedBy);
		Task<List<InventoryApproval>> PendingAsync();

		// The approval INBOX read. Deliberately separate from PendingAsync(): that one answers "what is
		// pending in the current company", this one answers "what may THIS approver ACT ON, in the company the
		// server resolved for them". It returns the visibility gate alongside the rows because the caller needs
		// to distinguish "not an inventory approver here" from "an approver with an empty queue", and deciding
		// that twice invites the two answers to disagree.
		Task<(bool isInventoryManager, List<InventoryApproval> pending)> ApprovalInboxAsync(
			CrossBuy.Models.Platform.BusinessContext context, int approverEmployeeId,
			CancellationToken cancellationToken = default);
		Task<List<InventoryApproval>> RecentAsync(int take = 50);
		Task<(bool ok, string? error)> ApproveAsync(int id, int approverEmp, string? note);
		Task<(bool ok, string? error)> RejectAsync(int id, int approverEmp, string? note);
	}

	public class InventoryApprovalService : IInventoryApprovalService
	{
		private readonly CrossDbContext _db;
		private readonly IStockService _stock;
		private readonly IProcurementService _proc;
		private readonly INotificationService _notify;

		/// A test-only seam that fails in the one window this repair is about: after the authoritative
		/// document exists and before the decision is persisted. Never set outside tests.
		/// PUBLIC only because the test assembly is separate and this repository declares no
		/// InternalsVisibleTo. It is never set by any production path - the seam exists so the one
		/// window this repair is about can be reproduced deterministically instead of by breaking a
		/// database, and a structural test asserts nothing under CrossBuy/ ever assigns it.
		public Func<Task>? BeforeDecisionSaveHookForTests { get; set; }
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _context;

		// THE INVENTORY AUTHORITY, resolved BY SCOPE rather than by casting a concrete class. That is the
		// pattern ModulePermissionAdapterBase already uses and its reason applies here too: a cast compiles
		// and then fails the first time the service is decorated or mocked. A missing module DENIES.
		//
		// OPTIONAL, and two constructors rather than one changed one, so every existing construction site
		// keeps compiling. Null is NOT allow-everything: see AuthorizeDecisionAsync, which refuses outright
		// when no authority is wired, because an approval engine that cannot ask is an approval engine that
		// must not decide.
		private readonly IEnumerable<CrossBuy.BL.Platform.IModuleAccessService>? _modules;

		public InventoryApprovalService(CrossDbContext db, IStockService stock, IProcurementService proc,
			INotificationService notify, CrossBuy.BL.Platform.IBusinessContextAccessor context)
		{ _db = db; _stock = stock; _proc = proc; _notify = notify; _context = context; }

		public InventoryApprovalService(CrossDbContext db, IStockService stock, IProcurementService proc,
			INotificationService notify, CrossBuy.BL.Platform.IBusinessContextAccessor context,
			IEnumerable<CrossBuy.BL.Platform.IModuleAccessService> modules)
			: this(db, stock, proc, notify, context) { _modules = modules; }

		// THE CANONICAL EVENT RECORDER, and the constructor DI actually picks.
		//
		// The container selects the greediest constructor whose parameters all resolve, so production always
		// lands here and always has a recorder. It is OPTIONAL only because five of the seven hand-built test
		// fixtures that construct this service are owned by another tab and cannot be edited from here; making
		// it required would have meant editing files this increment is not granted.
		//
		// THAT COMPROMISE IS NAMED RATHER THAN HIDDEN, because the repository has already paid for it once:
		// CrmService carried an optional recorder for the same reason and could be built writing rows and
		// publishing nothing, with no test able to tell. A guard test asserts that the constructor DI resolves
		// carries the recorder, so the hole cannot reach production - and the debt is recorded so it can be
		// closed the way SHF-77 closed the CRM one.
		public InventoryApprovalService(CrossDbContext db, IStockService stock, IProcurementService proc,
			INotificationService notify, CrossBuy.BL.Platform.IBusinessContextAccessor context,
			IEnumerable<CrossBuy.BL.Platform.IModuleAccessService> modules,
			CrossBuy.BL.Platform.IBusinessEventService events)
			: this(db, stock, proc, notify, context, modules) { _events = events; }

		private readonly CrossBuy.BL.Platform.IBusinessEventService? _events;

		// ---- the lifecycle event -------------------------------------------------------------------
		//
		// ONE emitter for all three transitions and all four families, so the payload shape and the identity
		// rule cannot drift between them. Called INSIDE the caller's transaction and BEFORE its commit, which
		// is the contract the kernel enforces: RecordAsync THROWS without an ambient transaction (ADR-001), so
		// an event for a decision that rolls back is not merely unlikely - it is unwritable.
		//
		// THE IDENTITY IS THE APPROVAL PLUS THE TRANSITION, and that is enough because the lifecycle allows
		// exactly one of each: one Pending, then one terminal decision, guarded by `Status != "Pending"`. There
		// is no round, no attempt and no version to fold in, and inventing one from a timestamp would
		// manufacture an identity the domain does not have.
		private Task EmitAsync(InventoryApproval ap, string eventType, CancellationToken cancellationToken = default)
		{
			if (_events is null) return Task.CompletedTask;
			return _events.RecordAsync(new CrossBuy.Models.Platform.BusinessEventRecord
			{
				EntityCode = CrossBuy.BL.Platform.EntityRegistry.InventoryApproval,
				EntityId = ap.ID,
				EventType = eventType,
				PayloadVersion = InventoryApprovalEventPayload.Version,
				Visibility = CrossBuy.Models.Platform.BusinessEventVisibility.Internal,
				DedupKey = $"inventoryapproval:{ap.ID}:{CrossBuy.BL.Platform.BusinessEventTypes.ActionOf(eventType)}",
				Payload = new InventoryApprovalEventPayload
				{
					ApprovalId = ap.ID,
					DocType = ap.DocType,
					Status = ap.Status,
					Amount = ap.Amount,
					RequestedByEmployeeId = ap.RequestedByEmployeeId,
					DecidedByEmployeeId = ap.DecidedByEmployeeId,
					ResultDocNo = ap.ResultDocNo,
				},
			}, cancellationToken);
		}

		// ---- THE SERVICE IS ITS OWN SECURITY BOUNDARY ----
		//
		// Before this, ApproveAsync and RejectAsync enforced company, Pending state and separation of duties,
		// and asked NOBODY whether the caller may approve anything at all. The only permission check was the
		// [InvPerm(manage)] attribute on the controller, which means the authoritative service was not a
		// boundary but an assumption: any other caller - a second controller, a background path, a future API,
		// a diagnostic endpoint - reached the same method and the same stock consequence with no gate at all.
		//
		// IT ASKS THE SAME QUESTION THE ATTRIBUTE ASKS, of the same service: Inventory manage. No role name
		// appears here. A role list in this file would be a second authorization source, and the two would
		// drift the first time either was edited.
		private async Task<bool> CanDecideAsync(int companyId, CancellationToken cancellationToken = default)
		{
			// No authority wired means the question cannot be asked, and an unanswerable question is a refusal.
			// This is the opposite of the usual optional-dependency default, deliberately: the thing on the other
			// side of this gate creates purchase orders and moves stock.
			if (_modules == null) return false;

			var inventory = _modules.FirstOrDefault(m => string.Equals(
				m.Scope, CrossBuy.BL.Platform.EntityRegistry.ScopeInventory, StringComparison.Ordinal));
			if (inventory == null) return false;

			var ctx = await _context.TryGetCurrentAsync(cancellationToken);
			if (ctx == null) return false;

			// The target names the company so the module refuses a foreign one before reading any role - the same
			// ordering every other module gate uses.
			return await inventory.CanAsync(ctx, "manage",
				new CrossBuy.Models.Platform.PermissionTarget { CompanyId = companyId }, cancellationToken);
		}

		// STAGE 1 BATCH A — `private const int CompanyId = 1` removed. It meant an approval raised by a
		// company-2 user was stored under company 1, notified company 1's managers, and on approval REPLAYED
		// its held stock/PO command into company 1.
		//
		// The replacement is deliberately split, because the two directions carry very different risk:
		//
		//   * READS and NEW SUBMISSIONS resolve the company from the BusinessContext. An unresolved context
		//     throws rather than defaulting — a threshold check or an approval queue for "no company" is
		//     meaningless.
		//   * APPROVE / REJECT use the APPROVAL ROW'S OWN CompanyID. That is the honest source: the payload was
		//     captured under that company's warehouses and vendors, so replaying it anywhere else would be a
		//     cross-company command execution. It also makes this change provably behaviour-preserving on
		//     existing data, where every row's CompanyID is already 1.
		//
		// No stock or GL calculation is altered: the same StockService/ProcurementService methods receive the
		// same payload; only the company argument now comes from the row instead of a constant.
		private async Task<int> CurrentCompanyIdAsync(CancellationToken cancellationToken = default)
		{
			var context = await _context.GetCurrentAsync(cancellationToken);   // throws when unresolved
			return context.CompanyId;
		}

		public async Task<bool> RequiresApprovalAsync(decimal amount)
		{
			int companyId = await CurrentCompanyIdAsync();
			var th = await _db.InventorySettings.AsNoTracking().Where(s => s.CompanyID == companyId).Select(s => s.ApprovalThreshold).FirstOrDefaultAsync();
			return th > 0 && Math.Abs(amount) >= th;
		}

		// A TRANSACTION, for the same reason RejectAsync grew one: the Requested fact has to be recorded
		// inside the transaction that makes the approval durably Pending, and a bare SaveChanges leaves
		// nowhere to put it. The threshold decision above this method is untouched - a document below the
		// threshold never reaches here, so it creates no approval row and emits no Requested fact.
		public async Task<int> SubmitAsync(string docType, decimal amount, object payload, int? requestedBy)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			int companyId = await CurrentCompanyIdAsync();
			var ap = new InventoryApproval
			{
				CompanyID = companyId, DocType = docType, Amount = Math.Round(amount, 2), PayloadJson = JsonSerializer.Serialize(payload),
				Status = "Pending", RequestedByEmployeeId = requestedBy, RequestedAt = DateTime.UtcNow
			};
			_db.InventoryApprovals.Add(ap);
			await _db.SaveChangesAsync();

			// The row has an id now, so the event can name it. Inside the transaction and before the commit:
			// a Requested fact that outlived a rollback would tell the platform review is owed on an approval
			// that does not exist, and the task orchestration would open work nobody can ever close.
			await EmitAsync(ap, CrossBuy.BL.Platform.InventoryApprovalEvents.Requested);
			await tx.CommitAsync();

			// AFTER THE COMMIT, matching the two decision paths. The recipients, the wording and the localized
			// document-type name are untouched - this call moved, it did not change. A notification sent from
			// inside a transaction that can still roll back is a statement that something might have happened.
			// notify all inventory managers except the requester (SoD) — in THIS company
			int reqId = requestedBy ?? -1;
			var managers = await _db.InventoryUserRoles.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.Role == "InventoryManager" && r.EmployeeId != reqId)
				.Select(r => r.EmployeeId).Distinct().ToListAsync();
			foreach (var m in managers)
				await _notify.NotifyAsync(m, "طلب اعتماد مخزون", "Inventory approval request",
					$"مستند {DocTypeName(docType)} بقيمة {ap.Amount:N2} بانتظار اعتمادك", $"{docType} of {ap.Amount:N2} awaits your approval", "InventoryApproval", ap.ID);
			return ap.ID;
		}

		public async Task<List<InventoryApproval>> PendingAsync()
		{
			int companyId = await CurrentCompanyIdAsync();
			return await _db.InventoryApprovals.AsNoTracking().Where(a => a.CompanyID == companyId && a.Status == "Pending").OrderBy(a => a.ID).ToListAsync();
		}

		// =============================================================================================
		// APPROVAL INBOX — the company-isolated read that ApprovalsController.Index consumes.
		//
		// WHAT WAS WRONG. ApprovalsController queried the tables directly, and NEITHER query named a company:
		//
		//     _db.InventoryUserRoles .AnyAsync(r => r.EmployeeId == empId && r.Role == "InventoryManager")
		//     _db.InventoryApprovals .Where (a => a.Status == "Pending" && a.RequestedByEmployeeId != empId)
		//
		// So an inventory manager saw EVERY company's pending approvals — requester name, document type and
		// amount — and the role that unlocked that view could have been granted in a DIFFERENT company
		// entirely: holding InventoryManager anywhere authorized the inbox everywhere. Two defects, one
		// missing predicate each: a data leak and an authorization widening.
		//
		// WHY THE COMPANY IS A PARAMETER. It comes from BusinessContext, which BusinessContextFactory derives
		// from the signed-in employee's own Employee.EmpCompanyID — never from a query string, form field,
		// route value or session blob, and with no company-1 fallback to land on. Taking it as an argument
		// rather than resolving it here keeps this method callable from a worker or a test with an explicit
		// company, and keeps it independent of the legacy company constant the write paths in this class still
		// carry.
		//
		// FAIL CLOSED. An unresolved company or an unresolved employee yields "not an approver, no rows" —
		// never an unfiltered read. Nothing here widens on missing input.
		// =============================================================================================
		public async Task<(bool isInventoryManager, List<InventoryApproval> pending)> ApprovalInboxAsync(
			CrossBuy.Models.Platform.BusinessContext context, int approverEmployeeId,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(context);

			var empty = new List<InventoryApproval>();

			// No company and no identity both mean "cannot authorize", so both answer with nothing.
			if (context.CompanyId <= 0 || approverEmployeeId <= 0) return (false, empty);

			// The InventoryManager gate, scoped to the RESOLVED company. An identical role row held in another
			// company must not open this company's queue — that is the widening being removed.
			bool isInventoryManager = await _db.InventoryUserRoles.AsNoTracking()
				.AnyAsync(r => r.CompanyID == context.CompanyId
					&& r.EmployeeId == approverEmployeeId
					&& r.Role == "InventoryManager", cancellationToken);
			if (!isInventoryManager) return (false, empty);

			// Separation of duties, preserved exactly as the inbox already applied it: a manager never reviews
			// their own submission. Kept as the same `!=` comparison, so a row with no recorded requester
			// continues to appear rather than silently vanishing.
			var pending = await _db.InventoryApprovals.AsNoTracking()
				.Where(a => a.CompanyID == context.CompanyId
					&& a.Status == "Pending"
					&& a.RequestedByEmployeeId != approverEmployeeId)
				.OrderByDescending(a => a.ID)
				.ToListAsync(cancellationToken);

			return (true, pending);
		}

		public async Task<List<InventoryApproval>> RecentAsync(int take = 50)
		{
			int companyId = await CurrentCompanyIdAsync();
			return await _db.InventoryApprovals.AsNoTracking().Where(a => a.CompanyID == companyId).OrderByDescending(a => a.ID).Take(take).ToListAsync();
		}

		// ONE TRANSACTION FOR THE CONSEQUENCE AND THE DECISION.
		//
		// WHAT WAS WRONG. The authoritative document was created and COMMITTED by the module service,
		// and only then was the approval marked Approved by a SEPARATE SaveChanges. Anything that failed
		// in between - a concurrency conflict, a lost connection, a constraint - left a purchase order or
		// a stock movement permanently in the books with the approval still Pending. The approver retries,
		// the payload is replayed, and a SECOND document is created for one decision. The books gain a
		// document nobody approved twice over, and no error was ever shown.
		//
		// WHY BeginOrJoin WORKS HERE, checked rather than assumed. CrossDbContext and every service below
		// are registered Scoped, so all four delegated operations run on THIS DbContext instance.
		// StockService.TransferAsync, PostCountAsync and WriteOffAsync each open ScopedTx.BeginOrJoinAsync,
		// which begins only when there is no ambient transaction - with one open here they JOIN, and a
		// joiner neither commits nor rolls back. ProcurementService.CreatePurchaseOrderAsync opens no
		// transaction at all, so its SaveChanges enlists in whatever is current. Nothing in those services
		// had to change, which matters: their business rules are the Inventory owner's, not this file's.
		//
		// THE ORDER IS NOT NEGOTIABLE EITHER WAY. Marking Approved and committing FIRST would only invert
		// the defect - an approval with no document. Both halves commit together or neither does.
		//
		// IDEMPOTENCY IS NOT THE FIX AND IS NOT PRETENDED TO BE. The Pending-only guard still refuses a
		// second approve, but it can only refuse what was recorded; a transaction is what makes sure the
		// record and the document agree about whether the first one happened.
		public async Task<(bool ok, string? error)> ApproveAsync(int id, int approverEmp, string? note)
		{
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

			// Scoped to the approver's company so an id from another company reads as "not found" — the same
			// answer as a genuinely missing row, so an approver cannot probe another company's queue.
			int companyId = await CurrentCompanyIdAsync();

			// AUTHORIZATION FIRST, before the row is even read. Asking after would let an unauthorized caller
			// tell an existing approval from a missing one by the error text, which turns a refusal into a
			// queue probe.
			if (!await CanDecideAsync(companyId)) return (false, "ليست لديك صلاحية لاعتماد المستندات");

			var ap = await _db.InventoryApprovals.FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == companyId);
			if (ap == null) return (false, "Order not found");
			if (ap.Status != "Pending") return (false, "The request has already been processed");
			if (ap.RequestedByEmployeeId == approverEmp) return (false, "Segregation of duties: the person who created the document cannot approve it");  // SoD

			// execute the held operation
			string? resultNo = null; string? err = null;
			var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
			try
			{
				switch (ap.DocType)
				{
					case "PurchaseOrder":
						{
							var p = JsonSerializer.Deserialize<PoApprovalPayload>(ap.PayloadJson ?? "{}", opts)!;
							var (ok, e, po) = await _proc.CreatePurchaseOrderAsync(ap.CompanyID, p.VendorId, p.WarehouseId, p.OrderDate, p.ExpectedDate, p.Notes, p.Lines, null);
							if (!ok) { err = e; } else resultNo = po?.OrderNo;
							break;
						}
					case "StockTransfer":
						{
							var p = JsonSerializer.Deserialize<TransferApprovalPayload>(ap.PayloadJson ?? "{}", opts)!;
							var (ok, e, tr) = await _stock.TransferAsync(ap.CompanyID, p.FromWarehouseId, p.ToWarehouseId, p.Date, p.Notes, p.Lines, null);
							if (!ok) { err = e; } else resultNo = tr?.TransferNo;
							break;
						}
					case "StockCount":
						{
							var p = JsonSerializer.Deserialize<CountApprovalPayload>(ap.PayloadJson ?? "{}", opts)!;
							var (ok, e, cnt) = await _stock.PostCountAsync(ap.CompanyID, p.WarehouseId, p.CountDate, p.Notes, p.Lines, null);
							if (!ok) { err = e; } else resultNo = cnt?.CountNo;
							break;
						}
					case "WriteOff":
						{
							var p = JsonSerializer.Deserialize<WriteOffApprovalPayload>(ap.PayloadJson ?? "{}", opts)!;
							var (ok, e, no, _, _) = await _stock.WriteOffAsync(ap.CompanyID, p.WarehouseId, p.WriteOffDate, p.Reason, p.Notes, p.Lines, null);
							if (!ok) { err = e; } else resultNo = no;
							break;
						}
					default: err = "Unknown document type"; break;
				}
			}
			catch (Exception ex) { err = ex.Message; }
			// A delegated operation may have written rows before it failed. Rolling back is what makes
			// "the document was not created" true rather than merely reported.
			if (err != null)
			{
				await tx.RollbackAsync();
				return (false, "Could not execute the approved document: " + err);
			}

			ap.Status = "Approved"; ap.DecidedByEmployeeId = approverEmp; ap.DecidedAt = DateTime.UtcNow; ap.DecisionNote = note; ap.ResultDocNo = resultNo;

			// The qualification seam for the window this fix exists to close: the document is written and the
			// decision is not yet persisted. Forcing that failure any other way means breaking the database.
			try
			{
				if (BeforeDecisionSaveHookForTests is not null) await BeforeDecisionSaveHookForTests();
				await _db.SaveChangesAsync();
				// The decision is formed and the document exists; the fact joins them inside the SAME transaction,
				// so the consequence, the Approved state and the Approved event share one fate.
				await EmitAsync(ap, CrossBuy.BL.Platform.InventoryApprovalEvents.Approved);
				await tx.CommitAsync();
			}
			catch (Exception ex)
			{
				// THE DEFECT, NOW A ROLLBACK. Everything the delegated service wrote goes with it, so the
				// approval stays Pending against a world in which the document was never created - and the
				// retry that follows produces exactly one, not a second.
				await tx.RollbackAsync();
				return (false, "تعذّر حفظ قرار الاعتماد: " + ex.Message);
			}

			// AFTER THE COMMIT, DELIBERATELY. A notification is a statement that something happened; sending
			// it from inside a transaction that can still roll back makes it a statement that something might
			// have. The wording, the localized document-type name and ResultDocNo are unchanged - the
			// Inventory owner owns what this says.
			if (ap.RequestedByEmployeeId != null)
				await _notify.NotifyAsync(ap.RequestedByEmployeeId.Value, "تم اعتماد مستندك", "Your document was approved", $"{DocTypeName(ap.DocType)} {resultNo}", $"{ap.DocType} {resultNo}", "InventoryApproval", ap.ID);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RejectAsync(int id, int approverEmp, string? note)
		{
			// A TRANSACTION, matching ApproveAsync. Rejecting writes one row today, so a transaction looks
			// like ceremony - it is a prerequisite. A canonical Rejected Business Event must be recorded
			// INSIDE the transaction that carries the decision (ADR-001), and a bare SaveChanges leaves
			// nowhere to put it. Adding the boundary now means the event increment adds an event and not a
			// transaction model. Rollback leaves the approval Pending, which is the property that matters.
			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

			// Same company scoping as ApproveAsync - another company id must read as not found.
			int companyId = await CurrentCompanyIdAsync();

			// The SAME authority approving needs. An actor who may not approve may not reject either: both
			// are the terminal decision on the document, and letting the weaker one through would let an
			// unauthorized actor destroy a pending purchase order instead of creating one.
			if (!await CanDecideAsync(companyId)) return (false, "ليست لديك صلاحية لاعتماد المستندات");

			var ap = await _db.InventoryApprovals.FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == companyId);
			if (ap == null) return (false, "Order not found");
			if (ap.Status != "Pending") return (false, "The request has already been processed");
			if (ap.RequestedByEmployeeId == approverEmp) return (false, "Segregation of duties: the person who created the document cannot reject it");
			ap.Status = "Rejected"; ap.DecidedByEmployeeId = approverEmp; ap.DecidedAt = DateTime.UtcNow; ap.DecisionNote = note;

			// The seam the approve path already carries, so the rejection window can be reproduced
			// deterministically instead of by breaking a database.
			if (BeforeDecisionSaveHookForTests != null) await BeforeDecisionSaveHookForTests();
			await _db.SaveChangesAsync();
			// The transaction above exists for this line. Rejected is a terminal decision like Approved, and
			// the review work it ends is the same work - so it is recorded the same way, in the same place.
			await EmitAsync(ap, CrossBuy.BL.Platform.InventoryApprovalEvents.Rejected);
			await tx.CommitAsync();

			// AFTER the commit. A notification for a rejection that rolled back is a message about something
			// that did not happen, and it cannot be unsent.
			if (ap.RequestedByEmployeeId != null)
				await _notify.NotifyAsync(ap.RequestedByEmployeeId.Value, "تم رفض مستندك", "Your document was rejected", note ?? "", note ?? "", "InventoryApproval", ap.ID);
			return (true, null);
		}

		// THE WIRE PAYLOAD. Versioned, and deliberately identifiers-and-amounts only.
		//
		// PayloadJson IS NOT HERE, on purpose. It is the deferred-intent blob - a whole purchase order or a
		// stock count with every line - and it is an implementation detail of replay, not a fact about the
		// approval. A consumer that needs the document resolves it through its own authorization rather than
		// reading it out of an event it was handed.
		//
		// NO COMPANY FIELD. The envelope carries it, and a second copy is a second answer waiting to disagree.
		//
		// NO LOCALIZED TEXT. DocType travels as the stable code the domain stores; whoever renders it resolves
		// the Arabic and English names, because a translation frozen into the permanent log ages badly and
		// cannot follow the reader's language.
		public sealed class InventoryApprovalEventPayload
		{
			public const int Version = 1;

			public int ApprovalId { get; init; }
			public string DocType { get; init; } = "";
			public string Status { get; init; } = "";
			public decimal Amount { get; init; }
			public int? RequestedByEmployeeId { get; init; }

			// Null on Requested; the decider on either terminal fact.
			public int? DecidedByEmployeeId { get; init; }

			// Present only once approval replayed the payload into a real document.
			public string? ResultDocNo { get; init; }
		}

		private static string DocTypeName(string t) => t switch
		{
			"PurchaseOrder" => "Purchase order", "StockTransfer" => "Inter-branch transfer", "StockCount" => "Stock-count adjustment", "WriteOff" => "Stock write-off", _ => t
		};
	}
}
