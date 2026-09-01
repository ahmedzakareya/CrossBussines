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
		// THE COMPANY IS RESOLVED, NOT ASSUMED.
		//
		// This class carried a hardcoded company constant across twelve call sites, five of which
		// WRITE: the approval row, and then - on approval - a purchase order, a stock transfer, a count
		// and a write-off. A manager in company 41 who approved a write-off created it in COMPANY 1's
		// stock and ledger. The service is registered in Program.cs and injected into
		// InventoryController, so that was a production path and not a fixture.
		//
		// Half of the class had already moved: ApprovalInboxAsync takes a BusinessContext and uses
		// context.CompanyId. This finishes the migration for the other half.
		//
		// NO PUBLIC SIGNATURE CHANGES. InventoryController is SHF-26, whose rule admits conversation
		// endpoints only and forbids approval behaviour changing under it, so the company is resolved
		// INSIDE the service through the platform's canonical resolver and no caller moves.
		private readonly CrossDbContext _db;
		private readonly IStockService _stock;
		private readonly IProcurementService _proc;
		private readonly INotificationService _notify;
		private readonly CrossBuy.BL.Platform.IRequestCompanyResolver _company;
		public InventoryApprovalService(CrossDbContext db, IStockService stock, IProcurementService proc, INotificationService notify, CrossBuy.BL.Platform.IRequestCompanyResolver company)
		{ _db = db; _stock = stock; _proc = proc; _notify = notify; _company = company; }

		/// The resolved company, or 0. ZERO IS NOT A COMPANY: no row carries it, so a caller that fails
		/// to check still reads and writes nothing rather than falling into somebody else's tenant. Every
		/// caller below checks anyway - reads return empty, writes refuse - because an unresolved company
		/// is an error and must look like one.
		private async Task<int> CompanyAsync()
		{
			var scope = await _company.ResolveAsync();
			return scope.Ok ? scope.CompanyId : 0;
		}

		public async Task<bool> RequiresApprovalAsync(decimal amount)
		{
			int company = await CompanyAsync();
			// No resolved company means no threshold to read. Answering "no approval needed" here would
			// wave the document straight through, so an unresolved caller is told approval IS required and
			// the submit below then refuses - the safe order of the two answers.
			if (company <= 0) return true;
			var th = await _db.InventorySettings.AsNoTracking().Where(s => s.CompanyID == company).Select(s => s.ApprovalThreshold).FirstOrDefaultAsync();
			return th > 0 && Math.Abs(amount) >= th;
		}

		public async Task<int> SubmitAsync(string docType, decimal amount, object payload, int? requestedBy)
		{
			int company = await CompanyAsync();
			// A write. Refused outright rather than filed against a company nobody named.
			if (company <= 0) return 0;

			var ap = new InventoryApproval
			{
				CompanyID = company, DocType = docType, Amount = Math.Round(amount, 2), PayloadJson = JsonSerializer.Serialize(payload),
				Status = "Pending", RequestedByEmployeeId = requestedBy, RequestedAt = DateTime.UtcNow
			};
			_db.InventoryApprovals.Add(ap);
			await _db.SaveChangesAsync();

			// notify all inventory managers except the requester (SoD)
			int reqId = requestedBy ?? -1;
			var managers = await _db.InventoryUserRoles.AsNoTracking()
				.Where(r => r.CompanyID == company && r.Role == "InventoryManager" && r.EmployeeId != reqId)
				.Select(r => r.EmployeeId).Distinct().ToListAsync();
			foreach (var m in managers)
				await _notify.NotifyAsync(m, "طلب اعتماد مخزون", "Inventory approval request",
					$"مستند {DocTypeName(docType)} بقيمة {ap.Amount:N2} بانتظار اعتمادك", $"{docType} of {ap.Amount:N2} awaits your approval", "InventoryApproval", ap.ID);
			return ap.ID;
		}

		public async Task<List<InventoryApproval>> PendingAsync()
		{
			int company = await CompanyAsync();
			if (company <= 0) return new List<InventoryApproval>();
			return await _db.InventoryApprovals.AsNoTracking()
				.Where(a => a.CompanyID == company && a.Status == "Pending").OrderBy(a => a.ID).ToListAsync();
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
			int RecentCompany = await CompanyAsync();
			if (RecentCompany <= 0) return new List<InventoryApproval>();
			return await 			_db.InventoryApprovals.AsNoTracking().Where(a => a.CompanyID == RecentCompany).OrderByDescending(a => a.ID).Take(take).ToListAsync();
		}

		public async Task<(bool ok, string? error)> ApproveAsync(int id, int approverEmp, string? note)
		{
			int company = await CompanyAsync();
			// The approval is loaded BY ID AND COMPANY. An id from another tenant is simply not found -
			// the same answer as an id that does not exist, so the endpoint cannot be used to probe.
			if (company <= 0) return (false, "تعذّر تحديد الشركة");
			var ap = await _db.InventoryApprovals.FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == company);
			if (ap == null) return (false, "الطلب غير موجود");
			if (ap.Status != "Pending") return (false, "تمت معالجة الطلب من قبل");
			if (ap.RequestedByEmployeeId == approverEmp) return (false, "فصل المهام: لا يمكن لمنشئ المستند اعتماده");  // SoD

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
							var (ok, e, po) = await _proc.CreatePurchaseOrderAsync(company, p.VendorId, p.WarehouseId, p.OrderDate, p.ExpectedDate, p.Notes, p.Lines, null);
							if (!ok) { err = e; } else resultNo = po?.OrderNo;
							break;
						}
					case "StockTransfer":
						{
							var p = JsonSerializer.Deserialize<TransferApprovalPayload>(ap.PayloadJson ?? "{}", opts)!;
							var (ok, e, tr) = await _stock.TransferAsync(company, p.FromWarehouseId, p.ToWarehouseId, p.Date, p.Notes, p.Lines, null);
							if (!ok) { err = e; } else resultNo = tr?.TransferNo;
							break;
						}
					case "StockCount":
						{
							var p = JsonSerializer.Deserialize<CountApprovalPayload>(ap.PayloadJson ?? "{}", opts)!;
							var (ok, e, cnt) = await _stock.PostCountAsync(company, p.WarehouseId, p.CountDate, p.Notes, p.Lines, null);
							if (!ok) { err = e; } else resultNo = cnt?.CountNo;
							break;
						}
					case "WriteOff":
						{
							var p = JsonSerializer.Deserialize<WriteOffApprovalPayload>(ap.PayloadJson ?? "{}", opts)!;
							var (ok, e, no, _, _) = await _stock.WriteOffAsync(company, p.WarehouseId, p.WriteOffDate, p.Reason, p.Notes, p.Lines, null);
							if (!ok) { err = e; } else resultNo = no;
							break;
						}
					default: err = "نوع مستند غير معروف"; break;
				}
			}
			catch (Exception ex) { err = ex.Message; }
			if (err != null) return (false, "تعذّر تنفيذ المستند المعتمَد: " + err);

			ap.Status = "Approved"; ap.DecidedByEmployeeId = approverEmp; ap.DecidedAt = DateTime.UtcNow; ap.DecisionNote = note; ap.ResultDocNo = resultNo;
			await _db.SaveChangesAsync();
			if (ap.RequestedByEmployeeId != null)
				await _notify.NotifyAsync(ap.RequestedByEmployeeId.Value, "تم اعتماد مستندك", "Your document was approved", $"{DocTypeName(ap.DocType)} {resultNo}", $"{ap.DocType} {resultNo}", "InventoryApproval", ap.ID);
			return (true, null);
		}

		public async Task<(bool ok, string? error)> RejectAsync(int id, int approverEmp, string? note)
		{
			int company = await CompanyAsync();
			// The approval is loaded BY ID AND COMPANY. An id from another tenant is simply not found -
			// the same answer as an id that does not exist, so the endpoint cannot be used to probe.
			if (company <= 0) return (false, "تعذّر تحديد الشركة");
			var ap = await _db.InventoryApprovals.FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == company);
			if (ap == null) return (false, "الطلب غير موجود");
			if (ap.Status != "Pending") return (false, "تمت معالجة الطلب من قبل");
			if (ap.RequestedByEmployeeId == approverEmp) return (false, "فصل المهام: لا يمكن لمنشئ المستند رفضه");
			ap.Status = "Rejected"; ap.DecidedByEmployeeId = approverEmp; ap.DecidedAt = DateTime.UtcNow; ap.DecisionNote = note;
			await _db.SaveChangesAsync();
			if (ap.RequestedByEmployeeId != null)
				await _notify.NotifyAsync(ap.RequestedByEmployeeId.Value, "تم رفض مستندك", "Your document was rejected", note ?? "", note ?? "", "InventoryApproval", ap.ID);
			return (true, null);
		}

		private static string DocTypeName(string t) => t switch
		{
			"PurchaseOrder" => "أمر شراء", "StockTransfer" => "تحويل بين الفروع", "StockCount" => "تسوية جرد", "WriteOff" => "إعدام مخزون", _ => t
		};
	}
}
