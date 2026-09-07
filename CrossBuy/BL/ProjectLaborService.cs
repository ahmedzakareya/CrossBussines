using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Projects & Contracting — P5-ب: project labor. Timesheet hours on project-linked tasks (TM-2 EntityType="Project")
	// × hourly cost (IEmployeeCostService) → posted via the EXISTING JournalEntryService as a reclassification:
	// Dr 510104 project cost [ProjectId] / Cr 520101 salary [UNtagged] — so ProfitabilityAsync counts only the debit.
	// Manual review + post-once (TaskItem.LaborPostedAt). No new accounting writer.
	public class ProjectLaborRow
	{
		public int TaskId { get; set; }
		public string Title { get; set; } = "";
		public decimal Hours { get; set; }
		public decimal Cost { get; set; }
		public bool Posted { get; set; }
	}

	public interface IProjectLaborService
	{
		Task<List<ProjectLaborRow>> GetProjectLaborAsync(int companyId, int projectId);
		Task<(bool ok, string? error)> PostTaskLaborAsync(int companyId, int taskId, int? userId);
	}

	public class ProjectLaborService : IProjectLaborService
	{
		public const string ProjectCostAccountCode = "510104";   // Dr — project execution cost
		public const string SalaryAccountCode = "520101";        // Cr — salaries (reclass; RequireCostCenter)
		private readonly CrossDbContext _db;
		private readonly IEmployeeCostService _cost;
		private readonly IJournalEntryService _je;
		public ProjectLaborService(CrossDbContext db, IEmployeeCostService cost, IJournalEntryService je) { _db = db; _cost = cost; _je = je; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
		private Task<int?> AccIdAsync(int companyId, string code) =>
			_db.Accounts.Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		// hours + labor cost for a task = Σ (employee hours × hourly cost)
		private async Task<(decimal hours, decimal cost, string? err)> TaskCostAsync(int companyId, int taskId, bool validateRate)
		{
			var byEmp = await _db.TimesheetEntries.AsNoTracking()
				.Where(e => e.CompanyId == companyId && e.TaskId == taskId && e.Hours > 0)
				.GroupBy(e => e.EmployeeId).Select(g => new { emp = g.Key, hours = g.Sum(x => x.Hours) }).ToListAsync();
			decimal hours = 0, cost = 0;
			foreach (var b in byEmp)
			{
				var rate = await _cost.HourlyCostAsync(companyId, b.emp);
				if (validateRate && rate <= 0) return (0, 0, $"Employee #{b.emp} has no derivable hourly rate — set one first");
				hours += b.hours; cost += R(b.hours * rate);
			}
			return (R(hours), R(cost), null);
		}

		public async Task<List<ProjectLaborRow>> GetProjectLaborAsync(int companyId, int projectId)
		{
			var tasks = await _db.TaskItems.AsNoTracking()
				.Where(t => t.CompanyId == companyId && t.EntityType == "Project" && t.EntityId == projectId)
				.OrderBy(t => t.ID).ToListAsync();
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var rows = new List<ProjectLaborRow>();
			foreach (var t in tasks)
			{
				var (h, c, _) = await TaskCostAsync(companyId, t.ID, false);
				var title = isAr ? t.Title : (string.IsNullOrWhiteSpace(t.TitleEn) ? t.Title : t.TitleEn);
				rows.Add(new ProjectLaborRow { TaskId = t.ID, Title = title, Hours = h, Cost = c, Posted = t.LaborPostedAt != null });
			}
			return rows;
		}

		public async Task<(bool ok, string? error)> PostTaskLaborAsync(int companyId, int taskId, int? userId)
		{
			var task = await _db.TaskItems.FirstOrDefaultAsync(t => t.ID == taskId && t.CompanyId == companyId);
			if (task == null) return (false, "Task not found");
			if (task.EntityType != "Project" || !(task.EntityId > 0)) return (false, "The task is not linked to a project");
			if (task.LaborPostedAt != null) return (false, "Labour for this task has already been posted");   // post-once
			int projectId = task.EntityId!.Value;

			var (hours, cost, err) = await TaskCostAsync(companyId, taskId, true);
			if (err != null) return (false, err);
			if (hours <= 0) return (false, "There are no hours recorded on the task to post");
			if (cost <= 0) return (false, "The labour cost is zero");

			var costAcc = await AccIdAsync(companyId, ProjectCostAccountCode);
			if (costAcc == null) return (false, $"The execution cost account ({ProjectCostAccountCode}) is not configured");
			var salAcc = await AccIdAsync(companyId, SalaryAccountCode);
			if (salAcc == null) return (false, $"The salary account ({SalaryAccountCode}) is not configured");
			// 520101 requires a cost center — use the project's (P0) or the first company cost center (does NOT affect project profitability, which filters by ProjectId)
			var prjCc = await _db.Projects.AsNoTracking().Where(p => p.ID == projectId).Select(p => p.CostCenterId).FirstOrDefaultAsync();
			int? cc = prjCc ?? await _db.CostCenters.AsNoTracking().Where(x => x.CompanyID == companyId).OrderBy(x => x.ID).Select(x => (int?)x.ID).FirstOrDefaultAsync();
			if (cc == null) return (false, "The salary account requires a cost centre and no cost centre is defined");

			var curId = await _db.Currencies.Select(c => c.ID).FirstOrDefaultAsync();
			var input = new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = DateTime.Today, JournalType = "Manual", CurrencyId = curId,
				SourceType = "ProjectLabor", SourceId = taskId,
				Description = "عمالة مشروع — مهمة " + task.Title, DescriptionEn = "Project labor — task " + task.Title,
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = costAcc.Value, Debit = cost, Credit = 0, ProjectId = projectId, Description = "تكلفة عمالة المشروع" },
					new() { AccountId = salAcc.Value, Debit = 0, Credit = cost, ProjectId = null, CostCenterId = cc, Description = "Reclassification of salaries to the project" },
				}
			};
			var (ok, jerr, _) = await _je.CreateAndPostAsync(input, userId);
			if (!ok) return (false, jerr);
			task.LaborPostedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
