using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE COMPANY'S OWN MARK, AND THE BRANCH'S — resolved at render time, embedded as bytes.
    //
    // Report Studio has offered a "شعار الشركة" tool since it shipped: it drops an Image element with
    // ImageRole = CompanyLogo and NO asset id, and the renderer then drew nothing, because the only
    // picture it knew how to draw was one uploaded into the report asset store. The toolbox promised a
    // feature the pipeline did not have. This is that feature.
    //
    // WHY NOT "just upload the logo as a report asset". Because a report asset is a picture SOMEONE
    // CHOSE for one template, and the company logo is a fact about the tenant. Uploading it would mean
    // every template carries a copy, a company that changes its logo has to re-author its documents,
    // and the second company on the platform prints the first one's mark. The role says WHICH logo;
    // the tenant's own record says what it is.
    //
    // A DATA URI, NEVER A PATH OR A URL, for the same reason every other image in a report is one: the
    // PDF converter is a headless browser that may have no route back to the application, and the
    // renderers' rule is that a document is self-contained. The renderer still never sees a path.
    //
    // THE FILE READ IS THE DANGEROUS PART, and it is fenced here rather than trusted:
    //
    //   * the value comes from the COMPANY'S OWN ROW, reached through the resolved context — never from
    //     a parameter, a template or anything a request can name;
    //   * the path is normalised, refused if it escapes wwwroot (traversal, a rooted path, a drive),
    //     and re-checked against the canonical web root AFTER combining;
    //   * only image extensions are served, each with a fixed content type — an .html or .js on the end
    //     of the column cannot become a data: document;
    //   * a file over the cap is skipped rather than embedded, because a 12MB logo on a 400-row register
    //     is a memory event, not a design choice.
    //
    // CACHED BY PATH AND STAMP. A register printing 400 rows resolves its logos once; a logo that is
    // replaced on disk invalidates itself, because the key carries the file's write time and length.
    // ============================================================================================
    public interface IReportOrgImageProvider
    {
        // Role → data URI, for the roles this tenant can actually fill. Missing roles are simply absent:
        // an unresolved logo renders as the empty box it already was.
        Task<IReadOnlyDictionary<ReportImageRole, string>> ResolveAsync(
            BusinessContext context, CancellationToken cancellationToken = default);
    }

    public sealed class ReportOrgImageProvider : IReportOrgImageProvider
    {
        // 2 MB. A letterhead mark is tens of kilobytes; anything past this is a photograph someone
        // uploaded by mistake, and embedding it would be paid for on every page of every document.
        public const int MaxBytes = 2 * 1024 * 1024;

        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".svg"] = "image/svg+xml",
        };

        // path|writeTimeTicks|length → data URI. Static because the provider is scoped and the answer is
        // a property of the FILE, not of the request.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new();

        private readonly CrossDbContext _db;
        private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;

        public ReportOrgImageProvider(CrossDbContext db, Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
        {
            _db = db;
            _env = env;
        }

        public async Task<IReadOnlyDictionary<ReportImageRole, string>> ResolveAsync(
            BusinessContext context, CancellationToken cancellationToken = default)
        {
            var map = new Dictionary<ReportImageRole, string>();
            if (context is null || context.CompanyId <= 0) return map;

            var company = await _db.Companies.AsNoTracking()
                .Where(c => c.CompanyID == context.CompanyId)
                .Select(c => c.CompanyImage)
                .FirstOrDefaultAsync(cancellationToken);

            if (DataUri(company) is string companyLogo)
                map[ReportImageRole.CompanyLogo] = companyLogo;

            // THE BRANCH IS READ WITH ITS COMPANY, not by id alone. The id comes from the resolved
            // context and not from a request, but a read that reaches a row without its tenancy
            // predicate is one refactor away from being the hole.
            if (context.BranchId is > 0)
            {
                var branch = await _db.Branches.AsNoTracking()
                    .Where(b => b.ID == context.BranchId.Value && b.CompanyID == context.CompanyId)
                    .Select(b => b.ImageUrl)
                    .FirstOrDefaultAsync(cancellationToken);

                if (DataUri(branch) is string branchLogo)
                    map[ReportImageRole.BranchLogo] = branchLogo;
            }

            return map;
        }

        // A STORED PATH IS NOT A URL AND NOT A FILE HANDLE. Returns null for anything it will not serve —
        // there is no error path here on purpose: a missing or refused logo is an empty box, and a report
        // that fails to render because a picture moved would be worse than one printed without it.
        private string? DataUri(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;

            var rel = stored.Replace('\\', '/').Trim();
            if (rel.Length == 0) return null;

            // An absolute URL is not ours to fetch — that is the SSRF the validator refuses for assets, and
            // it would be no better arriving through a column.
            if (rel.Contains("://", StringComparison.Ordinal)) return null;

            rel = rel.TrimStart('/');
            if (rel.Length == 0) return null;
            if (rel.Contains("..", StringComparison.Ordinal)) return null;
            if (Path.IsPathRooted(rel) || rel.Contains(':')) return null;

            var ext = Path.GetExtension(rel);
            if (!ContentTypes.TryGetValue(ext, out var contentType)) return null;

            var root = _env.WebRootPath;
            if (string.IsNullOrWhiteSpace(root)) return null;

            string full, canonicalRoot;
            try
            {
                canonicalRoot = Path.GetFullPath(root);
                full = Path.GetFullPath(Path.Combine(canonicalRoot, rel));
            }
            catch { return null; }

            // AFTER combining, not before. The checks above reject the obvious shapes; this one is what
            // actually holds — whatever the string was, the file being read is inside the web root.
            if (!full.StartsWith(
                    canonicalRoot.EndsWith(Path.DirectorySeparatorChar) ? canonicalRoot : canonicalRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return null;

            FileInfo info;
            try
            {
                info = new FileInfo(full);
                if (!info.Exists || info.Length == 0 || info.Length > MaxBytes) return null;
            }
            catch { return null; }

            var key = full + "|" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "|" + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Cache.TryGetValue(key, out var cached)) return cached;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(full); }
            catch { return null; }

            var uri = "data:" + contentType + ";base64," + Convert.ToBase64String(bytes);

            // Bounded on purpose. This is a per-tenant handful of files in practice; the clear is the
            // cheap way to guarantee it stays that way without a cache dependency.
            if (Cache.Count > 64) Cache.Clear();
            Cache[key] = uri;
            return uri;
        }
    }
}
