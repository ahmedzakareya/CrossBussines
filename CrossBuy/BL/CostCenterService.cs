using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class CostCenterNode
	{
		public int Id { get; set; }
		public int? ParentId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int? SourceHierarchicalId { get; set; }
		public bool IsActive { get; set; }
		public int Level { get; set; }
		public List<CostCenterNode> Children { get; set; } = new();
	}

	public interface ICostCenterService
	{
		Task<List<CostCenterNode>> GetTreeAsync(int companyId);
		Task<List<CostCenter>> GetFlatAsync(int companyId, bool activeOnly = true);
		/// Create cost centers from the company's org tree (branch/administrative-body nodes). Idempotent.
		Task<int> SeedFromHierarchyAsync(int companyId);
	}

	public class CostCenterService : ICostCenterService
	{
		private readonly CrossDbContext _context;
		public CostCenterService(CrossDbContext context) { _context = context; }

		public async Task<List<CostCenter>> GetFlatAsync(int companyId, bool activeOnly = true)
		{
			return await _context.CostCenters.AsNoTracking()
				.Where(c => c.CompanyID == companyId && (!activeOnly || c.IsActive))
				.OrderBy(c => c.Code).ToListAsync();
		}

		public async Task<List<CostCenterNode>> GetTreeAsync(int companyId)
		{
			var list = await GetFlatAsync(companyId, activeOnly: false);
			var nodes = list.ToDictionary(c => c.ID, c => new CostCenterNode
			{
				Id = c.ID, ParentId = c.ParentId, Code = c.Code, Name = c.Name, NameEn = c.NameEn,
				SourceHierarchicalId = c.SourceHierarchicalId, IsActive = c.IsActive,
			});
			var roots = new List<CostCenterNode>();
			foreach (var c in list)
			{
				var n = nodes[c.ID];
				if (c.ParentId.HasValue && nodes.TryGetValue(c.ParentId.Value, out var p)) p.Children.Add(n);
				else roots.Add(n);
			}
			void SetLevel(CostCenterNode n, int lvl) { n.Level = lvl; foreach (var ch in n.Children) SetLevel(ch, lvl + 1); }
			foreach (var r in roots) SetLevel(r, 0);
			return roots;
		}

		public async Task<int> SeedFromHierarchyAsync(int companyId)
		{
			var all = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var byId = all.ToDictionary(h => h.H_ID);

			// company root node(s): H_Type 1 with H_ObjectID == companyId
			var roots = all.Where(h => h.H_Type == 1 && h.H_ObjectID == companyId).Select(h => h.H_ID).ToHashSet();
			if (roots.Count == 0) return 0;

			bool BelongsToCompany(Hierarchical h)
			{
				var cur = h; var guard = 0;
				while (cur != null && guard++ < 50)
				{
					if (cur.H_Type == 1) return roots.Contains(cur.H_ID);
					cur = cur.H_Parent.HasValue && byId.ContainsKey(cur.H_Parent.Value) ? byId[cur.H_Parent.Value] : null;
				}
				return false;
			}

			// candidate org units = branch (2) or administrative body (3) under this company
			var units = all.Where(h => (h.H_Type == 2 || h.H_Type == 3) && BelongsToCompany(h)).ToList();
			if (units.Count == 0) return 0;

			var existing = await _context.CostCenters.AsNoTracking()
				.Where(c => c.CompanyID == companyId && c.SourceHierarchicalId != null)
				.Select(c => c.SourceHierarchicalId!.Value).ToListAsync();
			var existingSet = existing.ToHashSet();

			// next code suffix
			var count = await _context.CostCenters.CountAsync(c => c.CompanyID == companyId);
			var seq = count + 1;
			var created = 0;
			var createdByHid = new Dictionary<int, CostCenter>();

			foreach (var u in units)
			{
				if (existingSet.Contains(u.H_ID)) continue;
				var cc = new CostCenter
				{
					CompanyID = companyId,
					Code = "CC" + seq.ToString("D3"),
					Name = string.IsNullOrWhiteSpace(u.H_Name) ? (u.H_NameEn ?? $"Unit {u.H_ID}") : u.H_Name!,
					NameEn = string.IsNullOrWhiteSpace(u.H_NameEn) ? (u.H_Name ?? $"Unit {u.H_ID}") : u.H_NameEn!,
					SourceHierarchicalId = u.H_ID,
					IsActive = true,
					CreatedAt = DateTime.UtcNow,
				};
				_context.CostCenters.Add(cc);
				createdByHid[u.H_ID] = cc;
				seq++; created++;
			}
			if (created == 0) return 0;
			await _context.SaveChangesAsync();   // assigns IDs

			// map ParentId: nearest ancestor unit that is also a cost center
			var ccByHid = await _context.CostCenters.AsNoTracking()
				.Where(c => c.CompanyID == companyId && c.SourceHierarchicalId != null)
				.ToDictionaryAsync(c => c.SourceHierarchicalId!.Value, c => c.ID);

			foreach (var kv in createdByHid)
			{
				var node = byId[kv.Key];
				var cur = node.H_Parent.HasValue && byId.ContainsKey(node.H_Parent.Value) ? byId[node.H_Parent.Value] : null;
				var guard = 0;
				while (cur != null && guard++ < 50)
				{
					if (ccByHid.TryGetValue(cur.H_ID, out var parentCcId)) { kv.Value.ParentId = parentCcId; break; }
					cur = cur.H_Parent.HasValue && byId.ContainsKey(cur.H_Parent.Value) ? byId[cur.H_Parent.Value] : null;
				}
			}
			await _context.SaveChangesAsync();
			return created;
		}
	}
}
