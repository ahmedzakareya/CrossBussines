using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Projects & Contracting — P5-أ: project material issue. Posts actual material cost via StockService ONLY
	// (Cr inventory unchanged → stock_gl intact), Dr 510104 project execution cost, tagged ProjectId. No new writer.
	public class MaterialLineInput
	{
		public int ItemId { get; set; }
		public decimal Qty { get; set; }
		public int? BoqItemId { get; set; }
	}

	// public DTO for the material-issue screen's warehouse-balance rows (avoids dynamic/anonymous-type binding across the Views assembly)
	public class ProjectStockRow
	{
		public int ItemId { get; set; }
		public string ItemCode { get; set; } = "";
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public decimal QtyOnHand { get; set; }
	}

	public interface IProjectMaterialIssueService
	{
		Task<List<ProjectMaterialIssue>> GetIssuesAsync(int companyId, int projectId);
		Task<ProjectMaterialIssue?> GetAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveDraftAsync(int companyId, int projectId, int issueId, DateTime date, int warehouseId, string? note, List<MaterialLineInput> rows, int? userId);
		Task<(bool ok, string? error)> PostAsync(int companyId, int id, int? userId);
		Task<(bool ok, string? error)> DeleteAsync(int companyId, int id);
	}

	public class ProjectMaterialIssueService : IProjectMaterialIssueService
	{
		public const string ProjectCostAccountCode = "510104";   // تكلفة تنفيذ مشاريع (EXP, under 51)
		private readonly CrossDbContext _db;
		private readonly IStockService _stock;
		public ProjectMaterialIssueService(CrossDbContext db, IStockService stock) { _db = db; _stock = stock; }

		public Task<List<ProjectMaterialIssue>> GetIssuesAsync(int companyId, int projectId) =>
			_db.ProjectMaterialIssues.AsNoTracking().Include(x => x.Lines)
				.Where(x => x.CompanyID == companyId && x.ProjectId == projectId)
				.OrderByDescending(x => x.IssueNo).ToListAsync();

		public Task<ProjectMaterialIssue?> GetAsync(int companyId, int id) =>
			_db.ProjectMaterialIssues.AsNoTracking().Include(x => x.Lines)
				.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);

		public async Task<(bool ok, string? error, int id)> SaveDraftAsync(int companyId, int projectId, int issueId, DateTime date, int warehouseId, string? note, List<MaterialLineInput> rows, int? userId)
		{
			var prj = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			if (prj == null) return (false, "المشروع غير موجود", 0);
			if (warehouseId <= 0 || !await _db.Warehouses.AnyAsync(w => w.ID == warehouseId && w.CompanyID == companyId)) return (false, "اختر مخزنًا صحيحًا", 0);
			var clean = (rows ?? new()).Where(r => r.ItemId > 0 && r.Qty > 0).ToList();
			if (clean.Count == 0) return (false, "أضف صنفًا واحدًا على الأقل بكمية أكبر من صفر", 0);

			ProjectMaterialIssue hdr;
			if (issueId > 0)
			{
				hdr = await _db.ProjectMaterialIssues.Include(x => x.Lines).FirstOrDefaultAsync(x => x.ID == issueId && x.CompanyID == companyId) ?? throw new InvalidOperationException("الأذن غير موجود");
				if (hdr.Status != "Draft") return (false, "لا يمكن تعديل أذن مرحّل", 0);
				_db.ProjectMaterialIssueLines.RemoveRange(hdr.Lines); hdr.Lines.Clear();
			}
			else
			{
				int nextNo = (await _db.ProjectMaterialIssues.Where(x => x.CompanyID == companyId && x.ProjectId == projectId).Select(x => (int?)x.IssueNo).MaxAsync() ?? 0) + 1;
				hdr = new ProjectMaterialIssue { CompanyID = companyId, ProjectId = projectId, IssueNo = nextNo, Status = "Draft", CreatedAt = DateTime.UtcNow, CreatedBy = userId };
				_db.ProjectMaterialIssues.Add(hdr);
			}
			hdr.IssueDate = date; hdr.WarehouseId = warehouseId; hdr.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			foreach (var r in clean)
				hdr.Lines.Add(new ProjectMaterialIssueLine { ItemId = r.ItemId, Qty = r.Qty, BoqItemId = r.BoqItemId });
			await _db.SaveChangesAsync();
			return (true, null, hdr.ID);
		}

		public async Task<(bool ok, string? error)> PostAsync(int companyId, int id, int? userId)
		{
			var hdr = await _db.ProjectMaterialIssues.Include(x => x.Lines).FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (hdr == null) return (false, "الأذن غير موجود");
			if (hdr.Status == "Posted") return (false, "الأذن مرحّل بالفعل");   // no double posting
			if (hdr.Lines.Count == 0) return (false, "لا أصناف في الأذن");
			var costAcc = await _db.Accounts.Where(a => a.CompanyID == companyId && a.Code == ProjectCostAccountCode).Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (costAcc == null) return (false, $"حساب تكلفة التنفيذ ({ProjectCostAccountCode}) غير مُهيّأ");

			// StockService.PostMovementAsync manages its own transaction per movement — do NOT open an outer one.
			// Pre-validate availability so we don't post a partial voucher (functional-currency, non-negative warehouses).
			foreach (var l in hdr.Lines)
			{
				var (whQty, _, _) = await _stock.GetBalanceAsync(companyId, l.ItemId, hdr.WarehouseId);
				var allowNeg = await _db.Warehouses.AsNoTracking().Where(w => w.ID == hdr.WarehouseId).Select(w => w.AllowNegativeStock).FirstOrDefaultAsync();
				if (!allowNeg && whQty < l.Qty) return (false, $"الرصيد غير كافٍ للصنف #{l.ItemId}: المتاح {whQty:0.##}، المطلوب {l.Qty:0.##}");
			}
			foreach (var l in hdr.Lines)
			{
				var (mok, merr, mv) = await _stock.PostMovementAsync(companyId, new MovementRequest
				{
					Date = hdr.IssueDate, ItemId = l.ItemId, WarehouseId = hdr.WarehouseId, Direction = -1, Qty = l.Qty,
					SourceType = "ProjectIssue", SourceId = hdr.ID, SourceLineId = l.ID,
					ProjectId = hdr.ProjectId, CounterAccountOverride = costAcc.Value, PostToGl = true,
					Notes = $"صرف مواد للمشروع — أذن {hdr.IssueNo}"
				}, userId?.ToString());
				if (!mok || mv == null) return (false, $"تعذّر صرف الصنف #{l.ItemId}: {merr}");
				l.UnitCost = mv.UnitCost; l.TotalCost = mv.TotalCost; l.StockMovementId = mv.ID;
			}
			hdr.Status = "Posted"; hdr.PostedAt = DateTime.UtcNow; hdr.PostedBy = userId;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
		{
			var hdr = await _db.ProjectMaterialIssues.Include(x => x.Lines).FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (hdr == null) return (false, "الأذن غير موجود");
			if (hdr.Status == "Posted") return (false, "لا يمكن حذف أذن مرحّل");
			_db.ProjectMaterialIssueLines.RemoveRange(hdr.Lines);
			_db.ProjectMaterialIssues.Remove(hdr);
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
