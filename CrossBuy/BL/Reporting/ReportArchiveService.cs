using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // Reporting Platform (ADR-037) — THE ARCHIVE.
    //
    // Split in two on purpose:
    //   * the ROW (ReportArchiveEntry) is metadata in the database — what report, which template version, which
    //     parameters, what hash, what retention;
    //   * the BYTES live behind IReportArchiveStore.
    //
    // That split is what makes the store swappable for blob storage without a schema change, and it keeps
    // multi-megabyte artifacts out of the transactional database, where they would bloat every backup.
    //
    // CONTENT-ADDRESSED. The stored path is derived from the SHA-256 of the bytes, so archiving the same artifact
    // twice writes one file. A month-end pack regenerated five times because someone kept clicking costs one
    // file and five rows, and the five rows are the honest record of five requests.
    //
    // IMMUTABLE. Nothing rewrites an artifact. An archived document is the evidence that a report was produced
    // with a given template version and parameters — the same "history stays whole" instinct as our
    // reverse-never-delete rule for the ledger.
    // ============================================================================================

    public sealed class ReportArchiveOptions
    {
        // Filesystem root for the default store. Set from the host environment at registration
        // (wwwroot/uploads/reports) so it sits with the product's other uploads; overridable for tests.
        public string RootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "uploads", "reports");

        // Default retention for a newly archived artifact. null = keep indefinitely, which is the shipped default:
        // choosing a deletion policy for someone else's financial documents is not this platform's call.
        public int? DefaultRetentionDays { get; set; }

        // Refuse to archive an artifact bigger than this. A 400 MB "report" is a runaway query, and storing it
        // silently is how a disk fills at month end.
        public long MaxArtifactBytes { get; set; } = 64 * 1024 * 1024;
    }

    public interface IReportArchiveStore
    {
        string StoreName { get; }

        // Persists the bytes and returns an OPAQUE path for the row. Must be idempotent for identical content:
        // called twice with the same hash it returns the same path and does not duplicate the data.
        Task<string> PutAsync(int companyId, string contentHash, string extension, byte[] content,
            CancellationToken cancellationToken = default);

        // null = the bytes are gone (retention swept, disk restored without uploads, store migrated). Callers
        // must handle null: an archive row whose bytes vanished is a fact to report, not a crash.
        Task<byte[]?> GetAsync(string storedPath, CancellationToken cancellationToken = default);

        Task<bool> ExistsAsync(string storedPath, CancellationToken cancellationToken = default);

        Task<bool> DeleteAsync(string storedPath, CancellationToken cancellationToken = default);
    }

    // The shipped store: the local filesystem, fanned out by company and hash prefix.
    //
    // Layout: <root>/<companyId>/<first two hex chars of hash>/<hash>.<ext>
    // The two-character fan-out keeps any one directory to a few thousand entries; a flat directory with 200k
    // files is measurably slow to enumerate on both NTFS and ext4.
    public class FileSystemReportArchiveStore : IReportArchiveStore
    {
        private readonly ReportArchiveOptions _options;
        private readonly ILogger<FileSystemReportArchiveStore> _logger;

        public FileSystemReportArchiveStore(ReportArchiveOptions options,
            ILogger<FileSystemReportArchiveStore> logger)
        {
            _options = options;
            _logger = logger;
        }

        public string StoreName => "filesystem";

        public async Task<string> PutAsync(int companyId, string contentHash, string extension, byte[] content,
            CancellationToken cancellationToken = default)
        {
            var relative = RelativePath(companyId, contentHash, extension);
            var absolute = Absolute(relative);

            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

            // Content-addressed, so an existing file with this hash IS this artifact. Rewriting it would be
            // pointless work and a window in which a concurrent reader sees a half-written file.
            if (File.Exists(absolute)) return relative;

            // Written to a temp name and MOVED into place: a reader can then only ever see a complete file, even
            // if the process dies mid-write. A partially written PDF that looks like a valid archive entry is the
            // failure this avoids.
            var temp = absolute + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            await File.WriteAllBytesAsync(temp, content, cancellationToken);
            try
            {
                File.Move(temp, absolute, overwrite: false);
            }
            catch (IOException)
            {
                // Another request archived the same bytes first. That is the expected race for content-addressed
                // storage and it is benign — the file is already correct.
                File.Delete(temp);
            }

            return relative;
        }

        public async Task<byte[]?> GetAsync(string storedPath, CancellationToken cancellationToken = default)
        {
            var absolute = Absolute(storedPath);
            if (!File.Exists(absolute))
            {
                _logger.LogWarning("Archived report artifact is missing from the store: {Path}", storedPath);
                return null;
            }
            return await File.ReadAllBytesAsync(absolute, cancellationToken);
        }

        public Task<bool> ExistsAsync(string storedPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(File.Exists(Absolute(storedPath)));

        public Task<bool> DeleteAsync(string storedPath, CancellationToken cancellationToken = default)
        {
            var absolute = Absolute(storedPath);
            if (!File.Exists(absolute)) return Task.FromResult(false);
            File.Delete(absolute);
            return Task.FromResult(true);
        }

        private static string RelativePath(int companyId, string contentHash, string extension)
        {
            var hash = contentHash.ToLowerInvariant();
            var prefix = hash.Length >= 2 ? hash[..2] : "00";
            return Path.Combine(companyId.ToString(), prefix, $"{hash}.{extension}");
        }

        // Path containment check. StoredPath comes out of the database, and a database value is not a trusted
        // path: a row carrying "..\..\web.config" must not be readable through the archive. Rejecting anything
        // that does not resolve under the root is the guard.
        private string Absolute(string relativePath)
        {
            var root = Path.GetFullPath(_options.RootPath);
            var combined = Path.GetFullPath(Path.Combine(root, relativePath));

            if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ReportingException(
                    $"Archive path '{relativePath}' resolves outside the archive root and was refused.");

            return combined;
        }
    }

    public sealed class ReportArchiveInfo
    {
        public required long Id { get; init; }
        public required string ReportCode { get; init; }
        public int? TemplateId { get; init; }
        public int? TemplateVersionNo { get; init; }
        public long? RunId { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public long Length { get; init; }
        public required string ContentHash { get; init; }
        public DateTime? RetainUntil { get; init; }
        public DateTime? CreatedAt { get; init; }
        public int? CreatedBy { get; init; }

        // false = the row exists but the bytes do not. Surfaced so a UI shows "unavailable" rather than offering
        // a download that 404s.
        public bool BytesPresent { get; init; } = true;
    }

    public interface IReportArchiveService
    {
        Task<long?> ArchiveAsync(ReportArtifact artifact, ReportDefinition definition, int? templateId,
            int? templateVersionNo, long? runId, BusinessContext context, int? retentionDays = null,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ReportArchiveInfo>> ListAsync(string? reportCode, BusinessContext context,
            int take = 100, CancellationToken cancellationToken = default);

        Task<ReportArchiveInfo?> GetAsync(long archiveEntryId, BusinessContext context,
            CancellationToken cancellationToken = default);

        // null = not found, not permitted, or the bytes are gone. The three are distinguished in the log, not in
        // the return type — a caller has one thing to do in all three cases.
        Task<ReportArtifact?> RetrieveAsync(long archiveEntryId, BusinessContext context,
            CancellationToken cancellationToken = default);

        // Soft-deletes rows whose RetainUntil has passed and removes their bytes. Returns how many were swept.
        // NOT scheduled by this slice — see ADR-037 §Scheduling. It is callable so retention is testable and so an
        // operator can run it, but nothing invokes it automatically yet.
        Task<int> SweepExpiredAsync(BusinessContext context, CancellationToken cancellationToken = default);
    }

    public class ReportArchiveService : IReportArchiveService
    {
        private readonly CrossDbContext _db;
        private readonly IReportArchiveStore _store;
        private readonly IReportCatalog _catalog;
        private readonly IReportAuthorizationService _authorization;
        private readonly ReportArchiveOptions _options;
        private readonly IReportClock _clock;
        private readonly ILogger<ReportArchiveService> _logger;

        public ReportArchiveService(CrossDbContext db, IReportArchiveStore store, IReportCatalog catalog,
            IReportAuthorizationService authorization, ReportArchiveOptions options, IReportClock clock,
            ILogger<ReportArchiveService> logger)
        {
            _db = db;
            _store = store;
            _catalog = catalog;
            _authorization = authorization;
            _options = options;
            _clock = clock;
            _logger = logger;
        }

        public async Task<long?> ArchiveAsync(ReportArtifact artifact, ReportDefinition definition,
            int? templateId, int? templateVersionNo, long? runId, BusinessContext context,
            int? retentionDays = null, CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return null;

            if (!definition.Capabilities.AllowArchive)
            {
                _logger.LogInformation("Report {ReportCode} does not permit archiving; the request was ignored.",
                    definition.Code);
                return null;
            }

            if (artifact.Length > _options.MaxArtifactBytes)
            {
                _logger.LogWarning(
                    "Report {ReportCode} produced {Bytes} bytes, above the archive limit of {Limit}; it was not " +
                    "archived.", definition.Code, artifact.Length, _options.MaxArtifactBytes);
                return null;
            }

            var extension = Path.GetExtension(artifact.FileName).TrimStart('.');
            if (string.IsNullOrWhiteSpace(extension)) extension = "bin";

            var now = _clock.LocalNow;
            var days = retentionDays ?? _options.DefaultRetentionDays;

            // Bytes FIRST, row second. If the row were written first and the write failed, the archive would
            // contain an entry pointing at nothing; this way a failed row write leaves an orphan file, which is
            // harmless and is reclaimed by re-archiving the same content.
            var storedPath = await _store.PutAsync(context.CompanyId, artifact.ContentHash, extension,
                artifact.Content, cancellationToken);

            var entry = new ReportArchiveEntry
            {
                CompanyID = context.CompanyId,
                ReportCode = definition.Code,
                TemplateId = templateId,
                TemplateVersionNo = templateVersionNo,
                RunId = runId,
                FileName = artifact.FileName,
                ContentType = artifact.ContentType,
                Length = artifact.Length,
                ContentHash = artifact.ContentHash,
                StoredPath = storedPath,
                RetainUntil = days is > 0 ? now.AddDays(days.Value) : null,
                CreatedBy = context.EmployeeId,
                CreatedAt = now,
            };

            _db.ReportArchiveEntries.Add(entry);
            await _db.SaveChangesAsync(cancellationToken);
            return entry.Id;
        }

        public async Task<IReadOnlyList<ReportArchiveInfo>> ListAsync(string? reportCode, BusinessContext context,
            int take = 100, CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return Array.Empty<ReportArchiveInfo>();

            var isAdmin = await _authorization.IsAdministratorAsync(context, cancellationToken);

            var q = _db.ReportArchiveEntries.AsNoTracking()
                .Where(a => a.CompanyID == context.CompanyId && a.DeletedAt == null);

            // Same rule as history: your own archived documents unless you administer reporting. An archived
            // artifact is a rendered document containing data — broader than a metadata row — so the default is
            // narrower, not wider.
            if (!isAdmin) q = q.Where(a => a.CreatedBy == context.EmployeeId);
            if (!string.IsNullOrWhiteSpace(reportCode)) q = q.Where(a => a.ReportCode == reportCode);

            var rows = await q
                .OrderByDescending(a => a.Id)
                .Take(Math.Clamp(take, 1, 500))
                .ToListAsync(cancellationToken);

            // Presence is NOT probed per row here: 500 filesystem stats on a list screen is the sort of thing that
            // makes a page feel broken. It is checked on retrieval, where it matters.
            return rows.Select(Project).ToList();
        }

        public async Task<ReportArchiveInfo?> GetAsync(long archiveEntryId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            var entry = await LoadPermittedAsync(archiveEntryId, context, cancellationToken);
            if (entry == null) return null;

            var present = await _store.ExistsAsync(entry.StoredPath, cancellationToken);
            var info = Project(entry);
            return new ReportArchiveInfo
            {
                Id = info.Id,
                ReportCode = info.ReportCode,
                TemplateId = info.TemplateId,
                TemplateVersionNo = info.TemplateVersionNo,
                RunId = info.RunId,
                FileName = info.FileName,
                ContentType = info.ContentType,
                Length = info.Length,
                ContentHash = info.ContentHash,
                RetainUntil = info.RetainUntil,
                CreatedAt = info.CreatedAt,
                CreatedBy = info.CreatedBy,
                BytesPresent = present,
            };
        }

        public async Task<ReportArtifact?> RetrieveAsync(long archiveEntryId, BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            var entry = await LoadPermittedAsync(archiveEntryId, context, cancellationToken);
            if (entry == null) return null;

            var bytes = await _store.GetAsync(entry.StoredPath, cancellationToken);
            if (bytes == null) return null;

            return new ReportArtifact
            {
                FileName = entry.FileName,
                ContentType = entry.ContentType,
                Content = bytes,
                ContentHash = entry.ContentHash,
                IsInline = entry.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase),
            };
        }

        public async Task<int> SweepExpiredAsync(BusinessContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.CompanyId <= 0) return 0;

            if (!await _authorization.IsAdministratorAsync(context, cancellationToken))
                throw new ReportingException("Reporting administration is required to sweep the archive.");

            var now = _clock.LocalNow;
            var expired = await _db.ReportArchiveEntries
                .Where(a => a.CompanyID == context.CompanyId && a.DeletedAt == null
                            && a.RetainUntil != null && a.RetainUntil <= now)
                .ToListAsync(cancellationToken);

            if (expired.Count == 0) return 0;

            // Content-addressed storage means several rows can share one file. Deleting the bytes for a row whose
            // hash is still referenced by a LIVE row would silently break that row — so the hash is only removed
            // when nothing live still points at it.
            var hashes = expired.Select(a => a.ContentHash).Distinct().ToList();
            var stillReferenced = await _db.ReportArchiveEntries.AsNoTracking()
                .Where(a => a.CompanyID == context.CompanyId && a.DeletedAt == null
                            && hashes.Contains(a.ContentHash)
                            && !(a.RetainUntil != null && a.RetainUntil <= now))
                .Select(a => a.ContentHash)
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var entry in expired)
            {
                entry.DeletedAt = now;
                entry.updatedBy = context.EmployeeId;
                entry.UpdatedAt = now;

                if (!stillReferenced.Contains(entry.ContentHash))
                    await _store.DeleteAsync(entry.StoredPath, cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Swept {Count} expired report archive entries for company {CompanyId}.",
                expired.Count, context.CompanyId);
            return expired.Count;
        }

        // Company filter + creator/administrator check + the report's own permission, all three. The third matters:
        // an employee's module permission can be revoked AFTER they archived something, and the archive must not
        // become a way to keep reading a report they may no longer run.
        private async Task<ReportArchiveEntry?> LoadPermittedAsync(long id, BusinessContext context,
            CancellationToken cancellationToken)
        {
            if (context.CompanyId <= 0) return null;

            var entry = await _db.ReportArchiveEntries.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id && a.CompanyID == context.CompanyId && a.DeletedAt == null,
                    cancellationToken);
            if (entry == null) return null;

            var isAdmin = await _authorization.IsAdministratorAsync(context, cancellationToken);
            if (!isAdmin && entry.CreatedBy != context.EmployeeId) return null;

            if (_catalog.TryGetDefinition(entry.ReportCode, out var definition) && definition != null)
            {
                var decision = await _authorization.AuthorizeReportAsync(definition, ReportAccessLevel.Run,
                    context, cancellationToken);
                if (!decision.Allowed) return null;
            }

            return entry;
        }

        private static ReportArchiveInfo Project(ReportArchiveEntry a) => new()
        {
            Id = a.Id,
            ReportCode = a.ReportCode,
            TemplateId = a.TemplateId,
            TemplateVersionNo = a.TemplateVersionNo,
            RunId = a.RunId,
            FileName = a.FileName,
            ContentType = a.ContentType,
            Length = a.Length,
            ContentHash = a.ContentHash,
            RetainUntil = a.RetainUntil,
            CreatedAt = a.CreatedAt,
            CreatedBy = a.CreatedBy,
        };
    }
}