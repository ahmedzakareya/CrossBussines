using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class MaintenanceDueRow
	{
		public int ScheduleId { get; set; }
		public int AssetId { get; set; }
		public string AssetName { get; set; } = "";
		public string Title { get; set; } = "";
		public string Type { get; set; } = "";
		public DateTime NextDueDate { get; set; }
		public int DaysLeft { get; set; }        // negative = overdue
		public decimal? EstimatedCost { get; set; }
	}

	public interface IMaintenanceService
	{
		Task<List<MaintenanceSchedule>> GetSchedulesAsync(int companyId, int assetId);
		Task<List<MaintenanceRecord>> GetRecordsAsync(int companyId, int assetId);
		Task<(bool ok, string? error, int id)> SaveScheduleAsync(MaintenanceSchedule dto);
		Task DeleteScheduleAsync(int companyId, int id);
		/// Log a performed maintenance. Optional GL (Dr 520110 / Cr payFromGlAccountId) when a pay account is given.
		/// Advances the linked schedule (NextDueDate += IntervalMonths, LastDoneDate = date).
		Task<(bool ok, string? error)> LogMaintenanceAsync(int companyId, int assetId, int? scheduleId, DateTime date, string? description, decimal cost, string? vendor, int? payFromGlAccountId, int? projectId, int? userId);
		Task<List<MaintenanceDueRow>> DueSoonAsync(int companyId, int daysAhead);
	}

	public class MaintenanceService : IMaintenanceService
	{
		private readonly CrossDbContext _db;
		private readonly IJournalEntryService _journals;
		public MaintenanceService(CrossDbContext db, IJournalEntryService journals) { _db = db; _journals = journals; }
		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		public Task<List<MaintenanceSchedule>> GetSchedulesAsync(int companyId, int assetId) =>
			_db.MaintenanceSchedules.AsNoTracking().Where(s => s.CompanyID == companyId && s.AssetId == assetId).OrderBy(s => s.NextDueDate).ToListAsync();

		public Task<List<MaintenanceRecord>> GetRecordsAsync(int companyId, int assetId) =>
			_db.MaintenanceRecords.AsNoTracking().Where(r => r.CompanyID == companyId && r.AssetId == assetId).OrderByDescending(r => r.Date).ToListAsync();

		public async Task<(bool ok, string? error, int id)> SaveScheduleAsync(MaintenanceSchedule dto)
		{
			if (dto.AssetId <= 0) return (false, "الأصل مطلوب", 0);
			if (string.IsNullOrWhiteSpace(dto.Title)) return (false, "عنوان الجدول مطلوب", 0);
			if (dto.IntervalMonths <= 0) return (false, "الفترة بالأشهر يجب أن تكون أكبر من صفر", 0);
			MaintenanceSchedule e;
			if (dto.ID > 0) e = await _db.MaintenanceSchedules.FirstOrDefaultAsync(s => s.ID == dto.ID && s.CompanyID == dto.CompanyID) ?? throw new InvalidOperationException("الجدول غير موجود");
			else { e = new MaintenanceSchedule { CompanyID = dto.CompanyID, AssetId = dto.AssetId, CreatedAt = DateTime.UtcNow }; _db.MaintenanceSchedules.Add(e); }
			e.Title = dto.Title.Trim(); e.Type = dto.Type; e.IntervalMonths = dto.IntervalMonths;
			e.NextDueDate = dto.NextDueDate.Date; e.EstimatedCost = dto.EstimatedCost; e.IsActive = dto.IsActive;
			if (dto.ID <= 0) e.LastDoneDate = null;
			await _db.SaveChangesAsync();
			return (true, null, e.ID);
		}

		public async Task DeleteScheduleAsync(int companyId, int id)
		{
			var e = await _db.MaintenanceSchedules.FirstOrDefaultAsync(s => s.ID == id && s.CompanyID == companyId);
			if (e != null) { _db.MaintenanceSchedules.Remove(e); await _db.SaveChangesAsync(); }
		}

		public async Task<(bool ok, string? error)> LogMaintenanceAsync(int companyId, int assetId, int? scheduleId, DateTime date, string? description, decimal cost, string? vendor, int? payFromGlAccountId, int? projectId, int? userId)
		{
			var asset = await _db.FixedAssets.AsNoTracking().FirstOrDefaultAsync(a => a.ID == assetId && a.CompanyID == companyId);
			if (asset == null) return (false, "الأصل غير موجود");
			if (cost < 0) cost = 0;

			var rec = new MaintenanceRecord { CompanyID = companyId, AssetId = assetId, ScheduleId = scheduleId, Date = date.Date, Description = description, Cost = R(cost), Vendor = vendor, Status = "Done", ProjectId = projectId, CreatedAt = DateTime.UtcNow };

			// optional GL: Dr 520110 maintenance expense / Cr cash-or-payable — inherits the asset's cost center (+ project)
			if (payFromGlAccountId.HasValue && payFromGlAccountId.Value > 0 && cost > 0)
			{
				var maintAcc = await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == companyId && a.Code == "520110").Select(a => (int?)a.ID).FirstOrDefaultAsync();
				if (maintAcc == null) return (false, "حساب مصروف الصيانة 520110 غير موجود — شغّل asset_maintenance.sql");
				if (payFromGlAccountId.Value == maintAcc.Value) return (false, "حساب الدفع غير صالح");
				var lines = new List<JournalLineInput>
				{
					new() { AccountId = maintAcc.Value, Debit = R(cost), Credit = 0, CostCenterId = asset.CostCenterId, ProjectId = projectId, Description = $"صيانة أصل {asset.AssetNo ?? asset.Name}" },
					new() { AccountId = payFromGlAccountId.Value, Debit = 0, Credit = R(cost), CostCenterId = asset.CostCenterId, ProjectId = projectId, Description = "سداد صيانة" },
				};
				var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
				{ CompanyID = companyId, EntryDate = date.Date, JournalType = "Auto", SourceType = "AssetMaintenance", Description = $"صيانة أصل {asset.AssetNo ?? asset.Name}", Lines = lines }, userId);
				if (!ok) return (false, "تعذّر ترحيل قيد الصيانة: " + err);
				rec.JournalEntryId = entry!.ID;
			}
			_db.MaintenanceRecords.Add(rec);

			// advance the schedule
			if (scheduleId.HasValue)
			{
				var sch = await _db.MaintenanceSchedules.FirstOrDefaultAsync(s => s.ID == scheduleId.Value && s.CompanyID == companyId);
				if (sch != null) { sch.LastDoneDate = date.Date; sch.NextDueDate = date.Date.AddMonths(sch.IntervalMonths <= 0 ? 1 : sch.IntervalMonths); }
			}
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<MaintenanceDueRow>> DueSoonAsync(int companyId, int daysAhead)
		{
			var cutoff = DateTime.Today.AddDays(daysAhead <= 0 ? 30 : daysAhead);
			var rows = await (from s in _db.MaintenanceSchedules.AsNoTracking()
							  join a in _db.FixedAssets.AsNoTracking() on s.AssetId equals a.ID
							  where s.CompanyID == companyId && s.IsActive && s.NextDueDate <= cutoff
							  select new { s.ID, s.AssetId, AssetName = a.Name, s.Title, s.Type, s.NextDueDate, s.EstimatedCost }).ToListAsync();
			return rows.Select(r => new MaintenanceDueRow { ScheduleId = r.ID, AssetId = r.AssetId, AssetName = r.AssetName, Title = r.Title, Type = r.Type, NextDueDate = r.NextDueDate, EstimatedCost = r.EstimatedCost, DaysLeft = (int)(r.NextDueDate.Date - DateTime.Today).TotalDays })
				.OrderBy(r => r.DaysLeft).ToList();
		}
	}
}
