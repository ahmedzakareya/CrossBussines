using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public interface IHolidayService
	{
		Task<List<OfficialHoliday>> GetAllAsync(int companyId);
		Task<(bool ok, string? error)> SaveAsync(OfficialHoliday h);
		Task<bool> DeleteAsync(int companyId, int id);
		// dates within [from,to] that are official holidays for the company (expands recurring ones per year)
		Task<HashSet<DateTime>> HolidayDatesAsync(int companyId, DateTime from, DateTime to);
	}

	public class HolidayService : IHolidayService
	{
		private readonly CrossDbContext _db;
		public HolidayService(CrossDbContext db) { _db = db; }

		public Task<List<OfficialHoliday>> GetAllAsync(int companyId) =>
			_db.OfficialHolidays.AsNoTracking().Where(h => h.CompanyID == companyId).OrderBy(h => h.HolidayDate).ToListAsync();

		public async Task<(bool ok, string? error)> SaveAsync(OfficialHoliday h)
		{
			if (string.IsNullOrWhiteSpace(h.NameAr)) return (false, "اسم العطلة مطلوب");
			if (h.CompanyID <= 0) h.CompanyID = 1;
			if (h.ID == 0)
			{
				// prevent duplicate same-date holiday for the company
				if (await _db.OfficialHolidays.AnyAsync(x => x.CompanyID == h.CompanyID && x.HolidayDate == h.HolidayDate.Date))
					return (false, "يوجد عطلة بنفس التاريخ بالفعل");
				h.HolidayDate = h.HolidayDate.Date; h.CreatedAt = DateTime.UtcNow;
				_db.OfficialHolidays.Add(h);
			}
			else
			{
				var e = await _db.OfficialHolidays.FirstOrDefaultAsync(x => x.ID == h.ID && x.CompanyID == h.CompanyID);
				if (e == null) return (false, "العطلة غير موجودة");
				e.NameAr = h.NameAr; e.NameEn = h.NameEn; e.HolidayDate = h.HolidayDate.Date; e.IsRecurring = h.IsRecurring; e.Notes = h.Notes;
			}
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<bool> DeleteAsync(int companyId, int id)
		{
			var e = await _db.OfficialHolidays.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (e == null) return false;
			_db.OfficialHolidays.Remove(e); await _db.SaveChangesAsync(); return true;
		}

		public async Task<HashSet<DateTime>> HolidayDatesAsync(int companyId, DateTime from, DateTime to)
		{
			var set = new HashSet<DateTime>();
			var list = await _db.OfficialHolidays.AsNoTracking().Where(h => h.CompanyID == companyId).Select(h => new { h.HolidayDate, h.IsRecurring }).ToListAsync();
			var s = from.Date; var e = to.Date;
			foreach (var h in list)
			{
				if (h.IsRecurring)
				{
					for (int y = s.Year; y <= e.Year; y++)
					{
						try { var d = new DateTime(y, h.HolidayDate.Month, h.HolidayDate.Day); if (d >= s && d <= e) set.Add(d); }
						catch { /* Feb 29 on non-leap year → skip */ }
					}
				}
				else if (h.HolidayDate.Date >= s && h.HolidayDate.Date <= e) set.Add(h.HolidayDate.Date);
			}
			return set;
		}
	}
}
