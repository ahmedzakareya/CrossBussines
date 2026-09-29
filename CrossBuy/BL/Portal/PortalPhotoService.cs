using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace CrossBuy.BL.Portal
{
    // =============================================================================================
    // PORTAL CONTACT PHOTOGRAPHS — the authority, and the only way the bytes are reachable.
    //
    // WHAT THIS REPLACED, AND WHY IT HAD TO BE REPLACED. The design this converges from wrote the
    // photograph to wwwroot/uploads/portal/{company}/{guid}.jpg, stored that web path in a column,
    // and rendered it straight into an <img src>. PrivateFileGate classified the prefix
    // RequireAuthenticated, which sounds like authorization and is not:
    //
    //   * the gate reads HttpContext.User, which UseAuthentication populates from the DEFAULT
    //     scheme. A portal customer holds the CrossBuy.Portal cookie, which is deliberately NOT the
    //     default - so the customer would have got 404 on their OWN picture;
    //   * and it enforces no company and no customer, so ANY authenticated employee of ANY company
    //     could fetch ANY contact's photograph by knowing the url. A photograph of a named person is
    //     personal data, and "you were signed in as somebody" is not a reason to see it.
    //
    // Teaching the gate two schemes was rejected: a middleware that answers for two audiences with
    // one boolean is exactly how the confusion above happened in the first place.
    //
    // THE BYTES LIVE OUTSIDE wwwroot, under the content root, so they have NO URL AT ALL. That is
    // what makes "the raw path cannot bypass authorization" a structural fact rather than a promise:
    // there is no static route to bypass. The gate keeps its /uploads/portal entry as a backstop for
    // anything that ever writes there again, but nothing in this file depends on it.
    //
    // TWO AUDIENCES, TWO METHODS, NEVER ONE WITH A FLAG. A portal customer and a member of staff are
    // asking different questions with different identities, and a single method taking "isStaff"
    // would put the choice in the caller - which is the caller that must not have it.
    // =============================================================================================

    /// The bytes plus the type the platform VALIDATED at upload. Never a path, and never a URL.
    public sealed record PortalPhotoContent(Stream Stream, string ContentType);

    public interface IPortalPhotoService
    {
        /// The photograph of ONE portal contact, for the PORTAL audience. Null means refuse, and it
        /// covers every reason alike.
        Task<PortalPhotoContent?> OpenForPortalAsync(int portalUserId, CancellationToken ct = default);

        /// The same photograph for the STAFF audience, gated by the CRM read right and the staff
        /// member's own company.
        Task<PortalPhotoContent?> OpenForStaffAsync(int portalUserId, int companyId, CancellationToken ct = default);

        /// Stores an uploaded photograph for a contact of <paramref name="companyId"/> and returns the
        /// stored locator, or null when the upload is refused. The CALLER has already established
        /// staff authority; this method owns the file name, the folder and the accepted types.
        Task<string?> SaveAsync(int companyId, IFormFile photo, CancellationToken ct = default);

        /// Deletes the file a locator names, and refuses any locator that is not this company's own.
        void Delete(int companyId, string? locator);

        /// The extension this platform will accept for a posted content type, or null.
        string? ExtensionFor(string? contentType);
    }

    public sealed class PortalPhotoService : IPortalPhotoService
    {
        private readonly CrossDbContext _db;
        private readonly IPortalContextAccessor _portal;
        private readonly ICrmAccessService _crm;
        private readonly IHostEnvironment _env;

        public PortalPhotoService(
            CrossDbContext db, IPortalContextAccessor portal, ICrmAccessService crm, IHostEnvironment env)
        { _db = db; _portal = portal; _crm = crm; _env = env; }

        // ---- the storage contract, stated once so read and write cannot disagree ----------------

        /// Under the CONTENT root, never the web root. App_Data is the same place the reporting
        /// platform keeps its private assets, and the static-file middleware knows nothing about it.
        private const string RootFolder = "App_Data";
        private const string PhotoFolder = "portal-photos";

        public const long MaxBytes = 4 * 1024 * 1024;

        /// WHAT THE SERVER WILL ACCEPT, decided HERE from a fixed list and never taken from the file
        /// name the browser sent. An upload endpoint that honoured the posted extension would write
        /// whatever it was handed - an .html or an .svg is stored script on our own origin.
        private static readonly (string ContentType, string Extension)[] Allowed =
        {
            ("image/jpeg", ".jpg"),
            ("image/pjpeg", ".jpg"),
            ("image/png",  ".png"),
            ("image/webp", ".webp"),
        };

        /// The type a stored EXTENSION is served as. Derived from the same table, so a file can only
        /// ever be served as one of three image types whatever else is true of it.
        private static string? TypeForExtension(string extension)
            => extension switch
            {
                ".jpg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => null,
            };

        public string? ExtensionFor(string? contentType)
            => Allowed.FirstOrDefault(a =>
                string.Equals(a.ContentType, contentType, StringComparison.OrdinalIgnoreCase)).Extension;

        private string Root => Path.Combine(_env.ContentRootPath, RootFolder, PhotoFolder);

        // ---- reads ------------------------------------------------------------------------------

        // A PORTAL USER MAY READ THEIR OWN PHOTOGRAPH AND NO OTHER.
        //
        // The portal shows one avatar - the signed-in person's - so self is the whole requirement,
        // and anything wider would have to be justified by a screen that does not exist. A contact of
        // the SAME customer is still refused: two people sharing a customer is a commercial fact, not
        // consent to each other's photograph. If a future screen needs another contact's picture, the
        // rule to widen is this one method, with its own test, rather than a parameter here.
        public async Task<PortalPhotoContent?> OpenForPortalAsync(int portalUserId, CancellationToken ct = default)
        {
            var ctx = await _portal.TryGetCurrentAsync(ct);
            if (ctx == null) return null;

            // Not "does this id belong to my customer" - "is this id ME". The comparison is against
            // the resolved identity, so the route value decides nothing.
            if (portalUserId <= 0 || portalUserId != ctx.PortalUserId) return null;

            // The row is re-read rather than trusted from the context, and it is re-read WITH its
            // company and customer in the predicate: a link deactivated a second ago must stop
            // serving the picture on the very next request.
            var row = await _db.Set<PortalUser>().AsNoTracking()
                .Where(p => p.Id == portalUserId
                            && p.CompanyID == ctx.CompanyId
                            && p.CustomerId == ctx.CustomerId
                            && p.IsActive)
                .Select(p => new { p.CompanyID, p.PhotoPath })
                .FirstOrDefaultAsync(ct);

            return row == null ? null : Open(row.CompanyID, row.PhotoPath);
        }

        // STAFF READ. Two independent requirements, and both are about the CALLER rather than the row:
        // the CRM read right, and the caller's own company. A cross-company employee fails the second
        // even holding the first, which is the case the old static url could not express at all.
        public async Task<PortalPhotoContent?> OpenForStaffAsync(
            int portalUserId, int companyId, CancellationToken ct = default)
        {
            if (portalUserId <= 0 || companyId <= 0) return null;

            // "read" and not "manage": looking at a contact list is a read. Requiring manage would
            // mean a colleague who may see the screen cannot see what the screen renders.
            if (!await _crm.CanAsync("read")) return null;

            // Company in the WHERE clause, not checked afterwards: a contact of another company is
            // never materialised, so "belongs to someone else" and "does not exist" look the same.
            var row = await _db.Set<PortalUser>().AsNoTracking()
                .Where(p => p.Id == portalUserId && p.CompanyID == companyId)
                .Select(p => new { p.CompanyID, p.PhotoPath })
                .FirstOrDefaultAsync(ct);

            return row == null ? null : Open(row.CompanyID, row.PhotoPath);
        }

        // ---- the one place a stored locator becomes a file ---------------------------------------

        // THE LOCATOR IS DATA, AND DATA THAT DECIDES WHICH FILE IS OPENED IS CHECKED BEFORE IT IS
        // TRUSTED. Five independent checks, each closing a different way the column could be wrong:
        // a shape that is not ours, a company that is not the row's, a name nobody generated, an
        // extension outside the table, and a resolved path that escaped the root anyway.
        private PortalPhotoContent? Open(int companyId, string? locator)
        {
            if (string.IsNullOrWhiteSpace(locator)) return null;

            // Traversal and rooting are refused outright rather than normalised. A value that tries
            // either is not a photograph this product stored.
            if (locator.Contains("..", StringComparison.Ordinal)) return null;
            if (locator.Contains('\\')) return null;
            if (Path.IsPathRooted(locator)) return null;

            // Exactly "{company}/{name}", with the company from the ROW. A locator naming another
            // company is refused even though the file would have opened.
            var parts = locator.Split('/', StringSplitOptions.None);
            if (parts.Length != 2) return null;
            if (!int.TryParse(parts[0], out var locatorCompany) || locatorCompany != companyId) return null;

            var name = parts[1];
            if (!IsGeneratedName(name)) return null;

            var type = TypeForExtension(Path.GetExtension(name).ToLowerInvariant());
            if (type == null) return null;

            var root = Path.GetFullPath(Root);
            var full = Path.GetFullPath(Path.Combine(root, companyId.ToString(), name));

            // BELT AND BRACES, and it is not redundant: the checks above reason about the STRING, and
            // this one reasons about where the filesystem actually resolved it to.
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!File.Exists(full)) return null;

            try
            {
                return new PortalPhotoContent(
                    new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read), type);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file the server cannot read is a refusal, not a 500: the caller learns nothing
                // either way, which is the same answer every other failure gives.
                return null;
            }
        }

        /// The shape THIS SERVICE writes and nothing else: 32 lower-case hex characters and one of
        /// three extensions. A name the product did not generate is refused even if a file sits there.
        private static bool IsGeneratedName(string name)
        {
            var dot = name.LastIndexOf('.');
            if (dot != 32) return false;
            for (int i = 0; i < 32; i++)
            {
                char ch = name[i];
                bool hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
                if (!hex) return false;
            }
            return TypeForExtension(name[dot..].ToLowerInvariant()) != null;
        }

        // ---- write --------------------------------------------------------------------------------

        public async Task<string?> SaveAsync(int companyId, IFormFile photo, CancellationToken ct = default)
        {
            if (companyId <= 0 || photo == null || photo.Length == 0 || photo.Length > MaxBytes) return null;

            var extension = ExtensionFor(photo.ContentType);
            if (extension == null) return null;

            // The name NEVER comes from the upload. A browser can send "..\..\appsettings.json" as a
            // file name, and the only defence that always works is not to use it.
            var fileName = Guid.NewGuid().ToString("N") + extension;
            var folder = Path.Combine(Root, companyId.ToString());

            try
            {
                Directory.CreateDirectory(folder);
                await using var stream = File.Create(Path.Combine(folder, fileName));
                await photo.CopyToAsync(stream, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            return $"{companyId}/{fileName}";
        }

        public void Delete(int companyId, string? locator)
        {
            if (string.IsNullOrWhiteSpace(locator) || companyId <= 0) return;

            // The same five checks the read path applies, because a stored value that chooses which
            // file gets DELETED deserves at least as much suspicion as one that chooses which gets read.
            if (locator.Contains("..", StringComparison.Ordinal)) return;
            if (locator.Contains('\\') || Path.IsPathRooted(locator)) return;

            var parts = locator.Split('/', StringSplitOptions.None);
            if (parts.Length != 2) return;
            if (!int.TryParse(parts[0], out var locatorCompany) || locatorCompany != companyId) return;
            if (!IsGeneratedName(parts[1])) return;

            var root = Path.GetFullPath(Root);
            var full = Path.GetFullPath(Path.Combine(root, companyId.ToString(), parts[1]));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

            try { if (File.Exists(full)) File.Delete(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover file is untidy; a failed request over one would be worse.
            }
        }
    }
}
