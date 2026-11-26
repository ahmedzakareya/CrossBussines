using CrossBuy.BL;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // Company document library / File Manager (Metronic demo39 file-manager clone). Shared per company:
    // every employee in the company browses/uploads/organizes the same tree.
    [SessionValidation]
    public class FileManagerController : Controller
    {
        private readonly IFileManagerService _fm;
        private readonly CrossBuy.BL.Communication.ICommAttachmentService _commAttachments;
        private readonly IWebHostEnvironment _env;
        private readonly CrossBuy.BL.Platform.IBusinessContextAccessor _contexts;
        public FileManagerController(IFileManagerService fm,
            CrossBuy.BL.Communication.ICommAttachmentService commAttachments, IWebHostEnvironment env,
            CrossBuy.BL.Platform.IBusinessContextAccessor contexts)
        { _fm = fm; _commAttachments = commAttachments; _env = env; _contexts = contexts; }

        private async Task<(int empId, int companyId)?> MeAsync()
        {
            CrossBuy.Models.Platform.BusinessContext? context;
            try { context = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted); }
            catch (Exception) { return null; }
            if (context == null || context.CompanyId <= 0 || context.EmployeeId is not > 0) return null;
            return (context.EmployeeId.Value, context.CompanyId);
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
            var result = await _fm.ListAsync(me.Value.companyId, folderId, q);
            var context = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
            if (context == null || context.CompanyId != me.Value.companyId) return Unauthorized();

            var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(
                HttpContext.RequestServices.GetRequiredService<CrossDbContext>(), context,
                result.items.Select(item => item.ownerEmployeeId), HttpContext.RequestAborted);
            return Json(result with
            {
                items = result.items.Select(item => item with
                {
                    ownerAvatarUrl = photos.GetValueOrDefault(item.ownerEmployeeId)
                }).ToList()
            });
        }

        [HttpGet]
        public async Task<IActionResult> ChatAttachments(string? q)
        {
            var me = await MeAsync(); if (me == null) return Unauthorized();
            var context = await _contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
            if (context == null || context.CompanyId != me.Value.companyId) return Unauthorized();

            var legacy = await _fm.ListChatAttachmentsAsync(me.Value.companyId, me.Value.empId, q);
            var isArabic = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
            var communication = await _commAttachments.ListAccessibleAsync(context, q, HttpContext.RequestAborted);
            var legacyPhotos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(
                HttpContext.RequestServices.GetRequiredService<CrossDbContext>(), context,
                legacy.Select(item => item.ownerEmployeeId), HttpContext.RequestAborted);
            var rows = legacy.Select(item => new
                {
                    id = (long)item.id, isFolder = false, name = item.name, nameAr = item.nameAr,
                    nameEn = item.nameEn, size = item.size, sizeText = item.sizeText,
                    modified = item.modified, ext = item.ext, url = item.url,
                    ownerName = item.ownerName,
                    ownerAvatarUrl = legacyPhotos.GetValueOrDefault(item.ownerEmployeeId), isChatAttachment = true
                })
                .Concat(communication.Select(item => new
                {
                    id = item.AttachmentId, isFolder = false,
                    name = item.FileName, nameAr = item.FileName, nameEn = (string?)null,
                    size = item.SizeBytes, sizeText = FormatSize(item.SizeBytes),
                    modified = item.UploadedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                    ext = Path.GetExtension(item.FileName).TrimStart('.'),
                    // Content is streamed by DocumentsController, which re-checks thread/comment access.
                    // THE ROUTE IS SPELLED ONCE. This read $"/Documents/comm/{id}/content" literally, which
                    // is the same URL CommPanel builds for the conversation panel - two spellings of one
                    // route, and the kind that drifts silently because changing the action's template
                    // cannot break a string literal.
                    url = CrossBuy.BL.Communication.CommPanel.DeliveryUrl(item.AttachmentId),
                    ownerName = item.UploadedBy.Display(isArabic),
                    ownerAvatarUrl = item.UploadedBy.AvatarUrl, isChatAttachment = true
                }));
            return Json(rows.OrderByDescending(row => row.modified));
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
            _ => $"{bytes / (1024d * 1024):0.#} MB"
        };

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
        public async Task<IActionResult> Download(int id, bool preview = false, bool download = false)
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
            // Old library rows can have no MIME type (or the browser's generic octet-stream value),
            // which makes a valid PDF look like an empty iframe. Infer only from the stored filename;
            // the inline-preview allow-list below still decides whether the browser may render it.
            var contentType = file.ContentType;
            if (string.IsNullOrWhiteSpace(contentType) ||
                contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                if (provider.TryGetContentType(file.Name, out var inferred)) contentType = inferred;
            }
            contentType ??= "application/octet-stream";

            // Preview remains an explicit, safe inline path. The download button is a separate user
            // choice and receives a filename disposition so opening the modal never forces a download.
            if (preview && !CrossBuy.BL.ChatAttachments.IsSafeInlinePreview(file.Name, contentType))
                return NotFound();
            return download
                ? PhysicalFile(phys, contentType, file.Name, enableRangeProcessing: true)
                : PhysicalFile(phys, contentType, enableRangeProcessing: true);
        }

        private static List<int> ParseIds(string? ids) =>
            (ids ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
    }
}
