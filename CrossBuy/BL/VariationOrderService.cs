using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.BL.Construction;
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
		private readonly ICommercialRevisionService _revisions;
		public VariationOrderService(CrossDbContext db, ICommercialRevisionService revisions) { _db = db; _revisions = revisions; }
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
			if (!await _db.Projects.AnyAsync(p => p.ID == projectId && p.CompanyID == companyId)) return (false, "Project not found", 0);
			var clean = (lines ?? new()).Where(l => (l.Kind == "Adjust" && l.BoqItemId > 0) || (l.Kind == "New" && !string.IsNullOrWhiteSpace(l.Description))).ToList();
			if (clean.Count == 0) return (false, "Add at least one line (a new item or an amendment)", 0);

			VariationOrder vo;
			if (voId > 0)
			{
				vo = await _db.VariationOrders.Include(v => v.Lines).FirstOrDefaultAsync(v => v.ID == voId && v.CompanyID == companyId) ?? throw new InvalidOperationException("Variation order not found");
				if (vo.Status != "Draft") return (false, "An approved variation order cannot be edited", 0);
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
					if (it == null) return (false, "The amended line was not found", 0);
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
			if (vo == null) return (false, "Variation order not found");
			if (vo.Status != "Draft") return (false, "The variation order is already approved");
			if (vo.Lines.Count == 0) return (false, "The variation order has no lines");
			if (string.IsNullOrWhiteSpace(vo.Reason)) return (false, "A reason is required to approve a variation order");

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			var adjusted = new List<(VariationOrderLine Line, BoqItem Item)>();
			foreach (var line in vo.Lines.Where(l => l.Kind == "Adjust"))
			{
				if (line.BoqItemId is not > 0) return (false, "An amended line must reference an existing BOQ item");
				var item = await _db.BoqItems.FirstOrDefaultAsync(b => b.ID == line.BoqItemId && b.ProjectId == vo.ProjectId && b.CompanyID == companyId);
				if (item == null) return (false, $"The amended line #{line.BoqItemId} was not found");
				adjusted.Add((line, item));
			}

			int sort = ((await _db.BoqItems.Where(b => b.CompanyID == companyId && b.ProjectId == vo.ProjectId).Select(b => (int?)b.SortOrder).MaxAsync()) ?? 0) + 1;
			var addedItems = new List<BoqItem>();
			foreach (var line in vo.Lines.Where(l => l.Kind != "Adjust"))
			{
				var item = new BoqItem
				{
					CompanyID = companyId, ProjectId = vo.ProjectId, VariationOrderId = vo.ID, SortOrder = sort++,
					Code = line.Code, Description = line.Description ?? "", DescriptionEn = line.DescriptionEn, Unit = line.Unit,
					Quantity = line.Quantity, UnitPrice = line.UnitPrice,
					MaterialCost = line.MaterialCost, LaborCost = line.LaborCost, SubcontractCost = line.SubcontractCost, EquipmentCost = line.EquipmentCost,
					CreatedAt = DateTime.UtcNow
				};
				_db.BoqItems.Add(item);
				addedItems.Add(item);
			}

			// The revision service accepts only approved variation sources. This status change and all BOQ/revision
			// writes stay inside the same transaction, so no partially approved change can escape.
			vo.Status = "Approved"; vo.ApprovedAt = DateTime.UtcNow; vo.ApprovedBy = userId;
			await _db.SaveChangesAsync();

			var opened = await _revisions.OpenAsync(companyId, vo.ProjectId, null,
				CommercialRevisionSource.Variation, vo.ID, vo.ApprovedAt!.Value,
				userId);
			if (!opened.ok) return (false, opened.error);

			foreach (var (line, item) in adjusted)
			{
				line.OldQuantity = item.Quantity;
				line.OldUnitPrice = item.UnitPrice;
				var staged = await _revisions.StageLineChangeAsync(companyId, opened.revisionId,
					item.ID, line.Quantity, line.UnitPrice, $"Variation order VO-{vo.VoNo}");
				if (!staged.ok) return (false, staged.error);
				if (line.MaterialCost.HasValue) item.MaterialCost = line.MaterialCost;
				if (line.LaborCost.HasValue) item.LaborCost = line.LaborCost;
				if (line.SubcontractCost.HasValue) item.SubcontractCost = line.SubcontractCost;
				if (line.EquipmentCost.HasValue) item.EquipmentCost = line.EquipmentCost;
			}
			foreach (var item in addedItems)
			{
				var staged = await _revisions.StageLineChangeAsync(companyId, opened.revisionId,
					item.ID, item.Quantity, item.UnitPrice, $"New line from variation order VO-{vo.VoNo}");
				if (!staged.ok) return (false, staged.error);
			}

			var approved = await _revisions.ApproveAsync(companyId, opened.revisionId, userId, vo.Reason);
			if (!approved.ok) return (false, approved.error);

			// Recalculate the VO amount from the actual values at approval time; a draft may have become stale.
			var adjustmentImpact = await _db.CommercialRevisionLines
				.Where(l => l.CommercialRevisionId == opened.revisionId && l.BoqItemId != null)
				.SumAsync(l => (decimal?)l.ValueImpact) ?? 0m;
			vo.Value = R(addedItems.Sum(i => i.Quantity * i.UnitPrice) + adjustmentImpact);
			await _db.SaveChangesAsync();
			await tx.CommitAsync();	// operational/commercial only; no journal entry
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
		{
			var vo = await _db.VariationOrders.Include(v => v.Lines).FirstOrDefaultAsync(v => v.ID == id && v.CompanyID == companyId);
			if (vo == null) return (false, "Variation order not found");
			if (vo.Status != "Draft") return (false, "An approved variation order cannot be deleted");
			_db.VariationOrderLines.RemoveRange(vo.Lines);
			_db.VariationOrders.Remove(vo);
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
