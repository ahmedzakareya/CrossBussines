using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class PayrollEmployeeLine
	{
		public int EmployeeId { get; set; }
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int? CostCenterId { get; set; }
		public string? CostCenterName { get; set; }
		public decimal BaseSalary { get; set; }
		public decimal Allowances { get; set; }
		public decimal Gross { get; set; }            // base + allowances (statutory base for SI/tax)
		// attendance-driven adjustments (HR-3)
		public int LateMinutes { get; set; }
		public int OvertimeMinutes { get; set; }
		public int AbsentDays { get; set; }
		public decimal OvertimePay { get; set; }
		public decimal LatePenalty { get; set; }
		public decimal AbsencePenalty { get; set; }
		public decimal SiEmployee { get; set; }
		public decimal SiCompany { get; set; }
		public decimal Tax { get; set; }
		public decimal Net { get; set; }
		// HR4: loan/advance instalments taken this month (already subtracted from Net), and which loans they repay
		public decimal LoanDeduction { get; set; }
		public List<(int LoanId, decimal Amount)> LoanParts { get; set; } = new();
		// expense charged to GL = earnings minus penalties (keeps the entry balanced without new accounts)
		public decimal EarningExpense => Math.Round(Gross + OvertimePay - LatePenalty - AbsencePenalty, 2, MidpointRounding.AwayFromZero);
	}

	public class PayrollRunPreview
	{
		public int Year { get; set; }
		public int Month { get; set; }
		public bool AlreadyPosted { get; set; }
		public string? PostedEntryNo { get; set; }
		public List<PayrollEmployeeLine> Employees { get; set; } = new();
		public decimal TotalGross { get; set; }
		public decimal TotalOvertime { get; set; }
		public decimal TotalLatePenalty { get; set; }
		public decimal TotalAbsencePenalty { get; set; }
		public decimal TotalSiEmployee { get; set; }
		public decimal TotalSiCompany { get; set; }
		public decimal TotalTax { get; set; }
		public decimal TotalNet { get; set; }
		public decimal TotalLoanDeduction { get; set; }   // HR4: loan instalments deducted this month
	}

	// HR-4: accrued vs paid view of a payroll period (net to staff + statutory tax/SI to authorities)
	public class PayrollDisbursementView
	{
		public int Year { get; set; }
		public int Month { get; set; }
		public bool HasPayroll { get; set; }
		public decimal Net { get; set; }
		public decimal Tax { get; set; }
		public decimal Si { get; set; }
		public bool Disbursed { get; set; }
		public bool TaxRemitted { get; set; }
		public bool SiRemitted { get; set; }
	}

	public interface IAccountingPostingService
	{
		Task<PayrollRunPreview> PreviewPayrollAsync(int companyId, int year, int month);
		Task<(bool ok, string? error, int? entryId)> PostPayrollRunAsync(int companyId, int year, int month, int? userId);
		Task<PayrollDisbursementView> GetDisbursementViewAsync(int companyId, int year, int month);
		Task<(bool ok, string? error)> DisbursePayrollAsync(int companyId, int year, int month, int payFromGlAccountId, DateTime date, int? userId);
		Task<(bool ok, string? error)> RemitStatutoryAsync(int companyId, int year, int month, string component, int payFromGlAccountId, DateTime date, int? userId);
	}

	/// The bridge between HR/Payroll and the GL. Computes the monthly payroll from each employee's
	/// salary policy, then builds & posts the payroll journal via the posting engine using PostingRules.
	public class AccountingPostingService : IAccountingPostingService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		private readonly IAttendanceService _attendance;
		public AccountingPostingService(CrossDbContext context, IJournalEntryService journals, IAttendanceService attendance)
		{
			_context = context; _journals = journals; _attendance = attendance;
		}

		private static int SourceIdFor(int year, int month) => year * 100 + month;
		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		// Clamp the monthly wage to the insurable band before applying SI percentages (0 limit = no bound).
		public static decimal InsurableWage(decimal grossMonthly, Models.Context.Accounting.PayrollSettings? s)
		{
			if (s == null) return grossMonthly;
			var w = grossMonthly;
			if (s.SiMinMonthly > 0 && w < s.SiMinMonthly) w = s.SiMinMonthly;
			if (s.SiMaxMonthly > 0 && w > s.SiMaxMonthly) w = s.SiMaxMonthly;
			return w;
		}

		// Progressive income tax over annual taxable income. Brackets ordered by Ordinal; ToAmount null = open top.
		public static decimal ProgressiveAnnualTax(decimal annualTaxable, IEnumerable<Models.Context.Accounting.PayrollTaxBracket> brackets)
		{
			if (annualTaxable <= 0) return 0m;
			decimal tax = 0m;
			foreach (var b in brackets.OrderBy(x => x.Ordinal))
			{
				if (annualTaxable <= b.FromAmount) break;
				var upper = b.ToAmount ?? annualTaxable;
				var slice = Math.Min(annualTaxable, upper) - b.FromAmount;
				if (slice > 0) tax += slice * b.Rate / 100m;
			}
			return tax;
		}

		public async Task<PayrollRunPreview> PreviewPayrollAsync(int companyId, int year, int month)
		{
			var p = new PayrollRunPreview { Year = year, Month = month };

			// already posted?
			var src = SourceIdFor(year, month);
			var existing = await _context.JournalEntries.AsNoTracking()
				.FirstOrDefaultAsync(e => e.CompanyID == companyId && e.SourceType == "Payroll" && e.SourceId == src);
			if (existing != null) { p.AlreadyPosted = true; p.PostedEntryNo = existing.EntryNo; }

			var employees = await _context.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == companyId && e.IsActive)
				.ToListAsync();
			if (employees.Count == 0) return p;

			var empIds = employees.Select(e => e.ID).ToList();
			var assignments = await _context.PolicyAssignments.AsNoTracking()
				.Where(a => empIds.Contains(a.EmployeeID))
				.ToListAsync();
			var policyByEmp = assignments.GroupBy(a => a.EmployeeID).ToDictionary(g => g.Key, g => g.First().LeavePolicyTypeID);

			var salaryByPolicy = await _context.SalaryPolicies.AsNoTracking().ToListAsync();
			// HR4 (2026-11-08): the AMOUNTS come from each employee's own salary in force at the month's end (falling back
			// to the policy's salary). The policy still supplies the RATES it owns: overtime, penalties, SI shares, tax.
			var payOf = await new CrossBuy.BL.Hr.HrSalaryService(_context).ForManyAsync(empIds, new DateTime(year, month, 1).AddMonths(1).AddDays(-1));
			// HR4: loan instalments due this month, and the legal cap they are clipped to (the rule pack's LoanCapPct)
			var loans = new CrossBuy.BL.Hr.HrLoanService(_context);
			var loansDue = await loans.DueAsync(companyId, year, month);
			var loanCap = await loans.CapAsync(companyId);

			// payroll calc settings + progressive tax brackets (per company). Both optional → graceful legacy fallback.
			var paySettings = await _context.PayrollSettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyID == companyId);
			var taxBrackets = await _context.PayrollTaxBrackets.AsNoTracking()
				.Where(b => b.CompanyID == companyId).OrderBy(b => b.Ordinal).ToListAsync();

			// attendance summary for the month (late/overtime/absence) — only applied to employees actually tracked
			var attSummary = (await _attendance.MonthlySummaryAsync(companyId, year, month)).ToDictionary(r => r.EmployeeID);
			// THE COUNTRY RULE PACK (2026-11-08): when the company runs on one, overtime is paid as the statutory
			// percentage of the hourly wage by day type (working day / rest day / official holiday). Without a pack the
			// legacy flat SalaryPolicies.OvertimeRate per hour stays exactly as it was.
			var rulePack = (await new CrossBuy.BL.Hr.HrRulePackService(_context).EffectiveAsync(companyId))?.Rules;
			// HR5: when the pack requires it, only APPROVED overtime is paid (per employee, per month, by day type)
			var otApproved = rulePack?.Overtime.RequiresApproval == true
				? await new CrossBuy.BL.Hr.HrOvertimeService(_context, _attendance).ApprovedAsync(companyId, year, month) : null;
			// Nationals of the pack's country (for the "nationals" insurance lines): matched by CountriesLookup.CountryName.
			var nationalCountryIds = rulePack?.NationalCountryName is { Length: > 0 } ncn
				? (await _context.Set<CrossBuy.Models.Context.Admin.CountriesLookup>().AsNoTracking().Where(c => c.CountryName == ncn).Select(c => c.ID).ToListAsync()).ToHashSet()
				: new HashSet<int>();
			var mStart = new DateTime(year, month, 1); var mEnd = mStart.AddMonths(1).AddDays(-1);
			var trackedEmp = (await _context.AttendanceRecords.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.WorkDate >= mStart && r.WorkDate <= mEnd)
				.Select(r => r.EmployeeID).Distinct().ToListAsync()).ToHashSet();
			var costCenters = await _context.CostCenters.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.Code).ToListAsync();
			var ccBySource = costCenters.Where(c => c.SourceHierarchicalId != null).ToDictionary(c => c.SourceHierarchicalId!.Value, c => c);
			var defaultCc = costCenters.FirstOrDefault();
			var hier = await _context.Hierarchicals.AsNoTracking().ToListAsync();
			var hierById = hier.ToDictionary(h => h.H_ID);

			int? ResolveCostCenter(int? departmentId)
			{
				// walk up from the employee's department node to the nearest node mapped to a cost center
				var cur = departmentId.HasValue && hierById.ContainsKey(departmentId.Value) ? hierById[departmentId.Value] : null;
				var guard = 0;
				while (cur != null && guard++ < 50)
				{
					if (ccBySource.TryGetValue(cur.H_ID, out var cc)) return cc.ID;
					cur = cur.H_Parent.HasValue && hierById.ContainsKey(cur.H_Parent.Value) ? hierById[cur.H_Parent.Value] : null;
				}
				return defaultCc?.ID;   // fallback to the company's first cost center
			}

			foreach (var e in employees)
			{
				if (!payOf.TryGetValue(e.ID, out var pay) || pay.Total <= 0) continue;
				// an employee paid by their own salary may have no policy: its rates are then simply zero
				var sp = (policyByEmp.TryGetValue(e.ID, out var polId) ? salaryByPolicy.FirstOrDefault(s => s.LeavePolicyTypeID == polId) : null)
					?? new CrossBuy.Models.Context.Admin.SalaryPolicies { PaymentMethod = "" };

				var allowances = R(pay.Allowances);
				var gross = R(pay.Basic + allowances);
				// social insurance on the insurable wage (clamped to the statutory min/max band)
				decimal siEmp, siCo;
				if (rulePack is { Insurance.Count: > 0 } siPack)
				{
					// THE COUNTRY RULE PACK (2026-11-08): each insurance line on the wage up to its own cap, for everyone or
					// for nationals only. Without a pack the salary policy's shares on the settings band apply, as before.
					bool national = e.CountryID is int cid && nationalCountryIds.Contains(cid);
					decimal empSum = 0, coSum = 0;
					foreach (var line in siPack.Insurance.Where(l => !l.ForNationalsOnly || national))
					{
						var wage = line.WageCap is > 0 ? Math.Min(gross, line.WageCap.Value) : gross;
						empSum += wage * line.EmployeePct / 100m; coSum += wage * line.EmployerPct / 100m;
					}
					siEmp = R(empSum); siCo = R(coSum);
				}
				else
				{
					var insurable = InsurableWage(gross, paySettings);
					siEmp = R(insurable * sp.SocialInsuranceEmployeeShare / 100m);
					siCo = R(insurable * sp.SocialInsuranceCompanyShare / 100m);
				}
				// income tax: progressive annual schedule if brackets exist; otherwise legacy flat rate
				decimal tax = 0m;
				if (sp.IsTaxApplicable)
				{
					if (taxBrackets.Count > 0)
					{
						var annualTaxable = gross * 12m
							- ((paySettings?.TaxBaseExcludesEmployeeSI ?? false) ? siEmp * 12m : 0m)
							- (paySettings?.PersonalExemptionAnnual ?? 0m);
						tax = R(ProgressiveAnnualTax(annualTaxable, taxBrackets) / 12m);
					}
					else
					{
						tax = R(gross * sp.TaxRate / 100m);
					}
				}
				var ccId = ResolveCostCenter(e.DepartmentID);

				// attendance adjustments (only if this employee is actually being tracked this month)
				int lateMin = 0, otMin = 0, absDays = 0;
				decimal otPay = 0, latePen = 0, absPen = 0;
				if (trackedEmp.Contains(e.ID) && attSummary.TryGetValue(e.ID, out var att))
				{
					lateMin = att.LateMinutes; absDays = att.AbsentDays;
					// the overtime minutes that are payable: as recorded, or — when approval is required — what was approved
					int otW = att.OvertimeMinutes, otR = att.RestDayOvertimeMinutes, otH = att.HolidayOvertimeMinutes;
					if (otApproved != null)
					{
						var ap = otApproved.TryGetValue(e.ID, out var a) ? a : (0, 0, 0);
						otW = Math.Min(otW, ap.Item1); otR = Math.Min(otR, ap.Item2); otH = Math.Min(otH, ap.Item3);
					}
					otMin = otW;
					if (rulePack is { } rp && rp.WageDaysPerMonth > 0 && rp.HoursPerDay > 0)
					{
						// hourly wage on the wage the pack names (WageBasis: total or basic)
						var wageBase = CrossBuy.BL.Hr.HrRules.IsBasic(rp) ? R(pay.Basic) : gross;   // the pack's wage basis
						var hourly = wageBase / rp.WageDaysPerMonth / rp.HoursPerDay;
						var o = rp.Overtime;
						otPay = R(hourly / 60m * (otW * o.WorkingDayPct + otR * o.RestDayPct + otH * o.HolidayPct) / 100m);
						otMin = otW + otR + otH;
					}
					else otPay = R(otMin / 60m * sp.OvertimeRate);          // legacy: OvertimeRate = amount per overtime hour
					latePen = R(lateMin * sp.LatePenaltyPerMinute);
					absPen = R(absDays * sp.AbsencePenaltyPerDay);
				}
				var net = R(gross + otPay - siEmp - tax - latePen - absPen);
				// HR4: loan instalments, oldest loan first, within the cap of the wage and never past the net pay
				var loanParts = new List<(int LoanId, decimal Amount)>();
				if (loansDue.TryGetValue(e.ID, out var dueLoans))
				{
					var capBase = loanCap.OnBasic ? pay.Basic : gross;
					var capAmount = loanCap.Pct > 0 ? capBase * loanCap.Pct / 100m : decimal.MaxValue;
					loanParts = CrossBuy.BL.Hr.HrLoanService.Allocate(dueLoans, capAmount, net);
				}
				var loanDed = R(loanParts.Sum(x => x.Amount));
				net = R(net - loanDed);

				p.Employees.Add(new PayrollEmployeeLine
				{
					EmployeeId = e.ID, Name = e.FullName, NameEn = e.FullNameEn ?? e.FullName,
					CostCenterId = ccId, CostCenterName = ccId.HasValue ? costCenters.FirstOrDefault(c => c.ID == ccId)?.Name : null,
					BaseSalary = R(pay.Basic), Allowances = allowances, Gross = gross,
					LateMinutes = lateMin, OvertimeMinutes = otMin, AbsentDays = absDays,
					OvertimePay = otPay, LatePenalty = latePen, AbsencePenalty = absPen,
					SiEmployee = siEmp, SiCompany = siCo, Tax = tax, Net = net,
					LoanDeduction = loanDed, LoanParts = loanParts,
				});
			}

			p.TotalGross = R(p.Employees.Sum(x => x.Gross));
			p.TotalOvertime = R(p.Employees.Sum(x => x.OvertimePay));
			p.TotalLatePenalty = R(p.Employees.Sum(x => x.LatePenalty));
			p.TotalAbsencePenalty = R(p.Employees.Sum(x => x.AbsencePenalty));
			p.TotalSiEmployee = R(p.Employees.Sum(x => x.SiEmployee));
			p.TotalSiCompany = R(p.Employees.Sum(x => x.SiCompany));
			p.TotalTax = R(p.Employees.Sum(x => x.Tax));
			p.TotalNet = R(p.Employees.Sum(x => x.Net));
			p.TotalLoanDeduction = R(p.Employees.Sum(x => x.LoanDeduction));
			return p;
		}

		public async Task<(bool ok, string? error, int? entryId)> PostPayrollRunAsync(int companyId, int year, int month, int? userId)
		{
			var pre = await PreviewPayrollAsync(companyId, year, month);
			if (pre.AlreadyPosted) return (false, $"Payroll for this period has already been posted (entry {pre.PostedEntryNo})", null);
			if (pre.Employees.Count == 0 || pre.TotalGross <= 0) return (false, "There is no postable payroll in this period", null);

			// posting rules → account ids
			var rules = await _context.PostingRules.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.SourceType == "Payroll").ToListAsync();
			int? Dr(string code) => rules.FirstOrDefault(r => r.ComponentCode == code)?.DebitAccountId;
			int? Cr(string code) => rules.FirstOrDefault(r => r.ComponentCode == code)?.CreditAccountId;

			var salaryAcc = Dr("BasicSalary");
			var siExpAcc = Dr("SocialInsCompany");
			var netAcc = Cr("NetPay");
			var taxAcc = Cr("IncomeTax");
			var siPayAcc = Cr("SocialInsPayable");
			if (salaryAcc == null || netAcc == null)
				return (false, "The posting rules are incomplete (payroll/accrual). Run the accounting seed.", null);
			// HR4: loan deductions credit the employee-loans receivable they repay
			var loanAcc = Cr(CrossBuy.BL.Hr.HrLoanService.RuleComponent) ?? Dr(CrossBuy.BL.Hr.HrLoanService.RuleComponent);
			if (pre.TotalLoanDeduction > 0 && loanAcc == null)
				return (false, "The posting rule Payroll / EmployeeLoans is not defined, so the loan deductions cannot be posted.", null);

			var lines = new List<JournalLineInput>();

			// Dr salaries expense (earnings + overtime − late/absence penalties), grouped by cost center
			foreach (var g in pre.Employees.GroupBy(x => x.CostCenterId))
			{
				var amt = R(g.Sum(x => x.EarningExpense));
				if (amt > 0) lines.Add(new JournalLineInput { AccountId = salaryAcc.Value, Debit = amt, Credit = 0, CostCenterId = g.Key, Description = "Salaries and wages (including overtime and attendance deductions)" });
			}
			// Dr employer social insurance, grouped by cost center
			if (pre.TotalSiCompany > 0 && siExpAcc != null)
				foreach (var g in pre.Employees.GroupBy(x => x.CostCenterId))
				{
					var amt = R(g.Sum(x => x.SiCompany));
					if (amt > 0) lines.Add(new JournalLineInput { AccountId = siExpAcc.Value, Debit = amt, Credit = 0, CostCenterId = g.Key, Description = "Social insurance - employer share" });
				}

			// Credits
			lines.Add(new JournalLineInput { AccountId = netAcc.Value, Debit = 0, Credit = pre.TotalNet, Description = "Net salaries payable" });
			if (pre.TotalTax > 0 && taxAcc != null)
				lines.Add(new JournalLineInput { AccountId = taxAcc.Value, Debit = 0, Credit = pre.TotalTax, Description = "Payroll tax payable" });
			var siTotal = R(pre.TotalSiEmployee + pre.TotalSiCompany);
			if (siTotal > 0 && siPayAcc != null)
				lines.Add(new JournalLineInput { AccountId = siPayAcc.Value, Debit = 0, Credit = siTotal, Description = "Social insurance payable" });
			if (pre.TotalLoanDeduction > 0)
				lines.Add(new JournalLineInput { AccountId = loanAcc!.Value, Debit = 0, Credit = pre.TotalLoanDeduction, Description = "Employee loan instalments deducted" });

			var input = new JournalEntryInput
			{
				CompanyID = companyId,
				EntryDate = new DateTime(year, month, DateTime.DaysInMonth(year, month)),
				JournalType = "Auto",
				SourceType = "Payroll",
				SourceId = SourceIdFor(year, month),
				Description = $"قيد رواتب شهر {month}/{year}",
				DescriptionEn = $"Payroll {month}/{year}",
				Lines = lines,
			};

			var (ok, err, entry) = await _journals.CreateAndPostAsync(input, userId);
			if (!ok) return (false, err, null);

			// persist payslips (one per employee) — snapshot incl. attendance adjustments
			await _context.Payslips.Where(s => s.CompanyID == companyId && s.Year == year && s.Month == month).ExecuteDeleteAsync();
			foreach (var x in pre.Employees)
				_context.Payslips.Add(new Payslip
				{
					CompanyID = companyId, Year = year, Month = month, EmployeeID = x.EmployeeId, EmployeeName = x.Name, CostCenterId = x.CostCenterId,
					BaseSalary = x.BaseSalary, Allowances = x.Allowances, OvertimePay = x.OvertimePay, GrossEarnings = R(x.Gross + x.OvertimePay),
					LateMinutes = x.LateMinutes, LatePenalty = x.LatePenalty, AbsentDays = x.AbsentDays, AbsencePenalty = x.AbsencePenalty,
					SiEmployee = x.SiEmployee, SiCompany = x.SiCompany, Tax = x.Tax, Net = x.Net, LoanDeduction = x.LoanDeduction,
					JournalEntryId = entry!.ID, CreatedAt = DateTime.UtcNow,
				});
			await _context.SaveChangesAsync();
			// HR4: the deductions this posted payroll took, one row per loan; a loan they finish becomes Settled
			await new CrossBuy.BL.Hr.HrLoanService(_context).RecordAsync(companyId, year, month, pre.Employees.SelectMany(x => x.LoanParts), entry!.ID);
			return (true, null, entry!.ID);
		}

		// ---------------- HR-4: payroll disbursement & statutory remittance ----------------
		// resolve the payable control accounts from the Payroll posting rules
		private async Task<(int? net, int? tax, int? si)> PayrollLiabilityAccountsAsync(int companyId)
		{
			var rules = await _context.PostingRules.AsNoTracking()
				.Where(r => r.CompanyID == companyId && r.SourceType == "Payroll").ToListAsync();
			int? Cr(string c) => rules.FirstOrDefault(r => r.ComponentCode == c)?.CreditAccountId;
			return (Cr("NetPay"), Cr("IncomeTax"), Cr("SocialInsPayable"));
		}

		// sum the credits posted to a given account by the period's payroll entry (the amount actually accrued)
		private async Task<decimal> AccruedAsync(int companyId, int periodSrc, int? accId)
		{
			if (accId == null) return 0m;
			var je = await _context.JournalEntries.AsNoTracking()
				.FirstOrDefaultAsync(e => e.CompanyID == companyId && e.SourceType == "Payroll" && e.SourceId == periodSrc && e.Status == "Posted");
			if (je == null) return 0m;
			return R(await _context.JournalEntryLines.AsNoTracking()
				.Where(l => l.JournalEntryId == je.ID && l.AccountId == accId.Value).SumAsync(l => l.Credit));
		}

		private async Task<bool> SettlementExistsAsync(int companyId, string sourceType, int periodSrc) =>
			await _context.JournalEntries.AsNoTracking()
				.AnyAsync(e => e.CompanyID == companyId && e.SourceType == sourceType && e.SourceId == periodSrc && e.Status == "Posted");

		public async Task<PayrollDisbursementView> GetDisbursementViewAsync(int companyId, int year, int month)
		{
			var src = SourceIdFor(year, month);
			var v = new PayrollDisbursementView { Year = year, Month = month };
			var je = await _context.JournalEntries.AsNoTracking()
				.FirstOrDefaultAsync(e => e.CompanyID == companyId && e.SourceType == "Payroll" && e.SourceId == src && e.Status == "Posted");
			if (je == null) return v;
			v.HasPayroll = true;
			var (net, tax, si) = await PayrollLiabilityAccountsAsync(companyId);
			v.Net = await AccruedAsync(companyId, src, net);
			v.Tax = await AccruedAsync(companyId, src, tax);
			v.Si = await AccruedAsync(companyId, src, si);
			v.Disbursed = await SettlementExistsAsync(companyId, "PayrollPay", src);
			v.TaxRemitted = await SettlementExistsAsync(companyId, "PayrollRemitTax", src);
			v.SiRemitted = await SettlementExistsAsync(companyId, "PayrollRemitSI", src);
			return v;
		}

		public async Task<(bool ok, string? error)> DisbursePayrollAsync(int companyId, int year, int month, int payFromGlAccountId, DateTime date, int? userId)
		{
			var src = SourceIdFor(year, month);
			if (await SettlementExistsAsync(companyId, "PayrollPay", src)) return (false, "Payroll for this period has already been paid");
			var (netAcc, _, _) = await PayrollLiabilityAccountsAsync(companyId);
			var net = await AccruedAsync(companyId, src, netAcc);
			if (netAcc == null) return (false, "The salaries-payable account is not defined in the posting rules");
			if (net <= 0) return (false, "There is no net payroll payable for this period (has the payroll entry been posted?)");
			if (payFromGlAccountId == netAcc.Value) return (false, "Invalid payment account");
			var (ok, err, _) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date.Date, JournalType = "Auto", SourceType = "PayrollPay", SourceId = src,
				Description = $"صرف صافي رواتب شهر {month}/{year}", DescriptionEn = $"Payroll disbursement {month}/{year}",
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = netAcc.Value, Debit = net, Credit = 0, Description = "تصفية الرواتب المستحقة" },
					new() { AccountId = payFromGlAccountId, Debit = 0, Credit = net, Description = "Payment from bank/cash" },
				},
			}, userId);
			return (ok, err);
		}

		public async Task<(bool ok, string? error)> RemitStatutoryAsync(int companyId, int year, int month, string component, int payFromGlAccountId, DateTime date, int? userId)
		{
			var src = SourceIdFor(year, month);
			var (_, taxAcc, siAcc) = await PayrollLiabilityAccountsAsync(companyId);
			bool isTax = string.Equals(component, "Tax", StringComparison.OrdinalIgnoreCase);
			var sourceType = isTax ? "PayrollRemitTax" : "PayrollRemitSI";
			var liabAcc = isTax ? taxAcc : siAcc;
			var label = isTax ? "ضريبة كسب العمل" : "التأمينات الاجتماعية";
			if (await SettlementExistsAsync(companyId, sourceType, src)) return (false, $"{label} for this period has already been remitted");
			if (liabAcc == null) return (false, "The liability account is not defined in the posting rules");
			var amount = await AccruedAsync(companyId, src, liabAcc);
			if (amount <= 0) return (false, $"There is no {label} payable for this period");
			if (payFromGlAccountId == liabAcc.Value) return (false, "Invalid payment account");
			var (ok, err, _) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date.Date, JournalType = "Auto", SourceType = sourceType, SourceId = src,
				Description = $"توريد {label} عن شهر {month}/{year}", DescriptionEn = $"Statutory remittance ({component}) {month}/{year}",
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = liabAcc.Value, Debit = amount, Credit = 0, Description = $"تصفية {label} المستحقة" },
					new() { AccountId = payFromGlAccountId, Debit = 0, Credit = amount, Description = "Settlement from bank/cash" },
				},
			}, userId);
			return (ok, err);
		}
	}
}
