using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR4 — THE ONE PLACE AN EMPLOYEE'S PAY IS READ (2026-11-08).
	//
	// Payroll, the end-of-service settlement, the leave daily rate and the manufacturing labour rate all used to read the
	// salary of the LEAVE POLICY the employee was assigned to — four copies of one lookup, two of them by different paths,
	// and every employee on a policy earning the same. They now ask here:
	//   * the employee's own effective-dated salary (EmployeeSalaries) in force on the date, else
	//   * the legacy salary policy of their assigned leave policy, so nobody's figure changes until HR sets a salary.
	// Source says which one answered, so a screen can show "from the policy" honestly instead of implying it was set.
	// ==========================================================================================
	public sealed record SalaryParts(decimal Basic, decimal Housing, decimal Transport, decimal Other, string Source, DateTime? EffectiveFrom)
	{
		public decimal Allowances => Housing + Transport + Other;
		public decimal Total => Basic + Allowances;
		public bool IsEmployeeOwn => Source == HrSalaryService.SourceEmployee;
		public static readonly SalaryParts None = new(0, 0, 0, 0, HrSalaryService.SourceNone, null);
	}

	public sealed class HrSalaryService
	{
		public const string SourceEmployee = "employee", SourcePolicy = "policy", SourceNone = "none";
		private readonly CrossDbContext _db;
		public HrSalaryService(CrossDbContext db) { _db = db; }

		public async Task<SalaryParts> ForAsync(int employeeId, DateTime on, CancellationToken ct = default)
			=> (await ForManyAsync(new[] { employeeId }, on, ct)).TryGetValue(employeeId, out var p) ? p : SalaryParts.None;

		/// The pay in force on <paramref name="on"/> for each employee (absent from the result = no salary anywhere).
		public async Task<Dictionary<int, SalaryParts>> ForManyAsync(IEnumerable<int> employeeIds, DateTime on, CancellationToken ct = default)
		{
			var ids = employeeIds.Where(i => i > 0).Distinct().ToList();
			var result = new Dictionary<int, SalaryParts>();
			if (ids.Count == 0) return result;
			var day = on.Date;

			var own = await _db.EmployeeSalaries.AsNoTracking()
				.Where(s => ids.Contains(s.EmployeeID) && s.EffectiveFrom <= day)
				.ToListAsync(ct);
			foreach (var g in own.GroupBy(s => s.EmployeeID))
			{
				var s = g.OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.ID).First();
				result[g.Key] = new SalaryParts(s.BasicSalary, s.HousingAllowance, s.TransportAllowance, s.OtherAllowances, SourceEmployee, s.EffectiveFrom);
			}

			var rest = ids.Where(i => !result.ContainsKey(i)).ToList();
			if (rest.Count > 0)
			{
				var policyOf = (await _db.PolicyAssignments.AsNoTracking().Where(a => rest.Contains(a.EmployeeID)).ToListAsync(ct))
					.GroupBy(a => a.EmployeeID).ToDictionary(g => g.Key, g => g.First().LeavePolicyTypeID);
				var policyIds = policyOf.Values.Distinct().ToList();
				var salaries = (await _db.SalaryPolicies.AsNoTracking().Where(s => policyIds.Contains(s.LeavePolicyTypeID)).ToListAsync(ct))
					.GroupBy(s => s.LeavePolicyTypeID).ToDictionary(g => g.Key, g => g.OrderBy(s => s.ID).First());
				foreach (var id in rest)
					if (policyOf.TryGetValue(id, out var pol) && salaries.TryGetValue(pol, out var sp))
						result[id] = new SalaryParts(sp.BaseSalary, sp.HousingAllowance, sp.TransportationAllowance, sp.OtherAllowances, SourcePolicy, null);
			}
			return result;
		}

		public Task<List<EmployeeSalary>> HistoryAsync(int companyId, int employeeId, CancellationToken ct = default)
			=> _db.EmployeeSalaries.AsNoTracking().Where(s => s.CompanyID == companyId && s.EmployeeID == employeeId)
				.OrderByDescending(s => s.EffectiveFrom).ThenByDescending(s => s.ID).ToListAsync(ct);

		/// Records the pay from <paramref name="effectiveFrom"/>. A change on a date that already has a row REPLACES that
		/// row (a correction the same day); any other date adds a row and leaves the past untouched.
		public async Task<(bool Ok, string? ErrorAr, string? ErrorEn)> SetAsync(int companyId, int employeeId, DateTime effectiveFrom,
			decimal basic, decimal housing, decimal transport, decimal other, string? note, int? byEmployeeId, CancellationToken ct = default)
		{
			if (new[] { basic, housing, transport, other }.Any(v => v < 0 || v > 100_000_000m))
				return (false, "قيم الراتب يجب أن تكون صفراً أو أكثر", "Salary figures must be zero or more");
			if (basic <= 0) return (false, "الراتب الأساسي مطلوب", "The basic salary is required");
			var belongs = await _db.Employee.AsNoTracking().AnyAsync(e => e.ID == employeeId && e.EmpCompanyID == companyId, ct);
			if (!belongs) return (false, "الموظف غير موجود", "Employee not found");
			var day = effectiveFrom.Date;
			var row = await _db.EmployeeSalaries.FirstOrDefaultAsync(s => s.EmployeeID == employeeId && s.EffectiveFrom == day, ct);
			if (row == null) { row = new EmployeeSalary { CompanyID = companyId, EmployeeID = employeeId, EffectiveFrom = day }; _db.EmployeeSalaries.Add(row); }
			row.BasicSalary = basic; row.HousingAllowance = housing; row.TransportAllowance = transport; row.OtherAllowances = other;
			row.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			row.CreatedAt = DateTime.UtcNow; row.CreatedByEmployeeId = byEmployeeId;
			await _db.SaveChangesAsync(ct);
			return (true, null, null);
		}
	}
}
