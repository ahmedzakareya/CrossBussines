using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class SettlementPreview
	{
		public int EmployeeId { get; set; }
		public string? EmployeeName { get; set; }
		public bool AlreadyTerminated { get; set; }
		public DateTime JoinDate { get; set; }
		public decimal ServiceYears { get; set; }
		public decimal DailyRate { get; set; }
		public int LeaveDays { get; set; }
		public decimal LeaveValue { get; set; }
		public decimal GratuityDaysPerYear { get; set; }
		public decimal SuggestedGratuity { get; set; }
	}

	public interface IFinalSettlementService
	{
		Task<SettlementPreview> PreviewAsync(int companyId, int employeeId, DateTime terminationDate);
		Task<(bool ok, string? error)> PostAsync(int companyId, int employeeId, DateTime terminationDate, string? reason,
			decimal gratuity, decimal otherEarnings, decimal deductions, int payFromGlAccountId, int? userId);
	}

	// HR-7: end-of-service termination + final settlement. Reuses leave-encashment valuation + JournalEntryService.
	public class FinalSettlementService : IFinalSettlementService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		private readonly ILeaveAccrualService _leave;
		public FinalSettlementService(CrossDbContext context, IJournalEntryService journals, ILeaveAccrualService leave)
		{ _context = context; _journals = journals; _leave = leave; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		private async Task<int?> AccIdAsync(int companyId, string code) =>
			await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		// resolve the employee's cost center (walk up the org tree to the nearest mapped node; fallback to the first)
		private async Task<int?> ResolveCostCenterAsync(int companyId, int? departmentId)
		{
			var ccs = await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.Code).ToListAsync();
			if (ccs.Count == 0) return null;
			var bySource = ccs.Where(c => c.SourceHierarchicalId != null).ToDictionary(c => c.SourceHierarchicalId!.Value, c => c.ID);
			var hier = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var byId = hier.ToDictionary(h => h.H_ID);
			var cur = departmentId.HasValue && byId.ContainsKey(departmentId.Value) ? byId[departmentId.Value] : null;
			var guard = 0;
			while (cur != null && guard++ < 50)
			{
				if (bySource.TryGetValue(cur.H_ID, out var id)) return id;
				cur = cur.H_Parent.HasValue && byId.ContainsKey(cur.H_Parent.Value) ? byId[cur.H_Parent.Value] : null;
			}
			return ccs.First().ID;
		}

		public async Task<SettlementPreview> PreviewAsync(int companyId, int employeeId, DateTime terminationDate)
		{
			var emp = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == employeeId && e.EmpCompanyID == companyId);
			var pv = new SettlementPreview { EmployeeId = employeeId, EmployeeName = emp?.FullName };
			if (emp == null) return pv;
			pv.AlreadyTerminated = !emp.IsActive;
			pv.JoinDate = emp.DateOfJoining;
			var years = (terminationDate.Date - emp.DateOfJoining.Date).TotalDays / 365.25;
			pv.ServiceYears = R((decimal)Math.Max(0, years));
			pv.DailyRate = await _leave.DailyRateAsync(employeeId);

			var balances = await _leave.GetEncashableBalancesAsync(employeeId);
			pv.LeaveDays = balances.Sum(b => b.RemainingDays);
			pv.LeaveValue = R(balances.Sum(b => b.Amount));

			var settings = await _context.PayrollSettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyID == companyId);
			pv.GratuityDaysPerYear = settings?.GratuityDaysPerYear ?? 0m;
			pv.SuggestedGratuity = R(pv.ServiceYears * pv.GratuityDaysPerYear * pv.DailyRate);
			return pv;
		}

		public async Task<(bool ok, string? error)> PostAsync(int companyId, int employeeId, DateTime terminationDate, string? reason,
			decimal gratuity, decimal otherEarnings, decimal deductions, int payFromGlAccountId, int? userId)
		{
			var emp = await _context.Employee.FirstOrDefaultAsync(e => e.ID == employeeId && e.EmpCompanyID == companyId);
			if (emp == null) return (false, "الموظف غير موجود");
			if (!emp.IsActive) return (false, "الموظف منهٍ خدمته بالفعل");
			if (gratuity < 0 || otherEarnings < 0 || deductions < 0) return (false, "القيم لا يمكن أن تكون سالبة");

			var pv = await PreviewAsync(companyId, employeeId, terminationDate);
			var leaveValue = pv.LeaveValue;
			var gross = R(leaveValue + gratuity + otherEarnings);
			var net = R(gross - deductions);
			if (net < 0) return (false, "الاستقطاعات تتجاوز إجمالي المستحقات");

			var leaveAcc = await AccIdAsync(companyId, "520104");
			var gratuityAcc = await AccIdAsync(companyId, "520107");
			var salaryAcc = await AccIdAsync(companyId, "520101");
			if (leaveAcc == null || gratuityAcc == null || salaryAcc == null)
				return (false, "حسابات التسوية (520104/520107/520101) غير موجودة. شغّل بذرة المحاسبة.");

			var ccId = await ResolveCostCenterAsync(companyId, emp.DepartmentID);   // 520101 requires a cost center

			var lines = new List<JournalLineInput>();
			if (leaveValue > 0) lines.Add(new() { AccountId = leaveAcc.Value, Debit = leaveValue, Credit = 0, Description = "بدل رصيد إجازات" });
			if (gratuity > 0) lines.Add(new() { AccountId = gratuityAcc.Value, Debit = gratuity, Credit = 0, Description = "مكافأة نهاية الخدمة" });
			if (otherEarnings > 0) lines.Add(new() { AccountId = salaryAcc.Value, Debit = otherEarnings, Credit = 0, CostCenterId = ccId, Description = "مستحقات أخرى" });
			if (net > 0) lines.Add(new() { AccountId = payFromGlAccountId, Debit = 0, Credit = net, Description = "صافي التسوية المدفوع" });
			// deductions recovered against salary expense (keeps the entry balanced without a new control account)
			if (deductions > 0) lines.Add(new() { AccountId = salaryAcc.Value, Debit = 0, Credit = deductions, CostCenterId = ccId, Description = "استقطاعات نهاية الخدمة" });

			if (lines.Sum(l => l.Debit) <= 0) return (false, "لا توجد مستحقات لترحيلها");

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = terminationDate.Date, JournalType = "Auto", SourceType = "FinalSettlement", SourceId = employeeId,
				Description = $"تسوية نهاية خدمة — {emp.FullName}", DescriptionEn = $"Final settlement — {emp.FullName}",
				Lines = lines,
			}, userId);
			if (!ok) return (false, err);

			// record leave payout so the balance reflects it (linked to the settlement entry)
			var balances = await _leave.GetEncashableBalancesAsync(employeeId);
			foreach (var b in balances.Where(x => x.RemainingDays > 0))
				_context.LeaveEncashments.Add(new LeaveEncashment
				{
					CompanyID = companyId, EmployeeID = employeeId, EmployeeName = emp.FullName, LeaveTypeID = b.LeaveTypeId,
					Year = terminationDate.Year, Days = b.RemainingDays, DailyRate = b.DailyRate, Amount = b.Amount,
					EncashDate = terminationDate.Date, JournalEntryId = entry?.ID, CreatedAt = DateTime.UtcNow,
				});

			// terminate the employee + close any active contract
			emp.IsActive = false;
			var contracts = await _context.EmploymentContracts.Where(c => c.CompanyID == companyId && c.EmployeeID == employeeId && c.Status == "Active").ToListAsync();
			foreach (var c in contracts) { c.Status = "Terminated"; if (c.EndDate == null || c.EndDate > terminationDate) c.EndDate = terminationDate.Date; }

			_context.FinalSettlements.Add(new FinalSettlement
			{
				CompanyID = companyId, EmployeeID = employeeId, EmployeeName = emp.FullName, TerminationDate = terminationDate.Date, Reason = reason,
				ServiceYears = pv.ServiceYears, LeaveDays = pv.LeaveDays, LeaveValue = leaveValue, Gratuity = gratuity,
				OtherEarnings = otherEarnings, Deductions = deductions, NetSettlement = net, JournalEntryId = entry?.ID, CreatedAt = DateTime.UtcNow,
			});
			await _context.SaveChangesAsync();
			return (true, null);
		}
	}
}
