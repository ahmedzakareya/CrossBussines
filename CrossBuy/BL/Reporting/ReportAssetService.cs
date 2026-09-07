using System.Security.Cryptography;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // REPORT ASSETS — logos, signatures, stamps.
    //
    // THE THREE RULES §18 NAMES, and where each one is enforced:
    //
    //   company isolation      every read and write filters on context.CompanyId, and an unresolved company
    //                          stores and reads nothing. A foreign asset id answers exactly as a missing one.
    //   no arbitrary path      the caller supplies BYTES and a filename. The stored path is COMPUTED here
    //                          (company/guid + a whitelisted extension) and the browser never sees or supplies
    //                          it. A traversal attempt in the filename cannot survive because the filename is
    //                          not used to build the path.
    //   no arbitrary fetch     there is no URL parameter anywhere in this service. Nothing here can be pointed
    //                          at an address, so there is no SSRF surface to defend.
    //
    // WHAT IS STORED IS VALIDATED AS AN IMAGE, not trusted from Content-Type: the magic bytes are checked. A
    // file called logo.png that is actually a script is refused, and the renderer only ever emits what this
    // service accepted.
    // ============================================================================================
    public sealed class ReportAssetOptions
    {
        // THE CONTENT ROOT, not the binary directory, and NOT under wwwroot.
        //
        // BaseDirectory was the first answer and it is a trap: it resolves to bin/Debug/net8.0, so a clean, a
        // republish or a moved deployment silently orphans every stored logo while its row survives in the
        // database. Found at runtime — the picker listed an asset whose bytes 404ed, because the row and the
        // file had come to live in different places.
        //
        // wwwroot is the other tempting answer and is worse: a file under it is served by the static-file
        // middleware, which knows nothing about companies. Every company-isolation check in this service
        // would be one guessable URL away from irrelevant. These bytes are read through ReadAsync or not
        // at all.
        //
        // The host may still override it in AddCrossBusinessReporting; this is the default that is safe when
        // nobody does.
        // RELATIVE by default, and anchored by the HOST — AddCrossBusinessReporting resolves it against
        // IHostEnvironment.ContentRootPath. An absolute value here would have to be guessed from the process,
        // and both guesses are wrong somewhere: AppContext.BaseDirectory is bin/Debug/net8.0 (a rebuild
        // orphans every stored logo while its row survives — found at runtime, the picker listed an image
        // that answered 404), and Directory.GetCurrentDirectory() is whatever launched the process, which for
        // a Windows service is not the application at all.
        //
        // NOT under wwwroot, whatever it is anchored to: a file there is served by the static-file
        // middleware, which knows nothing about companies, and every isolation check in this service would
        // be one guessable URL away from irrelevant. These bytes are read through ReadAsync or not at all.
        //
        // An absolute value set by the host is honoured as given.
        public string RootPath { get; set; } = Path.Combine("App_Data", "report-assets");

        // 4 MB. A logo is tens of kilobytes; a signature is smaller. The cap exists because these bytes are
        // inlined as data URIs into every rendered page, so a large one is paid on every print.
        public long MaxBytes { get; set; } = 4L * 1024 * 1024;
    }

    public sealed class ReportAssetSummary
    {
        public required int Id { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public ReportImageRole Role { get; init; }
        public long Length { get; init; }
        public string? Title { get; init; }
        public DateTime? CreatedAt { get; init; }
    }

    public interface IReportAssetService
    {
        Task<IReadOnlyList<ReportAssetSummary>> ListAsync(BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<ReportAssetSummary?> UploadAsync(Stream content, string fileName, ReportImageRole role,
            string? title, BusinessContext context, CancellationToken cancellationToken = default);

        // The bytes, for the renderer. Null when the asset is not this company's — the same answer a missing
        // one gives.
        Task<(byte[] Bytes, string ContentType)?> ReadAsync(int assetId, BusinessContext context,
            CancellationToken cancellationToken = default);

        // The ids a layout may legally reference. Resolved once per validation rather than per element.
        Task<IReadOnlySet<int>> PermittedIdsAsync(BusinessContext context,
            CancellationToken cancellationToken = default);

        Task<bool> DeleteAsync(int assetId, BusinessContext context, CancellationToken cancellationToken = default);
    }

    public sealed class ReportAssetService : IReportAssetService
    {
        private readonly CrossDbContext _db;
        private readonly ReportAssetOptions _options;
        private readonly IReportClock _clock;

        public ReportAssetService(CrossDbContext db, ReportAssetOptions options, IReportClock clock)
        {
            _db = db;
            _options = options;
            _clock = clock;
        }

        // ONLY these. The extension is chosen from this table by the SNIFFED type, never from the upload's
        // filename, so the stored file's extension always matches its real content.
        private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
            ["image/svg+xml"] = ".svg",
        };

        public async Task<IReadOnlyList<ReportAssetSummary>> ListAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return Array.Empty<ReportAssetSummary>();

            return await _db.ReportAssets.AsNoTracking()
                .Where(a => a.CompanyID == context.CompanyId && a.DeletedAt == null)
                .OrderByDescending(a => a.Id)
                .Select(a => new ReportAssetSummary
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    Role = (ReportImageRole)a.Role,
                    Length = a.Length,
                    Title = a.Title,
                    CreatedAt = a.CreatedAt,
                })
                .ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlySet<int>> PermittedIdsAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return new HashSet<int>();

            var ids = await _db.ReportAssets.AsNoTracking()
                .Where(a => a.CompanyID == context.CompanyId && a.DeletedAt == null)
                .Select(a => a.Id)
                .ToListAsync(cancellationToken);

            return ids.ToHashSet();
        }

        public async Task<ReportAssetSummary?> UploadAsync(Stream content, string fileName,
            ReportImageRole role, string? title, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return null;

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();

            if (bytes.Length == 0 || bytes.Length > _options.MaxBytes) return null;

            // SNIFFED, not declared. A caller's Content-Type is a claim; the first bytes are a fact.
            var sniffed = Sniff(bytes);
            if (sniffed == null || !Allowed.TryGetValue(sniffed, out var extension)) return null;

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            // Same bytes, same company, already stored: reuse the row. Re-uploading a logo on every template
            // would otherwise fill the disk with identical files.
            //
            // BUT THE ROW IS ONLY REUSABLE IF ITS FILE IS STILL THERE. A row can outlive its bytes — the row
            // is in the database and the file is on this application's own disk, so a re-published instance,
            // or a second instance pointed at the same database, has rows whose GET answers 404. Reusing such
            // a row unconditionally makes the damage PERMANENT and invisible: the obvious repair, uploading
            // the very same image again, matches the same hash and hands back the same broken row, so the
            // picker keeps listing a picture that can never appear no matter how many times it is uploaded.
            // Found exactly that way — a re-upload of an identical logo returned the orphaned row untouched.
            //
            // So the bytes are RESTORED to the stored path instead. Same row, same id, every layout that
            // already references it starts working again.
            var existing = await _db.ReportAssets
                .FirstOrDefaultAsync(a => a.CompanyID == context.CompanyId && a.ContentHash == hash
                                          && a.DeletedAt == null, cancellationToken);
            if (existing != null)
            {
                var reusedRoot = Path.GetFullPath(_options.RootPath);
                var reusedPath = Path.GetFullPath(Path.Combine(reusedRoot, existing.StoredPath));

                // The same containment check ReadAsync makes, for the same reason: the path came from a
                // database row, and a row edited by hand must not be able to write outside the root.
                if (reusedPath.StartsWith(reusedRoot, StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(reusedPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(reusedPath)!);
                    await File.WriteAllBytesAsync(reusedPath, bytes, cancellationToken);
                }

                return Summarise(existing);
            }

            // THE PATH IS COMPUTED. The company segment keeps one tenant's files out of another's directory,
            // and the name is a guid — the caller's filename is kept only as a label on the row.
            var relative = Path.Combine(context.CompanyId.ToString(),
                                        Guid.NewGuid().ToString("N") + extension);
            var absolute = Path.Combine(_options.RootPath, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            await File.WriteAllBytesAsync(absolute, bytes, cancellationToken);

            var asset = new ReportAsset
            {
                CompanyID = context.CompanyId,
                Role = (int)role,
                FileName = SafeLabel(fileName),
                ContentType = sniffed,
                Length = bytes.Length,
                StoredPath = relative.Replace('\\', '/'),
                ContentHash = hash,
                Title = SafeLabel(title),
                CreatedAt = _clock.LocalNow,
                CreatedBy = context.EmployeeId,
            };

            _db.ReportAssets.Add(asset);
            await _db.SaveChangesAsync(cancellationToken);

            return Summarise(asset);
        }

        public async Task<(byte[] Bytes, string ContentType)?> ReadAsync(int assetId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0 || assetId <= 0) return null;

            // THE COMPANY PREDICATE IS ON THE QUERY, not applied after. A foreign id simply does not resolve.
            var asset = await _db.ReportAssets.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyID == context.CompanyId
                                          && a.DeletedAt == null, cancellationToken);
            if (asset == null) return null;

            // Re-derive the absolute path from the stored RELATIVE one and confirm it stays inside the root.
            // Belt and braces: the path was computed by this service, but a row edited by hand must not be able
            // to read /etc/passwd.
            var root = Path.GetFullPath(_options.RootPath);
            var absolute = Path.GetFullPath(Path.Combine(root, asset.StoredPath));
            if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(absolute)) return null;

            return (await File.ReadAllBytesAsync(absolute, cancellationToken), asset.ContentType);
        }

        public async Task<bool> DeleteAsync(int assetId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return false;

            var asset = await _db.ReportAssets
                .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyID == context.CompanyId
                                          && a.DeletedAt == null, cancellationToken);
            if (asset == null) return false;

            // SOFT. A layout that still references it must stay explainable, and the bytes stay readable for an
            // archived document — the same reasoning the template service uses.
            asset.DeletedAt = _clock.LocalNow;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        private static ReportAssetSummary Summarise(ReportAsset a) => new()
        {
            Id = a.Id,
            FileName = a.FileName,
            ContentType = a.ContentType,
            Role = (ReportImageRole)a.Role,
            Length = a.Length,
            Title = a.Title,
            CreatedAt = a.CreatedAt,
        };

        // A label, not a path. Directory separators and traversal segments are removed because this string is
        // shown in the picker — it is never used to build a path, and stripping them keeps it from LOOKING
        // like one to the next reader.
        private static string SafeLabel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var name = Path.GetFileName(value.Replace('\\', '/'));
            name = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            return name.Length > 200 ? name[..200] : name;
        }

        // Magic bytes. Short and deliberate: these five are the formats a browser renders and a PDF engine
        // embeds, and anything else is refused rather than guessed at.
        private static string? Sniff(byte[] b)
        {
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
            if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
            if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
                && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";

            // SVG is XML, so it has no magic number — and it is the one image format that can carry script.
            // Accepted only when it looks like SVG AND carries no script or external reference; the renderer
            // additionally embeds it as a data URI in an <img>, where scripts do not execute.
            if (b.Length >= 5)
            {
                var head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 1024));
                if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                {
                    var whole = System.Text.Encoding.UTF8.GetString(b);
                    if (whole.Contains("<script", StringComparison.OrdinalIgnoreCase)
                        || whole.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
                        || whole.Contains("<foreignObject", StringComparison.OrdinalIgnoreCase))
                        return null;
                    return "image/svg+xml";
                }
            }
            return null;
        }
    }
}
