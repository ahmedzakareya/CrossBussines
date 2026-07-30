using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Project BOQ totals (estimate — no GL). ΣLineValue vs the project's ContractValue for a quick sanity check.
	public class BoqSummary
	{
		public int ItemCount { get; set; }
		public decimal TotalValue { get; set; }            // Σ (Qty × UnitPrice)
		public decimal TotalCost { get; set; }             // Σ (material+labor+subcontract+equipment)
		public decimal Margin => Math.Round(TotalValue - TotalCost, 2);
		public decimal MarginPct => TotalValue != 0 ? Math.Round(Margin / TotalValue * 100m, 2) : 0m;
		public decimal? ContractValue { get; set; }        // from Project (P0)
		public decimal VarianceVsContract => Math.Round(TotalValue - (ContractValue ?? 0m), 2);  // ΣValue − contract
	}

	// one inline BOQ row posted from the editor (invoice-lines style). Kind = "main" (section header) or "sub".
	public class BoqRowInput
	{
		public string? Kind { get; set; }
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

	public interface IBoqService
	{
		Task<List<BoqItem>> GetForProjectAsync(int companyId, int projectId);
		Task<(bool ok, string? error, int id)> SaveItemAsync(BoqItem dto);
		Task<(bool ok, string? error)> DeleteItemAsync(int companyId, int id);
		Task<BoqSummary> GetSummaryAsync(int companyId, int projectId);
		// inline editor: replace the whole BOQ of a project in one save (rebuilds main→sub hierarchy from row order)
		Task<(bool ok, string? error, int count)> ReplaceAllAsync(int companyId, int projectId, List<BoqRowInput> rows);
	}

	public class BoqService : IBoqService
	{
		private readonly CrossDbContext _db;
		public BoqService(CrossDbContext db) { _db = db; }

		public Task<List<BoqItem>> GetForProjectAsync(int companyId, int projectId) =>
			_db.BoqItems.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.ProjectId == projectId)
				.OrderBy(b => b.SortOrder).ThenBy(b => b.ID).ToListAsync();

		public async Task<(bool ok, string? error, int id)> SaveItemAsync(BoqItem dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Description)) return (false, "الوصف مطلوب", 0);
			if (dto.Quantity < 0 || dto.UnitPrice < 0) return (false, "الكمية والسعر لا يكونان بالسالب", 0);
			// project must belong to the company
			var prj = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == dto.ProjectId && p.CompanyID == dto.CompanyID);
			if (prj == null) return (false, "المشروع غير موجود", 0);
			// parent (if any) must be a BOQ item of the SAME project
			if (dto.ParentId.HasValue)
			{
				if (dto.ParentId.Value == dto.ID) return (false, "لا يكون البند أبًا لنفسه", 0);
				var parentOk = await _db.BoqItems.AnyAsync(b => b.ID == dto.ParentId.Value && b.ProjectId == dto.ProjectId && b.CompanyID == dto.CompanyID);
				if (!parentOk) return (false, "البند الرئيسي غير صالح", 0);
			}

			BoqItem e;
			if (dto.ID > 0)
				e = await _db.BoqItems.FirstOrDefaultAsync(b => b.ID == dto.ID && b.CompanyID == dto.CompanyID) ?? throw new InvalidOperationException("البند غير موجود");
			else
			{
				e = new BoqItem { CompanyID = dto.CompanyID, ProjectId = dto.ProjectId, CreatedAt = DateTime.UtcNow };
				if (dto.SortOrder <= 0)
					e.SortOrder = ((await _db.BoqItems.Where(b => b.CompanyID == dto.CompanyID && b.ProjectId == dto.ProjectId).Select(b => (int?)b.SortOrder).MaxAsync()) ?? 0) + 1;
				else e.SortOrder = dto.SortOrder;
				_db.BoqItems.Add(e);
			}
			e.ParentId = dto.ParentId; if (dto.ID > 0 && dto.SortOrder > 0) e.SortOrder = dto.SortOrder;
			e.Code = string.IsNullOrWhiteSpace(dto.Code) ? null : dto.Code.Trim();
			e.Description = dto.Description.Trim();
			e.DescriptionEn = string.IsNullOrWhiteSpace(dto.DescriptionEn) ? null : dto.DescriptionEn.Trim();
			e.Unit = string.IsNullOrWhiteSpace(dto.Unit) ? null : dto.Unit.Trim();
			e.Quantity = dto.Quantity; e.UnitPrice = dto.UnitPrice;
			e.MaterialCost = dto.MaterialCost; e.LaborCost = dto.LaborCost;
			e.SubcontractCost = dto.SubcontractCost; e.EquipmentCost = dto.EquipmentCost;
			await _db.SaveChangesAsync();
			return (true, null, e.ID);
		}

		public async Task<(bool ok, string? error)> DeleteItemAsync(int companyId, int id)
		{
			var e = await _db.BoqItems.FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);
			if (e == null) return (false, "البند غير موجود");
			if (await _db.BoqItems.AnyAsync(b => b.ParentId == id)) return (false, "احذف البنود الفرعية أولًا");
			_db.BoqItems.Remove(e); await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error, int count)> ReplaceAllAsync(int companyId, int projectId, List<BoqRowInput> rows)
		{
			var prj = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			if (prj == null) return (false, "المشروع غير موجود", 0);

			// full replace (estimate data, no GL) — clear then re-insert in order
			var existing = await _db.BoqItems.Where(b => b.CompanyID == companyId && b.ProjectId == projectId).ToListAsync();
			if (existing.Count > 0) { _db.BoqItems.RemoveRange(existing); await _db.SaveChangesAsync(); }

			int sort = 1, count = 0; int? lastMainId = null;
			foreach (var r in rows)
			{
				if (string.IsNullOrWhiteSpace(r.Description)) continue;   // skip blank rows
				var e = new BoqItem
				{
					CompanyID = companyId, ProjectId = projectId, SortOrder = sort++,
					Code = string.IsNullOrWhiteSpace(r.Code) ? null : r.Code.Trim(),
					Description = r.Description.Trim(),
					DescriptionEn = string.IsNullOrWhiteSpace(r.DescriptionEn) ? null : r.DescriptionEn.Trim(),
					Unit = string.IsNullOrWhiteSpace(r.Unit) ? null : r.Unit.Trim(),
					Quantity = r.Quantity < 0 ? 0 : r.Quantity,
					UnitPrice = r.UnitPrice < 0 ? 0 : r.UnitPrice,
					MaterialCost = r.MaterialCost, LaborCost = r.LaborCost,
					SubcontractCost = r.SubcontractCost, EquipmentCost = r.EquipmentCost,
					CreatedAt = DateTime.UtcNow
				};
				bool isSub = string.Equals(r.Kind, "sub", StringComparison.OrdinalIgnoreCase);
				if (isSub && lastMainId.HasValue) e.ParentId = lastMainId;
				_db.BoqItems.Add(e);
				await _db.SaveChangesAsync();   // assign ID
				if (!isSub) lastMainId = e.ID;   // a main row becomes the parent for following sub rows
				count++;
			}
			return (true, null, count);
		}

		public async Task<BoqSummary> GetSummaryAsync(int companyId, int projectId)
		{
			var items = await GetForProjectAsync(companyId, projectId);
			var cv = await _db.Projects.AsNoTracking().Where(p => p.ID == projectId && p.CompanyID == companyId).Select(p => p.ContractValue).FirstOrDefaultAsync();
			return new BoqSummary
			{
				ItemCount = items.Count,
				TotalValue = Math.Round(items.Sum(i => i.LineValue), 2),
				TotalCost = Math.Round(items.Sum(i => i.EstimatedCost), 2),
				ContractValue = cv
			};
		}
	}
}
