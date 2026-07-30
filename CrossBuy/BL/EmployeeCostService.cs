using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// TM-4: THE single source of truth for an employee's hourly cost. Derivation is exactly what ManufService.AddLaborAsync
	// used (now rerouted here): explicit Employee.ManufHourlyRate, else BaseSalary (salary policy) ÷ PayrollSettings.StandardMonthlyHours.
	// Read-only — no GL/stock. Returns 0 when not derivable (caller decides how to handle).
	public interface IEmployeeCostService
	{
		Task<decimal> HourlyCostAsync(int companyId, int employeeId);
	}

	public class EmployeeCostService : IEmployeeCostService
	{
		private readonly CrossDbContext _db;
		public EmployeeCostService(CrossDbContext db) { _db = db; }

		public async Task<decimal> HourlyCostAsync(int companyId, int employeeId)
		{
			if (employeeId <= 0) return 0m;
			var empRate = await _db.Employee.AsNoTracking().Where(e => e.ID == employeeId).Select(e => e.ManufHourlyRate).FirstOrDefaultAsync();
			if (empRate.HasValue && empRate.Value > 0) return empRate.Value;
			// fallback: BaseSalary (from the employee's salary policy) ÷ standard monthly hours
			var baseSalary = await _db.SalaryPolicies.AsNoTracking().Where(sp => sp.Employees.Any(e => e.ID == employeeId)).Select(sp => (decimal?)sp.BaseSalary).FirstOrDefaultAsync();
			var stdHours = await _db.PayrollSettings.AsNoTracking().Where(p => p.CompanyID == companyId).Select(p => (decimal?)p.StandardMonthlyHours).FirstOrDefaultAsync() ?? 0m;
			if (baseSalary.HasValue && baseSalary.Value > 0 && stdHours > 0) return Math.Round(baseSalary.Value / stdHours, 4);
			return 0m;
		}
	}
}
