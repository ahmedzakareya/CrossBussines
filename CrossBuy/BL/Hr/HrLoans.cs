using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR4 — EMPLOYEE LOANS AND SALARY ADVANCES (2026-11-08).
	//
	// A loan is paid out by a journal entry (Dr Employee loans & advances / Cr the cash or bank it was paid from) and repaid
	// by PAYROLL: every posted payroll credits the same receivable with what it deducted, and writes one deduction row per
	// loan. Outstanding is therefore always Principal − posted deductions, never a stored balance that can drift.
	//
	// THE LEGAL CAP is the country rule pack's LoanCapPct of the wage (Kuwait Art. 59: 10%) — a setting. It is enforced
	// twice: when a loan is granted (the new instalment plus the instalments already running must fit, otherwise the
	// refusal proposes the largest instalment that fits and the months it takes), and when payroll deducts (the month's
	// deductions are clipped to the cap and to the net pay, oldest loan first; what is not taken stays outstanding).
	// ==========================================================================================
	public sealed record LoanRow(EmployeeLoan Loan, decimal Paid)
	{
		public decimal Outstanding => Math.Max(0, Loan.Principal - Paid);
	}
	public sealed record LoanCap(decimal Pct, string? Reference, bool OnBasic);

	public sealed class HrLoanService
	{
		public const string RuleComponent = "EmployeeLoans";
		public const string SourceType = "EmployeeLoan";
		private readonly CrossDbContext _db;
		public HrLoanService(CrossDbContext db) { _db = db; }

		public async Task<LoanCap> CapAsync(int companyId, CancellationToken ct = default)
		{
			var pack = (await new HrRulePackService(_db).EffectiveAsync(companyId, ct))?.Rules;
			return new LoanCap(pack?.LoanCapPct ?? 0, pack?.LoanReference, pack != null && HrRules.IsBasic(pack));
		}

		public async Task<int?> LoanAccountAsync(int companyId, CancellationToken ct = default)
			=> await _db.PostingRules.AsNoTracking().Where(r => r.CompanyID == companyId && r.SourceType == "Payroll" && r.ComponentCode == RuleComponent)
				.Select(r => r.CreditAccountId ?? r.DebitAccountId).FirstOrDefaultAsync(ct);

		/// The company's loans with what has been repaid, newest first (optionally one employee's).
		public async Task<List<LoanRow>> ListAsync(int companyId, int? employeeId = null, CancellationToken ct = default)
		{
			var loans = await _db.EmployeeLoans.AsNoTracking().Where(l => l.CompanyID == companyId && (employeeId == null || l.EmployeeID == employeeId))
				.OrderByDescending(l => l.ID).ToListAsync(ct);
			var ids = loans.Select(l => l.ID).ToList();
			var paid = (await _db.EmployeeLoanDeductions.AsNoTracking().Where(d => ids.Contains(d.LoanID)).Select(d => new { d.LoanID, d.Amount }).ToListAsync(ct))
				.GroupBy(d => d.LoanID).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
			return loans.Select(l => new LoanRow(l, paid.TryGetValue(l.ID, out var p) ? p : 0)).ToList();
		}

		/// Grants a loan and posts its payment. Every refusal is explained (Ar, En).
		public async Task<(bool Ok, string? Ar, string? En)> CreateAsync(int companyId, int employeeId, string kind, decimal principal, decimal installment,
			int firstYear, int firstMonth, DateTime disbursedOn, int paidFromAccountId, string? reason, int? byEmployeeId, int? userId,
			IJournalEntryService journals, CancellationToken ct = default)
		{
			kind = kind == "Advance" ? "Advance" : "Loan";
			if (principal <= 0 || installment <= 0) return (false, "المبلغ والقسط يجب أن يكونا أكبر من صفر", "The amount and the instalment must be above zero");
			if (installment > principal) return (false, "القسط أكبر من المبلغ نفسه", "The instalment is larger than the amount itself");
			if (firstMonth is < 1 or > 12 || firstYear is < 2000 or > 2100) return (false, "شهر بدء الخصم غير صحيح", "The first deduction month is not valid");
			var emp = await _db.Employee.AsNoTracking().Where(e => e.ID == employeeId && e.EmpCompanyID == companyId && e.IsActive).Select(e => new { e.ID }).FirstOrDefaultAsync(ct);
			if (emp == null) return (false, "الموظف غير موجود أو غير نشط", "The employee was not found or is inactive");

			var loanAcc = await LoanAccountAsync(companyId, ct);
			if (loanAcc == null) return (false, "قاعدة الترحيل (الرواتب / سلف الموظفين) غير معرفة لهذه الشركة", "The posting rule Payroll / EmployeeLoans is not defined for this company");
			if (paidFromAccountId == loanAcc) return (false, "حساب الصرف غير صحيح", "Invalid payment account");
			var payFromOk = await _db.Accounts.AsNoTracking().AnyAsync(a => a.ID == paidFromAccountId && a.CompanyID == companyId && a.IsPostable && a.IsActive, ct);
			if (!payFromOk) return (false, "اختر حساب الخزنة أو البنك الذي يُصرف منه", "Choose the cash or bank account it is paid from");

			// THE CAP: this instalment plus every instalment still running must fit inside the cap of the wage.
			var cap = await CapAsync(companyId, ct);
			if (cap.Pct > 0)
			{
				var pay = await new HrSalaryService(_db).ForAsync(employeeId, disbursedOn, ct);
				var wage = cap.OnBasic ? pay.Basic : pay.Total;
				if (wage <= 0) return (false, "لا يوجد راتب مسجل لهذا الموظف، فلا يمكن حساب سقف الاستقطاع", "No salary is recorded for this employee, so the deduction cap cannot be computed");
				var running = (await ListAsync(companyId, employeeId, ct)).Where(r => r.Loan.Status == "Active" && r.Outstanding > 0).Sum(r => Math.Min(r.Loan.Installment, r.Outstanding));
				var capAmount = Math.Floor(wage * cap.Pct / 100m * 1000m) / 1000m;
				var room = capAmount - running;
				if (installment > room)
				{
					var art = string.IsNullOrWhiteSpace(cap.Reference) ? "" : $" ({cap.Reference})";
					if (room <= 0)
						return (false, $"أقساط هذا الموظف الحالية ({running:0.###}) تستنفد سقف الاستقطاع {cap.Pct:0.##}% من الأجر{art} = {capAmount:0.###}. لا يمكن إضافة قسط جديد حتى يُسدَّد ما عليه.",
							$"This employee's running instalments ({running:0.###}) already use the {cap.Pct:0.##}% deduction cap{art} = {capAmount:0.###}. No new instalment fits until they are repaid.");
					var months = (int)Math.Ceiling(principal / room);
					return (false, $"القسط {installment:0.###} يتجاوز سقف الاستقطاع {cap.Pct:0.##}% من الأجر{art}: المتاح {room:0.###} شهرياً. المقترح: قسط {room:0.###} لمدة {months} شهراً.",
						$"The instalment {installment:0.###} exceeds the {cap.Pct:0.##}% deduction cap{art}: {room:0.###} a month is available. Proposed: {room:0.###} a month for {months} months.");
				}
			}

			var loan = new EmployeeLoan
			{
				CompanyID = companyId, EmployeeID = employeeId, Kind = kind, Principal = principal, Installment = installment,
				FirstYear = firstYear, FirstMonth = firstMonth, Status = "Active", Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
				DisbursedOn = disbursedOn.Date, PaidFromAccountId = paidFromAccountId, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = byEmployeeId,
			};
			_db.EmployeeLoans.Add(loan);
			await _db.SaveChangesAsync(ct);

			var name = await _db.Employee.AsNoTracking().Where(e => e.ID == employeeId).Select(e => e.FullName).FirstOrDefaultAsync(ct);
			var (ok, err, entry) = await journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = disbursedOn.Date, JournalType = "Auto", SourceType = SourceType, SourceId = loan.ID,
				Description = $"{(kind == "Advance" ? "سلفة راتب" : "قرض موظف")} — {name}", DescriptionEn = $"{(kind == "Advance" ? "Salary advance" : "Employee loan")} — {name}",
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = loanAcc.Value, Debit = principal, Credit = 0, Description = "سلف وقروض الموظفين" },
					new() { AccountId = paidFromAccountId, Debit = 0, Credit = principal, Description = "Paid from cash/bank" },
				},
			}, userId);
			if (!ok)
			{
				_db.EmployeeLoans.Remove(loan);   // nothing was paid out, so the loan does not exist
				await _db.SaveChangesAsync(ct);
				return (false, $"تعذّر ترحيل قيد الصرف: {err}", $"The payment entry could not be posted: {err}");
			}
			loan.DisbursementEntryId = entry!.ID;
			await _db.SaveChangesAsync(ct);
			return (true, null, null);
		}

		/// The loans payroll may deduct from in (year, month): active, started, not yet deducted in that month, with what is left.
		public async Task<Dictionary<int, List<LoanRow>>> DueAsync(int companyId, int year, int month, CancellationToken ct = default)
		{
			var period = year * 12 + month;
			var rows = (await ListAsync(companyId, null, ct))
				.Where(r => r.Loan.Status == "Active" && r.Outstanding > 0 && r.Loan.FirstYear * 12 + r.Loan.FirstMonth <= period).ToList();
			var ids = rows.Select(r => r.Loan.ID).ToList();
			var done = (await _db.EmployeeLoanDeductions.AsNoTracking().Where(d => ids.Contains(d.LoanID) && d.Year == year && d.Month == month)
				.Select(d => d.LoanID).ToListAsync(ct)).ToHashSet();
			return rows.Where(r => !done.Contains(r.Loan.ID)).GroupBy(r => r.Loan.EmployeeID)
				.ToDictionary(g => g.Key, g => g.OrderBy(r => r.Loan.ID).ToList());
		}

		/// This month's deductions for one employee: each loan's instalment (or what is left), oldest first, clipped to the cap
		/// and to the net pay available. Pure, so the clipping is testable.
		public static List<(int LoanId, decimal Amount)> Allocate(IEnumerable<LoanRow> due, decimal capAmount, decimal netAvailable)
		{
			var room = Math.Max(0, Math.Min(capAmount, netAvailable));
			var result = new List<(int, decimal)>();
			foreach (var r in due)
			{
				if (room <= 0) break;
				var take = Math.Min(Math.Min(r.Loan.Installment, r.Outstanding), room);
				take = Math.Round(take, 2, MidpointRounding.ToZero);
				if (take <= 0) continue;
				result.Add((r.Loan.ID, take));
				room -= take;
			}
			return result;
		}

		/// Writes the posted payroll's deductions and settles the loans they finish. Called inside the payroll posting.
		public async Task RecordAsync(int companyId, int year, int month, IEnumerable<(int LoanId, decimal Amount)> taken, int journalEntryId, CancellationToken ct = default)
		{
			var list = taken.ToList();
			if (list.Count == 0) return;
			foreach (var (loanId, amount) in list)
				_db.EmployeeLoanDeductions.Add(new EmployeeLoanDeduction { CompanyID = companyId, LoanID = loanId, Year = year, Month = month, Amount = amount, JournalEntryId = journalEntryId, CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync(ct);
			var ids = list.Select(x => x.LoanId).Distinct().ToList();
			foreach (var row in (await ListAsync(companyId, null, ct)).Where(r => ids.Contains(r.Loan.ID) && r.Outstanding <= 0))
			{
				var loan = await _db.EmployeeLoans.FirstAsync(l => l.ID == row.Loan.ID, ct);
				loan.Status = "Settled";
			}
			await _db.SaveChangesAsync(ct);
		}
	}
}
