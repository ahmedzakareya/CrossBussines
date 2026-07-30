using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Projects & Contracting — P6-أ: budget-vs-actual report. READ-ONLY aggregation, writes NO GL.
	// Estimated = BOQ per-line cost breakdown (P1). Actual (per item) = posted material issues attributed via BoqItemId (P5-أ).
	// Total actual = Σ debit on 510104 for the project (materials + labor, P5). Unattributed = total − attributed.
	public class BudgetLineRow
	{
		public int BoqItemId { get; set; }
		public string? Code { get; set; }
		public string Description { get; set; } = "";
		public decimal EstimatedCost { get; set; }   // material+labor+subcontract+equipment (BOQ)
		public decimal ActualCost { get; set; }       // attributed material issues (BoqItemId)
		public decimal Variance => Math.Round(EstimatedCost - ActualCost, 2);   // + = saving, − = overrun
		public bool Over => ActualCost > EstimatedCost;
	}

	public class BudgetReport
	{
		public Project? Project { get; set; }
		public List<BudgetLineRow> Lines { get; set; } = new();
		public decimal TotalEstimated { get; set; }
		public decimal TotalActual { get; set; }         // GL 510104 net debit by project (materials + labor)
		public decimal AttributedActual { get; set; }    // Σ attributed material (has BoqItemId)
		public decimal UnattributedActual => Math.Round(TotalActual - AttributedActual, 2);   // unattributed material + labor + general
		public decimal TotalVariance => Math.Round(TotalEstimated - TotalActual, 2);
	}

	public interface IProjectBudgetService
	{
		Task<BudgetReport> GetBudgetVsActualAsync(int companyId, int projectId);
	}

	public class ProjectBudgetService : IProjectBudgetService
	{
		private readonly CrossDbContext _db;
		public ProjectBudgetService(CrossDbContext db) { _db = db; }
		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		public async Task<BudgetReport> GetBudgetVsActualAsync(int companyId, int projectId)
		{
			var rep = new BudgetReport
			{
				Project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId)
			};

			// BOQ leaf items (parents/section headers roll up from children → measured on leaves only)
			var all = await _db.BoqItems.AsNoTracking().Where(b => b.CompanyID == companyId && b.ProjectId == projectId)
				.OrderBy(b => b.SortOrder).ThenBy(b => b.ID).ToListAsync();
			var parentIds = all.Where(x => x.ParentId != null).Select(x => x.ParentId!.Value).ToHashSet();
			var leaves = all.Where(x => !parentIds.Contains(x.ID)).ToList();

			// attributed actual material per BOQ item (posted issues, BoqItemId set)
			var matByBoq = (await (from l in _db.ProjectMaterialIssueLines.AsNoTracking()
								   join h in _db.ProjectMaterialIssues.AsNoTracking() on l.IssueId equals h.ID
								   where h.CompanyID == companyId && h.ProjectId == projectId && h.Status == "Posted" && l.BoqItemId != null
								   group l by l.BoqItemId!.Value into g
								   select new { boq = g.Key, cost = g.Sum(x => x.TotalCost) }).ToListAsync())
						   .ToDictionary(x => x.boq, x => R(x.cost));

			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			foreach (var b in leaves)
			{
				decimal est = R((b.MaterialCost ?? 0m) + (b.LaborCost ?? 0m) + (b.SubcontractCost ?? 0m) + (b.EquipmentCost ?? 0m));
				decimal act = matByBoq.TryGetValue(b.ID, out var c) ? c : 0m;
				var desc = isAr ? b.Description : (string.IsNullOrWhiteSpace(b.DescriptionEn) ? b.Description : b.DescriptionEn);
				rep.Lines.Add(new BudgetLineRow { BoqItemId = b.ID, Code = b.Code, Description = desc, EstimatedCost = est, ActualCost = act });
			}
			rep.TotalEstimated = R(rep.Lines.Sum(l => l.EstimatedCost));
			rep.AttributedActual = R(matByBoq.Values.Sum());

			// total actual = net debit on 510104 for this project (materials + labor)
			var costAccId = await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == ProjectMaterialIssueService.ProjectCostAccountCode).Select(a => (int?)a.ID).FirstOrDefaultAsync();
			if (costAccId != null)
				rep.TotalActual = R(await (from l in _db.JournalEntryLines.AsNoTracking()
										   join e in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
										   where e.CompanyID == companyId && e.Status == "Posted" && l.AccountId == costAccId.Value && l.ProjectId == projectId
										   select (decimal?)(l.Debit - l.Credit)).SumAsync() ?? 0m);
			return rep;
		}
	}
}
