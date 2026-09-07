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
		private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _context;

		public InventoryApprovalService(CrossDbContext db, IStockService stock, IProcurementService proc,
			INotificationService notify, CrossBuy.BL.Platform.IBusinessContextAccessor context)
		{ _db = db; _stock = stock; _proc = proc; _notify = notify; _context = context; }

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

		public async Task<int> SubmitAsync(string docType, decimal amount, object payload, int? requestedBy)
		{
			int companyId = await CurrentCompanyIdAsync();
			var ap = new InventoryApproval
			{
				CompanyID = companyId, DocType = docType, Amount = Math.Round(amount, 2), PayloadJson = JsonSerializer.Serialize(payload),
				Status = "Pending", RequestedByEmployeeId = requestedBy, RequestedAt = DateTime.UtcNow
			};
			_db.InventoryApprovals.Add(ap);
			await _db.SaveChangesAsync();

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

		public async Task<(bool ok, string? error)> ApproveAsync(int id, int approverEmp, string? note)
		{
			// Scoped to the approver's company so an id from another company reads as "not found" — the same
			// answer as a genuinely missing row, so an approver cannot probe another company's queue.
			int companyId = await CurrentCompanyIdAsync();
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
			if (err != null) return (false, "Could not execute the approved document: " + err);

			ap.Status = "Approved"; ap.DecidedByEmployeeId = approverEmp; ap.DecidedAt = DateTime.UtcNow; ap.DecisionNote = note; ap.ResultDocNo = resultNo;
			await _db.SaveChangesAsync();
			if (ap.RequestedByEmployeeId != null)
				await _notify.NotifyAsync(ap.RequestedByEmployeeId.Value, "تم اعتماد مستندك", "Your document was approved", $"{DocTypeName(ap.DocType)} {resultNo}", $"{ap.DocType} {resultNo}", "InventoryApproval", ap.ID);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RejectAsync(int id, int approverEmp, string? note)
		{
			// Same company scoping as ApproveAsync — another company's id must read as "not found".
			int companyId = await CurrentCompanyIdAsync();
			var ap = await _db.InventoryApprovals.FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == companyId);
			if (ap == null) return (false, "Order not found");
			if (ap.Status != "Pending") return (false, "The request has already been processed");
			if (ap.RequestedByEmployeeId == approverEmp) return (false, "Segregation of duties: the person who created the document cannot reject it");
			ap.Status = "Rejected"; ap.DecidedByEmployeeId = approverEmp; ap.DecidedAt = DateTime.UtcNow; ap.DecisionNote = note;
			await _db.SaveChangesAsync();
			if (ap.RequestedByEmployeeId != null)
				await _notify.NotifyAsync(ap.RequestedByEmployeeId.Value, "تم رفض مستندك", "Your document was rejected", note ?? "", note ?? "", "InventoryApproval", ap.ID);
			return (true, null);
		}

		private static string DocTypeName(string t) => t switch
		{
			"PurchaseOrder" => "Purchase order", "StockTransfer" => "Inter-branch transfer", "StockCount" => "Stock-count adjustment", "WriteOff" => "Stock write-off", _ => t
		};
	}
}
