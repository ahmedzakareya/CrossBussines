using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// HR-10 — training courses + enrollments. HR record only; no GL impact.
	public interface ITrainingService
	{
		Task<List<TrainingCourse>> GetCoursesAsync(int companyId, string? q);
		Task<TrainingCourse?> GetCourseAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveCourseAsync(TrainingCourse dto);
		Task DeleteCourseAsync(int companyId, int id);
		Task<List<TrainingEnrollment>> GetEnrollmentsAsync(int companyId, int courseId);
		Task<(bool ok, string? error)> EnrollAsync(int companyId, int courseId, IEnumerable<int> employeeIds);
		Task<(bool ok, string? error)> SetEnrollmentStatusAsync(int companyId, int id, string status, decimal? score, string? certificate);
		Task RemoveEnrollmentAsync(int companyId, int id);
		Task<List<TrainingEnrollment>> MyTrainingsAsync(int employeeId);
	}

	public class TrainingService : ITrainingService
	{
		private readonly CrossDbContext _db;
		public TrainingService(CrossDbContext db) { _db = db; }

		public Task<List<TrainingCourse>> GetCoursesAsync(int companyId, string? q)
		{
			var query = _db.TrainingCourses.AsNoTracking().Where(c => c.CompanyID == companyId);
			if (!string.IsNullOrWhiteSpace(q))
			{
				var t = q.Trim();
				query = query.Where(c => c.Code.Contains(t) || c.Title.Contains(t) || (c.TitleEn != null && c.TitleEn.Contains(t)) || (c.Provider != null && c.Provider.Contains(t)) || (c.ProviderEn != null && c.ProviderEn.Contains(t)));
			}
			return query.OrderByDescending(c => c.ID).ToListAsync();
		}

		public Task<TrainingCourse?> GetCourseAsync(int companyId, int id) =>
			_db.TrainingCourses.AsNoTracking().FirstOrDefaultAsync(c => c.ID == id && c.CompanyID == companyId);

		public async Task<(bool ok, string? error, int id)> SaveCourseAsync(TrainingCourse dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Title)) return (false, "الكود والعنوان مطلوبان", 0);
			var dup = await _db.TrainingCourses.AnyAsync(c => c.CompanyID == dto.CompanyID && c.Code == dto.Code && c.ID != dto.ID);
			if (dup) return (false, "كود الدورة مستخدم من قبل", 0);
			if (dto.StartDate.HasValue && dto.EndDate.HasValue && dto.EndDate < dto.StartDate) return (false, "تاريخ النهاية قبل البداية", 0);
			TrainingCourse e;
			if (dto.ID > 0)
			{
				e = await _db.TrainingCourses.FirstOrDefaultAsync(c => c.ID == dto.ID && c.CompanyID == dto.CompanyID) ?? throw new InvalidOperationException("الدورة غير موجودة");
			}
			else { e = new TrainingCourse { CompanyID = dto.CompanyID, CreatedAt = DateTime.UtcNow }; _db.TrainingCourses.Add(e); }
			e.Code = dto.Code.Trim(); e.Title = dto.Title.Trim(); e.TitleEn = dto.TitleEn; e.Provider = dto.Provider; e.ProviderEn = dto.ProviderEn; e.Category = dto.Category;
			e.Cost = dto.Cost < 0 ? 0 : dto.Cost; e.Hours = dto.Hours < 0 ? 0 : dto.Hours; e.StartDate = dto.StartDate; e.EndDate = dto.EndDate;
			e.IsActive = dto.IsActive; e.Notes = dto.Notes;
			await _db.SaveChangesAsync();
			return (true, null, e.ID);
		}

		public async Task DeleteCourseAsync(int companyId, int id)
		{
			var e = await _db.TrainingCourses.FirstOrDefaultAsync(c => c.ID == id && c.CompanyID == companyId);
			if (e == null) return;
			_db.TrainingEnrollments.RemoveRange(_db.TrainingEnrollments.Where(x => x.CourseId == id));
			_db.TrainingCourses.Remove(e);
			await _db.SaveChangesAsync();
		}

		public Task<List<TrainingEnrollment>> GetEnrollmentsAsync(int companyId, int courseId) =>
			_db.TrainingEnrollments.AsNoTracking().Where(x => x.CompanyID == companyId && x.CourseId == courseId).OrderBy(x => x.ID).ToListAsync();

		public async Task<(bool ok, string? error)> EnrollAsync(int companyId, int courseId, IEnumerable<int> employeeIds)
		{
			var course = await _db.TrainingCourses.AsNoTracking().FirstOrDefaultAsync(c => c.ID == courseId && c.CompanyID == companyId);
			if (course == null) return (false, "الدورة غير موجودة");
			var existing = await _db.TrainingEnrollments.Where(x => x.CourseId == courseId).Select(x => x.EmployeeID).ToListAsync();
			int added = 0;
			foreach (var emp in employeeIds.Distinct())
			{
				if (emp <= 0 || existing.Contains(emp)) continue;
				_db.TrainingEnrollments.Add(new TrainingEnrollment { CompanyID = companyId, CourseId = courseId, EmployeeID = emp, Status = "Planned", CreatedAt = DateTime.UtcNow });
				added++;
			}
			if (added == 0) return (false, "لا يوجد موظفون جدد للتسجيل");
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> SetEnrollmentStatusAsync(int companyId, int id, string status, decimal? score, string? certificate)
		{
			var e = await _db.TrainingEnrollments.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (e == null) return (false, "التسجيل غير موجود");
			var st = new[] { "Planned", "Attended", "Completed", "Cancelled" }.Contains(status) ? status : "Planned";
			e.Status = st; e.Score = score; e.Certificate = certificate;
			e.CompletedAt = st == "Completed" ? DateTime.UtcNow : null;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task RemoveEnrollmentAsync(int companyId, int id)
		{
			var e = await _db.TrainingEnrollments.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (e != null) { _db.TrainingEnrollments.Remove(e); await _db.SaveChangesAsync(); }
		}

		public Task<List<TrainingEnrollment>> MyTrainingsAsync(int employeeId) =>
			_db.TrainingEnrollments.AsNoTracking().Where(x => x.EmployeeID == employeeId).OrderByDescending(x => x.ID).ToListAsync();
	}
}
