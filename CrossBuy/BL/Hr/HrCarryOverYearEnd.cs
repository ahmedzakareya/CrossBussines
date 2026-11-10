using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// YEAR-END LEAVE CARRY-OVER, AUTOMATIC (2026-11-10).
	//
	// The carry-over itself is LeaveAccrualService.RunCarryOverAsync (unused balance -> next year, capped by each policy's
	// CarryOverLimit), which HR could only run by hand. In JANUARY of a year the hourly HR worker now runs it once for the
	// year just closed, unless HR already did (any carry-over row for the new year means a human ran it, and a human's run
	// is never overwritten). Then HR gets a task with the summary, linked nowhere in particular and due today, so the
	// result is seen and checked rather than silently applied.
	//
	// Once per company and year, through TaskAutoLog ("HrCarryOver:<year>"). Outside January nothing happens: a carry-over
	// for a year long past, run on a late install, would hand balances to people for years the system never saw.
	// Switch: TaskAutoRule RuleType "HrLeaveCarryOver" — absent means ON, inactive means OFF; its DefaultAssigneeEmployeeId
	// receives the task (0 = unassigned, for a manager to route).
	// ==========================================================================================
	public sealed record CarryOverYearEndResult(bool Ran, int Rows, int Days, string? Skipped);

	public sealed class HrCarryOverYearEnd
	{
		public const string RuleType = "HrLeaveCarryOver";
		public static string Key(int year) => $"HrCarryOver:{year}";

		private readonly CrossDbContext _db;
		private readonly ILeaveAccrualService _accrual;
		public HrCarryOverYearEnd(CrossDbContext db, ILeaveAccrualService accrual) { _db = db; _accrual = accrual; }

		public async Task<CarryOverYearEndResult> RunIfDueAsync(int companyId, DateTime today, CancellationToken ct = default)
		{
			if (today.Month != 1) return new(false, 0, 0, "not January");
			var year = today.Year;
			var rule = await _db.TaskAutoRules.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == companyId && r.RuleType == RuleType, ct);
			if (rule is { IsActive: false }) return new(false, 0, 0, "switched off");
			var key = Key(year);
			if (await _db.TaskAutoLogs.AsNoTracking().AnyAsync(l => l.CompanyId == companyId && l.RuleKey == key, ct)) return new(false, 0, 0, "already run");
			if (await _db.LeaveCarryOvers.AsNoTracking().AnyAsync(c => c.CompanyID == companyId && c.Year == year, ct))
			{
				// HR ran it by hand: record that the year is settled, never overwrite a human's run
				_db.TaskAutoLogs.Add(new TaskAutoLog { CompanyId = companyId, RuleKey = key, TaskId = 0, CreatedAt = DateTime.UtcNow });
				await _db.SaveChangesAsync(ct);
				return new(false, 0, 0, "run by HR");
			}

			// ONE unit: the carry-over rows, HR's task and the once-per-year marker land together or not at all (BeginOrJoin, so
			// a caller that already owns a transaction keeps owning it).
			await using var tx = await CrossBuy.BL.ScopedTx.BeginOrJoinAsync(_db);
			var (ok, _, rows) = await _accrual.RunCarryOverAsync(companyId, year - 1);
			if (!ok) return new(false, 0, 0, "carry-over refused");
			var days = await _db.LeaveCarryOvers.AsNoTracking().Where(c => c.CompanyID == companyId && c.Year == year).SumAsync(c => (int?)c.Days, ct) ?? 0;

			var task = new TaskItem
			{
				CompanyId = companyId,
				Title = $"ترحيل رصيد الإجازات من {year - 1} إلى {year}: {rows} سطر، {days} يوم — للمراجعة",
				TitleEn = $"Leave carried over from {year - 1} to {year}: {rows} line(s), {days} day(s) — review",
				AssigneeEmployeeId = rule?.DefaultAssigneeEmployeeId ?? 0,
				CreatedByEmployeeId = 0,
				Priority = "Normal",
				DueDate = today.Date,
				Status = "New",
				ActualHours = 0m,
				CreatedAt = DateTime.UtcNow,
			};
			_db.TaskItems.Add(task);
			await _db.SaveChangesAsync(ct);
			_db.TaskAutoLogs.Add(new TaskAutoLog { CompanyId = companyId, RuleKey = key, TaskId = task.ID, CreatedAt = DateTime.UtcNow });
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync();
			return new(true, rows, days, null);
		}
	}
}
