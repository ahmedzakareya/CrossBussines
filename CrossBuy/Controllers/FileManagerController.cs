using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
    // Company document library / File Manager (Metronic demo39 file-manager clone). Shared per company:
    // every employee in the company browses/uploads/organizes the same tree.
    [SessionValidation]
    public class FileManagerController : Controller
    {
        private readonly IFileManagerService _fm;
        private readonly IEmployeeService _employees;
        private readonly IWebHostEnvironment _env;
        private readonly CrossDbContext _db;
        public FileManagerController(IFileManagerService fm, IEmployeeService employees, IWebHostEnvironment env, CrossDbContext db)
        { _fm = fm; _employees = employees; _env = env; _db = db; }

        private async Task<(int empId, int companyId)?> MeAsync()
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var emp = await _employees.GetEmployeeByUserIdAsync(uid);
            if (emp == null) return null;
            var companyId = await _db.Employee.AsNoTracking().Where(e => e.ID == emp.ID).Select(e => e.EmpCompanyID).FirstOrDefaultAsync();
            return (emp.ID, companyId);
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? folderId)
        {
            var me = await MeAsync(); if (me == null) return RedirectToAction("Login", "Account");
            // optional deep-link (e.g. from the email sidebar) → open this folder on load
            ViewBag.InitialFolder = folderId.HasValue && await _fm.FolderExistsAsync(me.Value.companyId, folderId.Value) ? folderId : null;
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> List(int? folderId, string? q)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await _fm.ListAsync(me.Value.companyId, folderId, q));
        }

        [HttpGet]
        public async Task<IActionResult> FolderTree()
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            return Json(await _fm.FolderTreeAsync(me.Value.companyId));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateFolder(int? parentId, string name, string? nameEn)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var id = await _fm.CreateFolderAsync(me.Value.companyId, me.Value.empId, parentId, name, nameEn);
            return Json(new { ok = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Rename(int id, string name, string? nameEn)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var ok = await _fm.RenameAsync(me.Value.companyId, me.Value.empId, id, name, nameEn);
            return Json(new { ok });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Move(string ids, int? targetFolderId)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var idList = ParseIds(ids);
            var moved = await _fm.MoveAsync(me.Value.companyId, me.Value.empId, idList, targetFolderId);
            return Json(new { ok = true, moved });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string ids)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var deleted = await _fm.DeleteAsync(me.Value.companyId, me.Value.empId, ParseIds(ids));
            return Json(new { ok = true, deleted });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(268435456)] // 256 MB
        public async Task<IActionResult> Upload(int? parentId, List<IFormFile> files, string? customName, string? customNameEn)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            if (files == null || files.Count == 0) return Json(new { ok = false, count = 0 });

            var dir = System.IO.Path.Combine(_env.WebRootPath, "uploads", "library", me.Value.companyId.ToString());
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

            var count = 0;
            foreach (var f in files)
            {
                if (f == null || f.Length == 0) continue;
                var origName = System.IO.Path.GetFileName(f.FileName);
                var ext = System.IO.Path.GetExtension(origName);
                var stored = Guid.NewGuid().ToString("N") + ext;
                var full = System.IO.Path.Combine(dir, stored);
                using (var fs = new FileStream(full, FileMode.Create)) await f.CopyToAsync(fs);
                var webPath = "/uploads/library/" + me.Value.companyId + "/" + stored;

                // a single upload may be given a custom name (Arabic + optional English); keep the extension if omitted
                var displayName = origName;
                string? displayNameEn = null;
                if (files.Count == 1)
                {
                    if (!string.IsNullOrWhiteSpace(customName))
                    {
                        var cn = customName.Trim();
                        if (string.IsNullOrEmpty(System.IO.Path.GetExtension(cn))) cn += ext;
                        displayName = cn;
                    }
                    if (!string.IsNullOrWhiteSpace(customNameEn))
                    {
                        var cne = customNameEn.Trim();
                        if (string.IsNullOrEmpty(System.IO.Path.GetExtension(cne))) cne += ext;
                        displayNameEn = cne;
                    }
                }
                await _fm.AddFileAsync(me.Value.companyId, me.Value.empId, parentId,
                    displayName, displayNameEn, webPath, f.ContentType, f.Length);
                count++;
            }
            return Json(new { ok = count > 0, count });
        }

        [HttpGet]
        public async Task<IActionResult> Download(int id)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var file = await _fm.GetFileAsync(me.Value.companyId, id);
            if (file == null || string.IsNullOrEmpty(file.StoredPath)) return NotFound();
            // Containment BEFORE touching the filesystem: prove the stored path resolves inside the
            // document library. Traversal and rooted stored paths are refused rather than trimmed —
            // see CrossBuy.BL.FileManagerPaths. A refusal is NotFound, so no physical path is leaked.
            if (!CrossBuy.BL.FileManagerPaths.TryResolveLibraryFile(_env.WebRootPath, file.StoredPath, out var phys))
            {
                return NotFound();
            }
            if (!System.IO.File.Exists(phys)) return NotFound();
            return PhysicalFile(phys, file.ContentType ?? "application/octet-stream", file.Name);
        }

        private static List<int> ParseIds(string? ids) =>
            (ids ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
    }
}
