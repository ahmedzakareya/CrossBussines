using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Projects & Contracting — P6-د: variation orders. OPERATIONAL/estimate — writes NO GL, no new writer.
	// Approve applies the VO to BOQ (insert New items tagged VariationOrderId / revise Adjust items in place with old
	// snapshot). Revised contract value = original ContractValue + Σ approved VO values (original stays immutable).
	public class VoLineInput
	{
		public string Kind { get; set; } = "New";       // New | Adjust
		public int? BoqItemId { get; set; }
		public string? Code { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }
		public string? Unit { get; set; }
		public decimal Quantity { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal? MaterialCost { get; set; }
		public decimal? LaborCost { get; set; }
		public decimal? SubcontractCost { get; set; }
		public decimal? EquipmentCost { get; set; }
	}

	public class RevisedContract
	{
		public decimal Original { get; set; }
		public decimal ApprovedChanges { get; set; }
		public decimal Revised => Math.Round(Original + ApprovedChanges, 2);
	}

	public interface IVariationOrderService
	{
		Task<List<VariationOrder>> GetForProjectAsync(int companyId, int projectId);
		Task<VariationOrder?> GetAsync(int companyId, int id);
		Task<RevisedContract> RevisedContractValueAsync(int companyId, int projectId);
		Task<(bool ok, string? error, int id)> SaveDraftAsync(int companyId, int projectId, int voId, string? description, string? descriptionEn, string? reason, List<VoLineInput> lines, int? userId);
		Task<(bool ok, string? error)> ApproveAsync(int companyId, int id, int? userId);
		Task<(bool ok, string? error)> DeleteAsync(int companyId, int id);
	}

	public class VariationOrderService : IVariationOrderService
	{
		private readonly CrossDbContext _db;
		public VariationOrderService(CrossDbContext db) { _db = db; }
		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		public Task<List<VariationOrder>> GetForProjectAsync(int companyId, int projectId) =>
			_db.VariationOrders.AsNoTracking().Where(v => v.CompanyID == companyId && v.ProjectId == projectId).OrderBy(v => v.VoNo).ToListAsync();

		public Task<VariationOrder?> GetAsync(int companyId, int id) =>
			_db.VariationOrders.AsNoTracking().Include(v => v.Lines).FirstOrDefaultAsync(v => v.ID == id && v.CompanyID == companyId);

		public async Task<RevisedContract> RevisedContractValueAsync(int companyId, int projectId)
		{
			var orig = await _db.Projects.AsNoTracking().Where(p => p.ID == projectId && p.CompanyID == companyId).Select(p => p.ContractValue).FirstOrDefaultAsync() ?? 0m;
			var changes = await _db.VariationOrders.AsNoTracking().Where(v => v.CompanyID == companyId && v.ProjectId == projectId && v.Status == "Approved").SumAsync(v => (decimal?)v.Value) ?? 0m;
			return new RevisedContract { Original = R(orig), ApprovedChanges = R(changes) };
		}

		public async Task<(bool ok, string? error, int id)> SaveDraftAsync(int companyId, int projectId, int voId, string? description, string? descriptionEn, string? reason, List<VoLineInput> lines, int? userId)
		{
			if (!await _db.Projects.AnyAsync(p => p.ID == projectId && p.CompanyID == companyId)) return (false, "المشروع غير موجود", 0);
			var clean = (lines ?? new()).Where(l => (l.Kind == "Adjust" && l.BoqItemId > 0) || (l.Kind == "New" && !string.IsNullOrWhiteSpace(l.Description))).ToList();
			if (clean.Count == 0) return (false, "أضف سطرًا واحدًا على الأقل (بند جديد أو تعديل)", 0);

			VariationOrder vo;
			if (voId > 0)
			{
				vo = await _db.VariationOrders.Include(v => v.Lines).FirstOrDefaultAsync(v => v.ID == voId && v.CompanyID == companyId) ?? throw new InvalidOperationException("أمر التغيير غير موجود");
				if (vo.Status != "Draft") return (false, "لا يمكن تعديل أمر تغيير معتمد", 0);
				_db.VariationOrderLines.RemoveRange(vo.Lines); vo.Lines.Clear();
			}
			else
			{
				int nextNo = (await _db.VariationOrders.Where(v => v.CompanyID == companyId && v.ProjectId == projectId).Select(v => (int?)v.VoNo).MaxAsync() ?? 0) + 1;
				vo = new VariationOrder { CompanyID = companyId, ProjectId = projectId, VoNo = nextNo, Status = "Draft", CreatedAt = DateTime.UtcNow, CreatedBy = userId };
				_db.VariationOrders.Add(vo);
			}
			vo.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
			vo.DescriptionEn = string.IsNullOrWhiteSpace(descriptionEn) ? null : descriptionEn.Trim();
			vo.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

			decimal total = 0m;
			foreach (var l in clean)
			{
				decimal? oldQ = null, oldP = null;
				if (l.Kind == "Adjust" && l.BoqItemId > 0)
				{
					var it = await _db.BoqItems.AsNoTracking().FirstOrDefaultAsync(b => b.ID == l.BoqItemId!.Value && b.ProjectId == projectId && b.CompanyID == companyId);
					if (it == null) return (false, "بند التعديل غير موجود", 0);
					oldQ = it.Quantity; oldP = it.UnitPrice;
				}
				var line = new VariationOrderLine
				{
					Kind = l.Kind == "Adjust" ? "Adjust" : "New", BoqItemId = l.Kind == "Adjust" ? l.BoqItemId : null,
					Code = l.Code, Description = l.Description, DescriptionEn = l.DescriptionEn, Unit = l.Unit,
					Quantity = l.Quantity, UnitPrice = l.UnitPrice,
					MaterialCost = l.MaterialCost, LaborCost = l.LaborCost, SubcontractCost = l.SubcontractCost, EquipmentCost = l.EquipmentCost,
					OldQuantity = oldQ, OldUnitPrice = oldP
				};
				vo.Lines.Add(line);
				total += line.ValueChange;
			}
			vo.Value = R(total);
			await _db.SaveChangesAsync();
			return (true, null, vo.ID);
		}

		// Apply the VO to BOQ. NO journal entry (operational scope change; money follows via later billings/issues).
		public async Task<(bool ok, string? error)> ApproveAsync(int companyId, int id, int? userId)
		{
			var vo = await _db.VariationOrders.Include(v => v.Lines).FirstOrDefaultAsync(v => v.ID == id && v.CompanyID == companyId);
			if (vo == null) return (false, "أمر التغيير غير موجود");
			if (vo.Status != "Draft") return (false, "أمر التغيير معتمد بالفعل");
			if (vo.Lines.Count == 0) return (false, "لا سطور في أمر التغيير");

			int sort = ((await _db.BoqItems.Where(b => b.CompanyID == companyId && b.ProjectId == vo.ProjectId).Select(b => (int?)b.SortOrder).MaxAsync()) ?? 0) + 1;
			decimal total = 0m;
			foreach (var l in vo.Lines)
			{
				if (l.Kind == "Adjust" && l.BoqItemId > 0)
				{
					var it = await _db.BoqItems.FirstOrDefaultAsync(b => b.ID == l.BoqItemId!.Value && b.ProjectId == vo.ProjectId && b.CompanyID == companyId);
					if (it == null) return (false, $"بند التعديل #{l.BoqItemId} غير موجود");
					l.OldQuantity = it.Quantity; l.OldUnitPrice = it.UnitPrice;   // re-snapshot the actual current values
					it.Quantity = l.Quantity; it.UnitPrice = l.UnitPrice;
					if (l.MaterialCost.HasValue) it.MaterialCost = l.MaterialCost;
					if (l.LaborCost.HasValue) it.LaborCost = l.LaborCost;
					if (l.SubcontractCost.HasValue) it.SubcontractCost = l.SubcontractCost;
					if (l.EquipmentCost.HasValue) it.EquipmentCost = l.EquipmentCost;
				}
				else
				{
					_db.BoqItems.Add(new BoqItem
					{
						CompanyID = companyId, ProjectId = vo.ProjectId, VariationOrderId = vo.ID, SortOrder = sort++,
						Code = l.Code, Description = l.Description ?? "", DescriptionEn = l.DescriptionEn, Unit = l.Unit,
						Quantity = l.Quantity, UnitPrice = l.UnitPrice,
						MaterialCost = l.MaterialCost, LaborCost = l.LaborCost, SubcontractCost = l.SubcontractCost, EquipmentCost = l.EquipmentCost,
						CreatedAt = DateTime.UtcNow
					});
				}
				total += l.ValueChange;
			}
			vo.Value = R(total);
			vo.Status = "Approved"; vo.ApprovedAt = DateTime.UtcNow; vo.ApprovedBy = userId;
			await _db.SaveChangesAsync();   // NO journal entry
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
		{
			var vo = await _db.VariationOrders.Include(v => v.Lines).FirstOrDefaultAsync(v => v.ID == id && v.CompanyID == companyId);
			if (vo == null) return (false, "أمر التغيير غير موجود");
			if (vo.Status != "Draft") return (false, "لا يمكن حذف أمر تغيير معتمد");
			_db.VariationOrderLines.RemoveRange(vo.Lines);
			_db.VariationOrders.Remove(vo);
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
