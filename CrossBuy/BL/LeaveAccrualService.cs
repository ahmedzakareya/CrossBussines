using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class EncashableBalance
	{
		public int LeaveTypeId { get; set; }
		public string? NameAr { get; set; }
		public string? NameEn { get; set; }
		public int RemainingDays { get; set; }
		public decimal DailyRate { get; set; }
		public decimal Amount => Math.Round(RemainingDays * DailyRate, 2, MidpointRounding.AwayFromZero);
	}

	public class ProvisionPreview
	{
		public DateTime AsOf { get; set; }
		public decimal TotalDays { get; set; }
		public decimal TargetAmount { get; set; }   // liability the provision SHOULD hold
		public decimal CurrentBalance { get; set; }  // what 210206 holds now
		public decimal Adjustment => Math.Round(TargetAmount - CurrentBalance, 2, MidpointRounding.AwayFromZero);
		public int Employees { get; set; }
	}

	public class CarryOverRow
	{
		public int EmployeeId { get; set; }
		public string? EmployeeName { get; set; }
		public int LeaveTypeId { get; set; }
		public string? LeaveTypeName { get; set; }
		public int Remaining { get; set; }    // unused balance at end of the source year
		public int Limit { get; set; }         // CarryOverLimit
		public int CarryDays { get; set; }     // min(max(0,Remaining), Limit)
	}

	public interface ILeaveAccrualService
	{
		Task<decimal> DailyRateAsync(int employeeId);
		Task<List<EncashableBalance>> GetEncashableBalancesAsync(int employeeId);
		Task<(bool ok, string? error)> EncashAsync(int companyId, int employeeId, int leaveTypeId, int days, int payFromGlAccountId, DateTime date, int? userId);
		Task<ProvisionPreview> ProvisionPreviewAsync(int companyId, DateTime asOf);
		Task<(bool ok, string? error)> PostProvisionAsync(int companyId, DateTime asOf, int? userId);
		Task<List<CarryOverRow>> CarryOverPreviewAsync(int companyId, int fromYear);
		Task<(bool ok, string? error, int rows)> RunCarryOverAsync(int companyId, int fromYear);
	}

	// HR-2f: leave encashment (cash payout of unused days) + leave provision (period-end liability for earned-untaken leave).
	public class LeaveAccrualService : ILeaveAccrualService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		private readonly ILeaveDashboardService _leave;
		public LeaveAccrualService(CrossDbContext context, IJournalEntryService journals, ILeaveDashboardService leave)
		{ _context = context; _journals = journals; _leave = leave; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		private async Task<int?> AccIdAsync(int companyId, string code) =>
			await _context.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		public async Task<decimal> DailyRateAsync(int employeeId)
		{
			var assignment = await _context.PolicyAssignments.AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeID == employeeId);
			if (assignment == null) return 0m;
			var sp = await _context.SalaryPolicies.AsNoTracking().FirstOrDefaultAsync(s => s.LeavePolicyTypeID == assignment.LeavePolicyTypeID);
			if (sp == null) return 0m;
			var monthly = sp.BaseSalary + sp.HousingAllowance + sp.TransportationAllowance + sp.OtherAllowances;
			return R(monthly / 30m);   // standard 30-day month
		}

		public async Task<List<EncashableBalance>> GetEncashableBalancesAsync(int employeeId)
		{
			var result = new List<EncashableBalance>();
			var assignment = await _context.PolicyAssignments.AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeID == employeeId);
			if (assignment == null) return result;
			var rate = await DailyRateAsync(employeeId);

			// encashable leave types entitled under the employee's policy
			var typeIds = await _context.LeavePolicies.AsNoTracking()
				.Where(lp => lp.LeavePolicyTypeID == assignment.LeavePolicyTypeID)
				.Select(lp => lp.LeaveTypeID).Distinct().ToListAsync();
			var types = await _context.LeaveTypes.AsNoTracking()
				.Where(t => typeIds.Contains(t.ID) && t.IsEncashable).ToListAsync();

			foreach (var t in types)
			{
				var remaining = await _leave.RemainingForTypeAsync(employeeId, t.ID);
				if (remaining <= 0) continue;
				result.Add(new EncashableBalance { LeaveTypeId = t.ID, NameAr = t.NameAr, NameEn = t.NameEn, RemainingDays = remaining, DailyRate = rate });
			}
			return result;
		}

		public async Task<(bool ok, string? error)> EncashAsync(int companyId, int employeeId, int leaveTypeId, int days, int payFromGlAccountId, DateTime date, int? userId)
		{
			if (days <= 0) return (false, "عدد الأيام يجب أن يكون أكبر من صفر");
			var type = await _context.LeaveTypes.AsNoTracking().FirstOrDefaultAsync(t => t.ID == leaveTypeId);
			if (type == null || !type.IsEncashable) return (false, "نوع الإجازة غير قابل للصرف النقدي");
			var remaining = await _leave.RemainingForTypeAsync(employeeId, leaveTypeId);
			if (days > remaining) return (false, $"الأيام المطلوبة ({days}) تتجاوز الرصيد المتاح ({remaining})");

			var rate = await DailyRateAsync(employeeId);
			if (rate <= 0) return (false, "تعذّر حساب الأجر اليومي (لا توجد لائحة راتب للموظف)");
			var amount = R(days * rate);

			var expAcc = await AccIdAsync(companyId, "520104");
			if (expAcc == null) return (false, "حساب بدل الإجازات 520104 غير موجود. شغّل بذرة المحاسبة.");
			if (payFromGlAccountId == expAcc.Value) return (false, "حساب الدفع غير صالح");

			var emp = await _context.Employee.AsNoTracking().FirstOrDefaultAsync(e => e.ID == employeeId);
			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = date.Date, JournalType = "Auto", SourceType = "LeaveEncash",
				Description = $"صرف بدل إجازة ({days} يوم) — {emp?.FullName}", DescriptionEn = $"Leave encashment ({days} days)",
				Lines = new List<JournalLineInput>
				{
					new() { AccountId = expAcc.Value, Debit = amount, Credit = 0, Description = "بدل إجازات" },
					new() { AccountId = payFromGlAccountId, Debit = 0, Credit = amount, Description = "صرف من البنك/الخزينة" },
				},
			}, userId);
			if (!ok) return (false, err);

			_context.LeaveEncashments.Add(new LeaveEncashment
			{
				CompanyID = companyId, EmployeeID = employeeId, EmployeeName = emp?.FullName, LeaveTypeID = leaveTypeId,
				Year = date.Year, Days = days, DailyRate = rate, Amount = amount, EncashDate = date.Date,
				JournalEntryId = entry?.ID, CreatedAt = DateTime.UtcNow,
			});
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<ProvisionPreview> ProvisionPreviewAsync(int companyId, DateTime asOf)
		{
			var pv = new ProvisionPreview { AsOf = asOf.Date };
			var employees = await _context.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && e.IsActive).Select(e => e.ID).ToListAsync();
			pv.Employees = employees.Count;
			decimal totalDays = 0, totalAmount = 0;
			foreach (var empId in employees)
			{
				var balances = await GetEncashableBalancesAsync(empId);
				totalDays += balances.Sum(b => b.RemainingDays);
				totalAmount += balances.Sum(b => b.Amount);
			}
			pv.TotalDays = totalDays;
			pv.TargetAmount = R(totalAmount);

			// current balance of the provision liability (210206): credits − debits over posted lines
			var liab = await AccIdAsync(companyId, "210206");
			if (liab != null)
			{
				var agg = await _context.JournalEntryLines.AsNoTracking()
					.Join(_context.JournalEntries.AsNoTracking().Where(e => e.CompanyID == companyId && e.Status == "Posted" && e.EntryDate <= asOf.Date),
						l => l.JournalEntryId, e => e.ID, (l, e) => l)
					.Where(l => l.AccountId == liab.Value)
					.GroupBy(l => 1).Select(g => new { cr = g.Sum(x => x.Credit), dr = g.Sum(x => x.Debit) }).FirstOrDefaultAsync();
				pv.CurrentBalance = agg == null ? 0m : R(agg.cr - agg.dr);
			}
			return pv;
		}

		public async Task<(bool ok, string? error)> PostProvisionAsync(int companyId, DateTime asOf, int? userId)
		{
			var pv = await ProvisionPreviewAsync(companyId, asOf);
			var adj = pv.Adjustment;
			if (adj == 0m) return (false, "لا يوجد فرق في المخصص لترحيله (الرصيد الحالي مطابق للمطلوب)");

			var expAcc = await AccIdAsync(companyId, "520106");
			var liabAcc = await AccIdAsync(companyId, "210206");
			if (expAcc == null || liabAcc == null) return (false, "حسابات المخصص (520106/210206) غير موجودة. شغّل بذرة المحاسبة.");

			var amount = Math.Abs(adj);
			var lines = new List<JournalLineInput>();
			if (adj > 0)   // increase the provision
			{
				lines.Add(new() { AccountId = expAcc.Value, Debit = amount, Credit = 0, Description = "مصروف مخصص إجازات" });
				lines.Add(new() { AccountId = liabAcc.Value, Debit = 0, Credit = amount, Description = "زيادة مخصص الإجازات" });
			}
			else            // release surplus provision
			{
				lines.Add(new() { AccountId = liabAcc.Value, Debit = amount, Credit = 0, Description = "تخفيض مخصص الإجازات" });
				lines.Add(new() { AccountId = expAcc.Value, Debit = 0, Credit = amount, Description = "رد مخصص إجازات" });
			}

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = asOf.Date, JournalType = "Auto", SourceType = "LeaveProvision",
				Description = $"تسوية مخصص الإجازات حتى {asOf:yyyy-MM-dd}", DescriptionEn = $"Leave provision adjustment as of {asOf:yyyy-MM-dd}",
				Lines = lines,
			}, userId);
			if (!ok) return (false, err);

			_context.LeaveProvisionRuns.Add(new LeaveProvisionRun
			{
				CompanyID = companyId, AsOfDate = asOf.Date, TotalDays = pv.TotalDays, TotalAmount = pv.TargetAmount,
				Adjustment = adj, JournalEntryId = entry?.ID, CreatedAt = DateTime.UtcNow,
			});
			await _context.SaveChangesAsync();
			return (true, null);
		}

		// ---- annual leave carry-over (unused balance → next year, capped by CarryOverLimit) ----
		public async Task<List<CarryOverRow>> CarryOverPreviewAsync(int companyId, int fromYear)
		{
			var rows = new List<CarryOverRow>();
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var employees = await _context.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == companyId && e.IsActive).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync();
			var typeNames = (await _context.LeaveTypes.AsNoTracking().ToListAsync())
				.ToDictionary(t => t.ID, t => !isAr && !string.IsNullOrWhiteSpace(t.NameEn) ? t.NameEn : t.NameAr);

			foreach (var e in employees)
			{
				var assignment = await _context.PolicyAssignments.AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeID == e.ID);
				if (assignment == null) continue;
				// leave types with a positive carry-over limit under the employee's policy
				var limits = await _context.LeavePolicies.AsNoTracking()
					.Where(lp => lp.LeavePolicyTypeID == assignment.LeavePolicyTypeID && lp.CarryOverLimit > 0)
					.GroupBy(lp => lp.LeaveTypeID).Select(g => new { TypeId = g.Key, Limit = g.Max(x => x.CarryOverLimit) }).ToListAsync();
				foreach (var l in limits)
				{
					var remaining = await _leave.RemainingForTypeInYearAsync(e.ID, l.TypeId, fromYear);
					var carry = Math.Min(Math.Max(0, remaining), l.Limit);
					if (carry <= 0) continue;
					rows.Add(new CarryOverRow { EmployeeId = e.ID, EmployeeName = !isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName, LeaveTypeId = l.TypeId,
						LeaveTypeName = typeNames.TryGetValue(l.TypeId, out var n) ? n : null, Remaining = remaining, Limit = l.Limit, CarryDays = carry });
				}
			}
			return rows;
		}

		public async Task<(bool ok, string? error, int rows)> RunCarryOverAsync(int companyId, int fromYear)
		{
			var preview = await CarryOverPreviewAsync(companyId, fromYear);
			var targetYear = fromYear + 1;
			// idempotent: replace any prior carry-over for the target year
			await _context.LeaveCarryOvers.Where(c => c.CompanyID == companyId && c.Year == targetYear).ExecuteDeleteAsync();
			foreach (var r in preview)
				_context.LeaveCarryOvers.Add(new LeaveCarryOver
				{ CompanyID = companyId, EmployeeID = r.EmployeeId, LeaveTypeID = r.LeaveTypeId, Year = targetYear, Days = r.CarryDays, CreatedAt = DateTime.UtcNow });
			await _context.SaveChangesAsync();
			return (true, null, preview.Count);
		}
	}
}
