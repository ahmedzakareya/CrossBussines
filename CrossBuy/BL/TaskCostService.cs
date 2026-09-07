using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// TM-4: task costing. Two faces:
	//  (1) informational cost = Σ (hours × employee hourly cost) — DISPLAY ONLY, writes NO GL.
	//  (2) posting a WO-linked task's labor → routed through the EXISTING ManufService.AddLaborAsync (the labor writer);
	//      no new accounting writer. Post-once, guarded by TaskItem.LaborPostedAt (prevents double-posting).
	public class TaskCostLineDto { public int EmployeeId { get; set; } public string EmployeeName { get; set; } = ""; public decimal Hours { get; set; } public decimal Rate { get; set; } public decimal Cost { get; set; } }
	public class TaskCostDto { public decimal Total { get; set; } public bool LinkedToWorkOrder { get; set; } public bool LaborPosted { get; set; } public List<TaskCostLineDto> Lines { get; set; } = new(); }

	public interface ITaskCostService
	{
		Task<TaskCostDto> GetTaskCostAsync(int companyId, int taskId);
		Task<(bool ok, string? error)> PostToWorkOrderAsync(int companyId, int taskId, string? userId);
	}

	public class TaskCostService : ITaskCostService
	{
		private readonly CrossDbContext _db;
		private readonly IEmployeeCostService _cost;
		private readonly IManufService _manuf;
		public TaskCostService(CrossDbContext db, IEmployeeCostService cost, IManufService manuf) { _db = db; _cost = cost; _manuf = manuf; }

		// hours grouped by employee for a task (only entries with hours > 0)
		private async Task<List<(int emp, decimal hours)>> HoursByEmployeeAsync(int companyId, int taskId)
		{
			var g = await _db.TimesheetEntries.AsNoTracking()
				.Where(e => e.CompanyId == companyId && e.TaskId == taskId && e.Hours > 0)
				.GroupBy(e => e.EmployeeId)
				.Select(x => new { emp = x.Key, hours = x.Sum(e => e.Hours) })
				.ToListAsync();
			return g.Select(x => (x.emp, x.hours)).ToList();
		}

		public async Task<TaskCostDto> GetTaskCostAsync(int companyId, int taskId)
		{
			var task = await _db.TaskItems.AsNoTracking().FirstOrDefaultAsync(t => t.ID == taskId && t.CompanyId == companyId);
			var dto = new TaskCostDto { LinkedToWorkOrder = task?.EntityType == "ManufWorkOrder" && task.EntityId > 0, LaborPosted = task?.LaborPostedAt != null };
			var byEmp = await HoursByEmployeeAsync(companyId, taskId);
			var empIds = byEmp.Select(x => x.emp).ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn })
                .ToDictionaryAsync(e => e.ID, e => EmployeeNames.Of(e.FullName, e.FullNameEn));
			foreach (var (emp, hours) in byEmp)
			{
				var rate = await _cost.HourlyCostAsync(companyId, emp);   // read-only; no GL
				var cost = Math.Round(hours * rate, 2);
				dto.Lines.Add(new TaskCostLineDto { EmployeeId = emp, EmployeeName = names.TryGetValue(emp, out var n) ? n : "", Hours = hours, Rate = rate, Cost = cost });
				dto.Total += cost;
			}
			return dto;
		}

		public async Task<(bool ok, string? error)> PostToWorkOrderAsync(int companyId, int taskId, string? userId)
		{
			var task = await _db.TaskItems.FirstOrDefaultAsync(t => t.ID == taskId && t.CompanyId == companyId);
			if (task == null) return (false, "Task not found");
			if (task.EntityType != "ManufWorkOrder" || !(task.EntityId > 0)) return (false, "The task is not linked to a work order");
			if (task.LaborPostedAt != null) return (false, "Labour for this task has already been posted to the work order");
			int woId = task.EntityId!.Value;
			var wo = await _db.ManufWorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.ID == woId && w.CompanyID == companyId);
			if (wo == null) return (false, "Work order not found");

			var byEmp = await HoursByEmployeeAsync(companyId, taskId);
			if (byEmp.Count == 0) return (false, "There are no hours recorded on the task to post");

			// pre-validate every employee has a derivable hourly cost (avoid a partial post)
			foreach (var (emp, _) in byEmp)
				if (await _cost.HourlyCostAsync(companyId, emp) <= 0)
					return (false, $"Employee #{emp} has no derivable hourly rate — set one first");

			// post each employee's hours as a labor line via the EXISTING writer (rate derived by ManufService/EmployeeCostService)
			foreach (var (emp, hours) in byEmp)
			{
				var (ok, err, _) = await _manuf.AddLaborAsync(companyId, woId, "Employee", emp, null, hours, null, null, null, null, null, DateTime.Today, userId);
				if (!ok) return (false, $"Could not post labour for employee #{emp}: {err}");
			}

			task.LaborPostedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
