using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // ============================================================================================
    // AN EMPLOYEE'S PHOTOGRAPH — one resolver, every surface that shows a person.
    //
    // A comment projection carries the author's NAME and employee id and nothing else, so the shared
    // conversation panel drew the same grey silhouette for everyone: a thread with three people in it
    // looked like one person talking to themselves. Initials on a per-name colour fixed the identity
    // problem; this restores the actual photograph, which is what the rest of the product shows (the
    // header avatar, the org chart, chat).
    //
    // WHY IT IS SHARED AND NOT INLINE IN A CONTROLLER. Four surfaces want it already — the two
    // conversation endpoints (Accounting invoices, Inventory quotations) and the activity timeline —
    // and a photo lookup pasted into each is the same query, the same path normalisation and the same
    // tenancy predicate written four times to drift apart. The tenancy predicate is the one that
    // matters.
    //
    // IT LIVES IN PLATFORM, NOT COMMUNICATION. It answers a question about an Employee, which is the
    // kernel's own record; filing it under Communication would mean the timeline (kernel) had to
    // reference a module to draw a face.
    //
    // SCOPED TO THE CALLER'S COMPANY. An author from outside it resolves to NOTHING and the panel
    // falls back to initials: a conversation must not become a way to pull staff photographs out of a
    // company the reader cannot see. The names are already in the thread and are the platform's to
    // disclose; a photograph is a separate fact and is answered separately.
    //
    // A PATH, NOT BYTES. These are served by the ordinary static-file pipeline to a logged-in browser
    // — unlike a report, which must be self-contained and therefore embeds its images.
    // ============================================================================================
    public static class EmployeePhotos
    {
        /// <summary>employee id → web path of their photo, for those inside the caller's company who have one.</summary>
        public static async Task<Dictionary<int, string>> ResolveAsync(
            CrossDbContext db, BusinessContext context, IEnumerable<int> employeeIds,
            CancellationToken ct = default)
        {
            var ids = employeeIds.Where(i => i > 0).Distinct().ToList();

            if (ids.Count == 0 || context is null || context.CompanyId <= 0)
                return new Dictionary<int, string>();

            var rows = await db.Employee.AsNoTracking()
                .Where(e => ids.Contains(e.ID)
                            && e.EmpCompanyID == context.CompanyId
                            && e.ProfileImage != null && e.ProfileImage != "")
                .Select(e => new { e.ID, e.ProfileImage })
                .ToListAsync(ct);

            var map = new Dictionary<int, string>(rows.Count);
            foreach (var r in rows)
            {
                var url = Web(r.ProfileImage);
                if (url != null) map[r.ID] = url;
            }
            return map;
        }

        // A STORED PATH IS NOT A URL. These columns hold what the upload wrote — Windows separators,
        // sometimes a leading slash and sometimes not — and every screen that shows one has to
        // normalise it. Refuses anything that is not a relative path under the site, so a column
        // holding an absolute URL cannot turn an avatar into an off-site request.
        private static string? Web(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;

            var path = stored.Replace('\\', '/').Trim();
            if (path.Length == 0) return null;
            if (path.Contains("://", StringComparison.Ordinal)) return null;
            if (path.Contains("..", StringComparison.Ordinal)) return null;

            path = path.TrimStart('/');
            return path.Length == 0 ? null : "/" + path;
        }
    }
}
