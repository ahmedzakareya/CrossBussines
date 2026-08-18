using System.Globalization;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Library;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    // one row in the listing (folder or file). name = culture-appropriate display; nameAr/nameEn = raw twins (for the rename editor)
    public record LibItemDto(int id, bool isFolder, string name, string nameAr, string? nameEn, long size, string sizeText,
        string modified, string? ext, string? url, string ownerName);

    public record LibCrumbDto(int? id, string name);

    // full listing payload for a folder: rows + breadcrumb path + stats + current parent (for "up")
    public record LibListDto(List<LibItemDto> items, List<LibCrumbDto> path,
        int folderCount, int fileCount, long totalSize, string totalSizeText, int? parentId);

    // a folder option for the "move to folder" picker (indented by depth)
    public record LibFolderDto(int id, string name, int depth);

    public interface IFileManagerService
    {
        Task<LibListDto> ListAsync(int companyId, int? folderId, string? q);
        Task<int> CreateFolderAsync(int companyId, int empId, int? parentId, string name, string? nameEn);
        Task<bool> RenameAsync(int companyId, int empId, int id, string name, string? nameEn);
        Task<int> MoveAsync(int companyId, int empId, IEnumerable<int> ids, int? targetFolderId);
        Task<int> DeleteAsync(int companyId, int empId, IEnumerable<int> ids);
        Task<int> AddFileAsync(int companyId, int empId, int? parentId, string name, string? nameEn, string storedPath, string? contentType, long size);
        Task<LibraryItem?> GetFileAsync(int companyId, int id);
        Task<List<LibraryItem>> GetFilesAsync(int companyId, IEnumerable<int> ids);
        Task<List<LibFolderDto>> FolderTreeAsync(int companyId);
        Task<bool> FolderExistsAsync(int companyId, int folderId);
    }

    public class FileManagerService : IFileManagerService
    {
        private readonly CrossDbContext _db;
        public FileManagerService(CrossDbContext db) { _db = db; }

        private IQueryable<LibraryItem> Live(int companyId) =>
            _db.LibraryItems.AsNoTracking().Where(i => i.CompanyID == companyId && i.DeletedAt == null);

        public async Task<LibListDto> ListAsync(int companyId, int? folderId, string? q)
        {
            // validate the folder belongs to the company (else fall back to root)
            if (folderId.HasValue && !await FolderExistsAsync(companyId, folderId.Value)) folderId = null;

            var query = Live(companyId).Where(i => i.ParentId == folderId);
            var term = (q ?? "").Trim();
            if (term.Length > 0) query = query.Where(i => i.Name.Contains(term));

            // folders first, then files; each alphabetical
            var rows = await query.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name).Take(2000).ToListAsync();

            var isEn = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
            var ownerIds = rows.Select(r => r.OwnerEmpId).Distinct().ToList();
            var owners = await _db.Employee.AsNoTracking().Where(e => ownerIds.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync();

            var items = rows.Select(r =>
            {
                var o = owners.FirstOrDefault(x => x.ID == r.OwnerEmpId);
                var ownerName = o == null ? "" : (isEn ? (string.IsNullOrWhiteSpace(o.FullNameEn) ? o.FullName : o.FullNameEn) : o.FullName);
                var disp = DisplayName(r.Name, r.NameEn, isEn);
                return new LibItemDto(
                    r.Id, r.IsFolder, disp, r.Name, r.NameEn, r.Size, r.IsFolder ? "-" : SizeText(r.Size),
                    (r.UpdatedAt ?? r.CreatedAt)?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                    r.IsFolder ? null : Ext(r.Name),
                    r.IsFolder ? null : r.StoredPath,
                    ownerName);
            }).ToList();

            var path = await BreadcrumbAsync(companyId, folderId);
            var folderCount = rows.Count(r => r.IsFolder);
            var fileCount = rows.Count(r => !r.IsFolder);
            var totalSize = rows.Where(r => !r.IsFolder).Sum(r => r.Size);
            var currentParent = folderId.HasValue
                ? await _db.LibraryItems.AsNoTracking().Where(i => i.Id == folderId.Value).Select(i => i.ParentId).FirstOrDefaultAsync()
                : null;

            return new LibListDto(items, path, folderCount, fileCount, totalSize, SizeText(totalSize), currentParent);
        }

        private async Task<List<LibCrumbDto>> BreadcrumbAsync(int companyId, int? folderId)
        {
            var isEn = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
            var crumbs = new List<LibCrumbDto>();
            var id = folderId;
            var guard = 0;
            while (id.HasValue && guard++ < 50)
            {
                var f = await _db.LibraryItems.AsNoTracking()
                    .Where(i => i.Id == id.Value && i.CompanyID == companyId)
                    .Select(i => new { i.Id, i.Name, i.NameEn, i.ParentId }).FirstOrDefaultAsync();
                if (f == null) break;
                crumbs.Insert(0, new LibCrumbDto(f.Id, DisplayName(f.Name, f.NameEn, isEn)));
                id = f.ParentId;
            }
            return crumbs;
        }

        private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        public async Task<int> CreateFolderAsync(int companyId, int empId, int? parentId, string name, string? nameEn)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) name = "New folder";
            if (parentId.HasValue && !await FolderExistsAsync(companyId, parentId.Value)) parentId = null;
            var f = new LibraryItem
            {
                CompanyID = companyId, ParentId = parentId, IsFolder = true, Name = name, NameEn = Clean(nameEn),
                OwnerEmpId = empId, CreatedBy = empId, CreatedAt = DateTime.Now
            };
            _db.LibraryItems.Add(f);
            await _db.SaveChangesAsync();
            return f.Id;
        }

        public async Task<int> AddFileAsync(int companyId, int empId, int? parentId, string name, string? nameEn, string storedPath, string? contentType, long size)
        {
            if (parentId.HasValue && !await FolderExistsAsync(companyId, parentId.Value)) parentId = null;
            var f = new LibraryItem
            {
                CompanyID = companyId, ParentId = parentId, IsFolder = false,
                Name = (name ?? "file").Trim(), NameEn = Clean(nameEn), StoredPath = storedPath, ContentType = contentType, Size = size,
                OwnerEmpId = empId, CreatedBy = empId, CreatedAt = DateTime.Now
            };
            _db.LibraryItems.Add(f);
            await _db.SaveChangesAsync();
            return f.Id;
        }

        public async Task<bool> RenameAsync(int companyId, int empId, int id, string name, string? nameEn)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return false;
            var it = await _db.LibraryItems.FirstOrDefaultAsync(i => i.Id == id && i.CompanyID == companyId && i.DeletedAt == null);
            if (it == null) return false;
            it.Name = name; it.NameEn = Clean(nameEn); it.updatedBy = empId; it.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<int> MoveAsync(int companyId, int empId, IEnumerable<int> ids, int? targetFolderId)
        {
            if (targetFolderId.HasValue && !await FolderExistsAsync(companyId, targetFolderId.Value)) return 0;
            var idSet = ids.Distinct().ToList();
            var items = await _db.LibraryItems.Where(i => idSet.Contains(i.Id) && i.CompanyID == companyId && i.DeletedAt == null).ToListAsync();
            var moved = 0;
            foreach (var it in items)
            {
                // never move a folder into itself or one of its own descendants
                if (it.IsFolder && targetFolderId.HasValue &&
                    (targetFolderId.Value == it.Id || await IsDescendantAsync(companyId, targetFolderId.Value, it.Id)))
                    continue;
                it.ParentId = targetFolderId; it.updatedBy = empId; it.UpdatedAt = DateTime.Now;
                moved++;
            }
            await _db.SaveChangesAsync();
            return moved;
        }

        // is `candidateId` inside the subtree rooted at `ancestorId`?
        private async Task<bool> IsDescendantAsync(int companyId, int candidateId, int ancestorId)
        {
            int? p = candidateId; var guard = 0;
            while (p.HasValue && guard++ < 100)
            {
                if (p.Value == ancestorId) return true;
                p = await _db.LibraryItems.AsNoTracking().Where(i => i.Id == p.Value && i.CompanyID == companyId).Select(i => i.ParentId).FirstOrDefaultAsync();
            }
            return false;
        }

        public async Task<int> DeleteAsync(int companyId, int empId, IEnumerable<int> ids)
        {
            var roots = await _db.LibraryItems.Where(i => ids.Contains(i.Id) && i.CompanyID == companyId && i.DeletedAt == null).ToListAsync();
            if (roots.Count == 0) return 0;

            // expand folders to their whole subtree (BFS over ParentId)
            var toDelete = new List<LibraryItem>(roots);
            var frontier = roots.Where(r => r.IsFolder).Select(r => r.Id).ToList();
            var guard = 0;
            while (frontier.Count > 0 && guard++ < 200)
            {
                var children = await _db.LibraryItems
                    .Where(i => i.ParentId != null && frontier.Contains(i.ParentId.Value) && i.CompanyID == companyId && i.DeletedAt == null)
                    .ToListAsync();
                if (children.Count == 0) break;
                toDelete.AddRange(children);
                frontier = children.Where(c => c.IsFolder).Select(c => c.Id).ToList();
            }

            var now = DateTime.Now;
            foreach (var it in toDelete) { it.DeletedAt = now; it.updatedBy = empId; it.UpdatedAt = now; }
            await _db.SaveChangesAsync();
            return roots.Count;
        }

        public Task<LibraryItem?> GetFileAsync(int companyId, int id) =>
            _db.LibraryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.CompanyID == companyId && !i.IsFolder && i.DeletedAt == null);

        public Task<List<LibraryItem>> GetFilesAsync(int companyId, IEnumerable<int> ids)
        {
            var set = ids.Distinct().ToList();
            return _db.LibraryItems.AsNoTracking()
                .Where(i => set.Contains(i.Id) && i.CompanyID == companyId && !i.IsFolder && i.DeletedAt == null).ToListAsync();
        }

        public Task<bool> FolderExistsAsync(int companyId, int folderId) =>
            _db.LibraryItems.AsNoTracking().AnyAsync(i => i.Id == folderId && i.CompanyID == companyId && i.IsFolder && i.DeletedAt == null);

        // flat folder list (depth-ordered) for the move picker
        public async Task<List<LibFolderDto>> FolderTreeAsync(int companyId)
        {
            var isEn = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ar";
            var raw = await Live(companyId).Where(i => i.IsFolder)
                .Select(i => new { i.Id, i.Name, i.NameEn, i.ParentId }).ToListAsync();
            var folders = raw.Select(i => new { i.Id, Name = DisplayName(i.Name, i.NameEn, isEn), i.ParentId }).ToList();
            // key by ParentId with 0 = root (int? null can't be a Dictionary key); real folder ids start at 1
            var byParent = folders.GroupBy(f => f.ParentId ?? 0).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name).ToList());
            var outList = new List<LibFolderDto>();
            void Walk(int parentKey, int depth)
            {
                if (!byParent.TryGetValue(parentKey, out var kids)) return;
                foreach (var k in kids) { outList.Add(new LibFolderDto(k.Id, k.Name, depth)); Walk(k.Id, depth + 1); }
            }
            Walk(0, 0);
            return outList;
        }

        // show the English twin when the UI language isn't Arabic (fall back to the base Name)
        private static string DisplayName(string name, string? nameEn, bool isEn) =>
            isEn && !string.IsNullOrWhiteSpace(nameEn) ? nameEn! : name;

        private static string Ext(string name)
        {
            var i = name.LastIndexOf('.');
            return i >= 0 && i < name.Length - 1 ? name[(i + 1)..].ToLowerInvariant() : "";
        }

        private static string SizeText(long bytes)
        {
            if (bytes <= 0) return "0 KB";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double s = bytes; int k = 0;
            while (s >= 1024 && k < u.Length - 1) { s /= 1024; k++; }
            return (k == 0 ? s.ToString("0") : s.ToString("0.#")) + " " + u[k];
        }
    }
}
