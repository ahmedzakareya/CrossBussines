using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Comm;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    public class AnnouncementDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Priority { get; set; } = "Normal";
        public string Scope { get; set; } = "Company";
        public int? BranchID { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsActive { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? AuthorName { get; set; }
    }

    public interface IAnnouncementService
    {
        Task<int> CreateAsync(int companyId, int createdBy, string title, string body, string priority, string scope, int? branchId, DateTime? startsAt, DateTime? expiresAt);
        Task<List<AnnouncementDto>> ActiveForAsync(int companyId, int? branchId, int empId);
        Task<List<AnnouncementDto>> ListAsync(int companyId);
        Task DismissAsync(int empId, int announcementId);
        Task SetActiveAsync(int companyId, int announcementId, bool active);
    }

    public class AnnouncementService : IAnnouncementService
    {
        private readonly CrossDbContext _db;
        private readonly INotificationService _notify;
        public AnnouncementService(CrossDbContext db, INotificationService notify) { _db = db; _notify = notify; }

        public async Task<int> CreateAsync(int companyId, int createdBy, string title, string body, string priority, string scope, int? branchId, DateTime? startsAt, DateTime? expiresAt)
        {
            var a = new Announcement
            {
                CompanyID = companyId, CreatedBy = createdBy, CreatedAt = DateTime.UtcNow,
                Title = (title ?? "").Trim(), Body = body ?? "",
                Priority = string.IsNullOrWhiteSpace(priority) ? "Normal" : priority,
                Scope = scope == "Branch" && branchId.HasValue ? "Branch" : "Company",
                BranchID = scope == "Branch" ? branchId : null,
                StartsAt = startsAt, ExpiresAt = expiresAt, IsActive = true,
            };
            _db.Announcements.Add(a);
            await _db.SaveChangesAsync();

            // notify the audience (best-effort; never block)
            try
            {
                var q = _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && e.IsActive);
                if (a.Scope == "Branch" && a.BranchID.HasValue) q = q.Where(e => e.BranchID == a.BranchID.Value);
                var targets = await q.Select(e => e.ID).ToListAsync();
                foreach (var empId in targets)
                    await _notify.NotifyAsync(empId, a.Title, a.Title, a.Body, a.Body,
                        NotificationTypes.Announcement, a.Id, url: "/Announcements",
                        companyId: companyId, actorEmployeeId: createdBy, priority: a.Priority);
            }
            catch { }
            return a.Id;
        }

        private static IQueryable<Announcement> ActiveQuery(IQueryable<Announcement> q, DateTime now)
            => q.Where(a => a.IsActive && (a.StartsAt == null || a.StartsAt <= now) && (a.ExpiresAt == null || a.ExpiresAt >= now));

        public async Task<List<AnnouncementDto>> ActiveForAsync(int companyId, int? branchId, int empId)
        {
            var now = DateTime.UtcNow;
            var q = ActiveQuery(_db.Announcements.AsNoTracking().Where(a => a.CompanyID == companyId), now)
                .Where(a => a.Scope == "Company" || (a.Scope == "Branch" && a.BranchID == branchId));
            var dismissed = await _db.AnnouncementReads.AsNoTracking().Where(r => r.EmployeeId == empId).Select(r => r.AnnouncementId).ToListAsync();
            var list = await q.Where(a => !dismissed.Contains(a.Id)).OrderByDescending(a => a.Id).Take(20).ToListAsync();
            return list.Select(Map).ToList();
        }

        public async Task<List<AnnouncementDto>> ListAsync(int companyId)
        {
            var list = await _db.Announcements.AsNoTracking().Where(a => a.CompanyID == companyId).OrderByDescending(a => a.Id).Take(200).ToListAsync();
            var authorIds = list.Where(a => a.CreatedBy.HasValue).Select(a => a.CreatedBy!.Value).Distinct().ToList();
            var names = await _db.Employee.AsNoTracking().Where(e => authorIds.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(x => x.ID, x => x.FullName ?? x.FullNameEn ?? "-");
            return list.Select(a => { var d = Map(a); if (a.CreatedBy.HasValue && names.TryGetValue(a.CreatedBy.Value, out var n)) d.AuthorName = n; return d; }).ToList();
        }

        public async Task DismissAsync(int empId, int announcementId)
        {
            if (await _db.AnnouncementReads.AnyAsync(r => r.EmployeeId == empId && r.AnnouncementId == announcementId)) return;
            _db.AnnouncementReads.Add(new AnnouncementRead { EmployeeId = empId, AnnouncementId = announcementId, ReadAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
        }

        public async Task SetActiveAsync(int companyId, int announcementId, bool active)
        {
            var a = await _db.Announcements.FirstOrDefaultAsync(x => x.Id == announcementId && x.CompanyID == companyId);
            if (a == null) return;
            a.IsActive = active; a.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        private static AnnouncementDto Map(Announcement a) => new()
        {
            Id = a.Id, Title = a.Title, Body = a.Body, Priority = a.Priority, Scope = a.Scope, BranchID = a.BranchID,
            StartsAt = a.StartsAt, ExpiresAt = a.ExpiresAt, IsActive = a.IsActive, CreatedAt = a.CreatedAt,
        };
    }
}
