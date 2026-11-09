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

		// ---- the country rule pack's gratuity (2026-11-08) — present when a pack is installed ----
		public string? RulePackNameAr { get; set; }
		public string? RulePackNameEn { get; set; }
		public decimal MonthlyWage { get; set; }
		public bool WageIsBasic { get; set; }   // the pack computes on the basic wage (not basic + allowances)
		public CrossBuy.BL.Hr.HrGratuityResult? ByLaw { get; set; }
		public CrossBuy.BL.Hr.HrGratuityRule? GratuityRule { get; set; }
		public string? Currency { get; set; }
		/// HR4: what the employee still owes on loans and advances — recovered from the settlement automatically.
		public decimal OutstandingLoans { get; set; }
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

		// The constructor DI picks (the greediest): with the workflow, a leaver's pending approvals move up (HrApprovalHandover).
		private readonly ILeaveWorkflowService? _workflow;
		public FinalSettlementService(CrossDbContext context, IJournalEntryService journals, ILeaveAccrualService leave, ILeaveWorkflowService workflow)
			: this(context, journals, leave) { _workflow = workflow; }

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
			// A preview is a screen, so the name follows the UI language.
			var pv = new SettlementPreview
			{
				EmployeeId = employeeId,
				EmployeeName = emp is null ? null : CrossBuy.BL.EmployeeNames.Of(emp),
			};
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

			// THE COUNTRY RULE PACK (owner ruling 2026-11-08: every legal figure is a setting per country). When the company
			// runs on a pack, the suggestion is the statutory gratuity on termination by the employer — tiers and cap from
			// the pack — and the resignation figure is shown beside it. The amount stays editable at posting, as before.
			// The monthly wage is the same total LeaveAccrualService.DailyRateAsync divides by 30, read exactly here so the
			// gratuity carries no rounding from the daily figure.
			var eff = await new CrossBuy.BL.Hr.HrRulePackService(_context).EffectiveAsync(companyId);
			if (eff is { } pack)
			{
				// HR4: the last pay in force (the employee's own salary, else the policy's) on the termination date
				var pay = await new CrossBuy.BL.Hr.HrSalaryService(_context).ForAsync(employeeId, terminationDate);
				pv.MonthlyWage = CrossBuy.BL.Hr.HrRules.IsBasic(pack.Rules) ? pay.Basic : pay.Total;   // the pack's wage basis
				pv.WageIsBasic = CrossBuy.BL.Hr.HrRules.IsBasic(pack.Rules);
				pv.ByLaw = CrossBuy.BL.Hr.HrGratuity.Compute(pack.Rules, (decimal)Math.Max(0, years), pv.MonthlyWage);
				pv.GratuityRule = pack.Rules.Gratuity;
				pv.RulePackNameAr = pack.Pack.NameAr; pv.RulePackNameEn = pack.Pack.NameEn; pv.Currency = pack.Rules.Currency;
				if (pv.MonthlyWage > 0) pv.SuggestedGratuity = R(pv.ByLaw.OnTermination);
			}
			pv.OutstandingLoans = (await new CrossBuy.BL.Hr.HrLoanService(_context).ListAsync(companyId, employeeId))
				.Where(r => r.Loan.Status == "Active").Sum(r => r.Outstanding);
			return pv;
		}

		public async Task<(bool ok, string? error)> PostAsync(int companyId, int employeeId, DateTime terminationDate, string? reason,
			decimal gratuity, decimal otherEarnings, decimal deductions, int payFromGlAccountId, int? userId)
		{
			var emp = await _context.Employee.FirstOrDefaultAsync(e => e.ID == employeeId && e.EmpCompanyID == companyId);
			if (emp == null) return (false, "Employee not found");
			if (!emp.IsActive) return (false, "The employee has already been terminated");
			if (gratuity < 0 || otherEarnings < 0 || deductions < 0) return (false, "Values cannot be negative");

			var pv = await PreviewAsync(companyId, employeeId, terminationDate);
			var leaveValue = pv.LeaveValue;
			var gross = R(leaveValue + gratuity + otherEarnings);
			// HR4: outstanding loans are recovered from what is left after the other deductions (the rest stays owed)
			var loanRows = (await new CrossBuy.BL.Hr.HrLoanService(_context).ListAsync(companyId, employeeId)).Where(r => r.Loan.Status == "Active" && r.Outstanding > 0).OrderBy(r => r.Loan.ID).ToList();
			var loanRecovery = R(Math.Max(0, Math.Min(loanRows.Sum(r => r.Outstanding), gross - deductions)));
			var loanAcc = loanRecovery > 0 ? await new CrossBuy.BL.Hr.HrLoanService(_context).LoanAccountAsync(companyId) : null;
			if (loanRecovery > 0 && loanAcc == null) return (false, "The posting rule Payroll / EmployeeLoans is not defined, so the outstanding loans cannot be recovered.");
			var net = R(gross - deductions - loanRecovery);
			if (net < 0) return (false, "The deductions exceed the total entitlements");

			var leaveAcc = await AccIdAsync(companyId, "520104");
			var gratuityAcc = await AccIdAsync(companyId, "520107");
			var salaryAcc = await AccIdAsync(companyId, "520101");
			if (leaveAcc == null || gratuityAcc == null || salaryAcc == null)
				return (false, "The settlement accounts (520104/520107/520101) do not exist. Run the accounting seed.");

			var ccId = await ResolveCostCenterAsync(companyId, emp.DepartmentID);   // 520101 requires a cost center

			var lines = new List<JournalLineInput>();
			if (leaveValue > 0) lines.Add(new() { AccountId = leaveAcc.Value, Debit = leaveValue, Credit = 0, Description = "Leave balance allowance" });
			if (gratuity > 0) lines.Add(new() { AccountId = gratuityAcc.Value, Debit = gratuity, Credit = 0, Description = "End-of-service gratuity" });
			if (otherEarnings > 0) lines.Add(new() { AccountId = salaryAcc.Value, Debit = otherEarnings, Credit = 0, CostCenterId = ccId, Description = "Other entitlements" });
			if (net > 0) lines.Add(new() { AccountId = payFromGlAccountId, Debit = 0, Credit = net, Description = "Net settlement paid" });
			// deductions recovered against salary expense (keeps the entry balanced without a new control account)
			if (deductions > 0) lines.Add(new() { AccountId = salaryAcc.Value, Debit = 0, Credit = deductions, CostCenterId = ccId, Description = "End-of-service deductions" });
			if (loanRecovery > 0) lines.Add(new() { AccountId = loanAcc!.Value, Debit = 0, Credit = loanRecovery, Description = "Outstanding employee loans recovered" });

			if (lines.Sum(l => l.Debit) <= 0) return (false, "There are no entitlements to post");

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = terminationDate.Date, JournalType = "Auto", SourceType = "FinalSettlement", SourceId = employeeId,
				// The English description carried the ARABIC name. EnglishOf, not Of: this text is stored and
				// read by everyone later, so it must not depend on who pressed the button.
				Description = $"تسوية نهاية خدمة — {emp.FullName}",
				DescriptionEn = $"Final settlement — {CrossBuy.BL.EmployeeNames.EnglishOf(emp.FullName, emp.FullNameEn)}",
				Lines = lines,
			}, userId);
			if (!ok) return (false, err);

			// HR4: the loans the settlement repaid, oldest first; a loan it finishes is Settled
			if (loanRecovery > 0)
			{
				var left = loanRecovery; var y = terminationDate.Year; var m = terminationDate.Month;
				foreach (var r in loanRows)
				{
					if (left <= 0) break;
					var take = Math.Min(r.Outstanding, left); left -= take;
					var existing = await _context.EmployeeLoanDeductions.FirstOrDefaultAsync(d => d.LoanID == r.Loan.ID && d.Year == y && d.Month == m);
					if (existing != null) existing.Amount += take;   // the month's payroll already took an instalment: one row per loan per month
					else _context.EmployeeLoanDeductions.Add(new EmployeeLoanDeduction { CompanyID = companyId, LoanID = r.Loan.ID, Year = y, Month = m, Amount = take, JournalEntryId = entry?.ID, CreatedAt = DateTime.UtcNow });
					if (take >= r.Outstanding) { var l = await _context.EmployeeLoans.FirstAsync(x => x.ID == r.Loan.ID); l.Status = "Settled"; }
				}
			}

			// record leave payout so the balance reflects it (linked to the settlement entry)
			var balances = await _leave.GetEncashableBalancesAsync(employeeId);
			foreach (var b in balances.Where(x => x.RemainingDays > 0))
				_context.LeaveEncashments.Add(new LeaveEncashment
				{
					CompanyID = companyId, EmployeeID = employeeId, EmployeeName = emp.FullName, LeaveTypeID = b.LeaveTypeId,
					Year = terminationDate.Year, Days = b.RemainingDays, DailyRate = b.DailyRate, Amount = b.Amount,
					EncashDate = terminationDate.Date, JournalEntryId = entry?.ID, CreatedAt = DateTime.UtcNow,
				});

			// an approver who leaves hands their pending approvals to the next active manager above them (staged; saved below)
			if (_workflow != null) await CrossBuy.BL.Hr.HrApprovalHandover.StageAsync(_context, _workflow, employeeId);

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
